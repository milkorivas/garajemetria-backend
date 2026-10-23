using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.IntegrationTests;

public sealed class InfrastructureFailureTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task RollbackFailureIsLoggedWithoutLosingThePrimaryFailure()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        await using var db = new AuthenticationDbContext(new DbContextOptionsBuilder<AuthenticationDbContext>()
            .UseSqlServer(database.ConnectionString).AddInterceptors(new FailedRollback()).Options);
        var original = new InvalidOperationException("private-original");
        var exception = await Assert.ThrowsAsync<PersistenceFailureException>(() => SqlTransactions.ExecuteAsync<int>(
            db, "logging-test-rollback", _ => Task.FromException<int>(original), factory.CreateLogger("RollbackTest"), default));
        Assert.Equal(FailureCode.OperationOutcomeUnknown, exception.Code);
        Assert.Same(original, exception.InnerException);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("Persistence.Rollback", entry.Properties["Operation"]);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain("private-", entry.Message);
        Assert.Null(entry.Exception);
    }

    private sealed class FailedRollback : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private-rollback");
    }

    [SqlFact]
    public async Task PersistenceErrorsUseSafeProblemDetailsWithoutCookiesOrRetryAfter()
    {
        foreach (var code in new[] { FailureCode.ServiceUnavailable, FailureCode.InternalError, FailureCode.OperationOutcomeUnknown })
        {
            using var factory = new AuthenticationFactory(database, configureServices: services =>
            {
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(_ => new UnavailableUnitOfWork(code));
            });
            using var client = factory.Client();
            await AuthenticationFactory.BootstrapAsync(client);
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "synthetic-private-password" });
            Assert.Equal(code == FailureCode.InternalError ? HttpStatusCode.InternalServerError : HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.False(response.Headers.Contains("Retry-After"));
            Assert.True(response.Headers.CacheControl?.NoStore);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic-private-password", text);
            Assert.DoesNotContain("SqlException", text);
            using var json = JsonDocument.Parse(text);
            Assert.Equal((int)response.StatusCode, json.RootElement.GetProperty("status").GetInt32());
            Assert.StartsWith("urn:uuid:", json.RootElement.GetProperty("instance").GetString());
            Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
        }
    }

    [SqlFact]
    public async Task BootstrapPairWorksAcrossInstancesSharingProtectedKeys()
    {
        using var first = new AuthenticationFactory(database);
        using var client = first.Client();
        var bootstrap = await client.GetAsync("/api/auth/antiforgery");
        using var json = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
        var cookie = bootstrap.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var second = new AuthenticationFactory(database, shared: first);
        using var otherClient = second.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        otherClient.DefaultRequestHeaders.Add("Cookie", cookie);
        otherClient.DefaultRequestHeaders.Add(AuthenticationEndpoints.CsrfHeader, json.RootElement.GetProperty("requestToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await otherClient.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "synthetic" })).StatusCode);
        await AuthenticationFactory.BootstrapAsync(client);
        for (var attempt = 0; attempt < 4; attempt++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "synthetic" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await otherClient.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "synthetic" })).StatusCode);
    }

    private sealed class UnavailableUnitOfWork(FailureCode code) : IUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
            throw new PersistenceFailureException(code);
    }

    [SqlFact]
    public async Task SessionConnectionFailureIs503RatherThanAnInvalidCredentialsResult()
    {
        using var factory = new AuthenticationFactory(database);
        using var loginScope = factory.Services.CreateScope();
        var login = (await loginScope.ServiceProvider.GetRequiredService<MediatR.ISender>()
            .Send(new LoginCommand("user", "synthetic"))).Value!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var missing = new SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = "GarajeMetria_MissingTests_" + Guid.NewGuid().ToString("N") };
        db.Database.SetConnectionString(missing.ConnectionString);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer " + login.Tokens.Access.Value;
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => context.AuthenticateAsync("Bearer"));
        Assert.Equal(FailureCode.ServiceUnavailable, failure.Code);
    }

    [SqlFact]
    public async Task LostCommitAcknowledgmentDoesNotReturnTokensOrPretendRollback()
    {
        var interceptor = new LostCommitAcknowledgment();
        using var factory = new AuthenticationFactory(database, configureServices: services =>
            services.AddDbContext<AuthenticationDbContext>(options => options.AddInterceptors(interceptor)));
        using var client = factory.Client();
        await AuthenticationFactory.BootstrapAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "user", password = "synthetic" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("operation_outcome_unknown", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(u => u.DirectoryObjectId == factory.DirectoryAdapter.Id);
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.UserId == user.Id));
        Assert.Equal(1, interceptor.Interruptions);
    }

    private sealed class LostCommitAcknowledgment : DbTransactionInterceptor
    {
        public int Interruptions { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<AuthenticationSession>().Any() == true)
            {
                Interruptions++;
                throw new IOException("Synthetic lost acknowledgment after SQL committed.");
            }
            return Task.CompletedTask;
        }
    }
}
