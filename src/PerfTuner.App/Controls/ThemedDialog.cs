using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PerfTuner.App.Controls;

/// <summary>
/// Drop-in replacement for <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult)"/>
/// that follows the active colour theme (the system message box is always light/grey).
/// Borderless, rounded, draggable; Enter = primary button, Esc = cancel.
/// </summary>
public static class ThemedDialog
{
    public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var result = buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
        var owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        var window = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            Topmost = owner is null,
        };

        var (glyph, brushKey) = image switch
        {
            MessageBoxImage.Warning => ("", "WarnBrush"),
            MessageBoxImage.Error => ("", "BadBrush"),
            MessageBoxImage.Question => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };

        var icon = new TextBlock { Text = glyph, FontSize = 22, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        void AddButton(string label, MessageBoxResult value, bool primary)
        {
            var b = new Button { Content = label, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = value == defaultResult || (defaultResult == MessageBoxResult.None && primary) };
            b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
            b.IsCancel = value is MessageBoxResult.No or MessageBoxResult.Cancel || (buttons == MessageBoxButton.OK);
            b.Click += (_, _) => { result = value; window.Close(); };
            buttonRow.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                AddButton("Yes", MessageBoxResult.Yes, true);
                AddButton("No", MessageBoxResult.No, false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Yes", MessageBoxResult.Yes, true);
                AddButton("No", MessageBoxResult.No, false);
                AddButton("Cancel", MessageBoxResult.Cancel, false);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("OK", MessageBoxResult.OK, true);
                AddButton("Cancel", MessageBoxResult.Cancel, false);
                break;
            default:
                AddButton("OK", MessageBoxResult.OK, true);
                break;
        }

        var content = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        var stack = new StackPanel { MaxWidth = 440 };
        stack.Children.Add(heading);
        stack.Children.Add(body);
        stack.Children.Add(buttonRow);
        content.Children.Add(stack);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 22, 24, 20),
            Margin = new Thickness(16), // room for the shadow
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "PlateBrush");
        card.SetResourceReference(Border.CornerRadiusProperty, "PopupRadius");
        card.SetResourceReference(Border.BorderBrushProperty, "PlateEdgeBrush");
        card.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { window.DragMove(); } };

        window.Content = card;
        window.ShowDialog();
        return result;
    }
}
