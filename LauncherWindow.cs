using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace LaTaleGarden
{
    public partial class LauncherWindow : Window
    {
        private readonly UserControl surface;
        private readonly DispatcherTimer timer;
        private readonly Preferences settings;
        private readonly string previewPath;
        private SessionFiles active;
        private SessionStatus latest;
        private DateTime lastStatusTime;
        private bool busy, starting, closeWhenFinished, closingAllowed, loading = true;
        private ClientInfo currentClient;
        private string stage = "ready";
        private string aboutReturnPane = "HomePane";
        private Forms.NotifyIcon tray;
        private Drawing.Icon trayIcon;
        private System.Threading.EventWaitHandle activationEvent;
        private DateTime sessionStarted;

        private T UI<T>(string name) where T : class { return surface.FindName(name) as T; }
        private Button Button(string name) { return UI<Button>(name); }
        private TextBlock Text(string name) { return UI<TextBlock>(name); }
        private static BitmapImage LoadImage(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
        public LauncherWindow(string previewPath, string previewPane = "home")
        {
            this.previewPath = previewPath;
            this.previewPane = previewPane;
            settings = JsonFile.TryRead<Preferences>(AppPaths.PreferencesPath) ?? new Preferences();
            settings.Normalize();
            if (!DirectoryIsValid(settings.GameDirectory))
            {
                string alongside = Path.GetDirectoryName(AppPaths.Executable);
                settings.GameDirectory = DirectoryIsValid(alongside) ? alongside : "";
            }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) surface = (UserControl)XamlReader.Load(stream);
            Title = "LaTale Garden · 彩虹岛台服启动器";
            Icon = LoadImage("app-icon.png");
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            double scale = Math.Min(1.0, Math.Min((SystemParameters.WorkArea.Width - 60) / 1080.0, (SystemParameters.WorkArea.Height - 60) / 690.0));
            Width = 1080 * scale; Height = 690 * scale;
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = surface }; Content = viewbox;
            UI<Grid>("Surface").Clip = new RectangleGeometry(new Rect(0, 0, 1078, 688), 16, 16);
            UI<Image>("HeroImage").Source = LoadImage("hero.png");
            Text("VersionText").Text = "非官方辅助启动器  ·  v" + Program.Version;
            Text("AboutVersionText").Text = "版本 " + Program.Version + " · 公开测试版";
            Button("AboutAuthorButton").Content = "@" + Program.Author + " ↗";
            Button("AboutAuthorButton").ToolTip = Program.AuthorUrl;
            Button("AboutProjectButton").ToolTip = Program.ProjectUrl;
            Button("AboutFeedbackButton").ToolTip = Button("FeedbackButton").ToolTip = Program.IssuesUrl;
            Button("LaunchButton").Tag = LoadImage("launch-button.png");
            UI<TextBox>("DirectoryText").Text = settings.GameDirectory;
            UI<CheckBox>("CompatibilityToggle").IsChecked = settings.Compatibility;
            Text("SystemInfoText").Text = Native.OSLabel() + "\n当前进程代码页：" + Native.GetACP() + " · 原生 x64 / .NET Framework 4.8";
            AddChoices("WaitCombo", new[] { 5, 15, 30, 60, 120 }, "分钟", settings.WaitMinutes);
            AddChoices("SettleCombo", new[] { 10, 20, 30, 60, 120 }, "秒", settings.SettleSeconds);
            UI<CheckBox>("MinimizeCheck").IsChecked = settings.MinimizeAfterStart;
            UI<FrameworkElement>("DragBar").MouseLeftButtonDown += (s, e) => { if (e.OriginalSource is Border || e.OriginalSource is Grid) DragMove(); };
            UI<Grid>("Artwork").MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            Button("MinimizeButton").Click += (s, e) => WindowState = WindowState.Minimized;
            Button("CloseButton").Click += (s, e) => Close();
            Button("SettingsButton").Click += (s, e) => ShowPane("SettingsPane");
            Button("AboutButton").Click += (s, e) => {
                string current = new[] { "HomePane", "SettingsPane", "LogsPane", "RegionPane", "UpdatesPane" }.FirstOrDefault(name => UI<Grid>(name).Visibility == Visibility.Visible);
                if (current != null) aboutReturnPane = current;
                ShowPane("AboutPane");
            };
            Button("AboutBack").Click += (s, e) => ShowPane(aboutReturnPane);
            Button("AboutAuthorButton").Click += (s, e) => OpenProjectLink(Program.AuthorUrl);
            Button("AboutProjectButton").Click += (s, e) => OpenProjectLink(Program.ProjectUrl);
            Button("AboutFeedbackButton").Click += (s, e) => OpenProjectLink(Program.IssuesUrl);
            Button("FeedbackButton").Click += (s, e) => OpenProjectLink(Program.IssuesUrl);
            Button("AboutLogsButton").Click += (s, e) => { RefreshLogs(); ShowPane("LogsPane"); };
            Button("SettingsBack").Click += (s, e) => ShowPane("HomePane");
            Button("LogsBack").Click += (s, e) => ShowPane("HomePane");
            Button("LogsButton").Click += (s, e) => { RefreshLogs(); ShowPane("LogsPane"); };
            Button("ChooseDirectoryButton").Click += (s, e) => ChooseDirectory();
            Button("OpenDirectoryButton").Click += (s, e) => OpenFolder(settings.GameDirectory);
            Button("SaveSettingsButton").Click += (s, e) => SaveSettings();
            Button("CancelButton").Click += (s, e) => CancelLaunch();
            Button("LaunchButton").Click += async (s, e) => await PrimaryAction();
            Button("ExportLogsButton").Click += (s, e) => ExportLogs();
            Button("OpenLogsButton").Click += (s, e) => OpenFolder(active == null ? AppPaths.DataRoot : active.DirectoryPath);
            UI<CheckBox>("CompatibilityToggle").Checked += (s, e) => SaveMode();
            UI<CheckBox>("CompatibilityToggle").Unchecked += (s, e) => SaveMode();
            InitializeUpdates();
            InitializeRegion();
            Closing += OnClosing;
            Closed += (s, e) => { updateLifetime.Cancel(); if (tray != null) { tray.Visible = false; tray.Dispose(); } if (trayIcon != null) trayIcon.Dispose(); if (activationEvent != null) activationEvent.Dispose(); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => PollStatus();
            Loaded += async (s, e) => {
                loading = false;
                if (previewPath != null) { await PrepareRegionPreview(); PrepareUpdatePreview(); await Dispatcher.InvokeAsync(new Action(SavePreview), DispatcherPriority.ApplicationIdle); return; }
                activationEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, Program.ActivationEventName);
                string pending = AppPaths.PendingSessions().FirstOrDefault();
                if (pending != null)
                {
                    active = new SessionFiles(pending);
                    ShowStatus("recovery", "上次的原设置尚未确认恢复。请先完成恢复，再启动游戏。", false);
                }
                else DetectExistingClient();
                timer.Start();
                await RefreshRegionAsync(true);
            };
            if (string.IsNullOrEmpty(settings.GameDirectory)) ShowStatus("ready", "首次使用：请先选择彩虹岛台服安装目录。", false);
        }
        private static bool DirectoryIsValid(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "LaTaleLauncher.exe")) && File.Exists(Path.Combine(path, "LaTaleClient.exe")); } catch { return false; }
        }
        private void ShowPane(string pane)
        {
            foreach (string name in new[] { "HomePane", "SettingsPane", "LogsPane", "RegionPane", "AboutPane", "UpdatesPane" }) UI<Grid>(name).Visibility = name == pane ? Visibility.Visible : Visibility.Collapsed;
        }
        private void OpenProjectLink(string url)
        {
            try { using (var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { } }
            catch (Exception ex) { MessageBox.Show(this, "无法打开浏览器，请手动访问：\n" + url + "\n\n" + ex.Message, "打开 GitHub", MessageBoxButton.OK, MessageBoxImage.Information); }
        }
        private void AddChoices(string name, int[] numbers, string unit, int selected)
        {
            var combo = UI<ComboBox>(name);
            foreach (int number in numbers)
            {
                var item = new ComboBoxItem { Content = number + " " + unit, Tag = number };
                combo.Items.Add(item); if (number == selected) combo.SelectedItem = item;
            }
            if (combo.SelectedIndex < 0) combo.SelectedIndex = 1;
        }
        private void SaveMode()
        {
            if (loading) return;
            settings.Compatibility = UI<CheckBox>("CompatibilityToggle").IsChecked == true;
            SavePreferences();
        }
        private void SaveSettings()
        {
            settings.WaitMinutes = (int)((ComboBoxItem)UI<ComboBox>("WaitCombo").SelectedItem).Tag;
            settings.SettleSeconds = (int)((ComboBoxItem)UI<ComboBox>("SettleCombo").SelectedItem).Tag;
            settings.MinimizeAfterStart = UI<CheckBox>("MinimizeCheck").IsChecked == true;
            settings.Normalize();
            if (SavePreferences()) Text("SettingsNote").Text = SaveUpdateOptions() ? "已保存，下次启动时使用。" : "更新偏好未保存，请稍后重试。";
        }
        private bool SavePreferences()
        {
            try { JsonFile.Write(AppPaths.PreferencesPath, settings); return true; }
            catch (Exception ex) { MessageBox.Show(this, "设置保存失败：" + ex.Message, "彩虹岛启动器", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }
        private void ChooseDirectory()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择游戏目录中的 LaTaleLauncher.exe", Filter = "彩虹岛官方启动器|LaTaleLauncher.exe", CheckFileExists = true };
            if (Directory.Exists(settings.GameDirectory)) dialog.InitialDirectory = settings.GameDirectory;
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (!string.Equals(Path.GetFileName(dialog.FileName), "LaTaleLauncher.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 LaTaleLauncher.exe。");
                string directory = Path.GetDirectoryName(dialog.FileName); WindowsPlatform.ValidateDirectory(directory);
                settings.GameDirectory = directory; UI<TextBox>("DirectoryText").Text = directory; SavePreferences();
                ShowStatus("ready", "游戏目录已确认，可以启动。", false); DetectExistingClient();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "游戏目录", MessageBoxButton.OK, MessageBoxImage.Information); }
        }
        private void OpenFolder(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { MessageBox.Show(this, "目录尚不存在。请先选择游戏目录或启动一次。", "彩虹岛启动器"); return; }
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Native.Quote(directory)) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开目录"); }
        }
        private async Task PrimaryAction()
        {
            if (starting || installingUpdate || UpdateInstaller.Applying()) return;
            if (stage == "running" && currentClient != null && Native.SameProcess(currentClient.Id, currentClient.StartTicks)) { Native.FocusProcess(currentClient.Id, currentClient.StartTicks); return; }
            if (stage == "waiting" || stage == "initializing") { FocusOfficial(); return; }
            if (busy) return;
            string pending = AppPaths.PendingSessions().FirstOrDefault();
            bool recover = stage == "recovery" || pending != null;
            if (recover)
            {
                if (pending != null) active = new SessionFiles(pending);
                if (active == null) { ShowStatus("failed", "未找到恢复记录，请查看启动日志。", false); return; }
                if (!RegionCatalog.ValidJournal(active.ReadJournal(), Path.GetFileName(active.DirectoryPath)))
                {
                    ShowPane("RegionPane"); SetRegionResult("恢复记录无法使用，请查看检测结果并手动选择区域。"); await RefreshRegionAsync(true); return;
                }
            }
            else
            {
                if (!DirectoryIsValid(settings.GameDirectory)) { ChooseDirectory(); return; }
                if (DetectExistingClient()) return;
                active = new SessionFiles(Path.Combine(AppPaths.SessionsRoot, Guid.NewGuid().ToString("N")));
                var me = Process.GetCurrentProcess();
                var request = new LaunchRequest { GameDirectory = settings.GameDirectory, Compatibility = settings.Compatibility, WaitSeconds = settings.WaitMinutes * 60, SettleSeconds = settings.SettleSeconds, OwnerPid = me.Id, OwnerStartTicks = me.StartTime.ToUniversalTime().Ticks };
                JsonFile.Write(active.FilePath("request.json"), request);
                SavePreferences();
            }
            ShowPane("HomePane");
            sessionStarted = DateTime.UtcNow;
            ShowStatus(recover ? "restoring" : "checking", "请在 Windows 提示中允许启动辅助进程。", true);
            lastStatusTime = DateTime.MinValue; latest = null; starting = true;
            string selectedSession = active.DirectoryPath;
            try
            {
                await Task.Run(() => {
                    var info = new ProcessStartInfo(AppPaths.Executable, (recover ? "--recover " : "--worker ") + Native.Quote(selectedSession)) {
                        UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(AppPaths.Executable), WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var worker = Process.Start(info)) { if (worker == null) throw new IOException("辅助进程未启动。"); }
                });
            }
            catch (Win32Exception ex)
            {
                bool pendingRestore = active.ReadJournal() != null && active.ReadJournal().Pending;
                ShowStatus(pendingRestore ? "recovery" : "cancelled", ex.NativeErrorCode == 1223 ? "管理员授权已取消。" + (pendingRestore ? " 原设置仍需恢复。" : "本次未修改系统设置。") : ex.Message, false);
                active.Log("管理员进程未启动：" + ex.Message);
                if (closeWhenFinished && !pendingRestore) { closingAllowed = true; Close(); }
            }
            catch (Exception ex) { ShowStatus(recover ? "recovery" : "failed", ex.Message, false); active.Log(ex.ToString()); }
            finally { starting = false; }
        }
        private void CancelLaunch()
        {
            if (active == null) return;
            try { File.WriteAllText(active.FilePath("cancel.request"), "cancel"); Text("StatusMessage").Text = "正在取消等待，随后恢复原设置…"; Button("CancelButton").IsEnabled = false; }
            catch (Exception ex) { MessageBox.Show(this, "取消请求写入失败：" + ex.Message, "彩虹岛启动器"); }
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (closingAllowed || previewPath != null) { timer.Stop(); return; }
            if (installingUpdate) { e.Cancel = true; return; }
            if (busy || starting)
            {
                e.Cancel = true; closeWhenFinished = true; ShowPane("HomePane"); CancelLaunch();
            }
            else if (stage == "recovery")
            {
                if (MessageBox.Show(this, "原区域设置尚未确认恢复。建议先点击“恢复原设置”。\n\n仍然退出吗？恢复记录会保留，下次打开时继续提示。", "恢复尚未完成", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.No) { e.Cancel = true; ShowPane("HomePane"); }
                else timer.Stop();
            }
            else timer.Stop();
        }
        private void PollStatus()
        {
            if (activationEvent != null && activationEvent.WaitOne(0)) RestoreFromTray();
            if (active != null)
            {
                SessionStatus status = JsonFile.TryRead<SessionStatus>(active.FilePath("status.json"));
                if (status != null && status.UpdatedUtc != lastStatusTime)
                {
                    bool regionChanged = status.Stage != stage;
                    lastStatusTime = status.UpdatedUtc; latest = status;
                    if (status.ClientPid > 0) currentClient = new ClientInfo { Id = status.ClientPid, StartTicks = status.ClientStartTicks };
                    ShowStatus(status.Stage, status.Message, !status.Finished);
                    if (status.Finished || regionChanged) RefreshRegionFromEvent();
                    if (status.Finished) regionOperation = false;
                    Text("CountdownText").Visibility = status.RemainingSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (status.RemainingSeconds > 0) Text("CountdownText").Text = "最长还等待 " + (status.RemainingSeconds / 60) + ":" + (status.RemainingSeconds % 60).ToString("00");
                    if (status.Finished && status.Stage != "recovery")
                    {
                        if (closeWhenFinished) { closingAllowed = true; Close(); return; }
                        if (status.Stage == "running" && settings.MinimizeAfterStart) MinimizeToTray();
                    }
                    if (UI<Grid>("LogsPane").Visibility == Visibility.Visible) RefreshLogs();
                }
                Journal journal = active.ReadJournal();
                if (journal != null && journal.Pending && !Native.SameProcess(journal.WorkerPid, journal.WorkerStartTicks) && DateTime.UtcNow - (latest == null ? journal.CreatedUtc : latest.UpdatedUtc) > TimeSpan.FromSeconds(90))
                    ShowStatus("recovery", "辅助进程已退出，恢复仍未确认完成。请重试恢复。", false);
                else if (latest != null && !latest.Finished && (journal == null || !journal.Pending) && DateTime.UtcNow - latest.UpdatedUtc > TimeSpan.FromSeconds(90))
                    ShowStatus("failed", "启动进程已停止更新状态。请查看日志后重试。", false);
                else if (busy && !starting && latest == null && journal == null && DateTime.UtcNow - sessionStarted > TimeSpan.FromSeconds(30))
                    ShowStatus("failed", "辅助进程没有返回状态，请查看日志后重试。", false);
            }
            if (!busy && !starting && stage == "running" && currentClient != null && !Native.SameProcess(currentClient.Id, currentClient.StartTicks))
            {
                currentClient = null; ShowStatus("ready", "游戏已退出，随时可以再次出发。", false);
            }
        }
        private void ShowStatus(string next, string message, bool isBusy)
        {
            stage = next; busy = isBusy;
            string title = "准备就绪", action = "启动游戏";
            switch (next)
            {
                case "checking": title = "正在检查"; action = "检查中…"; break;
                case "preparing": title = "准备运行环境"; action = "准备中…"; break;
                case "waiting": title = "等你开始冒险"; action = "显示启动器"; break;
                case "initializing": title = "正在进入游戏"; action = "显示游戏"; break;
                case "restoring": title = "恢复原设置"; action = "恢复中…"; break;
                case "running": title = "游戏已启动"; action = "回到游戏"; break;
                case "failed": title = "这次没有启动成功"; action = "重新启动"; break;
                case "recovery": title = "原设置尚未恢复"; action = "恢复原设置"; break;
                case "cancelled": title = "已停止等待"; break;
                case "region-working": title = "正在处理区域"; action = "处理中…"; break;
                case "region-done": title = "区域配置已核对"; break;
                case "region-failed": title = "区域操作未完成"; break;
            }
            Text("StatusTitle").Text = title; Text("StatusMessage").Text = message.Length > 90 ? message.Substring(0, 90) + "…详见启动记录。" : message;
            Text("StatusMessage").ToolTip = message;
            if (regionOperation || next.StartsWith("region-", StringComparison.Ordinal)) SetRegionResult(message);
            Text("LaunchLabel").Text = action;
            System.Windows.Automation.AutomationProperties.SetName(Button("LaunchButton"), action);
            Text("LaunchIcon").Text = next == "recovery" ? "↻" : "▶";
            UI<System.Windows.Shapes.Ellipse>("StateDot").Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(next == "recovery" || next == "failed" ? "#D88946" : busy ? "#619FBE" : "#31A967"));
            Button("LaunchButton").IsEnabled = !busy || next == "waiting" || next == "initializing";
            Button("ChooseDirectoryButton").IsEnabled = !busy && next != "recovery" && next != "running";
            Button("SettingsButton").IsEnabled = !busy && next != "recovery";
            UI<CheckBox>("CompatibilityToggle").IsEnabled = !busy && next != "recovery";
            Button("CancelButton").Visibility = busy && next != "restoring" && next != "region-working" ? Visibility.Visible : Visibility.Collapsed;
            Button("CancelButton").IsEnabled = true;
            Text("CountdownText").Visibility = Visibility.Collapsed;
            Text("FooterText").Text = next == "recovery" ? "恢复完成前请保留窗口" : "游戏更新与登录由官方启动器完成";
            UpdateRegionAvailability();
        }
        private bool DetectExistingClient()
        {
            if (string.IsNullOrEmpty(settings.GameDirectory)) return false;
            foreach (var process in Process.GetProcessesByName("LaTaleClient"))
                using (process)
                    try
                    {
                        if (!Native.SamePath(Native.ProcessPath(process.Id), Path.Combine(settings.GameDirectory, "LaTaleClient.exe"))) continue;
                        currentClient = new ClientInfo { Id = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks };
                        ShowStatus("running", "检测到已运行的游戏，可直接切回游戏窗口。", false); return true;
                    }
                    catch { }
            return false;
        }
        private void FocusOfficial()
        {
            if (stage == "initializing" && currentClient != null) { Native.FocusProcess(currentClient.Id, currentClient.StartTicks); return; }
            foreach (var process in Process.GetProcessesByName("LaTaleLauncher"))
                using (process)
                    try { if (Native.SamePath(Native.ProcessPath(process.Id), Path.Combine(settings.GameDirectory, "LaTaleLauncher.exe"))) Native.FocusProcess(process.Id, process.StartTime.ToUniversalTime().Ticks); } catch { }
        }
        private string DiagnosticText()
        {
            string result = "LaTale Garden " + Program.Version + "\r\n" + Native.OSLabel() + "\r\n进程 ACP=" + Native.GetACP() + "；系统 LCID=" + Native.GetSystemDefaultLCID() + "\r\n游戏目录=" + settings.GameDirectory + "\r\n兼容模式=" + settings.Compatibility + "\r\n\r\n" + Text("RegionDetailsText").Text + "\r\n\r\n";
            try { string updateLog = Path.Combine(UpdatePaths.Root, "update.log"); if (File.Exists(updateLog)) result += "启动器更新记录：\r\n" + File.ReadAllText(updateLog) + "\r\n\r\n"; } catch { }
            string directory = active == null ? AppPaths.LatestSession() : active.DirectoryPath;
            if (directory != null && File.Exists(Path.Combine(directory, "launch.log")))
            {
                try { result += File.ReadAllText(Path.Combine(directory, "launch.log"), Encoding.UTF8); } catch (Exception ex) { result += "日志读取失败：" + ex.Message; }
                Journal journal = JsonFile.TryRead<Journal>(Path.Combine(directory, "recovery.json"));
                if (journal != null) result += "\r\n恢复待处理=" + journal.Pending + "; 原区域=" + (journal.Original == null ? "未知" : journal.Original.LocaleName);
            }
            else result += "尚无启动记录。点击启动游戏后，检查、启动与恢复过程会记录在这里。";
            return result;
        }
        private void RefreshLogs() { UI<TextBox>("LogText").Text = DiagnosticText(); UI<TextBox>("LogText").ScrollToEnd(); }
        private void ExportLogs()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出本地诊断日志", FileName = "LaTale诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt", Filter = "文本文件|*.txt" };
            if (dialog.ShowDialog(this) == true)
                try { File.WriteAllText(dialog.FileName, DiagnosticText(), new UTF8Encoding(true)); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败"); }
        }
        private void MinimizeToTray()
        {
            if (tray == null)
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                using (var icon = new Drawing.Icon(stream, new Drawing.Size(32, 32))) trayIcon = (Drawing.Icon)icon.Clone();
                tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "LaTale Garden · 彩虹岛台服启动器" };
                tray.DoubleClick += (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("打开启动器", null, (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray)));
                menu.Items.Add("退出", null, (s, e) => Dispatcher.BeginInvoke(new Action(() => { RestoreFromTray(); Close(); })));
                tray.ContextMenuStrip = menu;
            }
            tray.Visible = true; Hide();
        }
        private void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); if (tray != null) tray.Visible = false; }
        private void SavePreview()
        {
            surface.Measure(new Size(1080, 690)); surface.Arrange(new Rect(0, 0, 1080, 690)); surface.UpdateLayout();
            var image = new RenderTargetBitmap(1620, 1035, 144, 144, PixelFormats.Pbgra32); image.Render(surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.GetFullPath(previewPath))) png.Save(stream);
            closingAllowed = true; Close();
        }
    }
}
