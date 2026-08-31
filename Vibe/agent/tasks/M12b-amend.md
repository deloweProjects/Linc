# M12b-amend — the cause is found. Part B is now prescriptive, not a hunt.

> Amendment to `M12b.md`, which stays authoritative for everything not named here. **Part A is
> unchanged and still runs first** — the crash log is worth having regardless, and this diagnosis is
> exactly the evidence a future tester will not be able to produce without it.

## A1 — the evidence (planner-verified, not a hypothesis this time)

**Owner's Windows Error Reporting entry, second PC:**

```
P1: Linc.Desktop.exe      P2: 0.1.0.0
P4: combase.dll           P5: 10.0.26100.1882
P7: 80004005              P8: 000000000005d464
```

Faulting module **`combase.dll`** (COM), exception **`0x80004005` = E_FAIL**. Machine is **Windows 11
build 26100**, far newer than the 19041 target.

**I then listed the publish output directly.** Findings:

- WinAppSDK runtime **is** bundled — `Microsoft.WindowsAppRuntime.dll`, `Microsoft.ui.xaml.dll`,
  `CoreMessagingXP.dll`, `MRM.dll`, `WinUIEdit.dll`, both Bootstrap DLLs.
- .NET **is** self-contained — `coreclr.dll`, `hostfxr.dll` present.
- Payloads **are** in the output — `SCRCPY/Custom/bin/` (104 files) and `Assets/companion.apk`.
- **The only VC++ runtime file present is `vcruntime140_cor3.dll`** — .NET's private copy.
  **`msvcp140.dll`, `vcruntime140.dll` and `vcruntime140_1.dll` are absent from the entire tree.**

**Conclusion.** Those WinAppSDK components are native C++ and link against the VC++ 2015–2022 runtime.
The dev machine has it via Visual Studio; a clean Windows 11 does not. COM cannot activate the XAML
framework class, and `combase.dll` surfaces that as **E_FAIL** — matching the signature exactly.

**Hypotheses now DEAD, do not spend time on them:** B2.2 (Windows too old), B2.3 (wrong folder copied),
B2.4 (missing payloads). **B2.1 was right.**

## A2 — what Part B must now do instead of §B2's hunt

**A2.1 Confirm the dependency chain before fixing it.** Run `dumpbin /dependents` (or equivalent) against
`Microsoft.ui.xaml.dll`, `CoreMessagingXP.dll`, `MRM.dll` and `WinUIEdit.dll` **in the publish folder**,
and **paste the output.** Confirm they import `msvcp140.dll` / `vcruntime140.dll` /
`vcruntime140_1.dll`. If they do not, my conclusion is wrong — **say so and follow the evidence.**

**A2.2 Ship the runtime app-local.** Microsoft permits app-local deployment of the VC++ redistributable
DLLs, and it is the right choice here: a zippable folder that "just runs" is the entire point of this
milestone, and telling a beta tester to go install a redistributable first defeats it.

Copy the **x64** `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` (and `msvcp140_1.dll` /
`msvcp140_2.dll` if the dependency walk names them) into the publish output beside the exe. Source them
from the Visual Studio redist directory
(`C:\Program Files\Microsoft Visual Studio\18\Community\VC\Redist\MSVC\<ver>\x64\Microsoft.VC143.CRT\`) —
**locate it, do not assume the version folder name.** Wire it into the build so it happens on every
publish, not as a manual step: a `Content`/`None` item with `CopyToPublishDirectory`, or a target that
runs after publish. **State which mechanism you chose and why.**

**A2.3 Do not vendor them into the repo.** Copy from the installed redist at publish time. Adding
Microsoft's binaries to the source tree is a licensing and hygiene problem this project does not need,
and `git status` is already carrying enough.

**A2.4 Fail loudly if they are missing at publish time.** If the redist source directory cannot be
found, the **publish must fail with a plain-language message** naming what was not found. A silent
publish that produces a broken folder is precisely the failure this milestone exists to eliminate —
and precisely what happened here.

**A2.5 Extend `tools/packagesim` with an output-side check.** Assert the three (or five) VC++ DLLs are
present **in the publish folder**, not merely at some source path. **This is the gap that let a broken
package ship green:** M12 verified payloads at their *source* paths and never looked at the output. If
the publish folder does not exist when the harness runs, that is a **skip with a clear message**, not a
pass — a check that silently passes when it cannot run is worse than no check.

## A3 — acceptance changes

Replaces §B4's items 1–3:

1. `dumpbin` output pasted (A2.1), confirming or refuting the dependency.
2. Publish re-run; **list the VC++ DLLs in the output folder** proving they are now there.
3. `packagesim` green including the new output-side check.
4. **Negative proof:** delete one VC++ DLL from the publish output → the new check fails → restore →
   green. **And separately**, point the redist-source path at a non-existent directory → the publish
   fails with the plain-language message (A2.4) → restore → green.

## A4 — for the report

Say plainly whether the owner's second PC is expected to work now **without** installing anything, and
what the minimum OS requirement actually is. **The owner will re-test by copying the folder across —
that is the only proof that counts, and it is his to run, not yours to claim.**
