// quicksharesim: proves Linc's Quick Share engine against itself over real TCP loopback -
// UKEY2 in both roles, the SecureMessage channel, the PIN, payload framing, and a multi-chunk
// file arriving byte-identical - plus the pure wire helpers. Exit 0 = all checks passed.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Linc.Desktop.QuickShare;

var failures = new List<string>();
void True(bool condition, string what)
{
    Console.WriteLine($"    {(condition ? "ok  " : "FAIL")} {what}");
    if (!condition) failures.Add(what);
}

Console.WriteLine("--- wire helpers");
{
    var name = QsWire.ServiceInstanceName("AB12");
    var raw = QsWire.FromBase64Url(name);
    True(raw.Length == 10 && raw[0] == 0x23 && raw[5] == 0xFC && raw[6] == 0x9F && raw[7] == 0x5E && raw[8] == 0 && raw[9] == 0,
        "the mDNS instance name is 0x23 + id + FC9F5E + 00 00");
    True(!name.Contains('=') && !name.Contains('+') && !name.Contains('/'), "...base64url without padding");
    True(QsWire.ServiceType == "_FC9F5ED42C8A._tcp" &&
         Convert.ToHexString(SHA256.HashData("NearbySharing"u8.ToArray())).StartsWith("FC9F5ED42C8A"),
        "the service type is the first 6 bytes of SHA256(NearbySharing)");

    var info = QsWire.EndpointInfo("Test PC", QsWire.DeviceType.Laptop);
    var (parsedName, parsedType) = QsWire.ParseEndpointInfo(info);
    True(parsedName == "Test PC" && parsedType == QsWire.DeviceType.Laptop, "endpoint info round-trips name and device type");
    True(info[0] == 0x06, "...laptop is device type 3 in bits 1-3, visible (bit 4 clear)");
    var hidden = (byte[])info.Clone();
    hidden[0] |= 0x10;
    True(QsWire.ParseEndpointInfo(hidden).Name is null, "...and a hidden sender's name is not read as plain text");

    True(QsWire.Pin([]) == "0000", "the PIN of nothing is 0000");
    // Hand-computed from Chromium's rule: bytes SIGNED, hash mod 9973, multiplier *31 mod 9973.
    // [1,2] -> 1*1 + 2*31 = 63; [0xFF] -> -1 -> abs 1.
    True(QsWire.Pin([1, 2]) == "0063" && QsWire.Pin([0xFF]) == "0001", "the PIN reads bytes as signed and pads to 4 digits");
    var wake = QsWire.WakeServiceData();
    True(wake.Length == 24 && wake[0] == 0xFC && wake[1] == 0x12 && wake[2] == 0x8E && wake[3] == 0x01 && wake[4] == 0x42,
        "the BLE wake data is the fixed 14-byte prefix + 10 random bytes (fits one legacy advert)");

    True(QsConnection.SafeName("../../Windows/evil.exe") == "evil.exe" && QsConnection.SafeName("..\\x\\a:b.txt") == "a_b.txt",
        "a sender can't choose the folder a file lands in");
    True(QsConnection.SafeName("") == "Quick Share file" && QsConnection.SafeName("..") == "Quick Share file",
        "...and an empty or dot name gets a real one");
}

Console.WriteLine("--- loopback transfer: Linc sender -> Linc receiver");
{
    var temp = Directory.CreateTempSubdirectory("linc-qs-");
    var source = Path.Combine(temp.FullName, "photo.jpg");
    var payload = RandomNumberGenerator.GetBytes(1_300_000); // three 512 KB chunks, the last partial
    File.WriteAllBytes(source, payload);
    var inbox = Path.Combine(temp.FullName, "inbox");
    Directory.CreateDirectory(inbox);
    File.WriteAllBytes(Path.Combine(inbox, "photo.jpg"), [1]); // forces the " (1)" rename

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    string? receiverPin = null, senderName = null;
    List<string>? received = null;
    QsOffer? seenOffer = null;
    var server = Task.Run(async () =>
    {
        var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var (connection, offer) = await QsConnection.AcceptAsync(client, timeout.Token);
        await using (connection)
        {
            receiverPin = connection.Pin;
            senderName = connection.RemoteName;
            seenOffer = offer;
            (received, _) = await connection.ReceiveAsync(offer, inbox, null, timeout.Token);
        }
    });

    string? senderPin;
    bool accepted;
    await using (var sender = await QsConnection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), "Test Sender", timeout.Token))
    {
        senderPin = sender.Pin;
        accepted = await sender.SendFilesAsync([source], null, timeout.Token);
    }
    await server;
    listener.Stop();

    True(receiverPin is { Length: 4 } && receiverPin == senderPin, $"both ends derive the same PIN ({senderPin} / {receiverPin})");
    True(senderName == "Test Sender", "the receiver reads the sender's name from its endpoint info");
    True(seenOffer is { Files.Count: 1 } && seenOffer.Files[0].Name == "photo.jpg" && seenOffer.TotalBytes == payload.Length,
        "the offer names the file and its size before anything is accepted");
    True(accepted, "the sender sees the receiver accept");
    var landed = received?.SingleOrDefault();
    True(landed is not null && Path.GetFileName(landed) == "photo (1).jpg", "an existing file is never overwritten");
    True(landed is not null && File.ReadAllBytes(landed).AsSpan().SequenceEqual(payload),
        "the file arrives byte-identical across multiple encrypted chunks");
    temp.Delete(recursive: true);
}

