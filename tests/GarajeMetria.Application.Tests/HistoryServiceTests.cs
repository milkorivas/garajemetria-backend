using GarajeMetria.Application;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Stays;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.Application.Tests;

public sealed class HistoryServiceTests
{
    [Theory]
    [InlineData(FailureCode.ServiceUnavailable, FailureCode.HistoryAuditUnavailable)]
    [InlineData(FailureCode.OperationOutcomeUnknown, FailureCode.OperationOutcomeUnknown)]
    public async Task AuditMustBeConfirmedBeforeAnyDataIsReturned(FailureCode thrown, FailureCode expected)
    {
        var stub = new Stub { AuditError = thrown };
        using var services = Provider(stub);
        var sender = services.GetRequiredService<ISender>();
        Assert.Equal(expected, (await sender.Send(new SearchLprEventsQuery(new()))).Error?.Code);
        Assert.Equal(expected, (await sender.Send(new ExportHistoryCommand(HistoryKind.Events, new()))).Error?.Code);
        Assert.Equal(2, stub.AuditAttempts);
    }
    [Fact]
    public async Task EmptySuccessfulQueryStillRecordsAuthorizedWorkshopScope()
    {
        var stub = new Stub();
        using var services = Provider(stub);
        var result = await services.GetRequiredService<ISender>().Send(new SearchLprEventsQuery(new()));
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(new[] { "own", "other" }, stub.Audit!.Scope.WorkshopIds);
        Assert.True(stub.Audit.Scope.AllAuthorized);
        Assert.Equal("ReadEvents", stub.Audit.Operation);
        Assert.Equal(0, stub.Audit.RowCount);
    }
    [Fact]
    public async Task ExportLimitRejectsBeforeWriterAndNoReleaseAuditIsRecorded()
    {
        var stub = new Stub { Total = 10001 };
        using var services = Provider(stub);
        var result = await services.GetRequiredService<ISender>().Send(new ExportHistoryCommand(HistoryKind.Events, new()));
        Assert.Equal(FailureCode.HistoryExportLimitExceeded, result.Error?.Code);
        Assert.Equal(0, stub.WriterCalls);
        Assert.Equal(0, stub.AuditAttempts);
    }
    [Fact]
    public async Task ExportRevalidatesSessionAfterWriterBeforeAudit()
    {
        var stub = new Stub { RevokeOnWrite = true };
        using var services = Provider(stub);
        var result = await services.GetRequiredService<ISender>().Send(new ExportHistoryCommand(HistoryKind.Events, new()));
        Assert.Equal(FailureCode.SessionEnded, result.Error?.Code);
        Assert.Equal(0, stub.AuditAttempts);
    }
    [Fact]
    public async Task AdvisorCannotChooseAnotherWorkshopOrForgeHisRole()
    {
        var stub = new Stub(advisor: true);
        using var services = Provider(stub);
        var access = services.GetRequiredService<HistoryAccess>();
        Assert.Equal(FailureCode.Forbidden, (await access.ResolveAsync(["other"], default)).Error?.Code);
        Assert.Equal(new[] { "own" }, (await access.ResolveAsync([], default)).Value!.WorkshopIds);
        stub.User = stub.User! with { Role = UserRoles.Administrator };
        Assert.Equal(FailureCode.SessionEnded, (await access.ResolveAsync([], default)).Error?.Code);
    }
    [Fact]
    public async Task ValidationRunsBeforeQueriesOrAudit()
    {
        var stub = new Stub();
        using var services = Provider(stub);
        var result = await services.GetRequiredService<ISender>().Send(new SearchLprEventsQuery(new() { PageSize = 201 }));
        Assert.Equal(FailureCode.InvalidInput, result.Error?.Code);
        Assert.Equal(0, stub.ReadCalls);
        Assert.Equal(0, stub.AuditAttempts);
    }
    private static ServiceProvider Provider(Stub stub)
    {
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSingleton<ICurrentUserContext>(stub); services.AddSingleton<IUserAccessQuery>(stub);
        services.AddSingleton<IAuthenticationSessionRepository>(stub); services.AddSingleton<IHistoryCatalogQuery>(stub);
        services.AddSingleton<IHistoryReadSession>(stub); services.AddSingleton<IHistoryQuery>(stub);
        services.AddSingleton<IHistoryClosureDiscovery>(stub); services.AddSingleton<IHistoryAccessAuditStore>(stub);
        services.AddSingleton<IHistoryWorkbookWriter>(stub); services.AddSingleton<IHistoryAdmission>(stub);
        services.AddSingleton<ILprClock>(stub); services.AddSingleton<IWorkshopCalendar>(stub);
        services.AddSingleton<IHistoryCalendar>(stub); services.AddSingleton<TimeProvider>(stub);
        services.AddSingleton(new HistoryOptions());
        return services.BuildServiceProvider();
    }
    private sealed class Stub : TimeProvider, ICurrentUserContext, IUserAccessQuery, IAuthenticationSessionRepository,
        IHistoryCatalogQuery, IHistoryReadSession, IHistoryQuery, IHistoryClosureDiscovery, IHistoryAccessAuditStore,
        IHistoryWorkbookWriter, IHistoryAdmission, ILprClock, IWorkshopCalendar, IHistoryCalendar
    {
        private readonly DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        private readonly AuthenticationSession session;
        public Stub(bool advisor = false)
        {
            User = new(Guid.NewGuid(), Guid.NewGuid(), advisor ? UserRoles.Advisor : UserRoles.Administrator);
            session = new(User.SessionId, User.Id, User.Role, now.AddHours(-1), now.AddHours(1));
        }
        public CurrentUser? User { get; set; }
        public string CorrelationId => "synthetic-correlation";
        public FailureCode? AuditError { get; init; }
        public int AuditAttempts { get; private set; }
        public int WriterCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public long Total { get; init; }
        public bool RevokeOnWrite { get; init; }
        public HistoryAudit? Audit { get; private set; }
        public override DateTimeOffset GetUtcNow() => now;
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(now);
        public Task<LocalIdentity?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<LocalIdentity?>(new(id, id, "test", "Synthetic", "own", true, true));
        public Task<LocalIdentity> ProvisionAsync(DirectoryIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        Task<AuthenticationSession?> IAuthenticationSessionRepository.FindAsync(Guid id, CancellationToken ct) => Task.FromResult<AuthenticationSession?>(session);
        public Task<AuthenticationSession?> FindOpenAsync(Guid id, CancellationToken ct) => Task.FromResult<AuthenticationSession?>(session);
        public Task<RefreshTokenRecord?> FindRefreshAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Add(AuthenticationSession value) => throw new NotSupportedException();
        public void Add(RefreshTokenRecord value) => throw new NotSupportedException();
        public Task<string[]?> ResolveAsync(string[] requested, CancellationToken ct) => Task.FromResult<string[]?>(requested.Length == 0 ? ["own", "other"] : requested);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => action(ct);
        public Task<Outcome<HistoryPage>> ReadAsync(HistoryRequest r, HistoryScope scope, HistoryWindow? window, DateTimeOffset at, bool export, CancellationToken ct)
        { ReadCalls++; return Task.FromResult(Outcome<HistoryPage>.Success(new([], 1, 50, Total, at, at, window))); }
        public Task<IReadOnlyList<StayClosureWork>> ReadDueAsync(HistoryRequest r, HistoryScope scope, HistoryWindow? w, DateTimeOffset at, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StayClosureWork>>([]);
        public Task AppendAsync(HistoryAudit audit, CancellationToken ct)
        {
            AuditAttempts++; if (AuditError is { } error) throw new PersistenceFailureException(error);
            Audit = audit; return Task.CompletedTask;
        }
        public Task<Outcome<HistoryExport>> WriteAsync(HistoryPage page, HistoryKind kind, CancellationToken ct)
        {
            WriterCalls++;
            if (RevokeOnWrite) session.Close(now, "synthetic");
            return Task.FromResult(Outcome<HistoryExport>.Success(new([1, 2])));
        }
        public Task<Outcome<IAsyncDisposable>> AcquireAsync(Guid id, bool export, CancellationToken ct) => Task.FromResult(Outcome<IAsyncDisposable>.Success(new Lease()));
        public DateTime GetLocalTime(DateTimeOffset instant) => instant.UtcDateTime;
        public DateOnly GetLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
        public StayDayWindow GetDay(DateOnly date) => new(date, new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
