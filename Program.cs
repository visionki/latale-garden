using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;

namespace LaTaleGarden
{
    public static class Program
    {
        public const string TransactionMutex = @"Global\LaTaleGarden.LocaleTransaction.v1";
        public const string Version = "1.1.4";
        public const string ReleaseChannel = "stable";
        public static string StartupUpdateId;
        public const string Author = "visionki";
        public const string AuthorUrl = "https://github.com/visionki";
        public const string ProjectUrl = AuthorUrl + "/latale-garden";
        public const string IssuesUrl = ProjectUrl + "/issues";
        public static string ActivationEventName { get { return @"Local\LaTaleGarden.Activate." + WindowsIdentity.GetCurrent().User.Value; } }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                // All runtime options live in the executable; no adjacent .config is needed.
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key == null || Convert.ToInt32(key.GetValue("Release", 0)) < 528040)
                        throw new NotSupportedException("请先安装 .NET Framework 4.8 或更高版本，再打开启动器。");
                }
                AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
                if (args.Length > 0 && args[0] == "--probe-json")
                {
                    var probe = new ProcessLocaleProbe { ACP = Native.GetACP(), OEMCP = Native.GetOEMCP(), LCID = Native.GetSystemDefaultLCID() };
                    using (var data = new MemoryStream()) { new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ProcessLocaleProbe)).WriteObject(data, probe); Console.Write(Encoding.UTF8.GetString(data.ToArray())); }
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--probe")
                {
                    Console.WriteLine("GetACP=" + Native.GetACP() + "; GetSystemDefaultLCID=" + Native.GetSystemDefaultLCID());
                    return 0;
                }
                if (args.Length > 1 && args[0] == "--diagnostics")
                {
                    var data = new WindowsPlatform(new SessionFiles(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "diagnostic-session")), null).CaptureLocale();
                    File.WriteAllText(args[1], Native.OSLabel() + Environment.NewLine + "ConfiguredLocale=" + data.LocaleName + "; ACP=" + data.ACP + "; OEMCP=" + data.OEMCP + "; MACCP=" + data.MACCP + "; ProcessACP=" + data.RuntimeACP + Environment.NewLine + new WindowsPlatform(new SessionFiles(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "diagnostic-session")), null).ReadOnlyCommandCheck(), new UTF8Encoding(false));
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--apply-update") return UpdateInstaller.Apply(args[1]);
                if (args.Length >= 2 && (args[0] == "--worker" || args[0] == "--recover")) return RunWorker(args[1], args[0] == "--recover");
                if (args.Length == 2 && args[0] == "--region") return RunRegionWorker(args[1]);
                if (args.Length == 4 && args[0] == "--guard") return RunGuard(args[1], int.Parse(args[2]), long.Parse(args[3]));
                bool preview = args.Length >= 2 && args[0] == "--render-preview";
                if (args.Length == 2 && args[0] == "--updated") StartupUpdateId = args[1];
                else if (!preview)
                {
                    try { StartupUpdateId = UpdateInstaller.PendingStartup(); }
                    catch (Exception ex) { UpdatePaths.Log("未能读取上次更新记录：" + ex.Message); }
                }
                if (!preview && StartupUpdateId == null && UpdateInstaller.Applying())
                {
                    if (args.Length == 0) MessageBox.Show("启动器正在更新，请稍等片刻。", "LaTale Garden");
                    return 0;
                }
                string user = WindowsIdentity.GetCurrent().User.Value;
                using (var mutex = new Mutex(false, @"Local\LaTaleGarden.GUI." + user + (preview ? ".Preview" : "")))
                {
                    if (!TryLock(mutex, 0))
                    {
                        try { using (var activation = EventWaitHandle.OpenExisting(ActivationEventName)) activation.Set(); } catch { }
                        foreach (var other in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppPaths.Executable)))
                            using (other) if (other.Id != Process.GetCurrentProcess().Id && Native.VisibleWindow(other.Id) != IntPtr.Zero) Native.FocusProcess(other.Id, other.StartTime.ToUniversalTime().Ticks);
                        return 0;
                    }
                    try
                    {
                        var app = new Application();
                        app.DispatcherUnhandledException += (s, e) => {
                            MessageBox.Show("界面遇到错误：" + e.Exception.Message + "\n若启动流程正在进行，独立进程仍会负责恢复。", "彩虹岛启动器", MessageBoxButton.OK, MessageBoxImage.Error);
                            e.Handled = true;
                        };
                        return app.Run(new LauncherWindow(preview ? args[1] : null, preview && args.Length > 2 ? args[2] : "home"));
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataRoot);
                    File.AppendAllText(Path.Combine(AppPaths.DataRoot, "error.log"), DateTime.Now + " " + ex + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
                if (args.Length == 0) MessageBox.Show(ex.Message, "彩虹岛启动器", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        public static bool TryLock(Mutex mutex, int timeout)
        {
            try { return mutex.WaitOne(timeout); } catch (AbandonedMutexException) { return true; }
        }
        private static SessionFiles OpenSession(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            Guid id;
            if (!Guid.TryParseExact(Path.GetFileName(full), "N", out id)) throw new InvalidDataException("会话标识无效。");
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("启动会话不存在。");
            return new SessionFiles(full);
        }
        private static int RunWorker(string directory, bool recover)
        {
            var files = OpenSession(directory);
            if (!Native.IsAdmin) { files.Status("failed", "没有取得管理员权限，未执行区域切换。", true, null, 0); return 2; }
            using (var mutex = new Mutex(false, TransactionMutex))
            {
                if (!TryLock(mutex, 3000)) { files.Status("failed", "已有启动或恢复流程正在运行，请稍后再试。", true, null, 0); return 3; }
                try
                {
                    files.Log("辅助进程已取得管理员权限；" + Native.OSLabel());
                    if (recover)
                    {
                        Journal recovery = files.ReadJournal();
                        if (!RegionCatalog.ValidJournal(recovery, Path.GetFileName(files.DirectoryPath))) throw new InvalidDataException("恢复记录缺失或损坏，无法自动恢复。请进入区域管理，检查备份或手动选择区域。");
                        if (RegionCatalog.HasResolution(files.DirectoryPath)) { files.Status("region-done", "该记录已通过区域管理处理，请重新检测。", true, null, 0); return 0; }
                        if (RegionCatalog.OtherWorkerAlive(Path.GetDirectoryName(files.DirectoryPath), recovery.Id)) throw new InvalidOperationException("另一个辅助或恢复进程仍在运行，请稍后重试。");
                        using (var current = Process.GetCurrentProcess()) { recovery.WorkerPid = current.Id; recovery.WorkerStartTicks = current.StartTime.ToUniversalTime().Ticks; }
                        files.SaveJournal(recovery);
                        new WindowsPlatform(files, null).StartGuard();
                        files.Status("restoring", "正在恢复上次保存的系统区域…", false, null, 0);
                        bool restored = LaunchEngine.RestorePending(files, new WindowsPlatform(files, null), recovery);
                        if (restored) RegionCatalog.CompleteResolutions(files, recovery);
                        files.Status(restored ? "cancelled" : "recovery", restored ? "原区域设置已恢复，可以重新启动。" : "恢复未完成，请查看日志后重试。", true, null, 0);
                        return restored ? 0 : 4;
                    }
                    // Do not create a fresh transaction over a previous unfinished backup.
                    string parent = Path.GetDirectoryName(files.DirectoryPath);
                    foreach (string sibling in Directory.GetDirectories(parent))
                    {
                        if (SessionFiles.NeedsRecovery(sibling)) throw new InvalidOperationException("检测到未完成或损坏的恢复记录，请先处理原设置恢复。");
                    }
                    LaunchRequest request = JsonFile.Read<LaunchRequest>(files.FilePath("request.json"));
                    request.WaitSeconds = Math.Max(300, Math.Min(7200, request.WaitSeconds));
                    request.SettleSeconds = Math.Max(10, Math.Min(120, request.SettleSeconds));
                    new LaunchEngine(files, new WindowsPlatform(files, request)).Run(request);
                    var status = JsonFile.Read<SessionStatus>(files.FilePath("status.json"));
                    return status.Stage == "running" ? 0 : 1;
                }
                catch (Exception ex)
                {
                    files.Log("辅助进程异常：" + ex);
                    Journal journal = files.ReadJournal();
                    bool hasBrokenRecord = SessionFiles.NeedsRecovery(files.DirectoryPath) && !RegionCatalog.ValidJournal(journal, Path.GetFileName(files.DirectoryPath));
                    bool restored = !hasBrokenRecord && (journal == null || !journal.Pending || LaunchEngine.RestorePending(files, new WindowsPlatform(files, null), journal));
                    files.Status(restored ? "failed" : "recovery", ex.Message + (restored ? "" : " 原设置尚未恢复。"), true, null, 0);
                    return 5;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static int RunRegionWorker(string directory)
        {
            var files = OpenSession(directory);
            if (!Native.IsAdmin) { files.Status("region-failed", "没有取得管理员权限，未修改区域。", true, null, 0); return 2; }
            using (var mutex = new Mutex(false, TransactionMutex))
            {
                if (!TryLock(mutex, 3000)) { files.Status("region-failed", "已有启动或区域操作正在进行，请稍后重试。", true, null, 0); return 3; }
                try
                {
                    if (RegionCatalog.OtherWorkerAlive(Path.GetDirectoryName(files.DirectoryPath), Path.GetFileName(files.DirectoryPath))) throw new InvalidOperationException("另一个辅助或恢复进程仍在运行，请稍后重试。");
                    var request = JsonFile.Read<RegionRequest>(files.FilePath("region-request.json"));
                    files.Log("区域管理辅助进程；" + Native.OSLabel());
                    new RegionEngine(files, new WindowsPlatform(files, new LaunchRequest { OwnerPid = request.OwnerPid, OwnerStartTicks = request.OwnerStartTicks })).Run(request);
                    return JsonFile.Read<SessionStatus>(files.FilePath("status.json")).Stage == "region-done" ? 0 : 1;
                }
                catch (Exception ex)
                {
                    files.Log(ex.ToString());
                    var journal = files.ReadJournal();
                    bool restored = (!File.Exists(files.FilePath("recovery.json"))) || (RegionCatalog.ValidJournal(journal, Path.GetFileName(files.DirectoryPath)) && (!journal.Pending || LaunchEngine.RestorePending(files, new WindowsPlatform(files, null), journal)));
                    if (restored && journal != null) { try { RegionCatalog.CompleteResolutions(files, journal); } catch (Exception historyError) { files.Log(historyError.ToString()); } }
                    files.Status(restored ? "region-failed" : "recovery", ex.Message, true, null, 0);
                    return 5;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static int RunGuard(string directory, int workerPid, long workerTicks)
        {
            if (!Native.IsAdmin) return 2;
            var files = OpenSession(directory);
            File.WriteAllText(files.FilePath("guard.ready"), Process.GetCurrentProcess().Id.ToString());
            while (Native.SameProcess(workerPid, workerTicks)) Thread.Sleep(500);
            using (var mutex = new Mutex(false, TransactionMutex))
            {
                if (!TryLock(mutex, 15000)) { files.Log("恢复进程未取得事务锁，恢复记录仍保留。"); return 3; }
                try
                {
                    Journal journal = files.ReadJournal();
                    if (!RegionCatalog.ValidJournal(journal, Path.GetFileName(files.DirectoryPath)))
                    {
                        files.Log("独立恢复进程无法读取有效备份；未猜测原设置。");
                        files.Status("recovery", "恢复记录无法读取。请导出日志并检查原区域设置。", true, null, 0);
                        return 4;
                    }
                    if (RegionCatalog.HasResolution(files.DirectoryPath)) return 0;
                    if (!journal.Pending) { RegionCatalog.CompleteResolutions(files, journal); return 0; }
                    files.Log("独立恢复进程发现辅助进程退出且恢复未完成，接管恢复。");
                    try { files.Status("restoring", "独立进程正在恢复原区域设置…", false, null, 0); }
                    catch (Exception ex) { files.Log("状态写入失败，仍继续恢复：" + ex.Message); }
                    bool restored = LaunchEngine.RestorePending(files, new WindowsPlatform(files, null), journal);
                    if (restored) RegionCatalog.CompleteResolutions(files, journal);
                    files.Status(restored ? "cancelled" : "recovery", restored ? "独立恢复已完成。可关闭官方启动器后重新尝试。" : "独立恢复未完成，备份已保留，请点击恢复原设置。", true, null, 0);
                    return restored ? 0 : 4;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
