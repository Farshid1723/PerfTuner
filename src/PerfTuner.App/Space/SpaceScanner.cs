using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using PerfTuner.Core.Space;

namespace PerfTuner.App.Space;

/// <summary>Scan progress, reported about every 250 ms.</summary>
public readonly record struct ScanProgress(long Files, long Bytes, long Folders, int NoAccess, string Current, TimeSpan Elapsed);

/// <summary>
/// Builds a <see cref="SpaceNode"/> tree with the documented directory enumeration (FindFirstFileEx through
/// <see cref="FileSystemEnumerable{TResult}"/>; works without admin and on every file system). Junctions and symbolic
/// links are listed but never followed; cloud-only placeholders count as 0 bytes (they use no disk space); folders that
/// can't be read are flagged "no access". Up to 3 worker threads in background mode (low CPU and I/O priority).
/// </summary>
public static partial class SpaceScanner
{
    private const int MaxWorkers = 3;
    private const long AggregateAbove = 3_000_000;
    private const long SmallFile = 64 * 1024;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private sealed class Counters
    {
        public long Files;
        public long Bytes;
        public long Folders;
        public int NoAccess;
        public volatile string Current = "";
    }

    /// <summary>
    /// Scans a whole drive ("X:\"). <paramref name="ready"/> receives batches of finished, recalculated top-level nodes
    /// (first the files at the drive root, then each top-level folder when it is done) from worker threads; the caller
    /// attaches them (<see cref="SpaceNode.Attach"/>) on its own thread. Cancelling keeps what was delivered.
    /// </summary>
    public static async Task ScanDriveAsync(string root, Action<IReadOnlyList<SpaceNode>> ready, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ready);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') { throw new ArgumentException("expected a drive root like C:\\", nameof(root)); }
        var c = new Counters();
        var started = DateTime.UtcNow;
        using var timer = new Timer(_ => progress?.Report(new ScanProgress(c.Files, c.Bytes, c.Folders, c.NoAccess, c.Current, DateTime.UtcNow - started)), null, 250, 250);

        var folders = new List<(string Name, DateTime Write, FileAttributes Attr)>();
        var rootFiles = new List<SpaceNode>();
        await Task.Run(() =>
        {
            foreach (var e in Entries(root))
            {
                if (e.Dir)
                {
                    if (e.Attr.HasFlag(FileAttributes.ReparsePoint)) { rootFiles.Add(Reparse(e.Name, e.Write, e.Attr)); }
                    else { folders.Add((e.Name, e.Write, e.Attr)); }
                }
                else
                {
                    rootFiles.Add(FileNode(e, c));
                }
            }
        }, ct).ConfigureAwait(false);
        ready(rootFiles);

        await Parallel.ForEachAsync(folders, new ParallelOptions { MaxDegreeOfParallelism = MaxWorkers, CancellationToken = ct }, (f, token) =>
        {
            var node = ScanFolderInBackground(root + f.Name, f.Name, f.Write, f.Attr, c, token);
            if (!token.IsCancellationRequested) { ready([node]); }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        progress?.Report(new ScanProgress(c.Files, c.Bytes, c.Folders, c.NoAccess, "", DateTime.UtcNow - started));
    }

    /// <summary>Scans one folder again (for "Scan this folder again" and after operations). The name is the last segment.</summary>
    public static Task<SpaceNode> ScanFolderAsync(string fullPath, string name, CancellationToken ct) =>
        Task.Run(() =>
        {
            var attr = File.GetAttributes(fullPath);
            if (attr.HasFlag(FileAttributes.ReparsePoint) || !attr.HasFlag(FileAttributes.Directory))
            {
                throw new IOException("This folder has become a link (or a file) since the scan; scan the whole drive again.");
            }
            return ScanFolderInBackground(fullPath, name, Directory.GetLastWriteTimeUtc(fullPath), attr, new Counters(), ct);
        }, ct);

    private static SpaceNode ScanFolderInBackground(string fullPath, string name, DateTime write, FileAttributes attr, Counters c, CancellationToken ct)
    {
        var background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        try
        {
            var top = Folder(name, write, attr);
            var stack = new Stack<(SpaceNode Node, string Path)>();
            stack.Push((top, fullPath));
            while (stack.Count > 0 && !ct.IsCancellationRequested)
            {
                var (node, path) = stack.Pop();
                c.Current = path;
                Interlocked.Increment(ref c.Folders);
                long smallCount = 0, smallBytes = 0;
                var aggregate = Interlocked.Read(ref c.Files) > AggregateAbove;
                try
                {
                    foreach (var e in Entries(path))
                    {
                        if (e.Dir)
                        {
                            if (e.Attr.HasFlag(FileAttributes.ReparsePoint)) { node.Add(Reparse(e.Name, e.Write, e.Attr)); continue; }
                            var child = Folder(e.Name, e.Write, e.Attr);
                            node.Add(child);
                            stack.Push((child, path + "\\" + e.Name));
                        }
                        else if (aggregate && e.Len < SmallFile)
                        {
                            smallCount++;
                            smallBytes += e.Len;
                            Interlocked.Increment(ref c.Files);
                            Interlocked.Add(ref c.Bytes, e.Len);
                        }
                        else
                        {
                            node.Add(FileNode(e, c));
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    node.Flags |= SpaceFlags.NoAccess;
                    Interlocked.Increment(ref c.NoAccess);
                }
                if (smallCount > 0) { node.Add(SpaceNode.NewAggregate(smallCount, smallBytes)); }
                node.Children?.TrimExcess();
            }
            top.Recalculate();
            return top;
        }
        finally
        {
            if (background) { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); }
        }
    }

    private readonly record struct Entry(string Name, bool Dir, long Len, DateTime Write, FileAttributes Attr);

    private static FileSystemEnumerable<Entry> Entries(string path) =>
        new(path, static (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcDateTime, e.Attributes), Options);

    private static SpaceNode FileNode(Entry e, Counters c)
    {
        // Cloud-only placeholders (OneDrive "free up space") don't use disk space.
        var cloudOnly = (e.Attr & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;
        var size = cloudOnly ? 0 : e.Len;
        Interlocked.Increment(ref c.Files);
        Interlocked.Add(ref c.Bytes, size);
        var n = SpaceNode.NewFile(e.Name, size, e.Write);
        n.Flags = Flags(e.Attr);
        return n;
    }

    private static SpaceNode Folder(string name, DateTime write, FileAttributes attr)
    {
        var n = SpaceNode.NewFolder(name, write);
        n.Flags = Flags(attr);
        return n;
    }

    private static SpaceNode Reparse(string name, DateTime write, FileAttributes attr)
    {
        var n = SpaceNode.NewFile(name, 0, write);
        n.Flags = Flags(attr) | SpaceFlags.ReparsePoint;
        return n;
    }

    private static SpaceFlags Flags(FileAttributes a) =>
        (a.HasFlag(FileAttributes.Hidden) ? SpaceFlags.Hidden : 0) | (a.HasFlag(FileAttributes.System) ? SpaceFlags.System : 0);

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);
}
