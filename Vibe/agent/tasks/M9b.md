# TASK M9b — notification history: opt-in, default OFF, with retention and a clear action

> **Agent for this session: opencode.** **Read this whole file before editing anything.** It is the
> complete specification. Every design decision is already made — implement it exactly. If something
> here is impossible or self-contradictory, **stop and report that**; do not improvise an alternative.

---

## 0. Standing rules (non-negotiable)

- Workspace/project = **yellow** (`<repo-root>`). Product/code = **Linc**, at
  **`Linc`** — every relative path below hangs off that `Linc/` root.
- **Live docs (D-056):** `Vibe/agent/opencode-docs/`. Read **`DECISIONS.md`
  › D-045** (the decision you are implementing), **D-032** (the privacy default it scopes a reversal
  of), **D-044**, and `BRAIN.md`'s "Recurring gotchas".
  Your rules: `Vibe/agent\{AGENTS,OPENCODE,GUARDRAILS}.md`. Nothing auto-loads.
- **Rate limit ~40 req/min. On a throttle, WAIT AND RETRY.** Never abandon the task or change approach
  because of a throttle. Batch reads; combine shell commands.
- **One tool call at a time.**
- **Never claim you did NOT do something. Never describe file state as "pre-existing".** Report what a
  file contains *now*. **Write each part's result line into your report as you finish it.**
- **Never move the real cursor. Never tap the phone. Do not install the APK.**
- **Code + tests only. Do not edit any `.md` file. No protocol work** — v16 stays as it is. Notification
  data already arrives over the existing wire; this session only decides what to keep.
- Desktop build: kill any running `Linc.Desktop` (it hides to the tray on close and is single-instance
  — use `Stop-Process`), then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- **Run every harness with CWD = `Linc`**, not `yellow/` — some resolve the
  repo root by walking up for a direct `DESKTOP` child and fail confusingly from the wrong directory.
- `git status` works from `Linc/`.

---

## 1. What this is

M9a built `LincStore` and created the `notifications` table. **It is still empty and nothing reads it.**
This session fills it — but **only when the user has explicitly asked for that.**

**This is a privacy-sensitive feature and the guardrails are the point of it.** D-032 keeps notification
bodies off disk by default. D-045 permits a **scoped, consented** exception: notification history may
persist bodies **only** because the user turned it on. Everything below follows from that.

---

## 2. Decided design — implement exactly

**2.1 Default OFF. No exceptions, no "helpful" default.** A fresh install, a new device, an upgraded
install with no stored preference — all OFF. If you find yourself writing a default of `true`
anywhere, you have misread this task.

**2.2 Nothing is written to `notifications` while it is off.** Not "written then hidden", not "kept in
memory and discarded" — **the insert must not happen.** Check the setting at the ingestion point, before
the row is built. The harness will assert an empty table after ingesting with the toggle off.

**2.3 Retention: 7 / 30 / 90 days, default 30 when the feature is first enabled.** A stored row older
than the window is deleted. Prune **on app start** and **whenever the retention setting changes to a
shorter window** — the user shortening it expects the old data gone now, not in a day.

