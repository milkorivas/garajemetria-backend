using FluentValidation;
using GarajeMetria.Application;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.Application.Tests;

public sealed class AuthenticationTests
{
    [Theory]
    [InlineData("user", true)]
    [InlineData("TEST\\user", true)]
    [InlineData("ü.ser😀", true)]
    [InlineData("user@domain", false)]
    [InlineData("user/name", false)]
    [InlineData("TEST\\\\user", false)]
    [InlineData(" TEST\\user", false)]
    [InlineData("TEST\\ user", false)]
    [InlineData("user\u2003", false)]
    [InlineData("user\n", false)]
    [InlineData("", false)]
    public void UsernameFormatsMatchTheContract(string username, bool valid) => Assert.Equal(valid, InputRules.ValidUsername(username, 256));

    [Fact]
    public async Task ValidationCountsScalarsAndPreservesPassword()
    {
        var options = Options();
        options.MaxUsernameLength = 2;
        var validator = new LoginValidator(options);
        Assert.True((await validator.ValidateAsync(new LoginCommand("a😀", " \"\\😀 "))).IsValid);
        Assert.False((await validator.ValidateAsync(new LoginCommand("a😀b", "password"))).IsValid);
        Assert.False((await validator.ValidateAsync(new LoginCommand("ab", "\0"))).IsValid);
        Assert.False((await validator.ValidateAsync(new LoginCommand("ab", "\uD800"))).IsValid);
        Assert.Equal(1, InputRules.ScalarCount("😀"));
    }

    [Fact]
    public async Task MediationRejectsInvalidInputBeforeDirectoryOrPersistence()
    {
        var fixture = new Fixture();
        using var services = fixture.Services();
        var result = await services.GetRequiredService<ISender>().Send(new LoginCommand("a@domain", "secret"));
        Assert.Equal(FailureCode.InvalidInput, result.Error?.Code);
        Assert.Equal(0, fixture.Directory.Calls);
        Assert.Empty(fixture.Store.Sessions);
    }

    [Fact]
    public async Task LoginPreservesPasswordAndNormalizesTheUserQuota()
    {
        var f = new Fixture();
        var result = await f.Login("test\\UsEr", "  😀 password  ");
        Assert.True(result.Succeeded);
        Assert.Equal("  😀 password  ", f.Directory.Password);
        Assert.Equal("TEST\\USER", f.Limits.Subjects.Single());
        Assert.Equal("administrator", result.Value!.User!.Role);
    }

    [Theory]
    [InlineData(DirectoryStatus.Rejected, FailureCode.InvalidCredentials)]
    [InlineData(DirectoryStatus.NotFound, FailureCode.InvalidCredentials)]
    [InlineData(DirectoryStatus.Unavailable, FailureCode.DirectoryUnavailable)]
    public async Task FailedLoginDoesNotReplaceSessionOrProvision(DirectoryStatus status, FailureCode code)
    {
        var f = new Fixture();
        var success = await f.Login();
        var session = f.Store.Sessions.Single().Value;
        f.Directory.Status = status;
        var result = await f.Login();
        Assert.Equal(code, result.Error?.Code);
        Assert.True(session.IsActive(f.Clock.Now));
        Assert.Single(f.Store.Sessions);
        Assert.Equal(1, f.Store.Provisioned);
        Assert.NotNull(success.Value);
    }

    [Fact]
    public async Task UnapprovedDomainAndNoRoleDoNotProvision()
    {
        var f = new Fixture();
        Assert.Equal(FailureCode.InvalidCredentials, (await f.Login("OTHER\\user")).Error?.Code);
        Assert.Equal(0, f.Directory.Calls);
        f.Directory.Admin = false;
        Assert.Equal(FailureCode.NoRole, (await f.Login()).Error?.Code);
        Assert.Equal(0, f.Store.Provisioned);
    }

    [Fact]
    public async Task PendingAdvisorIsProvisionedWithoutCreatingSession()
    {
        var f = new Fixture();
        f.Directory.Admin = false;
        f.Directory.Advisor = true;
        Assert.Equal(FailureCode.NoWorkshop, (await f.Login()).Error?.Code);
        Assert.Equal(1, f.Store.Provisioned);
        Assert.Empty(f.Store.Sessions);
    }

    [Theory]
    [InlineData(false, true, FailureCode.ServiceUnavailable)]
    [InlineData(true, false, FailureCode.InactiveWorkshop)]
    public async Task AssignedAdvisorRequiresKnownActiveWorkshop(bool available, bool active, FailureCode expected)
    {
        var f = new Fixture();
        f.Directory.Admin = false;
        f.Directory.Advisor = true;
        f.Store.User = f.Store.User with { DefaultWorkshopId = "workshop-test", CatalogAvailable = available, WorkshopActive = active };
        Assert.Equal(expected, (await f.Login()).Error?.Code);
        Assert.Empty(f.Store.Sessions);
    }

