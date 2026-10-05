using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("CodexLimitMonitor.Tests")]

namespace CodexLimitMonitor;

internal record Limit(int Remaining, DateTimeOffset? Reset, int DurationMinutes);
internal record WindowInfo(string Path, int DurationMinutes, int Remaining);
internal record Snapshot(Limit? FiveHour, Limit? Weekly, DateTimeOffset FetchedAt, IReadOnlyList<WindowInfo>? Windows = null);

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\MWInteractive.CodexLimitMonitor", out bool first);
        if (!first) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => StartupLog.Unhandled(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => StartupLog.Unhandled(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupLog.Unhandled(args.Exception);
            args.SetObserved();
        };
        ApplicationConfiguration.Initialize();
        try { Application.Run(new TrayApp()); }
        catch (Exception ex) { StartupLog.Unhandled(ex); }
    }
}

internal static class CodexReader
{
    public static async Task<Snapshot> ReadAsync(CancellationToken cancellationToken, Action<string>? startupStatus = null)
    {
        string exe = FindCodex();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(exe, "app-server --stdio")
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Codex app-server could not start.");
            startupStatus?.Invoke("Process started");
        }
        catch (Exception) { startupStatus?.Invoke("Failed to start"); throw; }
        _ = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await Send(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "CodexLimitMonitor", version = "1.0.0" } } });
            using var initialized = await WaitFor(process, 1, timeout.Token);
            startupStatus?.Invoke("Initialized");
            await Send(process, new { method = "initialized", @params = new { } });
            await Send(process, new { id = 2, method = "account/rateLimits/read", @params = (object?)null });
            using var result = await WaitFor(process, 2, timeout.Token);
            return Parse(result.RootElement.GetProperty("result"));
        }
        catch (Exception) { startupStatus?.Invoke("Started but usage read failed"); throw; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    internal static Snapshot Parse(JsonElement root)
    {
        var (bucket, path) = PickCodexBucket(root);
        var found = new List<(WindowInfo Info, Limit Limit)>();
        FindWindows(bucket, path, found);
        Limit? five = found.Where(x => x.Info.DurationMinutes is >= 240 and <= 360)
            .OrderBy(x => Math.Abs(x.Info.DurationMinutes - 300)).Select(x => x.Limit).FirstOrDefault();
        Limit? weekly = found.Where(x => x.Info.DurationMinutes is >= 9_000 and <= 11_000)
            .OrderBy(x => Math.Abs(x.Info.DurationMinutes - 10_080)).Select(x => x.Limit).FirstOrDefault();
        if (found.Count == 0) throw new InvalidOperationException("Codex did not return a usage window.");
        return new Snapshot(five, weekly, DateTimeOffset.Now, found.Select(x => x.Info).ToArray());
    }

    private static void FindWindows(JsonElement element, string path, List<(WindowInfo Info, Limit Limit)> found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("usedPercent", out var used) && used.TryGetInt32(out int usedValue) &&
                element.TryGetProperty("windowDurationMins", out var durationEl) && durationEl.TryGetInt32(out int duration))
            {
                DateTimeOffset? reset = element.TryGetProperty("resetsAt", out var resetEl) && resetEl.TryGetInt64(out long seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                int remaining = Math.Clamp(100 - usedValue, 0, 100);
                found.Add((new WindowInfo(path, duration, remaining), new Limit(remaining, reset, duration)));
            }
            else foreach (var property in element.EnumerateObject())
                FindWindows(property.Value, path + "." + property.Name, found);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var child in element.EnumerateArray()) FindWindows(child, $"{path}[{index++}]", found);
        }
    }

    private static (JsonElement Bucket, string Path) PickCodexBucket(JsonElement root)
    {
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            if (buckets.TryGetProperty("codex", out var codex)) return (codex, "rateLimitsByLimitId.codex");
            foreach (var property in buckets.EnumerateObject())
                if (property.Name.Contains("codex", StringComparison.OrdinalIgnoreCase)) return (property.Value, "rateLimitsByLimitId." + property.Name);
        }
        return (root.GetProperty("rateLimits"), "rateLimits");
    }

    public static async Task<string> GetVersionAsync()
    {
        using var process = new Process { StartInfo = CreateStartInfo(FindCodex(), "--version") };
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output.Trim();
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    internal static ProcessStartInfo CreateStartInfo(string exe, string arguments)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };
        // Reading account limits does not require Git. Avoid a broken git.exe inherited from PATH.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        info.Environment["PATH"] = string.Join(Path.PathSeparator,
            new[] { Path.Combine(windows, "System32"), windows, Path.Combine(windows, "System32", "Wbem") });
        foreach (string key in info.Environment.Keys.Where(x => x.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        return info;
    }

    public static string GetExecutablePath() => FindCodex();

    public static string? GetGitExecutablePath()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                string path = Path.Combine(directory.Trim('"'), "git.exe");
                if (File.Exists(path)) return path;
            }
            catch (Exception) { /* Ignore invalid PATH entries. */ }
        }
        return null;
    }

    private static string FindCodex()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appBin = Path.Combine(local, "OpenAI", "Codex", "bin");
        if (Directory.Exists(appBin))
        {
            var candidates = Directory.EnumerateFiles(appBin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (string path in candidates) return path;
        }
        string cli = Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(cli)) return cli;
        throw new FileNotFoundException("Local Codex app-server was not found.");
    }

    private static async Task Send(Process process, object value)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> WaitFor(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("Codex app-server closed the connection.");
            JsonDocument doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var found) && found.TryGetInt32(out int responseId) && responseId == id)
            {
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    string message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "App-server error" : "App-server error";
                    doc.Dispose();
                    throw new InvalidOperationException(message);
                }
                return doc;
            }
            doc.Dispose();
        }
    }
}

