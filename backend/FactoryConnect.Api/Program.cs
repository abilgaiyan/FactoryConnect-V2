var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health", NotReady);
app.MapGet("/health/ready", NotReady);
app.MapGet("/dashboard/config", () => Results.Ok(new
{
    reportingBasePath = "/",
    requestTimeoutMilliseconds = 30000,
    sources = Array.Empty<object>()
}));

// No evidence authority exists yet. Never return simulated production results.
app.MapMethods("/api/{**path}", ["GET", "POST"], NotReady);
app.Run();

static IResult NotReady() => Results.Problem(
    statusCode: StatusCodes.Status503ServiceUnavailable,
    title: "V2 evidence authority is not configured",
    detail: "This foundation host has no persistence, acquisition or reporting provider.");
