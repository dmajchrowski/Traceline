using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Traceline.Core;

/// <summary>What one probe found: who answered, how long it took (NaN = no answer) and whether that was the destination.</summary>
public readonly record struct ProbeResult(IPAddress? Responder, double RttMs, bool Reached)
{
    public static readonly ProbeResult Lost = new(null, double.NaN, false);
}

public interface IProber
{
    /// <summary><paramref name="ttl"/> is the hop to probe; 0 means "go straight to the destination".</summary>
    Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct);
}

/// <summary>Picks the prober that matches the target's kind and, for routes, the configuration's packet type.</summary>
public sealed class Probers : IProber, IRoundProber
{
    private readonly IcmpProber _icmp = new();
    private readonly UdpTraceProber _udp = new();
    private readonly TcpTraceProber _tcpTrace = new();
    private readonly TcpProber _tcp = new();
    private readonly HttpProber _http = new();
    private readonly DnsProber _dns = new();

    public bool Handles(Target target, TraceConfig config) => _tcpTrace.Handles(target, config);

    public Task<ProbeResult[]> RoundAsync(Target target, TraceConfig config, IPAddress? address, int probeLength, CancellationToken ct) =>
        _tcpTrace.RoundAsync(target, config, address, probeLength, ct);

    public Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct) => target.Kind switch
    {
        ProbeKind.Tcp => _tcp.ProbeAsync(target, config, address, ttl, ct),
        ProbeKind.Http => _http.ProbeAsync(target, config, address, ttl, ct),
        ProbeKind.Dns => _dns.ProbeAsync(target, config, address, ttl, ct),
        _ => config.PacketType switch
        {
            PacketType.Udp => _udp.ProbeAsync(target, config, address, ttl, ct),
            PacketType.Tcp => _tcpTrace.ProbeAsync(target, config, address, ttl, ct),
            _ => _icmp.ProbeAsync(target, config, address, ttl, ct),
        },
    };
}

/// <summary>Sends one round: a probe per hop, slightly staggered, or a single probe in destination-only mode.</summary>
public static class RoundProbe
{
    private static long _sent;

    /// <summary>How many probes have been asked for since the program started (for the packets-per-second figure).</summary>
    public static long Sent => Interlocked.Read(ref _sent);

    public static async Task<ProbeResult[]> RunAsync(IProber prober, Target target, TraceConfig config, IPAddress? address, bool finalOnly, int probeLength, CancellationToken ct)
    {
        Interlocked.Add(ref _sent, finalOnly ? 1 : probeLength);
        if (finalOnly) return [await prober.ProbeAsync(target, config, address, 0, ct)];
        if (prober is IRoundProber whole && whole.Handles(target, config)) return await whole.RoundAsync(target, config, address, probeLength, ct);
        var tasks = new Task<ProbeResult>[probeLength];
        for (var i = 0; i < probeLength; i++)
        {
            tasks[i] = prober.ProbeAsync(target, config, address, i + 1, ct);
            if (config.SendDelayMs > 0 && i < probeLength - 1) await Task.Delay(config.SendDelayMs, ct);
        }
        return await Task.WhenAll(tasks);
    }
}

