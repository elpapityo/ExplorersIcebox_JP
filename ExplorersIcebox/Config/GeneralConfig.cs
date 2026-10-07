using System.Collections.Generic;
using System.IO;
namespace ExplorersIcebox.Config;


public class TravelPointPreset
{
    public string Name { get; set; } = "新しいポイント";
    public Vector3? Destination { get; set; } = null;
    public bool UseMount { get; set; } = true;
    public bool UseIslandReturn { get; set; } = false;
    public List<Vector3> Waypoints { get; set; } = new();

    // Stable IDs for user-created waypoints. The list order is the execution order,
    // while each ID stays with the same waypoint when rows are moved up/down.
    public List<int> WaypointIds { get; set; } = new();
    public int NextWaypointId { get; set; } = 1;
}


public class PastureFeedPreset
{
    public string Name { get; set; } = "登録餌";
    public uint Mode { get; set; } = 0;
    public uint ItemId { get; set; } = 0;
    public int ModeButtonIndex { get; set; } = -1;
    public int ItemButtonIndex { get; set; } = -1;
}

public class GeneralConfig : IYamlConfig
{
    public int CurrentConfigVersion { get; set; } = 0;
    public int ModeSelected { get; set; } = 0;
    public int routeSelected { get; set; } = 0;

    // The minimum amount of items you want to keep in your inventory
    public int MinimumItemKeep { get; set; } = 500;
    // Optional per-item keep amount. Items without an override use MinimumItemKeep.
    public Dictionary<string, int> ItemKeepAmount { get; set; } = new();
    public bool SkipSell { get; set; } = false;
    public bool DryTest { get; set; } = false;
    public bool RunMaxLoops { get; set; } = false;
    public bool RunMultiple { get; set; } = false;

    // Pasture: selected manual-care animal slots. Mammet-managed animals are always excluded.
    public HashSet<int> PastureTargetSlots { get; set; } = new();
    public bool PastureFeedAfterHarvest { get; set; } = true;
    public int PastureFeedThresholdHours { get; set; } = 24;
    public List<PastureFeedPreset> PastureFeedPresets { get; set; } = new();
    public int PastureSelectedFeedPreset { get; set; } = -1;

    // Farm: selected manual-care crop slots. Mammet-managed plots are always excluded.
    public HashSet<int> FarmWaterTargetSlots { get; set; } = new();
    // Raw MJI WaterLevel threshold. Only slots at or below this value are watered.
    public int FarmWaterLevelThreshold { get; set; } = 0;

    // User-defined travel points inside Island Sanctuary.
    // Destination, optional island-return, mount usage and intermediate waypoints are all user configurable.
    public List<TravelPointPreset> TravelPoints { get; set; } = new();

    // Legacy fields retained so existing v1.1.0.17/18 config files can still be read safely.
    public Vector3? FarmPosition { get; set; } = null;
    public Vector3? PasturePosition { get; set; } = null;

    // Collection plan (ReSanctuary-style TODO list).
    // Key = FFXIV item id, Value = desired final inventory count.
    public Dictionary<int, int> CollectionTargets { get; set; } = new();
    public List<int> CollectionOrder { get; set; } = new();
    /// <summary>
    ///     Amount of times you want to run this route
    /// </summary>
    public int RunAmount { get; set; } = 0;

    public Dictionary<string, int> ItemGatherAmount { get; set; } = new()
    {
        { "Palm Leaf", 0 },
        { "Apple", 0 },
        { "Branch", 0 },
        { "Stone", 0 },
        { "Clam", 0 },
        { "Laver", 0 },
        { "Coral", 0 },
        { "Islewort", 0 },
        { "Sand", 0 },
        { "Log", 0 },
        { "Palm Log", 0 },
        { "Vine", 0 },
        { "Sap", 0 },
        { "Copper", 0 },
        { "Limestone", 0 },
        { "Rock Salt", 0 },
        { "Sugarcane", 0 },
        { "Cotton", 0 },
        { "Hemp", 0 },
        { "Clay", 0 },
        { "Tinsand", 0 },
        { "Iron Ore", 0 },
        { "Quartz", 0 },
        { "Leucogranite", 0 },
        { "Islefish", 0 },
        { "Squid", 0 },
        { "Jellyfish", 0 },
        { "Resin", 0 },
        { "Coconut", 0 },
        { "Beehive", 0 },
        { "Wood Opal", 0 },
        { "Multicolored Isleblooms", 0 },
        { "Coal", 0 },
        { "Shale", 0 },
        { "Glimshroom", 0 },
        { "Marble", 0 },
        { "Mythril Ore", 0 },
        { "Effervescent Water", 0 },
        { "Spectrine", 0 },
        { "Durium Sand", 0 },
        { "Yellow Copper Ore", 0 },
        { "Gold Ore", 0 },
        { "Hawk's Eye Sand", 0 },
        { "Crystal Formation", 0 },
        { "Cabbage Seed", 0 },
        { "Pumpkin Seed", 0 },
        { "Parsnip Seed", 0 },
        { "Popoto Seed", 0 }
    };

    // Debug Stuff
    public uint PictoCircleColor { get; set; } = 0;
    public uint PictoLineColor { get; set; } = 0;
    public uint PictoWPColor { get; set; } = 0;
    public uint PictoTextCol { get; set; } = 0;
    public float DotRadius { get; set; } = 0f;
    public float LineWidth { get; set; } = 0f;
    public Vector2 DonutRadius { get; set; } = new(0.7f, 1.4f);
    public Vector2 FanPosition { get; set; } = new(0.0f, 6.283f);
    public float TextFloatPlus { get; set; } = 0.0f;

    // General Save

    public static string ConfigPath => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "ExplorersConfig.yaml");
    public void Save() => YamlConfig.Save(this, ConfigPath);
}
