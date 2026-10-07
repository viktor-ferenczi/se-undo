using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Shared.Ops;
using Shared.Record;
using Shared.Session;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace Shared.GridStore;

// The game side of the grid store: grid groups in and out of blueprint entries
public static class StoredGroups
{
    // The current world's folder under the storage root, design section 8: the save
    // folder name plus the world id
    public static string WorldFolder()
    {
        var session = MySession.Static;
        var folder = Sanitize(Path.GetFileName(session.CurrentPath));
        var key = session.WorldId == Guid.Empty ? folder : folder + "_" + session.WorldId;
        return Path.Combine(Options.Current.StorageRoot, "Worlds", key);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    // The grids the backup taken before a removal that takes a mechanical connection
    // apart holds along with this one.
    // Logical by default: what rotors, hinges, pistons and connectors hold together,
    // so a docked ship is put back with its carrier instead of being dropped.
    public static List<MyCubeGrid> GroupOf(MyCubeGrid grid) =>
        MyCubeGridGroups
            .Static.GetGroups(Options.Current.GroupLinkTypeForSnapshots)
            .GetGroupNodes(grid)
            .ToList();

    // The clipboard's RemovePilots reads no state of the clipboard. A dedicated server
    // has no clipboard component, so it gets an instance of its own.
    private static MyGridClipboard serverClipboard;

    // FormatterServices is obsolete on .NET 10, but its replacement does not exist on
    // .NET Framework 4.8, which the plugins build for too
#pragma warning disable SYSLIB0050
    private static MyGridClipboard Clipboard =>
        MyClipboardComponent.Static?.Clipboard
        ?? (
            serverClipboard ??= (MyGridClipboard)
                FormatterServices.GetUninitializedObject(typeof(MyGridClipboard))
        );

    // Builders as the clipboard copies them (pilots out, turrets not shooting), but
    // with the entity ids, so a server re-creates the grids as themselves
    public static List<MyObjectBuilder_CubeGrid> Capture(IEnumerable<MyCubeGrid> grids)
    {
        var clipboard = Clipboard;
        var builders = new List<MyObjectBuilder_CubeGrid>();
        foreach (var grid in grids)
        {
            var builder = (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder(copy: true);
            foreach (var turret in builder.CubeBlocks.OfType<MyObjectBuilder_LargeMissileTurret>())
                turret.IsShooting = false;
            clipboard.RemovePilots(builder);
            builders.Add(builder);
        }
        return builders;
    }

    private const long Mb = 1024 * 1024;

    // Stores the group and hands its row to stored, which commits the history node.
    // An entry larger than a budget asks the player first, design section 9: the
    // entry waits as a temporary file and the recorder keeps undo and redo locked
    // until the answer comes. Refused runs when the backup is dropped.
    public static void Save(
        List<MyObjectBuilder_CubeGrid> builders,
        StoreReason reason,
        Action<StoreRow> stored,
        Action<StoreRow> refused,
        bool mayAsk = true
    )
    {
        var actor = Actor.Current;
        var store = actor.Store;
        var row = Stage(builders, reason);
        var config = Options.Current;
        var overWorld = row.Bytes > config.GridStoreBudgetPerWorldMb * Mb;
        var overTotal = row.Bytes > config.GridStoreBudgetTotalMb * Mb;

        // An entry stored already takes no new space
        if (!store.IsStaged(row) || !overWorld && !overTotal)
        {
            Commit(row);
            stored(row);
            return;
        }

        var raisedMb = StoreRetention.RaisedBudgetMb(row.Bytes, config.BudgetRaiseStepMb);
        void Answer(bool raise)
        {
            if (raise)
            {
                // Every exceeded budget, so the next entry of this size fits too
                if (overWorld)
                    config.GridStoreBudgetPerWorldMb = raisedMb;
                if (overTotal)
                    config.GridStoreBudgetTotalMb = raisedMb;
                config.Save();
                Commit(row);
                stored(row);
            }
            else
            {
                store.Discard(row);
                Log.Info($"The backup of {row.MainGridName} was dropped, it is over the budget");
                refused(row);
            }
        }

        // Only the local player is asked. A server never raises its budgets for a
        // player, the backup is dropped.
        if (config.OversizedGridBackups != OversizedGridBackups.Ask || !mayAsk || !actor.IsLocal)
        {
            Answer(
                actor.IsLocal && config.OversizedGridBackups == OversizedGridBackups.AlwaysRaise
            );
            return;
        }

        var budgetMb = overWorld ? config.GridStoreBudgetPerWorldMb : config.GridStoreBudgetTotalMb;
        var text =
            $"Backing up \"{row.MainGridName}\" ({Recorder.Plural(row.GridCount, "grid")}, "
            + $"{Recorder.Plural(row.BlockCount, "block")}) needs {row.Bytes / (double)Mb:0.#} MB, "
            + $"more than the {(overWorld ? "per world" : "total")} grid store budget of "
            + $"{budgetMb} MB. Raise the budget to {raisedMb} MB? \"No\" keeps the budget and "
            + "drops the backup, so the change you just made cannot be undone.";

        // Closing the box any other way than through a button counts as No
        var answered = false;
        void Once(bool raise)
        {
            if (answered)
                return;
            answered = true;
            using var scope = Actor.Use(actor);
            try
            {
                Answer(raise);
            }
            catch (Exception e)
            {
                Log.Error($"Storing the backup of {row.MainGridName} failed: {e}");
            }
            finally
            {
                Recorder.Release();
            }
        }

        Recorder.Hold();
        var box = MyGuiSandbox.CreateMessageBox(
            MyMessageBoxStyleEnum.Info,
            MyMessageBoxButtonsType.YES_NO,
            new StringBuilder(text),
            new StringBuilder(Log.Name),
            callback: result => Once(result == MyGuiScreenMessageBox.ResultEnum.YES),
            focusedResult: MyGuiScreenMessageBox.ResultEnum.NO
        );
        box.Closed += (_, _) => Once(false);
        MyGuiSandbox.AddScreen(box);
    }

    // For the group backup an undo takes before it removes a mechanical base
    // (RazeBlocksOp.Prepare), in the middle of the replay where nobody can be asked.
    // ponytail: an oversized backup is refused unless the config says to always
    // raise; the question would need the replay to wait for the answer
    public static StoreRow SaveNow(List<MyObjectBuilder_CubeGrid> builders, StoreReason reason)
    {
        StoreRow result = null;
        Save(
            builders,
            reason,
            row => result = row,
            row =>
                throw new InvalidOperationException(
                    $"The backup of {row.MainGridName} is larger than the grid store budget"
                ),
            mayAsk: false
        );
        return result;
    }

    private static void Commit(StoreRow row)
    {
        Actor.Current.Store.Commit(row);
        try
        {
            Cleanup();
        }
        catch (Exception e)
        {
            Log.Error($"Grid store cleanup failed: {e}");
        }
    }

    // Retention, design section 9: this world's budget first, then the budget of
    // the whole storage root, where older worlds give way to the current one
    private static void Cleanup()
    {
        var config = Options.Current;
        var store = Actor.Current.Store;
        var removed = StoreRetention.Enforce(
            new[] { store },
            config.GridStoreBudgetPerWorldMb * Mb
        );

        var totalBudget = config.GridStoreBudgetTotalMb * Mb;
        var folders = StoreRetention.GridFolders(Options.Current.StorageRoot);
        if (StoreRetention.BytesOnDisk(folders) > totalBudget)
        {
            // The other worlds' indexes are only opened when something has to go
            var stores = folders
                .Select(f => PathsEqual(f, store.Folder) ? store : new GridStoreFolder(f))
                .ToList();
            removed += StoreRetention.Enforce(stores, totalBudget);
        }

        if (removed != 0)
            Log.Info($"Grid store cleanup removed {Recorder.Plural(removed, "backup")}");
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    private static StoreRow Stage(List<MyObjectBuilder_CubeGrid> builders, StoreReason reason)
    {
        var main = builders.OrderByDescending(b => b.CubeBlocks.Count).First();
        var sizes = builders.Select(b => b.GridSizeEnum).Distinct().ToList();

        var blueprint =
            MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
        blueprint.Id = new MyDefinitionId(
            new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)),
            main.DisplayName ?? "Grid"
        );
        blueprint.CubeGrids = builders.ToArray();
        var definitions =
            MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Definitions>();
        definitions.ShipBlueprints = new[] { blueprint };

        return Actor.Current.Store.Stage(
            BuilderXml.Write(definitions),
            new StoreRow
            {
                TimestampUtc = DateTime.UtcNow,
                Reason = reason,
                MainGridName = main.DisplayName,
                GridCount = builders.Count,
                BlockCount = builders.Sum(b => b.CubeBlocks.Count),
                Pcu = builders.SelectMany(b => b.CubeBlocks).Sum(Pcu),
                Size =
                    sizes.Count > 1 ? GridSizeClass.Mixed
                    : sizes[0] == MyCubeSize.Large ? GridSizeClass.Large
                    : GridSizeClass.Small,
                IsStatic = main.IsStatic,
                MainGridEntityId = main.EntityId,
            }
        );
    }

    private static int Pcu(MyObjectBuilder_CubeBlock block) =>
        MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out var definition)
            ? definition.PCU
            : 0;

    // The entry as the blueprint document it is; null when it is gone from the store
    public static MyObjectBuilder_Definitions LoadBlueprint(string id)
    {
        var xml = Actor.Current.Store.Read(id);
        return xml == null ? null : BuilderXml.Read<MyObjectBuilder_Definitions>(xml);
    }

    // Null when the entry is gone from the store
    public static List<MyObjectBuilder_CubeGrid> Load(string id) =>
        LoadBlueprint(id)?.ShipBlueprints[0].CubeGrids.ToList();
}
