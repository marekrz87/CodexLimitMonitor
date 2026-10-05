using System.Text.Json;
using CodexLimitMonitor;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}

if (args.Contains("--integration-no-git"))
{
    string? originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Environment.SetEnvironmentVariable("PATH", Path.Combine(windows, "System32") + Path.PathSeparator + windows);
        string startup = "Not started";
        var live = await CodexReader.ReadAsync(CancellationToken.None, status => startup = status);
        Check(live.Windows is { Count: > 0 }, "live app-server read without Git in PATH");
        Check(startup == "Initialized" && CodexReader.GetGitExecutablePath() is null, "app-server initializes with no Git executable");
        Check((await CodexReader.GetVersionAsync()).StartsWith("codex-cli "), "Codex version without Git in PATH");
        Console.WriteLine($"Live limits: 5-hour={live.FiveHour?.Remaining.ToString() ?? "unavailable"}, weekly={live.Weekly?.Remaining.ToString() ?? "unavailable"}");
    }
    finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
    return;
}

if (args.Contains("--integration-startup"))
{
    string executable = args.SkipWhile(x => x != "--integration-startup").Skip(1).First();
    var manager = new StartupManager();
    try
    {
        manager.Disable();
        var off = manager.Inspect(executable);
        Check(!off.Enabled && off.State == StartupEntryState.Missing && off.ShortcutState == StartupShortcutState.Missing,
            "real Windows startup OFF removes Run and startup shortcut");
        manager.Enable(executable);
        var on = manager.Inspect(executable);
        Check(on.Enabled && on.State == StartupEntryState.Ok && on.ShortcutState == StartupShortcutState.Ok,
            "real Windows startup ON points both mechanisms to current EXE");
    }
    finally { manager.Enable(executable); }
    Console.WriteLine($"Startup integration: {passed} tests passed");
    return;
}

using (var doc = JsonDocument.Parse("""
    {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":45,"windowDurationMins":10080,"resetsAt":1790410868},"secondary":null}}}
    """))
{
    var parsed = CodexReader.Parse(doc.RootElement);
    Check(parsed.Weekly?.Remaining == 55 && parsed.FiveHour is null, "weekly-only live schema");
    Check(parsed.Windows?.Count == 1 && parsed.Windows[0].Path == "rateLimitsByLimitId.codex.primary", "diagnostic window inventory");
    Check(MonitorPresentation.Tooltip(parsed, parsed.FetchedAt).StartsWith("Codex Limit Monitor\nWeek: 55% remaining\nReset "), "weekly tooltip without missing 5h");
    Check(MonitorPresentation.Tooltip(parsed, parsed.FetchedAt).Length <= 63, "weekly tooltip fits Windows limit");
    Check(!MonitorPresentation.IsStale(parsed, parsed.FetchedAt.AddMinutes(3)), "fresh at three minutes");
    Check(MonitorPresentation.IsStale(parsed, parsed.FetchedAt.AddMinutes(3).AddSeconds(1)), "stale after three minutes");
    Check(MonitorPresentation.Tooltip(parsed, parsed.FetchedAt.AddMinutes(4)).Contains("STALE"), "stale tooltip");
}
using (var doc = JsonDocument.Parse("""
    {"rateLimits":{"primary":{"usedPercent":91,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":96,"windowDurationMins":10080,"resetsAt":1800100000}}}
    """))
{
    var parsed = CodexReader.Parse(doc.RootElement);
    Check(parsed.FiveHour?.Remaining == 9 && parsed.Weekly?.Remaining == 4, "both windows and legacy bucket");
    Check(MonitorPresentation.Tooltip(parsed, parsed.FetchedAt).Contains("5h: 9% | Week: 4%"), "tooltip with both windows");
}
using (var doc = JsonDocument.Parse("""
    {"rateLimitsByLimitId":{"codex":{"unexpected":{"nested":{"usedPercent":88,"windowDurationMins":300,"resetsAt":1800000000}},"primary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1800100000}}}}
    """))
{
    var parsed = CodexReader.Parse(doc.RootElement);
    Check(parsed.FiveHour?.Remaining == 12 && parsed.Windows?.Count == 2, "find nested window without assuming field name");
}

