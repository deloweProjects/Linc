using System.Runtime.InteropServices;
using System.Text;

namespace Linc.Desktop.Services;

/// <summary>What a Winamp-API player reports right now.</summary>
public sealed record WinampState(string Title, bool Playing, long PositionMs, long DurationMs);

/// <summary>
/// Reads and controls players that expose the classic Winamp remote window instead of Windows'
/// System Media Transport Controls. AIMP is the reason this exists — it publishes nothing to
/// SMTC (verified: with AIMP playing, Windows reports no session for it), so without this the
/// phone's PC-media widget stays empty for anyone using it. Winamp itself and several other
/// players expose the same interface, so this is not an AIMP-only shim.
///
/// Deliberately a polled fallback, never the primary: SMTC is the supported path and is used
/// whenever any player registers there.
/// </summary>
public static class WinampRemote
{
    private const string WindowClass = "Winamp v1.x";
    private const uint WmUser = 0x0400;
    private const uint WmCommand = 0x0111;

    // Standard Winamp control ids.
    private const int CmdPrevious = 40044;
    private const int CmdPlay = 40045;
    private const int CmdPause = 40046;
    private const int CmdNext = 40048;

    private delegate bool EnumProc(nint handle, nint param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);

    private static nint _cached;

    /// <summary>
    /// The player's window, or 0. Found by enumeration rather than <c>FindWindow</c> — AIMP's
    /// class is not resolvable by name in either character set, though it enumerates fine.
    /// </summary>
    private static nint FindPlayer()
    {
        if (_cached != 0 && IsWindow(_cached))
        {
            return _cached;
        }
        var found = (nint)0;
        EnumWindows((handle, _) =>
        {
            var className = new StringBuilder(64);
            GetClassName(handle, className, className.Capacity);
            if (className.ToString() == WindowClass)
            {
                found = handle;
                return false; // stop enumerating
            }
            return true;
        }, 0);
        _cached = found;
        return found;
    }

    /// <summary>Current state, or null when no such player is running or nothing is loaded.</summary>
    public static WinampState? TryGetState()
    {
        try
        {
            var player = FindPlayer();
            if (player == 0)
            {
                return null;
            }

            var status = (long)SendMessage(player, WmUser, 0, 104); // 1 playing, 3 paused, 0 stopped
            if (status == 0)
            {
                return null;
            }

            var raw = new StringBuilder(512);
            GetWindowText(player, raw, raw.Capacity);
            var title = CleanTitle(raw.ToString());
            if (title.Length == 0)
            {
                return null;
            }

            var positionMs = (long)SendMessage(player, WmUser, 0, 105);
            var durationSeconds = (long)SendMessage(player, WmUser, 1, 105);
            return new WinampState(
                title,
                Playing: status == 1,
                PositionMs: positionMs < 0 ? 0 : positionMs,
                DurationMs: durationSeconds < 0 ? 0 : durationSeconds * 1000);
        }
        catch (Exception)
        {
            return null; // the player vanished mid-call; treat as "nothing playing"
        }
    }

    /// <summary>Applies a transport action. Returns false when no such player is present.</summary>
    public static bool Control(string action)
    {
        var player = FindPlayer();
        if (player == 0)
        {
            return false;
        }
        var command = action switch
        {
            "play" => CmdPlay,
            "pause" => CmdPause,
            "next" => CmdNext,
            "prev" => CmdPrevious,
            _ => 0,
        };
        if (command == 0)
        {
            return false;
        }
        SendMessage(player, WmCommand, command, 0);
        return true;
    }

    /// <summary>
    /// Turns "1. Artist - Track - Winamp" into "Artist - Track". The playlist index and the
    /// trailing app name are presentation, not metadata, and would look wrong on the phone.
    /// </summary>
    private static string CleanTitle(string windowText)
    {
        var title = windowText.Trim();
        const string suffix = " - Winamp";
        if (title.EndsWith(suffix, StringComparison.Ordinal))
        {
            title = title[..^suffix.Length];
        }
        var dot = title.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot <= 4 && title[..dot].All(char.IsDigit))
        {
            title = title[(dot + 2)..];
        }
        return title.Trim();
    }
}
