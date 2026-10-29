using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Logging;
using ECommons.Throttlers;
using ExplorersIcebox.Util;
using ExplorersIcebox.IPC;
using ExplorersIcebox.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Callback = ECommons.Automation.Callback;

namespace ExplorersIcebox.Scheduler;

/// <summary>
/// Manual-care cropland watering support.
/// Mammet-managed plots are excluded. Farm state is read directly from MJIManager.FarmState.
/// Water mode is CurrentMode=3. Runtime log verification shows ContextIconMenu index 3 selects CurrentMode=3.
/// </summary>
internal static unsafe class FarmAutomation
{
    private const uint HarvestCurrentMode = 1;
    private const int HarvestModeMenuIndex = 0;
    private const uint SowCurrentMode = 2;
    private const int SowModeMenuIndex = 1;
    private const uint WaterCurrentMode = 3;
    private const int WaterModeMenuIndex = 2;
    private const int QueueRefreshRemaining = 1;
    // Mandatory checkpoints for every transition from the lower farm (Slot 0-9)
    // to the upper farm (Slot 10-19).
    // These are user-confirmed internal game coordinates. Pass the exact Vector3
    // values directly to vnavmesh; do not convert from map coordinates.
    // Upper-farm plots must never be selected before both checkpoints are passed.
    private static readonly Vector3 FarmMidpointAfterFirstTen = new(-206.962387f, 60.830456f, 144.498901f);
    // Second mandatory internal checkpoint for the lower-farm -> upper-farm transition.
    private static readonly Vector3 FarmSecondMidpointAfterFirstTen = new(-187.511795f, 66.061340f, 136.319870f);
    private const float FarmMidpointArrivalRadius = 1.0f;

    internal sealed record FarmSlotState(
        int SlotId,
        byte SeedType,
        byte WaterLevel,
        byte GrowthLevel,
        byte YieldAvailable,
        bool UnderCare,
        bool WasUnderCare,
        bool CareHalted,
        uint PlotObjectIndex,
        uint LayoutId);

    private enum FarmRunKind
    {
        Water,
        HarvestAndSow,
    }

    private enum RunStep
    {
        Idle,
        FindPlot,
        MoveToPlot,
        SetWaterMode,
        WaterInteract,
        WaitWater,
        SetHarvestMode,
        HarvestInteract,
        WaitHarvest,
        SetSowMode,
        SowInteract,
        WaitSow,
        ApproachFarm,
        CrossToBackFarm,
        CrossToBackFarmSecond,
    }

    // Keep the original target set until the entire run is complete.
    // runQueue only contains targets whose physical plot object is currently loaded.
    private static readonly HashSet<int> masterTargetSlots = [];
    private static readonly HashSet<int> completedSlots = [];
    private static readonly List<int> runQueue = [];
    private static int activeSlot = -1;
    private static RunStep runStep;
    private static long stepStarted;
    private static byte waterBefore;
    private static string status = "停止中";
    private static bool updateHooked;
    private static long lastMoveRequest;
    private static bool modeClickSent;
    private static bool farmMidpointDone;
    private static FarmRunKind runKind = FarmRunKind.Water;
    private static uint selectedSeedItem;

    internal static bool IsRunning => runStep != RunStep.Idle;
    internal static string Status => status;

    internal static bool IsFarmAvailable
    {
        get
        {
            var mgr = MJIManager.Instance();
            return mgr != null && mgr->IsPlayerInSanctuary && mgr->FarmState != null;
        }
    }

    internal static IReadOnlyList<FarmSlotState> GetSlots()
    {
        var result = new List<FarmSlotState>();
        var mgr = MJIManager.Instance();
        if (mgr == null || mgr->FarmState == null)
            return result;

        var farm = mgr->FarmState;
        var count = Math.Clamp((int)mgr->GetFarmSlotCount(), 0, 20);
        for (var i = 0; i < count; i++)
        {
            var flags = farm->FarmSlotFlags[i];
            result.Add(new FarmSlotState(
                i,
                farm->SeedType[i],
                farm->WaterLevel[i],
                farm->GrowthLevel[i],
                farm->GardenerYield[i],
                flags.HasFlag(FarmSlotFlags.UnderCare),
                flags.HasFlag(FarmSlotFlags.WasUnderCare),
                flags.HasFlag(FarmSlotFlags.CareHalted),
                farm->PlotObjectIndex[i],
                farm->LayoutId[i]));
        }
        return result;
    }

    internal static uint GetCurrentMode()
    {
        var mgr = MJIManager.Instance();
        return mgr == null ? 0 : mgr->CurrentMode;
    }

    internal static uint GetCurrentModeItem()
    {
        var mgr = MJIManager.Instance();
        return mgr == null ? 0 : mgr->CurrentModeItem;
    }

