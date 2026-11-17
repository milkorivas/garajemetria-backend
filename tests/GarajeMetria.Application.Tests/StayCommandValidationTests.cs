using GarajeMetria.Application;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.Application.Tests;

public sealed class StayCommandValidationTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task ClosingRequiresTheExactEndOfTheLocalDay(int ticksFromEnd, bool valid)
    {
        var calendar = new Calendar(); var date = new DateOnly(2026, 11, 16);
        var clock = new Clock(calendar.GetDay(date).End.AddTicks(ticksFromEnd));
        var result = await new CloseStayDayValidator(calendar, clock).ValidateAsync(new CloseStayDayCommand(Guid.NewGuid(), date));
        Assert.Equal(valid, result.IsValid);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public async Task InvalidInternalClosureIsRejectedBeforeResolvingAnyPersistenceHandler()
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection(); services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddApplication(); services.AddStayClosureValidation();
        services.AddSingleton<IWorkshopCalendar>(new Calendar()); services.AddSingleton<ILprClock>(clock);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ISender>().Send(new CloseStayDayCommand(Guid.Empty, DateOnly.MinValue));
        Assert.Equal(FailureCode.InvalidInput, result.Error?.Code);
        Assert.Equal(0, clock.Reads);
        Assert.False((object)new CloseStayDayCommand(Guid.NewGuid(), new(2026, 11, 16)) is ILprAdministrationRequest);
        Assert.IsAssignableFrom<ILprAdministrationRequest>(new UpdateDeviceLocationCommand(Guid.NewGuid(), "Access", Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task LocationLengthAndOldRegistrationShapeAreValidated(int length, bool valid)
    {
        var description = new string('a', length);
        Assert.Equal(valid, (await new UpdateDeviceLocationValidator().ValidateAsync(new UpdateDeviceLocationCommand(Guid.NewGuid(), description, Guid.NewGuid()))).IsValid);
        Assert.True((await new RegisterDeviceValidator().ValidateAsync(new RegisterDeviceCommand("Camera", "site"))).IsValid);
        Assert.Equal(valid, (await new RegisterDeviceValidator().ValidateAsync(new RegisterDeviceCommand("Camera", "site") { LocationDescription = description })).IsValid);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("a\nb")]
    public async Task InvalidLocationsDoNotReachDomain(string description) =>
        Assert.False((await new UpdateDeviceLocationValidator().ValidateAsync(new UpdateDeviceLocationCommand(Guid.NewGuid(), description, Guid.NewGuid()))).IsValid);

    [Fact]
    public async Task LocationRequiresResourceIdentityAndExpectedVersion()
    {
        var validator = new UpdateDeviceLocationValidator();
        Assert.False((await validator.ValidateAsync(new UpdateDeviceLocationCommand(Guid.Empty, "Access", Guid.NewGuid()))).IsValid);
        Assert.False((await validator.ValidateAsync(new UpdateDeviceLocationCommand(Guid.NewGuid(), "Access", Guid.Empty))).IsValid);
        Assert.False((await validator.ValidateAsync(new UpdateDeviceLocationCommand(Guid.NewGuid(), new string('\uD800', 1), Guid.NewGuid()))).IsValid);
        Assert.False((await validator.ValidateAsync(new UpdateDeviceLocationCommand(Guid.NewGuid(), null!, Guid.NewGuid()))).IsValid);
        Assert.True((await validator.ValidateAsync(new UpdateDeviceLocationCommand(Guid.NewGuid(), " Access ", Guid.NewGuid()))).IsValid);
    }

    private sealed class Calendar : IWorkshopCalendar
    {
        public DateOnly GetLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
        public StayDayWindow GetDay(DateOnly date) => new(date, new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }
    private sealed class Clock(DateTimeOffset now) : ILprClock
    {
        public int Reads { get; private set; }
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct) { Reads++; return Task.FromResult(now); }
    }
}
