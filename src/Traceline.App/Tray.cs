using System.Diagnostics;
#if !PORTABLE
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
#endif

namespace Traceline.App;

#if PORTABLE

/// <summary>Builds without Windows Forms (other operating systems) have no tray icon.</summary>
internal static class Tray
{
    public static bool IsRunning(int port) => false;
    public static bool ShowExisting(string? instance, string? file) => false;
    public static void Run(int port, bool ownsEngine, bool minimized, string? instance, string? file, Action stop) { }
}

#else

/// <summary>
/// Desktop mode on Windows: Traceline's own window (<see cref="MainWindow"/>) and an icon in the notification area
/// that opens it, turns red and shows a notice when an alert is active, and (when it started the engine itself)
/// stops it on Exit. Closing the window leaves the icon; starting Traceline again brings the window back.
/// </summary>
internal static partial class Tray
{
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(IntPtr icon);

    public static bool IsRunning(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            return http.GetAsync($"http://localhost:{port}/api/state").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>The name of the signal that asks a desktop Traceline already running in this Windows session to show its window.</summary>
    private static string ShowSignal(string? instance) => $@"Local\Traceline.Show.{instance ?? "default"}";

    /// <summary>
    /// When Traceline's desktop mode is already running here (for the same --port, if one was given), brings its
    /// window to the front and returns true: there is nothing left for this process to do.
    /// </summary>
    /// <summary>
    /// Where a second start leaves the path of a file to open (one per line) for the Traceline that is already
    /// running, before it signals it. A named signal carries no text, and both run as the same user.
    /// </summary>
    private static string HandOver(string? instance) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Traceline", $"open-{instance ?? "default"}.txt");

    public static bool ShowExisting(string? instance, string? file)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignal(instance));
            if (file is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HandOver(instance))!);
                    File.AppendAllLines(HandOver(instance), [file]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the window still comes to the front */ }
            }
            return signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { return false; }
    }

    public static void Run(int port, bool ownsEngine, bool minimized, string? instance, string? file, Action stop)
    {
        var ui = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetCompatibleTextRenderingDefault(false);
            using var context = new TrayContext(port, ownsEngine, minimized, ShowSignal(instance), HandOver(instance), file);
            Application.Run(context);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        stop();
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly string _url;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
        private readonly Icon _ok = MakeIcon(Color.FromArgb(11, 110, 79)), _alert = MakeIcon(Color.FromArgb(198, 40, 40)), _idle = MakeIcon(Color.FromArgb(104, 113, 110));
        private HashSet<string> _alerting = [];
        private readonly MainWindow _window;
        private readonly EventWaitHandle _showSignal;
        private readonly RegisteredWaitHandle _showWait;
        private bool? _windowWorks;   // not known until the window has been opened once
        private bool _opening, _toldAboutTray;
        private readonly string _handOver;

        public TrayContext(int port, bool ownsEngine, bool minimized, string showSignal, string handOver, string? file)
        {
            _handOver = handOver;
            _url = $"http://localhost:{port}";
            _window = new MainWindow(_ok, _url);
            // The close button only hides the window. When it does close (Exit, Windows shutting down, a setup program asking), the icon goes too.
            _window.FormClosed += (_, _) => ExitThread();
            _window.HiddenToTray += () =>
            {
                if (_toldAboutTray) return;
                _toldAboutTray = true;
                _icon!.ShowBalloonTip(4000, "Traceline is still running",
                    (ownsEngine ? "Tracing continues in the background." : "The Traceline service keeps tracing.") + " Double-click this icon to open the window again.", ToolTipIcon.Info);
            };

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open Traceline", null, (_, _) => OpenWindow());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripMenuItem("Open in browser", null, (_, _) => OpenInBrowser(asApp: false)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(ownsEngine ? "Exit Traceline (stops tracing)" : "Exit (the Traceline service keeps tracing)", null, (_, _) => Exit()));
            _icon = new NotifyIcon { Icon = _idle, Text = "Traceline", Visible = true, ContextMenuStrip = menu };
            _icon.DoubleClick += (_, _) => OpenWindow();
            _icon.BalloonTipClicked += (_, _) => OpenWindow();
            _timer.Tick += async (_, _) => await PollAsync();
            _timer.Start();

            // Starting Traceline a second time signals this one to show its window (see Tray.ShowExisting).
            _ = _window.Handle;   // creates the window's handle, so that other threads can reach the UI thread through it
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, showSignal);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => { try { _window.BeginInvoke(() => { OpenWindow(); OpenHandedOver(); }); } catch (InvalidOperationException) { } }, null, -1, false);

            try { File.Delete(_handOver); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // left by a start that never reached anyone
            if (!minimized) _window.BeginInvoke(OpenWindow);
            if (file is not null) _window.BeginInvoke(() => OpenFile(file));
            _ = PollAsync();
        }

        /// <summary>Opens the files a second start of Traceline left for this one (see <see cref="Tray.ShowExisting"/>).</summary>
        private void OpenHandedOver()
        {
            string[] files;
            try
            {
                if (!File.Exists(_handOver)) return;
                files = File.ReadAllLines(_handOver);
                File.Delete(_handOver);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var file in files.Where(f => f.Length > 0).Distinct()) OpenFile(file);
        }

        /// <summary>Has the window open a session or workspace file. Without the window (a browser shows the UI) it can only say where to import it.</summary>
        private void OpenFile(string file)
        {
            if (_windowWorks == false)
                _icon.ShowBalloonTip(8000, "Traceline", $"To open {Path.GetFileName(file)}, import it on the Sessions page (a workspace: under Settings).", ToolTipIcon.Info);
            else _window.OpenFile(file);
        }

        /// <summary>Shows Traceline's window. The first time, this also starts WebView2; if that cannot be done, a browser is used from then on.</summary>
        private async void OpenWindow()
        {
            if (_windowWorks == false) { OpenInBrowser(asApp: true); return; }
            _window.BringToFrontNow();
            if (_windowWorks == true || _opening) return;
            _opening = true;
            try { _windowWorks = await _window.InitAsync(); }
            finally { _opening = false; }
            if (_windowWorks == true) return;
            _window.Hide();
            _icon.ShowBalloonTip(8000, "Traceline", "The Microsoft Edge WebView2 Runtime is missing, so Traceline opens in a browser instead.", ToolTipIcon.Info);
            OpenInBrowser(asApp: true);
            foreach (var file in _window.TakeWaitingFiles()) OpenFile(file);
        }

        private void Exit()
        {
            foreach (var form in Application.OpenForms.Cast<Form>().ToList()) form.Close();
            ExitThread();
        }

        /// <summary>Opens the UI in the default browser, or as an app window (no address bar) in Edge or Chrome when there is one.</summary>
        private void OpenInBrowser(bool asApp)
        {
            string[] browsers =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            ];
            try
            {
                if (asApp && browsers.FirstOrDefault(File.Exists) is { } browser) Process.Start(browser, $"--app={_url} --window-size=1400,900")?.Dispose();
                else Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _icon.ShowBalloonTip(5000, "Traceline", $"Open {_url} in a browser.", ToolTipIcon.Info);
            }
        }

        private async Task PollAsync()
        {
            try
            {
                using var response = await _http.GetAsync(_url + "/api/targets");
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Show(_idle, "Sign in to Traceline to see its status here");
                    return;
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var targets = doc.RootElement.EnumerateArray().ToList();
                var alerting = targets.Where(t => t.GetProperty("alerting").GetBoolean()).Select(t => t.GetProperty("target").GetProperty("name").GetString() ?? "").ToHashSet();
                var started = alerting.Except(_alerting).ToList();
                var ended = _alerting.Except(alerting).ToList();
                _alerting = alerting;
                Show(alerting.Count > 0 ? _alert : targets.Count > 0 ? _ok : _idle,
                    alerting.Count > 0 ? $"Alert: {string.Join(", ", alerting)}" : $"{targets.Count} target{(targets.Count == 1 ? "" : "s")}, no alerts");
                if (started.Count > 0)
                {
                    var sound = targets.Where(t => t.GetProperty("alerting").GetBoolean()).Select(t => t.TryGetProperty("alertSound", out var s) ? s.GetString() : null).FirstOrDefault();
                    await PlayAsync(sound);
                    _icon.ShowBalloonTip(10000, "Traceline alert", string.Join(", ", started), ToolTipIcon.Warning);
                }
                else if (ended.Count > 0)
                {
                    _icon.ShowBalloonTip(5000, "Traceline", $"Back to normal: {string.Join(", ", ended)}", ToolTipIcon.Info);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Show(_idle, "Traceline is not answering");
            }
        }

        /// <summary>Plays the alert's sound if it is a WAV (the built-in ones are); anything else falls back to the system sound.</summary>
        private async Task PlayAsync(string? sound)
        {
            if (sound == "none") return;
            try
            {
                var name = string.IsNullOrEmpty(sound) ? "Beep" : sound;
                using var response = await _http.GetAsync($"{_url}/api/sounds/{Uri.EscapeDataString(name)}");
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "audio/wav")
                {
                    using var player = new SoundPlayer(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
                    player.PlaySync();
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
            SystemSounds.Exclamation.Play();
        }

        private void Show(Icon icon, string text)
        {
            _icon.Icon = icon;
            _icon.Text = text.Length > 60 ? "Traceline: " + text[..49] + "…" : "Traceline: " + text;
            _status.Text = text;
        }

        private static Icon MakeIcon(Color colour)
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = new GraphicsPath();
                path.AddArc(0, 0, 12, 12, 180, 90); path.AddArc(19, 0, 12, 12, 270, 90); path.AddArc(19, 19, 12, 12, 0, 90); path.AddArc(0, 19, 12, 12, 90, 90);
                path.CloseFigure();
                using var fill = new SolidBrush(colour);
                g.FillPath(fill, path);
                using var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, [new PointF(4, 20), new PointF(10, 20), new PointF(13, 11), new PointF(18, 24), new PointF(21, 16), new PointF(28, 16)]);
            }
            var handle = bitmap.GetHicon();
            var icon = (Icon)Icon.FromHandle(handle).Clone();
            DestroyIcon(handle);
            return icon;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _showWait.Unregister(null); _showSignal.Dispose();
                _timer.Dispose(); _icon.Visible = false; _icon.Dispose(); _http.Dispose(); _window.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

#endif
