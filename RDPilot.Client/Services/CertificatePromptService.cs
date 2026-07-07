using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public sealed class CertificatePromptService : ICertificatePromptService
{
    public CertificateTrustDecision Prompt(RdpCertificatePrompt prompt)
    {
        return Dispatcher.UIThread.InvokeAsync(async () => await ShowPromptAsync(prompt)).GetAwaiter().GetResult();
    }

    private static async Task<CertificateTrustDecision> ShowPromptAsync(RdpCertificatePrompt prompt)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null)
        {
            return CertificateTrustDecision.Reject;
        }

        var window = new Window
        {
            Title = prompt.IsChanged ? "Certificate changed" : "Trust certificate",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        CertificateTrustDecision decision = CertificateTrustDecision.Reject;
        var bodyText = prompt.IsChanged
            ? $"The certificate for {prompt.Host}:{prompt.Port} has changed.\n\nCommon name: {prompt.CommonName}\nSubject: {prompt.Subject}\nIssuer: {prompt.Issuer}\nFingerprint: {prompt.Fingerprint}\n\nPrevious fingerprint: {prompt.PreviousFingerprint ?? "Unknown"}\n\nOnly continue if you expected this certificate change."
            : $"First connection to {prompt.Host}:{prompt.Port}.\n\nCommon name: {prompt.CommonName}\nSubject: {prompt.Subject}\nIssuer: {prompt.Issuer}\nFingerprint: {prompt.Fingerprint}\n\nTrust this certificate?";

        var message = new TextBlock
        {
            Text = bodyText,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var rejectButton = new Button { Content = "Reject", MinWidth = 92, Classes = { "Compact" } };
        rejectButton.Click += (_, _) => { decision = CertificateTrustDecision.Reject; window.Close(); };

        var onceButton = new Button { Content = "Trust Once", MinWidth = 108, Classes = { "Compact" } };
        onceButton.Click += (_, _) => { decision = CertificateTrustDecision.TrustOnce; window.Close(); };

        var alwaysButton = new Button { Content = "Trust Always", MinWidth = 116, Classes = { "Accent", "Compact" } };
        alwaysButton.Click += (_, _) => { decision = CertificateTrustDecision.TrustAlways; window.Close(); };

        buttons.Children.Add(rejectButton);
        buttons.Children.Add(onceButton);
        buttons.Children.Add(alwaysButton);

        window.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                message,
                buttons
            }
        };

        await window.ShowDialog(owner);
        return decision;
    }
}
