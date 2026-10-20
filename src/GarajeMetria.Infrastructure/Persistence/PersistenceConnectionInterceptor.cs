using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class PersistenceConnectionInterceptor : DbConnectionInterceptor
{
    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default) => throw SqlTransactions.Classify(eventData.Exception);
}
