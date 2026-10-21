namespace AgentStateDaemon.Core.Accounts;

internal sealed class AccountNotFoundException : Exception
{
}

internal sealed class AccountRevisionConflictException : Exception
{
}

internal sealed class AccountValidationException : Exception
{
}

internal sealed class AuthorityOperationException(string errorCode) : Exception
{
    public string ErrorCode { get; } = errorCode;
}

internal sealed class DaemonStorageException : Exception
{
    public DaemonStorageException(string errorCode, bool retryable = false)
    {
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    public string ErrorCode { get; }

    public bool Retryable { get; }
}
