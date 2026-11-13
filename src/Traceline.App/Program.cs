using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting.WindowsServices;
using Traceline.App;
using Traceline.Core;
using Traceline.Core.Notifications;

const string ServiceName = "Traceline";

// ---------------------------------------------------------------- command line

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// How it runs. On Windows, started plainly, Traceline is a desktop program: its own window and a tray icon ("--tray"
// says the same and is what the shortcuts use). "--headless" serves the UI without a window, as the Windows service
// does and as the builds for other systems always do.
#if PORTABLE
const bool HasDesktopMode = false;
#else
const bool HasDesktopMode = true;
#endif
var commandLineUse = args.Contains("--help") || args.Contains("-h") || args.Contains("/?") || args.Contains("--install-service") || args.Contains("--uninstall-service");
var headless = args.Contains("--headless") || args.Contains("--open") || args.Contains("--service");
if (commandLineUse || headless) ParentConsole.Attach();

if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
{
    Console.WriteLine("""
        Traceline - continuous route tracing with a web UI

          Traceline                     open Traceline in its own window, with a tray icon (Windows)
          Traceline --minimized         the same, starting with only the tray icon
          Traceline --headless          no window: serve the UI at http://localhost:7878 until Ctrl+C
                                        (always so on Linux and macOS)
          Traceline --open              the same, and open the UI in the default browser
          Traceline --port 9000         use another port
          Traceline --data C:\path      keep the database in another folder
          Traceline --trace host        add a target on start (can be repeated)
          Traceline --install-service   install and start as a Windows service (run as administrator)
          Traceline --uninstall-service stop and remove the Windows service
        """);
    return 0;
}

if (args.Contains("--install-service") || args.Contains("--uninstall-service"))
{
    static int Sc(string arguments)
    {
        // No window of its own: Traceline has no console to share with it, and Windows would open one for each call.
        using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        return p.ExitCode;
    }
    if (args.Contains("--uninstall-service"))
    {
        Sc($"stop {ServiceName}");
        // "sc stop" returns before the service has gone. Wait for it, so that whoever runs this next
        // (the uninstaller, an upgrade) finds the program file no longer in use.
        try
        {
            using var service = new System.ServiceProcess.ServiceController(ServiceName);
            service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
        var me = Environment.ProcessId;
        for (var i = 0; i < 50 && Process.GetProcessesByName("Traceline").Any(p => p.Id != me && p.SessionId == 0); i++) Thread.Sleep(200);
        return Sc($"delete {ServiceName}");
    }
    // Registers the service, or points the one that is already there at this program (an update), and starts it.
    // What goes wrong is said on the error output, which the setup program shows.
    var exe = Environment.ProcessPath!;
    var exists = Sc($"query {ServiceName}") != 1060;   // 1060: there is no such service
    var code = Sc($"{(exists ? "config" : "create")} {ServiceName} binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"Traceline\"");
    if (code != 0)
    {
        Console.Error.WriteLine(code == 5
            ? "Setting up the Traceline service needs administrator rights."
            : $"Windows could not {(exists ? "update" : "create")} the Traceline service (error {code}).");
        return code;
    }
    Sc($"description {ServiceName} \"Traces network routes continuously and serves the Traceline web UI.\"");
    Sc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
    code = Sc($"start {ServiceName}");
    if (code == 1056) code = 0;   // it is running already
    if (code == 0)
    {
        // "sc start" returns while the service is still starting. And a service that cannot do its work (its port is
        // taken by another program, say) reports that it runs and stops within a second or two, so keep watching a little.
        try
        {
            using var service = new System.ServiceProcess.ServiceController(ServiceName);
            service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            for (var i = 0; i < 10 && code == 0; i++)
            {
                Thread.Sleep(500);
                service.Refresh();
                if (service.Status != System.ServiceProcess.ServiceControllerStatus.Running) code = 1067;   // "the process terminated unexpectedly"
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) { code = 1053; }
    }
    if (code != 0)
        Console.Error.WriteLine($"The Traceline service is set up but {(code == 1067 ? "stopped again within seconds" : "did not start")} (error {code}). " +
            "Often another program, or another copy of Traceline, is already using its port. The Windows event log, under Application, says why.");
    return code;
}

var isService = WindowsServiceHelpers.IsWindowsService();
var trayMode = HasDesktopMode && OperatingSystem.IsWindows() && !isService && (args.Contains("--tray") || !headless);
var dataDir = Arg("--data") ?? Path.Combine(
    Environment.GetFolderPath(isService ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData), "Traceline");
Directory.CreateDirectory(dataDir);
SecretProtector.KeyFolder = dataDir;

// Desktop mode. Started a second time, it only brings the window that is already there to the front. And if a
// Traceline (usually the service) already answers on the port, it shows the window and tray icon for that one.
// A session or workspace file on the command line (Windows passes one when it is double-clicked) is opened by the
// window: the page imports it as whoever is signed in there.
var openFile = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)
    && (a.EndsWith(".traceline", StringComparison.OrdinalIgnoreCase) || a.EndsWith(WorkspaceFile.Extension, StringComparison.OrdinalIgnoreCase)));
if (openFile is not null) openFile = Path.GetFullPath(openFile);
var startMinimized = args.Contains("--minimized") && openFile is null;
if (trayMode)
{
    if (Tray.ShowExisting(Arg("--port"), openFile)) return 0;
    var existingPort = int.TryParse(Arg("--port"), out var tp) ? tp : 7878;
    if (Tray.IsRunning(existingPort))
    {
        Tray.Run(existingPort, ownsEngine: false, startMinimized, Arg("--port"), openFile, stop: () => { });
        return 0;
    }
}

var store = new Store(Path.Combine(dataDir, "traceline.db"));
Store.Json.Converters.Add(new JsonStringEnumConverter());
var engine = new TraceEngine(store, new Probers());
var hosts = new HostDirectory(store, () => engine.Settings);
var views = new ViewBuilder(store, hosts, id => engine.Session(id) is { } s ? engine.ConfigFor(s.Target) : engine.Configs[0]);
var reports = new Reports(store, views);
var sender = new ChannelSender(ChannelSender.CreateHttpClient());
var alerts = new AlertService(engine, views, sender, reports);
var sounds = new Sounds(dataDir);

