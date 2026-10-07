using ECommons.Logging;
using ExplorersIcebox.Util.PathCreation;
using System.Collections.Generic;
namespace ExplorersIcebox.Util;

public static class IslandHelper
{
    public static int GoalLoopAmount;
    public static int MaxRouteLoops = 999;
    public static int LoopCounter = 0;
    public static int CurrentRouteLoop = 0;
    public static int CurrentRouteLoopTotal = 0;
    public static KeyValuePair<string, RouteClass.RouteUtil> CurrentRoute;
    public static Dictionary<int, int> SellItems = new();

    public static Vector3 BaseStart = new(-268, 40, 226);

    public static Dictionary<string, ItemGathered> RouteItems = new();
    public static Dictionary<string, HashSet<ItemData.GatheringNode>> ItemNodeMap = new();

    /// <summary>
    ///     Returns the maximum amount of loops that you can do for this route in one set.
    ///     <para>
    ///         Used to check/set that your loop counter isn't more than what if feasable.
    ///     </para>
    /// </summary>
    /// <param name="loopAmountGathered"></param>
    /// <returns> [Int] Max Loop Amount</returns>
    public static int GetKeepAmount(string itemName)
    {
        if (C.ItemKeepAmount != null && C.ItemKeepAmount.TryGetValue(itemName, out var configured))
            return Math.Clamp(configured, 0, 999);

        return Math.Clamp(C.MinimumItemKeep, 0, 999);
    }

    public static int IslandLoopCalc(int loopAmountGathered, string itemName)
    {
        if (loopAmountGathered == 0)
            return 0; // safety to make sure that the amount gathered per loop isn't an invalid number

        var maxAmount = 999;
        var keepAmount = GetKeepAmount(itemName);
        var itemCap = maxAmount - keepAmount;
        return itemCap / loopAmountGathered;
    }

    /// <summary>
    ///     Check for how many loops in general that is needed
    /// </summary>
    /// <param name="amountWanted"></param>
    /// <param name="loopAmountGathered"></param>
    /// <returns> [Int] Minimum Amount of Loops </returns>
    public static int MinimumLoopCalc(int amountWanted, int loopAmountGathered) => (amountWanted + loopAmountGathered - 1) / loopAmountGathered;

    public static int SellAmount(int loopAmount, int amountGathered, int itemId, string itemName)
    {
        var keepAmount = GetKeepAmount(itemName);
        var itemSell = 0;

        if (PlayerHelper.GetItemCount(itemId, out var currentCount))
        {
            var surplus = currentCount - keepAmount;
            if (surplus > 0)
                itemSell = surplus;
        }

        return itemSell;
    }

    public static void UpdateNumbers()
    {
        RouteItems.Clear();
        ItemNodeMap.Clear();

        foreach (var wp in CurrentRoute.Value.RouteWaypoints)
        {
            if (wp.TargetId != 0)
            {
                var Node = ItemData.IslandNodeInfo.Where(x => x.Nodes.Contains(wp.TargetId)).FirstOrDefault();
                if (Node != null)
                {
                    foreach (var item in Node.ItemIds)
                    {
                        var itemName = ItemData.IslandItems[item].ItemName;
                        if (!RouteItems.ContainsKey(itemName))
                        {
                            RouteItems[itemName] = new()
                            {
                                Amount = 1,
                                ItemId = item,
                                GatherNodes = { Node.GatherName },
                                IgnoreNode = false
                            };
                        }
                        else
                        {
                            RouteItems[itemName].Amount += 1;
                            RouteItems[itemName].GatherNodes.Add(Node.GatherName);
                        }

                        if (!ItemNodeMap.ContainsKey(itemName))
                            ItemNodeMap[itemName] = new();

                        ItemNodeMap[itemName].Add(Node);
                    }
                }
            }
        }

        // Used to update each of the values to check and see what can be ignored
        foreach (var kvp in RouteItems)
        {
            var itemName = kvp.Key;
            var gathered = kvp.Value;

            if (!ItemNodeMap.TryGetValue(itemName, out var nodes)) continue;

            if (nodes.Count <= 1)
            {
                gathered.IgnoreNode = false;
            }
            else
            {
                // Ignore only if ANY of the nodes contains other items too
                gathered.IgnoreNode = nodes.Count > 1 && nodes.All(n => n.ItemIds.Count > 1);
            }
        }

        foreach (var kvp in RouteItems)
        {
            var itemName = kvp.Key;
            var gathered = kvp.Value;

            if (gathered.IgnoreNode)
                continue;
            var AmountWanted = C.ItemGatherAmount[itemName];

            GoalLoopAmount = Math.Max(GoalLoopAmount, MinimumLoopCalc(AmountWanted, gathered.Amount)); // 200 Loops
            MaxRouteLoops = Math.Min(MaxRouteLoops, IslandLoopCalc(gathered.Amount, itemName));                  // 65
        }
    }

    public static void UpdateCounters(Dictionary<string, ItemGathered> routeItems)
    {
        GoalLoopAmount = 0;
        MaxRouteLoops = 999;

        foreach (var kvp in routeItems)
        {
            var itemName = kvp.Key;
            var gathered = kvp.Value;

            if (gathered.IgnoreNode)
                continue;
            var AmountWanted = C.ItemGatherAmount[itemName];

            GoalLoopAmount = Math.Max(GoalLoopAmount, MinimumLoopCalc(AmountWanted, gathered.Amount));
            MaxRouteLoops = Math.Min(MaxRouteLoops, IslandLoopCalc(gathered.Amount, itemName));
        }
    }

    public static void UpdateShopCallback()
    {
        PluginLog.Debug("- - - Starting to update callbacks - - - ");
        var callback = 0;
        foreach (var item in ItemData.IslandItems)
        {
            if (AddonHelper.IsNodeVisible("MJIPouch", 1, 8, item.Value.NodeId, 2))
            {
                item.Value.SellSlot = callback;
                callback += 1;
                PluginLog.Debug($"Updated {item.Key} | {ItemData.IslandItems[item.Key].ItemName} callback to {item.Value.SellSlot}");
            }
            else
            {
                item.Value.SellSlot = 0;
                PluginLog.Debug($"No value was found for {item.Key}, setting to 0");
            }
        }
    }

    public class ItemGathered
    {
        public int Amount { get; set; }
        public int ItemId { get; set; }
        public HashSet<string> GatherNodes { get; set; } = new();
        public bool IgnoreNode { get; set; }
    }
}
