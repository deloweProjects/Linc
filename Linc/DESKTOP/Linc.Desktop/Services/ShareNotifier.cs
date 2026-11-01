using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Linc.Desktop.Services;

/// <summary>
/// One Windows notification per file shared with the phone, either way: the file's name, a live
/// progress bar, then "Sent"/"Received" or the reason it failed. The owner asked to see what is
/// being shared and why one didn't make it, rather than a single toast once it was over.
/// Updates are throttled (a few a second) because each one is a call into the notification
/// platform. Never throws: with notifications off, the Share page and the log still say it all.
/// </summary>
public sealed class ShareNotifier
{
    private const string Group = "linc-share";
    private readonly string _tag = Guid.NewGuid().ToString("N")[..16];
    private readonly string _name;
    private uint _sequence;
    private int _lastPercent = -1;
    private DateTime _lastUpdate = DateTime.MinValue;

    /// <summary>The notification's Cancel button carries this argument: the transfer's id (App routes it to ShareService.Cancel).</summary>
    public const string CancelArgument = "linc-share-cancel";

    private readonly Guid? _transfer;

    private ShareNotifier(string name, Guid? transfer)
    {
        _name = name;
        _transfer = transfer;
    }

    /// <summary>Shows the notification for a file starting to move. [toPhone] picks the wording; with [transfer] it has a Cancel button.</summary>
    public static ShareNotifier Start(string name, bool toPhone, Guid? transfer = null)
    {
        var notifier = new ShareNotifier(name, transfer);
        notifier.Show(toPhone ? "Sending to your phone" : "Receiving from your phone", toPhone ? "Sending…" : "Receiving…");
        return notifier;
    }

    private void Show(string title, string status)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddProgressBar(new AppNotificationProgressBar()
                    .BindTitle()
                    .BindValue()
                    .BindValueStringOverride()
                    .BindStatus())
                .SetTag(_tag)
                .SetGroup(Group);
            if (_transfer is { } id)
            {
                builder.AddButton(new AppNotificationButton("Cancel").AddArgument(CancelArgument, id.ToString("N")));
            }
            var notification = builder.BuildNotification();
            notification.Progress = Data(0, "0%", status);
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception)
        {
            // Notifications unavailable; the Share page and the log carry the same news.
        }
    }

    private AppNotificationProgressData Data(double value, string valueText, string status) =>
        new(++_sequence)
        {
            Title = _name,
            Value = value,
            ValueStringOverride = valueText,
            Status = status,
        };

    /// <summary>[percent] 0..100. Throttled to whole percents and at most four updates a second.</summary>
    public void Progress(double percent)
    {
        var whole = (int)Math.Clamp(percent, 0, 100);
        var now = DateTime.UtcNow;
        if (whole == _lastPercent || (whole < 100 && now - _lastUpdate < TimeSpan.FromMilliseconds(250)))
        {
            return;
        }
        _lastPercent = whole;
        _lastUpdate = now;
        try
        {
            _ = AppNotificationManager.Default.UpdateAsync(Data(whole / 100.0, $"{whole}%", "In progress"), _tag, Group);
        }
        catch (Exception)
        {
            // Notifications unavailable; the Share page shows the same progress.
        }
    }

    /// <summary>Replaces the progress notification with a short final one.</summary>
    public void Finish(string title, string? detail = null)
    {
        try
        {
            _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(_tag, Group);
            var builder = new AppNotificationBuilder().AddText(title).AddText(_name);
            if (detail is { Length: > 0 })
            {
                builder.AddText(detail);
            }
            AppNotificationManager.Default.Show(builder.SetGroup(Group).BuildNotification());
        }
        catch (Exception)
        {
            // Notifications unavailable; the Share page says how it ended.
        }
    }
}
