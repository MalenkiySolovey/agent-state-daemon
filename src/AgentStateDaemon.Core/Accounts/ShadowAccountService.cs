using System.Globalization;
using AgentStateDaemon.Core.Protocol;

namespace AgentStateDaemon.Core.Accounts;

internal sealed class ShadowAccountService(IShadowAccountRepository repository)
{
    public ShadowAccountReconcileResult Reconcile(AccountCollectionDocument collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.SchemaVersion != 1 || collection.Accounts is null ||
            collection.Accounts.Count > AccountService.MaximumAccountCount)
        {
            throw new AccountValidationException();
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in collection.Accounts)
        {
            if (account is null || account.SchemaVersion != 1 ||
                !IsValidText(account.AccountId, 1, 256) ||
                !IsValidText(account.DisplayName, 1, 256) ||
                account.Health is not ("valid" or "needs_relogin" or "unknown") ||
                account.SortOrder < 0 ||
                !IsUtcTimestamp(account.CreatedAt) ||
                account.PlanType is { Length: > 128 } ||
                !identifiers.Add(account.AccountId))
            {
                throw new AccountValidationException();
            }
        }

        return repository.ReconcileShadowAccounts(collection.Accounts);
    }

    internal static bool TryParseUtcTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value is null || !value.EndsWith('Z'))
            return false;

        return DateTimeOffset.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool IsValidText(string? value, int minimumLength, int maximumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= maximumLength;

    private static bool IsUtcTimestamp(string? value) => TryParseUtcTimestamp(value, out _);
}
