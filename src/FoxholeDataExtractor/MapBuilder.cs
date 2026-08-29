using System.Net.Http;
using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

/// <summary>
/// Builds maps.json. Hex metadata/images come from BPMapList when available.
/// Region-zone sites come from the official static War API: its Major map text markers
/// explicitly form the basis of Region Zones. Voronoi cells are clipped to the Foxhole hex.
/// </summary>
public sealed class MapBuilder
{
    private readonly DefaultFileProvider _provider;
    private readonly IReadOnlyDictionary<string, JToken> _packages;
    private readonly string _output;
    private readonly bool _exportImages;
    private readonly bool _calculateRegions;
    private readonly bool _useWarApiRegions;
    private readonly string _warApiBaseUrl;

    public MapBuilder(DefaultFileProvider provider, IReadOnlyDictionary<string, JToken> packages,
        string output, bool exportImages, bool calculateRegions, bool useWarApiRegions, string warApiBaseUrl)
    {
        _provider = provider; _packages = packages; _output = output;
        _exportImages = exportImages; _calculateRegions = calculateRegions;
        _useWarApiRegions = useWarApiRegions;
        _warApiBaseUrl = warApiBaseUrl.TrimEnd('/');
    }

    public List<JObject> Build()
    {
        var result = BuildHexesFromBpMapList();
        if (result.Count == 0 && _useWarApiRegions)
        {
            Console.WriteLine("WARN maps: BPMapList unavailable; using War API map list fallback (no asset grid/image metadata).");
            result = BuildHexesFromWarApi();
        }

        if (_useWarApiRegions && result.Count > 0)
        {
            Console.WriteLine("Maps: loading official Major markers used as Region Zone sites...");
            PopulateRegionsFromWarApi(result);
        }

        var regionCount = result.Sum(x => x["regions"]?.Count() ?? 0);
        Console.WriteLine($"Maps: {result.Count}; regions discovered: {regionCount}");
        return result.OrderBy(x => x["codeName"]?.ToString(), StringComparer.Ordinal).ToList();
    }

