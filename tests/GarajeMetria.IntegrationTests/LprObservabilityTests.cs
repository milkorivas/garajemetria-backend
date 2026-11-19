using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Api.Lpr;
using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Infrastructure.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprHttpTests
{
    [SqlFact]
    public async Task MetricsAndLogsDescribeOutcomesWithoutIdentityPayloadOrSecretLabels()
    {
        var samples = new ConcurrentBag<(string Name, object Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscribed) =>
        { if (instrument.Meter.Name == LprTelemetry.MeterName) subscribed.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => samples.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => samples.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        using var factory = Factory();
        factory.Services.GetRequiredService<GarajeMetria.Application.Lpr.LprOptions>().EventsPerDevice = 3;
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var body = EventBody(Guid.NewGuid(), device.Id, setup.Site, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(client, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostEventAsync(client, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostEventAsync(client, body.Replace("synthetic-invalid", "SYNTHETIC-INVALID", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostEventAsync(client, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEventAsync(oauth, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await oauth.PostAsync("http://localhost/connect/token", new StringContent(""))).StatusCode);
        var outcomes = samples.Where(x => x.Name == "garajemetria.lpr.requests").Select(x => x.Tags.Single(t => t.Key == "outcome").Value).ToArray();
        foreach (var outcome in new[] { "accepted", "duplicate", "conflict", "rate_limited", "unauthorized", "invalid" }) Assert.Contains(outcome, outcomes);
        Assert.Contains(samples, x => x.Name == "garajemetria.lpr.request.duration" && x.Value is double seconds && seconds >= 0);
        Assert.All(samples, sample =>
        {
            Assert.Equal(new[] { "outcome", "plate_status", "status_code", "traffic" }, sample.Tags.Select(x => x.Key).Order().ToArray());
            var text = string.Join('|', sample.Tags.Select(x => x.Value));
            foreach (var sensitive in new[] { device.Secret, token, device.ClientId, setup.Site, device.Id.ToString(), "synthetic-invalid" }) Assert.DoesNotContain(sensitive, text);
        });
        var logs = ReadLogs(factory);
        Assert.Contains("LPR request completed", logs);
        Assert.Contains("rate_limited", logs);
        foreach (var sensitive in new[] { device.Secret, token, "synthetic-invalid" }) Assert.DoesNotContain(sensitive, logs);
    }

    [SqlFact]
    public async Task OpenApiAndCorsDescribeEveryLprContractWithoutPublishingAnonymousOAuthMetadata()
    {
        using var factory = Factory();
        using var client = factory.Client();
        var document = await ReadAsync(await client.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths");
        foreach (var path in paths.EnumerateObject().Where(x => x.Name.StartsWith("/api/lpr/", StringComparison.Ordinal) || x.Name == "/connect/token"))
            foreach (var operation in path.Value.EnumerateObject())
            {
                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject())
                    Assert.True(response.Value.GetProperty("headers").TryGetProperty("Cache-Control", out _));
                var security = operation.Value.GetProperty("security").ToString();
                Assert.Contains(path.Name == "/connect/token" ? "LprClientBasic" :
                    path.Name == "/api/lpr/events" && operation.Name == "post" ? "LprOAuth" : "Bearer", security);
                Assert.DoesNotContain("X-CSRF", operation.Value.ToString());
            }
        var events = paths.GetProperty("/api/lpr/events").GetProperty("post");
        Assert.True(events.GetProperty("responses").GetProperty("429").GetProperty("headers").TryGetProperty("Retry-After", out _));
        Assert.False(events.GetProperty("responses").GetProperty("201").GetProperty("headers").TryGetProperty("Location", out _));
        Assert.True(paths.GetProperty("/api/lpr/devices").GetProperty("post").GetProperty("responses").GetProperty("201").GetProperty("headers").TryGetProperty("Location", out _));
        foreach (var path in new[] { "/.well-known/openid-configuration", "/.well-known/jwks", "/connect/introspect", "/connect/revoke", "/connect/register" })
        { Assert.False(paths.TryGetProperty(path, out _)); Assert.False((await client.GetAsync(path)).IsSuccessStatusCode); }
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/lpr/devices/" + Guid.NewGuid() + "/name");
        allowed.Headers.Add("Origin", "https://localhost");
        allowed.Headers.Add("Access-Control-Request-Method", "PUT");
        allowed.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,if-match");
        var preflight = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("https://localhost", Assert.Single(preflight.Headers.GetValues("Access-Control-Allow-Origin")));
        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/lpr/devices");
        denied.Headers.Add("Origin", "https://untrusted.test"); denied.Headers.Add("Access-Control-Request-Method", "POST");
        Assert.False((await client.SendAsync(denied)).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(0, factory.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task SqlHealthRejectsAMissingOAuthStoreWithoutExposingSchemaOrConnectionDetails()
    {
        var logs = new RecordingLogProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Authentication"] = database.ConnectionString }).Build();
        var probe = new HealthDiagnostics(configuration, new DirectoryOptions(), loggerFactory.CreateLogger<HealthDiagnostics>());
        Assert.Equal(ModuleStatus.Ok, await probe.CheckSqlAsync(default));
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_rename N'dbo.OpenIddictScopes', N'OpenIddictScopes_ProbeTest'");
        try { Assert.Equal(ModuleStatus.Error, await probe.CheckSqlAsync(default)); }
        finally { await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_rename N'dbo.OpenIddictScopes_ProbeTest', N'OpenIddictScopes'"); }
        Assert.Equal(ModuleStatus.Ok, await probe.CheckSqlAsync(default));
        Assert.All(logs.Entries, entry =>
        { Assert.Null(entry.Exception); Assert.DoesNotContain(database.ConnectionString, entry.Message); Assert.DoesNotContain("OpenIddictScopes", entry.Message); });
    }

    [SqlFact]
    public async Task ApiDocumentationIsClosedWhenDisabledWithoutDisablingDeviceOAuth()
    {
        Assert.False(new HttpOptions().EnableApiDocumentation);
        using var factory = Factory(configure: services =>
            ((HttpOptions)services.Single(x => x.ServiceType == typeof(HttpOptions)).ImplementationInstance!).EnableApiDocumentation = false);
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = factory.Client();
        foreach (var path in new[] { "/openapi/v1.json", "/swagger/index.html" }) Assert.False((await client.GetAsync(path)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(client, device.ClientId, device.Secret)).StatusCode);
    }
}