Console.WriteLine("--- a declined offer");
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var temp = Directory.CreateTempSubdirectory("linc-qs-");
    var source = Path.Combine(temp.FullName, "a.txt");
    File.WriteAllText(source, "hello");
    var server = Task.Run(async () =>
    {
        var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var (connection, _) = await QsConnection.AcceptAsync(client, timeout.Token);
        await using (connection)
        {
            await connection.RejectAsync(timeout.Token);
            await Task.Delay(200);
        }
    });
    bool accepted;
    await using (var sender = await QsConnection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), "S", timeout.Token))
    {
        accepted = await sender.SendFilesAsync([source], null, timeout.Token);
    }
    await server;
    listener.Stop();
    True(!accepted, "a decline reaches the sender as a decline, not an error");
    temp.Delete(recursive: true);
}

Console.WriteLine("--- a link sent as text arrives as a link");
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var temp = Directory.CreateTempSubdirectory("linc-qs-");
    List<QsReceivedText>? texts = null;
    QsOffer? offer = null;
    var server = Task.Run(async () =>
    {
        var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var (connection, o) = await QsConnection.AcceptAsync(client, timeout.Token);
        await using (connection)
        {
            offer = o;
            (_, texts) = await connection.ReceiveAsync(o, temp.FullName, null, timeout.Token);
        }
    });
    bool accepted;
    await using (var sender = await QsConnection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), "S", timeout.Token))
    {
        accepted = await sender.SendTextAsync("  https://example.com/a?b=c  ", timeout.Token);
    }
    await server;
    listener.Stop();
    True(accepted, "the text offer is accepted");
    True(offer is { Texts.Count: 1, Files.Count: 0 } &&
         offer.Texts[0].Type == Sharing.Nearby.TextMetadata.Types.Type.Url,
        "a web address is offered as a URL, so the phone shows Open rather than Copy");
    True(texts is { Count: 1 } && texts[0].Text == "  https://example.com/a?b=c  ",
        "the text itself arrives exactly as sent");
    True(Directory.GetFiles(temp.FullName).Length == 0, "...and no file is written for it");
    temp.Delete(recursive: true);
}

Console.WriteLine("--- auto-accept trusts the live link, never the name alone");
{
    True(QsTrust.IsOwnConnectedPhone("Pixel 7", "192.168.137.59", "Pixel 7", "192.168.137.59"),
        "same name, same address as the live Linc link: it is that phone");
    True(QsTrust.IsOwnConnectedPhone("pixel 7", "::ffff:192.168.137.59", "Pixel 7", "192.168.137.59"),
        "...including a dual-stack mapped address and a case difference");
    True(!QsTrust.IsOwnConnectedPhone("Pixel 7", "192.168.137.60", "Pixel 7", "192.168.137.59"),
        "a phone NAMED Pixel 7 elsewhere on the network is not trusted");
    True(!QsTrust.IsOwnConnectedPhone("Galaxy", "192.168.137.59", "Pixel 7", "192.168.137.59"),
        "a different name at the right address is not trusted either");
    True(!QsTrust.IsOwnConnectedPhone("Pixel 7", "192.168.137.59", "Pixel 7", null),
        "no live Wi-Fi link (disconnected, or USB) means nothing is trusted");
    True(!QsTrust.IsOwnConnectedPhone("Pixel 7", "not-an-ip", "Pixel 7", "192.168.137.59"),
        "garbage addresses are not trusted");
}

Console.WriteLine("--- Explorer hand-off arguments");
{
    True(QsShellIntegration.PathsFrom(["Linc.Desktop.exe", "--quickshare", @"C:\a b\x.jpg"]).SequenceEqual([@"C:\a b\x.jpg"]),
        "the file after --quickshare is picked up, spaces and all");
    True(QsShellIntegration.PathsFrom(["Linc.Desktop.exe", "--startup"]).Count == 0,
        "an ordinary launch carries no files");
    True(QsShellIntegration.PathsFrom(["x", "--quickshare", "a", "b", "--startup"]).SequenceEqual(["a", "b"]),
        "several files stop at the next switch");

    // The real pipe, both ends: what a second Linc (started by Explorer) hands the running one.
    using var stop = new CancellationTokenSource();
    var got = new TaskCompletionSource<IReadOnlyList<string>>();
    QsShellIntegration.Listen(paths => got.TrySetResult(paths), _ => { }, stop.Token);
    await Task.Delay(300); // let the server create the pipe
    var forwarded = QsShellIntegration.TryForward([@"C:\x\one.jpg", @"D:\two words.pdf"]);
    var arrived = await Task.WhenAny(got.Task, Task.Delay(5000)) == got.Task ? got.Task.Result : null;
    stop.Cancel();
    True(forwarded && arrived is not null && arrived.SequenceEqual([@"C:\x\one.jpg", @"D:\two words.pdf"]),
        "files forwarded over the pipe reach the running instance intact");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL CHECKS PASSED");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
failures.ForEach(f => Console.WriteLine($"  - {f}"));
return 1;
