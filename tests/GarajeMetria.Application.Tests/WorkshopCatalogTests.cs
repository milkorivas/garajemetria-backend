using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Workshops;

namespace GarajeMetria.Application.Tests;

public sealed class WorkshopCatalogTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task OnlyCompleteValidObservationsArePublished(bool complete, bool emptyConfirmed, bool empty)
    {
        var store = new Store();
        var source = new Source(new(empty ? [] : [new("001", "Example")], complete, emptyConfirmed));
        var handler = new SynchronizeWorkshopsHandler(source, store);
        if (complete && (!empty || emptyConfirmed))
        {
            await handler.Handle(new(), default);
            Assert.True(store.Published);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new(), default));
            Assert.False(store.Published);
        }
    }

    [Fact]
    public async Task DuplicateCodesDoNotReachPublication()
    {
        var store = new Store();
        var handler = new SynchronizeWorkshopsHandler(new Source(new([new("001", "One"), new("001", "Two")], true, true)), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new(), default));
        Assert.False(store.Published);
    }

    [Fact]
    public async Task SourceFailureDoesNotPublish()
    {
        var store = new Store();
        var handler = new SynchronizeWorkshopsHandler(new FailingSource(), store);
        await Assert.ThrowsAsync<TimeoutException>(() => handler.Handle(new(), default));
        Assert.False(store.Published);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task InvalidPaginationIsRejected(int page, int size) =>
        Assert.False((await new ListWorkshopsValidator(new()).ValidateAsync(new ListWorkshopsQuery(page, size, null))).IsValid);

    private sealed class Source(WorkshopRead read) : IWorkshopSource
    {
        public Task<WorkshopRead> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(read);
    }

    private sealed class FailingSource : IWorkshopSource
    {
        public Task<WorkshopRead> ReadAsync(CancellationToken cancellationToken) => throw new TimeoutException();
    }

    private sealed class Store : IWorkshopCatalogStore
    {
        public bool Published { get; private set; }
        public Task<CatalogObservation> BeginObservationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogObservation(1, DateTimeOffset.UnixEpoch));
        public Task<CatalogPublication> PublishAsync(CatalogObservation observation, IReadOnlyList<WorkshopEntry> entries, CancellationToken cancellationToken)
        {
            Published = true;
            return Task.FromResult(new CatalogPublication(true, DateTimeOffset.UnixEpoch));
        }
        public Task<DateTimeOffset?> LastPublishedAsync(CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
        public Task<WorkshopPage?> ListAsync(int pageNumber, int pageSize, bool? isActive, CancellationToken cancellationToken) =>
            Task.FromResult<WorkshopPage?>(null);
    }
}
