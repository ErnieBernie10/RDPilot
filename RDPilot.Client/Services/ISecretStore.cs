using System.Threading.Tasks;

namespace RDPilot.Client.Services;

public interface ISecretStore
{
    string Description { get; }
    Task<string?> GetSecretAsync(string key);
    Task SetSecretAsync(string key, string secret);
    Task DeleteSecretAsync(string key);
}
