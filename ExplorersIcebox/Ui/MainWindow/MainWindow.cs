using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Colors;
using ECommons.GameHelpers;
using ExplorersIcebox.Enums;
using ExplorersIcebox.Config;
using ExplorersIcebox.Scheduler;
using ExplorersIcebox.Scheduler.Tasks;
using ExplorersIcebox.IPC;
using ExplorersIcebox.Util;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;
namespace ExplorersIcebox.Ui.MainWindow;

internal class MainWindow : Window
{
    private readonly List<string> modeSelect = ["地上EXP周回", "飛行EXP周回", "素材収集"];
    private int selectedModeIndex = C.ModeSelected;
    private string materialSearch = string.Empty;
    private string inventorySearch = string.Empty;
    private int bulkInventoryKeepAmount = C.MinimumItemKeep;
    private int selectedTravelPoint = -1;
    private string newTravelPointName = string.Empty;
    private bool showTravelPathPreview = false;
    private int previewTravelPoint = -1;
    private readonly List<List<Vector3>> travelPreviewSegments = new();
    private string travelPreviewStatus = string.Empty;
    private int travelPreviewGeneration = 0;
    private Vector3 travelPreviewStart;

    private Vector2 toramemoTitleBarLinkPos;
    private Vector2 toramemoTitleBarLinkMin;
    private Vector2 toramemoTitleBarLinkMax;
    private bool toramemoTitleBarLinkLayoutValid;
    private const string ToramemoTopUrl = "https://toramemoblog.com/";

    // Table column widths follow the user's standard UI behavior:
    // resizing one column never steals width from the columns to its right.
    // The table grows horizontally and uses horizontal scrolling when needed.
    private readonly float[] travelPointColumnWidths = [74f, 150f, 310f, 112f, 64f];
    private readonly float[] travelWaypointColumnWidths = [64f, 235f, 84f, 64f];
    private readonly float[] gatherItemColumnWidths = [92f, 220f, 62f, 132f, 110f, 70f];
    private readonly float[] inventoryColumnWidths = [240f, 82f, 110f, 110f];
    private readonly float[] collectionPlanColumnWidths = [68f, 220f, 70f, 110f, 70f, 250f, 58f];

    public int selectedRoute = C.routeSelected;
    public MainWindow() :
        base($"Explorer's Icebox 日本語版 {P.GetType().Assembly.GetName().Version} ###Explorer'sIceboxMainWindow")
    {
        Flags = ImGuiWindowFlags.None;
        SizeConstraints = new()
        {
            MinimumSize = new(520, 420),
            MaximumSize = new(2000, 2000)
        };
        P.windowSystem.AddWindow(this);
        AllowPinning = false;
    }
    private List<string> routeNames => EmbedRoutes.Routes.Keys.OrderBy(name => ExtractNumber(name)).ToList();

    public void Dispose() { }

    private static int ExtractNumber(string input)
    {
        var match = Regex.Match(input, @"\d+");
        return match.Success ? int.Parse(match.Value) : int.MinValue;
    }

    private static bool CanUseQuickTravel(out string reason)
    {
        if (PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed)
        {
            reason = "牧場処理中は使用できません。停止してから実行してください。";
            return false;
        }
        if (SchedulerMain.State != IceBoxState.Idle)
        {
            reason = "自動周回中は使用できません。停止してから実行してください。";
            return false;
        }
        if (P.taskManager.NumQueuedTasks > 0)
        {
            reason = "別の処理を実行中です。完了してから実行してください。";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static string PositionText(Vector3? pos)
        => pos == null ? "未登録" : $"X:{pos.Value.X:F1}  Y:{pos.Value.Y:F1}  Z:{pos.Value.Z:F1}";

    private static void EnsureWaypointIds(TravelPointPreset preset)
    {
        preset.WaypointIds ??= new();
        if (preset.NextWaypointId < 1) preset.NextWaypointId = 1;

        while (preset.WaypointIds.Count > preset.Waypoints.Count)
            preset.WaypointIds.RemoveAt(preset.WaypointIds.Count - 1);

        var used = new HashSet<int>(preset.WaypointIds.Where(x => x > 0));
        while (preset.WaypointIds.Count < preset.Waypoints.Count)
        {
            while (used.Contains(preset.NextWaypointId)) preset.NextWaypointId++;
            preset.WaypointIds.Add(preset.NextWaypointId);
            used.Add(preset.NextWaypointId);
            preset.NextWaypointId++;
        }

        if (preset.WaypointIds.Count > 0)
            preset.NextWaypointId = Math.Max(preset.NextWaypointId, preset.WaypointIds.Max() + 1);
    }

    private static int AddWaypoint(TravelPointPreset preset, Vector3 position)
    {
        EnsureWaypointIds(preset);
        var id = preset.NextWaypointId++;
        preset.Waypoints.Add(position);
        preset.WaypointIds.Add(id);
        return id;
    }

    private static void MigrateLegacyTravelPoints()
    {
        if (C.TravelPoints.Count > 0 || (C.FarmPosition == null && C.PasturePosition == null))
            return;

        if (C.FarmPosition != null)
            C.TravelPoints.Add(new TravelPointPreset { Name = "畑", Destination = C.FarmPosition, UseMount = true });
        if (C.PasturePosition != null)
            C.TravelPoints.Add(new TravelPointPreset { Name = "牧草地", Destination = C.PasturePosition, UseMount = true });

        C.FarmPosition = null;
        C.PasturePosition = null;
        C.Save();
    }

    private static bool ColoredSmallButton(string label, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(
            MathF.Min(color.X + 0.12f, 1f), MathF.Min(color.Y + 0.12f, 1f), MathF.Min(color.Z + 0.12f, 1f), color.W));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(
            MathF.Max(color.X - 0.10f, 0f), MathF.Max(color.Y - 0.10f, 0f), MathF.Max(color.Z - 0.10f, 0f), color.W));
        var result = ImGui.SmallButton(label);
        ImGui.PopStyleColor(3);
        return result;
    }

