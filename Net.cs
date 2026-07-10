using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace DRMultiplayer
{
    // =====================================================================
    //  Socket plumbing. All socket I/O runs on background threads; nothing
    //  in here touches Unity. Results surface through NetHub.Events, which
    //  Plugin.Update drains on the main thread.
    // =====================================================================

    internal abstract class NetEvent { }
    internal class PeerConnectedEvent : NetEvent { public NetPeer Peer; public bool Incoming; }
    internal class PeerDisconnectedEvent : NetEvent { public NetPeer Peer; public string Reason; }
    internal class PacketEvent : NetEvent { public NetPeer Peer; public Envelope Envelope; }
    internal class ConnectFailedEvent : NetEvent { public string Error; }
    internal class HostFoundEvent : NetEvent { public string Address; public int Port; public string Name; }
    internal class ScanFinishedEvent : NetEvent { }

    /// <summary>One live, framed TCP connection.</summary>
    internal class NetPeer
    {
        private const int MaxFrame = 64 * 1024 * 1024; // world snapshots can be large

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly BlockingCollection<byte[]> _outbox = new BlockingCollection<byte[]>(256);
        private volatile bool _closed;

        internal string RemoteAddress { get; }

        internal NetPeer(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            // Half-open detection: if no data arrives for 15s a blocked Read throws, so the
            // ReadLoop catch below closes the peer (firing PeerDisconnectedEvent). The timeout is
            // PER Read — during an active transfer each Read returns partial data and resets it,
            // so large world snapshots never false-timeout. Paired with the heartbeat sender below.
            _client.ReceiveTimeout = 15000;
            _stream = client.GetStream();
            RemoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

            new Thread(ReadLoop) { IsBackground = true, Name = "DRMP read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "DRMP write" }.Start();
            new Thread(HeartbeatLoop) { IsBackground = true, Name = "DRMP heartbeat" }.Start();
        }

        internal void Send<T>(T packet)
        {
            if (_closed) return;
            var json = JsonConvert.SerializeObject(Envelope.Wrap(packet));
            var payload = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + payload.Length];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            try { _outbox.Add(frame); }
            catch (InvalidOperationException) { /* closing */ }
        }

        internal void Close(string reason = "closed")
        {
            if (_closed) return;
            _closed = true;
            try { _outbox.CompleteAdding(); } catch { }
            try { _client.Close(); } catch { }
            NetHub.Events.Enqueue(new PeerDisconnectedEvent { Peer = this, Reason = reason });
        }

        /// <summary>Transport-level keepalive: enqueue a zero-length frame every 5s so the remote's
        /// per-Read receive timeout stays fed on an otherwise idle link, and so a half-open link is
        /// noticed within ~15s. Purely framing — no Envelope/packet involved; the peer's ReadLoop
        /// treats a zero-length frame as a no-op keepalive.</summary>
        private void HeartbeatLoop()
        {
            while (!_closed)
            {
                var frame = new byte[4];   // 4-byte little-endian length header of 0, no payload
                try { _outbox.Add(frame); }
                catch (InvalidOperationException) { break; /* closing */ }
                Thread.Sleep(5000);
            }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var frame in _outbox.GetConsumingEnumerable())
                {
                    _stream.Write(frame, 0, frame.Length);
                }
            }
            catch (Exception e) { Close("send failed: " + e.Message); }
        }

        private void ReadLoop()
        {
            try
            {
                var header = new byte[4];
                while (!_closed)
                {
                    ReadExact(header, 4);
                    int len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                    if (len == 0) continue;   // zero-length keepalive frame — no payload, no event
                    if (len < 0 || len > MaxFrame) throw new IOException($"bad frame length {len}");
                    var payload = new byte[len];
                    ReadExact(payload, len);
                    var env = JsonConvert.DeserializeObject<Envelope>(Encoding.UTF8.GetString(payload));
                    if (env?.t != null)
                        NetHub.Events.Enqueue(new PacketEvent { Peer = this, Envelope = env });
                }
            }
            catch (Exception e)
            {
                Close(_closed ? "closed" : "connection lost: " + e.Message);
            }
        }

        private void ReadExact(byte[] buffer, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = _stream.Read(buffer, off, count - off);
                if (n <= 0) throw new IOException("remote closed the connection");
                off += n;
            }
        }
    }

    /// <summary>Owns the listener, the discovery responder and outgoing connects.</summary>
    internal static class NetHub
    {
        internal static readonly ConcurrentQueue<NetEvent> Events = new ConcurrentQueue<NetEvent>();

        private static TcpListener _listener;
        private static UdpClient _discovery;
        private static volatile bool _hosting;
        private static volatile string _hostName = "host";
        private static volatile int _tcpPort;

        // ---------------- hosting ----------------

        /// <summary>Open the TCP listener + UDP discovery responder. Throws on port conflicts.</summary>
        internal static void StartHost(int tcpPort, int discoveryPort, string hostName)
        {
            StopHost();
            _tcpPort = tcpPort;
            _hostName = hostName;
            _listener = new TcpListener(IPAddress.Any, tcpPort);
            _listener.Start();
            _hosting = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "DRMP accept" }.Start();
            Plugin.Log.LogInfo($"Hosting on TCP port {tcpPort}. LAN address(es): {string.Join(", ", LocalLanAddresses())}");

            try
            {
                _discovery = new UdpClient();
                _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                new Thread(DiscoveryLoop) { IsBackground = true, Name = "DRMP discovery" }.Start();
            }
            catch (Exception e)
            {
                // Discovery is best-effort; direct IP joins still work.
                Plugin.Log.LogWarning($"LAN discovery responder unavailable: {e.Message}");
                _discovery = null;
            }
        }

        internal static void StopHost()
        {
            _hosting = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            try { _discovery?.Close(); } catch { }
            _discovery = null;
        }

        private static void AcceptLoop()
        {
            var listener = _listener;
            try
            {
                while (_hosting)
                {
                    var client = listener.AcceptTcpClient();
                    Events.Enqueue(new PeerConnectedEvent { Peer = new NetPeer(client), Incoming = true });
                }
            }
            catch { /* listener stopped */ }
        }

        private static void DiscoveryLoop()
        {
            var udp = _discovery;
            try
            {
                while (_hosting)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var data = udp.Receive(ref remote);
                    if (Encoding.UTF8.GetString(data) != Discovery.Probe) continue;
                    var reply = Encoding.UTF8.GetBytes($"{Discovery.ReplyPrefix}{_tcpPort}|{_hostName}");
                    udp.Send(reply, reply.Length, remote);
                }
            }
            catch { /* socket closed */ }
        }

        // ---------------- joining ----------------

        /// <summary>Connect in the background; success/failure lands in Events.</summary>
        internal static void Connect(string address, int port)
        {
            new Thread(() =>
            {
                try
                {
                    var client = new TcpClient();
                    var ar = client.BeginConnect(address, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        client.Close();
                        throw new TimeoutException("connection timed out");
                    }
                    client.EndConnect(ar);
                    Events.Enqueue(new PeerConnectedEvent { Peer = new NetPeer(client), Incoming = false });
                }
                catch (Exception e)
                {
                    Events.Enqueue(new ConnectFailedEvent { Error = e.Message });
                }
            }) { IsBackground = true, Name = "DRMP connect" }.Start();
        }

        /// <summary>Broadcast a discovery probe and report every host that answers within ~1.5s.</summary>
        internal static void Scan(int discoveryPort)
        {
            new Thread(() =>
            {
                try
                {
                    using (var udp = new UdpClient())
                    {
                        udp.EnableBroadcast = true;
                        udp.Client.ReceiveTimeout = 500;
                        var probe = Encoding.UTF8.GetBytes(Discovery.Probe);

                        // Build a per-interface directed-broadcast list. One limited broadcast
                        // to 255.255.255.255 only reaches whatever interface the OS happens to
                        // pick, so on multi-homed machines (real LAN + Tailscale, etc.) a Linux
                        // host and a Mac never see each other. Probing each interface's directed
                        // broadcast fixes that; the global broadcast is kept as a fallback.
                        var targets = BroadcastTargets(discoveryPort);
                        foreach (var target in targets)
                            udp.Send(probe, probe.Length, target);
                        Plugin.Log.LogInfo($"LAN scan: probed {targets.Count} broadcast target(s).");

                        var seen = new HashSet<string>();
                        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
                        while (DateTime.UtcNow < deadline)
                        {
                            try
                            {
                                var remote = new IPEndPoint(IPAddress.Any, 0);
                                var data = udp.Receive(ref remote);
                                var text = Encoding.UTF8.GetString(data);
                                if (!text.StartsWith(Discovery.ReplyPrefix)) continue;
                                var parts = text.Substring(Discovery.ReplyPrefix.Length).Split(new[] { '|' }, 2);
                                if (parts.Length != 2 || !int.TryParse(parts[0], out var tcpPort)) continue;
                                var key = $"{remote.Address}:{tcpPort}";
                                if (!seen.Add(key)) continue;
                                Events.Enqueue(new HostFoundEvent
                                {
                                    Address = remote.Address.ToString(),
                                    Port = tcpPort,
                                    Name = parts[1]
                                });
                            }
                            catch (SocketException) { /* receive timeout — keep polling until deadline */ }
                        }
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"LAN scan failed: {e.Message}"); }
                Events.Enqueue(new ScanFinishedEvent());
            }) { IsBackground = true, Name = "DRMP scan" }.Start();
        }

        /// <summary>Per-interface directed-broadcast endpoints to probe during a scan, plus the
        /// global 255.255.255.255 fallback. Skips link-local, Tailscale/CGNAT (100.64/10) and /32
        /// addresses (a /32 has no usable broadcast). Never throws — falls back to just the global
        /// broadcast if interface enumeration fails.</summary>
        private static List<IPEndPoint> BroadcastTargets(int discoveryPort)
        {
            var targets = new List<IPEndPoint>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var addr = ua.Address.GetAddressBytes();
                        // Skip link-local 169.254/16.
                        if (addr[0] == 169 && addr[1] == 254) continue;
                        // Skip Tailscale/CGNAT 100.64.0.0/10.
                        if (addr[0] == 100 && addr[1] >= 64 && addr[1] <= 127) continue;

                        var mask = ua.IPv4Mask?.GetAddressBytes();
                        if (mask == null || mask.Length != 4) continue;
                        // A /32 (255.255.255.255) has no directed broadcast — skip it.
                        if (mask[0] == 255 && mask[1] == 255 && mask[2] == 255 && mask[3] == 255) continue;

                        var bcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                            bcast[i] = (byte)((addr[i] & mask[i]) | (~mask[i] & 0xFF));
                        targets.Add(new IPEndPoint(new IPAddress(bcast), discoveryPort));
                    }
                }
            }
            catch (Exception e)
            {
                // A single bad interface must not abort the scan; fall back to global broadcast.
                Plugin.Log.LogWarning($"LAN scan: interface enumeration failed: {e.Message}");
            }

            // Always include the limited broadcast as a fallback.
            targets.Add(new IPEndPoint(IPAddress.Broadcast, discoveryPort));
            return targets;
        }

        /// <summary>The host's real LAN IPv4 address(es), for showing joiners what to connect to.
        /// Includes only private ranges (10/8, 172.16/12, 192.168/16); excludes link-local and
        /// Tailscale/CGNAT (100.64/10). Side-effect-free; returns an empty list on any failure.</summary>
        internal static List<string> LocalLanAddresses()
        {
            var result = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var addr = ua.Address.GetAddressBytes();
                        // Exclude link-local 169.254/16 and Tailscale/CGNAT 100.64/10.
                        if (addr[0] == 169 && addr[1] == 254) continue;
                        if (addr[0] == 100 && addr[1] >= 64 && addr[1] <= 127) continue;
                        // Include only private ranges.
                        bool priv = addr[0] == 10
                                 || (addr[0] == 172 && addr[1] >= 16 && addr[1] <= 31)
                                 || (addr[0] == 192 && addr[1] == 168);
                        if (!priv) continue;
                        result.Add(ua.Address.ToString());
                    }
                }
            }
            catch { /* best-effort — return whatever we gathered */ }
            return result;
        }
    }
}
