using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Users;

namespace GarajeMetria.Application.Tests;

public sealed class UserAdministrationTests
{
    [Fact]
    public async Task RF002_CP008_AdvisorCannotDiscoverTargetOrDirectory()
    {
        var state = new State(UserRoles.Advisor);
        var result = await state.Handler.Handle(state.Command, default);
        Assert.Equal(FailureCode.Forbidden, result.Error?.Code);
        Assert.Equal(0, state.Reads);
        Assert.Equal(0, state.DirectoryReads);
    }

    [Fact]
    public async Task RF002_CP016_ChangeAuditsAndClosesSession()
    {
        var state = new State();
        var expectedVersion = state.Command.ExpectedVersion;
        var result = await state.Handler.Handle(state.Command, default);
        Assert.True(result.Succeeded);
        Assert.NotEqual(expectedVersion, result.Value!.Version);
        Assert.Equal("next", state.Assignment.WorkshopId);
        Assert.Equal(state.Now, Assert.Single(state.Audits).OccurredAt);
        Assert.False(state.TargetSession.IsActive(state.Now));
    }

    [Fact]
    public async Task RF002_CP017_NoChangePreservesVersionSessionAndAudit()
    {
        var state = new State();
        var result = await state.Handler.Handle(state.Command with { WorkshopId = "old" }, default);
        Assert.True(result.Succeeded);
        Assert.Equal(state.Command.ExpectedVersion, result.Value!.Version);
        Assert.Empty(state.Audits);
        Assert.True(state.TargetSession.IsActive(state.Now));
    }

    [Theory]
    [InlineData(WorkshopAvailability.Missing, FailureCode.ResourceNotFound)]
    [InlineData(WorkshopAvailability.Inactive, FailureCode.AssignmentConflict)]
    [InlineData(WorkshopAvailability.CatalogUnavailable, FailureCode.ServiceUnavailable)]
    public async Task RF002_CP022_InvalidDestinationHasNoEffects(WorkshopAvailability availability, FailureCode expected)
    {
        var state = new State { Availability = availability };
        var result = await state.Handler.Handle(state.Command, default);
        Assert.Equal(expected, result.Error?.Code);
        Assert.Empty(state.Audits);
        Assert.Equal("old", state.Assignment.WorkshopId);
        Assert.True(state.TargetSession.IsActive(state.Now));
    }

    [Theory]
    [InlineData(DirectoryStatus.Valid, true, null)]
    [InlineData(DirectoryStatus.Valid, false, FailureCode.AssignmentConflict)]
    [InlineData(DirectoryStatus.NotFound, false, FailureCode.AssignmentConflict)]
    [InlineData(DirectoryStatus.Unavailable, false, FailureCode.DirectoryUnavailable)]
    public async Task RF002_CP019_RemovalChecksLiveDirectoryOutsideTransaction(DirectoryStatus status, bool admin, FailureCode? expected)
    {
        var state = new State { DirectoryStatus = status, TargetAdmin = admin };
        var result = await state.Handler.Handle(state.Command with { WorkshopId = null }, default);
        Assert.Equal(expected, result.Error?.Code);
        Assert.Equal(1, state.DirectoryReads);
        Assert.False(state.DirectoryInsideTransaction);
    }

    [Fact]
    public async Task RF002_CP017_StaleAndConditionallyLostVersionsHaveNoEffects()
    {
        var state = new State();
        Assert.Equal(FailureCode.PreconditionFailed,
            (await state.Handler.Handle(state.Command with { ExpectedVersion = Guid.NewGuid() }, default)).Error?.Code);
        state.LoseConditionalWrite = true;
        Assert.Equal(FailureCode.PreconditionFailed, (await state.Handler.Handle(state.Command, default)).Error?.Code);
        Assert.Empty(state.Audits);
        Assert.True(state.TargetSession.IsActive(state.Now));
    }

