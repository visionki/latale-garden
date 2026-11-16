using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.Threading;

namespace LaTaleGarden
{
    [DataContract]
    public sealed class UpdateJob
    {
        [DataMember] public string Target;
        [DataMember] public string OriginalHash;
        [DataMember] public string Version;
        [DataMember] public string Nonce;
        [DataMember] public int ParentPid;
        [DataMember] public long ParentTicks;
        [DataMember] public DateTime CreatedUtc;
    }
    [DataContract]
    public sealed class UpdateStatus
    {
        [DataMember] public string Stage;
        [DataMember] public string Message;
        [DataMember] public int HelperPid;
        [DataMember] public long HelperTicks;
        [DataMember] public DateTime UpdatedUtc;
    }
    [DataContract]
    public sealed class UpdateReceipt
    {
        [DataMember] public string Nonce;
        [DataMember] public string Version;
        [DataMember] public int Pid;
        [DataMember] public long Ticks;
    }
    public static class UpdateInstaller
    {
        public static string MutexName { get { return @"Local\LaTaleGarden.Update.v1." + WindowsIdentity.GetCurrent().User.Value; } }
        public static bool Applying()
        {
            try { using (var mutex = new Mutex(false, MutexName)) { if (!Program.TryLock(mutex, 0)) return true; mutex.ReleaseMutex(); return false; } }
            catch (UnauthorizedAccessException) { return true; }
        }
        public static string JobPath(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new InvalidDataException("更新任务标识无效。");
            string path = Path.Combine(UpdatePaths.Jobs, id);
            RequireRegularPath(path);
            return path;
        }
        public static void RequireRegularPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("请将启动器放在本地磁盘后更新。");
            for (string item = full; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("此位置包含链接或重定向目录，请使用手动下载更新。");
        }
        public static void CheckIdle()
        {
            if (AppPaths.PendingSessions().Any()) throw new IOException("请先完成原区域恢复，再安装更新。");
            if (RegionCatalog.OtherWorkerAlive(AppPaths.SessionsRoot, "")) throw new IOException("启动或恢复辅助进程仍在运行，请稍后再更新。");
        }
        public static string Prepare(UpdateRelease release, byte[] feed, string package)
        {
            CheckIdle();
            RequireRegularPath(AppPaths.Executable);
            var checkedFeed = UpdateProtocol.ReadFeed(feed, UpdateProtocol.PublicKey);
            var verified = checkedFeed.Releases.Single(x => x.Version == release.Version);
            if (verified.Sha256 != release.Sha256 || UpdateProtocol.ParseVersion(verified.Version) <= UpdateProtocol.ParseVersion(Program.Version)) throw new InvalidDataException("更新版本信息已变化。");
            UpdateProtocol.VerifyPackage(package, verified);
            string id = Guid.NewGuid().ToString("N"), directory = JobPath(id);
            Directory.CreateDirectory(directory);
            File.Copy(AppPaths.Executable, Path.Combine(directory, "helper.exe"));
            File.Copy(package, Path.Combine(directory, "new.exe"));
            File.WriteAllBytes(Path.Combine(directory, "feed.json"), feed);
            using (var process = Process.GetCurrentProcess())
                JsonFile.Write(Path.Combine(directory, "job.json"), new UpdateJob {
                    Target = Path.GetFullPath(AppPaths.Executable), OriginalHash = UpdateProtocol.Hash(AppPaths.Executable), Version = verified.Version,
                    Nonce = Guid.NewGuid().ToString("N"), ParentPid = process.Id, ParentTicks = process.StartTime.ToUniversalTime().Ticks, CreatedUtc = DateTime.UtcNow
                });
            return id;
        }
        public static Process StartHelper(string id, bool elevated)
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(JobPath(id), "helper.exe"), "--apply-update " + id) {
                UseShellExecute = true, Verb = elevated ? "runas" : "", WorkingDirectory = JobPath(id), WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        public static bool CanWriteDirectory(string target)
        {
            string probe = Path.Combine(Path.GetDirectoryName(target), ".garden-access-" + Guid.NewGuid().ToString("N"));
            try { using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { } return true; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static UpdateStatus Status(string id) { return JsonFile.TryRead<UpdateStatus>(Path.Combine(JobPath(id), "status.json")); }
        private static void SetStatus(string directory, string stage, string message)
        {
            using (var me = Process.GetCurrentProcess())
                JsonFile.Write(Path.Combine(directory, "status.json"), new UpdateStatus { Stage = stage, Message = message, HelperPid = me.Id, HelperTicks = me.StartTime.ToUniversalTime().Ticks, UpdatedUtc = DateTime.UtcNow });
            UpdatePaths.Log(stage + ": " + message);
        }
        private static UpdateRelease Release(string directory, UpdateJob job)
        {
            return UpdateProtocol.ReadFeed(File.ReadAllBytes(Path.Combine(directory, "feed.json")), UpdateProtocol.PublicKey).Releases.Single(x => x.Version == job.Version);
        }
        private static void ValidateJob(string directory, UpdateJob job)
        {
            Guid nonce;
            if (job == null || !Guid.TryParseExact(job.Nonce, "N", out nonce) || job.ParentPid <= 0 || job.ParentTicks <= 0 ||
                job.CreatedUtc > DateTime.UtcNow.AddMinutes(2) || DateTime.UtcNow - job.CreatedUtc > TimeSpan.FromMinutes(15))
                throw new InvalidDataException("更新任务已过期或损坏，请重新点击更新。");
            if (!Path.IsPathRooted(job.Target) || Path.GetFullPath(job.Target) != job.Target || !job.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("启动器路径无效。");
            RequireRegularPath(job.Target);
            if (Native.SamePath(job.Target, AppPaths.Executable) || UpdateProtocol.Hash(AppPaths.Executable) != job.OriginalHash ||
                UpdateProtocol.Hash(job.Target) != job.OriginalHash) throw new InvalidDataException("原启动器已变化，未覆盖文件。");
            if (Native.SameProcess(job.ParentPid, job.ParentTicks) && !Native.SamePath(Native.ProcessPath(job.ParentPid), job.Target))
                throw new InvalidDataException("更新请求的进程身份不匹配。");
        }
        private static void RequireNoOtherCopy(UpdateJob job)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(job.Target)))
                using (process)
                {
                    if (process.Id == job.ParentPid && Native.SameProcess(job.ParentPid, job.ParentTicks)) continue;
                    if (Native.SamePath(Native.ProcessPath(process.Id), job.Target)) throw new IOException("此启动器的其他进程仍在运行，请稍后重试更新。");
                }
        }
        private static void CopyDurable(string source, string destination)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
        }
        // The staging file is on the target volume. A failed Replace never deletes the original.
        private static void Replace(string source, string target, string staging, string hash)
        {
            CopyDurable(source, staging);
            if (UpdateProtocol.Hash(staging) != hash) throw new InvalidDataException("写入文件校验失败。");
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(staging, target, null, true); return; }
                catch (IOException) { if (attempt >= 19) throw; Thread.Sleep(250); }
            }
        }
        public static int Apply(string id)
        {
            string directory = JobPath(id);
            UpdateJob job = null; UpdateRelease release = null; Process child = null;
            string staging = null; bool replaced = false, parentExited = false;
            using (var updateMutex = new Mutex(false, MutexName))
            using (var localeMutex = new Mutex(false, Program.TransactionMutex))
            {
                bool localeLocked = false;
                if (!Program.TryLock(updateMutex, 0)) { SetStatus(directory, "failed", "已有更新正在执行。"); return 2; }
                try
                {
                    job = JsonFile.Read<UpdateJob>(Path.Combine(directory, "job.json"));
                    ValidateJob(directory, job);
                    release = Release(directory, job);
                    if (UpdateProtocol.ParseVersion(release.Version) <= UpdateProtocol.ParseVersion(Program.Version) ||
                        release.MinimumWindowsBuild > UpdateProtocol.WindowsBuild() || release.MinimumFrameworkRelease > UpdateProtocol.FrameworkRelease())
                        throw new InvalidDataException("此版本不适用于当前启动器或系统。");
                    UpdateProtocol.VerifyPackage(Path.Combine(directory, "new.exe"), release);
                    CheckIdle(); RequireNoOtherCopy(job);
                    {
                        if (!Program.TryLock(localeMutex, 0)) throw new IOException("系统区域操作仍在进行，请稍后重试更新。");
                        localeLocked = true;
                        {
                            CheckIdle();
                            if (!CanWriteDirectory(job.Target)) throw new UnauthorizedAccessException("没有替换此位置文件的权限，请重新更新并允许管理员授权，或手动下载。");
                            SetStatus(directory, "ready", "已准备更新，正在等待启动器退出。");
                            var wait = Stopwatch.StartNew();
                            while (Native.SameProcess(job.ParentPid, job.ParentTicks) && wait.Elapsed < TimeSpan.FromSeconds(30) && !File.Exists(Path.Combine(directory, "cancel"))) Thread.Sleep(100);
                            if (File.Exists(Path.Combine(directory, "cancel"))) throw new IOException("更新准备已取消，原文件未替换。");
                            if (Native.SameProcess(job.ParentPid, job.ParentTicks)) throw new IOException("启动器尚未退出，已取消替换。");
                            parentExited = true;
                            CheckIdle(); RequireNoOtherCopy(job);
                            if (UpdateProtocol.Hash(job.Target) != job.OriginalHash) throw new IOException("原启动器已被更改，已取消替换。");
                            CopyDurable(job.Target, Path.Combine(directory, "previous.exe"));
                            if (UpdateProtocol.Hash(Path.Combine(directory, "previous.exe")) != job.OriginalHash) throw new IOException("旧版备份校验失败。");
                            staging = Path.Combine(Path.GetDirectoryName(job.Target), ".garden-" + id + ".new");
                            SetStatus(directory, "replacing", "正在替换启动器。");
                            Replace(Path.Combine(directory, "new.exe"), job.Target, staging, release.Sha256);
                            replaced = true;
                            SetStatus(directory, "installed", "正在确认新版启动。");
                            child = Process.Start(new ProcessStartInfo(job.Target, "--updated " + id) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(job.Target) });
                            if (child == null) throw new IOException("新版未能启动。");
                            long childTicks = child.StartTime.ToUniversalTime().Ticks;
                            wait.Restart();
                            while (wait.Elapsed < TimeSpan.FromSeconds(45))
                            {
                                var receipt = JsonFile.TryRead<UpdateReceipt>(Path.Combine(directory, "healthy.json"));
                                if (receipt != null && receipt.Nonce == job.Nonce && receipt.Version == release.Version && receipt.Pid == child.Id &&
                                    receipt.Ticks == childTicks && Native.SameProcess(child.Id, childTicks))
                                {
                                    SetStatus(directory, "confirmed", "已更新至 v" + release.Version + "，设置已保留。");
                                    return 0;
                                }
                                if (child.HasExited) break;
                                Thread.Sleep(150);
                            }
                            throw new IOException("新版没有完成启动确认。");
                        }
                    }
                }
                catch (Exception error)
                {
                    UpdatePaths.Log(error.ToString());
                    try
                    {
                        if (replaced)
                        {
                            if (child != null && !child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(5000); } }
                            string backup = Path.Combine(directory, "previous.exe");
                            if (UpdateProtocol.Hash(backup) != job.OriginalHash || UpdateProtocol.Hash(job.Target) != release.Sha256)
                                throw new IOException("回退前文件已变化；备份保留在 " + directory);
                            if (File.Exists(staging)) File.Delete(staging);
                            Replace(backup, job.Target, staging, job.OriginalHash);
                            SetStatus(directory, "rolled-back", "新版未正常启动，已恢复旧版。可稍后重试或手动下载。");
                        }
                        else SetStatus(directory, "failed", error.Message);
                        if (parentExited && job != null && File.Exists(job.Target))
                        {
                            // Release the update mutex before an ordinary restart in the finally block.
                            File.WriteAllText(Path.Combine(directory, "restart-old"), "1");
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        SetStatus(directory, "failed", "自动回退未完成，旧版备份保留在 " + directory + "。 " + rollbackError.Message);
                    }
                    return 1;
                }
                finally
                {
                    if (child != null) child.Dispose();
                    if (staging != null) { try { File.Delete(staging); } catch { } }
                    if (localeLocked) localeMutex.ReleaseMutex();
                    updateMutex.ReleaseMutex();
                    if (job != null && File.Exists(Path.Combine(directory, "restart-old")))
                    {
                        try
                        {
                            File.Delete(Path.Combine(directory, "restart-old"));
                            using (var old = Process.Start(new ProcessStartInfo(job.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(job.Target) })) { }
                        }
                        catch (Exception ex) { UpdatePaths.Log("请手动打开旧版：" + ex.Message); }
                    }
                }
            }
        }
        public static string PendingStartup()
        {
            if (!Directory.Exists(UpdatePaths.Jobs)) return null;
            foreach (var folder in new DirectoryInfo(UpdatePaths.Jobs).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc).Take(20))
            {
                var job = JsonFile.TryRead<UpdateJob>(Path.Combine(folder.FullName, "job.json"));
                var state = JsonFile.TryRead<UpdateStatus>(Path.Combine(folder.FullName, "status.json"));
                if (job != null && state != null && (state.Stage == "installed" || state.Stage == "replacing") && Native.SamePath(job.Target, AppPaths.Executable) && job.Version == Program.Version)
                    return folder.Name;
            }
            return null;
        }
        public static void Acknowledge(string id)
        {
            string directory = JobPath(id);
            var job = JsonFile.Read<UpdateJob>(Path.Combine(directory, "job.json"));
            if (!Native.SamePath(job.Target, AppPaths.Executable) || job.Version != Program.Version) throw new InvalidDataException("新版启动确认路径不匹配。");
            UpdateProtocol.VerifyPackage(AppPaths.Executable, Release(directory, job));
            using (var me = Process.GetCurrentProcess())
                JsonFile.Write(Path.Combine(directory, "healthy.json"), new UpdateReceipt { Nonce = job.Nonce, Version = Program.Version, Pid = me.Id, Ticks = me.StartTime.ToUniversalTime().Ticks });
            var status = Status(id);
            if (status != null && !Native.SameProcess(status.HelperPid, status.HelperTicks))
                SetStatus(directory, "confirmed", "已确认更新后的启动器正常打开。");
        }
        public static string LastResult()
        {
            if (!Directory.Exists(UpdatePaths.Jobs)) return null;
            foreach (var folder in new DirectoryInfo(UpdatePaths.Jobs).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc).Take(20))
            {
                var job = JsonFile.TryRead<UpdateJob>(Path.Combine(folder.FullName, "job.json"));
                var state = JsonFile.TryRead<UpdateStatus>(Path.Combine(folder.FullName, "status.json"));
                if (job != null && state != null && Native.SamePath(job.Target, AppPaths.Executable)) return state.Message;
            }
            return null;
        }
        public static void Cleanup()
        {
            if (Applying() || !Directory.Exists(UpdatePaths.Jobs)) return;
            try
            {
                var folders = new DirectoryInfo(UpdatePaths.Jobs).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
                int retained = 0;
                foreach (var folder in folders)
                {
                    Guid id; if (!Guid.TryParseExact(folder.Name, "N", out id)) continue;
                    string directory = JobPath(folder.Name);
                    var job = JsonFile.TryRead<UpdateJob>(Path.Combine(directory, "job.json"));
                    var state = JsonFile.TryRead<UpdateStatus>(Path.Combine(directory, "status.json"));
                    if (job == null || state == null || !Native.SamePath(job.Target, AppPaths.Executable) || Native.SameProcess(state.HelperPid, state.HelperTicks)) continue;
                    if (state.Stage != "confirmed" && state.Stage != "failed" && state.Stage != "rolled-back") continue;
                    string leftover = Path.Combine(Path.GetDirectoryName(AppPaths.Executable), ".garden-" + folder.Name + ".new");
                    try { File.Delete(leftover); } catch { }
                    if (retained++ < 2 || state.UpdatedUtc > DateTime.UtcNow.AddDays(-7)) continue;
                    // Delete only updater-owned files, never follow subdirectories or junctions.
                    foreach (string name in new[] { "helper.exe", "new.exe", "previous.exe", "feed.json", "job.json", "status.json", "healthy.json", "restart-old", "cancel" })
                        File.Delete(Path.Combine(directory, name));
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                string downloads = Path.Combine(UpdatePaths.Root, "Downloads");
                if (!Directory.Exists(downloads)) return;
                var packages = new DirectoryInfo(downloads).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
                foreach (var folder in packages.Skip(2))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(folder.Name, @"^[0-9]+\.[0-9]+\.[0-9]+-[a-f0-9]{12}$") || folder.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7)) continue;
                    RequireRegularPath(folder.FullName);
                    File.Delete(Path.Combine(folder.FullName, "LaTaleGarden.exe"));
                    foreach (var part in folder.GetFiles("LaTaleGarden.exe.*.part")) if (part.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) part.Delete();
                    if (!Directory.EnumerateFileSystemEntries(folder.FullName).Any()) folder.Delete();
                }
            }
            catch (Exception ex) { UpdatePaths.Log("旧缓存暂未清理：" + ex.Message); }
        }
    }
}