    private static bool ColoredButton(string label, Vector2 size, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(
            MathF.Min(color.X + 0.12f, 1f), MathF.Min(color.Y + 0.12f, 1f), MathF.Min(color.Z + 0.12f, 1f), color.W));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(
            MathF.Max(color.X - 0.10f, 0f), MathF.Max(color.Y - 0.10f, 0f), MathF.Max(color.Z - 0.10f, 0f), color.W));
        var result = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return result;
    }

    private static readonly ImGuiTableFlags UserTableFlags =
        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
        ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoKeepColumnsVisible | ImGuiTableFlags.ScrollX;

    private static float TextColumnWidth(IEnumerable<string> values, float minimum, float padding = 28f)
    {
        var max = minimum;
        foreach (var value in values)
            max = MathF.Max(max, ImGui.CalcTextSize(value ?? string.Empty).X + padding);
        return max;
    }


    private void ClearTravelPreview()
    {
        travelPreviewGeneration++;
        showTravelPathPreview = false;
        previewTravelPoint = -1;
        travelPreviewSegments.Clear();
        travelPreviewStatus = string.Empty;
    }

    private async void BuildTravelPreview(int pointIndex)
    {
        var generation = ++travelPreviewGeneration;
        showTravelPathPreview = false;
        travelPreviewSegments.Clear();
        travelPreviewStatus = string.Empty;
        previewTravelPoint = pointIndex;

        if (pointIndex < 0 || pointIndex >= C.TravelPoints.Count)
        {
            travelPreviewStatus = "対象ポイントがありません。";
            return;
        }
        if (!P.navmesh.IsReady())
        {
            travelPreviewStatus = "vnavmesh の準備が完了していません。";
            return;
        }

        var preset = C.TravelPoints[pointIndex];
        if (preset.Destination == null)
        {
            travelPreviewStatus = "目的地が未登録です。";
            return;
        }

        Vector3 start;
        if (preset.UseIslandReturn)
            start = IslandHelper.BaseStart;
        else if (Player.Available)
            start = Player.Object!.Position;
        else
        {
            travelPreviewStatus = "現在地を取得できません。";
            return;
        }

        travelPreviewStart = start;
        EnsureWaypointIds(preset);

        var targets = new List<Vector3>(preset.Waypoints);
        targets.Add(preset.Destination.Value);
        var calculatedSegments = new List<List<Vector3>>();
        travelPreviewStatus = $"VNA経路を計算中... 0 / {targets.Count}";

        for (var i = 0; i < targets.Count; i++)
        {
            try
            {
                var path = await P.navmesh.Pathfind(start, targets[i], false);
                if (generation != travelPreviewGeneration)
                    return;

                if (path == null || path.Count == 0)
                {
                    travelPreviewSegments.Clear();
                    travelPreviewStatus = $"区間 {i + 1} のVNA経路を取得できませんでした。";
                    return;
                }

                calculatedSegments.Add(path);
                start = targets[i];
                travelPreviewStatus = $"VNA経路を計算中... {i + 1} / {targets.Count}";
            }
            catch (Exception ex)
            {
                if (generation != travelPreviewGeneration)
                    return;
                travelPreviewSegments.Clear();
                travelPreviewStatus = $"VNA経路取得エラー: {ex.Message}";
                return;
            }
        }

        if (generation != travelPreviewGeneration)
            return;

        travelPreviewSegments.AddRange(calculatedSegments);
        showTravelPathPreview = true;
        travelPreviewStatus = $"VNA経路表示中: {travelPreviewSegments.Count} 区間";
    }

    private void RenderTravelPreview()
    {
        if (!showTravelPathPreview || previewTravelPoint != selectedTravelPoint || selectedTravelPoint < 0 || selectedTravelPoint >= C.TravelPoints.Count)
            return;

        var preset = C.TravelPoints[selectedTravelPoint];
        EnsureWaypointIds(preset);

        for (var i = 0; i < travelPreviewSegments.Count; i++)
            SplatoonManager.RenderPath(travelPreviewSegments[i], i == 0, false);

        // User-defined markers are intentionally separate from vnavmesh's generated red path nodes.
        SplatoonManager.RenderMarker(travelPreviewStart, "S", ImGuiColors.HealerGreen.ToUint(), 0.42f);
        for (var i = 0; i < preset.Waypoints.Count && i < preset.WaypointIds.Count; i++)
            SplatoonManager.RenderMarker(preset.Waypoints[i], $"#{preset.WaypointIds[i]}", ImGuiColors.DalamudYellow.ToUint(), 0.42f);

        if (preset.Destination != null)
            SplatoonManager.RenderMarker(preset.Destination.Value, $"G: {preset.Name}", ImGuiColors.DalamudYellow.ToUint(), 0.50f);
    }

    private void DrawTravelPointEditor(int pointIndex, bool canTravel, float editorWidth = 0f)
    {
        if (pointIndex < 0 || pointIndex >= C.TravelPoints.Count)
            return;

        var selected = C.TravelPoints[pointIndex];
        EnsureWaypointIds(selected);

        var editorHeight = 235f + Math.Max(1, selected.Waypoints.Count) * (ImGui.GetTextLineHeightWithSpacing() + 5f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.08f, 0.88f));
        if (ImGui.BeginChild($"TravelPointEditor_{pointIndex}", new(editorWidth, editorHeight), true))
        {
            ImGui.TextUnformatted($"設定: {selected.Name}");
            ImGui.SameLine();
            if (ColoredSmallButton($"閉じる##TravelClose_{pointIndex}", new Vector4(0.35f, 0.35f, 0.35f, 1f)))
            {
                selectedTravelPoint = -1;
                ClearTravelPreview();
                ImGui.EndChild();
                ImGui.PopStyleColor();
                return;
            }

            var editName = selected.Name;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("名前", ref editName, 40))
            {
                selected.Name = editName;
                C.Save();
            }

            ImGui.TextDisabled("目的地・チョコボ・アイルデジョンは一覧の「目的地設定」列から変更できます。");

            ImGui.Spacing();
            ImGui.TextUnformatted("経由地");
            ImGui.SameLine();
            using (ImRaii.Disabled(!Player.Available || !canTravel))
            {
                if (ColoredButton("現在地を経由地に追加", new(170, 0), new Vector4(0.10f, 0.42f, 0.72f, 1f)))
                {
                    AddWaypoint(selected, Player.Object!.Position);
                    C.Save();
                    if (showTravelPathPreview) BuildTravelPreview(pointIndex);
                }
            }

            if (selected.Waypoints.Count == 0)
            {
                ImGui.TextDisabled("経由地なし");
            }
            else
            {
                var tableStart = ImGui.GetCursorScreenPos();
                if (ImGui.BeginTable($"TravelWaypoints_{pointIndex}", 4, UserTableFlags))
                {
                    ImGui.TableSetupColumn("番号", ImGuiTableColumnFlags.WidthFixed, travelWaypointColumnWidths[0]);
                    ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthFixed, travelWaypointColumnWidths[1]);
                    ImGui.TableSetupColumn("並替", ImGuiTableColumnFlags.WidthFixed, travelWaypointColumnWidths[2]);
                    ImGui.TableSetupColumn("削除", ImGuiTableColumnFlags.WidthFixed, travelWaypointColumnWidths[3]);
                    ImGui.TableHeadersRow();
                    var autoWidths = new float[]
                    {
                        64f,
                        TextColumnWidth(selected.Waypoints.Select(x => PositionText(x)), 180f),
                        84f,
                        64f
                    };

                for (var i = 0; i < selected.Waypoints.Count; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"#{selected.WaypointIds[i]}");
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(PositionText(selected.Waypoints[i]));
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(i == 0))
                    {
                        if (ColoredSmallButton($"↑##WpUp_{pointIndex}_{i}", new Vector4(0.24f, 0.34f, 0.60f, 1f)))
                        {
                            (selected.Waypoints[i - 1], selected.Waypoints[i]) = (selected.Waypoints[i], selected.Waypoints[i - 1]);
                            (selected.WaypointIds[i - 1], selected.WaypointIds[i]) = (selected.WaypointIds[i], selected.WaypointIds[i - 1]);
                            C.Save();
                            if (showTravelPathPreview) BuildTravelPreview(pointIndex);
                            break;
                        }
                    }
                    ImGui.SameLine();
                    using (ImRaii.Disabled(i >= selected.Waypoints.Count - 1))
                    {
                        if (ColoredSmallButton($"↓##WpDown_{pointIndex}_{i}", new Vector4(0.24f, 0.34f, 0.60f, 1f)))
                        {
                            (selected.Waypoints[i + 1], selected.Waypoints[i]) = (selected.Waypoints[i], selected.Waypoints[i + 1]);
                            (selected.WaypointIds[i + 1], selected.WaypointIds[i]) = (selected.WaypointIds[i], selected.WaypointIds[i + 1]);
                            C.Save();
                            if (showTravelPathPreview) BuildTravelPreview(pointIndex);
                            break;
                        }
                    }
                    ImGui.TableNextColumn();
                    if (ColoredSmallButton($"削除##WpDelete_{pointIndex}_{i}", new Vector4(0.62f, 0.18f, 0.18f, 1f)))
                    {
                        selected.Waypoints.RemoveAt(i);
                        selected.WaypointIds.RemoveAt(i);
                        C.Save();
                        if (showTravelPathPreview) BuildTravelPreview(pointIndex);
                        break;
                    }
                }
                    ImGui.EndTable();
                }
            }

            ImGui.Spacing();
            using (ImRaii.Disabled(selected.Destination == null || !P.navmesh.IsReady()))
            {
                if (!showTravelPathPreview)
                {
                    if (ColoredButton("VNA経路を確認", new(150, 0), new Vector4(0.55f, 0.36f, 0.08f, 1f)))
                        BuildTravelPreview(pointIndex);
                }
                else
                {
                    if (ColoredButton("VNA経路表示を消す", new(150, 0), new Vector4(0.35f, 0.35f, 0.35f, 1f)))
                        ClearTravelPreview();
                }
            }
            ImGui.SameLine();
            if (!string.IsNullOrEmpty(travelPreviewStatus))
                ImGui.TextDisabled(travelPreviewStatus);

            ImGui.TextDisabled("経路確認は設定を開いている間だけ表示します。経由地を追加・並べ替えすると自動再計算します。");
            ImGui.TextDisabled("経由地の #番号 は固定IDです。↑↓で実行順を変えても、リストと3D表示の番号は変わりません。");
            ImGui.TextDisabled("実行順: アイルデジョン(ON時) → チョコボ(ON時) → 経由地 → 目的地");

            ImGui.Spacing();
            using (ImRaii.Disabled(!canTravel || selected.Destination == null))
            {
                if (ColoredButton($"「{selected.Name}」へ行く", new(180, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f)))
                    Task_QuickTravel.Enqueue(selected);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!canTravel))
            {
                if (ColoredButton("このポイントを削除", new(150, 0), new Vector4(0.62f, 0.18f, 0.18f, 1f)))
                {
                    C.TravelPoints.RemoveAt(pointIndex);
                    selectedTravelPoint = -1;
                    ClearTravelPreview();
                    C.Save();
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawQuickTravel()
    {
        MigrateLegacyTravelPoints();
        RenderTravelPreview();

        var canTravel = CanUseQuickTravel(out var disabledReason);
        using (ImRaii.Disabled(!canTravel))
        {
            if (ColoredButton("アイルデジョン", new(120, 0), new Vector4(0.10f, 0.42f, 0.72f, 1f)))
            {
                P.navmesh.Stop();
                Task_ReturnToBase.Enqueue(false);
            }
            ImGui.SameLine();
            if (ColoredButton("無人島へ戻る", new(140, 0), new Vector4(0.10f, 0.42f, 0.72f, 1f)))
            {
                P.navmesh.Stop();
                P.lifestream.ExecuteCommand("island");
            }
        }
        if (!canTravel)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(disabledReason);
        }

        // 指定ポイント機能は互換性維持のためソースと設定を残し、公開UIからは非表示。
        // 既存データは削除しない。
        var showRegisteredTravelPoints = false;
        if (!showRegisteredTravelPoints)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted("指定ポイント");
        ImGui.Separator();

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##NewTravelPointName", ref newTravelPointName, 40);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newTravelPointName)))
        {
            if (ColoredButton("新規追加", new(90, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f)))
            {
                C.TravelPoints.Add(new TravelPointPreset { Name = newTravelPointName.Trim() });
                selectedTravelPoint = C.TravelPoints.Count - 1;
                newTravelPointName = string.Empty;
                ClearTravelPreview();
                C.Save();
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled("名前を自由に入力して登録");

        if (C.TravelPoints.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("指定ポイントはまだ登録されていません。");
            ImGui.TextDisabled("例: 畑 / 牧草地 / 工房前 / お気に入り地点");
            return;
        }

        if (selectedTravelPoint >= C.TravelPoints.Count)
            selectedTravelPoint = C.TravelPoints.Count - 1;

        ImGui.Spacing();

        // One ImGui table owns both the header and every row. This is important:
        // native column resizing / right-click auto-fit then changes the real columns,
        // instead of trying to synchronize several independent tables.
        var flags = UserTableFlags | ImGuiTableFlags.NoClip;
        if (ImGui.BeginTable("TravelPointTable_v2", 5, flags))
        {
            ImGui.TableSetupColumn("順序", ImGuiTableColumnFlags.WidthFixed, travelPointColumnWidths[0]);
            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthFixed, travelPointColumnWidths[1]);
            ImGui.TableSetupColumn("目的地設定", ImGuiTableColumnFlags.WidthFixed, travelPointColumnWidths[2]);
            ImGui.TableSetupColumn("移動", ImGuiTableColumnFlags.WidthFixed, travelPointColumnWidths[3]);
            ImGui.TableSetupColumn("編集", ImGuiTableColumnFlags.WidthFixed, travelPointColumnWidths[4]);
            ImGui.TableHeadersRow();

            for (var i = 0; i < C.TravelPoints.Count; i++)
            {
                var point = C.TravelPoints[i];
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                using (ImRaii.Disabled(i == 0 || Task_QuickTravel.IsRunning))
                {
                    if (ColoredSmallButton($"↑##TravelUp_{i}", new Vector4(0.24f, 0.34f, 0.60f, 1f)))
                    {
                        (C.TravelPoints[i - 1], C.TravelPoints[i]) = (C.TravelPoints[i], C.TravelPoints[i - 1]);
                        if (selectedTravelPoint == i) selectedTravelPoint = i - 1;
                        else if (selectedTravelPoint == i - 1) selectedTravelPoint = i;
                        ClearTravelPreview();
                        C.Save();
                        break;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(i >= C.TravelPoints.Count - 1 || Task_QuickTravel.IsRunning))
                {
                    if (ColoredSmallButton($"↓##TravelDown_{i}", new Vector4(0.24f, 0.34f, 0.60f, 1f)))
                    {
                        (C.TravelPoints[i + 1], C.TravelPoints[i]) = (C.TravelPoints[i], C.TravelPoints[i + 1]);
                        if (selectedTravelPoint == i) selectedTravelPoint = i + 1;
                        else if (selectedTravelPoint == i + 1) selectedTravelPoint = i;
                        ClearTravelPreview();
                        C.Save();
                        break;
                    }
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(point.Name);

                ImGui.TableSetColumnIndex(2);
                using (ImRaii.Disabled(!Player.Available || !canTravel))
                {
                    if (ColoredSmallButton($"現在地を登録##TravelDestination_{i}", new Vector4(0.10f, 0.42f, 0.72f, 1f)))
                    {
                        if (point.Destination == null)
                        {
                            point.Destination = Player.Object!.Position;
                            C.Save();
                            if (showTravelPathPreview && selectedTravelPoint == i) BuildTravelPreview(i);
                        }
                        else
                        {
                            ImGui.OpenPopup($"目的地上書き確認##TravelDestinationOverwrite_{i}");
                        }
                    }
                }
                ImGui.SameLine();
                var rowUseMount = point.UseMount;
                if (ImGui.Checkbox($"チョコボ##TravelMount_{i}", ref rowUseMount))
                {
                    point.UseMount = rowUseMount;
                    C.Save();
                }
                ImGui.SameLine();
                var rowUseReturn = point.UseIslandReturn;
                if (ImGui.Checkbox($"デジョン##TravelReturn_{i}", ref rowUseReturn))
                {
                    point.UseIslandReturn = rowUseReturn;
                    C.Save();
                    if (showTravelPathPreview && selectedTravelPoint == i) BuildTravelPreview(i);
                }

                ImGui.TableSetColumnIndex(3);
                var isThisPointRunning = Task_QuickTravel.IsRunningFor(point);
                using (ImRaii.Disabled(!canTravel || point.Destination == null))
                {
                    if (ColoredSmallButton($"行く##TravelGo_{i}", new Vector4(0.10f, 0.48f, 0.24f, 1f)))
                        Task_QuickTravel.Enqueue(point);
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!isThisPointRunning))
                {
                    if (ColoredSmallButton($"STOP##TravelStop_{i}", new Vector4(0.62f, 0.18f, 0.18f, 1f)))
                        Task_QuickTravel.Stop();
                }

                ImGui.TableSetColumnIndex(4);
                var editing = selectedTravelPoint == i;
                if (ColoredSmallButton($"{(editing ? "閉じる" : "設定")}##TravelEdit_{i}", new Vector4(0.55f, 0.36f, 0.08f, 1f)))
                {
                    if (editing)
                    {
                        selectedTravelPoint = -1;
                        ClearTravelPreview();
                    }
                    else
                    {
                        selectedTravelPoint = i;
                        ClearTravelPreview();
                    }
                }

                if (ImGui.BeginPopup($"目的地上書き確認##TravelDestinationOverwrite_{i}"))
                {
                    ImGui.TextUnformatted("現在の目的地を上書きしますか？");
                    if (ColoredButton($"はい##OverwriteDestinationYes_{i}", new(80, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f)))
                    {
                        if (Player.Available)
                        {
                            point.Destination = Player.Object!.Position;
                            C.Save();
                            if (showTravelPathPreview && selectedTravelPoint == i) BuildTravelPreview(i);
                        }
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ColoredButton($"いいえ##OverwriteDestinationNo_{i}", new(80, 0), new Vector4(0.35f, 0.35f, 0.35f, 1f)))
                        ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }

                if (selectedTravelPoint == i)
                {
                    // Inline editor row. NoClip lets this child span the full table width
                    // while remaining physically between this row and the next row.
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var editorWidth = travelPointColumnWidths.Sum() + ImGui.GetStyle().CellPadding.X * 10f;
                    DrawTravelPointEditor(i, canTravel, editorWidth);
                    RenderTravelPreview();
                }
            }

            ImGui.EndTable();
        }
    }


    private void DrawNormalGather()
    {
        selectedModeIndex = Math.Clamp(C.ModeSelected, 0, modeSelect.Count - 1);

        ImGui.TextUnformatted("自動採集");
        ImGui.Separator();
        ImGui.TextWrapped("本家の通常採集機能です。素材一覧の「追加」にチェックすると、収集リストへ登録できます。");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("モードを選択", modeSelect[selectedModeIndex]))
        {
            for (var i = 0; i < modeSelect.Count; i++)
            {
                var isSelected = (i == selectedModeIndex);
                if (ImGui.Selectable(modeSelect[i], isSelected))
                {
                    C.ModeSelected = i;
                    selectedModeIndex = i;
                    C.Save();
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var DisableSelection = selectedModeIndex < modeSelect.Count - 1;
        var selectedRouteIndex = C.routeSelected;

        if (selectedModeIndex == 2)
        {
            ImGui.SetNextItemWidth(240);
            ImGui.InputText("素材検索", ref materialSearch, 64);
            if (!string.IsNullOrWhiteSpace(materialSearch))
            {
                var query = materialSearch.Trim();
                var matches = ItemData.IslandItems
                    .Select(x => new
                    {
                        ItemId = x.Key,
                        English = x.Value.ItemName,
                        Japanese = JapaneseUi.ItemName(x.Key, x.Value.ItemName),
                        BestRoute = CollectionPlan.FindBestRoute(x.Key)
                    })
                    .Where(x => x.BestRoute != null &&
                        (x.Japanese.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                         x.English.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(x => x.Japanese)
                    .Take(12)
                    .ToList();

                if (matches.Count == 0)
                {
                    ImGui.TextDisabled("対応ルートのある素材が見つかりません。");
                }
                else
                {
                    var height = Math.Min(matches.Count, 6) * ImGui.GetTextLineHeightWithSpacing() + 8;
                    if (ImGui.BeginChild("MaterialSearchResults", new(0, height), true))
                    {
                        foreach (var match in matches)
                        {
                            var best = match.BestRoute!.Value;
                            var label = $"{match.Japanese}  →  {JapaneseUi.RouteName(best.Route.Key)}##MaterialSearch_{match.ItemId}";
                            if (ImGui.Selectable(label))
                            {
                                selectedRouteIndex = best.Index;
                                C.routeSelected = best.Index;
                                C.Save();
                            }
                        }
                    }
                    ImGui.EndChild();
                }
                ImGui.TextDisabled("素材名を選ぶと、その素材を採れる既存ルートを自動選択します。");
            }

            using (ImRaii.Disabled(DisableSelection))
            {
                ImGui.SetNextItemWidth(240);
                if (ImGui.BeginCombo("ルートを選択", JapaneseUi.RouteName(routeNames[selectedRouteIndex])))
                {
                    for (var i = 0; i < routeNames.Count; i++)
                    {
                        var isSelected = (i == selectedRouteIndex);
                        if (ImGui.Selectable(JapaneseUi.RouteName(routeNames[i]), isSelected))
                        {
                            selectedRouteIndex = i;
                            C.routeSelected = i;
                            C.Save();
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
        }

        if (selectedModeIndex == 0) selectedRouteIndex = 7;
        else if (selectedModeIndex == 1) selectedRouteIndex = 18;

        var routeSelected = EmbedRoutes.Routes.Where(x => x.Key == routeNames[selectedRouteIndex]).FirstOrDefault();
        if (!EmbedRoutes.Routes.ContainsKey(routeSelected.Key)) return;

        Dictionary<string, IslandHelper.ItemGathered> routeItems = new();
        Dictionary<string, HashSet<ItemData.GatheringNode>> itemNodeMap = new();
        IslandHelper.CurrentRoute = routeSelected;

        foreach (var wp in routeSelected.Value.RouteWaypoints)
        {
            if (wp.TargetId == 0) continue;
            var Node = ItemData.IslandNodeInfo.Where(x => x.Nodes.Contains(wp.TargetId)).FirstOrDefault();
            if (Node == null) continue;
            foreach (var item in Node.ItemIds)
            {
                var itemName = ItemData.IslandItems[item].ItemName;
                if (!routeItems.ContainsKey(itemName))
                    routeItems[itemName] = new() { Amount = 1, ItemId = item, GatherNodes = { Node.GatherName }, IgnoreNode = false };
                else
                {
                    routeItems[itemName].Amount += 1;
                    routeItems[itemName].GatherNodes.Add(Node.GatherName);
                }
                if (!itemNodeMap.ContainsKey(itemName)) itemNodeMap[itemName] = new();
                itemNodeMap[itemName].Add(Node);
            }
        }

        foreach (var kvp in routeItems)
        {
            if (!itemNodeMap.TryGetValue(kvp.Key, out var nodes)) continue;
            kvp.Value.IgnoreNode = nodes.Count > 1 && nodes.All(n => n.ItemIds.Count > 1);
        }

        IslandHelper.UpdateCounters(routeItems);

        var DisableButtons = IslandHelper.GoalLoopAmount > IslandHelper.MaxRouteLoops || IslandHelper.MaxRouteLoops == 0 || IslandHelper.GoalLoopAmount == 0;
        var isRunning = SchedulerMain.State != IceBoxState.Idle;

        var pastureBusy = PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed || FarmAutomation.IsRunning;
        using (ImRaii.Disabled(DisableButtons || isRunning || pastureBusy))
        {
            if (ColoredButton("開始", new(120, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f))) SchedulerMain.EnablePlugin();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(DisableButtons || !isRunning))
        {
            if (ColoredButton("停止", new(120, 0), new Vector4(0.62f, 0.18f, 0.18f, 1f))) SchedulerMain.DisablePlugin();
        }
        ImGui.SameLine();
        ImGui.Text($"周回予定: {IslandHelper.GoalLoopAmount} / 最大: {IslandHelper.MaxRouteLoops}");
        if (isRunning && IslandHelper.CurrentRouteLoopTotal > 0)
        {
            ImGui.SameLine();
            ImGui.Text($"現在: {IslandHelper.CurrentRouteLoop} / {IslandHelper.CurrentRouteLoopTotal}周目");
        }

        if (DisableButtons)
        {
            ImGui.SameLine();
            ImGuiEx.IconWithTooltip(FontAwesomeIcon.ExclamationTriangle, "現在の設定では安全に実行できません。\n手元に残す素材数、または収集数を調整してください。");
        }
        ImGui.Spacing();
        ImGui.TextUnformatted("周回設定");
        ImGui.Separator();

        var SkipSell = C.SkipSell;
        if (ImGui.Checkbox("素材を売却しない", ref SkipSell)) { C.SkipSell = SkipSell; C.Save(); }

        var RunMaxLoops = C.RunMaxLoops;
        if (ImGui.Checkbox("可能な最大回数まで周回", ref RunMaxLoops)) { C.RunMaxLoops = RunMaxLoops; C.Save(); }
        ImGuiEx.HelpMarker("現在の素材所持数と残しておく数から計算し、可能な最大回数まで周回します。\n素材ごとの収集数を指定せず、できるだけ多く集めたい場合に便利です。");
        if (RunMaxLoops) IslandHelper.GoalLoopAmount = IslandHelper.MaxRouteLoops;

        var runMultiple = C.RunMultiple;
        if (ImGui.Checkbox("周回を繰り返す", ref runMultiple)) { C.RunMultiple = runMultiple; C.Save(); }
        if (runMultiple)
        {
            ImGui.SameLine();
            var RunAmount = C.RunAmount;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("###RunMultipleAmount", ref RunAmount)) { C.RunAmount = RunAmount; C.Save(); }
            ImGui.SameLine(); ImGui.Text("回");
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("素材ごとの収集設定", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var gatherTableStart = ImGui.GetCursorScreenPos();
            if (ImGui.BeginTable("Gathered items", 6, UserTableFlags))
            {
                ImGui.TableSetupColumn("収集リスト", ImGuiTableColumnFlags.WidthFixed, gatherItemColumnWidths[0]);
                ImGui.TableSetupColumn("素材", ImGuiTableColumnFlags.WidthFixed, gatherItemColumnWidths[1]);
                ImGui.TableSetupColumn("1周", ImGuiTableColumnFlags.WidthFixed, gatherItemColumnWidths[2]);
                ImGui.TableSetupColumn("周回計算から除外", ImGuiTableColumnFlags.WidthFixed, gatherItemColumnWidths[3]);
                ImGui.TableSetupColumn("収集目標", ImGuiTableColumnFlags.WidthFixed, gatherItemColumnWidths[4]);
                ImGui.TableSetupColumn("現在数", ImGuiTableColumnFlags.WidthFixed, gatherItemColumnWidths[5]);
                ImGui.TableHeadersRow();
                var gatherAutoWidths = new float[]
                {
                    92f,
                    TextColumnWidth(routeItems.Values.Select(x => JapaneseUi.ItemName(x.ItemId, ItemData.IslandItems[x.ItemId].ItemName)), 160f),
                    62f, 132f, 110f, 70f
                };

                foreach (var item in routeItems)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var inCollection = C.CollectionTargets.ContainsKey(item.Value.ItemId);
                    if (ImGui.Checkbox($"##Collect_{item.Value.ItemId}", ref inCollection))
                    {
                        if (inCollection)
                        {
                            PlayerHelper.GetItemCount(item.Value.ItemId, out var currentForTarget);
                            CollectionPlan.AddOrUpdate(item.Value.ItemId, Math.Min(999, currentForTarget + 100));
                        }
                        else
                        {
                            CollectionPlan.Remove(item.Value.ItemId);
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted("追加");

                    ImGui.TableNextColumn();
                    ImGui.Text(JapaneseUi.ItemName(item.Value.ItemId, item.Key));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Item ID: {item.Value.ItemId}");

                    ImGui.TableNextColumn(); ImGui.Text($"{item.Value.Amount}");
                    ImGui.TableNextColumn(); Utils.FancyCheckmark(item.Value.IgnoreNode);
                    ImGui.TableNextColumn();
                    var GatherAmount = C.ItemGatherAmount[item.Key];
                    ImGui.SetNextItemWidth(-1);
                    using (ImRaii.Disabled(RunMaxLoops))
                    {
                        if (ImGui.SliderInt($"###GatherAmount_{item.Key}", ref GatherAmount, 0, 999))
                        {
                            C.ItemGatherAmount[item.Key] = GatherAmount;
                            C.Save();
                        }
                    }
                    ImGui.TableNextColumn();
                    if (PlayerHelper.GetItemCount(item.Value.ItemId, out var count)) ImGui.Text($"{count}");
                }
                ImGui.EndTable();
            }
        }

#if DEBUG
        foreach (var item in IslandHelper.SellItems)
            ImGui.Text($"{ItemData.IslandItems[item.Key].ItemName} | {item.Value}");
#endif
    }

    private void DrawInventorySettings()
    {
        ImGui.TextUnformatted("在庫 / 売却設定");
        ImGui.Separator();
        ImGui.TextWrapped("売却時の素材個数を設定します。");
        ImGui.TextWrapped("現在所持している無人島素材を表示し、設定した保持数を超えた分を売却します。");
        ImGui.Spacing();

        var skipSell = C.SkipSell;
        if (ImGui.Checkbox("素材を売却しない", ref skipSell))
        {
            C.SkipSell = skipSell;
            C.Save();
        }

        var defaultKeep = C.MinimumItemKeep;
        using (ImRaii.Disabled(skipSell))
        {
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("未設定素材の保持数", ref defaultKeep, 0, 0))
            {
                C.MinimumItemKeep = Math.Clamp(defaultKeep, 0, 999);
                C.Save();
                IslandHelper.UpdateCounters(IslandHelper.RouteItems);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("個別の保持数を設定していない素材に使う既定値です。");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("一括保持数", ref bulkInventoryKeepAmount, 0, 0);
        bulkInventoryKeepAmount = Math.Clamp(bulkInventoryKeepAmount, 0, 999);
        ImGui.SameLine();
        using (ImRaii.Disabled(skipSell))
        {
            if (ImGui.Button("全在庫に適用"))
            {
                foreach (var kv in ItemData.IslandItems)
                {
                    if (PlayerHelper.GetItemCount(kv.Key, out var count) && count > 0)
                        C.ItemKeepAmount[kv.Value.ItemName] = bulkInventoryKeepAmount;
                }
                C.Save();
                IslandHelper.UpdateCounters(IslandHelper.RouteItems);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("現在所持している無人島素材すべてに同じ保持数を設定します。");

        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##InventorySearch", "素材検索", ref inventorySearch, 80);
        ImGui.SameLine();
        if (ImGui.Button("検索クリア")) inventorySearch = string.Empty;

        var ownedItems = ItemData.IslandItems
            .Where(kv => PlayerHelper.GetItemCount(kv.Key, out var count) && count > 0)
            .Select(kv =>
            {
                PlayerHelper.GetItemCount(kv.Key, out var count);
                return new { ItemId = kv.Key, Info = kv.Value, Count = count, Display = JapaneseUi.ItemName(kv.Key, kv.Value.ItemName) };
            })
            .Where(x => string.IsNullOrWhiteSpace(inventorySearch) ||
                        x.Display.Contains(inventorySearch, StringComparison.OrdinalIgnoreCase) ||
                        x.Info.ItemName.Contains(inventorySearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Display)
            .ToList();

        ImGui.Spacing();
        if (ownedItems.Count == 0)
        {
            ImGui.TextDisabled("表示できる所持素材がありません。");
            return;
        }

        var sellKinds = 0;
        var sellTotal = 0;
        foreach (var item in ownedItems)
        {
            if (ItemData.AlwaysIgnoreSell.Contains(item.ItemId))
                continue;
            var keep = IslandHelper.GetKeepAmount(item.Info.ItemName);
            var surplus = Math.Max(0, item.Count - keep);
            if (surplus > 0)
            {
                sellKinds++;
                sellTotal += surplus;
            }
        }

        var canImmediateSell = !skipSell && sellKinds > 0 && SchedulerMain.State == IceBoxState.Idle && P.taskManager.NumQueuedTasks == 0 && !CollectionPlan.IsRunning;
        using (ImRaii.Disabled(!canImmediateSell))
        {
            if (ImGui.Button("今すぐ売却", new(120, 0)))
                ImGui.OpenPopup("今すぐ売却の確認");
        }
        ImGui.SameLine();
        if (skipSell)
            ImGui.TextDisabled("自動売却OFF");
        else if (sellKinds == 0)
            ImGui.TextDisabled("売却対象なし");
        else
            ImGui.TextDisabled($"{sellKinds}種類 / 合計{sellTotal}個");

        if (ImGui.BeginPopupModal("今すぐ売却の確認", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("本当に売りますか？");
            ImGui.TextDisabled($"保持数を超えた {sellKinds}種類 / 合計{sellTotal}個を売却します。");
            ImGui.TextDisabled("拠点へ戻って売却し、完了後は拠点へ戻って停止します。");
            ImGui.Spacing();
            if (ImGui.Button("売却する", new(110, 0)))
            {
                ImGui.CloseCurrentPopup();
                Task_ImmediateSell.Start();
            }
            ImGui.SameLine();
            if (ImGui.Button("キャンセル", new(110, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        var tableStart = ImGui.GetCursorScreenPos();
        if (ImGui.BeginTable("InventoryKeepTable", 4, UserTableFlags))
        {
            ImGui.TableSetupColumn("素材", ImGuiTableColumnFlags.WidthFixed, inventoryColumnWidths[0]);
            ImGui.TableSetupColumn("現在数", ImGuiTableColumnFlags.WidthFixed, inventoryColumnWidths[1]);
            ImGui.TableSetupColumn("保持数", ImGuiTableColumnFlags.WidthFixed, inventoryColumnWidths[2]);
            ImGui.TableSetupColumn("売却予定", ImGuiTableColumnFlags.WidthFixed, inventoryColumnWidths[3]);
            ImGui.TableHeadersRow();

            var autoWidths = new float[]
            {
                TextColumnWidth(ownedItems.Select(x => x.Display), 180f),
                82f, 110f, 110f
            };

            foreach (var item in ownedItems)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(item.Display);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Item ID: {item.ItemId}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Count.ToString());

                ImGui.TableNextColumn();
                var keepAmount = IslandHelper.GetKeepAmount(item.Info.ItemName);
                ImGui.SetNextItemWidth(-1);
                using (ImRaii.Disabled(skipSell))
                {
                    if (ImGui.InputInt($"##InventoryKeep_{item.ItemId}", ref keepAmount, 0, 0))
                    {
                        keepAmount = Math.Clamp(keepAmount, 0, 999);
                        C.ItemKeepAmount[item.Info.ItemName] = keepAmount;
                        C.Save();
                        IslandHelper.UpdateCounters(IslandHelper.RouteItems);
                    }
                }

                ImGui.TableNextColumn();
                var overflow = Math.Max(0, item.Count - keepAmount);
                if (skipSell)
                    ImGui.TextDisabled("売却OFF");
                else if (overflow > 0)
                    ImGui.TextUnformatted(overflow.ToString());
                else
                    ImGui.TextDisabled("0");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("※ 売却は採集中ではなく、拠点へ戻って売却処理に入ったタイミングで行います。");
    }

    private void DrawCollectionList()
    {
        ImGui.TextUnformatted("収集リスト");
        ImGui.Separator();
        ImGui.TextWrapped("通常採集で追加した素材を、目標所持数まで順番に収集します。");
        ImGui.TextWrapped("登録素材を目標数まで集めます。コース内の他素材も採取します。開始時の売却は行いません。");
        ImGui.TextWrapped("1つのコースが終わるたびにアイルデジョンで拠点へ戻り、次の素材へ進みます。");
        ImGui.Spacing();

        var running = CollectionPlan.IsRunning;
        var hasEntries = C.CollectionTargets.Count > 0;
        var hasWork = !running && CollectionPlan.HasWork();

        using (ImRaii.Disabled(!hasWork || SchedulerMain.State != IceBoxState.Idle || P.taskManager.NumQueuedTasks > 0))
        {
            if (ColoredButton("収集リストを開始", new(160, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f)))
                CollectionPlan.Start();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!running))
        {
            if (ColoredButton("収集を停止", new(120, 0), new Vector4(0.62f, 0.18f, 0.18f, 1f)))
                SchedulerMain.DisablePlugin();
        }

        if (running && CollectionPlan.CurrentItemId is int currentId && ItemData.IslandItems.TryGetValue(currentId, out var currentInfo))
        {
            ImGui.SameLine();
            ImGui.Text($"実行中: {JapaneseUi.ItemName(currentId, currentInfo.ItemName)}");
        }

        if (!hasEntries)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("まだ素材が登録されていません。通常採集タブの素材一覧で「追加」にチェックしてください。");
            return;
        }

        ImGui.Spacing();
        var collectionTableStart = ImGui.GetCursorScreenPos();
        if (ImGui.BeginTable("CollectionPlanTable", 7, UserTableFlags))
        {
            ImGui.TableSetupColumn("順番", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[0]);
            ImGui.TableSetupColumn("素材", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[1]);
            ImGui.TableSetupColumn("現在数", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[2]);
            ImGui.TableSetupColumn("目標数", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[3]);
            ImGui.TableSetupColumn("残り", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[4]);
            ImGui.TableSetupColumn("自動ルート", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[5]);
            ImGui.TableSetupColumn("削除", ImGuiTableColumnFlags.WidthFixed, collectionPlanColumnWidths[6]);
            ImGui.TableHeadersRow();
            var collectionAutoWidths = new float[]
            {
                68f,
                TextColumnWidth(C.CollectionOrder.Select(id => JapaneseUi.ItemName(id, ItemData.IslandItems[id].ItemName)), 160f),
                70f, 110f, 70f,
                TextColumnWidth(C.CollectionOrder.Select(id => CollectionPlan.FindBestRoute(id)?.Route.Key is string r ? JapaneseUi.RouteName(r) : "未対応"), 180f),
                58f
            };

            foreach (var itemId in C.CollectionOrder.ToList())
            {
                if (!C.CollectionTargets.TryGetValue(itemId, out var target) || !ItemData.IslandItems.TryGetValue(itemId, out var info))
                    continue;

                PlayerHelper.GetItemCount(itemId, out var current);
                var remaining = Math.Max(0, target - current);
                var route = CollectionPlan.FindBestRoute(itemId);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                using (ImRaii.Disabled(running || C.CollectionOrder.IndexOf(itemId) <= 0))
                {
                    if (ImGui.SmallButton($"↑##CollectionUp_{itemId}"))
                    {
                        CollectionPlan.MoveOrder(itemId, -1);
                        break;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(running || C.CollectionOrder.IndexOf(itemId) >= C.CollectionOrder.Count - 1))
                {
                    if (ImGui.SmallButton($"↓##CollectionDown_{itemId}"))
                    {
                        CollectionPlan.MoveOrder(itemId, 1);
                        break;
                    }
                }

                ImGui.TableNextColumn();
                ImGui.Text(JapaneseUi.ItemName(itemId, info.ItemName));

                ImGui.TableNextColumn();
                ImGui.Text($"{current}");

                ImGui.TableNextColumn();
                var editTarget = target;
                ImGui.SetNextItemWidth(-1);
                using (ImRaii.Disabled(running))
                {
                    if (ImGui.InputInt($"##CollectionTarget_{itemId}", ref editTarget, 10, 100))
                    {
                        editTarget = Math.Clamp(editTarget, 1, 999);
                        CollectionPlan.AddOrUpdate(itemId, editTarget);
                    }
                }

                ImGui.TableNextColumn();
                ImGui.Text(remaining == 0 ? "完了" : remaining.ToString());

                ImGui.TableNextColumn();
                if (route != null)
                    ImGui.Text(JapaneseUi.RouteName(route.Value.Route.Key));
                else
                    ImGui.TextDisabled("対応ルートなし");

                ImGui.TableNextColumn();
                using (ImRaii.Disabled(running))
                {
                    if (ImGui.Button($"削除##CollectionRemove_{itemId}"))
                    {
                        CollectionPlan.Remove(itemId);
                        break;
                    }
                }
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("※ 目標数は「追加で採る数」ではなく、採集後に持っていたい最終所持数です。目標以上になれば次の素材へ進みます。");
        ImGui.TextDisabled("※ 収集リスト実行中は、設定済み目標を売却で減らさないため自動売却を一時停止し、終了時に元の設定へ戻します。");
    }

    private void DrawFarm()
    {
        ImGui.TextUnformatted("畑（水やり）");
        ImGui.Separator();
        if (ImGui.CollapsingHeader("使い方・注意事項##FarmHelp"))
        {
            ImGui.TextWrapped("現在は水やりのみ対応しています。対象にしたい畑へチェックを入れてから実行してください。Slot 0 ～ 19 を順番に回ります。");
            ImGui.TextWrapped("ゲーム内の畑番号は1からですが、プラグイン上のSlot番号は0から始まります。");
            ImGui.TextColored(new Vector4(1.00f, 0.72f, 0.20f, 1f), "注意");
            ImGui.SameLine();
            ImGui.TextWrapped("マメット管理中の区画は対象外です。マメットの管理・操作は行いません。");
            ImGui.TextColored(new Vector4(1.00f, 0.72f, 0.20f, 1f), "注意");
            ImGui.SameLine();
            ImGui.TextWrapped("本機能はランク20の環境で開発しています。開拓状況によっては未確認の状態があります。申し訳ありません。");
            ImGui.TextWrapped("収穫・種まきは現在未実装です。ご要望があれば実装を検討します。");
            ImGui.TextWrapped("水分値はゲーム内部の値をそのまま表示します。");
        }
        ImGui.Spacing();

        if (!FarmAutomation.IsFarmAvailable)
        {
            ImGui.TextDisabled("畑情報を取得できません。自分の無人島へ入ってから確認してください。");
            return;
        }

        var slots = FarmAutomation.GetSlots();
        ImGui.TextUnformatted($"現在モード: {FarmAutomation.GetCurrentMode()}（水やり=3）");

        var threshold = C.FarmWaterLevelThreshold;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("水やり対象の水分値以下", ref threshold))
        {
            C.FarmWaterLevelThreshold = Math.Clamp(threshold, 0, 255);
            C.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("初期値 0。判定にはゲーム内部値を使います。");

        ImGui.Spacing();
        var otherBusy = SchedulerMain.State != IceBoxState.Idle
            || P.taskManager.NumQueuedTasks > 0
            || PastureAutomation.IsHarvestRunning
            || PastureAutomation.IsRecording
            || PastureAutomation.IsRegisteringFeed;

        if (!FarmAutomation.IsRunning)
        {
            using (ImRaii.Disabled(otherBusy))
            {
                if (ColoredButton("選択区画の水やりを開始", new(180, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f)))
                    FarmAutomation.StartWatering();
            }
        }
        else
        {
            if (ColoredButton("水やりを停止", new(130, 0), new Vector4(0.62f, 0.18f, 0.18f, 1f)))
                FarmAutomation.Stop("ユーザーが停止しました");
        }
        ImGui.SameLine();
        ImGui.TextDisabled(FarmAutomation.Status);

        ImGui.Spacing();
        using (ImRaii.Disabled(FarmAutomation.IsRunning))
        {
            if (ImGui.Button("対象区画をすべて選択##FarmSelectAll"))
            {
                foreach (var slot in slots.Where(x => !x.UnderCare && x.SeedType != 0))
                    C.FarmWaterTargetSlots.Add(slot.SlotId);
                C.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("選択解除##FarmSelectNone"))
            {
                C.FarmWaterTargetSlots.Clear();
                C.Save();
            }
        }

        var farmTableHeight = MathF.Max(220f, ImGui.GetContentRegionAvail().Y - 8f);
        var farmTableFlags = UserTableFlags | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("FarmWaterTable", 8, farmTableFlags, new Vector2(0, farmTableHeight)))
        {
            ImGui.TableSetupColumn("対象", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 48f);
            ImGui.TableSetupColumn("種ID", ImGuiTableColumnFlags.WidthFixed, 58f);
            ImGui.TableSetupColumn("水分値", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("成長値", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("収穫量", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("管理", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("判定", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var slot in slots)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = C.FarmWaterTargetSlots.Contains(slot.SlotId);
                using (ImRaii.Disabled(slot.UnderCare || slot.SeedType == 0 || FarmAutomation.IsRunning))
                {
                    if (ImGui.Checkbox($"##FarmTarget_{slot.SlotId}", ref selected))
                    {
                        if (selected) C.FarmWaterTargetSlots.Add(slot.SlotId);
                        else C.FarmWaterTargetSlots.Remove(slot.SlotId);
                        C.Save();
                    }
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(slot.SlotId.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(slot.SeedType == 0 ? "空" : slot.SeedType.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(slot.WaterLevel.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(slot.GrowthLevel.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(slot.YieldAvailable.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(slot.UnderCare ? "マメット管理" : "プラグイン対象");
                ImGui.TableNextColumn();
                var needsWater = slot.SeedType != 0 && !slot.UnderCare && slot.YieldAvailable == 0 && slot.WaterLevel <= C.FarmWaterLevelThreshold;
                ImGui.TextUnformatted(needsWater ? "水やり対象" : "対象外");
            }

            ImGui.EndTable();
        }
    }

    private void DrawPasture()
    {
        ImGui.TextUnformatted("牧場（餌やり・収穫）");
        ImGui.Separator();
        if (ImGui.CollapsingHeader("使い方・注意事項##PastureHelp"))
        {
            ImGui.TextWrapped("1. ゲーム内の無人島モードを「餌やり」にし、与えたい餌を選択してください。");
            ImGui.TextWrapped("2. 「現在の餌設定を登録」を押して、選択中の餌を記憶します。");
            ImGui.TextWrapped("3. 次回からは「使用する餌」から餌を選び、対象の動物を選択して実行してください。");
            ImGui.TextColored(new Vector4(1.00f, 0.72f, 0.20f, 1f), "注意");
            ImGui.SameLine();
            ImGui.TextWrapped("実行ボタンは牧場の柵の内側に入ってから押してください。入口から少し入った位置での開始をおすすめします。");
            ImGui.TextWrapped("マメット管理中の家畜は処理対象から除外します。");
            ImGui.TextWrapped("動物ごとの個別餌設定や、餌やり／収穫を動物ごとに個別設定する機能は現在ありません。ご要望があれば検討します。");
        }
        ImGui.Spacing();

        if (!PastureAutomation.IsPastureAvailable)
        {
            ImGui.TextDisabled("牧場情報を取得できません。自分の無人島へ入ってから確認してください。");
            return;
        }

        var animals = PastureAutomation.GetAnimals();
        var mode = PastureAutomation.GetCurrentMode();
        ImGui.TextUnformatted($"現在モード: {mode.Mode}    選択アイテムID: {mode.Item}");

        ImGui.Spacing();
        ImGui.TextUnformatted("餌やり設定");
        ImGui.Separator();

        var feedAfterHarvest = C.PastureFeedAfterHarvest;
        if (ImGui.Checkbox("餌やりを行う", ref feedAfterHarvest))
        {
            C.PastureFeedAfterHarvest = feedAfterHarvest;
            C.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("餌残りが指定時間以下の家畜だけ給餌します。");

        var threshold = C.PastureFeedThresholdHours;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("給餌する餌残り時間以下", ref threshold))
        {
            C.PastureFeedThresholdHours = Math.Clamp(threshold, 0, 36);
            C.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("時間");

        var selectedPreset = PastureAutomation.SelectedFeedPreset;
        var preview = selectedPreset?.Name ?? "未登録";
        ImGui.SetNextItemWidth(280f);
        if (ImGui.BeginCombo("使用する餌", preview))
        {
            for (var i = 0; i < C.PastureFeedPresets.Count; i++)
            {
                var preset = C.PastureFeedPresets[i];
                var selected = i == C.PastureSelectedFeedPreset;
                if (ImGui.Selectable($"{preset.Name}  (Item {preset.ItemId})##FeedPreset_{i}", selected))
                {
                    C.PastureSelectedFeedPreset = i;
                    C.Save();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        using (ImRaii.Disabled(PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed))
        {
            if (ImGui.Button("現在の餌設定を登録"))
                PastureAutomation.RegisterCurrentFeedPreset();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(selectedPreset == null || PastureAutomation.IsHarvestRunning || PastureAutomation.IsRegisteringFeed))
        {
            if (ImGui.Button("選択中の餌登録を削除"))
                PastureAutomation.DeleteSelectedFeedPreset();
        }

        if (selectedPreset != null)
            ImGui.TextUnformatted($"登録餌: {selectedPreset.Name} / Mode={selectedPreset.Mode} / Item={selectedPreset.ItemId}");

        ImGui.Spacing();
        ImGui.TextUnformatted("処理");
        ImGui.Separator();

        if (!PastureAutomation.IsHarvestRunning)
        {
            using (ImRaii.Disabled(PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed))
            {
                var startLabel = C.PastureFeedAfterHarvest ? "餌やり → 収穫を開始" : "収穫を開始";
                if (ColoredButton(startLabel, new(180, 0), new Vector4(0.10f, 0.48f, 0.24f, 1f)))
                    PastureAutomation.StartHarvest();
            }
        }
        else
        {
            if (ColoredButton("牧場処理を停止", new(140, 0), new Vector4(0.62f, 0.18f, 0.18f, 1f)))
                PastureAutomation.StopHarvest("ユーザーが停止しました");
        }
        ImGui.SameLine();
        ImGui.TextDisabled(PastureAutomation.Status);

        ImGui.Spacing();
        if (ImGui.Button("対象の動物をすべて選択"))
        {
            foreach (var a in animals.Where(x => !x.UnderCare))
                C.PastureTargetSlots.Add(a.SlotId);
            C.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("選択解除"))
        {
            C.PastureTargetSlots.Clear();
            C.Save();
        }

        var pastureTableHeight = MathF.Max(220f, ImGui.GetContentRegionAvail().Y - 8f);
        var pastureTableFlags = UserTableFlags | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("PastureAnimalTable", 7, pastureTableFlags, new Vector2(0, pastureTableHeight)))
        {
            ImGui.TableSetupColumn("対象", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("家畜", ImGuiTableColumnFlags.WidthFixed, 210f);
            ImGui.TableSetupColumn("機嫌", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("餌残り", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableSetupColumn("収穫", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("管理", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var a in animals)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = C.PastureTargetSlots.Contains(a.SlotId);
                using (ImRaii.Disabled(a.UnderCare || PastureAutomation.IsHarvestRunning || PastureAutomation.IsRecording || PastureAutomation.IsRegisteringFeed))
                {
                    if (ImGui.Checkbox($"##PastureTarget_{a.SlotId}", ref selected))
                    {
                        if (selected) C.PastureTargetSlots.Add(a.SlotId);
                        else C.PastureTargetSlots.Remove(a.SlotId);
                        C.Save();
                    }
                }

                ImGui.TableNextColumn();
                var displayName = string.IsNullOrWhiteSpace(a.Nickname) ? a.Name : $"{a.Nickname} ({a.Name})";
                ImGui.TextUnformatted(displayName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.Mood switch
                {
                    4 => "とても良い",
                    3 => "良い",
                    2 => "普通",
                    1 => "悪い",
                    _ => "とても悪い"
                });

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{a.FoodLevel}時間");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.CanHarvest ? "回収可能" : "なし");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.UnderCare ? "マメット管理" : "プラグイン対象");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.SlotId.ToString());
            }

            ImGui.EndTable();
        }
    }

    private void DrawHelp()
    {
        ImGui.TextUnformatted("ヘルプ");
        ImGui.Separator();

        ImGui.TextWrapped("毎日の無人島作業の手間を、少しだけお手伝いするためのプラグインです。すべてを完全自動化するものではなく、採取・畑・牧場などの日常作業を補助します。");
        ImGui.Spacing();
        ImGui.TextWrapped("本家 Explorers-Icebox で提供されている自動化機能は通常採集です。ExplorersIceboxJP では、それをベースに収集リスト・畑・牧場などの補助機能を追加しています。");
        ImGui.Spacing();

        ImGui.TextUnformatted("必要プラグイン");
        ImGui.BulletText("Lifestream：「無人島へ戻る」機能で使用します。");
        ImGui.BulletText("vnavmesh：採取・畑・牧場などの移動処理で使用します。");
        ImGui.TextWrapped("必要なプラグインが導入されていない場合、該当する機能は正常に動作しません。");
        ImGui.Spacing();

        ImGui.TextUnformatted("移動");
        ImGui.BulletText("アイルデジョン：無人島の拠点へ戻ります。");
        ImGui.BulletText("無人島へ戻る：Lifestreamを利用して自分の無人島へ移動します。");
        ImGui.Spacing();

        ImGui.TextUnformatted("採取");
        ImGui.BulletText("通常採集：本家の通常採集機能です。素材一覧の「追加」にチェックすると収集リストへ登録できます。");
        ImGui.BulletText("収集リスト：登録した素材を目標所持数まで順番に収集します。コース内の他素材も採取します。");
        ImGui.BulletText("収集リスト開始時は売却を行いません。1コース終了ごとにアイルデジョンで拠点へ戻り、次の素材へ進みます。");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.00f, 0.72f, 0.20f, 1f), "注意：採取について");
        ImGui.TextWrapped("素材自体が採取可能になっていても、海中へ潜る必要がある場所や一部のエリアでは、飛行を解放していないと自動採取できない素材があります。飛行可能になると動作します。それまでは一部の素材を手動で採取してください。これは本家仕様です。");
        ImGui.Spacing();

        ImGui.TextUnformatted("畑");
        ImGui.BulletText("現在は水やりのみ対応しています。対象区画を選び、Slot 0 ～ 19 の順で処理します。");
        ImGui.BulletText("ゲーム内の畑番号は1から、プラグインのSlot番号は0から始まります。");
        ImGui.BulletText("マメット管理中の区画は対象外です。");
        ImGui.BulletText("ランク20の環境で開発しているため、開拓状況によっては未確認の状態があります。");
        ImGui.Spacing();

        ImGui.TextUnformatted("牧場");
        ImGui.BulletText("ゲーム内を餌やりモードにして餌を選び、「現在の餌設定を登録」で記憶します。");
        ImGui.BulletText("次回からは「使用する餌」と対象の動物を選び、牧場の柵の内側から実行してください。");
        ImGui.BulletText("入口から少し入った位置で開始すると、動作が安定しやすくなります。");
        ImGui.Spacing();

        ImGui.TextUnformatted("在庫");
        ImGui.BulletText("売却時の素材個数を設定します。設定した保持数を超えた分が売却対象になります。");
        ImGui.Spacing();

        ImGui.TextUnformatted("未実装・対象外の機能");
        ImGui.BulletText("畑：収穫・種まきは未実装です。");
        ImGui.BulletText("畑：マメットの管理・操作は対象外です。");
        ImGui.BulletText("牧場：動物ごとの個別餌設定は未実装です。");
        ImGui.BulletText("牧場：餌やり／収穫を動物ごとに個別設定する機能は未実装です。");
        ImGui.TextWrapped("ご要望があれば、今後の実装を検討します。");
        ImGui.Spacing();

        ImGui.TextUnformatted("本家仕様による制限");
        ImGui.BulletText("飛行未解放の状態では、一部の採取場所へ自動移動できない素材があります。飛行解放後は動作します。");
    }

    private void DrawLicense()
    {
        ImGui.TextUnformatted("ライセンス / 権利関係");
        ImGui.Separator();
        ImGui.TextWrapped("ExplorersIceboxJP は、LeontopodiumNivale14 / Explorers-Icebox を基にした改変版です。本家の公式版ではありません。");
        ImGui.Spacing();
        ImGui.TextUnformatted("Original project: Explorer's Icebox");
        ImGui.TextUnformatted("Original author: Ice / LeontopodiumNivale14");
        ImGui.TextUnformatted("Modified version: ExplorersIceboxJP");
        ImGui.TextUnformatted("Modified by: Elpa");
        ImGui.TextUnformatted("License: GNU AGPL-3.0-or-later");
        DrawExternalLink("https://www.gnu.org/licenses/agpl-3.0.html", "https://www.gnu.org/licenses/agpl-3.0.html");
        ImGui.Spacing();
        ImGui.TextWrapped("本改変版は GNU AGPL v3 以降の条件に従って配布し、対応する改変ソースコードを公開します。");
        ImGui.TextWrapped("ライセンス全文は LICENSE.md、改変・帰属情報は NOTICE.md、ソース公開に関する情報は SOURCE.md を参照してください。");
        ImGui.Spacing();
        ImGui.TextUnformatted("Original repository:");
        DrawExternalLink("https://github.com/LeontopodiumNivale14/Explorers-Icebox", "https://github.com/LeontopodiumNivale14/Explorers-Icebox");
    }

    private void CaptureToramemoTitleBarLinkLayout()
    {
        // Plugin JP Helper v0.5.4 と同じ方式。
        // WindowSystem は Draw() 後に標準タイトルバーボタンを描画するため、
        // Draw() 中は座標だけ保存し、PostDraw() で最前面へ文字リンクを描画する。
        const string label = "とらめもブログ";
        const float rightReserved = 78.0f;
        const float horizontalPadding = 6.0f;

        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var textSize = ImGui.CalcTextSize(label);
        var titleBarHeight = ImGui.GetFrameHeight();

        toramemoTitleBarLinkPos = new Vector2(
            windowPos.X + windowSize.X - rightReserved - textSize.X,
            windowPos.Y + Math.Max((titleBarHeight - textSize.Y) * 0.5f, 1.0f));

        toramemoTitleBarLinkMin = new Vector2(toramemoTitleBarLinkPos.X - horizontalPadding, windowPos.Y);
        toramemoTitleBarLinkMax = new Vector2(
            toramemoTitleBarLinkPos.X + textSize.X + horizontalPadding,
            windowPos.Y + titleBarHeight);
        toramemoTitleBarLinkLayoutValid = true;
    }

    private void DrawToramemoTitleBarLinkOverlay()
    {
        RefreshToramemoTitleBarLinkLayoutFromCurrentWindow();
        if (!toramemoTitleBarLinkLayoutValid)
            return;

        const string label = "とらめもブログ";
        var hovered = ImGui.IsMouseHoveringRect(toramemoTitleBarLinkMin, toramemoTitleBarLinkMax, false);
        var linkColor = hovered
            ? ImGui.GetColorU32(new Vector4(0.35f, 0.90f, 1.00f, 1.00f))
            : ImGui.GetColorU32(new Vector4(0.20f, 0.80f, 0.95f, 1.00f));

        ImGui.GetForegroundDrawList().AddText(toramemoTitleBarLinkPos, linkColor, label);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("とらめもブログを開く");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                OpenExternalUrl(ToramemoTopUrl);
        }
    }

    private unsafe void RefreshToramemoTitleBarLinkLayoutFromCurrentWindow()
    {
        const string label = "とらめもブログ";
        const float rightReserved = 78.0f;
        const float horizontalPadding = 6.0f;

        var window = ImGuiP.FindWindowByName(WindowName);
        if (window.IsNull)
        {
            toramemoTitleBarLinkLayoutValid = false;
            return;
        }

        var windowPos = window.Handle->Pos;
        var windowSize = window.Handle->Size;
        var textSize = ImGui.CalcTextSize(label);
        var titleBarHeight = ImGui.GetFrameHeight();

        toramemoTitleBarLinkPos = new Vector2(
            windowPos.X + windowSize.X - rightReserved - textSize.X,
            windowPos.Y + Math.Max((titleBarHeight - textSize.Y) * 0.5f, 1.0f));

        toramemoTitleBarLinkMin = new Vector2(toramemoTitleBarLinkPos.X - horizontalPadding, windowPos.Y);
        toramemoTitleBarLinkMax = new Vector2(
            toramemoTitleBarLinkPos.X + textSize.X + horizontalPadding,
            windowPos.Y + titleBarHeight);
        toramemoTitleBarLinkLayoutValid = true;
    }

    private static void DrawExternalLink(string label, string url)
    {
        var linkColor = new Vector4(0.35f, 0.70f, 1.00f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, linkColor);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("クリックしてブラウザで開く");
        }
        if (ImGui.IsItemClicked())
            OpenExternalUrl(url);
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[ExplorersIceboxJP] URLを開けませんでした: {url}");
        }
    }

    public override void Draw()
    {
        CaptureToramemoTitleBarLinkLayout();

        if (ImGui.BeginTabBar("IceboxMainTabs"))
        {
            if (ImGui.BeginTabItem("移動###MovementTab"))
            {
                if (ImGui.BeginChild("MovementTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                    DrawQuickTravel();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("採取###GatherTab"))
            {
                if (ImGui.BeginChild("GatherTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                {
                    if (ImGui.BeginTabBar("GatherSubTabs"))
                    {
                        if (ImGui.BeginTabItem("通常採集###NormalGatherSubTab"))
                        {
                            DrawNormalGather();
                            ImGui.EndTabItem();
                        }

                        var collectionLabel = C.CollectionTargets.Count > 0
                            ? $"収集リスト ({C.CollectionTargets.Count})###CollectionSubTab"
                            : "収集リスト###CollectionSubTab";
                        if (ImGui.BeginTabItem(collectionLabel))
                        {
                            DrawCollectionList();
                            ImGui.EndTabItem();
                        }
                        ImGui.EndTabBar();
                    }
                }
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("畑###FarmTab"))
            {
                if (ImGui.BeginChild("FarmTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                    DrawFarm();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("牧場###PastureTab"))
            {
                if (ImGui.BeginChild("PastureTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                    DrawPasture();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("在庫###InventoryTab"))
            {
                if (ImGui.BeginChild("InventoryTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                    DrawInventorySettings();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("ヘルプ###HelpTab"))
            {
                if (ImGui.BeginChild("HelpTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                    DrawHelp();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("ライセンス###LicenseTab"))
            {
                if (ImGui.BeginChild("LicenseTabBody", new(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar))
                    DrawLicense();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    public override void PostDraw()
    {
        DrawToramemoTitleBarLinkOverlay();
    }

}