/// <summary>
/// ICMP echo. On Windows this calls the system's ICMP interface directly (no administrator rights needed) on a
/// thread of its own and times the call with a high-resolution stopwatch; .NET's own Ping class adds close to a
/// millisecond of scheduling delay, which matters on a local network. The system's whole-millisecond figure is
/// used as a sanity bound. Elsewhere, .NET's Ping is used as is.
/// </summary>
public sealed class IcmpProber : IProber
{
    public Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        if (address is null) return Task.FromResult(ProbeResult.Lost);
        // Without fragmentation a packet has to fit one Ethernet frame; with it, up to a jumbo frame's worth is allowed.
        var size = Math.Clamp(config.PacketSize - 28, 0, config.AllowFragment ? 8972 : 1472);
        var hopLimit = ttl <= 0 ? 128 : ttl;
        if (!OperatingSystem.IsWindows()) return ManagedAsync(address, hopLimit, size, config.TimeoutMs, ct, config.AllowFragment);
        IPAddress.TryParse(config.Interface, out var source);
        return Task.Factory.StartNew(() => WinIcmp.Send(address, source, hopLimit, size, config.TimeoutMs, config.AllowFragment), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal static async Task<ProbeResult> ManagedAsync(IPAddress address, int ttl, int size, int timeoutMs, CancellationToken ct, bool allowFragment = false)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: !allowFragment && address.AddressFamily == AddressFamily.InterNetwork);
        var start = Stopwatch.GetTimestamp();
        PingReply reply;
        try
        {
            reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), new byte[size], options, ct);
        }
        catch (PingException)
        {
            return ProbeResult.Lost;
        }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return reply.Status switch
        {
            IPStatus.Success => new ProbeResult(reply.Address, Math.Clamp(elapsed, reply.RoundtripTime, reply.RoundtripTime + 0.999), true),
            IPStatus.TtlExpired or IPStatus.TimeExceeded => new ProbeResult(reply.Address, elapsed, false),
            _ => ProbeResult.Lost,
        };
    }
}

/// <summary>The Windows ICMP interface (iphlpapi), used synchronously: one handle and one blocking call per probe.</summary>
internal static partial class WinIcmp
{
    // IP_SUCCESS and IP_TTL_EXPIRED_TRANSIT (IPv6 reports "hop limit exceeded" with the same value).
    private const uint Success = 0, TtlExpiredTransit = 11013;

    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptions { public byte Ttl, Tos, Flags, OptionsSize; public IntPtr OptionsData; }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial IntPtr IcmpCreateFile();

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial IntPtr Icmp6CreateFile();

    [LibraryImport("iphlpapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IcmpCloseHandle(IntPtr handle);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apc, IntPtr context, uint source, uint destination,
        byte[] data, ushort size, ref IpOptions options, IntPtr reply, uint replySize, uint timeout);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint Icmp6SendEcho2(IntPtr handle, IntPtr evt, IntPtr apc, IntPtr context, byte[] source, byte[] destination,
        byte[] data, ushort size, ref IpOptions options, IntPtr reply, uint replySize, uint timeout);

    public static ProbeResult Send(IPAddress address, IPAddress? source, int ttl, int size, int timeoutMs, bool allowFragment = false)
    {
        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        if (source is not null && source.AddressFamily != address.AddressFamily) source = null;
        var handle = v6 ? Icmp6CreateFile() : IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return ProbeResult.Lost;
        var data = new byte[size];
        var replySize = (uint)(64 + size + 8);
        var reply = Marshal.AllocHGlobal((int)replySize);
        try
        {
            var options = new IpOptions { Ttl = (byte)Math.Clamp(ttl, 1, 255), Flags = (byte)(v6 || allowFragment ? 0 : 0x02) };   // 0x02 = don't fragment
            var start = Stopwatch.GetTimestamp();
            if (v6)
            {
                var count6 = Icmp6SendEcho2(handle, 0, 0, 0, SockAddr6(source ?? IPAddress.IPv6Any), SockAddr6(address), data, (ushort)size, ref options, reply, replySize, (uint)timeoutMs);
                var elapsed6 = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (count6 == 0) return ProbeResult.Lost;
                // ICMPV6_ECHO_REPLY: port (2), flow (4), address (16), scope (4), padding (2), status (4), round trip (4)
                var bytes = new byte[16];
                Marshal.Copy(reply + 6, bytes, 0, 16);
                var responder6 = new IPAddress(bytes, (uint)Marshal.ReadInt32(reply, 22));
                return Result((uint)Marshal.ReadInt32(reply, 28), responder6, elapsed6, (uint)Marshal.ReadInt32(reply, 32));
            }
            var from = source is null ? 0u : BitConverter.ToUInt32(source.GetAddressBytes(), 0);
            var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            var count = IcmpSendEcho2Ex(handle, 0, 0, 0, from, destination, data, (ushort)size, ref options, reply, replySize, (uint)timeoutMs);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (count == 0) return ProbeResult.Lost;
            // ICMP_ECHO_REPLY: address (4), status (4), round trip (4), ...
            var responder = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(reply, 0)));
            return Result((uint)Marshal.ReadInt32(reply, 4), responder, elapsed, (uint)Marshal.ReadInt32(reply, 8));
        }
        finally
        {
            Marshal.FreeHGlobal(reply);
            IcmpCloseHandle(handle);
        }
    }

    private static ProbeResult Result(uint status, IPAddress responder, double elapsed, uint reported)
    {
        if (status != Success && status != TtlExpiredTransit) return ProbeResult.Lost;
        // The stopwatch is the precise figure. If this thread was held up, fall back on the system's whole milliseconds.
        var rtt = reported > 0 || status == Success ? Math.Clamp(elapsed, reported, reported + 0.999) : elapsed;
        return new ProbeResult(responder, rtt, status == Success);
    }

    private static byte[] SockAddr6(IPAddress address)
    {
        var sa = new byte[28];
        sa[0] = 23;   // AF_INET6
        address.GetAddressBytes().CopyTo(sa, 8);
        BitConverter.GetBytes((uint)address.ScopeId).CopyTo(sa, 24);
        return sa;
    }
}

