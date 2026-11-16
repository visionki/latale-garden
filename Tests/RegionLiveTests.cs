// Opt-in elevated integration test. Runs the actual release EXE and always restores the baseline.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LaTaleGarden;

public static class RegionLiveTests
{
    static string root, sessions;
    static WindowsPlatform platform = new WindowsPlatform(null, null);
    static void Log(string message) { File.AppendAllText(Path.Combine(root, "results.txt"), DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Log("PASS " + message); }
    static SessionFiles NewFiles(string parent) { return new SessionFiles(Path.Combine(parent, Guid.NewGuid().ToString("N"))); }
    static void WaitGuard(SessionFiles files)
    {
        string ready = files.FilePath("guard.ready");
        if (!File.Exists(ready)) return;
        int pid = int.Parse(File.ReadAllText(ready));
        try { using (var guard = Process.GetProcessById(pid)) { if (!guard.WaitForExit(20000)) throw new TimeoutException("Guard has not exited"); } }
        catch (ArgumentException) { }
    }
    static SessionFiles Execute(string operation, string name, SessionFiles source)
    {
        var files = NewFiles(sessions);
        using (var me = Process.GetCurrentProcess())
            JsonFile.Write(files.FilePath("region-request.json"), new RegionRequest {
                Operation = operation, LocaleName = name, Source = source == null ? null : RegionCatalog.Reference(source.DirectoryPath),
                ExpectedCurrent = platform.CaptureLocale(), AcknowledgedPending = RegionCatalog.Pending(sessions),
                OwnerPid = me.Id, OwnerStartTicks = me.StartTime.ToUniversalTime().Ticks
            });
        using (var child = Process.Start(new ProcessStartInfo(AppPaths.Executable, "--region " + Native.Quote(files.DirectoryPath)) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (!child.WaitForExit(90000)) { child.Kill(); child.WaitForExit(10000); WaitGuard(files); throw new TimeoutException("Region worker timed out"); }
            WaitGuard(files);
            var status = JsonFile.Read<SessionStatus>(files.FilePath("status.json"));
            Check(child.ExitCode == 0 && status.Stage == "region-done", operation + " " + name + " worker completed: " + status.Message);
        }
        Log("Configured: " + Describe(platform.CaptureLocale()));
        var report = RegionDiagnostics.Read();
        Log("Fresh process: " + (report.Probe == null ? report.ProbeError : "ACP=" + report.Probe.ACP + " OEM=" + report.Probe.OEMCP + " LCID=" + report.Probe.LCID.ToString("x4")));
        return files;
    }
    static string Describe(LocaleSnapshot s) { return s.LocaleName + " Default=" + s.DefaultLanguage + " ACP=" + s.ACP + " OEM=" + s.OEMCP + " Mac=" + s.MACCP; }
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--allow-system-locale-changes") return 2;
        root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root); sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
        SessionFiles backstop = null; Journal baselineJournal = null; LocaleSnapshot baseline = null;
        bool failed = false;
        try
        {
            if (!Native.IsAdmin) throw new InvalidOperationException("Administrator token required");
            if (WindowsPlatform.AnyGameRunning() || RegionDiagnostics.WriteBusy()) throw new InvalidOperationException("Close the game and finish other region operations first");
            if (RegionCatalog.Pending(AppPaths.SessionsRoot).Length != 0) throw new InvalidOperationException("Resolve existing user recovery records before this test");
            baseline = platform.CaptureLocale();
            if (baseline.ACP == "65001") throw new InvalidOperationException("UTF-8 configuration is excluded");
            Log("BASELINE " + Describe(baseline)); JsonFile.Write(Path.Combine(root, "baseline.json"), baseline);
            backstop = NewFiles(Path.Combine(root, "backstop"));
            using (var me = Process.GetCurrentProcess()) baselineJournal = new Journal { Id = Path.GetFileName(backstop.DirectoryPath), Original = baseline, Operation = "launch", Pending = true, CreatedUtc = DateTime.UtcNow, WorkerPid = me.Id, WorkerStartTicks = me.StartTime.ToUniversalTime().Ticks };
            backstop.SaveJournal(baselineJournal);
            new WindowsPlatform(backstop, null).StartGuard();
            var source = NewFiles(sessions);
            source.SaveJournal(new Journal { Id = Path.GetFileName(source.DirectoryPath), Original = baseline, Pending = false, CreatedUtc = DateTime.UtcNow });
            string sourceHash = RegionCatalog.Hash(source.FilePath("recovery.json"));
            var first = Execute("restore", null, source);
            Check(File.ReadAllText(first.FilePath("launch.log")).Contains("恢复配置，第 1 次"), "already matching completed backup was actually reapplied");
            Execute("set", "zh-TW", null);
            Check(RegionCatalog.Same(platform.CaptureLocale(), RegionCatalog.Preset("zh-TW")), "Traditional Taiwan configuration persists after worker and guard exit");
            Execute("set", "zh-CN", null);
            Check(RegionCatalog.Same(platform.CaptureLocale(), RegionCatalog.Preset("zh-CN")), "Simplified China configuration persists after worker and guard exit");
            Execute("restore", null, source);
            Check(RegionCatalog.Same(platform.CaptureLocale(), baseline), "all original configuration values restored");
            Check(sourceHash == RegionCatalog.Hash(source.FilePath("recovery.json")), "completed backup file remains byte-identical");
            // Deliberately end only a test helper. A real production guard must restore the saved intent.
            Execute("set", "zh-TW", null);
            var interrupted = NewFiles(sessions);
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            using (var owner = Process.Start(new ProcessStartInfo(ps, "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60") { UseShellExecute = false, CreateNoWindow = true }))
            {
                interrupted.SaveJournal(new Journal { Id = Path.GetFileName(interrupted.DirectoryPath), Original = platform.CaptureLocale(), Target = baseline, Operation = "restore", Pending = true, CreatedUtc = DateTime.UtcNow, WorkerPid = owner.Id, WorkerStartTicks = owner.StartTime.ToUniversalTime().Ticks });
                using (var guard = Process.Start(new ProcessStartInfo(AppPaths.Executable, "--guard " + Native.Quote(interrupted.DirectoryPath) + " " + owner.Id + " " + owner.StartTime.ToUniversalTime().Ticks) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    try {
                        var wait = Stopwatch.StartNew();
                        while (!File.Exists(interrupted.FilePath("guard.ready"))) { if (guard.HasExited || wait.ElapsedMilliseconds > 12000) throw new IOException("Crash guard did not become ready"); Thread.Sleep(100); }
                    }
                    finally { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(5000); } }
                    if (!guard.WaitForExit(90000)) throw new TimeoutException("Crash recovery guard timed out");
                    Check(guard.ExitCode == 0 && !interrupted.ReadJournal().Pending && interrupted.ReadJournal().Committed && RegionCatalog.Same(platform.CaptureLocale(), baseline), "production guard restores intended backup after test owner termination");
                }
            }
        }
        catch (Exception ex) { failed = true; Log("FAIL " + ex); }
        finally
        {
            if (backstop != null && baselineJournal != null)
            {
                try {
                    using (var mutex = new Mutex(false, Program.TransactionMutex)) {
                        if (!Program.TryLock(mutex, 60000)) throw new TimeoutException("Cannot lock final restore; independent backstop remains armed");
                        try { if (!LaunchEngine.RestorePending(backstop, new WindowsPlatform(backstop, null), baselineJournal) || !platform.Matches(baseline)) throw new IOException("Final restore failed; independent backstop remains armed"); }
                        finally { mutex.ReleaseMutex(); }
                    }
                    Log("FINAL BASELINE VERIFIED " + Describe(platform.CaptureLocale()));
                }
                catch (Exception ex) { failed = true; Log("FINAL RESTORE ERROR " + ex); }
            }
            File.WriteAllText(Path.Combine(root, "complete.txt"), failed ? "FAIL" : "PASS");
        }
        return failed ? 1 : 0;
    }
}
