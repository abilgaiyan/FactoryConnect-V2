using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Api;
using FactoryConnect.Sql.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class OperatorEvidenceHttpAcceptance
{
    private sealed class FixtureIdentityVerifier(VerifiedHumanIdentity identity) : IHumanIdentityVerifier
    {
        public Task<VerifiedHumanIdentity> VerifyAsync(string assertion, CancellationToken ct)
            => assertion == "one-use-http-fixture-assertion"
                ? Task.FromResult(identity)
                : throw new SecurityDeniedException();
    }

    /// <summary>
    /// ASP.NET TestServer exercises the real API handlers with HTTPS request schemes and
    /// disposable SQL. TestServer does not prove a network TLS handshake or browser wiring.
    /// </summary>
    internal static async Task<int> Run(
        string sql, Guid installation, Guid site, Guid machine, Guid otherMachine,
        byte[] administratorSessionHash, Func<Task<long>> currentEpoch)
    {
        var passed = 0;
        var now = DateTimeOffset.UtcNow;
        var identity = new VerifiedHumanIdentity(
            "https://certificate.fixture/tenant", "certificate-manager", Guid.NewGuid().ToString("N"),
            "http-fixture-mfa", now.AddSeconds(-1), now.AddMinutes(3));

        var transactions = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.V2.Security",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (transactions) transactions.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);

        using var factory = new WebApplicationFactory<HostAssemblyMarker>().WithWebHostBuilder(host =>
        {
            // The repo uses .slnx and the API project lives under backend/,
            // not the conventional root-level FactoryConnect.Api directory.
            host.UseContentRoot(Directory.GetCurrentDirectory());
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Evidence"] = sql,
                    ["Evidence:InstallationId"] = installation.ToString("D"),
                    ["Evidence:SiteId"] = site.ToString("D"),
                    ["Evidence:StreamKey"] = "mtconnect:uuid:fixture-device",
                    ["Evidence:MachineDiscoveryCursorKey"] = Convert.ToBase64String(
                        SHA256.HashData(Encoding.UTF8.GetBytes("disposable-operator-http-acceptance-cursor-key"))),
                }));
            host.ConfigureTestServices(services =>
                services.AddSingleton<IHumanIdentityVerifier>(new FixtureIdentityVerifier(identity)));
        });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost/"),
            AllowAutoRedirect = false,
        });

        // Plain HTTP fails before the server consumes any identity proof.
        using (var plaintext = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/security/v2/human-sessions"))
        {
            plaintext.Content = new StringContent("one-use-http-fixture-assertion", Encoding.UTF8, "text/plain");
            using var refused = await http.SendAsync(plaintext);
            Check(refused.StatusCode == HttpStatusCode.Forbidden, "plain HTTP login is denied");
        }

        var loginWatch = Stopwatch.StartNew();
        using var signIn = await http.PostAsync("/api/security/v2/human-sessions",
            new StringContent("one-use-http-fixture-assertion", Encoding.UTF8, "text/plain"));
        loginWatch.Stop();
        Check(signIn.StatusCode == HttpStatusCode.OK, "verified one-use proof reaches real HTTPS-scheme session route");
        using var loginJson = JsonDocument.Parse(await signIn.Content.ReadAsStringAsync());
        var bearer = loginJson.RootElement.GetProperty("bearer").GetString()!;
        var secret = Convert.FromBase64String(bearer);
        Check(secret.Length == 32, "session route returns a random 32-byte bearer");
        var hash = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        try
        {
            using var replay = await http.PostAsync("/api/security/v2/human-sessions",
                new StringContent("one-use-http-fixture-assertion", Encoding.UTF8, "text/plain"));
            Check(replay.StatusCode == HttpStatusCode.Unauthorized, "HTTP proof replay cannot issue a second session");

            var rosterWatch = Stopwatch.StartNew();
            using var roster = await AuthorizedGet("/api/commissioning/v2/evidence-machines");
            rosterWatch.Stop();
            Check(roster.StatusCode == HttpStatusCode.OK, "authenticated machine selection reaches SQL authority");
            using (var rosterJson = JsonDocument.Parse(await roster.Content.ReadAsStringAsync()))
            {
                var machines = rosterJson.RootElement.GetProperty("machines");
                Check(machines.GetArrayLength() == 1 &&
                    machines[0].GetProperty("machineId").GetGuid() == machine,
                    "HTTP roster discloses only explicitly authorized machine");
                Check(rosterJson.RootElement.GetProperty("isComplete").GetBoolean() &&
                    rosterJson.RootElement.GetProperty("continuationSupported").GetBoolean() &&
                    rosterJson.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null &&
                    rosterJson.RootElement.GetProperty("maximumReturned").GetInt32() == 100 &&
                    roster.Headers.TryGetValues("X-Result-Complete", out var completeHeader) &&
                    completeHeader.Single() == "true",
                    "one-machine authorized roster proves complete and advertises bounded continuation capability");
            }
            foreach (var parameter in new[] { "cursor", "continuationToken" })
            {
                using var rejected = await AuthorizedGet(
                    "/api/commissioning/v2/evidence-machines?" + parameter + "=forged");
                Check(rejected.StatusCode == HttpStatusCode.BadRequest,
                    "authenticated machine discovery rejects unsigned " + parameter);
            }
            // A cursor for a different session, site or installation must never
            // be accepted, even when the machine seek key is otherwise valid.
            var cursorKey = SHA256.HashData(
                Encoding.UTF8.GetBytes("disposable-operator-http-acceptance-cursor-key"));
            var signedCursor = MachineDiscoveryCursor.Issue(cursorKey, installation, site, hash, machine);
            Check(MachineDiscoveryCursor.TryRead(signedCursor, cursorKey, installation, site, hash, out var signedAfter)
                && signedAfter == machine,
                "signed cursor roundtrip preserves the authenticated machine seek position");
            var anotherHash = SHA256.HashData(Encoding.UTF8.GetBytes("other human session"));
            Check(!MachineDiscoveryCursor.TryRead(signedCursor, cursorKey, installation, site, anotherHash, out _)
                && !MachineDiscoveryCursor.TryRead(signedCursor, cursorKey, Guid.NewGuid(), site, hash, out _)
                && !MachineDiscoveryCursor.TryRead(signedCursor, cursorKey, installation, Guid.NewGuid(), hash, out _),
                "signed cursor rejects cross-session, cross-installation and cross-site reuse");
            // A correctly signed but expired cursor must fail without SQL access.
            var expiredCursor = MachineDiscoveryCursor.Issue(cursorKey, installation, site, hash, machine);
            var segments = expiredCursor.Split('.');
            var expiredPayload = JsonDocument.Parse(Convert.FromBase64String(
                segments[0].Replace('-', '+').Replace('_', '/').PadRight((segments[0].Length + 3) / 4 * 4, '=')));
            var expiredBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Installation = expiredPayload.RootElement.GetProperty("Installation").GetGuid(),
                Site = expiredPayload.RootElement.GetProperty("Site").GetGuid(),
                Session = expiredPayload.RootElement.GetProperty("Session").GetString(),
                After = expiredPayload.RootElement.GetProperty("After").GetGuid(),
                Expires = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(),
            });
            static string UrlBase64(byte[] bytes) => Convert.ToBase64String(bytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            expiredCursor = UrlBase64(expiredBody) + "." +
                UrlBase64(HMACSHA256.HashData(cursorKey, expiredBody));
            Check(!MachineDiscoveryCursor.TryRead(expiredCursor, cursorKey, installation, site, hash, out _),
                "correctly signed expired machine cursor is rejected");
            using (var expiredHttp = await AuthorizedGet(
                "/api/commissioning/v2/evidence-machines?cursor=" + Uri.EscapeDataString(expiredCursor)))
                Check(expiredHttp.StatusCode == HttpStatusCode.BadRequest,
                    "authenticated HTTP discovery rejects expired signed cursor");

            var signatureStart = signedCursor.IndexOf('.') + 1;
            var forgedCursor = signedCursor[..signatureStart] +
                (signedCursor[signatureStart] == 'A' ? 'B' : 'A') + signedCursor[(signatureStart + 1)..];
            Check(!MachineDiscoveryCursor.TryRead(forgedCursor, cursorKey, installation, site, hash, out _)
                && !MachineDiscoveryCursor.TryRead("forged", cursorKey, installation, site, hash, out _),
                "signed cursor rejects modified and malformed tokens");
            using (var signedHttp = await AuthorizedGet(
                "/api/commissioning/v2/evidence-machines?cursor=" + Uri.EscapeDataString(signedCursor)))
            {
                Check(signedHttp.StatusCode == HttpStatusCode.OK,
                    "signed cursor reaches authorized SQL keyset page");
                using var signedJson = JsonDocument.Parse(await signedHttp.Content.ReadAsStringAsync());
                Check(signedJson.RootElement.GetProperty("machines").GetArrayLength() == 0 &&
                    signedJson.RootElement.GetProperty("isComplete").GetBoolean(),
                    "signed continuation after last authorized machine returns complete empty page");
            }
            using (var wrongSession = await AuthorizedGet(
                "/api/commissioning/v2/evidence-machines?cursor=" +
                Uri.EscapeDataString(MachineDiscoveryCursor.Issue(cursorKey, installation, site, anotherHash, machine))))
                Check(wrongSession.StatusCode == HttpStatusCode.BadRequest,
                    "HTTP rejects signed cursor belonging to a different session");

            var authority = new SqlSecurityAuthority(sql, installation);
            var firstPage = await authority.ReadOperatorEvidenceMachinePageAsync(hash, site);
            Check(firstPage.Machines.Count == 1 && !firstPage.HasMore &&
                firstPage.Machines[0].MachineId == machine,
                "authorized keyset page returns the single permitted machine");
            var afterPage = await authority.ReadOperatorEvidenceMachinePageAsync(hash, site, machine);
            Check(afterPage.Machines.Count == 0 && !afterPage.HasMore,
                "authorized keyset seek past the sole permitted machine is empty");

            var evidencePath = $"/api/commissioning/v2/machines/{machine:D}/evidence";
            var evidenceWatch = Stopwatch.StartNew();
            using var evidence = await AuthorizedGet(evidencePath);
            evidenceWatch.Stop();
            Check(evidence.StatusCode == HttpStatusCode.OK, "authorized HTTPS-scheme evidence handler reads retained SQL rows");
            using (var evidenceJson = JsonDocument.Parse(await evidence.Content.ReadAsStringAsync()))
            {
                var root = evidenceJson.RootElement;
                var rows = root.GetProperty("rows");
                Check(root.GetProperty("evidenceKind").GetString() == "historical-retained-evidence" &&
                    rows.GetArrayLength() == 2,
                    "HTTP evidence explicitly identifies historical retained observations");
                Check(rows.EnumerateArray().Any(row =>
                    row.GetProperty("rawValue").GetString() == " 00042 " &&
                    row.GetProperty("address").GetString() == "Counter" &&
                    !string.IsNullOrWhiteSpace(row.GetProperty("documentHash").GetString()) &&
                    !string.IsNullOrWhiteSpace(row.GetProperty("acknowledgementId").GetString())),
                    "HTTP response preserves exact stored source value and provenance");
            }
            using (var other = await AuthorizedGet($"/api/commissioning/v2/machines/{otherMachine:D}/evidence"))
                Check(other.StatusCode == HttpStatusCode.Unauthorized, "HTTP evidence rejects another machine");

            // Qualify actual authenticated HTTP continuation across 101 machine-scoped
            // grants. The existing unauthorized machine remains excluded.
            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(sql))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DECLARE @added TABLE(MachineId uniqueidentifier NOT NULL);
                    ;WITH n AS (SELECT 1 AS i UNION ALL SELECT i+1 FROM n WHERE i<100)
                    INSERT [master].Machine(MachineId,SiteId,DisplayName,IsRetired)
                        OUTPUT inserted.MachineId INTO @added(MachineId)
                        SELECT NEWID(),@site,CONCAT(N'HTTP pagination ',i),0 FROM n OPTION(MAXRECURSION 100);
                    INSERT security.PermissionGrant
                        (GrantId,GranteeId,GrantorId,RootOrigin,Permission,ScopeKind,SiteId,MachineId,
                         UseAllowed,CanDelegate,EffectiveFrom,ExpiresAt)
                        SELECT NEWID(),s.PrincipalId,s.PrincipalId,'bootstrap','commissioning.read',
                            'M',@site,a.MachineId,1,0,
                            DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME())
                        FROM @added a CROSS JOIN security.SessionAuthority s WHERE s.TokenHash=@hash;
                    """;
                command.Parameters.AddWithValue("@site", site);
                command.Parameters.AddWithValue("@hash", hash);
                await command.ExecuteNonQueryAsync();
            }
            var httpMachines = new HashSet<Guid>();
            string? nextHttpCursor = null;
            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var path = "/api/commissioning/v2/evidence-machines" +
                    (nextHttpCursor is null ? "" : "?cursor=" + Uri.EscapeDataString(nextHttpCursor));
                using var httpPage = await AuthorizedGet(path);
                Check(httpPage.StatusCode == HttpStatusCode.OK,
                    "authenticated HTTP discovery returns authorized fleet page " + pageIndex);
                using var document = JsonDocument.Parse(await httpPage.Content.ReadAsStringAsync());
                var root = document.RootElement;
                var rows = root.GetProperty("machines");
                var expectedCount = pageIndex == 0 ? 100 : 1;
                Check(rows.GetArrayLength() == expectedCount &&
                    root.GetProperty("isComplete").GetBoolean() == (pageIndex == 1) &&
                    httpPage.Headers.TryGetValues("X-Result-Complete", out var complete) &&
                    complete.Single() == (pageIndex == 1 ? "true" : "false"),
                    "authenticated HTTP page count and completeness match the 101-machine boundary");
                foreach (var row in rows.EnumerateArray())
                    Check(httpMachines.Add(row.GetProperty("machineId").GetGuid()) &&
                        row.GetProperty("machineId").GetGuid() != otherMachine,
                        "HTTP continuation does not duplicate or expose unauthorized machines");
                nextHttpCursor = root.GetProperty("nextCursor").GetString();
                Check((nextHttpCursor is null) == (pageIndex == 1),
                    "signed HTTP continuation exists only while authorized pages remain");
            }
            Check(httpMachines.Count == 101 && httpMachines.Contains(machine),
                "authenticated signed HTTP pagination covers all 101 authorized machine identities");

            // A previously issued continuation cannot preserve permissions that
            // were revoked between pages. SQL must re-evaluate grant ancestry.
            var revokeCursor = MachineDiscoveryCursor.Issue(cursorKey, installation, site, hash, machine);
            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(sql))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT security.GrantRevocation(GrantId,OperationId)
                    SELECT g.GrantId,NEWID()
                    FROM security.PermissionGrant g
                    JOIN security.SessionAuthority s ON s.PrincipalId=g.GranteeId
                    WHERE s.TokenHash=@hash AND g.Permission='commissioning.read'
                      AND g.ScopeKind='M' AND g.SiteId=@site AND g.MachineId<>@keep
                      AND NOT EXISTS(SELECT 1 FROM security.GrantRevocation r WHERE r.GrantId=g.GrantId);
                    """;
                command.Parameters.AddWithValue("@hash", hash);
                command.Parameters.AddWithValue("@site", site);
                command.Parameters.AddWithValue("@keep", machine);
                await command.ExecuteNonQueryAsync();
            }
            using (var changedPage = await AuthorizedGet(
                "/api/commissioning/v2/evidence-machines?cursor=" + Uri.EscapeDataString(revokeCursor)))
            {
                Check(changedPage.StatusCode == HttpStatusCode.OK,
                    "valid signed cursor rechecks changed grants against current SQL authority");
                using var changedJson = JsonDocument.Parse(await changedPage.Content.ReadAsStringAsync());
                Check(changedJson.RootElement.GetProperty("machines").GetArrayLength() == 0 &&
                    changedJson.RootElement.GetProperty("isComplete").GetBoolean() &&
                    changedJson.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null,
                    "revoked machine grants cannot leak through an existing continuation");
            }
            await new SqlSecurityAuthority(sql, installation).RevokeSessionAsync(
                administratorSessionHash, Guid.NewGuid(), hash, await currentEpoch(),
                "revoke disposable HTTP acceptance session");
            using (var revoked = await AuthorizedGet(evidencePath))
                Check(revoked.StatusCode == HttpStatusCode.Unauthorized,
                    "revoked human session is denied by the real evidence route");
            using (var revokedRoster = await AuthorizedGet("/api/commissioning/v2/evidence-machines"))
                Check(revokedRoster.StatusCode == HttpStatusCode.Unauthorized,
                    "revoked human session loses machine discovery");
            using (var revokedSigned = await AuthorizedGet(
                "/api/commissioning/v2/evidence-machines?cursor=" + Uri.EscapeDataString(signedCursor)))
                Check(revokedSigned.StatusCode == HttpStatusCode.Unauthorized,
                    "revoked operator cannot resume an otherwise valid signed HTTP cursor");
            var revokedKeysetDenied = false;
            try
            {
                await authority.ReadOperatorEvidenceMachinePageAsync(hash, site, machine);
            }
            catch (SecurityDeniedException)
            {
                revokedKeysetDenied = true;
            }
            Check(revokedKeysetDenied,
                "keyset continuation rechecks SQL session authority after revocation");

            Activity[] measured;
            lock (transactions)
                measured = transactions.Where(a => a.GetTagItem("security.acquisition.read") is true).ToArray();
            Check(measured.Length >= 2 && measured.All(a =>
                a.GetTagItem("security.sql.roundtrips") is not null),
                "HTTP evidence SQL roundtrips are observable for success and denial");
            var maximumRoundtrips = measured.Max(a => Convert.ToInt64(a.GetTagItem("security.sql.roundtrips")));
            Check(maximumRoundtrips > 0 && maximumRoundtrips <= 100,
                "bounded evidence HTTP route has no full-history or per-row SQL loop");
            Console.WriteLine($"MEASURED: operator HTTP login {loginWatch.Elapsed.TotalMilliseconds:F2} ms; " +
                $"roster {rosterWatch.Elapsed.TotalMilliseconds:F2} ms; " +
                $"evidence {evidenceWatch.Elapsed.TotalMilliseconds:F2} ms; " +
                $"evidence SQL roundtrips maximum {maximumRoundtrips}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
        return passed;

        async Task<HttpResponseMessage> AuthorizedGet(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await http.SendAsync(request);
        }

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }
    }
}
