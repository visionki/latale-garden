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
    public bool PostRestoreProbeFailure;
    public bool TemporaryLocaleSupported = true;
    public int GameCheckCount, DirectCount, CloseUpdaterCount;
    public bool Updating, CloseFailure, DirectFailure, EarlyDirectExit, UpdaterAutoRun;
    public readonly List<string> Calls = new List<string>();
    public readonly Queue<GameLaunchPlan> Plans = new Queue<GameLaunchPlan>();
    public readonly Queue<string> UpdateActions = new Queue<string>();
    public Action OnCheck;
    public Action OnCloseUpdater;
    public Func<GameLaunchPlan> GameCheckOverride;
    public GameLaunchPlan LastDirectPlan;
    public bool SupportsTemporaryLocale { get { return TemporaryLocaleSupported; } }
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
    public void ApplyTraditional() { Calls.Add("apply"); ApplyCount++; ApplyAt = Now; if (ApplyFailure) throw new IOException("Partially applied locale"); }
    public void Restore(LocaleSnapshot original) { RestoreCount++; if (RestoreCount <= RestoreFailures) throw new IOException("Restore denied"); Restored = original; RestoreAt = Now; }
    public bool Matches(LocaleSnapshot original) { return !BadVerification && Restored == original; }
    public GameLaunchPlan CheckGameUpdate(string directory) { Calls.Add("check"); GameCheckCount++; if (OnCheck != null) OnCheck(); var result = GameCheckOverride != null ? GameCheckOverride() : Plans.Count > 0 ? Plans.Dequeue() : GameLaunchPlan.Official("Use official"); if (result.State == GameUpdateState.NeedsUpdate) Updating = true; return result; }
    public void StartOfficialLauncher(string directory) { Calls.Add("official"); LaunchCount++; if (LaunchFailure) throw new IOException("Launch failed"); }
    public void CloseOfficialUpdater(string directory) { Calls.Add("close"); CloseUpdaterCount++; if (CloseFailure) throw new IOException("Cannot close official updater"); Updating = false; if (OnCloseUpdater != null) OnCloseUpdater(); }
    public void StartDirectClient(string directory, GameLaunchPlan plan) { Calls.Add("direct"); DirectCount++; LastDirectPlan = plan; Updating = false; if (DirectFailure) throw new IOException("Direct launch failed"); }
    public bool DirectClientExited { get { return EarlyDirectExit && DirectCount > 0; } }
    public string ReadUpdateAction() { return UpdateActions.Count > 0 ? UpdateActions.Dequeue() : ""; }
    public ClientInfo FindClient(string directory)
    {
        if (NeverClient || Now < 2 || (Updating && !UpdaterAutoRun)) return null;
        return new ClientInfo { Id = 100, StartTicks = 1000, HasWindow = !NeverWindow && Now >= 4 };
    }
    public void Probe() { if (PostRestoreProbeFailure && Restored != null) throw new IOException("Probe unavailable"); }
}
public static class EngineTests
{
    static string root;
    static List<string> results = new List<string>();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static SessionFiles Run(string name, FakePlatform fake, bool compatibility, int wait, bool officialOnly = false)
    {
        var files = new SessionFiles(Path.Combine(root, name));
        var request = new LaunchRequest { Compatibility = compatibility, OfficialOnly = officialOnly, GameDirectory = @"D:\Fake LaTale", WaitSeconds = wait };
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
            Test("unsupported Windows rejects compatibility before backup, guard, system writes or launch", () => {
                var p = new FakePlatform { TemporaryLocaleSupported = false }; var f = Run("unsupported-compatibility", p, true, 40);
                Check(Stage(f) == "failed" && p.CaptureCount == 0 && p.GuardCount == 0 && p.ApplyCount == 0 && p.RestoreCount == 0 && p.LaunchCount == 0 && f.ReadJournal() == null, "Unsupported system reached locale preparation");
                Check(JsonFile.Read<SessionStatus>(f.FilePath("status.json")).Message.Contains("Windows 11"), "Missing supported-system explanation");
            });
            Test("unsupported Windows can use standard launch without locale changes", () => {
                var p = new FakePlatform { TemporaryLocaleSupported = false }; var f = Run("unsupported-standard", p, false, 40);
                Check(Stage(f) == "client-started" && p.LaunchCount == 1 && p.ApplyCount == 0 && p.RestoreCount == 0 && p.CaptureCount == 0 && p.GuardCount == 0, "Standard launch blocked or changed locale");
            });
            Test("unsupported Windows still recovers a previous pending locale transaction", () => {
                var p = new FakePlatform { RestoreFailures = 50 }; var f = Run("unsupported-recovery", p, true, 40);
                Check(Stage(f) == "recovery" && f.ReadJournal().Pending, "Missing pending transaction");
                var recovery = new FakePlatform { TemporaryLocaleSupported = false };
                Check(LaunchEngine.RestorePending(f, recovery, f.ReadJournal()) && recovery.RestoreCount == 1 && !f.ReadJournal().Pending, "OS restriction blocked emergency recovery");
            });
            Test("default launch restores original Japanese locale at first client detection", () => {
                var p = new FakePlatform(); var f = Run("success", p, true, 40);
                Check(Stage(f) == "client-started", "Client not detected"); Check(p.RestoreAt == 2, "Restoration waited after client detection"); Check(p.Restored.LocaleName == "ja-JP", "Hardcoded original locale"); Check(!f.ReadJournal().Pending, "Journal still pending");
            });
            Test("standard mode never captures or modifies locale and never starts a guard", () => {
                var p = new FakePlatform(); var f = Run("standard", p, false, 40);
                Check(Stage(f) == "client-started" && p.ApplyCount == 0 && p.RestoreCount == 0 && p.CaptureCount == 0 && p.GuardCount == 0, "Locale operation in standard mode");
            });
            Test("cancel before preparation does not modify locale", () => {
                var p = new FakePlatform { CancelAt = 0 }; var f = Run("cancel-early", p, true, 40); Check(Stage(f) == "cancelled" && p.ApplyCount == 0, "Changed before cancellation");
            });
            Test("cancel while waiting restores once", () => {
                var p = new FakePlatform { CancelAt = 4, NeverClient = true }; var f = Run("cancel-wait", p, true, 40); Check(Stage(f) == "cancelled" && p.RestoreCount == 1 && !f.ReadJournal().Pending, "Not restored on cancel");
            });
            Test("GUI process exit triggers restoration", () => {
                var p = new FakePlatform { OwnerDiesAt = 5, NeverClient = true }; var f = Run("owner-exit", p, true, 40); Check(Stage(f) == "cancelled" && p.RestoreCount == 1, "GUI death not handled");
            });
            Test("timeout restores original settings", () => {
                var p = new FakePlatform { NeverClient = true }; var f = Run("timeout", p, true, 6); Check(Stage(f) == "failed" && p.RestoreCount == 1 && !f.ReadJournal().Pending, "Timeout leaked locale");
            });
            Test("invalid game and existing game stop before any locale operation", () => {
                foreach (var p in new[] { new FakePlatform { InvalidPath = true }, new FakePlatform { OldGame = true } }) { var f = Run("preflight-" + p.InvalidPath, p, true, 40); Check(Stage(f) == "failed" && p.CaptureCount == 0 && p.ApplyCount == 0, "Unsafe preflight"); }
            });
            Test("watchdog handshake failure prevents locale modification", () => {
                var p = new FakePlatform { GuardFailure = true }; var f = Run("guard-fail", p, true, 40); Check(Stage(f) == "failed" && p.ApplyCount == 0 && !f.ReadJournal().Pending, "Modified without guard");
            });
            Test("partial locale-setting failure rolls back and never starts official launcher", () => {
                var p = new FakePlatform { ApplyFailure = true }; var f = Run("apply-fail", p, true, 40); Check(Stage(f) == "failed" && p.RestoreCount == 1 && p.LaunchCount == 0, "Partial write leaked");
            });
            Test("official launcher failure rolls back", () => {
                var p = new FakePlatform { LaunchFailure = true }; var f = Run("launch-fail", p, true, 40); Check(Stage(f) == "failed" && p.RestoreCount == 1, "Launch error leaked");
            });
            Test("transient restore errors retry and verify before clearing journal", () => {
                var p = new FakePlatform { RestoreFailures = 2 }; var f = Run("restore-retry", p, true, 40); Check(Stage(f) == "client-started" && p.RestoreCount == 3 && !f.ReadJournal().Pending, "Retry failed");
            });
            Test("permanent restore failure retains journal and produces recoverable state", () => {
                var p = new FakePlatform { RestoreFailures = 50 }; var f = Run("restore-fail", p, true, 40); Check(Stage(f) == "recovery" && f.ReadJournal().Pending, "Lost recovery journal");
                var restart = new FakePlatform(); Check(LaunchEngine.RestorePending(f, restart, f.ReadJournal()), "Persisted recovery failed"); Check(!f.ReadJournal().Pending && restart.Restored.LocaleName == "ja-JP", "Incorrect restart recovery");
            });
            Test("read-back mismatch cannot be reported as restored", () => {
                var p = new FakePlatform { BadVerification = true }; var f = Run("verify-fail", p, true, 40); Check(Stage(f) == "recovery" && f.ReadJournal().Pending && p.RestoreCount == 3, "Unverified success");
            });
            Test("client without any window completes restoration without claiming window readiness", () => {
                var p = new FakePlatform { NeverWindow = true }; var f = Run("no-window", p, true, 12);
                var status = JsonFile.Read<SessionStatus>(f.FilePath("status.json"));
                Check(status.Stage == "client-started" && status.Finished && p.RestoreAt == 2 && p.RestoreCount == 1 && !status.Message.Contains("窗口已就绪"), "Client detection waited for a window or claimed visual validation");
            });
            Test("UTF-8 configuration is left intact and clearly unsupported by temporary mode", () => {
                var p = new FakePlatform(); p.Original.ACP = "65001"; var f = Run("utf8", p, true, 30); Check(Stage(f) == "failed" && p.ApplyCount == 0 && p.GuardCount == 0, "Unsupported UTF-8 modified");
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
            Test("direct launch restores at first client detection even without a window", () => {
                var p = new FakePlatform { NeverWindow = true }; p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current });
                var f = Run("immediate-no-window", p, true, 40, false);
                Check(Stage(f) == "client-started" && p.RestoreAt == 2 && p.RestoreCount == 1 && p.GuardCount == 1 && !f.ReadJournal().Pending && p.Restored.LocaleName == "ja-JP", "Immediate restoration waited or lost original locale");
                Check(JsonFile.Read<SessionStatus>(f.FilePath("status.json")).Finished && !JsonFile.Read<SessionStatus>(f.FilePath("status.json")).Message.Contains("窗口已就绪"), "Process detection claimed a ready game window");
            });
            Test("official launch restores immediately and preserves failed recovery records", () => {
                var official = new FakePlatform { NeverWindow = true }; var f = Run("immediate-official", official, true, 40, true);
                Check(Stage(f) == "client-started" && official.RestoreAt == 2 && official.LaunchCount == 1, "Official child did not restore immediately");
                var failure = new FakePlatform { NeverWindow = true, RestoreFailures = 50 }; f = Run("immediate-recovery", failure, true, 40, false);
                Check(Stage(f) == "recovery" && f.ReadJournal().Pending && failure.RestoreCount == 3, "Immediate restore failure lost recovery intent");
            });
            Test("direct standard launch never changes system locale or waits for a window", () => {
                var p = new FakePlatform { NeverWindow = true }; p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current });
                var f = Run("immediate-standard", p, false, 40, false);
                Check(Stage(f) == "client-started" && p.Now == 2 && p.CaptureCount == 0 && p.GuardCount == 0 && p.RestoreCount == 0, "Standard mode touched locale or waited for a window");
            });
            Test("legacy request flags cannot re-enable initialization waiting", () => {
                var p = new FakePlatform { NeverWindow = true };
                var f = new SessionFiles(Path.Combine(root, "legacy-request"));
                File.WriteAllText(f.FilePath("request.json"), "{\"Compatibility\":true,\"WaitSeconds\":40,\"SettleSeconds\":120,\"RestoreOnClientDetected\":false,\"GameDirectory\":\"D:\\\\Fake LaTale\"}");
                new LaunchEngine(f, p).Run(JsonFile.Read<LaunchRequest>(f.FilePath("request.json")));
                Check(Stage(f) == "client-started" && p.RestoreAt == 2 && !f.ReadJournal().Pending, "Legacy request restored the old waiting behavior");
            });
            Test("legacy preferences retain game and user options while dropping obsolete waiting", () => {
                string path = Path.Combine(root, "legacy-settings.json");
                File.WriteAllText(path, "{\"Compatibility\":false,\"GameDirectory\":\"D:\\\\Fake LaTale\",\"MinimizeAfterStart\":true,\"SettleSeconds\":120,\"WaitMinutes\":60}");
                var preferences = JsonFile.Read<Preferences>(path); preferences.Normalize(); JsonFile.Write(path, preferences);
                preferences = JsonFile.Read<Preferences>(path);
                Check(preferences.GameDirectory == @"D:\Fake LaTale" && !preferences.Compatibility && preferences.MinimizeAfterStart && preferences.WaitMinutes == 60 && !File.ReadAllText(path).Contains("SettleSeconds"), "Preferences migration lost user choices or retained waiting");
            });
            Test("post-restoration diagnostic failure cannot undo successful recovery", () => {
                var p = new FakePlatform { PostRestoreProbeFailure = true };
                var f = Run("post-restore-probe-failure", p, true, 40);
                Check(Stage(f) == "client-started" && !f.ReadJournal().Pending && p.RestoreCount == 1 && p.Restored.LocaleName == "ja-JP", "Optional diagnostics overrode verified restoration");
            });
            GameUpdateTests.Run(Path.Combine(root, "game-updates"), Test);
            Test("smart direct launch checks updates before any locale change and retains restoration", () => {
                var p = new FakePlatform(); p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current });
                p.OnCheck = () => { Check(p.ApplyCount == 0 && p.GuardCount == 0, "Changed locale during game update check"); p.Now += 3; };
                var f = Run("smart-direct", p, true, 40);
                Check(Stage(f) == "client-started" && p.DirectCount == 1 && p.LaunchCount == 0 && p.RestoreCount == 1 && p.Calls.IndexOf("check") < p.Calls.IndexOf("apply"), "Direct launch or restoration failed");
            });
            Test("manual official launch bypasses metadata and preserves the original workflow", () => {
                var p = new FakePlatform(); var f = Run("manual-official", p, true, 40, true);
                Check(Stage(f) == "client-started" && p.GameCheckCount == 0 && p.DirectCount == 0 && p.LaunchCount == 1 && p.RestoreCount == 1, "Manual official path changed");
            });
            Test("official update confirmation checks files, closes the updater and rechecks before locale writes", () => {
                var p = new FakePlatform(); p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.NeedsUpdate });
                p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current }); p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current }); p.UpdateActions.Enqueue("complete");
                var f = Run("update-complete", p, true, 40);
                Check(Stage(f) == "client-started" && p.DirectCount == 1 && p.CloseUpdaterCount == 1 && p.LaunchCount == 1 && p.GameCheckCount == 3 && p.ApplyCount == 1 && p.RestoreCount == 1, "Update confirmation sequence failed");
                Check(string.Join(",", p.Calls).StartsWith("check,official,check,close,check,apply,direct"), "System locale changed before patching and verification completed");
            });
            Test("No after an incomplete update keeps the official launcher and waits for another confirmation", () => {
                var p = new FakePlatform(); foreach (var state in new[] { GameUpdateState.NeedsUpdate, GameUpdateState.NeedsUpdate, GameUpdateState.Current, GameUpdateState.Current }) p.Plans.Enqueue(new GameLaunchPlan { State = state });
                foreach (string action in new[] { "complete", "keep", "complete" }) p.UpdateActions.Enqueue(action);
                var f = Run("update-keep", p, true, 40);
                Check(Stage(f) == "client-started" && p.LaunchCount == 1 && p.CloseUpdaterCount == 1 && p.GameCheckCount == 4 && p.ApplyCount == 1, "No restarted or closed updater");
                Check(p.Calls.IndexOf("close") > p.Calls.LastIndexOf("check") - 2, "Updater closed on failed first confirmation");
            });
            Test("unknown post-update launch checks retain their reason and permit another confirmation", () => {
                var p = new FakePlatform();
                p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.NeedsUpdate }); p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current });
                p.Plans.Enqueue(GameLaunchPlan.Official("显示设置暂时无法读取。"));
                p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current }); p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current });
                foreach (string action in new[] { "complete", "keep", "complete" }) p.UpdateActions.Enqueue(action);
                var f = Run("update-unknown-then-retry", p, true, 40);
                string log = File.ReadAllText(f.FilePath("launch.log"));
                Check(Stage(f) == "client-started" && p.LaunchCount == 1 && p.GameCheckCount == 5 && p.ApplyCount == 1 && p.RestoreCount == 1 && !f.ReadJournal().Pending, "Unconfirmed launch check prevented a fresh confirmation or lost restoration");
                Check(log.Contains("暂时无法完成启动检查。显示设置暂时无法读取。") && !log.Contains("尚未更新到最新版"), "Unknown launch check was mislabeled as an outdated game");
            });
            Test("Yes after an incomplete update closes the current updater before restarting", () => {
                var p = new FakePlatform(); foreach (var state in new[] { GameUpdateState.NeedsUpdate, GameUpdateState.NeedsUpdate, GameUpdateState.Current, GameUpdateState.Current }) p.Plans.Enqueue(new GameLaunchPlan { State = state });
                foreach (string action in new[] { "complete", "restart", "complete" }) p.UpdateActions.Enqueue(action);
                var f = Run("update-restart", p, true, 40);
                Check(Stage(f) == "client-started" && p.LaunchCount == 2 && p.CloseUpdaterCount == 2 && p.ApplyCount == 1 && p.RestoreCount == 1, "Restart sequence failed");
                Check(string.Join(",", p.Calls).StartsWith("check,official,check,close,official,check,close,check,apply,direct"), "Restart was not ordered safely");
            });
            Test("cancel or timeout during official updating performs no locale writes and leaves patcher running", () => {
                foreach (bool cancel in new[] { true, false }) {
                    var p = new FakePlatform { CancelAt = cancel ? 2 : double.MaxValue }; p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.NeedsUpdate });
                    var f = Run("update-stop-" + cancel, p, true, 4);
                    Check(Stage(f) == (cancel ? "cancelled" : "failed") && p.ApplyCount == 0 && p.GuardCount == 0 && p.CloseUpdaterCount == 0 && p.RestoreCount == 0, "Update wait changed locale or killed patcher");
                }
            });
            Test("failure to close the updater prevents locale writes and game launch", () => {
                var p = new FakePlatform { CloseFailure = true }; p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.NeedsUpdate }); p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current }); p.UpdateActions.Enqueue("complete");
                var f = Run("update-close-fail", p, true, 40); Check(Stage(f) == "failed" && p.DirectCount == 0 && p.ApplyCount == 0, "Continued after updater close failure");
            });
            Test("direct launch failure or early exit restores locale without opening a second client", () => {
                foreach (bool beforeStart in new[] { true, false }) {
                    var p = new FakePlatform { DirectFailure = beforeStart, EarlyDirectExit = !beforeStart }; p.Plans.Enqueue(new GameLaunchPlan { State = GameUpdateState.Current });
                    var f = Run("direct-fail-" + beforeStart, p, true, 40); Check(Stage(f) == "failed" && p.RestoreCount == 1 && p.LaunchCount == 0, "Direct failure leaked locale or spawned official launcher");
                }
            });
            File.WriteAllLines(Path.Combine(root, "results.txt"), results);
            Console.WriteLine("All " + results.Count + " checks passed; no real locale writes or game launches.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); return 1; }
    }
}
