using System;
using System.Reflection;
using ClientPlugin.Record;
using ClientPlugin.Session;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using Shared;
using Shared.Record;
using Shared.Session;
using Shared.Storage;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = Log.Name;
    public static Plugin Instance { get; private set; }
    private SettingsGenerator settingsGenerator;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    public void Init(object gameInstance)
    {
        Instance = this;
        Instance.settingsGenerator = new SettingsGenerator();

        Options.Current = Config.Current;
        Recorder.ControlsMayHaveChanged = TerminalContextPatches.ControlsMayHaveChanged;
        Config.Current.PropertyChanged += (_, _) => UndoSession.Configure();

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
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
