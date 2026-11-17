using FluentValidation;
using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Application.Lpr;

public sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceValidator()
    {
        RuleFor(x => x.Name).Must(LprValidation.ValidName).WithMessage("Invalid device name.");
        RuleFor(x => x.WorkshopId).Must(x => LprEventRules.ValidText(x, 256)).WithMessage("Invalid workshop identifier.");
        RuleFor(x => x.LocationDescription).Must(x => x is null || LprValidation.ValidLocation(x))
            .WithMessage("Invalid device location.");
    }
}
public sealed class UpdateDeviceLocationValidator : AbstractValidator<UpdateDeviceLocationCommand>
{
    public UpdateDeviceLocationValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).NotEmpty();
        RuleFor(x => x.LocationDescription).Must(LprValidation.ValidLocation).WithMessage("Invalid device location.");
    }
}
public sealed class RenameDeviceValidator : AbstractValidator<RenameDeviceCommand>
{
    public RenameDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).NotEmpty();
        RuleFor(x => x.Name).Must(LprValidation.ValidName).WithMessage("Invalid device name.");
    }
}
public sealed class DeactivateDeviceValidator : AbstractValidator<DeactivateDeviceCommand>
{
    public DeactivateDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).NotEmpty();
    }
}
public sealed class ReactivateDeviceValidator : AbstractValidator<ReactivateDeviceCommand>
{
    public ReactivateDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).NotEmpty();
    }
}
public sealed class RotateDeviceCredentialsValidator : AbstractValidator<RotateDeviceCredentialsCommand>
{
    public RotateDeviceCredentialsValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).NotEmpty();
    }
}
public sealed class RevokeDeviceCredentialsValidator : AbstractValidator<RevokeDeviceCredentialsCommand>
{
    public RevokeDeviceCredentialsValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).NotEmpty();
    }
}
public sealed class ListDevicesValidator : AbstractValidator<ListDevicesQuery>
{
    public ListDevicesValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x).Must(x => x.PageNumber > 0 && x.PageSize > 0 && (long)(x.PageNumber - 1) * x.PageSize <= int.MaxValue)
            .WithMessage("Invalid pagination offset.");
        RuleFor(x => x.WorkshopId).Must(x => x is null || LprEventRules.ValidText(x, 256)).WithMessage("Invalid workshop identifier.");
    }
}
public sealed class GetDeviceValidator : AbstractValidator<GetDeviceQuery>
{
    public GetDeviceValidator() => RuleFor(x => x.DeviceId).NotEmpty();
}
public sealed class ReceiveLprEventValidator : AbstractValidator<ReceiveLprEventCommand>
{
    public ReceiveLprEventValidator()
    {
        RuleFor(x => x.EventId).Must(LprEventRules.ValidEventId).WithMessage("Invalid event identifier.");
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.WorkshopId).Must(x => LprEventRules.ValidText(x, 256)).WithMessage("Invalid workshop identifier.");
        RuleFor(x => x.PlateOriginal).Must(LprEventRules.ValidPlateText).WithMessage("Invalid plate text.");
        RuleFor(x => x.Confidence).Must(LprEventRules.ValidConfidence).WithMessage("Invalid confidence.");
        RuleFor(x => x.Direction).Must(x => x is null || Enum.IsDefined(x.Value)).WithMessage("Invalid direction.");
    }
}
public sealed class PrepareDeviceTokenGrantValidator : AbstractValidator<PrepareDeviceTokenGrantCommand>
{
    public PrepareDeviceTokenGrantValidator()
    {
        // Protocol syntax, scope/grant checks and client authentication remain in OpenIddict.
        RuleFor(x => x.ClientId).Must(x => LprEventRules.ValidText(x, 100) && x!.StartsWith("lpr_", StringComparison.Ordinal) &&
            Guid.TryParseExact(x.AsSpan(4), "N", out var id) && id != Guid.Empty).WithMessage("Invalid client credentials.");
        RuleFor(x => x.ClientSecret).Must(x => !string.IsNullOrEmpty(x)).WithMessage("Invalid client credentials.");
    }
}
public sealed class ValidateDeviceAccessValidator : AbstractValidator<ValidateDeviceAccessQuery>
{
    public ValidateDeviceAccessValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.WorkshopId).Must(x => LprEventRules.ValidText(x, 256)).WithMessage("Invalid workshop identifier.");
    }
}

internal static class LprValidation
{
    internal static bool ValidLocation(string? value) => value is not null && !value.Any(char.IsControl) &&
        LprEventRules.ValidText(value.Trim(), 256);

    internal static bool ValidName(string? value) => value is not null && !value.Any(char.IsControl) &&
        LprEventRules.ValidText(value.Trim(), 128);
}