// Accounts. Version 1.0 had a single password; it becomes the first administrator.
var users = store.Get<List<UserAccount>>("users");
if (users.Count == 0 && engine.Settings.PasswordHash is { Length: > 0 } oldHash)
{
    users.Add(new UserAccount { Name = "admin", PasswordHash = oldHash, Role = "admin" });
    store.Set("users", users);
    engine.Settings.PasswordHash = null;
    engine.SaveSettings(engine.Settings);
}

var port = int.TryParse(Arg("--port"), out var p0) ? p0 : engine.Settings.Port;
var onNetwork = engine.Settings.ListenOnNetwork && users.Any(u => u.Role == "admin");

// HTTPS, when it is turned on: with the administrator's certificate file, or with one made here and kept in the data folder.
// A certificate that cannot be loaded must not stop the program; the UI then says what is wrong and stays on plain HTTP.
var httpsAtStart = (engine.Settings.HttpsEnabled, engine.Settings.HttpsPort, engine.Settings.HttpsOnly, engine.Settings.CertificatePath, engine.Settings.CertificatePassword);
var httpsPort = engine.Settings.HttpsPort;
X509Certificate2? certificate = null;
string? httpsError = null;
if (engine.Settings.HttpsEnabled)
{
    try
    {
        if (httpsPort == port) throw new ArgumentException("HTTPS needs a port of its own, different from the plain one.");
        certificate = engine.Settings.CertificatePath.Length > 0
            ? WebCertificate.Load(engine.Settings.CertificatePath, engine.Settings.CertificatePassword.Length > 0 ? SecretProtector.Unprotect(engine.Settings.CertificatePassword) : "")
            : WebCertificate.LoadOrCreateOwn(dataDir, engine.Settings, () => engine.SaveSettings(engine.Settings));
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException)
    {
        httpsError = ex.Message;
    }
}
var httpsOnly = certificate is not null && engine.Settings.HttpsOnly;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
// When run from the source tree the built UI is not next to the exe; this finds it there. In a published copy it does nothing.
builder.WebHost.UseStaticWebAssets();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 300_000_000;
    void Listen(int onPort, Action<ListenOptions> configure) { if (onNetwork) k.ListenAnyIP(onPort, configure); else k.ListenLocalhost(onPort, configure); }
    Listen(port, _ => { });
    if (certificate is not null) Listen(httpsPort, o => o.UseHttps(certificate));
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var app = builder.Build();

// ---------------------------------------------------------------- live updates (server-sent events)

var subscribers = new ConcurrentDictionary<Guid, (Channel<string> Queue, HashSet<long>? Only)>();
void Publish(object message, long? targetId = null)
{
    var json = JsonSerializer.Serialize(message, Store.Json);
    foreach (var (_, sub) in subscribers)
        if (targetId is null || sub.Only is null || sub.Only.Contains(targetId.Value)) sub.Queue.Writer.TryWrite(json);
}
engine.SampleTaken += (session, sample, info) => Publish(new { type = "sample", targetId = session.Target.Id, ts = sample.Ts, routeChanged = info.RouteChanged }, session.Target.Id);
engine.TargetsChanged += () => Publish(new { type = "targets" });
alerts.Fired += fired => Publish(new { type = "alert", fired.TargetId, fired.Target, fired.Rule, fired.State, fired.Text, fired.Ts, fired.Sound }, fired.TargetId);

// ---------------------------------------------------------------- sign-in, roles and share links

var logins = new ConcurrentDictionary<string, string>();
const string Cookie = "traceline_session";

static string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
    return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
}

static bool CheckPassword(string password, string stored)
{
    var parts = stored.Split(':');
    if (parts.Length != 2) return false;
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[0]), 200_000, HashAlgorithmName.SHA256, 32);
    return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[1]));
}

static bool TokenGiven(HttpContext ctx) => ctx.Request.Headers.ContainsKey("X-Share") || ctx.Request.Query.ContainsKey("share");

ShareLink? ShareOf(HttpContext ctx)
{
    var token = ctx.Request.Headers["X-Share"].FirstOrDefault() ?? ctx.Request.Query["share"].FirstOrDefault();
    if (string.IsNullOrEmpty(token)) return null;
    var share = store.Shares().FirstOrDefault(s => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s.Token), Encoding.UTF8.GetBytes(token)));
    return share is null || (share.ExpiresMs is long expires && expires < engine.Now) ? null : share;
}

UserAccount? UserOf(HttpContext ctx) =>
    ctx.Request.Cookies[Cookie] is { } c && logins.TryGetValue(c, out var name) ? users.FirstOrDefault(u => u.Name == name) : null;

// With no accounts at all, whoever can reach the UI (this computer only) is the administrator.
string? RoleOf(HttpContext ctx) => users.Count == 0 ? "admin" : UserOf(ctx)?.Role;

bool ShareAllows(ShareLink share, long targetId) => share.TargetIds.Count == 0 || share.TargetIds.Contains(targetId);

