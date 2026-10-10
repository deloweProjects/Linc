# Linc for Android — Material 3 Expressive redesign

_Spec for 1.0.0.11 (D-082). Written before the code; the code follows it. Where alpha18 lacks a
component the nearest one is named and noted._

Reference: Google's own Android 16 apps (Settings, Clock, Phone, Files, Recorder) and
m3.material.io's Expressive guidance. Bar: it looks like a first-party Pixel app.

---

## 1. Information architecture

```
MainActivity
├── Bottom NavigationBar (4)                Settings (gear in every top bar)
│   ├── Home      ─ Connection sheet        ├── Help & diagnostics → Logs
│   ├── Remote    ─ Keyboard / Media / Present / Voice / Power sheets
│   ├── Clipboard
│   └── Share
├── Onboarding (full-screen pager, first run or "Re-run setup")
└── MirrorActivity (unchanged) — opened by the Mirror PC FAB / Remote toolbar item
```

- Four destinations: **Home, Remote, Clipboard, Share**. Settings is a gear `IconButton` in the
  top bar of every top-level screen (as Files/Photos do) and opens a full-screen destination with a
  back arrow. Logs sits under Settings → Help & diagnostics.
- Today's **Status** tab becomes the **Connection** sheet, opened from Home's hero card.
- **Tools** becomes **Remote** (a tab; `ToolsActivity` stays as a thin host for old shortcuts and
  opens the same screen).
- **Mirror PC** is the primary action: an `ExtendedFloatingActionButton` on Home that collapses to
  its icon when the list scrolls, and an item on Remote's toolbar. `MirrorActivity` is untouched.
- **Motion**: switching tabs fades through (`motionScheme.defaultEffectsSpec`); pushing Settings
  or Logs slides on the shared X axis with `defaultSpatialSpec` springs. Sheets use the default
  `ModalBottomSheet` motion. Animator scale 0 is respected by Compose itself. Back from Settings
  and Logs is a `PredictiveBackHandler` (the screen follows the gesture); the manifest opts in with
  `android:enableOnBackInvokedCallback="true"`.

## 2. Tokens (`ui/theme`)

| Token | Value |
|---|---|
| Theme | `MaterialExpressiveTheme` everywhere (app, Remote, Mirror overlay) |
| Colour | Dynamic on Android 12+; otherwise a scheme generated from seed **indigo #4F5BD5** (tonal palette computed in `SeedScheme.kt`, light + dark), replacing the grey ramp. Follows the system light/dark |
| Type | M3 scale. Top-level titles: `LargeFlexibleTopAppBar` (displaySmall expanded → titleLarge collapsed). Section headers: `titleMedium` emphasised (SemiBold) in `primary`. Row titles `bodyLarge`; supporting `bodyMedium` in `onSurfaceVariant`. No bundled fonts |
| Shape | Hero cards and sheets **28 dp**; grouped list segments **24 dp outer / 4 dp inner**; buttons use Expressive `ButtonDefaults.shapes()` (morph on press); `MaterialShapes` (Cookie, Clover, Sunny, Soft burst) **only as clips** for icon/art containers — never with `shadow`/elevation (D-072, `PcRemoteOverlayTest`) |
| Colour roles | `primaryContainer` for the one hero per screen; `secondaryContainer` tiles; `tertiaryContainer` for media/fun; `surfaceContainer*` tiers for lists; `errorContainer` for warnings needing action. No literal colours except the connection-quality dots (green/amber/red) |
| Spacing | 16 dp gutters, 12 dp between groups, 2 dp between segments, 48 dp touch targets, rows 56–72 dp |
| Motion | `MaterialTheme.motionScheme` springs for every state change; no linear tweens; nothing delays the user |

## 3. Component kit (`ui/kit/`)

Each has `@Preview`s in light and dark.

