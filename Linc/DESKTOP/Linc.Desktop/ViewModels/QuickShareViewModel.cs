using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.QuickShare;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public sealed partial class QsNearbyVm(QsNearbyDevice device) : ObservableObject
{
    public QsNearbyDevice Device { get; } = device;
    public string Name => Device.Name;

    public string Glyph => Device.Type switch
    {
        QsWire.DeviceType.Phone => "",
        QsWire.DeviceType.Tablet => "",
        QsWire.DeviceType.Laptop => "",
        _ => "",
    };

    public string Detail => Device.Type switch
    {
        QsWire.DeviceType.Phone => "Phone",
        QsWire.DeviceType.Tablet => "Tablet",
        QsWire.DeviceType.Laptop => "Computer",
        _ => "Nearby device",
    };
}

public sealed partial class QsTransferVm(QsTransfer transfer) : ObservableObject
{
    public QsTransfer Transfer { get; } = transfer;
    public string Title => Transfer.Title;

    public string Headline => Transfer.Direction == QsDirection.Incoming
        ? $"From {Transfer.PeerName}"
        : $"To {Transfer.PeerName}";

    public string Glyph => Transfer.Direction == QsDirection.Incoming ? "" : "";

    public string StatusText => Transfer.State switch
    {
        QsState.WaitingForAnswer => Transfer.Message ?? (Transfer.Pin.Length > 0 ? $"Waiting — PIN {Transfer.Pin}" : "Waiting…"),
        QsState.Transferring => $"{Size(Transfer.Bytes)} of {Size(Transfer.TotalBytes)}",
        QsState.Done => Transfer.Message ?? "Done",
        QsState.Declined => Transfer.Message ?? "Declined",
        _ => Transfer.Message ?? "Failed",
    };

    public double Progress => Transfer.Fraction * 100;
    public bool ShowProgress => Transfer.State == QsState.Transferring;
    public bool IsIndeterminate => Transfer.State == QsState.WaitingForAnswer;
    public bool ShowWaiting => Transfer.State == QsState.WaitingForAnswer;

    public bool CanOpen => Transfer.State == QsState.Done && Transfer.Files is { Count: > 0 };

    /// <summary>The first received link, if any — Quick Share is how Android shares URLs too.</summary>
    public string? Link => Transfer.Texts?.FirstOrDefault(t =>
        t.Kind == Sharing.Nearby.TextMetadata.Types.Type.Url || Uri.IsWellFormedUriString(t.Text.Trim(), UriKind.Absolute))?.Text.Trim();

    public string? ReceivedText => Transfer.Texts is { Count: > 0 } texts ? string.Join("\n", texts.Select(t => t.Text)) : null;
    public bool HasLink => Link is not null;
    public bool HasText => ReceivedText is not null;

