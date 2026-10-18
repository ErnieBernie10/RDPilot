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
        RdpSessionStartRequest request,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived,
        Action<RdpSessionViewModel, RemoteFileOffer> remoteClipboardFilesReceived)
    {
        var session = new RdpSessionViewModel(
            request,
            remoteClipboardTextReceived,
            remoteClipboardFilesReceived,
            DecideCertificateTrust);
        // Construction roots callbacks and subscribes presentation before native delivery begins.
        try
        {
            session.Start(request);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
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
