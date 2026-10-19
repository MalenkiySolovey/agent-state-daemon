using AgentStateDaemon.Core.Accounts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentStateDaemon.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTestGroup
{
    public const string Name = "serial-daemon-tests";
}

public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-state-daemon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class SyntheticAccount
{
    public static AccountRecord Create(string? accountId = null) => new(
        accountId ?? $"synthetic-account-{Guid.NewGuid():N}",
        "Synthetic Account",
        null,
        "valid",
        true,
        0,
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        "test",
        Guid.NewGuid().ToString("N"));
}
