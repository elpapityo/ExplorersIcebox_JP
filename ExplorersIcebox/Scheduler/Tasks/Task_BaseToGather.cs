using Dalamud.Game.ClientState.Conditions;
using ExplorersIcebox.Util;
using System.Collections.Generic;
using System.Linq;
namespace ExplorersIcebox.Scheduler.Tasks;

/// <summary>
/// Base -> gathering-area movement.
///
/// Upstream behavior is preserved when flight is available. When an upstream route
/// requests flight but this character cannot fly here, only this base-travel task
/// supplies a compatibility fallback. Gathering-node movement is intentionally left
/// untouched in Task_IslandInteract.
/// </summary>
internal static class Task_BaseToGather
{
    private sealed class TravelState
    {
        public bool FlightUnavailable;
        public bool UnderwaterRoute;
        public bool WaterEntrySeen;
        public long NextDiveAttemptAt;
        public int DiveAttempts;
        public bool HadMount;
        public long LandedUnmountedAt;
        public List<Vector3>? SurfaceLeg;
        public Vector3 WaterEntryTarget;
        public Vector3 UnderwaterTarget;
        public bool AccidentalWaterSeen;
    }

    public static void Enqueue(List<Vector3> BaseWPList, bool mount, bool fly)
    {
        var state = CreateState(BaseWPList, fly);
        P.taskManager.Enqueue(() => BaseToGather(BaseWPList, mount, fly, state), "Moving from base -> gather point", Utils.TaskConfig);
    }

    internal static bool? BaseToGather(List<Vector3> BaseWPList, bool mount, bool fly)
    {
        var state = CreateState(BaseWPList, fly);
        return BaseToGather(BaseWPList, mount, fly, state);
    }

    private static TravelState CreateState(List<Vector3> waypoints, bool fly)
    {
        var state = new TravelState
        {
            FlightUnavailable = fly && !RouteCompatibility.CanFlyHere(),
            UnderwaterRoute = fly && !RouteCompatibility.CanFlyHere() && RouteCompatibility.IsUnderwaterGatherRoute(IslandHelper.CurrentRoute),
        };

        // The two upstream underwater routes both define the final BaseToLocation
        // point as the underwater destination, with the preceding point as the water
        // entry. Keep the upstream coordinates unchanged and only split execution of
        // that already-defined route when flight is unavailable.
        if (state.UnderwaterRoute && waypoints.Count >= 2)
        {
            state.SurfaceLeg = waypoints.Take(waypoints.Count - 1).ToList();
            state.WaterEntryTarget = waypoints[^2];
            state.UnderwaterTarget = waypoints[^1];
        }

        return state;
    }

