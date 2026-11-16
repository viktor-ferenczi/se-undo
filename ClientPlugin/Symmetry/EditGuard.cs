using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Shared;
using Shared.History;
using Shared.Ops;
using VRage.Game;
using VRageMath;

namespace ClientPlugin.Symmetry;

// Two optional checks before an edit of the local player happens, where the
// warnings of SymmetryWatch come after it:
//
// - a removal that would split the grid asks first, with or without symmetry;
// - with symmetry on, an edit that would not be the same at every mirror image
//   is not made at all, a message says why.
//
// The cube builder sends each mirror copy of a line or an area as a request of
// its own, in one frame. So the requests are held back, the record patches skip
// the game's method, and the next update looks at all of them together: it sends
// them on, or shows the dialog. What the edit would do is worked out from the
// grid as this client has it.
public static class EditGuard
{
    public enum Kind
    {
        Place,
        Remove,
        Paint,
    }

    private sealed class Held
    {
        public Kind Kind;
        public MyCubeGrid Grid;
        public Action Send;

        // Min corner, max corner and type of each block it would place, and the
        // blocks it would remove or paint
        public List<(Vector3I Min, Vector3I Max, MyCubeBlockDefinition Definition)> Placed;
        public List<MySlimBlock> Blocks;
    }

    private static readonly List<Held> held = new List<Held>();
    private static MyGuiScreenBase dialog;

    // The held requests are being sent on: the patches let them through
    private static bool sending;

    // For the tests, in the status file
    public static readonly List<string> Dialogs = new List<string>();

    private static bool Asking => dialog != null && dialog.State != MyGuiScreenState.CLOSED;

    public static void Clear()
    {
        held.Clear();
        Dialogs.Clear();
        dialog = null;
    }

    // --- from the record patches: true when the request is held back ---------------

    public static bool Placing(
        MyCubeGrid grid,
        Func<List<(Vector3I Min, Vector3I Max, MyDefinitionId Id)>> blocks,
        Action send
    ) =>
        Hold(
            Kind.Place,
            grid,
            send,
            h =>
                h.Placed = blocks()
                    .Where(b => grid.GetCubeBlock(b.Min) == null)
                    .Select(b =>
                        (b.Min, b.Max, MyDefinitionManager.Static.GetCubeBlockDefinition(b.Id))
                    )
                    .ToList()
        );

    public static bool Removing(MyCubeGrid grid, IEnumerable<Vector3I> positions, Action send) =>
        Hold(Kind.Remove, grid, send, h => h.Blocks = Blocks(grid, positions));

    public static bool Painting(MyCubeGrid grid, Vector3I min, Vector3I max, Action send) =>
        Hold(
            Kind.Paint,
            grid,
            send,
            h => h.Blocks = Blocks(grid, Enumerable.Empty<Vector3I>(), min, max)
        );

    private static List<MySlimBlock> Blocks(
        MyCubeGrid grid,
        IEnumerable<Vector3I> positions,
        Vector3I? min = null,
        Vector3I? max = null
    )
    {
        var cells = positions.ToList();
        if (min is { } a && max is { } b)
            for (var x = a.X; x <= b.X; x++)
            for (var y = a.Y; y <= b.Y; y++)
            for (var z = a.Z; z <= b.Z; z++)
                cells.Add(new Vector3I(x, y, z));
        return cells
            .Select(grid.GetCubeBlock)
            .Where(block => block != null && block.FatBlock?.IsSubBlock != true)
            .Distinct()
            .ToList();
    }

    private static bool Hold(Kind kind, MyCubeGrid grid, Action send, Action<Held> describe)
    {
        var config = Config.Current;
        if (
            sending
            || Replay.Active
            || MySession.Static == null
            || !(config.ConfirmGridSplit && kind == Kind.Remove)
                && !(config.PreventSymmetryBreak && SymmetryWatch.SymmetryOn(grid))
        )
            return false;

        // Whatever is asked for while a dialog is open is dropped
        if (!Asking)
        {
            var request = new Held
            {
                Kind = kind,
                Grid = grid,
                Send = send,
            };
            describe(request);
            held.Add(request);
        }
        return true;
    }

    // --- every frame --------------------------------------------------------------

    public static void Update()
    {
        if (held.Count == 0)
            return;

        var requests = held.ToList();
        held.Clear();
        foreach (var group in requests.GroupBy(r => r.Grid))
        {
            try
            {
                Decide(group.Key, group.ToList());
            }
            catch (Exception e)
            {
                Log.Error($"Checking an edit before it is made failed, the edit is dropped: {e}");
            }
        }
    }

