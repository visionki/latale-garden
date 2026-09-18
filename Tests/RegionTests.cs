using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LaTaleGarden;

public class FakeRegionPlatform : IRegionPlatform
{
    public bool SupportsTemporaryLocale { get { return true; } }
    public LocaleSnapshot Current = RegionCatalog.Preset("zh-CN");
    public int ApplyCount, RestoreCount, GuardCount, RestoreFailures;
    public bool Game, Cancel, Dead, ApplyFailure, WrongAppliedValue;
    public Action OnGuard, OnApply;
    public double ElapsedSeconds { get { return 0; } }
    public bool Cancelled { get { return Cancel; } }
    public bool OwnerAlive { get { return !Dead; } }
    public bool RegionGameRunning { get { return Game; } }
    public void Sleep(int milliseconds) { }
    public void ValidateGame(string directory) { throw new Exception("Unexpected game launch"); }
    public bool ExistingGame(string directory) { return Game; }
    public LocaleSnapshot CaptureLocale() { return Current; }
    public void StartGuard() { GuardCount++; if (OnGuard != null) OnGuard(); }
    public void ApplyTraditional() { throw new Exception("Unexpected temporary operation"); }
    public void ApplyRegion(string name)
    {
        ApplyCount++; Current = RegionCatalog.Preset(name);
        if (WrongAppliedValue) Current.MACCP = "10008";
        if (OnApply != null) OnApply();
        if (ApplyFailure) throw new IOException("Partial set failure");
    }
    public void Restore(LocaleSnapshot snapshot)
    {
        RestoreCount++;
        if (RestoreCount <= RestoreFailures) throw new IOException("Restore denied");
        Current = snapshot;
    }
    public bool Matches(LocaleSnapshot snapshot) { return RegionCatalog.Same(Current, snapshot); }
    public void StartOfficialLauncher(string directory) { throw new Exception("Unexpected game launch"); }
    public ClientInfo FindClient(string directory) { return null; }
    public void Probe() { }
}

