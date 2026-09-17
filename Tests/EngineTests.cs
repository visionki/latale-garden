using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LaTaleGarden;

public class FakePlatform : ILaunchPlatform
{
    public double Now, ApplyAt = -1, RestoreAt = -1;
    public int CaptureCount, ApplyCount, RestoreCount, LaunchCount, GuardCount;
    public bool InvalidPath, OldGame, GuardFailure, ApplyFailure, LaunchFailure, NeverClient, NeverWindow, BadVerification;
    public bool WindowDisappears;
    public int RestoreFailures;
    public double CancelAt = double.MaxValue, OwnerDiesAt = double.MaxValue;
    public LocaleSnapshot Original = new LocaleSnapshot { LocaleName = "ja-JP", DefaultLanguage = "0411", ACP = "932", OEMCP = "932", MACCP = "10001", RuntimeACP = 936 };
    public LocaleSnapshot Restored;
    public double ElapsedSeconds { get { return Now; } }
    public bool Cancelled { get { return Now >= CancelAt; } }
    public bool OwnerAlive { get { return Now < OwnerDiesAt; } }
    public void Sleep(int milliseconds) { Now += milliseconds / 1000.0; }
    public void ValidateGame(string directory) { if (InvalidPath) throw new IOException("Missing game"); }
    public bool ExistingGame(string directory) { return OldGame; }
    public LocaleSnapshot CaptureLocale() { CaptureCount++; return Original; }
    public void StartGuard() { GuardCount++; if (GuardFailure) throw new IOException("Guard startup failed"); }
    public void ApplyTraditional() { ApplyCount++; ApplyAt = Now; if (ApplyFailure) throw new IOException("Partially applied locale"); }
    public void Restore(LocaleSnapshot original) { RestoreCount++; if (RestoreCount <= RestoreFailures) throw new IOException("Restore denied"); Restored = original; RestoreAt = Now; }
    public bool Matches(LocaleSnapshot original) { return !BadVerification && Restored == original; }
    public void StartOfficialLauncher(string directory) { LaunchCount++; if (LaunchFailure) throw new IOException("Launch failed"); }
    public ClientInfo FindClient(string directory)
    {
        if (NeverClient || Now < 2) return null;
        return new ClientInfo { Id = 100, StartTicks = 1000, HasWindow = !NeverWindow && Now >= 4 && !(WindowDisappears && Now >= 6 && Now <= 8) };
    }
    public void Probe() { }
}
public static class EngineTests
{
    static string root;
    static List<string> results = new List<string>();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static SessionFiles Run(string name, FakePlatform fake, bool compatibility, int wait, int settle)
    {
        var files = new SessionFiles(Path.Combine(root, name));
        var request = new LaunchRequest { Compatibility = compatibility, GameDirectory = @"D:\Fake LaTale", WaitSeconds = wait, SettleSeconds = settle };
        new LaunchEngine(files, fake).Run(request);
        return files;
    }
    static string Stage(SessionFiles f) { return JsonFile.Read<SessionStatus>(f.FilePath("status.json")).Stage; }
    static void Test(string name, Action action) { action(); results.Add("PASS " + name); Console.WriteLine(results.Last()); }
    public static int Main(string[] args)
    {
        root = Path.GetFullPath(args.Length > 0 ? args[0] : "engine-test-results"); Directory.CreateDirectory(root);
        try
        {
            Test("success waits for new client window and initialization buffer, restores original Japanese locale", () => {
                var p = new FakePlatform(); var f = Run("success", p, true, 40, 12);
                Check(Stage(f) == "running", "Not running"); Check(p.RestoreAt >= 14, "Restored too early"); Check(p.Restored.LocaleName == "ja-JP", "Hardcoded original locale"); Check(!f.ReadJournal().Pending, "Journal still pending");
            });
            Test("standard mode never captures or modifies locale and never starts a guard", () => {
                var p = new FakePlatform(); var f = Run("standard", p, false, 40, 5);
                Check(Stage(f) == "running" && p.ApplyCount == 0 && p.RestoreCount == 0 && p.CaptureCount == 0 && p.GuardCount == 0, "Locale operation in standard mode");
            });
            Test("cancel before preparation does not modify locale", () => {
                var p = new FakePlatform { CancelAt = 0 }; var f = Run("cancel-early", p, true, 40, 10); Check(Stage(f) == "cancelled" && p.ApplyCount == 0, "Changed before cancellation");
            });
            Test("cancel while waiting restores once", () => {
                var p = new FakePlatform { CancelAt = 4, NeverClient = true }; var f = Run("cancel-wait", p, true, 40, 10); Check(Stage(f) == "cancelled" && p.RestoreCount == 1 && !f.ReadJournal().Pending, "Not restored on cancel");
            });
            Test("GUI process exit triggers restoration", () => {
                var p = new FakePlatform { OwnerDiesAt = 5 }; var f = Run("owner-exit", p, true, 40, 10); Check(Stage(f) == "cancelled" && p.RestoreCount == 1, "GUI death not handled");
            });
            Test("timeout restores original settings", () => {
                var p = new FakePlatform { NeverClient = true }; var f = Run("timeout", p, true, 6, 10); Check(Stage(f) == "failed" && p.RestoreCount == 1 && !f.ReadJournal().Pending, "Timeout leaked locale");
            });
            Test("invalid game and existing game stop before any locale operation", () => {
                foreach (var p in new[] { new FakePlatform { InvalidPath = true }, new FakePlatform { OldGame = true } }) { var f = Run("preflight-" + p.InvalidPath, p, true, 40, 10); Check(Stage(f) == "failed" && p.CaptureCount == 0 && p.ApplyCount == 0, "Unsafe preflight"); }
            });
            Test("watchdog handshake failure prevents locale modification", () => {
                var p = new FakePlatform { GuardFailure = true }; var f = Run("guard-fail", p, true, 40, 10); Check(Stage(f) == "failed" && p.ApplyCount == 0 && !f.ReadJournal().Pending, "Modified without guard");
            });
            Test("partial locale-setting failure rolls back and never starts official launcher", () => {
                var p = new FakePlatform { ApplyFailure = true }; var f = Run("apply-fail", p, true, 40, 10); Check(Stage(f) == "failed" && p.RestoreCount == 1 && p.LaunchCount == 0, "Partial write leaked");
            });
            Test("official launcher failure rolls back", () => {
                var p = new FakePlatform { LaunchFailure = true }; var f = Run("launch-fail", p, true, 40, 10); Check(Stage(f) == "failed" && p.RestoreCount == 1, "Launch error leaked");
            });
            Test("transient restore errors retry and verify before clearing journal", () => {
                var p = new FakePlatform { RestoreFailures = 2 }; var f = Run("restore-retry", p, true, 40, 5); Check(Stage(f) == "running" && p.RestoreCount == 3 && !f.ReadJournal().Pending, "Retry failed");
            });
            Test("permanent restore failure retains journal and produces recoverable state", () => {
                var p = new FakePlatform { RestoreFailures = 50 }; var f = Run("restore-fail", p, true, 40, 5); Check(Stage(f) == "recovery" && f.ReadJournal().Pending, "Lost recovery journal");
                var restart = new FakePlatform(); Check(LaunchEngine.RestorePending(f, restart, f.ReadJournal()), "Persisted recovery failed"); Check(!f.ReadJournal().Pending && restart.Restored.LocaleName == "ja-JP", "Incorrect restart recovery");
            });
            Test("read-back mismatch cannot be reported as restored", () => {
                var p = new FakePlatform { BadVerification = true }; var f = Run("verify-fail", p, true, 40, 5); Check(Stage(f) == "recovery" && f.ReadJournal().Pending && p.RestoreCount == 3, "Unverified success");
            });
            Test("process existence without a visible stable window is insufficient", () => {
                var p = new FakePlatform { NeverWindow = true }; var f = Run("no-window", p, true, 12, 5); Check(Stage(f) == "failed" && p.RestoreCount == 1, "Process mistaken for ready window");
                p = new FakePlatform { WindowDisappears = true }; f = Run("window-reset", p, true, 30, 5); Check(Stage(f) == "running" && p.RestoreAt >= 12, "Window stability timer was not reset");
            });
            Test("UTF-8 configuration is left intact and clearly unsupported by temporary mode", () => {
                var p = new FakePlatform(); p.Original.ACP = "65001"; var f = Run("utf8", p, true, 30, 5); Check(Stage(f) == "failed" && p.ApplyCount == 0 && p.GuardCount == 0, "Unsupported UTF-8 modified");
            });
            Test("invalid persisted snapshots rejected before registry writes", () => {
                var good = new FakePlatform().Original; WindowsPlatform.ValidateSnapshot(good); good.ACP = "bad";
                bool rejected = false; try { WindowsPlatform.ValidateSnapshot(good); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Invalid snapshot accepted");
            });
            Test("native process identity checks reject reused or incorrect creation timestamps", () => {
                var p = System.Diagnostics.Process.GetCurrentProcess(); Check(Native.SameProcess(p.Id, p.StartTime.ToUniversalTime().Ticks), "Own process not found"); Check(!Native.SameProcess(p.Id, 1), "PID only comparison");
            });
            Test("argument quoting preserves paths with spaces and trailing slashes", () => {
                Check(Native.Quote(@"D:\My Game\") == "\"D:\\My Game\\\\\"", "Trailing slash quoting broken");
            });
            Test("native Windows job terminates only its test child when its owner handle closes", () => {
                string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                var start = new System.Diagnostics.ProcessStartInfo(ps, "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60") { UseShellExecute = false, CreateNoWindow = true };
                using (var child = System.Diagnostics.Process.Start(start))
                {
                    using (var job = new ProcessJob()) { job.Add(child); }
                    Check(child.WaitForExit(5000), "Orphan child survived job close");
                }
            });
            Test("read-only identity lookup works for an already running elevated game when accessible", () => {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("LaTaleClient"))
                    using (process)
                    {
                        if (Native.ProcessPath(process.Id) == null) continue;
                        long ticks = process.StartTime.ToUniversalTime().Ticks;
                        Check(Native.SameProcess(process.Id, ticks), "Elevated game misreported as exited");
                    }
            });
            Test("corrupt or missing recovery records cannot be silently marked restored", () => {
                var f = new SessionFiles(Path.Combine(root, "corrupt-record"));
                File.WriteAllText(f.FilePath("recovery.json"), "{broken");
                Check(SessionFiles.NeedsRecovery(f.DirectoryPath), "Corrupt record ignored");
                var p = new FakePlatform(); Check(!LaunchEngine.RestorePending(f, p, null), "Missing backup reported as success"); Check(p.RestoreCount == 0, "Guessed original locale");
                File.WriteAllText(f.FilePath("recovery.json"), "{}"); Check(SessionFiles.NeedsRecovery(f.DirectoryPath), "Incomplete record ignored");
            });
            Test("atomic state writes tolerate temporary file locks without losing the intended value", () => {
                string path = Path.Combine(root, "locked-state.json");
                JsonFile.Write(path, new Preferences { WaitMinutes = 30 });
                var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = System.Threading.Tasks.Task.Run(() => { System.Threading.Thread.Sleep(150); locked.Dispose(); });
                try { JsonFile.Write(path, new Preferences { WaitMinutes = 60 }); }
                finally { release.Wait(); locked.Dispose(); }
                Check(JsonFile.Read<Preferences>(path).WaitMinutes == 60, "Atomic update lost after a file lock");
            });
            RegionTests.Run(Path.Combine(root, "regions"), Test);
            File.WriteAllLines(Path.Combine(root, "results.txt"), results);
            Console.WriteLine("All " + results.Count + " checks passed; no real locale writes or game launches.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); return 1; }
    }
}
