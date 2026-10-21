using System.Globalization;
using System.Text.Json;
using AgentStateDaemon.Core.Accounts;
using AgentStateDaemon.Core.Protocol;
using Microsoft.Data.Sqlite;

namespace AgentStateDaemon.Storage.Sqlite;

internal sealed class SqliteStateStore : IAccountRepository, IShadowAccountRepository, INicknameAuthorityRepository
{
    public const int CurrentSchemaVersion = 2;
    public const int BusyTimeoutMilliseconds = 5_000;
    private const string AuthorityMarkerKey = "nickname_authority_state";
    private const string InitialMigrationId = "0001_initial";
    private const string NicknameAuthorityMigrationId = "0002_nickname_authority";
    private static readonly JsonSerializerOptions MarkerJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _migrationObserver;
    private readonly Action? _beforeAccountCommit;
    private readonly Action<string>? _activationObserver;
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
        Action? beforeAccountCommit,
        Action<string>? activationObserver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        _databasePath = Path.Combine(fullPath, "state.db");
        _timeProvider = timeProvider;
        _migrationObserver = migrationObserver;
        _beforeAccountCommit = beforeAccountCommit;
        _activationObserver = activationObserver;
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
            EnsureAuthorityMarkerAndValidate(connection);
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
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureLegacyLocal(connection, transaction);
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
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureLegacyLocal(connection, transaction);
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

    public int GetDatabaseSchemaVersion()
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            return checked((int)ExecuteScalarInt64(connection, "PRAGMA user_version;"));
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public NicknameAuthorityStatus GetAuthorityStatus()
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            return ToStatus(ReadAuthorityMarker(connection, transaction: null)
                ?? throw new DaemonStorageException("database_unhealthy"));
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public NicknameAuthorityStatus TransitionAuthority(
        string expectedState,
        string nextState,
        long expectedEpoch,
        string? expectedMigrationId,
        string? nextMigrationId)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var marker = ReadAuthorityMarker(connection, transaction)
                ?? throw new DaemonStorageException("database_unhealthy");
            if (marker.State != expectedState || marker.Epoch != expectedEpoch ||
                !string.Equals(marker.MigrationId, expectedMigrationId, StringComparison.Ordinal) ||
                marker.Epoch == long.MaxValue || !IsLegalTransition(expectedState, nextState))
            {
                throw new AuthorityOperationException("authority_state_conflict");
            }

            var databaseVersion = checked((int)ExecuteScalarInt64(connection, transaction, "PRAGMA user_version;"));
            var activating = nextState == NicknameAuthorityStates.DaemonNicknameAuthority;
            if (activating)
            {
                _activationObserver?.Invoke("after_verified_read");
                if (expectedState != NicknameAuthorityStates.Verified || databaseVersion != 1 ||
                    !HasExactMigrationLedger(connection, transaction, 1))
                {
                    throw new DaemonStorageException("database_unhealthy");
                }
            }
            else if (nextState == NicknameAuthorityStates.Preparing && databaseVersion != 1)
            {
                throw new AuthorityOperationException("authority_state_conflict");
            }

            var updatedMarker = new AuthorityMarker(
                nextState,
                marker.Epoch + 1,
                CanonicalProtocolPin.ManifestSha256,
                nextMigrationId);
            WriteAuthorityMarker(connection, transaction, updatedMarker);

            if (activating)
            {
                _activationObserver?.Invoke("after_marker_update");
                using (var ledgerCommand = connection.CreateCommand())
                {
                    ledgerCommand.Transaction = transaction;
                    ledgerCommand.CommandText = "INSERT INTO schema_migrations (migration_id, applied_at_utc) VALUES ($migration_id, $applied_at);";
                    ledgerCommand.Parameters.AddWithValue("$migration_id", NicknameAuthorityMigrationId);
                    ledgerCommand.Parameters.AddWithValue("$applied_at", FormatTimestamp(_timeProvider.GetUtcNow()));
                    ledgerCommand.ExecuteNonQuery();
                }

                _activationObserver?.Invoke("after_ledger_insert");
                using (var versionCommand = connection.CreateCommand())
                {
                    versionCommand.Transaction = transaction;
                    versionCommand.CommandText = "PRAGMA user_version=2;";
                    versionCommand.ExecuteNonQuery();
                }

                _activationObserver?.Invoke("after_user_version_update");
                _activationObserver?.Invoke("before_activation_commit");
            }

