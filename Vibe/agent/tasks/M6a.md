# TASK M6a — Home sections: removable + restorable, persisted per device

> **Agent for this session: opencode.** (Claude Code takes M6b — see `Vibe/agent/tasks/` when it lands.)
> **Read this whole file before editing anything.** It is the complete specification for this
> session. Every design decision has already been made for you. Implement it exactly. If something
> here is impossible or self-contradictory, **stop and report that** — do not improvise an alternative.

---

## 0. Standing rules (non-negotiable)

- Workspace/project = **yellow** (`<repo-root>`). Product/code = **Linc**
  (`Linc`). Working docs: `Vibe/agent/opencode-docs/`.
  Rules: `Vibe/agent/{AGENTS,OPENCODE,GUARDRAILS}.md` — nothing auto-loads.
- **Rate limit ~40 req/min. On a throttle, WAIT AND RETRY.** Never abandon the task or change
  approach because of a throttle. Batch reads; combine shell commands.
- **One tool call at a time.**
- **Never claim you did NOT do something. Never describe file state as "pre-existing" or "already
  applied."** Report only what a file contains *now*. Write each section's result line into your
  report **the moment you finish it** — do not reconstruct the session at the end.
- **Never move the real mouse cursor. Never tap the phone. Do not install the APK.**
- **Code + tests only. Do not edit any `.md` file.**
- Desktop build: kill any running `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- **Do NOT run `tools/devicesim`** — it deletes and restores the real `settings.json`. Building it is fine.
- **No protocol work.** `PROTOCOL.md` stays at v15. Nothing in this task crosses the wire.

---

## 1. What you are building, and why

The Home page's left pane is a fixed stack of six widget cards. Users want to **remove the ones they
don't use and add them back later**, with the choice **remembered per phone**.

This session does **removal, restoration and persistence only**. Flipping the panes left/right, the
Apps section, and pop-out panels are later sessions — **do not build them.**

### The six sections and their permanent IDs

In `DESKTOP/Linc.Desktop/Views/HomePage.xaml`, inside the widgets pane's
`<ScrollViewer><StackPanel Spacing="14" Padding="10">`, there are six sibling cards. Assign these
**exact** string IDs — they are persisted, so they must never change:

| ID | The card, identified by its existing XAML comment |
|---|---|
| `phone` | `<!-- Phone preview card: gradient from the live Material You palette. -->` |
| `quickactions` | `<!-- Quick actions: tonal M3 pills, two rows + sound profile segmented. -->` |
| `media` | `<!-- Media widget. -->` |
| `photos` | `<!-- Recent photos strip (v10) ... -->` |
| `shared` | `<!-- Files the phone shared, as they land on this PC. -->` |
| `clipboard` | `<!-- Clipboard history (in-memory only). -->` |

Locate each by its comment and confirm each is a **single sibling element** in that `StackPanel`
before touching anything. If the structure differs from this description, **stop and report it.**

---

## 2. Design decisions — already made, implement as stated

**2.1 The UI is a "Sections" flyout, not per-card ✕ buttons.** One `Button` labelled **"Sections"**
at the top of the widgets pane, opening a `Flyout` with six `ToggleSwitch` (or `CheckBox`) rows, one
per section, each labelled in plain language ("Phone", "Quick actions", "Media", "Recent photos",
"Shared files", "Clipboard history"). Toggling a row hides/shows that card immediately and persists.

*Rationale, so you don't second-guess it:* per-card overlay ✕ buttons would mean wrapping all six
cards in new Grids and adding hover states — a lot of churn in a 651-line file for a feature whose
requirement is simply "removable, with a button to add them back." The flyout satisfies the
requirement with one new control and six `Visibility` bindings, and it is more discoverable and more
reversible. **The planner chose this deliberately; do not substitute a different interaction.**

**2.2 Persistence lives on `KnownDevice`, per device.** A new dep-free record, same shape as
`MirrorSettings` / `DesktopModeSettings`.

**2.3 The record carries a `PanesSwapped` field now, unused this session.** It is reserved for M6b
(flip left/right). It is included now purely so `KnownDevice` and every consuming csproj change
**once** instead of twice. **XML-document it as reserved for M6b and do not read or write it from any
UI this session.**

**2.4 Hidden-by-absence, not shown-by-presence.** Store the **hidden** IDs. A device with no saved
layout, or a legacy `settings.json` with no `Home` field, must show **all six** — never a blank pane.

**2.5 Unknown IDs are ignored, never dropped.** If a persisted list contains an ID this build doesn't
know (a future section, or a downgrade), ignore it for display but **keep it in the saved list** so a
newer build still honours it.

---

## 3. Exact edits

### 3.1 New `DESKTOP/Linc.Desktop/Services/HomeLayout.cs`

Dependency-free (no WinUI), like `MirrorSettings.cs`:

- `public sealed record HomeLayout(IReadOnlyList<string>? HiddenSections = null, bool PanesSwapped = false)`
- `public static readonly IReadOnlyList<string> AllSectionIds` — the six IDs above, in display order.
- `public static HomeLayout Default { get; }` — nothing hidden, panes not swapped.
- `public bool IsVisible(string id)` — true when `HiddenSections` is null or does not contain `id`
  (ordinal comparison).
- `public HomeLayout WithSection(string id, bool visible)` — returns a new record with `id` added to
  or removed from the hidden list. Adding an ID already hidden, or removing one not hidden, returns
  an equivalent record without duplicating entries. **Preserves unknown IDs** (2.5).
- `public static string DisplayName(string id)` — the plain-language label for the flyout; unknown ID
  returns the ID itself rather than throwing.
- Keep every method **pure and Context-free** so the harness can exercise them.

### 3.2 `DESKTOP/Linc.Desktop/Services/DeviceRegistry.cs`

- Append `HomeLayout? Home = null` as the **last** positional parameter of `KnownDevice`.
  **Append only — never reorder**, or existing `settings.json` files stop deserializing.
- On `IDeviceRegistry` and its implementation add, mirroring the existing `Mirror` trio exactly:
  - `HomeLayout Home { get; }` → `Active?.Home ?? HomeLayout.Default`
  - `event Action? HomeChanged;`
  - `void SaveHome(HomeLayout layout);` → `UpdateActive(d => d with { Home = layout })` then raise `HomeChanged`.

### 3.3 **Every tool csproj that compiles `DeviceRegistry.cs` — DO NOT SKIP THIS**

`tools/*` harnesses `<Compile Include>` the real `DeviceRegistry.cs` with **no `ProjectReference`**, so
every new type `KnownDevice` references must be added to each of them by hand. **This has broken the
build twice** (M8a and M5b each shipped with `devicesim` and `blescan` silently uncompilable).

1. `grep` every file matching `tools/*/*.csproj` for `DeviceRegistry.cs`.
2. To **each** match, add `<Compile Include="..\..\DESKTOP\Linc.Desktop\Services\HomeLayout.cs" />`.
3. `dotnet build` **every one of those projects** and paste each result. A project you did not build
   is a project you did not verify.

### 3.4 `DESKTOP/Linc.Desktop/ViewModels/HomeViewModel.cs`

Take `IDeviceRegistry` (it may already have it — check before adding). Add:

- Six public bool properties — `ShowPhone`, `ShowQuickActions`, `ShowMedia`, `ShowPhotos`,
  `ShowShared`, `ShowClipboard` — each `=> _registry.Home.IsVisible("<id>")`.
- `public IReadOnlyList<HomeSectionToggle> SectionToggles` for the flyout, where
  `HomeSectionToggle` is a small observable item exposing `Id`, `DisplayName`, and a settable
  `IsVisible` whose setter calls the command below. Build it once in the constructor.
- `[RelayCommand] private void SetSectionVisible((string Id, bool Visible) arg)` — calls
  `_registry.SaveHome(_registry.Home.WithSection(arg.Id, arg.Visible))`.
- Subscribe to `_registry.HomeChanged` **and** `ActiveDeviceChanged`, marshal through the existing
  `DispatcherQueue` pattern used elsewhere in this file, and raise `OnPropertyChanged` for all six
  `Show*` properties plus refresh the toggle items. **Switching phones must show that phone's layout.**
- `public bool AllSectionsHidden` → true when all six are hidden.

**Guard against the echo bug (this has bitten us):** a toggle item's setter must **not** re-issue a
save when the value it is being given already equals the registry's current state. Compare first and
return early — the same "differs from known state" guard used in `DevicePage.xaml.cs`.

### 3.5 `DESKTOP/Linc.Desktop/Views/HomePage.xaml`

1. On **each** of the six cards, add `Visibility="{x:Bind ViewModel.ShowX, Mode=OneWay}"` with the
   matching property. **Change nothing else inside any card** — no restyling, no re-indenting, no
   moving markup.
2. Directly above the six cards, inside the same `StackPanel`, add the **Sections** button + flyout
   bound to `ViewModel.SectionToggles`. Style it consistently with the pane's existing controls.
3. Below the button, a `TextBlock` visible only when `ViewModel.AllSectionsHidden`, reading
   plain-language guidance such as *"All sections are hidden. Use Sections to bring them back."*

### 3.6 New harness `Linc/tools/homelayoutsim`

Plain `net8.0` console modelled on `mirrorsettingssim`: numbered PASS/FAIL, non-zero exit on any
failure, no WinUI, no DXGI, no phone. Cover:

1. `HomeLayout` round-trips through `DeviceRegistry`.
2. Legacy `KnownDevice` JSON with **no** `Home` field → `HomeLayout.Default`, all six visible.
3. `IsVisible` for every ID with an empty hidden list, and with each single ID hidden.
4. `WithSection(id, false)` then `WithSection(id, true)` returns to all-visible; no duplicate entries
   when hiding an already-hidden ID.
5. **Unknown IDs survive** a round-trip and a `WithSection` call on a different ID (decision 2.5).
6. Hiding all six leaves a valid record that still deserializes.
7. `DisplayName` returns a non-empty label for all six and echoes an unknown ID.
8. **UI wiring checks** (same rationale as `displaysim` section 6 — read the files as text; a missing
   file is a FAIL, not a skip): `HomePage.xaml` contains all six `ViewModel.Show*` bindings, and
   contains a binding to `SectionToggles`. Add a comment saying these crude checks exist so a future
   session that unwires a card fails loudly.

---

## 4. Acceptance — all must pass

1. Desktop MSBuild x64 → **0 errors**, no new warnings in files you touched (`CS9113` in
   `HomeViewModel.cs:131` is pre-existing — but note you *are* editing that file this session, so
   confirm you introduced no *additional* warnings).
2. `tools/homelayoutsim` → green, exit 0. Paste full output.
3. **Every** csproj found in 3.3 builds. Paste each.
4. `tools/mirrorsettingssim`, `tools/displaysim`, `tools/desktopsim` → still green (you changed
   `KnownDevice`). `desktopsim`'s ADB section may fail with no phone attached — say so plainly and
   **do not weaken the check**.
5. **Negative proof.** Temporarily delete one card's `Visibility` binding from `HomePage.xaml`,
   re-run `homelayoutsim`, confirm the wiring check **FAILS**. Restore it, re-run, confirm green,
   then **rebuild the desktop**. Paste both harness outputs and confirm the restore.

---

## 5. Report format

- One result line per section (0–4), written as you complete it.
- Files changed, one line of reason each.
- Verbatim commands + results for every build and harness run, including **each** csproj from 3.3.
- The section-5 negative proof: both outputs + confirmation of the restore + the post-restore rebuild.
- `git status`.
- **What you could NOT verify and why.**
- Anything in this task file that was ambiguous or wrong — that is feedback the planner wants.

**Out of scope — do not do:** pane flipping / swapping sides (M6b), the Apps section (M6c), pop-out
panels (M6d), per-card ✕ buttons, protocol changes, restyling any card, refactoring `HomeViewModel`
beyond what section 3.4 requires, touching the tabbed panel or the zero-device empty state.
