namespace AgentStateDaemon.Host;

internal sealed record HostArguments(string DataDirectory, string PipeName, bool ShadowMode)
{
    private const string DefaultPipeName = "agent-state-daemon-v1";
    private const string TestPipePrefix = "agent-state-daemon-test-";
    internal const string ShadowPipeName = "codex-switchboard-agent-state-shadow-v1";

    public static HostArguments Parse(string[] args)
    {
        string? dataDirectory = null;
        var pipeName = DefaultPipeName;
        var shadowMode = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--shadow-mode")
            {
                if (shadowMode)
                    throw new ArgumentException("Invalid host arguments.", nameof(args));
                shadowMode = true;
                continue;
            }

            if (args[index] is "--data-dir" or "--pipe-name")
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Invalid host arguments.", nameof(args));
                }

                var value = args[++index];
                if (args[index - 1] == "--data-dir")
                {
                    if (dataDirectory is not null || string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException("Invalid host arguments.", nameof(args));
                    }

                    dataDirectory = Path.GetFullPath(value);
                }
                else
                {
                    if (pipeName != DefaultPipeName || !IsSafePipeName(value))
                    {
                        throw new ArgumentException("Invalid host arguments.", nameof(args));
                    }

                    pipeName = value;
                }
            }
            else
            {
                throw new ArgumentException("Invalid host arguments.", nameof(args));
            }
        }

        dataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentStateDaemon");

        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory) || IsNetworkPath(dataDirectory) ||
            (shadowMode && (!IsSafeShadowPipeName(pipeName) || args.All(argument => argument != "--data-dir"))) ||
            (!shadowMode && pipeName == ShadowPipeName))
        {
            throw new ArgumentException("Invalid host arguments.", nameof(args));
        }

        return new HostArguments(dataDirectory, pipeName, shadowMode);
    }

    private static bool IsSafePipeName(string value)
    {
        if (string.Equals(value, ShadowPipeName, StringComparison.Ordinal))
            return true;

        return IsTestPipeName(value);
    }

    private static bool IsSafeShadowPipeName(string value) =>
        string.Equals(value, ShadowPipeName, StringComparison.Ordinal) || IsTestPipeName(value);

    private static bool IsTestPipeName(string value) =>
        value.StartsWith(TestPipePrefix, StringComparison.Ordinal) && value.Length == TestPipePrefix.Length + 32 &&
        value[TestPipePrefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Network;
    }
}
