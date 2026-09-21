<p align="center">
  <img src="docs/logo.png" alt="" width="104" height="104">
</p>

<h1 align="center">Shotwin</h1>

<p align="center">
  <strong>A screenshot tool for Windows, built for pixel work.</strong><br>
  Capture, mark up, read the text out of an image, record a region to video, and keep a
  shot on top while you use it.
</p>

<p align="center">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="30 languages" src="https://img.shields.io/badge/languages-30-success">
  <img alt="MIT" src="https://img.shields.io/badge/license-MIT-blue">
</p>

---

.NET 10 + WPF with a SkiaSharp canvas. No Electron, no browser engine, and no runtime to
install: one self-contained executable that updates itself.

## Status

In daily use. Capture, annotation, pinning, text recognition, colour picking, scrolling
capture, screen recording and video trimming all work end to end, in thirty languages,
with in-place updates. See [Roadmap](#roadmap) for what is not built yet.

## Install

Download `Shotwin.exe` from [Releases](../../releases) and run it. That is the whole
install: it is self-contained, so there is no .NET runtime to fetch, no installer and no
admin rights. It keeps itself up to date from there.

To build it yourself instead:

```powershell
.\install.ps1                      # or -StartWithWindows to run at login
```

That publishes the same single-file exe, installs it to
`%LOCALAPPDATA%\Programs\Shotwin` and adds a Start menu entry you can search for and
pin. Per-user, so still no admin rights and no UAC prompt. `.\install.ps1 -Uninstall`
removes all of it. Building needs the .NET 10 SDK
(`winget install Microsoft.DotNet.SDK.10`).

Opening Shotwin from the Start menu shows its main window, which has everything in one
place as pages in a left rail — **Capture**, **Recent**, **Shortcuts**, **Settings**,
**About**. Switching pages never opens another window; the only separate windows are
the annotation editor and pinned shots, which are per-shot by nature.

Closing it leaves the app running in the tray, still listening for its shortcuts —
launching it again brings the window back rather than doing nothing.

Left-click the tray icon to capture, right-click for the menu. If you cannot see the
icon it is in the hidden-icons overflow behind the `^` chevron; drag it onto the
taskbar to keep it visible.

`--background` starts straight into the tray with no window, which is what the
run-at-login entry uses.

To build and run without installing:

```powershell
dotnet build src\Shotwin\Shotwin.csproj -c Release
.\src\Shotwin\bin\Release\net10.0-windows10.0.19041.0\Shotwin.exe
```

## Updates

Shotwin looks once at the latest release of
[rui-branco/shotwin](https://github.com/rui-branco/shotwin) when it starts, in the
background. The check is silent: offline, rate-limited or nothing published yet all
read the same as "nothing newer", and nothing is ever popped up over what you are
doing. A newer build only changes what the app offers — the version at the foot of
the left rail turns into **Update to 1.1.0**, and the **About** page says the same in
a line you can re-check on demand.

Clicking it downloads the published `Shotwin.exe` beside the installed one, checks it
is really a program, then replaces the running exe with it and restarts. The old build
is renamed out of the way — Windows will not let a running exe be overwritten, but it
will let it be renamed — and deleted on the next start. No installer, no admin rights,
and the Start menu shortcut and run-at-login entry keep pointing at the same path.

## Using it

| Shortcut | Action |
| --- | --- |
| `Ctrl+Shift+2` | Capture an area |
| `Ctrl+Shift+3` | Capture the screen the cursor is on |
| `Ctrl+Shift+4` | Grab text from an area |
| `Ctrl+Shift+5` | Pick a colour |
| `Ctrl+Shift+6` | Scroll a region and stitch it |
| `Ctrl+Shift+7` | Record a region, a window or a screen to a video |

Rebind them on the **Shortcuts** page: click a shortcut, press the combination you
want. The **Settings** page covers the save folder and file-name template, what
happens after a capture, the cursor readout and window snapping, and run-at-login.
Settings are stored in `%APPDATA%\Shotwin\settings.json` if you prefer editing by hand.

The **Recent** page is a gallery of everything already saved — select a shot to edit,
copy, pin, reveal or delete it.

### Text recognition

`Ctrl+Shift+4` (or **Grab text from an area** on the Capture page, `Shotwin.exe --text`,
or **Grab text** in the tray menu) drags out a region, reads the text in it and copies
that to the clipboard — no file saved, no editor. In the editor, **Copy text** /
`Ctrl+Shift+T` does the same for the shot you are annotating.

It uses `Windows.Media.Ocr`, which ships with Windows: offline, no model to download,
and it covers whichever language packs are installed (Chinese included). The Capture
page lists the languages it found, and says so up front if Windows has none.

Images under about two megapixels are doubled before recognition. Upscaling adds no
detail, but small UI text at native size makes the engine read "rn" as "m".

### Scrolling capture

`Ctrl+Shift+6` (or **Scrolling capture** on the Capture page, `Shotwin.exe --scrolling`,
or the tray menu) drags out a region exactly the way an area capture does, then takes it
over: the pointer parks in the middle of it, a wheel notch goes out, the region is
grabbed again, and every frame is joined onto the bottom of the last. A small window in
the corner counts the pixels as they pile up. It ends when the page stops moving, and
**Esc** or **Stop** ends it early and keeps what was captured. The result goes through
the same path as any other shot: clipboard, save, preview.

Windows has no API for this, so the scroll is synthesized. A wheel notch goes to the
window under the pointer rather than the focused one, which is what lets the capture
drive an app without ever taking the foreground off it. Frames are aligned on row
signatures — one luminance sum per row — and the winning offset is checked against real
pixels before anything is joined, so a page of flat background does not splice itself
together in the wrong place.

### Screen recording

`Ctrl+Shift+7` (or **Start a recording** on the Capture page, `Shotwin.exe --record`, or
the tray menu) opens the same overlay an area capture does: drag a region, or press
`Ctrl+A` for the whole monitor the pointer is on. `Esc` cancels without recording
anything.

A count-in runs first, so the shortcut that asked for the recording is not the first thing
in it. Then a red ring is drawn around what is in shot and a small bar appears in the
bottom-right with a pulsing dot, the elapsed time, and **Stop** / **Cancel** with their
keys written on them. Stop keeps the file, Cancel throws it away, and `Esc` and
`Shift+Esc` do the same from wherever you are — neither the bar nor the ring ever takes
the focus off what is being recorded.

Both are hidden from screen capture rather than kept out of its way, so neither appears in
the recording however the region is placed, and the ring passes clicks through to whatever
is underneath. The bar can be dragged anywhere, including over the region.

Encoding is H.264 through the encoder built into Windows: no FFmpeg to bundle, nothing to
download, and hardware acceleration where the machine has it. Video only, no audio track.
Thirty frames a second by default; **Settings** offers fifteen for something mostly still,
which roughly halves the file, and sixty or a hundred and twenty for something fast.

The result lands in the same save folder as the shots, named by the same template with an
`.mp4` on the end. A card appears when it finishes, the way the thumbnail does after a
screenshot: a frame from the video, how long it ran, and a play button that hands the file
to whatever Windows opens videos with. Recordings appear in **Recent** alongside the
shots.

One recording at a time. The shortcut, the tray item and the Capture button all say so
rather than starting a second one over the first.

### Trimming a recording

Opening a recording from **Recent** opens the video editor. It plays through Windows' own
playback pipeline — full resolution, hardware decoded — rather than by pulling frames out
of the file one at a time, which is what it did before and which capped the picture at a
quarter size to hold thirty frames a second.

The timeline shows the clip as blue pieces. Drag across empty track to keep another piece,
drag a piece's ends to shorten it or its middle to slide it, click inside one to put the
playhead there, and drop one with its cross or the `Delete` key. Playback jumps the gaps,
so what you watch is what you will get, and the length in the corner counts the pieces
rather than the file.

**Save** writes the pieces to a new file, joined end to end — never over the recording, so
experimenting with the handles costs nothing — and opens the folder with it selected,
unless Explorer is already showing that folder.

### In the overlay

Drag to select. Click without dragging to grab the window under the cursor. Hold
`Shift` to constrain to a square, hold `Space` mid-drag to move the whole selection,
`Ctrl+A` for the current monitor, `Esc` or right-click to cancel.

The overlay is deliberately quiet: a small crosshair and one chip beside it showing
the cursor position, or the selection size while dragging. No full-screen guide lines
and no zoom loupe — on a wide monitor the lines quarter the screen and the loupe
covers the thing you are trying to frame.

### After a capture

By default the shot is saved, copied to the clipboard, and a **preview thumbnail**
appears **where the drag finished** — under the hand that just made it, rather than in
a corner the eye has to go looking for. It is a staging area, not a notification:

- **Actions along the bottom** — edit, copy, save, pin, show in folder, dismiss
- **Click the thumbnail** to open the editor
- **Drag it** into any app or folder to drop the PNG

It opens **above** the cursor, since that is where the selection you just drew is, and
drops below only when there is no room up there. There is no countdown: it waits until
you go back to another window or the clipboard moves on, and the shot stays on the
clipboard ready to paste either way.

**Saving** is a separate question from the preview. *Save every capture to the folder
automatically* is on by default. Turn it off and a capture lives only on the clipboard
and in the preview — nothing is written to disk unless you press **Save** there, which
suits paste-once-and-forget shots.

The **Settings** page controls the rest: whether the preview appears and where it lands
(at the cursor, or pinned to any corner). Turn **Open the editor** on instead if you
would rather go straight there and skip the preview.

### Picking a colour

`Ctrl+Shift+5` (or **Pick a colour** on the Capture page, `Shotwin.exe --colour`, or the
tray menu) freezes the screen and follows the cursor with a 10x loupe: a pixel grid, a
ring around the exact pixel being sampled, and a swatch with the value. Click and it
goes to the clipboard; `Esc` cancels.

This is the one place a loupe earns its keep. Reading a single pixel *is* the task, so
magnifying it is the feature rather than something covering the shot — which is why the
capture overlay has no loupe at all.

Settings chooses the notation: `#1B1B1F`, `rgb(27, 27, 31)` or `hsl(240, 7%, 11%)`.

### In the editor

| Key | Tool |
| --- | --- |
| `V` | Select / move |
| `A` | Arrow |
| `R` `O` `L` | Rectangle, ellipse, line |
| `P` | Pen |
| `H` | Highlighter |
| `T` | Text |
| `S` | Step counter |
| `B` / `Shift+B` | Pixelate / blur |
| `C` | Crop |
| `K` | Cut a band out |

`Ctrl+C` copy, `Ctrl+S` save, `Ctrl+Shift+S` save as, `Ctrl+P` pin, `Ctrl+Z`/`Ctrl+Y`
undo/redo, `Ctrl+wheel` zoom, `Ctrl+0` fit, `Delete` remove selection, `Esc` close.

Hold `Shift` while drawing to snap arrows and lines to 45 degrees and boxes to square.

### Changing the image itself

Three tools edit the pixels rather than drawing on top of them, and all three are
undoable:

- **Crop** (`C`) — drag the region to keep. Annotations move with the pixels, so an
  arrow drawn before the crop still points at the same thing.
- **Cut** (`K`) — drag a band and it is removed, with the two halves closed up against
  each other. The axis follows the drag: a wide flat drag takes out a horizontal strip,
  a tall narrow one takes out a vertical strip. This is how you delete the dead space in
  the middle of a long screenshot without losing either end.
- **Add image** (`Ctrl+Shift+A`, or `Ctrl+V` to take one from the clipboard) — grows the
  canvas and places another image below, or to the right if you hold `Shift`. A small
  seam is left between them so the join reads as deliberate.

### Pinned shots

`Ctrl+P` floats the shot on top of everything. Drag to move, wheel to scale, `Ctrl+0`
for actual size, right-click for copy / save / fade / click-through, `Esc` to dismiss.

### Scripting

```powershell
Shotwin.exe --area     # or --screen, --scrolling, --record,
                       # --settings, --background
```

A second launch hands the request to the running instance and exits, so this works
from a Stream Deck, AutoHotkey or a shell without fighting the global hotkeys.

## Controlled Folder Access

Windows protects `Pictures`, `Documents`, `Desktop` and `Videos` by default on many
machines, and fails writes there with a misleading `FileNotFoundException`.

**On the first capture Shotwin asks where to keep shots**, and write-probes the answer
before accepting it. A folder you picked is a folder you can write to, which is what
keeps this from ever becoming your problem. The prompt appears once, after the shot is
already in hand, and you can change the folder later under Settings.

If a folder stops working later — the setting was edited by hand, a drive went away —
the app moves to `%LOCALAPPDATA%\Shotwin\Shots` at startup and carries on.

The Settings page **probes the save folder by writing a file**, rather than guessing
from the path — someone who has already allowed Shotwin never sees a warning about it.
When a write genuinely fails it offers the two ways out:

- **Allow Shotwin in Windows Security** — adds this exe to the Controlled Folder Access
  allow-list. Machine-wide Defender change, so Windows shows a UAC prompt; declining it
  changes nothing.
- **Use an unprotected folder** — switches the save folder to
  `%LOCALAPPDATA%\Shotwin\Shots`.

Saves fall back to that folder regardless, so a shot is never lost while this is
unresolved, and the editor says where it actually landed.

## When something goes wrong

UI-thread exceptions are logged to `%LOCALAPPDATA%\Shotwin\errors.log` and swallowed
rather than killed: a tray app that dies on one bad click also takes the global
shortcuts with it, and nothing on screen says why. A tray notification names the error
and points at the log.

Two settings that look harmless and are not, both learned the hard way:

- **`InvariantGlobalization` must stay off.** WPF converts values back through
  `XmlLanguage.GetSpecificCulture()`, which throws
  `Cannot find non-neutral culture related to 'en-us'` in invariant mode. It surfaces
  as a hard crash the first time a user touches a two-way binding, and nowhere else.
- **`FontFamily` takes one family name, not a fallback list.** `"Segoe Fluent Icons,
  Segoe MDL2 Assets"` is parsed as a single family name, matches nothing, and every
  glyph renders as an empty box even though both fonts are installed. Name one family.
- **`SWP_NOACTIVATE` must not be used on the overlay.** Shotwin normally has no
  foreground window, so Windows treats it as a background app and refuses focus
  changes. Both the overlay and the main window call `SetForegroundWindow` explicitly;
  without it the overlay paints on top but never gets the keyboard, so `Esc` goes to
  whatever app was focused before.

## Architecture

```
src/Shotwin/
  Interop/       Win32 P/Invoke. Everything here is in PHYSICAL pixels, never DIPs.
  Capture/       Virtual-desktop BitBlt, monitor and DPI enumeration, window z-order,
                 the scrolling capture loop and its stitcher, the MP4 recorder.
  Overlay/       The frozen-desktop selection UI (dimming, window snap, crosshair).
  Editor/        Annotation model, undo stack, Skia rendering, the editor window.
  Pin/           Always-on-top floating shot.
  Shell/         The main window and its pages.
  Services/      Settings, clipboard and disk IO, hotkeys, the CLI command channel.
```

Two rules keep the coordinate maths honest:

1. **Skia canvases are in physical pixels.** WPF reports mouse positions in DIPs, so
   every input point is multiplied by the window DPI scale on the way in. The overlay
   is placed with `SetWindowPos` in raw pixels because a mixed-DPI virtual desktop
   cannot be expressed exactly in DIPs.
2. **Annotations are in image pixels.** They survive zoom, window resizes and being
   dragged between monitors without any conversion.

Capture goes through `IScreenCapture`. The current implementation is a single BitBlt
of the whole virtual desktop, which is fast and dependency-free but cannot see
hardware-overlay or DRM-protected surfaces. A DXGI Desktop Duplication implementation
can replace it without touching callers.

## Build

```powershell
.\install.ps1                      # builds and installs in one step
dotnet build src/Shotwin            # or just build it
```

Needs the .NET 10 SDK; everything else comes from NuGet on first build. `install.ps1`
publishes self-contained and single-file, which is why the result is around 75&nbsp;MB and
why nothing has to be installed to run it.

| Script | Purpose |
| --- | --- |
| `install.ps1` | Build, publish and install to `%LOCALAPPDATA%\Programs\Shotwin` |
| `tools/make-logo.ps1` | Render `docs/logo.png` from the app's own icon |

## Roadmap

Not yet built, roughly in the order they are worth doing:

- **QR reading** via ZXing.Net.
- **Colour picking beyond hex.** OKLCH, average colour, APCA/WCAG contrast.
- **Backdrop.** Gradients, shadows, rounded corners behind the shot.
- **Ruler / measure mode**, crop, before-after GIF, canvas combining.
- **S3 upload.**
- **DXGI capture** for protected surfaces and lower latency.

## License

MIT
