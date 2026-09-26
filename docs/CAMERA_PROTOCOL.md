# Phone camera protocol (v1 draft)

A phone in **Camera mode** becomes an extra angle for a host: the Windows
app, or another phone or tablet running the full app. Design goals:

* **Full-quality clips.** The phone records locally at full resolution and
  frame rate. It sends only a light preview live, and uploads the real clip
  after the shot.
* **Frame-accurate alignment.** Every clip is cut on the host's clock.
* **One brain.** Only the host detects swings, so there is one decision per
  shot, never one per camera.
* **Zero config on a home network.** Discovery works without typing IP
  addresses.

## 1. Discovery and pairing

* The host advertises `_swingloop._tcp` over mDNS/Bonjour. TXT record:
  `v=1`, `name=<host name>`, `id=<host uuid>`.
* The phone lists the hosts it finds. The user taps one, and the host shows a
  6-digit code that the user confirms on the phone.
* After the first pairing, both sides store a shared key, so later sessions
  reconnect automatically.
* The control channel is a WebSocket (`wss://`, self-signed certificate
  pinned at pairing) to the port in the SRV record.

## 2. Clock sync

Clips are aligned on the host clock, so the phone must know
`offset = hostClock − phoneClock`.

* It uses NTP-style exchanges over the control channel: phone t0 → host
  t1/t2 → phone t3. The offset is `((t1 − t0) + (t2 − t3)) / 2`, taken from
  the sample with the **lowest round-trip time** out of the last 16.
* Sync runs 8 times at connect, then every 2 s. Wi-Fi jitter is usually
  1–3 ms when filtered this way, well under one frame at 240 fps.
* Frame timestamps come from the capture hardware clock on the phone
  (`CMSampleBuffer` presentation time, or `SENSOR_TIMESTAMP`), not from
  arrival time.

## 3. Messages (JSON over the WebSocket)

| Direction | Type | Body |
|---|---|---|
| phone → host | `hello` | device model, OS, camera capabilities (formats, max fps, zoom range, optical lenses) |
| host → phone | `configure` | `{width, height, fps, previewBitrate, position}` |
| phone → host | `status` | battery, thermal state, orientation, current zoom, recording fps |
| host → phone | `zoom` | `{factor}`; the phone uses the optical lens when it can, otherwise digital |
| host → phone | `clipRequest` | `{shotId, startHostUs, endHostUs, impactHostUs}` |
| phone → host | `clipReady` | `{shotId, url, width, height, fps, durationUs, impactOffsetUs, rotationDeg}` |
| both | `ping` / `pong` | clock sync samples |
| host → phone | `bye` | session ended |

## 4. Live preview

* **Stream:** H.264 baseline at about 540p and 30 fps, roughly 2 Mbit/s,
  sent as RTP over UDP on a port given in `configure`. Every frame carries
  its phone capture timestamp.
* **Host side:** the host wraps it as an `ICameraSource`
  (`windows/.../Capture/CameraSources.cs`), so the live grid, PiP and layout
  treat phones exactly like USB cameras.
* **Pose:** the host runs pose on a phone preview only if that phone is the
  selected detector camera. Normally the host's own camera detects, and the
  phones just record.

## 5. Full-quality clips

* The phone keeps a **rolling encoded buffer**: H.264/HEVC at the full
  format (for example 1080p240), 1-second GOPs, and the last ~8 s.
* On `clipRequest` it converts the host window to its own clock and cuts at
  GOP boundaries around it. It then trims losslessly to the exact frames
  (edit list), writes an MP4, and serves it over HTTPS on the paired channel.
* The host downloads the file into `shots/NNNN/camK-<position>.mp4`.
* Until the upload lands, the host shows the preview frames it already has
  for that window, so replay starts instantly and upgrades in place.

## 6. Failure handling

* **Phone drops mid-shot.** The shot is saved with the angles that arrived.
  Missing ones are retried when the phone reconnects, as long as the phone
  still has them in its buffer.
* **Thermal throttling.** The phone reports it in `status`. The host
  suggests a lower fps and shows a badge.
* **Clock sync lost** (no pong for 10 s). The camera is marked "unsynced"
  and its clips are aligned by audio cross-correlation of the impact instead.
  Every phone records audio for exactly this fallback.
