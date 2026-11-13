using System.Runtime.InteropServices;

namespace Traceline.App;

/// <summary>Keeps Windows from going to sleep for lack of keyboard and mouse activity. Does nothing on other systems.</summary>
internal static class Awake
{
    private const uint SystemRequired = 0x00000001;   // ES_SYSTEM_REQUIRED: restarts the system's idle timer

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Restarts the idle timer once. Call it more often than the shortest sleep timeout (one minute) to stay awake.</summary>
    public static void StayAwake()
    {
        if (OperatingSystem.IsWindows()) SetThreadExecutionState(SystemRequired);
    }
}
