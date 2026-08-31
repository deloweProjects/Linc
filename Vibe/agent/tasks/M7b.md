# TASK M7b — harden the app launcher: real virtualization, real lazy icons, remembered window geometry

**Agent for this session: Claude Code.** Read this whole file before editing anything. Decisions marked
**decided** are settled; where it says *your judgement*, it genuinely is yours.

M7a shipped the feature: a tile click opens the app in its own PC window. This session makes it hold up
with a real app list and a real workday.

---

## 0. Orientation and standing rules

- Project root for every relative path: **`Linc`**.
- **Live docs (D-056):** `Vibe/agent/opencode-docs/`. Read **D-059**
  (the launch design), **D-058** (the inventory + cache rules) and `BRAIN.md`'s "Recurring gotchas" —
  the note about `ItemsRepeater` realizing every tile is this session's starting point.
  `Vibe/agent/claude-docs/` project docs are DEAD; only `claude-docs/CLAUDE.md` is live.
- **Do not delegate to nemo. Never move the real cursor. Never tap the phone. Do not install the APK.**
- **Code + tests only. Do not edit any `.md` file. No protocol work** — v16 is correct and shipped.
- Desktop build: kill any running `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- `git status` from `Linc/`. Harnesses are safe (D-057) — no devicesim warnings.

---

## 1. Three jobs

### 1.1 Virtualize the grid — **decided**

`BRAIN.md` records that the Apps `ItemsRepeater` realizes **every** tile. Since M6c-3 the Apps card has
its **own** internal `ScrollViewer`, so virtualization is now achievable: an `ItemsRepeater` virtualizes
when it is inside a scrolling host that drives it. Make that actually happen — the repeater should be
the scroll host's direct content, with nothing in between that would measure it unbounded (a
`StackPanel` wrapper around it will silently kill virtualization; that is the classic mistake here).

**Prove it, don't assume it.** *Your judgement* on how — an `ElementPrepared` counter exposed for the
harness is the obvious route. The bar: with a few hundred apps, only a screenful plus a margin is
realized. State the number you measured.

### 1.2 Make icon fetching genuinely lazy — **decided**

M6c fetches an icon for every app in the list, because every tile was realized. With 1.1 done, fetch
**only for tiles the repeater actually realizes**, via `ElementPrepared` / `ElementClearing`.

Keep all three existing guards from M6c — `_appsRefreshInFlight`, `_appIconsInFlight`, `_appIconMisses`
(a package with no icon is asked once, *ever*). **Do not weaken them**; scrolling must not turn a miss
into a repeated fetch, and scrolling back and forth must not re-request a cached icon.

Icons already on disk load from cache without a round trip — that path stays.

### 1.3 Remember each app's window geometry — **decided**

Reopening an app should restore where and how big its window was. scrcpy takes `--window-x`,
`--window-y`, `--window-width`, `--window-height`; pass them when a remembered geometry exists and omit
them entirely when it does not (do **not** invent defaults — scrcpy's own placement is better than a
guess).

**Storage — follow D-058's rule.** This is per-device *and* per-app, so it belongs in the per-device
cache directory beside `apps.json`, e.g. `<root>/cache/<serial>/appwindows.json`. **`<root>` must come
from `DeviceRegistry.RootPath`, exactly as `AppCatalog` does** — never `Environment.GetFolderPath`.
That rule now covers every store under `%LOCALAPPDATA%\Linc` (D-057 + D-058). Reuse `AppCatalog`'s
approach; *your judgement* on whether it lives inside `AppCatalog` or beside it.

- **Capture on close, not continuously.** Read the window rectangle when the process exits.
  *Your judgement* on the mechanism; if you cannot read it reliably at exit, **say so and persist
  nothing** rather than storing a wrong rectangle.
- **Sanity-check on restore.** A remembered rectangle that is off-screen (monitor layout changed, a
  display disconnected) must be discarded rather than opening a window nobody can see. State the rule
  you applied.
- A corrupt or unreadable `appwindows.json` degrades to "no memory", never throws.

---

## 2. Also in scope: open-window visibility

Right now nothing on screen tells you which apps are open, and closing several means hunting windows.

Add a **modest** affordance to the Apps section: a count of open app windows and a **"Close all"**
action, shown only when at least one is open. `AppLaunchService` already tracks the dictionary and
already has `CloseAllAsync` from M7a — this is surfacing what exists, not new machinery.

**Do not** build a window manager, a per-window list with thumbnails, or docking. **Decided.**

---

## 3. Acceptance

1. Desktop MSBuild x64 → **0 errors**, no new warnings in files you touched (the `CS9113` on
   `PhotoVm`'s constructor is pre-existing).
2. **`tools/applaunchsim` extended and green**, covering: the geometry flags are **absent** when
   nothing is remembered and **present and correct** when it is; an off-screen remembered rectangle is
   discarded; `appwindows.json` round-trips and a corrupt file degrades to empty; the store resolves
   under an injected root (**assert it does not touch the real one** — the D-057 rule); and the
   realized-tile count stays bounded for a large list.
3. `appssim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim` green. `desktopsim`'s ADB section
   fails with no phone — say so; **do not weaken it**.
4. **Two negative proofs**, broken → FAIL → restored → green, outputs pasted:
   - Wrap the `ItemsRepeater` in a `StackPanel` (killing virtualization) → the realized-count check
     must fail.
   - Point the window store at `Environment.GetFolderPath` instead of the injected root → the D-057
     check must fail.
   Rebuild afterwards; confirm both restores.

**If a harness check written in an earlier session contradicts this task, invert it and say so
plainly** — as M7a did with `appssim`'s "tiles are inert" checks. That is expected here, not a
weakening; name every check you flip and why.

---

## 4. Report

Per-part result lines as you go. Files changed with one-line reasons. **The realized-tile count you
measured** for a large list, and how you measured it. How you capture geometry at exit and what you do
when you cannot. Your off-screen rule. Confirmation the new store uses `DeviceRegistry.RootPath`.
Every earlier-session check you inverted. Verbatim commands and results. Both negative proofs.
`git status` from `Linc/`. A numbered manual test script covering: scrolling a long app list (smooth,
icons filling in as tiles appear, no re-fetch when scrolling back), opening an app, moving and
resizing its window, closing it, reopening it to the same place and size, the open-window count, "Close
all", and a remembered window whose monitor is gone. **What you could not verify and why.** Anything
ambiguous or wrong in this file.

**Out of scope:** pop-out panels (deferred), any protocol change, `KnownDevice` schema changes,
per-app scrcpy quality settings, a window manager/taskbar, restyling the widgets pane or tabs panel.