/// <summary>
/// Hears the ICMP (or, for IPv6, ICMPv6) messages routers send back when a UDP or TCP packet runs out of hops. It is a raw socket,
/// which Windows only gives to administrators (the Traceline service qualifies). One listener per local address;
/// probers register the packet they are about to send and are told when its reply arrives.
/// </summary>
internal sealed class IcmpListener
{
    public readonly record struct Reply(IPAddress From, byte Type, byte Code, long Stamp);

    private static readonly ConcurrentDictionary<IPAddress, Lazy<IcmpListener>> Listeners = new();
    private readonly ConcurrentDictionary<(byte Protocol, ushort SourcePort, ushort DestinationPort, ushort Length), TaskCompletionSource<Reply>> _waiting = new();
    private readonly Socket _socket;

    public static IcmpListener For(IPAddress local)
    {
        try
        {
            return Listeners.GetOrAdd(local, l => new Lazy<IcmpListener>(() => new IcmpListener(l))).Value;
        }
        catch (SocketException ex)
        {
            Listeners.TryRemove(local, out _);
            throw new ProbeSetupException(ex.SocketErrorCode == SocketError.AccessDenied
                ? "UDP and TCP route tracing need administrator rights. Run Traceline as a service or as administrator, or switch this configuration to ICMP."
                : $"Could not listen for router replies on {local}: {ex.Message}");
        }
    }

