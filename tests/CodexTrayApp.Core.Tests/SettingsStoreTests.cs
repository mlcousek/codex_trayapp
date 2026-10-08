using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Load_writes_the_defaults_when_there_is_no_file()
    {
        using var store = Create();

        var settings = store.Load();

        settings.ShouldBe(AppSettings.Default);
        File.Exists(SettingsPath).ShouldBeTrue();
        var json = File.ReadAllText(SettingsPath);
        json.ShouldContain("\"pollIntervalSeconds\": 300");
        json.ShouldContain("\"theme\": \"system\"");
        json.ShouldContain("\"trayWindow\": \"auto\"");
        json.ShouldContain("\"thresholds\": [");
        json.ShouldContain("\"maskEmail\": true");
        json.ShouldContain("\"showLocalAnalytics\": false");
        json.ShouldContain("\"showExtraUsage\": false");
        json.ShouldContain("\"showInactiveWindows\": false");
        store.LastError.ShouldBeNull();
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var settings = new AppSettings
        {
            PollIntervalSeconds = 600,
            TrayWindow = "codex:secondary",
            ChartRangeHours = 168,
            HistoryRetentionDays = 30,
            Notifications = new NotificationSettings { Enabled = true, Thresholds = [50, 90] },
            ShowLocalAnalytics = true,
            ShowExtraUsage = true,
            ShowInactiveWindows = true,
            MaskEmail = false,
            Theme = ThemeSetting.Dark,
            PricingFilePath = @"C:\prices\pricing.json",
        };

        using (var writer = Create())
        {
            writer.Save(settings);
        }

        using var reader = Create();
        reader.Load().ShouldBe(settings);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "pollIntervalSeconds": 10,
              "historyRetentionDays": 0,
              "chartRangeHours": 5,
              "trayWindow": "  ",
              "notifications": { "enabled": true, "thresholds": [150, 95, 80, 80, -1] },
              "pricingFilePath": "  ",
              "somethingFromTheFuture": true
            }
            """);
        using var store = Create();

        var settings = store.Load();

        settings.PollIntervalSeconds.ShouldBe(AppSettings.MinimumPollIntervalSeconds);
        settings.HistoryRetentionDays.ShouldBe(1);
        settings.ChartRangeHours.ShouldBe(24);
        settings.TrayWindow.ShouldBe("auto");
        settings.Notifications.Enabled.ShouldBeTrue();
        settings.Notifications.Thresholds.ShouldBe([80, 95]);
        settings.PricingFilePath.ShouldBeNull();
        store.LastError.ShouldBeNull();
    }

    [Fact]
    public void A_broken_file_is_left_alone_and_reported()
    {
        Directory.CreateDirectory(_directory);
        const string Broken = "{ \"pollIntervalSeconds\": 600, ";
        File.WriteAllText(SettingsPath, Broken);
        using var store = Create();

        var settings = store.Load();

        settings.ShouldBe(AppSettings.Default);
        store.LastError.ShouldNotBeNull().ShouldContain("not valid JSON");
        File.ReadAllText(SettingsPath).ShouldBe(Broken);
    }

    [Fact]
    public void Reload_ignores_its_own_write_and_picks_up_external_edits()
    {
        using var store = Create();
        var changes = new List<AppSettings>();
        store.Changed += (_, s) => changes.Add(s);
        store.Load();

        store.Reload().ShouldBeFalse();
        changes.ShouldBeEmpty();

        File.WriteAllText(SettingsPath, "{ \"pollIntervalSeconds\": 900, \"theme\": \"light\" }");
        store.Reload().ShouldBeTrue();

        store.Current.PollIntervalSeconds.ShouldBe(900);
        store.Current.Theme.ShouldBe(ThemeSetting.Light);
        changes.Count.ShouldBe(1);
        store.Reload().ShouldBeFalse();
    }

    [Fact]
    public void Update_persists_and_raises_once()
    {
        using var store = Create();
        store.Load();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Update(s => s with { MaskEmail = false });
        store.Update(s => s with { MaskEmail = false });

        raised.ShouldBe(1);
        File.ReadAllText(SettingsPath).ShouldContain("\"maskEmail\": false");
    }

    [Fact]
    public async Task External_edits_are_reloaded_by_the_watcher()
    {
        using var store = Create(TimeSpan.FromMilliseconds(50));
        store.Load();
        var changed = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, s) => changed.TrySetResult(s);
        store.StartWatching();

        File.WriteAllText(SettingsPath, "{ \"historyRetentionDays\": 45 }");

        var finished = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        finished.ShouldBe(changed.Task, "the watcher did not reload the file");
        (await changed.Task).HistoryRetentionDays.ShouldBe(45);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A watcher may still hold the folder for a moment; the temp folder is cleaned by the OS.
        }
    }

    private SettingsStore Create(TimeSpan? debounce = null) => new(SettingsPath, new ListLogger<SettingsStore>(), debounce);
}
