using System.Net;
using System.Text.Json;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Api.Diagnostics;
using GarajeMetria.Application.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace GarajeMetria.IntegrationTests;

public sealed class LoggingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "GarajeMetria_LoggingTests_" + Guid.NewGuid().ToString("N"));
    private IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FileLogging:DirectoryPath"] = directory,
        ["Serilog:MinimumLevel:Default"] = "Debug"
    }).Build();

    [Fact]
    public void ConfigurationRejectsMissingRelativeInternalAndInvalidLimits()
    {
        var root = Path.GetFullPath(".");
        foreach (var path in new[] { "", "logs", root, Path.Combine(root, "logs"), AppContext.BaseDirectory })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileLogging:DirectoryPath"] = path }).Build();
            Assert.Throws<InvalidOperationException>(() => FileLogging.ReadOptions(configuration, root));
        }
        foreach (var key in new[] { "FileSizeLimitBytes", "RetainedFileCountLimit", "RetainedDays" })
        {
            var configuration = new ConfigurationBuilder().AddConfiguration(Configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["FileLogging:" + key] = "0" }).Build();
            Assert.Throws<InvalidOperationException>(() => FileLogging.ReadOptions(configuration, root));
        }
        var options = FileLogging.ReadOptions(Configuration, root);
        Assert.Equal(20 * 1024 * 1024, options.FileSizeLimitBytes);
        Assert.Equal(30, options.RetainedFileCountLimit);
        Assert.Equal(14, options.RetainedDays);
    }

    [Fact]
    public void JsonFilesRotateBySizeAndRemoveOldAndExcessFiles()
    {
        var options = FileLogging.ReadOptions(Configuration, Path.GetFullPath("."));
        FileLogging.ValidateAccess(options);
        var old = Path.Combine(directory, $"garajemetria-{Environment.ProcessId}-{DateTime.Today.AddDays(-20):yyyyMMdd}.json");
        File.WriteAllText(old, "old synthetic event");
        var previousProcess = Path.Combine(directory, $"garajemetria-0-{DateTime.Today.AddDays(-20):yyyyMMdd}.json");
        File.WriteAllText(previousProcess, "old process event");
        var unrelated = Path.Combine(directory, "other-application.txt");
        File.WriteAllText(unrelated, "unrelated file");
        options.FileSizeLimitBytes = 512;
        options.RetainedFileCountLimit = 3;
        using (var logger = FileLogging.Configure(new LoggerConfiguration(), options, Configuration).CreateLogger())
            for (var i = 0; i < 20; i++) logger.Information("Synthetic event {Index} {Padding}", i, new string('x', 200));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(previousProcess));
        Assert.True(File.Exists(unrelated));
        var files = Directory.GetFiles(directory, "*.json");
        Assert.InRange(files.Length, 2, 3);
        foreach (var file in files)
        {
            Assert.Contains(DateTime.Today.ToString("yyyyMMdd"), Path.GetFileName(file));
            foreach (var line in File.ReadLines(file))
            {
                using var json = JsonDocument.Parse(line);
                Assert.True(json.RootElement.TryGetProperty("@t", out _));
                Assert.Equal("GarajeMetria.Api", json.RootElement.GetProperty("Application").GetString());
            }
        }
    }

    [Fact]
    public void StartupFailsWithASafeMessageWhenTheDestinationIsNotADirectory()
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "occupied");
        File.WriteAllText(file, "synthetic file");
        var exception = Assert.Throws<InvalidOperationException>(() => FileLogging.ValidateAccess(new() { DirectoryPath = file }));
        Assert.DoesNotContain(directory, exception.Message);
        Assert.IsAssignableFrom<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task AnAlreadyStartedResponseIsLeftToTheFramework()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Response.Body = new MemoryStream();
        var handled = await new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance)
            .TryHandleAsync(context, new InvalidOperationException("private-password"), default);
        Assert.False(handled);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task TransportCancellationDoesNotProduceAProblemOrAnErrorEvent()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddConfiguration(Configuration);
        builder.AddFileLogging();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        await using (var app = builder.Build())
        {
            app.UseMiddleware<RequestLogScopeMiddleware>();
            app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
            app.UseMiddleware<RequestCancellationLoggingMiddleware>();
            app.MapGet("/cancel", (Func<HttpContext, string>)(context =>
            {
                context.RequestAborted = new CancellationToken(canceled: true);
                throw new OperationCanceledException(context.RequestAborted);
            }));
            await app.StartAsync();
            using var client = app.GetTestClient();
            var response = await client.GetAsync("/cancel");
            Assert.Equal(499, (int)response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
            await app.StopAsync();
        }
        var text = string.Join('\n', Directory.GetFiles(directory, "*.json").Select(File.ReadAllText));
        Assert.Contains("Http.Cancelled", text);
        Assert.Contains("OperationCanceledException", text);
        Assert.DoesNotContain("\"@l\":\"Error\"", text);
        Assert.DoesNotContain("Request failed", text);
    }

    [Fact]
    public async Task ConcurrentHostsKeepTheirLoggersIndependentAndReleaseTheirFiles()
    {
        var staticLogger = Log.Logger;
        var firstBuilder = WebApplication.CreateBuilder();
        firstBuilder.Configuration.AddConfiguration(Configuration);
        firstBuilder.AddFileLogging();
        var secondDirectory = Path.Combine(directory, "second-host");
        var secondBuilder = WebApplication.CreateBuilder();
        secondBuilder.Configuration.AddConfiguration(Configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileLogging:DirectoryPath"] = secondDirectory
        });
        secondBuilder.AddFileLogging();
        await using (var first = firstBuilder.Build())
        {
            Assert.Same(staticLogger, Log.Logger);
            await using (var second = secondBuilder.Build())
            {
                Assert.Same(staticLogger, Log.Logger);
                first.Logger.LogInformation("First host before second shutdown");
                second.Logger.LogInformation("Second host event");
            }
            first.Logger.LogInformation("First host after second shutdown");
        }
        var firstFile = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var secondFile = Assert.Single(Directory.GetFiles(secondDirectory, "*.json"));
        Assert.Contains("First host after second shutdown", File.ReadAllText(firstFile));
        Assert.DoesNotContain("Second host event", File.ReadAllText(firstFile));
        Assert.DoesNotContain("First host", File.ReadAllText(secondFile));
        using var firstLock = new FileStream(firstFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var secondLock = new FileStream(secondFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RawExceptionsAndFrameworkPayloadsNeverReachTheFile()
    {
        var options = FileLogging.ReadOptions(Configuration, Path.GetFullPath("."));
        FileLogging.ValidateAccess(options);
        using (var logger = FileLogging.Configure(new LoggerConfiguration(), options, Configuration).CreateLogger())
        {
            logger.ForContext("SourceContext", "ThirdParty.Client").ForContext("Secret", "private-secret")
                .Error(new InvalidOperationException("private-password"), "private-message {Payload}", "private-payload");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Error("SELECT private-token");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Authentication").Warning("private-cookie");
            logger.ForContext("SourceContext", "GarajeMetria.SafeCategory").Information("safe event");
        }
        var text = string.Join('\n', Directory.GetFiles(directory, "*.json").Select(File.ReadAllText));
        Assert.DoesNotContain("private-", text);
        Assert.Contains("GarajeMetria.SafeCategory", text);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        using var error = JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("InvalidOperationException", error.RootElement.GetProperty("ExceptionType").GetString());
        Assert.False(error.RootElement.TryGetProperty("@x", out _));
        Assert.False(error.RootElement.TryGetProperty("Secret", out _));
    }

    [Theory]
    [InlineData(false, 500, "Error")]
    [InlineData(true, 503, "Warning")]
    public async Task HttpErrorsAreLoggedOnceWithSafeClassificationAndProblemCorrelation(bool persistence, int status, string level)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddConfiguration(Configuration);
        builder.AddFileLogging();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
        string requestId;
        await using (var app = builder.Build())
        {
            app.UseMiddleware<RequestLogScopeMiddleware>();
            app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
            app.MapGet("/fail", (Func<string>)(() => throw (persistence
                ? new PersistenceFailureException(FailureCode.ServiceUnavailable, new Exception("private-sql"))
                : new InvalidOperationException("private-password"))));
            await app.StartAsync();
            using var client = app.GetTestClient();
            var response = await client.GetAsync("/fail?private-query=private-token");
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-", body);
            using var problem = JsonDocument.Parse(body);
            requestId = problem.RootElement.GetProperty("traceId").GetString()!;
            await app.StopAsync();
        }
        var lines = Directory.GetFiles(directory, "*.json").SelectMany(File.ReadAllLines).ToArray();
        Assert.DoesNotContain("private-", string.Join('\n', lines));
        var errors = lines.Select(line => JsonDocument.Parse(line)).ToList();
        try
        {
            var error = Assert.Single(errors, e => e.RootElement.TryGetProperty("ExceptionType", out _)).RootElement;
            Assert.Equal(level, error.GetProperty("@l").GetString());
            Assert.Equal(requestId, error.GetProperty("RequestId").GetString());
            Assert.Equal(32, error.GetProperty("TraceId").GetString()!.Length);
            Assert.False(error.TryGetProperty("@x", out _));
        }
        finally { errors.ForEach(e => e.Dispose()); }
    }

    public void Dispose()
    {
        // Only this test's explicitly created directory can be removed.
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task InvalidLoginJsonIsRecordedWithoutItsPayload()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"username\":\"private-user\",\"password\":private-password}"));
        var result = await StrictLoginReader.ReadAsync(context.Request,
            new AuthenticationOptions { MaxLoginBodyBytes = 4096, MaxJsonDepth = 2 }, factory.CreateLogger("LoginTest"), default);
        Assert.NotNull(result.Error);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("Login.InvalidPayload", entry.Properties["Operation"]);
        Assert.Equal("JsonReaderException", entry.Properties["ExceptionType"]);
        Assert.DoesNotContain("private-", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task AntiforgeryExceptionIsRecordedWithoutTokensOrCookies()
    {
        var logs = new RecordingLogProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery();
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = "https";
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/logout";
        context.Request.Headers[AuthenticationEndpoints.CsrfHeader] = "private-token";
        context.Request.Headers.Cookie = AuthenticationEndpoints.AntiforgeryCookie + "=private-cookie";
        var filter = new AuthenticationRequestFilter(new HttpOptions(), new AuthenticationOptions(), new UnusedRateLimitStore(),
            provider.GetRequiredService<IAntiforgery>(), provider.GetRequiredService<ILogger<AuthenticationRequestFilter>>());
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context, []),
            _ => throw new InvalidOperationException("The rejected request must not reach the endpoint."));
        Assert.NotNull(result);
        var entry = Assert.Single(logs.Entries, e => e.Properties.TryGetValue("Operation", out var operation) && Equals(operation, "Antiforgery.ValidationRejected"));
        Assert.Equal("AntiforgeryValidationException", entry.Properties["ExceptionType"]);
        Assert.DoesNotContain("private-", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task ExceptionsEscapingAfterTheResponseStartedAreRecordedAndRethrown()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var failure = new InvalidOperationException("private-password");
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var middleware = new RequestLogScopeMiddleware(_ => Task.FromException(failure), factory.CreateLogger<RequestLogScopeMiddleware>());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context)));
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Http.Unhandled", entry.Properties["Operation"]);
        Assert.DoesNotContain("private-", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void FailuresBeforeHostLoggingIsAvailableHaveASafeStructuredStderrFallback()
    {
        var previous = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            FileLogging.WriteHostFailure(new InvalidOperationException("private-config", new IOException("private-path")));
        }
        finally { Console.SetError(previous); }
        Assert.DoesNotContain("private-", output.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("InvalidOperationException", json.RootElement.GetProperty("ExceptionType").GetString());
        Assert.Equal("IOException", json.RootElement.GetProperty("CauseType").GetString());
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class UnusedRateLimitStore : IAuthenticationRateLimitStore
    {
        public Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test does not invoke the rate limiter.");
    }
}
