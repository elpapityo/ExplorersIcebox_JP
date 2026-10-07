using ECommons.EzIpcManager;
using ExplorersIcebox.Util;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace ExplorersIcebox.IPC;

#nullable disable
public class NavmeshIPC
{
    public const string Name = "vnavmesh";
    public const string Repo = "https://puni.sh/api/repository/veyn";
    [EzIPC("Nav.%m")] public readonly Func<float> BuildProgress;

    [EzIPC("Nav.%m")] public readonly Func<bool> IsReady;
    [EzIPC("Path.%m")] public readonly Func<bool> IsRunning;

    [EzIPC("Path.%m")] public readonly Action<List<Vector3>, bool> MoveTo;

    [EzIPC("Query.Mesh.%m")] public readonly Func<Vector3, float, float, Vector3?> NearestPoint;
    [EzIPC("Nav.%m")] public readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind;

    [EzIPC("SimpleMove.%m")] public readonly Func<Vector3, bool, bool> PathfindAndMoveTo;
    [EzIPC("SimpleMove.%m")] public readonly Func<bool> PathfindInProgress;
    [EzIPC("Query.Mesh.%m")] public readonly Func<Vector3, bool, float, Vector3?> PointOnFloor;
    [EzIPC("Nav.%m")] public readonly Func<bool> Rebuild;
    [EzIPC("Nav.%m")] public readonly Func<bool> Reload;
    [EzIPC("Path.%m")] public readonly Action<bool> SetAlignCamera;
    [EzIPC("Path.%m")] public readonly Action Stop;
    public NavmeshIPC()
    {
        EzIPC.Init(this, Name);
    }
    public static bool Installed => Utils.HasPlugin(Name);
}
