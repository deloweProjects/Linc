# M17-CODE — the pre-solved code for `M17a.md` and `M17b.md`

> **Companion to both task files. They tell you what; this tells you how.** Where this file gives a
> body, use it — do not redesign it. Where it says *shape only*, it is a skeleton and the surrounding
> code is yours to fit. **If any of it does not compile against the real tree, fix it and say so in
> the report** — this was written by the planner without a compiler.

---

## A — the icon (M17a Part A)

**One PowerShell script, run from `<repo-root>`.** Windows PowerShell 5.1 has
`System.Drawing`. It writes a rounded, inset PNG; the `.ico` packing follows M14's existing recipe.

```powershell
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile("Assets/ico.png")
$W = $src.Width; $H = $src.Height
$radius = [int]([Math]::Min($W,$H) * 0.22)   # A1: 22% of the shorter side
$inset  = 0.88                                # A1: artwork at 88%, centred

$out = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($out)
$g.SmoothingMode     = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.PixelOffsetMode   = 'HighQuality'
$g.Clear([System.Drawing.Color]::Transparent)

# rounded-rect clip over the inset box
$iw = [int]($W * $inset); $ih = [int]($H * $inset)
$ox = [int](($W - $iw) / 2); $oy = [int](($H - $ih) / 2)
$r  = [int]($radius * $inset)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($ox,             $oy,              2*$r, 2*$r, 180, 90)
$path.AddArc($ox+$iw-2*$r,    $oy,              2*$r, 2*$r, 270, 90)
$path.AddArc($ox+$iw-2*$r,    $oy+$ih-2*$r,     2*$r, 2*$r,   0, 90)
$path.AddArc($ox,             $oy+$ih-2*$r,     2*$r, 2*$r,  90, 90)
$path.CloseFigure()
$g.SetClip($path)
$g.DrawImage($src, $ox, $oy, $iw, $ih)
$g.Dispose()
$out.Save("$env:TEMP\linc-rounded.png", [System.Drawing.Imaging.ImageFormat]::Png)
"$W x $H, radius=$radius px, inset=$iw x $ih at ($ox,$oy)"
```

**Then:** resize that PNG to 16/24/32/48/64/128/256 (`HighQualityBicubic`, transparent background) and
pack the `.ico` exactly as M14 did. **Check the 16 px frame by eye before packing** — if the L is
mush, raise the inset for the 16/24 frames only and say so.
**Mono variant:** unchanged recipe from M14 (luma→alpha, RGB forced white), applied to the rounded
output. **Android foreground: do not round** — the adaptive mask already does it (M17a A2).

---

## B — the Android design system (M17a Part B)

### B1 `ui/theme/Dimens.kt` — new file, use it, delete magic numbers as you go
```kotlin
package app.linc.android.ui.theme

import androidx.compose.ui.unit.dp

/** M17a B1: one spacing scale for the whole app. No hand-set paddings anywhere else. */
object Dimens {
    val xs = 4.dp
    val s  = 8.dp
    val m  = 12.dp
    val l  = 16.dp
    val xl = 24.dp
    val xxl = 32.dp

    /** Card corner radius (B1). Buttons/chips use [radiusControl]. */
    val radiusCard = 20.dp
    val radiusControl = 12.dp

    /** Every interactive element is at least this tall/wide (B1). */
    val touchTarget = 48.dp

    /** M16: the trackpad's floor. Do not remove; do not turn back into a weight. */
    val trackpadMin = 280.dp
}
```

