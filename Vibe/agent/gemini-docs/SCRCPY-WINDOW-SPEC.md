# Custom scrcpy Window — Implementation Spec (iPhone-Mirroring parity)

> Owner-authored spec for the milestone **after M3** (see `ROADMAP.md`; decision **D-049**). This
> is the *from-source* scrcpy window redesign. It **supersedes** M3's interim flag-based
> `--window-borderless` look. It requires the native C/SDL build of scrcpy on Windows from the
> `SCRCPY/Default` submodule — do **not** attempt it until the from-source build environment is
> proven (that is step 1 of the milestone).

## Goal

Make the mirror window behave like Apple's **iPhone Mirroring** on macOS: bare phone video at rest,
a thin hover-reveal drag bar with window controls, rounded corners, and a drop shadow.

## Target behavior (confirmed against iPhone Mirroring)

- **At rest:** no title bar, no chrome, no icon, no title text — just the phone video, edge to edge.
- **On hover near the top of the window:** a thin bar fades in showing **close**, **minimize** and
  **maximize/restore** buttons (no icon, no title text). *(Maximize added per D-051 — owner wants to
  expand the window; the original spec said close/minimize only.)*
- **That hover bar is the drag zone:** click-drag anywhere on it moves the window, **except**
  directly on the buttons.
- **Mouse leaves the top region:** the bar fades back out — back to bare video.
- **Corners:** slightly rounded — subtle, small radius, not a pronounced rounded-rectangle look.

## Mechanism

- **`WM_NCCALCSIZE`** → remove the OS-drawn title bar; the client area becomes the whole window.
- **`WM_NCHITTEST`** → return `HTCAPTION` (draggable) for the top strip, normal client hit
  (`HTCLIENT`) for the button area, so dragging works everywhere on the strip except the buttons.
- **Mouse enter/leave tracking** on that top region (`WM_MOUSEMOVE` + `TrackMouseEvent` for
  `WM_MOUSELEAVE`) → drives the fade in/out of the bar and buttons.
- **Buttons drawn in scrcpy's own SDL render loop**, composited on top of the video frame.
- **`DwmExtendFrameIntoClientArea`** → keeps the window drop shadow on the borderless window.
- **`DWMWA_WINDOW_CORNER_PREFERENCE`** → rounded corners (small radius).

## Scope boundary (hold this line)

Touches **only** scrcpy's window-creation / event-handling code (wherever the SDL window and its
`WndProc` live in this version's source). **No** changes to capture, encode, input routing, or the
Linc wire protocol.

## Staging (the milestone runs in this order — do not skip step 1)

1. **Prove the from-source Windows build FIRST.** Build **unmodified** scrcpy from the
   `SCRCPY/Default` submodule (its documented Windows/MSYS2 or cross-build path), and confirm the
   produced `scrcpy.exe` runs and mirrors. If the build environment can't be stood up cleanly,
   **STOP and report** — do not start the window edits.
2. **Apply the window changes** in the isolated `SCRCPY/Custom` copy (keep `SCRCPY/Default`
   pristine), as a small diff confined to the window/WndProc/render files.
3. **Bundle the custom build** in place of M3's prebuilt binaries; keep `SCRCPY/NOTICE.md` accurate
   (now: modified window presentation, engine untouched).

## Implementation notes & caveats (for the agent, when this milestone runs)

- **SDL owns the HWND and its own `WndProc`.** Get the native handle via `SDL_GetWindowWMInfo`, and
  either subclass the window procedure (`SetWindowLongPtr(GWLP_WNDPROC)`) or use SDL's syswm event
  hook (`SDL_SetWindowsMessageHook` / `SDL_SYSWMEVENT`). Injecting `WM_NCCALCSIZE`/`WM_NCHITTEST`
  handling must cooperate with SDL's own borderless/resizable handling — verify SDL doesn't
  re-assert decorations.
- **`DWMWA_WINDOW_CORNER_PREFERENCE` is Windows 11+ only** (`DWMWCP_ROUND` / `DWMWCP_ROUNDSMALL`);
  on Windows 10 corners stay square — acceptable, just don't assume rounding is present.
- **The drop shadow** on a borderless window comes from extending the DWM frame
  (`DwmExtendFrameIntoClientArea` with a small margin) after removing the non-client area — the two
  work together.
