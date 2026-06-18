using System;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Services;

public sealed class RdpSessionFactory : IRdpSessionFactory
{
    private readonly ICertificateTrustStore _certificateTrustStore;
    private readonly ICertificatePromptService _certificatePromptService;

    public RdpSessionFactory()
        : this(new CertificateTrustStore(), new CertificatePromptService())
    {
    }

    public RdpSessionFactory(ICertificateTrustStore certificateTrustStore, ICertificatePromptService certificatePromptService)
    {
        _certificateTrustStore = certificateTrustStore;
        _certificatePromptService = certificatePromptService;
    }

    public RdpSessionViewModel Create(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        int width,
        int height,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived)
    {
        return new RdpSessionViewModel(
            connection,
            password,
            gatewayPassword,
            width,
            height,
            remoteClipboardTextReceived,
            DecideCertificateTrust);
    }

    private CertificateTrustDecision DecideCertificateTrust(RdpCertificatePrompt prompt)
    {
        var trustedFingerprint = _certificateTrustStore.GetTrustedFingerprint(prompt.Host, prompt.Port);
        if (!string.IsNullOrWhiteSpace(trustedFingerprint) && string.Equals(trustedFingerprint, prompt.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return CertificateTrustDecision.TrustAlways;
        }

        var decision = _certificatePromptService.Prompt(prompt);
        if (decision == CertificateTrustDecision.TrustAlways)
        {
            _certificateTrustStore.SaveTrustedFingerprint(prompt.Host, prompt.Port, prompt.Fingerprint);
        }

        return decision;
    }
}
