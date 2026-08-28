using Newtonsoft.Json;

namespace FoxholeDataExtractor;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault() ?? "help";
            var gameDir = Arg(args, "--game-dir") ?? Environment.GetEnvironmentVariable("FOXHOLE_GAME_DIR") ?? "/game";
            var output = Arg(args, "--output") ?? Environment.GetEnvironmentVariable("OUTPUT_DIR") ?? "./output";
            var modsDir = Arg(args, "--mods-dir") ?? Environment.GetEnvironmentVariable("MODS_DIR") ?? "/mods";

            if (command == "info")
            {
                Console.WriteLine($"Game: {Path.GetFullPath(gameDir)}");
                Console.WriteLine($"Output: {Path.GetFullPath(output)}");
                Console.WriteLine($"PAKs: {GameProvider.ResolvePakDirectory(gameDir)}");
                return 0;
            }

            if (command == "diff")
                return RunDiff(args, output);

            if (command != "extract")
            {
                Console.WriteLine("Usage: extract [--game-dir PATH] [--output PATH] [--config FILE] [--build-id ID] [--mods-dir PATH]");
                Console.WriteLine("       diff --old FILE --new FILE [--output PATH]");
                Console.WriteLine("       info [--game-dir PATH] [--output PATH]");
                return command == "help" ? 0 : 2;
            }

            var configPath = Arg(args, "--config") ?? "./config/extraction.json";
            var config = JsonConvert.DeserializeObject<ExtractionConfig>(File.ReadAllText(configPath))
                         ?? throw new InvalidOperationException("Invalid extraction config");
            var provider = GameProvider.Create(gameDir);
            var modsProvider = GameProvider.CreateOptionalMods(modsDir);
            var result = new Extractor(provider, modsProvider, config, Path.GetFullPath(output), Path.GetFullPath(gameDir))
                .Run(Arg(args, "--build-id"));
            Console.WriteLine($"Done: {result.Items.Count} catalogue entries.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunDiff(string[] args, string output)
    {
        var oldPath = Arg(args, "--old") ?? throw new ArgumentException("--old is required");
        var newPath = Arg(args, "--new") ?? throw new ArgumentException("--new is required");
        var oldItems = JsonConvert.DeserializeObject<List<Newtonsoft.Json.Linq.JObject>>(File.ReadAllText(oldPath)) ?? new();
        var newItems = JsonConvert.DeserializeObject<List<Newtonsoft.Json.Linq.JObject>>(File.ReadAllText(newPath)) ?? new();

        var oldMap = oldItems.Where(x => x["CodeName"] != null).ToDictionary(x => x["CodeName"]!.ToString(), StringComparer.Ordinal);
        var newMap = newItems.Where(x => x["CodeName"] != null).ToDictionary(x => x["CodeName"]!.ToString(), StringComparer.Ordinal);
        var added = newMap.Keys.Except(oldMap.Keys).Order().ToArray();
        var removed = oldMap.Keys.Except(newMap.Keys).Order().ToArray();
        var modified = newMap.Keys.Intersect(oldMap.Keys)
            .Where(k => !Equals(newMap[k], oldMap[k])).Order().ToArray();

        var result = new { generatedAt = DateTimeOffset.UtcNow, added, modified, removed };
        var path = Path.Combine(output, "changes.json");
        Extractor.WriteJson(path, result);
        Console.WriteLine($"Added {added.Length}, modified {modified.Length}, removed {removed.Length}");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
