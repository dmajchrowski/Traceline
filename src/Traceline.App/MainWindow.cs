#if !PORTABLE
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Traceline.App;

/// <summary>
/// Traceline's own window: the web UI shown by the WebView2 control that comes with Windows, so it needs no browser
/// and has its own place on the taskbar. The main window hides to the tray icon when closed; windows the UI pops
/// out (a target or a wall for a second screen) are plain windows that close.
/// </summary>
internal sealed class MainWindow : Form
{
    // The window's own parts (title bar, the "starting" text) follow the Windows light or dark setting, as the UI does by default.
    private static readonly bool Dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
    private static readonly Color Back = Dark ? Color.FromArgb(17, 20, 19) : Color.FromArgb(246, 247, 246), Fore = Dark ? Color.FromArgb(170, 178, 175) : Color.FromArgb(70, 78, 75);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Back };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Fore, BackColor = Back, Font = new Font("Segoe UI", 11f), Text = "Starting Traceline…" };
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 3000 };
    private readonly bool _main;
    private readonly string _url;
    private CoreWebView2Environment? _environment;
    private readonly Queue<string> _toOpen = new();   // files waiting for the page to say it can take them
    private const long LargestFileToOpen = 200L * 1024 * 1024;

    /// <summary>The main window's close button was used: the window is hidden, not closed.</summary>
    public event Action? HiddenToTray;

    public MainWindow(Icon icon, string url, bool main = true)
    {
        _main = main;
        _url = url;
        Text = "Traceline";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        var room = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1400, 900);   // never larger than the screen has room for
        MinimumSize = new Size(Math.Min(820, room.Width), Math.Min(560, room.Height));
        Size = new Size(Math.Min(1400, room.Width), Math.Min(900, room.Height));
        BackColor = Back;
        Controls.Add(_web);
        Controls.Add(_status);
        _status.BringToFront();
        _retry.Tick += (_, _) => { _retry.Stop(); if (_web.CoreWebView2 is { } core) core.Navigate(_url); };
    }

    /// <summary>
    /// Starts the browser part and, for the main window, opens the UI. False when it cannot be started (WebView2 is
    /// missing or broken); the caller then falls back to a normal browser.
    /// </summary>
    public async Task<bool> InitAsync(CoreWebView2Environment? environment = null)
    {
        try
        {
            _environment = environment ?? await CoreWebView2Environment.CreateAsync(null, WebViewDataFolder());
            await _web.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception)
        {
            // Whatever the reason (no runtime, a broken one, a profile folder that cannot be written), a browser can still show the UI.
            return false;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached || Environment.GetEnvironmentVariable("TRACELINE_DEVTOOLS") == "1";
        core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) || core.DocumentTitle.Contains("://") ? "Traceline" : core.DocumentTitle;
        core.WindowCloseRequested += (_, _) => { if (!_main) Close(); };
        core.NewWindowRequested += OnNewWindow;
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) { _status.Visible = false; return; }
            if (e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted) return;
            ShowStatus($"Traceline is not answering at {_url}.\nTrying again…");
            _retry.Start();
        };
        // The page says "ready" once it is loaded and someone is signed in, and again whenever it is asked.
        if (_main) core.WebMessageReceived += (_, e) =>
        {
            if (!e.Source.StartsWith(_url, StringComparison.OrdinalIgnoreCase)) return;
            string said;
            try { said = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
            if (said == "ready") HandOverFiles();
        };
        if (_main) core.Navigate(_url);
        else _status.Visible = false;
        return true;
    }

    /// <summary>
    /// Has the page open a session or workspace file (one that was double-clicked in Explorer, say). The page imports
    /// it through the same calls as its Import buttons, as whoever is signed in; this only reads the file for it.
    /// </summary>
    public void OpenFile(string path)
    {
        _toOpen.Enqueue(path);
        // A page that is already up answers "ready"; one that is still loading says so by itself when it is.
        try { _web.CoreWebView2?.PostWebMessageAsString("ready?"); } catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
    }

    /// <summary>The files that were never handed to the page, for when the window turns out not to work.</summary>
    public string[] TakeWaitingFiles()
    {
        var files = _toOpen.ToArray();
        _toOpen.Clear();
        return files;
    }

    private void HandOverFiles()
    {
        if (_web.CoreWebView2 is not { } core) return;
        while (_toOpen.TryDequeue(out var path))
        {
            var name = Path.GetFileName(path);
            object message;
            try
            {
                message = new FileInfo(path).Length > LargestFileToOpen
                    ? new { type = "open-failed", name, text = "It is too large to open this way. Import it on the Sessions page instead." }
                    : new { type = "open-file", name, data = Convert.ToBase64String(File.ReadAllBytes(path)) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message = new { type = "open-failed", name, text = ex.Message };
            }
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
    }

    /// <summary>
    /// Links to other sites open in the normal browser. Windows the UI opens for itself ("Pop out") become windows
    /// of this program.
    /// </summary>
    private async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) return;
        if (!target.IsLoopback)
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var child = new MainWindow(Icon!, e.Uri, main: false);
            if (e.WindowFeatures.HasSize) child.ClientSize = new Size(Math.Max(600, (int)e.WindowFeatures.Width), Math.Max(400, (int)e.WindowFeatures.Height));
            child.Show();
            if (await child.InitAsync(_environment))
            {
                e.NewWindow = child._web.CoreWebView2;
                e.Handled = true;
            }
            else child.Close();
        }
        finally { deferral.Complete(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = 1;
        if (Dark) _ = DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = true;
        _status.BringToFront();
    }

    public void BringToFrontNow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void WndProc(ref Message m)
    {
        const int SysCommand = 0x0112, Close = 0xF060;
        // The main window's close button (and Alt+F4) only hides it; the tray icon brings it back. This is caught here
        // and not in OnFormClosing: once Windows Forms has seen a close by the user cancelled, it reports every later
        // request to close as the user's too, and a setup program or Task Manager asking would only hide the window.
        if (_main && m.Msg == SysCommand && (m.WParam.ToInt64() & 0xFFF0) == Close)
        {
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _retry.Dispose(); _web.Dispose(); }
        base.Dispose(disposing);
    }

    // The rest is adapted from PortBridge (https://github.com/dmajchrowski/PortBridge), by the same author, MIT License.

    /// <summary>
    /// Where WebView2 keeps its profile (cookies, cache). WebView2 never runs its browser as administrator: for an
    /// elevated program it starts the browser as the user who owns the desktop. For an administrator that is the same
    /// account, but a standard user who started Traceline with an administrator's name and password owns the desktop
    /// themselves, and the browser, running as them, cannot use a folder in the administrator's profile.
    /// </summary>
    private static string WebViewDataFolder()
    {
        var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Traceline", "WebView2");
        try
        {
            var name = SessionString(WtsUserName);
            if (string.IsNullOrEmpty(name)) return own;
            var sid = (SecurityIdentifier)new NTAccount(SessionString(WtsDomainName) ?? "", name).Translate(typeof(SecurityIdentifier));
            if (sid == WindowsIdentity.GetCurrent().User) return own;
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value);
            if (key?.GetValue("ProfileImagePath") is not string profile || profile.Length == 0) return own;
            profile = Environment.ExpandEnvironmentVariables(profile);
            var folder = Path.Combine(profile, "AppData", "Local", "Traceline", "WebView2");
            // That user can change their own profile, and this process is elevated: do not follow a link they put there.
            for (var p = folder; p.Length > profile.Length; p = Path.GetDirectoryName(p)!)
                if (new DirectoryInfo(p) is { Exists: true } d && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) return own;
            return folder;
        }
        catch (Exception)
        {
            return own;
        }
    }

    private const int WtsUserName = 5, WtsDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>A string about this process's own session.</summary>
    private static string? SessionString(int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, -1, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }
}
#endif
