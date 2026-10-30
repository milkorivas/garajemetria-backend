using GarajeMetria.Domain.Workshops;

namespace GarajeMetria.Domain.Tests;

public sealed class WorkshopCatalogTests
{
    [Fact]
    public void ReconciliationPreservesIdentitiesAndHistoryWhileApplyingPresence()
    {
        WorkshopState[] existing = [new("first-id", "001", "Old", false), new("second-id", "002", "Absent", true)];
        var plan = WorkshopCatalog.Reconcile(existing, [new("001", "Renamed"), new("003", "New")]);
        Assert.Equal("003", Assert.Single(plan.Added).Code);
        var reactivated = Assert.Single(plan.Updated, x => x.Id == "first-id");
        Assert.True(reactivated.IsActive);
        Assert.Equal("Renamed", reactivated.Name);
        Assert.False(Assert.Single(plan.Updated, x => x.Id == "second-id").IsActive);
        Assert.Equal("second-id", Assert.Single(plan.DeactivatedIds));
        Assert.Equal(1, plan.Reactivated);
        Assert.Equal(1, plan.Changed);
        Assert.False(existing[0].IsActive);
    }

    [Fact]
    public void CodesRemainOpaqueAndCaseSensitive()
    {
        WorkshopEntry[] entries = [new("001", "First"), new("1", "Second"), new("a", "Third"), new("A", "Fourth")];
        Assert.True(WorkshopCatalog.IsValid(entries));
        Assert.Equal("001", entries[0].Code);
    }

    [Fact]
    public void DuplicatesAndBlankFieldsAreInvalid()
    {
        Assert.False(WorkshopCatalog.IsValid([new("001", "First"), new("001", "Second")]));
        Assert.False(WorkshopCatalog.IsValid([new("", "First")]));
        Assert.False(WorkshopCatalog.IsValid([new("001", " ")]));
        Assert.True(WorkshopCatalog.IsValid([]));
    }
}
