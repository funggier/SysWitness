namespace SysWitness.Sdk;

/// <summary>Sanitized read failure. Response bodies, headers and credentials are never attached.</summary>
public sealed class SysWitnessApiException : Exception
{
    public string ErrorCode { get; }
    public int? HttpStatus { get; }

    internal SysWitnessApiException(string errorCode, int? httpStatus = null)
        : base(httpStatus is { } status
            ? $"SysWitness read failed: {errorCode} (HTTP {status})."
            : $"SysWitness read failed: {errorCode}.")
    {
        ErrorCode = errorCode;
        HttpStatus = httpStatus;
    }
}
