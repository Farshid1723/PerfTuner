using System.Windows;
using System.Windows.Interop;
using PerfTuner.App.ViewModels;

namespace PerfTuner.App;

/// <summary>
/// View: everything is data-bound (charts are theme-aware sparklines fed by the view model).
/// Closing is handled by <see cref="App"/> (hide to tray, or full exit).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Controls.WindowFrame.Follow(this);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SnapLayoutHook);
        StateChanged += (_, _) => OnStateChanged();
        WireSpaceViews();

        // "Show them" on a startup recommendation: sort the table by impact, highest first.
        viewModel.Startup.SortByImpactRequested += () =>
        {
            var column = StartupGrid.Columns.FirstOrDefault(c => c.SortMemberPath == "ImpactRank");
            if (column is null) { return; }
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(StartupGrid.ItemsSource);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("ImpactRank", System.ComponentModel.ListSortDirection.Descending));
            foreach (var c in StartupGrid.Columns) { c.SortDirection = null; }
            column.SortDirection = System.ComponentModel.ListSortDirection.Descending;
        };
    }

    // ------------------------------------------------------------------ Space explorer wiring

    private SpaceViewModel Space => ((MainViewModel)DataContext).Cockpit.Space;

    private void OnSpaceTreeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        Space.SetSelection(SpaceTree.SelectedItems.OfType<SpaceRow>().Select(r => r.Node).ToList());

    private void OnSpaceReportSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        Space.SetSelection(SpaceReport.SelectedItems.OfType<ReportRow>().Select(r => r.Node).OfType<PerfTuner.Core.Space.SpaceNode>().ToList());

    private void OnSpaceTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SpaceTree.SelectedItem is SpaceRow { Node.IsFolder: true } row) { Space.OpenCommand.Execute(row.Node); }
    }

    private void WireSpaceViews()
    {
        SpaceTreemap.NodeClicked += (n, m) => Space.Click(n, m);
        SpaceSunburst.NodeClicked += (n, m) => Space.Click(n, m);
        SpaceTreemap.NodeOpened += n => Space.OpenCommand.Execute(n);
        SpaceSunburst.NodeOpened += n => Space.OpenCommand.Execute(n);
    }

    // ------------------------------------------------------------------ custom title bar

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        // A maximized chrome-less window overhangs the screen by the resize border; pad it back in.
        FrameRoot.Margin = maximized ? new Thickness(7) : new Thickness(0);
        FrameRoot.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    /// <summary>
    /// Windows 11 snap layouts appear when the maximize button reports HTMAXBUTTON (documented WM_NCHITTEST result).
    /// Because the button then receives non-client messages, hover and click are handled here.
    /// </summary>
    private nint SnapLayoutHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (IsOverMaxButton(lParam))
                {
                    MaxButton.Tag = "hover";
                    handled = true;
                    return HtMaxButton;
                }
                MaxButton.Tag = null;
                break;
            case WmNcMouseMove:
                if ((int)wParam != HtMaxButton) { MaxButton.Tag = null; }
                break;
            case WmNcMouseLeave:
                MaxButton.Tag = null;
                break;
            case WmNcLButtonDown when (int)wParam == HtMaxButton:
            case WmNcLButtonDblClk when (int)wParam == HtMaxButton:
                handled = true;
                break;
            case WmNcLButtonUp when (int)wParam == HtMaxButton:
                handled = true;
                OnMaximize(MaxButton, new RoutedEventArgs());
                break;
        }
        return 0;
    }

    private bool IsOverMaxButton(nint lParam)
    {
        if (!MaxButton.IsVisible) { return false; }
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        try
        {
            var p = MaxButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
