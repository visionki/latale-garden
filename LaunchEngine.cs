using System;
using System.Diagnostics;
using System.IO;

namespace LaTaleGarden
{
    public interface ILaunchPlatform
    {
        bool SupportsTemporaryLocale { get; }
        double ElapsedSeconds { get; }
        void Sleep(int milliseconds);
        void ValidateGame(string directory);
        bool ExistingGame(string directory);
        LocaleSnapshot CaptureLocale();
        void StartGuard();
        void ApplyTraditional();
        void Restore(LocaleSnapshot original);
        bool Matches(LocaleSnapshot original);
        void StartOfficialLauncher(string directory);
        ClientInfo FindClient(string directory);
        bool Cancelled { get; }
        bool OwnerAlive { get; }
        void Probe();
    }
    public class LaunchEngine
    {
        private readonly SessionFiles files;
        private readonly ILaunchPlatform platform;
        private Journal journal;
        public LaunchEngine(SessionFiles files, ILaunchPlatform platform) { this.files = files; this.platform = platform; }

        private void Update(string stage, string message, ClientInfo client, int remaining)
        {
            files.Status(stage, message, false, client, remaining);
        }
        private void CheckCancellation()
        {
            if (platform.Cancelled) throw new OperationCanceledException("已取消等待；官方启动器可能仍在运行。再次启动游戏前请先关闭它。");
            if (!platform.OwnerAlive) throw new OperationCanceledException("启动器界面已退出，已停止等待。");
        }
        public void Run(LaunchRequest request)
        {
            string outcome = "failed";
            string message = "启动未完成。";
            ClientInfo client = null;
            bool armed = false;
            try
            {
                Update("checking", "正在检查游戏目录与运行环境…", null, 0);
                files.Log("开始启动；兼容模式=" + request.Compatibility + "；目录=" + request.GameDirectory);
                platform.ValidateGame(request.GameDirectory);
                if (platform.ExistingGame(request.GameDirectory)) throw new InvalidOperationException("游戏或官方启动器已经运行。请先关闭它，再通过本启动器启动。");
                CheckCancellation();
                if (request.Compatibility)
                {
                    if (!platform.SupportsTemporaryLocale) throw new PlatformNotSupportedException(Native.TemporaryLocaleUnavailable);
                    LocaleSnapshot original = platform.CaptureLocale();
                    files.Log("启动前：区域=" + original.LocaleName + "；Default=" + original.DefaultLanguage + "；ACP=" + original.ACP + "；OEMCP=" + original.OEMCP + "；MACCP=" + original.MACCP);
                    if (original.ACP == "65001") throw new InvalidOperationException("当前系统启用了 UTF-8 代码页，临时兼容模式暂不支持。可在设置中关闭兼容模式，使用标准启动。");
                    journal = new Journal {
                        Id = Path.GetFileName(files.DirectoryPath), Original = original, Pending = false,
                        CreatedUtc = DateTime.UtcNow, WorkerPid = Process.GetCurrentProcess().Id,
                        WorkerStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                        GameDirectory = request.GameDirectory, Operation = "launch"
                    };
                    // The recovery record and watchdog must exist before the first system write.
                    files.SaveJournal(journal);
                    platform.StartGuard();
                    journal.Pending = true;
                    files.SaveJournal(journal);
                    armed = true;
                    Update("preparing", "已保存原设置，正在准备繁体运行环境…", null, 0);
                    platform.ApplyTraditional();
                    platform.Probe();
                    CheckCancellation();
                }
                platform.StartOfficialLauncher(request.GameDirectory);
                files.Log("官方启动器已打开，等待本次新客户端。");
                double waitStart = platform.ElapsedSeconds;
                double firstClientSeen = -1;
                double windowSince = -1;
                int lastClientId = 0;
                long lastClientTicks = 0;
                int waitSeconds = Math.Max(1, request.WaitSeconds);
                while (platform.ElapsedSeconds - waitStart < waitSeconds)
                {
                    CheckCancellation();
                    client = platform.FindClient(request.GameDirectory);
                    int remaining = Math.Max(0, waitSeconds - (int)(platform.ElapsedSeconds - waitStart));
                    if (client != null)
                    {
                        if (client.Id != lastClientId || client.StartTicks != lastClientTicks)
                        {
                            firstClientSeen = platform.ElapsedSeconds;
                            windowSince = -1;
                            lastClientId = client.Id;
                            lastClientTicks = client.StartTicks;
                            files.Log("检测到本次客户端 PID=" + client.Id + "，等待窗口与初始化缓冲。");
                        }
                        if (client.HasWindow && windowSince < 0) windowSince = platform.ElapsedSeconds;
                        if (!client.HasWindow) windowSince = -1;
                        Update("initializing", "客户端正在启动，正在等待游戏窗口稳定…", client, remaining);
                        if (windowSince >= 0 && platform.ElapsedSeconds - windowSince >= 3 && platform.ElapsedSeconds - firstClientSeen >= request.SettleSeconds)
                        {
                            outcome = "running";
                            message = request.Compatibility ? "游戏窗口已就绪，原区域设置已恢复。" : "游戏窗口已就绪，本次未修改系统区域。";
                            files.Log("游戏窗口已出现并经过初始化等待；这不代表已验证游戏中文字。");
                            break;
                        }
                    }
                    else
                    {
                        firstClientSeen = windowSince = -1;
                        lastClientId = 0;
                        Update("waiting", "请在官方启动器完成更新、登录并开始游戏。", null, remaining);
                    }
                    platform.Sleep(1000);
                }
                if (outcome != "running") throw new TimeoutException("等待游戏超时。请确认官方启动器已完成更新，再重新启动。");
            }
            catch (OperationCanceledException ex) { outcome = "cancelled"; message = ex.Message; files.Log(message); }
            catch (Exception ex) { outcome = "failed"; message = ex.Message; files.Log("启动失败：" + ex); }
            finally
            {
                if (armed)
                {
                    try { Update("restoring", "正在恢复启动前的系统区域，请稍候…", client, 0); }
                    catch (Exception ex) { files.Log("状态写入失败，继续恢复：" + ex.Message); }
                    if (!RestorePending(files, platform, journal))
                    {
                        outcome = "recovery";
                        message = "原设置尚未恢复，请点击“恢复原设置”重试。恢复记录已保留。";
                    }
                }
                if (armed && outcome != "recovery" && outcome != "running") message += " 原区域设置已恢复。";
                files.Log("流程结束：" + outcome + "；" + message);
                files.Status(outcome, message, true, client, 0);
            }
        }
        public static bool RestorePending(SessionFiles files, ILaunchPlatform platform, Journal journal)
        {
            if (journal == null) { files.Log("无法读取恢复记录，不能声称恢复成功。"); return false; }
            if (!journal.Pending) return true;
            if (journal.Original == null) { files.Log("恢复记录缺失原始值，拒绝猜测系统设置。"); return false; }
            LocaleSnapshot target = RegionCatalog.RecoveryTarget(journal);
            try { WindowsPlatform.ValidateSnapshot(target); }
            catch (Exception ex) { files.Log("恢复目标无效：" + ex.Message); return false; }
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    files.Log("恢复配置，第 " + attempt + " 次；目标=" + target.LocaleName);
                    // Even if registry values already match, reapply through the same official API.
                    platform.Restore(target);
                    if (!platform.Matches(target)) throw new IOException("恢复后的注册表值与目标备份不一致。");
                    journal.Pending = false;
                    if (journal.Operation == "restore") journal.Committed = true;
                    else if (journal.Operation == "set") journal.Committed = false;
                    files.SaveJournal(journal);
                    files.Log("已核对原区域与 ACP/OEMCP/MACCP，恢复完成。");
                    return true;
                }
                catch (Exception ex) { files.Log("恢复失败：" + ex.Message); platform.Sleep(1000 * attempt); }
            }
            return false;
        }
    }
}
