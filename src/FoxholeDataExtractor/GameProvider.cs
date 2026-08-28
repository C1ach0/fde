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
