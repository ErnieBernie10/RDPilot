using RDP.Client.Models;

namespace RDP.Client.Services;

public interface ICertificatePromptService
{
    CertificateTrustDecision Prompt(RdpCertificatePrompt prompt);
}
