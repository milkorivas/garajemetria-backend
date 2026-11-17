using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Infrastructure.Persistence;

// Preserve the RF-003 handler and retry comparison; only new writes require a matching active policy.
internal sealed class PolicyCheckedLprEventRepository(LprRepository inner, IStayPolicyRepository policies,
    AuthenticationDbContext db) : ILprEventRepository
{
    private Guid? checkedEvent;
    private Guid? checkedTransaction;

    public async Task<LprEventLookup> CompareAsync(ReceiveLprEventCommand incoming, CancellationToken cancellationToken)
    {
        checkedEvent = null;
        checkedTransaction = null;
        var result = await inner.CompareAsync(incoming, cancellationToken);
        if (result.Comparison == LprEventComparison.Missing)
        {
            var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Event admission requires a transaction.");
            await policies.GetActiveAsync(cancellationToken);
            checkedEvent = incoming.EventId;
            checkedTransaction = transaction.TransactionId;
        }
        return result;
    }

    public void Add(LprEvent observation)
    {
        if (checkedEvent != observation.EventId || checkedTransaction is null ||
            checkedTransaction != db.Database.CurrentTransaction?.TransactionId)
            throw new InvalidOperationException("Event admission requires policy verification in the same transaction.");
        inner.Add(observation);
        checkedEvent = null;
        checkedTransaction = null;
    }
}
