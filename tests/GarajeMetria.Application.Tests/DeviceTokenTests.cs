using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;

namespace GarajeMetria.Application.Tests;

public sealed class DeviceTokenTests
{
    [Fact]
    public async Task GrantUsesTheVerifiedStateAndSharedClockWithinTheDeviceTransaction()
    {
        var state = new State();
        var result = await state.GrantHandler().Handle(new("lpr_" + state.Id.ToString("N"), "synthetic"), default);
        Assert.True(result.Succeeded);
        Assert.Equal(state.Id, state.Scope?.DeviceId);
        Assert.Null(state.Scope?.AdministratorId);
        Assert.Equal(state.Authorization!.AuthorizationVersion, result.Value!.AuthorizationVersion);
        Assert.Equal(state.Now, result.Value.IssuedAt);
        Assert.Equal(state.Now.AddMinutes(15), result.Value.ExpiresAt);
        Assert.Equal(new[] { "begin", "secret", "state", "clock", "commit" }, state.Calls);
        Assert.False(state.InTransaction);
    }

    [Theory]
    [InlineData("secret", FailureCode.InvalidCredentials)]
    [InlineData("missing", FailureCode.InvalidCredentials)]
    [InlineData("inactive", FailureCode.InvalidCredentials)]
    [InlineData("revoked", FailureCode.InvalidCredentials)]
    [InlineData("workshop", FailureCode.InvalidCredentials)]
    [InlineData("catalog", FailureCode.ServiceUnavailable)]
    public async Task RejectedCredentialsOrAuthorizationCannotProduceAGrant(string condition, FailureCode code)
    {
        var state = new State();
        switch (condition)
        {
            case "secret": state.ValidSecret = false; break;
            case "missing": state.Authorization = null; break;
            case "inactive": state.Authorization = state.Authorization! with { IsActive = false }; break;
            case "revoked": state.Authorization = state.Authorization! with { HasCredentials = false }; break;
            case "workshop": state.Authorization = state.Authorization! with { WorkshopIsActive = false }; break;
            case "catalog": state.Authorization = state.Authorization! with { CatalogAvailable = false }; break;
        }
        var result = await state.GrantHandler().Handle(new("lpr_" + state.Id.ToString("N"), "synthetic"), default);
        Assert.Equal(code, result.Error?.Code);
        Assert.Null(result.Value);
        Assert.DoesNotContain("clock", state.Calls);
        if (condition == "secret") Assert.DoesNotContain("state", state.Calls);
    }

    [Fact]
    public async Task ExpirationIsRecheckedAfterTheAuthorizationReadFinishes()
    {
        var state = new State();
        state.Device = new(state.Id, "site", state.Authorization!.AuthorizationVersion, state.Now.AddMinutes(1), true);
        state.AdvanceDuringRead = true;
        var result = await new ValidateDeviceAccessHandler(new(state, state, state)).Handle(new(state.Id, "site"), default);
        Assert.Equal(FailureCode.InvalidDeviceToken, result.Error?.Code);
        Assert.Equal(new[] { "state", "clock" }, state.Calls);
    }

    private sealed class State : IDeviceCredentialService, IDeviceAuthorizationQuery, ILprClock, ILprUnitOfWork, ICurrentDeviceContext
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        public DeviceAuthorizationState? Authorization { get; set; }
        public CurrentDevice? Device { get; set; }
        public bool ValidSecret { get; set; } = true;
        public bool AdvanceDuringRead { get; set; }
        public bool InTransaction { get; private set; }
        public LprTransactionScope? Scope { get; private set; }
        public List<string> Calls { get; } = [];
        public State() => Authorization = new(Id, "site", true, true, Guid.NewGuid(), true, true);
        public PrepareDeviceTokenGrantHandler GrantHandler() => new(this, this, this, this, new(TimeSpan.FromMinutes(15)));
        public Task<bool> VerifyAsync(string id, string secret, CancellationToken ct)
        { Assert.True(InTransaction); Calls.Add("secret"); return Task.FromResult(ValidSecret); }
        public Task<DeviceAuthorizationState?> FindAsync(Guid id, CancellationToken ct)
        {
            Calls.Add("state");
            if (AdvanceDuringRead) Now = Now.AddMinutes(1);
            return Task.FromResult(Authorization);
        }
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct)
        { Calls.Add("clock"); return Task.FromResult(Now); }
        public async Task<T> ExecuteAsync<T>(LprTransactionScope scope, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            Scope = scope;
            InTransaction = true;
            Calls.Add("begin");
            try { var value = await action(ct); Calls.Add("commit"); return value; }
            finally { InTransaction = false; }
        }
        public Task<DeviceCredentials> CreateAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceCredentials> RotateAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