    internal static bool StartWatering()
    {
        if (IsRunning)
            return false;
        if (!IsFarmAvailable)
        {
            status = "畑情報を取得できません。";
            return false;
        }
        if (SchedulerMain.State != IceBoxState.Idle || P.taskManager.NumQueuedTasks > 0 || PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed)
        {
            status = "他の自動処理を停止してから開始してください。";
            return false;
        }
        if (!NavmeshIPC.Installed)
        {
            status = "vnavmesh が必要です。";
            return false;
        }

        masterTargetSlots.Clear();
        completedSlots.Clear();
        runQueue.Clear();
        activeSlot = -1;

        var threshold = Math.Clamp(C.FarmWaterLevelThreshold, 0, 255);
        foreach (var slot in GetSlots())
        {
            if (!C.FarmWaterTargetSlots.Contains(slot.SlotId))
                continue;
            if (!ShouldWater(slot, threshold))
                continue;
            masterTargetSlots.Add(slot.SlotId);
        }

        if (masterTargetSlots.Count == 0)
        {
            status = "水やり対象はありません。";
            return false;
        }

        RefreshVisibleQueue();
        runStep = RunStep.FindPlot;
        stepStarted = Environment.TickCount64;
        lastMoveRequest = 0;
        modeClickSent = false;
        farmMidpointDone = false;
        runKind = FarmRunKind.Water;
        selectedSeedItem = 0;
        EnsureUpdateHook();
        PluginLog.Information($"[Farm] 水やり開始: targets={masterTargetSlots.Count} visible={runQueue.Count} threshold={threshold}");
        status = $"水やり開始: {masterTargetSlots.Count}区画";
        return true;
    }

    internal static bool StartHarvestAndSow()
    {
        if (IsRunning)
            return false;
        if (!IsFarmAvailable)
        {
            status = "畑情報を取得できません。";
            return false;
        }
        if (SchedulerMain.State != IceBoxState.Idle || P.taskManager.NumQueuedTasks > 0 || PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed)
        {
            status = "他の自動処理を停止してから開始してください。";
            return false;
        }
        if (!NavmeshIPC.Installed)
        {
            status = "vnavmesh が必要です。";
            return false;
        }

        var mgr = MJIManager.Instance();
        if (mgr == null || mgr->CurrentMode != SowCurrentMode || mgr->CurrentModeItem == 0)
        {
            status = "ゲーム側で「種まき」モードにして、使用する種を選択してから開始してください。";
            return false;
        }

        masterTargetSlots.Clear();
        completedSlots.Clear();
        runQueue.Clear();
        activeSlot = -1;

        selectedSeedItem = mgr->CurrentModeItem;
        runKind = FarmRunKind.HarvestAndSow;

        foreach (var slot in GetSlots())
        {
            if (!C.FarmWaterTargetSlots.Contains(slot.SlotId))
                continue;
            if (!ShouldHarvestAndSow(slot))
                continue;
            masterTargetSlots.Add(slot.SlotId);
        }

        if (masterTargetSlots.Count == 0)
        {
            status = "収穫可能な対象区画はありません。";
            selectedSeedItem = 0;
            runKind = FarmRunKind.Water;
            return false;
        }

        RefreshVisibleQueue();
        runStep = RunStep.FindPlot;
        stepStarted = Environment.TickCount64;
        lastMoveRequest = 0;
        modeClickSent = false;
        farmMidpointDone = false;
        EnsureUpdateHook();
        PluginLog.Information($"[Farm] 収穫→種まき開始: targets={masterTargetSlots.Count} seedItem={selectedSeedItem}");
        status = $"収穫→種まき開始: {masterTargetSlots.Count}区画 / SeedItem={selectedSeedItem}";
        return true;
    }

    internal static void Stop(string reason)
    {
        if (P.navmesh.IsRunning())
            P.navmesh.Stop();
        masterTargetSlots.Clear();
        completedSlots.Clear();
        runQueue.Clear();
        activeSlot = -1;
        runStep = RunStep.Idle;
        stepStarted = 0;
        lastMoveRequest = 0;
        modeClickSent = false;
        farmMidpointDone = false;
        selectedSeedItem = 0;
        runKind = FarmRunKind.Water;
        status = reason;
        RemoveUpdateHookIfIdle();
        PluginLog.Information($"[Farm] 停止: {reason}");
    }

