namespace PerfTuner.Platform.Windows;

/// <summary>Fixed names and places of an installed or portable PerfTuner (v0.9+). Nothing here comes from input.</summary>
public static class ProductLayout
{
    public const string AppExeName = "PerfTuner.exe";
    public const string UninstallerName = "PerfTuner-Uninstall.exe";
    public const string InstallManifestName = "install.manifest";
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PerfTuner";
    public const string SignInTaskName = "PerfTuner (start at sign-in)";

    /// <summary>%ProgramFiles%\PerfTuner: the installed app (the engine service lives in its Service subfolder).</summary>
    public static string InstallRoot => ProductPaths.Production.InstallRoot;

    /// <summary>%ProgramFiles%\PerfTuner-Portable: where the portable exe unpacks (only administrators can create anything there).</summary>
    public static string PortableRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PerfTuner-Portable");

    /// <summary>%ProgramFiles%\PerfTuner-Setup: Setup's temporary unpack folder and the running uninstaller copy.</summary>
    public static string SetupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PerfTuner-Setup");

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "PerfTuner.lnk");
    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "PerfTuner.lnk");
}
