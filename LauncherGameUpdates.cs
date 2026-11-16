using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LaTaleGarden
{
    public partial class LauncherWindow
    {
        private Window gameUpdateDialog;
        private bool closingGameDialog, gameUpdateDecisionShown, gameUpdateSubmitted, gameUpdateDismissed;
        private string gameUpdateDialogStage = "";

        private Window CreateGameUpdateDialog(string message)
        {
            var dialog = new Window {
                Title = "更新游戏 · LaTale Garden", Width = 460, Height = 330, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Icon = Icon,
                Background = new SolidColorBrush(Color.FromRgb(255, 248, 237)), FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            NameScope.SetNameScope(dialog, new NameScope());
            if (IsVisible) dialog.Owner = this;
            var body = new Grid { Margin = new Thickness(24) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new TextBlock { Text = "先完成官方更新", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(82, 64, 50)) };
            body.Children.Add(title);
            var instructions = new TextBlock {
                Text = "请等待官方启动器更新完成，暂时不要点击“开始游戏”。若勾选了“自动执行”，请先取消勾选。\n\n完成后点击下方按钮，Garden 会复查文件，关闭官方启动器，再启动游戏。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 13, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(122, 103, 82))
            };
            Grid.SetRow(instructions, 1); body.Children.Add(instructions);
            var status = new TextBlock { Name = "GameUpdatePromptStatus", Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 11, MaxHeight = 43,
                VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(155, 111, 65)), ToolTip = message };
            Grid.SetRow(status, 2); body.Children.Add(status); dialog.RegisterName(status.Name, status);
            var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var complete = new Button { Name = "GameUpdateCompleteButton", Content = "我已更新完成", Style = (Style)surface.Resources["Coral"], Padding = new Thickness(13, 10, 13, 10), FontSize = 14, Margin = new Thickness(0, 0, 10, 0) };
            complete.Click += (s, e) => { if (SendGameUpdateAction("complete")) { gameUpdateSubmitted = true; complete.IsEnabled = false; status.Text = "正在重新检测，请稍候…"; } };
            buttons.Children.Add(complete); dialog.RegisterName(complete.Name, complete);
            var cancel = new Button { Name = "GameUpdateCancelButton", Content = "取消本次启动", Style = (Style)surface.Resources["Outline"], Padding = new Thickness(10, 8, 10, 8), FontSize = 11 };
            cancel.Click += (s, e) => { CancelLaunch(); gameUpdateDismissed = true; CloseGameUpdateDialog(); };
            Grid.SetColumn(cancel, 1); buttons.Children.Add(cancel); dialog.RegisterName(cancel.Name, cancel);
            Grid.SetRow(buttons, 3); body.Children.Add(buttons); dialog.Content = body;
            dialog.Closing += (s, e) => { if (!closingGameDialog) { gameUpdateDismissed = true; CancelLaunch(); } };
            return dialog;
        }
        private bool SendGameUpdateAction(string action)
        {
            if (active == null || !busy) return false;
            try
            {
                JsonFile.Write(active.FilePath("game-update-command.json"), new GameUpdateCommand { Id = Guid.NewGuid().ToString("N"), Action = action });
                return true;
            }
            catch (Exception ex) { MessageBox.Show(gameUpdateDialog ?? this, "操作未能提交：" + ex.Message, "更新游戏", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        }
        private void CloseGameUpdateDialog()
        {
            if (gameUpdateDialog == null) return;
            closingGameDialog = true;
            try { gameUpdateDialog.Close(); gameUpdateDialog = null; }
            finally { closingGameDialog = false; }
        }
        private void RenderGameUpdatePrompt(string next, string message)
        {
            bool needsDecision = next == "game-update-incomplete" || next == "game-update-unconfirmed";
            bool updateStage = next == "game-updating" || needsDecision || next == "game-verifying" || next == "game-updater-closing" || next == "game-updater-restarting";
            if (!updateStage)
            {
                CloseGameUpdateDialog(); gameUpdateDialogStage = ""; gameUpdateDecisionShown = gameUpdateSubmitted = gameUpdateDismissed = false; return;
            }
            if (previewPath != null || active == null || gameUpdateDismissed) return;
            if (gameUpdateDialog == null) { gameUpdateDialog = CreateGameUpdateDialog(message); gameUpdateDialog.Show(); }
            if (next != gameUpdateDialogStage)
            {
                if (next == "game-updating") gameUpdateSubmitted = false;
                if (!needsDecision) gameUpdateDecisionShown = false;
                gameUpdateDialogStage = next;
            }
            var status = (TextBlock)gameUpdateDialog.FindName("GameUpdatePromptStatus"); status.Text = message; status.ToolTip = message;
            ((Button)gameUpdateDialog.FindName("GameUpdateCompleteButton")).IsEnabled = next == "game-updating" && !gameUpdateSubmitted;
            if (needsDecision && !gameUpdateDecisionShown)
            {
                gameUpdateDecisionShown = true;
                Dispatcher.BeginInvoke(new Action(() => {
                    if (gameUpdateDialog == null || (stage != "game-update-incomplete" && stage != "game-update-unconfirmed")) return;
                    var choice = MessageBox.Show(gameUpdateDialog, message + "\n\n是否重启官方启动器？\n选择“是”会关闭本轮官方启动器后重新打开；选择“否”仅关闭本提醒。", stage == "game-update-incomplete" ? "更新尚未完成" : "启动检查未完成", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if ((stage == "game-update-incomplete" || stage == "game-update-unconfirmed") && busy) SendGameUpdateAction(choice == MessageBoxResult.Yes ? "restart" : "keep");
                }), DispatcherPriority.Normal);
            }
        }
    }
}