    internal static void LogSnapshot()
    {
        var mgr = MJIManager.Instance();
        if (mgr == null || mgr->FarmState == null)
        {
            PluginLog.Warning("[Farm] 状態取得不可");
            return;
        }

        PluginLog.Information($"[Farm] Snapshot CurrentMode={mgr->CurrentMode} slots={mgr->GetFarmSlotCount()}");
        foreach (var s in GetSlots())
        {
            if (TryGetPlotObject(s, out var obj) && obj != null)
                PluginLog.Information($"[Farm] Slot={s.SlotId} Seed={s.SeedType} Water={s.WaterLevel} Growth={s.GrowthLevel} Yield={s.YieldAvailable} UnderCare={s.UnderCare} WasCare={s.WasUnderCare} Halt={s.CareHalted} PlotIndex={s.PlotObjectIndex} Layout={s.LayoutId:X8} Loaded=True Pos={obj.Position}");
            else
                PluginLog.Information($"[Farm] Slot={s.SlotId} Seed={s.SeedType} Water={s.WaterLevel} Growth={s.GrowthLevel} Yield={s.YieldAvailable} UnderCare={s.UnderCare} WasCare={s.WasUnderCare} Halt={s.CareHalted} PlotIndex={s.PlotObjectIndex} Layout={s.LayoutId:X8} Loaded=False");
        }
    }

    internal static void Dispose()
    {
        if (updateHooked)
        {
            Svc.Framework.Update -= OnUpdate;
            updateHooked = false;
        }
    }

    private static bool ShouldWater(FarmSlotState slot, int threshold)
    {
        // Empty plots, mammet-managed plots and crops already waiting for harvest are excluded.
        // WaterLevel semantics are intentionally left configurable; the default threshold remains 0.
        return slot.SeedType != 0
            && !slot.UnderCare
            && slot.YieldAvailable == 0
            && slot.WaterLevel <= threshold;
    }

    private static bool ShouldHarvestAndSow(FarmSlotState slot)
        => slot.SeedType != 0
            && !slot.UnderCare
            && slot.YieldAvailable > 0;

    private static bool ShouldProcess(FarmSlotState slot)
        => runKind == FarmRunKind.HarvestAndSow
            ? ShouldHarvestAndSow(slot)
            : ShouldWater(slot, Math.Clamp(C.FarmWaterLevelThreshold, 0, 255));

    private static void EnsureUpdateHook()
    {
        if (updateHooked)
            return;
        Svc.Framework.Update += OnUpdate;
        updateHooked = true;
    }

    private static void RemoveUpdateHookIfIdle()
    {
        if (!updateHooked || IsRunning)
            return;
        Svc.Framework.Update -= OnUpdate;
        updateHooked = false;
    }

    private static void OnUpdate(object _)
    {
        if (!IsRunning)
            return;
        if (!IsFarmAvailable || Svc.Objects.LocalPlayer == null)
        {
            Stop("畑情報を取得できなくなったため停止しました。");
            return;
        }

        // Slots may change while the run is active (weather, growth, manual interaction, etc.).
        ReconcileTargets();
        if (IsComplete())
        {
            Finish();
            return;
        }

        var slots = GetSlots();
        var slot = activeSlot >= 0 ? slots.FirstOrDefault(x => x.SlotId == activeSlot) : null;

        switch (runStep)
        {
            case RunStep.FindPlot:
                TickFindPlot();
                break;
            case RunStep.MoveToPlot:
                if (slot == null) CompleteActive("区画情報が消えたためスキップ");
                else TickMoveToPlot(slot);
                break;
            case RunStep.SetWaterMode:
                TickSetWaterMode();
                break;
            case RunStep.WaterInteract:
                if (slot == null) CompleteActive("区画情報が消えたためスキップ");
                else TickWaterInteract(slot);
                break;
            case RunStep.WaitWater:
                if (slot == null) CompleteActive("区画情報が消えたため次へ");
                else TickWaitWater(slot);
                break;
            case RunStep.SetHarvestMode:
                TickSetMode(HarvestCurrentMode, HarvestModeMenuIndex, RunStep.HarvestInteract, "収穫");
                break;
            case RunStep.HarvestInteract:
                if (slot == null) CompleteActive("区画情報が消えたためスキップ");
                else TickHarvestInteract(slot);
                break;
            case RunStep.WaitHarvest:
                if (slot == null) CompleteActive("区画情報が消えたため次へ");
                else TickWaitHarvest(slot);
                break;
            case RunStep.SetSowMode:
                TickSetSowMode();
                break;
            case RunStep.SowInteract:
                if (slot == null) CompleteActive("区画情報が消えたためスキップ");
                else TickSowInteract(slot);
                break;
            case RunStep.WaitSow:
                if (slot == null) CompleteActive("区画情報が消えたため次へ");
                else TickWaitSow(slot);
                break;
            case RunStep.ApproachFarm:
                TickApproachFarm();
                break;
            case RunStep.CrossToBackFarm:
                TickCrossToBackFarm();
                break;
            case RunStep.CrossToBackFarmSecond:
                TickCrossToBackFarmSecond();
                break;
        }
    }

