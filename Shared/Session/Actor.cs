using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared.GridStore;
using Shared.Record;
using Shared.Storage;

namespace Shared.Session;

// The player a recording or a replay belongs to: the local player where the plugin
// is the host, or a player the server companion serves (design section 14). The
// recorder, the ops and the executor work on Actor.Current, which the code that
// calls them sets.
public sealed class Actor
{
    public readonly ulong SteamId;
    public readonly bool IsLocal;

    public UndoDocument Document;
    public PlayerOptions Options = new PlayerOptions();

    // Captures settling, the strokes being coalesced, held answers
    public readonly Recording Recording = new Recording();

    public string LastMessage;

    // A served player's client tells whether its terminal is open, which is where
    // terminal changes are recorded unless the player records them everywhere
    public bool TerminalOpen;

    // HUD text on the local client, a message to a served player's client
    public Action<string> Show = _ => { };

    // After every change of the history: the status file, the client's state
    public Action Changed = () => { };

    private GridStoreFolder store;

    public Actor(ulong steamId, bool isLocal)
    {
        SteamId = steamId;
        IsLocal = isLocal;
    }

    public long IdentityId =>
        IsLocal
            ? MySession.Static.LocalPlayerId
            : MySession.Static.Players.TryGetIdentityId(SteamId);

    // The character the player builds with, 0 while it has none
    public long CharacterId =>
        IsLocal ? MySession.Static.LocalCharacterEntityId
        : MySession.Static.Players.TryGetPlayerBySteamId(SteamId, out var player)
            ? player.Character?.EntityId ?? 0
        : 0;

    // A creative world, or creative tools switched on: undo is off without it.
    // Stricter than HasPlayerCreativeRights, which is true for everyone offline and
    // for a space master with the tools off.
    public bool Creative =>
        MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(SteamId);

    // Asked live, the tools can be switched any time; work under way still finishes
    public bool Active => Document != null && Sync.IsServer && Creative;

    // Opened on first use, when the session knows its world
    public GridStoreFolder Store => store ??= new GridStoreFolder(StoreFolder);

    public string StoreFolder =>
        IsLocal
            ? Path.Combine(StoredGroups.WorldFolder(), "grids")
            : Path.Combine(StoredGroups.WorldFolder(), "players", SteamId.ToString(), "grids");

    public void Notify(string text)
    {
        Log.Info(IsLocal ? text : $"{SteamId}: {text}");
        LastMessage = text;
        Show(text);
        Changed();
    }

    // --- the actor the code runs for --------------------------------------------

    private static Actor scoped;

    // The local player unless a served player's request or step is being handled
    public static Actor Current => scoped ?? Actors.Local;

    public static Scope Use(Actor actor) => new Scope(actor);

    public readonly struct Scope : IDisposable
    {
        private readonly Actor previous;

        public Scope(Actor actor)
        {
            previous = scoped;
            scoped = actor;
        }

        public void Dispose() => scoped = previous;
    }
}

// Every actor of the session: the local player where the plugin is the host, and the
// players the companion serves
public static class Actors
{
    public static Actor Local;
    public static readonly Dictionary<ulong, Actor> Served = new Dictionary<ulong, Actor>();

    public static IEnumerable<Actor> All =>
        Local == null ? Served.Values.ToList() : Served.Values.Prepend(Local).ToList();

    public static void Clear()
    {
        foreach (var actor in All)
            actor.Recording.Reset();
        Local = null;
        Served.Clear();
    }

    // Every frame: what each actor's recordings and pending replays have to finish
    public static void Update() =>
        ForEach(actor =>
        {
            actor.Recording.Update();
            if (actor.Document == null)
                return;
            Apply.Executor.Update(actor.Document.Build);
            Apply.Executor.Update(actor.Document.Terminal);
        });

    // Runs the per frame work of every actor with that actor as the current one
    public static void ForEach(Action<Actor> action)
    {
        foreach (var actor in All)
        {
            using (Actor.Use(actor))
                action(actor);
        }
    }
}
