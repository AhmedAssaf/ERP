namespace Platform.Shared.Results;

public enum ErrorKind
{
    Validation,
    NotFound,
    Refused,
    Conflict,
    InvariantViolated,
}

/// <summary>An expected failure. Code is stable and machine-readable; Message is a full sentence for people.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Refused(string code, string message) => new(ErrorKind.Refused, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Invariant(string code, string message) => new(ErrorKind.InvariantViolated, code, message);
}
