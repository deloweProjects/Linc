using System.Collections.Immutable;

namespace Linc.Desktop.Services;

/// <summary>
/// Per-device Home page layout (M6a). Stores which of the six sections are hidden.
/// Dependency-free (no WinUI) so the <c>tools\homelayoutsim</c> harness can exercise it.
/// </summary>
public sealed record HomeLayout(
    /// <summary>IDs of sections the user has hidden. Null or empty = all visible.</summary>
    IReadOnlyList<string>? HiddenSections = null,

    /// <summary>True when the widgets pane sits on the right and the tabbed panel on the left (M6b).</summary>
    bool PanesSwapped = false,

    /// <summary>
    /// Height in pixels of the Apps pane at the bottom of the right-hand column (M6c-3).
    /// Null = use the default split (roughly 65/35 in the tabs panel's favour). Appended to the
    /// parameter list on purpose: the existing order is what every saved settings.json names.
    /// </summary>
    double? AppsPaneHeight = null)
{
    /// <summary>
    /// The section IDs in display order (top to bottom in the widgets pane).
    /// <para><c>apps</c> (M6c) sits third, right after the two "act on the phone" sections and
    /// above the content strips: it is a directory of things the user reaches for by name, so it
    /// belongs with Quick actions rather than buried under Media/Photos — and it will become the
    /// launcher for per-app windows in M7. Storage is hidden-by-absence, so an existing saved
    /// layout gains it <b>visible</b> by default, which is intended for a new headline section.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> AllSectionIds =
        ImmutableList.Create("phone", "quickactions", "apps", "media", "photos", "shared", "clipboard");

    /// <summary>Default layout: nothing hidden, panes not swapped.</summary>
    public static HomeLayout Default { get; } = new();

    /// <summary>True when <paramref name="id"/> is visible (i.e. not in <see cref="HiddenSections"/>).</summary>
    public bool IsVisible(string id)
    {
        return HiddenSections is null || !HiddenSections.Contains(id, StringComparer.Ordinal);
    }

    /// <summary>Returns a new layout with <paramref name="id"/> shown or hidden.</summary>
    /// <remarks>
    /// Preserves any unknown IDs already in <see cref="HiddenSections"/> (forward-compat, D-0XX).
    /// Adding an already-hidden ID or removing an already-visible ID returns an equivalent record
    /// without duplicating entries.
    /// </remarks>
    public HomeLayout WithSection(string id, bool visible)
    {
        var currentHidden = HiddenSections is null ? new List<string>() : new List<string>(HiddenSections);

        if (visible)
        {
            // Show: remove from hidden list if present
            if (!currentHidden.Remove(id))
            {
                return this; // already visible, no change
            }
        }
        else
        {
            // Hide: add to hidden list if not already present
            if (currentHidden.Contains(id, StringComparer.Ordinal))
            {
                return this; // already hidden, no change
            }
            currentHidden.Add(id);
        }

        // Preserve unknown IDs by keeping the list as-is (we only mutate the one ID)
        return this with { HiddenSections = currentHidden.Count == 0 ? null : currentHidden };
    }

    /// <summary>Returns a new layout with the panes swapped or not (M6b). Same shape as
    /// <see cref="WithSection"/>: returns <c>this</c> when nothing changes, so callers can use a
    /// reference/equality no-op guard before saving.</summary>
    public HomeLayout WithPanesSwapped(bool swapped)
    {
        return swapped == PanesSwapped ? this : this with { PanesSwapped = swapped };
    }

    /// <summary>Returns a new layout with the Apps pane height set (M6c-3). Same shape as
    /// <see cref="WithPanesSwapped"/>: returns <c>this</c> when nothing changes, so the caller's
    /// "differs from the known value" guard stops the HomeChanged echo from re-saving. The height
    /// is rounded to a whole pixel first — a sub-pixel drag delta must not count as a change.</summary>
    public HomeLayout WithAppsPaneHeight(double? height)
    {
        var rounded = height is null ? (double?)null : Math.Round(height.Value);
        return AppsHeightEqual(rounded, AppsPaneHeight) ? this : this with { AppsPaneHeight = rounded };
    }

    private static bool AppsHeightEqual(double? a, double? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Math.Abs(a.Value - b.Value) < 0.5;
    }

    /// <summary>Plain-language label for the flyout. Unknown IDs return the ID itself.</summary>
    public static string DisplayName(string id) => id switch
    {
        "phone" => "Phone",
        "quickactions" => "Quick actions",
        "apps" => "Apps",
        "media" => "Media",
        "photos" => "Recent photos",
        "shared" => "Shared files",
        "clipboard" => "Clipboard history",
        _ => id
    };

    // Records auto-generate Equals by field, but IReadOnlyList<string> uses reference equality.
    // Override to compare the list contents so round-tripped records compare equal.
    public bool Equals(HomeLayout? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (PanesSwapped != other.PanesSwapped) return false;
        if (!AppsHeightEqual(AppsPaneHeight, other.AppsPaneHeight)) return false;
        return HiddenSectionsEqual(HiddenSections, other.HiddenSections);
    }

    private static bool HiddenSectionsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        // AppsPaneHeight is hashed ROUNDED, matching Equals' whole-pixel tolerance: two records
        // that compare equal must never hash differently.
        var height = AppsPaneHeight is null ? 0d : Math.Round(AppsPaneHeight.Value);
        if (HiddenSections is null) return HashCode.Combine(PanesSwapped, AppsPaneHeight is null, height);
        var hash = new HashCode();
        hash.Add(PanesSwapped);
        hash.Add(AppsPaneHeight is null);
        hash.Add(height);
        foreach (var s in HiddenSections)
        {
            hash.Add(s, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}