# Linc

Link your Android phone to your Windows PC — files, screen, clipboard, and notifications — with **zero terminal commands and zero ADB knowledge required**.

Linc uses ADB (wireless debugging) under the hood, but treats it strictly as an implementation detail: the apps bundle, configure, and manage it for you.

## Repository layout

This repo (`Linc/`) holds the **product and its resources** only:

```
ANDROID/       Android companion app (Kotlin, Jetpack Compose)
DESKTOP/       Windows app (C# / .NET 8, WinUI 3)
SCRCPY/        Vendored + custom scrcpy (mirror engine)
tools/         Verification harnesses (net8.0 consoles)
Documentation/         Formal, human-facing project documentation
```

The AI planning/agent docs live **one level up**, outside this repo, under `<repo-root>/`:

```
../master-docs/   Orchestrator (planner) context — ORCHESTRATOR, STATUS, AGENTS-REGISTRY, INDEX
../agent-docs/    Coding-agent rules (AGENTS.md · GEMINI.md · GUARDRAILS.md) +
                  working docs (claude-docs/ for Claude Code, gemini-docs/ for Antigravity)
```

## Documentation

- **`Documentation/`** — the **formal, human-facing** documentation of the whole project. Start at
  [Documentation/00-Linc-Master-Documentation.md](Documentation/00-Linc-Master-Documentation.md): what Linc is,
  how each app works, how they connect, its history, features, and design decisions.
- **Working docs** — the same project-reality docs (ROADMAP, PROTOCOL, DECISIONS, BRAIN,
  FEATURES, REQUIREMENTS-ANALYSIS) exist per active AI agent under `../agent-docs/`:
  **`claude-docs/`** for Claude Code and **`gemini-docs/`** for Antigravity. Only one agent works at a
  time; the human planner keeps the active folder current and syncs the other at each switch.
- **AI agents** must first read `../agent-docs/AGENTS.md`; Antigravity additionally follows
  `../agent-docs/GEMINI.md` and `../agent-docs/GUARDRAILS.md`.

The Era 1/2 planning history is archived under `v2.docs.old/`; its settled form is
[Documentation/02-History.md](Documentation/02-History.md).

## Building

### Android app (`ANDROID/`)
Requires Android Studio (or JDK 17 + Android SDK, compileSdk 35).

```
cd ANDROID
.\gradlew.bat assembleDebug
```

### Windows app (`DESKTOP/`)
Requires Visual Studio with the WinUI application development workload. Build from the IDE, or from the command line with **VS MSBuild** (the plain `dotnet build` CLI cannot build WinUI apps — it lacks the PRI resource-packaging tasks, which ship only with Visual Studio):

```
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find MSBuild\**\Bin\MSBuild.exe
& $msbuild DESKTOP\Linc.Desktop\Linc.Desktop.csproj -restore -p:Configuration=Debug -p:Platform=x64
```

## Status

The core product is **complete and hardware-verified** (pairing, auto-connect over USB/Wi-Fi/direct
TLS, file manager, two-way screen mirroring, clipboard, notifications, media, messages, calls,
folder/photo sync, device tabs, offline memory, Bluetooth presence). Work is now in the
**Convergence** phase — the phone and PC drive each other. See the active agent's `ROADMAP.md`
(currently [gemini-docs/ROADMAP.md](gemini-docs/ROADMAP.md)).