    private static bool? BaseToGather(List<Vector3> waypoints, bool mount, bool fly, TravelState state)
    {
        if (waypoints == null || waypoints.Count == 0)
            return true;

        // Preserve the upstream completion rule.
        if (PlayerHelper.GetDistanceToPlayer(waypoints[^1]) < 0.5f)
            return true;

        // If flight is available, this is exactly the upstream behavior.
        if (!state.FlightUnavailable)
        {
            if (!P.navmesh.IsRunning())
                MovementHelper.TryStartNavmesh(waypoints, mount, fly);
            return false;
        }

        var mounted = Svc.Condition[ConditionFlag.Mounted];
        var swimming = Svc.Condition[ConditionFlag.Swimming];
        var diving = Svc.Condition[ConditionFlag.Diving];

        // Upstream underwater routes need one explicit state transition only when
        // flight is unavailable: ground/surface travel -> Dive -> 3D navmesh.
        if (state.UnderwaterRoute)
        {
            if (diving)
            {
                state.WaterEntrySeen = true;
                if (!P.navmesh.IsRunning())
                {
                    // Questionable's current movement controller also uses the
                    // navmesh "fly" mode while ConditionFlag.Diving is active.
                    P.navmesh.MoveTo(new List<Vector3> { state.UnderwaterTarget }, true);
                    Svc.Log.Debug("[BaseRouteCompat] Diving confirmed; underwater leg started");
                }
                return false;
            }

            if (swimming)
            {
                // Do not treat every patch of water on the route as the underwater
                // entrance. The upstream route already gives us the intended water-entry
                // waypoint (the penultimate BaseToLocation point), so only start the
                // dive transition when we are actually near that point. This prevents
                // an accidental river/pond crossing from terminating base travel.
                var distanceToEntry = PlayerHelper.GetDistanceToPlayer(state.WaterEntryTarget);
                var atIntendedWaterEntry = distanceToEntry <= 12.0f;

                if (!atIntendedWaterEntry)
                {
                    state.AccidentalWaterSeen = true;
                    state.LandedUnmountedAt = 0;

                    // Keep travelling on the surface through accidental water. Never
                    // mount or dive here. If navmesh happened to stop, restart the same
                    // upstream surface leg directly in ground/surface mode without
                    // invoking mount logic while swimming.
                    if (!P.navmesh.IsRunning() && state.SurfaceLeg != null)
                        P.navmesh.MoveTo(new List<Vector3>(state.SurfaceLeg), false);

                    Svc.Log.Debug($"[BaseRouteCompat] Incidental swimming; continue surface leg, entryDistance={distanceToEntry:F1}m");
                    return false;
                }

                if (P.navmesh.IsRunning())
                    P.navmesh.Stop();

                var now = Environment.TickCount64;
                if (!state.WaterEntrySeen)
                {
                    state.WaterEntrySeen = true;
                    state.DiveAttempts = 0;
                    state.NextDiveAttemptAt = now;
                    Svc.Log.Debug($"[BaseRouteCompat] Intended water entry reached; surface leg stopped, entryDistance={distanceToEntry:F1}m");
                }

                // Retry only at the route-defined underwater entrance.
                if (now >= state.NextDiveAttemptAt && state.DiveAttempts < 4)
                {
                    state.DiveAttempts++;
                    var fired = MovementHelper.TryDive();
                    state.NextDiveAttemptAt = now + 5000;
                    Svc.Log.Debug($"[BaseRouteCompat] Dive attempt #{state.DiveAttempts}, fired={fired}, mounted={mounted}, swimming={swimming}, diving={diving}");
                }

                return false;
            }

            // If we crossed incidental water and came back onto land, remount once
            // before continuing. This applies only to base -> gather travel.
            if (!state.WaterEntrySeen && state.AccidentalWaterSeen && mount && !mounted)
            {
                var now = Environment.TickCount64;
                if (state.LandedUnmountedAt == 0)
                {
                    state.LandedUnmountedAt = now;
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();
                    Svc.Log.Debug("[BaseRouteCompat] Back on land after incidental water; waiting to remount");
                }

                if (now - state.LandedUnmountedAt >= 3000)
                    MovementHelper.TryMount();

                return false;
            }

            if (mounted)
            {
                state.HadMount = true;
                state.LandedUnmountedAt = 0;
                if (state.AccidentalWaterSeen)
                    state.AccidentalWaterSeen = false;
            }

            // While approaching the water, execute the original BaseToLocation route
            // only through its existing penultimate (water-entry) waypoint.
            if (!state.WaterEntrySeen && state.SurfaceLeg != null)
            {
                if (!P.navmesh.IsRunning())
                    MovementHelper.TryStartNavmesh(state.SurfaceLeg, mount, false);
                return false;
            }

            // Brief client-state transition after the dive command: do not remount,
            // do not restart ground movement, and wait for Swimming/Diving to settle.
            return false;
        }

        // Non-underwater fly=true route, but flight is not unlocked. The upstream
        // intermediate waypoints are flight-route guide points, not reliable ground
        // resume markers. Replaying them after a remount can deliberately send the
        // character back toward an already-passed point. Let vnavmesh pathfind from
        // the CURRENT position straight to the original final BaseToLocation point.
        // This branch is only for the flight-unavailable base -> gather fallback.

        if (mounted)
        {
            state.HadMount = true;
            state.LandedUnmountedAt = 0;
        }
        else if (mount && state.HadMount)
        {
            if (swimming || diving)
            {
                state.LandedUnmountedAt = 0;
                // Do not issue mount actions in water and do not stop an active path.
                if (P.navmesh.IsRunning())
                    return false;
            }
            else
            {
                var now = Environment.TickCount64;
                if (state.LandedUnmountedAt == 0)
                {
                    state.LandedUnmountedAt = now;
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();
                    Svc.Log.Debug("[BaseRouteCompat] Ground fallback paused for remount; current-position resume will be used");
                }

                if (now - state.LandedUnmountedAt >= 3000)
                {
                    MovementHelper.TryMount();
                    return false;
                }

                return false;
            }
        }

        if (!P.navmesh.IsRunning())
        {
            // Resume from the character's current position. Passing only the final
            // destination prevents old flight guide points behind us from being
            // replayed after water/remount interruptions. vnavmesh calculates the
            // ground path from the current position each time.
            var finalDestination = new List<Vector3> { waypoints[^1] };
            Svc.Log.Debug($"[BaseRouteCompat] Ground fallback pathfind from current position to final {waypoints[^1]}");
            MovementHelper.TryStartNavmesh(finalDestination, mount, false);
        }

        return false;
    }
}
