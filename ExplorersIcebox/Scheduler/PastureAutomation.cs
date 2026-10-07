using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Logging;
using ECommons.Throttlers;
using ExplorersIcebox.Config;
using ExplorersIcebox.Enums;
using ExplorersIcebox.Scheduler.Tasks;
using ExplorersIcebox.Util;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using Callback = ECommons.Automation.Callback;

namespace ExplorersIcebox.Scheduler;

/// <summary>
/// Manual-care pasture support. Mammet-managed animals are always excluded.
/// Animal state is read directly from MJI pasture data.
/// Harvest mode reuses the existing v1.1.0.56 implementation.
/// Feed mode uses the game's MJI mode value directly.
/// User-verified in game: CurrentMode 6 = 餌やり.
/// </summary>
internal static unsafe class PastureAutomation
{
    // User-verified game mode number for 餌やり.
    private const uint FeedCurrentMode = 6;
    // 実機確認: ContextIconMenu の index=6 は CurrentMode=7（ふれあい）になった。
    // ゲーム内の並びは 1収穫/2種まき/3水やり/4鋤き返し/5捕獲/6餌やり/7ふれあい/8手招き。
    // 表示項目は0始まりなので、餌やりは index=5。
    private const int FeedContextMenuIndex = 5;
    internal sealed record AnimalState(
        int SlotId,
        uint EntityId,
        string Name,
        string Nickname,
        byte Mood,
        byte FoodLevel,
        bool CanHarvest,
        bool UnderCare,
        bool WasUnderCare,
        bool CareHalted,
        uint AutoFoodItemId);

    private enum RunStep
    {
        Idle,
        FindAnimal,
        SearchPasture,
        MoveToAnimal,
        SetGatherMode,
        HarvestInteract,
        WaitHarvest,
        SetFeedMode,
        FeedInteract,
        WaitFeed,
    }

    private enum FeedSwitchStage
    {
        Idle,
        OpenModeMenu,
        ClickModeButton,
        WaitModeResult,
        CloseModeMenu,
        OpenItemMenu,
        ClickItemButton,
        Verify,
    }

    private enum RegisterStage
    {
        Idle,
        OpenModeMenu,
        CaptureModeButton,
        TryOpenItemMenu,
        CaptureItemButton,
        Save,
    }

    // Master target set is fixed at start. The visible queue is rebuilt from live GameObjects
    // so pasture expansion size and animal roaming do not force a fixed slot-order route.
    private static readonly HashSet<int> masterTargetSlots = [];
    private static readonly HashSet<int> completedSlots = [];
    private static readonly List<int> runQueue = [];
    private static int activeSlot = -1;
    private static RunStep runStep;
    private static long stepStarted;
    private static string status = "停止中";
    private static byte foodBefore;
    private static long pastureApproachArrivedAt;
    private static int pastureApproachStage;
    private static readonly List<int> pastureSearchOrder = [];
    private static int pastureSearchOrderIndex;
    private static Vector3 pastureMoveWatchPosition;
    private static long pastureMoveWatchAt;
    private static bool pastureTransitFallback;
    private static Vector3 pastureLastMoveTarget;
    private static long pastureLastMoveTargetAt;

    // User-verified internal coordinates for the Island Sanctuary pasture.
    // Used only while the target animal is not streamed in yet.
    private static readonly Vector3[] PastureApproachRoute =
    [
        new(-273.336670f, 55.149170f, 131.913727f), // 牧畜エリア入口
        new(-336.048462f, 49.009048f, 147.313736f), // 中間
        new(-391.478882f, 45.229355f, 134.388474f), // 最奥
    ];

    private static bool recording;
    private static bool updateHooked;
    private static uint lastMode = uint.MaxValue;
    private static uint lastModeItem = uint.MaxValue;
    private static readonly Dictionary<int, string> lastAnimalSnapshot = new();

    private static FeedSwitchStage feedSwitchStage;
    private static long feedSwitchStarted;

    private static RegisterStage registerStage;
    private static long registerStarted;
    private static uint registerMode;
    private static uint registerItem;
    private static int registerModeButton = -1;
    private static int registerItemButton = -1;
    private static string registerName = string.Empty;

    internal static bool IsHarvestRunning => runStep != RunStep.Idle;
    internal static bool IsRecording => recording;
    internal static bool IsRegisteringFeed => registerStage != RegisterStage.Idle;
    internal static string Status => status;

    internal static bool IsPastureAvailable
    {
        get
        {
            var mgr = MJIManager.Instance();
            return mgr != null && mgr->IsPlayerInSanctuary && mgr->PastureHandler != null;
        }
    }

    internal static IReadOnlyList<AnimalState> GetAnimals()
    {
        var result = new List<AnimalState>();
        var mgr = MJIManager.Instance();
        if (mgr == null || mgr->PastureHandler == null)
            return result;

        var names = Svc.Data.GetExcelSheet<BNpcName>();
        foreach (ref var animal in mgr->PastureHandler->MJIAnimals)
        {
            if (animal.EntityId == 0 || animal.BNPCNameId == 0)
                continue;

            var name = $"家畜 #{animal.AnimalType}";
            if (names != null && names.TryGetRow(animal.BNPCNameId, out var row))
                name = row.Singular.ToString();

            result.Add(new AnimalState(
                animal.SlotId,
                animal.EntityId,
                name,
                animal.NicknameString,
                animal.Mood,
                animal.FoodLevel,
                animal.ManualLeavingsAvailable,
                animal.UnderCare,
                animal.WasUnderCare,
                animal.CareHalted,
                animal.AutoFoodItemId));
        }

        return result.OrderBy(x => x.SlotId).ToList();
    }

    internal static (uint Mode, uint Item) GetCurrentMode()
    {
        var mgr = MJIManager.Instance();
        return mgr == null ? (0, 0) : (mgr->CurrentMode, mgr->CurrentModeItem);
    }

    internal static string GetItemName(uint itemId)
    {
        if (itemId == 0)
            return "未選択";
        var items = Svc.Data.GetExcelSheet<Item>();
        if (items != null && items.TryGetRow(itemId, out var row))
            return row.Name.ToString();
        return $"Item {itemId}";
    }

    internal static PastureFeedPreset? SelectedFeedPreset
    {
        get
        {
            if (C.PastureSelectedFeedPreset < 0 || C.PastureSelectedFeedPreset >= C.PastureFeedPresets.Count)
                return null;
            return C.PastureFeedPresets[C.PastureSelectedFeedPreset];
        }
    }

