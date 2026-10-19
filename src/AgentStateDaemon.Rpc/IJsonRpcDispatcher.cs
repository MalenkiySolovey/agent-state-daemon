namespace AgentStateDaemon.Rpc;

internal interface IJsonRpcDispatcher
{
    byte[]? Dispatch(ReadOnlyMemory<byte> request);
}
