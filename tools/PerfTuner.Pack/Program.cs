using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PerfTuner.Core.Setup;

// usage: PerfTuner.Pack <payload-dir> <launcher.exe> <out-dir>
//
// Payload = Brotli( [manifest length: 4 bytes][payload.manifest, UTF-8][content of each distinct file, in manifest order] ).
// Files with identical content (the app and the engine share several assemblies) are stored once.
// Appended to the launcher with a footer: [payload length: 8][mode: 1]["PTPAYLD2"]; mode P = portable, S = setup.
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: PerfTuner.Pack <payload-dir> <launcher.exe> <out-dir>");
    return 2;
}
var (root, launcher, outDir) = (Path.GetFullPath(args[0]), args[1], args[2]);
Directory.CreateDirectory(outDir);

var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
    .Select(f =>
    {
        var rel = Path.GetRelativePath(root, f).Replace('/', '\\');
        using var fs = File.OpenRead(f);
        return new PayloadEntry(rel, fs.Length, Convert.ToHexStringLower(SHA256.HashData(fs)));
    })
    .ToList();
var manifest = Encoding.UTF8.GetBytes(PayloadManifest.Format(entries));
var ordered = PayloadManifest.Parse(Encoding.UTF8.GetString(manifest)); // same order the launcher reads

using var payload = new MemoryStream();
using (var brotli = new BrotliStream(payload, CompressionLevel.SmallestSize, leaveOpen: true))
{
    brotli.Write(BitConverter.GetBytes(manifest.Length));
    brotli.Write(manifest);
    var stored = new HashSet<string>(StringComparer.Ordinal);
    foreach (var e in ordered)
    {
        if (!stored.Add(e.Sha256)) { continue; }
        using var fs = File.OpenRead(Path.Combine(root, e.Path.Replace('\\', Path.DirectorySeparatorChar)));
        fs.CopyTo(brotli);
    }
}
var data = payload.ToArray();
var stub = File.ReadAllBytes(launcher);
var sums = new StringBuilder();
foreach (var (name, mode) in new[] { ("PerfTuner-Portable.exe", (byte)'P'), ("PerfTuner-Setup.exe", (byte)'S') })
{
    var path = Path.Combine(outDir, name);
    using (var fs = File.Create(path))
    {
        fs.Write(stub);
        fs.Write(data);
        fs.Write(BitConverter.GetBytes((long)data.Length));
        fs.WriteByte(mode);
        fs.Write("PTPAYLD2"u8);
    }
    using var check = File.OpenRead(path);
    sums.Append(Convert.ToHexStringLower(SHA256.HashData(check))).Append("  ").Append(name).Append('\n');
    Console.WriteLine($"{name}: {new FileInfo(path).Length / 1048576.0:F2} MB");
}
File.WriteAllText(Path.Combine(outDir, "SHA256SUMS.txt"), sums.ToString());
Console.WriteLine($"{entries.Count} files, {entries.Select(e => e.Sha256).Distinct().Count()} distinct, payload {data.Length / 1048576.0:F2} MB, launcher {stub.Length / 1048576.0:F2} MB");
return 0;
