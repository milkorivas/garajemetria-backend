using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GarajeMetria.Infrastructure.Persistence;

public sealed class AuthenticationDbContextFactory : IDesignTimeDbContextFactory<AuthenticationDbContext>
{
    public AuthenticationDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("GARAJEMETRIA_MIGRATIONS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Set GARAJEMETRIA_MIGRATIONS_CONNECTION for migration tooling.");
        return new(new DbContextOptionsBuilder<AuthenticationDbContext>().UseSqlServer(connection).Options);
    }
}