    private IcmpListener(IPAddress local)
    {
        var v6 = local.AddressFamily == AddressFamily.InterNetworkV6;
        _socket = new Socket(local.AddressFamily, SocketType.Raw, v6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
        _socket.Bind(new IPEndPoint(local, 0));
        if (OperatingSystem.IsWindows())
        {
            // Without this Windows hands over only some of the replies.
            try { _socket.IOControl(IOControlCode.ReceiveAll, [1, 0, 0, 0], null); } catch (SocketException) { }
        }
        new Thread(v6 ? Loop6 : Loop) { IsBackground = true, Name = "Traceline ICMP listener" }.Start();
    }

    /// <summary><paramref name="udpLength"/> is the UDP length field of the packet about to be sent; 0 for TCP.</summary>
    public Task<Reply> Expect(byte protocol, int sourcePort, int destinationPort, int udpLength = 0)
    {
        var tcs = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[(protocol, (ushort)sourcePort, (ushort)destinationPort, (ushort)udpLength)] = tcs;
        return tcs.Task;
    }

    public void Forget(byte protocol, int sourcePort, int destinationPort, int udpLength = 0) =>
        _waiting.TryRemove((protocol, (ushort)sourcePort, (ushort)destinationPort, (ushort)udpLength), out _);

    private void Loop()
    {
        var b = new byte[2048];
        while (true)
        {
            int n;
            try { n = _socket.Receive(b); }
            catch (SocketException) { Thread.Sleep(50); continue; }
            catch (ObjectDisposedException) { return; }
            var stamp = Stopwatch.GetTimestamp();
            // Outer IP header, ICMP header (8 bytes), then the start of the packet that caused it.
            var ihl = (b[0] & 0x0F) * 4;
            if (n < ihl + 8 + 28 || b[9] != 1) continue;
            byte type = b[ihl], code = b[ihl + 1];
            if (type != 11 && type != 3) continue;
            var inner = ihl + 8;
            var ports = inner + (b[inner] & 0x0F) * 4;
            if (n < ports + 6) continue;
            var protocol = b[inner + 9];
            var length = protocol == 17 ? (ushort)((b[ports + 4] << 8) | b[ports + 5]) : (ushort)0;
            var key = (protocol, (ushort)((b[ports] << 8) | b[ports + 1]), (ushort)((b[ports + 2] << 8) | b[ports + 3]), length);
            if (_waiting.TryRemove(key, out var tcs))
                tcs.TrySetResult(new Reply(new IPAddress(b.AsSpan(12, 4)), type, code, stamp));
        }
    }

    /// <summary>
    /// The IPv6 flavour. The system strips the outer header, so a message starts with the ICMPv6 header (8 bytes)
    /// followed by the packet that caused it: a 40-byte IPv6 header and then the UDP or TCP header.
    /// Types are reported the IPv4 way (11 = ran out of hops, 3 = unreachable) so callers need not care.
    /// </summary>
    private void Loop6()
    {
        var b = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (true)
        {
            int n;
            try { n = _socket.ReceiveFrom(b, ref from); }
            catch (SocketException) { Thread.Sleep(50); continue; }
            catch (ObjectDisposedException) { return; }
            var stamp = Stopwatch.GetTimestamp();
            Dispatch6(b, n, ((IPEndPoint)from).Address, stamp);
        }
    }

    private void Dispatch6(byte[] b, int n, IPAddress from, long stamp)
    {
        if (Parse6(b, n) is not var (key, type, code)) return;
        if (_waiting.TryRemove(key, out var tcs)) tcs.TrySetResult(new Reply(from, type, code, stamp));
    }

    /// <summary>Reads an ICMPv6 error: which packet of ours it is about, and whether it means "out of hops" (11) or "unreachable" (3).</summary>
    internal static ((byte Protocol, ushort SourcePort, ushort DestinationPort, ushort Length) Key, byte Type, byte Code)? Parse6(byte[] b, int n)
    {
        // Some systems hand over the IPv6 header too; skip it when present.
        var at = n > 48 && (b[0] >> 4) == 6 && b[6] == 58 ? 40 : 0;
        if (n < at + 8 + 40 + 6) return null;
        byte type = b[at], code = b[at + 1];
        if (type != 3 && type != 1) return null;
        var inner = at + 8;
        var protocol = b[inner + 6];
        var ports = inner + 40;
        var length = protocol == 17 ? (ushort)((b[ports + 4] << 8) | b[ports + 5]) : (ushort)0;
        return ((protocol, (ushort)((b[ports] << 8) | b[ports + 1]), (ushort)((b[ports + 2] << 8) | b[ports + 3]), length), type == 3 ? (byte)11 : (byte)3, code);
    }

    /// <summary>The local address the system would send from to reach <paramref name="destination"/>.</summary>
    public static IPAddress LocalFor(IPAddress destination, string configured)
    {
        if (IPAddress.TryParse(configured, out var chosen) && chosen.AddressFamily == destination.AddressFamily) return chosen;
        using var s = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(destination, 9);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}

/// <summary>
/// Unix-style traceroute: UDP packets to port 33434. The destination answers "port unreachable".
/// Every packet of a target uses the same ports, so load-balancing routers send them all down one path
/// (the "Paris traceroute" idea); the packet's length tells the replies apart instead.
/// </summary>
public sealed class UdpTraceProber : IProber
{
    private const int DestinationPort = 33434;
    private readonly ConcurrentDictionary<long, int> _rounds = new();

    public async Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        if (address is null) return ProbeResult.Lost;
        var local = IcmpListener.LocalFor(address, config.Interface);
        var listener = IcmpListener.For(local);
        // The length encodes the hop and which of the last few rounds this is, so a late reply is not mistaken for a new one.
        var round = ttl <= 1 ? _rounds.AddOrUpdate(target.Id, 0, (_, v) => (v + 1) % 4) : _rounds.GetValueOrDefault(target.Id);
        var payload = Math.Clamp(config.PacketSize - (address.AddressFamily == AddressFamily.InterNetworkV6 ? 48 : 28), 0, 1100) + Math.Clamp(ttl, 0, 64) + 65 * round;
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(local, 40000 + (int)((target.Id * 7919 + 13) % 20000)));
        }
        catch (SocketException)
        {
            socket.Bind(new IPEndPoint(local, 0));
        }
        socket.Ttl = (short)(ttl <= 0 ? 128 : ttl);
        if (address.AddressFamily == AddressFamily.InterNetwork) socket.DontFragment = !config.AllowFragment;
        var sourcePort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var reply = listener.Expect(17, sourcePort, DestinationPort, payload + 8);
        try
        {
            var start = Stopwatch.GetTimestamp();
            socket.SendTo(new byte[payload], new IPEndPoint(address, DestinationPort));
            if (await Task.WhenAny(reply, Task.Delay(config.TimeoutMs, ct)) != reply) { ct.ThrowIfCancellationRequested(); return ProbeResult.Lost; }
            var r = await reply;
            return new ProbeResult(r.From, Stopwatch.GetElapsedTime(start, r.Stamp).TotalMilliseconds, r.Type == 3 && r.From.Equals(address));
        }
        catch (SocketException) { return ProbeResult.Lost; }
        finally { listener.Forget(17, sourcePort, DestinationPort, payload + 8); }
    }
}

