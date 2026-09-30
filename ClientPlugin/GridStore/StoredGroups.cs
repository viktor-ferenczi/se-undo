using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin.Ops;
using ClientPlugin.Session;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClientPlugin.GridStore;

// The game side of the grid store: grid groups in and out of blueprint entries
public static class StoredGroups
{
    // Where the current world's entries go, design sections 8 and 9
    public static string Folder()
    {
        var session = MySession.Static;
        var world = Sanitize(session.Name);
        if (session.WorldId != Guid.Empty)
            world += "_" + session.WorldId;

        if (Sync.IsServer)
        {
            var folder = Sanitize(Path.GetFileName(session.CurrentPath));
            var key = session.WorldId == Guid.Empty ? folder : folder + "_" + session.WorldId;
            return Path.Combine(UndoSession.StorageRoot, "Worlds", key, "grids");
        }

        var server = Sync.ServerId.ToString();
        var host = MyMultiplayer.Static?.HostName;
        if (!string.IsNullOrWhiteSpace(host))
            server += "_" + Sanitize(host);
        var player = $"{Sync.MyId}_{session.LocalPlayerId}";
        return Path.Combine(UndoSession.StorageRoot, "Servers", server, player, world, "grids");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    // The grids a delete or a snapshot takes along with this one
    public static List<MyCubeGrid> GroupOf(MyCubeGrid grid) =>
        MyCubeGridGroups
            .Static.GetGroups(Config.Current.GroupLinkTypeForSnapshots)
            .GetGroupNodes(grid)
            .ToList();

    // Builders as the clipboard copies them (pilots out, turrets not shooting), but
    // with the entity ids, so a server re-creates the grids as themselves
    public static List<MyObjectBuilder_CubeGrid> Capture(IEnumerable<MyCubeGrid> grids)
    {
        var clipboard = MyClipboardComponent.Static.Clipboard;
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

    public static StoreRow Save(List<MyObjectBuilder_CubeGrid> builders, StoreReason reason)
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

        return UndoSession.Store.Add(
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

    // Null when the entry is gone from the store
    public static List<MyObjectBuilder_CubeGrid> Load(string id)
    {
        var xml = UndoSession.Store.Read(id);
        return xml == null
            ? null
            : BuilderXml
                .Read<MyObjectBuilder_Definitions>(xml)
                .ShipBlueprints[0]
                .CubeGrids.ToList();
    }
}
