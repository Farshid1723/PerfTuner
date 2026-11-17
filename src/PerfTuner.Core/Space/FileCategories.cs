namespace PerfTuner.Core.Space;

public enum FileCategory
{
    Video,
    Audio,
    Images,
    DiskImages,
    Archives,
    Installers,
    Documents,
    CodeAndBuild,
    DataAndLogs,
    Other,
}

/// <summary>Well-known file types by extension, for the "by type" reports and the treemap/sunburst colours.</summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    public static FileCategory Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? FileCategory.Other
            : Map.TryGetValue(name[(dot + 1)..], out var c) ? c : FileCategory.Other;
    }

    public static string Title(FileCategory c) => c switch
    {
        FileCategory.Video => "Videos",
        FileCategory.Audio => "Music & audio",
        FileCategory.Images => "Pictures",
        FileCategory.DiskImages => "Disk images & virtual machines",
        FileCategory.Archives => "Archives",
        FileCategory.Installers => "Installers",
        FileCategory.Documents => "Documents",
        FileCategory.CodeAndBuild => "Code & build output",
        FileCategory.DataAndLogs => "Databases & logs",
        _ => "Other",
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) { d[e] = c; } }
        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "mpg", "mpeg", "ts", "m2ts", "flv", "3gp");
        Add(FileCategory.Audio, "mp3", "flac", "wav", "aac", "m4a", "ogg", "wma", "opus", "aiff");
        Add(FileCategory.Images, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "webp", "raw", "cr2", "nef", "arw", "dng", "psd", "svg");
        Add(FileCategory.DiskImages, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "wim", "esd", "avhdx", "vmsn", "vmem");
        Add(FileCategory.Archives, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab");
        Add(FileCategory.Installers, "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "msp", "msu");
        Add(FileCategory.Documents, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "md", "epub");
        Add(FileCategory.CodeAndBuild, "pdb", "obj", "o", "lib", "dll", "pch", "ipch", "class", "jar", "nupkg", "whl", "so", "a");
        Add(FileCategory.DataAndLogs, "log", "etl", "db", "sqlite", "mdf", "ldf", "bak", "dmp", "edb", "csv", "parquet", "json");
        return d;
    }
}
