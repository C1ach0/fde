using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

public sealed class Extractor
{
    private readonly DefaultFileProvider _provider;
    private readonly DefaultFileProvider? _modsProvider;
    private readonly ExtractionConfig _config;
    private readonly string _output;
    private readonly string _gameDirectory;

    public Extractor(DefaultFileProvider provider, DefaultFileProvider? modsProvider, ExtractionConfig config, string output, string gameDirectory)
    {
        _provider = provider;
        _modsProvider = modsProvider;
        _config = config;
        _output = output;
        _gameDirectory = gameDirectory;
    }

    public CatalogDocument Run(string? buildId)
    {
        Directory.CreateDirectory(Path.Combine(_output, "raw"));
        PrintProviderDiagnostics("game", _provider);
        if (_modsProvider != null) PrintProviderDiagnostics("mods", _modsProvider);

        var exported = 0;
        var failed = 0;
        var rawPackages = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

        var packages = _provider.Files.Values
            .Where(f => f.IsUePackage)
            .GroupBy(f => NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var files = packages
            .Where(f => MatchesConfiguredPath(f.Path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"UE packages visible: {packages.Count}");
        Console.WriteLine($"Selected {files.Count} package files from configured prefixes.");
        if (files.Count == 0)
        {
            Console.WriteLine("WARN: configured prefixes matched 0 packages. Applying Foxhole/FIR fallback (Blueprints + Data).");
            files = packages.Where(f => IsFoxholeDataPackage(f.Path))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine($"Fallback selected {files.Count} package files.");
        }

        foreach (var file in files)
        {
            try
            {
                var exports = _provider.LoadPackage(file).GetExports();
                var token = JToken.FromObject(exports, JsonSerializer.Create(JsonSettings()));
                var packagePath = PackagePath(file.Path);
                rawPackages[packagePath] = token;
                if (_config.WriteRawJson) WriteRaw(packagePath, token);
                exported++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"WARN {file.Path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Raw export: success={exported}, failed={failed}");
        Console.WriteLine("Building FIR-style catalog (Blueprint inheritance + common data tables)...");
        var items = new CatalogBuilder(rawPackages).Build();
        Console.WriteLine($"FIR-style catalog entries: {items.Count}");

        IconStats iconStats = new();
        if (_config.ExportIcons)
        {
            iconStats = new IconExporter(_provider, _modsProvider, _output).Export(items);
            Console.WriteLine($"Icons: original={iconStats.Original}, clean={iconStats.Clean}, missing-original={iconStats.MissingOriginal}, missing-clean={iconStats.MissingClean}");
        }

        var document = new CatalogDocument(DateTimeOffset.UtcNow, buildId, items);
        WriteJson(Path.Combine(_output, "catalog.json"), items);
        WriteJson(Path.Combine(_output, "version.json"), new VersionDocument(
            DateTimeOffset.UtcNow, buildId, _gameDirectory, exported, items.Count,
            iconStats.Original, iconStats.Clean, iconStats.MissingOriginal, iconStats.MissingClean));
        return document;
    }

    private bool MatchesConfiguredPath(string rawPath)
    {
        var path = NormalizePath(rawPath);

        var extensionOk = _config.IncludeExtensions.Length == 0 ||
            _config.IncludeExtensions.Any(ext =>
                path.EndsWith("." + ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

        // IsUePackage is authoritative. Some provider entries do not expose the extension in
        // exactly the same form as FModel, so an extension mismatch must not discard a package.
        if (!extensionOk && !path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            // Continue with prefix matching anyway; the caller already filtered IsUePackage.
        }

        if (_config.IncludePrefixes.Length == 0) return true;
        return _config.IncludePrefixes.Any(prefix => PrefixMatches(path, prefix));
    }

    private static bool PrefixMatches(string rawPath, string rawPrefix)
    {
        var path = NormalizePath(rawPath);
        var prefix = NormalizePath(rawPrefix).TrimEnd('/') + "/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // FModel commonly represents /Game as War/Content for Foxhole while mounted provider
        // paths may use one or the other. Compare the path below Content as a compatibility layer.
        var pathContent = AfterContent(path);
        var prefixContent = AfterContent(prefix);
        return pathContent != null && prefixContent != null &&
               pathContent.StartsWith(prefixContent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFoxholeDataPackage(string rawPath)
    {
        var path = "/" + NormalizePath(rawPath).TrimStart('/');
        return path.Contains("/Content/Blueprints/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Content/Data/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Game/Blueprints/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Game/Data/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? AfterContent(string raw)
    {
        var value = NormalizePath(raw);
        var marker = "Content/";
        var i = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return value[(i + marker.Length)..];
        if (value.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) return value["Game/".Length..];
        return null;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string PackagePath(string path)
    {
        var normalized = NormalizePath(path);
        foreach (var ext in new[] { ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl" })
            if (normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return normalized[..^ext.Length];
        return normalized;
    }

    private static void PrintProviderDiagnostics(string name, DefaultFileProvider provider)
    {
        var all = provider.Files.Values.ToList();
        var packages = all.Where(f => f.IsUePackage).ToList();
        Console.WriteLine($"CUE4Parse [{name}]: files={all.Count}, ue-packages={packages.Count}");
        foreach (var file in all.Take(15)) Console.WriteLine($"  [{name}] {file.Path}");
        if (all.Count > 15) Console.WriteLine($"  [{name}] ... and {all.Count - 15} more files");
    }

    private void WriteRaw(string packagePath, JToken token)
    {
        var safePath = NormalizePath(packagePath);
        var destination = Path.Combine(_output, "raw", safePath.Replace('/', Path.DirectorySeparatorChar) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, token.ToString(Formatting.Indented));
    }

    public static JsonSerializerSettings JsonSettings() => new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore
    };

    public static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented, JsonSettings()));
    }
}
