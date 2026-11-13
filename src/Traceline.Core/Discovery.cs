using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Traceline.Core;

public sealed class DiscoveredDevice
{
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Name { get; set; } = "";
    public double? PingMs { get; set; }
    public SortedSet<string> Methods { get; set; } = [];
    public string Description { get; set; } = "";
}

public sealed record DiscoveryOptions(string? Subnet, bool Ping = true, bool Arp = true, bool Mdns = true, bool Ssdp = true);

public sealed record LocalSubnet(string Cidr, string Address, string Interface);

/// <summary>
/// Finds the devices on the local network four ways: a ping sweep, the system's address-resolution (ARP)
/// table, multicast DNS (Bonjour) and UPnP/SSDP. Each device lists which methods saw it.
/// </summary>
public static partial class NetworkDiscovery
{
    /// <summary>The IPv4 networks this computer is directly connected to.</summary>
    public static List<LocalSubnet> Subnets() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength <= 30 && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a =>
                {
                    var prefix = Math.Max(a.PrefixLength, 22);   // never sweep more than 1024 addresses
                    var network = ToUInt(a.Address) & (uint.MaxValue << (32 - prefix));
                    return new LocalSubnet($"{FromUInt(network)}/{prefix}", a.Address.ToString(), n.Name);
                }))
            .OrderBy(s => HostDirectory.LocalOwner(IPAddress.Parse(s.Address)) == "Private network" ? 0 : 1)   // the ordinary LAN first
            .ToList();

    public static async Task<List<DiscoveredDevice>> ScanAsync(DiscoveryOptions options, string dataFolder, CancellationToken ct)
    {
        var subnets = Subnets();
        var cidr = string.IsNullOrWhiteSpace(options.Subnet) ? subnets.FirstOrDefault()?.Cidr : options.Subnet.Trim();
        if (cidr is null || !TryParseCidr(cidr, out var network, out var prefix)) throw new ArgumentException("Enter a network such as 192.168.1.0/24.");
        if (prefix < 22) throw new ArgumentException("That network is too large to scan. Use /22 or smaller.");
        var count = 1u << (32 - prefix);
        var addresses = Enumerable.Range(1, (int)count - 2).Select(i => FromUInt(network + (uint)i)).ToList();
        var local = subnets.FirstOrDefault(s => TryParseCidr(s.Cidr, out var n, out var p) && (network & (uint.MaxValue << (32 - p))) == n)?.Address;
        var localAddress = local is null ? IPAddress.Any : IPAddress.Parse(local);
        var found = new ConcurrentDictionary<string, DiscoveredDevice>();
        DiscoveredDevice Device(IPAddress ip) => found.GetOrAdd(ip.ToString(), key => new DiscoveredDevice { Ip = key });
        bool InRange(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork && (ToUInt(ip) & (uint.MaxValue << (32 - prefix))) == network;

        var tasks = new List<Task>();
        if (options.Ping) tasks.Add(PingSweepAsync(addresses, Device, ct));
        if (options.Ssdp) tasks.Add(SsdpAsync(localAddress, InRange, Device, ct));
        if (options.Arp) tasks.Add(TouchAllAsync(addresses));
        await Task.WhenAll(tasks);

        if (options.Arp && OperatingSystem.IsWindows())
        {
            await Task.Delay(1200, ct);   // give address resolution a moment to finish
            foreach (var (ip, mac) in ArpTable())
            {
                if (!InRange(ip)) continue;
                var d = Device(ip);
                d.Mac = mac; d.Methods.Add("ARP");
            }
        }
        if (options.Mdns) await MdnsAsync(localAddress, found.Keys.Select(IPAddress.Parse).ToList(), InRange, Device, ct);

        var vendors = Vendors(dataFolder);
        await Task.WhenAll(found.Values.Select(async d =>
        {
            if (d.Mac.Length >= 8 && vendors.TryGetValue(d.Mac[..8].Replace(":", "").ToUpperInvariant(), out var vendor)) d.Vendor = vendor;
            if (d.Name.Length > 0) return;
            try
            {
                var entry = await Dns.GetHostEntryAsync(d.Ip).WaitAsync(TimeSpan.FromSeconds(2), ct);
                if (entry.HostName != d.Ip) d.Name = entry.HostName;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException) { }
        }));
        return found.Values.OrderBy(d => ToUInt(IPAddress.Parse(d.Ip))).ToList();
    }

    private static async Task PingSweepAsync(List<IPAddress> addresses, Func<IPAddress, DiscoveredDevice> device, CancellationToken ct)
    {
        var prober = new IcmpProber();
        var target = new Target();
        var config = new TraceConfig { TimeoutMs = 800 };
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(addresses.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await prober.ProbeAsync(target, config, ip, 0, ct);
                if (!result.Reached) return;
                var d = device(ip);
                d.PingMs = Math.Round(result.RttMs, 2); d.Methods.Add("ICMP");
            }
            finally { gate.Release(); }
        }));
    }

    /// <summary>Sends a tiny datagram to every address so that the system resolves each one's hardware address.</summary>
    private static async Task TouchAllAsync(List<IPAddress> addresses)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        foreach (var ip in addresses)
        {
            try { await udp.SendAsync(new byte[1], 1, new IPEndPoint(ip, 9)); } catch (SocketException) { }
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    /// <summary>The system's table of IPv4 addresses and the hardware addresses that answered for them.</summary>
    private static List<(IPAddress Ip, string Mac)> ArpTable()
    {
        var list = new List<(IPAddress, string)>();
        var size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size == 0) return list;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return list;
            var entries = Marshal.ReadInt32(buffer);
            for (var i = 0; i < entries; i++)
            {
                // MIB_IPNETROW: index (4), address length (4), hardware address (8), IPv4 address (4), type (4)
                var row = buffer + 4 + i * 24;
                int length = Marshal.ReadInt32(row, 4), type = Marshal.ReadInt32(row, 20);
                if (length != 6 || type is not (3 or 4)) continue;
                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                if (mac.All(b => b == 0) || mac.All(b => b == 0xFF) || (mac[0] & 1) == 1) continue;
                list.Add((new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 16))), string.Join(':', mac.Select(b => b.ToString("x2")))));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    private static async Task MdnsAsync(IPAddress local, List<IPAddress> known, Func<IPAddress, bool> inRange, Func<IPAddress, DiscoveredDevice> device, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            await udp.SendAsync(MiniDns.BuildQuery("_services._dns-sd._udp.local", MiniDns.TypePtr, out _), group, ct);
            foreach (var ip in known)
            {
                var b = ip.GetAddressBytes();
                await udp.SendAsync(MiniDns.BuildQuery($"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa", MiniDns.TypePtr, out _), group, ct);
            }
            using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listen.CancelAfter(2500);
            while (true)
            {
                var packet = await udp.ReceiveAsync(listen.Token);
                if (!inRange(packet.RemoteEndPoint.Address) || packet.Buffer.Length < 12) continue;
                var d = device(packet.RemoteEndPoint.Address);
                d.Methods.Add("mDNS");
                var names = MiniDns.Parse(packet.Buffer).Texts;
                if (names.FirstOrDefault(n => n.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !n.StartsWith('_')) is { } name) d.Name = name;
                var services = names.Where(n => n.StartsWith('_')).Select(n => n.Split('.')[0].TrimStart('_')).Distinct().Take(6).ToList();
                if (services.Count > 0 && d.Description.Length == 0) d.Description = "Services: " + string.Join(", ", services);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task SsdpAsync(IPAddress local, Func<IPAddress, bool> inRange, Func<IPAddress, DiscoveredDevice> device, CancellationToken ct)
    {
        var locations = new ConcurrentDictionary<string, string>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
            var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            await udp.SendAsync(search, group, ct);
            await udp.SendAsync(search, group, ct);
            using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listen.CancelAfter(3000);
            while (true)
            {
                var packet = await udp.ReceiveAsync(listen.Token);
                if (!inRange(packet.RemoteEndPoint.Address)) continue;
                var text = Encoding.UTF8.GetString(packet.Buffer);
                var d = device(packet.RemoteEndPoint.Address);
                d.Methods.Add("UPnP");
                if (d.Description.Length == 0 && Header(text, "SERVER") is { Length: > 0 } server) d.Description = server;
                if (Header(text, "LOCATION") is { Length: > 0 } location) locations.TryAdd(d.Ip, location);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
        }

        // The device description file usually has a friendlier name than the SERVER header.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Task.WhenAll(locations.Select(async pair =>
        {
            try
            {
                if (!Uri.TryCreate(pair.Value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(uri.Host, out var host) || !inRange(host)) return;
                var xml = await http.GetStringAsync(uri, ct);
                var friendly = Tag(xml, "friendlyName");
                var model = Tag(xml, "modelName");
                if (friendly.Length > 0) device(IPAddress.Parse(pair.Key)).Description = model.Length > 0 && !friendly.Contains(model) ? $"{friendly} ({model})" : friendly;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }));
    }

    private static string? Header(string response, string name) =>
        response.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();

    private static string Tag(string xml, string name)
    {
        var m = Regex.Match(xml, $"<{name}>([^<]*)</{name}>", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
    }

    // ---------------------------------------------------------------- hardware vendors

    private static Dictionary<string, string>? _vendors;
    private static DateTime _vendorsRead;
    private static string? _vendorsPath;

    public static string VendorFile(string dataFolder) => Path.Combine(dataFolder, "oui.txt");

    /// <summary>
    /// Hardware-address prefixes and who they belong to, from the IEEE's public list (oui.txt) if it has been
    /// placed in the data folder. Without the file, vendors are simply left blank.
    /// </summary>
    public static Dictionary<string, string> Vendors(string dataFolder)
    {
        var path = VendorFile(dataFolder);
        if (!File.Exists(path)) return [];
        var written = File.GetLastWriteTimeUtc(path);
        if (_vendors is not null && written == _vendorsRead && path == _vendorsPath) return _vendors;
        var map = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(path))
        {
            // "28-6F-B9   (hex)\t\tNokia Shanghai Bell Co., Ltd."
            var at = line.IndexOf("(hex)", StringComparison.Ordinal);
            if (at < 8) continue;
            map[line[..8].Replace("-", "").ToUpperInvariant()] = line[(at + 5)..].Trim();
        }
        _vendorsRead = written; _vendorsPath = path;
        return _vendors = map;
    }

    // ---------------------------------------------------------------- address arithmetic

    private static uint ToUInt(IPAddress ip) { var b = ip.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromUInt(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    public static bool TryParseCidr(string cidr, out uint network, out int prefix)
    {
        network = 0; prefix = 0;
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out prefix) || prefix is < 8 or > 30) return false;
        network = ToUInt(ip) & (uint.MaxValue << (32 - prefix));
        return true;
    }
}
