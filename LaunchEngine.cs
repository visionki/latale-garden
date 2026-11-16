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
        GameLaunchPlan CheckGameUpdate(string directory);
        void StartOfficialLauncher(string directory);
        void CloseOfficialUpdater(string directory);
        void StartDirectClient(string directory, GameLaunchPlan plan);
        bool DirectClientExited { get; }
        string ReadUpdateAction();
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
        private GameLaunchPlan WaitForOfficialUpdate(LaunchRequest request, GameLaunchPlan first)
        {
            CheckCancellation();
            platform.StartOfficialLauncher(request.GameDirectory);
            files.Log("需要游戏更新；已打开官方启动器，等待用户点击“我已更新完成”。此阶段未修改系统区域。");
            double started = platform.ElapsedSeconds;
            string message = first.Message;
            bool askRestart = false;
            GameUpdateState confirmationState = GameUpdateState.Unknown;
            while (platform.ElapsedSeconds - started < request.WaitSeconds)
            {
                CheckCancellation();
                if (platform.FindClient(request.GameDirectory) != null) throw new InvalidOperationException("官方启动器已启动游戏。更新阶段尚未切换区域，请退出游戏后重新启动；更新时请取消官方“自动执行”。");
                string action = platform.ReadUpdateAction();
                if (!askRestart && action == "complete")
                {
                    Update("game-verifying", "正在复查游戏文件，请稍候…", null, 0);
                    var plan = platform.CheckGameUpdate(request.GameDirectory);
                    CheckCancellation();
                    if (plan.State == GameUpdateState.Current)
                    {
                        Update("game-updater-closing", "游戏已更新，正在关闭官方启动器…", null, 0);
                        platform.CloseOfficialUpdater(request.GameDirectory);
                        CheckCancellation();
                        // Closing the patcher can finish pending writes. Check again after it exits.
                        Update("game-verifying", "官方启动器已关闭，正在确认最终文件…", null, 0);
                        plan = platform.CheckGameUpdate(request.GameDirectory);
                        CheckCancellation();
                        if (plan.State == GameUpdateState.Current) return plan;
                    }
                    askRestart = true;
                    confirmationState = plan.State;
                    message = plan.State == GameUpdateState.NeedsUpdate ? "尚未更新到最新版。" + plan.Message : "暂时无法完成启动检查。" + plan.Message;
                    files.Log("更新完成复查未通过：" + message);
                }
                else if (askRestart && action == "restart")
                {
                    Update("game-updater-restarting", "正在重启官方启动器…", null, 0);
                    platform.CloseOfficialUpdater(request.GameDirectory);
                    CheckCancellation();
                    platform.StartOfficialLauncher(request.GameDirectory);
                    files.Log("用户选择重启：本轮官方启动器已重新打开，继续等待更新。");
                    askRestart = false; message = "请等待官方更新完成，再点击“我已更新完成”。";
                }
                else if (askRestart && action == "keep")
                {
                    files.Log("用户选择不重启；保留官方启动器，继续等待更新。");
                    askRestart = false; message = "继续等待官方更新。完成后可再次点击“我已更新完成”。";
                }
                int remaining = Math.Max(0, request.WaitSeconds - (int)(platform.ElapsedSeconds - started));
                Update(askRestart ? (confirmationState == GameUpdateState.NeedsUpdate ? "game-update-incomplete" : "game-update-unconfirmed") : "game-updating", message, null, remaining);
                platform.Sleep(500);
            }
            throw new TimeoutException("等待官方更新超时，已停止本次启动。官方启动器仍可继续更新，完成后请再试。");
        }
        public void Run(LaunchRequest request)
        {
            string outcome = "failed";
            string message = "启动未完成。";
            ClientInfo client = null;
            bool armed = false;
            double detectedSeconds = -1;
            try
            {
                Update("checking", "正在检查游戏目录与运行环境…", null, 0);
                files.Log("开始启动；兼容模式=" + request.Compatibility + "；恢复时机=本轮客户端进程出现；目录=" + request.GameDirectory);
                platform.ValidateGame(request.GameDirectory);
                if (platform.ExistingGame(request.GameDirectory)) throw new InvalidOperationException("游戏或官方启动器已经运行。请先关闭它，再通过本启动器启动。");
                CheckCancellation();
                if (request.Compatibility && !platform.SupportsTemporaryLocale) throw new PlatformNotSupportedException(Native.TemporaryLocaleUnavailable);
                GameLaunchPlan plan = GameLaunchPlan.Official("本次选择使用官方启动器。");
                if (!request.OfficialOnly)
                {
                    Update("game-checking", "正在检查官方游戏更新…", null, 0);
                    plan = platform.CheckGameUpdate(request.GameDirectory);
                    CheckCancellation();
                    if (plan.State == GameUpdateState.NeedsUpdate) plan = WaitForOfficialUpdate(request, plan);
                    CheckCancellation();
                    if (platform.ExistingGame(request.GameDirectory)) throw new InvalidOperationException("检查期间游戏或官方启动器被打开，请关闭后重试。");
                }
                bool direct = plan.State == GameUpdateState.Current;
                files.Log("本次启动方式=" + (direct ? "直接客户端" : "官方启动器") + "；" + plan.Message);
                if (request.Compatibility)
                {
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
                if (direct)
                {
                    Update("direct-starting", "游戏已是最新版，正在直接启动…", null, 0);
                    platform.StartDirectClient(request.GameDirectory, plan);
                    files.Log("已直接启动客户端，等待核对本轮进程身份。");
                }
                else
                {
                    platform.StartOfficialLauncher(request.GameDirectory);
                    files.Log("官方启动器已打开，等待本次新客户端。");
                }
                double waitStart = platform.ElapsedSeconds;
                int waitSeconds = direct ? Math.Min(90, Math.Max(1, request.WaitSeconds)) : Math.Max(1, request.WaitSeconds);
                while (platform.ElapsedSeconds - waitStart < waitSeconds)
                {
                    CheckCancellation();
                    if (direct && platform.DirectClientExited) throw new IOException("游戏客户端已退出。请查看提示或使用“打开官方启动器”重试。");
                    client = platform.FindClient(request.GameDirectory);
                    int remaining = Math.Max(0, waitSeconds - (int)(platform.ElapsedSeconds - waitStart));
                    if (client != null)
                    {
                        detectedSeconds = platform.ElapsedSeconds;
                        files.Log("检测到本次客户端 PID=" + client.Id + "；创建时间=" + client.StartTicks + "；可见窗口=" + client.HasWindow);
                        outcome = "client-started";
                        message = request.Compatibility ? "原系统区域已恢复，请在游戏内继续。" : "客户端已启动，请在游戏内继续。";
                        files.Log(request.Compatibility ? "已确认本次客户端进程，立即恢复原区域。" : "已确认本次客户端进程，本次未修改系统区域。");
                        break;
                    }
                    else
                    {
                        Update(direct ? "direct-starting" : "waiting", direct ? "正在确认本次客户端进程…" : "请在官方启动器完成更新并开始游戏。", null, remaining);
                    }
                    platform.Sleep(1000);
                }
                if (outcome != "client-started") throw new TimeoutException(direct ? "等待客户端启动超时，请使用“打开官方启动器”重试。" : "等待游戏超时。请确认官方启动器已完成更新，再重新启动。");
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
                    else if (detectedSeconds >= 0)
                    {
                        files.Log("从检测到客户端至恢复核对完成=" + Math.Round((platform.ElapsedSeconds - detectedSeconds) * 1000) + "ms。");
                        try { platform.Probe(); } catch (Exception ex) { files.Log("恢复后检查未完成：" + ex.Message); }
                    }
                }
                if (armed && outcome != "recovery" && outcome != "client-started") message += " 原区域设置已恢复。";
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
