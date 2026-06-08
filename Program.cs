using Recorder.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());

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
