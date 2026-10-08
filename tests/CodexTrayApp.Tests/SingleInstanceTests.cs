using CodexTrayApp.Startup;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>
/// A named Mutex is a real OS object, so <see cref="SingleInstance"/> is fully testable in-process: a second
/// <see cref="SingleInstance.TryAcquire(string, string, out SingleInstance?)"/> call finding the mutex already held
/// is the same code path a genuine second process would hit. Every test uses a GUID-suffixed name rather than the
/// production "Local\CodexUsageTray.*" names: this machine may have a real instance already running, and colliding
/// with its mutex would make the test fail for reasons unrelated to this code, and the activation signal would pop
/// that real instance's flyout as a side effect of running the test suite.
/// </summary>
public class SingleInstanceTests
{
    private static (string Mutex, string Activation) UniqueNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return (@"Local\CodexUsageTray.Test." + id, @"Local\CodexUsageTray.Test." + id + ".Activate");
    }

    [Fact]
    public void First_acquire_succeeds_and_a_second_is_refused()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        try
        {
            SingleInstance.TryAcquire(mutexName, eventName, out var second).ShouldBeFalse();
            second.ShouldBeNull();
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void A_second_acquire_signals_the_first_instances_activation()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        try
        {
            using var activated = new ManualResetEventSlim(false);
            first!.ListenForActivation(() => activated.Set());

            SingleInstance.TryAcquire(mutexName, eventName, out var second).ShouldBeFalse();
            second.ShouldBeNull();

            activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void A_signal_sent_before_the_first_instance_listens_is_not_lost()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        try
        {
            SingleInstance.TryAcquire(mutexName, eventName, out _).ShouldBeFalse();

            using var activated = new ManualResetEventSlim(false);
            first!.ListenForActivation(() => activated.Set());

            activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void Acquire_succeeds_again_after_the_holder_is_disposed()
    {
        var (mutexName, eventName) = UniqueNames();

        SingleInstance.TryAcquire(mutexName, eventName, out var first).ShouldBeTrue();
        first.ShouldNotBeNull();
        first!.Dispose();

        SingleInstance.TryAcquire(mutexName, eventName, out var second).ShouldBeTrue();
        second.ShouldNotBeNull();
        second!.Dispose();
    }
}
