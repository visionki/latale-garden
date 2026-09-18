using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
