using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PerfTuner.App.Controls;
using PerfTuner.Platform.Windows;

namespace PerfTuner.App.Setup;

/// <summary>
/// <c>PerfTuner.exe --setup [--silent]</c> (started by PerfTuner-Setup.exe from its verified unpack folder) and
/// <c>PerfTuner.exe --uninstall [--silent]</c> (started by PerfTuner-Uninstall.exe). Exit codes: 0 done,
/// 1223 cancelled, 2 failed, 3 PerfTuner still running.
/// </summary>
public static class SetupFlow
{
    public const int Done = 0;
    public const int Failed = 2;
    public const int StillRunning = 3;
    public const int Cancelled = 1223;

    public static int? TryRun(string[] args)
    {
        var silent = Array.IndexOf(args, "--silent") >= 0;
        if (Array.IndexOf(args, "--setup") >= 0) { return silent ? SilentInstall() : new SetupWindow().Run(); }
        if (Array.IndexOf(args, "--uninstall") >= 0) { return Uninstall(silent); }
        return null;
    }

    private static int SilentInstall()
    {
        var previous = ProductSetup.Installed();
        try
        {
            ProductSetup.Install(AppContext.BaseDirectory, previous?.Options ?? new SetupOptions(true, false, ServiceControl.Query().State != ServiceRunState.NotInstalled), _ => { });
            return Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HelperLog.Write("setup failed: " + ex.Message);
            return Failed;
        }
    }

    private static int Uninstall(bool silent)
    {
        if (!silent && ThemedDialog.Show(
                "This removes PerfTuner from this PC:\n\n• undoes PerfTuner's changes to Windows settings (power plan, visual effects, startup apps, security fixes, page file…)\n• removes the always-on engine service, the shortcuts and the start-at-sign-in task\n• deletes the program files\n\nFiles you moved or deleted with PerfTuner stay where they are. Your PerfTuner preferences in your user folder are kept.",
                "Uninstall PerfTuner", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return Cancelled;
        }
        while (ProductSetup.RunningCopies() is { Count: > 0 } running)
        {
            if (silent || ThemedDialog.Show("PerfTuner is still running. Close it (right-click its icon in the notification area › Exit), then choose OK.\n\n" + string.Join("\n", running),
                    "Close PerfTuner first", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return silent ? StillRunning : Cancelled;
            }
        }
        try
        {
            foreach (var line in ProductSetup.RemoveIntegration()) { HelperLog.Write("uninstall: " + line); }
            return Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            HelperLog.Write("uninstall failed: " + ex.Message);
            if (!silent) { ThemedDialog.Show("Uninstall couldn't finish: " + ex.Message, "Uninstall PerfTuner", MessageBoxButton.OK, MessageBoxImage.Error); }
            return Failed;
        }
    }

    /// <summary>The Setup wizard: choices → progress → done. Themed like the app.</summary>
    private sealed class SetupWindow
    {
        private readonly Window _window;
        private readonly ContentControl _page = new();
        private int _exitCode = Cancelled;
        private bool _busy;

        public SetupWindow()
        {
            _window = new Window
            {
                Title = "PerfTuner Setup",
                Width = 600,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            _window.Closing += (_, e) => e.Cancel = _busy;

            var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon256.png")) };
            var title = Text($"PerfTuner {ProductSetup.Version} Setup", 20, FontWeights.SemiBold);
            var sub = Text(Brand.Slogan, 13, FontWeights.Normal, "SubtleBrush");
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
            head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, sub } });

            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(28, 24, 28, 22), Margin = new Thickness(16),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 }, Child = new StackPanel { Children = { head, _page } } };
            card.SetResourceReference(Border.BackgroundProperty, "PlateBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { _window.DragMove(); } };
            _window.Content = card;
        }

        public int Run()
        {
            ShowChoices();
            _window.ShowDialog();
            return _exitCode;
        }

