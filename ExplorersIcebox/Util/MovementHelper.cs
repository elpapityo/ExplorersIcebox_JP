using Dalamud.Game.ClientState.Conditions;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace ExplorersIcebox.Util;

/// <summary>
///     Shared movement utilities for navmesh traversal, mounting, flying, and diving.
/// </summary>
public static unsafe class MovementHelper
{
    // Dive function from the game client
    private delegate byte DiveDelegate(void* control);
    private static readonly DiveDelegate DiveFunc =
        Marshal.GetDelegateForFunctionPointer<DiveDelegate>(
            Svc.SigScanner.ScanText("48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 1D ?? ?? ?? ?? 48 8D 54 24"));

    private static bool IsPlayerMounted => Svc.Condition[ConditionFlag.Mounted];
    private static bool IsCastingOrTransitioning =>
        Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.MountOrOrnamentTransition];

    /// <summary>
    ///     Attempts to move the player along a waypoint list using navmesh.
    ///     Handles mounting, flying, sprinting, and starting navmesh movement.
    ///     Returns true when navmesh is actively running toward the destination.
    /// </summary>
    public static bool TryStartNavmesh(List<Vector3> waypoints, bool mount, bool fly)
    {
        if (P.navmesh.IsRunning() && (IsPlayerMounted || !mount))
            return true;

        if (fly)
        {
            if (!IsPlayerMounted)
            {
                TryMount();
            }
            else
            {
                if (EzThrottler.Throttle("MoveToQueue_FlyMode"))
                    P.navmesh.MoveTo(new(waypoints), true);
            }
        }
        else
        {
            if (mount && !IsPlayerMounted)
                TryMount();

            if (!mount)
                TrySprint();

            if (EzThrottler.Throttle($"MoveToQueue_Ground_{waypoints[0]}"))
                P.navmesh.MoveTo(new(waypoints), false);
        }

        return false;
    }

    /// <summary>
    ///     Uses the mount roulette general action.
    /// </summary>
    public static void TryMount()
    {
        if (!IsCastingOrTransitioning && EzThrottler.Throttle("Using Mount Action Roulette", 250))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
    }

    /// <summary>
    ///     Uses the sprint general action.
    /// </summary>
    public static void TrySprint()
    {
        if (EzThrottler.Throttle("Using sprint"))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 26);
    }

    /// <summary>
    ///     Dismounts the player.
    /// </summary>
    public static void Dismount()
    {
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
    }

    /// <summary>
    ///     Executes the dive action. Player must be swimming or mounted on water.
    /// </summary>
    public static void ExecuteDive()
    {
        DiveFunc(Control.Instance());
    }

    /// <summary>
    ///     Returns true if the player is currently diving.
    /// </summary>
    public static bool IsDiving => Svc.Condition[ConditionFlag.Diving];

    /// <summary>
    ///     Returns true if the player is currently swimming.
    /// </summary>
    public static bool IsSwimming => Svc.Condition[ConditionFlag.Swimming];

    /// <summary>
    ///     Attempts to dive. If mounted on water, the upstream dive + dismount
    ///     sequence is preserved. If already swimming and unmounted, do not send
    ///     a dismount action because that action is invalid in this state.
    ///     Returns true if the dive action was fired.
    /// </summary>
    public static bool TryDive()
    {
        if (IsDiving)
            return false;

        if (IsPlayerMounted)
        {
            ExecuteDive();
            Dismount();
            return true;
        }

        if (IsSwimming)
        {
            ExecuteDive();
            return true;
        }

        return false;
    }
}