    private static void TickFindPlot()
    {
        if (ShouldCrossFarmMidpoint())
        {
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();
            runStep = RunStep.CrossToBackFarm;
            stepStarted = Environment.TickCount64;
            lastMoveRequest = 0;
            status = "下段(0-9)完了・第1指定地点へ移動";
            PluginLog.Information($"[Farm] 下段畑(Slot 0-9)完了・上段畑(Slot 10-19)へ進む前に第1指定地点へ移動 Dest={FarmMidpointAfterFirstTen}");
            return;
        }

        if (runQueue.Count <= QueueRefreshRemaining)
            RefreshVisibleQueue();

        if (runQueue.Count == 0)
        {
            runStep = RunStep.ApproachFarm;
            stepStarted = Environment.TickCount64;
            status = "未読込区画を探索中";
            return;
        }

        // Farm watering must always follow SlotId order.
        // Do not skip to a higher Slot just because it is closer or already loaded.
        var nextSlot = masterTargetSlots
            .Where(x => !completedSlots.Contains(x))
            .OrderBy(x => x)
            .FirstOrDefault(-1);

        if (nextSlot < 0)
        {
            Finish();
            return;
        }

        if (!runQueue.Contains(nextSlot))
        {
            runStep = RunStep.ApproachFarm;
            stepStarted = Environment.TickCount64;
            status = $"Slot {nextSlot} の読込待ち";
            return;
        }

        activeSlot = nextSlot;
        runQueue.Remove(nextSlot);

        var slot = GetSlots().FirstOrDefault(x => x.SlotId == activeSlot);
        if (slot == null || !ShouldProcess(slot))
        {
            completedSlots.Add(activeSlot);
            activeSlot = -1;
            return;
        }

        if (!TryGetPlotObject(slot, out var obj) || obj == null)
        {
            // It was loaded during the previous scan but disappeared before selection.
            activeSlot = -1;
            RefreshVisibleQueue();
            return;
        }

        runStep = RunStep.MoveToPlot;
        stepStarted = Environment.TickCount64;
        lastMoveRequest = 0;
        status = $"Slot {slot.SlotId} へ移動";
        PluginLog.Information($"[Farm] 次の区画 Slot={slot.SlotId} Pos={obj.Position} Remaining={UnresolvedCount()} VisibleQueue={runQueue.Count}");
    }

    private static void TickMoveToPlot(FarmSlotState slot)
    {
        if (!ShouldProcess(slot))
        {
            CompleteActive(runKind == FarmRunKind.HarvestAndSow ? "収穫対象でなくなったためスキップ" : "水やり不要になったためスキップ");
            return;
        }

        if (!TryGetPlotObject(slot, out var obj) || obj == null)
        {
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();
            activeSlot = -1;
            runStep = RunStep.FindPlot;
            stepStarted = Environment.TickCount64;
            RefreshVisibleQueue();
            return;
        }

        var player = Svc.Objects.LocalPlayer!;
        var distance = Vector3.Distance(player.Position, obj.Position);
        if (distance <= 3.2f)
        {
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();
            runStep = runKind == FarmRunKind.HarvestAndSow ? RunStep.SetHarvestMode : RunStep.SetWaterMode;
            stepStarted = Environment.TickCount64;
            status = runKind == FarmRunKind.HarvestAndSow
                ? $"Slot {slot.SlotId} 収穫モードへ切替"
                : $"Slot {slot.SlotId} 水やりモードへ切替";
            return;
        }

        if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress() && Environment.TickCount64 - lastMoveRequest >= 750)
        {
            lastMoveRequest = Environment.TickCount64;
            P.navmesh.PathfindAndMoveTo(obj.Position, false);
            PluginLog.Information($"[Farm] Slot={slot.SlotId} Plotへ移動 Layout={slot.LayoutId:X8} Pos={obj.Position} Dist={distance:F1}");
        }

