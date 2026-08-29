using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;

namespace FoxholeDataExtractor;

/// <summary>
/// Exports optional heavyweight assets. This pass is completely skipped when both flags are false.
/// Audio is decoded to its native decoded format (wav/ogg/etc.). Meshes are exported as ActorX
/// (.psk/.pskx) together with materials/textures handled by CUE4Parse-Conversion.
/// </summary>
public sealed class AssetExporter
{
    private readonly DefaultFileProvider _provider;
    private readonly string _output;
    private readonly AssetsConfig _config;

    public AssetExporter(DefaultFileProvider provider, string output, AssetsConfig config)
    {
        _provider = provider;
        _output = output;
        _config = config;
    }

    public AssetExportStats Export()
    {
        var stats = new AssetExportStats();
        if (!_config.Audio && !_config.Meshes) return stats;

        var files = _provider.Files.Values
            .Where(f => f.IsUePackage)
            .GroupBy(f => Normalize(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Asset export pass: scanning {files.Count} UE packages (audio={_config.Audio}, meshes={_config.Meshes})...");

        foreach (var file in files)
        {
            // Do not filter by filename here: Unreal packages containing a USoundWave/UStaticMesh
            // are not required to have Audio/Mesh in their path. When these flags are enabled,
            // correctness is preferred over speed and every UE package is inspected.
            var path = Normalize(file.Path);

            try
            {
                var exports = _provider.LoadPackage(file).GetExports();
                foreach (var export in exports)
                {
                    if (_config.Audio && export is USoundWave or UAkMediaAssetData)
                    {
                        stats.AudioFound++;
                        if (TryExportAudio(export, path)) stats.AudioExported++;
                        else stats.AudioFailed++;
                    }

                    if (_config.Meshes && export is UStaticMesh or USkeletalMesh)
                    {
                        stats.MeshesFound++;
                        if (TryExportMesh(export, path)) stats.MeshesExported++;
                        else stats.MeshesFailed++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN asset package {file.Path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Audio: {stats.AudioExported}/{stats.AudioFound} exported ({stats.AudioFailed} failed)");
        Console.WriteLine($"Meshes: {stats.MeshesExported}/{stats.MeshesFound} exported ({stats.MeshesFailed} failed)");
        return stats;
    }

    private bool TryExportAudio(object export, string packagePath)
    {
        try
        {
            string format;
            byte[]? bytes;
            string name;

            switch (export)
            {
                case USoundWave sound:
                    sound.Decode(true, out format, out bytes);
                    name = sound.Name;
                    break;
                case UAkMediaAssetData wwise:
                    wwise.Decode(true, out format, out bytes);
                    name = wwise.Name;
                    break;
                default:
                    return false;
            }

            if (bytes is null || bytes.Length == 0) return false;
            var extension = SafeExtension(format);
            var relativePackage = ContentRelative(packagePath);
            var folder = Path.GetDirectoryName(relativePackage)?.Replace('\\', '/') ?? "other";
            var destination = Path.Combine(_output, "assets", "audio", folder.Replace('/', Path.DirectorySeparatorChar), Safe(name) + "." + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN audio {packagePath}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryExportMesh(object export, string packagePath)
    {
        try
        {
            if (export is not CUE4Parse.UE4.Assets.Exports.UObject obj) return false;

            var category = MeshCategory(packagePath);
            var root = new DirectoryInfo(Path.Combine(_output, "assets", "meshes", category));
            Directory.CreateDirectory(root.FullName);

            var options = new ExporterOptions
            {
                ExportMaterials = true,
                ExportMorphTargets = true
            };
            var exporter = new Exporter(obj, options);
            return exporter.TryWriteToDir(root, out _, out _);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN mesh {packagePath}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string MeshCategory(string p)
    {
        if (p.Contains("/Vehicles/", StringComparison.OrdinalIgnoreCase)) return "vehicles";
        if (p.Contains("/Structures/", StringComparison.OrdinalIgnoreCase)) return "structures";
        if (p.Contains("/Items/", StringComparison.OrdinalIgnoreCase) || p.Contains("/Item", StringComparison.OrdinalIgnoreCase)) return "items";
        if (p.Contains("/Characters/", StringComparison.OrdinalIgnoreCase)) return "characters";
        if (p.Contains("/Environment/", StringComparison.OrdinalIgnoreCase) || p.Contains("/World/", StringComparison.OrdinalIgnoreCase)) return "environment";
        return "other";
    }

    private static string ContentRelative(string p)
    {
        var value = WithoutExtension(Normalize(p));
        var marker = "Content/";
        var i = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) value = value[(i + marker.Length)..];
        if (value.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) value = value[5..];
        var slash = value.LastIndexOf('/');
        return slash >= 0 ? value[..slash] : "other";
    }

    private static string SafeExtension(string value)
    {
        var ext = (value ?? "bin").Trim().TrimStart('.').ToLowerInvariant();
        return ext.Length > 0 && ext.All(c => char.IsLetterOrDigit(c)) ? ext : "bin";
    }

    private static string WithoutExtension(string p)
    {
        foreach (var ext in new[] { ".uasset", ".umap", ".uexp", ".ubulk" })
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return p[..^ext.Length];
        return p;
    }

    private static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');
    private static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }
}

public sealed class AssetExportStats
{
    public int AudioFound { get; set; }
    public int AudioExported { get; set; }
    public int AudioFailed { get; set; }
    public int MeshesFound { get; set; }
    public int MeshesExported { get; set; }
    public int MeshesFailed { get; set; }
}
