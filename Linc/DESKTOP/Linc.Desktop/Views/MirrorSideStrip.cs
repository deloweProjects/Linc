using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Linc.Desktop.Services;
using Linc.Desktop.Services.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Linc.Desktop.Views;

/// <summary>
/// The phone's buttons beside the mirror (Device › Phone buttons beside the mirror, on by default).
/// The owner, 2026-11-03: "my phone has swipe gestures for nav so it's so hard to get around, there
/// should be the nav buttons next to it, I don't want to switch the nav setting on the phone every
/// time I mirror. Also I need a button that swipes, the swiping should be easier".
///
/// A slim strip that sits flush on the scrcpy window's right (or left) edge and follows it, hides
/// while it's minimised, never takes focus (clicks leave the keyboard with the mirror) and stays out
/// of Alt+Tab. It also keeps the mirror window the picture's shape ("light grey/white bars at its
/// sides when it's wider than the phone picture"): after a resize or a rotation the window is
/// shrunk to the picture, and where it can't be (maximised, full screen) Linc lays click-through
/// panels of the chosen colour (Bars beside the picture) over scrcpy's bars, since scrcpy has no
/// colour option. All of that runs while mirroring even with the strip itself turned off.
/// </summary>
public sealed class MirrorSideStrip : Window
{
    private const double ButtonSize = 36;
    private const double Pad = 4;
    private static readonly TimeSpan FitSettle = TimeSpan.FromMilliseconds(350);

    private readonly IDeviceRegistry _registry = App.Services.GetRequiredService<IDeviceRegistry>();
    private readonly ILogService _log = App.Services.GetRequiredService<ILogService>();
    private readonly IAdbShell? _shell;
    private readonly IConnectionSupervisor? _supervisor;
    private readonly IMirrorService? _mirror;
    private readonly Func<int?> _processId;
    private readonly Func<(int Width, int Height)?> _picture;
    private readonly string _title;

    private readonly Grid _grid = new() { Padding = new Thickness(Pad) };
    private readonly List<Button> _buttons = [];
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly WinEventProc _onEvent; // held: the hooks call it from native code
    private readonly BarCovers _covers = new();
    private IntPtr _hookSystem;
    private IntPtr _hookLocation;
    private IntPtr _target;
    private int _targetPid;
    private bool _moving;
    private bool _visible;
    private bool _onTopmost;
    private int _columns;
    private PixelRect _placed;
    private PixelRect? _fitCandidate;
    private DateTime _fitSince;
    private bool _shadeOpen;
    private bool _screenOff;
    private (string Serial, int Width, int Height)? _phoneSize;
    private Button? _typeButton;
    private Button? _screenButton;