    [Fact]
    public async Task RF002_CP016_ValidatorsRejectMalformedInputAndAllowHugePage()
    {
        var state = new State();
        var validator = new SetDefaultWorkshopValidator();
        Assert.True((await validator.ValidateAsync(state.Command)).IsValid);
        foreach (var bad in new[] { "", " ", "line\nbreak", "\ud800", new string('a', 501) })
            Assert.False((await validator.ValidateAsync(state.Command with { Reason = bad })).IsValid);
        Assert.False((await validator.ValidateAsync(state.Command with { WorkshopId = " " })).IsValid);
        Assert.True((await new ListLocalUsersValidator(new()).ValidateAsync(new ListLocalUsersQuery(int.MaxValue, 100))).IsValid);
    }

    private sealed class State : ICurrentUserContext, IUserAdministrationQuery, ILocalUserRepository, IWorkshopQuery,
        IWorkshopAssignmentAuditRepository, ICorporateDirectory, IAuthenticationSessionRepository, IUnitOfWork
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch;
        public CurrentUser? User { get; }
        public string CorrelationId => "synthetic-correlation";
        public AssignmentState Assignment { get; private set; } = new(Guid.NewGuid(), Guid.NewGuid(), "old", Guid.NewGuid());
        public AuthenticationSession ActorSession { get; }
        public AuthenticationSession TargetSession { get; }
        public List<WorkshopAssignmentAuditEntry> Audits { get; } = [];
        public int Reads { get; private set; }
        public int DirectoryReads { get; private set; }
        public bool InTransaction { get; private set; }
        public bool DirectoryInsideTransaction { get; private set; }
        public bool LoseConditionalWrite { get; set; }
        public bool TargetAdmin { get; set; }
        public DirectoryStatus DirectoryStatus { get; set; } = DirectoryStatus.Valid;
        public WorkshopAvailability Availability { get; set; } = WorkshopAvailability.Active;
        public SetDefaultWorkshopCommand Command => new(Assignment.UserId.ToString(), "next", "Synthetic reason", Assignment.Version);
        public SetDefaultWorkshopHandler Handler => new(this, this, this, this, this, this, this,
            new(this, this, new FixedClock(Now)), this, new FixedClock(Now));

        public State(string role = UserRoles.Administrator)
        {
            User = new(Guid.NewGuid(), Guid.NewGuid(), role);
            ActorSession = new(User.SessionId, User.Id, role, Now, Now.AddHours(1));
            TargetSession = new(Guid.NewGuid(), Assignment.UserId, UserRoles.Advisor, Now, Now.AddHours(1));
        }

        public Task<AssignmentState?> FindAsync(string userId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<AssignmentState?>(Assignment);
        }
        public Task<LocalUserPage> ListAsync(int pageNumber, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetAssignmentAsync(Guid userId, Guid expectedVersion, string? workshopId, Guid version, CancellationToken cancellationToken)
        {
            Assert.True(InTransaction);
            if (LoseConditionalWrite) return Task.FromResult(false);
            Assignment = Assignment with { WorkshopId = workshopId, Version = version };
            return Task.FromResult(true);
        }
        public Task<WorkshopAvailability> CheckAsync(string workshopId, CancellationToken cancellationToken) => Task.FromResult(Availability);
        public void Add(WorkshopAssignmentAuditEntry entry) => Audits.Add(entry);
        public Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken)
        {
            DirectoryReads++;
            DirectoryInsideTransaction |= InTransaction;
            return Task.FromResult(new DirectoryResult(DirectoryStatus, new(objectId, "synthetic", "Synthetic", TargetAdmin, !TargetAdmin)));
        }
        public Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthenticationSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<AuthenticationSession?>(sessionId == ActorSession.Id ? ActorSession : TargetSession);
        public Task<AuthenticationSession?> FindOpenAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AuthenticationSession?>(TargetSession);
        public Task<RefreshTokenRecord?> FindRefreshAsync(Guid tokenId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Add(AuthenticationSession session) => throw new NotSupportedException();
        public void Add(RefreshTokenRecord refresh) => throw new NotSupportedException();
        public async Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            InTransaction = true;
            try { return await action(cancellationToken); }
            finally { InTransaction = false; }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
