using System.Text.Json;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Core.Protocol;
using AgentStateDaemon.Storage.Sqlite;
using Xunit;

namespace AgentStateDaemon.Tests;

[Collection(SerialTestGroup.Name)]
public sealed class NicknameAuthorityTests
{
    [Fact]
    public void FreshAndExistingV1DatabasesRemainLegacyLocalUntilExplicitActivation()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();

        Assert.Equal(1, store.SchemaVersionForTests());
        Assert.Equal(NicknameAuthorityStates.LegacyLocal, CreateService(store).GetStatus().State);

        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        store.DeleteAuthorityMarkerForTests();
        var reopened = new SqliteStateStore(directory.Path);
        reopened.Initialize();

        Assert.Equal(1, reopened.SchemaVersionForTests());
        Assert.Equal(NicknameAuthorityStates.LegacyLocal, CreateService(reopened).GetStatus().State);
        Assert.Equal(account.Revision, reopened.GetById(account.AccountId).Revision);
        Assert.Null(reopened.GetById(account.AccountId).Nickname);
    }

    [Fact]
    public void ExperimentalModeOffDoesNotStartMigrationAndLegacyWritesRemainAvailable()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        var service = CreateService(store, experimentalEnabled: false);

        var error = Assert.Throws<AuthorityOperationException>(() => service.BeginPreparation(0));
        Assert.Equal("experimental_feature_disabled", error.ErrorCode);
        var legacyUpdated = store.UpdateNickname(account.AccountId, "Legacy", account.Revision);

        Assert.Equal(1, store.SchemaVersionForTests());
        Assert.Equal(NicknameAuthorityStates.LegacyLocal, service.GetStatus().State);
        Assert.Equal("Legacy", legacyUpdated.Nickname);
    }

    [Fact]
    public void AuthorityMarkerContainsNoAccountOrNicknameValues()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create() with { Nickname = "SYNTHETIC_PRIVATE_NICKNAME" };
        store.SeedForTests(account);
        CreateService(store).BeginPreparation(0);

        var marker = store.GetAuthorityMarkerForTests();
        Assert.NotNull(marker);
        Assert.Contains("Preparing", marker, StringComparison.Ordinal);
        Assert.Contains(CanonicalProtocolPin.ManifestSha256, marker, StringComparison.Ordinal);
        Assert.DoesNotContain(account.AccountId, marker, StringComparison.Ordinal);
        Assert.DoesNotContain(account.DisplayName, marker, StringComparison.Ordinal);
        Assert.DoesNotContain(account.Nickname!, marker, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerCorruptionAndImpossibleVersionCombinationsFailClosed()
    {
        using var corruptDirectory = new TemporaryDirectory();
        var corrupt = new SqliteStateStore(corruptDirectory.Path);
        corrupt.Initialize();
        corrupt.SetAuthorityMarkerForTests("{");
        Assert.Equal("database_unhealthy", Assert.Throws<DaemonStorageException>(() => new SqliteStateStore(corruptDirectory.Path).Initialize()).ErrorCode);

        using var impossibleDirectory = new TemporaryDirectory();
        var impossible = new SqliteStateStore(impossibleDirectory.Path);
        impossible.Initialize();
        impossible.SetSchemaVersionForTests(2);
        Assert.Equal("database_unhealthy", Assert.Throws<DaemonStorageException>(() => new SqliteStateStore(impossibleDirectory.Path).Initialize()).ErrorCode);

        using var activeV1Directory = new TemporaryDirectory();
        var activeV1 = new SqliteStateStore(activeV1Directory.Path);
        activeV1.Initialize();
        activeV1.SetAuthorityMarkerForTests(AuthorityMarker("DaemonNicknameAuthority", 3, "synthetic-migration"));
        Assert.Equal("database_unhealthy", Assert.Throws<DaemonStorageException>(() => new SqliteStateStore(activeV1Directory.Path).Initialize()).ErrorCode);

        using var eagerLedgerDirectory = new TemporaryDirectory();
        var eagerLedger = new SqliteStateStore(eagerLedgerDirectory.Path);
        eagerLedger.Initialize();
        eagerLedger.AddMigrationForTests("0002_nickname_authority");
        Assert.Equal("database_unhealthy", Assert.Throws<DaemonStorageException>(() => new SqliteStateStore(eagerLedgerDirectory.Path).Initialize()).ErrorCode);

        using var missingV2MarkerDirectory = new TemporaryDirectory();
        var missingV2Marker = new SqliteStateStore(missingV2MarkerDirectory.Path);
        missingV2Marker.Initialize();
        var active = Activate(CreateService(missingV2Marker), CreateService(missingV2Marker).BeginPreparation(0));
        Assert.Equal(NicknameAuthorityStates.DaemonNicknameAuthority, active.State);
        missingV2Marker.DeleteAuthorityMarkerForTests();
        Assert.Equal("database_unhealthy", Assert.Throws<DaemonStorageException>(() => new SqliteStateStore(missingV2MarkerDirectory.Path).Initialize()).ErrorCode);

        using var preparingV2Directory = new TemporaryDirectory();
        var preparingV2 = new SqliteStateStore(preparingV2Directory.Path);
        preparingV2.Initialize();
        var activeState = Activate(CreateService(preparingV2), CreateService(preparingV2).BeginPreparation(0));
        Assert.Equal(2, preparingV2.SchemaVersionForTests());
        preparingV2.SetAuthorityMarkerForTests(AuthorityMarker("Preparing", 1, activeState.MigrationId!));
        Assert.Equal("database_unhealthy", Assert.Throws<DaemonStorageException>(() => new SqliteStateStore(preparingV2Directory.Path).Initialize()).ErrorCode);

    }

    [Theory]
    [InlineData("after_verified_read")]
    [InlineData("after_marker_update")]
    [InlineData("after_ledger_insert")]
    [InlineData("after_user_version_update")]
    [InlineData("before_activation_commit")]
    public void FailedActivationRollsBackMarkerLedgerAndDatabaseVersion(string failurePoint)
    {
        using var directory = new TemporaryDirectory();
        var injectedStore = new SqliteStateStore(
            directory.Path,
            TimeProvider.System,
            migrationObserver: null,
            beforeAccountCommit: null,
            activationObserver: stage =>
            {
                if (stage == failurePoint)
                    throw new ActivationTestException();
            });
        injectedStore.Initialize();
        var service = CreateService(injectedStore);
        var preparing = service.BeginPreparation(0);
        var verified = service.MarkVerified(preparing.Epoch, preparing.MigrationId!);

        Assert.Throws<ActivationTestException>(() => service.Activate(verified.Epoch, verified.MigrationId!));

        var reopened = new SqliteStateStore(directory.Path);
        reopened.Initialize();
        Assert.Equal(NicknameAuthorityStates.Verified, CreateService(reopened).GetStatus().State);
        Assert.Equal(1, reopened.SchemaVersionForTests());
        Assert.False(reopened.HasMigrationForTests("0002_nickname_authority"));
        Assert.True(reopened.HasMigrationForTests("0001_initial"));
    }

    [Fact]
    public void LifecyclePersistsAcrossRestartAndRollbackDoesNotDowngrade()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        var preparing = CreateService(store).BeginPreparation(0);

        store = Reopen(directory.Path);
        Assert.Equal(NicknameAuthorityStates.Preparing, CreateService(store, experimentalEnabled: false).GetStatus().State);
        var verified = CreateService(store).MarkVerified(preparing.Epoch, preparing.MigrationId!);

        store = Reopen(directory.Path);
        Assert.Equal(NicknameAuthorityStates.Verified, CreateService(store).GetStatus().State);
        var active = CreateService(store).Activate(verified.Epoch, verified.MigrationId!);

        store = Reopen(directory.Path);
        Assert.Equal(NicknameAuthorityStates.DaemonNicknameAuthority, CreateService(store, experimentalEnabled: false).GetStatus().State);
        Assert.Equal(2, store.SchemaVersionForTests());
        var rollback = CreateService(store).BeginRollback(active.Epoch, active.MigrationId!);

        store = Reopen(directory.Path);
        Assert.Equal(NicknameAuthorityStates.RollbackRequired, CreateService(store).GetStatus().State);
        Assert.Equal("legacy_operation_fenced", Assert.Throws<AuthorityOperationException>(
            () => store.UpdateNickname(account.AccountId, "Blocked", store.GetById(account.AccountId).Revision)).ErrorCode);
        Assert.Equal("legacy_operation_fenced", Assert.Throws<AuthorityOperationException>(() => store.ReconcileShadowAccounts([])).ErrorCode);
        var legacy = CreateService(store).CompleteRollback(rollback.Epoch, rollback.MigrationId!);

        store = Reopen(directory.Path);
        Assert.Equal(NicknameAuthorityStates.LegacyLocal, legacy.State);
        Assert.Equal(NicknameAuthorityStates.LegacyLocal, CreateService(store).GetStatus().State);
        Assert.Equal(2, store.SchemaVersionForTests());
        Assert.True(store.HasMigrationForTests("0002_nickname_authority"));
        var legacyWrite = store.UpdateNickname(account.AccountId, "After rollback", store.GetById(account.AccountId).Revision);
        Assert.Equal("After rollback", legacyWrite.Nickname);
        Assert.Throws<AuthorityOperationException>(() => CreateService(store).BeginPreparation(legacy.Epoch));
    }

    [Fact]
    public void AuthorityFencesLegacyWritesAndRequiresEpochAndMigrationIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        var service = CreateService(store);
        var preparing = service.BeginPreparation(0);

        var legacyUpdate = Assert.Throws<AuthorityOperationException>(() => store.UpdateNickname(account.AccountId, "Legacy overwrite", account.Revision));
        Assert.Equal("legacy_operation_fenced", legacyUpdate.ErrorCode);
        var legacyReconcile = Assert.Throws<AuthorityOperationException>(() => store.ReconcileShadowAccounts([]));
        Assert.Equal("legacy_operation_fenced", legacyReconcile.ErrorCode);
        Assert.Equal(account.Revision, store.GetById(account.AccountId).Revision);

        var mutation = Mutation(account, "Work", "Account");
        Assert.Equal("authority_state_conflict", Assert.Throws<AuthorityOperationException>(
            () => service.UpdateNickname(mutation, preparing.Epoch - 1, preparing.MigrationId)).ErrorCode);
        Assert.Equal("authority_state_conflict", Assert.Throws<AuthorityOperationException>(
            () => service.UpdateNickname(mutation, preparing.Epoch, "wrong-migration")).ErrorCode);

        var written = service.UpdateNickname(mutation, preparing.Epoch, preparing.MigrationId);
        Assert.Equal("Work", written.NicknameState.Nickname);
        var verified = service.MarkVerified(preparing.Epoch, preparing.MigrationId!);
        Assert.Equal("authority_state_conflict", Assert.Throws<AuthorityOperationException>(
            () => service.UpdateNickname(Mutation(store.GetById(account.AccountId), "No write", "Account"), verified.Epoch, verified.MigrationId)).ErrorCode);
        Assert.Equal("authority_state_conflict", Assert.Throws<AuthorityOperationException>(() => service.ReconcileSource(
            new AccountSourceCollection { Accounts = [Source(account.AccountId, "Account")] },
            verified.Epoch,
            verified.MigrationId)).ErrorCode);
    }

    [Fact]
    public void NicknameSetAtSignAndClearAreExactAtomicAndIndependentOfDescriptorFallback()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        var service = CreateService(store);
        var preparing = service.BeginPreparation(0);
        var active = Activate(service, preparing);

        var set = service.UpdateNickname(Mutation(store.GetById(account.AccountId), "synthetic@nickname", "Account"), active.Epoch, null);
        Assert.Equal("synthetic@nickname", set.NicknameState.Nickname);
        Assert.Equal("synthetic@nickname", set.Account.DisplayName);
        Assert.Equal("synthetic@nickname", service.GetNickname(account.AccountId).Nickname);

        var invalidEmpty = Assert.Throws<AccountValidationException>(() => service.UpdateNickname(
            Mutation(store.GetById(account.AccountId), string.Empty, "Account"), active.Epoch, null));
        Assert.NotNull(invalidEmpty);
        var invalidEmailFallback = Assert.Throws<AccountValidationException>(() => service.UpdateNickname(
            Mutation(store.GetById(account.AccountId), null, "user@fallback"), active.Epoch, null));
        Assert.NotNull(invalidEmailFallback);

        var clear = service.UpdateNickname(Mutation(store.GetById(account.AccountId), null, "Current Base"), active.Epoch, null);
        Assert.Null(clear.NicknameState.Nickname);
        Assert.Equal("Current Base", clear.Account.DisplayName);
        Assert.Equal("Current Base", store.GetById(account.AccountId).DisplayName);
    }

    [Fact]
    public void SourceReconcilePreservesNicknameAndRejectsHiddenFallbackAtomically()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        var service = CreateService(store);
        var preparing = service.BeginPreparation(0);
        var active = Activate(service, preparing);
        service.UpdateNickname(Mutation(store.GetById(account.AccountId), "Work", "Account"), active.Epoch, null);

        var sourceUpdate = Source(account.AccountId, "Account", health: "needs_relogin", sortOrder: 7);
        var updated = service.ReconcileSource(new AccountSourceCollection { Accounts = [sourceUpdate] }, active.Epoch, null);
        var stored = store.GetById(account.AccountId);
        Assert.Equal(1, updated.Updated);
        Assert.Equal("Work", stored.Nickname);
        Assert.Equal("Account", stored.DisplayName);
        Assert.Equal("Work", AccountService.ToDescriptor(stored).DisplayName);
        Assert.NotEqual(account.Revision, stored.Revision);

        var invalidCollection = new AccountSourceCollection
        {
            Accounts =
            [
                Source("synthetic-new", "Synthetic New"),
                Source(account.AccountId, "Hidden Alice")
            ]
        };
        Assert.Throws<AccountValidationException>(() => service.ReconcileSource(invalidCollection, active.Epoch, null));
        Assert.Throws<AccountNotFoundException>(() => store.GetById("synthetic-new"));
        Assert.Equal("Work", store.GetById(account.AccountId).Nickname);
        Assert.Equal("Account", store.GetById(account.AccountId).DisplayName);

        var removed = service.ReconcileSource(new AccountSourceCollection { Accounts = [] }, active.Epoch, null);
        Assert.Equal(1, removed.Deleted);
        Assert.Throws<AccountNotFoundException>(() => store.GetById(account.AccountId));
    }

    [Fact]
    public async Task ConcurrentNicknameCasHasExactlyOneWinner()
    {
        using var directory = new TemporaryDirectory();
        var firstStore = new SqliteStateStore(directory.Path);
        firstStore.Initialize();
        var account = SyntheticAccount.Create();
        firstStore.SeedForTests(account);
        var service = CreateService(firstStore);
        var preparing = service.BeginPreparation(0);
        var active = Activate(service, preparing);
        var secondStore = Reopen(directory.Path);
        var firstService = CreateService(firstStore);
        var secondService = CreateService(secondStore);
        var initial = firstService.GetNickname(account.AccountId);

        var outcomes = await Task.WhenAll(
            TryUpdateAsync(firstService, Mutation(firstStore.GetById(account.AccountId), "Personal", "Account"), active.Epoch),
            TryUpdateAsync(secondService, Mutation(secondStore.GetById(account.AccountId), "Team", "Account"), active.Epoch));

        Assert.Equal(1, outcomes.Count(outcome => outcome));
        Assert.Equal(1, outcomes.Count(outcome => !outcome));
        var current = firstService.GetNickname(account.AccountId);
        Assert.NotEqual(initial.Revision, current.Revision);
        Assert.True(current.Nickname is "Personal" or "Team");
        Assert.Equal(current.Nickname, AccountService.ToDescriptor(firstStore.GetById(account.AccountId)).DisplayName);
    }

    [Fact]
    public async Task LegacyWriteRacingPreparationEitherCommitsBeforeFenceOrIsRejectedAfterIt()
    {
        using var directory = new TemporaryDirectory();
        var initial = new SqliteStateStore(directory.Path);
        initial.Initialize();
        var account = SyntheticAccount.Create();
        initial.SeedForTests(account);

        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var legacyStore = new SqliteStateStore(
            directory.Path,
            TimeProvider.System,
            migrationObserver: null,
            beforeAccountCommit: () =>
            {
                writerEntered.Set();
                if (!releaseWriter.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Synthetic legacy write was not released.");
            });
        legacyStore.Initialize();
        var authorityStore = Reopen(directory.Path);
        var legacyWrite = Task.Run(() => legacyStore.UpdateNickname(account.AccountId, "Legacy first", account.Revision));
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(3)));

        using var preparationStarted = new ManualResetEventSlim();
        var preparation = Task.Run(() =>
        {
            preparationStarted.Set();
            return CreateService(authorityStore).BeginPreparation(0);
        });
        Assert.True(preparationStarted.Wait(TimeSpan.FromSeconds(3)));
        await Task.Delay(100);
        Assert.False(preparation.IsCompleted);

        releaseWriter.Set();
        await legacyWrite.WaitAsync(TimeSpan.FromSeconds(3));
        var preparing = await preparation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NicknameAuthorityStates.Preparing, preparing.State);
        Assert.Equal("Legacy first", authorityStore.GetById(account.AccountId).Nickname);

        var fencedLegacyWrite = Assert.Throws<AuthorityOperationException>(() =>
            authorityStore.UpdateNickname(account.AccountId, "Legacy after", authorityStore.GetById(account.AccountId).Revision));
        Assert.Equal("legacy_operation_fenced", fencedLegacyWrite.ErrorCode);
        Assert.Equal("Legacy first", authorityStore.GetById(account.AccountId).Nickname);
    }

    [Fact]
    public void NicknameWriteFailureLeavesClearAndFallbackUncommittedTogether()
    {
        using var directory = new TemporaryDirectory();
        var initialStore = new SqliteStateStore(directory.Path);
        initialStore.Initialize();
        var account = SyntheticAccount.Create();
        initialStore.SeedForTests(account);
        var service = CreateService(initialStore);
        var preparing = service.BeginPreparation(0);
        var active = Activate(service, preparing);
        var set = service.UpdateNickname(Mutation(initialStore.GetById(account.AccountId), "Work", "Account"), active.Epoch, null);

        var injectedStore = new SqliteStateStore(
            directory.Path,
            TimeProvider.System,
            migrationObserver: null,
            beforeAccountCommit: () => throw new MutationTestException());
        injectedStore.Initialize();
        var injectedService = CreateService(injectedStore);

        Assert.Throws<MutationTestException>(() => injectedService.UpdateNickname(
            Mutation(injectedStore.GetById(account.AccountId), null, "Current Base"), active.Epoch, null));

        var reopened = Reopen(directory.Path);
        var persisted = reopened.GetById(account.AccountId);
        Assert.Equal("Work", persisted.Nickname);
        Assert.Equal("Account", persisted.DisplayName);
        Assert.Equal(set.NicknameState.Revision, persisted.Revision);
    }

    private static NicknameAuthorityService CreateService(SqliteStateStore store, bool experimentalEnabled = true) =>
        new(store, store, experimentalEnabled);

    private static SqliteStateStore Reopen(string path)
    {
        var store = new SqliteStateStore(path);
        store.Initialize();
        return store;
    }

    private static NicknameAuthorityStatus Activate(NicknameAuthorityService service, NicknameAuthorityStatus preparing)
    {
        var verified = service.MarkVerified(preparing.Epoch, preparing.MigrationId!);
        return service.Activate(verified.Epoch, verified.MigrationId!);
    }

    private static AccountNicknameMutation Mutation(AccountRecord account, string? nickname, string safeFallback) => new()
    {
        SchemaVersion = 1,
        AccountId = account.AccountId,
        Nickname = nickname,
        SafeFallbackDisplayName = safeFallback,
        ExpectedRevision = account.Revision
    };

    private static string AuthorityMarker(string state, long epoch, string migrationId) => JsonSerializer.Serialize(new
    {
        state,
        epoch,
        protocolManifestSha256 = CanonicalProtocolPin.ManifestSha256,
        migrationId
    });

    private static AccountSourceDescriptor Source(
        string accountId,
        string fallback,
        string health = "valid",
        int sortOrder = 0) => new()
        {
            AccountId = accountId,
            SafeFallbackDisplayName = fallback,
            Health = health,
            IsActive = true,
            SortOrder = sortOrder,
            CreatedAt = "2026-10-20T12:00:00Z",
            PlanType = "plus"
        };

    private static Task<bool> TryUpdateAsync(
        NicknameAuthorityService service,
        AccountNicknameMutation mutation,
        long epoch)
    {
        return Task.Run(() =>
        {
            try
            {
                service.UpdateNickname(mutation, epoch, migrationId: null);
                return true;
            }
            catch (AccountRevisionConflictException)
            {
                return false;
            }
        });
    }

    private sealed class ActivationTestException : Exception;

    private sealed class MutationTestException : Exception;
}
