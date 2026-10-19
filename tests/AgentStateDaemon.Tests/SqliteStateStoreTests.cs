using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Storage.Sqlite;
using Xunit;

namespace AgentStateDaemon.Tests;

[Collection(SerialTestGroup.Name)]
public sealed class SqliteStateStoreTests
{
    private static readonly string[] ExpectedTables = ["accounts", "daemon_meta", "schema_migrations"];
    private static readonly string[] ExpectedAccountColumns = ["account_id", "display_name", "nickname", "health", "is_active", "sort_order", "created_at_utc", "plan_type", "revision", "updated_at_utc"];

    [Fact]
    public void EmptyDatabaseMigratesToLatestWithDeliberateSqliteSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);

        store.Initialize();

        Assert.Equal(SqliteStateStore.CurrentSchemaVersion, store.SchemaVersionForTests());
        Assert.Equal("ok", store.IntegrityCheckForTests());
        var settings = store.DatabaseSettingsForTests();
        Assert.Equal("wal", settings.JournalMode, ignoreCase: true);
        Assert.Equal(2, settings.Synchronous);
        Assert.Equal(1, settings.ForeignKeys);
        Assert.Equal(SqliteStateStore.BusyTimeoutMilliseconds, settings.BusyTimeout);
        Assert.Equal(ExpectedTables, store.TableNamesForTests());
    }

    [Fact]
    public void ExistingLatestDatabaseIsNotRemigrated()
    {
        using var directory = new TemporaryDirectory();
        var initial = new SqliteStateStore(directory.Path);
        initial.Initialize();
        var observedSteps = new List<string>();
        var reopened = new SqliteStateStore(directory.Path, TimeProvider.System, observedSteps.Add, null);

        reopened.Initialize();

        Assert.Empty(observedSteps);
        Assert.Equal(1, reopened.SchemaVersionForTests());
        Assert.Equal("ok", reopened.IntegrityCheckForTests());
    }

    [Fact]
    public void FailedInitialMigrationRollsBackSchemaAndLedger()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path, TimeProvider.System, _ => throw new MigrationTestException(), null);

        Assert.Throws<MigrationTestException>(store.Initialize);

        Assert.Equal(0, store.SchemaVersionForTests());
        Assert.Empty(store.TableNamesForTests());
        Assert.Equal("ok", store.IntegrityCheckForTests());
    }

    [Fact]
    public void NewerDatabaseSchemaIsRefusedWithoutDowngrade()
    {
        using var directory = new TemporaryDirectory();
        var initial = new SqliteStateStore(directory.Path);
        initial.Initialize();
        initial.SetSchemaVersionForTests(SqliteStateStore.CurrentSchemaVersion + 1);
        var reopened = new SqliteStateStore(directory.Path);

        var error = Assert.Throws<DaemonStorageException>(reopened.Initialize);

        Assert.Equal("unsupported_schema_version", error.ErrorCode);
        Assert.Equal(SqliteStateStore.CurrentSchemaVersion + 1, reopened.SchemaVersionForTests());
        Assert.Equal("ok", reopened.IntegrityCheckForTests());
    }

    [Fact]
    public void MetadataMutationChangesOpaqueRevisionAndCanClearNickname()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);

        var updated = store.UpdateNickname(account.AccountId, "Work", account.Revision);
        var cleared = store.UpdateNickname(account.AccountId, string.Empty, updated.Revision);

        Assert.Equal("Work", updated.Nickname);
        Assert.NotEqual(account.Revision, updated.Revision);
        Assert.Null(cleared.Nickname);
        Assert.NotEqual(updated.Revision, cleared.Revision);
        Assert.Equal("2026-10-01T12:00:00.000Z", new AgentStateDaemon.Core.Accounts.AccountService(store).Get(account.AccountId).Account.CreatedAt);
    }

    [Fact]
    public void StaleExpectedRevisionIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);
        store.UpdateNickname(account.AccountId, "First", account.Revision);

        Assert.Throws<AccountRevisionConflictException>(() => store.UpdateNickname(account.AccountId, "Stale", account.Revision));
        Assert.Equal("First", store.GetById(account.AccountId).Nickname);
    }

    [Fact]
    public void FailedMutationRollsBackBeforeReopen()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path, TimeProvider.System, null, () => throw new MutationTestException());
        store.Initialize();
        var account = SyntheticAccount.Create();
        store.SeedForTests(account);

        Assert.Throws<MutationTestException>(() => store.UpdateNickname(account.AccountId, "Uncommitted nickname", account.Revision));

        var reopened = new SqliteStateStore(directory.Path);
        reopened.Initialize();
        var persisted = reopened.GetById(account.AccountId);
        Assert.Null(persisted.Nickname);
        Assert.Equal(account.Revision, persisted.Revision);
        Assert.Equal("ok", reopened.IntegrityCheckForTests());
    }

    [Fact]
    public async Task RacingUpdatesWithSameRevisionHaveOneWinner()
    {
        using var directory = new TemporaryDirectory();
        var firstStore = new SqliteStateStore(directory.Path);
        firstStore.Initialize();
        var account = SyntheticAccount.Create();
        firstStore.SeedForTests(account);
        var secondStore = new SqliteStateStore(directory.Path);
        secondStore.Initialize();

        var outcomes = await Task.WhenAll(
            Task.Run(() => TryUpdate(firstStore, account, "First")),
            Task.Run(() => TryUpdate(secondStore, account, "Second")));

        Assert.Equal(1, outcomes.Count(outcome => outcome));
        Assert.Equal(1, outcomes.Count(outcome => !outcome));
        Assert.NotEqual(account.Revision, firstStore.GetById(account.AccountId).Revision);
        Assert.Equal("ok", firstStore.IntegrityCheckForTests());
    }

    [Fact]
    public void AccountSchemaHasNoCredentialOrSecretColumns()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStateStore(directory.Path);
        store.Initialize();
        var names = store.AccountColumnNamesForTests();

        Assert.Equal(
            ExpectedAccountColumns,
            names);
        Assert.DoesNotContain(names, column => column.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, column => column.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, column => column.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryUpdate(SqliteStateStore store, AccountRecord account, string nickname)
    {
        try
        {
            store.UpdateNickname(account.AccountId, nickname, account.Revision);
            return true;
        }
        catch (AccountRevisionConflictException)
        {
            return false;
        }
    }

    private sealed class MigrationTestException : Exception
    {
    }

    private sealed class MutationTestException : Exception
    {
    }
}
