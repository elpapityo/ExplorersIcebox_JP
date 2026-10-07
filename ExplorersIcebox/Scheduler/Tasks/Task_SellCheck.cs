using ExplorersIcebox.Enums;
using ExplorersIcebox.Util;
namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_SellCheck
{
    internal static bool SellToShop;

    public static void Enqueue()
    {
        P.taskManager.Enqueue(() => SellCheck(), "Checking if need to sell to vendor");
    }

    internal static bool? SellCheck()
    {
        Svc.Log.Information("Starting Sell Check");
        IslandHelper.SellItems.Clear();
        SellToShop = false;
        var LoopCount = Math.Min(IslandHelper.GoalLoopAmount, IslandHelper.MaxRouteLoops);
        if (C.RunMaxLoops)
            LoopCount = IslandHelper.MaxRouteLoops;


        IslandHelper.UpdateNumbers();
        foreach (var item in IslandHelper.RouteItems)
        {
            // IgnoreNode only affects loop count calculations, not sell eligibility.
            // Items like Islewort that appear in multi-item nodes should still be
            // sold when they exceed the keep limit.
            if (ItemData.AlwaysIgnoreSell.Contains(item.Value.ItemId))
                continue;

            var itemName = item.Key;
            var gatherAmount = IslandHelper.RouteItems[itemName].Amount;
            var itemId = item.Value.ItemId;

            var ItemSell = IslandHelper.SellAmount(LoopCount, gatherAmount, itemId, itemName);
            if (ItemSell > 0)
            {
                IslandHelper.SellItems.Add(itemId, ItemSell);
                SellToShop = true;
            }
        }

        if (C.SkipSell || !SellToShop)
        {
            SchedulerMain.State = IceBoxState.RunRoute;
        }
        else if (SellToShop)
        {
            Task_SellItems.Reset();
            Task_UpdateShop.Enqueue();
            SchedulerMain.State = IceBoxState.SellToNpc;
        }
        else
        {
            SchedulerMain.State = IceBoxState.Idle;
        }
        return true;
    }
}