/// <summary>A prober that sends a whole round itself instead of being asked hop by hop.</summary>
public interface IRoundProber
{
    bool Handles(Target target, TraceConfig config);
    Task<ProbeResult[]> RoundAsync(Target target, TraceConfig config, IPAddress? address, int probeLength, CancellationToken ct);
}

/// <summary>
/// TCP route tracing: real connection attempts (SYN) with a limited hop count. Routers on the way answer with
/// ICMP; the destination answers by accepting or refusing the connection, and either counts as reached.
///
/// Every probe of a target leaves from the same local port, so all of them are the same "flow" and
/// load-balancing routers send them down one path. Two connections cannot share a port at the same moment,
/// so the hops of a round are probed one after the other. To keep a round short, a router on the way is
/// only given the full timeout the first time it is probed; after that it gets a little longer than it
/// usually takes. The last hop of the round, the destination, always gets the full timeout.
/// </summary>
public sealed class TcpTraceProber : IProber, IRoundProber
{
    private const int QuickWaitMs = 300;
    /// <summary>The last round-trip time seen per hop (0 = it did not answer); absent = never probed.</summary>
    private readonly ConcurrentDictionary<(long Target, int Ttl), double> _seen = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<long, int> _ports = new();

    public bool Handles(Target target, TraceConfig config) => target.Kind == ProbeKind.Icmp && config.PacketType == PacketType.Tcp;

    public async Task<ProbeResult[]> RoundAsync(Target target, TraceConfig config, IPAddress? address, int probeLength, CancellationToken ct)
    {
        var results = Enumerable.Repeat(ProbeResult.Lost, probeLength).ToArray();
        if (address is null) return results;
        var gate = _gates.GetOrAdd(target.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            for (var ttl = 1; ttl <= probeLength; ttl++)
            {
                var wait = config.TimeoutMs;
                if (ttl < probeLength && _seen.TryGetValue((target.Id, ttl), out var usual))
                    wait = Math.Min(wait, Math.Max(QuickWaitMs, (int)(usual * 4) + 100));
                var result = await OneAsync(target, config, address, ttl, wait, ct);
                results[ttl - 1] = result;
                _seen[(target.Id, ttl)] = double.IsNaN(result.RttMs) ? 0 : result.RttMs;
                if (result.Reached) return results[..ttl];
            }
            return results;
        }
        finally { gate.Release(); }
    }