// "HTTPS only": browsers on other computers that arrive on the plain port are sent to the HTTPS one. This computer
// itself (the tray icon, scripts) and remote agents keep the plain port, because they cannot check a self-made certificate.
if (httpsOnly)
    app.Use(async (ctx, next) =>
    {
        var from = ctx.Connection.RemoteIpAddress;
        if (ctx.Request.IsHttps || from is null || IPAddress.IsLoopback(from) || ctx.Request.Path.StartsWithSegments("/api/agent")) { await next(); return; }
        ctx.Response.Redirect($"https://{ctx.Request.Host.Host}:{httpsPort}{ctx.Request.Path}{ctx.Request.QueryString}");
    });

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/", StringComparison.Ordinal) || path is "/api/login" or "/api/logout" or "/api/state" or "/api/agent/round") { await next(); return; }
    var get = HttpMethods.IsGet(ctx.Request.Method);
    if (TokenGiven(ctx))
    {
        // A share link may only read: the target list, target views and the live stream.
        var readable = path == "/api/targets" || path == "/api/stream" || (path.StartsWith("/api/targets/", StringComparison.Ordinal) && path.EndsWith("/view", StringComparison.Ordinal));
        if (ShareOf(ctx) is { } share && get && readable) { ctx.Items["share"] = share; await next(); return; }
        ctx.Response.StatusCode = 403;
        return;
    }
    var role = RoleOf(ctx);
    var adminOnly = path.StartsWith("/api/settings", StringComparison.Ordinal) || path.StartsWith("/api/users", StringComparison.Ordinal)
        || (path.StartsWith("/api/configs", StringComparison.Ordinal) && !get)
        || path.StartsWith("/api/channels/export", StringComparison.Ordinal);   // an export hands out every channel's password
    var allowed = role switch
    {
        "admin" => true,
        "editor" => !adminOnly,
        "viewer" => get && !adminOnly && !path.StartsWith("/api/channels", StringComparison.Ordinal) && !path.StartsWith("/api/configs", StringComparison.Ordinal),
        _ => false,
    };
    if (allowed) { await next(); return; }
    ctx.Response.StatusCode = role is null ? 401 : 403;
    if (role is not null) await ctx.Response.WriteAsJsonAsync(new { error = "Your account is not allowed to do that." });
});

app.UseDefaultFiles();
app.UseStaticFiles();

static IResult Problem(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);

// A configuration without the parts that are nobody's business but the administrator's.
static TraceConfig Public(TraceConfig c)
{
    var copy = JsonSerializer.Deserialize<TraceConfig>(JsonSerializer.Serialize(c, Store.Json), Store.Json)!;
    copy.AgentKey = ""; copy.AgentUrl = "";
    return copy;
}

// ---------------------------------------------------------------- state, sign-in, settings, accounts

app.MapGet("/api/state", (HttpContext ctx) =>
{
    var share = ShareOf(ctx);
    var tokenGiven = TokenGiven(ctx);
    var role = tokenGiven ? null : RoleOf(ctx);
    var s = engine.Settings;
    return Results.Json(new
    {
        version = AppInfo.Version,
        now = engine.Now,
        machine = Environment.MachineName,
        needsLogin = !tokenGiven && role is null,
        readOnly = tokenGiven || role == "viewer",
        shareInvalid = tokenGiven && share is null,
        shareName = share?.Name,
        role,
        user = UserOf(ctx)?.Name,
        hasUsers = users.Count > 0,
        listeningOnNetwork = onNetwork,
        imageSupported = TraceImage.Supported,
        settings = new
        {
            s.RawDays, s.RollupDays, s.OwnerLookup, s.ReverseDns, s.ListenOnNetwork, s.Port, s.SiteName, s.AutoSaveMode, s.AutoSaveMinutes,
            s.AllowRunProgram, s.CommentOnIpChange, agentKey = role == "admin" ? s.AgentKey : "",
            s.KeepAwake, s.HttpsEnabled, s.HttpsPort, s.HttpsOnly, certificatePath = role == "admin" ? s.CertificatePath : "", certificatePassword = "",
        },
        // What is actually running, as opposed to what the settings ask for after the next restart.
        https = new
        {
            active = certificate is not null, port = httpsPort, error = role == "admin" ? httpsError : null,
            selfMade = certificate is not null && engine.Settings.CertificatePath.Length == 0,
            expires = certificate is null ? (long?)null : new DateTimeOffset(certificate.NotAfter).ToUnixTimeMilliseconds(),
            passwordSet = s.CertificatePassword.Length > 0,
        },
        configs = role is null ? [] : engine.Configs.Select(Public),
        workspaces = role is null ? [] : engine.Workspaces,
        activeWorkspace = engine.ActiveWorkspace,
        dataFolder = role == "admin" ? dataDir : null,
        vendorList = role is not null && File.Exists(NetworkDiscovery.VendorFile(dataDir)),
    });
});

app.MapPost("/api/login", async (LoginRequest body, HttpContext ctx) =>
{
    if (users.Count == 0) return Results.Ok();
    var user = users.FirstOrDefault(u => string.Equals(u.Name, body.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
    if (user is null || !CheckPassword(body.Password ?? "", user.PasswordHash))
    {
        await Task.Delay(1500);   // slows down password guessing
        return Problem("Wrong name or password.", 401);
    }
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    logins[token] = user.Name;
    ctx.Response.Cookies.Append(Cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(30), Secure = ctx.Request.IsHttps });
    return Results.Ok();
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies[Cookie] is { } c) logins.TryRemove(c, out _);
    ctx.Response.Cookies.Delete(Cookie);
    return Results.Ok();
});

app.MapPut("/api/settings", (SettingsRequest body) =>
{
    var s = engine.Settings;
    if (body.ListenOnNetwork && !users.Any(u => u.Role == "admin")) return Problem("Create an administrator account before opening the UI to the network.");
    if (body.AutoSaveMode is not ("always" or "after" or "selected")) return Problem("Unknown auto-save mode.");
    s.RawDays = Math.Clamp(body.RawDays, 1, 3650); s.RollupDays = Math.Clamp(body.RollupDays, 1, 36500);
    s.OwnerLookup = body.OwnerLookup; s.ReverseDns = body.ReverseDns; s.ListenOnNetwork = body.ListenOnNetwork;
    s.Port = Math.Clamp(body.Port, 1, 65535); s.SiteName = body.SiteName ?? "";
    s.AutoSaveMode = body.AutoSaveMode; s.AutoSaveMinutes = Math.Clamp(body.AutoSaveMinutes, 1, 100_000);
    var certificatePath = body.CertificatePath?.Trim().Trim('"') ?? "";
    // An empty password box means "leave it as it is", unless the file itself was taken away.
    var certificatePassword = certificatePath.Length == 0 ? "" : body.CertificatePassword is { Length: > 0 } typed ? SecretProtector.Protect(typed) : s.CertificatePassword;
    if (body.HttpsEnabled)
    {
        if (body.HttpsPort == body.Port) return Problem("HTTPS needs a port of its own, different from the plain one.");
        if (certificatePath.Length > 0)
        {
            try { WebCertificate.Load(certificatePath, certificatePassword.Length > 0 ? SecretProtector.Unprotect(certificatePassword) : "").Dispose(); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException) { return Problem(ex.Message); }
        }
    }
    s.AllowRunProgram = body.AllowRunProgram; s.CommentOnIpChange = body.CommentOnIpChange; s.AgentKey = body.AgentKey?.Trim() ?? "";
    s.KeepAwake = body.KeepAwake;
    s.HttpsEnabled = body.HttpsEnabled; s.HttpsPort = Math.Clamp(body.HttpsPort, 1, 65535); s.HttpsOnly = body.HttpsOnly;
    s.CertificatePath = certificatePath; s.CertificatePassword = certificatePassword;
    engine.SaveSettings(s);
    return Results.Ok(new
    {
        restartNeeded = s.Port != port || (s.ListenOnNetwork && users.Any(u => u.Role == "admin")) != onNetwork
            || httpsAtStart != (s.HttpsEnabled, s.HttpsPort, s.HttpsOnly, s.CertificatePath, s.CertificatePassword),
    });
});

