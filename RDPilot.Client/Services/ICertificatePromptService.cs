using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public interface ICertificatePromptService
{
    CertificateTrustDecision Prompt(RdpCertificatePrompt prompt);
}
