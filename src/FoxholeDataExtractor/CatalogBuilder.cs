using Newtonsoft.Json.Linq;

namespace FoxholeDataExtractor;

/// <summary>
/// FIR-style catalog builder. CUE4Parse exports are intentionally kept in the same
/// array/object shape used by FModel, then this class resolves Blueprint SuperStruct
/// inheritance before coalescing the public Foxhole properties.
/// </summary>
public sealed class CatalogBuilder
{
    private const string BlueprintType = "BlueprintGeneratedClass";
    private readonly IReadOnlyDictionary<string, JToken> _packages;

    private static readonly string[][] CoreProperties =
    {
        P("CodeName"), P("ChassisName", "SourceString"), P("DisplayName", "SourceString"),
        P("Description", "SourceString"), P("Encumbrance"), P("EquipmentSlot"), P("ItemCategory"),
        P("ItemProfileType"), P("ProfileType"), P("FactionVariant"), P("TechID"), P("ItemFlagsMask"),
        P("Icon", "ObjectPath"), P("SubTypeIcon", "ResourceObject", "ObjectPath"), P("ItemComponentClass"),
        P("VehicleProfileType"), P("VehicleMovementProfileType"), P("ArmourType"), P("ShippableInfo"),
        P("FuelTank", "FuelCapacity"), P("DepthCuttoffForSwimDamage"), P("BuildLocationType"), P("MaxHealth"),
        P("VehiclesPerCrateBonusQuantity"), P("VehicleBuildType"), P("MapIconType"), P("BuildLocationFilter"),
        P("BoostSpeedModifier"), P("BoostGasUsageModifier"), P("bCanUseStructures"), P("bIsLarge"),
        P("bRequiresCoverOrLowStanceToInvoke"), P("bRequiresVehicleToBuild"), P("bSupportsVehicleMounts"),
        P("bIsStockpilable")
    };

    private static readonly string[][] ItemDynamicProperties =
    {
        P("CostPerCrate"), P("QuantityPerCrate"), P("CrateProductionTime"),
        P("SingleRetrieveTime"), P("CrateRetrieveTime")
    };

    private static readonly string[][] AmmoProperties =
    {
        P("Damage"), P("Suppression"), P("ExplosionRadius"), P("DamageType", "ObjectPath"),
        P("DamageInnerRadius"), P("DamageFalloff"), P("AccuracyRadius"), P("EnvironmentImpactAmount")
    };

    private static readonly string[][] WeaponProperties =
    {
        P("SuppressionMultiplier"), P("MaxAmmo"), P("MaxApexHalfAngle"), P("BaselineApexHalfAngle"),
        P("StabilityCostPerShot"), P("Agility"), P("CoverProvided"), P("StabilityFloorFromMovement"),
        P("StabilityGainRate"), P("MaximumRange"), P("MaximumReachability"), P("DamageMultiplier"),
        P("ArtilleryAccuracyMinDist"), P("ArtilleryAccuracyMaxDist"), P("MaxVehicleDeviationAngle")
    };

    private static readonly string[][] VehicleDynamicProperties =
    {
        P("ResourceRequirements"), P("MaxHealth"), P("MinorDamagePercent"), P("MajorDamagePercent"),
        P("RepairCost"), P("ResourcesPerBuildCycle"), P("ItemHolderCapacity"), P("FuelCapacity"),
        P("FuelConsumptionPerSecond"), P("SwimmingFuelConsumptionModifier"), P("DefaultSurfaceMovementRate"),
        P("OffroadSnowPenalty"), P("ReverseSpeedModifier"), P("RotationRate"), P("RotationSpeedCuttoff"),
        P("SpeedSqrThreshold"), P("EngineForce"), P("MassOverride"), P("TankArmour"),
        P("MinTankArmourPercent"), P("TankArmourMinPenetrationChance"), P("VehicleSubsystemDisableChances"),
        P("bHasTierUpgrades")
    };

    private static readonly string[][] StructureDynamicProperties =
    {
        P("MaxHealth"), P("ResourceRequirements"), P("DecayStartHours"), P("DecayDurationHours"),
        P("RepairCost"), P("StructuralIntegrity"), P("StoredItemCapacity"), P("RamDamageReceivedFlags"),
        P("bCanBeHarvested"), P("IsVaultable"), P("bIsDamagedWhileDrivingOver")
    };

    public CatalogBuilder(IReadOnlyDictionary<string, JToken> packages) => _packages = packages;

    public List<JObject> Build()
    {
        var result = new List<JObject>();
        foreach (var (path, token) in _packages.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsFirCatalogSearchPath(path)) continue;
            var bp = FindBlueprint(token);
            if (bp is null) continue;

            var item = Coalesce(path, bp);
            if (item["CodeName"] is null) continue;
            result.Add(item);
        }