app.MapGet("/api/users", () => users.Select(u => new { u.Name, u.Role }));
app.MapPost("/api/users", (UserRequest body) =>
{
    var name = body.Name?.Trim() ?? "";
    if (name.Length == 0) return Problem("Enter a name.");
    if (body.Role is not ("admin" or "editor" or "viewer")) return Problem("Choose a role.");
    var existing = users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
    if (existing is null && (body.Password?.Length ?? 0) < 8) return Problem("Use a password of at least 8 characters.");
    if (body.Password is { Length: > 0 and < 8 }) return Problem("Use a password of at least 8 characters.");
    var next = users.Where(u => u != existing).ToList();
    next.Add(new UserAccount { Name = existing?.Name ?? name, Role = body.Role, PasswordHash = body.Password is { Length: > 0 } pw ? HashPassword(pw) : existing!.PasswordHash });
    if (!next.Any(u => u.Role == "admin")) return Problem("At least one account must be an administrator.");
    users = next.OrderBy(u => u.Name).ToList();
    store.Set("users", users);
    return Results.Ok();
});
app.MapDelete("/api/users/{name}", (string name) =>
{
    var next = users.Where(u => !string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    if (next.Count > 0 && !next.Any(u => u.Role == "admin")) return Problem("At least one account must be an administrator.");
    if (next.Count == 0 && engine.Settings.ListenOnNetwork) return Problem("Turn off network access before removing the last account.");
    users = next;
    store.Set("users", users);
    foreach (var (token, user) in logins) if (string.Equals(user, name, StringComparison.OrdinalIgnoreCase)) logins.TryRemove(token, out _);
    return Results.Ok();
});

// ---------------------------------------------------------------- configurations, interfaces, workspaces, summaries

app.MapGet("/api/configs", () => engine.Configs);
app.MapPut("/api/configs", (List<TraceConfig> configs) =>
{
    try { engine.SaveConfigs(configs); return Results.Ok(); }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});

app.MapGet("/api/interfaces", () => NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => !a.Address.IsIPv6LinkLocal).Select(a => new { address = a.Address.ToString(), name = n.Name })));

app.MapPost("/api/workspaces", (NameRequest body) =>
{
    if (string.IsNullOrWhiteSpace(body.Name)) return Problem("Enter a name.");
    var ws = new Workspace { Id = Guid.NewGuid().ToString("N"), Name = body.Name.Trim() };
    engine.SaveWorkspaces([.. engine.Workspaces, ws]);
    return Results.Json(ws);
});
app.MapPatch("/api/workspaces/{id}", (string id, NameRequest body) =>
{
    if (string.IsNullOrWhiteSpace(body.Name)) return Problem("Enter a name.");
    engine.SaveWorkspaces(engine.Workspaces.Select(w => w.Id == Id(id) ? new Workspace { Id = w.Id, Name = body.Name.Trim() } : w).ToList());
    return Results.Ok();
});
app.MapPost("/api/workspaces/{id}/activate", async (string id) =>
{
    try { await engine.SwitchWorkspaceAsync(Id(id)); return Results.Ok(); }
    catch (KeyNotFoundException) { return Problem("No such workspace.", 404); }
});
app.MapDelete("/api/workspaces/{id}", async (string id) =>
{
    if (Id(id) == engine.ActiveWorkspace) return Problem("Switch to another workspace before deleting this one.");
    foreach (var s in engine.AllSessions.Where(s => s.Target.WorkspaceId == Id(id)).ToList()) await engine.RemoveAsync(s.Target.Id);
    engine.SaveWorkspaces(engine.Workspaces.Where(w => w.Id != Id(id)).ToList());
    store.Set("summaries", store.Get<List<SummaryDef>>("summaries").Where(s => s.WorkspaceId != Id(id)).ToList());
    return Results.Ok();
});
app.MapGet("/api/workspaces/{id}/export", (string id) =>
{
    if (engine.Workspaces.FirstOrDefault(w => w.Id == Id(id)) is not { } ws) return Problem("No such workspace.", 404);
    var targets = engine.AllSessions.Where(s => s.Target.WorkspaceId == ws.Id && !s.Target.Closed).Select(s => s.Target).ToList();
    var doc = new WorkspaceFile(WorkspaceFile.FormatName, ws.Name,
        targets.Select(t => new WorkspaceTarget(t.Input, t.Name, t.IntervalMs, t.FinalHopOnly, t.Paused, engine.ConfigFor(t).Name)).ToList(),
        store.Get<List<SummaryDef>>("summaries").Where(s => s.WorkspaceId == ws.Id)
            .Select(s => new WorkspaceSummary(s.Name, s.Items.Select(i => new WorkspaceSummaryItem(targets.FindIndex(t => t.Id == i.TargetId), i.Hop)).Where(i => i.Target >= 0).ToList())).ToList());
    return Results.File(JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions(Store.Json) { WriteIndented = true }), "application/json", $"{ws.Name}{WorkspaceFile.Extension}");
});
app.MapPost("/api/workspaces/import", (WorkspaceFile doc) =>
{
    // Files written before 1.3.6 (*.traceline-workspace.json) say nothing about their format; they are read as before.
    if (doc.Format is not (null or WorkspaceFile.FormatName) || string.IsNullOrWhiteSpace(doc.Name) || doc.Targets is null) return Problem("That file is not a Traceline workspace.");
    var wsName = doc.Name.Trim();
    if (engine.Workspaces.Any(w => w.Name == wsName)) wsName += " (imported)";
    var ws = new Workspace { Id = Guid.NewGuid().ToString("N"), Name = wsName };
    engine.SaveWorkspaces([.. engine.Workspaces, ws]);
    var created = new List<Target>();
    foreach (var t in doc.Targets)
    {
        try
        {
            var target = Target.Parse(t.Input);
            target.Name = string.IsNullOrWhiteSpace(t.Name) ? target.Name : t.Name;
            target.IntervalMs = Math.Clamp(t.IntervalMs, 500, 3_600_000); target.Paused = t.Paused;
            if (target.Kind == ProbeKind.Icmp) target.FinalHopOnly = t.FinalHopOnly;
            target.ConfigId = engine.Configs.FirstOrDefault(c => c.Name == t.Config && c.Id != TraceConfig.DefaultId)?.Id ?? "";
            target.WorkspaceId = ws.Id; target.CreatedMs = engine.Now;
            store.SaveTarget(target);
            engine.Adopt(target);
            created.Add(target);
        }
        catch (ArgumentException) { created.Add(new Target()); }
    }
    var summaries = store.Get<List<SummaryDef>>("summaries");
    foreach (var s in doc.Summaries ?? [])
        summaries.Add(new SummaryDef { Name = s.Name, WorkspaceId = ws.Id, Items = s.Items.Where(i => i.Target >= 0 && i.Target < created.Count && created[i.Target].Id != 0).Select(i => new SummaryItem(created[i.Target].Id, i.Hop)).ToList() });
    store.Set("summaries", summaries);
    return Results.Json(ws);
});

