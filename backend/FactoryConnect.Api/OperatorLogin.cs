using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace FactoryConnect.Api;

// Only an OIDC middleware-validated callback may cross the human-session verifier seam.
// No client assertion, claim, role or fixture can construct this context.
internal sealed class OidcHumanIdentityVerifier(IHttpContextAccessor accessor) : IHumanIdentityVerifier
{
    internal static readonly object ProofKey = new();
    internal sealed record Proof(string Nonce, VerifiedHumanIdentity Identity);
    public Task<VerifiedHumanIdentity> VerifyAsync(string assertion, CancellationToken ct)
    {
        var items = accessor.HttpContext?.Items;
        if (items is null || !items.Remove(ProofKey, out var value) || value is not Proof proof
            || assertion != proof.Nonce) throw new SecurityDeniedException();
        return Task.FromResult(proof.Identity);
    }
}

internal static class OperatorLogin
{
    const string Cookie = "operator-cookie";
    const string Oidc = "operator-oidc";
    const string TokenClaim = "fc2:session";
    const string UntilClaim = "fc2:expires";
    internal static void AddOperatorLogin(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        services.AddAuthorization();
        var authentication = services.AddAuthentication(Cookie).AddCookie(Cookie, options =>
        {
            options.Cookie.Name = "__Host-fc2-operator";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        var authority = config["Identity:Authority"];
        var client = config["Identity:ClientId"];
        if (string.IsNullOrWhiteSpace(authority) && string.IsNullOrWhiteSpace(client)) return;
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) || issuer.Scheme != "https"
            || string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(config["Identity:ClientSecret"]))
            throw new InvalidOperationException("Explicit HTTPS OIDC authority and confidential client configuration required.");
        services.AddSingleton<IHumanIdentityVerifier, OidcHumanIdentityVerifier>();
        authentication.AddOpenIdConnect(Oidc, options =>
        {
            options.Authority = authority;
            options.ClientId = client;
            options.ClientSecret = config["Identity:ClientSecret"];
            options.SignInScheme = Cookie;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.RequireHttpsMetadata = true;
            options.SaveTokens = false;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear(); options.Scope.Add("openid");
            options.TokenValidationParameters.ValidIssuer = authority;
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidateLifetime = true;
            options.TokenValidationParameters.RequireSignedTokens = true;
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            options.Events.OnTokenValidated = async context =>
            {
                var principal = context.Principal ?? throw new SecurityDeniedException();
                var subject = principal.FindFirst("sub")?.Value ?? throw new SecurityDeniedException();
                var issued = principal.FindFirst("iat")?.Value;
                if (!long.TryParse(issued, CultureInfo.InvariantCulture, out var seconds)) throw new SecurityDeniedException();
                var proofId = principal.FindFirst("jti")?.Value;
                if (string.IsNullOrWhiteSpace(proofId)) proofId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(context.TokenEndpointResponse?.IdToken ?? context.ProtocolMessage.IdToken ?? throw new SecurityDeniedException())));
                if (string.IsNullOrWhiteSpace(proofId)) throw new SecurityDeniedException();
                var proof = new OidcHumanIdentityVerifier.Proof(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    new(authority!, subject, proofId, principal.FindFirst("acr")?.Value ?? "oidc",
                        DateTimeOffset.FromUnixTimeSeconds(seconds), new DateTimeOffset(context.SecurityToken.ValidTo, TimeSpan.Zero)));
                context.HttpContext.Items[OidcHumanIdentityVerifier.ProofKey] = proof;
                await Task.CompletedTask;
            };
            // TicketReceived runs after nonce/state and protocol validation have completed.
            options.Events.OnTicketReceived = async context =>
            {
                if (context.HttpContext.Items[OidcHumanIdentityVerifier.ProofKey] is not OidcHumanIdentityVerifier.Proof proof)
                    throw new SecurityDeniedException();
                var session = await new SqlHumanSessionAuthority(config.GetConnectionString("Evidence")!,
                    Guid.Parse(config["Evidence:InstallationId"]!),
                    context.HttpContext.RequestServices.GetRequiredService<IHumanIdentityVerifier>())
                    .IssueAsync(proof.Nonce, context.HttpContext.RequestAborted);
                try
                {
                    // The authentication ticket is encrypted by ASP.NET Data Protection.
                    // Only the opaque SQL credential is retained, never provider tokens.
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(TokenClaim, Convert.ToBase64String(session.Token)),
                        new Claim(UntilClaim, session.ExpiresAt.ToString("O"))], Cookie));
                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.ExpiresUtc = session.ExpiresAt;
                    context.Properties.IsPersistent = false;
                }
                finally { CryptographicOperations.ZeroMemory(session.Token); }
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse(); context.Response.StatusCode = 401;
                return context.Response.WriteAsync("Operator identity verification failed.");
            };
        });
    }

    internal static void MapOperatorLogin(this WebApplication app)
    {
        app.MapGet("/api/security/v2/login", (HttpContext http, IConfiguration config) =>
            !http.Request.IsHttps ? Results.StatusCode(403)
            : string.IsNullOrWhiteSpace(config["Identity:Authority"]) ? Results.StatusCode(503)
            : Results.Challenge(new AuthenticationProperties { RedirectUri = "/operator-evidence" }, [Oidc]));
        app.MapGet("/api/security/v2/session", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.StatusCode(403);
            var ticket = await http.AuthenticateAsync(Cookie);
            var bearer = ticket.Principal?.FindFirst(TokenClaim)?.Value;
            var until = ticket.Principal?.FindFirst(UntilClaim)?.Value;
            if (!ticket.Succeeded || bearer is null || until is null) return Results.Unauthorized();
            // Recheck the durable SQL session before releasing a browser credential.
            var secret = Convert.FromBase64String(bearer); var hash = SHA256.HashData(secret);
            CryptographicOperations.ZeroMemory(secret);
            try
            {
                await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, Guid.Parse(config["Evidence:InstallationId"]!))
                    .ReadOperatorEvidenceMachinesAsync(hash, Guid.Parse(config["Evidence:SiteId"]!), ct);
                return Results.Ok(new { bearer, expiresAt = until });
            }
            catch (SecurityDeniedException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return Results.Problem(statusCode: 503, title: "Session authority unavailable"); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        });
        app.MapPost("/api/security/v2/logout", async (HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps || http.Request.Headers.Origin != $"https://{http.Request.Host}") return Results.StatusCode(403);
            var ticket = await http.AuthenticateAsync(Cookie);
            var bearer = ticket.Principal?.FindFirst(TokenClaim)?.Value;
            var revocationFailed = false;
            try
            {
                if (bearer is not null)
                {
                    byte[]? hash = null;
                    try
                    {
                        var secret = Convert.FromBase64String(bearer);
                        try { hash = SHA256.HashData(secret); }
                        finally { CryptographicOperations.ZeroMemory(secret); }
                        await new SqlSecurityAuthority(config.GetConnectionString("Evidence")!, Guid.Parse(config["Evidence:InstallationId"]!))
                            .EndOwnSessionAsync(hash, ct);
                    }
                    catch (SecurityDeniedException) { }
                    catch (Exception ex)
                    {
                        http.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("FactoryConnect.Api.OperatorLogin")
                            .LogError(ex, "Operator session revocation failed; local cookie will still be cleared. TraceId={TraceId}", http.TraceIdentifier);
                        revocationFailed = true;
                    }
                    finally { if (hash is not null) CryptographicOperations.ZeroMemory(hash); }
                }
            }
            finally
            {
                // Local cookie removal must not depend on SQL revocation, token decoding or request cancellation.
                await http.SignOutAsync(Cookie);
            }
            return revocationFailed
                ? Results.Problem(statusCode: 503, title: "Session revocation unavailable")
                : Results.NoContent();
        });
    }
}
