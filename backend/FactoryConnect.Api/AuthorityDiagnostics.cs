using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

/// <summary>Safe, bounded failure categories for API authority errors.
/// Never record SQL connection strings, request credentials or exception messages.</summary>
internal static class AuthorityDiagnostics
{
    internal static IResult Unavailable(HttpContext http, Exception exception, string operation, string title)
    {
        var category = Classify(exception);
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FactoryConnect.Api.Authority")
            .LogError("Authority operation {Operation} failed; Category={Category}; TraceId={TraceId}",
                operation, category, http.TraceIdentifier);
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: title, extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
    }

    internal static string Classify(Exception exception) => exception switch
    {
        SqlException sql when sql.Number is -2 => "sql-timeout",
        SqlException => "sql-error",
        TimeoutException => "timeout",
        InvalidOperationException => "authority-state",
        _ => "unexpected"
    };
}
