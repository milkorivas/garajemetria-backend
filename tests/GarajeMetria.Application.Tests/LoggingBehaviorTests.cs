using System.Diagnostics;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.Application.Tests;

public sealed class LoggingBehaviorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletionRecordsOnlySafeMetadataAndPreservesTheResponse(bool success)
    {
        var logger = new CaptureLogger<LoggingBehavior<LoginCommand, Outcome<string>>>();
        var behavior = new LoggingBehavior<LoginCommand, Outcome<string>>(logger);
        var response = success ? Outcome<string>.Success("private-response-token") : Outcome<string>.Fail(FailureCode.InvalidCredentials);
        var previous = Activity.Current;
        var result = await behavior.Handle(new("private-user", "private-password"), _ =>
        {
            Assert.NotNull(Activity.Current);
            return Task.FromResult(response);
        }, default);

        Assert.Same(response, result);
        Assert.Same(previous, Activity.Current);
        var completed = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains(success ? "success" : "InvalidCredentials", completed.Message);
        Assert.Contains("ElapsedMs", completed.Properties.Keys);
        Assert.Contains("LoginCommand", completed.Message);
        Assert.DoesNotContain("private-", string.Join(' ', logger.Entries.Select(e => e.Message)));
        Assert.All(logger.Entries, e => Assert.Null(e.Exception));
        Assert.Contains(logger.Scopes, s => s.ContainsKey("TraceId") && s["TraceId"] is string { Length: 32 });
    }

    [Fact]
    public async Task ValidationRejectionIsLoggedWithoutResolvingTheHandlerDependencies()
    {
        var logger = new CaptureLogger<LoggingBehavior<LoginCommand, Outcome<AuthenticationResult>>>();
        using var services = new ServiceCollection()
            .AddSingleton(new AuthenticationOptions { MaxUsernameLength = 256, MaxPasswordLength = 1024 })
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILogger<LoggingBehavior<LoginCommand, Outcome<AuthenticationResult>>>>(logger)
            .AddApplication().BuildServiceProvider();
        var result = await services.GetRequiredService<ISender>().Send(new LoginCommand("bad@user", "private-password"));
        Assert.Equal(FailureCode.InvalidInput, result.Error?.Code);
        Assert.Contains("InvalidInput", Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message);
    }

    [Fact]
    public async Task RequestedCancellationIsLoggedAtDebugAndPropagated()
    {
        var logger = new CaptureLogger<LoggingBehavior<LoginCommand, bool>>();
        var behavior = new LoggingBehavior<LoginCommand, bool>(logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException(cancellation.Token);
        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() => behavior.Handle(new("user", "password"),
            _ => Task.FromException<bool>(failure), cancellation.Token)));
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Debug, e.Level));
    }

    [Fact]
    public async Task ExceptionAndUnrequestedCancellationArePropagatedWithoutLoggingAnError()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("private-exception"), new OperationCanceledException() })
        {
            var logger = new CaptureLogger<LoggingBehavior<LoginCommand, bool>>();
            var behavior = new LoggingBehavior<LoginCommand, bool>(logger);
            var caught = await Record.ExceptionAsync(() => behavior.Handle(new("user", "password"),
                _ => Task.FromException<bool>(failure), default));
            Assert.Same(failure, caught);
            Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
        }
    }

    [Fact]
    public async Task ExistingActivityIsPreservedAndNonOutcomeResponsesAreNotInspected()
    {
        using var activity = new Activity("test").Start();
        var logger = new CaptureLogger<LoggingBehavior<LoginCommand, bool>>();
        var result = await new LoggingBehavior<LoginCommand, bool>(logger).Handle(new("user", "password"), _ => Task.FromResult(false), default);
        Assert.False(result);
        Assert.Same(activity, Activity.Current);
        Assert.Contains("completed", Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, Dictionary<string, object?> Properties)> Entries { get; } = [];
        public List<Dictionary<string, object?>> Scopes { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(((IEnumerable<KeyValuePair<string, object?>>)state).ToDictionary());
            return null;
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary()));
    }
}
