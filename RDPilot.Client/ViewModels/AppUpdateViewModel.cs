using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDPilot.Client.Models;
using RDPilot.Client.Services;

namespace RDPilot.Client.ViewModels;

public sealed partial class AppUpdateViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly AppUpdateEnvironment _environment;
    private readonly Func<int> _countOpenSessions;
    private CancellationTokenSource? _automaticChecks;
    private CancellationTokenSource? _download;
    private string? _installerPath;

    [ObservableProperty] private AppUpdateState _state;
    [ObservableProperty] private AvailableUpdate? _update;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _failureReason = "";
    [ObservableProperty] private bool _isDismissed;
    [ObservableProperty] private string _checkResult = "";
    [ObservableProperty] private int _openSessions;

    public AppUpdateViewModel(AppUpdateEnvironment environment, Func<int> countOpenSessions)
    {
        _environment = environment;
        _countOpenSessions = countOpenSessions;
    }

    public event EventHandler? ShutdownRequested;

    public bool IsSupported => _environment.Channel != AppUpdateChannel.None;
    public bool CanInstallInApp => _environment.Channel == AppUpdateChannel.Installer;
    public string CurrentVersionText => _environment.CurrentVersion is { } version ? $"RDPilot {version.ToString(3)}" : "RDPilot development build";
    public bool IsNoticeVisible => State != AppUpdateState.Idle && !IsDismissed;
    public bool IsDownloading => State == AppUpdateState.Downloading;
    public bool IsReadyToInstall => State == AppUpdateState.ReadyToInstall;
    public bool IsFailed => State == AppUpdateState.Failed;
    public bool HasReleaseNotes => State != AppUpdateState.Failed && Update != null;
    public bool CanDismiss => State is AppUpdateState.Available or AppUpdateState.Failed;
    public bool CanStartUpdate => CanInstallInApp && State == AppUpdateState.Available;
    public Uri? ReleasePageUri => Update == null ? null : new Uri(Update.ReleasePageUrl);
    private string AvailableVersion => Update?.Version.ToString(3) ?? "";

    public string NoticeText => State switch
    {
        AppUpdateState.Available => $"Update to {AvailableVersion}",
        AppUpdateState.Downloading => $"Downloading update {DownloadPercent:0}%",
        AppUpdateState.ReadyToInstall => $"Restart to update to {AvailableVersion}",
        AppUpdateState.InstallOnExit => $"{AvailableVersion} installs when RDPilot closes",
        AppUpdateState.Failed => "Update failed",
        _ => ""
    };

    public string PanelHeading => State switch
    {
        AppUpdateState.Failed => "Update failed",
        AppUpdateState.ReadyToInstall or AppUpdateState.InstallOnExit => $"RDPilot {AvailableVersion} is ready",
        _ => $"RDPilot {AvailableVersion} is available"
    };

    public string PanelDetail => State switch
    {
        AppUpdateState.Available when CanInstallInApp => $"You have {CurrentVersionText}.",
        AppUpdateState.Available => $"You have {CurrentVersionText}. Update RDPilot with your package manager.",
        AppUpdateState.Downloading => "Downloading the update. You can keep working.",
        AppUpdateState.ReadyToInstall => OpenSessions == 1
            ? "1 session will disconnect. Update now?"
            : $"{OpenSessions} sessions will disconnect. Update now?",
        AppUpdateState.InstallOnExit => "It installs when you close RDPilot.",
        AppUpdateState.Failed => FailureReason,
        _ => ""
    };

    public void SetAutomaticChecks(bool enabled)
    {
        if (!IsSupported || enabled == (_automaticChecks != null))
        {
            return;
        }

        if (!enabled)
        {
            _automaticChecks!.Cancel();
            _automaticChecks.Dispose();
            _automaticChecks = null;
            return;
        }

        _automaticChecks = new CancellationTokenSource();
        _ = RunAutomaticChecksAsync(_automaticChecks.Token);
    }

    public void RefreshOpenSessions()
    {
        OpenSessions = _countOpenSessions();
    }

    public void InstallPendingOnExit()
    {
        if (State != AppUpdateState.InstallOnExit || _installerPath == null)
        {
            return;
        }

        try
        {
            _environment.Installer.Launch(_installerPath, relaunch: false);
        }
        catch (Exception)
        {
            // The app is exiting; there is no UI left to report this in.
        }

        _installerPath = null;
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (!IsSupported)
        {
            return;
        }

        CheckResult = "Checking for updates...";
        try
        {
            var newer = await RefreshAsync(CancellationToken.None);
            if (newer == null)
            {
                CheckResult = "RDPilot is up to date.";
                return;
            }

            IsDismissed = false;
            CheckResult = $"RDPilot {newer.Version.ToString(3)} is available.";
        }
        catch (Exception ex)
        {
            CheckResult = $"Could not check for updates: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StartUpdateAsync()
    {
        if (!CanInstallInApp || Update is not { } update || State is not (AppUpdateState.Available or AppUpdateState.Failed))
        {
            return;
        }

        _download?.Dispose();
        _download = new CancellationTokenSource();
        DownloadPercent = 0;
        State = AppUpdateState.Downloading;
        try
        {
            _installerPath = await _environment.Installer.DownloadAsync(update, new Progress<double>(fraction => DownloadPercent = fraction * 100), _download.Token);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        RefreshOpenSessions();
        if (OpenSessions == 0)
        {
            InstallNow();
            return;
        }

        State = AppUpdateState.ReadyToInstall;
    }

    [RelayCommand]
    private void InstallNow()
    {
        if (_installerPath == null)
        {
            return;
        }

        try
        {
            _environment.Installer.Launch(_installerPath, relaunch: true);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        _installerPath = null;
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void InstallOnExit()
    {
        if (State == AppUpdateState.ReadyToInstall)
        {
            State = AppUpdateState.InstallOnExit;
        }
    }

    [RelayCommand]
    private void Later()
    {
        IsDismissed = true;
    }

    private void Fail(Exception ex)
    {
        FailureReason = ex.Message;
        State = AppUpdateState.Failed;
        IsDismissed = false;
    }

    private async Task RunAutomaticChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await RefreshAsync(cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Automatic checks fail silently; offline is not worth interrupting anyone for.
                }

                await Task.Delay(CheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<AvailableUpdate?> RefreshAsync(CancellationToken cancellationToken)
    {
        var latest = await _environment.Feed.GetLatestAsync(cancellationToken);
        if (latest == null || _environment.CurrentVersion is not { } current || latest.Version <= current)
        {
            return null;
        }

        var isNewer = Update == null || latest.Version > Update.Version;
        if (State is AppUpdateState.Idle or AppUpdateState.Available or AppUpdateState.Failed && (isNewer || State == AppUpdateState.Idle))
        {
            if (isNewer)
            {
                IsDismissed = false;
            }

            Update = latest;
            State = AppUpdateState.Available;
        }

        return latest;
    }

    partial void OnStateChanged(AppUpdateState value) => NotifyDerivedChanged();
    partial void OnUpdateChanged(AvailableUpdate? value) => NotifyDerivedChanged();
    partial void OnDownloadPercentChanged(double value) => OnPropertyChanged(nameof(NoticeText));
    partial void OnFailureReasonChanged(string value) => OnPropertyChanged(nameof(PanelDetail));
    partial void OnIsDismissedChanged(bool value) => OnPropertyChanged(nameof(IsNoticeVisible));
    partial void OnOpenSessionsChanged(int value) => OnPropertyChanged(nameof(PanelDetail));

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(IsNoticeVisible));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsReadyToInstall));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(HasReleaseNotes));
        OnPropertyChanged(nameof(CanDismiss));
        OnPropertyChanged(nameof(CanStartUpdate));
        OnPropertyChanged(nameof(ReleasePageUri));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(PanelHeading));
        OnPropertyChanged(nameof(PanelDetail));
    }

    public void Dispose()
    {
        _automaticChecks?.Cancel();
        _automaticChecks?.Dispose();
        _automaticChecks = null;
        _download?.Cancel();
        _download?.Dispose();
        _download = null;
    }
}
