using System;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Headless;

namespace RDPilot.Client.Tests;

internal static class AvaloniaTestEnvironment
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static HeadlessUnitTestSession? _session;

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
            _session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
            _initialized = true;
        }
    }

    public static void RunPendingDispatcherJobs()
    {
        RunOnUiThread(static () => { });
    }

    public static T RunOnUiThread<T>(Func<T> action)
    {
        EnsureInitialized();
        return _session!.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static void RunOnUiThread(Action action)
    {
        EnsureInitialized();
        _session!.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class TestApplication : Application
    {
    }
}