    [Fact]
    public async Task NewLoginClosesOnlyThePreviousSession()
    {
        var f = new Fixture();
        await f.Login();
        var old = f.Store.Sessions.Single().Value;
        await f.Login();
        Assert.False(old.IsActive(f.Clock.Now));
        Assert.Single(f.Store.Sessions.Values, s => s.IsActive(f.Clock.Now));
    }

    [Fact]
    public async Task RotationPreservesAbsoluteEndAndOldAccessAndReplayRevokesSuccessor()
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        var session = f.Store.Sessions.Single().Value;
        var absolute = session.AbsoluteExpiresAt;
        var renewed = await f.Refresh(login.Tokens.Refresh.Value);
        Assert.True(renewed.Succeeded);
        Assert.Equal(absolute, session.AbsoluteExpiresAt);
        Assert.True(session.IsActive(f.Clock.Now));
        Assert.NotNull(f.Tokens.Validate(login.Tokens.Access.Value, "access"));
        Assert.Equal(FailureCode.SessionEnded, (await f.Refresh(login.Tokens.Refresh.Value)).Error?.Code);
        Assert.False(session.IsActive(f.Clock.Now));
    }

    [Fact]
    public async Task ExpiredConsumedTokenDoesNotRevokeTheActiveSuccessor()
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        f.Clock.Now = f.Clock.Now.AddMinutes(20);
        var renewed = (await f.Refresh(login.Tokens.Refresh.Value)).Value!;
        f.Clock.Now = login.Tokens.Refresh.ExpiresAt;
        Assert.Equal(FailureCode.SessionEnded, (await f.Refresh(login.Tokens.Refresh.Value)).Error?.Code);
        Assert.True(f.Store.Sessions.Single().Value.IsActive(f.Clock.Now));
        Assert.True((await f.Refresh(renewed.Tokens.Refresh.Value)).Succeeded);
    }

    [Fact]
    public async Task ExpiryWhileWaitingForTransactionDoesNotRevoke()
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        await f.Refresh(login.Tokens.Refresh.Value);
        f.Store.BeforeTransaction = () => f.Clock.Now = login.Tokens.Refresh.ExpiresAt;
        Assert.Equal(FailureCode.SessionEnded, (await f.Refresh(login.Tokens.Refresh.Value)).Error?.Code);
        Assert.True(f.Store.Sessions.Single().Value.IsActive(f.Clock.Now));
    }

    [Theory]
    [InlineData(DirectoryStatus.Rejected, FailureCode.SessionEnded, false)]
    [InlineData(DirectoryStatus.NotFound, FailureCode.SessionEnded, false)]
    [InlineData(DirectoryStatus.Unavailable, FailureCode.DirectoryUnavailable, true)]
    public async Task RefreshDistinguishesDirectoryRejectionFromOutage(DirectoryStatus state, FailureCode code, bool active)
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        f.Directory.Status = state;
        Assert.Equal(code, (await f.Refresh(login.Tokens.Refresh.Value)).Error?.Code);
        Assert.Equal(active, f.Store.Sessions.Single().Value.IsActive(f.Clock.Now));
        Assert.Null(f.Store.Refreshes.Single().Value.ConsumedAt);
    }

    [Fact]
    public async Task ChangedRoleRequiresNewLogin()
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        f.Directory.Admin = false;
        f.Directory.Advisor = true;
        Assert.Equal(FailureCode.SessionEnded, (await f.Refresh(login.Tokens.Refresh.Value)).Error?.Code);
        Assert.False(f.Store.Sessions.Single().Value.IsActive(f.Clock.Now));
    }

    [Fact]
    public async Task QuotaRejectionStopsDirectoryCallsAndPreservesSession()
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        var calls = f.Directory.Calls;
        f.Limits.Allowed = false;
        Assert.Equal(FailureCode.RateLimited, (await f.Refresh(login.Tokens.Refresh.Value)).Error?.Code);
        Assert.Equal(calls, f.Directory.Calls);
        Assert.Null(f.Store.Refreshes.Single().Value.ConsumedAt);
    }

    [Fact]
    public async Task LogoutDoesNotRequireUnexpiredAccess()
    {
        var f = new Fixture();
        var login = (await f.Login()).Value!;
        f.Clock.Now = f.Clock.Now.AddMinutes(11);
        Assert.Null(f.Tokens.Validate(login.Tokens.Access.Value, "access"));
        Assert.True((await new LogoutHandler(f.Store, f.Store, f.Tokens, f.Clock)
            .Handle(new(login.Tokens.Refresh.Value), default)).Succeeded);
        Assert.False(f.Store.Sessions.Single().Value.IsActive(f.Clock.Now));
    }

    internal static AuthenticationOptions Options() => new()
    {
        DefaultDomain = "TEST",
        AllowedDomains = ["TEST"],
        MaxUsernameLength = 256,
        MaxPasswordLength = 1024,
        AccessTokenLifetimeSeconds = 600,
        RefreshTokenLifetimeSeconds = 1800,
        MaxSessionLifetimeSeconds = 28800,
        LoginUserLimit = 5,
        RefreshUserLimit = 10,
        RateLimitWindowSeconds = 60
    };

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new();
        public FakeDirectory Directory { get; } = new();
        public FakeStore Store { get; }
        public FakeLimits Limits { get; } = new();
        public FakeTokens Tokens { get; }
        public Fixture() { Store = new(Directory.Id); Tokens = new(Clock); }
        public Task<Outcome<AuthenticationResult>> Login(string username = "user", string password = "password") =>
            new LoginHandler(Directory, Store, Store, Store, Tokens, Limits, Options(), Clock).Handle(new(username, password), default);
        public Task<Outcome<AuthenticationResult>> Refresh(string token) =>
            new RefreshSessionHandler(Directory, Store, Store, Store, Tokens, Limits, Options(), Clock).Handle(new(token), default);
        public ServiceProvider Services() => new ServiceCollection().AddSingleton(Options()).AddSingleton<TimeProvider>(Clock)
            .AddSingleton<ICorporateDirectory>(Directory).AddSingleton<IUserAccessQuery>(Store).AddSingleton<IAuthenticationSessionRepository>(Store)
            .AddSingleton<IUnitOfWork>(Store).AddSingleton<ITokenService>(Tokens).AddSingleton<IAuthenticationRateLimitStore>(Limits)
            .AddApplication().BuildServiceProvider();
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeDirectory : ICorporateDirectory
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DirectoryStatus Status { get; set; } = DirectoryStatus.Valid;
        public bool Admin { get; set; } = true;
        public bool Advisor { get; set; }
        public int Calls { get; private set; }
        public string? Password { get; private set; }
        public Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
        { Password = password; return ReadAsync(Id, cancellationToken); }
        public Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new DirectoryResult(Status, new(Id, "user", "Synthetic User", Admin, Advisor))); }
    }
    private sealed class FakeLimits : IAuthenticationRateLimitStore
    {
        public bool Allowed { get; set; } = true;
        public List<string> Subjects { get; } = [];
        public Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken cancellationToken)
        { Subjects.Add(subject); return Task.FromResult(new RateDecision(Allowed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(60))); }
    }
    private sealed class FakeStore(Guid directoryId) : IUserAccessQuery, IAuthenticationSessionRepository, IUnitOfWork
    {
        public LocalIdentity User { get; set; } = new(Guid.NewGuid(), directoryId, "user", "Synthetic User", null, false, false);
        public Dictionary<Guid, AuthenticationSession> Sessions { get; } = [];
        public Dictionary<Guid, RefreshTokenRecord> Refreshes { get; } = [];
        public int Provisioned { get; private set; }
        public Action? BeforeTransaction { get; set; }
        public Task<LocalIdentity?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<LocalIdentity?>(User);
        public Task<LocalIdentity> ProvisionAsync(DirectoryIdentity identity, CancellationToken ct) { Provisioned = 1; return Task.FromResult(User); }
        Task<AuthenticationSession?> IAuthenticationSessionRepository.FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Sessions.GetValueOrDefault(id));
        public Task<AuthenticationSession?> FindOpenAsync(Guid user, CancellationToken ct) => Task.FromResult(Sessions.Values.SingleOrDefault(s => s.UserId == user && s.ClosedAt is null));
        public Task<RefreshTokenRecord?> FindRefreshAsync(Guid id, CancellationToken ct) => Task.FromResult(Refreshes.GetValueOrDefault(id));
        public void Add(AuthenticationSession session) => Sessions.Add(session.Id, session);
        public void Add(RefreshTokenRecord token) => Refreshes.Add(token.Id, token);
        public Task<T> ExecuteAsync<T>(Guid user, Func<CancellationToken, Task<T>> action, CancellationToken ct) { BeforeTransaction?.Invoke(); return action(ct); }
    }
    private sealed class FakeTokens(FakeClock clock) : ITokenService
    {
        private readonly Dictionary<string, (TokenIdentity Identity, string Use)> issued = [];
        public TokenPair Issue(AuthenticationSession session)
        {
            IssuedToken Create(string use, int lifetime)
            {
                var now = TokenTimes.FloorSecond(clock.Now);
                var expiry = TokenTimes.Expires(now, TimeSpan.FromSeconds(lifetime), session.AbsoluteExpiresAt);
                var id = Guid.NewGuid();
                var value = id.ToString();
                issued.Add(value, (new(session.UserId, session.Id, id, now, expiry, use == "access" ? session.Role : null), use));
                return new(value, id, Hash(value), now, expiry);
            }
            return new(Create("access", 600), Create("refresh", 1800));
        }
        public TokenIdentity? Validate(string token, string use) => issued.TryGetValue(token, out var item) && item.Use == use &&
            clock.Now >= item.Identity.IssuedAt && clock.Now < item.Identity.ExpiresAt ? item.Identity : null;
        public string Hash(string token) => token.Replace("-", "").PadRight(64, '0');
    }
}
