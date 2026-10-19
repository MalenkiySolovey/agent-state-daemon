using System.Globalization;
using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Core.Accounts;

internal sealed class AccountService(IAccountRepository repository)
{
    internal const int MaximumAccountCount = 1_024;

    public AccountListResult List()
    {
        var records = repository.List();
        if (records.Count > MaximumAccountCount)
        {
            throw new DaemonStorageException("resource_limit_exceeded");
        }

        var descriptors = records.Select(ToDescriptor).ToArray();
        var revisions = records.ToDictionary(record => record.AccountId, record => record.Revision, StringComparer.Ordinal);

        return new AccountListResult
        {
            Collection = new AccountCollectionDocument { Accounts = descriptors },
            Revisions = revisions
        };
    }

    public RevisionedAccount Get(string accountId)
    {
        ValidateAccountId(accountId);
        return ToResult(repository.GetById(accountId));
    }

    public RevisionedAccount UpdateMetadata(AccountMetadataUpdate update)
    {
        if (update.SchemaVersion != 1)
        {
            throw new AccountValidationException();
        }

        ValidateAccountId(update.AccountId);
        if (update.Nickname is null || update.Nickname.Length > 256 || update.ExpectedRevision is null || update.ExpectedRevision.Length is 0 or > 256)
        {
            throw new AccountValidationException();
        }

        return ToResult(repository.UpdateNickname(update.AccountId, update.Nickname, update.ExpectedRevision));
    }

    private static RevisionedAccount ToResult(AccountRecord record) => new()
    {
        Account = ToDescriptor(record),
        Revision = record.Revision
    };

    private static AccountDescriptor ToDescriptor(AccountRecord record) => new()
    {
        AccountId = record.AccountId,
        DisplayName = string.IsNullOrEmpty(record.Nickname) ? record.DisplayName : record.Nickname,
        Health = record.Health,
        IsActive = record.IsActive,
        SortOrder = record.SortOrder,
        CreatedAt = record.CreatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        PlanType = record.PlanType
    };

    private static void ValidateAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 256)
        {
            throw new AccountValidationException();
        }
    }
}
