using Linc.Desktop.Services;

namespace Linc.Desktop.Views;

/// <summary>
/// Pretend phone data for `--bubble-preview`: notifications (one that takes a reply), clips, a file
/// moving and a folder of files, so the bubble's pages can be looked at with no phone. Development only.
/// </summary>
internal static class LincBubblePreview
{
    public static BubbleSources Sources() => new(new Notifications(), new Clipboard(), new Share(), new Files());

    private sealed class Notifications : INotificationSyncService
    {
        private readonly List<NotificationItem> _items;

        public Notifications()
        {
            var now = DateTimeOffset.Now;
            NotificationAction[] chat = [new(0, "Reply", true), new(1, "Mark as read", false), new(2, "Mute", false)];
            _items =
            [
                new("wa:1", "WhatsApp", "Thandi", "Are we still meeting at 3? I'm at the library, second floor by the windows. Bring the charger if you can 🙏", now.AddMinutes(-1), "com.whatsapp", "msg", "Study group", null, chat),
                new("wa:2", "WhatsApp", "Mum", "Call me when you're free", now.AddMinutes(-6), "com.whatsapp", "msg", null, null, chat),
                new("wa:s", "WhatsApp", "WhatsApp", "30 messages from 5 chats", now.AddMinutes(-1), "com.whatsapp"),
                new("gm:1", "Gmail", "NUST Registry", "Your exam timetable for semester 2 is now available on the portal.", now.AddMinutes(-22), "com.google.android.gm", "email", null, null, [new(0, "Archive", false), new(1, "Reply", true)]),
                new("ec:1", "EcoCash", "Payment received", "You have received $12.00 from T. Moyo.", now.AddHours(-2), "co.econet.ecocash"),
            ];
        }

        public event Action? Changed;
        public event Action<NotificationItem>? Posted { add { } remove { } }
        public event Action<string>? Removed { add { } remove { } }

        public IReadOnlyList<NotificationItem> Items => _items;
        public int RelayedCount => _items.Count;
        public int DismissedCount => 0;
        public bool Enabled { get; set; } = true;

        public void Start() { }

        public Task DismissAsync(string key)
        {
            _items.RemoveAll(n => n.Key == key);
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> FireActionAsync(string key, int index) => Task.FromResult(true);

        public async Task<bool> ReplyAsync(string key, int index, string text)
        {
            await Task.Delay(400);
            return true;
        }
    }

    private sealed class Clipboard : IClipboardSyncService
    {
        private readonly List<ClipEntry> _history =
        [
            new(3, "https://github.com/deloweProjects/Linc/releases", true, DateTimeOffset.Now.AddMinutes(-2)),
            new(2, "Meet at the library, second floor, 15:00", false, DateTimeOffset.Now.AddMinutes(-15), Sent: true),
            new(1, "0772 123 456", true, DateTimeOffset.Now.AddHours(-3)),
            new(0, "older clip", false, DateTimeOffset.Now.AddDays(-1)),
        ];

        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ClipEntry> History => _history;
        public event Action? HistoryChanged { add { } remove { } }
        public event Action<string>? ReceivedFromPhone { add { } remove { } }

        public void Start() { }
        public void Recopy(ClipEntry entry) { }
        public Task<bool> SendToPhoneAsync(string text) => Task.FromResult(true);
        public Task<bool> SyncNowAsync() => Task.FromResult(true);
        public void SetPinned(ClipEntry entry, bool pinned) { }
        public void Delete(ClipEntry entry) { }
        public void Clear() { }
        public void Reload() { }
    }

    private sealed class Share : IShareService
    {
        public IReadOnlyList<PhoneTransfer> Transfers { get; } =
        [
            new(Guid.NewGuid(), "holiday-photos.zip", "", PhoneTransferState.Sending, 42, null),
            new(Guid.NewGuid(), "assignment-final.pdf", "", PhoneTransferState.Done, 100, null),
            new(Guid.NewGuid(), "IMG_20261103_0912.jpg", "", PhoneTransferState.Done, 100, null, ToPhone: false),
        ];

        public IReadOnlyList<ReceivedShare> Received => [];
        public string ReceivedFolder => "";
        public event Action<PhoneTransfer>? TransferChanged { add { } remove { } }
        public event Action<ReceivedShare>? ShareReceived { add { } remove { } }

        public void Start() { }
        public Task SendFileToPhoneAsync(string localPath, CancellationToken ct) => Task.CompletedTask;
        public Task SendFilesToPhoneAsync(IReadOnlyList<string> paths) => Task.CompletedTask;
    }

    private sealed class Files : IFileService
    {
        public Task<IReadOnlyList<StorageRoot>> GetRootsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StorageRoot>>([new("Internal storage", "/storage/emulated/0")]);

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct)
        {
            await Task.Delay(500, ct);
            var now = DateTimeOffset.Now;
            return
            [
                new("Linc", path + "/Linc", true, 0, now),
                new("lecture-notes-week9.pdf", path + "/lecture-notes-week9.pdf", false, 2_400_000, now.AddMinutes(-40)),
                new("IMG_20261103_0912.jpg", path + "/IMG_20261103_0912.jpg", false, 3_100_000, now.AddHours(-3)),
                new("budget.xlsx", path + "/budget.xlsx", false, 48_000, now.AddDays(-2)),
                new("song.mp3", path + "/song.mp3", false, 6_800_000, now.AddDays(-5)),
            ];
        }

        public async Task<string> PullAsync(string remotePath, string localDirectory, IProgress<double> progress, CancellationToken ct)
        {
            for (int p = 0; p <= 100; p += 20)
            {
                progress.Report(p);
                await Task.Delay(250, ct);
            }
            return System.IO.Path.Combine(localDirectory, System.IO.Path.GetFileName(remotePath));
        }

        public Task<string> PushAsync(string localPath, string remoteDirectory, IProgress<double> progress, CancellationToken ct) => Task.FromResult(remoteDirectory);
        public Task RenameAsync(string path, string newName, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task CreateFolderAsync(string parentPath, string name, CancellationToken ct) => Task.CompletedTask;
        public Task<string> PullScreenshotAsync(CancellationToken ct) => Task.FromResult("");
    }
}