string path = Path.Combine(Path.GetTempPath(), "CodexLimitMonitor-tests-" + Guid.NewGuid().ToString("N"), "alerts.json");
try
{
    var reset = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    var store = new AlertStore(path);
    Snapshot One(int five, int week, DateTimeOffset fiveReset = default) =>
        new(new Limit(five, fiveReset == default ? reset : fiveReset, 300), new Limit(week, reset, 10080), DateTimeOffset.Now);
    Check(store.NewAlerts(One(9, 4)).Count() == 3, "separate 10/5 thresholds");
    Check(!store.NewAlerts(One(9, 4)).Any(), "no duplicate alerts on refresh");
    Check(store.NewAlerts(One(1, 1)).Count() == 3, "lower thresholds trigger once");
    Check(!new AlertStore(path).NewAlerts(One(1, 1)).Any(), "alert history persists after restart");
    Check(new AlertStore(path).NewAlerts(One(9, 1, reset.AddHours(5))).Count() == 1, "new window resets its thresholds");
}
finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
Check(typeof(AppInfo).Assembly.GetName().Version?.ToString() == "1.0.0.0", "assembly version 1.0.0");
using (var sheet = new Bitmap(6 * 112, 3 * 112))
using (var canvas = Graphics.FromImage(sheet))
{
    canvas.Clear(Color.White);
    string[] samples = ["5", "9", "10", "53", "99", "100"];
    int[] sizes = [16, 20, 24];
    for (int row = 0; row < sizes.Length; row++)
    for (int column = 0; column < samples.Length; column++)
    {
        using var source = TrayApp.RenderIconBitmap(samples[column], 128);
        using var scaled = new Bitmap(sizes[row], sizes[row]);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }
        int minX = scaled.Width, minY = scaled.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < scaled.Height; y++)
        for (int x = 0; x < scaled.Width; x++)
        {
            var pixel = scaled.GetPixel(x, y);
            if (pixel.R > 180 && pixel.G > 180 && pixel.B > 180 && pixel.A > 100)
            {
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        }
        Check(minX >= 1 && minY >= 1 && maxX < scaled.Width - 1 && maxY < scaled.Height - 1,
            $"icon {samples[column]} at {sizes[row]} px has no clipped glyph");
        canvas.InterpolationMode = InterpolationMode.NearestNeighbor;
        canvas.DrawImage(scaled, new Rectangle(column * 112 + 16, row * 112 + 16, 80, 80));
        canvas.DrawString($"{samples[column]} / {sizes[row]}px", SystemFonts.DefaultFont,
            Brushes.Black, column * 112 + 12, row * 112 + 98);
    }
    if (args.Contains("--capture-icons"))
    {
        string output = args.SkipWhile(x => x != "--capture-icons").Skip(1).First();
        sheet.Save(output, ImageFormat.Png);
        Console.WriteLine("Icon sheet: " + output);
    }
}
var info = CodexReader.CreateStartInfo("C:\\example\\codex.exe", "app-server --stdio");
Check(!info.Environment["PATH"]!.Contains("Git", StringComparison.OrdinalIgnoreCase), "app-server PATH excludes Git");
Check(info.WorkingDirectory == Path.GetTempPath(), "app-server starts outside project folder");
Check(UserMessages.For(new FileNotFoundException()) == UserMessages.CodexUnavailable &&
      UserMessages.For(new IOException()) == UserMessages.ReadFailed, "friendly public errors");
string diagnostic = DiagnosticsText.Build(null, "codex-cli test", "Disconnected", UserMessages.ReadFailed,
    "C:\\example\\codex.exe", null, "Failed to start", DateTimeOffset.Now,
    new StartupInspection(StartupEntryState.Missing, null), Application.ExecutablePath);
Check(diagnostic.Contains("Application version: 1.0.0") && diagnostic.Contains("Git required: No") &&
      diagnostic.Contains("App-server startup result: Failed to start"), "public diagnostics fields");
Check(diagnostic.Contains("Start with Windows: Disabled") && diagnostic.Contains("Startup registry entry: Missing") &&
      diagnostic.Contains("Startup folder shortcut: Missing") && diagnostic.Contains("Tray icon: Active"), "startup diagnostics fields");
string shortcutPath = Path.Combine(Path.GetTempPath(), "CodexLimitMonitorTests-" + Guid.NewGuid().ToString("N"), "startup.lnk");
var shortcutProbe = new StartupShortcut(shortcutPath);
string shortcutProbeExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "whoami.exe");
try
{
    Check(shortcutProbe.Inspect(shortcutProbeExecutable) == StartupShortcutState.Missing, "startup shortcut initially missing");
    shortcutProbe.Enable(shortcutProbeExecutable);
    Check(shortcutProbe.Inspect(shortcutProbeExecutable) == StartupShortcutState.Ok, "startup shortcut points to executable");
    Check(shortcutProbe.Inspect(Application.ExecutablePath) == StartupShortcutState.InvalidPath, "startup shortcut detects stale executable");
}
finally { shortcutProbe.Disable(); Directory.Delete(Path.GetDirectoryName(shortcutPath)!); }
Check(shortcutProbe.Inspect(shortcutProbeExecutable) == StartupShortcutState.Missing, "startup shortcut removed");
Check(FirstRunStartup.ShouldEnable(false, false, new StartupInspection(StartupEntryState.Missing, null)),
      "clean first run enables Windows startup by default");
Check(!FirstRunStartup.ShouldEnable(true, false, new StartupInspection(StartupEntryState.Missing, null)) &&
      !FirstRunStartup.ShouldEnable(false, true, new StartupInspection(StartupEntryState.Missing, null)) &&
      !FirstRunStartup.ShouldEnable(false, false, new StartupInspection(StartupEntryState.InvalidPath, null)),
      "existing installation and stale entry are not silently repaired");
