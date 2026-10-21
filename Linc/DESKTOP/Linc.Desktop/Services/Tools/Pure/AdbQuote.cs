using System.Text;

namespace Linc.Desktop.Services.Tools;

/// <summary>
/// The one place Tools turns user text into shell words (Vibe/claude-notes/linc-tools/PLAN.md
/// §1.1). Never build a command from user text without going through here. Dependency-free, so
/// tools/toolsim proves it.
/// </summary>
public static class AdbQuote
{
    /// <summary>One shell word: single quotes, with each <c>'</c> written as <c>'\''</c>.</summary>
    public static string Arg(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// The characters <c>input text</c> can send: printable ASCII. Anything else (é, emoji, other
    /// scripts) needs the Linc app's clipboard, so Type sends what it can and says so.
    /// </summary>
    public static bool IsTypeable(char c) => c >= ' ' && c <= '~';

    /// <summary>
    /// <c>input text</c> arguments for <paramref name="text"/>, in chunks the phone's shell takes
    /// comfortably. <c>input text</c> reads <c>%s</c> as a space, so spaces become <c>%s</c>, and a
    /// typed <c>%</c> followed by <c>s</c> is split across two chunks so it stays a <c>%</c>. Each
    /// chunk is one quoted shell word. Characters that aren't <see cref="IsTypeable"/> are
    /// dropped; check first with it.
    /// </summary>
    public static IReadOnlyList<string> InputTextChunks(string text, int maxChunk = 100)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0) chunks.Add(Arg(current.ToString()));
            current.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!IsTypeable(c)) continue;
            if (c == ' ')
            {
                if (current.Length + 2 > maxChunk) Flush();
                current.Append("%s");
                continue;
            }
            if (current.Length + 1 > maxChunk) Flush();
            current.Append(c);
            // "%" followed by "s" would read as a space: end the chunk between them.
            if (c == '%' && i + 1 < text.Length && text[i + 1] == 's') Flush();
        }
        Flush();
        return chunks;
    }
}
