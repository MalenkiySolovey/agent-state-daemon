namespace AgentStateDaemon.Rpc;

internal static class ProtocolLimits
{
    public const int MaximumFrameBytes = 1_048_576;
    public const int MaximumConcurrentClients = 16;
    public const int RequestTimeoutSeconds = 10;
    public const int IdleConnectionTimeoutSeconds = 60;
    public const int ShutdownTimeoutSeconds = 10;
}