- **Button hit-testing:** the button rectangles must be excluded from the `HTCAPTION` region in
  `WM_NCHITTEST` (return `HTCLIENT` there), or the buttons won't be clickable — they'd be part of
  the drag zone.
- **Fade** is a timed alpha on the SDL-drawn overlay; keep it cheap (it runs every frame).
- Risk: this is the hardest single task in the project (native build + Win32 non-client handling
  inside SDL). Expect to spend real time on step 1. It is a strong candidate for the most-trusted
  agent and small, checkpointed steps.

## PROVEN from-source build recipe (M3.5a, 2026-07-23 — the gate PASSED)

The native Windows client build is reproducible on this machine. Recipe that worked:

- **Toolchain:** MSYS2 (already installed at `C:\msys64`), **MINGW64** shell. Invoke non-interactively
  via `C:\msys64\usr\bin\bash.exe -lc "export MSYSTEM=MINGW64; source /etc/profile; <cmd>"`.
- **Packages (pacman, MINGW64):** `mingw-w64-x86_64-{SDL2, ffmpeg, libusb, make, gcc, meson, ninja}`.
  **Do NOT** explicitly install `mingw-w64-x86_64-pkg-config` — it conflicts with `pkgconf` (which
  meson pulls in and which already provides `pkg-config`). Versions seen: SDL2 2.32.10, ffmpeg 8.1.2,
  libusb 1.0.30, gcc 16.1.0, meson 1.11.2, ninja 1.13.2.
- **Client-only build (no Java/Android SDK) using our bundled prebuilt server.** From inside the
  `SCRCPY/Default` source dir:
  ```
  meson setup x --buildtype=release --strip -Db_lto=true -Dprebuilt_server=../Custom/bin/scrcpy-server
  ninja -Cx
  ```
  **Gotcha:** `-Dprebuilt_server` must be a **relative** path, not a Windows absolute path.
  `server/meson.build` does `if not prebuilt_server.startswith('/')` → prepends `../`, so a `D:/…`
  absolute path becomes `../D:/…` and fails. `../Custom/bin/scrcpy-server` resolves correctly.
- **Output:** `SCRCPY/Default/x/app/scrcpy.exe`. Runs via `./run x --version` (MSYS2 sets the DLL
  path). Reported `scrcpy 3.3.4`, SDL 2.32.10, libavcodec 62.28.102, libusb 1.0.30.

**Bundling nuance for M3.5b (critical):** the from-source exe links against MSYS2's **current**
mingw64 DLLs (SDL2 2.32.x, **avcodec-62 / avformat-62 / avutil-60**, swresample, libusb) — NOT the
**avcodec-61** release DLLs bundled in M3. To ship our own build standalone we must bundle **its**
mingw64 dependency DLLs from `C:\msys64\mingw64\bin`. Determine the exact set with `ntldd -R
x/app/scrcpy.exe` (or `ldd`) in the MINGW64 shell and copy exactly those (plus transitive mingw
runtime DLLs like `libwinpthread-1.dll`, `zlib1.dll`). `./run x` works inside MSYS2 only because
`/mingw64/bin` is on its PATH.

## M3.5b-1 SHIPPED — standalone from-source bundle (2026-07-23, D-050)

Linc now ships its own from-source `scrcpy.exe`. Durable facts for future rebuilds:

- **BUILD PORTABLE.** Add **`-Dportable=true`** to `meson setup`. Without it scrcpy resolves
  `scrcpy-server` at the MSYS2 prefix (`C:/msys64/mingw64/share/scrcpy/scrcpy-server`) and dies on any
  clean machine (*"does not exist … Server connection failed"*). Portable = server/adb/icon load next
  to the exe, which is how we bundle. Full working config:
  ```
  cd SCRCPY/Custom/src && rm -rf x && \
  meson setup x --buildtype=release --strip -Db_lto=true -Dportable=true \
    -Dprebuilt_server=../bin/scrcpy-server && ninja -Cx
  ```
- **Editable source lives at `SCRCPY/Custom/src`** (copy of `SCRCPY/Default`, unmodified until b-2).
  `.gitignore` ignores only `SCRCPY/Custom/src/x/` (the meson build dir); the source itself is tracked.
