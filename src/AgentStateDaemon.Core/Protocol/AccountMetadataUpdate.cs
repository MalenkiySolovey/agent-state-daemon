using System.Text.Json.Serialization;

namespace AgentStateDaemon.Core.Protocol;

public sealed record AccountMetadataUpdate
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("accountId")]
    public required string AccountId { get; init; }

    [JsonPropertyName("nickname")]
    public required string Nickname { get; init; }

    [JsonPropertyName("expectedRevision")]
    public string? ExpectedRevision { get; init; }
}
