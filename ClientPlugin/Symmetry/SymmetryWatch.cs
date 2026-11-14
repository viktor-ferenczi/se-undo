using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared;
using Shared.History;
using Shared.Ops;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Symmetry;

// Warns the player when an edit made with the cube builder's symmetry on did not
// come out the same at every mirror image, or split the grid.
//
// The cube builder leaves out the mirror copies it cannot build, so what it asks
// for says little. The watch follows what the local player asked for until the
// result is known (at once on a local server, with the server's answer on a
// client), then mirrors each block the edit changed on the grid's planes and looks
// for the same change there. It works without the history, so also on a server
// that has no companion.
public static class SymmetryWatch
{
    private enum Kind
    {
        Place,
        Remove,
        Paint,
    }

    // The game mirrors a cell on each plane, an odd plane runs along the cell's
    // edge, see MyCubeBuilderGizmo.MirrorBlockByPlane
    private readonly struct Plane
    {
        private readonly int axis;
        private readonly int twice;

        public Plane(int axis, int position, bool odd)
        {
            this.axis = axis;
            twice = 2 * position + (odd ? (axis == 2 ? 1 : -1) : 0);
        }

        public Vector3I Mirror(Vector3I cell)
        {
            cell[axis] = twice - cell[axis];
            return cell;
        }
    }

    private sealed class Item
    {
        public Vector3I Min;
        public MyDefinitionId Definition;

        // Removal and paint: the block as it was when asked for
        public MySlimBlock Block;
        public Vector3 Color;
        public MyStringHash Skin;
    }

    private sealed class Edit
    {
        public Kind Kind;
        public MyCubeGrid Grid;
        public List<List<Plane>> Mirrors;
        public readonly List<Item> Items = new List<Item>();
        public int Started;
        public int LastRequest;

        // The frame the result was known in, 0 before
        public int Known;
        public SplitWatch Splits;
    }

    // How long the server's answer is waited for, and after it for the splits the
    // server finds a frame later, in frames
    private const int AnswerFrames = 60;
    private const int SplitFramesLocal = 2;
    private const int SplitFramesRemote = 30;

    // Paint requests come every frame while the button is held; they are one edit
    // until they pause, and no longer than a second
    private const int PaintQuietFrames = 20;
    private const int PaintEditFrames = 60;

    private const int WarningMs = 5000;

    private static readonly List<Edit> edits = new List<Edit>();
    private static MyHudNotification mismatchHud;
    private static MyHudNotification splitHud;

    // For the tests, in the status file
    public static readonly List<string> Warnings = new List<string>();

    private static int Frame => MySession.Static.GameplayFrameCounter;

    public static void Clear()
    {
        foreach (var edit in edits)
            edit.Splits?.Close();
        edits.Clear();
        Warnings.Clear();
        mismatchHud = null;
        splitHud = null;
    }

    // --- what the local player asks for, from the record patches -------------------

    public static void Placing(
        MyCubeGrid grid,
        IEnumerable<(Vector3I Min, MyDefinitionId Id)> blocks
    )
    {
        var edit = Begin(Kind.Place, grid);
        if (edit == null)
            return;

        foreach (var (min, id) in blocks)
            if (grid.GetCubeBlock(min) == null && edit.Items.All(i => i.Min != min))
                edit.Items.Add(new Item { Min = min, Definition = id });
    }

    public static void Removing(MyCubeGrid grid, IEnumerable<Vector3I> positions)
    {
        var edit = Begin(Kind.Remove, grid);
        if (edit == null)
            return;

        edit.Splits ??= new SplitWatch(grid);
        AddBlocks(edit, positions);
    }