### B2 One card, one screen scaffold — every screen uses these two
```kotlin
package app.linc.android.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import app.linc.android.ui.theme.Dimens

/**
 * M17a B1: the ONE card treatment. Tonal surface, no elevation, no outline — mixing those three
 * is most of why the app looked unfinished. Every card in the app is this composable.
 */
@Composable
fun LincCard(
    title: String? = null,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Dimens.radiusCard),
        color = MaterialTheme.colorScheme.surfaceContainer,
    ) {
        Column(Modifier.padding(Dimens.l), verticalArrangement = Arrangement.spacedBy(Dimens.m)) {
            if (title != null) {
                Text(title, style = MaterialTheme.typography.titleMedium)
            }
            content()
        }
    }
}

/**
 * M17a B2: every screen gets a top app bar and a scroll host from day one (the M5a/M16 lesson).
 * Pass scrollable = false only for a screen that manages its own scrolling internally.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LincScreen(
    title: String,
    modifier: Modifier = Modifier,
    scrollable: Boolean = true,
    content: @Composable ColumnScope.() -> Unit,
) {
    Scaffold(
        modifier = modifier,
        topBar = { TopAppBar(title = { Text(title, style = MaterialTheme.typography.headlineSmall) }) },
    ) { inner ->
        val base = Modifier
            .fillMaxSize()
            .padding(inner)
            .padding(horizontal = Dimens.l)
        Column(
            modifier = if (scrollable) base.verticalScroll(rememberScrollState()) else base,
            verticalArrangement = Arrangement.spacedBy(Dimens.m),
        ) {
            Spacer(Modifier.height(Dimens.s))
            content()
            Spacer(Modifier.height(Dimens.xl))
        }
    }
}

/** M17a B2: a real empty state — icon + one sentence — never a bare "nothing here". */
@Composable
fun LincEmpty(icon: @Composable () -> Unit, text: String) {
    Column(
        Modifier.fillMaxWidth().padding(vertical = Dimens.xxl),
        horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Dimens.m),
    ) {
        icon()
        Text(
            text,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
```

### B3 Shapes + type wiring — add to the existing `Theme.kt`, do not fork it
```kotlin
private val LincShapes = Shapes(
    small      = RoundedCornerShape(Dimens.radiusControl),
    medium     = RoundedCornerShape(Dimens.radiusCard),
    large      = RoundedCornerShape(Dimens.radiusCard),
)
// then inside MaterialTheme( ... ) add:  shapes = LincShapes,
```
**Then sweep:** every `Card(`/`Surface(`/hand-rolled `Box` with a background becomes `LincCard`;
every `.padding(NN.dp)` becomes a `Dimens` value; every `fontSize =` becomes a type-scale style.
**`ToolsScreen.kt` keeps `Dimens.trackpadMin` and its scroll host** (M16) — port them, don't drop them.

---

## C — auto-update: the decision (M17b Part A)

### C1 `DESKTOP/Linc.Desktop/Services/UpdateDecision.cs` — new file, full body
```csharp
namespace Linc.Desktop.Services;

/// <summary>What the app should do about an available update (M17b A).</summary>
public enum UpdateAction { None, Optional, Forced }

/// <summary>
/// The whole auto-update feature is this one rule. Pure statics, no HTTP and no WinUI, so the
/// harness proves it with no network and no UI (the D-036 pattern).
/// <para>
/// SAFETY RULE: anything malformed or missing degrades to <see cref="UpdateAction.None"/>, never to
/// Forced. A typo in the backend manifest must not be able to brick every installed copy.
/// </para>
/// </summary>
public static class UpdateDecision
{
    public static UpdateAction Decide(
        string? installed, string? latest, string? minimumSupported, string? skippedVersion)
    {
        if (!TryParse(installed, out var have))
        {
            return UpdateAction.None; // we cannot even place ourselves — do nothing
        }

        // Forced first, and it deliberately IGNORES skippedVersion: a user cannot skip past a
        // version the backend has declared unsupported.
        if (TryParse(minimumSupported, out var min) && Compare(have, min) < 0)
        {
            return UpdateAction.Forced;
        }

        if (!TryParse(latest, out var newest) || Compare(have, newest) >= 0)
        {
            return UpdateAction.None;
        }

        // A skip applies to exactly the version that was skipped, so the next release asks again.
        if (TryParse(skippedVersion, out var skipped) && Compare(skipped, newest) == 0)
        {
            return UpdateAction.None;
        }

        return UpdateAction.Optional;
    }

    /// <summary>Semantic-ish parse: up to four numeric parts, any pre-release suffix ignored.</summary>
    public static bool TryParse(string? text, out int[] parts)
    {
        parts = [0, 0, 0, 0];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var chunks = core.Split('.');
        if (chunks.Length == 0 || chunks.Length > 4)
        {
            return false;
        }

        for (var i = 0; i < chunks.Length; i++)
        {
            if (!int.TryParse(chunks[i], out var value) || value < 0)
            {
                return false; // "1.x.0" is malformed, not "1.0.0"
            }
            parts[i] = value;
        }
        return true;
    }

    /// <summary>Numeric comparison, so 1.10.0 &gt; 1.9.0 (a string compare gets this wrong).</summary>
    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] < b[i] ? -1 : 1;
            }
        }
        return 0;
    }
}
```

