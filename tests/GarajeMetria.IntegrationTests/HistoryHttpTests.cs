using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprHttpTests
{
    [SqlFact]
    public async Task HistoryExportDeadlineCancelsWriterAndReturnsNoFile()
    {
        using var factory = Factory(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new HistoryOptions { QueryTimeoutSeconds = 5, ExportTimeoutSeconds = 1, SqlTimeoutSeconds = 1 }));
            services.Replace(ServiceDescriptor.Scoped<IHistoryWorkbookWriter, WaitingHistoryWriter>());
        });
        var setup = await SetupAsync(factory);
        using var client = Client(factory, setup.Access);
        var response = await client.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { workshopIds = new[] { setup.Site } } });
        var diagnosticPath = factory.Services.GetRequiredService<IConfiguration>()["FileLogging:DirectoryPath"]!;
        var diagnostic = string.Join(Environment.NewLine, Directory.GetFiles(diagnosticPath, "*", SearchOption.AllDirectories)
            .SelectMany(ReadSharedLog).Where(x => x.Contains("History", StringComparison.Ordinal) || x.Contains("Request failed:", StringComparison.Ordinal)));
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync() + Environment.NewLine + diagnostic);
        Assert.NotEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
    }
    [SqlFact]
    public async Task HistoryOpenApiDocumentsUserQueriesWithoutDevicePreconditions()
    {
        using var factory = Factory();
        using var client = factory.Client();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadAsync(response);
        var get = json.GetProperty("paths").GetProperty("/api/lpr/events").GetProperty("get");
        var parameters = get.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(parameters, x => x.GetProperty("name").GetString() == "workshopIds");
        Assert.DoesNotContain(parameters, x => x.GetProperty("name").GetString() == "If-Match");
        Assert.True(get.GetProperty("responses").TryGetProperty("503", out _));
        Assert.True(json.GetProperty("paths").GetProperty("/api/history/exports").GetProperty("post")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", out _));
    }
    [SqlFact]
    public async Task HistoryExcelAllowsExactlyTenThousandRowsAndRejectsExcessWithoutTruncation()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var client = Client(factory, setup.Access);
        var device = await CreateAsync(client, setup.Site);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await SeedHistoryEventsAsync(device.Id, setup.Site, DateTimeOffset.UtcNow, 10000);
        var seedSeconds = watch.Elapsed.TotalSeconds;
        var end = DateTimeOffset.UtcNow.AddSeconds(1);
        var period = "&from=" + Uri.EscapeDataString(end.AddYears(-1).ToString("O")) + "&to=" + Uri.EscapeDataString(end.ToString("O"));
        watch.Restart();
        var deep = await ReadAsync(await client.GetAsync("/api/lpr/events?workshopIds=" + setup.Site + period + "&plate=012&plateMatch=contains&pageNumber=100&pageSize=50"));
        Assert.Equal(9999, deep.GetProperty("totalCount").GetInt64());
        Assert.Equal(50, deep.GetProperty("items").GetArrayLength());
        var deepPageSeconds = watch.Elapsed.TotalSeconds;
        var receipt = await ReadAsync(await client.GetAsync("/api/lpr/events?workshopIds=" + setup.Site + period + "&timeBasis=receipt"));
        Assert.Equal(10000, receipt.GetProperty("totalCount").GetInt64());
        var allResponse = await client.GetAsync("/api/lpr/events?plate=012&plateMatch=contains" + period);
        if (allResponse.StatusCode == HttpStatusCode.InternalServerError)
        {
            using var inspection = factory.Services.CreateScope();
            var ports = inspection.ServiceProvider;
            var sites = await ports.GetRequiredService<IHistoryCatalogQuery>().ResolveAsync([], default);
            await ports.GetRequiredService<IHistoryReadSession>().ExecuteAsync(ct => ports.GetRequiredService<IHistoryQuery>().ReadAsync(
                new SearchLprEventsQuery(new() { Plate = "012", PlateMatch = "contains", From = end.AddYears(-1), To = end }),
                new(new(Guid.NewGuid(), Guid.NewGuid(), "administrator"), sites!, true), new(end.AddYears(-1), end), DateTimeOffset.UtcNow, false, ct), default);
        }
        Assert.True(allResponse.IsSuccessStatusCode, await allResponse.Content.ReadAsStringAsync());
        var all = await ReadAsync(allResponse);
        Assert.True(all.GetProperty("totalCount").GetInt64() >= 9999);
        watch.Restart();
        var response = await client.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { workshopIds = new[] { setup.Site } }, order = "asc" });
        var logPath = factory.Services.GetRequiredService<IConfiguration>()["FileLogging:DirectoryPath"]!;
        var stages = Directory.Exists(logPath) ? string.Join(Environment.NewLine, Directory.GetFiles(logPath, "*", SearchOption.AllDirectories)
            .SelectMany(ReadSharedLog).Where(x => x.Contains("History stage:", StringComparison.Ordinal))) : "No stage log";
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync() + Environment.NewLine + stages);
        var exportSeconds = watch.Elapsed.TotalSeconds;
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
        {
            Assert.Equal(10001, document.WorkbookPart!.WorksheetParts.First().Worksheet.Descendants<Row>().Count());
            Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(document));
        }
        await SeedHistoryEventsAsync(device.Id, setup.Site, DateTimeOffset.UtcNow, 1);
        response = await client.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { workshopIds = new[] { setup.Site } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("history_export_limit_exceeded", (await ReadAsync(response)).GetProperty("code").GetString());
        var folder = Path.Combine(AppContext.BaseDirectory, "history-load-evidence");
        Directory.CreateDirectory(folder);
        await SaveHistoryPlanAsync(setup.Site, end.AddYears(-1), end, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "10000-rows.json"), JsonSerializer.Serialize(new
        { rows = 10000, seedSeconds, deepPageSeconds, exportSeconds, bytes = bytes.Length, utc = DateTimeOffset.UtcNow }));
    }
    [SqlFact]
    public async Task HistoryStaysPresenceTimelineAndMidnightClosureUseRealIngestion()
    {
        var clock = new EventClock();
        using var factory = Factory(configure: services => services.Replace(ServiceDescriptor.Scoped<ILprClock>(_ => clock)));
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        using var camera = Client(factory, token);
        foreach (var (minutes, direction) in new[] { (-15, "entry"), (-5, "exit"), (-1, "entry") })
        {
            var received = await camera.PostAsJsonAsync("/api/lpr/events", new
            {
                event_id = Guid.NewGuid(),
                device_id = device.Id,
                site_id = setup.Site,
                capture_timestamp = clock.Now.AddMinutes(minutes).UtcDateTime.ToString("O"),
                plate = "0012BCD",
                confidence = 0,
                direction
            });
            Assert.Equal(HttpStatusCode.Created, received.StatusCode);
        }
        var suffix = "?workshopIds=" + setup.Site;
        var stays = await ReadAsync(await admin.GetAsync("/api/stays" + suffix));
        Assert.Equal(2, stays.GetProperty("totalCount").GetInt64());
        var present = await ReadAsync(await admin.GetAsync("/api/vehicles/present" + suffix));
        Assert.Single(present.GetProperty("items").EnumerateArray());
        var stay = present.GetProperty("items")[0];
        Assert.Equal("open", stay.GetProperty("state").GetString());
        var id = stay.GetProperty("stayId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/stays/{id:D}")).StatusCode);
        var observations = await ReadAsync(await admin.GetAsync($"/api/stays/{id:D}/observations?pageSize=1"));
        Assert.Equal(1, observations.GetProperty("totalCount").GetInt64());
        var timeline = await ReadAsync(await admin.GetAsync("/api/vehicles/timeline" + suffix + "&plate=0012BCD"));
        Assert.Equal(5, timeline.GetProperty("totalCount").GetInt64());
        Assert.Equal(HttpStatusCode.Unauthorized, (await camera.GetAsync("/api/lpr/events" + suffix)).StatusCode);
        clock.Now = clock.Now.Date.AddDays(1).AddSeconds(1);
        present = await ReadAsync(await admin.GetAsync("/api/vehicles/present" + suffix));
        Assert.Empty(present.GetProperty("items").EnumerateArray());
        foreach (var kind in new[] { "stays", "present", "timeline" })
        {
            object filters = kind == "timeline" ? new { workshopIds = new[] { setup.Site }, plate = "0012BCD" } : new { workshopIds = new[] { setup.Site } };
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/history/exports", new { kind, filters })).StatusCode);
        }
        // Synthetic persisted lineage isolates the public identity resolver from the calculator's own fusion tests.
        Guid absorbed;
        await using (var db = database.CreateContext())
        {
            var source = await db.VehicleStays.SingleAsync(x => x.WorkshopId == setup.Site && x.Id != id);
            var target = await db.VehicleStays.SingleAsync(x => x.Id == id);
            var partition = await db.VehicleStayPartitions.SingleAsync(x => x.Id == target.PartitionId);
            absorbed = source.Id;
            source.AbsorbedByStayId = id;
            db.StayLineage.Add(new()
            {
                PartitionId = partition.Id,
                Revision = partition.ProjectionRevision,
                SourceStayId = absorbed,
                TargetStayId = id,
                Kind = GarajeMetria.Domain.Stays.StayLineageKind.Fusion,
                RecordedAt = clock.Now
            });
            await db.SaveChangesAsync();
        }
        var resolution = await ReadAsync(await admin.GetAsync($"/api/stays/{absorbed:D}"));
        Assert.True(resolution.GetProperty("wasReplaced").GetBoolean());
        Assert.Equal(id, resolution.GetProperty("resolvedStayId").GetGuid());
        var afterFusion = await ReadAsync(await admin.GetAsync($"/api/stays/{absorbed:D}/observations"));
        Assert.Equal(id, afterFusion.GetProperty("resolvedStayId").GetGuid());
    }

    [SqlFact]
    public async Task HistoryAdvisorIsolationIncludesDetailSelectorsCountsAndExcel()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        await SeedHistoryEventsAsync(device.Id, setup.Site, DateTimeOffset.UtcNow, 3);
        var own = "history-own-" + Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var session = new AuthenticationSession(Guid.NewGuid(), userId, UserRoles.Advisor, factory.Clock.Now.AddMinutes(-1), factory.Clock.Now.AddHours(1));
        await using (var db = database.CreateContext())
        {
            db.Workshops.Add(new() { Id = own, Code = own, Name = "Own workshop", IsActive = true });
            db.Users.Add(new() { Id = userId, DirectoryObjectId = Guid.NewGuid(), Username = "synthetic-advisor", DisplayName = "Synthetic", DefaultWorkshopId = own });
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }
        using var scope = factory.Services.CreateScope();
        using var advisor = Client(factory, scope.ServiceProvider.GetRequiredService<ITokenService>().Issue(session)!.Access.Value);
        var empty = await ReadAsync(await advisor.GetAsync("/api/lpr/events"));
        Assert.Equal(0, empty.GetProperty("totalCount").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await advisor.GetAsync("/api/lpr/events?workshopIds=" + setup.Site)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await advisor.GetAsync("/api/lpr/events?deviceIds=" + device.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await advisor.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { workshopIds = new[] { setup.Site } } })).StatusCode);
        var devices = await ReadAsync(await advisor.GetAsync("/api/history/devices"));
        Assert.Empty(devices.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await advisor.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { } })).StatusCode);
        await using var check = database.CreateContext();
        var eventId = await check.LprEvents.Where(x => x.WorkshopId == setup.Site).Select(x => x.EventId).FirstAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await advisor.GetAsync($"/api/lpr/events/{eventId:D}")).StatusCode);
        var unknown = await advisor.GetAsync($"/api/lpr/events/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain(await check.Set<HistoryAccessAuditRow>().Where(x => x.ActorUserId == userId).Select(x => x.Id).ToArrayAsync(),
            id => check.Set<HistoryAccessAuditWorkshopRow>().Any(x => x.AuditId == id && x.WorkshopId != own));
    }
    [SqlFact]
    public async Task HistoryEventsPreserveInvalidReadingsFiltersPagesAndAudit()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var client = Client(factory, setup.Access);
        var device = await CreateAsync(client, setup.Site);
        var now = DateTimeOffset.UtcNow;
        await SeedHistoryEventsAsync(device.Id, setup.Site, now, 3);
        var query = "/api/lpr/events?workshopIds=" + setup.Site;
        var response = await client.GetAsync(query + "&pageSize=1&pageNumber=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await ReadAsync(response);
        Assert.Equal(3, json.GetProperty("totalCount").GetInt64());
        Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(3, json.GetProperty("totalPages").GetInt64());
        var invalid = await ReadAsync(await client.GetAsync(query + "&plateStatus=invalid&association=unassigned"));
        Assert.Equal(1, invalid.GetProperty("totalCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("items")[0].GetProperty("plateNormalized").ValueKind);
        var partial = await ReadAsync(await client.GetAsync(query + "&plate=012&plateMatch=contains"));
        Assert.Equal(2, partial.GetProperty("totalCount").GetInt64());
        var missing = await ReadAsync(await client.GetAsync(query + "&confidenceMode=missing"));
        Assert.Equal(0, missing.GetProperty("totalCount").GetInt64());
        var literal = await ReadAsync(await client.GetAsync(query + "&originalText=" + Uri.EscapeDataString("=%_[bad]")));
        Assert.Equal(1, literal.GetProperty("totalCount").GetInt64());
        var id = invalid.GetProperty("items")[0].GetProperty("eventId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/lpr/events/{id:D}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(query + "&pageSize=201")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(query + "&pageSize=1&pageSize=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(query + "&from=2026-11-19T01:00:00&to=2026-11-19T02:00:00")).StatusCode);
        foreach (var invalidBody in new[]
        {
            "{\"kind\":\"events\",\"filters\":{\"From\":\"2026-11-19T01:00:00\",\"To\":\"2026-11-19T02:00:00\"}}",
            "{\"kind\":\"events\",\"filters\":{\"PageNumber\":1}}",
            "{\"kind\":\"events\",\"filters\":{\"pageNumber\":1}}",
            "{\"kind\":\"events\",\"filters\":{\"direction\":\"entry\",\"direction\":\"exit\"}}"
        })
        {
            using var body = new StringContent(invalidBody, System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/history/exports", body)).StatusCode);
        }
        var beyond = await ReadAsync(await client.GetAsync(query + "&pageNumber=2147483647&pageSize=200"));
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        await using var db = database.CreateContext();
        var audits = await db.Set<HistoryAccessAuditRow>().Where(x => x.Operation == "ReadEvents").ToArrayAsync();
        Assert.NotEmpty(audits);
        Assert.All(audits, x => Assert.Equal("AuthorizedRelease", x.Status));
        Assert.True(await db.Set<HistoryAccessAuditWorkshopRow>().AnyAsync(x => x.WorkshopId == setup.Site));
    }

    [SqlFact]
    public async Task HistoryExcelExportsAllRowsAsTextAndAuditFailureBlocksDelivery()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var client = Client(factory, setup.Access);
        var device = await CreateAsync(client, setup.Site);
        await SeedHistoryEventsAsync(device.Id, setup.Site, DateTimeOffset.UtcNow, 3);
        var response = await client.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { workshopIds = new[] { setup.Site } }, order = "asc" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        using var buffer = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var workbook = SpreadsheetDocument.Open(buffer, false);
        var rows = workbook.WorkbookPart!.WorksheetParts.First().Worksheet.Descendants<Row>().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Empty(workbook.WorkbookPart.WorksheetParts.SelectMany(x => x.Worksheet.Descendants<CellFormula>()));
        Assert.Contains(rows.SelectMany(x => x.Descendants<Text>()), x => x.Text == "=%_[bad]");
        using var broken = Factory(factory, services => services.Replace(ServiceDescriptor.Scoped<IHistoryAccessAuditStore, FailedHistoryAudit>()));
        using var brokenClient = Client(broken, setup.Access);
        var blocked = await brokenClient.GetAsync("/api/lpr/events?workshopIds=" + setup.Site);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("history_audit_unavailable", (await ReadAsync(blocked)).GetProperty("code").GetString());
        blocked = await brokenClient.PostAsJsonAsync("/api/history/exports", new { kind = "events", filters = new { workshopIds = new[] { setup.Site } } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.NotEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", blocked.Content.Headers.ContentType?.MediaType);
    }

    [SqlFact]
    public async Task HistoryTimelineSelectorsAndEmptyStaysRespectTheContract()
    {
        using var factory = Factory();
        var setup = await SetupAsync(factory);
        using var client = Client(factory, setup.Access);
        var device = await CreateAsync(client, setup.Site);
        await SeedHistoryEventsAsync(device.Id, setup.Site, DateTimeOffset.UtcNow, 3);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHistoryReadSession>().ExecuteAsync(ct =>
                scope.ServiceProvider.GetRequiredService<IHistoryQuery>().ReadAsync(new ListHistoryDevicesQuery(new()),
                    new(new(Guid.NewGuid(), Guid.NewGuid(), "administrator"), [setup.Site], false), null, DateTimeOffset.UtcNow, false, ct), default);
        }
        foreach (var path in new[] { "/api/stays", "/api/vehicles/present", "/api/history/devices", "/api/history/locations",
            "/api/vehicles/timeline?plate=0012BCD" })
        {
            var response = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") + "workshopIds=" + setup.Site);
            Assert.True(response.StatusCode == HttpStatusCode.OK, path + ": " + await response.Content.ReadAsStringAsync());
            Assert.True((await ReadAsync(response)).TryGetProperty("totalCount", out _));
        }
    }
    private async Task SeedHistoryEventsAsync(Guid device, string site, DateTimeOffset now, int count)
    {
        await using var db = database.CreateContext();
        for (var i = 0; i < count; i++) db.LprEvents.Add(new()
        {
            EventId = Guid.NewGuid(),
            DeviceId = device,
            WorkshopId = site,
            CapturedAt = now.AddTicks(-i),
            ReceivedAt = now,
            PlateOriginal = i == 0 ? "=%_[bad]" : "0012-BCD",
            PlateNormalized = i == 0 ? "=%_[BAD]" : "0012BCD",
            PlateStatus = i == 0 ? "invalid" : "valid",
            PlateFormat = i == 0 ? null : "spanish_national",
            PlateRulesVersion = "es-v1",
            Confidence = 0,
            Direction = LprDirection.Entry
        });
        await db.SaveChangesAsync();
    }
    private sealed class FailedHistoryAudit : IHistoryAccessAuditStore
    {
        public Task AppendAsync(HistoryAudit audit, CancellationToken cancellationToken) =>
            throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
    }
    private sealed class WaitingHistoryWriter : IHistoryWorkbookWriter
    {
        public Task<Outcome<HistoryExport>> WriteAsync(HistoryPage page, HistoryKind kind, CancellationToken ct) =>
            new TaskCompletionSource<Outcome<HistoryExport>>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
    }
    private static string[] ReadSharedLog(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n');
    }
    private async Task SaveHistoryPlanAsync(string site, DateTimeOffset from, DateTimeOffset to, string folder)
    {
        await using var db = database.CreateContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SET STATISTICS XML ON; SELECT COUNT_BIG(*) FROM [LprEvents] WHERE [WorkshopId] COLLATE Latin1_General_100_BIN2 = @site AND [CapturedAt] >= @from AND [CapturedAt] < @to; SET STATISTICS XML OFF;";
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@site", site));
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@from", from));
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@to", to));
        await using var reader = await command.ExecuteReaderAsync();
        string? plan = null;
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount != 1) continue;
                var value = reader.GetValue(0);
                var text = value is System.Data.SqlTypes.SqlXml xml ? xml.Value : value as string;
                if (text?.Contains("ShowPlanXML", StringComparison.Ordinal) == true) plan = text;
            }
        } while (await reader.NextResultAsync());
        Assert.NotNull(plan);
        Assert.Contains("ShowPlanXML", plan);
        await File.WriteAllTextAsync(Path.Combine(folder, "capture-count.sqlplan"), plan);
    }
}