        if (Environment.TickCount64 - stepStarted > 30000)
            Stop($"Slot {slot.SlotId} への移動が30秒を超えたため停止しました。");
    }

    private static void TickSetWaterMode()
    {
        var mgr = MJIManager.Instance();
        if (mgr == null)
            return;
        if (mgr->CurrentMode == WaterCurrentMode)
        {
            modeClickSent = false;
            runStep = RunStep.WaterInteract;
            stepStarted = Environment.TickCount64;
            PluginLog.Information($"[Farm] 水やりMode確認 Mode={mgr->CurrentMode}");
            return;
        }

        if (!modeClickSent)
        {
            if (TryGetAddonByName("ContextIconMenu", out AtkUnitBase* context) && IsAddonReady(context) && context->IsVisible)
            {
                if (EzThrottler.Throttle("FarmWater_ContextMenu", 500))
                {
                    // Pasture v72 uses this same ContextIconMenu callback shape.
                    // Runtime log verification on v1.1.0.75 established index 2 -> CurrentMode 2; water CurrentMode 3 uses index 3.
                    Callback.Fire(context, true, 0, WaterModeMenuIndex, 0, 0, 0);
                    modeClickSent = true;
                    PluginLog.Information($"[Farm] 水やりメニュー選択 index={WaterModeMenuIndex} beforeMode={mgr->CurrentMode} expectedMode={WaterCurrentMode}");
                }
            }
            else if (TryGetAddonByName("MJIHud", out AtkUnitBase* hud) && IsAddonReady(hud))
            {
                if (EzThrottler.Throttle("FarmWater_OpenModeMenu", 500))
                    Callback.Fire(hud, false, 11, 0);
            }
        }
        else if (EzThrottler.Throttle("FarmWater_WaitMode", 1000))
        {
            PluginLog.Information($"[Farm] 水やりモード待ち actual={mgr->CurrentMode} expected={WaterCurrentMode}");
        }

        if (Environment.TickCount64 - stepStarted > 6500)
            Stop($"水やりモード(CurrentMode={WaterCurrentMode})へ切り替わらないため停止しました。実値={mgr->CurrentMode}");
    }

    private static void TickWaterInteract(FarmSlotState slot)
    {
        if (!TryGetPlotObject(slot, out var obj) || obj == null)
        {
            activeSlot = -1;
            runStep = RunStep.FindPlot;
            stepStarted = Environment.TickCount64;
            RefreshVisibleQueue();
            return;
        }

        var distance = Vector3.Distance(Svc.Objects.LocalPlayer!.Position, obj.Position);
        if (distance > 3.6f)
        {
            runStep = RunStep.MoveToPlot;
            stepStarted = Environment.TickCount64;
            return;
        }

        waterBefore = slot.WaterLevel;
        Utils.TargetgameObject(obj);
        Utils.InteractWithObject(obj);
        PluginLog.Information($"[Farm] Slot={slot.SlotId} 水やり実行 WaterBefore={waterBefore} Layout={slot.LayoutId:X8} Pos={obj.Position}");
        runStep = RunStep.WaitWater;
        stepStarted = Environment.TickCount64;
        status = $"Slot {slot.SlotId} 水やり確認中";
    }

    private static void TickWaitWater(FarmSlotState original)
    {
        var current = GetSlots().FirstOrDefault(x => x.SlotId == original.SlotId);
        if (current == null)
        {
            CompleteActive("区画情報が消えたため次へ");
            return;
        }

        // Do not assume whether the game's WaterLevel increases or decreases after watering.
        // Any actual change is accepted as confirmation.
        if (current.WaterLevel != waterBefore)
        {
            PluginLog.Information($"[Farm] Slot={current.SlotId} 水やり確認成功 Water={waterBefore}->{current.WaterLevel}");
            CompleteActive($"Slot {current.SlotId} 水やり完了");
            return;
        }

        if (!ShouldWater(current, Math.Clamp(C.FarmWaterLevelThreshold, 0, 255)))
        {
            PluginLog.Information($"[Farm] Slot={current.SlotId} 水やり後状態確認 Water={waterBefore}->{current.WaterLevel}");
            CompleteActive($"Slot {current.SlotId} 水やり完了");
            return;
        }

        if (Environment.TickCount64 - stepStarted > 6000)
            Stop($"Slot {current.SlotId} の水分値が変化しないため停止しました。Water={current.WaterLevel}");
    }


    private static void TickSetMode(uint expectedMode, int menuIndex, RunStep nextStep, string modeName)
    {
        var mgr = MJIManager.Instance();
        if (mgr == null)
            return;

        if (mgr->CurrentMode == expectedMode)
        {
            modeClickSent = false;
            runStep = nextStep;
            stepStarted = Environment.TickCount64;
            PluginLog.Information($"[Farm] {modeName}Mode確認 Mode={mgr->CurrentMode} Item={mgr->CurrentModeItem}");
            return;
        }

        if (!modeClickSent)
        {
            if (TryGetAddonByName("ContextIconMenu", out AtkUnitBase* context) && IsAddonReady(context) && context->IsVisible)
            {
                if (EzThrottler.Throttle($"Farm{modeName}_ContextMenu", 500))
                {
                    Callback.Fire(context, true, 0, menuIndex, 0, 0, 0);
                    modeClickSent = true;
                    PluginLog.Information($"[Farm] {modeName}メニュー選択 index={menuIndex} beforeMode={mgr->CurrentMode} expectedMode={expectedMode}");
                }
            }
            else if (TryGetAddonByName("MJIHud", out AtkUnitBase* hud) && IsAddonReady(hud))
            {
                if (EzThrottler.Throttle($"Farm{modeName}_OpenModeMenu", 500))
                    Callback.Fire(hud, false, 11, 0);
            }
        }
        else if (EzThrottler.Throttle($"Farm{modeName}_WaitMode", 1000))
        {
            PluginLog.Information($"[Farm] {modeName}モード待ち actual={mgr->CurrentMode} expected={expectedMode}");
        }

        if (Environment.TickCount64 - stepStarted > 6500)
            Stop($"{modeName}モード(CurrentMode={expectedMode})へ切り替わらないため停止しました。実値={mgr->CurrentMode}");
    }

    private static void TickHarvestInteract(FarmSlotState slot)
    {
        if (!TryGetPlotObject(slot, out var obj) || obj == null)
        {
            activeSlot = -1;
            runStep = RunStep.FindPlot;
            stepStarted = Environment.TickCount64;
            RefreshVisibleQueue();
            return;
        }

        if (Vector3.Distance(Svc.Objects.LocalPlayer!.Position, obj.Position) > 3.6f)
        {
            runStep = RunStep.MoveToPlot;
            stepStarted = Environment.TickCount64;
            return;
        }

        Utils.TargetgameObject(obj);
        Utils.InteractWithObject(obj);
        PluginLog.Information($"[Farm] Slot={slot.SlotId} 収穫実行 Seed={slot.SeedType} Yield={slot.YieldAvailable}");
        runStep = RunStep.WaitHarvest;
        stepStarted = Environment.TickCount64;
        status = $"Slot {slot.SlotId} 収穫確認中";
    }

    private static void TickWaitHarvest(FarmSlotState original)
    {
        var current = GetSlots().FirstOrDefault(x => x.SlotId == original.SlotId);
        if (current == null)
        {
            CompleteActive("区画情報が消えたため次へ");
            return;
        }

        if (current.SeedType == 0)
        {
            PluginLog.Information($"[Farm] Slot={current.SlotId} 収穫確認成功 Seed={original.SeedType}->0 Yield={original.YieldAvailable}->{current.YieldAvailable}");
            runStep = RunStep.SetSowMode;
            stepStarted = Environment.TickCount64;
            modeClickSent = false;
            status = $"Slot {current.SlotId} 種まきモードへ切替";
            return;
        }

        if (Environment.TickCount64 - stepStarted > 6000)
            Stop($"Slot {current.SlotId} の収穫後に空区画を確認できないため停止しました。Seed={current.SeedType} Yield={current.YieldAvailable}");
    }

    private static void TickSetSowMode()
    {
        var mgr = MJIManager.Instance();
        if (mgr == null)
            return;

        if (mgr->CurrentMode == SowCurrentMode)
        {
            if (mgr->CurrentModeItem != selectedSeedItem)
            {
                Stop($"種まきモードへ戻りましたが、開始時に選択していた種を確認できません。開始時Item={selectedSeedItem} 現在Item={mgr->CurrentModeItem}");
                return;
            }

            modeClickSent = false;
            runStep = RunStep.SowInteract;
            stepStarted = Environment.TickCount64;
            PluginLog.Information($"[Farm] 種まきMode確認 Mode={mgr->CurrentMode} SeedItem={mgr->CurrentModeItem}");
            return;
        }

        TickSetMode(SowCurrentMode, SowModeMenuIndex, RunStep.SowInteract, "種まき");
    }

    private static void TickSowInteract(FarmSlotState slot)
    {
        var mgr = MJIManager.Instance();
        if (mgr == null)
            return;

        if (mgr->CurrentMode != SowCurrentMode || mgr->CurrentModeItem != selectedSeedItem)
        {
            runStep = RunStep.SetSowMode;
            stepStarted = Environment.TickCount64;
            return;
        }

        var current = GetSlots().FirstOrDefault(x => x.SlotId == slot.SlotId);
        if (current == null)
        {
            CompleteActive("区画情報が消えたため次へ");
            return;
        }
        if (current.SeedType != 0)
        {
            CompleteActive($"Slot {current.SlotId} は既に種まき済みのため次へ");
            return;
        }

        if (!TryGetPlotObject(current, out var obj) || obj == null)
        {
            activeSlot = -1;
            runStep = RunStep.FindPlot;
            stepStarted = Environment.TickCount64;
            RefreshVisibleQueue();
            return;
        }

        if (Vector3.Distance(Svc.Objects.LocalPlayer!.Position, obj.Position) > 3.6f)
        {
            runStep = RunStep.MoveToPlot;
            stepStarted = Environment.TickCount64;
            return;
        }

        Utils.TargetgameObject(obj);
        Utils.InteractWithObject(obj);
        PluginLog.Information($"[Farm] Slot={current.SlotId} 種まき実行 SelectedSeedItem={selectedSeedItem}");
        runStep = RunStep.WaitSow;
        stepStarted = Environment.TickCount64;
        status = $"Slot {current.SlotId} 種まき確認中";
    }

    private static void TickWaitSow(FarmSlotState original)
    {
        var current = GetSlots().FirstOrDefault(x => x.SlotId == original.SlotId);
        if (current == null)
        {
            CompleteActive("区画情報が消えたため次へ");
            return;
        }

        if (current.SeedType != 0)
        {
            PluginLog.Information($"[Farm] Slot={current.SlotId} 種まき確認成功 SeedType={current.SeedType} SelectedSeedItem={selectedSeedItem}");
            CompleteActive($"Slot {current.SlotId} 収穫→種まき完了");
            return;
        }

        if (Environment.TickCount64 - stepStarted > 6000)
            Stop($"Slot {current.SlotId} の種まき後にSeedTypeが変化しないため停止しました。SelectedSeedItem={selectedSeedItem}");
    }

    private static bool ShouldCrossFarmMidpoint()
    {
        if (farmMidpointDone)
            return false;

        // Slot 0-9 = lower farm, Slot 10-19 = upper farm.
        // If any upper-farm target remains, do not allow selection of it until
        // every targeted lower-farm plot is complete and both checkpoints have
        // been traversed. This makes the lower -> upper crossing mandatory.
        var hasUpperTargets = masterTargetSlots.Any(x => x >= 10 && x <= 19 && !completedSlots.Contains(x));
        if (!hasUpperTargets)
            return false;

        var hasPendingLowerTargets = masterTargetSlots.Any(x => x >= 0 && x <= 9 && !completedSlots.Contains(x));
        return !hasPendingLowerTargets;
    }

    private static void TickCrossToBackFarm()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return;

        var distance = Vector3.Distance(player.Position, FarmMidpointAfterFirstTen);
        if (distance <= FarmMidpointArrivalRadius)
        {
            // 第1地点到達時は旧経路を明示的に破棄し、
            // 第2地点へは PathfindAndMoveTo ではなく単一ウェイポイントを直接渡す。
            // これにより第1→第2の間で別のNavmesh経路へ寄ろうとする動きを防ぐ。
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();

            P.navmesh.MoveTo(new List<Vector3> { FarmSecondMidpointAfterFirstTen }, false);
            runStep = RunStep.CrossToBackFarmSecond;
            stepStarted = Environment.TickCount64;
            lastMoveRequest = Environment.TickCount64;
            status = "第1指定地点通過・第2指定地点へ直行";
            PluginLog.Information($"[Farm] 第1指定地点通過 Pos={player.Position} -> 第2指定地点へ直行 Dest={FarmSecondMidpointAfterFirstTen}");
            return;
        }

        if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress() && Environment.TickCount64 - lastMoveRequest >= 750)
        {
            lastMoveRequest = Environment.TickCount64;
            P.navmesh.PathfindAndMoveTo(FarmMidpointAfterFirstTen, false);
            PluginLog.Information($"[Farm] 中継ポイントへ移動 Dest={FarmMidpointAfterFirstTen} Dist={distance:F1}");
        }

        if (Environment.TickCount64 - stepStarted > 15000)
            Stop($"畑中継ポイントへの移動が15秒を超えたため停止しました。現在地={player.Position}");
    }


    private static void TickCrossToBackFarmSecond()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return;

        var distance = Vector3.Distance(player.Position, FarmSecondMidpointAfterFirstTen);
        if (distance <= FarmMidpointArrivalRadius)
        {
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();
            farmMidpointDone = true;
            runStep = RunStep.FindPlot;
            stepStarted = Environment.TickCount64;
            lastMoveRequest = 0;
            status = "第2指定地点通過・上段(10-19)へ";
            PluginLog.Information($"[Farm] 第2指定地点通過 Pos={player.Position} -> 上段畑(Slot 10-19)の処理へ");
            return;
        }

        if (!P.navmesh.IsRunning() && Environment.TickCount64 - lastMoveRequest >= 750)
        {
            lastMoveRequest = Environment.TickCount64;
            P.navmesh.MoveTo(new List<Vector3> { FarmSecondMidpointAfterFirstTen }, false);
            PluginLog.Information($"[Farm] 第2中継ポイントへ直行 Dest={FarmSecondMidpointAfterFirstTen} Dist={distance:F1}");
        }

        if (Environment.TickCount64 - stepStarted > 15000)
            Stop($"畑第2中継ポイントへの移動が15秒を超えたため停止しました。現在地={player.Position}");
    }

    private static void TickApproachFarm()
    {
        RefreshVisibleQueue();
        if (runQueue.Count > 0)
        {
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();
            runStep = RunStep.FindPlot;
            stepStarted = Environment.TickCount64;
            status = "区画を検出しました";
            return;
        }

        var farmPoint = C.TravelPoints.FirstOrDefault(x => x.Destination != null && x.Name.Contains("畑", StringComparison.OrdinalIgnoreCase));
        if (farmPoint?.Destination is not Vector3 destination)
        {
            Stop("未処理区画のオブジェクトが未読込です。移動タブで名前に「畑」を含むポイントを登録してください。");
            return;
        }

        var player = Svc.Objects.LocalPlayer!;
        var distance = Vector3.Distance(player.Position, destination);
        if (distance <= 5f)
        {
            if (P.navmesh.IsRunning())
                P.navmesh.Stop();
            if (Environment.TickCount64 - stepStarted > 8000)
                Stop($"畑ポイント付近でも未処理区画を取得できませんでした。未処理={UnresolvedCount()}");
            return;
        }

        if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress() && Environment.TickCount64 - lastMoveRequest >= 1000)
        {
            lastMoveRequest = Environment.TickCount64;
            P.navmesh.PathfindAndMoveTo(destination, false);
            PluginLog.Information($"[Farm] 未読込区画探索のため畑ポイントへ移動 Dest={destination} Unresolved={UnresolvedCount()}");
        }
    }

    private static void CompleteActive(string message)
    {
        if (P.navmesh.IsRunning())
            P.navmesh.Stop();
        if (activeSlot >= 0)
            completedSlots.Add(activeSlot);
        PluginLog.Information($"[Farm] {message}");
        activeSlot = -1;
        runStep = RunStep.FindPlot;
        stepStarted = Environment.TickCount64;
        lastMoveRequest = 0;

        if (runQueue.Count <= QueueRefreshRemaining)
            RefreshVisibleQueue();

        status = message;
        if (IsComplete())
            Finish();
    }

    private static void Finish()
    {
        if (P.navmesh.IsRunning())
            P.navmesh.Stop();
        var total = masterTargetSlots.Count;
        masterTargetSlots.Clear();
        completedSlots.Clear();
        runQueue.Clear();
        activeSlot = -1;
        runStep = RunStep.Idle;
        stepStarted = 0;
        lastMoveRequest = 0;
        modeClickSent = false;
        farmMidpointDone = false;
        var completedKind = runKind;
        var completedSeedItem = selectedSeedItem;
        selectedSeedItem = 0;
        runKind = FarmRunKind.Water;
        status = completedKind == FarmRunKind.HarvestAndSow ? "収穫→種まき完了" : "水やり完了";
        if (completedKind == FarmRunKind.HarvestAndSow)
            PluginLog.Information($"[Farm] 収穫→種まき完了: {total}区画 SeedItem={completedSeedItem}");
        else
            PluginLog.Information($"[Farm] 水やり完了: {total}区画");
        RemoveUpdateHookIfIdle();
    }

    private static void ReconcileTargets()
    {
        if (masterTargetSlots.Count == 0)
            return;

        var slots = GetSlots().ToDictionary(x => x.SlotId);
        foreach (var slotId in masterTargetSlots)
        {
            if (completedSlots.Contains(slotId) || slotId == activeSlot)
                continue;
            if (!slots.TryGetValue(slotId, out var slot) || !ShouldProcess(slot))
                completedSlots.Add(slotId);
        }

        runQueue.RemoveAll(x => completedSlots.Contains(x) || x == activeSlot);
    }

    private static void RefreshVisibleQueue()
    {
        if (Svc.Objects.LocalPlayer == null)
            return;

        var slots = GetSlots().ToDictionary(x => x.SlotId);
        var known = new HashSet<int>(runQueue);
        if (activeSlot >= 0)
            known.Add(activeSlot);

        var added = 0;
        foreach (var slotId in masterTargetSlots)
        {
            if (completedSlots.Contains(slotId) || known.Contains(slotId))
                continue;
            if (!slots.TryGetValue(slotId, out var slot) || !ShouldProcess(slot))
                continue;
            if (!TryGetPlotObject(slot, out var obj) || obj == null)
                continue;
            runQueue.Add(slotId);
            added++;
        }

        SortVisibleQueueBySlot();
        if (added > 0)
            PluginLog.Information($"[Farm] 読込済み区画を追加: +{added} Queue={runQueue.Count} Unresolved={UnresolvedCount()}");
    }

    private static void SortVisibleQueueBySlot()
    {
        if (runQueue.Count <= 1)
            return;

        runQueue.Sort();
    }

    private static int UnresolvedCount() => masterTargetSlots.Count(x => !completedSlots.Contains(x) && x != activeSlot);

    private static bool IsComplete() => masterTargetSlots.Count > 0 && completedSlots.Count >= masterTargetSlots.Count;

    private static bool TryGetPlotObject(FarmSlotState slot, out IGameObject? obj)
    {
        obj = null;
        if (slot.LayoutId == 0)
            return false;

        foreach (var candidate in Svc.Objects)
        {
            if (candidate.Address == nint.Zero)
                continue;
            var native = (GameObject*)candidate.Address;
            if (native->LayoutId != slot.LayoutId)
                continue;
            obj = candidate;
            return true;
        }

        return false;
    }
}
