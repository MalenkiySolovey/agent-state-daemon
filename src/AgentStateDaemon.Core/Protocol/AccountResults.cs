using System.Text.Json.Serialization;

namespace AgentStateDaemon.Core.Protocol;

public sealed record AccountListResult
{
    [JsonPropertyName("collection")]
    public required AccountCollectionDocument Collection { get; init; }

    [JsonPropertyName("revisions")]
    public required IReadOnlyDictionary<string, string> Revisions { get; init; }
}

public sealed record RevisionedAccount
{
    [JsonPropertyName("account")]
    public required AccountDescriptor Account { get; init; }

    [JsonPropertyName("revision")]
    public required string Revision { get; init; }
}
