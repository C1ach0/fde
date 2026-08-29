using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace FoxholeDataExtractor;

public static class GameProvider
{
    public static DefaultFileProvider Create(string gameDirectory)
    {
        var pakDirectory = ResolvePakDirectory(gameDirectory);
        Console.WriteLine($"PAK directory: {pakDirectory}");
        PrintPhysicalPaks("game", pakDirectory);
        return CreateFromPakDirectory("game", pakDirectory);
    }

    public static DefaultFileProvider? CreateOptionalMods(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory) || !Directory.EnumerateFiles(modsDirectory, "*.pak", SearchOption.TopDirectoryOnly).Any())
        {
            Console.WriteLine($"No optional PAK found in {modsDirectory}; clean icons disabled.");
            return null;
        }

        Console.WriteLine($"Optional PAK directory: {Path.GetFullPath(modsDirectory)}");
        PrintPhysicalPaks("mods", modsDirectory);
        return CreateFromPakDirectory("mods", modsDirectory);
    }

    private static DefaultFileProvider CreateFromPakDirectory(string name, string directory)
    {
        // IMPORTANT:
        // Initialize() scans/registers the VFS archives. It does not mean their file indexes
        // are already exposed through provider.Files. Mount() is the step that mounts the
        // registered PAK readers and populates the provider with their entries.
        //
        // Use the current constructor form rather than the obsolete bool isCaseInsensitive
        // overload so the path comparer is explicit.
        var versions = new VersionContainer(EGame.GAME_UE4_24);
        var provider = new DefaultFileProvider(
            new DirectoryInfo(directory),
            SearchOption.TopDirectoryOnly,
            versions,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            Console.WriteLine($"CUE4Parse [{name}]: Initialize()...");
            provider.Initialize();
            Console.WriteLine($"CUE4Parse [{name}]: Initialize() OK; files-before-mount={provider.Files.Count}");

            Console.WriteLine($"CUE4Parse [{name}]: Mount()...");
            var mounted = provider.Mount();
            Console.WriteLine($"CUE4Parse [{name}]: Mount() OK; newly-mounted={mounted}; files-after-mount={provider.Files.Count}");

            provider.PostMount();
            Console.WriteLine($"CUE4Parse [{name}]: PostMount() OK; files={provider.Files.Count}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: CUE4Parse [{name}] failed while initializing/mounting '{directory}'.");
            Console.Error.WriteLine($"       {ex.GetType().FullName}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            throw;
        }

        if (provider.Files.Count == 0)
        {
            Console.Error.WriteLine($"WARN: CUE4Parse [{name}] mounted no visible files from '{directory}'.");
            Console.Error.WriteLine("      Physical .pak files exist, so this is now a VFS/mount/configuration issue, not a catalog prefix issue.");
            Console.Error.WriteLine($"      CUE4Parse game version currently configured as: {versions.Game}");
        }

        return provider;
    }

    private static void PrintPhysicalPaks(string name, string directory)
    {
        var paks = Directory.EnumerateFiles(directory, "*.pak", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine($"Physical PAKs [{name}]: {paks.Count}");
        foreach (var pak in paks)
        {
            var info = new FileInfo(pak);
            Console.WriteLine($"  + {info.Name} ({info.Length / 1024d / 1024d:0.0} MiB)");
        }
    }

    public sealed class MountedMod : IDisposable
    {
        public string Name { get; }
        public DefaultFileProvider Provider { get; }
        private readonly string _stagingDirectory;
        internal MountedMod(string name, DefaultFileProvider provider, string stagingDirectory)
        { Name = name; Provider = provider; _stagingDirectory = stagingDirectory; }
        public void Dispose()
        {
            try { Directory.Delete(_stagingDirectory, true); } catch { }
        }
    }

    /// <summary>Mount every mod PAK independently. This prevents one mod from overriding another
    /// in a merged provider and lets Extractor write output/mods/&lt;ModName&gt;/...</summary>
    public static List<MountedMod> CreateMods(string modsDirectory)
    {
        var result = new List<MountedMod>();
        if (!Directory.Exists(modsDirectory)) return result;
        foreach (var pak in Directory.EnumerateFiles(modsDirectory, "*.pak", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(pak);
            var staging = Path.Combine(Path.GetTempPath(), "foxhole-extractor-mods", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var stagedPak = Path.Combine(staging, Path.GetFileName(pak));
            try { File.CreateSymbolicLink(stagedPak, Path.GetFullPath(pak)); }
            catch { File.Copy(pak, stagedPak); }
            result.Add(new MountedMod(name, CreateFromPakDirectory("mod:" + name, staging), staging));
        }
        return result;
    }

    public static DefaultFileProvider CreateSinglePak(string pakPath, out string stagingDirectory)
    {
        if (!File.Exists(pakPath)) throw new FileNotFoundException("Mod PAK not found", pakPath);
        stagingDirectory = Path.Combine(Path.GetTempPath(), "foxhole-extractor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var target = Path.Combine(stagingDirectory, Path.GetFileName(pakPath));
        try
        {
            File.CreateSymbolicLink(target, Path.GetFullPath(pakPath));
        }
        catch
        {
            File.Copy(pakPath, target, overwrite: true);
        }
        PrintPhysicalPaks(Path.GetFileNameWithoutExtension(pakPath), stagingDirectory);
        return CreateFromPakDirectory(Path.GetFileNameWithoutExtension(pakPath), stagingDirectory);
    }

    public static IReadOnlyList<string> FindModPaks(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(modsDirectory, "*.pak", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ResolvePakDirectory(string gameDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(gameDirectory, "War", "Content", "Paks"),
            Path.Combine(gameDirectory, "Content", "Paks"),
            gameDirectory
        };
        foreach (var candidate in candidates)
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.pak", SearchOption.TopDirectoryOnly).Any())
                return Path.GetFullPath(candidate);
        throw new DirectoryNotFoundException($"Could not find Foxhole PAKs below '{gameDirectory}'. Expected War/Content/Paks.");
    }
}