    internal static bool RegisterCurrentFeedPreset()
    {
        if (IsHarvestRunning || IsRecording || IsRegisteringFeed)
        {
            status = "別の牧場処理を停止してから登録してください。";
            return false;
        }
        if (!IsPastureAvailable)
        {
            status = "無人島の牧場情報を取得できません。";
            return false;
        }

        var mgr = MJIManager.Instance();
        if (mgr == null || mgr->CurrentMode != FeedCurrentMode || mgr->CurrentModeItem == 0)
        {
            status = $"ゲーム側で餌やりモード（内部Mode={FeedCurrentMode}）と使用する餌を選んでから登録してください。";
            return false;
        }

        registerMode = FeedCurrentMode;
        registerItem = mgr->CurrentModeItem;
        registerName = GetItemName(registerItem);
        registerModeButton = -1;
        registerItemButton = -1;

        // Mode と ItemID はゲーム内部から直接取得できているため、
        // 餌設定そのものはこの時点で保存する。
        // ContextIconMenu の位置は自動切替用の補助情報であり、登録成功条件にはしない。
        var existing = C.PastureFeedPresets.FindIndex(x => x.ItemId == registerItem);
        var preset = new PastureFeedPreset
        {
            Name = registerName,
            Mode = registerMode,
            ItemId = registerItem,
            ModeButtonIndex = -1,
            ItemButtonIndex = -1,
        };
        if (existing >= 0)
        {
            C.PastureFeedPresets[existing] = preset;
            C.PastureSelectedFeedPreset = existing;
        }
        else
        {
            C.PastureFeedPresets.Add(preset);
            C.PastureSelectedFeedPreset = C.PastureFeedPresets.Count - 1;
        }
        C.Save();

        registerStage = RegisterStage.Idle;
        status = $"餌設定を登録しました: {preset.Name}";
        PluginLog.Information($"[PastureFeed] 登録完了 Name={preset.Name} Mode={preset.Mode} Item={preset.ItemId}");
        return true;
    }

    internal static void DeleteSelectedFeedPreset()
    {
        var i = C.PastureSelectedFeedPreset;
        if (i < 0 || i >= C.PastureFeedPresets.Count)
            return;
        C.PastureFeedPresets.RemoveAt(i);
        if (C.PastureFeedPresets.Count == 0)
            C.PastureSelectedFeedPreset = -1;
        else
            C.PastureSelectedFeedPreset = Math.Min(i, C.PastureFeedPresets.Count - 1);
        C.Save();
    }

    internal static bool StartHarvest()
    {
        if (IsHarvestRunning || IsRegisteringFeed || FarmAutomation.IsRunning)
            return false;
        if (SchedulerMain.State != IceBoxState.Idle || P.taskManager.NumQueuedTasks > 0)
        {
            status = "別の自動処理が動作中です。";
            return false;
        }
        if (!IsPastureAvailable)
        {
            status = "無人島の牧場情報を取得できません。";
            return false;
        }
        if (C.PastureFeedAfterHarvest && SelectedFeedPreset == null)
        {
            status = "餌やりを有効にする場合は、先に使用する餌を登録・選択してください。";
            return false;
        }

        masterTargetSlots.Clear();
        completedSlots.Clear();
        runQueue.Clear();
        foreach (var animal in GetAnimals())
        {
            if (!C.PastureTargetSlots.Contains(animal.SlotId))
                continue;
            if (animal.UnderCare)
                continue;

            // 収穫済みでも、餌やり条件に該当する家畜は処理対象にする。
            if (!animal.CanHarvest && !ShouldFeed(animal))
                continue;

            masterTargetSlots.Add(animal.SlotId);
        }

        if (masterTargetSlots.Count == 0)
        {
            status = "対象の中に収穫または餌やりが必要な家畜がいません。";
            return false;
        }

        activeSlot = -1;
        pastureApproachArrivedAt = 0;
        pastureApproachStage = 0;
        pastureSearchOrder.Clear();
        pastureSearchOrderIndex = 0;
        ResetPastureMoveWatch();
        feedSwitchStage = FeedSwitchStage.Idle;
        RefreshVisibleQueue(addNewAnimals: true);
        SetRunStep(RunStep.FindAnimal, $"餌やり→収穫開始: {masterTargetSlots.Count}匹");
        EnsureUpdateHook();
        return true;
    }

    internal static void StopHarvest(string reason = "牧場自動処理を停止しました")
    {
        if (P.navmesh.IsRunning())
            P.navmesh.Stop();
        runStep = RunStep.Idle;
        activeSlot = -1;
        pastureApproachArrivedAt = 0;
        pastureApproachStage = 0;
        pastureSearchOrder.Clear();
        pastureSearchOrderIndex = 0;
        ResetPastureMoveWatch();
        feedSwitchStage = FeedSwitchStage.Idle;
        status = reason;
        ReleaseUpdateHookIfUnused();
    }

    internal static void StartRecording()
    {
        recording = true;
        lastMode = uint.MaxValue;
        lastModeItem = uint.MaxValue;
        lastAnimalSnapshot.Clear();
        PluginLog.Information("[PastureTrace] 記録開始");
        SnapshotForLog(force: true);
        EnsureUpdateHook();
    }

    internal static void StopRecording()
    {
        if (!recording)
            return;
        SnapshotForLog(force: true);
        recording = false;
        PluginLog.Information("[PastureTrace] 記録終了");
        ReleaseUpdateHookIfUnused();
    }

    internal static void LogSnapshot()
    {
        PluginLog.Information("[PastureTrace] ---- 手動スナップショット ----");
        SnapshotForLog(force: true);
    }

    internal static void Dispose()
    {
        recording = false;
        runStep = RunStep.Idle;
        registerStage = RegisterStage.Idle;
        feedSwitchStage = FeedSwitchStage.Idle;
        if (updateHooked)
        {
            Svc.Framework.Update -= OnUpdate;
            updateHooked = false;
        }
    }

    private static void EnsureUpdateHook()
    {
        if (updateHooked)
            return;
        Svc.Framework.Update += OnUpdate;
        updateHooked = true;
    }

    private static void ReleaseUpdateHookIfUnused()
    {
        if (!updateHooked || recording || IsHarvestRunning || IsRegisteringFeed)
            return;
        Svc.Framework.Update -= OnUpdate;
        updateHooked = false;
    }

