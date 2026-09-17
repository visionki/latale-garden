using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using LaTaleGarden;

public static class UpdateInstallTests
{
    static string root, source, healthy, failed, healthyFeed, failedFeed;
    static List<string> lines = new List<string>();
    static List<string> jobs = new List<string>();
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Test(string name, Action action) { action(); lines.Add("PASS " + name); Console.WriteLine(lines.Last()); }
    static void StopCopy(string target)
    {
        foreach (var process in Process.GetProcesses())
            using (process)
                if (Native.SamePath(Native.ProcessPath(process.Id), target))
                {
                    try { process.CloseMainWindow(); if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(5000); } } catch (InvalidOperationException) { }
                }
    }
    static void RunCase(string name, bool failure, bool tamper, bool lockLocale, bool lockFile)
    {
        string targetDir = Path.Combine(root, name, "中文 与 空格");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "我的彩虹岛启动器.exe");
        File.Copy(source, target);
        string originalHash = UpdateProtocol.Hash(target);
        string id = Guid.NewGuid().ToString("N"), directory = UpdateInstaller.JobPath(id);
        jobs.Add(directory); Directory.CreateDirectory(directory);
        File.Copy(source, Path.Combine(directory, "helper.exe"));
        File.Copy(failure ? failed : healthy, Path.Combine(directory, "new.exe"));
        File.Copy(failure ? failedFeed : healthyFeed, Path.Combine(directory, "feed.json"));
        JsonFile.Write(Path.Combine(directory, "job.json"), new UpdateJob {
            Target = target, OriginalHash = originalHash, Version = "9.0.0", Nonce = Guid.NewGuid().ToString("N"),
            ParentPid = int.MaxValue - 1, ParentTicks = 1, CreatedUtc = DateTime.UtcNow
        });
        if (tamper) using (var file = File.Open(Path.Combine(directory, "new.exe"), FileMode.Open, FileAccess.Write)) file.WriteByte(0);
        Mutex locale = null, gui = null; FileStream locked = null; Process helper = null;
        try
        {
            if (lockLocale) { locale = new Mutex(false, Program.TransactionMutex); Check(Program.TryLock(locale, 0), "Locale mutex busy before test"); }
            // Failure cases must not activate a user window when the helper restarts the old app.
            if (failure || tamper || lockLocale || lockFile)
            {
                gui = new Mutex(false, @"Local\LaTaleGarden.GUI." + WindowsIdentity.GetCurrent().User.Value);
                Check(Program.TryLock(gui, 0), "Close the real launcher before running installer tests");
            }
            if (lockFile) locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            helper = UpdateInstaller.StartHelper(id, false);
            Check(helper.WaitForExit(60000), "Updater timed out");
            var status = UpdateInstaller.Status(id);
            Check(status != null, "Missing update status");
            if (failure)
            {
                Check(status.Stage == "rolled-back", "Expected rollback: " + status.Message);
                Check(UpdateProtocol.Hash(target) == originalHash, "Original not restored");
                Check(UpdateProtocol.Hash(Path.Combine(directory, "previous.exe")) == originalHash, "Backup damaged");
            }
            else if (tamper || lockLocale || lockFile)
            {
                Check(status.Stage == "failed", "Expected safe failure: " + status.Message);
                Check(UpdateProtocol.Hash(target) == originalHash, "Original modified after rejected update");
            }
            else
            {
                Check(status.Stage == "confirmed" && helper.ExitCode == 0, "Update was not confirmed: " + status.Message);
                Check(UpdateProtocol.Hash(target) == UpdateProtocol.Hash(healthy), "Wrong new executable");
                Check(UpdateProtocol.Hash(Path.Combine(directory, "previous.exe")) == originalHash, "Missing original backup");
                var receipt = JsonFile.Read<UpdateReceipt>(Path.Combine(directory, "healthy.json"));
                Check(Native.SameProcess(receipt.Pid, receipt.Ticks) && Native.SamePath(Native.ProcessPath(receipt.Pid), target), "New UI did not acknowledge its actual process");
            }
            Check(Directory.GetFiles(targetDir).Length == 1, "Adjacent staging file was left behind");
        }
        finally
        {
            if (locked != null) locked.Dispose();
            if (helper != null) { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(5000); } helper.Dispose(); }
            Thread.Sleep(500); StopCopy(target);
            if (locale != null) { locale.ReleaseMutex(); locale.Dispose(); }
            if (gui != null) { gui.ReleaseMutex(); gui.Dispose(); }
        }
    }
    public static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]); source = args[1]; healthy = args[2]; failed = args[3]; healthyFeed = args[4]; failedFeed = args[5];
        byte[] preferences = File.Exists(UpdatePaths.Settings) ? File.ReadAllBytes(UpdatePaths.Settings) : null;
        string gameSettingsHash = File.Exists(AppPaths.PreferencesPath) ? UpdateProtocol.Hash(AppPaths.PreferencesPath) : null;
        try
        {
            UpdateInstaller.CheckIdle(); Check(!UpdateInstaller.Applying(), "An actual update is running");
            using (var gui = new Mutex(false, @"Local\LaTaleGarden.GUI." + WindowsIdentity.GetCurrent().User.Value))
            { Check(Program.TryLock(gui, 0), "Close the real launcher before running installer tests"); gui.ReleaseMutex(); }
            JsonFile.Write(UpdatePaths.Settings, new UpdatePreferences { AutoCheck = false, ShowAnnouncements = false });
            Test("real WPF update replaces a renamed EXE in a Unicode path and confirms the new UI", () => RunCase("real-ui", false, false, false, false));
            Test("failed new process restores the exact previous EXE", () => RunCase("rollback", true, false, false, false));
            Test("modified staged package is rejected before replacing the old EXE", () => RunCase("tamper", false, true, false, false));
            Test("active locale transaction prevents replacement without changing the original", () => RunCase("locale-lock", false, false, true, false));
            Test("locked executable safely fails and keeps the original", () => RunCase("file-lock", false, false, false, true));
            Test("game preferences remain byte-for-byte unchanged", () => Check((File.Exists(AppPaths.PreferencesPath) ? UpdateProtocol.Hash(AppPaths.PreferencesPath) : null) == gameSettingsHash, "Game preferences changed"));
            File.WriteAllLines(Path.Combine(root, "results.txt"), lines);
            return 0;
        }
        catch (Exception ex) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (preferences != null) File.WriteAllBytes(UpdatePaths.Settings, preferences); else if (File.Exists(UpdatePaths.Settings)) File.Delete(UpdatePaths.Settings);
            // Copy test evidence before removing only the exact GUID jobs created by this run.
            foreach (var job in jobs)
            {
                string id = Path.GetFileName(job);
                if (!Native.SamePath(job, UpdateInstaller.JobPath(id))) continue;
                string evidence = Path.Combine(root, "jobs", id); Directory.CreateDirectory(evidence);
                foreach (var file in Directory.GetFiles(job, "*.json")) File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)), true);
                try { Directory.Delete(job, true); } catch { }
            }
        }
    }
}
