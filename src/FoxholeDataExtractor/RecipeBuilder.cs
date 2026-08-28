using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

/// <summary>
/// Builds recipes.json from the actual production/conversion definitions found in
/// structure/facility blueprints. BPItemDynamicData.CostPerCrate is only used as a
/// fallback when no real ConversionEntries recipe was found for an item.
/// </summary>
public sealed class RecipeBuilder
{
    private readonly IReadOnlyDictionary<string, JToken> _packages;

    public RecipeBuilder(IReadOnlyDictionary<string, JToken> packages) => _packages = packages;

    public JObject Build(IEnumerable<JObject> catalog)
    {
        var byOutput = new Dictionary<string, JArray>(StringComparer.Ordinal);

        foreach (var (packagePath, package) in _packages.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            // ConversionEntries are production recipes. Do not restrict this to a hard-coded
            // facility list: new production structures can then be picked up automatically.
            Scan(package, packagePath, modification: null, jsonPath: "$", byOutput);
        }

        // CostPerCrate is not authoritative for facility-produced items. Keep it only for
        // items for which the game data exposes no ConversionEntries recipe at all (e.g.
        // classic Factory/MPF-style production data).
        foreach (var item in catalog)
        {
            var codeName = item["CodeName"]?.ToString();
            if (string.IsNullOrWhiteSpace(codeName) || byOutput.ContainsKey(codeName)) continue;

            if (item["ItemDynamicData"] is not JObject dynamicData ||
                dynamicData["CostPerCrate"] is not JArray costs || costs.Count == 0)
                continue;

            var inputs = new JArray();
            foreach (var cost in costs.OfType<JObject>())
            {
                var inputCode = cost["ItemCodeName"]?.ToString();
                if (string.IsNullOrWhiteSpace(inputCode)) continue;
                inputs.Add(new JObject
                {
                    ["CodeName"] = inputCode,
                    ["Quantity"] = cost["Quantity"]?.DeepClone()
                });
            }
            if (inputs.Count == 0) continue;

            var quantity = dynamicData["QuantityPerCrate"]?.DeepClone() ?? new JValue(1);
            var conversion = new JObject
            {
                ["ItemInput"] = inputs,
                ["CrateInput"] = new JArray(),
                ["LiquidInput"] = new JArray(),
                ["ItemOutput"] = new JArray(new JObject
                {
                    ["CodeName"] = codeName,
                    ["Quantity"] = quantity
                }),
                ["CrateOutput"] = new JArray(),
                ["LiquidOutput"] = new JArray(),
                ["Duration"] = dynamicData["CrateProductionTime"]?.DeepClone()
            };

            Add(byOutput, codeName, new JObject
            {
                ["Source"] = "ItemDynamicDataFallback",
                ["ObjectPath"] = dynamicData["ObjectPath"]?.DeepClone(),
                ["Modification"] = null,
                ["Conversion"] = conversion
            });
        }

        var result = new JObject();
        foreach (var pair in byOutput.OrderBy(x => x.Key, StringComparer.Ordinal))
            result[pair.Key] = pair.Value;

        var multiLocation = byOutput.Count(x => x.Value.Count > 1);
        Console.WriteLine($"Recipes: {byOutput.Count} outputs, {multiLocation} outputs have multiple production recipes/locations.");
        foreach (var pair in byOutput.Where(x => x.Value.Count > 1).OrderByDescending(x => x.Value.Count).Take(20))
            Console.WriteLine($"  MULTI {pair.Key}: {pair.Value.Count} production entries");
        return result;
    }

    private static void Scan(JToken token, string packagePath, string? modification, string jsonPath,
        Dictionary<string, JArray> byOutput)
    {
        if (token is JObject obj)
        {
            // A { Key: EFortModificationType::..., Value: { ConversionEntries: [...] } }
            // changes the production context for everything below Value.
            if (obj["Key"]?.Type == JTokenType.String && obj["Value"] is JToken value)
            {
                var key = obj["Key"]!.ToString();
                if (key.StartsWith("EFortModificationType::", StringComparison.Ordinal))
                {
                    Scan(value, packagePath, key, jsonPath + ".Value", byOutput);
                    return;
                }
            }

            if (obj["ConversionEntries"] is JArray entries)
            {
                foreach (var entry in entries.OfType<JObject>())
                    AddConversion(packagePath, modification, $"{jsonPath}.ConversionEntries[{entries.IndexOf(entry)}]", entry, byOutput);
            }

            foreach (var property in obj.Properties())
            {
                if (property.Name == "ConversionEntries") continue; // already consumed above
                Scan(property.Value, packagePath, modification, jsonPath + "." + property.Name, byOutput);
            }
        }
        else if (token is JArray arr)
        {
            for (var i = 0; i < arr.Count; i++) Scan(arr[i], packagePath, modification, $"{jsonPath}[{i}]", byOutput);
        }
    }

    private static void AddConversion(string packagePath, string? modification, string jsonPath, JObject entry,
        Dictionary<string, JArray> byOutput)
    {
        var outputs = new[]
        {
            (Name: "ItemOutput", Type: "Item"),
            (Name: "CrateOutput", Type: "Crate"),
            (Name: "LiquidOutput", Type: "Liquid")
        };

        foreach (var (name, outputType) in outputs)
        {
            if (entry[name] is not JArray values) continue;
            foreach (var output in values.OfType<JObject>())
            {
                var codeName = output["CodeName"]?.ToString();
                if (string.IsNullOrWhiteSpace(codeName)) continue;

                var facility = Path.GetFileName(packagePath.Replace('\\', '/'));
                Add(byOutput, codeName, new JObject
                {
                    ["Source"] = "ConversionEntries",
                    ["ObjectPath"] = packagePath,
                    ["Facility"] = facility,
                    ["Modification"] = modification is null ? JValue.CreateNull() : new JValue(modification),
                    ["ProductionLocation"] = modification is null ? facility : $"{facility} / {modification}",
                    ["JsonPath"] = jsonPath,
                    ["OutputType"] = outputType,
                    // Preserve the complete game structure: inputs, outputs, limits, duration,
                    // power delta and bConsumeResourceNodes all remain untouched.
                    ["Conversion"] = entry.DeepClone()
                });
            }
        }
    }

    private static void Add(Dictionary<string, JArray> byOutput, string codeName, JObject recipe)
    {
        if (!byOutput.TryGetValue(codeName, out var recipes))
            byOutput[codeName] = recipes = new JArray();

        // Keep EVERY production occurrence. Two entries can have identical inputs/outputs but
        // still represent distinct production slots/locations/modifications in the game data.
        // JsonPath + ObjectPath make each occurrence traceable to its exact source.
        recipes.Add(recipe);
    }
}
