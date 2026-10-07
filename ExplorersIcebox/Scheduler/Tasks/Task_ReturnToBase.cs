using ECommons.GameHelpers;
using ECommons.Throttlers;
using ExplorersIcebox.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
namespace ExplorersIcebox.Scheduler.Tasks;

internal static class Task_ReturnToBase
{
    public static void Enqueue(bool continueWorkflow = true)
    {
        P.taskManager.Enqueue(() => TeleportCheck(continueWorkflow), continueWorkflow ? "Returning to base" : "Return to base only");
    }

    internal static unsafe bool? TeleportCheck(bool continueWorkflow = true)
    {
        if (Player.DistanceTo(new Vector3(-268, 40, 226)) < 5)
        {
            if (continueWorkflow)
            {
                Svc.Log.Debug("Teleport has completed, moving onto check sell");
                SchedulerMain.State = IceBoxState.CheckSell;
            }
            else
            {
                Svc.Log.Information("Standalone return to base completed");
                SchedulerMain.State = IceBoxState.Idle;
            }
            return true;
        }
        if (!Player.IsBusy)
        {
            if (EzThrottler.Throttle("Returning to base"))
            {
                Svc.Log.Information("Launching action to return to base");
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 27);
            }
        }

        return false;
    }
}
