using System;
using System.Reflection;
using System.Threading;
using Avalonia.Headless;

namespace RDPilot.Client.Tests;

internal static class AvaloniaTestEnvironment
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static HeadlessUnitTestSession? _session;
    private static SynchronizationContext? _synchronizationContext;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
            _synchronizationContext = new HeadlessSessionSynchronizationContext(_session);
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
        return _session!.Dispatch(() => RunWithSessionSynchronizationContext(action), CancellationToken.None).GetAwaiter().GetResult();
    }

    public static void RunOnUiThread(Action action)
    {
        EnsureInitialized();
        _session!.Dispatch(() => RunWithSessionSynchronizationContext(action), CancellationToken.None).GetAwaiter().GetResult();
    }

    private static T RunWithSessionSynchronizationContext<T>(Func<T> action)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void RunWithSessionSynchronizationContext(Action action)
    {
        RunWithSessionSynchronizationContext(() =>
        {
            action();
            return true;
        });
    }

    private sealed class HeadlessSessionSynchronizationContext(HeadlessUnitTestSession session) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _ = session.Dispatch(() => callback(state), CancellationToken.None);
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                callback(state);
                return;
            }

            session.Dispatch(() => callback(state), CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
