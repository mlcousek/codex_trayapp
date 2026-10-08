using System.Runtime.ExceptionServices;

namespace CodexTrayApp.Tests;

/// <summary>Runs WPF work on a single-threaded-apartment thread, which WPF visuals require.</summary>
internal static class StaThread
{
    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        return result;
    }
}
