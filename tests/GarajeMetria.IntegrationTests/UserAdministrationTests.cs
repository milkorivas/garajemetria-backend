using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Users;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

public sealed class UserAdministrationTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    private async Task<AuthenticationResult> LoginAsync(AuthenticationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
    }

    private async Task<string> PublishAsync(AuthenticationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        await store.PublishAsync(await store.BeginObservationAsync(default), [new("0001", "Synthetic Workshop")], default);
        await using var db = database.CreateContext();
        return (await db.Workshops.SingleAsync(x => x.Code == "0001")).Id;
    }

    private static HttpClient Client(AuthenticationFactory factory, AuthenticationResult login)
    {
        var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.Tokens.Access.Value);
        return client;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string userId, string? tag, string? workshopId, string reason = "Synthetic reason")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{userId}/default-workshop")
        { Content = JsonContent.Create(new { workshopId, reason }) };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await client.SendAsync(request);
    }

    [SqlFact]
    public async Task RF002_CP016_SelfChangeAuditsAndRevokesAccessAndRefresh()
    {
        using var factory = new AuthenticationFactory(database);
        var workshop = await PublishAsync(factory);
        var login = await LoginAsync(factory);
        using var client = Client(factory, login);
        var id = login.User!.Id;
        var get = await client.GetAsync($"/api/users/{id}/default-workshop");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var tag = get.Headers.ETag!.ToString();
        var response = await PutAsync(client, id, tag, workshop, "  Synthetic 🚗 assignment  ");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(tag, response.Headers.ETag!.ToString());
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(FailureCode.SessionEnded, (await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new RefreshSessionCommand(login.Tokens.Refresh.Value))).Error?.Code);
        await using var db = database.CreateContext();
        var audit = await db.AssignmentAudits.SingleAsync(x => x.UserId == Guid.Parse(id));
        Assert.Equal(Guid.Parse(id), audit.ActorId);
        Assert.Null(audit.PreviousWorkshopId);
        Assert.Equal(workshop, audit.NewWorkshopId);
        Assert.Equal("  Synthetic 🚗 assignment  ", audit.Reason);
        Assert.NotEmpty(audit.CorrelationId);
    }

    [SqlFact]
    public async Task RF002_CP017_NoChangeAndNullRemovalRequireCurrentRole()
    {
        using var factory = new AuthenticationFactory(database);
        var login = await LoginAsync(factory);
        using var client = Client(factory, login);
        var id = login.User!.Id;
        var tag = (await client.GetAsync($"/api/users/{id}/default-workshop")).Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(client, id, tag, null)).StatusCode);
        Assert.Equal(tag, (await client.GetAsync($"/api/users/{id}/default-workshop")).Headers.ETag!.ToString());
        factory.DirectoryAdapter.Admin = false;
        factory.DirectoryAdapter.Advisor = true;
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, id, tag, null)).StatusCode);
        factory.DirectoryAdapter.Status = DirectoryStatus.Unavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PutAsync(client, id, tag, null)).StatusCode);
        await using var db = database.CreateContext();
        Assert.False(await db.AssignmentAudits.AnyAsync(x => x.UserId == Guid.Parse(id)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
    }

    [SqlFact]
    public async Task RF002_CP017_ConcurrentChangesHaveOneWinnerAndOneAudit()
    {
        using var factory = new AuthenticationFactory(database);
        var workshop = await PublishAsync(factory);
        var login = await LoginAsync(factory);
        var target = new LocalUser { Id = Guid.NewGuid(), DirectoryObjectId = Guid.NewGuid(), Username = "pending", DisplayName = "Synthetic Pending" };
        await using (var db = database.CreateContext())
        {
            db.Users.Add(target);
            db.Sessions.Add(new(Guid.NewGuid(), target.Id, UserRoles.Advisor, factory.Clock.Now, factory.Clock.Now.AddHours(1)));
            await db.SaveChangesAsync();
        }
        using var client = Client(factory, login);
        var tag = (await client.GetAsync($"/api/users/{target.Id}/default-workshop")).Headers.ETag!.ToString();
        var responses = await Task.WhenAll(PutAsync(client, target.Id.ToString(), tag, workshop), PutAsync(client, target.Id.ToString(), tag, workshop));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.PreconditionFailed);
        await using var check = database.CreateContext();
        Assert.Equal(1, await check.AssignmentAudits.CountAsync(x => x.UserId == target.Id));
        Assert.NotNull((await check.Sessions.SingleAsync(x => x.UserId == target.Id)).ClosedAt);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
    }

    [SqlFact]
    public async Task RF002_CP018_ListIsLocalBoundedAndDoesNotExposeDirectoryOrRoles()
    {
        using var factory = new AuthenticationFactory(database);
        var login = await LoginAsync(factory);
        using var client = Client(factory, login);
        var calls = factory.DirectoryAdapter.Calls;
        var response = await client.GetAsync("/api/users?pageNumber=1&pageSize=1");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "defaultWorkshopId", "displayName", "id", "username" }, user.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(calls, factory.DirectoryAdapter.Calls);
        using var far = JsonDocument.Parse(await client.GetStringAsync("/api/users?pageNumber=2147483647&pageSize=100"));
        Assert.Empty(far.RootElement.GetProperty("items").EnumerateArray());
        foreach (var query in new[] { "pageSize=101", "pageNumber=0", "pageNumber=1&pageNumber=2", "pageSize=1.5", "sort=username" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/users?" + query)).StatusCode);
    }

    [SqlFact]
    public async Task RF002_CP008_AuthenticationAndRoleAreRequiredBeforeResourceLookup()
    {
        using var factory = new AuthenticationFactory(database);
        using var anonymous = factory.Client();
        await AuthenticationFactory.BootstrapAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users/missing/default-workshop")).StatusCode);
        var admin = await LoginAsync(factory);
        var workshop = await PublishAsync(factory);
        await using (var db = database.CreateContext())
        {
            var user = await db.Users.SingleAsync(x => x.Id == Guid.Parse(admin.User!.Id));
            user.DefaultWorkshopId = workshop;
            await db.SaveChangesAsync();
        }
        factory.DirectoryAdapter.Admin = false;
        factory.DirectoryAdapter.Advisor = true;
        var advisor = await LoginAsync(factory);
        using var client = Client(factory, advisor);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users/missing/default-workshop")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(client, "missing", null, null)).StatusCode);
    }

    [SqlFact]
    public async Task RF002_CP016_StrictJsonAndPreconditionsRejectInvalidRequests()
    {
        using var factory = new AuthenticationFactory(database);
        var login = await LoginAsync(factory);
        using var client = Client(factory, login);
        var id = login.User!.Id;
        var tag = (await client.GetAsync($"/api/users/{id}/default-workshop")).Headers.ETag!.ToString();
        Assert.Equal((HttpStatusCode)428, (await PutAsync(client, id, null, null)).StatusCode);
        foreach (var invalid in new[] { "*", "W/" + tag, tag + "," + tag, "invalid" })
            Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(client, id, invalid, null)).StatusCode);
        foreach (var body in new[] { "{}", "[]", "{\"workshopId\":null,\"reason\":\"a\",\"role\":\"administrator\"}",
            "{\"workshopId\":null,\"reason\":\"a\",\"reason\":\"b\"}", "{\"workshopId\":null,\"reason\":\"\\ud800\"}",
            "{\"workshopId\":false,\"reason\":\"a\"}", "{\"workshopId\":null,\"reason\":\" \"}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}/default-workshop")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("If-Match", tag);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PutAsync(client, id, tag, null, new string('a', 17000))).StatusCode);
        foreach (var mediaType in new[] { "text/plain", "application/json; charset=utf-16" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}/default-workshop")
            { Content = new StringContent("{}") };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            request.Headers.TryAddWithoutValidation("If-Match", tag);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(client, id, tag, null, string.Concat(Enumerable.Repeat("🚗", 500)))).StatusCode);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, $"/api/users/{id}/default-workshop");
        preflight.Headers.Add("Origin", "https://localhost");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,if-match");
        var cors = await client.SendAsync(preflight);
        Assert.Contains("PUT", string.Join(',', cors.Headers.GetValues("Access-Control-Allow-Methods")));
    }

    [SqlFact]
    public async Task RF002_CP020_RollbackAndUnknownCommitPreserveAtomicityAndAllowReread()
    {
        foreach (var unknown in new[] { false, true })
        {
            using var factory = new AuthenticationFactory(database, configureServices: services =>
                services.AddDbContext<AuthenticationDbContext>(options => options.AddInterceptors(
                    unknown ? new AssignmentLostCommit() : new AssignmentSaveFailure())));
            var workshop = await PublishAsync(factory);
            var login = await LoginAsync(factory);
            var target = new LocalUser { Id = Guid.NewGuid(), DirectoryObjectId = Guid.NewGuid(), Username = "synthetic", DisplayName = "Synthetic" };
            await using (var db = database.CreateContext())
            {
                db.Users.Add(target);
                db.Sessions.Add(new(Guid.NewGuid(), target.Id, UserRoles.Administrator, factory.Clock.Now, factory.Clock.Now.AddHours(1)));
                await db.SaveChangesAsync();
            }
            using var client = Client(factory, login);
            var path = $"/api/users/{target.Id}/default-workshop";
            var tag = (await client.GetAsync(path)).Headers.ETag!.ToString();
            var response = await PutAsync(client, target.Id.ToString(), tag, workshop);
            Assert.Equal(unknown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, response.StatusCode);
            if (unknown) Assert.Contains("operation_outcome_unknown", await response.Content.ReadAsStringAsync());
            await using var check = database.CreateContext();
            Assert.Equal(unknown ? workshop : null, (await check.Users.SingleAsync(x => x.Id == target.Id)).DefaultWorkshopId);
            Assert.Equal(unknown ? 1 : 0, await check.AssignmentAudits.CountAsync(x => x.UserId == target.Id));
            Assert.Equal(unknown, (await check.Sessions.SingleAsync(x => x.UserId == target.Id)).ClosedAt.HasValue);
            var currentTag = (await client.GetAsync(path)).Headers.ETag!.ToString();
            Assert.Equal(!unknown, currentTag == tag);
            if (unknown)
                Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutAsync(client, target.Id.ToString(), tag, workshop)).StatusCode);
        }
    }

    [SqlFact]
    public async Task RF002_CP021_SharedCleanupRemovesOnlyEligibleAssignmentAudits()
    {
        using var factory = new AuthenticationFactory(database);
        var login = await LoginAsync(factory);
        var workshop = await PublishAsync(factory);
        var id = Guid.Parse(login.User!.Id);
        var old = new WorkshopAssignmentAuditEntry(id, id, null, workshop, "Synthetic old", DateTimeOffset.UtcNow.AddDays(-366), "test");
        var recent = new WorkshopAssignmentAuditEntry(id, id, workshop, null, "Synthetic recent", DateTimeOffset.UtcNow.AddDays(-364), "test");
        await using (var db = database.CreateContext())
        {
            db.AssignmentAudits.AddRange(old, recent);
            await db.CleanupSchedules.ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuthenticationRetentionStore>();
        await store.CleanBatchAsync("synthetic-cleaner", new(30, 24, 365, 24, 100), default);
        await using var check = database.CreateContext();
        Assert.False(await check.AssignmentAudits.AnyAsync(x => x.Id == old.Id));
        Assert.True(await check.AssignmentAudits.AnyAsync(x => x.Id == recent.Id));
        Assert.True(await check.Users.AnyAsync(x => x.Id == id));
        Assert.True(await check.Workshops.AnyAsync(x => x.Id == workshop));
    }

    [SqlFact]
    public async Task RF002_CP007_SelfChangeConcurrentWithRefreshDoesNotReactivateSession()
    {
        using var factory = new AuthenticationFactory(database);
        var workshop = await PublishAsync(factory);
        var login = await LoginAsync(factory);
        using var client = Client(factory, login);
        var id = login.User!.Id;
        var tag = (await client.GetAsync($"/api/users/{id}/default-workshop")).Headers.ETag!.ToString();
        using var refreshScope = factory.Services.CreateScope();
        var refresh = refreshScope.ServiceProvider.GetRequiredService<ISender>().Send(new RefreshSessionCommand(login.Tokens.Refresh.Value));
        var change = PutAsync(client, id, tag, workshop);
        await Task.WhenAll(refresh, change);
        Assert.Equal(HttpStatusCode.NoContent, (await change).StatusCode);
        using var checkScope = factory.Services.CreateScope();
        var sender = checkScope.ServiceProvider.GetRequiredService<ISender>();
        if ((await refresh).Value is { } renewed)
            Assert.Equal(FailureCode.SessionEnded, (await sender.Send(new RefreshSessionCommand(renewed.Tokens.Refresh.Value))).Error?.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    private sealed class AssignmentSaveFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<WorkshopAssignmentAuditEntry>().Any() == true)
                throw new InvalidOperationException("Synthetic assignment audit failure.");
            return ValueTask.FromResult(result);
        }
    }

    [SqlFact]
    public async Task RF002_CP022_AssignmentUsesConfirmedCatalogAndValidatesEvenUnchangedDestinations()
    {
        using var factory = new AuthenticationFactory(database);
        var login = await LoginAsync(factory);
        using var client = Client(factory, login);
        var target = new LocalUser { Id = Guid.NewGuid(), DirectoryObjectId = Guid.NewGuid(), Username = "synthetic", DisplayName = "Synthetic" };
        await using (var db = database.CreateContext())
        {
            db.Users.Add(target);
            await db.SaveChangesAsync();
            await db.Catalog.ExecuteDeleteAsync();
        }
        var id = target.Id.ToString();
        var tag = (await client.GetAsync($"/api/users/{id}/default-workshop")).Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PutAsync(client, id, tag, "missing")).StatusCode);
        var workshop = await PublishAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(client, id, tag, "missing")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
            await store.PublishAsync(await store.BeginObservationAsync(default), [], default);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, id, tag, workshop)).StatusCode);
        await PublishAsync(factory);
        // Source failures do not expire a previously published local copy.
        await using (var db = database.CreateContext())
        {
            var catalog = await db.Catalog.SingleAsync();
            catalog.LastPublishedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }
        var changed = await PutAsync(client, id, tag, workshop);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        tag = changed.Headers.ETag!.ToString();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
            await store.PublishAsync(await store.BeginObservationAsync(default), [], default);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(client, id, tag, workshop)).StatusCode);
        await using var check = database.CreateContext();
        Assert.Equal(1, await check.AssignmentAudits.CountAsync(x => x.UserId == target.Id));
        Assert.Equal(workshop, (await check.Users.SingleAsync(x => x.Id == target.Id)).DefaultWorkshopId);
    }

    private sealed class AssignmentLostCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<WorkshopAssignmentAuditEntry>().Any() == true)
                throw new IOException("Synthetic assignment commit acknowledgment loss.");
            return Task.CompletedTask;
        }
    }
}
