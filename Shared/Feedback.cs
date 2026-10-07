using Shared.Session;
using VRage.Utils;

namespace Shared;

public static class Log
{
    public const string Name = "Undo";

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Warning(string message) => Write(LogLevel.Warning, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    private static void Write(LogLevel level, string message)
    {
        if (level <= (Options.Current?.LogLevel ?? LogLevel.Info))
            MyLog.Default.WriteLine($"{Name}: {level}: {message}");
    }
}

public static class Notify
{
    // Undo, redo and refusal texts for the player the code runs for. Always logged
    // and written to the status file, so tests can read it.
    public static void Show(string text) => Actor.Current?.Notify(text);
}