    private IntPtr Own => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <param name="processId">The scrcpy process to follow (null: not running).</param>
    /// <param name="picture">The phone picture's size, from scrcpy's own log.</param>
    /// <param name="title">scrcpy's window title (<c>--window-title</c>).</param>
    public MirrorSideStrip(Func<int?> processId, Func<(int Width, int Height)?> picture, string title = "Linc", bool preview = false)
    {
        _processId = processId;
        _picture = picture;
        _title = title;
        if (!preview)
        {
            _shell = App.Services.GetService<IAdbShell>();
            _supervisor = App.Services.GetService<IConnectionSupervisor>();
            _mirror = App.Services.GetService<IMirrorService>();
        }
        Title = "Linc phone buttons";
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E)),
            RequestedTheme = ElementTheme.Dark,
            Child = _grid,
        };
        BuildButtons();

        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Frameless(Own);

        _onEvent = OnWinEvent;
        _hookSystem = SetWinEventHook(EventSystemForeground, EventSystemMinimizeEnd, IntPtr.Zero, _onEvent, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _watch.Tick += (_, _) => Follow();
        _watch.Start();
        _registry.MirrorChanged += OnSettingsChanged;
        if (_mirror is not null) _mirror.PictureSizeChanged += OnPictureChanged;
        Closed += (_, _) =>
        {
            _watch.Stop();
            _registry.MirrorChanged -= OnSettingsChanged;
            if (_mirror is not null) _mirror.PictureSizeChanged -= OnPictureChanged;
            if (_hookSystem != IntPtr.Zero) UnhookWinEvent(_hookSystem);
            if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);
            _covers.Destroy();
        };
    }

    private void OnSettingsChanged() => DispatcherQueue.TryEnqueue(Follow);

    // A rotation: let scrcpy resize its window first, then fit what's left.
    private void OnPictureChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        _fitCandidate = null;
        Follow();
    });

    // ---- the buttons ----

    private void BuildButtons()
    {
        Add("", "Back", () => Key(4));
        Add("", "Home", () => Key(3));
        Add("", "Recent apps", () => Key(187));
        Add("", "Swipe up", () => SwipeAsync(MirrorStripLayout.Swipe.Up));
        Add("", "Swipe down", () => SwipeAsync(MirrorStripLayout.Swipe.Down));
        Add("", "Swipe left", () => SwipeAsync(MirrorStripLayout.Swipe.Left));
        Add("", "Swipe right", () => SwipeAsync(MirrorStripLayout.Swipe.Right));
        Add("", "Volume up", () => Key(24));
        Add("", "Volume down", () => Key(25));
        Add("", "Rotate the phone", () => { ScrcpyShortcut(false, 0x52); return Task.CompletedTask; }); // scrcpy's Alt+R
        Add("", "Notifications (again to close)", ShadeAsync);
        Add("", "Screenshot (Pictures › Linc › Screenshots)", ScreenshotAsync);
        _typeButton = Add("", "Type on the phone", () => { OpenTyping(); return Task.CompletedTask; });
        _screenButton = Add("", "Phone screen off while mirroring (again to turn it on)", () =>
        {
            // scrcpy's Alt+O turns the phone's screen off and keeps mirroring; Alt+Shift+O back on.
            _screenOff = !_screenOff;
            ScrcpyShortcut(!_screenOff, 0x4F);
            return Task.CompletedTask;
        });
        Add("", "Power / lock", () => Key(26));
    }

    private Button Add(string glyph, string tip, Func<Task> act)
    {
        var button = new Button
        {
            Width = ButtonSize,
            Height = ButtonSize,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(Colors.White) },
        };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += async (_, _) =>
        {
            try
            {
                await act();
            }
            catch (Exception ex)
            {
                AsyncVoidGuard.Report(ex, $"Mirror buttons: {tip}");
            }
        };
        _buttons.Add(button);
        return button;
    }

    private void Layout(int columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        var rows = (_buttons.Count + columns - 1) / columns;
        for (var c = 0; c < columns; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ButtonSize) });
        for (var r = 0; r < rows; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ButtonSize) });
        // Column by column, so the groups (navigation, swipes, volume...) stay together.
        for (var i = 0; i < _buttons.Count; i++)
        {
            Grid.SetColumn(_buttons[i], i / rows);
            Grid.SetRow(_buttons[i], i % rows);
            _grid.Children.Add(_buttons[i]);
        }
    }

    private string? Serial => _supervisor?.Device?.Serial;

    private Task Key(int code) => Run($"input keyevent {code}");

    private async Task Run(string command, bool secret = false)
    {
        if (_shell is null || Serial is not { } serial)
        {
            return;
        }
        var result = await _shell.RunAsync(serial, command, TimeSpan.FromSeconds(10), secret);
        if (!result.Ok)
        {
            _log.Log(LogLevel.Warn, $"Mirror buttons: the phone didn't take '{(secret ? "typed text" : command)}' ({result.Text.Trim()}).");
        }
    }

    private async Task SwipeAsync(MirrorStripLayout.Swipe swipe)
    {
        if (_shell is null || Serial is not { } serial)
        {
            return;
        }
        if (_phoneSize?.Serial != serial)
        {
            var size = await _shell.RunAsync(serial, "wm size", TimeSpan.FromSeconds(10));
            var (w, h, over) = MirrorService.ParseWmSize(size.Text);
            if (over is not null && over.Split('x') is [var ow, var oh] && int.TryParse(ow, out var x) && int.TryParse(oh, out var y))
            {
                (w, h) = (x, y);
            }
            if (w <= 0)
            {
                _log.Log(LogLevel.Warn, $"Mirror buttons: couldn't read the phone's screen size for a swipe ('{size.Text.Trim()}').");
                return;
            }
            _phoneSize = (serial, w, h);
        }
        var picture = _picture() ?? (1, 2);
        var (sw, sh) = MirrorStripLayout.Oriented(_phoneSize.Value.Width, _phoneSize.Value.Height, picture.Width, picture.Height);
        await Run(MirrorStripLayout.SwipeCommand(swipe, sw, sh));
    }

    private Task ShadeAsync()
    {
        _shadeOpen = !_shadeOpen;
        return Run(_shadeOpen ? "cmd statusbar expand-notifications" : "cmd statusbar collapse");
    }

    private async Task ScreenshotAsync()
    {
        if (_shell is null || Serial is not { } serial)
        {
            return;
        }
        var png = await _shell.ExecOutAsync(serial, "screencap -p", TimeSpan.FromSeconds(20));
        if (png is null)
        {
            _log.Log(LogLevel.Warn, "Mirror buttons: couldn't take a screenshot.");
            return;
        }
        var folder = ViewModels.Tools.CaptureToolViewModel.ScreenshotFolder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, CaptureCommands.FileName(DateTime.Now, "png"));
        await File.WriteAllBytesAsync(path, png);
        _log.Log(LogLevel.Info, $"Mirror buttons: screenshot saved to {path}");
    }

    /// <summary>A scrcpy keyboard shortcut (its modifier is Alt): the mirror is brought to the front
    /// first, since keys go to the window in front. The click on the strip lets Linc do that.</summary>
    private void ScrcpyShortcut(bool shift, ushort key)
    {
        if (_target == IntPtr.Zero) return;
        SetForegroundWindow(_target);
        if (GetForegroundWindow() != _target)
        {
            // Windows kept another window in front: the keys would land there instead.
            _log.Log(LogLevel.Warn, "Mirror buttons: couldn't bring the mirror to the front for its shortcut; click the mirror and try again.");
            return;
        }
        var keys = new List<INPUT> { KeyInput(0xA4, false) }; // VK_LMENU
        if (shift) keys.Add(KeyInput(0xA0, false)); // VK_LSHIFT
        keys.Add(KeyInput(key, false));
        keys.Add(KeyInput(key, true));
        if (shift) keys.Add(KeyInput(0xA0, true));
        keys.Add(KeyInput(0xA4, true));
        SendInput((uint)keys.Count, [.. keys], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Text typed here goes to the phone with <c>input text</c>. The strip can take the
    /// keyboard only while this is open; the mirror gets it back when it closes.</summary>
    private void OpenTyping()
    {
        if (_typeButton is null) return;
        var box = new TextBox { PlaceholderText = "Type, then Enter", Width = 240 };
        var note = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 240, Text = "Plain letters, numbers and symbols. For other letters, copy and paste them into the mirror." };
        async Task SendAsync()
        {
            var text = box.Text;
            box.Text = "";
            foreach (var chunk in AdbQuote.InputTextChunks(text))
            {
                await Run($"input text {chunk}", secret: true);
            }
        }
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            try { await SendAsync(); } catch (Exception ex) { AsyncVoidGuard.Report(ex, "Mirror buttons: typing"); }
        };
        Button Small(string label, Func<Task> act)
        {
            var b = new Button { Content = label };
            b.Click += async (_, _) =>
            {
                try { await act(); } catch (Exception ex) { AsyncVoidGuard.Report(ex, $"Mirror buttons: {label}"); }
            };
            return b;
        }
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Placement = _registry.Mirror.ControlStrip == MirrorSettings.StripLeft ? FlyoutPlacementMode.Left : FlyoutPlacementMode.Right,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { Small("Send", SendAsync), Small("Enter", () => Key(66)), Small("Delete", () => Key(67)) },
                    },
                    note,
                },
            },
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) =>
        {
            Activatable(false);
            if (_target != IntPtr.Zero) SetForegroundWindow(_target);
        };
        Activatable(true);
        SetForegroundWindow(Own);
        flyout.ShowAt(_typeButton);
    }

    private void Activatable(bool on)
    {
        var ex = GetWindowLongPtr(Own, GwlExStyle);
        SetWindowLongPtr(Own, GwlExStyle, on ? ex & ~WsExNoActivate : ex | WsExNoActivate);
    }

    // ---- following the mirror window ----

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EventSystemForeground)
        {
            Stack();
            return;
        }
        if (hwnd != _target || idObject != 0) return;
        if (evt == EventSystemMoveSizeStart)
        {
            _moving = true;
            _covers.Hide();
        }
        else if (evt == EventSystemMoveSizeEnd)
        {
            _moving = false;
            _fitCandidate = null;
        }
        Follow();
    }

    private void Follow()
    {
        try
        {
            FollowCore();
        }
        catch (Exception ex)
        {
            AsyncVoidGuard.Report(ex, "Mirror buttons: following the mirror window");
        }
    }

    private void FollowCore()
    {
        var pid = _processId();
        if (pid is not { } id)
        {
            HideAll();
            return;
        }
        if (id != _targetPid || !IsWindow(_target))
        {
            _target = FindWindow(id, _title);
            _targetPid = _target == IntPtr.Zero ? 0 : id;
            if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);
            _hookLocation = _target == IntPtr.Zero ? IntPtr.Zero
                : SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _onEvent, (uint)id, 0, WinEventOutOfContext);
        }
        if (_target == IntPtr.Zero || !IsWindowVisible(_target) || IsIconic(_target))
        {
            HideAll();
            return;
        }
        GetWindowRect(_target, out var wr);
        GetClientRect(_target, out var cr);
        var origin = new POINT();
        ClientToScreen(_target, ref origin);
        var window = new PixelRect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
        var client = new PixelRect(origin.X, origin.Y, cr.Right, cr.Bottom);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(_target, 2), ref info); // MONITOR_DEFAULTTONEAREST
        var monitor = new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
        var work = new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        var fullScreen = window == monitor;
        var settings = _registry.Mirror;

        // The strip: one column, or more when the window is too short for it.
        if (settings.ControlStrip == MirrorSettings.StripOff)
        {
            HideStrip();
        }
        else
        {
            var scale = GetDpiForWindow(Own) / 96.0;
            var room = Math.Max(window.Height, (int)(ButtonSize * 4 * scale));
            var columns = Math.Clamp((int)Math.Ceiling(_buttons.Count * ButtonSize * scale / Math.Max(1, room - Pad * 2 * scale)), 1, 3);
            Layout(columns);
            var rows = (_buttons.Count + columns - 1) / columns;
            var stripW = (int)Math.Ceiling((columns * ButtonSize + Pad * 2) * scale);
            var stripH = (int)Math.Ceiling((rows * ButtonSize + Pad * 2) * scale);
            var place = MirrorStripLayout.Dock(window, stripW, stripH, settings.ControlStrip == MirrorSettings.StripLeft, fullScreen ? monitor : work);
            if (place != _placed || !_visible)
            {
                _placed = place;
                AppWindow.MoveAndResize(new RectInt32(place.X, place.Y, place.Width, place.Height));
            }
            if (!_visible)
            {
                _visible = true;
                AppWindow.Show(false);
            }
        }

        // The bars: fit the window to the picture; cover what can't be fitted.
        if (_picture() is { } picture && !_moving)
        {
            var zoomed = IsZoomed(_target) || fullScreen;
            var fit = zoomed ? null : MirrorStripLayout.Fit(client, picture.Width, picture.Height);
            if (fit is { } f)
            {
                if (_fitCandidate != client)
                {
                    _fitCandidate = client;
                    _fitSince = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - _fitSince >= FitSettle)
                {
                    _fitCandidate = null;
                    SetWindowPos(_target, IntPtr.Zero,
                        f.X - (client.X - window.X), f.Y - (client.Y - window.Y),
                        f.Width + (window.Width - client.Width), f.Height + (window.Height - client.Height),
                        SwpNoZOrder | SwpNoActivate);
                    _log.Log(LogLevel.Info, $"Mirror: window fitted to the phone picture ({f.Width}x{f.Height}).");
                }
                _covers.Show(MirrorStripLayout.Bars(client, picture.Width, picture.Height), settings.BarColour == MirrorSettings.BarsWhite);
            }
            else
            {
                _fitCandidate = null;
                _covers.Show(zoomed ? MirrorStripLayout.Bars(client, picture.Width, picture.Height) : [], settings.BarColour == MirrorSettings.BarsWhite);
            }
        }
        Stack();
    }

    /// <summary>Keeps the strip and the bar covers just above the mirror: on top while it's in
    /// front, under whatever covers it otherwise.</summary>
    private void Stack()
    {
        if (_target == IntPtr.Zero) return;
        var topmost = (GetWindowLongPtr(_target, GwlExStyle) & WsExTopmost) != 0; // scrcpy full screen
        var foreground = GetForegroundWindow();
        IntPtr after;
        if (topmost)
        {
            after = HwndTopmost;
        }
        else if (foreground == _target || foreground == Own)
        {
            after = HwndTop;
        }
        else
        {
            after = GetWindow(_target, 3); // GW_HWNDPREV
            while (after != IntPtr.Zero && (after == Own || _covers.Owns(after))) after = GetWindow(after, 3);
            if (after == IntPtr.Zero) after = HwndTop;
        }
        if (_onTopmost && !topmost)
        {
            SetWindowPos(Own, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            _covers.Place(HwndNoTopmost);
        }
        _onTopmost = topmost;
        _covers.Place(after);
        if (_visible) SetWindowPos(Own, after, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void HideStrip()
    {
        if (!_visible) return;
        _visible = false;
        AppWindow.Hide();
    }

    private void HideAll()
    {
        HideStrip();
        _covers.Hide();
    }

    private static IntPtr FindWindow(int pid, string title)
    {
        var found = IntPtr.Zero;
        var text = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            text.Clear();
            GetWindowText(hwnd, text, text.Capacity);
            if (text.ToString() != title) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>No frame, no border, never activated (clicks don't steal focus), not in Alt+Tab;
    /// as <see cref="LincBubbleWindow"/> does it, with small round corners.</summary>
    private static void Frameless(IntPtr hwnd)
    {
        try
        {
            int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int)); // DWMWA_BORDER_COLOR
            int round = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            var ex = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, ex | WsExNoActivate | 0x00000080); // WS_EX_TOOLWINDOW
            var style = GetWindowLongPtr(hwnd, -16); // GWL_STYLE
            style &= ~(nint)(0x00C00000 | 0x00040000 | 0x00800000); // WS_CAPTION | WS_THICKFRAME | WS_BORDER
            SetWindowLongPtr(hwnd, -16, style | unchecked((nint)0x80000000)); // WS_POPUP
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | 0x0020); // FRAMECHANGED
        }
        catch (Exception)
        {
            // Older Windows: a thin border may show; the strip still works.
        }
    }

    /// <summary>
    /// Plain Win32 panels of one colour laid over scrcpy's bars, click-through (a double-click on
    /// the bars still reaches scrcpy) and never activated. The class's brush paints them, so they
    /// need no code of their own.
    /// </summary>
    private sealed class BarCovers
    {
        private readonly IntPtr[] _windows = new IntPtr[2];
        private bool? _white;
        private readonly PixelRect[] _shown = new PixelRect[2];

        public bool Owns(IntPtr hwnd) => hwnd != IntPtr.Zero && (hwnd == _windows[0] || hwnd == _windows[1]);

        public void Show(IReadOnlyList<PixelRect> bars, bool white)
        {
            if (bars.Count == 0)
            {
                Hide();
                return;
            }
            if (_white != white)
            {
                Destroy();
                _white = white;
            }
            for (var i = 0; i < _windows.Length; i++)
            {
                if (i >= bars.Count || bars[i].Width <= 0 || bars[i].Height <= 0)
                {
                    if (_windows[i] != IntPtr.Zero) ShowWindow(_windows[i], 0);
                    _shown[i] = default;
                    continue;
                }
                if (_windows[i] == IntPtr.Zero) _windows[i] = Create(white);
                if (_windows[i] == IntPtr.Zero) continue;
                if (_shown[i] == bars[i]) continue;
                _shown[i] = bars[i];
                SetWindowPos(_windows[i], IntPtr.Zero, bars[i].X, bars[i].Y, bars[i].Width, bars[i].Height, SwpNoZOrder | SwpNoActivate | 0x0040); // SHOWWINDOW
            }
        }

        public void Place(IntPtr after)
        {
            foreach (var hwnd in _windows)
            {
                if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd)) SetWindowPos(hwnd, after, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }

        public void Hide()
        {
            for (var i = 0; i < _windows.Length; i++)
            {
                if (_windows[i] != IntPtr.Zero) ShowWindow(_windows[i], 0); // SW_HIDE
                _shown[i] = default;
            }
        }

        public void Destroy()
        {
            for (var i = 0; i < _windows.Length; i++)
            {
                if (_windows[i] != IntPtr.Zero) DestroyWindow(_windows[i]);
                _windows[i] = IntPtr.Zero;
                _shown[i] = default;
            }
        }

        private static IntPtr Create(bool white)
        {
            var name = white ? "LincMirrorBarWhite" : "LincMirrorBarBlack";
            var instance = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = instance,
                hbrBackground = GetStockObject(white ? 0 : 4), // WHITE_BRUSH : BLACK_BRUSH
                lpszClassName = name,
            };
            RegisterClassEx(ref wc); // 0 when it's already registered: fine
            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED: click-through, never focused, not in Alt+Tab.
            var hwnd = CreateWindowEx(0x08000000 | 0x80 | 0x20 | 0x80000, name, "", unchecked((int)0x80000000), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return hwnd;
            SetLayeredWindowAttributes(hwnd, 0, 255, 2); // LWA_ALPHA, fully opaque
            int square = 1; // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(hwnd, 33, ref square, sizeof(int));
            return hwnd;
        }
    }

    // ---- Win32 ----

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExTopmost = 0x00000008;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTop = IntPtr.Zero, HwndTopmost = new(-1), HwndNoTopmost = new(-2);
    private const uint EventSystemForeground = 0x0003, EventSystemMoveSizeStart = 0x000A, EventSystemMoveSizeEnd = 0x000B,
        EventSystemMinimizeEnd = 0x0017, EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x0002u : 0u } }, // KEYEVENTF_KEYUP
    };

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>Opens the phone buttons beside the mirror while it runs, closes them when it stops.
/// Started once from App.</summary>
public static class MirrorStripHost
{
    private static MirrorSideStrip? _strip;

