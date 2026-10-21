using System.Globalization;
using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Core.Accounts;

internal sealed class NicknameAuthorityService
{
    private readonly IAccountRepository _accounts;
    private readonly INicknameAuthorityRepository _authorityRepository;
    private readonly bool _experimentalEnabled;

    public NicknameAuthorityService(
        IAccountRepository accounts,
        INicknameAuthorityRepository authorityRepository,
        bool experimentalEnabled)
    {
        _accounts = accounts;
        _authorityRepository = authorityRepository;
        _experimentalEnabled = experimentalEnabled;
    }

    public bool ExperimentalEnabled => _experimentalEnabled;

    public NicknameAuthorityStatus GetStatus() => _authorityRepository.GetAuthorityStatus();

    public int GetDatabaseSchemaVersion() => _authorityRepository.GetDatabaseSchemaVersion();

    public AccountNicknameState GetNickname(string accountId)
    {
        ValidateAccountId(accountId);
        return ToNicknameState(_accounts.GetById(accountId));
    }

    public AccountNicknameCollection ListNicknames()
    {
        var records = _accounts.List();
        if (records.Count > AccountService.MaximumAccountCount)
            throw new DaemonStorageException("resource_limit_exceeded");

        return new AccountNicknameCollection { Accounts = records.Select(ToNicknameState).ToArray() };
    }

    public NicknameMutationResult UpdateNickname(AccountNicknameMutation mutation, long expectedAuthorityEpoch, string? migrationId)
    {
        ValidateMutation(mutation);
        if (expectedAuthorityEpoch < 0)
            throw new AccountValidationException();

        var state = GetStatus().State;
        if (state != NicknameAuthorityStates.DaemonNicknameAuthority && !_experimentalEnabled)
            throw new AuthorityOperationException("experimental_feature_disabled");

        var updated = _authorityRepository.UpdateNicknameWithAuthority(mutation, expectedAuthorityEpoch, migrationId);
        return new NicknameMutationResult
        {
            NicknameState = ToNicknameState(updated),
            Account = AccountService.ToDescriptor(updated)
        };
    }

    public SourceReconcileResult ReconcileSource(AccountSourceCollection collection, long expectedAuthorityEpoch, string? migrationId)
    {
        ValidateSourceCollection(collection);
        if (expectedAuthorityEpoch < 0)
            throw new AccountValidationException();

        var state = GetStatus().State;
        if (state != NicknameAuthorityStates.DaemonNicknameAuthority && !_experimentalEnabled)
            throw new AuthorityOperationException("experimental_feature_disabled");

        var result = _authorityRepository.ReconcileSourceAccounts(collection.Accounts, expectedAuthorityEpoch, migrationId);
        return new SourceReconcileResult
        {
            Inserted = result.Inserted,
            Updated = result.Updated,
            Deleted = result.Deleted,
            Unchanged = result.Unchanged
        };
    }

