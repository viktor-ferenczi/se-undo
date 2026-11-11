using Sandbox.Game.World;

namespace Shared.Apply;

// The paint requests one frame may send. Each request makes the game update the
// emissivity of every emissive block on its grid, one render message each, on every
// machine that gets it. The render queue holds 1M messages a frame and the game
// crashes when it fills up, so a replay of a big repaint is spread over frames. The
// same limits as Paint Replacer's.
public static class PaintBudget
{
    public const int MessagesPerFrame = 100000;
    public const int RequestsPerFrame = 1000;

    private static int frame = -1;
    private static int messages;
    private static int requests;

    // Takes a request that costs `cost` messages from this frame's budget. The first
    // request of a frame always fits, so a grid with more emissive blocks than the
    // budget still gets one request a frame.
    public static bool TryTake(int cost)
    {
        var now = MySession.Static.GameplayFrameCounter;
        if (now != frame)
        {
            frame = now;
            messages = 0;
            requests = 0;
        }
        if (requests >= RequestsPerFrame || (requests != 0 && messages + cost > MessagesPerFrame))
            return false;
        messages += cost;
        requests++;
        return true;
    }
}
