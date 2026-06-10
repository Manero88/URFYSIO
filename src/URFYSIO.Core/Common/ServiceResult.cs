namespace URFYSIO.Core.Common;

/// <summary>
/// Lightweight success/failure result for EXPECTED business-validation outcomes
/// (overlapping slot, booked slot, past date, ...). These are normal control flow,
/// not exceptional situations — returning a result instead of throwing
/// <c>DomainException</c> keeps the Visual Studio debugger from breaking on every
/// routine validation failure and makes the happy/sad paths explicit at the call
/// site. <c>DomainException</c> remains in use for genuinely exceptional or
/// cross-cutting cases that should abort the request pipeline.
/// </summary>
public sealed record ServiceResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;

    public static ServiceResult<T> Ok(T value) => new(value, null);
    public static ServiceResult<T> Fail(string error) => new(default, error);
}