public static class RegionTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static SessionFiles NewFiles(string root) { return new SessionFiles(Path.Combine(root, Guid.NewGuid().ToString("N"))); }
    static SessionFiles Backup(string root, bool pending, LocaleSnapshot snapshot)
    {
        var f = NewFiles(root);
        f.SaveJournal(new Journal { Id = Path.GetFileName(f.DirectoryPath), Original = snapshot, Pending = pending, CreatedUtc = DateTime.UtcNow });
        return f;
    }
    static RegionRequest Request(string root, FakeRegionPlatform p, SessionFiles backup)
    {
        return new RegionRequest { Operation = backup == null ? "set" : "restore", LocaleName = "zh-TW", Source = backup == null ? null : RegionCatalog.Reference(backup.DirectoryPath), ExpectedCurrent = p.Current, AcknowledgedPending = RegionCatalog.Pending(root) };
    }
    static SessionFiles Run(string root, FakeRegionPlatform p, RegionRequest request)
    {
        var f = NewFiles(root); new RegionEngine(f, p).Run(request); return f;
    }
    static string Stage(SessionFiles f) { return JsonFile.Read<SessionStatus>(f.FilePath("status.json")).Stage; }
    public static void Run(string root, Action<string, Action> test)
    {
        Directory.CreateDirectory(root);
        Action<string, Action<string>> scenario = (name, action) => test(name, () => { string dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); action(dir); });
        scenario("completed backup is reapplied even when current values already match, without changing source", dir => {
            var p = new FakeRegionPlatform(); var b = Backup(dir, false, p.Current); string hash = RegionCatalog.Hash(b.FilePath("recovery.json"));
            var f = Run(dir, p, Request(dir, p, b));
            Check(Stage(f) == "region-done" && p.RestoreCount == 1 && f.ReadJournal().Committed, "Reapply was skipped");
            Check(hash == RegionCatalog.Hash(b.FilePath("recovery.json")), "Source was rewritten");
        });
        scenario("restore preserves non-Chinese locale and exact saved code pages", dir => {
            var p = new FakeRegionPlatform(); var target = new LocaleSnapshot { LocaleName = "ja-JP", DefaultLanguage = "0411", ACP = "932", OEMCP = "437", MACCP = "10001" };
            var b = Backup(dir, false, target); var f = Run(dir, p, Request(dir, p, b));
            Check(Stage(f) == "region-done" && RegionCatalog.Same(p.Current, target), "Original values not preserved");
        });
        scenario("successful manual selection persists and keeps the previous launch backup", dir => {
            var p = new FakeRegionPlatform(); var b = Backup(dir, false, p.Current); var f = Run(dir, p, Request(dir, p, null));
            Check(Stage(f) == "region-done" && p.Current.LocaleName == "zh-TW" && p.RestoreCount == 0, "Selection reverted");
            Check(f.ReadJournal().Committed && !f.ReadJournal().Pending && RegionCatalog.LatestBackup(dir).Reference.Id == Path.GetFileName(b.DirectoryPath), "Launch backup replaced");
        });
        scenario("partial manual selection failure rolls back the pre-operation values", dir => {
            var p = new FakeRegionPlatform { ApplyFailure = true }; var before = p.Current; var f = Run(dir, p, Request(dir, p, null));
            Check(Stage(f) == "region-failed" && p.RestoreCount == 1 && RegionCatalog.Same(before, p.Current) && !f.ReadJournal().Committed && !f.ReadJournal().Pending, "Partial write leaked");
        });
        scenario("failed manual rollback retains the original target for restart recovery", dir => {
            var p = new FakeRegionPlatform { ApplyFailure = true, RestoreFailures = 100 }; var before = p.Current; var f = Run(dir, p, Request(dir, p, null));
            Check(Stage(f) == "recovery" && SessionFiles.NeedsRecovery(f.DirectoryPath), "Recovery not retained");
            var restart = new FakeRegionPlatform { Current = p.Current };
            Check(LaunchEngine.RestorePending(f, restart, f.ReadJournal()) && RegionCatalog.Same(restart.Current, before) && !f.ReadJournal().Committed, "Restart retained unsuccessful selection");
        });
        scenario("failed explicit restore retries the chosen backup, never the pre-action wrong locale", dir => {
            var p = new FakeRegionPlatform { RestoreFailures = 100, Current = RegionCatalog.Preset("zh-TW") }; var b = Backup(dir, false, RegionCatalog.Preset("zh-CN"));
            var f = Run(dir, p, Request(dir, p, b));
            Check(Stage(f) == "recovery" && f.ReadJournal().Pending && RegionCatalog.RecoveryTarget(f.ReadJournal()).LocaleName == "zh-CN", "Wrong retry intent");
            var restart = new FakeRegionPlatform { Current = p.Current };
            Check(LaunchEngine.RestorePending(f, restart, f.ReadJournal()) && restart.Current.LocaleName == "zh-CN" && f.ReadJournal().Committed, "Wrong restart recovery");
        });
        scenario("full read-back mismatch rolls a manual selection back", dir => {
            var p = new FakeRegionPlatform { WrongAppliedValue = true }; var f = Run(dir, p, Request(dir, p, null));
            Check(Stage(f) == "region-failed" && p.Current.LocaleName == "zh-CN" && p.RestoreCount == 1, "Incorrect Mac code page accepted");
        });
        scenario("guard startup failure prevents manual writes", dir => {
            var p = new FakeRegionPlatform { OnGuard = () => { throw new IOException("No guard"); } }; var f = Run(dir, p, Request(dir, p, null));
            Check(Stage(f) == "region-failed" && p.ApplyCount == 0 && p.RestoreCount == 0 && !f.ReadJournal().Pending, "Wrote without guard");
        });
        scenario("cancel or owner death before writes leaves settings intact", dir => {
            foreach (bool dead in new[] { false, true }) {
                var p = new FakeRegionPlatform(); p.OnGuard = () => { p.Dead = dead; p.Cancel = !dead; };
                var f = Run(dir, p, Request(dir, p, null)); Check(Stage(f) == "region-failed" && p.ApplyCount == 0 && p.RestoreCount == 0, "Wrote after owner cancellation");
            }
        });
        scenario("owner exit or cancellation during manual write rolls back before commit", dir => {
            foreach (bool dead in new[] { false, true }) {
                var p = new FakeRegionPlatform(); p.OnApply = () => { p.Dead = dead; p.Cancel = !dead; };
                var f = Run(dir, p, Request(dir, p, null)); Check(Stage(f) == "region-failed" && p.Current.LocaleName == "zh-CN" && !f.ReadJournal().Committed, "Committed after cancellation");
            }
        });
        scenario("manual choice blocks both existing game and game appearing during guard startup", dir => {
            foreach (bool existing in new[] { false, true }) {
                var p = new FakeRegionPlatform { Game = existing }; p.OnGuard = () => p.Game = true;
                var f = Run(dir, p, Request(dir, p, null)); Check(Stage(f) == "region-failed" && p.ApplyCount == 0, "Switched with game running");
            }
        });
        scenario("explicit backup restore remains possible while game is running", dir => {
            var p = new FakeRegionPlatform { Game = true }; var b = Backup(dir, false, p.Current); var f = Run(dir, p, Request(dir, p, b));
            Check(Stage(f) == "region-done" && p.RestoreCount == 1, "Emergency restore blocked");
        });
        scenario("stale current configuration before or after guard startup prevents writes", dir => {
            foreach (bool later in new[] { false, true }) {
                var p = new FakeRegionPlatform(); var request = Request(dir, p, null);
                if (later) p.OnGuard = () => p.Current = RegionCatalog.Preset("zh-TW"); else p.Current = RegionCatalog.Preset("zh-TW");
                var f = Run(dir, p, request); Check(Stage(f) == "region-failed" && p.ApplyCount == 0 && p.RestoreCount == 0, "Overwrote changed settings");
            }
        });
        scenario("backup changed after confirmation is rejected", dir => {
            var p = new FakeRegionPlatform(); var b = Backup(dir, false, p.Current); var request = Request(dir, p, b);
            var changed = b.ReadJournal(); changed.Original = RegionCatalog.Preset("zh-TW"); b.SaveJournal(changed);
            var f = Run(dir, p, request); Check(Stage(f) == "region-failed" && p.RestoreCount == 0 && p.GuardCount == 0, "Changed backup accepted");
        });
        scenario("pending source is resolved with a verifiable receipt and unchanged original file", dir => {
            var p = new FakeRegionPlatform(); var b = Backup(dir, true, p.Current); string hash = RegionCatalog.Hash(b.FilePath("recovery.json"));
            var f = Run(dir, p, Request(dir, p, b));
            Check(Stage(f) == "region-done" && !SessionFiles.NeedsRecovery(b.DirectoryPath) && RegionCatalog.HasResolution(b.DirectoryPath) && hash == RegionCatalog.Hash(b.FilePath("recovery.json")), "Resolution lost history");
            var op = f.ReadJournal(); op.Committed = false; f.SaveJournal(op);
            Check(SessionFiles.NeedsRecovery(b.DirectoryPath), "Uncommitted receipt trusted");
        });
        scenario("manual choice can resolve acknowledged corrupt history without deleting evidence", dir => {
            var p = new FakeRegionPlatform(); var b = NewFiles(dir); File.WriteAllText(b.FilePath("recovery.json"), "{broken");
            string hash = RegionCatalog.Hash(b.FilePath("recovery.json")); var f = Run(dir, p, Request(dir, p, null));
            Check(Stage(f) == "region-done" && !SessionFiles.NeedsRecovery(b.DirectoryPath) && RegionCatalog.HasResolution(b.DirectoryPath), "Corrupt history stayed blocked");
            Check(hash == RegionCatalog.Hash(b.FilePath("recovery.json")), "Evidence deleted");
            File.AppendAllText(b.FilePath("recovery.json"), "changed"); Check(SessionFiles.NeedsRecovery(b.DirectoryPath), "Receipt trusted after source changed");
        });
        scenario("new pending record after confirmation requires fresh acknowledgement", dir => {
            var p = new FakeRegionPlatform(); var request = Request(dir, p, null); Backup(dir, true, p.Current);
            var f = Run(dir, p, request); Check(Stage(f) == "region-failed" && p.ApplyCount == 0 && p.GuardCount == 0, "Unacknowledged history overwritten");
        });
        scenario("pending explicit restore exposes its intended backup, not its starting configuration", dir => {
            var f = NewFiles(dir); f.SaveJournal(new Journal { Id = Path.GetFileName(f.DirectoryPath), Operation = "restore", Pending = true, Original = RegionCatalog.Preset("zh-TW"), Target = RegionCatalog.Preset("zh-CN"), CreatedUtc = DateTime.UtcNow });
            Check(RegionCatalog.LatestBackup(dir).Target.LocaleName == "zh-CN", "Misleading recovery target");
        });
        scenario("UTF-8 mode and unsupported manual locales are rejected without changes", dir => {
            foreach (bool utf8 in new[] { false, true }) {
                var p = new FakeRegionPlatform(); if (utf8) p.Current.ACP = "65001";
                var request = Request(dir, p, null); if (!utf8) request.LocaleName = "ja-JP";
                var f = Run(dir, p, request); Check(Stage(f) == "region-failed" && p.ApplyCount == 0 && p.GuardCount == 0, "Unsupported mode modified");
            }
        });
        scenario("malformed and traversing backup references are rejected before writes", dir => {
            var p = new FakeRegionPlatform(); var b = NewFiles(dir); File.WriteAllText(b.FilePath("recovery.json"), "{}");
            var request = Request(dir, p, b); var f = Run(dir, p, request);
            Check(Stage(f) == "region-failed" && p.RestoreCount == 0, "Malformed backup used");
            request.Source.Id = ".."; f = Run(dir, p, request); Check(Stage(f) == "region-failed" && p.RestoreCount == 0, "Path traversal accepted");
        });
        test("region detection sees another thread holding the real transaction mutex", () => {
            using (var mutex = new Mutex(false, Program.TransactionMutex)) {
                Check(Program.TryLock(mutex, 1000), "External operation is active");
                try { Check(Task.Run(() => RegionDiagnostics.WriteBusy()).Result, "Busy mutex ignored"); }
                finally { mutex.ReleaseMutex(); }
            }
        });
    }
}
