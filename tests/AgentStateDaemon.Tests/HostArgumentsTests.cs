using AgentStateDaemon.Host;
using Xunit;

namespace AgentStateDaemon.Tests;

public sealed class HostArgumentsTests
{
    [Fact]
    public void ShadowModeRequiresDedicatedPipeAndExplicitDataDirectory()
    {
        using var directory = new TemporaryDirectory();

        var options = HostArguments.Parse(
        [
            "--data-dir", directory.Path,
            "--pipe-name", HostArguments.ShadowPipeName,
            "--shadow-mode"
        ]);

        Assert.True(options.ShadowMode);
        Assert.Equal(HostArguments.ShadowPipeName, options.PipeName);
        Assert.Equal(Path.GetFullPath(directory.Path), options.DataDirectory);
        Assert.False(options.ExperimentalNicknameAuthority);
        Assert.Throws<ArgumentException>(() => HostArguments.Parse(["--shadow-mode"]));
        Assert.Throws<ArgumentException>(() => HostArguments.Parse(
        [
            "--data-dir", directory.Path,
            "--pipe-name", "agent-state-daemon-v1",
            "--shadow-mode"
        ]));
    }

    [Fact]
    public void NormalModeCannotClaimTheShadowPipe()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(() => HostArguments.Parse(
        [
            "--data-dir", directory.Path,
            "--pipe-name", HostArguments.ShadowPipeName
        ]));
    }

    [Fact]
    public void ExperimentalNicknameAuthorityRequiresExplicitIsolatedDataAndPipe()
    {
        using var directory = new TemporaryDirectory();
        var pipeName = $"agent-state-daemon-test-{Guid.NewGuid():N}";
        var options = HostArguments.Parse(
        [
            "--data-dir", directory.Path,
            "--pipe-name", pipeName,
            "--experimental-nickname-authority"
        ]);

        Assert.True(options.ExperimentalNicknameAuthority);
        Assert.Equal(pipeName, options.PipeName);
        Assert.Throws<ArgumentException>(() => HostArguments.Parse(["--experimental-nickname-authority"]));
        Assert.Throws<ArgumentException>(() => HostArguments.Parse(
        [
            "--data-dir", directory.Path,
            "--experimental-nickname-authority"
        ]));
        Assert.Throws<ArgumentException>(() => HostArguments.Parse(
        [
            "--data-dir", directory.Path,
            "--pipe-name", "agent-state-daemon-v1",
            "--experimental-nickname-authority"
        ]));
    }
}
