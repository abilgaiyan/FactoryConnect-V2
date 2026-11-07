using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FactoryConnect.Api;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

// Test-only identity provider. No identity fixture is registered in the shipped API.
internal static class OperatorBrowserAcceptance
{
    sealed record Login(string Redirect,string State,string Nonce,string Challenge,string Client);
    internal static async Task<int> Run(string sql,Guid installation,Guid site,Guid machine,
        byte[] adminToken,Func<Task<long>> epoch)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct=deadline.Token;
        using var caKey=RSA.Create(3072);
        var caRequest=new CertificateRequest("CN=FC2 disposable browser root",caKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
        using var root=caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        using var serverKey=RSA.Create(3072);
        var request=new CertificateRequest("CN=localhost",serverKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
        using var publicCert=request.Create(root,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(30),RandomNumberGenerator.GetBytes(16));
        using var certificate=publicCert.CopyWithPrivateKey(serverKey);
        using var trust=new X509Store(StoreName.Root,StoreLocation.CurrentUser);
        trust.Open(OpenFlags.ReadWrite);trust.Add(root);
        try
        {
            using var signingKey=RSA.Create(3072);
            var securityKey=new RsaSecurityKey(signingKey){KeyId="browser-fixture-key"};
            var clientSecret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pending=new ConcurrentDictionary<string,Login>();
            var codes=new ConcurrentDictionary<string,Login>();
            var mode="bad-audience";
            var callback="";
            const string issuer="https://certificate.fixture/tenant";
            var providerAddress="";
            var providerBuilder=WebApplication.CreateBuilder();
            providerBuilder.Logging.ClearProviders();
            providerBuilder.WebHost.ConfigureKestrel(o=>o.Listen(IPAddress.Loopback,0,l=>l.UseHttps(certificate)));
            await using var provider=providerBuilder.Build();
            provider.MapGet("/.well-known/openid-configuration",()=>Results.Json(new{
                issuer,authorization_endpoint=providerAddress+"/authorize",token_endpoint=providerAddress+"/token",jwks_uri=providerAddress+"/keys",
                response_types_supported=new[]{"code"},subject_types_supported=new[]{"public"},
                id_token_signing_alg_values_supported=new[]{"RS256"},code_challenge_methods_supported=new[]{"S256"},
                token_endpoint_auth_methods_supported=new[]{"client_secret_post"}}));
            provider.MapGet("/keys",()=>
            {
                var key=JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
                return Results.Json(new{keys=new[]{new{kty=key.Kty,kid=key.Kid,n=key.N,e=key.E,use="sig",alg="RS256"}}});
            });
            provider.MapGet("/authorize",(HttpContext h)=>
            {
                var q=h.Request.Query;
                if(q["redirect_uri"]!=callback||q["client_id"]!="fc2-browser"||q["code_challenge_method"]!="S256"||q["response_type"]!="code")
                    return Results.BadRequest();
                var id=Guid.NewGuid().ToString("N");
                pending[id]=new(callback,q["state"].ToString(),q["nonce"].ToString(),q["code_challenge"].ToString(),"fc2-browser");
                h.Response.Headers.CacheControl="no-store";
                return Results.Content($"<form method='post' action='/authenticate'><input type='hidden' name='id' value='{id}'/><label>Password<input type='password' name='password'/></label><button>Provider sign in</button></form>","text/html");
            });
            provider.MapPost("/authenticate",async(HttpContext h)=>
            {
                var form=await h.Request.ReadFormAsync(ct);
                if(form["password"]!=password||!pending.TryRemove(form["id"].ToString(),out var login))return Results.Unauthorized();
                var code=Guid.NewGuid().ToString("N");codes[code]=login;
                return Results.Redirect(login.Redirect+"?code="+code+"&state="+Uri.EscapeDataString(login.State));
            });
            provider.MapPost("/token",async(HttpContext h)=>
            {
                var form=await h.Request.ReadFormAsync(ct);
                if(!codes.TryRemove(form["code"].ToString(),out var login)||form["redirect_uri"]!=login.Redirect
                    ||form["client_id"]!=login.Client||form["client_secret"]!=clientSecret
                    ||form["grant_type"]!="authorization_code"
                    ||Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())))!=login.Challenge)
                    return Results.Unauthorized();
                var now=DateTimeOffset.UtcNow;
                var token=new JwtSecurityToken(issuer,mode=="bad-audience"?"wrong-client":login.Client,
                    [new("sub","browser-operator"),new("nonce",mode=="bad-nonce"?"incorrect-nonce":login.Nonce),new("jti",Guid.NewGuid().ToString("N")),
                     new("iat",(mode=="expired"?now.AddMinutes(-10):now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),System.Security.Claims.ClaimValueTypes.Integer64)],
                    (mode=="expired"?now.AddMinutes(-10):now.AddSeconds(-1)).UtcDateTime,(mode=="expired"?now.AddMinutes(-5):now.AddMinutes(3)).UtcDateTime,new SigningCredentials(securityKey,SecurityAlgorithms.RsaSha256));
                return Results.Json(new{access_token="unused-fixture-access-token",token_type="Bearer",expires_in=180,id_token=new JwtSecurityTokenHandler().WriteToken(token)});
            });
            await provider.StartAsync(ct);providerAddress=provider.Urls.Single();
            var authority=new SqlSecurityAuthority(sql,installation);
            var principal=Guid.NewGuid();
            await authority.EnrollHumanAsync(adminToken,new(Guid.NewGuid(),principal,issuer,"browser-operator",await epoch(),"enroll isolated browser operator"));
            await using var connection=new SqlConnection(sql);await connection.OpenAsync(ct);
            await using var parentCommand=new SqlCommand("SELECT g.GrantId FROM security.PermissionGrant g JOIN security.SessionAuthority s ON s.PrincipalId=g.GranteeId WHERE s.TokenHash=@token AND g.Permission='commissioning.read'",connection);
            parentCommand.Parameters.AddWithValue("@token",adminToken);
            var parent=(Guid)(await parentCommand.ExecuteScalarAsync(ct))!;
            var grant=Guid.NewGuid();
            await authority.GrantPermissionAsync(adminToken,new(Guid.NewGuid(),grant,principal,parent,"commissioning.read",AuthorizationScope.Machine(site,machine),true,false,DateTimeOffset.UtcNow.AddSeconds(-1),DateTimeOffset.UtcNow.AddMinutes(10),await epoch(),"isolated browser grant"));
            await using var api=EvidenceApiHost.Build([],builder=>
            {
                builder.Logging.ClearProviders();
                // Resolve the pinned test issuer's discovery document over the fixture's
                // actual TLS listener; token issuer validation remains exact and enabled.
                builder.Services.PostConfigure<OpenIdConnectOptions>("operator-oidc", options =>
                    options.MetadataAddress=providerAddress+"/.well-known/openid-configuration");
                builder.WebHost.UseWebRoot(Path.GetFullPath("dist"));
                builder.WebHost.ConfigureKestrel(o=>o.Listen(IPAddress.Loopback,0,l=>l.UseHttps(certificate)));
                builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{
                    ["ConnectionStrings:Evidence"]=sql,["Evidence:InstallationId"]=installation.ToString(),
                    ["Evidence:SiteId"]=site.ToString(),["Evidence:StreamKey"]="mtconnect:uuid:fixture-device",
                    ["Identity:Authority"]=issuer,["Identity:ClientId"]="fc2-browser",["Identity:ClientSecret"]=clientSecret});
            });
            await api.StartAsync(ct);var address=api.Urls.Single();callback=address+"/signin-oidc";
            var info=new ProcessStartInfo("node","scripts/ci/operator-browser.mjs"){
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            info.Environment["FC2_BROWSER_URL"]=address;
            info.Environment["FC2_BROWSER_MACHINE"]=machine.ToString();
            info.Environment["FC2_BROWSER_PASSWORD"]=password;
            info.Environment["FC2_BROWSER_SPKI"]=Convert.ToBase64String(SHA256.HashData(serverKey.ExportSubjectPublicKeyInfo()));
            using var child=Process.Start(info)!;
            var errors=child.StandardError.ReadToEndAsync(ct);
            var stages=0;
            try
            {
                while(await child.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    using var message=JsonDocument.Parse(line);var stage=message.RootElement.GetProperty("stage").GetString();
                    if(stage=="invalid-denied")
                    {
                        var next=message.RootElement.GetProperty("next").GetString();
                        mode=next is "bad-nonce" or "expired" or "valid" ? next : throw new InvalidOperationException();
                    }
                    else if(stage=="revoke-session")
                    {
                        var hash=Convert.FromHexString(message.RootElement.GetProperty("hash").GetString()!);
                        await authority.RevokeSessionAsync(adminToken,Guid.NewGuid(),hash,await epoch(),"browser acceptance session revocation",ct);
                    }
                    else if(stage=="revoke-grant")await authority.RevokeGrantAsync(adminToken,Guid.NewGuid(),grant,await epoch(),"browser acceptance grant revocation",ct);
                    else if(stage!="complete")throw new InvalidOperationException("Unexpected browser stage");
                    stages++;Console.WriteLine("PASS: real HTTPS browser "+stage);
                    await child.StandardInput.WriteLineAsync("continue");await child.StandardInput.FlushAsync(ct);
                }
                await child.WaitForExitAsync(ct);
                if(child.ExitCode!=0||stages!=6)throw new InvalidOperationException("Browser acceptance failed: "+await errors);
                return stages;
            }
            finally{if(!child.HasExited)child.Kill(true);}
        }
        finally{trust.Remove(root);}
    }
}
