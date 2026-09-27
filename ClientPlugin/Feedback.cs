using ClientPlugin.Session;
using Sandbox.ModAPI;
using VRage.Utils;

namespace ClientPlugin;

public static class Log
{
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Warning(string message) => Write(LogLevel.Warning, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    private static void Write(LogLevel level, string message)
    {
        if (level <= Config.Current.LogLevel)
            MyLog.Default.WriteLine($"{Plugin.Name}: {level}: {message}");
    }
}

public static class Notify
{
    // HUD text for undo, redo and refusals. Always logged and written to the
    // status file, so tests can read it.
    public static void Show(string text)
    {
        Log.Info(text);
        UndoSession.LastMessage = text;
        UndoSession.Changed();

        var config = Config.Current;
        if (config.Notifications)
            MyAPIGateway.Utilities?.ShowNotification(text, config.NotificationDurationMs);
    }
}
