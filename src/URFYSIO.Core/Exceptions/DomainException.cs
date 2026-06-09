using System.Diagnostics;

namespace URFYSIO.Core.Exceptions;

/// <summary>
/// Expected, user-caused business error (e.g. overlapping availability slot, slot already booked).
/// Carries an HTTP status code that <c>ExceptionHandlingMiddleware</c> surfaces as a ProblemDetails
/// response. These are not bugs — they're the app signalling a legitimate 4xx back to the client —
/// so we ask the debugger not to treat them as user-unhandled:
///   • <c>[DebuggerNonUserCode]</c> marks the type as non-user code.
///   • <c>[DebuggerStepThrough]</c> additionally stops the debugger from stepping into / breaking
///     inside the constructor and the static factory helpers.
/// NOTE: Visual Studio's "Break when this exception type is user-unhandled" can still override
/// these attributes. If VS keeps breaking, uncheck DomainException under
/// Debug → Windows → Exception Settings (Common Language Runtime Exceptions).
/// </summary>
[DebuggerNonUserCode]
[DebuggerStepThrough]
public class DomainException : Exception
{
    public int StatusCode { get; }

    public DomainException(string message, int statusCode = 409) : base(message)
    {
        StatusCode = statusCode;
    }

    public static DomainException NotFound(string message) => new(message, 404);
    public static DomainException Conflict(string message) => new(message, 409);
    public static DomainException Forbidden(string message) => new(message, 403);
    public static DomainException Validation(string message) => new(message, 400);
}