    public NicknameAuthorityStatus BeginPreparation(long expectedEpoch)
    {
        EnsureExperimentalEnabled();
        ValidateExpectedEpoch(expectedEpoch);
        return Transition(
            NicknameAuthorityStates.LegacyLocal,
            NicknameAuthorityStates.Preparing,
            expectedEpoch,
            expectedMigrationId: null,
            nextMigrationId: Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    }

    public NicknameAuthorityStatus MarkVerified(long expectedEpoch, string migrationId)
    {
        EnsureExperimentalEnabled();
        ValidateMigrationId(migrationId);
        ValidateExpectedEpoch(expectedEpoch);
        return Transition(NicknameAuthorityStates.Preparing, NicknameAuthorityStates.Verified, expectedEpoch, migrationId, migrationId);
    }

    public NicknameAuthorityStatus Activate(long expectedEpoch, string migrationId)
    {
        EnsureExperimentalEnabled();
        ValidateMigrationId(migrationId);
        ValidateExpectedEpoch(expectedEpoch);
        return Transition(NicknameAuthorityStates.Verified, NicknameAuthorityStates.DaemonNicknameAuthority, expectedEpoch, migrationId, migrationId);
    }

    public NicknameAuthorityStatus BeginRollback(long expectedEpoch, string migrationId)
    {
        EnsureExperimentalEnabled();
        ValidateMigrationId(migrationId);
        ValidateExpectedEpoch(expectedEpoch);
        return Transition(NicknameAuthorityStates.DaemonNicknameAuthority, NicknameAuthorityStates.RollbackRequired, expectedEpoch, migrationId, migrationId);
    }

    public NicknameAuthorityStatus CompleteRollback(long expectedEpoch, string migrationId)
    {
        EnsureExperimentalEnabled();
        ValidateMigrationId(migrationId);
        ValidateExpectedEpoch(expectedEpoch);
        return Transition(NicknameAuthorityStates.RollbackRequired, NicknameAuthorityStates.LegacyLocal, expectedEpoch, migrationId, migrationId);
    }

    private NicknameAuthorityStatus Transition(
        string expectedState,
        string nextState,
        long expectedEpoch,
        string? expectedMigrationId,
        string? nextMigrationId)
    {
        var current = _authorityRepository.GetAuthorityStatus();
        if (current.State != expectedState || current.Epoch != expectedEpoch ||
            !string.Equals(current.MigrationId, expectedMigrationId, StringComparison.Ordinal))
        {
            throw new AuthorityOperationException("authority_state_conflict");
        }

        if (current.Epoch == long.MaxValue)
            throw new AuthorityOperationException("authority_state_conflict");

        return _authorityRepository.TransitionAuthority(
            expectedState,
            nextState,
            expectedEpoch,
            expectedMigrationId,
            nextMigrationId);
    }

    private void EnsureExperimentalEnabled()
    {
        if (!_experimentalEnabled)
            throw new AuthorityOperationException("experimental_feature_disabled");
    }

    private static void ValidateSourceCollection(AccountSourceCollection collection)
    {
        if (collection is null || collection.SchemaVersion != 1 || collection.Accounts is null ||
            collection.Accounts.Count > AccountService.MaximumAccountCount)
            throw new AccountValidationException();

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in collection.Accounts)
        {
            if (account is null || account.SchemaVersion != 1 ||
                !IsLengthValid(account.AccountId, 1, 256) ||
                !IsSafeFallback(account.SafeFallbackDisplayName) ||
                account.Health is not ("unknown" or "valid" or "needs_relogin") ||
                account.SortOrder < 0 || !ShadowAccountService.TryParseUtcTimestamp(account.CreatedAt, out _) ||
                account.PlanType is { Length: > 128 } || !identifiers.Add(account.AccountId))
            {
                throw new AccountValidationException();
            }
        }
    }

    private static void ValidateMutation(AccountNicknameMutation mutation)
    {
        if (mutation is null || mutation.SchemaVersion != 1 || !IsLengthValid(mutation.AccountId, 1, 256) ||
            !IsLengthValid(mutation.ExpectedRevision, 1, 256) || !IsSafeFallback(mutation.SafeFallbackDisplayName))
        {
            throw new AccountValidationException();
        }

        if (mutation.Nickname is { } nickname && !IsText(nickname, 1, 256))
            throw new AccountValidationException();

        if (mutation.Nickname is not null && mutation.SafeFallbackDisplayName != "Account")
            throw new AccountValidationException();
    }

    private static bool IsText(string? value, int minimumLength, int maximumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= maximumLength && !string.IsNullOrWhiteSpace(value);

    private static bool IsLengthValid(string? value, int minimumLength, int maximumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= maximumLength;

    private static bool IsSafeFallback(string? value) =>
        IsText(value, 1, 256) && !value!.Contains('@');

    private static AccountNicknameState ToNicknameState(AccountRecord record) => new()
    {
        AccountId = record.AccountId,
        Nickname = record.Nickname,
        Revision = record.Revision
    };

    private static void ValidateAccountId(string accountId)
    {
        if (!IsLengthValid(accountId, 1, 256))
            throw new AccountValidationException();
    }

    private static void ValidateExpectedEpoch(long epoch)
    {
        if (epoch < 0)
            throw new AccountValidationException();
    }

    private static void ValidateMigrationId(string migrationId)
    {
        if (string.IsNullOrWhiteSpace(migrationId) || migrationId.Length > 256)
            throw new AccountValidationException();
    }
}
