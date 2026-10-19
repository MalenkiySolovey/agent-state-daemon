namespace AgentStateDaemon.Host;

internal sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream _lockFile;

    private DataDirectoryLease(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    public static DataDirectoryLease Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var lockPath = Path.Combine(dataDirectory, ".daemon.lock");
        try
        {
            return new DataDirectoryLease(new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None));
        }
        catch (IOException)
        {
            throw new IOException("Data directory lock is unavailable.");
        }
    }

    public void Dispose() => _lockFile.Dispose();
}
