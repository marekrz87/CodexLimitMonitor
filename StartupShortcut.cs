namespace CodexLimitMonitor;

internal enum StartupShortcutState { Ok, Missing, InvalidPath, Unavailable }

internal sealed class StartupShortcut
{
    private readonly string _path;

    public StartupShortcut(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CodexLimitMonitor.lnk");

    public StartupShortcutState Inspect(string executable)
    {
        if (!File.Exists(_path)) return StartupShortcutState.Missing;
        try
        {
            dynamic shortcut = Shell().CreateShortcut(_path);
            string target = (string)shortcut.TargetPath;
            return File.Exists(target) &&
                   string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                ? StartupShortcutState.Ok : StartupShortcutState.InvalidPath;
        }
        catch (Exception) { return StartupShortcutState.Unavailable; }
    }

    public void Enable(string executable)
    {
        string fullPath = Path.GetFullPath(executable);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Startup EXE was not found.", fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        dynamic shortcut = Shell().CreateShortcut(_path);
        shortcut.TargetPath = fullPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(fullPath)!;
        shortcut.Description = "Codex Limit Monitor";
        shortcut.WindowStyle = 1;
        shortcut.Save();
    }

    public void Disable()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static dynamic Shell()
    {
        Type type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcuts are unavailable.");
        return Activator.CreateInstance(type)!;
    }
}
