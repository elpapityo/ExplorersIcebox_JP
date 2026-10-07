using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ExplorersIcebox.Config;
using ExplorersIcebox.Util;

namespace ExplorersIcebox.Scheduler.Tasks;

/// <summary>
/// Executes a user-defined Island Sanctuary travel point.
/// Optional island return is performed first, followed by optional mounting,
/// then each registered waypoint and finally the destination.
/// </summary>
internal static class Task_QuickTravel
{
    private static TravelPointPreset? activePreset;
    private static long mountLostAt;

    internal static bool IsRunning => activePreset != null &&
        (P.taskManager.NumQueuedTasks > 0 || P.navmesh.IsRunning() || P.navmesh.PathfindInProgress());

    internal static bool IsRunningFor(TravelPointPreset preset)
        => IsRunning && ReferenceEquals(activePreset, preset);

    internal static void Stop()
    {
        if (activePreset == null)
            return;

        Svc.Log.Information($"Travel point {activePreset.Name}: stopped by user");
        P.taskManager.Abort();
        P.navmesh.Stop();
        activePreset = null;
        mountLostAt = 0;
    }

    internal static void Enqueue(TravelPointPreset preset)
    {
        if (!Player.Available || preset.Destination == null)
            return;

        P.navmesh.Stop();
        activePreset = preset;
        mountLostAt = 0;
        var name = string.IsNullOrWhiteSpace(preset.Name) ? "指定ポイント" : preset.Name.Trim();

        if (preset.UseIslandReturn)
        {
            Svc.Log.Information($"Travel point {name}: using Isle Return first");
            Task_ReturnToBase.Enqueue(false);
        }

        if (preset.UseMount)
            Task_MountBeforeStart.Enqueue();

        for (var i = 0; i < preset.Waypoints.Count; i++)
        {
            var waypoint = preset.Waypoints[i];
            var waypointNo = i + 1;
            P.taskManager.Enqueue(() => MoveAndWait(waypoint, preset.UseMount), $"{name} 経由地 {waypointNo}", Utils.TaskConfig);
        }

        var destination = preset.Destination.Value;
        P.taskManager.Enqueue(() => MoveAndWait(destination, preset.UseMount), $"{name} へ移動", Utils.TaskConfig);
        P.taskManager.Enqueue(() =>
        {
            activePreset = null;
            mountLostAt = 0;
            return true;
        }, $"{name} 移動完了", Utils.TaskConfig);
    }

    private static bool? MoveAndWait(Vector3 destination, bool useMount)
    {
        if (!Player.Available)
            return false;

        if (useMount)
        {
            if (Svc.Condition[ConditionFlag.Mounted])
            {
                mountLostAt = 0;
            }
            else
            {
                var now = Environment.TickCount64;
                if (mountLostAt == 0)
                    mountLostAt = now;

                // Do not fight water traversal. Keep swimming toward land; once back on
                // land and dismounted for three seconds, stop briefly and remount.
                if (!MovementHelper.IsSwimming && !MovementHelper.IsDiving && now - mountLostAt >= 3000)
                {
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();

                    MovementHelper.TryMount();
                    return false;
                }
            }
        }

        if (Vector3.Distance(Player.Object!.Position, destination) < 2f)
        {
            // Do not advance to the next waypoint until vnavmesh has fully settled.
            // Otherwise SimpleMove.PathfindInProgress can still be true for the previous
            // segment and the next waypoint never starts.
            if (P.navmesh.IsRunning() || P.navmesh.PathfindInProgress())
            {
                P.navmesh.Stop();
                return false;
            }

            return true;
        }

        if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress())
        {
            if (EzThrottler.Throttle($"QuickTravel_{destination.X:F1}_{destination.Z:F1}", 500))
            {
                // Ground pathing still keeps the character mounted when UseMount is enabled.
                // Mounting itself is handled and confirmed before this task is reached.
                P.navmesh.PathfindAndMoveTo(destination, false);
            }
        }

        return false;
    }
}
