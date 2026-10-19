using System.Collections.Concurrent;
using System.IO.Pipes;
using AgentStateDaemon.Rpc;

namespace AgentStateDaemon.Transport.NamedPipes;

internal sealed class NamedPipeRpcServer
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly string _pipeName;
    private readonly IJsonRpcDispatcher _dispatcher;
    private readonly Action<string, string, long>? _safeLog;

    public NamedPipeRpcServer(string pipeName, IJsonRpcDispatcher dispatcher, Action<string, string, long>? safeLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _safeLog = safeLog;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var clientSlots = new SemaphoreSlim(ProtocolLimits.MaximumConcurrentClients);
        var activeClients = new ConcurrentDictionary<long, Task>();
        var activePipes = new ConcurrentDictionary<long, NamedPipeServerStream>();
        var activeRequests = new ConcurrentDictionary<long, Task<byte[]?>>();
        long nextClientId = 0;
        long nextRequestId = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await clientSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    ProtocolLimits.MaximumConcurrentClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 4096);

                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    clientSlots.Release();
                    throw;
                }

                var clientId = Interlocked.Increment(ref nextClientId);
                activePipes.TryAdd(clientId, pipe);
                var task = HandleClientAsync(pipe, activeRequests, () => Interlocked.Increment(ref nextRequestId), cancellationToken);
                activeClients.TryAdd(clientId, task);
                _ = task.ContinueWith(
                    completedTask =>
                    {
                        _ = completedTask.Exception;
                        activeClients.TryRemove(clientId, out _);
                        activePipes.TryRemove(clientId, out _);
                        clientSlots.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var drain = Task.WhenAll(activeClients.Values.Concat(activeRequests.Values));
            try
            {
                await drain.WaitAsync(TimeSpan.FromSeconds(ProtocolLimits.ShutdownTimeoutSeconds), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                foreach (var pipe in activePipes.Values)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                try
                {
                    await drain.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _safeLog?.Invoke("transport", "shutdown_timeout", ProtocolLimits.ShutdownTimeoutSeconds * 1_000L);
                }
            }
            catch (Exception)
            {
                _safeLog?.Invoke("transport", "client_shutdown_error", 0);
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        ConcurrentDictionary<long, Task<byte[]?>> activeRequests,
        Func<long> nextRequestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var reader = new BoundedNdjsonReader(pipe);
                while (!cancellationToken.IsCancellationRequested)
                {
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(ProtocolLimits.IdleConnectionTimeoutSeconds));
                    byte[]? request;
                    try
                    {
                        request = await reader.ReadFrameAsync(idleTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _safeLog?.Invoke("transport", "idle_timeout", ProtocolLimits.IdleConnectionTimeoutSeconds * 1_000L);
                        return;
                    }

                    idleTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (request is null)
                    {
                        return;
                    }

                    var requestId = nextRequestId();
                    var dispatch = Task.Run(() => _dispatcher.Dispatch(request), CancellationToken.None);
                    activeRequests.TryAdd(requestId, dispatch);
                    _ = dispatch.ContinueWith(
                        completedTask =>
                        {
                            _ = completedTask.Exception;
                            activeRequests.TryRemove(requestId, out _);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    byte[]? response;
                    try
                    {
                        response = await dispatch.WaitAsync(TimeSpan.FromSeconds(ProtocolLimits.RequestTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        _safeLog?.Invoke("rpc", "request_timeout", ProtocolLimits.RequestTimeoutSeconds * 1_000L);
                        return;
                    }

                    if (response is null)
                    {
                        continue;
                    }

                    if (response.Length > ProtocolLimits.MaximumFrameBytes)
                    {
                        _safeLog?.Invoke("transport", "response_limit", 0);
                        return;
                    }

                    await pipe.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await pipe.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
                    await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (FrameTooLargeException)
        {
            _safeLog?.Invoke("transport", "oversized_frame", 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            _safeLog?.Invoke("transport", "client_io_error", 0);
        }
        catch (Exception)
        {
            _safeLog?.Invoke("transport", "client_error", 0);
        }
    }

    private sealed class BoundedNdjsonReader(Stream stream)
    {
        private readonly byte[] _readBuffer = new byte[4096];
        private int _readOffset;
        private int _readCount;

        public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
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
                    if (length > ProtocolLimits.MaximumFrameBytes)
                    {
                        throw new FrameTooLargeException();
                    }

                    return length == bytes.Length ? bytes : bytes[..length];
                }

                frame.WriteByte(next);
                if (frame.Length > ProtocolLimits.MaximumFrameBytes + 1L)
                {
                    throw new FrameTooLargeException();
                }
            }
        }
    }

    private sealed class FrameTooLargeException : Exception
    {
    }
}
