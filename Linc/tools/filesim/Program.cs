using Linc.Desktop.Services;

// Regression for the M1 Files-page fix. Exercises two things the owner's blank Files page depended
// on, both against the REAL code compiled into this harness (see FileSim.csproj):
//
//   1. The Direct-TLS files channel — the real TlsFileService over the real Framing codec, talking
//      to a loopback server that speaks the real files-channel protocol (list / pull / push, the
//      'not-granted' permission message, and the NeedsAdb guidance for mutations).
//   2. The FileServiceRouter's transport selection by HasAdb — the exact M1 bug suspect ("router
//      choosing the wrong path when HasAdb is false"): HasAdb=true must route to ADB, HasAdb=false
//      must fall to Direct TLS (and still list the directory, not go blank).
//
//   dotnet run --project tools/filesim
//
// The ADB-sync transport itself (AdvancedSharpAdbClient) needs a live adb server + device, so it
// can't run in a plain console; that plumbing stays covered by tools/probe and on-device checks.
// Here the ADB side is a semantics-faithful stub so the router's choice can be asserted.

var failures = new List<string>();
var log = new FakeLog();

// ---- Direct TLS files channel: the real TlsFileService against a real files-channel server ----
{
    var phone = new InMemoryPhone();
    using var conn = new FakeConnection(phone) { HasAdb = false };
    var tls = new TlsFileService(conn, log);
    var tmp = FreshTempDir();

    await Case("TLS list returns the phone's directory", failures, async () =>
    {
        var entries = await tls.ListAsync("/storage/emulated/0", CancellationToken.None);
        Expect(entries.Any(e => e.Name == "notes.txt" && !e.IsDirectory), "notes.txt listed as a file");
        Expect(entries.Any(e => e.Name == "Download" && e.IsDirectory), "Download listed as a directory");
        Expect(entries[0].IsDirectory, "directories sort ahead of files");
    });

    await Case("TLS pull downloads the real bytes", failures, async () =>
    {
        var local = await tls.PullAsync("/storage/emulated/0/notes.txt", tmp, new Progress<double>(), CancellationToken.None);
        Expect(File.ReadAllText(local) == "hello from the phone", "pulled content matches the phone file");
    });

    await Case("TLS push uploads to the phone", failures, async () =>
    {
        var src = Path.Combine(tmp, "frompc.txt");
        File.WriteAllText(src, "sent from pc");
        await tls.PushAsync(src, "/storage/emulated/0/Download", new Progress<double>(), CancellationToken.None);
        Expect(phone.Exists("/storage/emulated/0/Download/frompc.txt"), "file now present on the phone");
    });

    await Case("TLS GetRoots offers internal storage", failures, async () =>
    {
        var roots = await tls.GetRootsAsync(CancellationToken.None);
        Expect(roots.Count == 1 && roots[0].Path == "/storage/emulated/0", "single internal-storage root");
    });

    await Case("TLS maps 'not-granted' to a plain-language permission message", failures, async () =>
    {
        var message = await ThrowsLinc(() => tls.ListAsync(InMemoryPhone.DeniedDir, CancellationToken.None));
        Expect(message.Contains("All files access"), "names the permission the user must grant");
    });

    foreach (var (name, action) in new (string, Func<Task>)[]
    {
        ("rename", () => tls.RenameAsync("/storage/emulated/0/notes.txt", "x.txt", CancellationToken.None)),
        ("delete", () => tls.DeleteAsync("/storage/emulated/0/notes.txt", CancellationToken.None)),
        ("new folder", () => tls.CreateFolderAsync("/storage/emulated/0", "New", CancellationToken.None)),
        ("screenshot", () => tls.PullScreenshotAsync(CancellationToken.None)),
    })
    {
        await Case($"TLS {name} asks for ADB instead of failing silently", failures, async () =>
        {
            var message = await ThrowsLinc(action);
            Expect(message.Contains("USB or wireless-debugging"), "plain-language NeedsAdb guidance");
        });
    }
}

// ---- FileServiceRouter: the real router picks the transport by HasAdb (the M1 bug suspect) ----
{
    var phone = new InMemoryPhone();
    using var conn = new FakeConnection(phone);
    var adb = new FileService(phone);
    var tls = new TlsFileService(conn, log);
    var router = new FileServiceRouter(adb, tls, conn);

    await Case("HasAdb=true routes browsing to the ADB service", failures, async () =>
    {
        conn.HasAdb = true;
        var before = adb.ListCalls;
        await router.ListAsync("/storage/emulated/0", CancellationToken.None);
        Expect(adb.ListCalls == before + 1, "ADB service handled the listing");
    });

    await Case("HasAdb=true allows mutations (routed to ADB)", failures, async () =>
    {
        conn.HasAdb = true;
        await router.DeleteAsync("/storage/emulated/0/photo.jpg", CancellationToken.None);
        Expect(!phone.Exists("/storage/emulated/0/photo.jpg"), "delete went through over ADB");
    });

    await Case("HasAdb=false routes browsing to Direct TLS (never blank)", failures, async () =>
    {
        conn.HasAdb = false;
        var before = adb.ListCalls;
        var entries = await router.ListAsync("/storage/emulated/0", CancellationToken.None);
        Expect(adb.ListCalls == before, "ADB service was NOT used");
        Expect(entries.Any(e => e.Name == "notes.txt"), "TLS listing still populated the directory");
    });

    await Case("HasAdb=false refuses a delete with ADB guidance", failures, async () =>
    {
        conn.HasAdb = false;
        var message = await ThrowsLinc(() => router.DeleteAsync("/storage/emulated/0/notes.txt", CancellationToken.None));
        Expect(message.Contains("USB or wireless-debugging"), "router surfaced the TLS NeedsAdb message");
    });

    await Case("a live HasAdb flip re-selects the transport", failures, async () =>
    {
        conn.HasAdb = false;
        var src = Path.Combine(FreshTempDir(), "viaTls.txt");
        File.WriteAllText(src, "hi");
        await router.PushAsync(src, "/storage/emulated/0/Download", new Progress<double>(), CancellationToken.None);
        conn.HasAdb = true; // e.g. a USB cable just took over from Direct TLS
        var listed = await router.ListAsync("/storage/emulated/0/Download", CancellationToken.None);
        Expect(listed.Any(e => e.Name == "viaTls.txt"), "same phone seen over both transports after the switch");
    });
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL SCENARIOS PASSED");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;

static async Task Case(string name, List<string> failures, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine($"  PASS  {name}");
    }
    catch (ExpectException ex)
    {
        Console.WriteLine($"  FAIL  {name} — {ex.Message}");
        failures.Add($"{name}: {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL  {name} — threw {ex.GetType().Name}: {ex.Message}");
        failures.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}");
    }
}

static void Expect(bool condition, string what)
{
    if (!condition)
    {
        throw new ExpectException(what);
    }
}

static async Task<string> ThrowsLinc(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (LincException ex)
    {
        return ex.Message;
    }
    throw new ExpectException("expected a LincException but none was thrown");
}

static string FreshTempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "linc-filesim", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
}

sealed class ExpectException(string message) : Exception(message);