    public static void Painting(MyCubeGrid grid, Vector3I min, Vector3I max)
    {
        var edit = Begin(Kind.Paint, grid);
        if (edit == null)
            return;

        var cells = new List<Vector3I>();
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var z = min.Z; z <= max.Z; z++)
            cells.Add(new Vector3I(x, y, z));
        AddBlocks(edit, cells);
    }

    private static void AddBlocks(Edit edit, IEnumerable<Vector3I> positions)
    {
        foreach (var block in positions.Select(edit.Grid.GetCubeBlock).Distinct())
            if (
                block != null
                && block.FatBlock?.IsSubBlock != true
                && edit.Items.All(i => i.Block != block)
            )
                edit.Items.Add(
                    new Item
                    {
                        Min = block.Min,
                        Definition = block.BlockDefinition.Id,
                        Block = block,
                        Color = block.ColorMaskHSV,
                        Skin = block.SkinSubtypeId,
                    }
                );
    }

    // The edit the requests of this frame on this grid belong to, null when there
    // is nothing to watch
    private static Edit Begin(Kind kind, MyCubeGrid grid)
    {
        var config = Config.Current;
        if (
            !config.WarnOnSymmetryMismatch && !config.WarnOnSymmetrySplit
            || Replay.Active
            || MySession.Static == null
            || MyCubeBuilder.Static?.UseSymmetry != true
        )
            return null;

        var mirrors = Mirrors(grid);
        if (mirrors.Count == 0)
            return null;

        var frame = Frame;
        var edit = edits.FirstOrDefault(e =>
            e.Kind == kind
            && e.Grid == grid
            && (kind == Kind.Paint ? frame < e.Started + PaintEditFrames : e.Started == frame)
        );
        if (edit == null)
        {
            edit = new Edit
            {
                Kind = kind,
                Grid = grid,
                Mirrors = mirrors,
                Started = frame,
            };
            edits.Add(edit);
        }
        edit.LastRequest = frame;
        return edit;
    }

    // Every combination of the grid's planes: one, three or seven mirror images
    private static List<List<Plane>> Mirrors(MyCubeGrid grid)
    {
        var planes = new List<Plane>();
        if (grid.XSymmetryPlane is { } x)
            planes.Add(new Plane(0, x.X, grid.XSymmetryOdd));
        if (grid.YSymmetryPlane is { } y)
            planes.Add(new Plane(1, y.Y, grid.YSymmetryOdd));
        if (grid.ZSymmetryPlane is { } z)
            planes.Add(new Plane(2, z.Z, grid.ZSymmetryOdd));

        return Enumerable
            .Range(1, (1 << planes.Count) - 1)
            .Select(mask => planes.Where((_, i) => (mask & 1 << i) != 0).ToList())
            .ToList();
    }

    // --- every frame --------------------------------------------------------------

    public static void Update()
    {
        if (edits.Count == 0 || MySession.Static == null)
            return;

        foreach (var edit in edits.Where(Settled).ToList())
        {
            edits.Remove(edit);
            try
            {
                Finish(edit);
            }
            catch (Exception e)
            {
                Log.Error($"Checking a symmetry edit failed: {e}");
            }
        }
    }

    private static bool Applied(Edit edit, Item item) =>
        edit.Kind switch
        {
            Kind.Place => edit.Grid.BlockAt(item.Min) is { } block
                && block.BlockDefinition.Id == item.Definition,
            Kind.Remove => edit.Grid.GetCubeBlock(item.Min) != item.Block,
            _ => edit.Grid.GetCubeBlock(item.Min) == item.Block
                && (item.Block.ColorMaskHSV != item.Color || item.Block.SkinSubtypeId != item.Skin),
        };

    private static bool Settled(Edit edit)
    {
        if (edit.Grid.MarkedForClose)
            return true;

        var frame = Frame;
        if (edit.Kind == Kind.Paint)
            return frame
                >= edit.LastRequest + PaintQuietFrames + (Sync.IsServer ? 0 : SplitFramesRemote);

        if (edit.Known == 0)
        {
            if (frame < edit.Started + AnswerFrames && !edit.Items.All(i => Applied(edit, i)))
                return false;
            edit.Known = frame;
        }
        if (edit.Kind == Kind.Place)
            return true;

        // A client's grid stays marked for the disconnect test only the server runs,
        // so there the wait is the frames alone
        return Sync.IsServer
            ? frame >= edit.Known + SplitFramesLocal && edit.Splits.Settled
            : frame >= edit.Known + SplitFramesRemote;
    }

    private static void Finish(Edit edit)
    {
        var pieces = edit.Splits?.Close().Count ?? 0;
        if (edit.Grid.MarkedForClose)
            return;

        var config = Config.Current;
        var applied = edit.Items.Where(i => Applied(edit, i)).ToList();
        if (config.WarnOnSymmetryMismatch && applied.Count != 0)
            CheckMirrors(edit, applied);
        // With the question before a removal on, the player has confirmed the split
        if (config.WarnOnSymmetrySplit && !config.ConfirmGridSplit && pieces != 0)
            Warn(ref splitHud, $"Grid split: {Plural(pieces, "part")} split off");
    }

    // The cells of each changed block, mirrored: the same change must be there
    private static void CheckMirrors(Edit edit, List<Item> applied)
    {
        // Min and max corner of each changed block. A removed block still knows its own.
        var boxes = new Dictionary<Vector3I, (Vector3I Max, MySlimBlock Tag)>();
        foreach (var item in applied)
        {
            var block = item.Block ?? edit.Grid.BlockAt(item.Min);
            boxes[block.Min] = (block.Max, block);
        }

        var (expected, missing) = Count(
            edit.Mirrors,
            boxes,
            (source, min, max) => Same(edit, source, min, max, boxes)
        );
        if (missing == 0)
            return;

        var verb = edit.Kind switch
        {
            Kind.Place => "placed",
            Kind.Remove => "removed",
            _ => "painted",
        };
        Warn(
            ref mismatchHud,
            $"Symmetry mismatch: {verb} {expected - missing} of {expected} mirrored blocks"
        );
    }

    // How many blocks the boxes and all their mirror images are, and how many of
    // those images are not the same by the given test. A box is a block's min
    // corner with its max corner and whatever the test needs.
    public static (int Expected, int Missing) Count<T>(
        MyCubeGrid grid,
        Dictionary<Vector3I, (Vector3I Max, T Tag)> boxes,
        Func<T, Vector3I, Vector3I, bool> same
    ) => Count(Mirrors(grid), boxes, same);

    private static (int Expected, int Missing) Count<T>(
        List<List<Plane>> mirrors,
        Dictionary<Vector3I, (Vector3I Max, T Tag)> boxes,
        Func<T, Vector3I, Vector3I, bool> same
    )
    {
        var expected = new HashSet<Vector3I>(boxes.Keys);
        var missing = new HashSet<Vector3I>();
        foreach (var pair in boxes)
        foreach (var mirror in mirrors)
        {
            Vector3I a = pair.Key,
                b = pair.Value.Max;
            foreach (var plane in mirror)
            {
                a = plane.Mirror(a);
                b = plane.Mirror(b);
            }
            var min = Vector3I.Min(a, b);
            var max = Vector3I.Max(a, b);
            expected.Add(min);
            if (!same(pair.Value.Tag, min, max))
                missing.Add(min);
        }
        return (expected.Count, missing.Count);
    }

    // The cube builder mirrors edits on this grid
    public static bool SymmetryOn(MyCubeGrid grid) =>
        MyCubeBuilder.Static?.UseSymmetry == true && Mirrors(grid).Count != 0;

    // The same block type, or the variant the game builds as its mirror copy
    public static bool SameType(MyCubeBlockDefinition a, MyCubeBlockDefinition b) =>
        a.Id == b.Id
        || a.Id.TypeId == b.Id.TypeId
            && (a.MirroringBlock == b.Id.SubtypeName || b.MirroringBlock == a.Id.SubtypeName);

    // Whether the mirror image of a changed block got the same change: a block of
    // the same type, or of its mirrored variant, in exactly the mirrored cells was
    // placed or removed by this edit, or is there and looks the same after a paint.
    // The orientation is not compared, the game picks it by the block's own rules.
    private static bool Same(
        Edit edit,
        MySlimBlock source,
        Vector3I min,
        Vector3I max,
        Dictionary<Vector3I, (Vector3I Max, MySlimBlock Tag)> boxes
    )
    {
        if (edit.Kind == Kind.Paint)
            return edit.Grid.BlockAt(min) is { } painted
                && painted.Max == max
                && painted.SkinSubtypeId == source.SkinSubtypeId
                && Vector3.DistanceSquared(painted.ColorMaskHSV, source.ColorMaskHSV) < 1e-6f;

        if (!boxes.TryGetValue(min, out var other) || other.Max != max)
            return false;

        return SameType(source.BlockDefinition, other.Tag.BlockDefinition);
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // Red, in the middle of the screen with the other HUD notifications. Each kind
    // has one notification, a new warning replaces the text of the one showing.
    private static void Warn(ref MyHudNotification hud, string text)
    {
        Log.Info(text);
        Warnings.Add(text);
        Session.UndoSession.Changed();
        if (MyHud.Notifications == null)
            return;

        hud ??= new MyHudNotification(MyCommonTexts.CustomText, WarningMs, MyFontEnum.Red);
        hud.SetTextFormatArguments(text);
        MyHud.Notifications.Remove(hud);
        MyHud.Notifications.Add(hud);
    }
}