internal sealed class AlertStore
{
    private readonly string _path;
    private readonly Dictionary<string, HashSet<int>> _sent = new();

    public AlertStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MWInteractive", "CodexLimitMonitor", "alerts.json");
        try
        {
            if (File.Exists(_path))
                foreach (var item in JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(_path)) ?? new())
                    _sent[item.Key] = item.Value.ToHashSet();
        }
        catch (Exception) { /* Damaged cache only resets notification history. */ }
    }

    public IEnumerable<(string Name, int Threshold)> NewAlerts(Snapshot snapshot)
    {
        foreach (var (name, limit) in new[] { ("5h", snapshot.FiveHour), ("Week", snapshot.Weekly) })
        {
            if (limit is null) continue;
            string prefix = name + ":";
            string key = prefix + (limit.Reset?.ToUnixTimeSeconds().ToString() ?? "unknown");
            foreach (string stale in _sent.Keys.Where(x => x.StartsWith(prefix) && x != key).ToArray()) _sent.Remove(stale);
            if (!_sent.TryGetValue(key, out var sent)) _sent[key] = sent = new HashSet<int>();
            foreach (int threshold in new[] { 10, 5, 2 })
                if (limit.Remaining <= threshold && sent.Add(threshold)) yield return (name, threshold);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_sent.ToDictionary(x => x.Key, x => x.Value.ToArray())));
    }
}

internal static class MonitorPresentation
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    public static bool IsStale(Snapshot? snapshot, DateTimeOffset now) =>
        snapshot is not null && now - snapshot.FetchedAt > TimeSpan.FromMinutes(3);

    public static string Tooltip(Snapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return "Codex Limit Monitor\nNo data";
        string title = IsStale(snapshot, now) ? "Codex · STALE" : AppInfo.Name;
        string limits = snapshot.FiveHour is null
            ? $"Week: {FormatRemaining(snapshot.Weekly)} remaining"
            : $"5h: {FormatRemaining(snapshot.FiveHour)} | Week: {FormatRemaining(snapshot.Weekly)}";
        string reset = snapshot.Weekly?.Reset is null ? "" :
            "\nReset " + snapshot.Weekly.Reset.Value.LocalDateTime.ToString("MMM d h:mm tt", English);
        return $"{title}\n{limits}{reset}";
    }

    private static string FormatRemaining(Limit? limit) => limit is null ? "—" : $"{limit.Remaining}%";

    public static string FormatReset(DateTimeOffset? reset) => reset is null ? "Unknown" :
        reset.Value.LocalDateTime.ToString("MMM d, h:mm tt", English);
}

internal static class UserMessages
{
    public const string CodexUnavailable = "Codex is not available. Make sure the ChatGPT/Codex desktop app is installed and you are signed in.";
    public const string ReadFailed = "Unable to read Codex usage data. The app will retry automatically.";
    public static string For(Exception error) => error is FileNotFoundException ? CodexUnavailable : ReadFailed;
}

