namespace AgentStateDaemon.Core.Accounts;

internal sealed record AccountRecord(
    string AccountId,
    string DisplayName,
    string? Nickname,
    string Health,
    bool IsActive,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    string? PlanType,
    string Revision);
