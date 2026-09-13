namespace Integration.Api.Errors;

/// <summary>
/// An error the API knows how to turn into a ProblemDetails response on its own,
/// with a stable, safe errorCode. Anything else becomes a generic 500.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string errorCode, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Detail = detail;
    }

    public ApiException(int statusCode, string errorCode, string detail, IReadOnlyDictionary<string, string[]> fieldErrors)
        : this(statusCode, errorCode, detail)
    {
        FieldErrors = fieldErrors;
    }

    public int StatusCode { get; }

    public string ErrorCode { get; }

    public string Detail { get; }

    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }
}
