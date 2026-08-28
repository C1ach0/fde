using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

public sealed class IconExporter
{
    private const int MaxDiagnosticFailures = 8;
    private static int _diagnosticFailures;
    private readonly DefaultFileProvider _original;
    private readonly DefaultFileProvider? _clean;
    private readonly string _output;

    public IconExporter(DefaultFileProvider original, DefaultFileProvider? clean, string output)
    {
        _original = original;
        _clean = clean;
        _output = output;
    }

    public IconStats Export(List<JObject> entries)
    {
        var originalDir = Path.Combine(_output, "icons", "original");
        var cleanDir = Path.Combine(_output, "icons", "clean");
        Directory.CreateDirectory(originalDir);
        Directory.CreateDirectory(cleanDir);

        var stats = new IconStats();
        foreach (var item in entries)
        {
            var codeName = item["CodeName"]?.ToString();
            var iconAsset = item["Icon"]?.ToString();
            if (string.IsNullOrWhiteSpace(codeName) || string.IsNullOrWhiteSpace(iconAsset)) continue;

            var file = SafeFileName(codeName) + ".png";
            var originalRel = $"icons/original/{file}";
            var cleanRel = $"icons/clean/{file}";
            var originalOk = TryExport(_original, iconAsset, Path.Combine(_output, originalRel));
            var cleanOk = _clean != null && TryExport(_clean, iconAsset, Path.Combine(_output, cleanRel));

            if (originalOk) stats.Original++; else stats.MissingOriginal++;
            if (cleanOk) stats.Clean++; else stats.MissingClean++;
            item["Icons"] = new JObject
            {
                ["Original"] = originalOk ? originalRel : null,
                ["Clean"] = cleanOk ? cleanRel : null
            };
        }

        // Also export every texture from the optional clean-icon PAK. This is intentionally
        // independent from catalog mapping: a newly added mod icon is still preserved even if
        // Foxhole/FIR metadata changes. Mapped CodeName files above remain the consumer-friendly API.
        if (_clean != null)
        {
            var unmappedDir = Path.Combine(cleanDir, "_assets");
            var all = _clean.Files.Values
                .Where(f => f.IsUePackage && IsLikelyIcon(f.Path))
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var assetExports = 0;
            foreach (var f in all)
            {
                var dest = Path.Combine(unmappedDir, SafeAssetName(f.Path) + ".png");
                if (File.Exists(dest)) continue;
                if (TryExport(_clean, f.Path, dest)) assetExports++;
            }
            Console.WriteLine($"Clean icon asset sweep: candidates={all.Count}, exported={assetExports} -> icons/clean/_assets");
        }

        return stats;
    }

    private static bool TryExport(DefaultFileProvider provider, string rawReference, string destination)
    {
        foreach (var packagePath in PackageCandidates(rawReference))
        {
            try
            {
                var file = provider.Files.Values.FirstOrDefault(f =>
                    f.IsUePackage && string.Equals(WithoutExtension(f.Path), WithoutExtension(packagePath), StringComparison.OrdinalIgnoreCase));
                if (file != null)
                {
                    var packageByFile = provider.LoadPackage(file);
                    if (TryWriteTexture(packageByFile.GetExports().OfType<UTexture>().FirstOrDefault(), destination)) return true;
                }

                var package = provider.LoadPackage(packagePath);
                if (TryWriteTexture(package.GetExports().OfType<UTexture>().FirstOrDefault(), destination)) return true;
            }
            catch (Exception ex)
            {
                LogFailure($"load '{packagePath}'", ex);
            }
        }
        return false;
    }

    private static bool TryWriteTexture(UTexture? texture, string destination)
    {
        if (texture is null)
        {
            LogFailure($"texture lookup for '{destination}'", new InvalidOperationException("Package contains no UTexture export."));
            return false;
        }

        try
        {
            // This is the path used by CUE4Parse's current texture example/exporter.
            // Foxhole's Windows assets use the DesktopMobile texture platform.
            var bitmap = texture.Decode(ETexturePlatform.DesktopMobile);
            if (bitmap is null)
            {
                LogFailure($"decode '{texture.Name}'", new InvalidOperationException("CUE4Parse returned a null bitmap."));
                return false;
            }

            var bytes = bitmap.Encode(ETextureFormat.Png, false, out var extension);
            if (bytes is null || bytes.Length == 0)
            {
                LogFailure($"encode '{texture.Name}'", new InvalidOperationException($"PNG encoder returned no bytes (extension={extension})."));
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            return true;
        }
        catch (Exception ex)
        {
            LogFailure($"decode/encode '{texture.Name}' ({texture.GetType().Name})", ex);
            return false;
        }
    }

    private static void LogFailure(string operation, Exception ex)
    {
        var n = Interlocked.Increment(ref _diagnosticFailures);
        if (n > MaxDiagnosticFailures) return;
        Console.Error.WriteLine($"ICON DEBUG {n}/{MaxDiagnosticFailures}: {operation}");
        Console.Error.WriteLine($"  {ex.GetType().FullName}: {ex.Message}");
        if (ex.InnerException != null)
            Console.Error.WriteLine($"  inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
        if (n == MaxDiagnosticFailures)
            Console.Error.WriteLine("ICON DEBUG: further icon failures suppressed.");
    }

    private static IEnumerable<string> PackageCandidates(string value)
    {
        var s = value.Trim().Trim('"', '\'');
        var quote = s.IndexOf('\'');
        if (quote >= 0 && s.EndsWith("'")) s = s[(quote + 1)..^1];
        s = s.Replace('\\', '/');
        var dot = s.LastIndexOf('.');
        if (dot > s.LastIndexOf('/')) s = s[..dot];
        s = s.TrimStart('/');
        var candidates = new List<string> { s };
        if (s.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) candidates.Add("War/Content/" + s[5..]);
        if (s.StartsWith("War/Content/", StringComparison.OrdinalIgnoreCase)) candidates.Add("Game/" + s["War/Content/".Length..]);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLikelyIcon(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/UI/", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("Icon", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("/Slate/", StringComparison.OrdinalIgnoreCase);
    }

    private static string WithoutExtension(string s)
    {
        s = s.Replace('\\', '/').TrimStart('/');
        if (s.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) s = s[..^7];
        return s;
    }

    private static string SafeAssetName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
        return SafeFileName(name);
    }

    private static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }
}
