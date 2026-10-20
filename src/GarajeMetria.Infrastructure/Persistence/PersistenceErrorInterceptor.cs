using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class PersistenceErrorInterceptor : DbCommandInterceptor
{
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default) =>
        throw SqlTransactions.Classify(eventData.Exception);
}
