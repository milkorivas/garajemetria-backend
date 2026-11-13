using System.Security.Cryptography;
using System.Text.Json;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.Infrastructure.Lpr;

internal sealed class DeviceCredentialService(AuthenticationDbContext db, IOpenIddictApplicationManager manager,
    IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication> store, DeviceOAuthOptions options) : IDeviceCredentialService
{
    private static readonly string Permissions = JsonSerializer.Serialize(new[] { OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.ClientCredentials, OpenIddictConstants.Permissions.Prefixes.Scope + "lpr.events.write" });

    public async Task<DeviceCredentials> CreateAsync(Guid deviceId, CancellationToken ct)
    {
        RequireTransaction();
        var secret = Secret();
        await manager.CreateAsync(new OpenIddictEntityFrameworkCoreApplication
        {
            Id = LprRepository.ApplicationId(deviceId),
            ClientId = "lpr_" + deviceId.ToString("N"),
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            Permissions = Permissions
        }, secret, ct);
        return Credentials(deviceId, secret);
    }
    public async Task<DeviceCredentials> RotateAsync(Guid deviceId, CancellationToken ct)
    {
        RequireTransaction();
        var application = await FindAsync(deviceId, ct);
        application.Permissions = Permissions;
        var secret = Secret();
        await manager.UpdateAsync(application, secret, ct);
        return Credentials(deviceId, secret);
    }
    public async Task RevokeAsync(Guid deviceId, CancellationToken ct)
    {
        RequireTransaction();
        var application = await FindAsync(deviceId, ct);
        // Keep a confidential client with no usable secret or grant permissions. Manager validation
        // requires a secret for confidential clients; clearing is an explicit store operation, not hashing.
        await store.SetClientSecretAsync(application, null, ct);
        application.Permissions = "[]";
        await store.UpdateAsync(application, ct);
    }
    public async Task<bool> VerifyAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return false;
        foreach (var entry in db.ChangeTracker.Entries<OpenIddictEntityFrameworkCoreApplication>().Where(x => x.Entity.ClientId == clientId).ToArray())
        {
            if (entry.State != EntityState.Unchanged) throw new InvalidOperationException("Cannot verify pending credential changes.");
            entry.State = EntityState.Detached;
        }
        var application = await manager.FindByClientIdAsync(clientId, ct);
        return application is not null && await manager.ValidateClientSecretAsync(application, clientSecret, ct);
    }
    private async Task<OpenIddictEntityFrameworkCoreApplication> FindAsync(Guid id, CancellationToken ct) =>
        (OpenIddictEntityFrameworkCoreApplication?)await manager.FindByIdAsync(LprRepository.ApplicationId(id), ct) ??
        throw new InvalidOperationException("Device OAuth application is missing.");
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private DeviceCredentials Credentials(Guid id, string secret) => new("lpr_" + id.ToString("N"), secret,
        new Uri(new Uri(options.Issuer), "/connect/token").AbsoluteUri, "lpr.events.write");
    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Device credential changes require a transaction.");
    }
}