string testKeyPath = @"Software\MWInteractive\CodexLimitMonitorTests\" + Guid.NewGuid().ToString("N");
var startupManager = new StartupManager(testKeyPath, "CodexLimitMonitor");
try
{
    startupManager.Enable(Application.ExecutablePath);
    Check(startupManager.Inspect(Application.ExecutablePath).Enabled, "Start with Windows ON and current EXE");
    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKeyPath))
        Check((string?)key?.GetValue("CodexLimitMonitor") == $"\"{Application.ExecutablePath}\"", "HKCU Run value points to current EXE");
    startupManager.Disable();
    Check(startupManager.Inspect(Application.ExecutablePath).State == StartupEntryState.Missing, "Start with Windows OFF removes HKCU value");
    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKeyPath))
        key.SetValue("CodexLimitMonitor", "\"C:\\old\\CodexLimitMonitor.exe\"");
    Check(startupManager.Inspect(Application.ExecutablePath).State == StartupEntryState.InvalidPath, "old startup path detected");
    startupManager.Enable(Application.ExecutablePath);
    Check(startupManager.Inspect(Application.ExecutablePath).Enabled, "Repair startup corrects old path");
}
finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKeyPath, false); }
string preferencesPath = Path.Combine(Path.GetTempPath(), "CodexLimitMonitor-prefs-" + Guid.NewGuid().ToString("N"), "preferences.json");
try
{
    var preferencesStore = new PreferencesStore(preferencesPath);
    preferencesStore.Save(new UserPreferences { StartupIntent = true, OnboardingCompleted = true, TrayGuidanceDismissed = true });
    var loaded = preferencesStore.Load();
    Check(loaded.StartupIntent == true && loaded.OnboardingCompleted && loaded.TrayGuidanceDismissed, "onboarding preferences survive restart");
}
finally { Directory.Delete(Path.GetDirectoryName(preferencesPath)!, true); }
Exception? uiError = null;
var uiThread = new Thread(() =>
{
    try
    {
        using var about = new AboutForm();
        Check(about.Controls.OfType<Label>().Any(x => x.Text == "Version 1.0.0") &&
              about.Controls.OfType<LinkLabel>().Any(x => x.Text == "More apps by MW Interactive"), "About content and link");
        using var diagnosticsForm = new DiagnosticsForm(() => diagnostic, () => { });
        Check(diagnosticsForm.Controls.OfType<Button>().Any(x => x.Text == "Copy diagnostics") &&
              diagnosticsForm.Controls.OfType<Button>().Any(x => x.Text == "Repair startup") &&
              diagnosticsForm.Controls.OfType<TextBox>().Any(x => x.Text.Contains("Codex executable path:")), "Diagnostics form and repair action");
        using var onboarding = new OnboardingForm(true, () => { });
        Check(onboarding.Controls.OfType<CheckBox>().Single().Checked &&
              onboarding.Controls.OfType<Button>().Any(x => x.Text == "Keep visible next to clock"), "onboarding recommendations");
        using var guidance = new TrayGuidanceForm(() => { });
        Check(guidance.Controls.OfType<Button>().Any(x => x.Text == "Open Windows settings"), "tray guidance controls");
        using var warning = new StartupWarningForm();
        Check(warning.Controls.OfType<Label>().Any(x => x.Text == "Start with Windows is no longer enabled."), "startup warning message");
        if (args.Contains("--test-settings"))
            Check(TrayApp.OpenTaskbarSettings(), "Keep visible opens Windows taskbar settings");
        using var popup = new LimitPopup(() => { });
        popup.UpdateSnapshot(new Snapshot(null, new Limit(54, DateTimeOffset.Now.AddDays(2), 10080), DateTimeOffset.Now), null);
        Check(popup.Controls.OfType<Label>().Any(x => x.Text == "5-hour limit: —"), "missing 5-hour limit is neutral in UI");
        var banner = popup.Controls.Find("MwInteractiveBanner", true).SingleOrDefault();
        Check(banner?.Controls.OfType<LinkLabel>().Any(x => x.Text == "View on Google Play ↗") == true, "MW Interactive banner visible in popup");
        if (args.Contains("--capture-popup"))
        {
            string output = args.SkipWhile(x => x != "--capture-popup").Skip(1).First();
            popup.Location = new Point(-2000, -2000);
            popup.Show();
            Application.DoEvents();
            using var image = new Bitmap(popup.Width, popup.Height);
            popup.DrawToBitmap(image, new Rectangle(Point.Empty, popup.Size));
            image.Save(output, ImageFormat.Png);
            popup.Hide();
            Console.WriteLine("Popup screenshot: " + output);
        }
    }
    catch (Exception ex) { uiError = ex; }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();
if (uiError is not null) throw uiError;
Console.WriteLine($"{passed} tests passed");
