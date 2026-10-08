using System.IO;
using CodexTrayApp.Core.Domain;
using CodexTrayApp;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace CodexTrayApp.Tests;

public sealed class ThresholdNotifierRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void App_registration_uses_durable_account_scoped_crossing_claims()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = UsageSnapshot.Empty(now) with
        {
            Source = UsageSource.Live,
            AccountKey = "synthetic-account",
            AccountIdentityKey = "synthetic-identity",
            AccountSourceKey = "synthetic-source",
            Windows = [UsageWindow.Create("codex:primary", 85, now.AddHours(2))],
        };
        var path = Path.Combine(_root, "history.db");

        App.CreateThresholdNotifier(path).Evaluate(snapshot, [80, 95], now).Single().Threshold.ShouldBe(80);
        App.CreateThresholdNotifier(path).Evaluate(snapshot, [80, 95], now).ShouldBeEmpty();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
