using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Shared.History;
using Shared.Ops;
using Shared.Session;
using VRage.Utils;
using VRageMath;

namespace Shared.Record;

// Holding the paint button repaints every frame; everything painted on one grid
// until the stroke timeout passes without a call becomes one node
public sealed class PaintStroke
{
    private struct Change
    {
        public Vector3 OldColor;
        public MyStringHash OldSkin;
        public Vector3 NewColor;
        public MyStringHash NewSkin;
    }

    public readonly MyCubeGrid Grid;
    private readonly Dictionary<Vector3I, Change> changes = new Dictionary<Vector3I, Change>();
    private DateTime lastUtc;

    public PaintStroke(MyCubeGrid grid)
    {
        Grid = grid;
    }

    public bool Expired =>
        DateTime.UtcNow - lastUtc > TimeSpan.FromMilliseconds(Options.Current.PaintStrokeTimeoutMs);

    public void Touch() => lastUtc = DateTime.UtcNow;

    // The first change of a block in the stroke keeps its original paint
    public void Record(MySlimBlock block, Vector3 oldColor, MyStringHash oldSkin)
    {
        if (!changes.TryGetValue(block.Min, out var change))
            change = new Change { OldColor = oldColor, OldSkin = oldSkin };
        change.NewColor = block.ColorMaskHSV;
        change.NewSkin = block.SkinSubtypeId;
        changes[block.Min] = change;
        Touch();
    }

    public void Commit()
    {
        var changed = changes
            .Where(c => c.Value.OldColor != c.Value.NewColor || c.Value.OldSkin != c.Value.NewSkin)
            .ToList();
        if (changed.Count == 0 || Grid.MarkedForClose)
            return;

        var applyColor = changed.Any(c => c.Value.OldColor != c.Value.NewColor);
        var applySkin = changed.Any(c => c.Value.OldSkin != c.Value.NewSkin);
        var handle = Recorder.Handle(Grid);
        Recorder.Commit(
            $"painted {Recorder.Plural(changed.Count, "block")}",
            new List<Op>
            {
                new PaintOp
                {
                    Grid = handle,
                    ApplyColor = applyColor,
                    ApplySkin = applySkin,
                    Blocks = changed
                        .Select(c => new BlockPaint
                        {
                            Min = c.Key,
                            ColorHsv = c.Value.NewColor,
                            Skin = c.Value.NewSkin.String,
                        })
                        .ToList(),
                },
            },
            new List<Op>
            {
                new PaintOp
                {
                    Grid = handle,
                    ApplyColor = applyColor,
                    ApplySkin = applySkin,
                    Blocks = changed
                        .Select(c => new BlockPaint
                        {
                            Min = c.Key,
                            ColorHsv = c.Value.OldColor,
                            Skin = c.Value.OldSkin.String,
                        })
                        .ToList(),
                },
            }
        );
    }
}
