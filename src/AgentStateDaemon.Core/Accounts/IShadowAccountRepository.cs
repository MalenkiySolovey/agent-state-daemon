using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Core.Accounts;

internal interface IShadowAccountRepository
{
    ShadowAccountReconcileResult ReconcileShadowAccounts(IReadOnlyList<AccountDescriptor> accounts);
}

internal sealed record ShadowAccountReconcileResult(
    int Inserted,
    int Updated,
    int Deleted,
    int Unchanged);
