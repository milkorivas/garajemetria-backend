using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Lpr;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.Application.Tests;

public sealed class DeviceAdministrationTests
{
    [Theory]
    [InlineData(WorkshopAvailability.CatalogUnavailable, FailureCode.ServiceUnavailable)]
    [InlineData(WorkshopAvailability.Missing, FailureCode.ResourceNotFound)]
    [InlineData(WorkshopAvailability.Inactive, FailureCode.DeviceConflict)]
    public async Task RegistrationRejectsUnavailableWorkshopBeforeGeneratingCredentials(WorkshopAvailability status, FailureCode expected)
    {
        var state = new State { Availability = status };
        using var provider = state.Provider();
        Assert.Equal(expected, (await provider.GetRequiredService<ISender>().Send(new RegisterDeviceCommand("Camera", "site"))).Error?.Code);
        Assert.Equal(0, state.CredentialChanges);
        Assert.Empty(state.Audits);
    }

    [Theory]
    [InlineData(UserRoles.Advisor, FailureCode.Forbidden)]
    [InlineData(null, FailureCode.SessionEnded)]
    public async Task UnauthorizedActorCannotDiscoverDevices(string? role, FailureCode expected)
    {
        var state = new State(role);
        using var provider = state.Provider();
        Assert.Equal(expected, (await provider.GetRequiredService<ISender>().Send(new GetDeviceQuery(state.Device.Id))).Error?.Code);
        Assert.Equal(0, state.Reads);
        Assert.Equal(0, state.QuotaCalls);
    }

    [Fact]
    public async Task SessionIsRecheckedAfterEnteringTheTransaction()
    {
        var state = new State { ExpireInsideTransaction = true };
        using var provider = state.Provider();
        var result = await provider.GetRequiredService<ISender>().Send(new RenameDeviceCommand(state.Device.Id, "Other", state.Device.Version));
        Assert.Equal(FailureCode.SessionEnded, result.Error?.Code);
        Assert.Equal(0, state.Reads);
        Assert.Equal(0, state.CredentialChanges);
        Assert.Empty(state.Audits);
    }

    [Fact]
    public async Task VersionIsCheckedBeforeNoOpAndCredentialChanges()
    {
        var state = new State();
        using var provider = state.Provider();
        var sender = provider.GetRequiredService<ISender>();
        Assert.Equal(FailureCode.PreconditionFailed, (await sender.Send(new RenameDeviceCommand(state.Device.Id, state.Device.Name, Guid.NewGuid()))).Error?.Code);
        var before = state.Device.Snapshot();
        Assert.True((await sender.Send(new RenameDeviceCommand(state.Device.Id, " Camera ", before.Version))).Succeeded);
        Assert.Equal(before, state.Device.Snapshot());
        Assert.Empty(state.Audits);
        Assert.Equal(0, state.CredentialChanges);
        state.LoseWrite = true;
        Assert.Equal(FailureCode.PreconditionFailed, (await sender.Send(new RotateDeviceCredentialsCommand(state.Device.Id, before.Version))).Error?.Code);
        Assert.Equal(0, state.CredentialChanges);
        Assert.Empty(state.Audits);
    }

