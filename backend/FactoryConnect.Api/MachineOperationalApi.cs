using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

/// <summary>Explicit, authorized machine operational transitions. No endpoint starts a runtime or activates a package.</summary>
public static class MachineOperationalApi
{
    private sealed record ProposeRequest(Guid OperationId, Guid ProposalId, Guid? ExpectedStateId,
        long ExpectedGeneration, long ExpectedSecurityEpoch, DateTimeOffset ExpiresAt, string Reason);
    private sealed record ApproveRequest(Guid OperationId, Guid ProposalId, Guid StateId,
        long ExpectedSecurityEpoch, string Reason);
    private sealed record SuspendRequest(Guid OperationId, Guid StateId, Guid? ExpectedStateId,
        long ExpectedGeneration, long ExpectedSecurityEpoch, string Reason);

    private sealed record RuntimeAssignmentInput(Guid OperationId, Guid AssignmentId, Guid RuntimeIdentityId,
        long ExpectedCredentialGeneration, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo,
        long ExpectedAssignmentGeneration, Guid? SupersedesAssignmentId, long ExpectedSecurityEpoch, string Reason);

    private sealed record ActivationInput(Guid OperationId, Guid ActivationId, Guid PackageRevisionId,
        string ContentHash, Guid ApprovalReviewId, Guid RuntimeIdentityId, Guid AssignmentId,
        long AssignmentGeneration, long CredentialGeneration, DateTimeOffset EffectiveFrom,
        DateTimeOffset? EffectiveTo, long ExpectedActivationGeneration, Guid? SupersedesActivationId,
        long ExpectedSecurityEpoch, string Reason);

    public static void MapMachineOperational(this WebApplication app)
    {
        app.MapPost("/api/admin/v2/sites/{siteId:guid}/machines/{machineId:guid}/activation-requests",
            async (Guid siteId, Guid machineId, ActivationInput request, HttpContext http, IConfiguration config, CancellationToken ct) =>
            {
                if (request.ContentHash is null || request.ContentHash.Length != 64 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(request.ContentHash, "^[0-9a-fA-F]{64}$"))
                    return Results.BadRequest(new { error = "Expected 32-byte SHA-256 content hash in hexadecimal." });
                return await Execute(http, config, (authority, hash) =>
                    authority.RequestActivationAsync(hash, new ActivationRequestCommand(
                        request.OperationId, request.ActivationId, siteId, machineId,
                        request.PackageRevisionId, Convert.FromHexString(request.ContentHash),
                        request.ApprovalReviewId, request.RuntimeIdentityId, request.AssignmentId,
                        request.AssignmentGeneration, request.CredentialGeneration, request.EffectiveFrom,
                        request.EffectiveTo, request.ExpectedActivationGeneration,
                        request.SupersedesActivationId, request.ExpectedSecurityEpoch, request.Reason), ct), ct);
            }).RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/sites/{siteId:guid}/machines/{machineId:guid}/runtime-assignments",
            async (Guid siteId, Guid machineId, RuntimeAssignmentInput request, HttpContext http, IConfiguration config, CancellationToken ct) =>
                await Execute(http, config, (authority, hash) =>
                    authority.AssignRuntimeAsync(hash, new RuntimeAssignmentRequest(
                        request.OperationId, request.AssignmentId, siteId, machineId, request.RuntimeIdentityId,
                        request.ExpectedCredentialGeneration, request.EffectiveFrom, request.EffectiveTo,
                        request.ExpectedAssignmentGeneration, request.SupersedesAssignmentId,
                        request.ExpectedSecurityEpoch, request.Reason), ct), ct))
            .RequireRateLimiting("security-boundary");

        app.MapGet("/api/admin/v2/sites/{siteId:guid}/machines/{machineId:guid}/operational-state",
            async (Guid siteId, Guid machineId, HttpContext http, IConfiguration config, CancellationToken ct) =>
                await Execute(http, config, (authority, hash) =>
                    authority.ReadMachineOperationalStateAsync(hash, siteId, machineId, ct), ct))
            .RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/sites/{siteId:guid}/machines/{machineId:guid}/enable-proposals",
            async (Guid siteId, Guid machineId, ProposeRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
                await Execute(http, config, (authority, hash) =>
                    authority.ProposeMachineEnableAsync(hash, new MachineEnableProposalRequest(
                        request.OperationId, request.ProposalId, siteId, machineId, request.ExpectedStateId,
                        request.ExpectedGeneration, request.ExpectedSecurityEpoch, request.ExpiresAt, request.Reason), ct), ct))
            .RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/sites/{siteId:guid}/machines/{machineId:guid}/enable-approvals",
            async (Guid siteId, Guid machineId, ApproveRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
                await Execute(http, config, (authority, hash) =>
                    authority.ApproveMachineEnableAsync(hash, request.OperationId, request.ProposalId,
                        request.StateId, siteId, machineId, request.ExpectedSecurityEpoch, request.Reason, ct), ct))
            .RequireRateLimiting("security-boundary");

        app.MapPost("/api/admin/v2/sites/{siteId:guid}/machines/{machineId:guid}/suspensions",
            async (Guid siteId, Guid machineId, SuspendRequest request, HttpContext http, IConfiguration config, CancellationToken ct) =>
                await Execute(http, config, (authority, hash) =>
                    authority.SuspendMachineAsync(hash, new MachineSuspensionRequest(
                        request.OperationId, request.StateId, siteId, machineId, request.ExpectedStateId,
                        request.ExpectedGeneration, request.ExpectedSecurityEpoch, request.Reason), ct), ct))
            .RequireRateLimiting("security-boundary");
    }

    private static async Task<IResult> Execute<T>(HttpContext http, IConfiguration config,
        Func<SqlSecurityAuthority, byte[], Task<T>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps) return Results.Problem(statusCode: 403, title: "HTTPS required");
        if (!Guid.TryParse(config["Evidence:InstallationId"], out var installationId) ||
            string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")))
            return Results.Problem(statusCode: 503, title: "Machine operational authority unavailable");
        if (!http.Request.Headers.TryGetValue("Authorization", out var authorization) ||
            authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal) ||
            authorization[0]!.Length > 192) return Results.Unauthorized();
        byte[] bearer;
        try { bearer = Convert.FromBase64String(authorization[0]![7..]); }
        catch (FormatException) { return Results.Unauthorized(); }
        if (bearer.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bearer);
            return Results.Unauthorized();
        }
        var hash = SHA256.HashData(bearer);
        CryptographicOperations.ZeroMemory(bearer);
        try
        {
            var result = await action(new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, installationId), hash);
            if (result is byte[] json)
                return Results.Content(System.Text.Encoding.UTF8.GetString(json), "application/json");
            return Results.Ok(result);
        }
        catch (SecurityDeniedException) { return Results.Unauthorized(); }
        catch (SecurityConflictException) { return Results.Conflict(new { error = "Operational authority changed; refresh before retrying." }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid operational request." }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return AuthorityDiagnostics.Unavailable(http, ex, "machine-operational", "Machine operational authority unavailable");
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
