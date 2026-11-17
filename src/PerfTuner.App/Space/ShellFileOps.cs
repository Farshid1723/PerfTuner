using System.IO;
using System.Runtime.InteropServices;

namespace PerfTuner.App.Space;

/// <summary>What happened to one item.</summary>
public enum ItemOutcome
{
    Recycled,
    DeletedPermanently,
    Moved,
    Failed,
    Skipped,
}

public sealed record ItemResult(string Path, ItemOutcome Outcome, string? NewPath = null);

/// <summary>
/// Delete / recycle / move through the documented Shell <c>IFileOperation</c> interface, so Windows shows its own
/// progress, conflict and "too big for the Recycle Bin" dialogs. A progress sink records the real outcome per item
/// (recycled vs deleted for good, moved to where). Runs on its own STA thread; the caller passes validated paths only.
/// </summary>
public static partial class ShellFileOps
{
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x00080000;

    /// <summary>To the Recycle Bin. Items that can't be recycled trigger Windows' own warning instead of a silent permanent delete.</summary>
    public static Task<IReadOnlyList<ItemResult>> RecycleAsync(IReadOnlyList<string> paths, nint owner) =>
        RunAsync(paths, null, FofNoConfirmation | FofWantNukeWarning | FofAllowUndo | FofxRecycleOnDelete, owner);

    /// <summary>Deleted for good (the caller has already shown the "can't be undone" confirmation).</summary>
    public static Task<IReadOnlyList<ItemResult>> DeletePermanentlyAsync(IReadOnlyList<string> paths, nint owner) =>
        RunAsync(paths, null, FofNoConfirmation, owner);

    /// <summary>Moved into <paramref name="destinationFolder"/>; Windows asks about name conflicts.</summary>
    public static Task<IReadOnlyList<ItemResult>> MoveAsync(IReadOnlyList<string> paths, string destinationFolder, nint owner) =>
        RunAsync(paths, destinationFolder, FofNoConfirmMkdir | FofAllowUndo, owner);

    private static Task<IReadOnlyList<ItemResult>> RunAsync(IReadOnlyList<string> paths, string? destination, uint flags, nint owner)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<ItemResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Run(paths, destination, flags, owner)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "PerfTuner file operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Callbacks received during the last operation (diagnostics; in memory only, never logged).</summary>
    public static IReadOnlyList<string> LastCallbacks { get; private set; } = [];