| Component | What it is |
|---|---|
| `LincScaffold` | `Scaffold` + `LargeFlexibleTopAppBar` (exit-until-collapsed), gear action, optional back arrow, FAB slot, `SnackbarHost`; content is a `LazyColumn` with 16 dp gutters |
| `SectionHeader` | emphasised `titleSmall` in `primary`, 16 dp start, 24 dp above / 8 dp below |
| `SettingsGroup` / `SettingsRow` | segmented grouped list: rows on `surfaceContainer` (light) / `surfaceContainerHigh` (dark), 24 dp outer corners on the first/last row, 4 dp inner, 2 dp gaps. Row: leading tonal icon in a 40 dp circle, headline, `HelperText` supporting line, trailing slot (Switch / value / chevron / button). Whole row clickable when it has an action |
| `HeroCard` | 28 dp `primaryContainer` card, the one per screen |
| `ToolTile` | 2-up grid tile, `secondaryContainer`, icon clipped to a `MaterialShapes` shape, title + live state line, press morph (corner 28 → 20 dp spring); disabled shows at 38 % and still explains |
| `StatusChip` | quality dot + label in a `surfaceContainerHighest` pill |
| `BlockedState` | icon clipped to `MaterialShapes.Cookie9Sided`, one-line title, plain reason (always shown — **never** hidden by the explanations toggle), one action |
| `ConnectedChoice` | 2–3 exclusive options as connected `ToggleButton`s (`ButtonGroupDefaults.connected*ButtonShapes`) |
| `Loading` | `LoadingIndicator` / `ContainedLoadingIndicator`; `LinearWavyProgressIndicator` with a % label for downloads |
| `LincSnackbar` | `SnackbarHostState` provided through `LocalSnackbar`; replaces Toasts everywhere except over the mirror video |
| `HelperText` | the ONLY place explanatory text renders; reads `LocalShowExplanations` (Part 6) |
| Dialogs | `AlertDialog` only for destructive confirms (shut down, restart, clear history); `ModalBottomSheet` for everything else |

Missing in alpha18: none of the above. `HorizontalFloatingToolbar` exists and is used.

## 4. Screens

Every top-level screen has `@Preview`s: light, dark, 360 dp wide, 200 % font, and its blocked or
empty state. States below: **L**oading / **E**mpty / **B**locked / **Er**ror / **N**ormal.

### 4.1 Home
- Top bar "Linc" (flexible), gear. FAB: `ExtendedFloatingActionButton` "Mirror PC" (collapses on scroll).
- **Background warning** (`errorContainer` card, Allow + Auto-start) — only while not exempt.
- **Connection hero** (`HeroCard`): PC name ("Your PC" until the hello names it), `StatusChip`
  (Connected / Waiting / Off), transport icon, one quality line ("Protocol v23" goes to the sheet,
  not here). Tap → Connection sheet.
- **Tool grid** (`ToolTile` 2-up): Remote, Send to PC (→ Share tab), Clipboard (→ tab, last clip
  as state), Voice Access (on/off), Microphone (on/off), Quick Share.
- **Now playing** strip (tertiary) when the PC plays media: title/artist, play/pause.
- States — N: as above. B (companion off): hero says "Linc isn't running" + Start. E (never
  paired with a PC — no desktop cert and never connected): `BlockedState` "Connect to a PC" →
  onboarding's Connect page.

