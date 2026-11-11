using System;
using Shared.Api;
using Shared.History;
using Shared.Session;

namespace Shared.Ops;

// A step another plugin or a mod added through UndoApi.AddStep. Its owner applies it:
// the op only hands the data to the handler the owner registered.
public class PluginStepOp : Op
{
    public string Owner;
    public string Data;

    public override string Validate(GridRegistry grids) =>
        UndoApi.Handler(Owner) == null ? $"{Owner} is not loaded" : null;

    public override Func<bool> Apply(GridRegistry grids)
    {
        var refusal = UndoApi.Handler(Owner)(Data, Actor.Current.SteamId);
        if (refusal != null)
            throw new OpRefusedException(refusal);
        return null;
    }
}
