using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPilot.Client.Services;

public sealed class WindowsSingleInstanceCoordinator : IDisposable
{
    private const string InstanceNamePrefix = "RDPilot.Client.";
    private const string PipeNamePrefix = "RDPilot.Client.Connect.";
    private const int ConnectionTimeoutMilliseconds = 2000;
    private readonly Mutex? _mutex;
    private readonly string? _pipeName;
    private readonly CancellationTokenSource? _cancellationTokenSource;

    private WindowsSingleInstanceCoordinator(Mutex mutex, string pipeName, Func<string, Task> onConnectionRequested)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _cancellationTokenSource = new CancellationTokenSource();
        if (OperatingSystem.IsWindows())
        {
            _ = ListenAsync(onConnectionRequested, _cancellationTokenSource.Token);
        }
    }

    private WindowsSingleInstanceCoordinator()
    {
    }

    public bool IsPrimaryInstance => _mutex != null;

    public static async Task<WindowsSingleInstanceCoordinator> CreateAsync(LaunchOptions options, Func<string, Task> onConnectionRequested)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsSingleInstanceCoordinator();
        }

        var userId = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new WindowsSingleInstanceCoordinator();
        }
        var instanceName = InstanceNamePrefix + userId;
        var pipeName = PipeNamePrefix + userId;
        var mutex = new Mutex(true, instanceName, out var createdNew);
        if (createdNew)
        {
            return new WindowsSingleInstanceCoordinator(mutex, pipeName, onConnectionRequested);
        }

        mutex.Dispose();
        if (!string.IsNullOrWhiteSpace(options.ConnectionId))
        {
            try
            {
                await SendConnectionRequestAsync(pipeName, options.ConnectionId);
            }
            catch (IOException)
            {
                // The primary instance may be closing while the jump-list process starts.
            }
            catch (TimeoutException)
            {
                // The primary instance may be starting or closing while the jump-list process starts.
            }
        }

        return new WindowsSingleInstanceCoordinator();
    }

    private static async Task SendConnectionRequestAsync(string pipeName, string connectionId)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectionTimeoutMilliseconds);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(connectionId);
        await writer.FlushAsync();
    }

    [SupportedOSPlatform("windows")]
    private async Task ListenAsync(Func<string, Task> onConnectionRequested, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServerStream(_pipeName!);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var connectionId = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(connectionId))
                {
                    await onConnectionRequested(connectionId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A client can exit while writing. Continue accepting requests.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            1024,
            1024,
            security);
    }

    public void Dispose()
    {
        if (_cancellationTokenSource == null)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _mutex?.Dispose();
    }
}
