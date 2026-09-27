using System;
using System.IO;
using System.Text;
using ClientPlugin.History;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClientPlugin.Ops;

public static class GameAccess
{
    public const string GridMissing = "the grid no longer exists";

    public static MyCubeGrid ResolveGrid(this GridRegistry grids, int handle)
    {
        var entityId = grids.EntityIdOf(handle);
        return
            entityId != 0
            && MyEntities.TryGetEntityById(entityId, out MyCubeGrid grid)
            && !grid.MarkedForClose
            ? grid
            : null;
    }

    public static long LocalCharacterId => MySession.Static.LocalCharacterEntityId;

    public static long LocalIdentityId => MySession.Static.LocalPlayerId;

    // A cube block of the grid whose min corner is exactly this position
    public static MySlimBlock BlockAt(this MyCubeGrid grid, Vector3I min)
    {
        var block = grid.GetCubeBlock(min);
        return block != null && block.Min == min ? block : null;
    }
}

// Object builders as nested XML text, written with Keen's serializer which knows
// the polymorphic builder types
public static class BuilderXml
{
    public static string Write(MyObjectBuilder_Base builder)
    {
        using var stream = new MemoryStream();
        if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
            throw new InvalidOperationException($"Serializing {builder.GetType().Name} failed");
        return Encoding.UTF8.GetString(stream.ToArray()).TrimStart('﻿');
    }

    public static T Read<T>(string xml)
        where T : MyObjectBuilder_Base
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out T builder))
            throw new InvalidOperationException($"Deserializing {typeof(T).Name} failed");
        return builder;
    }
}

// Enough to place a block again: definition, position and orientation. The entity
// id is reused on replay while it is free, so references to the block survive.
public class BlockPlacement
{
    public string Definition;
    public Vector3I Min;
    public Base6Directions.Direction Forward;
    public Base6Directions.Direction Up;
    public long EntityId;

    public static BlockPlacement From(MySlimBlock block) =>
        new BlockPlacement
        {
            Definition = block.BlockDefinition.Id.ToString(),
            Min = block.Min,
            Forward = block.Orientation.Forward,
            Up = block.Orientation.Up,
            EntityId = block.FatBlock?.EntityId ?? 0,
        };

    public MyCubeGrid.MyBlockLocation ToLocation(long ownerId)
    {
        var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(
            MyDefinitionId.Parse(Definition)
        );
        var orientation = new MyBlockOrientation(Forward, Up);
        var min = Min;
        MySlimBlock.ComputeMax(definition, orientation, ref min, out var max);
        var center = MySlimBlock.ComputePositionInGrid(new MatrixI(orientation), definition, min);
        orientation.GetQuaternion(out var rotation);
        var entityId =
            EntityId != 0 && !MyEntities.EntityExists(EntityId)
                ? EntityId
                : MyEntityIdentifier.AllocateId();
        return new MyCubeGrid.MyBlockLocation(
            definition.Id,
            min,
            max,
            center,
            rotation,
            entityId,
            ownerId
        );
    }
}