        return result
            .GroupBy(x => x["CodeName"]!.ToString(), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x["CodeName"]!.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private JObject Coalesce(string packagePath, JObject blueprint)
    {
        var result = new JObject { ["ObjectPath"] = WithoutExtension(packagePath) };
        ExtractInherited(packagePath, blueprint, new[] { "Properties" }, CoreProperties, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var codeName = result["CodeName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(codeName))
        {
            BundleDataTable("War/Content/Blueprints/Data/BPAmmoDynamicData", codeName, AmmoProperties, "AmmoDynamicData", result);
            BundleDataTable("War/Content/Blueprints/Data/BPWeaponDynamicData", codeName, WeaponProperties, "WeaponDynamicData", result);
            BundleDataTable("War/Content/Blueprints/Data/BPItemDynamicData", codeName, ItemDynamicProperties, "ItemDynamicData", result);
            BundleDataTable("War/Content/Blueprints/Data/BPVehicleDynamicData", codeName, VehicleDynamicProperties, "VehicleDynamicData", result);
            BundleDataTable("War/Content/Blueprints/Data/BPStructureDynamicData", codeName, StructureDynamicProperties, "StructureDynamicData", result);
        }

        return result;
    }

    private void BundleDataTable(string packagePath, string row, string[][] properties, string outputName, JObject result)
    {
        if (!TryGetPackage(packagePath, out var token)) return;
        var selected = FindByType(token, "DataTable") ?? FirstObject(token);
        if (selected is null) return;

        var values = ExtractFrom(selected, new[] { "Rows", row }, properties);
        if (!values.HasValues) return;
        values["ObjectPath"] = packagePath;
        result[outputName] = values;
    }

    private void ExtractInherited(string packagePath, JObject blueprint, string[] basePath, string[][] wanted,
        JObject result, HashSet<string> visited)
    {
        var key = WithoutExtension(packagePath);
        if (!visited.Add(key)) return;

        var selectedType = blueprint["Name"]?.ToString();
        var package = _packages.FirstOrDefault(x => PathEquals(x.Key, packagePath)).Value;
        var selected = selectedType is null ? blueprint : FindByType(package, selectedType) ?? blueprint;
        var local = ExtractFrom(selected, basePath, wanted);

        var missing = new List<string[]>();
        foreach (var path in wanted)
        {
            var name = path[0];
            if (local.TryGetValue(name, out var value)) result[name] = value.DeepClone();
            else missing.Add(path);
        }

        if (missing.Count == 0) return;
        var super = blueprint["SuperStruct"] as JObject;
        var superObjectName = super?["ObjectName"]?.ToString();
        var superObjectPath = super?["ObjectPath"]?.ToString();
        if (string.IsNullOrWhiteSpace(superObjectName) || string.IsNullOrWhiteSpace(superObjectPath) ||
            !superObjectName.StartsWith(BlueprintType + "'", StringComparison.Ordinal)) return;

        var superPackagePath = NormalizeReferenceToPackage(superObjectPath);
        if (!TryGetPackage(superPackagePath, out var superToken)) return;
        var superBp = FindBlueprint(superToken);
        if (superBp is null) return;
        ExtractInherited(superPackagePath, superBp, basePath, missing.ToArray(), result, visited);
    }

    private static JObject ExtractFrom(JObject selected, string[] basePath, string[][] properties)
    {
        JToken? baseToken = selected;
        foreach (var element in basePath)
        {
            if (baseToken is JArray arr)
                baseToken = arr.OfType<JObject>().FirstOrDefault(x => x[element] != null)?[element];
            else
                baseToken = baseToken?[element];
        }

        var result = new JObject();
        foreach (var propertyPath in properties)
        {
            JToken? value = baseToken;
            foreach (var element in propertyPath) value = value?[element];
            if (value is not null) result[propertyPath[0]] = value.DeepClone();
        }
        return result;
    }

    private bool TryGetPackage(string rawPath, out JToken token)
    {
        var wanted = WithoutExtension(NormalizeReferenceToPackage(rawPath));
        foreach (var pair in _packages)
        {
            if (string.Equals(WithoutExtension(pair.Key), wanted, StringComparison.OrdinalIgnoreCase))
            {
                token = pair.Value;
                return true;
            }
        }
        token = JValue.CreateNull();
        return false;
    }

    private static JObject? FindBlueprint(JToken token) => FindByType(token, BlueprintType);

    private static JObject? FindByType(JToken? token, string type)
        => token is JArray arr
            ? arr.OfType<JObject>().FirstOrDefault(x => string.Equals(x["Type"]?.ToString(), type, StringComparison.Ordinal))
            : null;

    private static JObject? FirstObject(JToken? token) => token is JArray arr ? arr.OfType<JObject>().FirstOrDefault() : token as JObject;

    private static bool IsFirCatalogSearchPath(string raw)
    {
        var p = WithoutExtension(raw).Replace('\\', '/');
        return p.StartsWith("War/Content/Blueprints/ItemPickups/", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith("War/Content/Blueprints/Vehicles/", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith("War/Content/Blueprints/Structures/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeReferenceToPackage(string value)
    {
        var s = value.Trim().Trim('"', '\'').Replace('\\', '/');
        var quote = s.IndexOf('\'');
        if (quote >= 0 && s.EndsWith("'")) s = s[(quote + 1)..^1];
        var dot = s.LastIndexOf('.');
        if (dot > s.LastIndexOf('/')) s = s[..dot];
        s = s.TrimStart('/');
        if (s.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) s = "War/Content/" + s[5..];
        return s;
    }

    private static string WithoutExtension(string value)
    {
        var s = value.Replace('\\', '/');
        if (s.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            s = s[..s.LastIndexOf('.')];
        return s;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(WithoutExtension(a), WithoutExtension(NormalizeReferenceToPackage(b)), StringComparison.OrdinalIgnoreCase);

    private static string[] P(params string[] p) => p;
}