**2.4 A "Clear history" action deletes every stored notification immediately**, for all devices, and
reports how many rows it removed in plain language. It must work whether the feature is on or off —
turning the feature off does **not** delete what is already stored (that is the Clear button's job),
but it does stop new rows. **Say this in the UI**, because a user who turns it off will reasonably
assume the history is gone.

**2.5 Turning it off does not delete. Turning it on does not backfill.** History starts from the moment
it is enabled. Do not attempt to recover past notifications from anywhere.

**2.6 Where the setting lives: `SettingsPage`**, in its own card headed "Notification history", with
the toggle, the retention picker (disabled while the toggle is off), the Clear action, and a short
plain-language line explaining that bodies are stored on this PC and that history is kept per device.
Follow the existing Settings card conventions; **do not restyle the page.**

**2.7 Persist the preference where device-independent settings already live.** This is an app-wide
preference, not a per-device one — **do not** put it on `KnownDevice` or `HomeLayout`. Find where the
existing app-wide settings are stored and follow that pattern. State what you chose and why.

**2.8 Bound the growth.** Store notification **text** only. **Do not** store icon or art blobs this
session — D-045's "blobs by hash" idea is deferred; a text-only history with retention cannot grow
without bound, and blobs need a hashing/GC design of their own.

**2.9 Never log notification bodies.** `BRAIN.md`'s standing rule: log that an event happened, never its
content. Storing a body in the database because the user asked is the consented exception; writing it
into `logs/` is not, and never becomes one.

---

## 3. The work

1. **`Services/LincStore.cs`** — typed methods over the existing `notifications` table: insert one;
   query recent for a device (newest first, with a sensible cap); prune older than N days; delete all
   (returning the count). Keep them callable from a plain `net8.0` harness, as M9a did.
2. **The setting** — per 2.7, plus retention as an int (7/30/90) with the 2.3 default.
3. **The ingestion point** — find the **single** place an incoming notification is handled on the
   desktop (start from `CompanionClient`'s notification path and the notifications view model) and add
   the guarded persist there. **One place only.** If you find more than one plausible ingestion point,
   **stop and report it** rather than instrumenting several.
4. **`Views/SettingsPage.xaml`** + its view model — the card per 2.6. The Clear action asks for
   confirmation before deleting.
5. **Prune on startup** — alongside M9a's existing fire-and-forget startup task, and equally
   non-blocking and non-throwing.
6. **`tools/storesim`** — extend, don't replace. Add: insert-and-query round-trips; **ingesting with
   the toggle OFF leaves the table empty** (2.2); prune deletes only rows past the window and keeps the
   rest; delete-all returns the right count and empties the table; retention change to a shorter window
   prunes immediately; and a check that **`LincStore.cs` never writes a notification body to the log**
   (a crude text check that no log call takes the body parameter — comment it as crude, per house style).

---

## 4. Acceptance — all must pass

1. Desktop MSBuild x64 → **0 errors**, no new warnings in files you touched (the `CS9113` on
   `PhotoVm`'s constructor is pre-existing).
2. `tools/storesim` green, exit 0 — paste full output.
3. `appssim`, `applaunchsim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim` still green.
   `desktopsim`'s ADB section fails with no phone — say so; **do not weaken it**.
4. **Launch the built exe, confirm it starts and stays alive ~10 s, then `Stop-Process` it.** Confirm
   the real `linc.db` still reports `schema_version = 1` and that `notifications` is **still empty**
   (you did not enable the feature, so nothing may have been stored). Report the row count.
5. **Two negative proofs**, broken → FAIL → restored → green, both outputs pasted:
   - Make the default `true` → the "default is off" check must fail.
   - Remove the toggle guard at the ingestion point → the "off means empty table" check must fail.
   **Both must be provable without writing to the real store** — assert against a temp-rooted store.
   Rebuild afterwards; confirm both restores.

---

## 5. Report format

- One result line per part, written as you finish it.
- Files changed, one line of reason each. **Where you put the preference and why** (2.7). **The single
  ingestion point you chose, and what else you considered and rejected** (3.3).
- Verbatim commands and results for the build, every harness, and the launch check.
- Explicit confirmation that **no code path logs a notification body** (2.9).
- Both negative proofs. `git status` from `Linc/`.
- A numbered manual test script for the owner covering: default off on a fresh profile, turning it on,
  notifications accumulating, turning it off (history stays, new ones stop), Clear, and each retention
  window.
- **What you could NOT verify and why.**
- Anything in this task file that was ambiguous or wrong — that is feedback the planner wants.

**Out of scope — do not build:** the offline queue (M9c), cache-first Sync (M9d), icon/art blob storage
(2.8), search or filtering over history, a history *viewer* UI (this session stores and manages; M9d
decides how it is browsed), protocol changes, `KnownDevice` changes, moving `settings.json` ownership
into SQLite.