    private static void OnUpdate(IFramework _)
    {
        try
        {
            if (recording)
                SnapshotForLog(force: false);
            if (IsRegisteringFeed)
                TickFeedRegistration();
            if (IsHarvestRunning)
                TickRun();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[Pasture] 更新処理で例外");
            recording = false;
            registerStage = RegisterStage.Idle;
            StopHarvest("エラーで牧場処理を停止しました");
        }
    }

    private static void SnapshotForLog(bool force)
    {
        var mgr = MJIManager.Instance();
        if (mgr == null)
        {
            if (force)
                PluginLog.Information("[PastureTrace] MJIManager=null");
            return;
        }

        if (force || mgr->CurrentMode != lastMode || mgr->CurrentModeItem != lastModeItem)
        {
            lastMode = mgr->CurrentMode;
            lastModeItem = mgr->CurrentModeItem;
            PluginLog.Information($"[PastureTrace] Mode={lastMode} Item={lastModeItem} InSanctuary={mgr->IsPlayerInSanctuary}");
        }

        foreach (var a in GetAnimals())
        {
            var state = $"Entity={a.EntityId:X8} Food={a.FoodLevel} Mood={a.Mood} Harvest={a.CanHarvest} UnderCare={a.UnderCare} WasCare={a.WasUnderCare} Halted={a.CareHalted} AutoFood={a.AutoFoodItemId}";
            if (force || !lastAnimalSnapshot.TryGetValue(a.SlotId, out var old) || old != state)
            {
                lastAnimalSnapshot[a.SlotId] = state;
                PluginLog.Information($"[PastureTrace] Slot={a.SlotId} Name={DisplayName(a)} {state}");
            }
        }
    }

    private static void TickFeedRegistration()
    {
        if (Environment.TickCount64 - registerStarted > 7000)
        {
            FinishRegistration(false, "餌設定のUI位置を確認できませんでした。");
            return;
        }

        switch (registerStage)
        {
            case RegisterStage.OpenModeMenu:
                if (TryGetContextMenu(out var menu))
                {
                    registerStage = RegisterStage.CaptureModeButton;
                    return;
                }
                OpenIslandModeMenu();
                return;

            case RegisterStage.CaptureModeButton:
                if (!TryGetContextMenu(out menu))
                    return;
                registerModeButton = FindSelectedRadioIndex(menu);
                if (registerModeButton < 0)
                    return;
                PluginLog.Information($"[PastureFeed] Mode button={registerModeButton}");
                if (!ClickContextIconEntry(menu, registerModeButton))
                {
                    FinishRegistration(false, "餌やりモードのボタンを取得できませんでした。");
                    return;
                }
                registerStage = RegisterStage.TryOpenItemMenu;
                registerStarted = Environment.TickCount64;
                return;

            case RegisterStage.TryOpenItemMenu:
                // Some game states keep the previously selected feed item and close the menu immediately.
                // If a second icon menu appears, capture its selected button too; otherwise mode-only calibration is valid.
                if (Environment.TickCount64 - registerStarted < 250)
                    return;
                if (TryGetContextMenu(out menu))
                {
                    registerStage = RegisterStage.CaptureItemButton;
                    return;
                }
                registerStage = RegisterStage.Save;
                return;

            case RegisterStage.CaptureItemButton:
                if (!TryGetContextMenu(out menu))
                {
                    registerStage = RegisterStage.Save;
                    return;
                }
                registerItemButton = FindSelectedRadioIndex(menu);
                CloseContextMenu(menu);
                registerStage = RegisterStage.Save;
                return;

            case RegisterStage.Save:
                FinishRegistration(true, string.Empty);
                return;
        }
    }

    private static void FinishRegistration(bool success, string error)
    {
        if (!success)
        {
            registerStage = RegisterStage.Idle;
            status = error;
            ReleaseUpdateHookIfUnused();
            return;
        }

        var existing = C.PastureFeedPresets.FindIndex(x => x.ItemId == registerItem);
        var preset = new PastureFeedPreset
        {
            Name = registerName,
            Mode = registerMode,
            ItemId = registerItem,
            ModeButtonIndex = registerModeButton,
            ItemButtonIndex = registerItemButton,
        };
        if (existing >= 0)
        {
            C.PastureFeedPresets[existing] = preset;
            C.PastureSelectedFeedPreset = existing;
        }
        else
        {
            C.PastureFeedPresets.Add(preset);
            C.PastureSelectedFeedPreset = C.PastureFeedPresets.Count - 1;
        }
        C.Save();
        PluginLog.Information($"[PastureFeed] 登録完了 Name={preset.Name} Mode={preset.Mode} Item={preset.ItemId} ModeButton={preset.ModeButtonIndex} ItemButton={preset.ItemButtonIndex}");
        registerStage = RegisterStage.Idle;
        status = $"餌設定を登録しました: {preset.Name}";
        ReleaseUpdateHookIfUnused();
    }

