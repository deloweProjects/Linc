# TASK M6b — fix the M6a visibility regression, then flip the Home panes left/right

**Agent for this session: Claude Code.** Read this whole file before editing anything. The design
decisions below are settled — implement them rather than re-deriving them. Where this file says
"decided", that is the planner's call. Where it says "your judgement", it genuinely is yours.

---

## 0. Orientation and standing rules

- Workspace/project = **yellow** (`<repo-root>`). Product/code = **Linc** (`Linc`).
- **Live project docs — the single source of truth for every agent (D-056):**
  `Vibe/agent/opencode-docs/`. Read it as "the live docs", not "opencode's
  docs". Start with `BRAIN.md`; its "Recurring gotchas" section is directly relevant to Part 0.
  **`Vibe/agent/claude-docs/` project docs are DEAD** — stale by whole milestones, do not read them.
  The one live file there is `claude-docs/CLAUDE.md`, your behavioural guide. Also read
  `Vibe/agent/AGENTS.md` and `Vibe/agent/GUARDRAILS.md`.
- **Do not delegate to nemo.** You were chosen because this session needs judgement about an existing
  651-line view and a layout mechanism with real regression risk.
- **Do not over-explore.** The task is fully scoped below. Read what you need, then build.
- **Never move the real mouse cursor. Never tap the phone. Do not install the APK.** Hand interaction
  checks back as a numbered manual script.