- **Bundle = `SCRCPY/Custom/bin/`**: our `scrcpy.exe` + its **98 mingw64 DLLs** (from `ldd`, copied
  out of `C:\msys64\mingw64\bin`) + `scrcpy-server`, `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`,
  `linc.ico`. csproj Content glob is `../../SCRCPY/Custom/bin/**/*` (bin ONLY — never the whole
  `Custom` tree, or the source gets copied into build output). No `.bat`/`.vbs`/`icon.png`/`.bak` clutter.
- **Verify a rebuild non-intrusively:** `…\Custom\bin\scrcpy.exe --list-encoders` with a device
  attached — starts the server, lists encoders, no mirror window. Clean list = portable path good.

## M3.5b-2 (first cut) SHIPPED — frameless + rounded + drag (2026-07-23, Antigravity)

New Windows-only module **`SCRCPY/Custom/src/app/src/sys/win/window.{c,h}`**, called from `screen.c`
right after `SDL_CreateWindow` (guarded `#ifdef _WIN32`). `meson.build` adds `window.c` to the app
sources and links `dwmapi`. Constants in `window.h`: `SC_WIN_CORNER_RADIUS`, `SC_WIN_CAPTION_H`.
Mechanism landed: subclass SDL's `WndProc` via `SetWindowLongPtr(GWLP_WNDPROC)` (old proc saved in a
static, chained with `CallWindowProc`); `WM_NCCALCSIZE`→0 (client = whole window); `WM_NCHITTEST`
chains then overrides the top `SC_WIN_CAPTION_H` px to `HTCAPTION`; `SetWindowRgn` rounded region
(re-applied on `WM_SIZE`); DWM shadow via `DwmExtendFrameIntoClientArea` 1px bottom margin. Built
MSBuild x64, `--list-encoders` clean. **Owner test:** corners + drag confirmed; asked for a slightly
larger radius.

### KNOWN REGRESSION → fix in b-2b: the window can't be resized or maximized.
`WM_NCCALCSIZE`→0 on a window **without `WS_THICKFRAME`** leaves no resize borders and no
maximize/snap. The custom-chrome recipe is: **keep** `WS_THICKFRAME | WS_MAXIMIZEBOX` on the style
(add them after SDL creates the window; `SetWindowPos(... SWP_FRAMECHANGED)`), still return 0 from
`WM_NCCALCSIZE` (frame stays invisible), and:
- **Resize:** in `WM_NCHITTEST`, before the caption check, test an inset border (`SC_WIN_RESIZE_BORDER`
  ~6 px in window pixels) against the window rect and return `HTLEFT/HTRIGHT/HTTOP/HTBOTTOM` +
  the four corners. Order: **resize border first, then the top caption strip, then chain to SDL.**
- **Maximize without covering the taskbar:** handle **`WM_GETMINMAXINFO`** — clamp `ptMaxPosition`/
  `ptMaxSize`/`ptMaxTrackSize` to the monitor **work area** (`MonitorFromWindow` +
  `GetMonitorInfo().rcWork`); a borderless maximized window otherwise overflows the frame and hides
  the taskbar. Maximize is then reachable by double-clicking the caption strip, Aero-snap, or Win+↑.
- **Corners when maximized:** on `WM_SIZE`, if `IsZoomed(hwnd)` → `SetWindowRgn(hwnd, NULL, TRUE)`
  (square, fills the work area); else apply the rounded region. A rounded region on a maximized
  window looks wrong and clips oddly.

Keep `window.c` a **self-contained, reusable module** — future features (M6 pop-out bars, M7 app
windows) will want the same frameless/rounded/resizable chrome, so no scrcpy-mirror-specific
assumptions in it.

## M3.5b-2b SHIPPED — resize + maximize + rounder corners (2026-07-23, Antigravity)

Fixed the b-2a regression per D-051. `window.c` now keeps `WS_THICKFRAME | WS_MAXIMIZEBOX` (set via
`SetWindowLongPtr` + `SetWindowPos(SWP_FRAMECHANGED)`) while `WM_NCCALCSIZE` still returns 0;
`hit_resize()` returns the 8 edge/corner `HT*` codes for a `SC_WIN_RESIZE_BORDER`(6)px inset, checked
first in `WM_NCHITTEST`; `WM_GETMINMAXINFO` clamps maximize to `MONITORINFO.rcWork`; the rounded region
is dropped when `IsZoomed`. Radius 24→32. **Owner-verified:** drag-resize on every edge/corner +
maximize (double-click strip / Win+↑) work, taskbar not covered, square when maximized. Only the hover
control bar (b-2c) remains before M3.5b is complete.

