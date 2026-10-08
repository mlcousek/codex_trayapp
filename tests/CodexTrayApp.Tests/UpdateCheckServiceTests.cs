using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Updates;
using CodexTrayApp.Hosting;
using CodexTrayApp.Startup;
using CodexTrayApp.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>
/// One evaluation of the weekly update check: the setting gate, the due date, announcing a release once, and what a
/// failure leaves behind. GitHub is a stub handler; state and settings live in a temp folder. Announcements are
/// queued on the dispatcher, so each test pumps it and counts how often the tray icon was asked for.
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settings;
    private readonly UpdateCheckStateStore _state;
    private readonly UpdateNotifier _notifier = new();
    private readonly MutableClock _clock = new(Now);

    public UpdateCheckServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new SettingsStore(Path.Combine(_directory, "settings.json"), NullLogger<SettingsStore>.Instance);
        _settings.Load();
        _state = new UpdateCheckStateStore(Path.Combine(_directory, "update-check.json"), NullLogger<UpdateCheckStateStore>.Instance);
    }

    public void Dispose()
    {
        _settings.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Nothing_is_asked_while_the_setting_is_off()
    {
        _settings.Update(s => s with { CheckForUpdates = false });
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));

        var announced = RunOnce(github);

        github.Calls.ShouldBe(0);
        announced.ShouldBe(0);
        _notifier.Last.ShouldBeNull();
        _state.Load().ShouldBe(UpdateCheckState.Empty);
    }

    [Fact]
    public void Nothing_is_asked_before_a_check_is_due()
    {
        _state.Save(new UpdateCheckState(Now.AddDays(-1), null) { Repository = "mlcousek/codex_trayapp" });
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));

        RunOnce(github);

        github.Calls.ShouldBe(0);
        _notifier.Last.ShouldBeNull();
    }

    [Fact]
    public void A_newer_release_is_announced_once_and_remembered()
    {
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));

        var announced = RunOnce(github);

        github.Calls.ShouldBe(1);
        announced.ShouldBe(1);
        _notifier.Available.ShouldNotBeNull().Version.ShouldBe(new Version(0, 2, 0));
        _notifier.LastChecked.ShouldBe(Now);
        _state.Load().ShouldBe(new UpdateCheckState(Now, "0.2.0") { Repository = "mlcousek/codex_trayapp" });
    }

    [Fact]
    public void The_same_release_a_week_later_is_checked_but_not_announced_again()
    {
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));
        RunOnce(github);

        _clock.Now = Now.AddDays(7);
        var announced = RunOnce(github);

        github.Calls.ShouldBe(2);
        announced.ShouldBe(0);
        _notifier.Available.ShouldNotBeNull();
        _state.Load().ShouldBe(new UpdateCheckState(Now.AddDays(7), "0.2.0") { Repository = "mlcousek/codex_trayapp" });
    }

    [Fact]
    public void A_later_release_is_announced_in_its_turn()
    {
        _state.Save(new UpdateCheckState(Now.AddDays(-8), "0.2.0") { Repository = "mlcousek/codex_trayapp" });

        var announced = RunOnce(Answering(HttpStatusCode.OK, Latest("v0.3.0")));

        announced.ShouldBe(1);
        _state.Load().LastNotifiedVersion.ShouldBe("0.3.0");
    }

    [Fact]
    public void An_up_to_date_answer_announces_nothing()
    {
        var announced = RunOnce(Answering(HttpStatusCode.OK, Latest("v0.1.1")));

        announced.ShouldBe(0);
        _notifier.Last.ShouldNotBeNull().Status.ShouldBe(UpdateCheckStatus.UpToDate);
        _notifier.Available.ShouldBeNull();
        _state.Load().ShouldBe(new UpdateCheckState(Now, null) { Repository = "mlcousek/codex_trayapp" });
    }

    [Fact]
    public void A_failed_check_is_recorded_waits_a_week_and_keeps_what_was_announced()
    {
        _state.Save(new UpdateCheckState(Now.AddDays(-8), "0.1.5") { Repository = "mlcousek/codex_trayapp" });

        var announced = RunOnce(Answering(HttpStatusCode.InternalServerError, "{}"));

        announced.ShouldBe(0);
        _notifier.Last.ShouldNotBeNull().Status.ShouldBe(UpdateCheckStatus.Failed);
        _state.Load().ShouldBe(new UpdateCheckState(Now, "0.1.5") { Repository = "mlcousek/codex_trayapp" });
    }

    [Fact]
    public void A_transport_that_throws_becomes_a_failed_check_rather_than_an_exception()
    {
        var github = new StubHandler(_ => throw new HttpRequestException("no route to host"));

        var announced = RunOnce(github);

        announced.ShouldBe(0);
        _notifier.Last.ShouldNotBeNull().Status.ShouldBe(UpdateCheckStatus.Failed);
        _state.Load().LastCheckedUtc.ShouldBe(Now);
    }

    [Fact]
    public void The_announcement_names_both_versions() =>
        UpdateCheckService.FormatAvailable(new ReleaseInfo(new Version(0, 2, 0), "v0.2.0", "https://example.invalid"), new Version(0, 1, 1))
            .ShouldBe("Codex Usage Tray 0.2.0 is available. You are on 0.1.1.");

    private static string Latest(string tag) =>
        $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/mlcousek/codex_trayapp/releases/tag/{{tag}}"}""";

    private static StubHandler Answering(HttpStatusCode status, string body) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    /// <summary>Runs one evaluation on an STA thread, pumps its dispatcher, and returns how many announcements were made.</summary>
    private int RunOnce(StubHandler github, string? repository = "mlcousek/codex_trayapp") => StaThread.Run(() =>
    {
        using var http = new HttpClient(github);
        var checker = new UpdateChecker(http, "0.1.1", NullLogger<UpdateChecker>.Instance, repository);
        var services = Substitute.For<IServiceProvider>();
        var service = new UpdateCheckService(
            checker,
            _state,
            _settings,
            _notifier,
            services,
            Dispatcher.CurrentDispatcher,
            _clock,
            NullLogger<UpdateCheckService>.Instance);

        service.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        PumpDispatcher();

        // The substitute has no tray icon to give, so the announcement fails quietly after asking for it; the
        // question itself is what shows an announcement was attempted.
        return services.ReceivedCalls().Count(c => Equals(c.GetArguments()[0], typeof(TrayIconController)));
    });

    [Fact]
    public void Unconfigured_repository_is_unavailable_without_requests_or_due_state()
    {
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));
        RunOnce(github, repository: null).ShouldBe(0);
        github.Calls.ShouldBe(0);
        _notifier.Last.ShouldNotBeNull().Status.ShouldBe(UpdateCheckStatus.Unavailable);
        _notifier.LastChecked.ShouldBeNull();
        _state.Load().ShouldBe(UpdateCheckState.Empty);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Changing_repository_does_not_inherit_the_old_deadline_or_announcement()
    {
        _state.Save(new UpdateCheckState(Now.AddDays(-1), "0.2.0") { Repository = "old/tray" });
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));

        RunOnce(github, "new/tray").ShouldBe(1);

        github.Calls.ShouldBe(1);
        _state.Load().ShouldBe(new UpdateCheckState(Now, "0.2.0") { Repository = "new/tray" });
        _notifier.Available.ShouldNotBeNull().Url.ShouldBe("https://github.com/new/tray/releases/latest");
    }

    [Fact]
    public void Repository_casing_does_not_reset_the_weekly_deadline()
    {
        _state.Save(new UpdateCheckState(Now.AddDays(-1), "0.2.0") { Repository = "MLCOUSEK/CODEX_TRAYAPP" });
        var github = Answering(HttpStatusCode.OK, Latest("v0.3.0"));

        RunOnce(github).ShouldBe(0);

        github.Calls.ShouldBe(0);
    }

    [Fact]
    public void Removing_repository_clears_the_offer_without_overwriting_previous_due_state()
    {
        var github = Answering(HttpStatusCode.OK, Latest("v0.2.0"));
        RunOnce(github);
        var previous = _state.Load();

        RunOnce(github, null).ShouldBe(0);

        github.Calls.ShouldBe(1);
        _notifier.Available.ShouldBeNull();
        _notifier.LastChecked.ShouldBeNull();
        _state.Load().ShouldBe(previous);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