- **Code + tests only. Do not edit any `.md` file** — the planner owns all docs.
- **No protocol work.** `PROTOCOL.md` stays at v15. Nothing here crosses the wire.
- Desktop build (plain `dotnet build` cannot build WinUI): kill any running `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- **Do NOT run `tools/devicesim`** — it backs up, deletes and restores the real `settings.json`, so an
  interrupted run destroys the real pairing. Building it is fine.

---

## PART 0 — the M6a regression. Do this first, report it before starting Part 1.

M6a gave the six Home widget cards persisted show/hide. Three of them already had **data-driven**
visibility, so it correctly introduced combined properties in
`DESKTOP/Linc.Desktop/ViewModels/HomeViewModel.cs`:

```csharp
public bool ShowMediaWidget  => ShowMedia  && HasMedia;
public bool ShowPhotosWidget => ShowPhotos && HasPhotos;
public bool ShowSharedWidget => ShowShared && HasReceivedShares;
```

`HomePage.xaml` now binds those three cards' `Visibility` to the **combined** properties.

**The bug:** the combined properties are raised **only** inside `RefreshSections()` — i.e. only when
the *layout* half changes. The sites that raise the *data* half do not raise them:

- `HasMedia` — raised at roughly **line 1108**
- `HasPhotos` — raised at roughly **lines 355, 479 and 645**
- `HasReceivedShares` — raised at roughly **line 529**

So when music starts playing, a photo arrives, or a file is shared, **the card no longer appears.**
It only shows up if the user happens to open the Sections flyout or switch device. Three
previously-working widgets are effectively dead. A clean build and a green harness cannot see this.

**Fix it.** Every site that raises `HasMedia`, `HasPhotos` or `HasReceivedShares` must also raise the
matching `Show*Widget`. *Your judgement* on the mechanism — paired `OnPropertyChanged` calls at each
site, `[NotifyPropertyChangedFor]` where the source is an `[ObservableProperty]`, or a small helper —
but **verify by grep that you found every site**, including any that raise these indirectly through a
collection-changed handler. Line numbers above are a guide, not a guarantee; they may have shifted.

State in your report: the exact list of sites you found, the mechanism you chose, and how you
convinced yourself the list is complete.

**Also add a harness check that would have caught it.** In `tools/homelayoutsim`, extend the wiring
section to assert that for each of the three combined properties, the `HomeViewModel.cs` text contains
**at least two** `OnPropertyChanged(nameof(ShowXWidget))` occurrences (one in `RefreshSections`, at
least one at a data site). Crude, deliberately so — comment it to that effect.

> **Known weak check, don't be fooled by it:** the existing wiring assertion for `ViewModel.ShowMedia`
> passes on the substring inside `ViewModel.ShowMediaWidget`. Tighten those three assertions to match
> the exact binding string actually used by each card while you are in there.

---

## PART 1 — flip the panes left/right

### What it is

`HomePage.xaml`'s root is a three-column `Grid`: **column 0** the widgets pane
(`<ColumnDefinition x:Name="WidgetsColumn" Width="430" MinWidth="320" />`), **column 1** the
`Splitter`, **column 2** the tabbed panel (Notifications / Photos / Messages / Calls). Users want to
**swap which side each pane sits on**, remembered per device.

`HomeLayout.PanesSwapped` **already exists and is already persisted** — M6a shipped it unused
specifically so `KnownDevice` and all four consuming tool csprojs would migrate only once. **You do
not need to touch `KnownDevice`, `DeviceRegistry`, or any `tools/*.csproj` in this session.** If you
find yourself editing them, stop and re-read.

### Decided design

**1.1 The fixed-width column follows the widgets pane.** Do **not** simply swap `Grid.Column` on the
two panes — column 0 is a fixed 430px and column 2 is star-sized, so a naive swap would give the
widgets pane the star column and squeeze the tab panel into 430px. **When swapped, the column
*widths* swap too**, so the widgets pane keeps its fixed width and its `MinWidth`, and the tab panel
keeps the flexible remainder. The splitter stays in column 1 either way.

**1.2 Do it in code-behind, not binding gymnastics.** A single `ApplyPaneOrder()` method in
`HomePage.xaml.cs` that sets both panes' `Grid.Column` and both `ColumnDefinition.Width`/`MinWidth`
values from the current `PanesSwapped`. Call it once when the page loads and again whenever the value
changes. *Rationale:* `x:Bind` onto `ColumnDefinition.Width` needs `GridLength` converters and gets
ugly fast; one imperative method is clearer and easier to verify. **Decided.**

**1.3 The control is a second button, "Swap sides", beside the existing "Sections" button.** Same
styling. One click toggles and persists immediately. **Decided** — do not build a settings page, a
drag-to-rearrange interaction, or a flyout item.

**1.4 The splitter must keep working after a flip**, and dragging it must resize the widgets pane in
both orientations — not silently resize the wrong pane. This is the main thing that can go subtly
wrong; call out in your report how you satisfied yourself it holds.

**1.5 Guard the echo.** Toggling writes `_registry.SaveHome(current with { PanesSwapped = … })`, which
raises `HomeChanged`, which re-applies the layout. Use the same "differs from the known value" guard
the rest of this codebase uses (`HomeSectionToggle`, `DevicePage.xaml.cs`) so the write cannot
re-trigger itself. **This project has hit the echo bug three times — do not make it four.**

### The work

- `ViewModels/HomeViewModel.cs` — a `PanesSwapped` bool reading `_registry.Home.PanesSwapped`, a
  command to toggle it (persisting via `SaveHome`), and a re-raise inside `RefreshSections()` so a
  device switch re-applies the right order.
- `Views/HomePage.xaml` — the **"Swap sides"** button beside "Sections". Nothing else structural.
- `Views/HomePage.xaml.cs` — `ApplyPaneOrder()` per 1.2, called on load and on change.
- `Services/HomeLayout.cs` — only if `PanesSwapped` needs a `With…` helper for symmetry with
  `WithSection`. Keep it pure. **Do not change the record's shape or parameter order.**
- `tools/homelayoutsim` — add checks: `PanesSwapped` round-trips through `DeviceRegistry`; a legacy
  record without it defaults to `false`; toggling twice returns to the original; and a wiring check
  that `HomePage.xaml` contains the Swap-sides button binding and `HomePage.xaml.cs` contains
  `ApplyPaneOrder`.

---

## Acceptance

1. Desktop MSBuild x64 → **0 errors**. You are editing `HomeViewModel.cs`, which carries a
   pre-existing `CS9113`; confirm you added no *further* warnings.
2. `tools/homelayoutsim` green, exit 0 — paste full output, including the new Part 0 and Part 1 checks.
3. `tools/mirrorsettingssim`, `tools/displaysim`, `tools/desktopsim` still green. `desktopsim`'s ADB
   section fails with no phone attached — say so plainly and **do not weaken the check**.
4. **Two negative proofs, both required.**
   - Remove one of the Part 0 data-site raises → `homelayoutsim` must **FAIL** → restore → green.
   - Remove the Swap-sides wiring → must **FAIL** → restore → green.
   Paste all four outputs, confirm both restores, then **rebuild the desktop**.

---

## Report

Files changed with a one-line reason each; verbatim commands and results for every build and harness
run; **the complete list of data-raise sites you found in Part 0 and how you know it is complete**;
**how you satisfied yourself the splitter still resizes the correct pane after a flip**; how you
guaranteed the swap toggle cannot re-trigger itself; both negative proofs; `git status`; a numbered
manual test script for the owner covering *media appearing while playing*, *hide/show*, *flip*,
*flip then hide*, *restart persistence*, and *switching between two phones*; **what you could not
verify and why**; and anything in this task file that was ambiguous or wrong — that is feedback the
planner wants, not a failure.

**Out of scope:** the Apps section (M6c), pop-out panels (M6d), per-card ✕ buttons, protocol changes,
`KnownDevice` / `DeviceRegistry` / any `tools/*.csproj` schema change, restyling any card, refactoring
`HomeViewModel` beyond what is required above, the tabbed panel's internals, the zero-device state.
