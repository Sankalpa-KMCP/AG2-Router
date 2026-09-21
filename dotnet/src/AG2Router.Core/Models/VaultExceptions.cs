namespace AG2Router.Core.Models;

/// <summary>
/// Exception thrown on Windows Credential Manager errors.
/// </summary>
public class WinCredException : Exception
{
    public WinCredException(string message) : base(message) { }
    public WinCredException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown on Windows DPAPI encryption or decryption failures.
/// </summary>
public class DpapiException : Exception
{
    public DpapiException(string message) : base(message) { }
    public DpapiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Generic vault operation error (I/O, invalid parameters, atomic write failure).
/// </summary>
public class VaultException : Exception
{
    public VaultException(string message) : base(message) { }
    public VaultException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when vault file is corrupt, malformed, or has invalid version/framing.
/// Fails closed: corrupted vault files must NEVER be overwritten.
/// </summary>
public class VaultCorruptionException : Exception
{
    public VaultCorruptionException(string message) : base(message) { }
    public VaultCorruptionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown on account enrollment errors.
/// </summary>
public class AccountEnrollmentException : Exception
{
    public AccountEnrollmentException(string message) : base(message) { }
    public AccountEnrollmentException(string message, Exception innerException) : base(message, innerException) { }
}