internal static class DiagnosticsText
{
    public static string Build(Snapshot? snapshot, string codexVersion, string connectionState,
        string? lastError, string codexPath, string? gitPath, string startupResult, DateTimeOffset now,
        StartupInspection startup, string executable)
    {
        string windows = snapshot?.Windows is { Count: > 0 } list
            ? string.Join(", ", list.Select(x => x.DurationMinutes switch
            {
                >= 240 and <= 360 => $"5-hour ({x.Remaining}% remaining)",
                >= 9_000 and <= 11_000 => $"Weekly ({x.Remaining}% remaining)",
                _ => $"{x.DurationMinutes}-minute ({x.Remaining}% remaining)"
            })) : "No data";
        return $"Application version: {AppInfo.Version}\r\n" +
               $"Codex version: {codexVersion}\r\n" +
               $"Connection status: {connectionState}\r\n" +
               $"Last successful update: {(snapshot is null ? "Never" : snapshot.FetchedAt.LocalDateTime.ToString("MMM d, yyyy h:mm:ss tt", CultureInfo.GetCultureInfo("en-US")))}\r\n" +
               $"Available limit windows: {windows}\r\n" +
               $"Stale: {(MonitorPresentation.IsStale(snapshot, now) ? "Yes" : "No")}\r\n" +
               $"Last error: {lastError ?? "None"}\r\n" +
               $"Codex executable path: {codexPath}\r\n" +
               $"Git executable path: {gitPath ?? "Not found"}\r\n" +
               "Git required: No (usage read)\r\n" +
               $"App-server startup result: {startupResult}\r\n" +
               $"Start with Windows: {(startup.Enabled ? "Enabled" : "Disabled")}\r\n" +
               $"Startup registry entry: {startup.DisplayState}\r\n" +
               $"Startup folder shortcut: {startup.ShortcutDisplayState}\r\n" +
               $"Executable: {executable}\r\n" +
               "Tray icon: Active";
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly StartupManager _startup = new();
    private readonly PreferencesStore _preferencesStore = new();
    private readonly UserPreferences _preferences;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autostart;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _freshnessTimer;
    private readonly AlertStore _alerts;
    private readonly LimitPopup _popup;
    private readonly CancellationTokenSource _shutdown = new();
    private Snapshot? _snapshot;
    private bool _refreshing;
    private bool _changingAutostart;
    private string _connectionState = "Connecting";
    private string _startupResult = "Not started";
    private string? _lastError;

    public TrayApp()
    {
        _icon = new NotifyIcon { Icon = DrawIcon("--"), Text = AppInfo.Name + " - Connecting", Visible = true };
        StartupLog.StartRun();
        StartupLog.Write($"Start. Version={AppInfo.Version}; EXE={Application.ExecutablePath}");
        StartupLog.Write("Launch source: unknown; HKCU Run registration checked by tray initialization");
        StartupLog.Write("Tray icon created");
        _alerts = new AlertStore();
        bool preferencesExist = _preferencesStore.Exists;
        bool usageHistoryExists = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MWInteractive", "CodexLimitMonitor", "alerts.json"));
        _preferences = _preferencesStore.Load();
        var startupEntry = _startup.Inspect(Application.ExecutablePath);
        StartupLog.Write("HKCU Run registration: " + startupEntry.DisplayState);
        if (_preferences.StartupIntent is null && startupEntry.Enabled)
        {
            _preferences.StartupIntent = true;
            _preferencesStore.Save(_preferences);
        }
        else if (_preferences.StartupIntent is null &&
                 FirstRunStartup.ShouldEnable(preferencesExist, usageHistoryExists, startupEntry))
        {
            try
            {
                _startup.Enable(Application.ExecutablePath);
                _preferences.StartupIntent = true;
                _preferencesStore.Save(_preferences);
                StartupLog.Write("First run: Start with Windows enabled");
            }
            catch (Exception ex) { StartupLog.Write("First-run startup setup failed: " + ex.GetType().Name); }
        }
        else if (_preferences.StartupIntent is null && startupEntry.State == StartupEntryState.InvalidPath)
        {
            _preferences.StartupIntent = true;
            _preferencesStore.Save(_preferences);
        }
        _popup = new LimitPopup(() => _ = RefreshAsync());
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open", null, (_, _) => ShowPopup());
        _menu.Items.Add("Refresh", null, (_, _) => _ = RefreshAsync());
        _menu.Items.Add("Diagnostics", null, async (_, _) => await ShowDiagnosticsAsync());
        _autostart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = _startup.Inspect(Application.ExecutablePath).Enabled };
        _autostart.CheckedChanged += (_, _) => { if (!_changingAutostart) SetAutostart(_autostart.Checked); };
        _menu.Items.Add(_autostart);
        _menu.Opening += (_, _) => RefreshAutostartCheck();
        _menu.Items.Add("Keep visible next to clock...", null, (_, _) => ShowTrayGuidance());
        _menu.Items.Add("About", null, (_, _) => new AboutForm().ShowDialog());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon.ContextMenuStrip = _menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        _timer = new System.Windows.Forms.Timer { Interval = 60_000, Enabled = true };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _freshnessTimer = new System.Windows.Forms.Timer { Interval = 15_000, Enabled = true };
        _freshnessTimer.Tick += (_, _) => UpdatePresentation();
        var firstRefresh = new System.Windows.Forms.Timer { Interval = 100 };
        firstRefresh.Tick += (_, _) =>
        {
            firstRefresh.Stop();
            firstRefresh.Dispose();
            _ = RefreshAsync();
        };
        firstRefresh.Start();
        var introduction = new System.Windows.Forms.Timer { Interval = 900 };
        introduction.Tick += (_, _) =>
        {
            introduction.Stop();
            introduction.Dispose();
            ShowStartupIntroduction();
        };
        introduction.Start();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _popup.SetBusy(true);
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                _connectionState = attempt == 1 ? "Connecting" : $"Reconnecting ({attempt}/3)";
                UpdatePresentation();
                try
                {
                    StartupLog.Write("App-server connection attempt " + attempt);
                    _snapshot = await Task.Run(() => CodexReader.ReadAsync(_shutdown.Token,
                        status => _startupResult = status), _shutdown.Token);
                    _connectionState = "Connected";
                    _lastError = null;
                    StartupLog.Write("App-server connection succeeded");
                    break;
                }
                catch (Exception ex) when (attempt < 3 && !_shutdown.IsCancellationRequested)
                {
                    StartupLog.Write("App-server connection failed: " + ex.GetType().Name);
                    _lastError = UserMessages.For(ex);
                    UpdatePresentation();
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), _shutdown.Token);
                }
            }
            UpdatePresentation();
            var current = _snapshot!;
            var newAlerts = _alerts.NewAlerts(current).ToArray();
            foreach (var alert in newAlerts)
            {
                _icon.BalloonTipTitle = $"{AppInfo.Name} - {alert.Name} limit";
                _icon.BalloonTipText = $"{(alert.Name == "5h" ? current.FiveHour!.Remaining : current.Weekly!.Remaining)}% remaining (threshold: {alert.Threshold}%).";
                _icon.ShowBalloonTip(5000);
                if (newAlerts.Length > 1) await Task.Delay(TimeSpan.FromSeconds(6), _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StartupLog.Write("App-server connection failed: " + ex.GetType().Name);
            _lastError = UserMessages.For(ex);
            _connectionState = "Disconnected";
            UpdatePresentation();
        }
        finally { _refreshing = false; _popup.SetBusy(false); }
    }

    private void UpdatePresentation()
    {
        bool stale = MonitorPresentation.IsStale(_snapshot, DateTimeOffset.Now);
        string number = (_snapshot?.FiveHour, _snapshot?.Weekly) switch
        {
            (not null, not null) => Math.Min(_snapshot.FiveHour.Remaining, _snapshot.Weekly.Remaining).ToString("00"),
            (not null, null) => _snapshot!.FiveHour!.Remaining.ToString("00"),
            (null, not null) => _snapshot!.Weekly!.Remaining.ToString("00"),
            _ => "--"
        };
        var old = _icon.Icon;
        _icon.Icon = DrawIcon(number);
        old?.Dispose();
        _icon.Text = FitTooltip(MonitorPresentation.Tooltip(_snapshot, DateTimeOffset.Now));
        _popup.UpdateSnapshot(_snapshot, _lastError, stale);
    }

    private static string FitTooltip(string text) => text.Length <= 63 ? text : text[..63];

    private async Task ShowDiagnosticsAsync()
    {
        string version;
        try { version = await CodexReader.GetVersionAsync(); }
        catch (Exception) { version = "Unavailable"; }
        string path;
        try { path = CodexReader.GetExecutablePath(); }
        catch (Exception) { path = "Not found"; }
        string BuildBody() => DiagnosticsText.Build(_snapshot, version, _connectionState, _lastError,
            path, CodexReader.GetGitExecutablePath(), _startupResult, DateTimeOffset.Now,
            _startup.Inspect(Application.ExecutablePath), Application.ExecutablePath);
        new DiagnosticsForm(BuildBody, () => SetAutostart(true)).ShowDialog();
    }

    private void ShowPopup()
    {
        _popup.ShowNearTray();
        _popup.Activate();
    }

    internal static Icon DrawIcon(string text)
    {
        using var bitmap = RenderIconBitmap(text, 128);
        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    internal static Bitmap RenderIconBitmap(string text, int size)
    {
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(22, 31, 41));
            g.FillRoundedRectangle(bg, new Rectangle(0, 0, size - 1, size - 1), Math.Max(2, size / 5));
            using var family = new FontFamily("Segoe UI");
            using var glyphs = new GraphicsPath();
            glyphs.AddString(text, family, (int)FontStyle.Bold, 100, Point.Empty, StringFormat.GenericTypographic);
            RectangleF bounds = glyphs.GetBounds();
            float targetWidth = size * (text.Length switch { 1 => 0.70f, 2 => 0.90f, _ => 0.84f });
            float targetHeight = size * 0.82f;
            float scale = Math.Min(targetWidth / bounds.Width, targetHeight / bounds.Height);
            using var transform = new Matrix();
            transform.Translate(-bounds.X, -bounds.Y);
            transform.Scale(scale, scale, MatrixOrder.Append);
            transform.Translate((size - bounds.Width * scale) / 2f,
                (size - bounds.Height * scale) / 2f - size * 0.01f, MatrixOrder.Append);
            glyphs.Transform(transform);
            using var fg = new SolidBrush(Color.White);
            g.FillPath(fg, glyphs);
        }
        return bitmap;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr handle);

    private void RefreshAutostartCheck()
    {
        _changingAutostart = true;
        _autostart.Checked = _startup.Inspect(Application.ExecutablePath).Enabled;
        _changingAutostart = false;
    }

    private void SetAutostart(bool value)
    {
        try
        {
            if (value) _startup.Enable(Application.ExecutablePath);
            else _startup.Disable();
            _preferences.StartupIntent = value;
            _preferences.StartupWarningShown = false;
            _preferencesStore.Save(_preferences);
            RefreshAutostartCheck();
        }
        catch (Exception)
        {
            RefreshAutostartCheck();
            MessageBox.Show("Could not update the Windows startup setting.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowStartupIntroduction()
    {
        var state = _startup.Inspect(Application.ExecutablePath);
        if (_preferences.StartupIntent == true && state.State != StartupEntryState.Ok && !_preferences.StartupWarningShown)
        {
            using var warning = new StartupWarningForm();
            if (warning.ShowDialog() == DialogResult.Yes) SetAutostart(true);
            else
            {
                _preferences.StartupWarningShown = true;
                _preferencesStore.Save(_preferences);
            }
        }
        if (!_preferences.OnboardingCompleted)
        {
            using var onboarding = new OnboardingForm(_preferences.StartupIntent != false && !_preferences.StartupWarningShown, ShowTrayGuidance);
            if (onboarding.ShowDialog() == DialogResult.OK)
            {
                if (onboarding.StartupSelected != _startup.Inspect(Application.ExecutablePath).Enabled)
                    SetAutostart(onboarding.StartupSelected);
                _preferences.OnboardingCompleted = true;
                _preferencesStore.Save(_preferences);
            }
        }
    }

    private void ShowTrayGuidance()
    {
        OpenTaskbarSettings();
        if (_preferences.TrayGuidanceDismissed) return;
        using var guidance = new TrayGuidanceForm(() => { OpenTaskbarSettings(); });
        if (guidance.ShowDialog() == DialogResult.OK)
        {
            _preferences.TrayGuidanceDismissed = true;
            _preferencesStore.Save(_preferences);
        }
    }

    internal static bool OpenTaskbarSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); return true; }
        catch (Exception) { MessageBox.Show("Could not open Windows taskbar settings.", AppInfo.Name); return false; }
    }

    protected override void ExitThreadCore()
    {
        _shutdown.Cancel();
        _timer.Stop();
        _timer.Dispose();
        _freshnessTimer.Stop();
        _freshnessTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _popup.Close();
        base.ExitThreadCore();
    }
}

