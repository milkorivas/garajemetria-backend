using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.IntegrationTests;

public sealed class LprRunbookTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [Fact]
    public void CollectionExportsOnlyNonSecretVariablesAndNoSavedResponses()
    {
        using var collection = LoadCollection();
        string[] allowed = ["baseUrl", "adminUsername", "siteId", "deviceName", "deviceId", "clientId",
            "deviceEtag", "eventId", "invalidEventId", "captureTimestamp", "receivedAt"];
        foreach (var variable in collection.RootElement.GetProperty("variable").EnumerateArray())
        {
            var key = variable.GetProperty("key").GetString()!;
            Assert.Contains(key, allowed);
            if (key is not ("baseUrl" or "deviceName")) Assert.Equal("", variable.GetProperty("value").GetString());
        }
        foreach (var item in collection.RootElement.GetProperty("item").EnumerateArray())
            Assert.Empty(item.GetProperty("response").EnumerateArray());
        var text = collection.RootElement.GetRawText();
        Assert.Contains("pm.vault.set", text);
        Assert.Contains("pm.vault.get", text);
        Assert.DoesNotContain("console.", text);
        Assert.DoesNotContain("pm.environment.set", text);
        Assert.DoesNotContain("disableSslVerification", text);
    }

    [SqlFact]
    public async Task ExportedHttpTemplatesCompleteTheSyntheticLifecycleAgainstIsolatedSql()
    {
        using var collection = LoadCollection();
        using var factory = new AuthenticationFactory(database);
        using var client = factory.Client();
        var site = "runbook-" + Guid.NewGuid().ToString("N");
        await using (var db = database.CreateContext())
        {
            db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic runbook", IsActive = true });
            if (!await db.Catalog.AnyAsync()) db.Catalog.Add(new() { Id = 1, LastPublishedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        // Execute the exported HTTP templates, not the Postman JavaScript/Vault runtime.
        var values = new Dictionary<string, string>
        {
            ["baseUrl"] = "https://localhost",
            ["adminUsername"] = "user",
            ["adminPassword"] = "synthetic-only",
            ["siteId"] = site,
            ["deviceName"] = "Synthetic runbook",
            ["eventId"] = Guid.NewGuid().ToString("D"),
            ["invalidEventId"] = Guid.NewGuid().ToString("D"),
            ["captureTimestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
        int[] statuses = [200, 200, 201, 200, 201, 200, 409, 201, 200, 200, 401, 200, 200, 204, 401, 401, 204, 200, 200, 200, 204, 401];
        var steps = collection.RootElement.GetProperty("item").EnumerateArray().ToArray();
        Assert.Equal(statuses.Length, steps.Length);
        var evidence = new List<object>();
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            using var request = BuildRequest(step.GetProperty("request"), values);
            using var response = await client.SendAsync(request);
            Assert.True((int)response.StatusCode == statuses[index], $"Runbook step {index:D2}: expected {statuses[index]}, got {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync();
            using var document = string.IsNullOrEmpty(body) ? null : JsonDocument.Parse(body);
            var json = document?.RootElement ?? default;
            string? code = null;
            switch (index)
            {
                case 0: values["csrfToken"] = json.GetProperty("requestToken").GetString()!; break;
                case 1:
                    Assert.Equal("administrator", json.GetProperty("user").GetProperty("role").GetString());
                    values["adminToken"] = json.GetProperty("accessToken").GetString()!;
                    break;
                case 2:
                    values["deviceId"] = json.GetProperty("device").GetProperty("id").GetString()!;
                    values["clientId"] = json.GetProperty("credentials").GetProperty("clientId").GetString()!;
                    values["deviceSecret"] = json.GetProperty("credentials").GetProperty("clientSecret").GetString()!;
                    break;
                case 3 or 11 or 18:
                    values["deviceToken"] = json.GetProperty("access_token").GetString()!;
                    Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
                    Assert.Equal("lpr.events.write", json.GetProperty("scope").GetString());
                    Assert.False(json.TryGetProperty("refresh_token", out _));
                    break;
                case 4:
                    Assert.False(json.GetProperty("duplicate").GetBoolean());
                    Assert.Equal("valid", json.GetProperty("plate_status").GetString());
                    values["receivedAt"] = json.GetProperty("received_at").GetString()!;
                    break;
                case 5 or 12 or 19:
                    Assert.True(json.GetProperty("duplicate").GetBoolean());
                    Assert.Equal(values["receivedAt"], json.GetProperty("received_at").GetString());
                    break;
                case 6:
                    code = json.GetProperty("code").GetString();
                    Assert.Equal("event_conflict", code);
                    break;
                case 7:
                    Assert.Equal("invalid", json.GetProperty("plate_status").GetString());
                    Assert.False(json.GetProperty("duplicate").GetBoolean());
                    break;
                case 8:
                    Assert.False(json.TryGetProperty("credentials", out _));
                    Assert.False(json.TryGetProperty("version", out _));
                    break;
                case 9 or 17:
                    Assert.NotEqual(values["deviceSecret"], json.GetProperty("credentials").GetProperty("clientSecret").GetString());
                    values["deviceSecret"] = json.GetProperty("credentials").GetProperty("clientSecret").GetString()!;
                    if (index == 17)
                    {
                        Assert.Equal(values["deviceId"], json.GetProperty("device").GetProperty("id").GetString());
                        Assert.Equal(site, json.GetProperty("device").GetProperty("workshopId").GetString());
                    }
                    break;
                case 10 or 14 or 21:
                    code = json.GetProperty("code").GetString();
                    Assert.Equal("invalid_device_token", code);
                    break;
                case 15:
                    code = json.GetProperty("error").GetString();
                    Assert.Equal("invalid_client", code);
                    Assert.False(json.TryGetProperty("access_token", out _));
                    break;
            }
            if (response.Headers.ETag is { } tag) values["deviceEtag"] = tag.ToString();
            evidence.Add(new { step = index, status = (int)response.StatusCode, code });
        }
        await using (var db = database.CreateContext())
        {
            var id = Guid.Parse(values["deviceId"]);
            var device = await db.LprDevices.SingleAsync(x => x.Id == id);
            Assert.False(device.IsActive);
            Assert.False(device.HasCredentials);
            Assert.Equal(2, await db.LprEvents.CountAsync(x => x.DeviceId == id));
            Assert.Equal(6, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == id));
        }
        Assert.Equal(1, factory.DirectoryAdapter.Calls);
        var path = Path.Combine(RepositoryRoot(), "artifacts", "agent-checks", "rf003-runbook-flow.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            completedUtc = DateTimeOffset.UtcNow,
            outcome = "passed",
            transport = "WebApplicationFactory",
            database = "isolated SQL Server",
            directory = "synthetic adapter",
            postmanRuntimeExecuted = false,
            persistedEvents = 2,
            administrativeAudits = 6,
            finalDeviceActive = false,
            steps = evidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static HttpRequestMessage BuildRequest(JsonElement template, Dictionary<string, string> values)
    {
        string Resolve(string value) => Regex.Replace(value, @"\{\{([^{}]+)\}\}", match => values[match.Groups[1].Value]);
        var request = new HttpRequestMessage(new HttpMethod(template.GetProperty("method").GetString()!), Resolve(template.GetProperty("url").GetString()!));
        if (template.TryGetProperty("body", out var body))
        {
            request.Content = body.GetProperty("mode").GetString() == "raw"
                ? new StringContent(Resolve(body.GetProperty("raw").GetString()!), Encoding.UTF8, "application/json")
                : new FormUrlEncodedContent(body.GetProperty("urlencoded").EnumerateArray()
                    .Select(field => new KeyValuePair<string, string>(field.GetProperty("key").GetString()!, Resolve(field.GetProperty("value").GetString()!))));
        }
        foreach (var header in template.GetProperty("header").EnumerateArray())
            if (header.GetProperty("key").GetString() != "Content-Type")
                request.Headers.TryAddWithoutValidation(header.GetProperty("key").GetString()!, Resolve(header.GetProperty("value").GetString()!));
        var auth = template.GetProperty("auth");
        if (auth.GetProperty("type").GetString() == "bearer")
            request.Headers.Authorization = new("Bearer", Resolve(auth.GetProperty("bearer")[0].GetProperty("value").GetString()!));
        if (auth.GetProperty("type").GetString() == "basic")
        {
            var fields = auth.GetProperty("basic").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => Resolve(x.GetProperty("value").GetString()!));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(
                Uri.EscapeDataString(fields["username"]) + ":" + Uri.EscapeDataString(fields["password"]))));
        }
        return request;
    }

    private static JsonDocument LoadCollection() => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "http", "RF-003.postman_collection.json")));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GarajeMetria.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
