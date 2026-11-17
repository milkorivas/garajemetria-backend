using System.Globalization;
using System.Text;
using System.Text.Json;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Api.Users;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Domain.Authentication;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;

namespace GarajeMetria.Api.Lpr;

public sealed record LprDeviceResponse(Guid Id, string Name, string WorkshopId, bool IsActive, bool HasCredentials,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string? LocationDescription { get; init; }
    public DateTimeOffset? LocationRecordedAt { get; init; }
    public override string ToString() => nameof(LprDeviceResponse);
}
public sealed record LprDevicePageResponse(IReadOnlyList<LprDeviceResponse> Items, int PageNumber, int PageSize, int TotalCount);
public sealed record LprProvisioningResponse(LprDeviceResponse Device, DeviceCredentials Credentials)
{
    public override string ToString() => nameof(LprProvisioningResponse);
}
public sealed record LprRotationResponse(DeviceCredentials Credentials)
{
    public override string ToString() => nameof(LprRotationResponse);
}

public static class LprAdministrationEndpoints
{
    public static void MapLprAdministrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/lpr/devices").WithTags("LPR Devices")
            .RequireAuthorization(new AuthorizationPolicyBuilder("Bearer").RequireRole(UserRoles.Administrator).Build())
            .WithMetadata(new LprTrafficMetadata(LprTraffic.Administration));
        group.MapGet("", async (HttpContext context, ISender sender) =>
        {
            var query = context.Request.Query;
            if (query.Keys.Any(x => x is not ("pageNumber" or "pageSize" or "workshopId" or "isActive")) || query.Any(x => x.Value.Count != 1) ||
                !Integer(query, "pageNumber", 1, out var page) || !Integer(query, "pageSize", 20, out var size)) return LprProblems.Invalid();
            bool? active = null;
            if (query.ContainsKey("isActive"))
            {
                if (!bool.TryParse(query["isActive"], out var value)) return LprProblems.Invalid();
                active = value;
            }
            var result = await sender.Send(new ListDevicesQuery(page, size, query.ContainsKey("workshopId") ? query["workshopId"].ToString() : null, active), context.RequestAborted);
            return result.Succeeded ? Results.Ok(new LprDevicePageResponse(result.Value!.Items.Select(Map).ToArray(),
                result.Value.PageNumber, result.Value.PageSize, result.Value.TotalCount)) : Error(context, result.Error!);
        }).WithName("ListLprDevices").WithSummary("Consultar dispositivos con paginación")
            .Produces<LprDevicePageResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(500).ProducesProblem(503);

        group.MapGet("/{id}", async (string id, HttpContext context, ISender sender) =>
        {
            if (!Id(id, out var deviceId) || context.Request.Query.Count != 0) return LprProblems.Invalid();
            var result = await sender.Send(new GetDeviceQuery(deviceId), context.RequestAborted);
            if (!result.Succeeded) return Error(context, result.Error!);
            context.Response.Headers.ETag = UserAdministrationEndpoints.ETag(result.Value!.Version);
            return Results.Ok(Map(result.Value));
        }).WithName("GetLprDevice").WithSummary("Consultar dispositivo y ETag")
            .Produces<LprDeviceResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(500).ProducesProblem(503);

        group.MapPost("", async (HttpContext context, ISender sender) =>
        {
            if (context.Request.Query.Count != 0) return LprProblems.Invalid();
            var read = await ReadAsync(context.Request, ["name", "workshopId"], context.RequestAborted, ["locationDescription"]);
            if (read.Error is not null) return read.Error;
            var result = await sender.Send(new RegisterDeviceCommand(read.Values!["name"], read.Values["workshopId"])
            { LocationDescription = read.Values.GetValueOrDefault("locationDescription") }, context.RequestAborted);
            if (!result.Succeeded) return Error(context, result.Error!);
            var value = result.Value!;
            context.Response.Headers.ETag = UserAdministrationEndpoints.ETag(value.Device.Version);
            return Results.Created(context.Request.PathBase + "/api/lpr/devices/" + value.Device.Id.ToString("D"), new LprProvisioningResponse(Map(value.Device), value.Credentials));
        }).WithName("RegisterLprDevice").WithSummary("Registrar un dispositivo y generar sus credenciales")
            .Produces<LprProvisioningResponse>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(500).ProducesProblem(503);

        Mutation(group.MapPut("/{id}/name", async (string id, HttpContext context, ISender sender) =>
        {
            var error = Precondition(context, id, out var deviceId, out var version);
            if (error is not null) return error;
            var read = await ReadAsync(context.Request, ["name"], context.RequestAborted);
            if (read.Error is not null) return read.Error;
            var result = await sender.Send(new RenameDeviceCommand(deviceId, read.Values!["name"], version), context.RequestAborted);
            if (!result.Succeeded) return Error(context, result.Error!);
            context.Response.Headers.ETag = UserAdministrationEndpoints.ETag(result.Value!.Version);
            return Results.NoContent();
        }).WithName("RenameLprDevice").WithSummary("Cambiar el nombre del dispositivo")).Produces(204).ProducesProblem(415);

        Mutation(group.MapPut("/{id}/location", async (string id, HttpContext context, ISender sender) =>
        {
            var error = Precondition(context, id, out var deviceId, out var version);
            if (error is not null) return error;
            var read = await ReadAsync(context.Request, ["locationDescription"], context.RequestAborted);
            if (read.Error is not null) return read.Error;
            var result = await sender.Send(new UpdateDeviceLocationCommand(deviceId, read.Values!["locationDescription"], version), context.RequestAborted);
            if (!result.Succeeded) return Error(context, result.Error!);
            context.Response.Headers.ETag = UserAdministrationEndpoints.ETag(result.Value!.Version);
            return Results.NoContent();
        }).WithName("UpdateLprDeviceLocation").WithSummary("Cambiar la ubicación descriptiva del dispositivo")).Produces(204).ProducesProblem(415);

