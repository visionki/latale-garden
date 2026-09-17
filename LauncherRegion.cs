using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LaTaleGarden
{
    public partial class LauncherWindow
    {
        private readonly string previewPane;
        private RegionReport regionReport;
        private bool refreshingRegion, refreshRegionAgain, regionFresh, regionOperation;
        private DateTime lastRegionAttempt;

        private void InitializeRegion()
        {
            UI<ComboBox>("RegionCombo").SelectedIndex = 0;
            Button("RegionButton").Click += async (s, e) => { ShowPane("RegionPane"); await RefreshRegionAsync(true); };
            Button("RegionBack").Click += (s, e) => ShowPane("HomePane");
            Button("RegionRefreshButton").Click += async (s, e) => await RefreshRegionAsync(true);
            Button("RegionRestoreButton").Click += async (s, e) => await StartRegionAction("restore");
            Button("RegionApplyButton").Click += async (s, e) => await StartRegionAction("set");
            Button("RegionLogsButton").Click += (s, e) => { RefreshLogs(); ShowPane("LogsPane"); };
            Button("WindowsRegionButton").Click += (s, e) => {
                try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe"), "intl.cpl,,2") { UseShellExecute = true }); }
                catch (Exception ex) { SetRegionResult("无法打开 Windows 区域设置：" + ex.Message); }
            };
            Activated += async (s, e) => { if (!loading) await RefreshRegionAsync(false); };
            UpdateRegionAvailability();
        }
        private void SetRegionResult(string message)
        {
            Text("RegionResultText").Text = message;
            Text("RegionResultText").Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        private async void RefreshRegionFromEvent() { await RefreshRegionAsync(true); }
        private async Task RefreshRegionAsync(bool force)
        {
            if (previewPath != null || closingAllowed) return;
            if (refreshingRegion) { if (force) refreshRegionAgain = true; return; }
            if (!force && DateTime.UtcNow - lastRegionAttempt < TimeSpan.FromSeconds(5)) return;
            refreshingRegion = true; lastRegionAttempt = DateTime.UtcNow;
            Button("RegionRefreshButton").IsEnabled = false;
            UpdateRegionAvailability();
            try
            {
                regionReport = await Task.Run(() => RegionDiagnostics.Read());
                regionFresh = !regionReport.ReadChanged;
                RenderRegionReport();
            }
            catch (Exception ex)
            {
                regionFresh = false;
                Text("HomeRegionState").Text = "检测失败";
                Text("RegionStateText").Text = "检测失败 · 旧值已过期";
                if (regionReport == null) { Text("HomeRegionText").Text = "未知区域"; Text("RegionCurrentText").Text = "无法读取"; }
                SetRegionResult("区域检测失败，请重试：" + ex.Message);
            }
            finally
            {
                refreshingRegion = false;
                Button("RegionRefreshButton").IsEnabled = true;
                UpdateRegionAvailability();
            }
            if (refreshRegionAgain && !closingAllowed) { refreshRegionAgain = false; await RefreshRegionAsync(true); }
        }
        private void RenderRegionReport()
        {
            if (regionReport == null) return;
            var value = regionReport.Configured;
            Text("HomeRegionText").Text = Text("RegionCurrentText").Text = RegionCatalog.Name(value.LocaleName);
            string label = "已读取";
            bool amber = false;
            if (regionReport.ReadChanged) { label = "设置变化 · 请重检"; amber = true; }
            else if (busy || starting || regionReport.WriteBusy)
            {
                label = settings.Compatibility && value.LocaleName == "zh-TW" && (stage == "waiting" || stage == "initializing") ? "临时繁体 · 启动中" : "区域操作中";
                amber = true;
            }
            else if (regionReport.Pending.Length > 0) { label = "恢复待处理"; amber = true; }
            else if (regionReport.Backup != null) { amber = !RegionCatalog.Same(value, regionReport.Backup.Target); label = amber ? "与原区域不同" : "配置与原区域一致"; }
            if (!regionReport.ProbeMatches && !amber) { amber = true; label = regionReport.Probe == null ? "进程检测未完成" : "配置与读数不同"; }
            Text("HomeRegionState").Text = Text("RegionStateText").Text = label;
            var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(amber ? "#FFF0CF" : "#E4F1E4"));
            UI<Border>("HomeRegionBadge").Background = UI<Border>("RegionBadge").Background = color;
            var backup = regionReport.Backup;
            Text("RegionBackupLabel").Text = backup == null ? "最近一次启动前" : backup.Label;
            Text("RegionBackupText").Text = backup == null ? "暂无有效备份" : RegionCatalog.Name(backup.Target.LocaleName);
            Text("RegionBackupTime").Text = backup == null ? "可以手动选择区域；原记录会保留。" : "备份时间 " + backup.Journal.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm");
            Button("RegionRestoreButton").Content = backup != null && backup.Pending ? "↻  重新执行恢复" : "↻  恢复启动前原区域";
            var detail = new StringBuilder();
            detail.AppendLine("检测时间：" + regionReport.CheckedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            detail.AppendLine("配置区域：" + value.LocaleName + " / " + value.DefaultLanguage);
            detail.AppendLine("配置代码页：ANSI " + value.ACP + " / OEM " + value.OEMCP + " / Mac " + value.MACCP);
            if (regionReport.Probe != null)
                detail.AppendLine("新进程读数：ANSI " + regionReport.Probe.ACP + " / OEM " + regionReport.Probe.OEMCP + " / LCID " + regionReport.Probe.LCID.ToString("x4"));
            else detail.AppendLine("新进程检测：" + regionReport.ProbeError);
            detail.AppendLine(regionReport.ProbeMatches ? "配置与检查进程的区域、ANSI/OEM 读数一致。" : "配置与检查读数未确认一致，可能需要重启后完整生效。");
            detail.AppendLine("这些读数不代表游戏内部编码或文字已验证。");
            detail.AppendLine("待处理记录：" + regionReport.Pending.Length);
            if (backup != null)
            {
                detail.AppendLine("恢复目标：" + backup.Target.LocaleName + " / " + backup.Target.DefaultLanguage);
                detail.AppendLine("备份代码页：ANSI " + backup.Target.ACP + " / OEM " + backup.Target.OEMCP + " / Mac " + backup.Target.MACCP);
                detail.AppendLine("备份时间：" + backup.Journal.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                detail.AppendLine("备份会话：" + backup.Reference.Id);
            }
            Text("RegionDetailsText").Text = detail.ToString();
            UpdateRegionAvailability();
        }
        private void UpdateRegionAvailability()
        {
            if (UI<Button>("RegionApplyButton") == null) return;
            bool idle = !busy && !starting && !refreshingRegion && (regionReport == null || !regionReport.WriteBusy);
            bool readable = regionReport != null && regionFresh;
            Button("RegionRestoreButton").IsEnabled = idle && readable && regionReport.Backup != null;
            Button("RegionApplyButton").IsEnabled = idle && readable && !regionReport.GameRunning && regionReport.Configured.ACP != "65001";
            UI<ComboBox>("RegionCombo").IsEnabled = idle;
            string reason = !idle ? "启动或区域操作进行中，请等待完成。" : !readable ? "请先成功检测当前系统区域。" : regionReport.GameRunning ? "游戏或官方启动器正在运行；退出后可手动选区。" : regionReport.Configured.ACP == "65001" ? "当前启用 UTF-8，请通过 Windows 区域设置处理。" : "";
            Text("RegionAvailabilityText").Text = reason;
            Text("RegionAvailabilityText").Visibility = reason.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        private async Task StartRegionAction(string operation)
        {
            if (busy || starting || previewPath != null) return;
            await RefreshRegionAsync(true);
            if (busy || starting || refreshingRegion || !regionFresh || regionReport == null || regionReport.WriteBusy) { SetRegionResult("请等待当前操作结束并重新检测。"); return; }
            var report = regionReport;
            if (operation == "restore" && report.Backup == null) { SetRegionResult("没有有效备份，请手动选择区域。"); return; }
            if (operation == "set" && (report.GameRunning || report.Configured.ACP == "65001")) { UpdateRegionAvailability(); return; }
            string selected = (string)((ComboBoxItem)UI<ComboBox>("RegionCombo").SelectedItem).Tag;
            var target = operation == "restore" ? report.Backup.Target : RegionCatalog.Preset(selected);
            string message = "当前：" + RegionCatalog.Name(report.Configured.LocaleName) + "\n目标：" + RegionCatalog.Name(target.LocaleName) + "\n\n";
            message += operation == "restore" ? "将重新应用所示备份并核对完整配置。" : "这是系统级区域修改，会保留所选区域。完整生效需要重启电脑。";
            if (operation == "set" && report.Pending.Length > 0) message += "\n将同时记录对 " + report.Pending.Length + " 条待处理记录的人工修复，原记录会保留。";
            if (MessageBox.Show(this, message, operation == "restore" ? "确认再次恢复" : "确认应用区域", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            try
            {
                active = new SessionFiles(Path.Combine(AppPaths.SessionsRoot, Guid.NewGuid().ToString("N")));
                using (var me = Process.GetCurrentProcess())
                    JsonFile.Write(active.FilePath("region-request.json"), new RegionRequest { Operation = operation, LocaleName = selected, Source = operation == "restore" ? report.Backup.Reference : null, ExpectedCurrent = report.Configured, AcknowledgedPending = report.Pending, OwnerPid = me.Id, OwnerStartTicks = me.StartTime.ToUniversalTime().Ticks });
                latest = null; lastStatusTime = DateTime.MinValue; sessionStarted = DateTime.UtcNow;
                regionOperation = true; starting = true;
                ShowStatus("region-working", "请在 Windows 提示中允许修改系统区域…", true);
                string directory = active.DirectoryPath;
                await Task.Run(() => {
                    var info = new ProcessStartInfo(AppPaths.Executable, "--region " + Native.Quote(directory)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(AppPaths.Executable) };
                    using (var process = Process.Start(info)) if (process == null) throw new IOException("区域辅助进程未启动。");
                });
            }
            catch (Exception ex)
            {
                var native = ex as Win32Exception;
                ShowStatus("region-failed", native != null && native.NativeErrorCode == 1223 ? "管理员授权已取消，未执行区域修改。" : ex.Message, false);
                if (active != null) active.Log(ex.ToString());
                regionOperation = false;
            }
            finally
            {
                starting = false; UpdateRegionAvailability();
                if (closeWhenFinished && !busy && stage != "recovery") { closingAllowed = true; Close(); }
            }
        }
        private async Task PrepareRegionPreview()
        {
            try
            {
                regionReport = await Task.Run(() => RegionDiagnostics.Read());
                regionFresh = !regionReport.ReadChanged;
                RenderRegionReport();
            }
            catch (Exception ex) { SetRegionResult(ex.Message); }
            if (previewPane.StartsWith("region", StringComparison.Ordinal)) ShowPane("RegionPane");
            if (previewPane == "region-details") UI<Expander>("RegionDetailsExpander").IsExpanded = true;
        }
    }
}
