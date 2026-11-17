using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Stays;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprHttpTests
{
    [SqlFact]
    public async Task LocationHistoryAndSynchronousStaysAreVisibleAtTheIngestionSuccessBoundary()
    {
        var clock = new EventClock();
        using var factory = Factory(configure: services => { services.RemoveAll<ILprClock>(); services.AddSingleton<ILprClock>(clock); });
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var createdResponse = await admin.PostAsJsonAsync("/api/lpr/devices", new { name = "Camera", workshopId = setup.Site, locationDescription = "Synthetic access A" });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await ReadAsync(createdResponse);
        var id = created.GetProperty("device").GetProperty("id").GetGuid();
        Assert.Equal("Synthetic access A", created.GetProperty("device").GetProperty("locationDescription").GetString());
        var clientId = created.GetProperty("credentials").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("credentials").GetProperty("clientSecret").GetString()!;
        using var oauth = factory.Client();
        var grant = await ReadAsync(await TokenAsync(oauth, clientId, secret));
        using var device = Client(factory, grant.GetProperty("access_token").GetString()!);
        var original = clock.Now;
        clock.Now = original.AddMinutes(1);
        var firstId = Guid.NewGuid();
        var firstBody = StayBody(firstId, id, setup.Site, clock.Now, "entry");
        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(device, firstBody)).StatusCode);
        await using var db = database.CreateContext();
        var first = await db.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == setup.Site);
        Assert.Equal(clock.Now, first.EntryAt); Assert.Null(first.ExitAt);
        var state = await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == id);
        clock.Now = original.AddMinutes(2);
        var changed = await MutationAsync(admin, id, "location", createdResponse.Headers.ETag!.ToString(), "{\"locationDescription\":\"Synthetic access B\"}", HttpMethod.Put);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var same = await MutationAsync(admin, id, "location", changed.Headers.ETag!.ToString(), "{\"locationDescription\":\" Synthetic access B \"}", HttpMethod.Put);
        Assert.Equal(changed.Headers.ETag.ToString(), same.Headers.ETag?.ToString());
        clock.Now = original.AddMinutes(3);
        var lateId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(device, StayBody(lateId, id, setup.Site, original.AddMinutes(1.5), "entry"))).StatusCode);
        var exitId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(device, StayBody(exitId, id, setup.Site, original.AddMinutes(4), "exit"))).StatusCode);
        var duplicate = await PostEventAsync(device, firstBody);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        db.ChangeTracker.Clear();
        var stay = await db.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == setup.Site && x.AbsorbedByStayId == null);
        Assert.Equal(first.Id, stay.Id); Assert.Equal(original.AddMinutes(1), stay.EntryAt); Assert.Equal(original.AddMinutes(4), stay.ExitAt);
        Assert.Equal(3, await db.StayObservationAssignments.CountAsync(x => x.PartitionId == stay.PartitionId));
        Assert.Equal(2, await db.LprDeviceLocations.CountAsync(x => x.DeviceId == id));
        Assert.Equal(3, await db.LprObservationLocations.CountAsync(x => x.DeviceId == id));
        var links = await (from link in db.LprObservationLocations.AsNoTracking()
                           join location in db.LprDeviceLocations on link.LocationId equals location.Id
                           where link.DeviceId == id
                           select new { link.EventId, link.Provenance, location.Description }).ToArrayAsync();
        Assert.Equal("Synthetic access A", links.Single(x => x.EventId == firstId).Description);
        Assert.Equal("Synthetic access A", links.Single(x => x.EventId == lateId).Description);
        Assert.Equal("Synthetic access B", links.Single(x => x.EventId == exitId).Description);
        Assert.Equal(ObservationLocationProvenance.LatestKnownForFutureCapture, links.Single(x => x.EventId == exitId).Provenance);
        Assert.Equal(state.AuthorizationVersion, (await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == id)).AuthorizationVersion);
        Assert.Equal(2, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == id));
        var logDirectory = factory.Services.GetRequiredService<IConfiguration>()["FileLogging:DirectoryPath"]!;
        var logs = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(logDirectory, "*.json"))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            logs.Append(await reader.ReadToEndAsync());
        }
        var text = logs.ToString();
        Assert.Contains("Stay recalculation staged", text);
        foreach (var sensitive in new[] { "1234BCD", "Synthetic access A", "Synthetic access B", secret, grant.GetProperty("access_token").GetString()! })
            Assert.DoesNotContain(sensitive, text);
    }

    [SqlTheory]
    [InlineData("{}")]
    [InlineData("{\"locationDescription\":null}")]
    [InlineData("{\"locationDescription\":\" \"}")]
    [InlineData("{\"locationDescription\":\"A\",\"locationDescription\":\"B\"}")]
    [InlineData("{\"locationDescription\":\"A\",\"workshopId\":\"other\"}")]
    [InlineData("{\"locationDescription\":\"A\\nB\"}")]
    public async Task LocationPutRejectsInvalidOrOverpostedBodiesWithoutMutation(string body)
    {
        using var factory = Factory(); var setup = await SetupAsync(factory); using var admin = Client(factory, setup.Access);
        var camera = await CreateAsync(admin, setup.Site);
        Assert.Equal(HttpStatusCode.BadRequest, (await MutationAsync(admin, camera.Id, "location", camera.Tag, body, HttpMethod.Put)).StatusCode);
        Assert.Equal(camera.Tag, (await admin.GetAsync("/api/lpr/devices/" + camera.Id)).Headers.ETag?.ToString());
        await using var db = database.CreateContext();
        Assert.Empty(await db.LprDeviceLocations.Where(x => x.DeviceId == camera.Id).ToArrayAsync());
    }

    [SqlFact]
    public async Task DeviceIdentityCannotMaintainLocationsAndMissingOrStaleEtagsAreRejected()
    {
        using var factory = Factory(); var setup = await SetupAsync(factory); using var admin = Client(factory, setup.Access);
        var camera = await CreateAsync(admin, setup.Site); using var oauth = factory.Client();
        using var device = Client(factory, (await ReadAsync(await TokenAsync(oauth, camera.ClientId, camera.Secret))).GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MutationAsync(device, camera.Id, "location", camera.Tag, "{\"locationDescription\":\"A\"}", HttpMethod.Put)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await MutationAsync(admin, camera.Id, "location", null, "{\"locationDescription\":\"A\"}", HttpMethod.Put)).StatusCode);
        var changed = await MutationAsync(admin, camera.Id, "location", camera.Tag, "{\"locationDescription\":\"A\"}", HttpMethod.Put);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await MutationAsync(admin, camera.Id, "location", camera.Tag, "{\"locationDescription\":\"B\"}", HttpMethod.Put)).StatusCode);
    }

    [SqlFact]
    public async Task ADiscrepantNodeRejectsNewEventsButCanConfirmAnIdenticalRetryWithoutRebindingLocation()
    {
        using var first = Factory(); var setup = await SetupAsync(first); using var admin = Client(first, setup.Access);
        var camera = await CreateAsync(admin, setup.Site); using var oauth = first.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, camera.ClientId, camera.Secret))).GetProperty("access_token").GetString()!;
        using var accepted = Client(first, token);
        var id = Guid.NewGuid(); var body = EventBody(id, camera.Id, setup.Site, DateTimeOffset.UtcNow);
        var original = await ReadAsync(await PostEventAsync(accepted, body));
        using var second = Factory(shared: first, configure: services =>
        { services.RemoveAll<StayOptions>(); services.AddSingleton(new StayOptions { EstimatedDurationMinutes = 45 }); });
        using var discrepant = Client(second, token);
        var retry = await PostEventAsync(discrepant, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var duplicate = await ReadAsync(retry);
        Assert.True(duplicate.GetProperty("duplicate").GetBoolean());
        foreach (var property in original.EnumerateObject().Where(p => p.Name != "duplicate"))
            Assert.Equal(property.Value.GetRawText(), duplicate.GetProperty(property.Name).GetRawText());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostEventAsync(discrepant,
            EventBody(Guid.NewGuid(), camera.Id, setup.Site, DateTimeOffset.UtcNow))).StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.LprEvents.CountAsync(x => x.DeviceId == camera.Id));
        Assert.Equal(1, await db.LprObservationLocations.CountAsync(x => x.DeviceId == camera.Id));
    }

    private static string StayBody(Guid id, Guid device, string site, DateTimeOffset at, string? direction, string plate = "1234BCD") => JsonSerializer.Serialize(new
    { event_id = id, device_id = device, site_id = site, capture_timestamp = at.UtcDateTime.ToString("O"), plate, confidence = 0m, direction });

    [SqlFact]
    public async Task LocationAuditFailureRollsBackVersionPointerHistoryAndDeviceMutation()
    {
        var fault = new LocationAuditFault();
        using var factory = Factory(configure: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault)));
        var setup = await SetupAsync(factory); using var admin = Client(factory, setup.Access);
        var camera = await CreateAsync(admin, setup.Site);
        fault.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await MutationAsync(admin, camera.Id, "location", camera.Tag,
            "{\"locationDescription\":\"Synthetic access\"}", HttpMethod.Put)).StatusCode);
        await using var db = database.CreateContext();
        Assert.Null((await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == camera.Id)).CurrentLocationId);
        Assert.Empty(await db.LprDeviceLocations.Where(x => x.DeviceId == camera.Id).ToArrayAsync());
        Assert.Equal(1, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == camera.Id));
        Assert.Equal(camera.Tag, (await admin.GetAsync("/api/lpr/devices/" + camera.Id)).Headers.ETag?.ToString());
        fault.Enabled = false;
        Assert.Equal(HttpStatusCode.NoContent, (await MutationAsync(admin, camera.Id, "location", camera.Tag,
            "{\"locationDescription\":\"Synthetic access\"}", HttpMethod.Put)).StatusCode);
        Assert.Equal(1, await db.LprDeviceLocations.CountAsync(x => x.DeviceId == camera.Id));
    }

    private sealed class LocationAuditFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<DeviceAdministrationAuditRow>().Any(x =>
                x.State == EntityState.Added && x.Entity.Operation == GarajeMetria.Domain.Lpr.DeviceAdministrationOperation.LocationChanged))
                throw new InvalidOperationException("Synthetic location audit failure.");
            return ValueTask.FromResult(result);
        }
    }
}
