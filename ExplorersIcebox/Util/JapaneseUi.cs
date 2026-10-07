using System.Collections.Generic;

namespace ExplorersIcebox.Util;

/// <summary>
/// 日本語表示専用ヘルパー。
/// 内部で使用している英語のルート名・素材名は変更せず、画面表示だけを日本語化する。
/// 素材名は可能な限りFFXIVクライアントのExcelデータから取得する。
/// </summary>
public static class JapaneseUi
{
    public static string ItemName(int itemId, string fallback)
        => OnPluginLoad.IslandItemInfo.TryGetValue(itemId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : fallback;

    public static string RouteName(string source)
    {
        var result = source;

        // 長い語から先に置換し、内部キーそのものは変更しない。
        foreach (var (english, itemId) in RouteItemAliases)
        {
            if (ItemData.IslandItems.TryGetValue(itemId, out var info))
                result = result.Replace(english, ItemName(itemId, info.ItemName), StringComparison.OrdinalIgnoreCase);
        }

        result = result
            .Replace("Mountain XP Loop", "山岳EXP周回", StringComparison.OrdinalIgnoreCase)
            .Replace("Ground XP Route", "地上EXP周回", StringComparison.OrdinalIgnoreCase)
            .Replace("XP Loop", "EXP周回", StringComparison.OrdinalIgnoreCase)
            .Replace("XP Route", "EXP周回", StringComparison.OrdinalIgnoreCase)
            .Replace("Seeds", "種", StringComparison.OrdinalIgnoreCase)
            .Replace("Seed", "種", StringComparison.OrdinalIgnoreCase);

        return result;
    }

    private static readonly KeyValuePair<string, int>[] RouteItemAliases =
    [
        new("Effervescent Water", ItemData.EffervescentWater_ID),
        new("Multicolored Isleblooms", ItemData.MulticoloredIsleblooms_ID),
        new("Yellow Copper Ore", ItemData.YellowCopperOre_ID),
        new("Crystal Formation", ItemData.CrystalFormation_ID),
        new("Hawk Sand", ItemData.HawksEyeSand_ID),
        new("Hawk's Eye Sand", ItemData.HawksEyeSand_ID),
        new("Leucogranite", ItemData.Leucogranite_ID),
        new("Glimshroom", ItemData.Glimshroom_ID),
        new("Rocksalt", ItemData.RockSalt_ID),
        new("Rock Salt", ItemData.RockSalt_ID),
        new("Iron Ore", ItemData.IronOre_ID),
        new("Jellyfish", ItemData.Jellyfish_ID),
        new("Palm Log", ItemData.PalmLog_ID),
        new("Palm Leaf", ItemData.PalmLeaf_ID),
        new("Limestone", ItemData.Limestone_ID),
        new("Sugarcane", ItemData.Sugarcane_ID),
        new("Cabbage", ItemData.CabbageSeed_Id),
        new("Pumpkin", ItemData.PumpkinSeed_Id),
        new("Parsnip", ItemData.ParsnipSeed_Id),
        new("Popoto", ItemData.PopotoSeed_Id),
        new("Coconut", ItemData.Coconut_ID),
        new("Mythril", ItemData.MythrilOre_ID),
        new("Copper", ItemData.CopperOre_ID),
        new("Marble", ItemData.Marble_ID),
        new("Quartz", ItemData.Quartz_ID),
        new("Cotton", ItemData.Cotton_ID),
        new("Tinsand", ItemData.Tinsand_ID),
        new("IsleFish", ItemData.Islefish_ID),
        new("Islefish", ItemData.Islefish_ID),
        new("Clam Shell", ItemData.Clam_ID),
        new("Clam", ItemData.Clam_ID),
        new("Laver", ItemData.Laver_ID),
        new("Larve", ItemData.Laver_ID),
        new("Squid", ItemData.Squid_ID),
        new("Coral", ItemData.Coral_ID),
        new("Islewort", ItemData.Islewort_ID),
        new("Branch", ItemData.Branch_ID),
        new("Resin", ItemData.Resin_ID),
        new("Hemp", ItemData.Hemp_ID),
        new("Apple", ItemData.Apple_ID),
        new("Beehive", ItemData.Beehive_ID),
        new("Opal", ItemData.WoodOpal_ID),
        new("Sap", ItemData.Sap_ID),
        new("Log", ItemData.Log_ID),
        new("Clay", ItemData.Clay_ID),
        new("Sand", ItemData.Sand_ID),
        new("Coal", ItemData.Coal_ID),
        new("Shale", ItemData.Shale_ID),
        new("Vine", ItemData.Vine_ID),
        new("Gold Ore", ItemData.GoldOre_ID),
        new("Crystal", ItemData.CrystalFormation_ID),
    ];
}
