using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RDP.Client.Services;

public static class SecretStore
{
    public static ISecretStore CreateDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsCredentialSecretStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacKeychainSecretStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxSecretServiceStore();
        return new UnsupportedSecretStore();
    }

    public static string PasswordKey(string connectionId) => $"connection/{connectionId}/password";
    public static string GatewayPasswordKey(string connectionId) => $"connection/{connectionId}/gatewayPassword";
}

internal sealed class UnsupportedSecretStore : ISecretStore
{
    public string Description => "Unsupported platform secret store";

    public Task<string?> GetSecretAsync(string key) => throw CreateException();
    public Task SetSecretAsync(string key, string secret) => throw CreateException();
    public Task DeleteSecretAsync(string key) => throw CreateException();

    private static InvalidOperationException CreateException()
    {
        return new InvalidOperationException("This platform does not have a configured credential vault implementation.");
    }
}

internal sealed class LinuxSecretServiceStore : ISecretStore
{
    public string Description => "Linux Secret Service via secret-tool";

    public async Task<string?> GetSecretAsync(string key)
    {
        var result = await RunSecretToolAsync(null, "lookup", "service", AppDataPaths.AppName, "key", key);
        if (result.ExitCode != 0) return null;
        var secret = result.StdOut.TrimEnd('\r', '\n');
        return secret.Length == 0 ? null : secret;
    }

    public async Task SetSecretAsync(string key, string secret)
    {
        var result = await RunSecretToolAsync(secret, "store", "--label", $"{AppDataPaths.AppName} {key}", "service", AppDataPaths.AppName, "key", key);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to save password with secret-tool. Install libsecret/gnome-keyring and ensure a Secret Service session is available. {result.StdErr}".Trim());
        }
    }

    public async Task DeleteSecretAsync(string key)
    {
        await RunSecretToolAsync(null, "clear", "service", AppDataPaths.AppName, "key", key);
    }

    private static Task<ProcessResult> RunSecretToolAsync(string? stdin, params string[] args)
    {
        return ProcessRunner.RunAsync("secret-tool", args, stdin);
    }
}

internal sealed class MacKeychainSecretStore : ISecretStore
{
    public string Description => "macOS Keychain";

    public async Task<string?> GetSecretAsync(string key)
    {
        var result = await ProcessRunner.RunAsync("security", new[] { "find-generic-password", "-s", AppDataPaths.AppName, "-a", key, "-w" }, null);
        if (result.ExitCode != 0) return null;
        var secret = result.StdOut.TrimEnd('\r', '\n');
        return secret.Length == 0 ? null : secret;
    }

    public async Task SetSecretAsync(string key, string secret)
    {
        var result = await ProcessRunner.RunAsync("security", new[] { "add-generic-password", "-U", "-s", AppDataPaths.AppName, "-a", key, "-w", secret }, null);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to save password in macOS Keychain. {result.StdErr}".Trim());
        }
    }

    public async Task DeleteSecretAsync(string key)
    {
        await ProcessRunner.RunAsync("security", new[] { "delete-generic-password", "-s", AppDataPaths.AppName, "-a", key }, null);
    }
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string[] args, string? stdin)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                UseShellExecute = false
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(127, "", ex.Message);
        }
    }
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public string Description => "Windows Credential Manager";

    public Task<string?> GetSecretAsync(string key)
    {
        var target = GetTargetName(key);
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPtr))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return Task.FromResult<string?>(null);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(bytes));
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public Task SetSecretAsync(string key, string secret)
    {
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var credential = new Credential
        {
            Type = CredTypeGeneric,
            TargetName = GetTargetName(key),
            CredentialBlobSize = secretBytes.Length,
            Persist = CredPersistLocalMachine,
            UserName = Environment.UserName
        };

        var blob = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            credential.CredentialBlob = blob;
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save password in Windows Credential Manager.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }

        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key)
    {
        CredDelete(GetTargetName(key), CredTypeGeneric, 0);
        return Task.CompletedTask;
    }

    private static string GetTargetName(string key) => $"{AppDataPaths.AppName}/{key}";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential userCredential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
