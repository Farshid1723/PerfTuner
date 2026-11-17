using PerfTuner.Core.Security;

namespace PerfTuner.Platform.Windows;

/// <summary>
/// Every file-system location the product uses, derived from fixed known folders.
/// Nothing here is influenced by IPC input; derived paths go through
/// <see cref="PathGuard"/> so a crafted environment cannot redirect them.
/// </summary>
public sealed class ProductPaths
{
    private ProductPaths(string dataRoot, string installRoot)
    {
        DataRoot = dataRoot;
        InstallRoot = installRoot;
        ConfigFile = Resolve(dataRoot, "config.json");
        LogDirectory = Resolve(dataRoot, "logs");
        StateDirectory = Resolve(dataRoot, "state");
        RunningMarker = Resolve(StateDirectory, "running.marker");
        ServiceInstallDirectory = Resolve(installRoot, "Service");
    }

    /// <summary>%ProgramData%\PerfTuner (prod) or %LOCALAPPDATA%\PerfTuner\Engine (standalone engine). Admin-only write in prod.</summary>
    public string DataRoot { get; }

    /// <summary>%ProgramFiles%\PerfTuner. Admin-only write (critical: the service binary lives here).</summary>
    public string InstallRoot { get; }

    public string ServiceInstallDirectory { get; }
    public string ConfigFile { get; }
    public string LogDirectory { get; }
    public string StateDirectory { get; }
    public string RunningMarker { get; }

    public static ProductPaths Production { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerfTuner"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PerfTuner"));

    public static ProductPaths Development { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "Engine"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTuner", "Engine", "install"));

    private static string Resolve(string root, string child)
    {
        if (!PathGuard.TryResolveUnderRoot(root, child, out var full))
        {
            throw new InvalidOperationException($"Refusing path '{child}' outside '{root}'.");
        }
        return full;
    }
}
