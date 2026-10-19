using System.Reflection;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Rpc;
using AgentStateDaemon.Storage.Sqlite;
using AgentStateDaemon.Transport.NamedPipes;

namespace AgentStateDaemon.Host;

internal static class Program
{
    private const string DefaultPipeName = "agent-state-daemon-v1";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("error=windows_named_pipe_transport_required");
            return 2;
        }

        HostArguments options;
        try
        {
            options = HostArguments.Parse(args);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("error=invalid_arguments");
            return 2;
        }

        try
        {
            ProtocolArtifactVerifier.Verify(AppContext.BaseDirectory);
            using var dataDirectoryLease = DataDirectoryLease.Acquire(options.DataDirectory);
            var store = new SqliteStateStore(options.DataDirectory);
            store.Initialize();

            var timeProvider = TimeProvider.System;
            var startedAt = timeProvider.GetUtcNow();
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.1.0-experimental";
            var dispatcher = new JsonRpcDispatcher(
                new AccountService(store),
                version,
                startedAt,
                timeProvider,
                WriteRpcLog,
                options.ShadowMode ? new ShadowAccountService(store) : null,
                options.ShadowMode);
            var server = new NamedPipeRpcServer(options.PipeName, dispatcher, WriteRpcLog);

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                Console.WriteLine($"event=daemon_started version={version} protocol_schema_version=1 shadow_mode={options.ShadowMode.ToString().ToLowerInvariant()}");
                await server.RunAsync(shutdown.Token).ConfigureAwait(false);
                Console.WriteLine("event=daemon_stopped");
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            return 0;
        }
        catch (DaemonStorageException exception)
        {
            Console.Error.WriteLine($"error={exception.ErrorCode}");
            return 3;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("error=data_directory_unavailable");
            return 4;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("error=daemon_startup_failed");
            return 5;
        }
    }

    private static void WriteRpcLog(string method, string result, long durationMilliseconds) =>
        Console.WriteLine($"event=rpc method={method} result={result} duration_ms={durationMilliseconds}");
}
