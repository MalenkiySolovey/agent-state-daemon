using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Client;

public sealed class AgentStateDaemonClient : IAsyncDisposable
{
    private const int MaximumFrameBytes = 1_048_576;
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly string _pipeName;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly byte[] _readBuffer = new byte[4096];
    private NamedPipeClientStream? _pipe;
    private int _readOffset;
    private int _readCount;
    private long _nextRequestId;
    private bool _disposed;

    public AgentStateDaemonClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(checked((int)timeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
            _pipe = pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<PingResult>("system.ping", null, cancellationToken).ConfigureAwait(false)).Alive;

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SystemInfo>("system.info", null, cancellationToken);

    public Task<AccountListResult> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AccountListResult>("accounts.list", null, cancellationToken);

    public Task<RevisionedAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        SendAsync<RevisionedAccount>("accounts.get", new AccountIdParameters(accountId), cancellationToken);

    public Task<RevisionedAccount> UpdateMetadataAsync(AccountMetadataUpdate update, CancellationToken cancellationToken = default) =>
        SendAsync<RevisionedAccount>("accounts.updateMetadata", update, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        _requestGate.Dispose();
    }

    private async Task<TResult> SendAsync<TResult>(string method, object? parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipe = _pipe;
            if (pipe is not { IsConnected: true })
            {
                throw new InvalidOperationException("Connect the client before sending requests.");
            }

            var id = Interlocked.Increment(ref _nextRequestId);
            var request = JsonSerializer.SerializeToUtf8Bytes(new RpcRequest("2.0", id, method, parameters), JsonOptions);
            if (request.Length > MaximumFrameBytes)
            {
                throw new InvalidOperationException("The request exceeds the supported size.");
            }

            await pipe.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var responseBytes = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The daemon closed the connection without a response.");
            using var response = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
                !root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || responseId.GetInt64() != id)
            {
                throw new InvalidDataException("The daemon returned an invalid response.");
            }

            var hasResult = root.TryGetProperty("result", out var result);
            var hasError = root.TryGetProperty("error", out var error);
            if (hasResult == hasError)
            {
                throw new InvalidDataException("The daemon response must contain exactly one result or error.");
            }

            if (hasError)
            {
                throw ParseRpcError(error);
            }

            return result.Deserialize<TResult>(JsonOptions)
                ?? throw new InvalidDataException("The daemon returned an empty result.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static AgentStateDaemonRpcException ParseRpcError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode) ? parsedCode : -32603;
        var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? "The daemon request failed."
            : "The daemon request failed.";
        var errorCode = error.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("errorCode", out var errorCodeElement) &&
                        errorCodeElement.ValueKind == JsonValueKind.String
            ? errorCodeElement.GetString() ?? "internal_error"
            : "internal_error";
        return new AgentStateDaemonRpcException(code, errorCode, message);
    }

    private async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream(capacity: 4096);
        while (true)
        {
            if (_readOffset >= _readCount)
            {
                _readCount = await stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                _readOffset = 0;
                if (_readCount == 0)
                {
                    return null;
                }
            }

            var next = _readBuffer[_readOffset++];
            if (next == (byte)'\n')
            {
                var bytes = frame.ToArray();
                var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                if (length > MaximumFrameBytes)
                {
                    throw new InvalidDataException("The daemon response exceeds the supported size.");
                }

                return length == bytes.Length ? bytes : bytes[..length];
            }

            frame.WriteByte(next);
            if (frame.Length > MaximumFrameBytes + 1L)
            {
                throw new InvalidDataException("The daemon response exceeds the supported size.");
            }
        }
    }

    private sealed record RpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Parameters);

    private sealed record AccountIdParameters(
        [property: JsonPropertyName("accountId")] string AccountId);

    private sealed record PingResult(
        [property: JsonPropertyName("alive")] bool Alive);
}

public sealed record SystemInfo
{
    [JsonPropertyName("daemonVersion")]
    public required string DaemonVersion { get; init; }

    [JsonPropertyName("protocolSchemaVersion")]
    public int ProtocolSchemaVersion { get; init; }

    [JsonPropertyName("protocolManifestSha256")]
    public required string ProtocolManifestSha256 { get; init; }

    [JsonPropertyName("databaseSchemaVersion")]
    public int DatabaseSchemaVersion { get; init; }

    [JsonPropertyName("startedAt")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; init; }
}

public sealed class AgentStateDaemonRpcException(int jsonRpcCode, string errorCode, string message) : Exception(message)
{
    public int JsonRpcCode { get; } = jsonRpcCode;

    public string ErrorCode { get; } = errorCode;
}