    private static void Decide(MyCubeGrid grid, List<Held> requests)
    {
        if (grid.MarkedForClose || MySession.Static == null)
            return;

        var config = Config.Current;
        if (config.PreventSymmetryBreak && SymmetryWatch.SymmetryOn(grid))
            foreach (var kind in requests.Select(r => r.Kind).Distinct())
            {
                var (expected, missing) = Mirrored(grid, kind, requests);
                if (missing == 0)
                    continue;

                var verb = kind switch
                {
                    Kind.Place => "placed",
                    Kind.Remove => "removed",
                    _ => "painted",
                };
                Show(
                    "Symmetry",
                    $"Nothing was changed: only {expected - missing} of {expected} mirrored blocks "
                        + $"would be {verb}, the edit would break the symmetry.",
                    MyMessageBoxButtonsType.OK,
                    _ => { }
                );
                return;
            }

        if (config.ConfirmGridSplit)
        {
            var removed = new HashSet<MySlimBlock>(
                requests.Where(r => r.Kind == Kind.Remove).SelectMany(r => r.Blocks)
            );
            var parts = removed.Count == 0 ? 1 : Parts(grid, removed);
            if (parts > 1)
            {
                Show(
                    "Grid split",
                    $"Removing {Plural(removed.Count, "block")} would split {grid.DisplayName} "
                        + $"into {parts} parts. Remove?",
                    MyMessageBoxButtonsType.YES_NO,
                    yes =>
                    {
                        if (yes && !grid.MarkedForClose)
                            Send(requests);
                    }
                );
                return;
            }
        }
        Send(requests);
    }

    private static void Send(List<Held> requests)
    {
        sending = true;
        try
        {
            foreach (var request in requests)
                request.Send();
        }
        finally
        {
            sending = false;
        }
    }

    // The blocks these requests would change with all their mirror images, and how
    // many of the images would not get the same change, as SymmetryWatch counts them
    // afterwards. A paint is the same where the image is a block the paint covers too.
    private static (int Expected, int Missing) Mirrored(
        MyCubeGrid grid,
        Kind kind,
        List<Held> requests
    )
    {
        var boxes = new Dictionary<Vector3I, (Vector3I Max, MyCubeBlockDefinition Tag)>();
        foreach (var request in requests.Where(r => r.Kind == kind))
            if (kind == Kind.Place)
                foreach (var (min, max, definition) in request.Placed)
                    boxes[min] = (max, definition);
            else
                foreach (var block in request.Blocks)
                    boxes[block.Min] = (block.Max, block.BlockDefinition);

        return SymmetryWatch.Count(
            grid,
            boxes,
            (source, min, max) =>
                boxes.TryGetValue(min, out var other)
                && other.Max == max
                && (kind == Kind.Paint || SymmetryWatch.SameType(source, other.Tag))
        );
    }

    // How many parts the grid would be in without these blocks, by the neighbours
    // the game keeps for its own split test (blocks joined by mount points)
    private static int Parts(MyCubeGrid grid, HashSet<MySlimBlock> removed)
    {
        var seen = new HashSet<MySlimBlock>(removed);
        var parts = 0;
        var queue = new Queue<MySlimBlock>();
        foreach (var start in grid.CubeBlocks)
        {
            if (!seen.Add(start))
                continue;

            parts++;
            queue.Enqueue(start);
            while (queue.Count != 0)
                foreach (var next in queue.Dequeue().Neighbours)
                    if (seen.Add(next))
                        queue.Enqueue(next);
        }
        return parts;
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static void Show(
        string caption,
        string text,
        MyMessageBoxButtonsType buttons,
        Action<bool> answered
    )
    {
        Log.Info($"{caption}: {text}");
        Dialogs.Add(text);
        Session.UndoSession.Changed();
        dialog = MyGuiSandbox.CreateMessageBox(
            MyMessageBoxStyleEnum.Info,
            buttons,
            new StringBuilder(text),
            new StringBuilder(caption),
            callback: result => answered(result == MyGuiScreenMessageBox.ResultEnum.YES),
            focusedResult: buttons == MyMessageBoxButtonsType.OK
                ? MyGuiScreenMessageBox.ResultEnum.YES
                : MyGuiScreenMessageBox.ResultEnum.NO
        );
        MyGuiSandbox.AddScreen(dialog);
    }
}