    private List<JObject> BuildHexesFromBpMapList()
    {
        // Do not depend on a hard-coded mount root. Find BPMapList by package leaf name.
        var pair = _packages.FirstOrDefault(p =>
            WithoutExtension(p.Key).EndsWith("BPMapList", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(pair.Key)) return new();

        var database = FindProperty(pair.Value, "MapDatabase") as JArray;
        if (database is null)
        {
            Console.WriteLine("WARN maps: BPMapList found but MapDatabase was not found.");
            return new();
        }

        var result = new List<JObject>();
        foreach (var row in database.OfType<JObject>())
        {
            var key = row["Key"]?.ToString(); var value = row["Value"] as JObject;
            if (string.IsNullOrWhiteSpace(key) || value is null) continue;
            var wcId = StripEnum(value["WorldConquestMapId"]?.ToString()) ?? key;
            var map = new JObject
            {
                ["codeName"] = key,
                ["displayName"] = value.SelectToken("DisplayName.SourceString")?.DeepClone()
                                  ?? value.SelectToken("DisplayName.LocalizedString")?.DeepClone(),
                ["worldConquestMapId"] = wcId,
                ["isInHexGrid"] = value["bIsInHexGrid"]?.DeepClone(),
                ["grid"] = value["GridCoord"]?.DeepClone(),
                ["isIsland"] = value["bIsIsland"]?.DeepClone(),
                ["canOceanTravel"] = value["bCanOceanTravel"]?.DeepClone(),
                ["regions"] = new JArray()
            };

            var imageRef = value.SelectToken("Image.ObjectPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(imageRef))
            {
                map["imageAsset"] = imageRef;
                if (_exportImages)
                {
                    var rel = $"assets/maps/images/{SafeFileName(key)}.png";
                    if (TextureExporter.TryExport(_provider, imageRef, Path.Combine(_output, rel))) map["image"] = rel;
                }
            }
            result.Add(map);
        }
        return result;
    }

    private List<JObject> BuildHexesFromWarApi()
    {
        try
        {
            using var http = NewHttp();
            var json = http.GetStringAsync($"{_warApiBaseUrl}/maps").GetAwaiter().GetResult();
            var names = JArray.Parse(json).Values<string>().Where(x => !string.IsNullOrWhiteSpace(x));
            return names.Select(x => new JObject
            {
                ["codeName"] = x, ["worldConquestMapId"] = x, ["regions"] = new JArray()
            }).ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"WARN maps: War API map list failed: {ex.Message}"); return new(); }
    }

    private void PopulateRegionsFromWarApi(List<JObject> maps)
    {
        using var http = NewHttp();
        foreach (var map in maps)
        {
            var mapName = map["worldConquestMapId"]?.ToString() ?? map["codeName"]?.ToString();
            if (string.IsNullOrWhiteSpace(mapName) || mapName.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var json = http.GetStringAsync($"{_warApiBaseUrl}/maps/{Uri.EscapeDataString(mapName)}/static").GetAwaiter().GetResult();
                var data = JObject.Parse(json);
                var major = (data["mapTextItems"] as JArray)?.OfType<JObject>()
                    .Where(x => string.Equals(x["mapMarkerType"]?.ToString(), "Major", StringComparison.OrdinalIgnoreCase))
                    .Where(x => x["x"] != null && x["y"] != null)
                    .Select(x => new JObject
                    {
                        ["name"] = x["text"]?.DeepClone(),
                        ["x"] = x["x"]?.DeepClone(),
                        ["y"] = x["y"]?.DeepClone(),
                        ["markerType"] = "Major"
                    }).ToList() ?? new List<JObject>();

                if (_calculateRegions && major.Count > 0) BuildVoronoi(major);
                map["regionId"] = data["regionId"]?.DeepClone();
                map["regions"] = new JArray(major);
            }
            catch (Exception ex)
            {
                // Some BPMapList entries are not active World Conquest maps. Keep the hex metadata.
                Console.Error.WriteLine($"WARN maps: static data unavailable for {mapName}: {ex.Message}");
            }
        }
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FoxholeDataExtractor/3.0");
        return http;
    }

    private static void BuildVoronoi(List<JObject> sites)
    {
        foreach (var site in sites)
        {
            var sx = site["x"]!.Value<double>(); var sy = site["y"]!.Value<double>();
            // Foxhole normalized map coordinates are rectangular; the playable region is a
            // flat-top hex. These six normalized vertices clip the unbounded Voronoi cells.
            var polygon = new List<(double X,double Y)>
            { (0.25,0.0),(0.75,0.0),(1.0,0.5),(0.75,1.0),(0.25,1.0),(0.0,0.5) };
            foreach (var other in sites)
            {
                if (ReferenceEquals(site, other)) continue;
                var ox=other["x"]!.Value<double>(); var oy=other["y"]!.Value<double>();
                var a=2*(ox-sx); var b=2*(oy-sy); var c=ox*ox+oy*oy-sx*sx-sy*sy;
                polygon=ClipHalfPlane(polygon,a,b,c); if (polygon.Count==0) break;
            }
            site["polygon"] = new JArray(polygon.Select(p => new JObject { ["x"]=p.X, ["y"]=p.Y }));
        }
    }

    private static List<(double X,double Y)> ClipHalfPlane(List<(double X,double Y)> input,double a,double b,double c)
    {
        var output=new List<(double X,double Y)>(); if(input.Count==0)return output;
        static double F((double X,double Y)p,double a,double b,double c)=>a*p.X+b*p.Y-c;
        for(var i=0;i<input.Count;i++)
        {
            var p=input[i]; var q=input[(i+1)%input.Count]; var fp=F(p,a,b,c); var fq=F(q,a,b,c);
            var pin=fp<=1e-10; var qin=fq<=1e-10; if(pin)output.Add(p); if(pin==qin)continue;
            var t=fp/(fp-fq); output.Add((p.X+(q.X-p.X)*t,p.Y+(q.Y-p.Y)*t));
        }
        return output;
    }

    private static JToken? FindProperty(JToken token,string name)
    {
        if(token is JObject o){if(o.TryGetValue(name,StringComparison.OrdinalIgnoreCase,out var d))return d;foreach(var p in o.Properties()){var r=FindProperty(p.Value,name);if(r!=null)return r;}}
        else if(token is JArray a)foreach(var c in a){var r=FindProperty(c,name);if(r!=null)return r;} return null;
    }
    private static string? StripEnum(string? s)=>s?.Contains("::")==true?s[(s.LastIndexOf("::",StringComparison.Ordinal)+2)..]:s;
    private static string WithoutExtension(string s){s=s.Replace('\\','/').TrimStart('/');foreach(var e in new[]{".uasset",".umap",".uexp",".ubulk"})if(s.EndsWith(e,StringComparison.OrdinalIgnoreCase))return s[..^e.Length];return s;}
    private static string SafeFileName(string s){foreach(var c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_');return s;}
}
