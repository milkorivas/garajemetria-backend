using System.Net;
using System.Net.Http.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Workshops;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GarajeMetria.IntegrationTests;

public sealed class UserAssignmentConcurrencyTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task RF002_CP007_LoginConcurrentWithAssignmentUsesNewStateOrLosesItsSession()
    {
        using var factory = new AuthenticationFactory(database, configureServices: services =>
        {
            services.RemoveAll<ICorporateDirectory>();
            services.AddSingleton<ICorporateDirectory>(new DualDirectory());
        });
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        await store.PublishAsync(await store.BeginObservationAsync(default), [new("0001", "Synthetic Workshop")], default);
        await using var db = database.CreateContext();
        var workshop = (await db.Workshops.SingleAsync(x => x.Code == "0001")).Id;
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var actor = (await sender.Send(new LoginCommand("user", "synthetic"))).Value!;
        var target = (await sender.Send(new LoginCommand("target", "synthetic"))).Value!;
        using var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Tokens.Access.Value);
        var id = target.User!.Id;
        var tag = (await client.GetAsync($"/api/users/{id}/default-workshop")).Headers.ETag!;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}/default-workshop")
        { Content = JsonContent.Create(new { workshopId = workshop, reason = "Synthetic assignment" }) };
        request.Headers.IfMatch.Add(tag);
        using var loginScope = factory.Services.CreateScope();
        var login = loginScope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("target", "synthetic"));
        var change = client.SendAsync(request);
        await Task.WhenAll(login, change);
        Assert.Equal(HttpStatusCode.NoContent, (await change).StatusCode);
        var concurrent = (await login).Value!;
        using var targetClient = factory.Client();
        targetClient.DefaultRequestHeaders.Authorization = new("Bearer", concurrent.Tokens.Access.Value);
        Assert.Equal(concurrent.User!.DefaultWorkshopId is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            (await targetClient.GetAsync("/api/users")).StatusCode);
        targetClient.DefaultRequestHeaders.Authorization = new("Bearer", target.Tokens.Access.Value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/users")).StatusCode);
    }

    private sealed class DualDirectory : ICorporateDirectory
    {
        private readonly DirectoryIdentity actor = new(Guid.NewGuid(), "user", "Synthetic Admin", true, false);
        private readonly DirectoryIdentity target = new(Guid.NewGuid(), "target", "Synthetic Target", true, false);
        public Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult(new DirectoryResult(DirectoryStatus.Valid, username == "target" ? target : actor));
        public Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken) =>
            Task.FromResult(new DirectoryResult(DirectoryStatus.Valid, objectId == target.ObjectId ? target : actor));
    }
}
