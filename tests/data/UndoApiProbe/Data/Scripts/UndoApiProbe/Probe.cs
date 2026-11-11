using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;

namespace UndoApiProbe
{
    // A test mod for the Undo API (Docs/API.md), and an example of using it from a
    // mod script. The chat command "/probe <grid id> <name>" renames a grid and adds
    // the rename to the player's undo history as a step this mod applies itself.
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Probe : MySessionComponentBase
    {
        private const long UndoApiMessageId = 487710606L;
        private const string Owner = "UndoApiProbe";

        private Func<string, string, string, string, ulong, bool> addStep;

        public override void BeforeStart()
        {
            // Undo records where the world changes
            if (!MyAPIGateway.Multiplayer.IsServer)
                return;
            MyAPIGateway.Utilities.SendModMessage(
                UndoApiMessageId,
                (Action<Dictionary<string, Delegate>>)Connect
            );
            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;
        }

        private void Connect(Dictionary<string, Delegate> api)
        {
            addStep = (Func<string, string, string, string, ulong, bool>)api["AddStep"];
            var register = (Action<string, Func<string, ulong, string>>)api["RegisterStepHandler"];
            register(Owner, Apply);
            MyAPIGateway.Utilities.ShowMessage(Owner, "Connected to Undo");
        }

        private void OnMessageEntered(string text, ref bool sendToOthers)
        {
            if (!text.StartsWith("/probe "))
                return;
            sendToOthers = false;

            var parts = text.Split(new[] { ' ' }, 3);
            long id;
            if (parts.Length < 3 || !long.TryParse(parts[1], out id))
                return;
            var grid = MyAPIGateway.Entities.GetEntityById(id) as IMyCubeGrid;
            if (grid == null)
                return;

            var old = grid.CustomName;
            grid.CustomName = parts[2];
            var recorded =
                addStep != null
                && addStep(Owner, "renamed with the probe mod", id + " " + old, id + " " + parts[2], 0);
            MyAPIGateway.Utilities.ShowMessage(Owner, recorded ? "Recorded" : "Not recorded");
        }

        // Undo and redo: the data is "<grid id> <name>"
        private string Apply(string data, ulong steamId)
        {
            var parts = data.Split(new[] { ' ' }, 2);
            long id;
            if (parts.Length < 2 || !long.TryParse(parts[0], out id))
                return "the step is damaged";
            var grid = MyAPIGateway.Entities.GetEntityById(id) as IMyCubeGrid;
            if (grid == null)
                return "the grid is gone";
            grid.CustomName = parts[1];
            return null;
        }
    }
}