    private static void TickRun()
    {
        if (!Player.Available || !IsPastureAvailable)
        {
            StopHarvest("牧場情報を取得できなくなったため停止しました");
            return;
        }

        var animals = GetAnimals();
        var current = activeSlot >= 0 ? animals.FirstOrDefault(x => x.SlotId == activeSlot) : null;

        switch (runStep)
        {
            case RunStep.FindAnimal:
                ResolveCompletedOrInvalidTargets(animals);
                if (completedSlots.Count >= masterTargetSlots.Count)
                {
                    StopHarvest("餌やり→収穫が完了しました");
                    return;
                }

                // Keep the current near-first batch intact. Only when the batch is almost
                // exhausted do we scan unresolved animals again and append newly streamed ones.
                // The selected animal's live Position is still re-read immediately before moving.
                if (runQueue.Count <= 1)
                    RefreshVisibleQueue(addNewAnimals: true);
                if (runQueue.Count == 0)
                {
                    PreparePastureSearch();
                    SetRunStep(RunStep.SearchPasture, "現在位置から見える未処理家畜がないため探索");
                    return;
                }

                activeSlot = runQueue[0];
                runQueue.RemoveAt(0);
                current = animals.FirstOrDefault(x => x.SlotId == activeSlot);
                if (current == null || current.UnderCare || (!current.CanHarvest && !ShouldFeed(current)))
                {
                    CompleteActiveAnimal("収穫・餌やり不要または対象外のため完了扱い");
                    return;
                }
                SetRunStep(RunStep.MoveToAnimal, $"{DisplayName(current)} へ移動");
                return;

            case RunStep.SearchPasture:
                ResolveCompletedOrInvalidTargets(animals);
                if (completedSlots.Count >= masterTargetSlots.Count)
                {
                    StopHarvest("餌やり→収穫が完了しました");
                    return;
                }

                // Search all unresolved EntityIds while moving through the pasture. As soon as
                // even one animal streams in, stop the exploratory move and return to nearest-first processing.
                RefreshVisibleQueue(addNewAnimals: true);
                if (runQueue.Count > 0)
                {
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();
                    pastureApproachArrivedAt = 0;
                    SetRunStep(RunStep.FindAnimal, $"家畜を{runQueue.Count}匹検出・近い順に再リスト");
                    return;
                }

                if (pastureSearchOrder.Count == 0)
                    PreparePastureSearch();
                if (pastureSearchOrderIndex >= pastureSearchOrder.Count)
                {
                    StopHarvest($"未処理家畜{masterTargetSlots.Count - completedSlots.Count}匹の位置を取得できなかったため停止しました");
                    return;
                }

                pastureApproachStage = pastureSearchOrder[pastureSearchOrderIndex];
                var searchPoint = PastureApproachRoute[pastureApproachStage];
                var searchStageName = pastureApproachStage switch
                {
                    0 => "牧畜エリア入口",
                    1 => "牧畜エリア中間",
                    _ => "牧畜エリア最奥",
                };
                var toSearchPoint = Vector3.Distance(Player.Object!.Position, searchPoint);

                if (toSearchPoint > 4f)
                {
                    pastureApproachArrivedAt = 0;
                    status = $"未読込の家畜を探すため{searchStageName}へ移動中";
                    if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress() && EzThrottler.Throttle("PastureApproachMove", 700))
                    {
                        PluginLog.Information($"[Pasture] 未処理家畜が未読込 Stage={pastureApproachStage + 1}/3 {searchStageName}へ探索移動");
                        P.navmesh.PathfindAndMoveTo(searchPoint, false);
                    }
                    return;
                }

                if (pastureApproachArrivedAt == 0)
                {
                    pastureApproachArrivedAt = Environment.TickCount64;
                    PluginLog.Information($"[Pasture] {searchStageName}到着・未処理家畜の読込待ち");
                }

                if (Environment.TickCount64 - pastureApproachArrivedAt > 1500)
                {
                    pastureSearchOrderIndex++;
                    pastureApproachArrivedAt = 0;
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();

                    if (pastureSearchOrderIndex < pastureSearchOrder.Count)
                    {
                        var nextStage = pastureSearchOrder[pastureSearchOrderIndex];
                        PluginLog.Information($"[Pasture] 家畜未読込のため次の探索地点へ RouteStage={nextStage + 1}/3 Search={pastureSearchOrderIndex + 1}/{pastureSearchOrder.Count}");
                    }
                    else
                    {
                        PluginLog.Warning($"[Pasture] 全探索地点で未処理家畜を取得できず Remaining={masterTargetSlots.Count - completedSlots.Count}");
                    }
                }
                return;

            case RunStep.MoveToAnimal:
                if (current == null)
                {
                    CompleteActiveAnimal("家畜情報を取得できず完了扱い");
                    return;
                }
                if (!TryGetAnimalObject(current, out var obj) || obj == null)
                {
                    // A previously visible roaming animal can move out of the streamed object range.
                    // Do not run toward a fixed deep checkpoint for that specific slot; return it to
                    // the unresolved master set and choose from whatever is currently visible.
                    RequeueActiveAnimal("家畜の現在位置を取得できなくなったため再選定");
                    return;
                }

                pastureApproachArrivedAt = 0;
                pastureApproachStage = 0;
                var playerPosition = Player.Object!.Position;
                var distance = Vector3.Distance(playerPosition, obj.Position);
                if (distance <= 2.7f)
                {
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();

                    // User-requested priority: feed first, then harvest.
                    if (ShouldFeed(current))
                    {
                        feedSwitchStage = FeedSwitchStage.Idle;
                        SetRunStep(RunStep.SetFeedMode, "餌やりを優先してモード切替");
                    }
                    else
                    {
                        SetRunStep(RunStep.SetGatherMode, "収穫モードへ切替");
                    }
                    return;
                }

                UpdatePastureMoveWatch(playerPosition);
                if (!pastureTransitFallback && distance > 8f && pastureMoveWatchAt != 0 && Environment.TickCount64 - pastureMoveWatchAt >= 4000)
                {
                    pastureTransitFallback = true;
                    if (P.navmesh.IsRunning())
                        P.navmesh.Stop();
                    PluginLog.Warning($"[Pasture] 移動停滞を検出・牧畜経由へ切替 Slot={current.SlotId} Player={playerPosition} Animal={obj.Position}");
                }

                if (pastureTransitFallback && TryGetPastureTransitPoint(playerPosition, obj.Position, out var transitPoint, out var transitStage))
                {
                    status = $"{DisplayName(current)} へ移動中（停滞回避 {transitStage + 1}/3）";
                    if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress() && EzThrottler.Throttle("PastureRunTransitMove", 700))
                    {
                        PluginLog.Information($"[Pasture] 停滞回避ルート Slot={current.SlotId} Stage={transitStage + 1}/3 Dest={transitPoint} Animal={obj.Position}");
                        P.navmesh.PathfindAndMoveTo(transitPoint, false);
                    }
                    return;
                }

                if (pastureTransitFallback)
                {
                    pastureTransitFallback = false;
                    pastureMoveWatchPosition = playerPosition;
                    pastureMoveWatchAt = Environment.TickCount64;
                    PluginLog.Information($"[Pasture] 停滞回避完了・家畜への直接移動へ復帰 Slot={current.SlotId}");
                }

                // The animal position is live and can change while roaming. If it has moved enough
                // from the destination used for the current route, refresh the short VNav route.
                var targetMoved = pastureLastMoveTargetAt != 0 && Vector3.Distance(pastureLastMoveTarget, obj.Position) >= 2.0f;
                if (targetMoved && Environment.TickCount64 - pastureLastMoveTargetAt >= 1200 && P.navmesh.IsRunning())
                {
                    P.navmesh.Stop();
                    PluginLog.Information($"[Pasture] 家畜移動を検出・最新位置へ経路更新 Slot={current.SlotId} Old={pastureLastMoveTarget} New={obj.Position}");
                }

                if (!P.navmesh.IsRunning() && !P.navmesh.PathfindInProgress() && EzThrottler.Throttle("PastureRunMove", 700))
                {
                    pastureLastMoveTarget = obj.Position;
                    pastureLastMoveTargetAt = Environment.TickCount64;
                    P.navmesh.PathfindAndMoveTo(obj.Position, false);
                }
                return;

