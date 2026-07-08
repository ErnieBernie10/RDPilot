using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace RDPilot.Client.Tests;

internal static class AvaloniaTestEnvironment
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            AppBuilder.Configure<TestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            _initialized = true;
        }
    }

    public static void RunPendingDispatcherJobs()
    {
        EnsureInitialized();

        RunOnUiThread(static () => Dispatcher.UIThread.RunJobs());
    }

    public static void RunOnUiThread(Action action)
    {
        EnsureInitialized();

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
    }

    private sealed class TestApplication : Application
    {
    }
}
