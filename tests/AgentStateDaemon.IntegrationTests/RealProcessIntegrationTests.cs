using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AgentStateDaemon.Client;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Core.Protocol;
using AgentStateDaemon.Rpc;
using AgentStateDaemon.Storage.Sqlite;
using AgentStateDaemon.Transport.NamedPipes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentStateDaemon.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class RealProcessIntegrationTests
{
    private static readonly string?[] RaceNicknames = ["Race A", "Race B"];

    [Fact]
    public async Task RealDaemonAndClientCompleteRevisionedAccountWorkflowAndRecoverAfterCrash()
    {
        using var directory = new IntegrationTemporaryDirectory();
        var pipeName = TestPipeName();
        var database = new SqliteStateStore(directory.Path);
        database.Initialize();
        var original = CreateSyntheticAccount();

        await using var daemon = await DaemonProcess.StartAsync(directory.Path, pipeName);
        await using (var firstClient = await daemon.ConnectClientAsync())
        {
            Assert.True(await firstClient.PingAsync());
            var empty = await firstClient.ListAccountsAsync();
            Assert.Empty(empty.Collection.Accounts);

            database.SeedForTests(original);
            var listed = await firstClient.ListAccountsAsync();
            var revisionFromList = listed.Revisions[original.AccountId];
            var fetched = await firstClient.GetAccountAsync(original.AccountId);
            Assert.Equal(revisionFromList, fetched.Revision);
            Assert.Equal(original.AccountId, fetched.Account.AccountId);

            var update = await firstClient.UpdateMetadataAsync(new AccountMetadataUpdate
            {
                SchemaVersion = 1,
                AccountId = original.AccountId,
                Nickname = "Work",
                ExpectedRevision = fetched.Revision
            });
            Assert.Equal("Work", update.Account.DisplayName);
            Assert.NotEqual(fetched.Revision, update.Revision);

            var stale = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() => firstClient.UpdateMetadataAsync(new AccountMetadataUpdate
            {
                SchemaVersion = 1,
                AccountId = original.AccountId,
                Nickname = "Stale write",
                ExpectedRevision = fetched.Revision
            }));
            Assert.Equal("revision_conflict", stale.ErrorCode);

            await using var secondClient = await daemon.ConnectClientAsync();
            var simultaneousReads = await Task.WhenAll(
                firstClient.ListAccountsAsync(),
                secondClient.ListAccountsAsync());
            Assert.All(simultaneousReads, result => Assert.Single(result.Collection.Accounts));

            var current = await firstClient.GetAccountAsync(original.AccountId);
            var raceResults = await Task.WhenAll(
                TryUpdateAsync(firstClient, original.AccountId, current.Revision, "Race A"),
                TryUpdateAsync(secondClient, original.AccountId, current.Revision, "Race B"));
            Assert.Equal(1, raceResults.Count(succeeded => succeeded));
            Assert.Equal(1, raceResults.Count(succeeded => !succeeded));

            var info = await firstClient.GetSystemInfoAsync();
            Assert.Equal(1, info.ProtocolSchemaVersion);
            Assert.Equal(CanonicalProtocolPin.ManifestSha256, info.ProtocolManifestSha256);
            Assert.Equal(1, info.DatabaseSchemaVersion);
            Assert.DoesNotContain(directory.Path, JsonSerializer.Serialize(info), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, JsonSerializer.Serialize(info), StringComparison.OrdinalIgnoreCase);

            const string marker = "SYNTHETIC_ACCESS_TOKEN_DO_NOT_PRINT";
            var rawRequest = "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"accounts.get\",\"params\":{\"accountId\":\"" + original.AccountId + "\",\"apiKey\":\"" + marker + "\"}}";
            var rawResponse = await SendRawAsync(pipeName, rawRequest);
            Assert.DoesNotContain(marker, rawResponse, StringComparison.Ordinal);
            await daemon.WaitForOutputAsync("result=invalid_params");
            Assert.DoesNotContain(marker, daemon.Output, StringComparison.Ordinal);

            var unavailableShadowResponse = await SendRawAsync(pipeName,
                "{\"jsonrpc\":\"2.0\",\"id\":100,\"method\":\"shadow.accounts.reconcile\",\"params\":{\"collection\":{\"schemaVersion\":1,\"accounts\":[]}}}");
            using var unavailableShadow = JsonDocument.Parse(unavailableShadowResponse);
            Assert.Equal(-32601, unavailableShadow.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        await daemon.KillAsync();
        SqliteConnection.ClearAllPools();
        AssertNoSecretMarkersOnDisk(directory.Path, "SYNTHETIC_ACCESS_TOKEN_DO_NOT_PRINT", "SYNTHETIC_API_KEY_DO_NOT_PRINT");
        var beforeCrash = new SqliteStateStore(directory.Path);
        beforeCrash.Initialize();
        var committedBeforeCrash = beforeCrash.GetById(original.AccountId);
        Assert.Contains(committedBeforeCrash.Nickname, RaceNicknames);

        var helperPath = Path.Combine(AppContext.BaseDirectory, "test-support", "AgentStateDaemon.TestSupport.exe");
        var helperInfo = new System.Diagnostics.ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        helperInfo.ArgumentList.Add("--hold-uncommitted");
        helperInfo.ArgumentList.Add(beforeCrash.GetDatabasePathForTests());
        helperInfo.ArgumentList.Add(original.AccountId);
        using var helper = System.Diagnostics.Process.Start(helperInfo)
            ?? throw new InvalidOperationException("The SQLite crash helper could not be started.");
        var helperReady = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("READY", helperReady);
        helper.Kill(entireProcessTree: true);
        await helper.WaitForExitAsync();
        SqliteConnection.ClearAllPools();

        var restartedPipe = TestPipeName();
        await using var restartedDaemon = await DaemonProcess.StartAsync(directory.Path, restartedPipe);
        await using var restartedClient = await restartedDaemon.ConnectClientAsync();
        var recovered = await restartedClient.GetAccountAsync(original.AccountId);
        Assert.Equal(committedBeforeCrash.Nickname, recovered.Account.DisplayName);
        Assert.Equal(committedBeforeCrash.Revision, recovered.Revision);
        Assert.Equal("ok", beforeCrash.IntegrityCheckForTests());
    }

    [Fact]
    public async Task NamedPipeServerShutdownCancelsIdleClientWithinBound()
    {
        using var directory = new IntegrationTemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var pipeName = TestPipeName();
        var server = new NamedPipeRpcServer(pipeName, new JsonRpcDispatcher(
            new AccountService(store), "test", DateTimeOffset.UtcNow));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);
        await using var client = new AgentStateDaemonClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));
        Assert.True(await client.PingAsync());

        shutdown.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task DaemonRemainsAvailableAfterClientDisconnectsImmediately()
    {
        using var directory = new IntegrationTemporaryDirectory();
        await using var daemon = await DaemonProcess.StartAsync(directory.Path, TestPipeName());

        await using (var abandonedClient = new AgentStateDaemonClient(daemon.PipeName))
        {
            await abandonedClient.ConnectAsync(TimeSpan.FromSeconds(3));
        }

        await using var client = await daemon.ConnectClientAsync();
        Assert.True(await client.PingAsync());
    }

    [Fact]
    public async Task ShadowModeAdvertisesCapabilityAndReconcilesPersistedCanonicalSnapshots()
    {
        using var directory = new IntegrationTemporaryDirectory();
        var pipeName = TestPipeName();
        await using (var daemon = await DaemonProcess.StartAsync(directory.Path, pipeName, shadowMode: true))
        await using (var client = await daemon.ConnectClientAsync())
        {
            using var info = JsonDocument.Parse(await SendRawAsync(pipeName,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"system.info\",\"params\":{}}"));
            var infoResult = info.RootElement.GetProperty("result");
            var capability = infoResult.GetProperty("capabilities").EnumerateArray()
                .Single(item => item.GetProperty("capabilityId").GetString() == "shadow.accounts.reconcile");
            Assert.True(capability.GetProperty("supported").GetBoolean());
            Assert.Equal(CanonicalProtocolPin.ManifestSha256,
                capability.GetProperty("extensions").GetProperty("protocolManifestSha256").GetString());
            var metadataCapability = infoResult.GetProperty("capabilities").EnumerateArray()
                .Single(item => item.GetProperty("capabilityId").GetString() == "accounts.updateMetadata");
            Assert.True(metadataCapability.GetProperty("supported").GetBoolean());
            Assert.Equal(CanonicalProtocolPin.ManifestSha256,
                metadataCapability.GetProperty("extensions").GetProperty("protocolManifestSha256").GetString());

            using var inserted = JsonDocument.Parse(await SendShadowReconcileAsync(pipeName, 2, """
                [
                  {"schemaVersion":1,"accountId":"synthetic-a","displayName":"Synthetic A","health":"valid","isActive":true,"sortOrder":0,"createdAt":"2026-10-20T12:00:00Z","planType":"plus"},
                  {"schemaVersion":1,"accountId":"synthetic-b","displayName":"Synthetic B","health":"unknown","isActive":false,"sortOrder":1,"createdAt":"2026-10-20T12:01:00Z","planType":null}
                ]
                """));
            Assert.True(inserted.RootElement.TryGetProperty("result", out _), inserted.RootElement.GetRawText());
            Assert.Equal(2, inserted.RootElement.GetProperty("result").GetProperty("inserted").GetInt32());

            var initial = await client.ListAccountsAsync();
            Assert.Collection(initial.Collection.Accounts,
                first =>
                {
                    Assert.Equal("synthetic-a", first.AccountId);
                    Assert.Equal("Synthetic A", first.DisplayName);
                    Assert.True(first.IsActive);
                },
                second =>
                {
                    Assert.Equal("synthetic-b", second.AccountId);
                    Assert.Equal(1, second.SortOrder);
                });

            using var changed = JsonDocument.Parse(await SendShadowReconcileAsync(pipeName, 3, """
                [{"schemaVersion":1,"accountId":"synthetic-b","displayName":"Synthetic B2","health":"needs_relogin","isActive":true,"sortOrder":0,"createdAt":"2026-10-20T12:02:00Z","planType":"team"}]
                """));
            var changedResult = changed.RootElement.GetProperty("result");
            Assert.Equal(1, changedResult.GetProperty("updated").GetInt32());
            Assert.Equal(1, changedResult.GetProperty("deleted").GetInt32());

            var final = await client.ListAccountsAsync();
            var only = Assert.Single(final.Collection.Accounts);
            Assert.Equal("synthetic-b", only.AccountId);
            Assert.Equal("Synthetic B2", only.DisplayName);
            Assert.Equal("needs_relogin", only.Health);
            Assert.True(only.IsActive);
            Assert.Equal(0, only.SortOrder);
            Assert.Equal("team", only.PlanType);
        }

        await using var restarted = await DaemonProcess.StartAsync(directory.Path, TestPipeName(), shadowMode: true);
        await using var restartedClient = await restarted.ConnectClientAsync();
        var recovered = Assert.Single((await restartedClient.ListAccountsAsync()).Collection.Accounts);
        Assert.Equal("synthetic-b", recovered.AccountId);
        Assert.Equal("Synthetic B2", recovered.DisplayName);
    }

    [Fact]
    public async Task RealProcessNicknameAuthorityLifecycleFencesLegacyAndOldDaemonAcrossRollback()
    {
        using var preActivationDirectory = new IntegrationTemporaryDirectory();
        var preActivationStore = new SqliteStateStore(preActivationDirectory.Path);
        preActivationStore.Initialize();
        await using (var oldDaemon = await DaemonProcess.StartBaselineAsync(preActivationDirectory.Path, TestPipeName()))
        await using (var oldClient = await oldDaemon.ConnectClientAsync())
        {
            Assert.True(await oldClient.PingAsync());
            Assert.Equal(1, (await oldClient.GetSystemInfoAsync()).DatabaseSchemaVersion);
        }

        using var directory = new IntegrationTemporaryDirectory();
        var initializedStore = new SqliteStateStore(directory.Path);
        initializedStore.Initialize();
        var synthetic = CreateSyntheticAccount();
        initializedStore.SeedForTests(synthetic);
        var pipeName = TestPipeName();
        NicknameAuthorityStatus active;

        await using (var daemon = await DaemonProcess.StartAsync(
            directory.Path,
            pipeName,
            shadowMode: true,
            experimentalNicknameAuthority: true))
        await using (var client = await daemon.ConnectClientAsync())
        {
            var info = await client.GetSystemInfoAsync();
            Assert.Equal(CanonicalProtocolPin.ManifestSha256, info.ProtocolManifestSha256);
            Assert.Equal(1, info.DatabaseSchemaVersion);
            Assert.True(info.Capabilities.Single(item => item.CapabilityId == "accounts.nicknameAuthority.lifecycle").Supported);
            Assert.True(info.Capabilities.Single(item => item.CapabilityId == "accounts.nicknameAuthority.lifecycle").Extensions!.Enabled);
            Assert.True(info.Capabilities.Single(item => item.CapabilityId == "accounts.updateNickname").Supported);
            Assert.False(info.Capabilities.Single(item => item.CapabilityId == "accounts.updateNickname").Extensions!.Enabled);

            var initialAuthority = await client.GetNicknameAuthorityAsync();
            Assert.Equal("LegacyLocal", initialAuthority.State);
            Assert.Equal(0, initialAuthority.Epoch);
            var exactInitialNickname = await client.GetNicknameAsync(synthetic.AccountId);
            Assert.Null(exactInitialNickname.Nickname);
            Assert.Equal(synthetic.Revision, exactInitialNickname.Revision);

            var preparing = await client.BeginNicknameAuthorityPreparationAsync(initialAuthority.Epoch);
            Assert.Equal("Preparing", preparing.State);
            Assert.Equal(1, preparing.Epoch);
            Assert.False(string.IsNullOrWhiteSpace(preparing.MigrationId));

            var staleEpoch = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() =>
                client.MarkNicknameAuthorityVerifiedAsync(initialAuthority.Epoch, preparing.MigrationId!));
            Assert.Equal("authority_state_conflict", staleEpoch.ErrorCode);
            var wrongMigration = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() =>
                client.MarkNicknameAuthorityVerifiedAsync(preparing.Epoch, "wrong-migration"));
            Assert.Equal("authority_state_conflict", wrongMigration.ErrorCode);

            var legacyUpdate = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() => client.UpdateMetadataAsync(new AccountMetadataUpdate
            {
                SchemaVersion = 1,
                AccountId = synthetic.AccountId,
                Nickname = "Legacy overwrite",
                ExpectedRevision = synthetic.Revision
            }));
            Assert.Equal("legacy_operation_fenced", legacyUpdate.ErrorCode);

            using var legacyReconcile = JsonDocument.Parse(await SendRawAsync(pipeName,
                "{\"jsonrpc\":\"2.0\",\"id\":501,\"method\":\"shadow.accounts.reconcile\",\"params\":{\"collection\":{\"schemaVersion\":1,\"accounts\":[]}}}"));
            Assert.Equal("legacy_operation_fenced", legacyReconcile.RootElement.GetProperty("error").GetProperty("data").GetProperty("errorCode").GetString());

            var preparingSet = await client.UpdateNicknameAsync(new AccountNicknameMutation
            {
                SchemaVersion = 1,
                AccountId = synthetic.AccountId,
                Nickname = "Work",
                SafeFallbackDisplayName = "Account",
                ExpectedRevision = exactInitialNickname.Revision
            }, preparing.Epoch, preparing.MigrationId);
            Assert.Equal("Work", preparingSet.NicknameState.Nickname);
            Assert.Equal("Work", preparingSet.Account.DisplayName);

            var sourceUpdate = new AccountSourceDescriptor
            {
                AccountId = synthetic.AccountId,
                SafeFallbackDisplayName = "Account",
                Health = "needs_relogin",
                IsActive = false,
                SortOrder = 4,
                CreatedAt = "2026-10-20T12:00:00Z",
                PlanType = "team"
            };
            var reconciled = await client.ReconcileSourceAccountsAsync(
                new AccountSourceCollection { Accounts = [sourceUpdate] },
                preparing.Epoch,
                preparing.MigrationId);
            Assert.Equal(1, reconciled.Updated);
            var preserved = await client.GetNicknameAsync(synthetic.AccountId);
            Assert.Equal("Work", preserved.Nickname);
            Assert.NotEqual(preparingSet.NicknameState.Revision, preserved.Revision);
            Assert.Equal("Work", (await client.GetAccountAsync(synthetic.AccountId)).Account.DisplayName);

            var verified = await client.MarkNicknameAuthorityVerifiedAsync(preparing.Epoch, preparing.MigrationId!);
            Assert.Equal("Verified", verified.State);
            var frozenMutation = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() => client.UpdateNicknameAsync(
                new AccountNicknameMutation
                {
                    SchemaVersion = 1,
                    AccountId = synthetic.AccountId,
                    Nickname = "Frozen",
                    SafeFallbackDisplayName = "Account",
                    ExpectedRevision = preserved.Revision
                },
                verified.Epoch,
                verified.MigrationId));
            Assert.Equal("authority_state_conflict", frozenMutation.ErrorCode);

            active = await client.ActivateNicknameAuthorityAsync(verified.Epoch, verified.MigrationId!);
            Assert.Equal("DaemonNicknameAuthority", active.State);
            Assert.Equal(2, (await client.GetSystemInfoAsync()).DatabaseSchemaVersion);

            var activeSet = await client.UpdateNicknameAsync(new AccountNicknameMutation
            {
                SchemaVersion = 1,
                AccountId = synthetic.AccountId,
                Nickname = "synthetic@active",
                SafeFallbackDisplayName = "Account",
                ExpectedRevision = preserved.Revision
            }, active.Epoch, migrationId: null);
            Assert.Equal("synthetic@active", activeSet.NicknameState.Nickname);
            Assert.Equal("synthetic@active", activeSet.Account.DisplayName);

            var clear = await client.UpdateNicknameAsync(new AccountNicknameMutation
            {
                SchemaVersion = 1,
                AccountId = synthetic.AccountId,
                Nickname = null,
                SafeFallbackDisplayName = "Synthetic Base",
                ExpectedRevision = activeSet.NicknameState.Revision
            }, active.Epoch, migrationId: null);
            Assert.Null(clear.NicknameState.Nickname);
            Assert.Equal("Synthetic Base", clear.Account.DisplayName);
            Assert.Equal("Synthetic Base", (await client.GetAccountAsync(synthetic.AccountId)).Account.DisplayName);
            Assert.Equal(synthetic.AccountId, Assert.Single((await client.ListNicknamesAsync()).Accounts).AccountId);
        }

        var oldActiveFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DaemonProcess.StartBaselineAsync(directory.Path, TestPipeName()));
        Assert.Contains("unsupported_schema_version", oldActiveFailure.Message, StringComparison.Ordinal);

        await using (var restarted = await DaemonProcess.StartAsync(directory.Path, TestPipeName()))
        await using (var restartedClient = await restarted.ConnectClientAsync())
        {
            Assert.Equal("DaemonNicknameAuthority", (await restartedClient.GetNicknameAuthorityAsync()).State);
            var restartedInfo = await restartedClient.GetSystemInfoAsync();
            Assert.Equal(2, restartedInfo.DatabaseSchemaVersion);
            Assert.True(restartedInfo.Capabilities.Single(item => item.CapabilityId == "accounts.updateNickname").Extensions!.Enabled);
            var currentNickname = await restartedClient.GetNicknameAsync(synthetic.AccountId);
            var updatedWhileFlagOff = await restartedClient.UpdateNicknameAsync(new AccountNicknameMutation
            {
                SchemaVersion = 1,
                AccountId = synthetic.AccountId,
                Nickname = "Restarted active",
                SafeFallbackDisplayName = "Account",
                ExpectedRevision = currentNickname.Revision
            }, active.Epoch, migrationId: null);
            Assert.Equal("Restarted active", updatedWhileFlagOff.NicknameState.Nickname);
        }

        NicknameAuthorityStatus rollbackRequired;
        await using (var rollbackHost = await DaemonProcess.StartAsync(
            directory.Path,
            TestPipeName(),
            experimentalNicknameAuthority: true))
        await using (var rollbackClient = await rollbackHost.ConnectClientAsync())
        {
            rollbackRequired = await rollbackClient.BeginNicknameAuthorityRollbackAsync(active.Epoch, active.MigrationId!);
            Assert.Equal("RollbackRequired", rollbackRequired.State);
            var rollbackRevision = (await rollbackClient.GetNicknameAsync(synthetic.AccountId)).Revision;
            var sourceFrozen = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() => rollbackClient.ReconcileSourceAccountsAsync(
                new AccountSourceCollection { Accounts = [] }, rollbackRequired.Epoch, rollbackRequired.MigrationId));
            Assert.Equal("authority_state_conflict", sourceFrozen.ErrorCode);
            var frozenMutation = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() =>
                rollbackClient.UpdateNicknameAsync(new AccountNicknameMutation
                {
                    SchemaVersion = 1,
                    AccountId = synthetic.AccountId,
                    Nickname = "Frozen rollback",
                    SafeFallbackDisplayName = "Account",
                    ExpectedRevision = rollbackRevision
                }, rollbackRequired.Epoch, rollbackRequired.MigrationId));
            Assert.Equal("authority_state_conflict", frozenMutation.ErrorCode);
        }

        await using (var rollbackRestart = await DaemonProcess.StartAsync(
            directory.Path,
            TestPipeName(),
            experimentalNicknameAuthority: true))
        await using (var rollbackClient = await rollbackRestart.ConnectClientAsync())
        {
            Assert.Equal("RollbackRequired", (await rollbackClient.GetNicknameAuthorityAsync()).State);
            var rollbackRevision = (await rollbackClient.GetNicknameAsync(synthetic.AccountId)).Revision;
            var legacyFence = await Assert.ThrowsAsync<AgentStateDaemonRpcException>(() => rollbackClient.UpdateMetadataAsync(new AccountMetadataUpdate
            {
                SchemaVersion = 1,
                AccountId = synthetic.AccountId,
                Nickname = "Legacy overwrite",
                ExpectedRevision = rollbackRevision
            }));
            Assert.Equal("legacy_operation_fenced", legacyFence.ErrorCode);

            var completed = await rollbackClient.CompleteNicknameAuthorityRollbackAsync(rollbackRequired.Epoch, rollbackRequired.MigrationId!);
            Assert.Equal("LegacyLocal", completed.State);
            Assert.Equal(2, (await rollbackClient.GetSystemInfoAsync()).DatabaseSchemaVersion);
        }

        var oldRollbackFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DaemonProcess.StartBaselineAsync(directory.Path, TestPipeName()));
        Assert.Contains("unsupported_schema_version", oldRollbackFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataDirectoryAllowsOnlyOneDaemonOwner()
    {
        using var directory = new IntegrationTemporaryDirectory();
        await using var daemon = await DaemonProcess.StartAsync(directory.Path, TestPipeName());
        var secondStart = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "daemon-host", "AgentStateDaemon.Host.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        secondStart.ArgumentList.Add("--data-dir");
        secondStart.ArgumentList.Add(directory.Path);
        secondStart.ArgumentList.Add("--pipe-name");
        secondStart.ArgumentList.Add(TestPipeName());
        using var second = System.Diagnostics.Process.Start(secondStart)
            ?? throw new InvalidOperationException("The second daemon process could not be started.");

        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, second.ExitCode);
        Assert.False(daemon.HasExited);
        await daemon.KillAsync();
        Assert.True(daemon.HasExited);
    }

    private static async Task<bool> TryUpdateAsync(AgentStateDaemonClient client, string accountId, string revision, string nickname)
    {
        try
        {
            await client.UpdateMetadataAsync(new AccountMetadataUpdate
            {
                SchemaVersion = 1,
                AccountId = accountId,
                Nickname = nickname,
                ExpectedRevision = revision
            });
            return true;
        }
        catch (AgentStateDaemonRpcException exception) when (exception.ErrorCode == "revision_conflict")
        {
            return false;
        }
    }

    private static async Task<string> SendRawAsync(string pipeName, string request)
    {
        NamedPipeClientStream? pipe = null;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var candidate = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await candidate.ConnectAsync(250);
                pipe = candidate;
                break;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastFailure = exception;
                await candidate.DisposeAsync();
                await Task.Delay(50);
            }
        }

        if (pipe is null)
            throw new TimeoutException("The daemon Named Pipe was not available after retries.", lastFailure);

        await using (pipe.ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(request + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3))
                ?? throw new EndOfStreamException("The daemon closed the pipe before returning a response.");
        }
    }

    private static Task<string> SendShadowReconcileAsync(string pipeName, int requestId, string accountsJson)
    {
        using var accounts = JsonDocument.Parse(accountsJson);
        var compactAccounts = JsonSerializer.Serialize(accounts.RootElement);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":" + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"method\":\"shadow.accounts.reconcile\",\"params\":{\"collection\":{\"schemaVersion\":1,\"accounts\":" + compactAccounts + "}}}";
        return SendRawAsync(pipeName, request);
    }

    private static AccountRecord CreateSyntheticAccount() => new(
        $"synthetic-account-{Guid.NewGuid():N}",
        "Synthetic Account",
        null,
        "valid",
        true,
        0,
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        "test",
        Guid.NewGuid().ToString("N"));

    private static string TestPipeName() => $"agent-state-daemon-test-{Guid.NewGuid():N}";

    private static void AssertNoSecretMarkersOnDisk(string dataDirectory, params string[] markers)
    {
        foreach (var file in Directory.GetFiles(dataDirectory, "state.db*"))
        {
            var content = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, content, StringComparison.Ordinal);
            }
        }
    }

    private sealed class IntegrationTemporaryDirectory : IDisposable
    {
        public IntegrationTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-state-daemon-integration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
