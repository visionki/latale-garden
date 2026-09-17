using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LaTaleGarden;

public static class UpdateUiTests
{
    static LauncherWindow window; static UserControl surface;
    static List<string> lines = new List<string>();
    static void Set(string name, object value) { typeof(LauncherWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value); }
    static void Call(string name, params object[] args) { typeof(LauncherWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args); }
    static T UI<T>(string name) where T : class { return surface.FindName(name) as T; }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); lines.Add("PASS " + name); Console.WriteLine(lines[lines.Count - 1]); }
    static void Layout() { surface.Measure(new Size(1080, 690)); surface.Arrange(new Rect(0, 0, 1080, 690)); surface.UpdateLayout(); }
    [STAThread] public static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        try
        {
            new Application(); window = new LauncherWindow(Path.Combine(args[0], "unused-preview.png"));
            surface = (UserControl)typeof(LauncherWindow).GetField("surface", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            var prefs = new UpdatePreferences(); Set("updatePreferences", prefs);
            Call("ShowPane", "UpdatesPane"); Call("RenderUpdate"); Layout();
            Check(UI<Button>("DownloadUpdateButton").Visibility == Visibility.Collapsed && UI<Button>("CheckUpdateButton").IsEnabled, "no-update state offers checking without a download button");
            Set("availableUpdate", new UpdateRelease { Version = "1.2.0", Notes = "第一项改进\n第二项改进" }); Call("RenderUpdate"); Layout();
            Check(UI<Button>("UpdatesButton").Content.ToString().Contains("新版本") && UI<Button>("DownloadUpdateButton").Visibility == Visibility.Visible, "new version appears in the home badge and update page");
            foreach (var name in new[] { "CheckUpdateButton", "DownloadUpdateButton", "IgnoreUpdateButton", "LaterUpdateButton" })
            {
                var element = UI<FrameworkElement>(name); var point = element.TranslatePoint(new Point(), surface);
                Check(element.ActualHeight > 0 && point.Y >= 0 && point.Y + element.ActualHeight <= 690, name + " fits inside the normal update viewport");
            }
            prefs.IgnoredVersion = "1.2.0"; Call("RenderUpdate");
            Check(!UI<Button>("UpdatesButton").Content.ToString().Contains("新版本") && UI<Button>("DownloadUpdateButton").Visibility == Visibility.Visible, "ignored version stops the badge but remains available for manual download");
            Set("downloadingUpdate", true); Call("RenderUpdate");
            Check(!UI<Button>("CheckUpdateButton").IsEnabled && !UI<Button>("DownloadUpdateButton").IsEnabled && UI<Button>("CancelUpdateButton").Visibility == Visibility.Visible, "download prevents duplicate actions and provides cancellation");
            Set("downloadingUpdate", false); Set("packageReady", true); Call("RenderUpdate");
            Check(UI<Button>("DownloadUpdateButton").Content.ToString().Contains("重启"), "verified package offers restart and install");
            Set("announcementFeed", new AnnouncementFeed { Schema = 1, Items = new[] { new Announcement { Id = "test", Title = "测试动态", Text = "固定格式的文字", StartsUtc = "2020-01-01T00:00:00Z", ExpiresUtc = "2099-01-01T00:00:00Z" } } });
            Call("RenderAnnouncements");
            Check(UI<StackPanel>("AnnouncementContent").Visibility == Visibility.Visible && UI<Button>("AnnouncementLinkButton").Visibility == Visibility.Collapsed, "announcement with no link shows only text and dismissal");
            prefs.ShowAnnouncements = false; Call("RenderAnnouncements");
            Check(UI<StackPanel>("AnnouncementContent").Visibility == Visibility.Collapsed, "disabled project announcements are hidden");
            Call("ShowPane", "SettingsPane"); Layout();
            var save = UI<Button>("SaveSettingsButton"); var position = save.TranslatePoint(new Point(), surface);
            Check(save.ActualHeight > 0 && position.Y + save.ActualHeight < 690, "settings save remains visible without scrolling");
            File.WriteAllLines(Path.Combine(args[0], "results.txt"), lines); window.Close(); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(args[0], "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
}
