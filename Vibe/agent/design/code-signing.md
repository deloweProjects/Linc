# Code signing for Linc — options, recommendation, and what's already wired

_1.0.0.11, D-086. Only the owner can get a certificate (it needs his identity). Facts below are as of
mid-2026 from my own knowledge — prices and eligibility change, so check each provider's page before
paying._

## Why it matters

Unsigned, `Linc-Setup.exe` and `Linc.Desktop.exe` make Windows SmartScreen say "Windows protected
your PC". People get past it with **More info → Run anyway**, but many won't. A signature names the
publisher; SmartScreen still needs *reputation* (downloads over time) before the warning disappears
completely, even with a signature — and since 2024 that is true for EV certificates too.

## The options

| Option | Cost | Who | What the owner must do | Fit for Linc |
|---|---|---|---|---|
| **Certum Open Source Code Signing** | roughly €30–70 / year (cloud "SimplySign" variant: no card reader) | Individual open-source developers, worldwide | ID check (passport scan + a video/selfie step), prove the project is open source (link to the GitHub repo); install SimplySign Desktop, which puts the certificate in the Windows store while signed in | **Recommended.** Cheap, open to individuals outside the US, signs on this PC exactly as `release.ps1` already does (`LINC_SIGN_THUMBPRINT`). The certificate says "Open Source Developer, <name>". |
| **SignPath Foundation** | Free | OSS projects with an OSI licence (MIT ✓) and a public repo | Apply with the project; set up signing *from CI* — SignPath only signs artifacts built by a verified pipeline (GitHub Actions), not on a developer's PC; publish a code-signing policy page | Good later. Today Linc is built on the owner's PC (VS MSBuild, Android Studio JBR), so this needs a GitHub Actions build first — real work, and the WinUI build is the hard part. |
| **Azure Trusted Signing** | about $10 / month | Organisations; **individuals only in the US and Canada** (identity validation) | Azure subscription, identity validation | Probably **not available** to an individual in Zimbabwe — check the current eligibility page before trying. |
| **Commercial OV / EV** (Sectigo, DigiCert, SSL.com…) | about $200–600 / year; hardware token required since 2023 | Individuals (OV) or registered businesses (EV) | ID/business verification, buy or rent a hardware token or cloud HSM | Works, but the most expensive, and EV no longer skips SmartScreen's reputation period. |

## Recommendation

**Certum Open Source Code Signing, SimplySign (cloud) variant.** Steps for the owner:

1. On certum.eu, buy "Open Source Code Signing" (choose the SimplySign / cloud key option).
2. Complete the identity verification (they email instructions; have a passport ready).
3. Install **SimplySign Desktop**, sign in — the certificate appears in Windows' certificate store.
4. Find its thumbprint: `Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint`.
5. Before releasing: `$env:LINC_SIGN_THUMBPRINT = "<thumbprint>"`, then run `release.ps1` as usual. It
   signs `Linc.Desktop.exe`, the installer and its uninstaller with SHA-256 and a timestamp. Nothing
   else changes; without the variable, nothing is signed.

(A `.pfx` file works too: `LINC_SIGN_PFX` + `LINC_SIGN_PASSWORD`.)

### Draft text for the open-source application

> **Project:** Linc — https://github.com/deloweProjects/Linc (MIT licence)
>
> Linc connects an Android phone to a Windows PC: notifications, messages and calls on the PC,
> clipboard sync, file sharing (over its own link or Quick Share), and the PC's screen and
> controls on the phone. It is developed in the open by one maintainer; releases are published as
> GitHub Releases with SHA-256 checksums. The Windows app is a WinUI 3 / .NET 8 application
> distributed as an Inno Setup installer and a portable zip; the certificate will sign
> `Linc.Desktop.exe` and `Linc-Setup-<version>.exe` for those releases only. Builds are made from
> the tagged source in the public repository.

## What is already wired

- `Linc\tools\release.ps1`: signs when `LINC_SIGN_THUMBPRINT` or `LINC_SIGN_PFX` is set (needs the
  Windows SDK's `signtool.exe`), passes `/DSign` and `/Slinc=…` to Inno Setup so the installer and
  uninstaller are signed too, and timestamps everything (`timestamp.digicert.com`).
- `Linc\installer\Linc.iss`: `SignTool=linc` / `SignedUninstaller=yes` only under `/DSign`.

## SmartScreen until then

The README tells people: on "Windows protected your PC", click **More info**, check the publisher and
file name, then **Run anyway**. The SHA-256 in `CHECKSUMS.txt` lets them check the file first.
