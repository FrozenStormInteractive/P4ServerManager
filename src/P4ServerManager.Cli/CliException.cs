namespace P4ServerManager.Cli;

/// <summary>
/// An error the CLI reports with its message alone, without a stack trace.
/// </summary>
internal sealed class CliException(string message, Exception? innerException = null)
    : Exception(message, innerException);
