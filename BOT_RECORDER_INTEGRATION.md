# Missa Recorder (.NET) — Integration Guide for the Teams Bot

> **Audience:** the Teams bot agent (`Missa The Translator`).
> **Purpose:** describe how the .NET recorder service is structured and the exact
> contract the bot must follow to talk to it. Everything here is derived from the
> recorder source, not assumptions.

---

## 1. What the recorder is (and is not)

The recorder is a standalone **ASP.NET Core (.NET 10)** HTTP service. It:

- Accepts **signed** HTTP requests to enable a recording and to ingest audio.
- Runs audio (16 kHz / 16-bit / mono PCM WAV) through the **Azure Speech-to-Text REST API**.
- Posts recognized transcript text **back to the bot**.

It is **NOT** a Teams/Graph media producer. It never joins calls and never pulls
Teams audio itself. In production (`teams` mode) it is a *receiver* — the **bot**
captures application-hosted media frames and pushes them to the recorder.

```
                      (signed HTTP)
  Teams Bot  ──── POST /api/recordings/enable ──────▶  Recorder
  (you)      ──── POST /api/recordings/media-frame ─▶  (registers call + queues audio)
                                                          │
                                                          ▼
                                                 Azure Speech STT (REST)
                                                          │
                                                          ▼ recognized text
  Teams Bot  ◀── POST /api/botAudioTranscription ──── Recorder (callback)
```

---

## 2. Base URL & endpoints

Default base URL: **`http://127.0.0.1:5000`** (configurable via `ASPNETCORE_URLS`).
All recorder endpoints are under the route prefix **`/api/recordings`**.

| Method | Path | Signed? | Purpose |
|--------|------|---------|---------|
| `GET`  | `/api/recordings/media-readiness` | No | Diagnostics: config/hosting readiness |
| `POST` | `/api/recordings/enable` | **Yes** | Register a call & start the pipeline |
| `POST` | `/api/recordings/media-frame` | **Yes** | Push real Teams meeting audio frames |
| `POST` | `/api/recordings/audio-chunk` | **Yes** | Internal sink (recorder forwards frames here itself) |

> The bot normally calls **`/enable`** and **`/media-frame`** only.
> `/audio-chunk` is internal — the recorder re-signs and forwards frames to it.

---

## 3. Request signing (REQUIRED on all POST endpoints)

Every signed request must include **two headers**:

| Header | Value |
|--------|-------|
| `X-Missa-Timestamp` | Current Unix time in **seconds** (string) |
| `X-Missa-Signature` | `sha256=` + lowercase hex of the HMAC (see below) |

**Signature algorithm:**

```
message   = "<timestamp>.<rawBody>"           // literal: timestamp + "." + body
hmac      = HMAC_SHA256(key = RECORDER_SHARED_SECRET, message)
signature = "sha256=" + lowercase_hex(hmac)
```

**Rules the recorder enforces:**

- The timestamp must be within **±300 seconds** of the recorder's clock (replay protection).
- The HMAC is computed over the **exact raw request body bytes** you send.
  ⚠️ **Serialize the JSON once**, then sign *and* send that same string. Re-serializing
  (different whitespace/key order) will break the signature.
- Comparison is constant-time; the `sha256=` prefix is optional on the incoming header
  but recommended.
- The shared secret is read from `RECORDER_SHARED_SECRET` (or `SECRET_RECORDER_SHARED_SECRET`).
  It **must be identical** on the bot and the recorder.

### Node.js signing helper (drop-in for the bot)

```js
const crypto = require("crypto");

async function signedPost(url, bodyObj, sharedSecret) {
  const body = JSON.stringify(bodyObj);                 // serialize ONCE
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const signature = "sha256=" + crypto
    .createHmac("sha256", sharedSecret)
    .update(`${timestamp}.${body}`)
    .digest("hex");                                      // lowercase hex

  return fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Missa-Timestamp": timestamp,
      "X-Missa-Signature": signature,
    },
    body,                                                // send the SAME string
  });
}
```

---

## 4. Endpoint payloads

