using ECommons.Automation.NeoTaskManager;
using ECommons.Logging;
using ExplorersIcebox.Config;
using ExplorersIcebox.IPC;
using ExplorersIcebox.Scheduler;
using ExplorersIcebox.Ui;
using ExplorersIcebox.Ui.MainWindow;
using ExplorersIcebox.Util;
using Pictomancy;
namespace ExplorersIcebox;

public sealed class ExplorersIcebox : IDalamudPlugin
{
    private static GeneralConfig? Config;
    private static GatherRoutes? GatherRoutesConfig;
    private static GatherRoutes? embeddedRoutes;



    internal static ExplorersIcebox P = null!;
    internal DebugWindow debugWindow;

    // Internal IPC's from other plugins
    internal LifestreamIPC lifestream;
    internal MainWindow mainWindow;
    internal NavmeshIPC navmesh;

    // Taskmanager from Ecommons (bless)
    internal TaskManager taskManager;

    // Window Systems for the plugin (nice and neatly)
    internal WindowSystem windowSystem;

    public ExplorersIcebox(IDalamudPluginInterface pi)
    {
        P = this;

        ECommonsMain.Init(pi, P, Module.DalamudReflector, Module.ObjectFunctions, Module.SplatoonAPI);

        // Icon deployment diagnostic. PluginLog is only safe after ECommonsMain.Init.
        try
        {
            var dllPath = typeof(ExplorersIcebox).Assembly.Location;
            var dllDir = System.IO.Path.GetDirectoryName(dllPath) ?? string.Empty;
            var devIconPath = System.IO.Path.Combine(dllDir, "images", "icon.png");
            var namedIconPath = System.IO.Path.Combine(dllDir, "ExplorersIceboxJP.png");
            PluginLog.Information($"[Icon] DLL={dllPath}");
            PluginLog.Information($"[Icon] images\\icon.png exists={System.IO.File.Exists(devIconPath)} path={devIconPath}");
            PluginLog.Information($"[Icon] ExplorersIceboxJP.png exists={System.IO.File.Exists(namedIconPath)} path={namedIconPath}");
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"[Icon] Failed to verify deployed icon files: {ex.Message}");
        }
        File_Migration.UpdateItemConfig();

        PctService.Initialize(pi);

        //IPC's that are used
        taskManager = new();
        lifestream = new();
        navmesh = new();

        // all the windows
        windowSystem = new();
        mainWindow = new();
        debugWindow = new();

        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += () =>
        {
            mainWindow.IsOpen = true;
        };
        /*
        Svc.PluginInterface.UiBuilder.OpenConfigUi += () =>
        {

        };
        */
        EzCmd.Add("/explorersiceboxjp", OnCommand, """
                                                 プラグイン画面を開きます
                                                 /iceboxjp - /explorersiceboxjp の短縮コマンド
                                                 /explorersiceboxjp s|settings - 設定画面を開きます
                                                 """);
        EzCmd.Add("/iceboxjp", OnCommand);

        taskManager = new(new(abortOnTimeout: true, timeLimitMS: 20000, showDebug: true));

        Init();
        Svc.Framework.Update += Tick;
        OnPluginLoad.UpdateItemNames();
    }
    public string Name => "ExplorersIceboxJP";

    // Lazy-loaded static config accessor
    public static GeneralConfig C => Config ??= LoadConfig<GeneralConfig>();
    public static GatherRoutes G => GatherRoutesConfig ??= LoadConfig<GatherRoutes>();

    public static GatherRoutes EmbedRoutes => embeddedRoutes ??= LoadEmbeddedConfig<GatherRoutes>("ExplorersIcebox.Routes.CustomRoutes.yaml");

    public void Dispose()
    {
        PastureAutomation.Dispose();
        FarmAutomation.Dispose();
        Safe(() => Svc.Framework.Update -= Tick);
        Safe(() => Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw);
        ECommonsMain.Dispose();
        PctService.Dispose();
    }

    private static T LoadConfig<T>() where T : IYamlConfig, new()
    {
        var path = typeof(T).GetProperty("ConfigPath")!.GetValue(null)!.ToString()!;
        var config = YamlConfig.Load<T>(path);

        if (config == null)
        {
            PluginLog.Warning($"[{typeof(T).Name}] Config was null. Creating new default.");
            config = new();
            YamlConfig.Save(config, path);
        }

        PluginLog.Information($"[{typeof(T).Name}] Loaded from {path}");
        return config;
    }

    private static T LoadEmbeddedConfig<T>(string resourceName) where T : IYamlConfig, new()
    {
        var config = YamlConfig.LoadFromResource<T>(resourceName);

        if (config == null)
        {
            PluginLog.Warning($"[{typeof(T).Name}] Embedded config was null. Returning new default.");
            config = new();
        }

        PluginLog.Information($"[{typeof(T).Name}] Loaded from embedded resource: {resourceName}");
        return config;
    }

    public void Init() { }

    private void Tick(object _)
    {
        if (Svc.Objects.LocalPlayer != null)
        {
            SchedulerMain.Tick();
        }
        else
        {
            SchedulerMain.DisablePlugin();
        }
    }

    private void OnCommand(string command, string args)
    {
        if (args.EqualsIgnoreCaseAny("d", "debug"))
        {
            debugWindow.IsOpen = !debugWindow.IsOpen;
        }
        else if (args.EqualsIgnoreCaseAny("s", "settings")) { }
        else
        {
            mainWindow.IsOpen = !mainWindow.IsOpen;
        }
    }
}
