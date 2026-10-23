using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

public sealed class HttpAuthenticationTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task LoginRotationAndLogoutRespectTheHttpAndCookieContract()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "  synthetic😀  " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var login = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("administrator", login.RootElement.GetProperty("user").GetProperty("role").GetString());
        Assert.False(login.RootElement.TryGetProperty("refreshToken", out _));
        Assert.False(login.RootElement.TryGetProperty("refreshTokenExpiresAt", out _));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", login.RootElement.GetProperty("accessTokenExpiresAt").GetString()!);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(AuthenticationEndpoints.RefreshCookie + "=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/auth", cookie);
        Assert.DoesNotContain("domain=", cookie);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "expired-or-invalid-ignored-by-auth-endpoints");
        var refresh = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        using var refreshed = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        Assert.False(refreshed.RootElement.TryGetProperty("user", out _));
        Assert.NotEqual(login.RootElement.GetProperty("accessToken").GetString(), refreshed.RootElement.GetProperty("accessToken").GetString());
        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal("", await logout.Content.ReadAsStringAsync());
        Assert.Equal(2, logout.Headers.GetValues("Set-Cookie").Count());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [SqlFact]
    public async Task BootstrapRemainsAnonymousWithAnyBearerAndDoesNotCreateSession()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-invalid-token");
        var response = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(AuthenticationEndpoints.AntiforgeryCookie + "=", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("expires=", cookie);
        Assert.Equal(0, factory.DirectoryAdapter.Calls);
        await using var db = database.CreateContext();
        Assert.False(await db.Users.AnyAsync(u => u.DirectoryObjectId == factory.DirectoryAdapter.Id));
    }

    [SqlFact]
    public async Task OriginAndAntiforgeryFailuresHaveNoAuthenticationEffects()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        var noPair = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "password" });
        Assert.Equal(HttpStatusCode.Forbidden, noPair.StatusCode);
        Assert.False(noPair.Headers.Contains("Set-Cookie"));
        await AuthenticationFactory.BootstrapAsync(client);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.test");
        var wrongOrigin = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "password" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        Assert.Contains("origin_not_allowed", await wrongOrigin.Content.ReadAsStringAsync());
        Assert.False(wrongOrigin.Headers.Contains("Set-Cookie"));
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/auth/antiforgery")).StatusCode);
        Assert.Equal(0, factory.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task StrictJsonRejectsMalformedDuplicateUnknownAndInvalidUnicodeBeforeAd()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        string[] invalid = ["", "[]", "null", "{\"username\":\"user\"}",
            "{\"username\":\"user\",\"password\":\"x\",\"role\":\"administrator\"}",
            "{\"username\":\"user\",\"password\":\"x\",\"password\":\"y\"}",
            "{\"Username\":\"user\",\"password\":\"x\"}",
            "{\"username\":\"user\",\"password\":\"\\uD800\"}",
            "{\"username\":\"user@domain\",\"password\":\"x\"}"];
        foreach (var body in invalid)
        {
            var response = await client.PostAsync("/api/auth/login", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(0, factory.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task ContentTypeEncodingAndBodyLimitsAreExplicit()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/auth/login", new StringContent("{}"))).StatusCode);
        using var compressed = new StringContent("{}", Encoding.UTF8, "application/json");
        compressed.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/auth/login", compressed)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/auth/login", new StringContent(new string(' ', 16385), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(0, factory.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task AdvisorWithoutAssignmentPersistsProfileAndAuditButNotSession()
    {
        using var factory = new AuthenticationFactory(database);
        factory.DirectoryAdapter.Admin = false;
        factory.DirectoryAdapter.Advisor = true;
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "password" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("workshop_required", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(x => x.DirectoryObjectId == factory.DirectoryAdapter.Id);
        Assert.Null(user.DefaultWorkshopId);
        Assert.True(await db.ProvisioningAudits.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await db.Sessions.AnyAsync(x => x.UserId == user.Id));
    }

    [SqlFact]
    public async Task ReplayedRefreshRevokesTheWinnerAcrossIndependentClients()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "password" });
        var refreshCookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
        using var replay = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var bootstrap = factory.Client();
        var boot = await bootstrap.GetAsync("/api/auth/antiforgery");
        var csrfCookie = boot.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var json = JsonDocument.Parse(await boot.Content.ReadAsStringAsync());
        replay.DefaultRequestHeaders.Add("Cookie", refreshCookie + "; " + csrfCookie);
        replay.DefaultRequestHeaders.Add(AuthenticationEndpoints.CsrfHeader, json.RootElement.GetProperty("requestToken").GetString());
        var response = await replay.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [SqlFact]
    public async Task ConcurrentLoginsLeaveOneActiveSessionAndOneProfile()
    {
        using var factory = new AuthenticationFactory(database);
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var results = await Task.WhenAll(first.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("one", "password")),
            second.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("two", "password")));
        Assert.All(results, x => Assert.True(x.Succeeded));
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(x => x.DirectoryObjectId == factory.DirectoryAdapter.Id);
        Assert.Equal(1, await db.Sessions.CountAsync(x => x.UserId == user.Id && x.ClosedAt == null));
        Assert.Equal(1, await db.ProvisioningAudits.CountAsync(x => x.UserId == user.Id));
    }

    [SqlFact]
    public async Task UserQuotaIsSharedAndReturnsRetryAfterWithoutCallingAd()
    {
        using var factory = new AuthenticationFactory(database);
        factory.DirectoryAdapter.Status = DirectoryStatus.Rejected;
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { username = i % 2 == 0 ? "user" : "test\\USER", password = "password" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "password" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(5, factory.DirectoryAdapter.Calls);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [SqlFact]
    public async Task OpenApiAndSwaggerExposeTheImplementedContract()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal(5, paths.EnumerateObject().Count());
        Assert.True(paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses").TryGetProperty("503", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [SqlFact]
    public async Task CorsPreflightDoesNotRequireAntiforgeryOrAuthenticate()
    {
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, factory.DirectoryAdapter.Calls);
    }
}
