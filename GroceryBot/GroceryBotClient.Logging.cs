using System.Text;

namespace GroceryBot;

public partial class GroceryBotClient
{
    private static readonly Lock LogFileLock = new();
    private static readonly string DebugLogPath = ResolveDebugLogPath();

    private static void InitializeLogging()
    {
        lock (LogFileLock)
        {
            File.WriteAllText(DebugLogPath, string.Empty, Encoding.UTF8);
        }
    }

    private static void LogLine(string message)
    {
        lock (LogFileLock)
        {
            File.AppendAllText(DebugLogPath, message + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string ResolveDebugLogPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GroceryBot.csproj")))
            {
                return Path.Combine(directory.FullName, "DebugResults.md");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "DebugResults.md");
    }
}
