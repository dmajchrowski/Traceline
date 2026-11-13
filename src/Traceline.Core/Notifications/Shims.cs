using System.Globalization;
using System.Reflection;

namespace Traceline.Core.Notifications;

// The files in this folder come from the author's ServiceMonitor project (MIT). These small types stand in
// for the parts of that project they refer to.

public static class AppInfo
{
    public const string ProductName = "Traceline";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string MachineName { get; } = Environment.MachineName;
}

public sealed record NotificationAttachment(string FileName, string MediaType, byte[] Content);

/// <summary>An extra block of detail under a notification (unused by Traceline so far).</summary>
public sealed record EventSummary(DateTimeOffset Time, string Log, string Source, int EventId, string Message);

public static class TimeText
{
    /// <summary>"2026-10-31 14:03:22 (UTC-05:00)" in the computer's time zone.</summary>
    public static string Local(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local);
        var offset = local.Offset;
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $" (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration().ToString(@"hh\:mm")})";
    }
}
