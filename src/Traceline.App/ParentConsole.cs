using System.Runtime.InteropServices;

namespace Traceline.App;

/// <summary>
/// On Windows Traceline is a windowed program, so that opening it never shows a console window (hiding one after the
/// fact does not work with Windows Terminal, the default on Windows 11). The command-line uses (--help, --headless,
/// --install-service) still want to write somewhere: this attaches to the console of the terminal they were started
/// from. Without one, or when the output is already redirected to a file or another program, it changes nothing.
/// </summary>
internal static partial class ParentConsole
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    public static void Attach()
    {
        if (OperatingSystem.IsWindows()) AttachConsole(-1);   // -1: the process that started this one
    }
}
