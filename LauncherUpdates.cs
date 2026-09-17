using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LaTaleGarden
{
    public partial class LauncherWindow
    {
        private UpdatePreferences updatePreferences;
        private UpdateRelease availableUpdate;
        private byte[] availableFeed;
        private AnnouncementFeed announcementFeed;
        private Announcement shownAnnouncement;
        private bool checkingUpdate, downloadingUpdate, installingUpdate, packageReady;
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private CancellationTokenSource downloadCancellation;
        private string updateMessage = "可以检查启动器的新版本。";

        private void InitializeUpdates()
        {
            updatePreferences = JsonFile.TryRead<UpdatePreferences>(UpdatePaths.Settings) ?? new UpdatePreferences();
            updatePreferences.Normalize();
            UI<CheckBox>("AutoCheckUpdates").IsChecked = updatePreferences.AutoCheck;
            UI<CheckBox>("AutoDownloadUpdates").IsChecked = updatePreferences.AutoDownload;
            UI<CheckBox>("ShowAnnouncements").IsChecked = updatePreferences.ShowAnnouncements;
            UI<CheckBox>("PreviewUpdates").IsChecked = updatePreferences.IncludePreview;
            Button("UpdatesButton").Click += (s, e) => ShowPane("UpdatesPane");
            Button("UpdatesBack").Click += (s, e) => ShowPane("HomePane");
            Button("AboutUpdatesButton").Click += async (s, e) => { ShowPane("UpdatesPane"); await CheckUpdates(true); };
            Button("CheckUpdateButton").Click += async (s, e) => await CheckUpdates(true);
            Button("ReleasePageButton").Click += (s, e) => OpenProjectLink(availableUpdate == null ? Program.ProjectUrl + "/releases" : availableUpdate.ReleaseUrl);
            Button("DownloadUpdateButton").Click += async (s, e) => { if (packageReady) await InstallUpdate(); else await DownloadUpdate(); };
            Button("CancelUpdateButton").Click += (s, e) => { if (downloadCancellation != null) downloadCancellation.Cancel(); };
            Button("IgnoreUpdateButton").Click += (s, e) => {
                if (availableUpdate == null) return;
                updatePreferences.IgnoredVersion = availableUpdate.Version;
                SaveUpdatePreferences(); RenderUpdate(); ShowPane("HomePane");
            };
            Button("LaterUpdateButton").Click += (s, e) => ShowPane("HomePane");
            Button("AnnouncementLinkButton").Click += (s, e) => { if (shownAnnouncement != null && UpdateProtocol.SafeLink(shownAnnouncement.Url)) OpenProjectLink(shownAnnouncement.Url); };
            Button("DismissAnnouncementButton").Click += (s, e) => {
                if (shownAnnouncement == null) return;
                updatePreferences.DismissedAnnouncements = new[] { shownAnnouncement.Id }.Concat(updatePreferences.DismissedAnnouncements).Distinct().Take(100).ToArray();
                SaveUpdatePreferences(); RenderAnnouncements();
            };
            if (previewPath == null)
            {
                try { if (File.Exists(UpdatePaths.CachedFeed)) AcceptFeed(File.ReadAllBytes(UpdatePaths.CachedFeed)); } catch (Exception ex) { UpdatePaths.Log("缓存未使用：" + ex.Message); }
                try { if (File.Exists(UpdatePaths.CachedAnnouncements)) announcementFeed = UpdateProtocol.ReadAnnouncements(File.ReadAllBytes(UpdatePaths.CachedAnnouncements), UpdateProtocol.PublicKey); } catch { }
                try { var result = UpdateInstaller.LastResult(); if (!string.IsNullOrEmpty(result)) Text("UpdateResultText").Text = result; } catch { }
            }
            RenderUpdate(); RenderAnnouncements();
            ContentRendered += async (s, e) => {
                if (previewPath != null) return;
                if (Program.StartupUpdateId != null)
                {
                    try { UpdateInstaller.Acknowledge(Program.StartupUpdateId); Program.StartupUpdateId = null; Text("UpdateResultText").Text = "已更新至 v" + Program.Version + "，原设置已保留。"; }
                    catch (Exception ex) { UpdatePaths.Log("启动确认失败：" + ex); closingAllowed = true; Close(); return; }
                }
                await Task.Run(() => UpdateInstaller.Cleanup());
                await Task.WhenAll(CheckUpdates(false), CheckAnnouncements(false));
            };
        }
        private bool SaveUpdatePreferences()
        {
            try { JsonFile.Write(UpdatePaths.Settings, updatePreferences); return true; }
            catch (Exception ex) { UpdatePaths.Log("更新偏好保存失败：" + ex.Message); return false; }
        }
        private bool SaveUpdateOptions()
        {
            updatePreferences.AutoCheck = UI<CheckBox>("AutoCheckUpdates").IsChecked == true;
            updatePreferences.AutoDownload = UI<CheckBox>("AutoDownloadUpdates").IsChecked == true;
            updatePreferences.ShowAnnouncements = UI<CheckBox>("ShowAnnouncements").IsChecked == true;
            updatePreferences.IncludePreview = UI<CheckBox>("PreviewUpdates").IsChecked == true;
            if (availableFeed != null) AcceptFeed(availableFeed);
            RenderAnnouncements();
            return SaveUpdatePreferences();
        }
        private void AcceptFeed(byte[] data)
        {
            var feed = UpdateProtocol.ReadFeed(data, UpdateProtocol.PublicKey);
            availableUpdate = UpdateProtocol.Select(feed, Program.Version, updatePreferences.IncludePreview, UpdateProtocol.WindowsBuild(), UpdateProtocol.FrameworkRelease());
            availableFeed = data; packageReady = false;
            if (availableUpdate != null)
                try { UpdateProtocol.VerifyPackage(UpdatePaths.Package(availableUpdate), availableUpdate); packageReady = true; } catch { }
        }
        private async Task CheckUpdates(bool manual)
        {
            if (checkingUpdate || downloadingUpdate || installingUpdate || updateLifetime.IsCancellationRequested) return;
            if (!manual && (!updatePreferences.AutoCheck || !UpdatePreferences.Due(updatePreferences.LastUpdateAttemptUtc, DateTime.UtcNow))) return;
            checkingUpdate = true; updateMessage = "正在检查新版本…"; RenderUpdate();
            updatePreferences.LastUpdateAttemptUtc = DateTime.UtcNow; SaveUpdatePreferences();
            try
            {
                byte[] bytes = await Task.Run(() => new UpdateClient().Document(UpdateProtocol.FeedUrl, updateLifetime.Token));
                AcceptFeed(bytes);
                JsonFile.Write(UpdatePaths.CachedFeed, UpdateProtocol.ReadJson<SignedDocument>(bytes));
                updatePreferences.LastSuccessUtc = DateTime.UtcNow; SaveUpdatePreferences();
                updateMessage = availableUpdate == null ? "当前没有适用于此电脑的新版本。" : packageReady ? "新版已下载，可以重启启动器完成更新。" : "发现新版本，可查看更新内容后下载。";
            }
            catch (Exception ex)
            {
                if (!updateLifetime.IsCancellationRequested)
                {
                    updateMessage = "暂时无法检查更新，可稍后重试或打开版本页面。";
                    if (availableUpdate != null) updateMessage += " 下方保留上次确认的版本信息。";
                    UpdatePaths.Log("检查失败：" + ex.Message);
                }
            }
            finally { checkingUpdate = false; RenderUpdate(); }
            if (manual) await CheckAnnouncements(true);
            if (!manual && availableUpdate != null && !packageReady && updatePreferences.AutoDownload && availableUpdate.Version != updatePreferences.IgnoredVersion &&
                !updateLifetime.IsCancellationRequested) await DownloadUpdate();
        }
        private async Task CheckAnnouncements(bool manual)
        {
            if (!updatePreferences.ShowAnnouncements || updateLifetime.IsCancellationRequested ||
                (!manual && !UpdatePreferences.Due(updatePreferences.LastAnnouncementAttemptUtc, DateTime.UtcNow))) return;
            updatePreferences.LastAnnouncementAttemptUtc = DateTime.UtcNow; SaveUpdatePreferences();
            try
            {
                byte[] bytes = await Task.Run(() => new UpdateClient().Document(UpdateProtocol.AnnouncementsUrl, updateLifetime.Token));
                announcementFeed = UpdateProtocol.ReadAnnouncements(bytes, UpdateProtocol.PublicKey);
                JsonFile.Write(UpdatePaths.CachedAnnouncements, UpdateProtocol.ReadJson<SignedDocument>(bytes));
                Text("AnnouncementEmptyText").Text = "暂时没有新的项目动态。";
            }
            catch (Exception ex)
            {
                Text("AnnouncementEmptyText").Text = "暂时无法获取项目动态，可稍后重试。";
                if (!updateLifetime.IsCancellationRequested) UpdatePaths.Log("项目动态未刷新：" + ex.Message);
            }
            RenderAnnouncements();
        }
        private async Task DownloadUpdate()
        {
            if (availableUpdate == null || downloadingUpdate || checkingUpdate || installingUpdate || updateLifetime.IsCancellationRequested) return;
            var selected = availableUpdate;
            if (updatePreferences.IgnoredVersion == selected.Version) { updatePreferences.IgnoredVersion = ""; SaveUpdatePreferences(); }
            downloadingUpdate = true; packageReady = false;
            downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
            UI<ProgressBar>("UpdateProgress").Value = 0;
            updateMessage = "正在下载 v" + selected.Version + "…"; RenderUpdate();
            var progress = new Progress<long>(bytes => { UI<ProgressBar>("UpdateProgress").Value = bytes * 100.0 / selected.Size; Text("UpdateStatusText").Text = "正在下载 " + (bytes * 100 / selected.Size) + "%"; });
            try
            {
                await Task.Run(() => new UpdateClient().Download(selected, UpdatePaths.Package(selected), bytes => ((IProgress<long>)progress).Report(bytes), downloadCancellation.Token));
                packageReady = true; updateMessage = "下载并校验完成。重启启动器即可更新，设置会保留。";
            }
            catch (Exception ex)
            {
                updateMessage = downloadCancellation.IsCancellationRequested ? "下载已取消，可以稍后重新下载。" : "下载未完成或校验失败，可重试或手动下载。";
                UpdatePaths.Log("下载结束：" + ex.Message);
            }
            finally { downloadingUpdate = false; downloadCancellation.Dispose(); downloadCancellation = null; RenderUpdate(); }
        }
        private async Task InstallUpdate()
        {
            if (!packageReady || availableUpdate == null || installingUpdate || checkingUpdate || downloadingUpdate) return;
            if (busy || starting || regionOperation || refreshingRegion) { updateMessage = "请等待启动、检测或区域恢复完成，再更新启动器。"; RenderUpdate(); return; }
            installingUpdate = true; updateMessage = "正在准备更新…"; RenderUpdate(); UpdateRegionAvailability();
            Button("LaunchButton").IsEnabled = false; Button("SettingsButton").IsEnabled = false;
            Process helper = null; string jobId = null;
            try
            {
                var candidate = availableUpdate; var feed = availableFeed;
                string id = await Task.Run(() => UpdateInstaller.Prepare(candidate, feed, UpdatePaths.Package(candidate)));
                jobId = id;
                bool elevate = !UpdateInstaller.CanWriteDirectory(AppPaths.Executable);
                if (elevate) updateMessage = "此位置需要管理员权限，请在 Windows 提示中允许更新。";
                RenderUpdate();
                helper = await Task.Run(() => UpdateInstaller.StartHelper(id, elevate));
                if (helper == null) throw new IOException("更新助手未启动。");
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    var result = UpdateInstaller.Status(id);
                    if (result != null && result.Stage == "ready")
                    {
                        closingAllowed = true; Close(); return;
                    }
                    if (result != null && result.Stage == "failed") throw new IOException(result.Message);
                    if (helper.HasExited) throw new IOException("更新助手已退出，请稍后重试。");
                    await Task.Delay(100);
                }
                throw new IOException("更新助手没有响应，旧版仍可继续使用。");
            }
            catch (Exception ex)
            {
                if (jobId != null) { try { File.WriteAllText(Path.Combine(UpdateInstaller.JobPath(jobId), "cancel"), "1"); } catch { } }
                var native = ex as Win32Exception;
                updateMessage = native != null && native.NativeErrorCode == 1223 ? "已取消管理员授权，尚未替换启动器。" : ex.Message;
                UpdatePaths.Log("安装未完成：" + ex);
            }
            finally
            {
                if (helper != null) helper.Dispose();
                installingUpdate = false;
                if (!closingAllowed) { ShowStatus(stage, Text("StatusMessage").ToolTip as string ?? Text("StatusMessage").Text, busy); RenderUpdate(); }
            }
        }
        private void RenderUpdate()
        {
            if (updatePreferences == null) return;
            bool working = checkingUpdate || downloadingUpdate || installingUpdate;
            Text("UpdateVersionText").Text = availableUpdate == null ? "当前版本 v" + Program.Version : "v" + Program.Version + "  →  v" + availableUpdate.Version;
            Text("UpdateStatusText").Text = updateMessage;
            Text("UpdateNotesText").Text = availableUpdate == null ? "有新版本时，这里会显示更新内容。" : availableUpdate.Notes;
            Text("UpdateChannelText").Text = updatePreferences.IncludePreview ? "接收正式版与公开测试版更新" : "仅接收正式版更新";
            Button("CheckUpdateButton").IsEnabled = !working;
            Button("AboutUpdatesButton").IsEnabled = !working;
            Button("DownloadUpdateButton").Visibility = availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
            Button("DownloadUpdateButton").IsEnabled = !working;
            Button("DownloadUpdateButton").Content = installingUpdate ? "正在准备更新…" : packageReady ? "重启启动器并更新" : "下载更新";
            Button("IgnoreUpdateButton").Visibility = availableUpdate == null || working ? Visibility.Collapsed : Visibility.Visible;
            Button("CancelUpdateButton").Visibility = downloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
            UI<ProgressBar>("UpdateProgress").Visibility = downloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
            UI<CheckBox>("AutoCheckUpdates").IsEnabled = UI<CheckBox>("AutoDownloadUpdates").IsEnabled =
                UI<CheckBox>("ShowAnnouncements").IsEnabled = UI<CheckBox>("PreviewUpdates").IsEnabled = !working;
            Button("SaveSettingsButton").IsEnabled = !working;
            UpdateBadge();
        }
        private void UpdateBadge()
        {
            bool notifyUpdate = availableUpdate != null && availableUpdate.Version != updatePreferences.IgnoredVersion;
            Button("UpdatesButton").Content = notifyUpdate ? (packageReady ? "新版已就绪 ↗" : "发现新版本 ↗") : shownAnnouncement != null ? "有新动态 ↗" : "更新与动态 ›";
        }
        private void RenderAnnouncements()
        {
            shownAnnouncement = updatePreferences.ShowAnnouncements && announcementFeed != null
                ? UpdateProtocol.VisibleAnnouncement(announcementFeed, updatePreferences.DismissedAnnouncements, DateTime.UtcNow) : null;
            UI<StackPanel>("AnnouncementContent").Visibility = shownAnnouncement == null ? Visibility.Collapsed : Visibility.Visible;
            Text("AnnouncementEmptyText").Visibility = shownAnnouncement == null ? Visibility.Visible : Visibility.Collapsed;
            if (!updatePreferences.ShowAnnouncements) Text("AnnouncementEmptyText").Text = "项目动态已关闭，可在设置中开启。";
            if (shownAnnouncement != null)
            {
                Text("AnnouncementTitleText").Text = shownAnnouncement.Title;
                Text("AnnouncementBodyText").Text = shownAnnouncement.Text;
                Button("AnnouncementLinkButton").Visibility = string.IsNullOrEmpty(shownAnnouncement.Url) ? Visibility.Collapsed : Visibility.Visible;
            }
            UpdateBadge();
        }
        private void PrepareUpdatePreview()
        {
            if (previewPane == "settings") ShowPane("SettingsPane");
            if (!previewPane.StartsWith("updates", StringComparison.Ordinal)) return;
            ShowPane("UpdatesPane");
            if (previewPane == "updates-available" || previewPane == "updates-ready")
            {
                availableUpdate = new UpdateRelease { Version = "1.1.4", Notes = "• 优化启动体验\n• 改进区域检测与操作提示" };
                packageReady = previewPane == "updates-ready";
                updateMessage = packageReady ? "下载并校验完成。重启启动器即可更新，设置会保留。" : "发现新版本，可查看更新内容后下载。";
            }
            announcementFeed = new AnnouncementFeed { Schema = 1, Items = new[] { new Announcement {
                Id = "preview", Title = "欢迎来到 LaTale Garden", Text = "新功能与维护通知会发布在这里。\n使用中遇到问题，欢迎到 GitHub 反馈。",
                StartsUtc = "2020-01-01T00:00:00Z", ExpiresUtc = "2099-01-01T00:00:00Z", Url = Program.IssuesUrl
            } } };
            RenderUpdate(); RenderAnnouncements();
        }
    }
}
