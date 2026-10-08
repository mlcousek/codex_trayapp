using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Cache;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.Core.Updates;
using CodexTrayApp.Startup;
using CodexTrayApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Tests;

public sealed class SettingsUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Release_page_opens_only_on_request_even_when_checks_are_disabled(bool checksEnabled) => StaThread.Run(() =>
    {
        Verify("sample/tray", checksEnabled, withRelease: false, "https://github.com/sample/tray/releases/latest");
        Verify("sample/tray", checksEnabled, withRelease: true, "https://github.com/sample/tray/releases/tag/v0.2.0");
        return true;
    });

    [Fact]
    public void Unconfigured_build_disables_release_action_and_explains_availability() => StaThread.Run(() =>
    {
        Verify(null, checksEnabled: true, withRelease: false, expectedUrl: null);
        Verify(null, checksEnabled: true, withRelease: true, expectedUrl: null);
        return true;
    });

    [Fact]
    public void Invalid_connection_paths_do_not_replace_saved_preferences() => StaThread.Run(() =>
    {
        Verify(null, true, false, null, (model, settings) =>
        {
            settings.Save(settings.Current with { CodexHomePath = _root });
            model.CodexExecutableText = Path.Combine(_root, "missing.exe");
            model.CodexHomeText = _root;
            model.ApplyCodexPathsCommand.Execute(null);
            settings.Current.CodexHomePath.ShouldBe(_root);
            settings.Current.CodexExecutablePath.ShouldBeNull();
            model.FileError.ShouldNotBeNull().ShouldContain("existing Codex .exe");
            model.CodexExecutableText = string.Empty;
            model.CodexHomeText = "relative-home";
            model.ApplyCodexPathsCommand.Execute(null);
            settings.Current.CodexHomePath.ShouldBe(_root);
            model.FileError.ShouldNotBeNull().ShouldContain("absolute path");
            model.CodexHomeText = string.Empty;
            model.ApplyCodexPathsCommand.Execute(null);
            settings.Current.CodexHomePath.ShouldBeNull();
            model.FileError.ShouldBeNull();
        });
        return true;
    });

    private void Verify(string? repository, bool checksEnabled, bool withRelease, string? expectedUrl, Action<SettingsViewModel, SettingsStore>? inspect = null)
    {
        using var settings = new SettingsStore(Path.Combine(_root, "settings.json"), NullLogger<SettingsStore>.Instance);
        settings.Save(AppSettings.Default with { CheckForUpdates = checksEnabled });
        var pricing = new PricingProvider(Path.Combine(_root, "pricing.json"), NullLogger<PricingProvider>.Instance);
        using var poller = new UsagePoller(Substitute.For<IUsageProvider>(), Substitute.For<ISnapshotCache>(), new PollingOptions(), TimeProvider.System, NullLogger<UsagePoller>.Instance);
        var scanner = new JsonlScanner(_root, Substitute.For<IAnalyticsStore>(), TimeProvider.System, NullLogger<JsonlScanner>.Instance);
        using var analytics = new LocalAnalyticsProvider(scanner, _root, NullLogger<LocalAnalyticsProvider>.Instance);
        using var http = new HttpClient();
        var checker = new UpdateChecker(http, "0.1.0", NullLogger<UpdateChecker>.Instance, repository);
        var notifier = new UpdateNotifier();
        if (withRelease)
        {
            notifier.Report(UpdateCheckOutcome.Available(new ReleaseInfo(new Version(0, 2, 0), "v0.2.0", "https://github.com/sample/tray/releases/tag/v0.2.0")), DateTimeOffset.UtcNow);
        }

        var opened = new List<string>();
        using var model = new SettingsViewModel(settings, Substitute.For<IAutostartEntry>(), pricing, poller, Substitute.For<IHistoryStore>(), analytics,
            notifier, checker, Dispatcher.CurrentDispatcher, NullLogger<SettingsViewModel>.Instance, opened.Add);
        inspect?.Invoke(model, settings);

        opened.ShouldBeEmpty();
        model.OpenReleasePageCommand.CanExecute(null).ShouldBe(expectedUrl is not null);
        model.OpenReleasePageCommand.Execute(null);
        if (expectedUrl is null)
        {
            opened.ShouldBeEmpty();
            model.UpdateStatus.ShouldContain("unavailable");
            model.HasUpdate.ShouldBeFalse();
        }
        else
        {
            opened.ShouldHaveSingleItem().ShouldBe(expectedUrl);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
