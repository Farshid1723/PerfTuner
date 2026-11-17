using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerfTuner.App.Controls;
using PerfTuner.Core.Security;
using PerfTuner.Platform.Windows.Security;

namespace PerfTuner.App.ViewModels;

/// <summary>One finding as shown on the Security page.</summary>
public sealed record FindingRow(SecurityFinding Finding)
{
    public string Title => Finding.Title;
    public string Found => Finding.Found;
    public string Why => Finding.Why;
    public string Status => Finding.Status.ToString();
    public string StatusText => Finding.Status switch
    {
        SecurityStatus.Good => "OK",
        SecurityStatus.Warn => "Improve",
        SecurityStatus.Bad => "Fix this",
        SecurityStatus.Info => "Info",
        _ => "Unknown",
    };
    public string Glyph => Finding.Status switch
    {
        SecurityStatus.Good => "",
        SecurityStatus.Warn => "",
        SecurityStatus.Bad => "",
        SecurityStatus.Info => "",
        _ => "",
    };
    public bool HasFix => Finding.Fix is not null && Finding.Status is SecurityStatus.Warn or SecurityStatus.Bad;
    public string FixText => Finding.Fix?.Title ?? "";
    public bool HasOpen => Finding.OpenUri is not null;
    public string OpenText => Finding.OpenLabel ?? "Open settings";
}

public sealed record FindingGroup(string Name, string Summary, IReadOnlyList<FindingRow> Rows);

/// <summary>
/// The Security page (v0.9): a plain-language check-up of Windows' protection, device security, network exposure
/// and the misconfigurations a penetration test looks for first. Fixes are shown in full and need approval; they are
/// recorded in the settings journal (History › Changes to Windows settings), so each can be undone.
/// </summary>
public sealed partial class SecurityViewModel(MainViewModel main) : ObservableObject
{
    private bool _scannedOnce;

    public ObservableCollection<FindingGroup> Groups { get; } = [];
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scoreText = "Not checked yet";
    [ObservableProperty] private double _scorePercent;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _message = "";
    private IReadOnlyList<SecurityFinding> _last = [];

    public void SetActive(bool active)
    {
        if (active && !_scannedOnce) { _ = ScanAsync(); }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) { return; }
        IsScanning = true;
        _scannedOnce = true;
        Message = "";
        try
        {
            _last = await Task.Run(SecurityChecks.RunAll).ConfigureAwait(true);
            Groups.Clear();
            foreach (var g in _last.GroupBy(f => f.Group))
            {
                var rows = g.OrderBy(f => f.Status switch { SecurityStatus.Bad => 0, SecurityStatus.Warn => 1, SecurityStatus.Unknown => 2, SecurityStatus.Info => 3, _ => 4 })
                    .Select(f => new FindingRow(f)).ToList();
                var bad = rows.Count(r => r.Finding.Status == SecurityStatus.Bad);
                var warn = rows.Count(r => r.Finding.Status == SecurityStatus.Warn);
                Groups.Add(new FindingGroup(g.Key, bad + warn == 0 ? "All good" : $"{bad} to fix · {warn} to improve", rows));
            }
            var (good, total) = SecurityRules.Score(_last.Select(f => f.Status));
            ScorePercent = total == 0 ? 0 : Math.Round(100.0 * good / total);
            ScoreText = $"{good} of {total} checks OK";
            var badCount = _last.Count(f => f.Status == SecurityStatus.Bad);
            Summary = badCount == 0
                ? "No serious gaps found. The items marked \"Improve\" make the PC harder to attack."
                : $"{badCount} gap(s) worth closing. Each one explains why it matters and what a fix would change.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task FixAsync(FindingRow? row)
    {
        if (row?.Finding.Fix is not { } fix) { return; }
        if (!main.IsElevatedProcess) { Message = "Fixes need full power (administrator)."; return; }
        var text = new StringBuilder();
        text.AppendLine(fix.WhatChanges).AppendLine();
        text.AppendLine(CultureInfo.CurrentCulture, $"Takes effect: {fix.TakesEffect}.");
        if (fix.Warning is { } w) { text.AppendLine().AppendLine("⚠ " + w); }
        text.AppendLine().Append("PerfTuner records the previous value, so you can undo this in History › Changes to Windows settings.");
        if (ThemedDialog.Show(text.ToString(), fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        try
        {
            SecurityChecks.Apply(fix, main.SystemSettings.Journal);
            Message = $"✔ {fix.Title}. Takes effect: {fix.TakesEffect}.";
            main.AddActivity("You", "Security: " + fix.Title);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException or InvalidOperationException)
        {
            Message = "✖ The change couldn't be made: " + ex.Message;
        }
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Open(FindingRow? row)
    {
        // The address comes from the fixed check list (windowsdefender: / ms-settings: pages), never from input.
        if (row?.Finding.OpenUri is not { } uri || !(uri.StartsWith("windowsdefender://", StringComparison.Ordinal) || uri.StartsWith("ms-settings:", StringComparison.Ordinal))) { return; }
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Message = "✖ Windows couldn't open that page: " + ex.Message;
        }
    }

    /// <summary>Report in Markdown on the clipboard (stays on this PC unless you paste it somewhere).</summary>
    [RelayCommand]
    private void CopyReport()
    {
        if (_last.Count == 0) { return; }
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# PerfTuner security check-up · {DateTime.Now:g}");
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**{ScoreText}**");
        foreach (var g in _last.GroupBy(f => f.Group))
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {g.Key}").AppendLine();
            sb.AppendLine("| Check | Status | Found | Why it matters |").AppendLine("|---|---|---|---|");
            foreach (var f in g)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Esc(f.Title)} | {new FindingRow(f).StatusText} | {Esc(f.Found)} | {Esc(f.Why)} |");
            }
        }
        Clipboard.SetText(sb.ToString());
        Message = "✔ Report copied to the clipboard (Markdown).";
    }

    private static string Esc(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
