// fingerprintsim — D-094: which known phone is this, from whatever signals the PC has seen?
//
// Runs the REAL PhoneFingerprint scorer (compiled from Linc.Desktop) against a fixed set of known
// phones. Exits 0 when every check passes, 1 otherwise, like every harness in tools/.
//
//   1. Each strong signal alone (serial, certificate) recognises its phone.
//   2. A learned Wi-Fi MAC alone recognises its phone, whichever of its MACs it is.
//   3. A strong MISMATCH is never outvoted by weaker matches (MAC + model of phone A, serial of B).
//   4. Two phones of the same model with no stronger signal: "don't know", never a guess.
//   5. Unknown or junk signals: no match.
//   6. MAC normalisation: case, separators, and refusing broadcast/multicast/zero.
using Linc.Desktop.Services;

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures.Add(what);
}

var pixelA = new KnownDevice("PIXEL-A", "Pixel 7", DateTimeOffset.UtcNow, PhoneCertBase64: "CERT-A",
    WifiMacs: ["da:a1:19:00:00:01", "da:a1:19:00:00:02"]);
var pixelB = new KnownDevice("PIXEL-B", "Pixel 7", DateTimeOffset.UtcNow, PhoneCertBase64: "CERT-B",
    WifiMacs: ["da:a1:19:00:00:0b"]);
var itel = new KnownDevice("131722549O000808", "itel S685LN", DateTimeOffset.UtcNow);
KnownDevice[] known = [pixelA, pixelB, itel];

KnownDevice? Best(PhoneSignals s) => PhoneFingerprint.Best(known, s);

Console.WriteLine("--- 1. strong signals alone");
Check(Best(new(Serial: "PIXEL-B")) == pixelB, "the serial alone recognises its phone");
Check(Best(new(CertBase64: "CERT-A")) == pixelA, "the certificate alone recognises its phone");
Check(Best(new(Serial: "131722549O000808", Model: "itel S685LN")) == itel, "serial + model agree");

Console.WriteLine("--- 2. Wi-Fi MAC alone");
Check(Best(new(WifiMac: "DA-A1-19-00-00-02")) == pixelA, "a phone's second network's MAC still recognises it");
Check(Best(new(WifiMac: "da:a1:19:00:00:0b")) == pixelB, "and the other phone's MAC recognises the other phone");

Console.WriteLine("--- 3. a strong mismatch is never outvoted");
Check(Best(new(Serial: "PIXEL-B", WifiMac: "da:a1:19:00:00:01", Model: "Pixel 7")) == pixelB,
    "phone A's MAC + model cannot beat phone B's serial");
Check(PhoneFingerprint.Score(pixelA, new(Serial: "PIXEL-B", WifiMac: "da:a1:19:00:00:01")) < PhoneFingerprint.MatchThreshold,
    "a serial mismatch drags a MAC match below the threshold");
Check(Best(new(CertBase64: "CERT-X", Serial: "PIXEL-A")) is null,
    "a certificate nobody has, against a known serial, is not trusted either way");

Console.WriteLine("--- 4. lookalikes");
Check(Best(new(Model: "Pixel 7")) is null, "the model alone matches nobody (two Pixel 7s)");
Check(Best(new(WifiMac: "02:00:00:00:00:99", Model: "Pixel 7")) is null, "an unknown MAC + a shared model: don't know");

Console.WriteLine("--- 5. unknown and junk");
Check(Best(new()) is null, "no signals, no match");
Check(Best(new(Serial: "SOMEONE-ELSE")) is null, "an unknown serial matches nobody");
Check(Best(new(WifiMac: "ff:ff:ff:ff:ff:ff")) is null, "broadcast never identifies a phone");
Check(PhoneFingerprint.Best([], new(Serial: "PIXEL-A")) is null, "no known phones, no match");

Console.WriteLine("--- 6. MAC normalisation");
Check(PhoneFingerprint.NormalizeMac("DA-A1-19-00-00-01") == "da:a1:19:00:00:01", "dashes and upper case normalise");
Check(PhoneFingerprint.NormalizeMac("daa119000001") == "da:a1:19:00:00:01", "bare hex normalises");
Check(PhoneFingerprint.NormalizeMac("00:00:00:00:00:00") is null, "all-zero is refused");
Check(PhoneFingerprint.NormalizeMac("01:00:5e:00:00:fb") is null, "multicast is refused");
Check(PhoneFingerprint.NormalizeMac("da:a1:19") is null, "a short address is refused");
Check(PhoneFingerprint.NormalizeMac(null) is null, "null is refused");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("PASS: fingerprinting recognises phones by any one strong signal or a learned MAC, and never guesses.");
    return 0;
}
Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;
