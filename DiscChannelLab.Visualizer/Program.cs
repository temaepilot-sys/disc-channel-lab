using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DiscChannelLab.Visualizer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Forms = System.Windows.Forms;

if (args.Contains("--self-test")) return SelfTests.Run();

string home = AppContext.BaseDirectory;
string cache = Path.Combine(home, "cache");
Directory.CreateDirectory(cache);
string log = Path.Combine(cache, "visualizer.log");
void Log(string message) => File.AppendAllText(log, $"{DateTime.Now:O} {message}{Environment.NewLine}");

try
{
    var settingsPath = Path.Combine(home, "AudioVisualizationSettings.json");
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    var settings = File.Exists(settingsPath)
        ? JsonSerializer.Deserialize<VisualizationSettings>(File.ReadAllText(settingsPath)) ?? new()
        : new VisualizationSettings();
    settings.Validate();
    if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, jsonOptions));

    string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = home });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    var app = builder.Build();
    var loader = new DemoAudioSource(settings);
    var preferences = new ViewerPreferencesStore(Path.Combine(home, "ViewerSettings.json"));
    int pipeArgument = Array.IndexOf(args, "--live-pipe");
    string? pipeName = pipeArgument >= 0 && pipeArgument + 1 < args.Length ? args[pipeArgument + 1] : null;
    if (pipeName is not null && (!pipeName.StartsWith("DiscChannelLab.SpaceSketch.v1.", StringComparison.Ordinal) || pipeName.Length > 160 || pipeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.'))))
        throw new ArgumentException("Invalid link pipe name.");
    using var live = new LiveReceiver(pipeName, settings, Log);
    live.Start();
    var gate = new SemaphoreSlim(1, 1);
    byte[]? demo = null;
    long heartbeat = Environment.TickCount64;
    bool connected = false;
    app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'";
        if (context.Request.Path.StartsWithSegments("/api") && context.Request.Headers["X-Visualizer-Token"] != secret)
        {
            context.Response.StatusCode = 403; return;
        }
        try { await next(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log(e.ToString());
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = e.Message });
            }
        }
    });
    foreach (var (url, file, mime) in new[] { ("/", "index.html", "text/html; charset=utf-8"), ("/app.js", "app.js", "text/javascript; charset=utf-8"), ("/style.css", "style.css", "text/css; charset=utf-8") })
        app.MapGet(url, () =>
        {
            var resource = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith(".Web." + file));
            return Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!, mime);
        });
    app.MapPost("/api/heartbeat", () => { heartbeat = Environment.TickCount64; connected = true; return Results.Ok(); });
    app.MapPost("/api/live", () => Results.Json(live.Snapshot()));
    app.MapPost("/api/settings/load", () => Results.Json(new { settings = preferences.Load() }));
    app.MapPost("/api/settings/save", async (HttpContext context) =>
    {
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 16384;
        var value = await context.Request.ReadFromJsonAsync<ViewerPreferences>(context.RequestAborted)
            ?? throw new InvalidDataException("Viewer settings are missing.");
        preferences.Save(value);
        return Results.Ok();
    });
    app.MapPost("/api/demo", async (HttpContext context) =>
    {
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            demo ??= await Task.Run(loader.Demo, context.RequestAborted);
            Log($"Demo loaded; FFT={settings.FftSize}, Hop={settings.HopSize}, Bands={settings.BandCount}");
            return Results.Bytes(demo, "application/octet-stream");
        }
        finally { gate.Release(); }
    });
    app.MapPost("/api/quit", () =>
    {
        _ = Task.Run(async () => { await Task.Delay(300); app.Lifetime.StopApplication(); });
        return Results.Ok();
    });
    await app.StartAsync();
    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    string page = address + "/#" + secret;
    File.WriteAllText(Path.Combine(cache, "endpoint.json"), JsonSerializer.Serialize(new { url = page, address, token = secret, pid = Environment.ProcessId }));
    Log("Started local visualizer");
    if (!args.Contains("--headless"))
    {
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe");
        if (File.Exists(edge))
        {
            var start = new ProcessStartInfo(edge) { UseShellExecute = false };
            start.ArgumentList.Add("--app=" + page);
            start.ArgumentList.Add("--user-data-dir=" + Path.Combine(cache, "browser"));
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--window-size=1360,900");
            Process.Start(start)?.Dispose();
        }
        else Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
    }
    _ = Task.Run(async () =>
    {
        while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Task.Delay(5000);
            if (Environment.TickCount64 - Interlocked.Read(ref heartbeat) > (connected ? 75000 : 180000))
            { app.Lifetime.StopApplication(); break; }
        }
    });
    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception e)
{
    Log(e.ToString());
    if (!args.Contains("--headless")) Forms.MessageBox.Show(e.Message, "DiscChannelLab — Space sketch");
    return 1;
}
