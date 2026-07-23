using Recorder.Api.Services;
using Recorder.Api.Services.MediaBot;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Run under the Windows Service Control Manager when installed as a service (see
// install-service.ps1). No-op when launched from a console, so `dotnet run` still works.
builder.Host.UseWindowsService();

// Persist the full log stream to a rolling daily file next to the exe (logs/recorder-<date>.log)
// so it's available while running headless as a service - the console stream is otherwise lost.
// Console sink is kept so `dotnet .\Recorder.Api.dll` still shows live logs when debugging.
// Path is anchored to AppContext.BaseDirectory (not the CWD, which is System32 under a service).
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "recorder-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true));

builder.Configuration
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ASPNETCORE_URLS"] = "http://127.0.0.1:5000",
    })
    .AddInMemoryCollection(LoadLocalConfiguration())
    .AddEnvironmentVariables();

var urls = builder.Configuration["ASPNETCORE_URLS"];
if (!string.IsNullOrWhiteSpace(urls))
{
    builder.WebHost.UseUrls(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

builder.Services.AddControllers();
builder.Services.AddHttpClient<NodeCallbackService>();
builder.Services.AddHttpClient(nameof(AppHostedMediaBridgeService));
builder.Services.AddSingleton<AzureSpeechTranscriptionService>();
builder.Services.AddSingleton<DebugPersistenceService>();
builder.Services.AddSingleton<AppHostedMediaReadinessService>();
builder.Services.AddSingleton<AppHostedMediaBridgeService>();
builder.Services.AddSingleton<IAppHostedMediaBridge>(sp => sp.GetRequiredService<AppHostedMediaBridgeService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppHostedMediaBridgeService>());
builder.Services.AddSingleton<TeamsMediaService>();
builder.Services.AddSingleton<MicrophoneCaptureService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MicrophoneCaptureService>());

// ----- Media bot (application-hosted media) registrations -----
// The bot only builds its Graph Communications client lazily on first join, so these
// are harmless when MEDIA_BOT_ENABLED is false (the recorder runs exactly as before).
builder.Services.AddSingleton(MediaBotOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHttpClient(nameof(MediaBotAuthenticationProvider));
builder.Services.AddSingleton<MediaBotAuthenticationProvider>();
builder.Services.AddSingleton<MediaBotService>();

var app = builder.Build();

// Wire the circular dependency after the container is built.
var teamsMediaService = app.Services.GetRequiredService<TeamsMediaService>();
var micCapture = app.Services.GetRequiredService<MicrophoneCaptureService>();
teamsMediaService.SetMicrophoneCaptureService(micCapture);

app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

app.MapControllers();

app.Run();

static Dictionary<string, string?> LoadLocalConfiguration()
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    // Walk up from the executable's own folder, NOT Directory.GetCurrentDirectory(). Under the
    // Windows Service Control Manager the working directory is C:\Windows\System32, so a CWD-based
    // search would never find .localConfigs / env/.env.local and the bot would start mis-configured.
    var current = new DirectoryInfo(AppContext.BaseDirectory);

    while (current is not null)
    {
        LoadEnvFile(Path.Combine(current.FullName, ".localConfigs"), values);
        LoadEnvFile(Path.Combine(current.FullName, "env", ".env.local"), values);
        current = current.Parent;
    }

    return values;
}

static void LoadEnvFile(string path, IDictionary<string, string?> values)
{
    if (!File.Exists(path))
    {
        return;
    }

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim().Trim('"');
        if (key.Length > 0 && !values.ContainsKey(key))
        {
            values[key] = value;
        }
    }
}
