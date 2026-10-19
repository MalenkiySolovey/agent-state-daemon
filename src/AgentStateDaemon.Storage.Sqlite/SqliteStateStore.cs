using System.Globalization;
using AgentStateDaemon.Core.Accounts;
using Microsoft.Data.Sqlite;

namespace AgentStateDaemon.Storage.Sqlite;

internal sealed class SqliteStateStore : IAccountRepository, IShadowAccountRepository
{
    public const int CurrentSchemaVersion = 1;
    public const int BusyTimeoutMilliseconds = 5_000;

    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _migrationObserver;
    private readonly Action? _beforeAccountCommit;
    private int _unhealthy;
    private bool _initialized;

    public SqliteStateStore(string dataDirectory, TimeProvider? timeProvider = null)
        : this(dataDirectory, timeProvider ?? TimeProvider.System, null, null)
    {
    }

    internal SqliteStateStore(
        string dataDirectory,
        TimeProvider timeProvider,
        Action<string>? migrationObserver,
        Action? beforeAccountCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        _databasePath = Path.Combine(fullPath, "state.db");
        _timeProvider = timeProvider;
        _migrationObserver = migrationObserver;
        _beforeAccountCommit = beforeAccountCommit;
    }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            using var connection = OpenConnection(createIfMissing: true);
            var journalMode = ExecuteScalarString(connection, "PRAGMA journal_mode=WAL;");
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new DaemonStorageException("database_unhealthy");
            }

            VerifyIntegrity(connection);
            ApplyMigrations(connection);
            VerifyIntegrity(connection);
            _initialized = true;
        }
        catch (DaemonStorageException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
        catch (IOException)
        {
            throw new DaemonStorageException("storage_failure", retryable: true);
        }
    }

    public IReadOnlyList<AccountRecord> List()
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT account_id, display_name, nickname, health, is_active, sort_order,
                       created_at_utc, plan_type, revision
                FROM accounts
                ORDER BY sort_order, account_id COLLATE BINARY
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", AccountService.MaximumAccountCount + 1);

            using var reader = command.ExecuteReader();
            var records = new List<AccountRecord>();
            while (reader.Read())
            {
                if (records.Count == AccountService.MaximumAccountCount)
                {
                    throw new DaemonStorageException("resource_limit_exceeded");
                }

                records.Add(ReadAccount(reader));
            }

            return records;
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public AccountRecord GetById(string accountId)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var command = CreateGetCommand(connection, null, accountId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new AccountNotFoundException();
            }

            return ReadAccount(reader);
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public AccountRecord UpdateNickname(string accountId, string nickname, string expectedRevision)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = TryGet(connection, transaction, accountId);
            if (current is null)
            {
                throw new AccountNotFoundException();
            }

            if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new AccountRevisionConflictException();
            }

            var newRevision = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE accounts
                    SET nickname = $nickname,
                        revision = $revision,
                        updated_at_utc = $updated_at
                    WHERE account_id = $account_id AND revision = $expected_revision;
                    """;
                command.Parameters.AddWithValue("$nickname", nickname.Length == 0 ? DBNull.Value : nickname);
                command.Parameters.AddWithValue("$revision", newRevision);
                command.Parameters.AddWithValue("$updated_at", FormatTimestamp(_timeProvider.GetUtcNow()));
                command.Parameters.AddWithValue("$account_id", accountId);
                command.Parameters.AddWithValue("$expected_revision", expectedRevision);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new AccountRevisionConflictException();
                }
            }

            _beforeAccountCommit?.Invoke();
            var updated = TryGet(connection, transaction, accountId) ?? throw new DaemonStorageException("storage_failure");
            transaction.Commit();
            return updated;
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public ShadowAccountReconcileResult ReconcileShadowAccounts(IReadOnlyList<AgentStateDaemon.Core.Protocol.AccountDescriptor> accounts)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var currentAccounts = ReadAccounts(connection, transaction);
            var currentById = currentAccounts.ToDictionary(account => account.AccountId, StringComparer.Ordinal);
            var targetIds = new HashSet<string>(accounts.Select(account => account.AccountId), StringComparer.Ordinal);
            var inserted = 0;
            var updated = 0;
            var unchanged = 0;

            foreach (var target in accounts)
            {
                if (!ShadowAccountService.TryParseUtcTimestamp(target.CreatedAt, out var createdAt))
                    throw new AccountValidationException();

                if (currentById.TryGetValue(target.AccountId, out var current))
                {
                    if (IsProjectionEqual(current, target, createdAt))
                    {
                        unchanged++;
                        continue;
                    }

                    UpdateShadowAccount(connection, transaction, target, createdAt);
                    updated++;
                }
                else
                {
                    InsertShadowAccount(connection, transaction, target, createdAt);
                    inserted++;
                }
            }

            var deleted = 0;
            foreach (var current in currentAccounts)
            {
                if (targetIds.Contains(current.AccountId))
                    continue;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM accounts WHERE account_id = $account_id;";
                command.Parameters.AddWithValue("$account_id", current.AccountId);
                deleted += command.ExecuteNonQuery();
            }

            _beforeAccountCommit?.Invoke();
            transaction.Commit();
            return new ShadowAccountReconcileResult(inserted, updated, deleted, unchanged);
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    internal void SeedForTests(AccountRecord account)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (
                account_id, display_name, nickname, health, is_active, sort_order,
                created_at_utc, plan_type, revision, updated_at_utc)
            VALUES (
                $account_id, $display_name, $nickname, $health, $is_active, $sort_order,
                $created_at_utc, $plan_type, $revision, $updated_at_utc);
            """;
        command.Parameters.AddWithValue("$account_id", account.AccountId);
        command.Parameters.AddWithValue("$display_name", account.DisplayName);
        command.Parameters.AddWithValue("$nickname", (object?)account.Nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$health", account.Health);
        command.Parameters.AddWithValue("$is_active", account.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$sort_order", account.SortOrder);
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(account.CreatedAtUtc));
        command.Parameters.AddWithValue("$plan_type", (object?)account.PlanType ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", account.Revision);
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(_timeProvider.GetUtcNow()));
        command.ExecuteNonQuery();
    }

    internal string IntegrityCheckForTests()
    {
        using var connection = OpenConnection();
        return ExecuteScalarString(connection, "PRAGMA integrity_check;");
    }

    internal int SchemaVersionForTests()
    {
        using var connection = OpenConnection();
        return checked((int)ExecuteScalarInt64(connection, "PRAGMA user_version;"));
    }

    internal void SetSchemaVersionForTests(int version)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version={version.ToString(CultureInfo.InvariantCulture)};";
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<string> AccountColumnNamesForTests()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(accounts);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    internal IReadOnlyList<string> TableNamesForTests()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    internal (string JournalMode, long Synchronous, long ForeignKeys, long BusyTimeout) DatabaseSettingsForTests()
    {
        using var connection = OpenConnection();
        return (
            ExecuteScalarString(connection, "PRAGMA journal_mode;"),
            ExecuteScalarInt64(connection, "PRAGMA synchronous;"),
            ExecuteScalarInt64(connection, "PRAGMA foreign_keys;"),
            ExecuteScalarInt64(connection, "PRAGMA busy_timeout;"));
    }

    internal string GetDatabasePathForTests() => _databasePath;

    private void ApplyMigrations(SqliteConnection connection)
    {
        var userVersion = checked((int)ExecuteScalarInt64(connection, "PRAGMA user_version;"));
        if (userVersion > CurrentSchemaVersion)
        {
            throw new DaemonStorageException("unsupported_schema_version");
        }

        var ledgerExists = ExecuteScalarInt64(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';") == 1;

        if (userVersion == 0)
        {
            if (ledgerExists && ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;") != 0)
            {
                throw new DaemonStorageException("database_unhealthy");
            }

            using var transaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, transaction, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    migration_id TEXT PRIMARY KEY NOT NULL,
                    applied_at_utc TEXT NOT NULL
                );
                CREATE TABLE daemon_meta (
                    meta_key TEXT PRIMARY KEY NOT NULL,
                    meta_value TEXT NOT NULL
                );
                CREATE TABLE accounts (
                    account_id TEXT PRIMARY KEY NOT NULL CHECK(length(account_id) BETWEEN 1 AND 256),
                    display_name TEXT NOT NULL CHECK(length(display_name) BETWEEN 1 AND 256),
                    nickname TEXT NULL CHECK(nickname IS NULL OR length(nickname) <= 256),
                    health TEXT NOT NULL CHECK(health IN ('unknown', 'valid', 'needs_relogin')),
                    is_active INTEGER NOT NULL CHECK(is_active IN (0, 1)),
                    sort_order INTEGER NOT NULL CHECK(sort_order >= 0),
                    created_at_utc TEXT NOT NULL,
                    plan_type TEXT NULL CHECK(plan_type IS NULL OR length(plan_type) <= 128),
                    revision TEXT NOT NULL CHECK(length(revision) BETWEEN 1 AND 256),
                    updated_at_utc TEXT NOT NULL
                );
                CREATE INDEX ix_accounts_sort_order ON accounts(sort_order, account_id);
                """);
            _migrationObserver?.Invoke("0001_after_schema");
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO schema_migrations (migration_id, applied_at_utc) VALUES ('0001_initial', $applied_at); PRAGMA user_version=1;";
                command.Parameters.AddWithValue("$applied_at", FormatTimestamp(_timeProvider.GetUtcNow()));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return;
        }

        if (!ledgerExists)
        {
            throw new DaemonStorageException("database_unhealthy");
        }

        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id='0001_initial';";
        if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1 ||
            ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;") != 1)
        {
            throw new DaemonStorageException("database_unhealthy");
        }
    }

    private SqliteConnection OpenConnection()
        => OpenConnection(createIfMissing: false);

    private SqliteConnection OpenConnection(bool createIfMissing)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = BusyTimeoutMilliseconds / 1_000
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        var integrity = ExecuteScalarString(connection, "PRAGMA integrity_check;");
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new DaemonStorageException("database_unhealthy");
        }
    }

    private static AccountRecord? TryGet(SqliteConnection connection, SqliteTransaction transaction, string accountId)
    {
        using var command = CreateGetCommand(connection, transaction, accountId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    private static List<AccountRecord> ReadAccounts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_id, display_name, nickname, health, is_active, sort_order,
                   created_at_utc, plan_type, revision
            FROM accounts
            ORDER BY sort_order, account_id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", AccountService.MaximumAccountCount + 1);

        using var reader = command.ExecuteReader();
        var records = new List<AccountRecord>();
        while (reader.Read())
        {
            if (records.Count == AccountService.MaximumAccountCount)
                throw new DaemonStorageException("resource_limit_exceeded");
            records.Add(ReadAccount(reader));
        }

        return records;
    }

    private static bool IsProjectionEqual(
        AccountRecord current,
        AgentStateDaemon.Core.Protocol.AccountDescriptor target,
        DateTimeOffset targetCreatedAt) =>
        current.Nickname is null &&
        string.Equals(current.DisplayName, target.DisplayName, StringComparison.Ordinal) &&
        string.Equals(current.Health, target.Health, StringComparison.Ordinal) &&
        current.IsActive == target.IsActive &&
        current.SortOrder == target.SortOrder &&
        current.CreatedAtUtc == targetCreatedAt &&
        string.Equals(current.PlanType, target.PlanType, StringComparison.Ordinal);

    private void InsertShadowAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentStateDaemon.Core.Protocol.AccountDescriptor account,
        DateTimeOffset createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounts (
                account_id, display_name, nickname, health, is_active, sort_order,
                created_at_utc, plan_type, revision, updated_at_utc)
            VALUES (
                $account_id, $display_name, NULL, $health, $is_active, $sort_order,
                $created_at_utc, $plan_type, $revision, $updated_at);
            """;
        AddShadowAccountParameters(command, account, createdAt);
        command.ExecuteNonQuery();
    }

    private void UpdateShadowAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentStateDaemon.Core.Protocol.AccountDescriptor account,
        DateTimeOffset createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE accounts
            SET display_name = $display_name,
                nickname = NULL,
                health = $health,
                is_active = $is_active,
                sort_order = $sort_order,
                created_at_utc = $created_at_utc,
                plan_type = $plan_type,
                revision = $revision,
                updated_at_utc = $updated_at
            WHERE account_id = $account_id;
            """;
        AddShadowAccountParameters(command, account, createdAt);
        command.ExecuteNonQuery();
    }

    private void AddShadowAccountParameters(
        SqliteCommand command,
        AgentStateDaemon.Core.Protocol.AccountDescriptor account,
        DateTimeOffset createdAt)
    {
        command.Parameters.AddWithValue("$account_id", account.AccountId);
        command.Parameters.AddWithValue("$display_name", account.DisplayName);
        command.Parameters.AddWithValue("$health", account.Health);
        command.Parameters.AddWithValue("$is_active", account.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$sort_order", account.SortOrder);
        command.Parameters.AddWithValue("$created_at_utc", createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$plan_type", (object?)account.PlanType ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(_timeProvider.GetUtcNow()));
    }

    private static SqliteCommand CreateGetCommand(SqliteConnection connection, SqliteTransaction? transaction, string accountId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_id, display_name, nickname, health, is_active, sort_order,
                   created_at_utc, plan_type, revision
            FROM accounts WHERE account_id = $account_id;
            """;
        command.Parameters.AddWithValue("$account_id", accountId);
        return command;
    }

    private static AccountRecord ReadAccount(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4) != 0,
        reader.GetInt32(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8));

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _unhealthy) != 0)
        {
            throw new DaemonStorageException("database_unhealthy");
        }

        if (!_initialized)
        {
            Initialize();
        }
    }

    private DaemonStorageException MapStorageError(SqliteException exception)
    {
        if (exception.SqliteErrorCode is 11 or 26)
        {
            Interlocked.Exchange(ref _unhealthy, 1);
            return new DaemonStorageException("database_unhealthy");
        }

        return new DaemonStorageException("storage_failure", retryable: exception.SqliteErrorCode is 5 or 6);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ExecuteScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ExecuteScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
