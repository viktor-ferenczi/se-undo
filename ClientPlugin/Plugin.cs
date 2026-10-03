using System;
using System.Reflection;
using ClientPlugin.Session;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using ClientPlugin.Storage;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("0.1.1.0")]
[assembly: AssemblyFileVersion("0.1.1.0")]

#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "Undo";
    public static Plugin Instance { get; private set; }
    private SettingsGenerator settingsGenerator;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    public void Init(object gameInstance)
    {
        Instance = this;
        Instance.settingsGenerator = new SettingsGenerator();

        Config.Current.PropertyChanged += (_, _) => UndoSession.Configure();

        CleanClientHistories();

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    // Histories of server sessions nobody came back to, design section 8
    private static void CleanClientHistories()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Config.Current.ClientHistoryRetentionDays);
            var deleted = ClientRetention.Clean(UndoSession.StorageRoot, cutoff);
            if (deleted != 0)
                Log.Info($"Removed {deleted} client history folders past the retention time");
        }
        catch (Exception e)
        {
            Log.Error($"Cleaning the client histories failed: {e}");
        }
    }

    public void Dispose()
    {
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        Instance = null;
    }

    public void Update()
    {
        UndoSession.Update();
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        Instance.settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(Instance.settingsGenerator.Dialog);
    }
}
