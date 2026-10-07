using Dalamud.Game.ClientState.Conditions;
using ExplorersIcebox.Util;

namespace ExplorersIcebox.Scheduler.Tasks;

/// <summary>
/// Ensures the player is mounted before the normal automation workflow begins.
/// Uses the plugin's existing mount-roulette helper and waits until the mounted
/// condition is confirmed so movement does not begin first.
/// </summary>
internal static class Task_MountBeforeStart
{
    public static void Enqueue()
    {
        P.taskManager.Enqueue(MountAndWait, "Mount before start", Utils.TaskConfig);
    }

    private static bool? MountAndWait()
    {
        if (Svc.Condition[ConditionFlag.Mounted])
            return true;

        MovementHelper.TryMount();
        return false;
    }
}
