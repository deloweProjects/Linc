using System.Text;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// Undoes "mojibake": UTF-8 text that something once decoded as Windows-1252, so an em dash
/// shows as "â€”" (the owner saw "Linc PC log â€" 2026-…" in the clipboard history). Linc's own
/// path is UTF-8 end to end (both Framing classes, Microsoft.Data.Sqlite, the WinRT clipboard),
/// so such text arrived already damaged — copied out of a tool that read a UTF-8 file as ANSI —
/// and was then kept in the history. Display and copy-back go through this.
/// <para>
/// Conservative by construction: the text is changed only when EVERY character maps back to a
/// single Windows-1252 byte AND those bytes form valid UTF-8 that contains a multi-byte
/// character. Real text with accents ("café") fails the UTF-8 test and comes back untouched.
/// </para>
/// </summary>
public static class TextRepair
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Windows-1252's 0x80–0x9F characters (where it differs from Latin-1), back to their byte.</summary>
    private static readonly Dictionary<char, byte> Cp1252High = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87,
        ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E,
        ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    public static string FixMojibake(string text)
    {
        // Fast path: every UTF-8 lead byte of a multi-byte character reads as Â..ô in 1252.
        if (string.IsNullOrEmpty(text) || !HasLeadCharacter(text))
        {
            return text;
        }
        var bytes = new byte[text.Length];
        bool multiByte = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80)
            {
                bytes[i] = (byte)c;
            }
            else if (c <= 0xFF)
            {
                bytes[i] = (byte)c; // Latin-1 range, including the five bytes 1252 leaves undefined
                multiByte = true;
            }
            else if (Cp1252High.TryGetValue(c, out var b))
            {
                bytes[i] = b;
                multiByte = true;
            }
            else
            {
                return text; // a character 1252 can't produce: this text was never misread as 1252
            }
        }
        if (!multiByte)
        {
            return text;
        }
        try
        {
            var repaired = StrictUtf8.GetString(bytes);
            return repaired.Length < text.Length ? repaired : text;
        }
        catch (DecoderFallbackException)
        {
            return text; // not UTF-8 underneath: genuine accented text
        }
    }

    private static bool HasLeadCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c is >= 'Â' and <= 'ô') return true;
        }
        return false;
    }
}
