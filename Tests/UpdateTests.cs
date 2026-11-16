using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LaTaleGarden;

public sealed class MemoryTransport : IUpdateTransport
{
    public byte[] Bytes; public bool Fail, Cancel;
    public void Copy(Uri uri, Stream target, long limit, Action<long> progress, CancellationToken token)
    {
        target.Write(Bytes, 0, Fail || Cancel ? Bytes.Length / 2 : Bytes.Length);
        if (Fail) throw new IOException("Simulated disconnected download");
        if (Cancel) throw new OperationCanceledException();
    }
}
public static class UpdateTests
{
    static readonly List<string> lines = new List<string>();
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, "Expected rejection"); }
    static void Test(string name, Action action) { action(); lines.Add("PASS " + name); Console.WriteLine(lines.Last()); }
    static byte[] Sign(object value, RSACryptoServiceProvider rsa)
    {
        byte[] bytes = value is UpdateFeed ? UpdateProtocol.WriteJson((UpdateFeed)value) : UpdateProtocol.WriteJson((AnnouncementFeed)value);
        return UpdateProtocol.WriteJson(new SignedDocument { Payload = Convert.ToBase64String(bytes), Signature = Convert.ToBase64String(rsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256"))) });
    }
    static UpdateRelease Release(string version, string file)
    {
        return new UpdateRelease { Version = version, Channel = "preview", Url = Program.ProjectUrl + "/releases/download/v" + version + "/LaTaleGarden.exe",
            Notes = "测试更新", Sha256 = UpdateProtocol.Hash(file), Size = new FileInfo(file).Length, MinimumWindowsBuild = 10240, MinimumFrameworkRelease = 528040 };
    }
    public static int Main(string[] args)
    {
        string root = args[0], fixture = args[1];
        Directory.CreateDirectory(root);
        try
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            using (var wrong = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = wrong.PersistKeyInCsp = false;
                var release = Release("9.0.0", fixture);
                var feed = new UpdateFeed { Schema = 1, Releases = new[] { release } };
                var signed = Sign(feed, rsa);
                Test("signed metadata verifies with the pinned publisher key", () => Check(UpdateProtocol.ReadFeed(signed, rsa.ToXmlString(false)).Releases.Length == 1, "Missing release"));
                Test("wrong publisher, modified payload and unsigned metadata are rejected", () => {
                    Reject(() => UpdateProtocol.ReadFeed(signed, wrong.ToXmlString(false)));
                    var document = UpdateProtocol.ReadJson<SignedDocument>(signed); document.Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
                    Reject(() => UpdateProtocol.ReadFeed(UpdateProtocol.WriteJson(document), rsa.ToXmlString(false)));
                    Reject(() => UpdateProtocol.ReadFeed(UpdateProtocol.WriteJson(feed), rsa.ToXmlString(false)));
                });
                Test("schema and document limits fail closed", () => {
                    Reject(() => UpdateProtocol.Verify<UpdateFeed>(new byte[UpdateProtocol.MaximumDocument + 1], rsa.ToXmlString(false)));
                    feed.Schema = 2; Reject(() => UpdateProtocol.ReadFeed(Sign(feed, rsa), rsa.ToXmlString(false))); feed.Schema = 1;
                });
                Test("versions are compared numerically, with no downgrade or same-version update", () => {
                    Check(UpdateProtocol.ParseVersion("1.10.0") > UpdateProtocol.ParseVersion("1.9.9"), "Lexicographic ordering");
                    Check(UpdateProtocol.Select(feed, "9.0.0", true, 30000, 600000) == null, "Same version");
                    Check(UpdateProtocol.Select(feed, "10.0.0", true, 30000, 600000) == null, "Downgrade");
                    foreach (var invalid in new[] { "../2.0", "1.2", "1.2.3.4", "01.2.3", "-1.2.3", "v1.2.3" }) Reject(() => UpdateProtocol.ParseVersion(invalid));
                });
                Test("stable channel excludes previews and OS/runtime requirements are respected", () => {
                    Check(UpdateProtocol.Select(feed, "1.1.3", false, 30000, 600000) == null, "Preview leaked");
                    Check(UpdateProtocol.Select(feed, "1.1.3", true, 30000, 600000) != null, "Preview unavailable");
                    Check(UpdateProtocol.Select(feed, "1.1.3", true, 9000, 600000) == null, "Unsupported OS");
                    Check(UpdateProtocol.Select(feed, "1.1.3", true, 30000, 500000) == null, "Unsupported runtime");
                });
                Test("download URL is pinned to the project, exact tag and executable", () => {
                    string original = release.Url;
                    foreach (var url in new[] { "http://github.com/visionki/latale-garden/releases/download/v9.0.0/LaTaleGarden.exe",
                        original.Replace("visionki", "other"), original + "?redirect=evil", "https://example.com/a.exe" }) { release.Url = url; Reject(() => UpdateProtocol.Validate(release)); }
                    release.Url = original;
                    Check(!UpdateProtocol.DownloadHost(new Uri("https://github.com.evil.test/a")) && !UpdateProtocol.DownloadHost(new Uri("http://github.com/a")) &&
                        !UpdateProtocol.DownloadHost(new Uri("https://user@github.com/a")) && UpdateProtocol.DownloadHost(new Uri("https://release-assets.githubusercontent.com/a")), "Redirect trust");
                });
                Test("duplicate release versions and invalid sizes/hashes are rejected", () => {
                    feed.Releases = new[] { release, release }; Reject(() => UpdateProtocol.ReadFeed(Sign(feed, rsa), rsa.ToXmlString(false))); feed.Releases = new[] { release };
                    var hash = release.Sha256; release.Sha256 = "invalid"; Reject(() => UpdateProtocol.Validate(release)); release.Sha256 = hash;
                    var size = release.Size; release.Size = long.MaxValue; Reject(() => UpdateProtocol.Validate(release)); release.Size = size;
                });
                Test("package hash, file size, architecture and embedded version are verified", () => {
                    UpdateProtocol.VerifyPackage(fixture, release);
                    var fakeVersion = Release("8.0.0", fixture); Reject(() => UpdateProtocol.VerifyPackage(fixture, fakeVersion));
                    string tampered = Path.Combine(root, "tampered.exe"); File.Copy(fixture, tampered, true);
                    using (var stream = File.Open(tampered, FileMode.Open, FileAccess.Write)) stream.WriteByte(0);
                    Reject(() => UpdateProtocol.VerifyPackage(tampered, release));
                });
                Test("interrupted, cancelled and corrupt downloads retain the previous valid package", () => {
                    string target = Path.Combine(root, "download", "LaTaleGarden.exe"); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(fixture, target, true);
                    foreach (int failure in new[] { 0, 1, 2 }) {
                        var transport = new MemoryTransport { Bytes = File.ReadAllBytes(fixture), Fail = failure == 0, Cancel = failure == 1 };
                        if (failure == 2) transport.Bytes[0] = 0;
                        Reject(() => new UpdateClient(transport).Download(release, target, null, CancellationToken.None));
                        Check(UpdateProtocol.Hash(target) == release.Sha256, "Original package replaced after failure");
                        Check(Directory.GetFiles(Path.GetDirectoryName(target), "*.part").Length == 0, "Partial file leaked");
                    }
                });
                Test("valid full package download commits only after verification", () => {
                    string target = Path.Combine(root, "complete", "LaTaleGarden.exe");
                    new UpdateClient(new MemoryTransport { Bytes = File.ReadAllBytes(fixture) }).Download(release, target, null, CancellationToken.None);
                    UpdateProtocol.VerifyPackage(target, release);
                });
                Test("preferences migration defaults, one-day throttle and clock reversal are handled", () => {
                    var prefs = UpdateProtocol.ReadJson<UpdatePreferences>(Encoding.UTF8.GetBytes("{}")); prefs.Normalize();
                    prefs = UpdateProtocol.ReadJson<UpdatePreferences>(UpdateProtocol.WriteJson(prefs));
                    Check(prefs.AutoCheck && prefs.ShowAnnouncements && !prefs.AutoDownload && prefs.DismissedAnnouncements.Length == 0, "Wrong defaults");
                    var now = DateTime.UtcNow;
                    Check(!UpdatePreferences.Due(now.AddMinutes(-1), now) && UpdatePreferences.Due(now.AddDays(-2), now) && UpdatePreferences.Due(now.AddDays(3), now), "Throttle");
                });
                var announcement = new Announcement { Id = "release-113", Title = "新功能", Text = "更新说明", Url = Program.ProjectUrl, StartsUtc = "2026-01-01T00:00:00Z", ExpiresUtc = "2027-01-01T00:00:00Z" };
                var announcements = new AnnouncementFeed { Schema = 1, Items = new[] { announcement } };
                Test("signed announcements respect start, expiry and dismissal", () => {
                    var verified = UpdateProtocol.ReadAnnouncements(Sign(announcements, rsa), rsa.ToXmlString(false));
                    Check(UpdateProtocol.VisibleAnnouncement(verified, new string[0], new DateTime(2026, 9, 18)) != null, "Active missing");
                    Check(UpdateProtocol.VisibleAnnouncement(verified, new[] { announcement.Id }, new DateTime(2026, 9, 18)) == null, "Dismissal ignored");
                    Check(UpdateProtocol.VisibleAnnouncement(verified, new string[0], new DateTime(2027, 1, 1)) == null, "Expired visible");
                    Check(UpdateProtocol.VisibleAnnouncement(verified, new string[0], new DateTime(2025, 12, 31)) == null, "Future visible");
                });
                Test("announcement links reject scripts, local files and embedded credentials", () => {
                    foreach (string url in new[] { "javascript:alert(1)", "file:///C:/Windows/notepad.exe", "https://user@github.com/a", "http://github.com" }) {
                        announcement.Url = url; Reject(() => UpdateProtocol.ReadAnnouncements(Sign(announcements, rsa), rsa.ToXmlString(false)));
                    }
                });
                Test("update jobs reject traversal, unknown identifiers and network paths", () => {
                    foreach (string id in new[] { "..", "../some", "", "not-a-guid" }) Reject(() => UpdateInstaller.JobPath(id));
                    Reject(() => UpdateInstaller.RequireRegularPath(@"\\server\share\launcher.exe"));
                });
            }
            File.WriteAllLines(Path.Combine(root, "results.txt"), lines);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); return 1; }
    }
}
