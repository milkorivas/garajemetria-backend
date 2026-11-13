using FluentValidation;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Lpr;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.Application.Tests;

public sealed class LprContractsTests
{
    [Fact]
    public async Task EveryRequestValidatorIsRegisteredAndInvalidInputStopsBeforeAuthorizationOrHandler()
    {
        var state = new State();
        using var provider = state.Services().BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        object[] invalid =
        [
            new RegisterDeviceCommand(" ", "site"),
            new RenameDeviceCommand(state.Id, "Camera", Guid.Empty),
            new DeactivateDeviceCommand(Guid.Empty, Guid.NewGuid()),
            new ReactivateDeviceCommand(state.Id, Guid.Empty),
            new RotateDeviceCredentialsCommand(Guid.Empty, Guid.NewGuid()),
            new RevokeDeviceCredentialsCommand(state.Id, Guid.Empty),
            new ListDevicesQuery(int.MaxValue, 100),
            new GetDeviceQuery(Guid.Empty),
            state.Command with { Confidence = 0.000000001m },
            new PrepareDeviceTokenGrantCommand("bad", "private-secret"),
            new ValidateDeviceAccessQuery(Guid.Empty, "site")
        ];
        foreach (var request in invalid)
        {
            var result = Assert.IsAssignableFrom<IOutcome>(await sender.Send(request));
            Assert.False(result.Succeeded);
            Assert.Equal(FailureCode.InvalidInput, result.Error?.Code);
        }
        Assert.Equal(0, state.SessionReads);
        Assert.Equal(0, state.AuthorizationReads);
        Assert.Equal(0, state.HandlerCalls);
    }

    [Theory]
    [InlineData(UserRoles.Advisor, FailureCode.Forbidden)]
    [InlineData(null, FailureCode.SessionEnded)]
    public async Task UnauthorizedAdministratorsCannotDiscoverWhetherADeviceExists(string? role, FailureCode expected)
    {
        var state = new State(role);
        using var provider = state.Services().BuildServiceProvider();
        var result = await provider.GetRequiredService<ISender>().Send(new GetDeviceQuery(state.Id));
        Assert.Equal(expected, result.Error?.Code);
        Assert.Equal(0, state.HandlerCalls);
    }

