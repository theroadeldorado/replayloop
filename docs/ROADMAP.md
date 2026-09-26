# Roadmap

## Phase 1: shared core + Windows MVP (this commit)

- [x] Swing detector: pose + impact sound + motion; waggles and half swings ignored, practice swings (no impact) not saved
- [x] Audio impact detector, motion energy
- [x] Session folders: atomic `session.json`, per-shot folders, one clip per camera
- [x] Vector annotations: line, arrow, circle, box, angle (live degrees), freehand; colors, widths, dashes; per-shot or on every shot; editable; undo
- [x] Layout math: best-fit multi-camera grid, uniform crop, rotation auto-crop, digital zoom window
- [x] Core tests (20 suites) under ASan/UBSan
- [x] Windows: multiple local cameras, highest-fps format, ring buffer, hardware H.264 clips, thumbnails
- [x] Windows: MoveNet pose (ONNX + DirectML), rotation-aware
- [x] Windows: auto-replay loop until the next swing, live inset, ½ ¼ ⅛ speed, frame step, jump to impact, scrubber with an impact marker
- [x] Windows: compare two shots side by side or as a ghost overlay, aligned at impact
- [x] Windows: star, comment, club per shot; thumbnail strip; browse past sessions
- [x] Windows: PiP window (always on top, resizable, aspect lock, replay/live/both)
- [x] Windows: cast replay (Miracast/DLNA), mirror everything (Win+K)
- [x] Windows: share/save video or frame, with or without drawings
- [x] Windows: optical zoom where the camera supports it, digital otherwise; mounting rotation + fine leveling; mirror
- [x] Builds in CI (MSVC core + tests, WinUI app)
- [ ] **Verify on hardware**: run with real webcams (see README, "Status")

## Phase 2: phones as cameras

- [ ] Protocol implementation on the host (mDNS, pairing, clock sync, RTP preview → `ICameraSource`, clip download)
- [ ] iOS camera mode (AVFoundation 120/240 fps, encoded ring buffer, VideoToolbox)
- [ ] Android camera mode (CameraX/Camera2 high speed, MediaCodec)
- [ ] Audio cross-correlation fallback alignment

## Phase 3: full phone apps

- [ ] iOS/iPadOS app: same features as Windows, plus AirPlay and system PiP
- [ ] Android app: same features, plus Google Cast and system PiP
- [ ] swingcore XCFramework + Android NDK/JNI packaging
- [ ] Auto-rotation in portrait and landscape

## Ideas that would make it great

- **Voice control:** "replay", "slower", "star that", "7 iron". Hands are busy holding a club.
- **Swing positions:** auto markers for P1–P10 (address, top, impact…) from the pose timeline; tap to jump.
- **Skeleton overlay** toggle from pose data (already computed) to show spine angle, head movement and hip sway.
- **Auto swing plane** line drawn from the address shaft angle.
- **Tempo trend:** backswing:downswing ratio for each shot in the session, with a chart.
- **Launch monitor integration:** attach ball speed, carry and spin to each shot. Use the GSPro / OpenConnect APIs, or read simulator logs, so the replay shows what the ball did.
- **Smart trim:** loop only takeaway → finish, skipping the pre-shot routine.
- **Slow-motion export:** retime exported clips so a shared video plays at ½ or ¼ speed anywhere.
- **Pro reference library:** ghost a tour swing (same camera angle) over yours, aligned at impact.
- **Coach links:** share a session with a coach who draws on it from their own device (shapes are already vectors).
- **Cloud backup** of starred shots.
- **Kiosk / range mode:** full screen, big fonts, auto-end the session after idle.
