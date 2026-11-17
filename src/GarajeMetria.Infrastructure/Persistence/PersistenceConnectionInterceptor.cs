using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class PersistenceConnectionInterceptor : DbConnectionInterceptor
{
    // Indexed computed columns and filtered indexes require these options on every writing connection.
    private const string SessionOptions = "SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF;";

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SessionOptions;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default) => throw SqlTransactions.Classify(eventData.Exception);
}
