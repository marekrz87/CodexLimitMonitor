using System.Text;

namespace CodexLimitMonitor;

internal static class StartupLog
{
    private const long MaxBytes = 64 * 1024;
    private static readonly object Sync = new();
    internal static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MWInteractive", "CodexLimitMonitor", "logs", "startup.log");

    public static void StartRun()
    {
        try
        {
            lock (Sync)
            {
                string path = PathName;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                for (int index = 3; index >= 1; index--)
                {
                    string older = index == 1 ? path : path + "." + (index - 1);
                    if (File.Exists(older)) File.Move(older, path + "." + index, overwrite: true);
                }
            }
        }
        catch (Exception) { /* Logging must never prevent tray startup. */ }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string path = PathName;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    string[] lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2), Encoding.UTF8);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception) { /* Diagnostics must never prevent tray startup. */ }
    }

    public static void Unhandled(Exception? error) =>
        Write("Unhandled exception: " + (error?.GetType().FullName ?? "Unknown"));
}