    [Fact]
    public async Task LifecycleAuditsEveryEffectiveTransitionAndPreservesWorkshop()
    {
        var state = new State();
        using var provider = state.Provider();
        var sender = provider.GetRequiredService<ISender>();
        var original = state.Device.Snapshot();
        Assert.True((await sender.Send(new RenameDeviceCommand(state.Device.Id, "New camera", state.Device.Version))).Succeeded);
        Assert.Equal(original.AuthorizationVersion, state.Device.AuthorizationVersion);
        Assert.True((await sender.Send(new RevokeDeviceCredentialsCommand(state.Device.Id, state.Device.Version))).Succeeded);
        Assert.False(state.Device.HasCredentials);
        Assert.True((await sender.Send(new RevokeDeviceCredentialsCommand(state.Device.Id, state.Device.Version))).Succeeded);
        var rotated = await sender.Send(new RotateDeviceCredentialsCommand(state.Device.Id, state.Device.Version));
        Assert.NotNull(rotated.Value!.Credentials);
        Assert.True(state.Device.HasCredentials);
        Assert.True((await sender.Send(new DeactivateDeviceCommand(state.Device.Id, state.Device.Version))).Succeeded);
        Assert.False(state.Device.IsActive);
        Assert.True((await sender.Send(new DeactivateDeviceCommand(state.Device.Id, state.Device.Version))).Succeeded);
        Assert.Equal(FailureCode.DeviceConflict, (await sender.Send(new RotateDeviceCredentialsCommand(state.Device.Id, state.Device.Version))).Error?.Code);
        var reactivated = await sender.Send(new ReactivateDeviceCommand(state.Device.Id, state.Device.Version));
        Assert.True(reactivated.Succeeded);
        Assert.NotNull(reactivated.Value!.Credentials);
        Assert.Equal(original.WorkshopId, reactivated.Value.Device.WorkshopId);
        Assert.NotEqual(original.AuthorizationVersion, state.Device.AuthorizationVersion);
        Assert.Equal(FailureCode.DeviceConflict, (await sender.Send(new ReactivateDeviceCommand(state.Device.Id, state.Device.Version))).Error?.Code);
        Assert.Equal(5, state.Audits.Count);
        Assert.Equal(4, state.CredentialChanges);
        Assert.All(state.Audits, audit => Assert.Equal(state.User!.Id, audit.ActorId));
    }

    [Fact]
    public async Task InactiveWorkshopAllowsRenameDeactivateAndRevokeButNotRotation()
    {
        var state = new State { Availability = WorkshopAvailability.Inactive };
        using var provider = state.Provider();
        var sender = provider.GetRequiredService<ISender>();
        Assert.Equal(FailureCode.DeviceConflict, (await sender.Send(new RotateDeviceCredentialsCommand(state.Device.Id, state.Device.Version))).Error?.Code);
        Assert.True((await sender.Send(new RenameDeviceCommand(state.Device.Id, "Other", state.Device.Version))).Succeeded);
        Assert.True((await sender.Send(new RevokeDeviceCredentialsCommand(state.Device.Id, state.Device.Version))).Succeeded);
        Assert.True((await sender.Send(new DeactivateDeviceCommand(state.Device.Id, state.Device.Version))).Succeeded);
        Assert.Equal(FailureCode.DeviceConflict, (await sender.Send(new ReactivateDeviceCommand(state.Device.Id, state.Device.Version))).Error?.Code);
    }

    [Fact]
    public async Task ExhaustedQuotaPrecedesBusinessTransactionAndReturnsRetryAfter()
    {
        var state = new State { AllowQuota = false };
        using var provider = state.Provider();
        var result = await provider.GetRequiredService<ISender>().Send(new RegisterDeviceCommand("Camera", "site"));
        Assert.Equal(FailureCode.RateLimited, result.Error?.Code);
        Assert.Equal(2, result.Error?.RetryAfterSeconds);
        Assert.Equal(0, state.Transactions);
        Assert.Equal(0, state.CredentialChanges);
    }