    private static IReadOnlyList<ItemResult> Run(IReadOnlyList<string> paths, string? destination, uint flags, nint owner)
    {
        var sink = new Sink();
        var op = (IFileOperation)new FileOperationCoClass();
        var shellItems = new List<object>();
        uint cookie = 0;
        var advised = false;
        try
        {
            op.SetOperationFlags(flags);
            if (owner != 0) { op.SetOwnerWindow(owner); }
            cookie = op.Advise(sink);
            advised = true;
            IShellItem? dest = null;
            if (destination is not null)
            {
                dest = CreateItem(destination);
                shellItems.Add(dest);
            }
            var queued = new List<string>();
            foreach (var p in paths)
            {
                if (!File.Exists(p) && !Directory.Exists(p)) { sink.Results[p] = new ItemResult(p, ItemOutcome.Skipped); continue; }
                var item = CreateItem(p);
                shellItems.Add(item);
                if (dest is null) { op.DeleteItem(item, null); } else { op.MoveItem(item, dest, null, null); }
                queued.Add(p);
            }
            if (queued.Count > 0)
            {
                try { op.PerformOperations(); }
                catch (COMException) { /* cancelled or partly failed: per-item results below tell what happened */ }
            }
            LastCallbacks = [.. sink.Trace];
            // The disk decides what happened. The per-item HRESULTs are success codes such as
            // COPYENGINE_S_DONT_PROCESS_CHILDREN (0x00270008) for a normal delete or move, so they can't be compared
            // with S_OK. An item still at its old place was not deleted or moved (Skip, Cancel, "No" to the
            // "too big for the Recycle Bin" question); a Recycle Bin copy reported by Windows means "recycled".
            return paths.Select(p =>
            {
                if (sink.Results.TryGetValue(p, out var skipped) && skipped.Outcome == ItemOutcome.Skipped) { return skipped; }
                var stillThere = File.Exists(p) || Directory.Exists(p);
                if (stillThere) { return new ItemResult(p, ItemOutcome.Failed); }
                sink.NewPaths.TryGetValue(p, out var newPath);
                if (destination is not null)
                {
                    newPath ??= Path.Combine(destination, Path.GetFileName(p.TrimEnd('\\')));
                    return File.Exists(newPath) || Directory.Exists(newPath)
                        ? new ItemResult(p, ItemOutcome.Moved, newPath)
                        : new ItemResult(p, ItemOutcome.Failed);
                }
                return new ItemResult(p, newPath is not null ? ItemOutcome.Recycled : ItemOutcome.DeletedPermanently);
            }).ToList();
        }
        finally
        {
            if (advised) { try { op.Unadvise(cookie); } catch (COMException) { } }
            foreach (var i in shellItems) { Marshal.FinalReleaseComObject(i); }
            Marshal.FinalReleaseComObject(op);
        }
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var unknown));
        try { return (IShellItem)Marshal.GetObjectForIUnknown(unknown); }
        finally { Marshal.Release(unknown); }
    }

    private static string? PathOf(IShellItem? item)
    {
        if (item is null) { return null; }
        try
        {
            item.GetDisplayName(SigdnFileSysPath, out var p);
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (COMException)
        {
            return null;
        }
    }

    private const uint SigdnFileSysPath = 0x80058000;

    [ComVisible(true)]
    private sealed class Sink : IFileOperationProgressSink
    {
        public Dictionary<string, ItemResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Trace { get; } = [];

        /// <summary>Where Windows put each item: the moved item, or its copy in the Recycle Bin.</summary>
        public Dictionary<string, string> NewPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void StartOperations() => Trace.Add("start");
        public void FinishOperations(int hrResult) => Trace.Add($"finish hr=0x{hrResult:X8}");
        public void PreRenameItem(uint flags, IShellItem item, string newName) { }
        public void PostRenameItem(uint flags, IShellItem item, string newName, int hrRename, IShellItem newlyCreated) { }
        public void PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string newName) { }

        public void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string newName, int hrMove, IShellItem newlyCreated)
        {
            Trace.Add($"postMove hr=0x{hrMove:X8} item={PathOf(item)} new={(newlyCreated is null ? "null" : PathOf(newlyCreated) ?? "?")}");
            if (PathOf(item) is { } p && PathOf(newlyCreated) is { } n) { NewPaths[p] = n; }
        }

        public void PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string newName) { }
        public void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string newName, int hrCopy, IShellItem newlyCreated) { }
        public void PreDeleteItem(uint flags, IShellItem item) { }

        public void PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem newlyCreated)
        {
            Trace.Add($"postDelete hr=0x{hrDelete:X8} item={PathOf(item)} new={(newlyCreated is null ? "null" : PathOf(newlyCreated) ?? "?")}");
            // A newly created item means it now lives in the Recycle Bin; none means it is gone for good.
            if (PathOf(item) is { } p && PathOf(newlyCreated) is { } n) { NewPaths[p] = n; }
        }

        public void PreNewItem(uint flags, IShellItem destinationFolder, string newName) { }
        public void PostNewItem(uint flags, IShellItem destinationFolder, string newName, string templateName, uint fileAttributes, int hrNew, IShellItem newItem) { }
        public void UpdateProgress(uint workTotal, uint workSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    /// <summary>Opens Explorer at the item with it selected (documented SHOpenFolderAndSelectItems; no process is started by PerfTuner).</summary>
    public static void ShowInExplorer(string path)
    {
        if (SHParseDisplayName(path, 0, out var pidl, 0, out _) != 0 || pidl == 0) { return; }
        try { _ = SHOpenFolderAndSelectItems(pidl, 0, 0, 0); }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid, out nint item);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [LibraryImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SHOpenFolderAndSelectItems(nint pidlFolder, uint count, nint children, uint flags);

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private class FileOperationCoClass;

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint hwnd);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IFileOperationProgressSink? sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destinationFolder);
        void CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IFileOperationProgressSink? sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destinationFolder);
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IFileOperationProgressSink? sink);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        void StartOperations();
        void FinishOperations(int hrResult);
        void PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int hrRename, IShellItem newlyCreated);
        void PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, int hrMove, IShellItem newlyCreated);
        void PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, int hrCopy, IShellItem newlyCreated);
        void PreDeleteItem(uint flags, IShellItem item);
        void PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem newlyCreated);
        void PreNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string templateName, uint fileAttributes, int hrNew, IShellItem newItem);
        void UpdateProgress(uint workTotal, uint workSoFar);
        void ResetTimer();
        void PauseTimer();
        void ResumeTimer();
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdnName, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
