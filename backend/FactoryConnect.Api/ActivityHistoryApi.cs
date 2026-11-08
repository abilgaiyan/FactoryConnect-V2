using System.Security.Cryptography;
using FactoryConnect.Sql.Security;

namespace FactoryConnect.Api;

internal static class ActivityHistoryApi
{
    internal static void MapActivityHistory(this WebApplication app)
    {
        app.MapGet("/api/commissioning/v2/machines/{machineId:guid}/activity",async(
            Guid machineId,HttpContext http,IConfiguration config,ILoggerFactory loggerFactory,CancellationToken ct)=>
            await Read(http,config,loggerFactory.CreateLogger("FactoryConnect.ActivityHistory.Integrity"),async(authority,token,site,stream)=> {
                var series=await authority.ReadActivitySeriesAsync(token,site,machineId,stream,ct);
                return Results.Ok(new { machineId,siteId=site,streamKey=stream,
                    evidenceKind="historical-derived-activity",currentConnectivity=(string?)null,
                    verification="immutable-publication-receipts",continuity="same-batch-only-not-physical-completeness",
                    status=series.Count==0?"no-published-history":"published-history",series });
            }));
        app.MapGet("/api/commissioning/v2/machines/{machineId:guid}/activity/{mappingId:guid}",async(
            Guid machineId,Guid mappingId,HttpContext http,IConfiguration config,ILoggerFactory loggerFactory,CancellationToken ct)=>
            await Read(http,config,loggerFactory.CreateLogger("FactoryConnect.ActivityHistory.Integrity"),async(authority,token,site,stream)=> {
                var take=20;
                if(http.Request.Query.TryGetValue("take",out var value) &&
                    (value.Count!=1 || !int.TryParse(value[0],out take) || take is <1 or >100))
                    return Results.BadRequest(new { error="take must be between 1 and 100" });
                string? cursor=null;
                if(http.Request.Query.TryGetValue("cursor",out var requested))
                {
                    if(requested.Count!=1 || requested[0]?.Length is not (>0 and <=2048))return Results.BadRequest();
                    cursor=requested[0];
                }
                var page=await authority.ReadActivityPeriodsAsync(token,site,machineId,stream,mappingId,take,cursor,ct);
                return Results.Ok(new { machineId,siteId=site,streamKey=stream,
                    evidenceKind="historical-derived-activity",currentConnectivity=(string?)null,
                    verification="immutable-publication-receipts",continuity="same-batch-only-not-physical-completeness",
                    page.Series,page.Rows,page.NextCursor });
            }));
    }
    private static async Task<IResult> Read(HttpContext http,IConfiguration config,ILogger logger,
        Func<SqlSecurityAuthority,byte[],Guid,string,Task<IResult>> read)
    {
        http.Response.Headers.CacheControl="no-store";
        if(!http.Request.IsHttps)return Results.Problem(statusCode:403,title:"HTTPS required");
        if(!Guid.TryParse(config["Evidence:InstallationId"],out var installation) ||
            !Guid.TryParse(config["Evidence:SiteId"],out var site) ||
            string.IsNullOrWhiteSpace(config.GetConnectionString("Evidence")) ||
            string.IsNullOrWhiteSpace(config["Evidence:StreamKey"]))
            return Results.Problem(statusCode:503,title:"Activity history authority is unavailable");
        if(!http.Request.Headers.TryGetValue("Authorization",out var authorization) || authorization.Count!=1 ||
            authorization[0]?.StartsWith("Bearer ",StringComparison.Ordinal)!=true || authorization[0]!.Length>192)
            return Results.Unauthorized();
        byte[] secret;
        try { secret=Convert.FromBase64String(authorization[0]![7..]); }
        catch(FormatException){return Results.Unauthorized();}
        if(secret.Length!=32){CryptographicOperations.ZeroMemory(secret);return Results.Unauthorized();}
        var hash=SHA256.HashData(secret);CryptographicOperations.ZeroMemory(secret);
        try { return await read(new SqlSecurityAuthority(config.GetConnectionString("Evidence")!,installation),hash,site,config["Evidence:StreamKey"]!); }
        catch(SecurityDeniedException){return Results.Unauthorized();}
        catch(KeyNotFoundException){return Results.NotFound(new { error="No published activity series" });}
        catch(ArgumentException){return Results.BadRequest(new { error="Invalid activity page or cursor" });}
        catch(InvalidDataException error)
        {
            logger.LogError(error,"Activity history integrity verification rejected read. Path={Path} TraceId={TraceId}",
                http.Request.Path,http.TraceIdentifier);
            return Results.Problem(statusCode:503,title:"Activity history integrity verification failed");
        }
        catch(OperationCanceledException)when(http.RequestAborted.IsCancellationRequested){throw;}
        catch(Exception){return Results.Problem(statusCode:503,title:"Activity history authority is unavailable");}
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
