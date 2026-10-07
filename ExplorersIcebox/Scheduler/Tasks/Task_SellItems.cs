using ECommons.Throttlers;
using ExplorersIcebox.Enums;
using ExplorersIcebox.Util;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using Callback = ECommons.Automation.Callback;

namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_SellItems
{
    private const string DisposeShopAddon = "MJIDisposeShop";
    private const string ShippingAddon = "MJIDisposeShopShipping";
    private const string SelectStringAddon = "SelectString";

    private const int SelectItemDelayMs = 2000;
    private const int ShippingDialogSettleMs = 800;
    private const int ConfirmShipmentDelayMs = 1000;
    private const int BetweenItemsDelayMs = 800;
    private const int MinCloseSettleMs = 1500;
    private const int BulkCheckTimeoutMs = 4000;

    // 0 = overcap check, 1 = material deficit check, 2 = shipment checks finished.
    private const byte BulkShipCheckComplete = 2;

    // Item we last confirmed via the shipping callback; cleared when all shipping UI is closed.
    private static int LastShippedItemId;
    private static bool sellPipelineActive;
    private static bool finishToIdleAfterSell;
    private static long lastShipmentCompleteTick;

    internal static void Reset()
    {
        sellPipelineActive = false;
        LastShippedItemId = 0;
        lastShipmentCompleteTick = 0;
        finishToIdleAfterSell = false;
    }

    public static void Enqueue(bool returnToIdleAfterSell = false)
    {
        if (sellPipelineActive)
            return;

        sellPipelineActive = true;
        finishToIdleAfterSell = returnToIdleAfterSell;
        LastShippedItemId = 0;
        lastShipmentCompleteTick = 0;
        var baseDict = EmbedRoutes.BaseRoutes["Base -> Shopkeep"];
        var waypoints = baseDict.Waypoints;
        var dataId = baseDict.TargetId;

        P.taskManager.Enqueue(() => MoveToNpc(waypoints), "Moving to NPC");
        P.taskManager.Enqueue(() => TargetShopkeeper(dataId), $"Target task: {dataId}");
        P.taskManager.Enqueue(() => OpenExportShop(dataId), "Interacting w/ the material seller vendor");
        P.taskManager.Enqueue(() => SellAllItems(), "Selling all the items to the npc");
        P.taskManager.Enqueue(() => WaitForSellAgentIdle(), "Waiting for sell agent to settle");
        P.taskManager.Enqueue(() => LeaveNPC(waypoints), "Leaving the NPC");
    }

    internal static bool? MoveToNpc(List<Vector3> waypoints)
    {
        var lastWp = waypoints.Count - 1;
        if (PlayerHelper.GetDistanceToPlayer(waypoints[lastWp]) < 0.5f)
            return true;

        if (!P.navmesh.IsRunning() && EzThrottler.Throttle("Telling navmesh to move to spot"))
            P.navmesh.MoveTo(new(waypoints), false);

        return false;
    }

    internal static unsafe bool? OpenExportShop(ulong dataId)
    {
        if (AllSellItemsDone())
            return true;

        if (IsDisposeShopReady(AgentMJIDisposeShop.Instance()))
            return true;

        if (AddonHelper.TryGetActiveAddon(SelectStringAddon, out var menu))
        {
            if (EzThrottler.Throttle("Selecting Export Materials"))
                Callback.Fire(menu, true, 0);
        }
        else if (EzThrottler.Throttle($"Interacting w/ shop seller {dataId}"))
        {
            Utils.TryGetObjectByDataId(dataId, out var gameObject);
            Utils.InteractWithObject(gameObject);
        }

        return false;
    }

    internal static bool? TargetShopkeeper(ulong dataId)
    {
        Utils.TryGetObjectByDataId(dataId, out var gameObject);

        if (gameObject == null || gameObject.IsTarget() || !gameObject.IsTargetable)
            return true;

        if (EzThrottler.Throttle($"Targeting: {dataId}"))
            Utils.TargetgameObject(gameObject);

        return false;
    }

    internal static bool? SellAllItems()
    {
        foreach (var (itemId, sellAmount) in IslandHelper.SellItems)
        {
            if (sellAmount == 0)
                continue;

            if (LastShippedItemId != 0 && LastShippedItemId != itemId)
                continue;

            if (TryToSellItem(itemId, sellAmount))
                return false;

            if (LastShippedItemId != 0)
                return false;
        }

        return true;
    }

    internal static unsafe bool? WaitForSellAgentIdle()
    {
        if (!AllSellItemsDone() || LastShippedItemId != 0)
            return false;

        EnsureShipmentCompleteTick();

        var agent = AgentMJIDisposeShop.Instance();
        if (agent == null || agent->Data == null)
        {
            if (!AddonHelper.IsAddonActive(DisposeShopAddon))
            {
                SchedulerMain.State = IceBoxState.LeavingSellNpc;
                return true;
            }

            if (!HasBulkCheckTimeoutElapsed())
                return false;

            SchedulerMain.State = IceBoxState.LeavingSellNpc;
            return true;
        }

        if (!IsSafeToCloseShop(agent->Data))
            return false;

        SchedulerMain.State = IceBoxState.LeavingSellNpc;
        return true;
    }

    private static unsafe bool TryToSellItem(int itemId, int amount)
    {
        var agent = AgentMJIDisposeShop.Instance();
        if (!IsDisposeShopReady(agent))
            return true;

        var data = agent->Data;
        if (!TryFindAgentItem(agent, (uint)itemId, out var agentItem))
        {
            Svc.Log.Warning($"Item {itemId} not found in dispose shop agent data, skipping");
            IslandHelper.SellItems[itemId] = 0;
            return false;
        }

        if (LastShippedItemId == itemId)
        {
            if (IsItemShipmentComplete(data))
            {
                if (!EzThrottler.Throttle($"Between sell items {itemId}", BetweenItemsDelayMs))
                    return true;

                IslandHelper.SellItems[itemId] = 0;
                MarkLastShipmentComplete();
                return false;
            }

            return true;
        }

        if (LastShippedItemId != 0)
            return true;

        if (IsShippingDialogOpen(data))
        {
            if (!EzThrottler.Throttle($"Shipping dialog settle {itemId}", ShippingDialogSettleMs))
                return true;

            if (AddonHelper.TryGetActiveAddon(ShippingAddon, out var mjiShip)
                && EzThrottler.Throttle($"Selling {itemId}", ConfirmShipmentDelayMs))
            {
                data->CurShipItemIndex = agentItem.ItemIndex;
                data->CurShipQuantity = amount;
                Callback.Fire(mjiShip, true, 11, amount);
                LastShippedItemId = itemId;
            }

            return true;
        }

        if (IsShippingUiActive(data))
            return true;

        if (TryGetAddonMaster<MJIDisposeShop>(DisposeShopAddon, out var mjiShop) && mjiShop.IsAddonReady)
        {
            var itemName = agentItem.Name.ToString();
            var entry = mjiShop.ExportItems.FirstOrDefault(x => x.ItemName == itemName);
            if (entry == null)
            {
                Svc.Log.Warning($"Export entry not found for {itemName} ({itemId}), skipping");
                IslandHelper.SellItems[itemId] = 0;
                return false;
            }

            if (EzThrottler.Throttle($"Selecting item to ship {itemId}", SelectItemDelayMs))
                entry.Select();

            return true;
        }

        return true;
    }

    internal static unsafe bool? LeaveNPC(List<Vector3> waypoints)
    {
        if (PlayerHelper.GetDistanceToPlayer(IslandHelper.BaseStart) < 0.5f)
        {
            Svc.Log.Information("Leave NPC will complete after this");
            sellPipelineActive = false;
            SchedulerMain.State = finishToIdleAfterSell ? IceBoxState.Idle : IceBoxState.RunRoute;
            finishToIdleAfterSell = false;
            return true;
        }

        if (AddonHelper.TryGetActiveAddon(DisposeShopAddon, out var mjiShop))
        {
            if (!CanFireCloseShop())
                return false;

            if (EzThrottler.Throttle("Closing shop", 500))
                Callback.Fire(mjiShop, true, 1);

            return false;
        }

        if (!P.navmesh.IsRunning())
        {
            var reverseWp = new List<Vector3>(waypoints);
            reverseWp.Reverse();

            if (EzThrottler.Throttle("Telling navmesh to move to spot"))
                P.navmesh.MoveTo(new(reverseWp), false);
        }

        return false;
    }

    private static bool AllSellItemsDone()
    {
        if (IslandHelper.SellItems.Count == 0)
            return true;

        foreach (var amount in IslandHelper.SellItems.Values)
        {
            if (amount > 0)
                return false;
        }

        return true;
    }

    private static void MarkLastShipmentComplete()
    {
        LastShippedItemId = 0;
        lastShipmentCompleteTick = Environment.TickCount64;
    }

    private static void EnsureShipmentCompleteTick()
    {
        if (lastShipmentCompleteTick == 0)
            lastShipmentCompleteTick = Environment.TickCount64;
    }

    private static bool HasCloseSettleElapsed() =>
        lastShipmentCompleteTick != 0
        && Environment.TickCount64 - lastShipmentCompleteTick >= MinCloseSettleMs;

    private static bool HasBulkCheckTimeoutElapsed() =>
        lastShipmentCompleteTick != 0
        && Environment.TickCount64 - lastShipmentCompleteTick >= BulkCheckTimeoutMs;

    private static unsafe bool CanFireCloseShop()
    {
        EnsureShipmentCompleteTick();

        var agent = AgentMJIDisposeShop.Instance();
        if (agent == null || agent->Data == null)
            return HasBulkCheckTimeoutElapsed();

        return IsSafeToCloseShop(agent->Data);
    }

    private static unsafe bool IsSafeToCloseShop(AgentMJIDisposeShop.AgentData* data)
    {
        if (!IsItemShipmentComplete(data))
            return false;

        if (!HasCloseSettleElapsed())
            return false;

        if (data->CurBulkShipCheckStage == BulkShipCheckComplete)
            return true;

        return HasBulkCheckTimeoutElapsed();
    }

    private static unsafe bool IsItemShipmentComplete(AgentMJIDisposeShop.AgentData* data) =>
        !data->AddonDirty && !IsShippingUiActive(data);

    private static unsafe bool IsShippingDialogOpen(AgentMJIDisposeShop.AgentData* data) =>
        data->SelectCountAddonHandle != 0
        && AddonHelper.IsAddonActive(ShippingAddon);

    private static unsafe bool IsShippingUiActive(AgentMJIDisposeShop.AgentData* data) =>
        data->SelectCountAddonHandle != 0
        || data->ConfirmAddonHandle != 0
        || AddonHelper.IsAddonActive(ShippingAddon);

    private static unsafe bool IsDisposeShopReady(AgentMJIDisposeShop* agent) =>
        agent != null
        && agent->Data != null
        && agent->Data->InitializationState == 3
        && agent->Data->DataInitialized;

    private static unsafe bool TryFindAgentItem(
        AgentMJIDisposeShop* agent,
        uint itemId,
        out AgentMJIDisposeShop.ItemData agentItem)
    {
        for (var i = 0; i < agent->Data->Items.LongCount; i++)
        {
            ref var item = ref agent->Data->Items[i];
            if (item.ItemId == itemId)
            {
                agentItem = item;
                return true;
            }
        }

        agentItem = default;
        return false;
    }
}
