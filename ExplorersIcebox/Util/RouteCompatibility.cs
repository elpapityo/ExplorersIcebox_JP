using FFXIVClientStructs.FFXIV.Client.Game.UI;
using ExplorersIcebox.Util.PathCreation;
using System.Collections.Generic;
using System.Linq;

namespace ExplorersIcebox.Util;

/// <summary>
/// Compatibility helpers used only for base -> gathering-area travel.
/// Route data itself is never rewritten.
/// </summary>
internal static unsafe class RouteCompatibility
{
    private static readonly HashSet<string> UnderwaterNodeNames = new()
    {
        "Seaweed Tangle",
        "Large Shell",
        "Coral Formation",
    };

    internal static bool CanFlyHere()
        => PlayerState.Instance() != null && PlayerState.Instance()->CanFly;

    /// <summary>
    /// The upstream route table contains exactly two underwater gathering routes.
    /// Identify them from their actual gathering-node type rather than from world Y.
    /// This deliberately avoids the old Y &lt; -5 heuristic, because normal Island
    /// Sanctuary ground can also use negative world Y values.
    /// </summary>
    internal static bool IsUnderwaterGatherRoute(KeyValuePair<string, RouteClass.RouteUtil> route)
    {
        foreach (var wp in route.Value.RouteWaypoints)
        {
            if (wp.TargetId == 0)
                continue;

            var node = ItemData.IslandNodeInfo.FirstOrDefault(x => x.Nodes.Contains(wp.TargetId));
            if (node != null && UnderwaterNodeNames.Contains(node.GatherName))
                return true;
        }

        return false;
    }
}