        MapState(group, "deactivate", "DeactivateLprDevice", "Desactivar el dispositivo", async (id, version, sender, ct) =>
        {
            var result = await sender.Send(new DeactivateDeviceCommand(id, version), ct);
            return (result.Value?.Version, (object?)null, result.Error);
        });
        MapState(group, "credentials/revoke", "RevokeLprCredentials", "Revocar credenciales", async (id, version, sender, ct) =>
        {
            var result = await sender.Send(new RevokeDeviceCredentialsCommand(id, version), ct);
            return (result.Value?.Version, (object?)null, result.Error);
        });
        MapState(group, "reactivate", "ReactivateLprDevice", "Reactivar y generar nuevas credenciales", async (id, version, sender, ct) =>
        {
            var result = await sender.Send(new ReactivateDeviceCommand(id, version), ct);
            return (result.Value?.Device.Version, result.Value is null ? null : new LprProvisioningResponse(Map(result.Value.Device), result.Value.Credentials), result.Error);
        }).Produces<LprProvisioningResponse>();
        MapState(group, "credentials/rotate", "RotateLprCredentials", "Rotar las credenciales", async (id, version, sender, ct) =>
        {
            var result = await sender.Send(new RotateDeviceCredentialsCommand(id, version), ct);
            return (result.Value?.Version, result.Value is null ? null : new LprRotationResponse(result.Value.Credentials), result.Error);
        }).Produces<LprRotationResponse>();
    }

    private static RouteHandlerBuilder MapState(RouteGroupBuilder group, string path, string name, string summary,
        Func<Guid, Guid, ISender, CancellationToken, Task<(Guid? Version, object? Body, Failure? Error)>> action)
    {
        var builder = Mutation(group.MapPost("/{id}/" + path, async (string id, HttpContext context, ISender sender) =>
        {
            var error = Precondition(context, id, out var deviceId, out var version);
            if (error is not null) return error;
            var read = await ReadAsync(context.Request, [], context.RequestAborted);
            if (read.Error is not null) return read.Error;
            var result = await action(deviceId, version, sender, context.RequestAborted);
            if (result.Error is not null) return Error(context, result.Error);
            context.Response.Headers.ETag = UserAdministrationEndpoints.ETag(result.Version!.Value);
            return result.Body is null ? Results.NoContent() : Results.Ok(result.Body);
        }).WithName(name).WithSummary(summary));
        if (path is "deactivate" or "credentials/revoke") builder.Produces(204);
        return builder;
    }
    private static RouteHandlerBuilder Mutation(RouteHandlerBuilder b) => b.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
        .ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(428)
        .ProducesProblem(429).ProducesProblem(500).ProducesProblem(503);
    private static IResult? Precondition(HttpContext context, string id, out Guid deviceId, out Guid version)
    {
        version = default;
        if (!Id(id, out deviceId) || context.Request.Query.Count != 0) return LprProblems.Invalid();
        var header = context.Request.Headers.IfMatch;
        if (header.Count == 0) return ApiProblems.Create(428, "precondition_required", "Se requiere If-Match.");
        return header.Count == 1 && UserAdministrationEndpoints.TryETag(header[0], out version) ? null : LprProblems.Invalid();
    }
    private static bool Id(string text, out Guid id) => Guid.TryParseExact(text, "D", out id) && id != Guid.Empty;
    private static bool Integer(IQueryCollection query, string name, int fallback, out int value)
    { value = fallback; return !query.ContainsKey(name) || int.TryParse(query[name], NumberStyles.None, CultureInfo.InvariantCulture, out value); }
    private static IResult Error(HttpContext context, Failure failure)
    {
        if (failure.RetryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return LprProblems.Failure(failure);
    }
    private static LprDeviceResponse Map(DeviceDto value) => new(value.Id, value.Name, value.WorkshopId, value.IsActive, value.HasCredentials, value.CreatedAt, value.UpdatedAt)
    { LocationDescription = value.LocationDescription, LocationRecordedAt = value.LocationRecordedAt };

    private static async Task<(Dictionary<string, string>? Values, IResult? Error)> ReadAsync(HttpRequest request, string[] fields, CancellationToken ct, string[]? optionalFields = null)
    {
        if (fields.Length != 0 && (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.Parameters.Count > 1 ||
            type.Parameters.Any(x => !x.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) ||
            type.Charset.HasValue && !type.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContentEncoding.Count > 1 || request.Headers.ContentEncoding.Count == 1 && request.Headers.ContentEncoding[0] != "identity"))
            return (null, ApiProblems.Create(415, "unsupported_media_type", "Se requiere JSON UTF-8."));
        if (request.ContentLength > 8192) return (null, ApiProblems.Create(413, "payload_too_large", "Cuerpo demasiado grande."));
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (stream.Length + count > 8192) return (null, ApiProblems.Create(413, "payload_too_large", "Cuerpo demasiado grande."));
            await stream.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (fields.Length == 0) return stream.Length == 0 ? (new(), null) : (null, LprProblems.Invalid());
        try
        {
            var bytes = stream.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return (null, LprProblems.Invalid());
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (!(fields.Contains(property.Name, StringComparer.Ordinal) || (optionalFields?.Contains(property.Name, StringComparer.Ordinal) ?? false)) || property.Value.ValueKind != JsonValueKind.String ||
                    !values.TryAdd(property.Name, property.Value.GetString()!)) return (null, LprProblems.Invalid());
            }
            return fields.All(values.ContainsKey) ? (values, null) : (null, LprProblems.Invalid());
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        { return (null, LprProblems.Invalid()); }
    }
}