internal sealed class LimitPopup : Form
{
    private readonly Label _five = new(), _week = new(), _fiveReset = new(), _weekReset = new(), _updated = new();
    private readonly ProgressBar _fiveBar = new(), _weekBar = new();
    private readonly Button _refresh = new();

    public LimitPopup(Action refresh)
    {
        Text = AppInfo.Name;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(402, 371);
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = AppInfo.Name, Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(18, 13), AutoSize = true });
        Configure(_five, 18, 54, 366);
        Configure(_fiveReset, 18, 80, 366);
        Configure(_fiveBar, 18, 107, 366);
        Configure(_week, 18, 141, 366);
        Configure(_weekReset, 18, 167, 366);
        Configure(_weekBar, 18, 194, 366);
        Configure(_updated, 18, 239, 268);
        _five.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _week.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _fiveReset.ForeColor = Color.FromArgb(78, 91, 105);
        _weekReset.ForeColor = Color.FromArgb(78, 91, 105);
        _updated.Font = new Font("Segoe UI", 8.5f);
        _refresh.Text = "Refresh";
        _refresh.SetBounds(305, 233, 79, 30);
        _refresh.Click += (_, _) => refresh();
        Controls.Add(_refresh);
        var banner = new Panel
        {
            Name = "MwInteractiveBanner",
            AccessibleName = "MW Interactive app banner",
            Location = new Point(18, 284), Size = new Size(366, 70),
            BackColor = Color.FromArgb(229, 244, 238), BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand
        };
        var title = new Label
        {
            Text = "Discover more apps by MW Interactive",
            Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(28, 65, 58),
            Location = new Point(13, 10), Size = new Size(338, 21), Cursor = Cursors.Hand
        };
        var subtitle = new Label
        {
            Text = "Simple tools for Android", Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(66, 96, 88), Location = new Point(13, 37),
            Size = new Size(164, 19), Cursor = Cursors.Hand
        };
        var link = new LinkLabel
        {
            Text = "View on Google Play ↗", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            LinkColor = Color.FromArgb(15, 96, 76), ActiveLinkColor = Color.FromArgb(8, 74, 58),
            Location = new Point(190, 37), Size = new Size(161, 19)
        };
        banner.Click += (_, _) => BrandLinks.OpenPlayStore();
        title.Click += (_, _) => BrandLinks.OpenPlayStore();
        subtitle.Click += (_, _) => BrandLinks.OpenPlayStore();
        link.LinkClicked += (_, _) => BrandLinks.OpenPlayStore();
        banner.Controls.Add(title);
        banner.Controls.Add(subtitle);
        banner.Controls.Add(link);
        Controls.Add(banner);
        UpdateSnapshot(null, null);
        Deactivate += (_, _) => Hide();
    }

    private void Configure(Control c, int x, int y, int width)
    {
        c.SetBounds(x, y, width, c is ProgressBar ? 16 : 25);
        Controls.Add(c);
    }

    public void UpdateSnapshot(Snapshot? value, string? error, bool stale = false)
    {
        _five.Text = Format("5-hour limit", value?.FiveHour);
        _week.Text = Format("Weekly limit", value?.Weekly);
        _fiveReset.Text = value?.FiveHour is null ? "" : "Reset: " + MonitorPresentation.FormatReset(value.FiveHour.Reset);
        _weekReset.Text = value?.Weekly is null ? "" : "Reset: " + MonitorPresentation.FormatReset(value.Weekly.Reset);
        _fiveBar.Value = value?.FiveHour?.Remaining ?? 0;
        _weekBar.Value = value?.Weekly?.Remaining ?? 0;
        _updated.Text = value is null ? (error is null ? "Connecting..." : "No data - see Diagnostics") :
            $"Last updated: {value.FetchedAt.LocalDateTime:HH:mm:ss}" + (stale ? " · STALE" : "");
    }

    private static string Format(string title, Limit? limit) => limit is null
        ? $"{title}: —"
        : $"{title}: {limit.Remaining}% remaining";

    public void SetBusy(bool busy) => _refresh.Enabled = !busy;

    public void ShowNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        Show();
    }
}

