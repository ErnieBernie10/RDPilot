using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Threading;

namespace RDPilot.Client.Tests;

internal static class AvaloniaTestEnvironment
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static HeadlessUnitTestSession? _session;
    private static readonly AsyncLocal<bool> InHeadlessSession = new();

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

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
        if (InHeadlessSession.Value && Dispatcher.UIThread.CheckAccess()) return action();
        return _session!.Dispatch(() =>
        {
            InHeadlessSession.Value = true;
            try { return action(); }
            finally { InHeadlessSession.Value = false; }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static void RunOnUiThread(Action action)
    {
        EnsureInitialized();
        if (InHeadlessSession.Value && Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        _session!.Dispatch(() =>
        {
            InHeadlessSession.Value = true;
            try { action(); }
            finally { InHeadlessSession.Value = false; }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        EnsureInitialized();
        if (InHeadlessSession.Value && Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }
        await _session!.Dispatch(async () =>
        {
            InHeadlessSession.Value = true;
            try
            {
                await action();
                return true;
            }
            finally { InHeadlessSession.Value = false; }
        }, CancellationToken.None);
    }
}