### 4.2 Remote (flagship)
- Top bar "Remote", gear. No page scroll.
- **Trackpad** fills the space: 28 dp `surfaceContainerHigh`, subtle dot grid, a 24 dp scroll
  strip on the right edge (drag = scroll), first-use hint chips ("Tap to click", "Two fingers to
  scroll", "Tap, then drag") that fade after three successful gestures.
- Below it, a connected **Left | Right** button pair (press-and-hold = hold the button).
- `HorizontalFloatingToolbar` (expanded): Keyboard, Media, Present, Voice, Power, and Mirror PC as
  its FAB-style trailing item. Each opens a `ModalBottomSheet`:
  - **Keyboard**: shows the IME; sticky Ctrl/Alt/Shift/Win `ToggleButton`s; Esc, Tab, arrows;
    dictation (phone speech → typed on the PC). Reuses `ModifierKeys`, `PcKeyboardLayout`,
    `KeyboardSink`.
  - **Media**: hero with a Cookie-clipped art placeholder, large play/pause, prev/next, volume
    `Slider` (sends set-volume), mute toggle.
  - **Present**: Previous (1/3 width) and Next (2/3, filled); tonal row Start / End / Black.
  - **Voice**: Voice Access on/off, Phone | PC mic `ConnectedChoice`, setup progress; the
    Microphone tool and "use phone mic in Mirror PC". Reuses `PcVoiceAccess`.
  - **Power**: Lock / Sleep / Restart / Shut down tiles (the last two confirm in an `AlertDialog`)
    and a brightness `Slider`.
- B: `BlockedState` with the per-version reasons (v13 media, v14 input, v17 controls keep their
  meaning); toolbar items are disabled, not hidden. Haptic ticks on clicks and latches.

### 4.3 Clipboard (Part 4)
- Top bar "Clipboard", gear. Composer card (multiline field + Send) → "Sent to your PC". Sync now
  button. Search field. List grouped by day (Pinned first): preview (3 lines, tap expands), source
  icon (phone/PC), time, actions Copy here / Send to PC / Pin / Delete. Overflow: Clear history
  (confirm).
- `HelperText`: "Android only lets Linc read what you copy while Linc is open — copies made
  elsewhere arrive when you open Linc or share text to it."
- E: "Nothing copied yet". B (not connected): composer disabled with the reason; history usable.

### 4.4 Share (Part 4)
- Top bar "Share", gear. Mode line: "Sending over Linc" / "Linc isn't connected — sending with
  Quick Share".
- Big actions (3 `ToolTile`s): Send files, Send photos, Send text or link.
- Received from PC (open / share), Transfers (in progress with progress, done, failed + Retry).
- All-files access missing → an `errorContainer` row with "Allow" — no silent failures.

### 4.5 Settings
Grouped sections, in order: **Connection** (background connection; stay connected in the
background + Allow / Auto-start; allow USB / Wi-Fi), **Features shared with the PC**
(notifications, SMS, calls, clipboard sync, all-files access — each granted state + fix),
**Clipboard** (sync, keep history, history size 100/500/2,000, don't save sensitive copies),
**Mirror PC** (quality, pointer mode, keyboard on text fields, which mic the PC hears),
**Appearance** (Show explanations), **Updates**, **Help & diagnostics** (Logs, Copy log, Re-run
setup, Write an NFC tag), **About** (version).

### 4.6 Connection sheet
`ModalBottomSheet`, grouped rows: PC, Connection (USB / Wi-Fi / Direct), Address, Protocol,
Round trip, Connected since; actions **Reconnect** and **Start / Stop companion**. Technical values
live in supporting lines, never titles.

### 4.7 Onboarding
Full-screen `HorizontalPager`; progress dots; decorative clipped `MaterialShapes` (no shadows).
Pages: Welcome → Notifications → Optional permissions (each skippable) → Background (exemption +
maker auto-start) → Connect to PC (wireless debugging steps + "the PC can send you the app") →
Done. Back / Skip / Next as a `ButtonGroup`.

### 4.8 Logs
Monospace, newest first; `FilterChip`s All / Warnings / Errors; Copy as an extended FAB ("Copy
log" — the report from D-073); empty state.

### 4.9 Mirror PC overlay
Behaviour unchanged (D-069/D-074/D-075). Tokens only: the seed scheme as its non-dynamic fallback.

## 5. Copy and accessibility
Sentence case; verbs on buttons; ≤ 2 lines of supporting text; no ADB/TLS/channel/HRESULT outside
Logs and the Connection sheet's supporting lines; `contentDescription` on every icon-only control;
48 dp targets; contrast checked in both themes; 200 % font scale works (no fixed heights on text);
TalkBack order = visual order.

## 6. Don'ts
No custom fonts, no animation libraries, no hard-coded colours (except quality dots), no shadows on
Expressive shapes, never hide blocked controls (disable + explain), no service/protocol behaviour
changes beyond the listed bugs and parts.

## 7. Proposed changes to the brief
- **Settings "Write an NFC tag"** lives under Help & diagnostics (it is a one-time setup task);
  the brief didn't place it.
- **The instant-hotspot (WRITE_SECURE_SETTINGS) status row** moves into the Connection group as a
  read-only row ("Hotspot quick link: ready") — kept, since D-063 made it disable-never-hide.
- **The phone never checked for updates.** `UpdateDecision`, `UpdateInstaller` and `UpdateUi`'s
  cards exist, but nothing fetched the manifest or called them, so "Keep Linc up to date" did
  nothing. 1.0.0.11 wires it: on opening Linc (switch on, at most every 6 h) the manifest is read;
  an optional update is a card on Home ("Update" downloads with a wavy % bar, checks the SHA-256,
  hands the APK to Android's installer; "Skip this version"); a forced one is the existing
  full-screen `UpdateRequiredScreen`.
