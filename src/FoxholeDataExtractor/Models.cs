using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

public sealed record ExtractionConfig(
    string[] IncludePrefixes,
    string[] IncludeExtensions,
    bool WriteRawJson,
    bool ExportIcons = true);

public sealed record CatalogDocument(
    DateTimeOffset GeneratedAt,
    string? SteamBuildId,
    List<JObject> Items);

public sealed record VersionDocument(
    DateTimeOffset GeneratedAt,
    string? SteamBuildId,
    string GameDirectory,
    int ExportedPackages,
    int CatalogEntries,
    int OriginalIcons,
    int CleanIcons,
    int MissingOriginalIcons,
    int MissingCleanIcons);

public sealed class IconStats
{
    public int Original { get; set; }
    public int Clean { get; set; }
    public int MissingOriginal { get; set; }
    public int MissingClean { get; set; }
}
