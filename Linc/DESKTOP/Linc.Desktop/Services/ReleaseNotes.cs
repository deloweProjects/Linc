namespace Linc.Desktop.Services;

/// <summary>
/// Settings › About › What's new. The build carries <c>Releases/RELEASE-NOTES.md</c> and
/// <c>Releases/TEST-NOTES.md</c> as they were when it was built (embedded in Linc.Desktop.csproj),
/// so About can say what this version brought with no request. A release shows its own section
/// ("## 1.0.0.18 (…)"); a test build shows the test notes, newest first, as the Telegram post does.
/// Dependency-free.
/// </summary>
public static class ReleaseNotes
{
    public const string ReleaseResource = "Linc.ReleaseNotes.md";
    public const string TestResource = "Linc.TestNotes.md";

    /// <summary>What's new: a title, an optional lead line, and the items, newest first.</summary>
    public sealed record Notes(string Title, string? Lead, IReadOnlyList<string> Items);

    /// <summary>The notes this build should show, or null when it carries none.</summary>
    public static Notes? ForThisBuild(System.Version? version, bool isTest)
    {
        if (isTest && ForTest(Embedded(TestResource)) is { } test)
        {
            return test;
        }
        return ForRelease(Embedded(ReleaseResource), version);
    }

    /// <summary>
    /// The section for <paramref name="version"/> in RELEASE-NOTES.md. A build whose version has no
    /// section (a local build is 1.0.0.0) shows the newest one, under that one's own number.
    /// </summary>
    public static Notes? ForRelease(string? markdown, System.Version? version)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }
        var lines = markdown.Replace("\r", "").Split('\n');
        int start = -1;
        int newest = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }
            var heading = lines[i][3..];
            var number = heading.Split(' ', 2)[0];
            if (!System.Version.TryParse(number, out _))
            {
                continue; // "## Where the binaries are"
            }
            if (newest < 0)
            {
                newest = i;
            }
            if (version is not null && number == version.ToString())
            {
                start = i;
                break;
            }
        }
        if (start < 0)
        {
            start = newest;
        }
        if (start < 0)
        {
            return null;
        }

        var title = $"What's new in {lines[start][3..].Split(' ', 2)[0]}";
        string? lead = null;
        var items = new List<string>();
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            var line = lines[i];
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(Plain(line[2..]));
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && items.Count > 0 && line.Trim().Length > 0)
            {
                items[^1] += " " + Plain(line.Trim()); // a wrapped bullet
            }
            else if (items.Count == 0 && line.Trim().Length > 0 && !line.StartsWith('#'))
            {
                lead = lead is null ? Plain(line.Trim()) : lead + " " + Plain(line.Trim());
            }
        }
        return items.Count == 0 && lead is null ? null : new Notes(title, lead, items);
    }

    /// <summary>TEST-NOTES.md: plain "• " lines under one heading, newest first.</summary>
    public static Notes? ForTest(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }
        var items = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => Plain(l.TrimStart('•', '-', ' ')))
            .ToList();
        return items.Count == 0 ? null : new Notes("What's new in this test build", null, items);
    }

    /// <summary>Markdown's emphasis and code marks off, for a TextBlock.</summary>
    public static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Replace("**", "").Replace("`", ""), @"(?<![\w*])\*(?=\S)([^*]+?)\*(?!\w)", "$1").Trim();

    private static string? Embedded(string name)
    {
        try
        {
            using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
