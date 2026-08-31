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
