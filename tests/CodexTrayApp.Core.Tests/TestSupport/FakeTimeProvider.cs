namespace CodexTrayApp.Core.Tests.TestSupport;

/// <summary>A clock the test controls. Only <see cref="GetUtcNow"/> is overridden; timers are not used in these tests.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public FakeTimeProvider(DateTimeOffset now)
    {
        UtcNow = now;
    }

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}
