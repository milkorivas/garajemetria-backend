using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarajeMetria.Api.Lpr;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OpenIddict.Server;

namespace GarajeMetria.IntegrationTests;

public sealed class LprHttpTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task AdminRoutesPreserveEtagsSecretsPaginationAndDeviceLifecycle()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var response = await admin.PostAsJsonAsync("/api/lpr/devices", new { name = "Camera", workshopId = setup.Site });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var created = await ReadAsync(response);
        var id = created.GetProperty("device").GetProperty("id").GetGuid();
        var secret = created.GetProperty("credentials").GetProperty("clientSecret").GetString()!;
        Assert.Equal("/api/lpr/devices/" + id.ToString("D"), response.Headers.Location?.OriginalString);
        var tag = response.Headers.ETag!.ToString();
        var detail = await admin.GetAsync(response.Headers.Location);
        Assert.Equal(tag, detail.Headers.ETag?.ToString());
        Assert.DoesNotContain(secret, await detail.Content.ReadAsStringAsync());
        Assert.False((await ReadAsync(detail)).TryGetProperty("version", out _));
        var page = await ReadAsync(await admin.GetAsync("/api/lpr/devices?workshopId=" + setup.Site));
        Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Empty((await ReadAsync(await admin.GetAsync("/api/lpr/devices?pageNumber=2&workshopId=" + setup.Site))).GetProperty("items").EnumerateArray());
        var unchanged = await MutationAsync(admin, id, "name", tag, "{\"name\":\"Camera\"}", HttpMethod.Put);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        Assert.Equal(tag, unchanged.Headers.ETag?.ToString());
        var rename = await MutationAsync(admin, id, "name", tag, "{\"name\":\"Other\"}", HttpMethod.Put);
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.NotEqual(tag, rename.Headers.ETag?.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await MutationAsync(admin, id, "deactivate", tag)).StatusCode);
        tag = rename.Headers.ETag!.ToString();
        var rotate = await MutationAsync(admin, id, "credentials/rotate", tag);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        Assert.NotEqual(secret, (await ReadAsync(rotate)).GetProperty("credentials").GetProperty("clientSecret").GetString());
        var revoked = await MutationAsync(admin, id, "credentials/revoke", rotate.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var disabled = await MutationAsync(admin, id, "deactivate", revoked.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        var reactivated = await MutationAsync(admin, id, "reactivate", disabled.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(id, (await ReadAsync(reactivated)).GetProperty("device").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.DeleteAsync("/api/lpr/devices/" + id)).StatusCode);
    }

    [SqlTheory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("W/\"abc\"", 400)]
    [InlineData("\"01234567890123456789012345678901\",\"01234567890123456789012345678901\"", 400)]
    public async Task PreconditionsRejectMissingWeakWildcardOrMultipleEtags(string? tag, int status)
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var created = await CreateAsync(admin, setup.Site);
        Assert.Equal(status, (int)(await MutationAsync(admin, created.Id, "deactivate", tag)).StatusCode);
    }

    [SqlTheory]
    [InlineData("{\"name\":\"A\",\"name\":\"B\",\"workshopId\":\"site\"}")]
    [InlineData("{\"Name\":\"A\",\"workshopId\":\"site\"}")]
    [InlineData("{\"name\":\"A\",\"workshopId\":\"site\",\"isActive\":true}")]
    [InlineData("{\"name\":null,\"workshopId\":\"site\"}")]
    [InlineData("{\"name\":\"A\",\"workshopId\":123}")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"\\ud800\",\"workshopId\":\"site\"}")]
    public async Task AdministrativeJsonRejectsMalformedInputWithoutCreatingDevices(string body)
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var response = await admin.PostAsync("/api/lpr/devices", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = database.CreateContext();
        Assert.False(await db.LprDevices.AnyAsync(x => x.WorkshopId == setup.Site));
    }

    [SqlFact]
    public async Task AdministrativeLimitsQueriesAndPermissionsAreExplicit()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/lpr/devices?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/lpr/devices?pageNumber=1&pageNumber=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/lpr/devices?unknown=1")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await admin.PostAsync("/api/lpr/devices", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await admin.PostAsync("/api/lpr/devices", new StringContent(new string('x', 8193), Encoding.UTF8, "application/json"))).StatusCode);
        var created = await CreateAsync(admin, setup.Site);
        Assert.Equal(HttpStatusCode.BadRequest, (await MutationAsync(admin, created.Id, "deactivate", created.Tag, "{}")).StatusCode);
        using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/lpr/devices")).StatusCode);
        await using var db = database.CreateContext();
        await db.Users.Where(x => x.DirectoryObjectId == factory.DirectoryAdapter.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.DefaultWorkshopId, setup.Site));
        factory.DirectoryAdapter.Admin = false;
        factory.DirectoryAdapter.Advisor = true;
        using var scope = factory.Services.CreateScope();
        var login = (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        using var advisor = Client(factory, login.Tokens.Access.Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await advisor.GetAsync("/api/lpr/devices")).StatusCode);
    }

    [SqlFact]
    public async Task ClientCredentialsUsesProtectedTokensSeparateFromUsersWithoutRefreshCookiesOrSessionReplacement()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var first = await CreateAsync(admin, setup.Site);
        var second = await CreateAsync(admin, setup.Site);
        using var anonymous = factory.Client();
        var tokenReply = await TokenAsync(anonymous, first.ClientId, first.Secret);
        Assert.Equal(HttpStatusCode.OK, tokenReply.StatusCode);
        Assert.False(tokenReply.Headers.Contains("Set-Cookie"));
        Assert.True(tokenReply.Headers.CacheControl?.NoStore);
        var data = await ReadAsync(tokenReply);
        Assert.Equal("Bearer", data.GetProperty("token_type").GetString());
        Assert.Equal("lpr.events.write", data.GetProperty("scope").GetString());
        Assert.InRange(data.GetProperty("expires_in").GetInt32(), 898, 900);
        Assert.False(data.TryGetProperty("refresh_token", out _));
        Assert.False(data.TryGetProperty("id_token", out _));
        var token = data.GetProperty("access_token").GetString()!;
        Assert.Equal(5, token.Split('.').Length);
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(factory, token)).StatusCode);
        var another = (await ReadAsync(await TokenAsync(anonymous, first.ClientId, first.Secret))).GetProperty("access_token").GetString()!;
        Assert.NotEqual(token, another);
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(factory, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(factory, another)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(anonymous, second.ClientId, second.Secret)).StatusCode);
        using var deviceClient = Client(factory, token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await deviceClient.GetAsync("/api/lpr/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(factory, setup.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/lpr/devices")).StatusCode);
        var logPath = factory.Services.GetRequiredService<IConfiguration>()["FileLogging:DirectoryPath"]!;
        var logs = string.Join('\n', Directory.GetFiles(logPath, "*.json").Select(path =>
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }));
        Assert.Contains("PrepareDeviceTokenGrantCommand", logs);
        Assert.DoesNotContain(first.Secret, logs);
        Assert.DoesNotContain(second.Secret, logs);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain(Basic(first.ClientId, first.Secret).Parameter!, logs);
    }

    [SqlTheory]
    [InlineData("password", null, "unsupported_grant_type")]
    [InlineData("refresh_token", null, "unsupported_grant_type")]
    [InlineData("client_credentials", "openid", "invalid_scope")]
    [InlineData("client_credentials", "offline_access", "invalid_scope")]
    [InlineData("client_credentials", "other", "invalid_scope")]
    public async Task UnsupportedGrantsAndScopesUseOAuthErrors(string grant, string? scopeValue, string expected)
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = factory.Client();
        var response = await TokenAsync(client, device.ClientId, device.Secret, grant, scopeValue);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, (await ReadAsync(response)).GetProperty("error").GetString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [SqlFact]
    public async Task TokenEndpointRejectsNonBasicMixedSourcesQueryJsonAndOversizedBodies()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = factory.Client();
        var bad = await TokenAsync(client, device.ClientId, "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains(bad.Headers.WwwAuthenticate, x => x.Scheme == "Basic");
        Assert.Equal("invalid_client", (await ReadAsync(bad)).GetProperty("error").GetString());
        var post = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = device.ClientId, ["client_secret"] = device.Secret }));
        Assert.False(post.IsSuccessStatusCode);
        using var mixed = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = device.ClientId }) };
        mixed.Headers.Authorization = Basic(device.ClientId, device.Secret);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(mixed)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/connect/token", new { grant_type = "client_credentials" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/connect/token?client_id=synthetic", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/connect/token")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/connect/token", new StringContent(
            "grant_type=client_credentials&grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("http://localhost/connect/token", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/connect/token", new StringContent(new string('x', 4097), Encoding.UTF8, "application/x-www-form-urlencoded"))).StatusCode);
    }

    [SqlFact]
    public async Task RotationRevocationAndDeactivationInvalidateTokensInAnotherHostImmediately()
    {
        using var first = Factory();
        var setup = await SetupAsync(first);
        using var second = Factory(first);
        using var admin = Client(second, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = first.Client();
        var old = (await ReadAsync(await TokenAsync(client, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(first, old)).StatusCode);
        var rotate = await MutationAsync(admin, device.Id, "credentials/rotate", device.Tag);
        var secret = (await ReadAsync(rotate)).GetProperty("credentials").GetProperty("clientSecret").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(first, old)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(client, device.ClientId, device.Secret)).StatusCode);
        var current = (await ReadAsync(await TokenAsync(client, device.ClientId, secret))).GetProperty("access_token").GetString()!;
        var revoke = await MutationAsync(admin, device.Id, "credentials/revoke", rotate.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(first, current)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(client, device.ClientId, secret)).StatusCode);
        var disable = await MutationAsync(admin, device.Id, "deactivate", revoke.Headers.ETag!.ToString());
        var reactivated = await MutationAsync(admin, device.Id, "reactivate", disable.Headers.ETag!.ToString());
        secret = (await ReadAsync(reactivated)).GetProperty("credentials").GetProperty("clientSecret").GetString()!;
        var token = (await ReadAsync(await TokenAsync(client, device.ClientId, secret))).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(first, token)).StatusCode);
        await MutationAsync(admin, device.Id, "deactivate", reactivated.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(first, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(first, old)).StatusCode);
    }

    [SqlFact]
    public async Task TokenEntryValidationExpirationAndWorkshopActivityAreCheckedWithoutCorporateAd()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = factory.Client();
        var token = (await ReadAsync(await TokenAsync(client, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        var calls = factory.DirectoryAdapter.Calls;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(factory, token)).StatusCode);
        await using var db = database.CreateContext();
        await db.Workshops.Where(x => x.Id == setup.Site).ExecuteUpdateAsync(x => x.SetProperty(w => w.IsActive, false));
        Assert.Equal(HttpStatusCode.Forbidden, (await ProbeAsync(factory, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(client, device.ClientId, device.Secret)).StatusCode);
        await db.Workshops.Where(x => x.Id == setup.Site).ExecuteUpdateAsync(x => x.SetProperty(w => w.IsActive, true));
        await db.Set<OpenIddictEntityFrameworkCoreToken>().Where(x => x.Application!.Id == device.Id.ToString("N")).ExecuteUpdateAsync(x => x.SetProperty(t => t.Status, "revoked"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(factory, token)).StatusCode);
        var latest = (await ReadAsync(await TokenAsync(client, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(factory, latest)).StatusCode);
        Assert.Equal(calls, factory.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task OpenApiDescribesJsonEtagsBasicAndFormWithoutAntiforgeryForDeviceToken()
    {
        using var factory = Factory();
        using var client = factory.Client();
        var document = await ReadAsync(await client.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/lpr/devices/{id}/credentials/rotate", out _));
        var token = paths.GetProperty("/connect/token").GetProperty("post");
        Assert.True(token.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/x-www-form-urlencoded", out _));
        Assert.Contains("LprClientBasic", token.GetProperty("security").ToString());
        Assert.DoesNotContain("X-CSRF", token.ToString());
        var list = paths.GetProperty("/api/lpr/devices").GetProperty("get");
        Assert.Equal(4, list.GetProperty("parameters").GetArrayLength());
    }

    [SqlFact]
    public async Task RotationAfterGrantPreparationNeverUpgradesTheAlreadyVerifiedAuthorizationVersion()
    {
        var pause = new GrantPause();
        using var first = Factory(configure: services =>
        {
            services.RemoveAll<IRequestHandler<PrepareDeviceTokenGrantCommand, Outcome<DeviceTokenGrant>>>();
            services.AddScoped<IRequestHandler<PrepareDeviceTokenGrantCommand, Outcome<DeviceTokenGrant>>>(sp =>
                new PausedGrant(ActivatorUtilities.CreateInstance<PrepareDeviceTokenGrantHandler>(sp), pause));
        });
        var setup = await SetupAsync(first);
        using var second = Factory(first);
        using var admin = Client(second, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = first.Client();
        var issuance = TokenAsync(client, device.ClientId, device.Secret);
        try
        {
            await pause.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var rotation = await MutationAsync(admin, device.Id, "credentials/rotate", device.Tag);
            Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        }
        finally { pause.Release.TrySetResult(); }
        var response = await issuance;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stale = (await ReadAsync(response)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(second, stale)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(client, device.ClientId, device.Secret)).StatusCode);
    }

    [SqlFact]
    public async Task ClientQuotaCountsRejectedSecretsAndSqlFailureNeverIssuesOrAcceptsAToken()
    {
        var fault = new SqlFault();
        using var factory = Factory(configure: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault)));
        var options = factory.Services.GetRequiredService<LprOptions>();
        options.TokensPerClient = 2;
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = factory.Client();
        var issued = await TokenAsync(client, device.ClientId, device.Secret, scope: LprOAuth.Scope);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var token = (await ReadAsync(issued)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(client, device.ClientId, "wrong-secret")).StatusCode);
        var limited = await TokenAsync(client, device.ClientId, device.Secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("temporarily_unavailable", (await ReadAsync(limited)).GetProperty("error").GetString());
        fault.Enabled = true;
        var unavailable = await TokenAsync(client, device.ClientId, device.Secret);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("temporarily_unavailable", (await ReadAsync(unavailable)).GetProperty("error").GetString());
        fault.CommandFragment = "LprDevices";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ProbeAsync(factory, token)).StatusCode);
    }

    [SqlFact]
    public async Task AHostWithOtherDeviceCertificatesCannotAcceptTheToken()
    {
        using var first = Factory();
        var setup = await SetupAsync(first);
        using var admin = Client(first, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = first.Client();
        var token = (await ReadAsync(await TokenAsync(client, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var otherKeys = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(otherKeys, token)).StatusCode);
    }

    [SqlFact]
    public async Task IpQuotaRunsBeforeOAuthParsingIncludingRejectedRequests()
    {
        using var factory = Factory();
        factory.Services.GetRequiredService<LprOptions>().TokensPerIp = 1;
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/connect/token", new { invalid = true })).StatusCode);
        var blocked = await client.PostAsync("/connect/token", new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
        Assert.Equal("temporarily_unavailable", (await ReadAsync(blocked)).GetProperty("error").GetString());
    }

    [SqlFact]
    public async Task CatalogReactivationRequiresANewTokenInAnotherHostButPreservesTheDeviceSecret()
    {
        using var first = Factory();
        var setup = await SetupAsync(first);
        using var second = Factory(first);
        using var admin = Client(first, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var client = first.Client();
        var token = (await ReadAsync(await TokenAsync(client, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(second, token)).StatusCode);
        using var scope = first.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        await store.PublishAsync(await store.BeginObservationAsync(default), [], default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(second, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(client, device.ClientId, device.Secret)).StatusCode);
        await store.PublishAsync(await store.BeginObservationAsync(default), [new(setup.Site, "Restored")], default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(second, token)).StatusCode);
        var reply = await TokenAsync(client, device.ClientId, device.Secret);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var fresh = (await ReadAsync(reply)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(second, fresh)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/lpr/devices/" + device.Id)).StatusCode);
    }

    [SqlFact]
    public async Task EventEndpointPersistsInvalidLowConfidenceReadingsAndConfirmsOldEquivalentRetries()
    {
        var clock = new EventClock();
        using var factory = Factory(configure: services => { services.RemoveAll<ILprClock>(); services.AddSingleton<ILprClock>(clock); });
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var id = Guid.NewGuid();
        var body = EventBody(id, device.Id, setup.Site, clock.Now.AddMinutes(-29));
        var created = await PostEventAsync(client, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null(created.Headers.Location);
        Assert.False(created.Headers.Contains("Set-Cookie"));
        Assert.True(created.Headers.CacheControl?.NoStore);
        var receipt = await ReadAsync(created);
        Assert.Equal(id, receipt.GetProperty("event_id").GetGuid());
        Assert.Equal("invalid", receipt.GetProperty("plate_status").GetString());
        Assert.False(receipt.GetProperty("duplicate").GetBoolean());
        Assert.False(receipt.TryGetProperty("plate", out _));
        clock.Now = clock.Now.AddMinutes(2);
        var duplicate = await PostEventAsync(client, body.Replace("\"confidence\":0", "\"confidence\":0e9999999999", StringComparison.Ordinal).Replace("}", ",\"direction\":null}", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var repeated = await ReadAsync(duplicate);
        Assert.True(repeated.GetProperty("duplicate").GetBoolean());
        Assert.Equal(receipt.GetProperty("received_at").GetString(), repeated.GetProperty("received_at").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PostEventAsync(client, body.Replace("synthetic-invalid", "SYNTHETIC-INVALID", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostEventAsync(client, body.Replace(id.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.GetAsync("/api/lpr/events")).StatusCode);
        var logs = ReadLogs(factory);
        Assert.DoesNotContain("synthetic-invalid", logs);
        Assert.DoesNotContain(token, logs);
    }

    [SqlTheory]
    [InlineData("{\"event_id\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{} {}")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("case")]
    [InlineData("uuid")]
    [InlineData("uuid-spaces")]
    [InlineData("nil")]
    [InlineData("timestamp-zone")]
    [InlineData("timestamp-unknown")]
    [InlineData("timestamp-precision")]
    [InlineData("timestamp-leap")]
    [InlineData("confidence-string")]
    [InlineData("confidence-rounding")]
    [InlineData("confidence-tiny")]
    [InlineData("confidence-large")]
    [InlineData("direction")]
    [InlineData("unicode")]
    public async Task EventJsonRejectsMalformedOrInexactValuesBeforePersistence(string variant)
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var id = Guid.NewGuid();
        var date = DateTimeOffset.UtcNow;
        var body = EventBody(id, device.Id, setup.Site, date);
        body = variant switch
        {
            "missing" => body.Replace("\"plate\":\"synthetic-invalid\",", "", StringComparison.Ordinal),
            "duplicate" => body.Replace("}", ",\"confidence\":0}", StringComparison.Ordinal),
            "unknown" => body.Replace("}", ",\"image\":\"synthetic\"}", StringComparison.Ordinal),
            "case" => body.Replace("event_id", "Event_id", StringComparison.Ordinal),
            "uuid" => body.Replace(id.ToString(), id.ToString("N"), StringComparison.Ordinal),
            "uuid-spaces" => body.Replace(id.ToString(), " " + id.ToString() + " ", StringComparison.Ordinal),
            "nil" => body.Replace(id.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
            "timestamp-zone" => body.Replace(date.UtcDateTime.ToString("O"), "2026-11-13T12:00:00", StringComparison.Ordinal),
            "timestamp-unknown" => body.Replace(date.UtcDateTime.ToString("O"), "2026-11-13T12:00:00-00:00", StringComparison.Ordinal),
            "timestamp-precision" => body.Replace(date.UtcDateTime.ToString("O"), "2026-11-13T12:00:00.12345678Z", StringComparison.Ordinal),
            "timestamp-leap" => body.Replace(date.UtcDateTime.ToString("O"), "2026-11-13T12:00:60Z", StringComparison.Ordinal),
            "confidence-string" => body.Replace("\"confidence\":0", "\"confidence\":\"0\"", StringComparison.Ordinal),
            "confidence-rounding" => body.Replace("\"confidence\":0", "\"confidence\":0.123456780000000000000000000001", StringComparison.Ordinal),
            "confidence-tiny" => body.Replace("\"confidence\":0", "\"confidence\":1e-999999999", StringComparison.Ordinal),
            "confidence-large" => body.Replace("\"confidence\":0", "\"confidence\":1e999999999", StringComparison.Ordinal),
            "direction" => body.Replace("}", ",\"direction\":\"Entry\"}", StringComparison.Ordinal),
            "unicode" => body.Replace("synthetic-invalid", "\\ud800", StringComparison.Ordinal),
            _ => variant
        };
        var response = await PostEventAsync(client, body);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_input", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var db = database.CreateContext();
        Assert.False(await db.LprEvents.AnyAsync(x => x.DeviceId == device.Id));
    }

    [SqlFact]
    public async Task EventBoundaryEnforcesDeviceIdentitySizesMediaAndDuplicateQuotas()
    {
        using var factory = Factory();
        factory.Services.GetRequiredService<LprOptions>().EventsPerDevice = 2;
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var body = EventBody(Guid.NewGuid(), device.Id, setup.Site, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEventAsync(oauth, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEventAsync(admin, body)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/lpr/events", new StringContent(body))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostEventAsync(client, new string(' ', 8193))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/lpr/events", new UnknownLengthJson(new byte[8193]))).StatusCode);
        using var malformed = new ByteArrayContent([0x7b, 0x22, 0xc3, 0x28, 0x22, 0x7d]);
        malformed.Headers.ContentType = new("application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/lpr/events", malformed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostEventAsync(client, body.Replace(device.Id.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(client, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostEventAsync(client, body)).StatusCode);
        var limited = await PostEventAsync(client, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("rate_limited", (await ReadAsync(limited)).GetProperty("code").GetString());
    }

    private static string EventBody(Guid id, Guid device, string site, DateTimeOffset date) => JsonSerializer.Serialize(new
    { event_id = id, capture_timestamp = date.UtcDateTime.ToString("O"), device_id = device, site_id = site, plate = "synthetic-invalid", confidence = 0 });
    private static Task<HttpResponseMessage> PostEventAsync(HttpClient client, string body) =>
        client.PostAsync("/api/lpr/events", new StringContent(body, Encoding.UTF8, "application/json"));
    private sealed class EventClock : ILprClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(Now);
    }
    private sealed class UnknownLengthJson : HttpContent
    {
        private readonly byte[] data;
        public UnknownLengthJson(byte[] data) { this.data = data; Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [SqlFact]
    public async Task EventErrorsAreSafeAndAnUnacknowledgedCommitCanBeReconciledOverHttp()
    {
        var sql = new SqlFault { CommandFragment = "LprEvents" };
        var commit = new EventCommitFault();
        using var factory = Factory(configure: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(sql, commit)));
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var body = EventBody(Guid.NewGuid(), device.Id, setup.Site, DateTimeOffset.UtcNow);
        sql.Enabled = true;
        var unavailable = await PostEventAsync(client, body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("service_unavailable", (await ReadAsync(unavailable)).GetProperty("code").GetString());
        sql.Enabled = false;
        commit.Enabled = true;
        var unknown = await PostEventAsync(client, body);
        Assert.Equal(HttpStatusCode.InternalServerError, unknown.StatusCode);
        Assert.Equal("operation_outcome_unknown", (await ReadAsync(unknown)).GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-invalid", await unknown.Content.ReadAsStringAsync());
        commit.Enabled = false;
        var repeated = await PostEventAsync(client, body);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.True((await ReadAsync(repeated)).GetProperty("duplicate").GetBoolean());
        Assert.DoesNotContain("synthetic-invalid", ReadLogs(factory));
    }

    [SqlFact]
    public async Task EventAuthenticationRejectsACryptographicallyValidTokenWithoutTheWriteScope()
    {
        using var factory = Factory(configure: services =>
        {
            services.AddSingleton<StripDeviceScope>();
            services.Configure<OpenIddictServerOptions>(options => options.Handlers.Add(
                OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
                    .UseSingletonHandler<StripDeviceScope>().SetOrder(OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order + 700).Build()));
        });
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var response = await PostEventAsync(client, EventBody(Guid.NewGuid(), device.Id, setup.Site, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("device_forbidden", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [SqlTheory]
    [InlineData("1e-8", "0.00000001")]
    [InlineData("0.123456780000000000000000000000000", "0.12345678")]
    [InlineData("1.000000000000000000000000000000000", "1")]
    public async Task EventNumbersAreConvertedExactlyAndOffsetsAndOlderUuidVersionsAreAccepted(string number, string expected)
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var client = Client(factory, token);
        var date = DateTimeOffset.UtcNow;
        var textId = Guid.NewGuid().ToString();
        var id = Guid.Parse(textId[..14] + "1" + textId[15..]);
        var body = EventBody(id, device.Id, setup.Site, date).Replace("synthetic-invalid", "0000 BBB", StringComparison.Ordinal)
            .Replace("\"confidence\":0", "\"confidence\":" + number, StringComparison.Ordinal)
            .Replace(date.UtcDateTime.ToString("O"), date.ToOffset(TimeSpan.FromHours(1)).ToString("O"), StringComparison.Ordinal)
            .Replace("}", ",\"direction\":\"exit\"}", StringComparison.Ordinal);
        var response = await PostEventAsync(client, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("valid", (await ReadAsync(response)).GetProperty("plate_status").GetString());
        await using var db = database.CreateContext();
        var saved = await db.LprEvents.SingleAsync(x => x.EventId == id);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), saved.Confidence);
        Assert.Equal(date, saved.CapturedAt);
        Assert.Equal(GarajeMetria.Domain.Lpr.LprDirection.Exit, saved.Direction);
    }
    private static string ReadLogs(AuthenticationFactory factory)
    {
        var path = factory.Services.GetRequiredService<IConfiguration>()["FileLogging:DirectoryPath"]!;
        return string.Join('\n', Directory.GetFiles(path, "*.json").Select(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }
    private sealed class EventCommitFault : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default) =>
            Enabled && data.Context!.ChangeTracker.Entries<LprEventRow>().Any()
                ? throw new InvalidOperationException("Synthetic lost acknowledgment") : Task.CompletedTask;
    }
    private sealed class StripDeviceScope : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
    {
        public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
        { context.AccessTokenPrincipal!.SetScopes([]); return ValueTask.CompletedTask; }
    }

    private AuthenticationFactory Factory(AuthenticationFactory? shared = null, Action<IServiceCollection>? configure = null) =>
        new(database, shared: shared, configureServices: services =>
        { services.AddSingleton<IStartupFilter, DeviceProbe>(); configure?.Invoke(services); });
    private async Task<(string Access, string Site)> SetupAsync(AuthenticationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var login = (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        var site = "http-" + Guid.NewGuid().ToString("N");
        await using var db = database.CreateContext();
        db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic", IsActive = true });
        if (!await db.Catalog.AnyAsync()) db.Catalog.Add(new() { Id = 1, LastPublishedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (login.Tokens.Access.Value, site);
    }
    private static HttpClient Client(AuthenticationFactory factory, string token)
    { var client = factory.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client; }
    private static async Task<(Guid Id, string ClientId, string Secret, string Tag)> CreateAsync(HttpClient client, string site)
    {
        var response = await client.PostAsJsonAsync("/api/lpr/devices", new { name = "Synthetic camera", workshopId = site });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ReadAsync(response);
        return (json.GetProperty("device").GetProperty("id").GetGuid(), json.GetProperty("credentials").GetProperty("clientId").GetString()!,
            json.GetProperty("credentials").GetProperty("clientSecret").GetString()!, response.Headers.ETag!.ToString());
    }
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static AuthenticationHeaderValue Basic(string id, string secret) => new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(Uri.EscapeDataString(id) + ":" + Uri.EscapeDataString(secret))));
    private static Task<HttpResponseMessage> TokenAsync(HttpClient client, string id, string secret, string grant = "client_credentials", string? scope = null)
    {
        var fields = new Dictionary<string, string> { ["grant_type"] = grant };
        if (scope is not null) fields["scope"] = scope;
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = Basic(id, secret);
        return client.SendAsync(request);
    }
    private static Task<HttpResponseMessage> MutationAsync(HttpClient client, Guid id, string operation, string? tag, string? body = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, $"/api/lpr/devices/{id:D}/{operation}");
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return client.SendAsync(request);
    }
    private static async Task<HttpResponseMessage> ProbeAsync(AuthenticationFactory factory, string token)
    { using var client = Client(factory, token); return await client.GetAsync("/_tests/lpr-device"); }
    private sealed class GrantPause
    {
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class PausedGrant(PrepareDeviceTokenGrantHandler inner, GrantPause pause)
        : IRequestHandler<PrepareDeviceTokenGrantCommand, Outcome<DeviceTokenGrant>>
    {
        public async Task<Outcome<DeviceTokenGrant>> Handle(PrepareDeviceTokenGrantCommand request, CancellationToken ct)
        {
            var result = await inner.Handle(request, ct);
            if (result.Succeeded)
            {
                pause.Prepared.TrySetResult();
                await pause.Release.Task.WaitAsync(ct);
            }
            return result;
        }
    }
    private sealed class SqlFault : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public string? CommandFragment { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) => Enabled &&
                (CommandFragment is null || command.CommandText.Contains(CommandFragment, StringComparison.Ordinal))
                ? throw new PersistenceFailureException(FailureCode.ServiceUnavailable) : ValueTask.FromResult(result);
    }
    // HTTP-only probe exists in the test host, never in production. It exercises the real device scheme and SQL stores.
    private sealed class DeviceProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path == "/_tests/lpr-device")
                {
                    context.SetEndpoint(new Endpoint(http =>
                    { http.Response.StatusCode = 204; return Task.CompletedTask; },
                        new EndpointMetadataCollection(new AuthorizeAttribute { Policy = LprOAuth.WriterPolicy },
                            new LprTrafficMetadata(LprTraffic.Event)), "Test device authentication"));
                }
                await continuation();
            });
            next(app);
        };
    }
}
