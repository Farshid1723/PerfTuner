using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PerfTuner.App.ViewModels;

namespace PerfTuner.App.Controls;

/// <summary>
/// Small round "?" next to a section title. Click: a themed hint pops up with a short explanation
/// and a "More help" link that opens the Help page at the matching topic (<see cref="HelpTopics"/>).
/// </summary>
public sealed class HelpButton : Button
{
    public static readonly DependencyProperty TopicProperty =
        DependencyProperty.Register(nameof(Topic), typeof(string), typeof(HelpButton), new PropertyMetadata(null));

    public string? Topic
    {
        get => (string?)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    private Popup? _popup;

    public HelpButton()
    {
        SetResourceReference(StyleProperty, "HelpButtonStyle");
        Content = "?";
        Focusable = true;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties_SetName();
    }

    private void AutomationProperties_SetName() =>
        System.Windows.Automation.AutomationProperties.SetName(this, "Help");

    protected override void OnClick()
    {
        base.OnClick();
        if (_popup is { IsOpen: true }) { _popup.IsOpen = false; return; }
        var topic = HelpTopics.Find(Topic);
        _popup = BuildPopup(topic);
        _popup.IsOpen = true;
    }

    private Popup BuildPopup(HelpTopic topic)
    {
        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -12,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = true,
        };

        var title = new TextBlock { Text = topic.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var hint = new TextBlock { Text = topic.Hint, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");

        var more = new Button { Content = "More help  →", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        more.SetResourceReference(StyleProperty, "LinkButton");
        more.Click += (_, _) =>
        {
            popup.IsOpen = false;
            if (Window.GetWindow(this)?.DataContext is MainViewModel vm && vm.OpenHelpCommand.CanExecute(topic.Id))
            {
                vm.OpenHelpCommand.Execute(topic.Id);
            }
        };

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(hint);
        stack.Children.Add(more);

        var card = new Border
        {
            Width = 300,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(10), // room for the shadow
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.3, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "PlateBrush");
        card.SetResourceReference(Border.CornerRadiusProperty, "PopupRadius");
        card.SetResourceReference(Border.BorderBrushProperty, "PlateEdgeBrush");
        card.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        card.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; } };

        popup.Child = card;
        popup.Opened += (_, _) => more.Focus();
        return popup;
    }
}
