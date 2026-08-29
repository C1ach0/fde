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

        // Vehicle assembly costs live in BPVehicleDynamicData while the production slot,
        // duration, required base vehicle and facility power live in AssemblyItems/facility data.
        // Index the catalog once so both sources can be joined by CodeName.
        var vehicleDynamicByCodeName = catalog
            .Where(x => x["CodeName"]?.Type == JTokenType.String && x["VehicleDynamicData"] is JObject)
            .ToDictionary(x => x["CodeName"]!.ToString(), x => (JObject)x["VehicleDynamicData"]!, StringComparer.Ordinal);

        var structureDynamicByCodeName = catalog
            .Where(x => x["CodeName"]?.Type == JTokenType.String && x["StructureDynamicData"] is JObject)
            .ToDictionary(x => x["CodeName"]!.ToString(), x => (JObject)x["StructureDynamicData"]!, StringComparer.Ordinal);

        foreach (var (packagePath, package) in _packages.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            // ConversionEntries are production recipes. Do not restrict this to a hard-coded
            // facility list: new production structures can then be picked up automatically.
            Scan(package, packagePath, modification: null, jsonPath: "$", byOutput, vehicleDynamicByCodeName, structureDynamicByCodeName, inheritedPowerDelta: null);
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
        Dictionary<string, JArray> byOutput,
        IReadOnlyDictionary<string, JObject> vehicleDynamicByCodeName,
        IReadOnlyDictionary<string, JObject> structureDynamicByCodeName,
        JToken? inheritedPowerDelta)
    {
        if (token is JObject obj)
        {
            // PowerGridInfo is generally defined on the facility while AssemblyItems can live
            // deeper inside a modification. Carry the closest known PowerDelta down the tree.
            var powerDelta = obj["PowerGridInfo"]?["PowerDelta"]?.DeepClone() ?? inheritedPowerDelta;

            if (obj["Key"]?.Type == JTokenType.String && obj["Value"] is JToken value)
            {
                var key = obj["Key"]!.ToString();
                if (key.StartsWith("EFortModificationType::", StringComparison.Ordinal))
                {
                    Scan(value, packagePath, key, jsonPath + ".Value", byOutput,
                        vehicleDynamicByCodeName, structureDynamicByCodeName, powerDelta);
                    return;
                }
            }

            if (obj["ConversionEntries"] is JArray entries)
            {
                foreach (var entry in entries.OfType<JObject>())
                    AddConversion(packagePath, modification, $"{jsonPath}.ConversionEntries[{entries.IndexOf(entry)}]", entry, byOutput);
            }

            if (obj["AssemblyItems"] is JArray assemblyItems)
            {
                for (var i = 0; i < assemblyItems.Count; i++)
                    if (assemblyItems[i] is JObject assemblyItem)
                        AddAssembly(packagePath, modification, $"{jsonPath}.AssemblyItems[{i}]", assemblyItem,
                            powerDelta ?? new JValue(0), byOutput, vehicleDynamicByCodeName, structureDynamicByCodeName);
            }

            foreach (var property in obj.Properties())
            {
                if (property.Name is "ConversionEntries" or "AssemblyItems") continue;
                Scan(property.Value, packagePath, modification, jsonPath + "." + property.Name, byOutput,
                    vehicleDynamicByCodeName, structureDynamicByCodeName, powerDelta);
            }
        }
        else if (token is JArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                Scan(arr[i], packagePath, modification, $"{jsonPath}[{i}]", byOutput,
                    vehicleDynamicByCodeName, structureDynamicByCodeName, inheritedPowerDelta);
        }
    }


    private static void AddAssembly(string packagePath, string? modification, string jsonPath, JObject assemblyItem,
        JToken powerDelta, Dictionary<string, JArray> byOutput,
        IReadOnlyDictionary<string, JObject> vehicleDynamicByCodeName,
        IReadOnlyDictionary<string, JObject> structureDynamicByCodeName)
    {
        var codeName = assemblyItem["CodeName"]?.ToString();
        if (string.IsNullOrWhiteSpace(codeName)) return;

        // AssemblyItems is shared by vehicle and structure assembly. Determine the output type
        // from the dynamic-data table instead of guessing from names/folders.
        JObject? dynamicData = null;
        string outputType;
        string outputArray;
        if (vehicleDynamicByCodeName.TryGetValue(codeName, out var vehicleData))
        {
            dynamicData = vehicleData;
            outputType = "Vehicle";
            outputArray = "VehicleOutput";
        }
        else if (structureDynamicByCodeName.TryGetValue(codeName, out var structureData))
        {
            dynamicData = structureData;
            outputType = "Structure";
            outputArray = "StructureOutput";
        }
        else
        {
            // Do not emit a fake/incomplete recipe if the assembly output cannot be typed and
            // its construction resources cannot be joined to dynamic data.
            return;
        }

        var itemInputs = new JArray();
        if (dynamicData["AltResourceAmounts"] is JObject altResources)
        {
            AddResource(itemInputs, altResources["Resource"] as JObject);
            if (altResources["OtherResources"] is JArray others)
                foreach (var resource in others.OfType<JObject>()) AddResource(itemInputs, resource);
        }

        var vehicleInputs = new JArray();
        var required = assemblyItem["RequiredCodeName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(required) && !required.Equals("None", StringComparison.OrdinalIgnoreCase))
            vehicleInputs.Add(new JObject { ["CodeName"] = required, ["Quantity"] = 1, ["Limit"] = 0 });

        var conversion = new JObject
        {
            ["ItemInput"] = itemInputs,
            ["CrateInput"] = new JArray(),
            ["LiquidInput"] = new JArray(),
            ["VehicleInput"] = vehicleInputs,
            ["ItemOutput"] = new JArray(),
            ["CrateOutput"] = new JArray(),
            ["LiquidOutput"] = new JArray(),
            ["VehicleOutput"] = new JArray(),
            ["StructureOutput"] = new JArray(),
            ["Duration"] = assemblyItem["Duration"]?.DeepClone() ?? new JValue(0),
            ["PowerDelta"] = powerDelta.DeepClone(),
            ["bConsumeResourceNodes"] = false
        };
        ((JArray)conversion[outputArray]!).Add(new JObject
        {
            ["CodeName"] = codeName, ["Quantity"] = 1, ["Limit"] = 0
        });

        var facility = Path.GetFileName(packagePath.Replace('\\', '/'));
        Add(byOutput, codeName, new JObject
        {
            ["Source"] = "AssemblyItems",
            ["ObjectPath"] = packagePath,
            ["Facility"] = facility,
            ["Modification"] = modification is null ? JValue.CreateNull() : new JValue(modification),
            ["ProductionLocation"] = modification is null ? facility : $"{facility} / {modification}",
            ["JsonPath"] = jsonPath,
            ["OutputType"] = outputType,
            ["Conversion"] = conversion
        });
    }


    private static void AddResource(JArray inputs, JObject? resource)
    {
        if (resource is null) return;
        var codeName = resource["CodeName"]?.ToString();
        var quantity = resource["Quantity"]?.Value<double>() ?? 0;
        if (string.IsNullOrWhiteSpace(codeName) || codeName.Equals("None", StringComparison.OrdinalIgnoreCase) || quantity <= 0) return;
        inputs.Add(new JObject
        {
            ["CodeName"] = codeName,
            ["Quantity"] = resource["Quantity"]?.DeepClone() ?? new JValue(0),
            ["Limit"] = 0
        });
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