internal sealed class DiagnosticsForm : Form
{
    public DiagnosticsForm(Func<string> getText, Action repairStartup)
    {
        Text = AppInfo.Name + " - Diagnostics";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(680, 382);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(248, 250, 252);
        var details = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = getText(), Location = new Point(18, 18), Size = new Size(644, 304),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(details);
        var copy = new Button { Text = "Copy diagnostics", Location = new Point(506, 336), Size = new Size(156, 30) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(details.Text); copy.Text = "Copied"; }
            catch (Exception) { MessageBox.Show("Could not copy diagnostics to the clipboard.", AppInfo.Name); }
        };
        Controls.Add(copy);
        var repair = new Button { Text = "Repair startup", Location = new Point(18, 336), Size = new Size(145, 30) };
        repair.Click += (_, _) => { repairStartup(); details.Text = getText(); };
        Controls.Add(repair);
    }
}

internal sealed class StartupWarningForm : Form
{
    public StartupWarningForm()
    {
        Text = AppInfo.Name;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(410, 132);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "Start with Windows is no longer enabled.", Location = new Point(20, 22), AutoSize = true });
        Controls.Add(new Button { Text = "Restore startup", DialogResult = DialogResult.Yes, Location = new Point(125, 78), Size = new Size(130, 32) });
        Controls.Add(new Button { Text = "Ignore", DialogResult = DialogResult.No, Location = new Point(270, 78), Size = new Size(120, 32) });
    }
}

