# SwingLoop

Record your golf swing, and watch it replay on a loop until your next one.

* **Real swings only.** Waggles, half swings and practice swings (no ball
  struck) are ignored. SwingLoop watches your body and listens for the strike.
* **Instant replay** with slow motion (½ ¼ ⅛), frame stepping and
  jump-to-impact. It keeps looping until you hit again.
* **Multiple angles**, synced at impact. Any mix of webcams now; phones as
  cameras next.
* **Drawing:** lines, arrows, circles, boxes, angles and freehand, in any
  color. Drawings stay editable, and can stay on screen for every shot (swing
  plane, head position).
* **Compare** two shots side by side or as a ghost overlay.
* **Star, comment and tag** each shot with a club. Browse every session in
  its own folder.
* **Picture-in-picture**: float a resizable replay window over your golf
  simulator.
* **Cast** the replay to a TV, or mirror the whole screen.
* **Share or save** video or stills, with or without drawings.

Native on Windows (WinUI 3). iPhone/iPad and Android apps share the same
C++ core (see the [roadmap](docs/ROADMAP.md)).

## Status

| Part | State |
|---|---|
| `core/`: swing detection, sessions, drawings, layout | Built and tested (20 test suites, clean under ASan/UBSan) |
| `windows/`: WinUI 3 app | MVP builds in CI (download `SwingLoop-win-x64` from the Actions run). **Not yet run on hardware**: needs testing with real cameras. |
| Phone camera mode | Protocol designed ([CAMERA_PROTOCOL.md](docs/CAMERA_PROTOCOL.md)) |
| iOS / Android apps | Planned ([ARCHITECTURE.md](docs/ARCHITECTURE.md)) |

## Build (Windows)

Requirements:

* Windows 10 2004+ or Windows 11
* Visual Studio 2022 with *Desktop development with C++* and *.NET desktop
  development*, or just the Build Tools
* CMake 3.20+
* .NET 8 SDK

```powershell
./build.ps1 -Run
```

That builds `swingcore.dll`, runs the core tests, builds the app and launches it.

**Pose detection (recommended).** Export the MoveNet model once:

```powershell
pip install tensorflow tf2onnx
python tools/export_movenet.py
```

Without it, SwingLoop still captures swings from the impact sound plus motion.

## Build the core anywhere

```sh
cd core && make test
```

## Using it

1. Open SwingLoop. A session starts automatically.
2. Pick your cameras in **Settings**:
   * Choose which one watches for swings.
   * Set its position, rotation and zoom.
3. Hit balls. Each real swing is saved and loops in **Replay**. The live
   view stays in the corner.
4. Click thumbnails to review shots. Ctrl+click a second shot to **Compare**.
5. **End session** (or close the app) to finish. Sessions are saved in
   `Videos\SwingLoop`.

| Key | Action |
|---|---|
| Space | Play / pause |
| ← → | Frame step (Shift: 5 frames) |
| ↑ ↓ or 1–4 | Speed |
| I | Jump to impact |
| M | Save the last swing manually |
| S | Star |
| P | Picture-in-picture |
| V L A C B G F | Select, line, arrow, circle, box, angle, freehand |
| K | Toggle "keep drawings on every shot" |
| Del / Ctrl+Z / Esc | Delete shape / undo / stop drawing |
| F11 | Full screen |

## Layout

```
core/       swingcore: C++17, C ABI, tests
windows/    SwingLoop for Windows (WinUI 3, .NET 8)
docs/       architecture, phone camera protocol, roadmap
tools/      pose model export
```
