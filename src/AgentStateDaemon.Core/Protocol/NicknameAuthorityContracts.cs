using System.Text.Json.Serialization;

#pragma warning disable CA1711 // These public DTO names must match the canonical protocol contracts.

namespace AgentStateDaemon.Core.Protocol;

public sealed record AccountSourceDescriptor
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("accountId")]
    public required string AccountId { get; init; }

    [JsonPropertyName("safeFallbackDisplayName")]
    public required string SafeFallbackDisplayName { get; init; }

    [JsonPropertyName("health")]
    public required string Health { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }

    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }
}

public sealed record AccountSourceCollection
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("accounts")]
    public required IReadOnlyList<AccountSourceDescriptor> Accounts { get; init; }
}

public sealed record AccountNicknameState
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("accountId")]
    public required string AccountId { get; init; }

    [JsonPropertyName("nickname")]
    public required string? Nickname { get; init; }

    [JsonPropertyName("revision")]
    public required string Revision { get; init; }
}

public sealed record AccountNicknameCollection
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("accounts")]
    public required IReadOnlyList<AccountNicknameState> Accounts { get; init; }
}

public sealed record AccountNicknameMutation
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("accountId")]
    public required string AccountId { get; init; }

    [JsonPropertyName("nickname")]
    public required string? Nickname { get; init; }

    [JsonPropertyName("safeFallbackDisplayName")]
    public required string SafeFallbackDisplayName { get; init; }

    [JsonPropertyName("expectedRevision")]
    public required string ExpectedRevision { get; init; }
}

public sealed record NicknameAuthorityStatus
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    [JsonPropertyName("protocolManifestSha256")]
    public required string ProtocolManifestSha256 { get; init; }

    [JsonPropertyName("migrationId")]
    public required string? MigrationId { get; init; }
}

public sealed record NicknameMutationResult
{
    [JsonPropertyName("nicknameState")]
    public required AccountNicknameState NicknameState { get; init; }

    [JsonPropertyName("account")]
    public required AccountDescriptor Account { get; init; }
}

public sealed record SourceReconcileResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("inserted")]
    public int Inserted { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; init; }

    [JsonPropertyName("unchanged")]
    public int Unchanged { get; init; }
}