    /// <summary>A single probe: used for destination-only tracing (ttl 0).</summary>
    public async Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        if (address is null) return ProbeResult.Lost;
        var gate = _gates.GetOrAdd(target.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await OneAsync(target, config, address, ttl, config.TimeoutMs, ct); }
        finally { gate.Release(); }
    }

    private async Task<ProbeResult> OneAsync(Target target, TraceConfig config, IPAddress address, int ttl, int waitMs, CancellationToken ct)
    {
        var local = IcmpListener.LocalFor(address, config.Interface);
        var listener = ttl <= 0 ? null : IcmpListener.For(local);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.LingerState = new LingerOption(true, 0);   // closing must free the port at once, for the next hop
        Bind(socket, local, target.Id);
        if (ttl > 0) socket.Ttl = (short)ttl;
        if (OperatingSystem.IsWindows())
        {
            // SIO_TCP_INITIAL_RTO with "no SYN retransmissions": a repeated SYN would be answered a second time and confuse the timing.
            try { socket.IOControl(unchecked((int)0x98000011), [0xFF, 0xFF, 0xFE, 0x00], null); } catch (SocketException) { }
        }
        int sourcePort = ((IPEndPoint)socket.LocalEndPoint!).Port, port = Math.Clamp(config.TcpPort, 1, 65535);
        var reply = listener?.Expect(6, sourcePort, port);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var start = Stopwatch.GetTimestamp();
            var connect = socket.ConnectAsync(new IPEndPoint(address, port), stop.Token).AsTask();
            var timeout = Task.Delay(waitMs, ct);
            var first = reply is null ? await Task.WhenAny(connect, timeout) : await Task.WhenAny(reply, connect, timeout);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (first == timeout) { ct.ThrowIfCancellationRequested(); return ProbeResult.Lost; }
            if (first == connect)
            {
                if (connect.IsCompletedSuccessfully) return new ProbeResult(address, elapsed, true);
                if (connect.Exception?.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return new ProbeResult(address, elapsed, true);
                // Windows sometimes fails the connect because of the very ICMP message we are waiting for.
                if (reply is null || await Task.WhenAny(reply, Task.Delay(50, ct)) != reply) return ProbeResult.Lost;
            }
            var r = await reply!;
            return new ProbeResult(r.From, Stopwatch.GetElapsedTime(start, r.Stamp).TotalMilliseconds, false);
        }
        finally
        {
            stop.Cancel();
            listener?.Forget(6, sourcePort, port);
        }
    }

    /// <summary>Binds to the target's fixed local port, choosing one the first time (or again if another program took it).</summary>
    private void Bind(Socket socket, IPAddress local, long targetId)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (_ports.TryGetValue(targetId, out var port))
        {
            try { socket.Bind(new IPEndPoint(local, port)); return; }
            catch (SocketException) { _ports.TryRemove(targetId, out _); }
        }
        socket.Bind(new IPEndPoint(local, 0));
        _ports[targetId] = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>Time to open a TCP connection to host:port. A refused or timed-out connection counts as lost.</summary>
public sealed class TcpProber : IProber
{
    public async Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        if (address is null) return ProbeResult.Lost;
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);
        var start = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, target.Port), timeout.Token);
            return new ProbeResult(address, Stopwatch.GetElapsedTime(start).TotalMilliseconds, true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return ProbeResult.Lost;
        }
    }
}

/// <summary>
/// Time from sending a web request to receiving the response headers, on a fresh connection each time
/// (so it includes name lookup, connect and TLS). Server errors (5xx) and timeouts count as lost.
/// </summary>
public sealed class HttpProber : IProber
{
    public async Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromMilliseconds(config.TimeoutMs) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Traceline/1.1");
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await client.GetAsync(target.Extra, HttpCompletionOption.ResponseHeadersRead, ct);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return (int)response.StatusCode >= 500 ? ProbeResult.Lost : new ProbeResult(null, elapsed, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return ProbeResult.Lost;
        }
    }
}

