using System.Text.Json;
using Microsoft.Win32;

namespace CodexLimitMonitor;

internal enum StartupEntryState { Ok, Missing, InvalidPath }

internal readonly record struct StartupInspection(StartupEntryState State, string? TargetPath,
    StartupShortcutState ShortcutState = StartupShortcutState.Missing)
{
    public bool Enabled => State == StartupEntryState.Ok || ShortcutState == StartupShortcutState.Ok;
    public string DisplayState => State switch
    {
        StartupEntryState.Ok => "OK",
        StartupEntryState.Missing => "Missing",
        _ => "Invalid path"
    };
    public string ShortcutDisplayState => ShortcutState switch
    {
        StartupShortcutState.Ok => "OK",
        StartupShortcutState.Missing => "Missing",
        StartupShortcutState.InvalidPath => "Invalid path",
        _ => "Unavailable"
    };
}

internal sealed class StartupManager
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "CodexLimitMonitor";

    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly StartupShortcut? _shortcut;

    public StartupManager(string keyPath = RunKey, string valueName = ValueName)
    {
        _keyPath = keyPath;
        _valueName = valueName;
        if (keyPath == RunKey && valueName == ValueName) _shortcut = new StartupShortcut();
    }

    public StartupInspection Inspect(string currentExecutable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        string? raw = key?.GetValue(_valueName) as string;
        StartupShortcutState shortcutState = _shortcut?.Inspect(currentExecutable) ?? StartupShortcutState.Missing;
        if (string.IsNullOrWhiteSpace(raw)) return new(StartupEntryState.Missing, null, shortcutState);

        string? target = ParseExecutable(raw);
        if (target is null || !File.Exists(target)) return new(StartupEntryState.InvalidPath, target, shortcutState);
        try
        {
            bool same = string.Equals(Path.GetFullPath(target), Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase);
            return new(same ? StartupEntryState.Ok : StartupEntryState.InvalidPath, target, shortcutState);
        }
        catch (Exception) { return new(StartupEntryState.InvalidPath, target, shortcutState); }
    }

    public void Enable(string currentExecutable)
    {
        string fullPath = Path.GetFullPath(currentExecutable);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The application EXE was not found.", fullPath);
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(_valueName, $"\"{fullPath}\"", RegistryValueKind.String);
        _shortcut?.Enable(fullPath);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
        _shortcut?.Disable();
    }

    private static string? ParseExecutable(string command)
    {
        string value = command.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 1 && string.IsNullOrWhiteSpace(value[(end + 1)..]) ? value[1..end] : null;
        }
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
}

internal sealed class UserPreferences
{
    public bool? StartupIntent { get; set; }
    public bool OnboardingCompleted { get; set; }
    public bool TrayGuidanceDismissed { get; set; }
    public bool StartupWarningShown { get; set; }
}

internal static class FirstRunStartup
{
    public static bool ShouldEnable(bool preferencesExist, bool usageHistoryExists, StartupInspection entry) =>
        !preferencesExist && !usageHistoryExists && entry.State == StartupEntryState.Missing;
}

internal sealed class PreferencesStore
{
    private readonly string _path;

    public PreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MWInteractive", "CodexLimitMonitor", "preferences.json");
    }

    public bool Exists => File.Exists(_path);

    public UserPreferences Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(_path)) ?? new UserPreferences()
                : new UserPreferences();
        }
        catch (Exception) { return new UserPreferences(); }
    }

    public void Save(UserPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
        File.Move(temporary, _path, overwrite: true);
    }
}
