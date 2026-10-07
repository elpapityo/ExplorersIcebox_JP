using ExplorersIcebox.Util;
using ExplorersIcebox.Util.PathCreation;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ExplorersIcebox.Scheduler;

/// <summary>
/// ReSanctuary-style collection TODO list runner.
/// The configured amount is the desired FINAL inventory count.
/// Each material is gathered with the existing Icebox route workflow, so the
/// normal "return to base -> route" behavior remains unchanged between items.
/// </summary>
internal static class CollectionPlan
{
    internal static bool IsRunning { get; private set; }
    internal static int? CurrentItemId { get; private set; }

    private static bool savedSkipSell;
    private static bool savedRunMaxLoops;
    private static bool savedRunMultiple;

    private static List<string> RouteNames => EmbedRoutes.Routes.Keys.OrderBy(ExtractNumber).ToList();

    private static int ExtractNumber(string input)
    {
        var match = Regex.Match(input, @"\d+");
        return match.Success ? int.Parse(match.Value) : int.MinValue;
    }

    internal static bool HasWork()
    {
        NormalizeOrder();
        foreach (var itemId in C.CollectionOrder)
        {
            if (!C.CollectionTargets.TryGetValue(itemId, out var target) || target <= 0)
                continue;
            if (PlayerHelper.GetItemCount(itemId, out var current) && current < target && FindBestRoute(itemId) != null)
                return true;
        }
        return false;
    }

    internal static bool Start()
    {
        if (PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed)
            return false;
        if (IsRunning || !HasWork())
            return false;

        savedSkipSell = C.SkipSell;
        savedRunMaxLoops = C.RunMaxLoops;
        savedRunMultiple = C.RunMultiple;

        // A collection target is a final inventory amount. Selling during the
        // plan could undo an already completed target, so selling is suspended
        // only while this plan is running and restored afterwards.
        C.SkipSell = true;
        C.RunMaxLoops = false;
        C.RunMultiple = false;

        IsRunning = true;
        CurrentItemId = null;

        if (!PrepareNextItem())
        {
            Finish(false);
            return false;
        }

        SchedulerMain.QueueStartSequence();
        return true;
    }

    internal static void AdvanceAfterRoute()
    {
        if (!IsRunning)
            return;

        if (PrepareNextItem())
        {
            SchedulerMain.QueueStartSequence();
            return;
        }

        Finish(true);
    }

    internal static void CancelAndRestore()
    {
        if (!IsRunning)
            return;
        RestoreTemporarySettings();
        IsRunning = false;
        CurrentItemId = null;
    }

    private static void Finish(bool logComplete)
    {
        RestoreTemporarySettings();
        IsRunning = false;
        CurrentItemId = null;
        if (logComplete)
            Svc.Log.Information("Collection plan completed");
        SchedulerMain.State = global::ExplorersIcebox.Enums.IceBoxState.EndProcess;
    }

    private static void RestoreTemporarySettings()
    {
        C.SkipSell = savedSkipSell;
        C.RunMaxLoops = savedRunMaxLoops;
        C.RunMultiple = savedRunMultiple;
    }

    private static bool PrepareNextItem()
    {
        NormalizeOrder();

        foreach (var itemId in C.CollectionOrder)
        {
            if (!C.CollectionTargets.TryGetValue(itemId, out var target) || target <= 0)
                continue;
            if (!PlayerHelper.GetItemCount(itemId, out var current) || current >= target)
                continue;

            var routeInfo = FindBestRoute(itemId);
            if (routeInfo == null)
            {
                Svc.Log.Warning($"No gathering route found for collection-plan item {itemId}");
                continue;
            }

            if (!ItemData.IslandItems.TryGetValue(itemId, out var itemInfo))
                continue;

            foreach (var key in C.ItemGatherAmount.Keys.ToList())
                C.ItemGatherAmount[key] = 0;

            var remaining = Math.Clamp(target - current, 1, 999);
            C.ItemGatherAmount[itemInfo.ItemName] = remaining;
            C.ModeSelected = 2;
            C.routeSelected = routeInfo.Value.Index;
            IslandHelper.CurrentRoute = routeInfo.Value.Route;
            CurrentItemId = itemId;

            Svc.Log.Information($"Collection plan: {itemInfo.ItemName} {current}/{target}, route={routeInfo.Value.Route.Key}, remaining={remaining}");
            return true;
        }

        return false;
    }

    internal static (int Index, KeyValuePair<string, RouteClass.RouteUtil> Route, int Yield)? FindBestRoute(int itemId)
    {
        var names = RouteNames;
        (int Index, KeyValuePair<string, RouteClass.RouteUtil> Route, int Yield)? best = null;

        for (var i = 0; i < names.Count; i++)
        {
            var key = names[i];
            if (!EmbedRoutes.Routes.TryGetValue(key, out var route))
                continue;

            var routePair = new KeyValuePair<string, RouteClass.RouteUtil>(key, route);

            var yield = 0;
            var matchingNodes = new HashSet<ItemData.GatheringNode>();
            foreach (var wp in route.RouteWaypoints)
            {
                if (wp.TargetId == 0)
                    continue;
                var node = ItemData.IslandNodeInfo.FirstOrDefault(x => x.Nodes.Contains(wp.TargetId));
                if (node != null && node.ItemIds.Contains(itemId))
                {
                    yield++;
                    matchingNodes.Add(node);
                }
            }

            // Match the existing route counter behavior. If Icebox marks this
            // material as an ignored multi-item-node result on this route, it
            // cannot safely calculate a target loop count for the collection plan.
            var ignoredByRouteCounter = matchingNodes.Count > 1 && matchingNodes.All(n => n.ItemIds.Count > 1);
            if (yield <= 0 || ignoredByRouteCounter)
                continue;

            if (best == null || yield > best.Value.Yield)
                best = (i, new KeyValuePair<string, RouteClass.RouteUtil>(key, route), yield);
        }

        return best;
    }


    internal static bool IsCurrentTargetReached()
    {
        if (!IsRunning || CurrentItemId is not int itemId)
            return false;
        if (!C.CollectionTargets.TryGetValue(itemId, out var target) || target <= 0)
            return false;
        return PlayerHelper.GetItemCount(itemId, out var current) && current >= target;
    }

    internal static void MoveOrder(int itemId, int direction)
    {
        if (IsRunning || direction == 0)
            return;

        NormalizeOrder();
        var index = C.CollectionOrder.IndexOf(itemId);
        if (index < 0)
            return;

        var next = index + Math.Sign(direction);
        if (next < 0 || next >= C.CollectionOrder.Count)
            return;

        (C.CollectionOrder[index], C.CollectionOrder[next]) = (C.CollectionOrder[next], C.CollectionOrder[index]);
        C.Save();
    }

    internal static void AddOrUpdate(int itemId, int target)
    {
        target = Math.Clamp(target, 1, 999);
        C.CollectionTargets[itemId] = target;
        if (!C.CollectionOrder.Contains(itemId))
            C.CollectionOrder.Add(itemId);
        C.Save();
    }

    internal static void Remove(int itemId)
    {
        C.CollectionTargets.Remove(itemId);
        C.CollectionOrder.RemoveAll(x => x == itemId);
        C.Save();
    }

    private static void NormalizeOrder()
    {
        C.CollectionOrder.RemoveAll(id => !C.CollectionTargets.ContainsKey(id));
        foreach (var id in C.CollectionTargets.Keys)
            if (!C.CollectionOrder.Contains(id))
                C.CollectionOrder.Add(id);
    }
}
