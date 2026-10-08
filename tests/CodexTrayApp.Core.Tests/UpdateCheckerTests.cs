using System.Net;
using CodexTrayApp.Core.Tests.TestSupport;
using CodexTrayApp.Core.Updates;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class UpdateCheckerTests
{
    private const string LatestBody = """
        {"tag_name":"v0.2.0","html_url":"https://github.com/mlcousek/codex_trayapp/releases/tag/v0.2.0"}
        """;

    [Theory]
    [InlineData("v0.1.1", "0.1.1")]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("v10.0.1", "10.0.1")]
    public void Release_tags_parse_with_or_without_the_v(string tag, string expected) =>
        UpdateChecker.ParseVersion(tag).ShouldBe(Version.Parse(expected));

    [Theory]
    [InlineData("v0.2.0-beta.1")]
    [InlineData("0.2.0+build.5")]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData(null)]
    public void Prereleases_and_unreadable_tags_are_ignored(string? tag) =>
        UpdateChecker.ParseVersion(tag).ShouldBeNull();

    [Fact]
    public void A_two_part_version_never_looks_older_than_its_four_part_self()
    {
        // Version treats an unspecified revision as -1, so without normalising, 0.1.1 < 0.1.1.0 and every check
        // would claim an update was available.
        UpdateChecker.Normalize(new Version(0, 1, 1)).ShouldBe(UpdateChecker.Normalize(new Version(0, 1, 1, 0)));
    }

    [Fact]
    public async Task A_newer_release_is_reported_with_its_url()
    {
        var checker = Create("0.1.1", HttpStatusCode.OK, LatestBody);

        var outcome = await checker.CheckAsync(TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(UpdateCheckStatus.UpdateAvailable);
        outcome.Release.ShouldNotBeNull().Version.ShouldBe(new Version(0, 2, 0));
        outcome.Release!.Tag.ShouldBe("v0.2.0");
        outcome.Release.Url.ShouldBe("https://github.com/mlcousek/codex_trayapp/releases/tag/v0.2.0");
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.2.1")]
    [InlineData("1.0.0")]
    public async Task The_same_or_a_newer_build_is_up_to_date(string current)
    {
        var checker = Create(current, HttpStatusCode.OK, LatestBody);

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.UpToDate);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_unhappy_response_fails_quietly(HttpStatusCode status)
    {
        var checker = Create("0.1.1", status, "{}");

        var outcome = await checker.CheckAsync(TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(UpdateCheckStatus.Failed);
        outcome.Release.ShouldBeNull();
        outcome.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_network_failure_never_escapes()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("no route to host"));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.1", new ListLogger<UpdateChecker>(), "mlcousek/codex_trayapp");

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Failed);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"nightly\"}")]
    public async Task An_unreadable_body_fails_rather_than_inventing_a_version(string body)
    {
        var checker = Create("0.1.1", HttpStatusCode.OK, body);

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Failed);
    }

    [Fact]
    public async Task The_request_identifies_the_app_and_asks_for_the_github_api()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LatestBody) };
        });
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.1", new ListLogger<UpdateChecker>(), "mlcousek/codex_trayapp");

        await checker.CheckAsync(TestContext.Current.CancellationToken);

        seen.ShouldNotBeNull();
        seen!.Method.ShouldBe(HttpMethod.Get);
        seen.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/mlcousek/codex_trayapp/releases/latest");
        seen.Headers.UserAgent.ToString().ShouldStartWith("CodexUsageTray/");
        seen.Headers.Accept.ToString().ShouldContain("application/vnd.github+json");
        // Nothing about the user may ride along.
        seen.Content.ShouldBeNull();
        seen.Headers.Authorization.ShouldBeNull();
        seen.RequestUri.Query.ShouldBeEmpty();
    }

    private static UpdateChecker Create(string currentVersion, HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return new UpdateChecker(new HttpClient(handler), currentVersion, new ListLogger<UpdateChecker>(), "mlcousek/codex_trayapp");
    }

    [Fact]
    public async Task Omitting_the_repository_never_targets_an_assumed_destination()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected request.")));
        var checker = new UpdateChecker(http, "0.1.0", new ListLogger<UpdateChecker>());

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Unavailable);
        checker.ReleasesPageUrl.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("owner/repo?redirect=other")]
    [InlineData("owner/repo/extra")]
    [InlineData("../repo")]
    public async Task Unconfigured_or_invalid_repository_sends_no_requests(string? repository)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, "0.1.1", new ListLogger<UpdateChecker>(), repository);
        checker.IsConfigured.ShouldBeFalse();
        checker.ReleasesPageUrl.ShouldBeEmpty();
        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Unavailable);
    }

    [Fact]
    public async Task Configured_repository_controls_the_request_and_release_fallback()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.ShouldBe("https://api.github.com/repos/sample/custom-tray/releases/latest");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"tag_name":"v0.2.0"}""") };
        });
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, "0.1.1", new ListLogger<UpdateChecker>(), "sample/custom-tray");
        checker.IsConfigured.ShouldBeTrue();
        var outcome = await checker.CheckAsync(TestContext.Current.CancellationToken);
        outcome.Release.ShouldNotBeNull().Url.ShouldBe("https://github.com/sample/custom-tray/releases/latest");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("17")]
    public async Task Non_object_response_does_not_escape_as_an_exception(string body)
    {
        (await Create("0.1.1", HttpStatusCode.OK, body).CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Failed);
    }

    [Theory]
    [InlineData("file:///C:/unknown.exe")]
    [InlineData("https://example.test/install")]
    [InlineData("https://github.com/other/repo/releases/latest")]
    [InlineData("http://github.com/mlcousek/codex_trayapp/releases/latest")]
    public async Task Unexpected_release_destination_falls_back_to_configured_github_page(string url)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { tag_name = "v0.2.0", html_url = url });
        var outcome = await Create("0.1.1", HttpStatusCode.OK, body).CheckAsync(TestContext.Current.CancellationToken);
        outcome.Release.ShouldNotBeNull().Url.ShouldBe("https://github.com/mlcousek/codex_trayapp/releases/latest");
    }
}
