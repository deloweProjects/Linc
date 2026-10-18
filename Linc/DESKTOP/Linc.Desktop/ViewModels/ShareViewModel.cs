using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.QuickShare;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public sealed partial class PhoneTransferVm(PhoneTransfer transfer, Action<string> retry)
{
    [RelayCommand]
    private void Retry() => retry(Transfer.LocalPath);

    public PhoneTransfer Transfer { get; } = transfer;
    public string Name => Transfer.Name;
    public double Progress => Transfer.Progress;
    public bool IsSending => Transfer.State == PhoneTransferState.Sending;
    public bool IsFailed => Transfer.State == PhoneTransferState.Failed;
    public string Glyph => Transfer.State switch
    {
        PhoneTransferState.Done => "",
        PhoneTransferState.Failed => "",
        _ => "",
    };
    public string StatusText => Transfer.Message ?? (IsSending ? $"Sending… {Transfer.Progress:0} %" : "");
}

public sealed partial class ReceivedVm(ReceivedShare share)
{
    public string Name => share.Name;
    public string Caption => $"{share.ReceivedAt.LocalDateTime:g}";

    [RelayCommand]
    private void Open() => Process.Start(new ProcessStartInfo(share.LocalPath) { UseShellExecute = true });

    [RelayCommand]
    private void ShowInFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{share.LocalPath}\"") { UseShellExecute = true });
}

/// <summary>
/// The Linc half of the Share page (1.0.0.11, D-083). One model on both apps: "Send to my phone"
/// uses the Linc link when it is up — fast, no prompts — and Quick Share when it isn't. The page
/// says which it is using. The Quick Share half (nearby devices, visibility) stays in
/// <see cref="QuickShareViewModel"/>, on the same page.
/// </summary>
public sealed partial class ShareViewModel : ObservableObject
{
    private readonly IShareService _share;
    private readonly IQuickShareService _quickShare;
    private readonly IClipboardSyncService _clipboard;
    private readonly IConnectionManager _connection;
    private readonly IConnectionSupervisor _supervisor;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<PhoneTransferVm> Transfers { get; } = [];
    public ObservableCollection<ReceivedVm> Received { get; } = [];

    public ShareViewModel(IShareService share, IQuickShareService quickShare, IClipboardSyncService clipboard,
        IConnectionManager connection, IConnectionSupervisor supervisor)
    {
        _share = share;
        _quickShare = quickShare;
        _clipboard = clipboard;
        _connection = connection;
        _supervisor = supervisor;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        TextToPhone = "";
        Message = "";
        foreach (var t in share.Transfers) Transfers.Add(new PhoneTransferVm(t, RetryPath));
        foreach (var r in share.Received) Received.Add(new ReceivedVm(r));
        share.TransferChanged += t => _dispatcher.TryEnqueue(() => OnTransfer(t));
        share.ShareReceived += r => _dispatcher.TryEnqueue(() =>
        {
            Received.Insert(0, new ReceivedVm(r));
            OnPropertyChanged(nameof(HasReceived));
            OnPropertyChanged(nameof(NoReceived));
        });
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(OverLinc));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(CanSendText));
        });
    }

    public bool OverLinc => _supervisor.State == LinkState.Connected;

    public string ModeText => OverLinc
        ? $"Sending to {_connection.Current?.Model ?? "your phone"} over Linc"
        : "Linc isn't connected — sending with Quick Share";

    public string ModeHint => OverLinc
        ? "Straight to the phone's Download/Linc folder, no prompts. Use Quick Share below for other people's devices."
        : "Pick your phone (or any device) under Quick Share below; it asks the phone to accept. When Linc reconnects, sending goes back to the Linc link.";

    public bool HasTransfers => Transfers.Count > 0;
    public bool HasReceived => Received.Count > 0;
    public bool NoReceived => Received.Count == 0;

    [ObservableProperty]
    public partial string TextToPhone { get; set; }

    partial void OnTextToPhoneChanged(string value) => OnPropertyChanged(nameof(CanSendText));

    public bool CanSendText => OverLinc && TextToPhone.Trim().Length > 0;

    [ObservableProperty]
    public partial string Message { get; set; }

    [ObservableProperty]
    public partial bool HasMessage { get; set; }

    /// <summary>
    /// Files from the picker, a drop, the tray or Explorer: over Linc when connected; otherwise
    /// queued for Quick Share and the caller shows the page.
    /// </summary>
    /// <returns>True when they went over Linc.</returns>
    public bool SendFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            Say("Only files can be sent — folders aren't supported yet.");
            return OverLinc;
        }
        if (OverLinc)
        {
            _ = _share.SendFilesToPhoneAsync(files);
            Say(files.Count == 1 ? $"Sending {Path.GetFileName(files[0])} to your phone…" : $"Sending {files.Count} files to your phone…");
            return true;
        }
        _quickShare.QueueFiles(files);
        Say("Linc isn't connected, so these are ready to send with Quick Share — pick a device below.");
        return false;
    }

    /// <summary>A link opens on the phone; other text lands on its clipboard.</summary>
    [RelayCommand]
    private async Task SendTextAsync()
    {
        var text = TextToPhone.Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            try
            {
                await _connection.ContinueUrlAsync(uri.AbsoluteUri, CancellationToken.None);
                TextToPhone = "";
                Say("The link is opening on your phone.");
            }
            catch (LincException)
            {
                Say("Your phone isn't connected.");
            }
            return;
        }
        if (await _clipboard.SendToPhoneAsync(text))
        {
            TextToPhone = "";
            Say("Sent — it's on your phone's clipboard.");
        }
        else
        {
            Say("Your phone isn't connected.");
        }
    }

    private void RetryPath(string path) => _ = _share.SendFilesToPhoneAsync([path]);

    [RelayCommand]
    private void OpenReceivedFolder()
    {
        Directory.CreateDirectory(_share.ReceivedFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_share.ReceivedFolder}\"") { UseShellExecute = true });
    }

    private void OnTransfer(PhoneTransfer t)
    {
        var index = Transfers.ToList().FindIndex(x => x.Transfer.Id == t.Id);
        if (index >= 0) Transfers[index] = new PhoneTransferVm(t, RetryPath);
        else Transfers.Insert(0, new PhoneTransferVm(t, RetryPath));
        OnPropertyChanged(nameof(HasTransfers));
    }

    private void Say(string message)
    {
        Message = message;
        HasMessage = message.Length > 0;
    }
}
