using System;

namespace ClientPlugin.Ops;

public static class OpTypes
{
    // Every concrete op, for the XML serializer of the undo document
    public static readonly Type[] All =
    {
        typeof(BuildBlocksOp),
        typeof(RazeBlocksOp),
        typeof(RestoreBlocksOp),
        typeof(MergeBackOp),
        typeof(PaintOp),
    };
}
