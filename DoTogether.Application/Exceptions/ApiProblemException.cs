namespace DoTogether.Application.Exceptions;

public sealed class ApiProblemException : Exception
{
    public int StatusCode { get; }
    public string Title { get; }
    public string ErrorCode { get; }
    public IReadOnlyDictionary<string, object?> Extensions { get; }

    private ApiProblemException(
        int statusCode,
        string title,
        string errorCode,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null) : base(detail)
    {
        StatusCode = statusCode;
        Title = title;
        ErrorCode = errorCode;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    public static ApiProblemException BadRequest(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(400, "Bad Request", errorCode, detail, extensions);

    public static ApiProblemException Unauthorized(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(401, "Unauthorized", errorCode, detail, extensions);

    public static ApiProblemException Forbidden(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(403, "Forbidden", errorCode, detail, extensions);

    public static ApiProblemException Conflict(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(409, "Conflict", errorCode, detail, extensions);

    public static ApiProblemException NotFound(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(404, "Not Found", errorCode, detail, extensions);

    public static ApiProblemException Unprocessable(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(422, "Unprocessable Entity", errorCode, detail, extensions);

    public static ApiProblemException BadGateway(string errorCode, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(502, "Bad Gateway", errorCode, detail, extensions);
}