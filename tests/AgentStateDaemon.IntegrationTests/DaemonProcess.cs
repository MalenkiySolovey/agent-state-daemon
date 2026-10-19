using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStateDaemon.Client;

namespace AgentStateDaemon.IntegrationTests;

internal sealed class DaemonProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly Task _standardOutputPump;
    private readonly Task _standardErrorPump;

    private DaemonProcess(Process process, string pipeName)
    {
        _process = process;
        PipeName = pipeName;
        _standardOutputPump = PumpLinesAsync(process.StandardOutput);
        _standardErrorPump = PumpLinesAsync(process.StandardError);
    }

    public string PipeName { get; }

    public bool HasExited => _process.HasExited;

    public string Output => string.Join('\n', _output);

    public static async Task<DaemonProcess> StartAsync(string dataDirectory, string pipeName, bool shadowMode = false)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "daemon-host", "AgentStateDaemon.Host.exe");
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("The built daemon host executable was not copied to the integration-test directory.");
        }

        var startInfo = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(pipeName);
        if (shadowMode)
            startInfo.ArgumentList.Add("--shadow-mode");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The daemon process could not be started.");
        var daemon = new DaemonProcess(process, pipeName);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                var output = daemon.Output;
                await daemon.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"The daemon exited during startup with code {exitCode}. {output}");
            }

            await using var client = new AgentStateDaemonClient(pipeName);
            try
            {
                await client.ConnectAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                return daemon;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        await daemon.DisposeAsync().ConfigureAwait(false);
        throw new TimeoutException("The daemon Named Pipe did not become available before the startup deadline.");
    }

    public async Task<AgentStateDaemonClient> ConnectClientAsync()
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 40 && !_process.HasExited; attempt++)
        {
            var client = new AgentStateDaemonClient(PipeName);
            try
            {
                await client.ConnectAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                return client;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
            {
                lastFailure = exception;
                await client.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("The daemon Named Pipe could not be reconnected after retries.", lastFailure);
    }

    public async Task WaitForOutputAsync(string expectedText)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_output.Any(line => line.Contains(expectedText, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException("Expected daemon log event was not observed.");
    }

    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(_standardOutputPump, _standardErrorPump).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await KillAsync().ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task PumpLinesAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _output.Enqueue(line);
            }
        }
        catch (IOException)
        {
        }
    }
}
