using System.Text.Json.Serialization;

namespace AgentStateDaemon.Core.Protocol;

public sealed record AccountCollectionDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("accounts")]
    public required IReadOnlyList<AccountDescriptor> Accounts { get; init; }
}
