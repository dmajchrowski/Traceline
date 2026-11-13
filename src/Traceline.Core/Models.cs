using System.Text.Json.Serialization;

namespace Traceline.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProbeKind>))]
public enum ProbeKind { Icmp, Tcp, Http, Dns }

/// <summary>How the route is traced. Udp and Tcp need administrator rights; Agent asks another Traceline to do the probing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PacketType>))]
public enum PacketType { Icmp, Udp, Tcp, Agent }

/// <summary>Something being monitored. <see cref="Input"/> is what the user typed; the rest is derived from it.</summary>
public sealed class Target
{
    public long Id { get; set; }
    public string Input { get; set; } = "";
    public string Name { get; set; } = "";
    public ProbeKind Kind { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    /// <summary>Full URL for HTTP probes; the DNS server for DNS probes (empty = system resolver).</summary>
    public string Extra { get; set; } = "";
    /// <summary>"ipv4", "ipv6" or empty for whatever the system prefers.</summary>
    public string Family { get; set; } = "";
    public string Address { get; set; } = "";
    public int IntervalMs { get; set; } = 2500;
    public bool FinalHopOnly { get; set; }
    public bool Paused { get; set; }
    public bool FollowDns { get; set; } = true;
    public long CreatedMs { get; set; }
    /// <summary>Id of the named configuration in use; empty = the default one.</summary>
    public string ConfigId { get; set; } = "";
    /// <summary>The workspace this target belongs to; empty = the first one.</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>Closed targets are not traced and not listed, but their recorded session can be reopened.</summary>
    public bool Closed { get; set; }
    /// <summary>Keep the session when it is closed even if the auto-save setting would discard it.</summary>
    public bool Keep { get; set; }

    /// <summary>
    /// Parses "host", "ipv4:host", "tcp://host:443", "https://example.com/path" and "dns://name" or "dns://name@server".
    /// </summary>
    public static Target Parse(string input)
    {
        input = input.Trim();
        if (input.Length == 0) throw new ArgumentException("Enter a host name or address.");
        var t = new Target { Input = input, Name = input };
        if (input.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input[6..].TrimEnd('/');
            var colon = rest.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rest[(colon + 1)..], out var port) || port is < 1 or > 65535)
                throw new ArgumentException("Use tcp://host:port, for example tcp://example.com:443.");
            t.Kind = ProbeKind.Tcp; t.Host = rest[..colon].Trim('[', ']'); t.Port = port; t.FinalHopOnly = true;
        }
        else if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) throw new ArgumentException("That web address is not valid.");
            t.Kind = ProbeKind.Http; t.Host = uri.Host; t.Port = uri.Port; t.Extra = uri.ToString(); t.FinalHopOnly = true;
        }
        else if (input.StartsWith("dns://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input[6..].TrimEnd('/');
            var at = rest.IndexOf('@');
            t.Kind = ProbeKind.Dns; t.Host = at < 0 ? rest : rest[..at]; t.Extra = at < 0 ? "" : rest[(at + 1)..]; t.FinalHopOnly = true;
            if (t.Host.Length == 0) throw new ArgumentException("Use dns://name or dns://name@server.");
        }
        else
        {
            if (input.StartsWith("ipv4:", StringComparison.OrdinalIgnoreCase)) { t.Family = "ipv4"; input = input[5..]; }
            else if (input.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase)) { t.Family = "ipv6"; input = input[5..]; }
            if (input.Contains(' ') || input.Contains('/')) throw new ArgumentException("Enter a host name or IP address.");
            t.Kind = ProbeKind.Icmp; t.Host = input.Trim();
        }
        return t;
    }
}

/// <summary>A named set of engine and display settings. Each target uses one; "default" always exists.</summary>
public sealed class TraceConfig
{
    public const string DefaultId = "default";

    public string Id { get; set; } = DefaultId;
    public string Name { get; set; } = "Default settings";
    public string Description { get; set; } = "";

    // Engine
    public PacketType PacketType { get; set; }
    /// <summary>Destination port for TCP route tracing.</summary>
    public int TcpPort { get; set; } = 443;
    public int TimeoutMs { get; set; } = 3000;
    /// <summary>Total packet size in bytes, headers included.</summary>
    public int PacketSize { get; set; } = 56;
    /// <summary>Pause between the packets of one round.</summary>
    public int SendDelayMs { get; set; } = 10;
    public int MaxHops { get; set; } = 30;
    /// <summary>Local address to send from; empty = let the system choose.</summary>
    public string Interface { get; set; } = "";
    /// <summary>New targets with this configuration start as destination-only.</summary>
    public bool StartFinalHopOnly { get; set; }
    /// <summary>Off = packets carry the "don't fragment" flag (IPv4), which is how a path's largest packet size is found.</summary>
    public bool AllowFragment { get; set; } = true;
    /// <summary>
    /// A hop that has stopped answering loses its address in the route once it has been silent for both
    /// <see cref="SilentMinutes"/> and <see cref="SilentSamples"/>. Off = the last known address stays.
    /// </summary>
    public bool RemoveSilentHops { get; set; } = true;
    public int SilentMinutes { get; set; } = 5;
    public int SilentSamples { get; set; } = 15;
    /// <summary>For <see cref="PacketType.Agent"/>: the other Traceline's address (http://host:port), its agent key, and what it should send.</summary>
    public string AgentUrl { get; set; } = "";
    public string AgentKey { get; set; } = "";
    public PacketType AgentPacketType { get; set; }