app.MapGet("/api/summaries", () => store.Get<List<SummaryDef>>("summaries").Where(s => s.WorkspaceId == engine.ActiveWorkspace));
app.MapPut("/api/summaries", (List<SummaryDef> list) =>
{
    if (list.Any(s => string.IsNullOrWhiteSpace(s.Name))) return Problem("Every summary needs a name.");
    foreach (var s in list) s.WorkspaceId = engine.ActiveWorkspace;
    store.Set("summaries", store.Get<List<SummaryDef>>("summaries").Where(s => s.WorkspaceId != engine.ActiveWorkspace).Concat(list).ToList());
    Publish(new { type = "targets" });
    return Results.Ok();
});

app.MapPut("/api/hosts/{ip}", (string ip, AliasRequest body) =>
{
    if (!IPAddress.TryParse(ip, out _)) return Problem("Not an address.");
    if (body.Alias is not null) hosts.SetAlias(ip, body.Alias);
    if (body.Notes is not null) hosts.SetNotes(ip, body.Notes);
    return Results.Ok();
});
app.MapPost("/api/hosts/{ip}/resolve", (string ip) =>
{
    if (!IPAddress.TryParse(ip, out _)) return Problem("Not an address.");
    hosts.Refresh(ip);
    return Results.Ok();
});

// Packets per second over the last few seconds, and the "keep the computer awake" setting, which Windows wants to be told again and again.
var packetsPerSecond = 0.0;
var sentBefore = RoundProbe.Sent;
using var rateTimer = new System.Threading.Timer(_ =>
{
    var sent = RoundProbe.Sent;
    packetsPerSecond = (sent - sentBefore) / 5.0;
    sentBefore = sent;
    if (engine.Settings.KeepAwake && engine.Sessions.Any(s => !s.Target.Paused)) Awake.StayAwake();
}, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
app.MapGet("/api/rate", () => new { pps = Math.Round(packetsPerSecond, 1) });

// ---------------------------------------------------------------- targets and sessions

object Summary(TraceSession session)
{
    var now = engine.Now;
    var agg = new Agg();
    foreach (var (_, rtt) in session.Recent(now - 600_000)) agg.Add(rtt);
    double? R(double v) => double.IsNaN(v) ? null : Math.Round(v, 2);
    var cfg = session.Config;
    return new
    {
        target = session.Target,
        error = session.Error,
        lastTs = session.LastSample?.Ts,
        hops = session.LastSample is { FinalOnly: false } last ? last.Rtts.Length : 0,
        cur = session.LastSample is { } l ? R(l.Rtts[^1]) : null,
        avg = R(agg.Avg), lossPct = R(agg.LossPct), jitter = R(agg.Jitter), mos = R(agg.Mos), count = agg.N,
        alerting = alerts.Rules.Any(r => alerts.IsActive(r.Id, session.Target.Id)),
        alertSound = alerts.ActiveSound(session.Target.Id),
        warnMs = cfg.WarnMs, badMs = cfg.BadMs, configName = cfg.Name,
    };
}

app.MapGet("/api/targets", (HttpContext ctx, long? id) =>
{
    var share = ctx.Items["share"] as ShareLink;
    // A single closed session can be asked for by id, so that it can still be looked at.
    var sessions = id is long one && share is null ? engine.AllSessions.Where(s => s.Target.Id == one) : engine.Sessions;
    return sessions.Where(s => share is null || ShareAllows(share, s.Target.Id)).Select(Summary);
});

app.MapPost("/api/targets", (AddTargetRequest body) =>
{
    try
    {
        var added = new List<Target>();
        foreach (var line in (body.Input ?? "").Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!line.StartsWith('#')) added.Add(engine.Add(line, body.IntervalMs, body.ConfigId));
        return added.Count == 0 ? Problem("Enter a host name or address.") : Results.Json(added);
    }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});

app.MapPatch("/api/targets/{id:long}", async (long id, PatchTargetRequest body) =>
{
    if (engine.Session(id) is null) return Problem("No such target.", 404);
    if (body.ConfigId is { } cid && engine.Configs.All(c => c.Id != cid)) return Problem("No such configuration.");
    await engine.UpdateAsync(id, t =>
    {
        if (body.Name is { Length: > 0 } name) t.Name = name;
        if (body.IntervalMs is int interval) t.IntervalMs = interval;
        if (body.Paused is bool paused) t.Paused = paused;
        if (body.FinalHopOnly is bool finalOnly) t.FinalHopOnly = finalOnly;
        if (body.FollowDns is bool follow) t.FollowDns = follow;
        if (body.ConfigId is { } configId) t.ConfigId = configId;
        if (body.Keep is bool keep) t.Keep = keep;
        if (body.Closed is false) { t.Closed = false; t.WorkspaceId = engine.ActiveWorkspace; }
    });
    return Results.Ok();
});

app.MapPost("/api/targets/{id:long}/close", async (long id) =>
{
    if (engine.Session(id) is null) return Problem("No such target.", 404);
    return Results.Json(new { kept = await engine.CloseAsync(id) });
});

app.MapDelete("/api/targets/{id:long}", async (long id) => { await engine.RemoveAsync(id); return Results.Ok(); });

app.MapDelete("/api/targets/{id:long}/data", (long id, long? before) =>
{
    store.DeleteData(id, before ?? long.MaxValue);
    if (before is null) engine.Session(id)?.ResetRoute();
    return Results.Ok();
});

app.MapGet("/api/sessions", () => engine.AllSessions.Select(s =>
{
    var range = store.DataRange(s.Target.Id);
    var t = s.Target;
    var state = t.Closed ? "Closed" : t.WorkspaceId != engine.ActiveWorkspace ? "Other workspace" : t.Paused ? "Paused" : "Tracing";
    return new { target = t, state, workspace = engine.Workspaces.FirstOrDefault(w => w.Id == t.WorkspaceId)?.Name, first = range?.First, last = range?.Last, samples = range?.Count ?? 0 };
}));

app.MapPost("/api/sessions/import", async (HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    buffer.Position = 0;
    try
    {
        var target = SessionArchive.Import(store, buffer, engine.ActiveWorkspace);
        engine.Adopt(target);
        return Results.Json(target);
    }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});

app.MapPost("/api/sessions/import-text", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    try
    {
        var target = SessionArchive.ImportText(store, await reader.ReadToEndAsync(), engine.ActiveWorkspace);
        engine.Adopt(target);
        return Results.Json(target);
    }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});