    [RelayCommand]
    private void Open()
    {
        if (Transfer.Files is not { Count: > 0 } files)
        {
            return;
        }
        // One file: open it. Several: show them in their folder.
        var target = files.Count == 1 ? files[0] : Path.GetDirectoryName(files[0])!;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void CopyText()
    {
        if (ReceivedText is not { } text)
        {
            return;
        }
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>The Share page: Quick Share visibility, nearby devices, and transfers.</summary>
public sealed partial class QuickShareViewModel : ObservableObject
{
    private readonly IQuickShareService _quickShare;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<QsNearbyVm> Nearby { get; } = [];
    public ObservableCollection<QsTransferVm> Transfers { get; } = [];

    public QuickShareViewModel(IQuickShareService quickShare)
    {
        _quickShare = quickShare;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _receiveEnabled = quickShare.ReceiveEnabled;
        _deviceName = quickShare.DeviceName;
        _autoAcceptOwnPhone = quickShare.AutoAcceptOwnPhone;
        _explorerMenuEnabled = QsShellIntegration.IsRegistered();
        quickShare.QueuedFilesChanged += () => _dispatcher.TryEnqueue(NotifyQueue);
        quickShare.NearbyChanged += () => _dispatcher.TryEnqueue(RebuildNearby);
        quickShare.TransferChanged += transfer => _dispatcher.TryEnqueue(() => OnTransferChanged(transfer));
        RebuildNearby();
        foreach (var transfer in quickShare.Transfers)
        {
            Transfers.Add(new QsTransferVm(transfer));
        }
    }

    [ObservableProperty]
    private bool _receiveEnabled;

    partial void OnReceiveEnabledChanged(bool value)
    {
        _quickShare.SetReceiveEnabled(value);
        OnPropertyChanged(nameof(VisibilityText));
    }

    [ObservableProperty]
    private string _deviceName;

    [ObservableProperty]
    private bool _autoAcceptOwnPhone;

    partial void OnAutoAcceptOwnPhoneChanged(bool value) => _quickShare.SetAutoAcceptOwnPhone(value);

    [ObservableProperty]
    private bool _explorerMenuEnabled;

    [ObservableProperty]
    private string? _shellError;

    partial void OnExplorerMenuEnabledChanged(bool value)
    {
        try
        {
            if (value)
            {
                QsShellIntegration.Register(Environment.ProcessPath ?? "");
            }
            else
            {
                QsShellIntegration.Unregister();
            }
            ShellError = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ShellError = $"Couldn't change the Explorer menu: {ex.Message}";
        }
    }

    [ObservableProperty]
    private string _textToSend = "";

    partial void OnTextToSendChanged(string value) => OnPropertyChanged(nameof(CanSendText));

    public bool CanSendText => SelectedDevice is not null && TextToSend.Trim().Length > 0;

    public IReadOnlyList<string> QueuedFiles => _quickShare.QueuedFiles;
    public bool HasQueuedFiles => QueuedFiles.Count > 0;

    public string QueuedText => QueuedFiles.Count switch
    {
        0 => "",
        1 => $"Ready to send: {Path.GetFileName(QueuedFiles[0])}. Pick a device, then Send.",
        var n => $"Ready to send: {n} files. Pick a device, then Send.",
    };

    public bool CanSendQueued => SelectedDevice is not null && HasQueuedFiles;

    [RelayCommand]
    private async Task SendTextAsync()
    {
        if (SelectedDevice is not { } target || TextToSend.Trim().Length == 0)
        {
            return;
        }
        var text = TextToSend.Trim();
        TextToSend = "";
        await _quickShare.SendTextAsync(target.Device, text, CancellationToken.None);
    }

    [RelayCommand]
    private void PasteClipboard()
    {
        _ = PasteAsync();

        async Task PasteAsync()
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                TextToSend = await content.GetTextAsync();
            }
        }
    }

    [RelayCommand]
    private async Task SendQueuedAsync()
    {
        var files = _quickShare.QueuedFiles;
        if (SelectedDevice is null || files.Count == 0)
        {
            return;
        }
        _quickShare.ClearQueuedFiles();
        await SendAsync(files);
    }

    [RelayCommand]
    private void ClearQueued() => _quickShare.ClearQueuedFiles();

    private void NotifyQueue()
    {
        OnPropertyChanged(nameof(QueuedFiles));
        OnPropertyChanged(nameof(HasQueuedFiles));
        OnPropertyChanged(nameof(QueuedText));
        OnPropertyChanged(nameof(CanSendQueued));
    }

    [ObservableProperty]
    private QsNearbyVm? _selectedDevice;

    partial void OnSelectedDeviceChanged(QsNearbyVm? value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSendText));
        OnPropertyChanged(nameof(CanSendQueued));
    }

    public string VisibilityText => ReceiveEnabled
        ? $"Nearby Android phones on this network see \"{_quickShare.DeviceName}\" in their Quick Share sheet. Every transfer asks you first."
        : "Hidden. Nearby phones can't send to this PC.";

    public bool IsScanning => _quickShare.IsScanning;
    public bool CanSend => SelectedDevice is not null;
    public bool HasNearby => Nearby.Count > 0;
    public bool HasTransfers => Transfers.Count > 0;

    public string NearbyHint => IsScanning
        ? "Looking… On the phone, open Quick Share and set \"Who can share with you\" to Everyone (it shows up here within a few seconds)."
        : HasNearby
            ? "Pick a device, then choose files to send."
            : "No devices yet. Scan, and on the phone set Quick Share to Everyone.";

    public string ReceivedFolder => _quickShare.ReceivedFolder;

    [RelayCommand]
    private void SaveName()
    {
        if (DeviceName.Trim().Length == 0)
        {
            DeviceName = _quickShare.DeviceName;
            return;
        }
        _quickShare.SetDeviceName(DeviceName);
        DeviceName = _quickShare.DeviceName;
        OnPropertyChanged(nameof(VisibilityText));
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        await _quickShare.ScanAsync(TimeSpan.FromSeconds(30), _scanCts.Token);
    }

    [RelayCommand]
    private void OpenReceivedFolder()
    {
        Directory.CreateDirectory(_quickShare.ReceivedFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_quickShare.ReceivedFolder}\"") { UseShellExecute = true });
    }

    public Task SendAsync(IReadOnlyList<string> paths) =>
        SelectedDevice is { } target && paths.Count > 0
            ? _quickShare.SendAsync(target.Device, paths, CancellationToken.None)
            : Task.CompletedTask;

    public void StopScan() => _scanCts?.Cancel();

    private void RebuildNearby()
    {
        var selected = SelectedDevice?.Device.Id;
        Nearby.Clear();
        foreach (var device in _quickShare.Nearby)
        {
            Nearby.Add(new QsNearbyVm(device));
        }
        SelectedDevice = Nearby.FirstOrDefault(n => n.Device.Id == selected) ?? (Nearby.Count == 1 ? Nearby[0] : null);
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(HasNearby));
        OnPropertyChanged(nameof(NearbyHint));
    }

    private void OnTransferChanged(QsTransfer transfer)
    {
        var vm = new QsTransferVm(transfer);
        var index = Transfers.ToList().FindIndex(t => t.Transfer.Id == transfer.Id);
        if (index >= 0)
        {
            Transfers[index] = vm;
        }
        else
        {
            Transfers.Insert(0, vm);
        }
        OnPropertyChanged(nameof(HasTransfers));
    }
}