### C2 The Kotlin mirror — `service/UpdateDecision.kt`
```kotlin
package app.linc.android.service

enum class UpdateAction { None, Optional, Forced }

/** M17b A — the same rule as the desktop's UpdateDecision.cs. Pure; no Context, no network. */
object UpdateDecision {

    fun decide(installed: String?, latest: String?, minimumSupported: String?, skipped: String?): UpdateAction {
        val have = parse(installed) ?: return UpdateAction.None
        val min = parse(minimumSupported)
        // Forced ignores `skipped` on purpose — see the C# twin.
        if (min != null && compare(have, min) < 0) return UpdateAction.Forced
        val newest = parse(latest) ?: return UpdateAction.None
        if (compare(have, newest) >= 0) return UpdateAction.None
        val skip = parse(skipped)
        if (skip != null && compare(skip, newest) == 0) return UpdateAction.None
        return UpdateAction.Optional
    }

    fun parse(text: String?): IntArray? {
        val core = text?.trim()?.trimStart('v', 'V')?.split('-', '+')?.firstOrNull()
            ?.takeIf { it.isNotEmpty() } ?: return null
        val chunks = core.split('.')
        if (chunks.isEmpty() || chunks.size > 4) return null
        val out = IntArray(4)
        chunks.forEachIndexed { i, c ->
            val v = c.toIntOrNull() ?: return null
            if (v < 0) return null
            out[i] = v
        }
        return out
    }

    fun compare(a: IntArray, b: IntArray): Int {
        for (i in 0 until 4) if (a[i] != b[i]) return if (a[i] < b[i]) -1 else 1
        return 0
    }
}
```

### C3 The harness cases — `tools/updatesim` (or wherever you put it), all of these
```
installed  latest   minimum  skipped   expect
1.0.0      1.0.0    1.0.0    null      None       equal all round
1.2.0      1.1.0    1.0.0    null      None       latest older than installed
1.0.0      1.1.0    1.0.0    null      Optional   normal upgrade
1.0.0      1.1.0    1.0.0    1.1.0     None       skip honoured for Optional
1.0.0      1.1.0    1.1.0    1.1.0     Forced     SKIP IGNORED — the load-bearing case
1.9.0      1.10.0   1.0.0    null      Optional   numeric, not string, comparison
1.0.0      null     null     null      None       empty manifest
1.0.0      "x.y.z"  "x.y.z"  null      None       garbage never forces
null       1.1.0    1.1.0    null      None       unknown installed version
1.0.0      1.1.0    ""       null      Optional   missing minimum is not a floor
```
**Negative proof (a)** = make `Decide` consult `skippedVersion` before the Forced branch → row 5
flips to `None` → check fails. **Negative proof (b)** = replace `Compare` with
`string.CompareOrdinal` → row 6 flips → check fails.

---

## D — fetch + gate (M17b Part B), shape only

```csharp
/// <summary>M17b B: the manifest, exactly as the backend must serve it. Static file, plain GET.</summary>
public sealed record UpdateManifest(
    string? Latest, string? MinimumSupported, string? Url, string? Notes, string? Sha256);

// Deserialize with System.Text.Json, PropertyNameCaseInsensitive = true.
// Every field nullable on purpose: a partial manifest degrades to None, it does not throw.
```

**The gate that makes the feature inert (M17b B2/B5) — put this first in the check method:**
```csharp
if (string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
{
    return; // No URL configured: no request, no UI, no log line. Acceptance item 4.
}
if (DateTime.UtcNow - _lastCheckUtc < TimeSpan.FromHours(6) || _inFlight)
{
    return; // B3: at most one check per 6 h, one in flight at a time.
}
```
**Failures are silent** — catch, log at `Debug`, return. A dead backend never reaches the user.

**Desktop apply (C):** download to `Path.GetTempPath()`, compute SHA256, compare to the manifest's,
**refuse and surface a plain-language message on mismatch**, otherwise `Process.Start` the installer
and exit. **Phone apply:** `FileProvider.getUriForFile` → `ACTION_VIEW` with
`application/vnd.android.package-archive` and `FLAG_GRANT_READ_URI_PERMISSION`; if
`packageManager.canRequestPackageInstalls()` is false, say so plainly and offer
`ACTION_MANAGE_UNKNOWN_APP_SOURCES`. **Do not execute either path against the owner's machine or
phone** — exercise decision + fetch against a temp fixture and stop, and say so.