            transaction.Commit();
            return ToStatus(updatedMarker);
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public AccountRecord UpdateNicknameWithAuthority(AccountNicknameMutation mutation, long expectedAuthorityEpoch, string? migrationId)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureAuthorityWriteAllowed(connection, transaction, expectedAuthorityEpoch, migrationId, allowPreparing: true);

            var current = TryGet(connection, transaction, mutation.AccountId);
            if (current is null)
                throw new AccountNotFoundException();
            if (!string.Equals(current.Revision, mutation.ExpectedRevision, StringComparison.Ordinal))
                throw new AccountRevisionConflictException();
            if (mutation.Nickname is not null && mutation.SafeFallbackDisplayName != "Account")
                throw new AccountValidationException();
            if (string.IsNullOrWhiteSpace(mutation.SafeFallbackDisplayName) || mutation.SafeFallbackDisplayName.Length > 256 ||
                mutation.SafeFallbackDisplayName.Contains('@'))
                throw new AccountValidationException();

            var newRevision = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE accounts
                    SET nickname = $nickname,
                        display_name = $display_name,
                        revision = $revision,
                        updated_at_utc = $updated_at
                    WHERE account_id = $account_id AND revision = $expected_revision;
                    """;
                command.Parameters.AddWithValue("$nickname", (object?)mutation.Nickname ?? DBNull.Value);
                command.Parameters.AddWithValue("$display_name", mutation.SafeFallbackDisplayName);
                command.Parameters.AddWithValue("$revision", newRevision);
                command.Parameters.AddWithValue("$updated_at", FormatTimestamp(_timeProvider.GetUtcNow()));
                command.Parameters.AddWithValue("$account_id", mutation.AccountId);
                command.Parameters.AddWithValue("$expected_revision", mutation.ExpectedRevision);
                if (command.ExecuteNonQuery() != 1)
                    throw new AccountRevisionConflictException();
            }

            _beforeAccountCommit?.Invoke();
            var updated = TryGet(connection, transaction, mutation.AccountId) ?? throw new DaemonStorageException("storage_failure");
            transaction.Commit();
            return updated;
        }
        catch (SqliteException exception)
        {
            throw MapStorageError(exception);
        }
    }

    public ShadowAccountReconcileResult ReconcileSourceAccounts(
        IReadOnlyList<AccountSourceDescriptor> accounts,
        long expectedAuthorityEpoch,
        string? migrationId)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var marker = EnsureAuthorityWriteAllowed(connection, transaction, expectedAuthorityEpoch, migrationId, allowPreparing: true);
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
                    if (current.Nickname is not null && target.SafeFallbackDisplayName != "Account")
                        throw new AccountValidationException();

                    if (IsSourceProjectionEqual(current, target, createdAt))
                    {
                        unchanged++;
                        continue;
                    }

                    UpdateSourceAccount(connection, transaction, target, createdAt);
                    updated++;
                }
                else
                {
                    InsertSourceAccount(connection, transaction, target, createdAt);
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

            _ = marker;
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

    internal void DeleteAuthorityMarkerForTests()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM daemon_meta WHERE meta_key=$key;";
        command.Parameters.AddWithValue("$key", AuthorityMarkerKey);
        command.ExecuteNonQuery();
    }

    internal void SetAuthorityMarkerForTests(string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO daemon_meta (meta_key, meta_value) VALUES ($key, $value) ON CONFLICT(meta_key) DO UPDATE SET meta_value=excluded.meta_value;";
        command.Parameters.AddWithValue("$key", AuthorityMarkerKey);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal string? GetAuthorityMarkerForTests()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT meta_value FROM daemon_meta WHERE meta_key=$key;";
        command.Parameters.AddWithValue("$key", AuthorityMarkerKey);
        return command.ExecuteScalar() as string;
    }

    internal bool HasMigrationForTests(string migrationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id=$migration_id;";
        command.Parameters.AddWithValue("$migration_id", migrationId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    internal void AddMigrationForTests(string migrationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO schema_migrations (migration_id, applied_at_utc) VALUES ($migration_id, $applied_at);";
        command.Parameters.AddWithValue("$migration_id", migrationId);
        command.Parameters.AddWithValue("$applied_at", FormatTimestamp(_timeProvider.GetUtcNow()));
        command.ExecuteNonQuery();
    }

    internal void RemoveMigrationForTests(string migrationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schema_migrations WHERE migration_id=$migration_id;";
        command.Parameters.AddWithValue("$migration_id", migrationId);
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

        if (!HasExactMigrationLedger(connection, transaction: null, userVersion))
        {
            throw new DaemonStorageException("database_unhealthy");
        }
    }

    private static void EnsureAuthorityMarkerAndValidate(SqliteConnection connection)
    {
        var version = checked((int)ExecuteScalarInt64(connection, "PRAGMA user_version;"));
        if (version is not (1 or 2) || !HasExactMigrationLedger(connection, transaction: null, version))
            throw new DaemonStorageException("database_unhealthy");

        using var transaction = connection.BeginTransaction(deferred: false);
        var marker = ReadAuthorityMarker(connection, transaction);
        if (marker is null)
        {
            if (version != 1)
                throw new DaemonStorageException("database_unhealthy");

            marker = new AuthorityMarker(
                NicknameAuthorityStates.LegacyLocal,
                0,
                CanonicalProtocolPin.ManifestSha256,
                null);
            WriteAuthorityMarker(connection, transaction, marker);
        }

        if (!IsValidMarker(marker) || !string.Equals(marker.ProtocolManifestSha256, CanonicalProtocolPin.ManifestSha256, StringComparison.Ordinal))
            throw new DaemonStorageException("database_unhealthy");

        var validCombination = version switch
        {
            1 => marker.State is NicknameAuthorityStates.LegacyLocal or NicknameAuthorityStates.Preparing or NicknameAuthorityStates.Verified,
            2 => marker.State is NicknameAuthorityStates.DaemonNicknameAuthority or NicknameAuthorityStates.RollbackRequired or NicknameAuthorityStates.LegacyLocal,
            _ => false
        };
        if (!validCombination ||
            (version == 1 && marker.State == NicknameAuthorityStates.LegacyLocal && (marker.Epoch != 0 || marker.MigrationId is not null)) ||
            (version == 2 && (marker.MigrationId is null ||
                (marker.State == NicknameAuthorityStates.LegacyLocal && marker.Epoch < 5))))
        {
            throw new DaemonStorageException("database_unhealthy");
        }

        transaction.Commit();
    }

    private static bool HasExactMigrationLedger(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT migration_id FROM schema_migrations ORDER BY migration_id COLLATE BINARY;";
        using var reader = command.ExecuteReader();
        var migrations = new List<string>();
        while (reader.Read())
            migrations.Add(reader.GetString(0));

        return version switch
        {
            1 => migrations.Count == 1 && migrations[0] == InitialMigrationId,
            2 => migrations.Count == 2 && migrations[0] == InitialMigrationId && migrations[1] == NicknameAuthorityMigrationId,
            _ => false
        };
    }

    private static AuthorityMarker? ReadAuthorityMarker(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT meta_value FROM daemon_meta WHERE meta_key=$key;";
        command.Parameters.AddWithValue("$key", AuthorityMarkerKey);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            return null;

        try
        {
            var markerText = (string)value;
            if (markerText.Length > 2_048)
                throw new DaemonStorageException("database_unhealthy");

            using var document = JsonDocument.Parse(markerText, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DaemonStorageException("database_unhealthy");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("state" or "epoch" or "protocolManifestSha256" or "migrationId") || !names.Add(property.Name))
                    throw new DaemonStorageException("database_unhealthy");
            }

            if (names.Count != 4 ||
                !root.TryGetProperty("state", out var stateElement) || stateElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("epoch", out var epochElement) || !epochElement.TryGetInt64(out var epoch) ||
                !root.TryGetProperty("protocolManifestSha256", out var digestElement) || digestElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("migrationId", out var migrationElement) ||
                migrationElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new DaemonStorageException("database_unhealthy");
            }

            var marker = new AuthorityMarker(
                stateElement.GetString()!,
                epoch,
                digestElement.GetString()!,
                migrationElement.ValueKind == JsonValueKind.Null ? null : migrationElement.GetString());
            if (!IsValidMarker(marker) ||
                !string.Equals(marker.ProtocolManifestSha256, CanonicalProtocolPin.ManifestSha256, StringComparison.Ordinal))
            {
                throw new DaemonStorageException("database_unhealthy");
            }

            return marker;
        }
        catch (JsonException)
        {
            throw new DaemonStorageException("database_unhealthy");
        }
    }

    private static void WriteAuthorityMarker(SqliteConnection connection, SqliteTransaction transaction, AuthorityMarker marker)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daemon_meta (meta_key, meta_value) VALUES ($key, $value)
            ON CONFLICT(meta_key) DO UPDATE SET meta_value=excluded.meta_value;
            """;
        command.Parameters.AddWithValue("$key", AuthorityMarkerKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(marker, MarkerJsonOptions));
        command.ExecuteNonQuery();
    }

    private static bool IsValidMarker(AuthorityMarker? marker)
    {
        if (marker is null || marker.Epoch < 0 || string.IsNullOrWhiteSpace(marker.ProtocolManifestSha256))
            return false;

        return marker.State switch
        {
            NicknameAuthorityStates.LegacyLocal => marker.MigrationId is null || IsValidMigrationId(marker.MigrationId),
            NicknameAuthorityStates.Preparing => marker.Epoch >= 1 && IsValidMigrationId(marker.MigrationId),
            NicknameAuthorityStates.Verified => marker.Epoch >= 2 && IsValidMigrationId(marker.MigrationId),
            NicknameAuthorityStates.DaemonNicknameAuthority => marker.Epoch >= 3 && IsValidMigrationId(marker.MigrationId),
            NicknameAuthorityStates.RollbackRequired => marker.Epoch >= 4 && IsValidMigrationId(marker.MigrationId),
            _ => false
        };
    }

    private static bool IsValidMigrationId(string? migrationId) =>
        !string.IsNullOrWhiteSpace(migrationId) && migrationId.Length <= 256;

    private static NicknameAuthorityStatus ToStatus(AuthorityMarker marker) => new()
    {
        State = marker.State,
        Epoch = marker.Epoch,
        ProtocolManifestSha256 = marker.ProtocolManifestSha256,
        MigrationId = marker.MigrationId
    };

    private static bool IsLegalTransition(string currentState, string nextState) => (currentState, nextState) switch
    {
        (NicknameAuthorityStates.LegacyLocal, NicknameAuthorityStates.Preparing) => true,
        (NicknameAuthorityStates.Preparing, NicknameAuthorityStates.Verified) => true,
        (NicknameAuthorityStates.Verified, NicknameAuthorityStates.DaemonNicknameAuthority) => true,
        (NicknameAuthorityStates.DaemonNicknameAuthority, NicknameAuthorityStates.RollbackRequired) => true,
        (NicknameAuthorityStates.RollbackRequired, NicknameAuthorityStates.LegacyLocal) => true,
        _ => false
    };

    private static AuthorityMarker EnsureLegacyLocal(SqliteConnection connection, SqliteTransaction transaction)
    {
        var marker = ReadAuthorityMarker(connection, transaction)
            ?? throw new DaemonStorageException("database_unhealthy");
        if (marker.State != NicknameAuthorityStates.LegacyLocal)
            throw new AuthorityOperationException("legacy_operation_fenced");
        return marker;
    }

    private static AuthorityMarker EnsureAuthorityWriteAllowed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedAuthorityEpoch,
        string? migrationId,
        bool allowPreparing)
    {
        var marker = ReadAuthorityMarker(connection, transaction)
            ?? throw new DaemonStorageException("database_unhealthy");
        if (marker.Epoch != expectedAuthorityEpoch ||
            (marker.State != NicknameAuthorityStates.DaemonNicknameAuthority &&
             !(allowPreparing && marker.State == NicknameAuthorityStates.Preparing)) ||
            (marker.State == NicknameAuthorityStates.Preparing && !string.Equals(marker.MigrationId, migrationId, StringComparison.Ordinal)))
        {
            throw new AuthorityOperationException("authority_state_conflict");
        }

        return marker;
    }

    private sealed record AuthorityMarker(
        string State,
        long Epoch,
        string ProtocolManifestSha256,
        string? MigrationId);

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

    private static bool IsSourceProjectionEqual(
        AccountRecord current,
        AccountSourceDescriptor target,
        DateTimeOffset targetCreatedAt) =>
        string.Equals(current.DisplayName, target.SafeFallbackDisplayName, StringComparison.Ordinal) &&
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

    private void InsertSourceAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountSourceDescriptor account,
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
        AddSourceAccountParameters(command, account, createdAt);
        command.ExecuteNonQuery();
    }

    private void UpdateSourceAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountSourceDescriptor account,
        DateTimeOffset createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE accounts
            SET display_name = $display_name,
                health = $health,
                is_active = $is_active,
                sort_order = $sort_order,
                created_at_utc = $created_at_utc,
                plan_type = $plan_type,
                revision = $revision,
                updated_at_utc = $updated_at
            WHERE account_id = $account_id;
            """;
        AddSourceAccountParameters(command, account, createdAt);
        command.ExecuteNonQuery();
    }

    private void AddSourceAccountParameters(
        SqliteCommand command,
        AccountSourceDescriptor account,
        DateTimeOffset createdAt)
    {
        command.Parameters.AddWithValue("$account_id", account.AccountId);
        command.Parameters.AddWithValue("$display_name", account.SafeFallbackDisplayName);
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

    private static long ExecuteScalarInt64(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
