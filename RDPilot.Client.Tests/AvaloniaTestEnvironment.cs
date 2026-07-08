using System;
using Avalonia.Threading;
using RDPilot.Client;

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

            Program.BuildAvaloniaApp().SetupWithoutStarting();
            _initialized = true;
        }
    }

    public static void RunPendingDispatcherJobs()
    {
        EnsureInitialized();
        Dispatcher.UIThread.RunJobs();
    }
}