### 4.1 `POST /api/recordings/enable`

Call this **once per meeting**, as soon as the bot has resolved the meeting metadata.

```jsonc
{
  "callId": "string",            // REQUIRED — unique per call (use the Graph call id)
  "joinWebUrl": "string",        // REQUIRED — meeting join URL
  "onlineMeetingId": "string",   // REQUIRED — see gotcha #1 below
  "organizerId": "string",       // optional
  "meetingSubject": "string",    // optional
  "botId": "string",             // optional
  "botEndpoint": "https://..."   // optional — overrides recorder's BOT_ENDPOINT for callbacks
}
```

**Responses:**
- `200 OK` → `{ "status": "ok", "message": "Recording enabled" }`
- `400` → `{ "error": "missing_meeting_metadata" }` (one of the 3 required fields is empty)
- `403` → `{ "error": "invalid_signature" }`

On success the recorder logs `Recorder enabled for call <callId>` and registers the
call so media frames will be accepted.

### 4.2 `POST /api/recordings/media-frame`

Push captured meeting audio. The call **must be enabled first** (see gotcha #3).

```jsonc
{
  "callId": "string",            // REQUIRED — must match an enabled call
  "audioBase64": "string",       // REQUIRED — base64 of 16kHz/16-bit/mono PCM WAV
  "contentType": "audio/wav; codecs=audio/pcm; samplerate=16000",  // optional (this is the default)
  "language": "en-US",           // optional
  "speaker": "Meeting audio",    // optional — label shown on the transcript
  "timestamp": "2026-06-16T..Z"  // optional ISO-8601
}
```

**Responses:**
- `202 Accepted` → `{ "status": "queued" }`
- `404` → `{ "error": "unknown_call" }` (call was not enabled first)
- `429` → `{ "error": "media_queue_full" }` (back off; bounded queue is full)
- `400` / `403` → missing fields / bad signature

> **Audio format:** Azure Speech (and the recorder) expect **16 kHz, 16-bit, mono PCM**
> wrapped in WAV. Send frames in that format to avoid empty/garbled transcripts.

---

## 5. Transcript callback (recorder → bot)

When Azure Speech returns text, the recorder POSTs it to the bot.

- **URL:** `{botEndpoint || BOT_ENDPOINT}/api/botAudioTranscription`
  (`botEndpoint` from the `/enable` payload wins; otherwise the recorder's `BOT_ENDPOINT` env var)
- **Auth header:** `Authorization: Bearer <token>`
  where `token = lowercase_hex( HMAC_SHA256(key = RECORDER_SHARED_SECRET, message = "") )`
  — i.e. HMAC over an **empty** body, so it's a constant per shared secret.
- **Body** (camelCase JSON):

```jsonc
{
  "callId": "string",
  "text": "recognized speech text",
  "speaker": "Meeting audio",
  "language": "en-US",
  "timestamp": "2026-06-16T..Z",
  "isFinal": true
}
```

**The bot must implement `POST /api/botAudioTranscription`** to:
1. Validate the bearer token (recompute the empty-body HMAC and compare).
2. Parse the body above and route the `text` to its transcript handling.

### Node.js token validation (bot side)

```js
const expected = crypto
  .createHmac("sha256", process.env.RECORDER_SHARED_SECRET)
  .update("")            // empty message
  .digest("hex");
// compare against the Bearer token from the Authorization header (constant-time)
```

---

## 6. Recorder configuration (env vars)

These run on the **recorder** host. The bot only needs to match `RECORDER_SHARED_SECRET`
and know the recorder's base URL.

| Variable | Purpose | Notes |
|----------|---------|-------|
| `RECORDER_SHARED_SECRET` | HMAC key for signing/verification | **Must match the bot** |
| `BOT_ENDPOINT` | Default callback base URL | Must match the **live** bot tunnel |
| `AZURE_SPEECH_KEY` | Azure Speech key | also `COGNITIVE_SERVICES_KEY` |
| `AZURE_SPEECH_REGION` | Azure Speech region | e.g. `southcentralus`; or set an endpoint |
| `RECORDER_MEDIA_SOURCE` | `teams` / `local` / `hybrid` | unknown values fall back to `local` |
| `RECORDER_CAPTURE_MICROPHONE` | Local mic capture on/off | set `false` for the bot/app-hosted flow |
| `RECORDER_CAPTURE_LOOPBACK` | WASAPI loopback capture on/off | set `false` for the bot/app-hosted flow |
| `RECORDER_AUDIO_CHUNK_INTERVAL_MS` | Local-capture flush interval | clamped 3000–30000, default 10000 |
| `ASPNETCORE_URLS` | Bind address | default `http://127.0.0.1:5000` |

### Media-source modes

- **`teams`** — recorder ignores local audio devices and waits for the bot's
  `media-frame` posts. **Use this for the bot integration.**
- **`local`** — recorder captures the host machine's microphone/loopback directly
  (testing only; unrelated to Teams meeting audio).
- **`hybrid`** — both at once (usually not what you want; causes mixed audio).

**For the bot-driven flow, run the recorder with:**
```
RECORDER_MEDIA_SOURCE=teams
RECORDER_CAPTURE_MICROPHONE=false
RECORDER_CAPTURE_LOOPBACK=false
BOT_ENDPOINT=<the live bot tunnel, e.g. https://<id>.devtunnels.ms>
```

---

## 7. Internal flow (for context)

1. Bot → `POST /enable` → recorder registers the call (`AppHostedMediaBridgeService`).
2. Bot → `POST /media-frame` → frame is validated and pushed onto a **bounded channel**
   (capacity 1024, **drop-oldest** when full).
3. A background worker dequeues each frame, **re-signs** it, and POSTs it to the recorder's
   own `/audio-chunk` endpoint.
4. `/audio-chunk` → `AzureSpeechTranscriptionService` POSTs the audio bytes to the
   Azure Speech `conversation/cognitiveservices/v1` REST endpoint.
5. Recognized `DisplayText` → `POST {botEndpoint}/api/botAudioTranscription`.

---

## 8. Gotchas / checklist (these are the things that actually break)

1. **`onlineMeetingId` is REQUIRED.** `/enable` returns `400 missing_meeting_metadata`
   without `callId` **and** `joinWebUrl` **and** `onlineMeetingId`. The bot must resolve
   `onlineMeetingId` (e.g. via Graph `GET /onlineMeetings?$filter=JoinWebUrl eq '<url>'`)
   **before** calling `/enable`. If the bot logs `onlineMeetingIdPresent: false`, it is
   blocked here and the recorder will never be called.

2. **Tunnel / `BOT_ENDPOINT` must match the live bot.** If the recorder's `BOT_ENDPOINT`
   (or the `botEndpoint` in the enable payload) points at a stale dev tunnel, transcripts
   are POSTed into the void. Keep them in sync with the bot's current tunnel host.

3. **Enable before frames.** `/media-frame` returns `404 unknown_call` if the call was not
   enabled first. Always `/enable`, confirm `200`, then start posting frames.

4. **Sign the exact bytes you send.** Serialize JSON once; sign and transmit the same
   string. Mismatched serialization → `403 invalid_signature`.

5. **Shared secret must be identical** on both sides, for both the request signature
   (bot → recorder) and the callback bearer token (recorder → bot).

6. **Audio must be 16 kHz / 16-bit / mono PCM WAV**, base64-encoded, or Azure Speech
   returns empty text.

7. **Don't run the recorder in `local`/mic mode for the bot flow** — it injects the host's
   own (often silent) microphone audio alongside the bot's frames.

---

## 9. Quick verification

Confirm the recorder is reachable and configured:
```
GET http://127.0.0.1:5000/api/recordings/media-readiness
```
Then a signed `/enable` (with a dummy `callId`/`joinWebUrl`/`onlineMeetingId`) should log
`Recorder enabled for call <callId>` on the recorder. If that works, any remaining problem
is on the bot side (metadata resolution, tunnel, or frame format).
