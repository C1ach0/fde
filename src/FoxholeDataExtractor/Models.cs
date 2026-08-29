using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

public sealed class ExtractionConfig
{
    public CatalogConfig Catalog { get; set; } = new();
    public RawConfig Raw { get; set; } = new();
    public AssetsConfig Assets { get; set; } = new();
    public MapsConfig Maps { get; set; } = new();
    public ModsConfig Mods { get; set; } = new();
}

public sealed class CatalogConfig
{
    public string[] IncludePrefixes { get; set; } = { "War/Content/Blueprints/", "War/Content/Data/" };
}

public sealed class RawConfig { public bool Enabled { get; set; } = true; }
public sealed class AssetsConfig
{
    public bool Icons { get; set; } = true;
    public bool Maps { get; set; } = true;
    public bool Audio { get; set; } = true;
    public bool Meshes { get; set; } = true;
}
public sealed class MapsConfig
{
    public bool Enabled { get; set; } = true;
    public bool CalculateRegions { get; set; } = true;
    // Major map markers are the official basis of Region Zones. Using the static War API
    // avoids having to deserialize every .umap just to recover the Voronoi sites.
    public bool UseWarApiRegions { get; set; } = true;
    public string WarApiBaseUrl { get; set; } = "https://war-service-live.foxholeservices.com/api/worldconquest";
}
public sealed class ModsConfig
{
    public bool Enabled { get; set; } = true;
    public bool WriteRawJson { get; set; } = false;
}

public sealed record CatalogDocument(DateTimeOffset GeneratedAt, string? SteamBuildId, List<JObject> Items);
public sealed record VersionDocument(DateTimeOffset GeneratedAt, string? SteamBuildId, string GameDirectory,
    int ExportedPackages, int CatalogEntries, int OriginalIcons, int CleanIcons, int MissingOriginalIcons, int MissingCleanIcons);
public sealed class IconStats
{
    public int Original { get; set; }
    public int Clean { get; set; }
    public int MissingOriginal { get; set; }
    public int MissingClean { get; set; }
}
