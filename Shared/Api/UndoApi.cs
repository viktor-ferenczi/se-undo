using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.History;
using Shared.Ops;
using Shared.Record;
using Shared.Session;
using VRage.Game.ModAPI;
using VRageMath;

namespace Shared.Api;

// Lets other plugins and mods put their own steps into a player's undo history,
// without referencing this assembly; Docs/API.md shows how to call it. A caller asks
// for the functions with a mod message, which plugins and mod scripts can both send:
//
//   MyAPIGateway.Utilities.SendModMessage(487710606L,
//       (Action<Dictionary<string, Delegate>>)(api => undoApi = api));
//
// The callback runs inside the call when Undo is loaded, and not at all otherwise.
// Every function takes the player's Steam id, 0 for the local player, and does
// nothing and returns false where Undo does not record for that player. Recording
// happens where the world changes: offline, on a lobby host, and on a dedicated
// server that runs the Undo companion. Main thread only.
public static class UndoApi
{
    public const long ModMessageId = 487710606L;
    public const int Version = 1;

    private static readonly Dictionary<string, Func<string, ulong, string>> handlers =
        new Dictionary<string, Func<string, ulong, string>>();

    private static bool running;

    public static void Start()
    {
        if (running || MyAPIGateway.Utilities == null)
            return;
        running = true;
        handlers.Clear();
        MyAPIGateway.Utilities.RegisterMessageHandler(ModMessageId, OnModMessage);
        MySession.OnUnloading += Stop;
        Log.Info($"Undo API {Version} answers mod message {ModMessageId}");
    }

    public static void Stop()
    {
        if (!running)
            return;
        running = false;
        MySession.OnUnloading -= Stop;
        MyAPIGateway.Utilities?.UnregisterMessageHandler(ModMessageId, OnModMessage);

        // A mod's handler must not outlive its session
        handlers.Clear();
    }

    private static void OnModMessage(object message)
    {
        if (message is Action<Dictionary<string, Delegate>> receive)
            receive(Functions());
    }

    // A fresh dictionary for each caller
    public static Dictionary<string, Delegate> Functions() =>
        new Dictionary<string, Delegate>
        {
            ["Version"] = new Func<int>(() => Version),
            ["IsRecording"] = new Func<ulong, bool>(IsRecording),
            ["RecordRemoval"] = new Func<
                IMyCubeGrid,
                List<Vector3I>,
                List<IMyCubeGrid>,
                string,
                ulong,
                bool
            >(RecordRemoval),
            ["BeforeEdit"] = new Func<IMyCubeGrid, ulong, bool>(BeforeEdit),
            ["AddStep"] = new Func<string, string, string, string, ulong, bool>(AddStep),
            ["RegisterStepHandler"] = new Action<string, Func<string, ulong, string>>(
                RegisterStepHandler
            ),
        };

    public static Func<string, ulong, string> Handler(string owner) =>
        owner != null && handlers.TryGetValue(owner, out var handler) ? handler : null;

    public static void RegisterStepHandler(string owner, Func<string, ulong, string> handler)
    {
        if (string.IsNullOrEmpty(owner))
            return;
        if (handler == null)
            handlers.Remove(owner);
        else
            handlers[owner] = handler;
    }

    public static bool IsRecording(ulong steamId) => Run(steamId, () => true);

    // Before the caller removes the blocks. The step is committed once the removal
    // and the splits it causes settled; see RazeCapture.
    private static bool RecordRemoval(
        IMyCubeGrid grid,
        List<Vector3I> positions,
        List<IMyCubeGrid> closing,
        string label,
        ulong steamId
    ) =>
        grid is MyCubeGrid cubeGrid
        && positions != null
        && Run(
            steamId,
            () =>
            {
                Recorder.BeforeEdit(cubeGrid);
                var capture = RazeCapture.Begin(
                    cubeGrid,
                    positions,
                    string.IsNullOrEmpty(label) ? "removed blocks" : label,
                    closing?.OfType<MyCubeGrid>().Where(g => g != cubeGrid).Distinct().ToList()
                );
                if (capture == null)
                    return false;
                Recorder.Begin(capture);
                return true;
            }
        );

    // Before the caller changes the grid by its own means; backs the grid's group up
    // when it is due, the same as before the player's own edits
    private static bool BeforeEdit(IMyCubeGrid grid, ulong steamId) =>
        grid is MyCubeGrid cubeGrid
        && Run(
            steamId,
            () =>
            {
                Recorder.BeforeEdit(cubeGrid);
                return true;
            }
        );

    private static bool AddStep(
        string owner,
        string label,
        string undoData,
        string redoData,
        ulong steamId
    ) =>
        Handler(owner) != null
        && Run(
            steamId,
            () =>
            {
                Recorder.Flush();
                Recorder.Commit(
                    string.IsNullOrEmpty(label) ? owner : label,
                    new List<Op>
                    {
                        new PluginStepOp { Owner = owner, Data = redoData },
                    },
                    new List<Op>
                    {
                        new PluginStepOp { Owner = owner, Data = undoData },
                    }
                );
                return true;
            }
        );

    // Runs the call as the player's actor, where Undo records for that player. Never
    // throws: a failure here must not break the plugin or mod that called.
    private static bool Run(ulong steamId, Func<bool> call)
    {
        try
        {
            if (!running || Thread.CurrentThread != MySandboxGame.Static?.UpdateThread)
                return false;
            var actor = Find(steamId);
            if (actor == null)
                return false;
            using (Actor.Use(actor))
                return Recorder.CanRecord && call();
        }
        catch (Exception e)
        {
            Log.Error($"An API call failed: {e}");
            return false;
        }
    }

    private static Actor Find(ulong steamId)
    {
        var local = Actors.Local;
        if (local != null && (steamId == 0 || steamId == local.SteamId))
            return local;
        return Actors.Served.TryGetValue(steamId, out var served) ? served : null;
    }
}
