using System.Runtime.InteropServices;
using Microsoft.Win32;
using PerfTuner.Platform.Windows.Undocumented;

namespace PerfTuner.Platform.Windows.Apps;

/// <summary>A built-in Windows app PerfTuner offers to remove.</summary>
public sealed record BuiltInApp(string Name, string Title, string Note = "");

/// <summary>One installed package of the current user.</summary>
public sealed record InstalledPackage(string FullName, string Name, string Root);

/// <summary>
/// Built-in apps and Microsoft Store repair through Windows' package manager
/// (Windows.Management.Deployment.PackageManager, called through its documented ABI so no
/// projection library has to ship). Removal is for the current user only and can't be undone
/// here; the apps can be reinstalled from Microsoft Store.
/// </summary>
public static partial class AppPackages
{
    private const string Repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    public const string StoreName = "Microsoft.WindowsStore";

    /// <summary>The only packages the "built-in apps" list can remove.</summary>
    public static IReadOnlyList<BuiltInApp> Removable { get; } =
    [
        new("Microsoft.BingNews", "Microsoft News"),
        new("Microsoft.BingWeather", "Weather"),
        new("Microsoft.BingSearch", "Bing Search"),
        new("Microsoft.GetHelp", "Get Help"),
        new("Microsoft.Getstarted", "Tips"),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection"),
        new("Microsoft.People", "People"),
        new("Microsoft.SkypeApp", "Skype"),
        new("Microsoft.ZuneMusic", "Media Player", "Windows' music and video player."),
        new("Microsoft.ZuneVideo", "Films & TV"),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal"),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub"),
        new("Microsoft.Office.OneNote", "OneNote for Windows 10"),
        new("Microsoft.OneConnect", "Mobile Plans"),
        new("Microsoft.Wallet", "Wallet"),
        new("Microsoft.YourPhone", "Phone Link"),
        new("Microsoft.3DBuilder", "3D Builder"),
        new("Microsoft.Microsoft3DViewer", "3D Viewer"),
        new("Microsoft.Office.Sway", "Sway"),
        new("Microsoft.Messaging", "Messaging"),
        new("Microsoft.XboxApp", "Xbox Console Companion"),
        new("Microsoft.GamingApp", "Xbox", "Also used to install PC Game Pass games."),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Win+G overlay, screen recording in games."),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text overlay"),
        new("Microsoft.Xbox.TCUI", "Xbox TCUI", "Some games' Xbox sign-in uses it."),
        new("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", "Xbox sign-in in games needs it."),
        new("Clipchamp.Clipchamp", "Clipchamp"),
        new("Microsoft.Todos", "Microsoft To Do"),
        new("Microsoft.PowerAutomateDesktop", "Power Automate"),
        new("MicrosoftTeams", "Microsoft Teams (personal)"),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub"),
        new("Microsoft.WindowsMaps", "Maps"),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder"),
        new("Microsoft.WindowsAlarms", "Clock"),
        new("Microsoft.MicrosoftStickyNotes", "Sticky Notes"),
        new("Microsoft.549981C3F5F10", "Cortana"),
        new("Microsoft.OutlookForWindows", "Outlook (new)"),
        new("Microsoft.Windows.DevHome", "Dev Home"),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist"),
        new("Microsoft.Copilot", "Copilot"),
    ];

    /// <summary>This user's installed main packages (no resource packs), from the package repository.</summary>
    public static IReadOnlyList<InstalledPackage> Installed()
    {
        var list = new List<InstalledPackage>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Repository);
            if (root is null) { return list; }
            foreach (var full in root.GetSubKeyNames())
            {
                var parts = full.Split('_');
                if (parts.Length != 5 || parts[3].Length != 0) { continue; } // Name_Version_Arch_ResourceId_PublisherId; main packages have no resource id
                using var key = root.OpenSubKey(full);
                if (key?.GetValue("PackageRootFolder") is not string folder || folder.Length == 0) { continue; }
                list.Add(new InstalledPackage(full, parts[0], folder));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // not readable: nothing listed
        }
        return list;
    }

    /// <summary>Installed apps from <see cref="Removable"/>.</summary>
    public static IReadOnlyList<(BuiltInApp App, InstalledPackage Package)> InstalledBuiltIns()
    {
        var installed = Installed();
        return [.. Removable.SelectMany(a => installed.Where(p => p.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).Select(p => (a, p)))];
    }

    /// <summary>Removes one built-in app for the current user (only names on the list).</summary>
    public static void Remove(InstalledPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!Removable.Any(a => a.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase))
            || !Installed().Any(p => p.FullName == package.FullName))
        {
            throw new InvalidOperationException("This app isn't on PerfTuner's list of removable built-in apps.");
        }
        WithPackageManager(pm =>
        {
            var name = HString.Create(package.FullName);
            try { return Call(pm, 8, name); }
            finally { HString.Delete(name); }
        });
    }

    /// <summary>Clears Microsoft Store's cache with Windows' own WSReset (opens the Store when done).</summary>
    public static void ResetStoreCache() => WindowsTools.Run(WindowsTools.StoreReset, [], TimeSpan.Zero, wait: false);

    /// <summary>Re-registers Microsoft Store from its installed files (fixes a Store that won't open).</summary>
    public static void ReregisterStore()
    {
        var store = Installed().FirstOrDefault(p => p.Name.Equals(StoreName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Microsoft Store isn't installed for this account.");
        Register(store);
    }

    /// <summary>Re-registers every installed app of this user from its own files. Returns (ok, failed).</summary>
    public static (int Ok, int Failed) ReregisterAll(Action<int, int>? progress = null)
    {
        var packages = Installed().Where(p => IsWindowsAppsFolder(p.Root)).ToList();
        int ok = 0, failed = 0;
        for (var i = 0; i < packages.Count; i++)
        {
            progress?.Invoke(i + 1, packages.Count);
            try { Register(packages[i]); ok++; }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException) { failed++; }
        }
        return (ok, failed);
    }

    private static bool IsWindowsAppsFolder(string root)
    {
        var full = Path.GetFullPath(root);
        string[] allowed =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps") + "\\",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel"),
        ];
        return allowed.Any(a => full.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    private static void Register(InstalledPackage package)
    {
        if (!IsWindowsAppsFolder(package.Root)) { throw new InvalidOperationException("Not a Windows app folder."); }
        var manifest = Path.Combine(package.Root, "AppxManifest.xml");
        if (!File.Exists(manifest)) { throw new InvalidOperationException("The app's manifest is missing."); }
        WithPackageManager(pm =>
        {
            var uri = CreateUri(new Uri(manifest).AbsoluteUri);
            try { return Call(pm, 10, uri, 0, 1 /* ForceApplicationShutdown */); }
            finally { Marshal.Release(uri); }
        });
    }

    // ------------------------------------------------------------------ WinRT ABI (ids and slots from Windows.Management.winmd)

    private static readonly Guid IidPackageManager = new("9A7D4B65-5E8F-4FC7-A2E5-7F6925CB8B53");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid IidUriFactory = new("44A9796F-723E-4FDF-A218-033E75B0C084");
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs one PackageManager call on an initialized apartment and waits for its async operation.</summary>
    private static void WithPackageManager(Func<nint, nint> start)
    {
        var init = RoInitialize(1 /* MTA */);
        try
        {
            var cls = HString.Create("Windows.Management.Deployment.PackageManager");
            nint inspectable;
            try { Check(RoActivateInstance(cls, out inspectable)); }
            finally { HString.Delete(cls); }
            try
            {
                var iid = IidPackageManager;
                Check(Marshal.QueryInterface(inspectable, in iid, out var pm));
                try
                {
                    var op = start(pm);
                    try { Wait(op); }
                    finally { Marshal.Release(op); }
                }
                finally { Marshal.Release(pm); }
            }
            finally { Marshal.Release(inspectable); }
        }
        finally
        {
            if (init is 0 or 1) { RoUninitialize(); }
        }
    }

    private static unsafe nint Call(nint pm, int slot, nint arg)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)pm)[slot];
        nint op;
        Check(fn(pm, arg, &op));
        return op;
    }

    private static unsafe nint Call(nint pm, int slot, nint uri, nint deps, uint options)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, nint*, int>)(*(nint**)pm)[slot];
        nint op;
        Check(fn(pm, uri, deps, options, &op));
        return op;
    }

    private static unsafe void Wait(nint operation)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(operation, in iid, out var info));
        try
        {
            var vtbl = *(nint**)info;
            var getStatus = (delegate* unmanaged[Stdcall]<nint, int*, int>)vtbl[7];
            var getError = (delegate* unmanaged[Stdcall]<nint, int*, int>)vtbl[8];
            var close = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[10];
            var deadline = DateTime.UtcNow + OperationTimeout;
            int status;
            while (true)
            {
                Check(getStatus(info, &status));
                if (status != 0) { break; } // 0 = started
                if (DateTime.UtcNow > deadline) { throw new TimeoutException("Windows didn't finish in time."); }
                Thread.Sleep(150);
            }
            if (status != 1)
            {
                int error;
                getError(info, &error);
                close(info);
                throw new InvalidOperationException(status == 2 ? "Cancelled." : Describe(error));
            }
            close(info);
        }
        finally { Marshal.Release(info); }
    }

    private static unsafe nint CreateUri(string text)
    {
        var cls = HString.Create("Windows.Foundation.Uri");
        nint factory;
        try
        {
            var iid = IidUriFactory;
            Check(RoGetActivationFactory(cls, in iid, out factory));
        }
        finally { HString.Delete(cls); }
        try
        {
            var value = HString.Create(text);
            try
            {
                var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)factory)[6];
                nint uri;
                Check(create(factory, value, &uri));
                return uri;
            }
            finally { HString.Delete(value); }
        }
        finally { Marshal.Release(factory); }
    }

    private static string Describe(int hr) => hr switch
    {
        unchecked((int)0x80073CFA) => "Windows doesn't allow removing this app (it's part of Windows).",
        unchecked((int)0x80073D19) => "An app or another user still uses it.",
        unchecked((int)0x80073CF6) => "Windows couldn't register the app.",
        _ => $"Windows reported 0x{hr:X8}: {Marshal.GetExceptionForHR(hr)?.Message}",
    };

    private static void Check(int hr)
    {
        if (hr < 0) { throw new InvalidOperationException(Describe(hr)); }
    }

    private static partial class HString
    {
        public static nint Create(string s)
        {
            Check(WindowsCreateString(s, (uint)s.Length, out var h));
            return h;
        }

        public static void Delete(nint h) => WindowsDeleteString(h);

        [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int WindowsCreateString(string source, uint length, out nint hstring);

        [LibraryImport("combase.dll")]
        private static partial int WindowsDeleteString(nint hstring);
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int type);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, out nint instance);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
}
