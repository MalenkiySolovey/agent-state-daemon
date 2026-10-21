using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Core.Accounts;

internal interface INicknameAuthorityRepository
{
    int GetDatabaseSchemaVersion();

    NicknameAuthorityStatus GetAuthorityStatus();

    NicknameAuthorityStatus TransitionAuthority(
        string expectedState,
        string nextState,
        long expectedEpoch,
        string? expectedMigrationId,
        string? nextMigrationId);

    AccountRecord UpdateNicknameWithAuthority(AccountNicknameMutation mutation, long expectedAuthorityEpoch, string? migrationId);

    ShadowAccountReconcileResult ReconcileSourceAccounts(
        IReadOnlyList<AccountSourceDescriptor> accounts,
        long expectedAuthorityEpoch,
        string? migrationId);
}

internal static class NicknameAuthorityStates
{
    public const string LegacyLocal = "LegacyLocal";
    public const string Preparing = "Preparing";
    public const string Verified = "Verified";
    public const string DaemonNicknameAuthority = "DaemonNicknameAuthority";
    public const string RollbackRequired = "RollbackRequired";
}
