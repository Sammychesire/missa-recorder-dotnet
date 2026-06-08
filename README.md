# Missa Recorder .NET

Standalone .NET recorder service for Missa Teams transcription experiments.

## Run Locally

```powershell
$env:RECORDER_SHARED_SECRET="your-shared-secret"
$env:BOT_ENDPOINT="https://your-node-bot-endpoint"
$env:AZURE_SPEECH_KEY="your-speech-key"
$env:AZURE_SPEECH_REGION="your-speech-region"

$env:RECORDER_MEDIA_SOURCE="teams"
$env:RECORDER_CAPTURE_MICROPHONE="false"
$env:RECORDER_CAPTURE_LOOPBACK="false"
$env:RECORDER_AUDIO_CHUNK_INTERVAL_MS="10000"

dotnet restore .\Recorder.Api.csproj
dotnet build .\Recorder.Api.csproj
dotnet .\bin\Debug\net10.0\Recorder.Api.dll
```

## Readiness

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:5000/api/recordings/media-readiness" -UseBasicParsing
```

`RECORDER_MEDIA_SOURCE=teams` waits for real Teams/app-hosted media frames posted to `/api/recordings/media-frame`.

`RECORDER_MEDIA_SOURCE=local` uses local Windows audio capture. That is useful for testing, but it is not real Teams app-hosted media.
