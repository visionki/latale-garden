using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LaTaleGarden
{
    [DataContract]
    public class JournalReference
    {
        [DataMember] public string Id;
        [DataMember] public string Hash;
    }
    [DataContract]
    public class RegionRequest
    {
        [DataMember] public string Operation;
        [DataMember] public string LocaleName;
        [DataMember] public JournalReference Source;
        [DataMember] public JournalReference[] AcknowledgedPending;
        [DataMember] public LocaleSnapshot ExpectedCurrent;
        [DataMember] public int OwnerPid;
        [DataMember] public long OwnerStartTicks;
    }
    [DataContract]
    public class ResolutionReceipt
    {
        [DataMember] public string OperationId;
        [DataMember] public string OriginalHash;
    }
    [DataContract]
    public class ProcessLocaleProbe
    {
        [DataMember] public uint ACP;
        [DataMember] public uint OEMCP;
        [DataMember] public uint LCID;
    }
    public class RegionBackup
    {
        public JournalReference Reference;
        public Journal Journal;
        public LocaleSnapshot Target;
        public bool Pending;
        public string Label;
    }
    public class RegionReport
    {
        public LocaleSnapshot Configured;
        public ProcessLocaleProbe Probe;
        public string ProbeError;
        public DateTime CheckedUtc;
        public bool WriteBusy;
        public bool GameRunning;
        public bool ReadChanged;
        public RegionBackup Backup;
        public JournalReference[] Pending;
        public bool ProbeMatches
        {
            get { return Probe != null && !ReadChanged && Configured.ACP == Probe.ACP.ToString(CultureInfo.InvariantCulture) && Configured.OEMCP == Probe.OEMCP.ToString(CultureInfo.InvariantCulture) && int.Parse(Configured.DefaultLanguage, NumberStyles.HexNumber) == Probe.LCID; }
        }
    }
    public static class RegionCatalog
    {
        public static bool Same(LocaleSnapshot a, LocaleSnapshot b)
        {
            return a != null && b != null && a.LocaleName == b.LocaleName && a.DefaultLanguage == b.DefaultLanguage && a.ACP == b.ACP && a.OEMCP == b.OEMCP && a.MACCP == b.MACCP;
        }
        public static string Name(string name)
        {
            if (name == "zh-CN") return "中文（简体，中国）";
            if (name == "zh-TW") return "中文（繁体，台湾）";
            try { return CultureInfo.GetCultureInfo(name).DisplayName; } catch { return "未知区域"; }
        }
        public static LocaleSnapshot Preset(string name)
        {
            if (name != "zh-CN" && name != "zh-TW") throw new InvalidDataException("手动选择仅支持简体中国和繁体台湾。");
            var culture = CultureInfo.GetCultureInfo(name);
            return new LocaleSnapshot { LocaleName = name, DefaultLanguage = culture.LCID.ToString("x4"), ACP = culture.TextInfo.ANSICodePage.ToString(), OEMCP = culture.TextInfo.OEMCodePage.ToString(), MACCP = culture.TextInfo.MacCodePage.ToString() };
        }
        public static LocaleSnapshot RecoveryTarget(Journal journal)
        {
            return journal.Operation == "restore" ? journal.Target : journal.Original;
        }
        public static bool ValidJournal(Journal journal, string id)
        {
            try
            {
                if (journal == null || journal.Id != id) return false;
                WindowsPlatform.ValidateSnapshot(journal.Original);
                if (!string.IsNullOrEmpty(journal.Operation) && journal.Operation != "launch" && journal.Operation != "set" && journal.Operation != "restore") return false;
                if (journal.Operation == "restore" || journal.Operation == "set") WindowsPlatform.ValidateSnapshot(journal.Target);
                return true;
            }
            catch { return false; }
        }
        public static string SessionPath(string root, string id)
        {
            Guid parsed;
            if (id == null || !Guid.TryParseExact(id, "N", out parsed)) throw new InvalidDataException("会话标识无效。");
            return Path.Combine(root, id);
        }
        public static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static JournalReference Reference(string directory)
        {
            return new JournalReference { Id = Path.GetFileName(directory), Hash = Hash(Path.Combine(directory, "recovery.json")) };
        }
        public static bool HasResolution(string directory)
        {
            try
            {
                var receipt = JsonFile.TryRead<ResolutionReceipt>(Path.Combine(directory, "resolved.json"));
                if (receipt == null || receipt.OriginalHash != Hash(Path.Combine(directory, "recovery.json"))) return false;
                string resolvedPath = SessionPath(Path.GetDirectoryName(directory), receipt.OperationId);
                var record = JsonFile.TryRead<Journal>(Path.Combine(resolvedPath, "recovery.json"));
                return ValidJournal(record, receipt.OperationId) && record.Committed && !record.Pending && (record.Operation == "set" || record.Operation == "restore") && record.Resolves != null && record.Resolves.Any(r => r.Id == Path.GetFileName(directory) && r.Hash == receipt.OriginalHash);
            }
            catch { return false; }
        }
        public static JournalReference[] Pending(string root)
        {
            if (!Directory.Exists(root)) return new JournalReference[0];
            var result = new List<JournalReference>();
            foreach (string directory in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
            {
                Guid id;
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out id) && SessionFiles.NeedsRecovery(directory)) result.Add(Reference(directory));
            }
            return result.ToArray();
        }
        public static RegionBackup ReadBackup(string root, JournalReference reference)
        {
            if (reference == null) throw new InvalidDataException("没有选择有效的恢复备份。");
            string directory = SessionPath(root, reference.Id);
            string file = Path.Combine(directory, "recovery.json");
            string hashBefore = Hash(file);
            var journal = JsonFile.Read<Journal>(file);
            if (reference.Hash != hashBefore || hashBefore != Hash(file)) throw new InvalidOperationException("备份已发生变化，请重新检测后再恢复。");
            if (!ValidJournal(journal, reference.Id)) throw new InvalidDataException("备份记录损坏，无法猜测原区域。可手动选择区域。");
            bool pending = SessionFiles.NeedsRecovery(directory);
            if (!pending && !string.IsNullOrEmpty(journal.Operation) && journal.Operation != "launch") throw new InvalidOperationException("请选择启动前备份或未完成操作的恢复记录。");
            return new RegionBackup { Reference = reference, Journal = journal, Pending = pending, Target = pending ? RecoveryTarget(journal) : journal.Original, Label = pending ? "未完成操作的恢复目标" : "最近一次启动前" };
        }
        public static RegionBackup LatestBackup(string root)
        {
            if (!Directory.Exists(root)) return null;
            var records = new List<RegionBackup>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                try { records.Add(ReadBackup(root, Reference(directory))); } catch { }
            }
            return records.OrderByDescending(r => r.Pending).ThenByDescending(r => r.Journal.CreatedUtc).FirstOrDefault();
        }
        public static bool SameReferences(JournalReference[] a, JournalReference[] b)
        {
            var left = (a ?? new JournalReference[0]).Select(r => r.Id + ":" + r.Hash).OrderBy(s => s, StringComparer.Ordinal);
            var right = (b ?? new JournalReference[0]).Select(r => r.Id + ":" + r.Hash).OrderBy(s => s, StringComparer.Ordinal);
            return left.SequenceEqual(right);
        }
        public static void CompleteResolutions(SessionFiles files, Journal journal)
        {
            if (!journal.Committed || journal.Pending || journal.Resolves == null) return;
            string root = Path.GetDirectoryName(files.DirectoryPath);
            foreach (var source in journal.Resolves)
            {
                string directory = SessionPath(root, source.Id);
                if (source.Hash != Hash(Path.Combine(directory, "recovery.json"))) throw new IOException("旧记录已变化，未将其标记为人工处理完成。");
                JsonFile.Write(Path.Combine(directory, "resolved.json"), new ResolutionReceipt { OperationId = journal.Id, OriginalHash = source.Hash });
            }
        }
        public static bool OtherWorkerAlive(string root, string ownId)
        {
            if (!Directory.Exists(root)) return false;
            foreach (string directory in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(directory) == ownId) continue;
                var journal = JsonFile.TryRead<Journal>(Path.Combine(directory, "recovery.json"));
                if (journal != null && Native.SameProcess(journal.WorkerPid, journal.WorkerStartTicks)) return true;
                string guardFile = Path.Combine(directory, "guard.ready");
                if (!SessionFiles.NeedsRecovery(directory) || !File.Exists(guardFile)) continue;
                int pid;
                try
                {
                    if (int.TryParse(File.ReadAllText(guardFile), out pid))
                    {
                        string executable = Native.ProcessPath(pid);
                        if (executable != null && string.Equals(Path.GetFileName(executable), "LaTaleGarden.exe", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch (IOException) { }
            }
            return false;
        }
    }
    public static class RegionDiagnostics
    {
        public static bool WriteBusy()
        {
            try { using (var mutex = new Mutex(false, Program.TransactionMutex)) { if (!Program.TryLock(mutex, 0)) return true; mutex.ReleaseMutex(); return false; } }
            catch (UnauthorizedAccessException) { return true; }
        }
        public static RegionReport Read()
        {
            var platform = new WindowsPlatform(null, null);
            var report = new RegionReport { Configured = platform.CaptureLocale(), WriteBusy = WriteBusy(), GameRunning = WindowsPlatform.AnyGameRunning() };
            try
            {
                var info = new ProcessStartInfo(AppPaths.Executable, "--probe-json") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var process = Process.Start(info))
                {
                    if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("新进程检测超时。"); }
                    string data = process.StandardOutput.ReadToEnd();
                    if (process.ExitCode != 0) throw new IOException("新进程检测失败。");
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(data))) report.Probe = (ProcessLocaleProbe)new DataContractJsonSerializer(typeof(ProcessLocaleProbe)).ReadObject(stream);
                }
            }
            catch (Exception ex) { report.ProbeError = ex.Message; }
            report.ReadChanged = !RegionCatalog.Same(report.Configured, platform.CaptureLocale());
            report.Backup = RegionCatalog.LatestBackup(AppPaths.SessionsRoot);
            report.Pending = RegionCatalog.Pending(AppPaths.SessionsRoot);
            report.CheckedUtc = DateTime.UtcNow;
            return report;
        }
    }
    public interface IRegionPlatform : ILaunchPlatform
    {
        bool RegionGameRunning { get; }
        void ApplyRegion(string localeName);
    }
    public class RegionEngine
    {
        private readonly SessionFiles files;
        private readonly IRegionPlatform platform;
        public RegionEngine(SessionFiles files, IRegionPlatform platform) { this.files = files; this.platform = platform; }
        private void CheckCancel()
        {
            if (platform.Cancelled || !platform.OwnerAlive) throw new OperationCanceledException("区域操作已取消。");
        }
        public void Run(RegionRequest request)
        {
            Journal journal = null;
            bool armed = false;
            bool committed = false;
            string outcome = "region-failed", message = "区域操作未完成。";
            try
            {
                if (request.Operation != "set" && request.Operation != "restore") throw new InvalidDataException("区域操作无效。");
                string root = Path.GetDirectoryName(files.DirectoryPath);
                if (!RegionCatalog.SameReferences(RegionCatalog.Pending(root), request.AcknowledgedPending)) throw new InvalidOperationException("待恢复记录已变化，请重新检测并确认。");
                LocaleSnapshot current = platform.CaptureLocale();
                WindowsPlatform.ValidateSnapshot(current);
                if (!RegionCatalog.Same(current, request.ExpectedCurrent)) throw new InvalidOperationException("当前区域已变化，请重新检测后再操作。");
                LocaleSnapshot target;
                JournalReference[] resolves;
                if (request.Operation == "set")
                {
                    if (platform.RegionGameRunning) throw new InvalidOperationException("请先关闭游戏与官方启动器，再手动选择区域。");
                    if (current.ACP == "65001") throw new InvalidOperationException("当前启用 UTF-8 系统代码页，请通过 Windows 区域设置处理；未修改该选项。");
                    target = RegionCatalog.Preset(request.LocaleName);
                    resolves = request.AcknowledgedPending ?? new JournalReference[0];
                }
                else
                {
                    var backup = RegionCatalog.ReadBackup(root, request.Source);
                    target = backup.Target;
                    resolves = backup.Pending ? new[] { backup.Reference } : new JournalReference[0];
                }
                CheckCancel();
                var process = Process.GetCurrentProcess();
                journal = new Journal { Id = Path.GetFileName(files.DirectoryPath), Original = current, Target = target, Operation = request.Operation, Pending = false, Committed = false, Resolves = resolves, CreatedUtc = DateTime.UtcNow, WorkerPid = process.Id, WorkerStartTicks = process.StartTime.ToUniversalTime().Ticks };
                files.SaveJournal(journal);
                platform.StartGuard();
                // A user may launch the game or change regional settings while the guard starts.
                CheckCancel();
                if (!platform.Matches(current)) throw new InvalidOperationException("操作前区域再次发生变化，已停止写入。");
                if (request.Operation == "set" && platform.RegionGameRunning) throw new InvalidOperationException("游戏已打开，手动区域切换已停止。");
                journal.Pending = true;
                files.SaveJournal(journal);
                armed = true;
                files.Status("region-working", request.Operation == "restore" ? "正在重新应用备份并核对原值…" : "正在应用所选区域并核对设置…", false, null, 0);
                files.Log("区域操作=" + request.Operation + "；修改前=" + current.LocaleName + "；目标=" + target.LocaleName);
                if (request.Operation == "restore")
                {
                    if (!LaunchEngine.RestorePending(files, platform, journal)) throw new IOException("备份恢复尚未完成，请重试。");
                    committed = true;
                    RegionCatalog.CompleteResolutions(files, journal);
                }
                else
                {
                    platform.ApplyRegion(target.LocaleName);
                    CheckCancel();
                    if (!platform.Matches(target)) throw new IOException("所选区域写入后的完整配置核对失败。");
                    journal.Committed = true;
                    journal.Pending = false;
                    files.SaveJournal(journal);
                    committed = true;
                    RegionCatalog.CompleteResolutions(files, journal);
                }
                outcome = "region-done";
                message = request.Operation == "restore" ? "备份配置已重新应用并核对。游戏内文字需实际确认。" : "所选区域已保存并保留。完整生效需要重启电脑。";
            }
            catch (Exception ex)
            {
                message = ex.Message; files.Log("区域操作异常：" + ex);
                if (armed && journal != null && !committed)
                {
                    journal.Committed = false;
                    journal.Pending = true;
                    if (!LaunchEngine.RestorePending(files, platform, journal)) { outcome = "recovery"; message += " 恢复未完成，记录已保留。"; }
                    else if (journal.Operation == "restore")
                    {
                        outcome = "region-done"; message = "备份配置已重新应用并核对。";
                        try { RegionCatalog.CompleteResolutions(files, journal); } catch (Exception historyError) { files.Log(historyError.ToString()); message += " 部分历史记录处理未完成，请查看日志。"; }
                    }
                    else message += " 已回退到本次手动操作前的配置。";
                }
                else if (committed) message = "区域配置已保存，部分历史记录处理未完成；请查看日志并重新检测。";
            }
            files.Status(outcome, message, true, null, 0);
        }
    }
}
