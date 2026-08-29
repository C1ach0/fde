using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

public sealed class ResourceExporter
{
    private readonly DefaultFileProvider _provider;
    private readonly string _output;
    private readonly AssetsConfig _config;
    public ResourceExporter(DefaultFileProvider provider, string output, AssetsConfig config)
    { _provider = provider; _output = output; _config = config; }

    public JObject ExportCatalogTextures(IEnumerable<JObject> catalog)
    {
        var manifest = new JArray();
        if (!_config.Icons) return new JObject { ["resources"] = manifest };

        foreach (var item in catalog)
        {
            var code = item["CodeName"]?.ToString();
            var icon = item["Icon"]?.ToString();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(icon)) continue;
            var category = Category(item["ObjectPath"]?.ToString());
            var rel = $"assets/icons/{category}/{Safe(code)}.png";
            var ok = TextureExporter.TryExport(_provider, icon, Path.Combine(_output, rel));
            if (ok) item["iconFile"] = rel;
            manifest.Add(new JObject { ["type"]="icon", ["category"]=category, ["codeName"]=code, ["source"]=icon, ["file"]=ok ? rel : null });
        }
        return new JObject { ["resources"] = manifest };
    }


    public int ExportReferencedTextures(IEnumerable<JToken> packageTokens)
    {
        if (!_config.Icons) return 0;
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in packageTokens) CollectTextureRefs(token, refs);

        var exported = 0;
        foreach (var reference in refs)
        {
            var leaf = reference.Replace('\\','/').Split('/').LastOrDefault() ?? "texture";
            var dot = leaf.IndexOf('.'); if (dot > 0) leaf = leaf[..dot];
            var rel = $"assets/textures/{Safe(leaf)}.png";
            if (TextureExporter.TryExport(_provider, reference, Path.Combine(_output, rel))) exported++;
        }
        Console.WriteLine($"Referenced textures exported: {exported}/{refs.Count}");
        return exported;
    }

    private static void CollectTextureRefs(JToken token, HashSet<string> refs)
    {
        if (token.Type == JTokenType.String)
        {
            var value = token.ToString();
            if ((value.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("/Textures/", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("Icon", StringComparison.OrdinalIgnoreCase)) &&
                (value.Contains("/", StringComparison.Ordinal) || value.Contains("'", StringComparison.Ordinal)))
            {
                // CUE4Parse commonly serializes references as Texture2D'/Game/.../Foo.Foo'.
                var q1 = value.IndexOf('\''); var q2 = value.LastIndexOf('\'');
                if (q1 >= 0 && q2 > q1) value = value[(q1 + 1)..q2];
                refs.Add(value);
            }
            return;
        }
        if (token is JObject o) foreach (var p in o.Properties()) CollectTextureRefs(p.Value, refs);
        else if (token is JArray a) foreach (var c in a) CollectTextureRefs(c, refs);
    }

    public JObject BuildAssetIndex()
    {
        var rows = new JArray();
        foreach (var f in _provider.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var p = f.Path.Replace('\\','/');
            string? kind = null;
            if (_config.Audio && (p.Contains("/Audio/",StringComparison.OrdinalIgnoreCase) || p.Contains("Sound",StringComparison.OrdinalIgnoreCase))) kind="audio";
            else if (_config.Maps && p.EndsWith(".umap",StringComparison.OrdinalIgnoreCase)) kind="map-level";
            else if (_config.Meshes && (p.Contains("/Meshes/",StringComparison.OrdinalIgnoreCase) || p.Contains("Mesh",StringComparison.OrdinalIgnoreCase))) kind="mesh";
            else if (_config.Icons && (p.Contains("/Textures/",StringComparison.OrdinalIgnoreCase) || p.Contains("Icon",StringComparison.OrdinalIgnoreCase))) kind="texture";
            if (kind is null) continue;
            rows.Add(new JObject { ["type"]=kind, ["objectPath"]=p, ["uePackage"]=f.IsUePackage });
        }
        return new JObject { ["assets"] = rows };
    }

    private static string Category(string? path)
    {
        path ??= "";
        if (path.Contains("/Structures/",StringComparison.OrdinalIgnoreCase)) return "structures";
        if (path.Contains("/Vehicles/",StringComparison.OrdinalIgnoreCase)) return "vehicles";
        return "items";
    }
    private static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s=s.Replace(c,'_'); return s; }
}
