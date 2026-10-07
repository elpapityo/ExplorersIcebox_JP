using ExplorersIcebox.Enums;
using ExplorersIcebox.Scheduler;
using ExplorersIcebox.Util;
namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_GatherLoop
{
    public static void Enqueue()
    {
        Task_GatherMode.Enqueue();
        foreach (var wpList in IslandHelper.CurrentRoute.Value.BaseToLocation)
        {
            Task_BaseToGather.Enqueue(wpList.Waypoints, wpList.Mount, wpList.Fly);
        }

        var totalLoops = IslandHelper.GoalLoopAmount;
        if (C.RunMaxLoops)
            totalLoops = IslandHelper.MaxRouteLoops;

        IslandHelper.CurrentRouteLoop = 0;
        IslandHelper.CurrentRouteLoopTotal = totalLoops;

        // Collection plans are queued one gathering node at a time. This lets us
        // re-check the requested final inventory count immediately after each node
        // instead of pre-queuing every remaining loop.
        if (CollectionPlan.IsRunning)
        {
            EnqueueCollectionNode(1, 0, totalLoops);
            return;
        }

        for (var i = 0; i < totalLoops; i++)
        {
            var loopNumber = i + 1;
            P.taskManager.Enqueue(() =>
            {
                IslandHelper.CurrentRouteLoop = loopNumber;
                return true;
            }, $"Route loop {loopNumber}/{totalLoops}");

            foreach (var entry in IslandHelper.CurrentRoute.Value.RouteWaypoints)
            {
                Task_IslandInteract.Enqueue(entry.Waypoints, entry.TargetId, entry.Mount, entry.Fly);
            }
        }
        P.taskManager.Enqueue(() => CheckLoopCount(), "Checking loop count");
    }

    private static void EnqueueCollectionNode(int loopNumber, int nodeIndex, int totalLoops)
    {
        if (loopNumber > totalLoops)
        {
            P.taskManager.Enqueue(() => CheckLoopCount(), "Collection target/loop check");
            return;
        }

        if (nodeIndex == 0)
        {
            P.taskManager.Enqueue(() =>
            {
                IslandHelper.CurrentRouteLoop = loopNumber;
                return true;
            }, $"Collection route loop {loopNumber}/{totalLoops}");
        }

        var entries = IslandHelper.CurrentRoute.Value.RouteWaypoints;
        if (nodeIndex >= entries.Count)
        {
            P.taskManager.Enqueue(() => CollectionNodeFinished(loopNumber, nodeIndex, totalLoops), "Collection loop decision");
            return;
        }

        var entry = entries[nodeIndex];
        Task_IslandInteract.Enqueue(entry.Waypoints, entry.TargetId, entry.Mount, entry.Fly);
        P.taskManager.Enqueue(() => CollectionNodeFinished(loopNumber, nodeIndex, totalLoops), "Collection target check");
    }

    private static bool? CollectionNodeFinished(int loopNumber, int nodeIndex, int totalLoops)
    {
        if (CollectionPlan.IsCurrentTargetReached())
            return CheckLoopCount();

        var nextNode = nodeIndex + 1;
        if (nextNode < IslandHelper.CurrentRoute.Value.RouteWaypoints.Count)
            EnqueueCollectionNode(loopNumber, nextNode, totalLoops);
        else if (loopNumber < totalLoops)
            EnqueueCollectionNode(loopNumber + 1, 0, totalLoops);
        else
            return CheckLoopCount();

        return true;
    }

    internal static bool? CheckLoopCount()
    {
        IslandHelper.LoopCounter += 1;

        if (CollectionPlan.IsRunning)
        {
            CollectionPlan.AdvanceAfterRoute();
            return true;
        }

        if (C.RunMultiple && IslandHelper.LoopCounter < C.RunAmount)
            SchedulerMain.State = IceBoxState.Start;
        else
            SchedulerMain.State = IceBoxState.EndProcess;

        return true;
    }
}
