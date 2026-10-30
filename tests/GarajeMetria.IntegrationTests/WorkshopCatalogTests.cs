using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Workshops;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Workshops;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.IntegrationTests;

public sealed class WorkshopCatalogTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    private SqlWorkshopCatalogStore Store(AuthenticationDbContext db) => new(db, NullLogger<SqlWorkshopCatalogStore>.Instance);

    [SqlFact]
    public async Task EnabledSqlSourcePublishesThirtySyntheticWorkshopsAtStartup()
    {
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.Workshops (Code nvarchar(12) NOT NULL PRIMARY KEY, Name nvarchar(256) NOT NULL, IsActive bit NOT NULL)");
            await db.Database.ExecuteSqlRawAsync("CREATE VIEW dbo.ActiveWorkshops AS SELECT Code, Name FROM dbo.Workshops WHERE IsActive = 1");
            await db.Database.ExecuteSqlRawAsync("WITH N AS (SELECT 1 AS Id UNION ALL SELECT Id + 1 FROM N WHERE Id < 30) INSERT dbo.Workshops SELECT RIGHT('000000' + CONVERT(varchar(6), Id), 6), N'Taller de prueba ' + CONVERT(nvarchar(2), Id), 1 FROM N");
        }
        using var factory = new AuthenticationFactory(database, enableWorkshopSource: true);
        _ = factory.Services;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var scope = factory.Services.CreateScope();
            var page = await scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>().ListAsync(1, 100, true, timeout.Token);
            if (page is { TotalCount: 30 } && page.Items.Any(x => x.Code == "000001"))
            {
                Assert.Equal(30, page.Items.Count);
                Assert.All(page.Items, x => Assert.StartsWith("Taller de prueba", x.Name));
                break;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    [SqlFact]
    public async Task MissingInitialCopyDiffersFromConfirmedEmptyCopy()
    {
        await using var db = database.CreateContext();
        await db.Catalog.ExecuteDeleteAsync();
        var store = Store(db);
        Assert.Null(await store.ListAsync(1, 20, null, default));
        var observation = await store.BeginObservationAsync(default);
        await store.PublishAsync(observation, [], default);
        var page = await store.ListAsync(1, 20, true, default);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [SqlFact]
    public async Task DeactivationConcurrentWithLoginInvalidatesTokensAndReactivationAllowsNewSession()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        var observation = await store.BeginObservationAsync(default);
        await store.PublishAsync(observation, [new("00001", "Example")], default);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var adminLogin = (await sender.Send(new LoginCommand("user", "synthetic"))).Value!;
        var userId = Guid.Parse(adminLogin.User!.Id);
        await using (var db = database.CreateContext())
        {
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.DefaultWorkshopId = (await db.Workshops.SingleAsync(x => x.Code == "00001")).Id;
            await db.SaveChangesAsync();
        }
        factory.DirectoryAdapter.Admin = false;
        factory.DirectoryAdapter.Advisor = true;
        var login = (await sender.Send(new LoginCommand("user", "synthetic"))).Value!;
        var next = await store.BeginObservationAsync(default);
        using var publishScope = factory.Services.CreateScope();
        using var loginScope = factory.Services.CreateScope();
        await Task.WhenAll(
            publishScope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>().PublishAsync(next, [], default),
            loginScope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic")));
        using var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Tokens.Access.Value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workshops")).StatusCode);
        using var checkScope = factory.Services.CreateScope();
        var checkSender = checkScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(FailureCode.SessionEnded, (await checkSender.Send(new RefreshSessionCommand(login.Tokens.Refresh.Value))).Error?.Code);
        Assert.Equal(FailureCode.InactiveWorkshop, (await checkSender.Send(new LoginCommand("user", "synthetic"))).Error?.Code);
        await using (var db = database.CreateContext())
            Assert.False(await db.Sessions.AnyAsync(x => x.UserId == userId && x.Role == UserRoles.Advisor && x.ClosedAt == null));
        // Login rate window elapsed; a stale catalog remains usable without a source read.
        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);
        var reactivate = await store.BeginObservationAsync(default);
        await store.PublishAsync(reactivate, [new("00001", "Reactivated")], default);
        var renewed = await checkSender.Send(new LoginCommand("user", "synthetic"));
        Assert.True(renewed.Succeeded);
        Assert.Equal(FailureCode.SessionEnded, (await checkSender.Send(new RefreshSessionCommand(login.Tokens.Refresh.Value))).Error?.Code);
    }

    [SqlFact]
    public async Task PublicationPreservesIdentityClosesOnlyAdvisorsAndRejectsOlderObservations()
    {
        await using var db = database.CreateContext();
        var store = Store(db);
        var first = await store.BeginObservationAsync(default);
        await store.PublishAsync(first, [new("00001", "Original")], default);
        var workshop = await db.Workshops.SingleAsync(x => x.Code == "00001");
        var id = workshop.Id;
        var advisor = new LocalUser { Id = Guid.NewGuid(), DirectoryObjectId = Guid.NewGuid(), DefaultWorkshopId = id };
        var admin = new LocalUser { Id = Guid.NewGuid(), DirectoryObjectId = Guid.NewGuid(), DefaultWorkshopId = id };
        db.Users.AddRange(advisor, admin);
        var now = DateTimeOffset.UtcNow;
        var advisorSession = new AuthenticationSession(Guid.NewGuid(), advisor.Id, UserRoles.Advisor, now, now.AddHours(1));
        var adminSession = new AuthenticationSession(Guid.NewGuid(), admin.Id, UserRoles.Administrator, now, now.AddHours(1));
        db.Sessions.AddRange(advisorSession, adminSession);
        await db.SaveChangesAsync();
        var old = await store.BeginObservationAsync(default);
        var latest = await store.BeginObservationAsync(default);
        var result = await store.PublishAsync(latest, [], default);
        Assert.Equal(1, result.Deactivated);
        Assert.False((await store.PublishAsync(old, [new("00001", "Stale")], default)).Published);
        db.ChangeTracker.Clear();
        Assert.False((await db.Workshops.SingleAsync(x => x.Id == id)).IsActive);
        Assert.NotNull((await db.Sessions.SingleAsync(x => x.Id == advisorSession.Id)).ClosedAt);
        Assert.Null((await db.Sessions.SingleAsync(x => x.Id == adminSession.Id)).ClosedAt);
        Assert.Equal(id, (await db.Users.SingleAsync(x => x.Id == advisor.Id)).DefaultWorkshopId);
        var reactivation = await store.BeginObservationAsync(default);
        Assert.Equal(1, (await store.PublishAsync(reactivation, [new("00001", "Renamed")], default)).Reactivated);
        db.ChangeTracker.Clear();
        workshop = await db.Workshops.SingleAsync(x => x.Code == "00001");
        Assert.Equal(id, workshop.Id);
        Assert.Equal("Renamed", workshop.Name);
        Assert.NotNull((await db.Sessions.SingleAsync(x => x.Id == advisorSession.Id)).ClosedAt);
    }

    [SqlFact]
    public async Task IndependentPublishersKeepTheNewestObservation()
    {
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var first = Store(firstDb);
        var second = Store(secondDb);
        var older = await first.BeginObservationAsync(default);
        var newer = await second.BeginObservationAsync(default);
        await Task.WhenAll(first.PublishAsync(older, [new("00001", "Older")], default),
            second.PublishAsync(newer, [new("00001", "Newest")], default));
        await using var check = database.CreateContext();
        Assert.Equal("Newest", (await check.Workshops.SingleAsync(x => x.Code == "00001")).Name);
    }

    [SqlFact]
    public async Task InvalidPublicationRetainsPreviousCatalogAndTimestamp()
    {
        await using var db = database.CreateContext();
        var store = Store(db);
        var original = await store.BeginObservationAsync(default);
        var published = await store.PublishAsync(original, [new("00001", "Valid")], default);
        var next = await store.BeginObservationAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(next,
            [new("00001", "One"), new("00001", "Two")], default));
        Assert.Equal(published.LastPublishedAt, await store.LastPublishedAsync(default));
        Assert.Equal("Valid", (await db.Workshops.SingleAsync(x => x.Code == "00001")).Name);
    }

    [SqlFact]
    public async Task HttpCatalogRequiresAdministratorAndValidPagination()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        var observation = await store.BeginObservationAsync(default);
        await store.PublishAsync(observation, [new("00001", "Example One"), new("00002", "Example Two")], default);
        using var client = factory.Client();
        var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using (var schema = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync()))
        {
            var operation = schema.RootElement.GetProperty("paths").GetProperty("/api/workshops").GetProperty("get");
            Assert.Equal(3, operation.GetProperty("parameters").GetArrayLength());
            Assert.True(operation.TryGetProperty("security", out _));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workshops")).StatusCode);
        var login = (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Tokens.Access.Value);
        var response = await client.GetAsync("/api/workshops?pageSize=1&isActive=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(2, json.RootElement.GetProperty("totalCount").GetInt32());
        foreach (var query in new[] { "pageSize=101", "pageNumber=0", "pageNumber=2147483647&pageSize=100", "isActive=bad", "pageSize=1&pageSize=2" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/workshops?" + query)).StatusCode);
        await using (var db = database.CreateContext())
        {
            var user = await db.Users.SingleAsync(x => x.Id == Guid.Parse(login.User!.Id));
            user.DefaultWorkshopId = (await db.Workshops.FirstAsync()).Id;
            await db.SaveChangesAsync();
        }
        factory.DirectoryAdapter.Admin = false;
        factory.DirectoryAdapter.Advisor = true;
        using var advisorScope = factory.Services.CreateScope();
        var advisor = (await advisorScope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", advisor.Tokens.Access.Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workshops")).StatusCode);
    }
}