    private sealed class State : TimeProvider, ICurrentUserContext, IAuthenticationSessionRepository, ILprDeviceRepository,
        ILprDeviceAdministrationQuery, IWorkshopQuery, IDeviceCredentialService, IDeviceAdministrationAuditRepository, ILprUnitOfWork, ILprClock, ILprRateLimitStore
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
        public CurrentUser? User { get; }
        public string CorrelationId => "synthetic-correlation";
        public AuthenticationSession Session { get; }
        public LprDevice Device { get; private set; } = new(Guid.NewGuid(), "Camera", "site", true, DateTimeOffset.UnixEpoch);
        public WorkshopAvailability Availability { get; set; } = WorkshopAvailability.Active;
        public bool ExpireInsideTransaction { get; set; }
        public bool LoseWrite { get; set; }
        public bool AllowQuota { get; set; } = true;
        public int Reads { get; private set; }
        public int CredentialChanges { get; private set; }
        public int Transactions { get; private set; }
        public int QuotaCalls { get; private set; }
        public List<DeviceAdministrationAuditEntry> Audits { get; } = [];
        public State(string? role = UserRoles.Administrator)
        {
            Session = new(Guid.NewGuid(), Guid.NewGuid(), role ?? UserRoles.Administrator, Now, Now.AddHours(1));
            User = role is null ? null : new(Session.UserId, Session.Id, role);
        }
        public ServiceProvider Provider() => new ServiceCollection().AddSingleton<TimeProvider>(this)
            .AddSingleton<ICurrentUserContext>(this).AddSingleton<IAuthenticationSessionRepository>(this)
            .AddSingleton<ILprDeviceRepository>(this).AddSingleton<ILprDeviceAdministrationQuery>(this)
            .AddSingleton<IWorkshopQuery>(this).AddSingleton<IDeviceCredentialService>(this)
            .AddSingleton<IDeviceAdministrationAuditRepository>(this).AddSingleton<ILprUnitOfWork>(this)
            .AddSingleton<ILprClock>(this).AddSingleton<ILprRateLimitStore>(this).AddSingleton(new LprOptions())
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)).AddApplication().BuildServiceProvider();
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(Now);
        public Task<AuthenticationSession?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<AuthenticationSession?>(Session);
        Task<LprDevice?> ILprDeviceRepository.FindAsync(Guid id, CancellationToken ct) { Reads++; return Task.FromResult<LprDevice?>(Device); }
        public void Add(LprDevice device) => Device = device;
        public Task<bool> SaveAsync(LprDevice device, Guid version, CancellationToken ct) => Task.FromResult(!LoseWrite);
        public Task<WorkshopAvailability> CheckAsync(string id, CancellationToken ct) => Task.FromResult(Availability);
        public void Add(DeviceAdministrationAuditEntry entry) => Audits.Add(entry);
        public Task<DeviceCredentials> CreateAsync(Guid id, CancellationToken ct) => RotateAsync(id, ct);
        public Task<DeviceCredentials> RotateAsync(Guid id, CancellationToken ct) { CredentialChanges++; return Task.FromResult(new DeviceCredentials("synthetic-client", "synthetic-secret-" + CredentialChanges, "/connect/token", "lpr.events.write")); }
        public Task RevokeAsync(Guid id, CancellationToken ct) { CredentialChanges++; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string clientId, string clientSecret, CancellationToken ct) => throw new NotSupportedException();
        public Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken ct) { QuotaCalls++; return Task.FromResult(new RateDecision(AllowQuota, Now, Now.AddMilliseconds(1100))); }
        public Task<T> ExecuteAsync<T>(LprTransactionScope scope, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            Transactions++;
            if (ExpireInsideTransaction) Session.Close(Now, "test");
            return action(ct);
        }
        Task<DeviceDto?> ILprDeviceAdministrationQuery.FindAsync(Guid id, CancellationToken ct) { Reads++; return Task.FromResult<DeviceDto?>(Dto()); }
        public Task<DevicePage> ListAsync(int page, int size, string? workshop, bool? active, CancellationToken ct) { Reads++; return Task.FromResult(new DevicePage([Dto()], page, size, 1)); }
        private DeviceDto Dto() => new(Device.Id, Device.Name, Device.WorkshopId, Device.IsActive, Device.HasCredentials, Device.CreatedAt, Device.UpdatedAt, Device.Version);
        public Task<AuthenticationSession?> FindOpenAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<RefreshTokenRecord?> FindRefreshAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Add(AuthenticationSession session) => throw new NotSupportedException();
        public void Add(RefreshTokenRecord token) => throw new NotSupportedException();
    }
}
