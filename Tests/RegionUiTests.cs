using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LaTaleGarden;

public static class RegionUiTests
{
    static LauncherWindow window;
    static UserControl surface;
    static List<string> results = new List<string>();
    static void Set(string name, object value) { typeof(LauncherWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value); }
    static void Call(string name, params object[] args) { typeof(LauncherWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args); }
    static T UI<T>(string name) where T : class { return surface.FindName(name) as T; }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); results.Add("PASS " + message); Console.WriteLine(results[results.Count - 1]); }
    static RegionReport Report()
    {
        var snapshot = RegionCatalog.Preset("zh-CN");
        return new RegionReport { Configured = snapshot, Probe = new ProcessLocaleProbe { ACP = 936, OEMCP = 936, LCID = 0x804 }, CheckedUtc = DateTime.UtcNow, Pending = new JournalReference[0], Backup = new RegionBackup { Target = snapshot, Label = "最近一次启动前", Reference = new JournalReference { Id = Guid.NewGuid().ToString("N") }, Journal = new Journal { CreatedUtc = DateTime.UtcNow } } };
    }
    static void Render(RegionReport report)
    {
        Set("regionReport", report); Set("regionFresh", !report.ReadChanged); Call("RenderRegionReport");
        surface.Measure(new Size(1080, 690)); surface.Arrange(new Rect(0, 0, 1080, 690)); surface.UpdateLayout();
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        try
        {
            new Application();
            window = new LauncherWindow(Path.Combine(root, "unused-preview.png"));
            surface = (UserControl)typeof(LauncherWindow).GetField("surface", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Set("compatibilityAvailable", false); Call("ShowStatus", "ready", "", false);
            Check(!UI<CheckBox>("CompatibilityToggle").IsEnabled && UI<CheckBox>("CompatibilityToggle").IsChecked == false && UI<TextBlock>("LaunchLabel").Text == "普通启动" && UI<TextBlock>("StatusMessage").Text.Contains("不修复"), "unsupported Windows disables compatibility and clearly offers only standard launch");
            Call("ShowStatus", "recovery", "测试恢复", false);
            Check(UI<Button>("LaunchButton").IsEnabled && UI<TextBlock>("LaunchLabel").Text == "恢复原设置", "unsupported Windows preserves the emergency recovery action");
            Set("compatibilityAvailable", true); Call("ShowStatus", "ready", "", false);
            Check(UI<CheckBox>("CompatibilityToggle").IsEnabled && UI<TextBlock>("CompatibilityHint").Text.Contains("仅 Win11"), "Windows 11 enables the original temporary locale mode");
            Check(UI<Button>("OpenOfficialButton").IsEnabled, "idle home exposes the manual official-launch fallback");
            Call("ShowStatus", "client-started", "已恢复原区域，请确认游戏文字。", false);
            Check(UI<TextBlock>("StatusTitle").Text == "客户端已启动" && UI<TextBlock>("LaunchLabel").Text == "显示游戏" && UI<Button>("LaunchButton").IsEnabled && !UI<Button>("OpenOfficialButton").IsEnabled && !UI<Button>("ChooseDirectoryButton").IsEnabled, "detected-client state permits focusing the game while preventing a duplicate launch");
            Set("currentClient", new ClientInfo { Id = int.MaxValue, StartTicks = 1 }); Call("PollStatus");
            Check(UI<TextBlock>("LaunchLabel").Text == "启动游戏" && UI<Button>("OpenOfficialButton").IsEnabled && UI<Button>("ChooseDirectoryButton").IsEnabled, "client exit after immediate restoration returns the launcher to ready");
            Call("ShowStatus", "game-updating", "游戏文件需要更新。", true);
            Check(!UI<Button>("OpenOfficialButton").IsEnabled && !UI<CheckBox>("CompatibilityToggle").IsEnabled && UI<Button>("CancelButton").Visibility == Visibility.Visible && UI<TextBlock>("FooterText").Visibility == Visibility.Collapsed, "official update wait prevents duplicate launches and exposes cancellation");
            Call("ShowStatus", "game-update-unconfirmed", "游戏检查未完成：请先在官方启动器保存窗口和分辨率设置。", true);
            Check(UI<TextBlock>("StatusTitle").Text == "启动检查未完成" && UI<TextBlock>("StatusMessage").Text.Contains("分辨率") && !UI<Button>("LaunchButton").IsEnabled && UI<Button>("CancelButton").Visibility == Visibility.Visible, "unknown launch checks show their reason without claiming an incomplete update");
            var prompt = (Window)typeof(LauncherWindow).GetMethod("CreateGameUpdateDialog", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, new object[] { "游戏文件需要更新：ROWID.SPF" });
            var promptBody = (FrameworkElement)prompt.Content;
            promptBody.Measure(new Size(460, 291)); promptBody.Arrange(new Rect(0, 0, 460, 291)); promptBody.UpdateLayout();
            var complete = (Button)prompt.FindName("GameUpdateCompleteButton"); var cancelUpdate = (Button)prompt.FindName("GameUpdateCancelButton");
            foreach (var button in new[] { complete, cancelUpdate }) {
                Point position = button.TranslatePoint(new Point(0, 0), promptBody);
                if (button.ActualWidth <= 0 || button.ActualHeight <= 0 || position.Y + button.ActualHeight > 291 || position.X + button.ActualWidth > 460) throw new Exception("Update dialog button outside viewport");
            }
            Check((string)complete.Content == "我已更新完成" && (string)cancelUpdate.Content == "取消本次启动", "update prompt has visible confirmation and cancel buttons");
            var drawing = new DrawingVisual(); using (var paint = drawing.RenderOpen()) { paint.DrawRectangle(prompt.Background, null, new Rect(0, 0, 460, 291)); paint.DrawRectangle(new VisualBrush(promptBody), null, new Rect(0, 0, 460, 291)); }
            var bitmap = new RenderTargetBitmap(460, 291, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(Path.Combine(root, "game-update-dialog.png"))) encoder.Save(output);
            prompt.Content = null;
            Call("ShowStatus", "ready", "", false);
            Call("ShowPane", "RegionPane");
            Render(Report());
            Check(UI<Button>("RegionRestoreButton").IsEnabled && UI<Button>("RegionApplyButton").IsEnabled, "idle verified settings allow both recovery and selection");
            foreach (string name in new[] { "RegionCurrentText", "RegionRestoreButton", "RegionCombo", "RegionApplyButton", "WindowsRegionButton", "RegionLogsButton" })
            {
                var element = UI<FrameworkElement>(name); Point position = element.TranslatePoint(new Point(0, 0), surface);
                if (element.ActualHeight <= 0 || position.Y < 0 || position.Y + element.ActualHeight > 690 || position.X + element.ActualWidth > 1080) throw new Exception("Main control outside viewport: " + name);
            }
            Check(true, "all main region controls fit the collapsed management page");
            var report = Report(); report.Backup = null; Render(report);
            Check(!UI<Button>("RegionRestoreButton").IsEnabled && UI<Button>("RegionApplyButton").IsEnabled && UI<TextBlock>("RegionBackupText").Text.Contains("暂无"), "missing backup disables only backup restore");
            report = Report(); report.GameRunning = true; Render(report);
            Check(!UI<Button>("RegionApplyButton").IsEnabled && UI<Button>("RegionRestoreButton").IsEnabled && UI<TextBlock>("RegionAvailabilityText").Text.Contains("游戏"), "game presence blocks selection while preserving emergency restore");
            report = Report(); report.WriteBusy = true; Render(report);
            Check(!UI<Button>("RegionApplyButton").IsEnabled && !UI<Button>("RegionRestoreButton").IsEnabled, "external operation locks both write actions");
            report = Report(); report.ReadChanged = true; Render(report);
            Check(!UI<Button>("RegionApplyButton").IsEnabled && !UI<Button>("RegionRestoreButton").IsEnabled, "changing detection result requires a fresh read");
            report = Report(); report.Configured.ACP = "65001"; Render(report);
            Check(!UI<Button>("RegionApplyButton").IsEnabled && UI<TextBlock>("RegionAvailabilityText").Text.Contains("UTF-8"), "UTF-8 mode is explained and cannot be overwritten through manual choice");
            report = Report(); report.Configured = RegionCatalog.Preset("zh-TW"); report.Backup = null;
            report.Probe = new ProcessLocaleProbe { ACP = 950, OEMCP = 950, LCID = 0x804 }; Render(report);
            Check(UI<TextBlock>("RegionStateText").Text.Contains("读数不同") && UI<TextBlock>("RegionDetailsText").Text.Contains("重启"), "mixed post-switch readouts are shown as incomplete activation");
            report = Report(); report.Pending = new[] { new JournalReference { Id = "test" } }; Render(report);
            Check(UI<TextBlock>("RegionStateText").Text.Contains("恢复待处理"), "pending history cannot appear as a healthy completed state");
            report = Report(); Set("refreshingRegion", true); Render(report);
            Check(!UI<Button>("RegionApplyButton").IsEnabled && !UI<Button>("RegionRestoreButton").IsEnabled, "in-flight detection prevents repeated write clicks");
            File.WriteAllLines(Path.Combine(root, "results.txt"), results);
            window.Close(); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
}