    [Fact]
    public async Task ActiveAdministratorReachesTheQueryAndExpiredSessionDoesNot()
    {
        var state = new State();
        using var provider = state.Services().BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new GetDeviceQuery(state.Id))).Succeeded);
        Assert.Equal(1, state.HandlerCalls);
        state.Session.Close(state.Now, "test");
        Assert.Equal(FailureCode.SessionEnded, (await sender.Send(new GetDeviceQuery(state.Id))).Error?.Code);
        Assert.Equal(1, state.HandlerCalls);
    }

    [Fact]
    public async Task DeviceIdentityAndScopeAreCheckedBeforeReadingAuthorizationOrEvents()
    {
        var state = new State();
        using var provider = state.Services().BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        Assert.Equal(FailureCode.DeviceForbidden, (await sender.Send(state.Command with { DeviceId = Guid.NewGuid() })).Error?.Code);
        Assert.Equal(FailureCode.DeviceForbidden, (await sender.Send(state.Command with { WorkshopId = "SITE" })).Error?.Code);
        state.Device = state.Device! with { HasEventWriteScope = false };
        Assert.Equal(FailureCode.DeviceForbidden, (await sender.Send(state.Command)).Error?.Code);
        state.Device = null;
        Assert.Equal(FailureCode.InvalidDeviceToken, (await sender.Send(state.Command)).Error?.Code);
        Assert.Equal(0, state.AuthorizationReads);
        Assert.Equal(0, state.HandlerCalls);
    }

    [Theory]
    [InlineData("expired", FailureCode.InvalidDeviceToken)]
    [InlineData("missing", FailureCode.InvalidDeviceToken)]
    [InlineData("inactive", FailureCode.InvalidDeviceToken)]
    [InlineData("revoked", FailureCode.InvalidDeviceToken)]
    [InlineData("version", FailureCode.InvalidDeviceToken)]
    [InlineData("workshop", FailureCode.InvalidDeviceToken)]
    [InlineData("catalog", FailureCode.ServiceUnavailable)]
    [InlineData("inactive-workshop", FailureCode.InactiveWorkshop)]
    public async Task CurrentAuthorizationFailuresNeverReachTheEventHandler(string condition, FailureCode expected)
    {
        var state = new State();
        switch (condition)
        {
            case "expired": state.Device = state.Device! with { ExpiresAt = state.Now }; break;
            case "missing": state.Authorization = null; break;
            case "inactive": state.Authorization = state.Authorization! with { IsActive = false }; break;
            case "revoked": state.Authorization = state.Authorization! with { HasCredentials = false }; break;
            case "version": state.Authorization = state.Authorization! with { AuthorizationVersion = Guid.NewGuid(), WorkshopIsActive = false }; break;
            case "workshop": state.Authorization = state.Authorization! with { WorkshopId = "other" }; break;
            case "catalog": state.Authorization = state.Authorization! with { CatalogAvailable = false }; break;
            case "inactive-workshop": state.Authorization = state.Authorization! with { WorkshopIsActive = false }; break;
        }
        using var provider = state.Services().BuildServiceProvider();
        Assert.Equal(expected, (await provider.GetRequiredService<ISender>().Send(state.Command)).Error?.Code);
        Assert.Equal(0, state.HandlerCalls);
    }

    [Fact]
    public async Task AuthorizedDeviceReachesHandlerWithCancellationAndWithoutCorporateSessionReads()
    {
        var state = new State();
        using var provider = state.Services().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        Assert.True((await provider.GetRequiredService<ISender>().Send(state.Command, cancellation.Token)).Succeeded);
        Assert.Equal(cancellation.Token, state.LastCancellation);
        Assert.Equal(1, state.AuthorizationReads);
        Assert.Equal(1, state.HandlerCalls);
        Assert.Equal(0, state.SessionReads);
    }

    [Fact]
    public async Task ValidationAwaitsAsynchronousRulesBeforeAnyAuthorizationOrEffects()
    {
        var state = new State();
        var validator = new AsyncValidator();
        using var provider = state.Services().AddSingleton<IValidator<ReceiveLprEventCommand>>(validator).BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var result = await provider.GetRequiredService<ISender>().Send(state.Command, cancellation.Token);
        Assert.Equal(FailureCode.InvalidInput, result.Error?.Code);
        Assert.Equal(cancellation.Token, validator.Cancellation);
        Assert.Equal(0, state.AuthorizationReads);
        Assert.Equal(0, state.HandlerCalls);
    }

    [Fact]
    public async Task ValidatorsEnforceTextPrecisionAndPaginationWithoutRejectingInvalidPlateFormatOrOffsets()
    {
        var state = new State();
        var validator = new ReceiveLprEventValidator();
        Assert.True((await validator.ValidateAsync(state.Command with { PlateOriginal = "synthetic-invalid", Confidence = 0.00000001m })).IsValid);
        Assert.True((await validator.ValidateAsync(state.Command with { CapturedAt = state.Now.ToOffset(TimeSpan.FromHours(1)), Confidence = 0.1234567800m })).IsValid);
        foreach (var text in new[] { "", " - ", "text\n", new string((char)0xd800, 1), new string('x', 33) })
            Assert.False((await validator.ValidateAsync(state.Command with { PlateOriginal = text })).IsValid);
        foreach (var confidence in new[] { -0.1m, 1.1m, 0.000000001m })
            Assert.False((await validator.ValidateAsync(state.Command with { Confidence = confidence })).IsValid);
        Assert.False((await validator.ValidateAsync(state.Command with { EventId = Guid.AllBitsSet })).IsValid);
        Assert.False((await validator.ValidateAsync(state.Command with { Direction = (LprDirection)99 })).IsValid);
        Assert.False((await validator.ValidateAsync(state.Command with { WorkshopId = new string('x', 257) })).IsValid);
        Assert.True((await new RegisterDeviceValidator().ValidateAsync(new RegisterDeviceCommand(" Camera ", "site"))).IsValid);
        Assert.False((await new RegisterDeviceValidator().ValidateAsync(new RegisterDeviceCommand("Camera\n", "site"))).IsValid);
        Assert.True((await new ListDevicesValidator().ValidateAsync(new ListDevicesQuery(int.MaxValue, 1))).IsValid);
        Assert.False((await new ListDevicesValidator().ValidateAsync(new ListDevicesQuery(1, 101))).IsValid);
        Assert.True((await new PrepareDeviceTokenGrantValidator().ValidateAsync(new PrepareDeviceTokenGrantCommand("lpr_" + state.Id.ToString("N"), "synthetic-secret"))).IsValid);
    }

    [Fact]
    public async Task SensitiveRequestsAndResponsesNeverIncludePayloadOrSecretsInToStringOrBehaviorLogs()
    {
        var state = new State();
        const string secret = "synthetic-private-secret";
        var grant = new PrepareDeviceTokenGrantCommand("lpr_" + state.Id.ToString("N"), secret);
        var credentials = new DeviceCredentials(grant.ClientId, secret, "/connect/token", "lpr.events.write");
        var result = Outcome<DeviceRotationResult>.Success(new(credentials, Guid.NewGuid()));
        var logger = new CaptureLogger<LoggingBehavior<PrepareDeviceTokenGrantCommand, Outcome<DeviceRotationResult>>>();
        await new LoggingBehavior<PrepareDeviceTokenGrantCommand, Outcome<DeviceRotationResult>>(logger)
            .Handle(grant, _ => Task.FromResult(result), default);
        var receiveLogger = new CaptureLogger<LoggingBehavior<ReceiveLprEventCommand, Outcome<ReceiveLprEventResult>>>();
        await new LoggingBehavior<ReceiveLprEventCommand, Outcome<ReceiveLprEventResult>>(receiveLogger)
            .Handle(state.Command, _ => Task.FromResult(Outcome<ReceiveLprEventResult>.Fail(FailureCode.InvalidInput)), default);
        var text = string.Join(' ', logger.Messages.Concat(receiveLogger.Messages)) + grant + state.Command + credentials + result;
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain(state.Command.PlateOriginal, text);
    }

    private sealed class AsyncValidator : AbstractValidator<ReceiveLprEventCommand>
    {
        public CancellationToken Cancellation { get; private set; }
        public AsyncValidator() => RuleFor(x => x.EventId).MustAsync(async (_, ct) =>
        {
            await Task.Yield();
            Cancellation = ct;
            return false;
        });
    }

    [Fact]
    public async Task IdenticalDuplicateRequestsConsumeDeviceQuotaAndRejectionPrecedesTheHandler()
    {
        var state = new State { Duplicate = true };
        using var provider = state.Services().BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        Assert.True((await sender.Send(state.Command)).Value!.Duplicate);
        Assert.True((await sender.Send(state.Command)).Value!.Duplicate);
        state.AllowQuota = false;
        var rejected = await sender.Send(state.Command);
        Assert.Equal(FailureCode.RateLimited, rejected.Error?.Code);
        Assert.Equal(2, rejected.Error?.RetryAfterSeconds);
        Assert.Equal(3, state.QuotaCalls);
        Assert.Equal(2, state.HandlerCalls);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, error));
    }

    private sealed class State : TimeProvider, ICurrentUserContext, ICurrentDeviceContext,
        IAuthenticationSessionRepository, IDeviceAuthorizationQuery, ILprClock, ILprRateLimitStore
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch;
        public CurrentUser? User { get; }
        public string CorrelationId => "synthetic-correlation";
        public CurrentDevice? Device { get; set; }
        public DeviceAuthorizationState? Authorization { get; set; }
        public AuthenticationSession Session { get; }
        public ReceiveLprEventCommand Command { get; }
        public int SessionReads { get; private set; }
        public int AuthorizationReads { get; private set; }
        public int HandlerCalls { get; set; }
        public bool Duplicate { get; set; }
        public bool AllowQuota { get; set; } = true;
        public int QuotaCalls { get; private set; }
        public CancellationToken LastCancellation { get; private set; }

        public State(string? role = UserRoles.Administrator)
        {
            Session = new(Guid.NewGuid(), Guid.NewGuid(), role ?? UserRoles.Administrator, Now, Now.AddHours(1));
            User = role is null ? null : new(Session.UserId, Session.Id, role);
            Device = new(Id, "site", Guid.NewGuid(), Now.AddMinutes(15), true);
            Authorization = new(Id, "site", true, true, Device.AuthorizationVersion, true, true);
            Command = new(Guid.NewGuid(), Id, "site", Now, "0000BBB", 0.1m);
        }

        public IServiceCollection Services() => new ServiceCollection().AddSingleton<TimeProvider>(this)
            .AddSingleton<ICurrentUserContext>(this).AddSingleton<ICurrentDeviceContext>(this)
            .AddSingleton<IAuthenticationSessionRepository>(this).AddSingleton<IDeviceAuthorizationQuery>(this)
            .AddSingleton<ILprClock>(this).AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILprRateLimitStore>(this).AddSingleton(new LprOptions())
            .AddApplication().AddSingleton<IRequestHandler<ReceiveLprEventCommand, Outcome<ReceiveLprEventResult>>>(new ReceiveHandler(this))
            .AddSingleton<IRequestHandler<GetDeviceQuery, Outcome<DeviceDto>>>(new GetHandler(this));
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken) => Task.FromResult(Now);
        public Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken ct)
        {
            QuotaCalls++;
            return Task.FromResult(new RateDecision(AllowQuota, Now, Now.AddMilliseconds(1100)));
        }
        public Task<AuthenticationSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            SessionReads++;
            return Task.FromResult<AuthenticationSession?>(Session);
        }
        Task<DeviceAuthorizationState?> IDeviceAuthorizationQuery.FindAsync(Guid id, CancellationToken cancellationToken)
        {
            AuthorizationReads++;
            LastCancellation = cancellationToken;
            return Task.FromResult(Authorization);
        }
        public Task<AuthenticationSession?> FindOpenAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RefreshTokenRecord?> FindRefreshAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Add(AuthenticationSession session) => throw new NotSupportedException();
        public void Add(RefreshTokenRecord refresh) => throw new NotSupportedException();
    }

    private sealed class ReceiveHandler(State state) : IRequestHandler<ReceiveLprEventCommand, Outcome<ReceiveLprEventResult>>
    {
        public Task<Outcome<ReceiveLprEventResult>> Handle(ReceiveLprEventCommand request, CancellationToken cancellationToken)
        {
            state.HandlerCalls++;
            return Task.FromResult(Outcome<ReceiveLprEventResult>.Success(new(state.Duplicate, new(request.EventId, state.Now, "valid"))));
        }
    }
    private sealed class GetHandler(State state) : IRequestHandler<GetDeviceQuery, Outcome<DeviceDto>>
    {
        public Task<Outcome<DeviceDto>> Handle(GetDeviceQuery request, CancellationToken cancellationToken)
        {
            state.HandlerCalls++;
            return Task.FromResult(Outcome<DeviceDto>.Success(new(state.Id, "Camera", "site", true, true, state.Now, state.Now, Guid.NewGuid())));
        }
    }
}
