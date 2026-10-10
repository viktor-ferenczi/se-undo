using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Config;
using Sandbox.Game.World;
using Shared;
using Shared.Companion;
using Shared.Session;
using VRage.FileSystem;
using VRage.Plugins;

// Define assembly version when compiled by Magnetar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

#endif

namespace ServerPlugin;

// The Undo companion for a dedicated server, design section 14. Everything it does
// is in Shared/Companion; this is the Magnetar side: config, patches, the frame loop.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const string ConfigFileName = Log.Name + ".cfg";
    private static bool failed;

    public static UndoServerConfig Config { get; private set; }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    public void Init(object gameInstance)
    {
        var path = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        try
        {
            Config = ConfigStorage.LoadXml<UndoServerConfig>(path);
        }
        catch (Exception e)
        {
            Config = new UndoServerConfig();
            Log.Warning($"Loading {path} failed, using the defaults: {e.Message}");
        }
        Config.Path = path;
        Options.Current = Config;

        new Harmony(Log.Name).PatchAll(Assembly.GetExecutingAssembly());
        Log.Info("Companion loaded");
    }

    public void Dispose()
    {
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
    }

    public void Update()
    {
        if (failed)
            return;

        try
        {
            // Magnetar initializes plugins after the world loaded, too late for a
            // session component of the plugin to be registered
            if (!CompanionServer.Running && MySession.Static?.Ready == true)
                CompanionServer.Start();
            Actors.Update();
        }
        catch (Exception e)
        {
            failed = true;
            Log.Error($"Update failed, the companion stops: {e}");
        }
    }
}