app.MapGet("/api/targets/{id:long}/view", (long id, long from, long to, long? focusFrom, long? focusTo, string? hops, int? buckets, HttpContext ctx) =>
{
    if (ctx.Items["share"] is ShareLink share && !ShareAllows(share, id)) return Results.StatusCode(403);
    if (engine.Session(id) is null) return Problem("No such target.", 404);
    if (to <= from) return Problem("The time range is empty.");
    var view = views.Build(id, from, to, focusFrom ?? from, focusTo ?? to, HopList(hops), buckets ?? 600);
    return Results.Json(view with { Config = Public(view.Config) });
});

// ---------------------------------------------------------------- comments

app.MapPost("/api/targets/{id:long}/events", (long id, CommentRequest body) =>
    string.IsNullOrWhiteSpace(body.Text) ? Problem("Enter some text.") : Results.Json(store.AddEvent(id, body.Ts ?? engine.Now, "comment", body.Text.Trim())));
app.MapPut("/api/events/{id:long}", (long id, CommentRequest body) => { store.UpdateEvent(id, (body.Text ?? "").Trim()); return Results.Ok(); });
app.MapDelete("/api/events/{id:long}", (long id) => { store.DeleteEvent(id); return Results.Ok(); });

// ---------------------------------------------------------------- exports

app.MapGet("/api/targets/{id:long}/export/{kind}", (long id, string kind, long from, long to, string? hops, int? group, bool? utc) =>
{
    if (engine.Session(id)?.Target is not { } target) return Problem("No such target.", 404);
    var name = string.Concat(target.Name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
    var stamp = DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(to, engine.Now)).ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
    IResult File(string content, string type, string extension) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(), type, $"{name}-{stamp}.{extension}");
    var hopList = HopList(hops);
    switch (kind)
    {
        case "samples.csv": return File(reports.SamplesCsv(id, from, to, utc ?? false), "text/csv", "csv");
        case "stats.csv": return File(reports.StatsCsv(id, from, to, group ?? 60, utc ?? false), "text/csv", "stats.csv");
        case "trace.txt": return Results.Text(reports.Text(target, from, to));
        case "report.html": return File(reports.Html(target, from, to, hopList), "text/html", "html");
        case "report": return Results.Content(reports.Html(target, from, to, hopList), "text/html", Encoding.UTF8);
        case "session": return Results.File(SessionArchive.Export(store, target, from, to), "application/gzip", $"{name}-{stamp}.traceline");
        case "image.png" or "image.gif" or "image.bmp":
            if (!TraceImage.Supported) return Problem("Images can only be made on Windows.");
            var format = kind[6..];
            return Results.File(TraceImage.Render(target, views.Build(id, from, to, from, to, hopList.Contains(0) ? hopList : [.. hopList, 0], ViewBuilder.BucketsFor(from, to, target.IntervalMs)), format),
                $"image/{format}", $"{name}-{stamp}.{format}");
        default: return Problem("Unknown export.", 404);
    }
});

// ---------------------------------------------------------------- alerts and channels