### b-2c (separate, gated): the hover-reveal control bar (min / max-restore / close)
Deferred out of b-2b because it needs scrcpy's **SDL render loop** and **click-interception** — higher
risk. **Injection points pinned from the 3.3.4 source (durable facts for the agent):**

- **Draw** the overlay in `sc_display_render` (`app/src/display.c`) **after the video `SDL_RenderCopy`/
  `SDL_RenderCopyEx` and before `SDL_RenderPresent` (line ~349).** Cleanest wiring: give
  `struct sc_display` an optional `overlay` pointer + one render call there; keep all button logic in a
  **new portable `control_bar.{c,h}` component** (SDL-only, reusable by future features). Glyphs draw
  with `SDL_RenderDrawLine`/`SDL_RenderFillRect` (no font): minimize = a bar, maximize = a square
  outline (restore = two offset squares), close = an X; blend via `SDL_SetRenderDrawBlendMode(BLEND)`.
- **Intercept** mouse events in `sc_screen_handle_event` (`app/src/screen.c`, **before the
  `sc_input_manager_handle_event(&screen->im, event)` call at line ~877**). If a `SDL_MOUSEMOTION`/
  `SDL_MOUSEBUTTONDOWN`/`UP` falls in the button cluster, update hover / fire the action and **`return
  true` without calling the input manager** — so the press is never forwarded to the phone as a touch.
  This touches only the *dispatch* layer, NOT `input_manager.c`/the controller/the protocol.
- **`WM_NCHITTEST` trap:** window.c currently returns `HTCAPTION` for the whole top strip, so Windows
  eats those clicks as non-client and **SDL never sees them**. window.c must return `HTCLIENT` over the
  button cluster (top-right) so the SDL overlay receives the clicks. Share the button geometry
  (count / width / height, top-right origin) via constants in `control_bar.h` that **both** window.c
  (for the exclusion) and the overlay (for drawing/hit) use — keeps Win32 hit-test and SDL draw in sync.
- **Actions via SDL** (portable, no Win32): `SDL_MinimizeWindow`, `SDL_MaximizeWindow`/`SDL_RestoreWindow`
  (toggle on `SDL_GetWindowFlags & SDL_WINDOW_MAXIMIZED`), close = push `SDL_QUIT` (or a
  `SDL_WINDOWEVENT_CLOSE`).
- **Repaint:** scrcpy only renders on events/new frames. v1 = **instant** show/hide driven by the
  mouse-motion events that cross the top strip + `SDL_WINDOWEVENT_LEAVE` to hide (call the existing
  `sc_screen_render`); **no timer**. Smooth alpha **fade is deferred to a b-2c polish pass** (needs an
  `SDL_AddTimer` pushing a repaint event while animating) — land working buttons first.

### b-2c COORDINATE-SPACE GOTCHA (found on first test — durable fact)
The b-2c overlay was implemented correctly but drawn in the **wrong coordinate space**, so buttons were
invisible/misplaced. Two facts about this scrcpy build:
- The window is created with **`SDL_WINDOW_ALLOW_HIGHDPI`** (`screen.c`), so **SDL mouse-event coords are
  in window *points*** and **`SDL_GetWindowSize` returns points**, but the **renderer draws in drawable
  *pixels***. On a scaled display points ≠ pixels.
- The **video render path does NOT call `SDL_RenderSetLogicalSize`** (only the no-video icon path does);
  it letterboxes by passing an explicit `geometry` dstrect. So the renderer's coordinate space is raw
  **output/drawable pixels**.

**Rule for the overlay: do everything in drawable pixels.** Draw with `SDL_GetRendererOutputSize`
(not `SDL_GetWindowSize`); defensively `SDL_RenderSetLogicalSize(r,0,0)` before drawing and restore after.
For hit-testing, scale the incoming mouse point coords to pixels by `output/window` (get the renderer via
`screen->display.renderer`) before comparing against the pixel-space cluster. This makes the Win32
hit-test (`GetClientRect`, physical px), the drawn buttons, and the SDL hover test all agree. Fixed in
**b-2c-2**.