            case RunStep.SetGatherMode:
                if (Task_GatherMode.GatherMode() == true)
                    SetRunStep(RunStep.HarvestInteract, "収穫を実行");
                else if (Elapsed > 6000)
                    NextAnimal("収穫モードへ切り替えできずスキップ");
                return;

            case RunStep.HarvestInteract:
                if (current == null)
                {
                    NextAnimal("家畜情報を取得できずスキップ");
                    return;
                }
                if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
                    return;
                if (!TryGetAnimalObject(current, out obj) || obj == null || !obj.IsTargetable)
                {
                    if (Elapsed > 5000)
                        NextAnimal("家畜へ操作できずスキップ");
                    return;
                }
                if (Vector3.Distance(Player.Object!.Position, obj.Position) > 3.5f)
                {
                    SetRunStep(RunStep.MoveToAnimal, "家畜から離れたため再移動");
                    return;
                }

                Utils.TargetgameObject(obj);
                if (EzThrottler.Throttle($"PastureHarvestInteract_{activeSlot}", 800))
                {
                    Utils.InteractWithObject(obj);
                    SetRunStep(RunStep.WaitHarvest, "収穫結果を確認中");
                }
                return;

            case RunStep.WaitHarvest:
                current = GetAnimals().FirstOrDefault(x => x.SlotId == activeSlot);
                if (current == null || !current.CanHarvest)
                {
                    if (current != null && ShouldFeed(current))
                    {
                        feedSwitchStage = FeedSwitchStage.Idle;
                        SetRunStep(RunStep.SetFeedMode, "餌やりモードへ切替");
                    }
                    else
                    {
                        NextAnimal("収穫完了");
                    }
                    return;
                }
                if (Elapsed > 5000)
                    NextAnimal("収穫状態が変化しないためスキップ");
                return;

            case RunStep.SetFeedMode:
                current = GetAnimals().FirstOrDefault(x => x.SlotId == activeSlot);
                if (current == null || current.UnderCare)
                {
                    NextAnimal("給餌対象外のためスキップ");
                    return;
                }
                var feedResult = TickFeedModeSwitch();
                if (feedResult == true)
                {
                    foodBefore = current.FoodLevel;
                    SetRunStep(RunStep.FeedInteract, $"{SelectedFeedPreset!.Name} を与える");
                }
                else if (feedResult == null)
                {
                    StopHarvest("餌やりモードへ安全に切り替えできなかったため停止しました");
                }
                return;

            case RunStep.FeedInteract:
                if (current == null)
                {
                    NextAnimal("家畜情報を取得できずスキップ");
                    return;
                }
                if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
                    return;
                if (!TryGetAnimalObject(current, out obj) || obj == null || !obj.IsTargetable)
                {
                    if (Elapsed > 5000)
                        StopHarvest($"Slot {current.SlotId} へ給餌操作できないため停止しました");
                    return;
                }
                if (Vector3.Distance(Player.Object!.Position, obj.Position) > 3.5f)
                {
                    SetRunStep(RunStep.MoveToAnimal, "家畜から離れたため再移動");
                    return;
                }

                Utils.TargetgameObject(obj);
                if (EzThrottler.Throttle($"PastureFeedInteract_{activeSlot}", 800))
                {
                    Utils.InteractWithObject(obj);
                    SetRunStep(RunStep.WaitFeed, "給餌結果を確認中");
                }
                return;

