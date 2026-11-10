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
                    !rosterJson.RootElement.GetProperty("continuationSupported").GetBoolean() &&
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

            await new SqlSecurityAuthority(sql, installation).RevokeSessionAsync(
                administratorSessionHash, Guid.NewGuid(), hash, await currentEpoch(),
                "revoke disposable HTTP acceptance session");
            using (var revoked = await AuthorizedGet(evidencePath))
                Check(revoked.StatusCode == HttpStatusCode.Unauthorized,
                    "revoked human session is denied by the real evidence route");
            using (var revokedRoster = await AuthorizedGet("/api/commissioning/v2/evidence-machines"))
                Check(revokedRoster.StatusCode == HttpStatusCode.Unauthorized,
                    "revoked human session loses machine discovery");
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