app.MapGet("/api/alerts", () => new
{
    rules = alerts.Rules.Select(r => new { rule = r, activeOn = engine.Sessions.Where(s => alerts.IsActive(r.Id, s.Target.Id)).Select(s => s.Target.Id) }),
    deliveries = alerts.Deliveries.Take(100),
    recent = store.Events(null, engine.Now - 7L * 86_400_000, long.MaxValue).Where(e => e.Kind == "alert").TakeLast(100).Reverse(),
    variables = AlertService.Variables,
});
// The alert log: every time an alert started, ended or repeated in the last N days (all there is when no number is
// given), newest first, with the name of the target even when it is closed now.
app.MapGet("/api/alerts/log", (int? days) =>
{
    var names = engine.AllSessions.ToDictionary(s => s.Target.Id, s => s.Target.Name);
    var from = days is > 0 ? engine.Now - days.Value * 86_400_000L : 0;
    return store.Events(null, from, long.MaxValue, 5000, "alert").AsEnumerable().Reverse()
        .Select(e => new { e.Id, e.TargetId, e.Ts, e.Text, target = names.GetValueOrDefault(e.TargetId) });
});
// The user guide as it was when this program was built, for the Help page.
app.MapGet("/api/guide", () =>
{
    using var stream = typeof(Program).Assembly.GetManifestResourceStream("USER-GUIDE.md");
    if (stream is null) return Problem("The guide is not part of this build.", 404);
    using var reader = new StreamReader(stream);
    return Results.Text(reader.ReadToEnd(), "text/markdown", Encoding.UTF8);
});
app.MapPut("/api/alerts", (List<AlertRule> rules) =>
{
    if (rules.Any(r => string.IsNullOrWhiteSpace(r.Name))) return Problem("Every alert needs a name.");
    alerts.SaveRules(rules);
    return Results.Ok();
});
app.MapGet("/api/channels", () => Results.Json(alerts.Channels.ToList()));
app.MapPut("/api/channels", (List<ChannelConfig> channels) => { alerts.SaveChannels(channels); return Results.Ok(); });
app.MapPost("/api/channels/test", async (ChannelConfig channel) =>
{
    try { return Results.Json(new { ok = true, text = await alerts.TestAsync(channel) }); }
    catch (DeliveryException ex) { return Results.Json(new { ok = false, text = ex.Message }); }
});
// What the page needs to set channels up: the mail providers it can fill the server settings in for, and for each
// channel with a webhook address the host name in it. The address itself is a secret and never goes to the page.
app.MapGet("/api/channels/setup", () =>
{
    var hosts = new Dictionary<string, string>();
    foreach (var c in alerts.Channels) hosts[c.Id] = ChannelSender.UrlHost(c);
    return Results.Json(new { providers = SmtpProvider.All, hosts });
});
app.MapPost("/api/channels/telegram-chat", async (TelegramChatRequest body, CancellationToken ct) => Results.Json(await sender.FindTelegramChatAsync(body.BotToken, ct)));
// Channel export files (*.smchannels), which Service Monitor reads and writes too. The secrets leave this computer only
// inside the file, encrypted with the password in the request.
app.MapPost("/api/channels/export", (ChannelExportRequest body) =>
{
    if (alerts.Channels.Count == 0) return Problem("There are no channels to export yet.");
    try
    {
        var now = DateTimeOffset.Now;
        return Results.Json(new
        {
            name = $"{AppInfo.MachineName} channels {now:yyyy-MM-dd}{ChannelTransfer.FileExtension}",
            file = ChannelTransfer.Export(alerts.Channels, body.Password ?? "", now),
            count = alerts.Channels.Count,
        });
    }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});
app.MapPost("/api/channels/import", (ChannelImportRequest body) =>
{
    try
    {
        var channels = alerts.Channels.ToList();
        var result = ChannelTransfer.Merge(channels, ChannelTransfer.Read(body.File ?? "", body.Password ?? ""));
        alerts.SaveChannels(channels);
        return Results.Json(result);
    }
    catch (InvalidDataException ex) { return Problem(ex.Message); }
});

// ---------------------------------------------------------------- alert sounds

app.MapGet("/api/sounds", () => sounds.Names());
app.MapGet("/api/sounds/{name}", (string name) => sounds.Get(name) is var (bytes, type) ? Results.File(bytes, type) : Problem("No such sound.", 404));
app.MapPost("/api/sounds/{name}", async (string name, HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    try { sounds.Save(name, buffer.ToArray()); return Results.Ok(); }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});
app.MapDelete("/api/sounds/{name}", (string name) => { sounds.Delete(name); return Results.Ok(); });

// ---------------------------------------------------------------- share links

app.MapGet("/api/shares", () => store.Shares());
app.MapPost("/api/shares", (ShareRequest body) =>
{
    var share = new ShareLink
    {
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant(),
        Name = string.IsNullOrWhiteSpace(body.Name) ? "Shared view" : body.Name.Trim(),
        TargetIds = body.TargetIds ?? [], CreatedMs = engine.Now,
        ExpiresMs = body.ExpiresInHours is > 0 ? engine.Now + (long)(body.ExpiresInHours.Value * 3_600_000) : null,
    };
    store.SaveShare(share);
    return share;
});
app.MapDelete("/api/shares/{token}", (string token) => { store.DeleteShare(token); return Results.Ok(); });

// ---------------------------------------------------------------- local network discovery

app.MapGet("/api/discovery/subnets", () => NetworkDiscovery.Subnets());
app.MapPost("/api/discovery/scan", async (DiscoveryOptions options, CancellationToken ct) =>
{
    try { return Results.Json(await NetworkDiscovery.ScanAsync(options, dataDir, ct)); }
    catch (ArgumentException ex) { return Problem(ex.Message); }
});
app.MapPost("/api/discovery/vendors", async () =>
{
    // The IEEE's public list of hardware-address prefixes; fetched only when someone asks for it here.
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Traceline/" + AppInfo.Version);
        var text = await http.GetStringAsync("https://standards-oui.ieee.org/oui/oui.txt");
        if (!text.Contains("(hex)")) return Problem("The downloaded list was not in the expected format.");
        await File.WriteAllTextAsync(NetworkDiscovery.VendorFile(dataDir), text);
        return Results.Json(new { vendors = NetworkDiscovery.Vendors(dataDir).Count });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return Problem($"Could not download the list: {ex.Message}"); }
});

// ---------------------------------------------------------------- remote agent