internal sealed class TrayGuidanceForm : Form
{
    public TrayGuidanceForm(Action openSettings)
    {
        Text = "Keep visible next to clock";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(470, 174);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "To keep Codex Limit Monitor visible next to the clock,\nenable it under Other system tray icons.", Location = new Point(20, 22), Size = new Size(430, 62) });
        var open = new Button { Text = "Open Windows settings", Location = new Point(20, 113), Size = new Size(190, 34) };
        open.Click += (_, _) => openSettings();
        Controls.Add(open);
        Controls.Add(new Button { Text = "Don't show again", DialogResult = DialogResult.OK, Location = new Point(275, 113), Size = new Size(175, 34) });
    }
}

internal sealed class OnboardingForm : Form
{
    private readonly CheckBox _startup;
    public bool StartupSelected => _startup.Checked;

    public OnboardingForm(bool startupEnabled, Action showTrayGuidance)
    {
        Text = "Welcome to " + AppInfo.Name;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 220);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "Codex Limit Monitor is running in the Windows system tray.", Location = new Point(20, 22), Size = new Size(420, 44) });
        Controls.Add(new Label { Text = "Recommended:", Location = new Point(20, 78), AutoSize = true });
        _startup = new CheckBox { Text = "Start with Windows", Checked = startupEnabled, Location = new Point(20, 110), AutoSize = true };
        Controls.Add(_startup);
        var visible = new Button { Text = "Keep visible next to clock", Location = new Point(20, 160), Size = new Size(225, 34) };
        visible.Click += (_, _) => showTrayGuidance();
        Controls.Add(visible);
        Controls.Add(new Button { Text = "Done", DialogResult = DialogResult.OK, Location = new Point(344, 160), Size = new Size(95, 34) });
    }
}

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = AppInfo.Name + " - About";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(430, 280);
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = AppInfo.Name, Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(22, 19), AutoSize = true });
        Controls.Add(new Label { Text = "Version " + AppInfo.Version, Location = new Point(23, 57), AutoSize = true });
        Controls.Add(new Label
        {
            Text = "A lightweight Windows tray utility for monitoring\nyour Codex usage limits.",
            Location = new Point(23, 96), Size = new Size(385, 48)
        });
        Controls.Add(new Label
        {
            Text = "Unofficial community tool.\nNot affiliated with or endorsed by OpenAI.\n\nCreated by MW Interactive.",
            Location = new Point(23, 154), Size = new Size(385, 78)
        });
        var link = new LinkLabel { Text = "More apps by MW Interactive", Location = new Point(23, 244), AutoSize = true };
        link.LinkClicked += (_, _) => BrandLinks.OpenPlayStore();
        Controls.Add(link);
    }
}

internal static class BrandLinks
{
    public static void OpenPlayStore()
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.MwInteractivePlayStoreUrl) { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show("Could not open Google Play.", AppInfo.Name); }
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