            case RunStep.WaitFeed:
                current = GetAnimals().FirstOrDefault(x => x.SlotId == activeSlot);
                if (current == null)
                {
                    StopHarvest("給餌後の家畜状態を取得できないため停止しました");
                    return;
                }
                if (current.FoodLevel > foodBefore)
                {
                    if (current.CanHarvest)
                        SetRunStep(RunStep.SetGatherMode, "給餌完了・続けて収穫モードへ切替");
                    else
                        NextAnimal("給餌完了");
                    return;
                }
                if (Elapsed > 5000)
                    StopHarvest($"Slot {current.SlotId} の餌残りが変化しないため停止しました");
                return;
        }
    }

    private static bool ShouldFeed(AnimalState animal)
        => C.PastureFeedAfterHarvest && !animal.UnderCare && animal.FoodLevel <= Math.Clamp(C.PastureFeedThresholdHours, 0, 36);

    /// <summary>
    /// Returns true when the registered feed mode+item is active, false while switching,
    /// and null when the UI cannot be safely matched to the registered preset.
    /// </summary>
    private static bool? TickFeedModeSwitch()
    {
        var preset = SelectedFeedPreset;
        var mgr = MJIManager.Instance();
        if (preset == null || mgr == null || preset.ItemId == 0)
            return null;

        if (mgr->CurrentMode == FeedCurrentMode && mgr->CurrentModeItem == preset.ItemId)
        {
            feedSwitchStage = FeedSwitchStage.Idle;
            return true;
        }

        var feedSwitchTimeout = feedSwitchStage == FeedSwitchStage.ClickItemButton ? 30000 : 6500;
        if (feedSwitchStage != FeedSwitchStage.Idle && Environment.TickCount64 - feedSwitchStarted > feedSwitchTimeout)
        {
            PluginLog.Warning($"[PastureFeed] 餌設定タイムアウト actual=Mode:{mgr->CurrentMode} Item:{mgr->CurrentModeItem} expected=Mode:{FeedCurrentMode} Item:{preset.ItemId} stage={feedSwitchStage}");
            feedSwitchStage = FeedSwitchStage.Idle;
            return null;
        }

        switch (feedSwitchStage)
        {
            case FeedSwitchStage.Idle:
                feedSwitchStage = FeedSwitchStage.OpenModeMenu;
                feedSwitchStarted = Environment.TickCount64;
                return false;

            case FeedSwitchStage.OpenModeMenu:
                if (TryGetContextMenu(out _))
                {
                    feedSwitchStage = FeedSwitchStage.ClickModeButton;
                    return false;
                }
                OpenIslandModeMenu();
                return false;

            case FeedSwitchStage.ClickModeButton:
                if (!TryGetContextMenu(out var menu))
                    return false;

                var beforeMode = mgr->CurrentMode;
                var beforeItem = mgr->CurrentModeItem;
                if (!ClickContextIconEntry(menu, FeedContextMenuIndex))
                    return null;

                PluginLog.Information($"[PastureFeed] 餌やりメニュー選択 index={FeedContextMenuIndex} before=Mode:{beforeMode} Item:{beforeItem}");
                feedSwitchStage = FeedSwitchStage.WaitModeResult;
                feedSwitchStarted = Environment.TickCount64;
                return false;

            case FeedSwitchStage.WaitModeResult:
                if (Environment.TickCount64 - feedSwitchStarted < 250)
                    return false;

                if (mgr->CurrentMode != FeedCurrentMode)
                {
                    if (EzThrottler.Throttle("PastureFeed_WaitMode78", 1000))
                        PluginLog.Information($"[PastureFeed] 餌やりモード待ち actual=Mode:{mgr->CurrentMode} Item:{mgr->CurrentModeItem} expectedMode={FeedCurrentMode}");
                    return false;
                }

                if (mgr->CurrentModeItem == preset.ItemId)
                {
                    feedSwitchStage = FeedSwitchStage.Idle;
                    return true;
                }

                // モード選択用 ContextIconMenu が画面上に残っていることを実機ログで確認済み。
                // そのまま次段へ進むと、餌一覧ではなく旧モード一覧を誤取得するため、
                // upstream Explorers-Icebox / visland と同じく -1 callback で閉じてから次へ進む。
                feedSwitchStage = FeedSwitchStage.CloseModeMenu;
                feedSwitchStarted = Environment.TickCount64;
                return false;

            case FeedSwitchStage.CloseModeMenu:
                if (TryGetContextMenu(out menu))
                {
                    CloseContextMenu(menu);
                    if (EzThrottler.Throttle("PastureFeed_CloseModeMenu78", 1000))
                        PluginLog.Information($"[PastureFeed] モード選択メニューを閉じます actual=Mode:{mgr->CurrentMode} Item:{mgr->CurrentModeItem}");
                    return false;
                }

                feedSwitchStage = FeedSwitchStage.OpenItemMenu;
                feedSwitchStarted = Environment.TickCount64;
                return false;

            case FeedSwitchStage.OpenItemMenu:
                // ここで見つかる ContextIconMenu は、旧モード一覧を閉じた後に
                // MJIHud のアイテム欄から新規に開いたものだけを対象にする。
                if (TryGetContextMenu(out _))
                {
                    feedSwitchStage = FeedSwitchStage.ClickItemButton;
                    feedSwitchStarted = Environment.TickCount64;
                            return false;
                }

                OpenIslandItemMenu();
                if (EzThrottler.Throttle("PastureFeed_WaitItemMenu78", 1000))
                    PluginLog.Information($"[PastureFeed] アイテム欄クリック→餌選択メニュー待ち actual=Mode:{mgr->CurrentMode} Item:{mgr->CurrentModeItem} expectedItem={preset.ItemId}");
                return false;

            case FeedSwitchStage.ClickItemButton:
                if (mgr->CurrentMode == FeedCurrentMode && mgr->CurrentModeItem == preset.ItemId)
                {
                    feedSwitchStage = FeedSwitchStage.Idle;
                            return true;
                }

                if (!TryGetContextMenu(out menu))
                    return false;

                if (!TrySelectFeedItemFromContextMenu(menu, preset.ItemId, out var selectedIndex, out var selectedIconId, out var availableCount))
                {
                    if (EzThrottler.Throttle("PastureFeed_WaitExactFeedMenu", 1000))
                        PluginLog.Information($"[PastureFeed] 餌一覧の確定待ち Item={preset.ItemId} EntryCount={menu->EntryCount}");
                    return false;
                }

                PluginLog.Information($"[PastureFeed] 餌を自動選択 Item={preset.ItemId} index={selectedIndex} icon={selectedIconId} count={availableCount}");
                feedSwitchStage = FeedSwitchStage.Verify;
                feedSwitchStarted = Environment.TickCount64;
                return false;

            case FeedSwitchStage.Verify:
                if (mgr->CurrentMode == FeedCurrentMode && mgr->CurrentModeItem == preset.ItemId)
                {
                    PluginLog.Information($"[PastureFeed] 餌自動設定成功 Mode={mgr->CurrentMode} Item={mgr->CurrentModeItem}");
                    feedSwitchStage = FeedSwitchStage.Idle;
                    return true;
                }

                if (EzThrottler.Throttle("PastureFeed_WaitItem78", 1000))
                    PluginLog.Information($"[PastureFeed] 餌反映待ち actual=Mode:{mgr->CurrentMode} Item:{mgr->CurrentModeItem} expected=Mode:{FeedCurrentMode} Item:{preset.ItemId}");
                return false;

            default:
                feedSwitchStage = FeedSwitchStage.Idle;
                feedSwitchStarted = Environment.TickCount64;
                return false;
        }
    }

    private static bool TryGetAnimalObject(AnimalState animal, out IGameObject? obj)
    {
        // Island Sanctuary pasture animals live in the game's MJI object range.
        // They are not guaranteed to appear in Dalamud's normal ObjectTable enumeration,
        // so first resolve the MJI EntityId directly through the native GameObjectManager.
        var nativeManager = GameObjectManager.Instance();
        if (nativeManager != null)
        {
            var nativeObject = nativeManager->Objects.GetObjectByEntityId(animal.EntityId);
            if (nativeObject != null)
            {
                obj = Svc.Objects.CreateObjectReference((nint)nativeObject);
                if (obj != null)
                {
                    if (EzThrottler.Throttle($"PastureAnimalNative_{animal.SlotId}", 5000))
                        PluginLog.Information($"[Pasture] Slot={animal.SlotId} Native取得成功 Name={animal.Name} MJI={animal.EntityId:X8} Pos={obj.Position}");
                    return true;
                }
            }
        }

        // Fallbacks are kept only for compatibility with cases where the actor is also
        // mirrored into Dalamud's table.
        obj = Svc.Objects.FirstOrDefault(x => x.EntityId == animal.EntityId);
        if (obj != null)
            return true;

        obj = Svc.Objects.FirstOrDefault(x => x.GameObjectId == animal.EntityId);
        if (obj != null)
            return true;

        obj = Svc.Objects.FirstOrDefault(x => unchecked((uint)x.GameObjectId) == animal.EntityId);
        if (obj != null)
            return true;

        if (EzThrottler.Throttle($"PastureAnimalNotFound_{animal.SlotId}", 3000))
        {
            var nativeCount = nativeManager == null ? -1 : nativeManager->Objects.EntityIdSortedCount;
            PluginLog.Warning($"[Pasture] Slot={animal.SlotId} Native家畜取得失敗 Name={animal.Name} MJI={animal.EntityId:X8} NativeCount={nativeCount}");
        }
        return false;
    }

    private static void ResetPastureMoveWatch()
    {
        pastureMoveWatchPosition = default;
        pastureMoveWatchAt = 0;
        pastureTransitFallback = false;
        pastureLastMoveTarget = default;
        pastureLastMoveTargetAt = 0;
    }

    private static void UpdatePastureMoveWatch(Vector3 playerPosition)
    {
        if (pastureMoveWatchAt == 0)
        {
            pastureMoveWatchPosition = playerPosition;
            pastureMoveWatchAt = Environment.TickCount64;
            return;
        }

        if (Vector3.Distance(playerPosition, pastureMoveWatchPosition) >= 1.5f)
        {
            pastureMoveWatchPosition = playerPosition;
            pastureMoveWatchAt = Environment.TickCount64;
        }
    }

    private static bool TryGetPastureTransitPoint(Vector3 playerPosition, Vector3 animalPosition, out Vector3 transitPoint, out int transitStage)
    {
        transitPoint = default;
        transitStage = -1;

        if (Vector3.Distance(playerPosition, animalPosition) < 35f)
            return false;

        var playerStage = GetNearestPastureRouteIndex(playerPosition);
        var animalStage = GetNearestPastureRouteIndex(animalPosition);
        if (playerStage == animalStage)
            return false;

        transitStage = playerStage + Math.Sign(animalStage - playerStage);
        transitPoint = PastureApproachRoute[transitStage];

        // Reaching a checkpoint should immediately allow the next stage to be selected.
        return Vector3.Distance(playerPosition, transitPoint) > 4f;
    }

    private static int GetNearestPastureRouteIndex(Vector3 position)
    {
        var bestIndex = 0;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < PastureApproachRoute.Length; i++)
        {
            var distance = Vector3.Distance(position, PastureApproachRoute[i]);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestIndex = i;
        }
        return bestIndex;
    }

    private static string DisplayName(AnimalState a)
        => string.IsNullOrWhiteSpace(a.Nickname) ? a.Name : $"{a.Nickname} ({a.Name})";

    private static long Elapsed => Environment.TickCount64 - stepStarted;

    private static void SetRunStep(RunStep value, string message)
    {
        runStep = value;
        stepStarted = Environment.TickCount64;
        if (value == RunStep.MoveToAnimal)
            ResetPastureMoveWatch();
        status = message;
        PluginLog.Information($"[Pasture] {message}");
    }

    private static void NextAnimal(string message)
        => CompleteActiveAnimal(message);

    private static void CompleteActiveAnimal(string message)
    {
        if (activeSlot >= 0)
        {
            completedSlots.Add(activeSlot);
            runQueue.Remove(activeSlot);
        }
        PluginLog.Information($"[Pasture] Slot={activeSlot} {message} Progress={completedSlots.Count}/{masterTargetSlots.Count}");
        activeSlot = -1;
        pastureApproachArrivedAt = 0;
        pastureApproachStage = 0;
        pastureSearchOrder.Clear();
        pastureSearchOrderIndex = 0;
        ResetPastureMoveWatch();
        feedSwitchStage = FeedSwitchStage.Idle;
        SetRunStep(RunStep.FindAnimal, message);
    }

    private static void RequeueActiveAnimal(string message)
    {
        PluginLog.Information($"[Pasture] Slot={activeSlot} {message}");
        activeSlot = -1;
        ResetPastureMoveWatch();
        feedSwitchStage = FeedSwitchStage.Idle;
        SetRunStep(RunStep.FindAnimal, message);
    }

    private static void ResolveCompletedOrInvalidTargets(IReadOnlyList<AnimalState> animals)
    {
        foreach (var slot in masterTargetSlots)
        {
            if (completedSlots.Contains(slot) || slot == activeSlot)
                continue;

            var animal = animals.FirstOrDefault(x => x.SlotId == slot);
            if (animal == null)
                continue;

            if (animal.UnderCare || (!animal.CanHarvest && !ShouldFeed(animal)))
            {
                completedSlots.Add(slot);
                runQueue.Remove(slot);
                PluginLog.Information($"[Pasture] Slot={slot} 現在状態では処理不要のため完了扱い Progress={completedSlots.Count}/{masterTargetSlots.Count}");
            }
        }
    }

    private static void PreparePastureSearch()
    {
        pastureSearchOrder.Clear();
        pastureSearchOrderIndex = 0;
        pastureApproachArrivedAt = 0;

        if (Player.Object == null)
            return;

        var playerPosition = Player.Object.Position;
        pastureSearchOrder.AddRange(Enumerable.Range(0, PastureApproachRoute.Length)
            .OrderBy(i => Vector3.Distance(playerPosition, PastureApproachRoute[i])));

        PluginLog.Information($"[Pasture] 探索順を現在地から近い順に設定 [{string.Join(",", pastureSearchOrder.Select(i => i + 1))}]");
    }

    private static void RefreshVisibleQueue(bool addNewAnimals)
    {
        if (Player.Object == null)
            return;

        var animals = GetAnimals();
        var currentPosition = Player.Object.Position;
        var loaded = new Dictionary<int, (AnimalState Animal, float Distance)>();

        foreach (var animal in animals)
        {
            if (!masterTargetSlots.Contains(animal.SlotId) || completedSlots.Contains(animal.SlotId) || animal.SlotId == activeSlot)
                continue;
            if (animal.UnderCare || (!animal.CanHarvest && !ShouldFeed(animal)))
                continue;
            if (!TryGetAnimalObject(animal, out var obj) || obj == null)
                continue;

            loaded[animal.SlotId] = (animal, Vector3.Distance(currentPosition, obj.Position));
        }

        if (!addNewAnimals)
            return;

        // Preserve the current batch order while it is being consumed. A fresh scan is only
        // performed near the end of the batch; then newly streamed animals are added and the
        // remaining batch is rebuilt from the player's current position.
        runQueue.RemoveAll(slot => completedSlots.Contains(slot) || slot == activeSlot || !loaded.ContainsKey(slot));
        foreach (var slot in loaded.Keys)
        {
            if (!runQueue.Contains(slot))
                runQueue.Add(slot);
        }

        runQueue.Sort((a, b) => loaded[a].Distance.CompareTo(loaded[b].Distance));

        if (EzThrottler.Throttle("PastureVisibleQueue", 700))
        {
            var order = string.Join(", ", runQueue.Select(slot => $"{slot}:{loaded[slot].Distance:F1}m"));
            PluginLog.Information($"[Pasture] 現在地基準で候補再リスト Count={runQueue.Count} [{order}] Remaining={masterTargetSlots.Count - completedSlots.Count}");
        }
    }

    private static void OpenIslandModeMenu()
    {
        if (TryGetAddonByName("MJIHud", out AtkUnitBase* hud) && IsAddonReady(hud) && EzThrottler.Throttle("PastureOpenModeMenu", 400))
            Callback.Fire(hud, false, 11, 0);
    }

    private static void OpenIslandItemMenu()
    {
        if (!TryGetAddonByName("MJIHud", out AtkUnitBase* hud) || !IsAddonReady(hud) || !EzThrottler.Throttle("PastureOpenItemMenu", 400))
            return;

        // 実機ノード診断で、MJIHud の右側「アイテム」欄は NodeId=16、
        // ButtonClick EventParam=2 と確認済み。Callback(11,1) は旧ContextIconMenuを
        // 誤取得するケースがあったため、手動クリックと同じButtonClickイベントを送る。
        var node = hud->UldManager.SearchNodeById(16);
        var button = node == null ? null : node->GetAsAtkComponentButton();
        if (button == null)
            return;

        var owner = button->AtkComponentBase.OwnerNode;
        if (owner == null)
            return;

        var evt = (AtkEvent*)owner->AtkResNode.AtkEventManager.Event;
        var guard = 0;
        while (evt != null && guard++ < 16)
        {
            if (evt->State.EventType == AtkEventType.ButtonClick && evt->Param == 2)
            {
                hud->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
                PluginLog.Information("[PastureFeed] MJIHud アイテム欄クリック NodeId=16 EventParam=2");
                return;
            }
            evt = evt->NextEvent;
        }
    }

    private static bool TryGetContextMenu(out AddonContextIconMenu* menu)
    {
        menu = null;
        if (!TryGetAddonByName("ContextIconMenu", out AtkUnitBase* raw) || !IsAddonReady(raw) || !raw->IsVisible)
            return false;
        menu = (AddonContextIconMenu*)raw;
        return true;
    }

    private static void CloseContextMenu(AddonContextIconMenu* menu)
    {
        if (menu != null)
            Callback.Fire((AtkUnitBase*)menu, true, -1);
    }

    private static int FindSelectedRadioIndex(AddonContextIconMenu* menu)
    {
        var max = Math.Clamp(menu->EntryCount, 0, 10);
        for (var i = 0; i < max; i++)
        {
            var radio = GetRadio(menu, i);
            if (radio != null && radio->IsSelected)
                return i;
        }
        return -1;
    }



    private static bool TrySelectFeedItemFromContextMenu(AddonContextIconMenu* menu, uint itemId, out int selectedIndex, out uint itemIconId, out uint availableCount)
    {
        selectedIndex = -1;
        itemIconId = 0;
        availableCount = 0;

        if (menu == null || itemId is not (37612 or 37613 or 37614))
            return false;

        var raw = (AtkUnitBase*)menu;
        if (raw->AtkValues == null || raw->AtkValuesCount < 33)
            return false;

        // 2026-10-06 実機ログで、実際の餌選択 ContextIconMenu は
        // AtkValue[4]=3、3件の餌が8値間隔で並び、
        // [11+8*i]=IconId / [12+8*i]=ItemId / [16+8*i]=所持数。
        // 旧モード一覧は AtkValue[4]=9 なので、この条件で誤クリックを防ぐ。
        var entryCountValue = &raw->AtkValues[4];
        if (entryCountValue->Type != AtkValueType.UInt || entryCountValue->UInt != 3)
            return false;

        var allFeedEntriesValid = true;
        for (var i = 0; i < 3; i++)
        {
            var iconValue = &raw->AtkValues[11 + (i * 8)];
            var itemValue = &raw->AtkValues[12 + (i * 8)];
            var countValue = &raw->AtkValues[16 + (i * 8)];
            if (iconValue->Type != AtkValueType.UInt || itemValue->Type != AtkValueType.UInt || countValue->Type != AtkValueType.UInt ||
                itemValue->UInt is not (37612 or 37613 or 37614))
            {
                allFeedEntriesValid = false;
                break;
            }

            if (itemValue->UInt == itemId)
            {
                selectedIndex = i;
                itemIconId = iconValue->UInt;
                availableCount = countValue->UInt;
            }
        }

        if (!allFeedEntriesValid || selectedIndex < 0 || itemIconId == 0 || availableCount == 0)
            return false;

        // 実際の餌一覧は ContextIconMenu 内の AtkComponentList240 に表示されている。
        // callback 引数を推測せず、ゲームUIのリスト項目クリックそのものを送る。
        var list = menu->AtkComponentList240;
        if (list == null || selectedIndex < 0 || selectedIndex >= list->ListLength)
            return false;

        list->DispatchItemEvent(selectedIndex, AtkEventType.ListItemClick);
        PluginLog.Information($"[PastureFeed] 餌リスト項目クリック Item={itemId} index={selectedIndex} icon={itemIconId} count={availableCount} ListLength={list->ListLength}");
        return true;
    }


    private static bool ClickContextIconEntry(AddonContextIconMenu* menu, int index)
    {
        if (menu == null || index < 0 || index >= Math.Clamp(menu->EntryCount, 0, 10))
            return false;

        // ContextIconMenu is selected by its addon callback, not by calling Click() on the radio button.
        // This matches the callback pattern already used by established Dalamud plugins.
        Callback.Fire((AtkUnitBase*)menu, true, 0, index, 0, 0, 0);
        return true;
    }

    private static AtkComponentRadioButton* GetRadio(AddonContextIconMenu* menu, int index)
    {
        if (menu == null)
            return null;
        return index switch
        {
            0 => menu->AtkComponentRadioButton250,
            1 => menu->AtkComponentRadioButton258,
            2 => menu->AtkComponentRadioButton260,
            3 => menu->AtkComponentRadioButton268,
            4 => menu->AtkComponentRadioButton270,
            5 => menu->AtkComponentRadioButton278,
            6 => menu->AtkComponentRadioButton280,
            7 => menu->AtkComponentRadioButton288,
            8 => menu->AtkComponentRadioButton290,
            9 => menu->AtkComponentRadioButton298,
            _ => null,
        };
    }
}
