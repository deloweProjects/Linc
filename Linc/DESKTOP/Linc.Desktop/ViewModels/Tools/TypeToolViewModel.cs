using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// Tools › Type (TOOLS.md §1): type on the phone from this keyboard, for a phone whose screen is
/// dead or black. A line at a time by default; with Live keys every key goes straight away,
/// gathered for a moment so a burst of typing is one command. Typed text is never logged.
/// </summary>
public partial class TypeToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    // Keys typed within this long of each other go as one command: each `input` call starts a
    // program on the phone (100-300 ms), so one per key would fall behind.
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(40);

    private readonly List<LiveKey> _pending = [];
    private bool _flushing;
    private (int Width, int Height)? _screen;
    private string _screenSerial = "";

    /// <summary>The line to type (Live keys off).</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Every key goes to the phone as it's pressed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineMode), nameof(LiveLabel))]
    public partial bool LiveKeys { get; set; }

    /// <summary>Send presses Enter after the line (hidden in ⋯, on by default).</summary>
    [ObservableProperty]
    public partial bool PressEnterAfter { get; set; } = true;

    public bool LineMode => !LiveKeys;

    public string LiveLabel => LiveKeys ? "Live keys on" : "Live keys";

    /// <summary>Copy and Select all need <c>input keycombination</c> (Android 13+).</summary>
    public bool CanCombos => Ctx.Sdk >= 33;

    public string CopyLabel => CanCombos ? "Copy" : "Copy (needs Android 13)";

    public string SelectAllLabel => CanCombos ? "Select all" : "Select all (needs Android 13)";

    public override Task ShownAsync()
    {
        OnPropertyChanged(nameof(CanCombos));
        OnPropertyChanged(nameof(CopyLabel));
        OnPropertyChanged(nameof(SelectAllLabel));
        return Task.CompletedTask;
    }

    public override void Hidden()
    {
        LiveKeys = false;
        lock (_pending) _pending.Clear();
    }

    // ---- what Rescue reuses ------------------------------------------------------------------

    public Task<bool> WakeAsync() => KeysAsync(TypeCommands.Wake);

    public async Task<bool> KeysAsync(params int[] codes) =>
        await Ctx.DoAsync("input keyevent " + string.Join(' ', codes.Select(c => c.ToString(CultureInfo.InvariantCulture))));

    /// <summary>Swipes up from the bottom of the screen, which opens the lock screen's PIN pad.</summary>
    public async Task<bool> SwipeUpAsync()
    {
        if (_screen is null || _screenSerial != Ctx.Serial)
        {
            var size = await Ctx.RunAsync("wm size");
            _screen = size.Ok ? TypeCommands.ParseScreenSize(size.Text) : null;
            _screenSerial = Ctx.Serial;
        }
        var (w, h) = _screen ?? (1080, 2400); // a typical phone, if the phone didn't say
        return await Ctx.DoAsync(TypeCommands.SwipeUp(w, h));
    }

    /// <summary>Types <paramref name="text"/> into whatever field has focus on the phone, then
    /// Enter when asked. The text never reaches the log or the caption.</summary>
    public async Task<bool> TypeAsync(string text, bool enter)
    {
        var command = TypeCommands.LineCommand(text, enter, out var dropped);
        if (command is null) return false;
        var ok = await Ctx.DoAsync(command, secret: true);
        if (ok && dropped) Ctx.Say("Some characters need the Linc app on the phone, so they were left out.");
        return ok;
    }

    // ---- the surface -------------------------------------------------------------------------

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Text;
        if (text.Length == 0)
        {
            await KeysAsync(TypeCommands.Enter);
            return;
        }
        if (await TypeAsync(text, PressEnterAfter)) Text = "";
    }

    [RelayCommand]
    private Task WakeKeyAsync() => WakeAsync();

    [RelayCommand]
    private Task UnlockSwipeAsync() => SwipeUpAsync();

    /// <summary>One key by its Android code (Home, Back, D-pad, media keys…).</summary>
    [RelayCommand]
    private Task KeyAsync(string code) =>
        int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? KeysAsync(n) : Task.CompletedTask;

    [RelayCommand]
    private Task NotificationsAsync() => Ctx.DoAsync("cmd statusbar expand-notifications");

    [RelayCommand]
    private Task PasteAsync() => CanCombos
        ? Ctx.DoAsync("input keycombination KEYCODE_CTRL_LEFT KEYCODE_V")
        : KeysAsync(TypeCommands.Paste);

    [RelayCommand]
    private Task CopyAsync() => CanCombos ? Ctx.DoAsync("input keycombination KEYCODE_CTRL_LEFT KEYCODE_C") : Task.CompletedTask;

    [RelayCommand]
    private Task SelectAllAsync() => CanCombos ? Ctx.DoAsync("input keycombination KEYCODE_CTRL_LEFT KEYCODE_A") : Task.CompletedTask;

    [RelayCommand]
    private void ToggleLive() => LiveKeys = !LiveKeys;

    // ---- Live keys ---------------------------------------------------------------------------

    /// <summary>A character typed while Live keys is on.</summary>
    public void LiveCharacter(char c) => Queue(LiveKey.Char(c));

    /// <summary>A non-character key (by its VirtualKey name). True when it was sent, so the view
    /// stops it moving focus.</summary>
    public bool LiveSpecialKey(string virtualKey)
    {
        if (TypeCommands.KeyCodeFor(virtualKey) is not { } code) return false;
        Queue(Linc.Desktop.Services.Tools.LiveKey.Key(code));
        return true;
    }

    private void Queue(LiveKey key)
    {
        lock (_pending) _pending.Add(key);
        if (_flushing) return;
        _flushing = true;
        _ = FlushAsync();
    }

    private async Task FlushAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(Gather);
                LiveKey[] batch;
                lock (_pending)
                {
                    batch = [.. _pending];
                    _pending.Clear();
                }
                if (batch.Length == 0) return;
                if (TypeCommands.LiveCommand(batch) is { } command)
                {
                    await Ctx.DoAsync(command, secret: true);
                }
                if (batch.Any(k => k.IsCharacter && !AdbQuote.IsTypeable(k.Character)))
                {
                    Ctx.Say("Some characters need the Linc app on the phone, so they were left out.");
                }
            }
        }
        finally
        {
            _flushing = false;
            // A key that arrived after the last look but before the flag dropped.
            bool more;
            lock (_pending) more = _pending.Count > 0;
            if (more)
            {
                _flushing = true;
                _ = FlushAsync();
            }
        }
    }
}