app.MapPost("/api/agent/round", async (AgentRequest body, HttpContext ctx, CancellationToken ct) =>
{
    var key = engine.Settings.AgentKey;
    var given = ctx.Request.Headers["X-Agent-Key"].FirstOrDefault() ?? "";
    if (key.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(given))) return Results.StatusCode(403);
    if (body.Config.PacketType == PacketType.Agent) return Problem("An agent cannot pass a trace on to another agent.");
    try
    {
        IPAddress? address = null;
        if (body.Target.Kind is ProbeKind.Icmp or ProbeKind.Tcp)
        {
            // The name is looked up here, so that the trace follows the route this machine would take.
            var found = IPAddress.TryParse(body.Target.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(body.Target.Host, ct);
            address = found.FirstOrDefault(a => body.Target.Family != "ipv6" && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? found.FirstOrDefault();
            if (IPAddress.TryParse(body.Address, out var asked) && found.Contains(asked)) address = asked;
        }
        var results = await RoundProbe.RunAsync(engine.Prober, body.Target, body.Config, address, body.FinalOnly, Math.Clamp(body.ProbeLength, 1, 64), ct);
        return Results.Json(results.Select(r => new AgentHop(r.Responder?.ToString(), double.IsNaN(r.RttMs) ? null : r.RttMs, r.Reached)));
    }
    catch (ProbeSetupException ex) { return Problem(ex.Message); }
    catch (System.Net.Sockets.SocketException ex) { return Problem($"The agent could not look up {body.Target.Host}: {ex.Message}"); }
});

// ---------------------------------------------------------------- live stream

app.MapGet("/api/stream", async (HttpContext ctx) =>
{
    var share = ctx.Items["share"] as ShareLink;
    var id = Guid.NewGuid();
    var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    subscribers[id] = (queue, share is { TargetIds.Count: > 0 } ? [.. share.TargetIds] : null);
    // Open streams must end when the program stops, or shutdown waits for them until it times out.
    using var ending = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, app.Lifetime.ApplicationStopping);
    var ended = ending.Token;
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ended);
        await ctx.Response.Body.FlushAsync(ended);
        while (!ended.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ended);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var message = await queue.Reader.ReadAsync(wait.Token);
                await ctx.Response.WriteAsync($"data: {message}\n\n", ended);
            }
            catch (OperationCanceledException) when (!ended.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": keep-alive\n\n", ended);
            }
            await ctx.Response.Body.FlushAsync(ended);
        }
    }
    catch (OperationCanceledException) { }
    finally { subscribers.TryRemove(id, out _); }
});

app.MapFallbackToFile("index.html");

// ---------------------------------------------------------------- run

engine.Start();
foreach (var i in Enumerable.Range(0, args.Length).Where(i => args[i] == "--trace" && i + 1 < args.Length))
{
    try { engine.Add(args[i + 1]); } catch (ArgumentException) { /* already there or not valid */ }
}

using var pruneTimer = new System.Threading.Timer(_ =>
{
    try { store.Prune(engine.Now - engine.Settings.RawDays * 86_400_000L, engine.Now - engine.Settings.RollupDays * 86_400_000L); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not remove old data"); }
}, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://localhost:{port}";
    if (!isService && !trayMode) Console.WriteLine($"Traceline {AppInfo.Version} is running. Open {url}{(certificate is not null ? $" or https://localhost:{httpsPort}" : "")}  (data: {dataDir})");
    if (httpsError is not null && !isService && !trayMode) Console.WriteLine($"HTTPS is off: {httpsError}");
    if (args.Contains("--open")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    if (trayMode) new Thread(() => Tray.Run(port, ownsEngine: true, startMinimized, Arg("--port"), openFile, stop: () => app.Lifetime.StopApplication())) { IsBackground = true, Name = "Tray" }.Start();
});

try { await app.RunAsync(); }
catch (OperationCanceledException) { /* shutdown took longer than the host allows; still save what is in memory */ }
await engine.DisposeAsync();
store.Dispose();
return 0;

static string Id(string id) => id == "main" ? "" : id;   // the first workspace has an empty id, which cannot be put in a URL

static int[] HopList(string? hops) => (hops ?? "0").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(h => int.TryParse(h, out var n) ? n : -1).Where(n => n is >= 0 and <= 64).Distinct().Take(40).ToArray();

record LoginRequest(string? Name, string? Password);
record UserRequest(string? Name, string? Password, string? Role);
record NameRequest(string? Name);
record AliasRequest(string? Alias, string? Notes);
record ChannelImportRequest(string? File, string? Password);
record ChannelExportRequest(string? Password);
record TelegramChatRequest(string? BotToken);
record AddTargetRequest(string? Input, int? IntervalMs, string? ConfigId);
record PatchTargetRequest(string? Name, int? IntervalMs, bool? Paused, bool? FinalHopOnly, bool? FollowDns, string? ConfigId, bool? Keep, bool? Closed);
record CommentRequest(long? Ts, string? Text);
record ShareRequest(string? Name, List<long>? TargetIds, double? ExpiresInHours);
record SettingsRequest(int RawDays, int RollupDays, bool OwnerLookup, bool ReverseDns, bool ListenOnNetwork, int Port, string? SiteName,
    string AutoSaveMode, int AutoSaveMinutes, bool AllowRunProgram, bool CommentOnIpChange, string? AgentKey,
    bool KeepAwake = false, bool HttpsEnabled = false, int HttpsPort = 7879, bool HttpsOnly = false, string? CertificatePath = null, string? CertificatePassword = null);
record WorkspaceTarget(string Input, string? Name, int IntervalMs, bool FinalHopOnly, bool Paused, string? Config);
record WorkspaceSummaryItem(int Target, int Hop);
record WorkspaceSummary(string Name, List<WorkspaceSummaryItem> Items);
/// <summary>
/// A workspace as a file of its own (*.traceline-workspace): its targets with their settings, and its summary screens.
/// What was recorded is not in it; that travels as session files (*.traceline).
/// </summary>
record WorkspaceFile(string? Format, string Name, List<WorkspaceTarget> Targets, List<WorkspaceSummary>? Summaries)
{
    public const string FormatName = "traceline-workspace", Extension = ".traceline-workspace";
}