    public static void Begin()
    {
        var mirror = App.Services.GetRequiredService<IMirrorService>();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Apply()
        {
            if (mirror.State == MirrorState.Running && _strip is null)
            {
                try
                {
                    _strip = new MirrorSideStrip(() => mirror.ProcessId, () => mirror.PictureSize);
                    _strip.Closed += (_, _) => _strip = null;
                }
                catch (Exception ex)
                {
                    App.Services.GetRequiredService<ILogService>().Log(LogLevel.Warn, $"Mirror buttons couldn't open ({ex.Message}).");
                    _strip = null;
                }
            }
            else if (mirror.State != MirrorState.Running && _strip is { } open)
            {
                _strip = null;
                open.Close();
            }
        }
        mirror.StateChanged += () => dispatcher.TryEnqueue(Apply);
        Apply();
    }

    /// <summary>
    /// `--strip-preview` (with `--data-root` somewhere empty): only the strip, beside any window
    /// titled "Linc" from another program, as if it showed a 1080x2400 phone, with no phone
    /// commands sent. So its docking, fitting and bar covers can be checked on screen without a
    /// phone. For development only.
    /// </summary>
    public static void Preview()
    {
        var self = Environment.ProcessId;
        _strip = new MirrorSideStrip(() =>
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.MainWindowTitle == "Linc") return p.Id;
                }
                catch (Exception)
                {
                    // Gone, or not ours to ask.
                }
                finally
                {
                    p.Dispose();
                }
            }
            return null;
        }, () => (1080, 2400), preview: true);
    }
}