        private void ShowChoices()
        {
            var previous = ProductSetup.Installed();
            var options = previous?.Options ?? new SetupOptions(true, false, ServiceControl.Query().State != ServiceRunState.NotInstalled);
            var intro = previous is null
                ? $"Installs PerfTuner for everyone on this PC, in {ProductLayout.InstallRoot}. A Start-menu shortcut and an entry in Settings › Apps › Installed apps (with Uninstall) are always added."
                : $"Upgrades PerfTuner {previous.Version} to {ProductSetup.Version} in place. Your settings, history and undo records are kept.";
            var desktop = Check("Create a desktop shortcut", options.DesktopShortcut);
            var signIn = Check("Start PerfTuner in the notification area when I sign in (administrator accounts; starts with administrator rights without asking)", options.StartAtSignIn);
            var service = Check("Install the always-on engine (a Windows service, so tuning keeps working with the window closed)", options.InstallService);
            var note = Text("PerfTuner needs administrator rights and asks Windows for them each time it starts. It changes nothing on your PC until you ask it to, and every change can be undone. Open source, MIT license.", 12, FontWeights.Normal, "SubtleBrush");
            note.Margin = new Thickness(0, 14, 0, 0);

            var install = Button(previous is null ? "Install" : "Upgrade", primary: true);
            var cancel = Button("Cancel", primary: false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => { _exitCode = Cancelled; _window.Close(); };
            install.Click += (_, _) => _ = InstallAsync(new SetupOptions(desktop.IsChecked == true, signIn.IsChecked == true, service.IsChecked == true));

            _page.Content = new StackPanel
            {
                Children = { Body(intro), Spacer(14), desktop, signIn, service, note, Buttons(install, cancel) },
            };
        }

        private async Task InstallAsync(SetupOptions options)
        {
            var status = Body("Preparing…");
            var bar = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 16, 0, 8) };
            _page.Content = new StackPanel { Children = { Text("Installing…", 15, FontWeights.SemiBold), Spacer(8), status, bar } };
            _busy = true;
            string? error = null;
            try
            {
                var progress = new Progress<string>(s => status.Text = s);
                await Task.Run(() => ProductSetup.Install(AppContext.BaseDirectory, options, ((IProgress<string>)progress).Report)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                error = ex.Message;
                HelperLog.Write("setup failed: " + ex.Message);
            }
            _busy = false;
            if (error is null) { ShowDone(); } else { ShowFailed(error); }
        }

        private void ShowDone()
        {
            _exitCode = Done;
            var launch = Check("Start PerfTuner now", true);
            var finish = Button("Finish", primary: true);
            finish.Click += (_, _) =>
            {
                if (launch.IsChecked == true)
                {
                    try
                    {
                        using var _ = Process.Start(new ProcessStartInfo(Path.Combine(ProductLayout.InstallRoot, ProductLayout.AppExeName)) { UseShellExecute = false, WorkingDirectory = ProductLayout.InstallRoot });
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        HelperLog.Write("launch after setup failed: " + ex.Message);
                    }
                }
                _window.Close();
            };
            _page.Content = new StackPanel
            {
                Children =
                {
                    Text("✔ PerfTuner is installed", 15, FontWeights.SemiBold, "GoodBrush"), Spacer(8),
                    Body("Find it in the Start menu. To remove it, use Settings › Apps › Installed apps › PerfTuner › Uninstall: that also undoes PerfTuner's changes to Windows settings."),
                    Spacer(12), launch, Buttons(finish),
                },
            };
        }

        private void ShowFailed(string error)
        {
            _exitCode = Failed;
            var close = Button("Close", primary: true);
            close.Click += (_, _) => _window.Close();
            var retry = Button("Back", primary: false);
            retry.Click += (_, _) => ShowChoices();
            _page.Content = new StackPanel
            {
                Children = { Text("✖ Setup couldn't finish", 15, FontWeights.SemiBold, "BadBrush"), Spacer(8), Body(error), Buttons(close, retry) },
            };
        }

        private static TextBlock Text(string text, double size, FontWeight weight, string brush = "TextBrush")
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private static TextBlock Body(string text)
        {
            var t = Text(text, 13.5, FontWeights.Normal);
            t.LineHeight = 20;
            return t;
        }

        private static FrameworkElement Spacer(double h) => new Border { Height = h };

        private static CheckBox Check(string label, bool value)
        {
            var c = new CheckBox { IsChecked = value, Margin = new Thickness(0, 4, 0, 4), Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } };
            ((TextBlock)c.Content).SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return c;
        }

        private static Button Button(string label, bool primary)
        {
            var b = new Button { Content = label, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
            b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
            return b;
        }

        private static StackPanel Buttons(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            foreach (var b in buttons) { row.Children.Add(b); }
            return row;
        }
    }
}
