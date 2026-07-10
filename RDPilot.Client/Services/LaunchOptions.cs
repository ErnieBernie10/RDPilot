namespace RDPilot.Client.Services;

public sealed class LaunchOptions
{
    public string? ConnectionId { get; init; }

    public static LaunchOptions Parse(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--connect", System.StringComparison.OrdinalIgnoreCase))
            {
                return new LaunchOptions { ConnectionId = args[index + 1] };
            }
        }

        return new LaunchOptions();
    }
}