/// <summary>Time for a DNS server to answer a lookup of the target name. No answer or an error reply counts as lost.</summary>
public sealed class DnsProber : IProber
{
    public async Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        var server = IPAddress.TryParse(target.Extra, out var explicitServer) ? explicitServer : MiniDns.SystemServers().FirstOrDefault();
        if (server is null) return ProbeResult.Lost;
        var start = Stopwatch.GetTimestamp();
        var answer = await MiniDns.QueryAsync(server, target.Host, MiniDns.TypeA, config.TimeoutMs, ct);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return answer is { Rcode: 0 } ? new ProbeResult(server, elapsed, true) : ProbeResult.Lost;
    }
}

/// <summary>A very small DNS client: one question over UDP, enough for timing lookups and reading TXT and PTR records.</summary>
public static class MiniDns
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16;

    /// <summary>Texts holds TXT strings and PTR target names.</summary>
    public sealed record Answer(int Rcode, List<string> Texts, int AnswerCount);

    public static List<IPAddress> SystemServers() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => !a.IsIPv6SiteLocal)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Distinct().ToList();

    public static async Task<Answer?> QueryAsync(IPAddress server, string name, ushort type, int timeoutMs, CancellationToken ct)
    {
        var query = BuildQuery(name, type, out var id);
        using var udp = new UdpClient(server.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), timeout.Token);
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                var data = result.Buffer;
                if (data.Length >= 12 && ((data[0] << 8) | data[1]) == id) return Parse(data);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static byte[] BuildQuery(string name, ushort type, out ushort id)
    {
        id = (ushort)Random.Shared.Next(1, 65535);
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var encoded = Encoding.ASCII.GetBytes(label);
            if (encoded.Length is 0 or > 63) throw new ArgumentException("Not a valid DNS name.");
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return [.. bytes];
    }

    internal static Answer Parse(byte[] d)
    {
        var rcode = d[3] & 0x0F;
        int questions = (d[4] << 8) | d[5], answers = (d[6] << 8) | d[7];
        var pos = 12;
        var texts = new List<string>();
        try
        {
            for (var i = 0; i < questions; i++) pos = SkipName(d, pos) + 4;
            for (var i = 0; i < answers; i++)
            {
                pos = SkipName(d, pos);
                var type = (d[pos] << 8) | d[pos + 1];
                var length = (d[pos + 8] << 8) | d[pos + 9];
                pos += 10;
                if (type == TypeTxt)
                {
                    var sb = new StringBuilder();
                    for (var p = pos; p < pos + length;) { int n = d[p]; sb.Append(Encoding.UTF8.GetString(d, p + 1, n)); p += n + 1; }
                    texts.Add(sb.ToString());
                }
                else if (type == TypePtr)
                {
                    texts.Add(ReadName(d, pos));
                }
                pos += length;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            // A truncated or odd reply: keep whatever was read.
        }
        return new Answer(rcode, texts, answers);
    }

    private static int SkipName(byte[] d, int pos)
    {
        while (true)
        {
            int n = d[pos];
            if (n == 0) return pos + 1;
            if ((n & 0xC0) == 0xC0) return pos + 2;
            pos += n + 1;
        }
    }

    /// <summary>Reads a possibly compressed name ("\x04host\x05local\x00" or a pointer into the packet).</summary>
    internal static string ReadName(byte[] d, int pos)
    {
        var labels = new List<string>();
        for (var jumps = 0; jumps < 16;)
        {
            int n = d[pos];
            if (n == 0) break;
            if ((n & 0xC0) == 0xC0) { pos = ((n & 0x3F) << 8) | d[pos + 1]; jumps++; continue; }
            labels.Add(Encoding.UTF8.GetString(d, pos + 1, n));
            pos += n + 1;
        }
        return string.Join('.', labels);
    }
}
