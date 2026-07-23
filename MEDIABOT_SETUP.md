# Media Bot + Recorder — combined app (run once on the VM)

This project is now **two things in one process**:

1. **Recorder** (existing) — receives audio, runs Azure Speech, posts transcripts to Node.
2. **Media bot** (new) — joins the Teams call with **application-hosted media**, receives
   **per-participant** audio, maps each stream to a participant identity, and hands WAV
   chunks to the recorder **in-process**.

One `dotnet run` starts both. The media bot only activates when `MEDIA_BOT_ENABLED=true`;
otherwise the recorder behaves exactly as before.

> ⚠️ **This is a working scaffold, not a verified build.** The Microsoft media SDK is
> Windows-only, native, and version-sensitive. Lines marked `// [SDK]` in the code are the
> spots most likely to need a small adjustment on the **first build against the restored
> packages**. Expect to iterate against a live call. See "Status / what to expect" below.

---

## 1. Prerequisites on the VM (the hard part — do this first)

The media platform needs real, public, certificate-backed media — **devtunnels cannot
carry media**, only signaling. On the Azure VM (`missa-media-bot-aueast…`, `20.92.101.184`):

1. **TLS certificate** for the VM's FQDN
   `missa-media-bot-aueast.australiaeast.cloudapp.azure.com`, installed in
   `LocalMachine\My`. Note its **thumbprint**.
2. **Open ports** in the Azure **NSG** *and* **Windows Firewall**:
   - **TCP `8445`** — media (public).
   - **TCP `9442`** (or your chosen signaling port) — the HTTPS calling notification URL.
3. **Calling registration**: in the **Azure Bot** resource for app `3c8c192b…`, set the
   **calling webhook** to `https://missa-media-bot-aueast.australiaeast.cloudapp.azure.com:9442/api/calls`
   and enable calling. (`Calls.AccessMedia.All` is already consented.)
4. **.NET 8 SDK** on the VM (you already have `8.0.412`). Build/run as **win-x64**.

---

## 2. Environment variables (recorder + media bot)

```powershell
# --- recorder (unchanged) ---
$env:RECORDER_MEDIA_SOURCE='teams'          # receive frames; do NOT capture local audio
$env:RECORDER_CAPTURE_LOOPBACK='false'
$env:RECORDER_CAPTURE_MICROPHONE='false'
$env:RECORDER_SHARED_SECRET='45bf2b00...'   # must match Node
$env:BOT_ENDPOINT='https://<NODE_CURRENT_TUNNEL>'   # Node's live tunnel for the transcript callback
$env:AZURE_SPEECH_KEY='7mjnEpnp8...'
$env:AZURE_SPEECH_REGION='southcentralus'

# --- media bot (new) ---
$env:MEDIA_BOT_ENABLED='true'
$env:MICROSOFT_APP_ID='3c8c192b-06b6-4809-b8b9-ad4c36a2f9c7'
$env:MICROSOFT_APP_PASSWORD='<current bot client secret>'
$env:MICROSOFT_APP_TENANT_ID='588cadf4-9902-4465-86c0-8bcf04f4f102'
$env:MEDIA_BOT_SERVICE_FQDN='missa-media-bot-aueast.australiaeast.cloudapp.azure.com'
$env:MEDIA_BOT_CERT_THUMBPRINT='<thumbprint of the installed cert>'
$env:MEDIA_BOT_MEDIA_PORT='8445'
$env:MEDIA_BOT_NOTIFICATION_URL='https://missa-media-bot-aueast.australiaeast.cloudapp.azure.com:9442/api/calls'

# the app's web host (signaling + media-bot HTTP). Bind to the signaling port.
$env:ASPNETCORE_URLS='https://0.0.0.0:9442;http://127.0.0.1:5000'
```

Run:
```powershell
cd C:\Apps\missa-recorder-dotnet
dotnet run -r win-x64
```

---

## 3. Node side (flip back to the media-bot path)

In `.localConfigs` / `.env.local.user`:
- `MEDIA_BOT_BASE_URL=http://missa-media-bot-aueast.australiaeast.cloudapp.azure.com:9442`
  (re-activates `joinMediaBot()` → `POST /api/media-bot/join`).
- Ensure Node **does not also do its own Graph `create-call`** join (or you'll get two bot
  participants). The media bot owns the join now.
- `RECORDER_BASE_URL` is unused in this mode (the media bot feeds the recorder in-process).

Flow: `@Missa join` → Node resolves the meeting → `POST /api/media-bot/join` →
media bot joins with app-hosted media → per-participant audio → recorder → Node transcript,
attributed to each speaker, mute-aware (Teams sends no frames for muted participants).

---

## 4. Status / what to expect

| Piece | State |
|---|---|
| Project structure, endpoints, DI, in-process hand-off | ✅ written |
| HMAC join auth (matches Node + recorder) | ✅ |
| Recorder reused unchanged | ✅ |
| Media SDK calls (`// [SDK]`) | ⚠️ **verify on first build** — API shapes vary by SDK version |
| Inbound `/api/calls` token validation | ⚠️ minimal (TODO for production) |
| Cert / media-port reachability | ⚠️ must be set up + tested on the VM |

**First-build checklist:**
1. `dotnet restore -r win-x64` — confirm the media packages resolve.
2. `dotnet build` — fix any `// [SDK]` API mismatches (method/property names, enum values).
3. Run; `@Missa join`; watch for `Joining meeting for call …` then `CallHandler started`.
4. When someone speaks, the recorder should log `First media chunk received` with the
   speaker name, then transcripts flow to Node attributed per participant.

The signaling/identity logic is the easy part; **getting media to connect (cert + ports +
public IP) is where time goes.** Validate media connectivity with a Microsoft sample
(`PolicyRecordingBot`) first if it fights you.
