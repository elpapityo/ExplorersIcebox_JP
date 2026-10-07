﻿using ExplorersIcebox.Enums;
using ExplorersIcebox.Util;

namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_ImmediateSell
{
    internal static bool BuildSellList()
    {
        IslandHelper.SellItems.Clear();

        foreach (var item in ItemData.IslandItems)
        {
            if (ItemData.AlwaysIgnoreSell.Contains(item.Key))
                continue;

            if (!PlayerHelper.GetItemCount(item.Key, out var currentCount) || currentCount <= 0)
                continue;

            var keepAmount = IslandHelper.GetKeepAmount(item.Value.ItemName);
            var surplus = Math.Max(0, currentCount - keepAmount);
            if (surplus > 0)
                IslandHelper.SellItems[item.Key] = surplus;
        }

        return IslandHelper.SellItems.Count > 0;
    }

    internal static void Start()
    {
        if (C.SkipSell || SchedulerMain.State != IceBoxState.Idle || P.taskManager.NumQueuedTasks > 0)
            return;

        if (!BuildSellList())
            return;

        Task_SellItems.Reset();
        Task_ReturnToBase.Enqueue(false);
        Task_UpdateShop.Enqueue();
        Task_SellItems.Enqueue(true);
    }
}
