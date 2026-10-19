namespace AgentStateDaemon.Core.Accounts;

internal interface IAccountRepository
{
    IReadOnlyList<AccountRecord> List();

    AccountRecord GetById(string accountId);

    AccountRecord UpdateNickname(string accountId, string nickname, string expectedRevision);
}