    // Display
    public int WarnMs { get; set; } = 100;
    public int BadMs { get; set; } = 200;
    /// <summary>"each" = every graph scales to its own data, "destination" = all follow the destination graph, "fixed" = <see cref="ScaleMs"/>.</summary>
    public string ScaleMode { get; set; } = "each";
    public int ScaleMs { get; set; } = 200;
    /// <summary>Loss percentage that fills a timeline's full height.</summary>
    public int LossScalePct { get; set; } = 30;
    public bool ShowMinMax { get; set; } = true;
    public bool ShowJitter { get; set; }
    public int JitterScaleMs { get; set; } = 60;
    public int JitterTargetMs { get; set; } = 30;
}

/// <summary>One round of probes. Rtts[i] is hop i+1 in milliseconds, NaN when lost; the last entry is the destination.</summary>
public sealed record Sample(long Ts, long RouteId, bool FinalOnly, float[] Rtts);

public sealed record RollupRow(long Ts, long RouteId, Agg[] Aggs);

public sealed record TimelineEvent(long Id, long TargetId, long Ts, string Kind, string Text);

public sealed class HostInfo
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public string Asn { get; set; } = "";
    public string Owner { get; set; } = "";
    /// <summary>A name the user gave this address; shown instead of the DNS name.</summary>
    public string Alias { get; set; } = "";
    /// <summary>Free text the user keeps about this address.</summary>
    public string Notes { get; set; } = "";
    public long UpdatedMs { get; set; }
}

public sealed class AppSettings
{
    public int RawDays { get; set; } = 30;
    public int RollupDays { get; set; } = 365;
    public bool OwnerLookup { get; set; } = true;
    public bool ReverseDns { get; set; } = true;
    public bool ListenOnNetwork { get; set; }
    public int Port { get; set; } = 7878;
    public string SiteName { get; set; } = "";
    /// <summary>What happens to a target's data when it is closed: "always" keep, keep "after" N minutes of tracing, or only when "selected".</summary>
    public string AutoSaveMode { get; set; } = "always";
    public int AutoSaveMinutes { get; set; } = 10;
    /// <summary>Alerts may start programs only when this is on.</summary>
    public bool AllowRunProgram { get; set; }
    /// <summary>When set, other Traceline instances that present this key may use this one as a remote agent.</summary>
    public string AgentKey { get; set; } = "";
    public string ActiveWorkspace { get; set; } = "";
    /// <summary>Add a note to the timeline when a target's address changes.</summary>
    public bool CommentOnIpChange { get; set; } = true;
    /// <summary>Ask Windows not to go to sleep while something is being traced.</summary>
    public bool KeepAwake { get; set; }

    // HTTPS for the web UI
    public bool HttpsEnabled { get; set; }
    public int HttpsPort { get; set; } = 7879;
    /// <summary>Send browsers on other computers from the plain-HTTP port to HTTPS.</summary>
    public bool HttpsOnly { get; set; }
    /// <summary>A .pfx/.p12 or .pem file with the certificate and its key; empty = a certificate Traceline makes for itself.</summary>
    public string CertificatePath { get; set; } = "";
    /// <summary>Password of the certificate file, stored protected.</summary>
    public string CertificatePassword { get; set; } = "";
    /// <summary>Password of the self-made certificate file, stored protected.</summary>
    public string OwnCertificatePassword { get; set; } = "";

    // Settings from version 1.0, read once to build the default configuration and the first user.
    public int? WarnMs { get; set; }
    public int? BadMs { get; set; }
    public int? SendDelayMs { get; set; }
    public int? MaxHops { get; set; }
    public string? PasswordHash { get; set; }
}

public sealed class UserAccount
{
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>"admin" (everything), "editor" (targets, notes, alerts, sharing) or "viewer" (read only).</summary>
    public string Role { get; set; } = "viewer";
}

public sealed class Workspace
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed record SummaryItem(long TargetId, int Hop);

/// <summary>A user-made summary screen: a named list of destinations and individual hops.</summary>
public sealed class SummaryDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public List<SummaryItem> Items { get; set; } = [];
}

public sealed class ShareLink
{
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Empty = every target.</summary>
    public List<long> TargetIds { get; set; } = [];
    public long? ExpiresMs { get; set; }
    public long CreatedMs { get; set; }
}

/// <summary>A probe could not even be attempted (missing rights, unsupported combination). Shown to the user as the target's error.</summary>
public sealed class ProbeSetupException(string message) : Exception(message);
