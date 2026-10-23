using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarajeMetria.Api.Authentication;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.File;

namespace GarajeMetria.Api.Diagnostics;

public sealed class FileLoggingOptions
{
    public string DirectoryPath { get; set; } = "";
    public long FileSizeLimitBytes { get; set; } = 20 * 1024 * 1024;
    public int RetainedFileCountLimit { get; set; } = 30;
    public int RetainedDays { get; set; } = 14;
}

public static class FileLogging
{
    public static FileLoggingOptions ReadOptions(IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection("FileLogging").Get<FileLoggingOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.DirectoryPath) || !Path.IsPathFullyQualified(options.DirectoryPath) ||
            ApiConfiguration.IsInside(contentRoot, options.DirectoryPath) ||
            ApiConfiguration.IsInside(AppContext.BaseDirectory, options.DirectoryPath) ||
            options.FileSizeLimitBytes <= 0 || options.RetainedFileCountLimit <= 0 || options.RetainedDays is <= 0 or > 365)
            throw new InvalidOperationException("FileLogging requires an external absolute directory and positive rotation/retention limits (at most 365 days).");
        options.DirectoryPath = Path.GetFullPath(options.DirectoryPath);
        return options;
    }

    public static void ValidateAccess(FileLoggingOptions options)
    {
        // Fail startup if the deployment identity cannot create, flush and remove a file.
        try
        {
            Directory.CreateDirectory(options.DirectoryPath);
            var probe = Path.Combine(options.DirectoryPath, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
            file.WriteByte(0);
            file.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The configured log directory is not writable.", exception);
        }
    }

    public static LoggerConfiguration Configure(LoggerConfiguration logger, FileLoggingOptions options, IConfiguration configuration)
    {
        return logger.ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "GarajeMetria.Api")
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            // Framework diagnostics can contain raw exceptions, SQL, headers or request data.
            // Their safe application-level equivalents are recorded by our pipeline and handler.
            .Filter.ByExcluding(IsUnsafeFrameworkEvent)
            .WriteTo.File(new SafeExceptionJsonFormatter(), Path.Combine(options.DirectoryPath,
                $"garajemetria-{Environment.ProcessId}-.json"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: options.FileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                retainedFileTimeLimit: TimeSpan.FromDays(options.RetainedDays),
                hooks: new ExpiredLogFilesHook(options.RetainedDays));
    }

    public static void AddFileLogging(this WebApplicationBuilder builder)
    {
        var options = ReadOptions(builder.Configuration, builder.Environment.ContentRootPath);
        ValidateAccess(options);
        // SelfLog messages may contain file paths or raw sink exceptions. Emit only a safe signal.
        SelfLog.Enable(_ => Console.Error.WriteLine("GarajeMetria logging failure: check log storage availability and permissions."));
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((_, logger) => Configure(logger, options, builder.Configuration), preserveStaticLogger: true);
    }

    public static void WriteHostFailure(Exception exception) => Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = "Fatal",
        Operation = "Host.Lifetime",
        ExceptionType = exception.GetType().Name,
        CauseType = exception.GetBaseException().GetType().Name
    }));

    private static bool IsUnsafeFrameworkEvent(LogEvent e) =>
        e.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string category } &&
        (category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
         category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
}

// The sink retains this process's files. Also expire files left by previous IIS processes.
internal sealed class ExpiredLogFilesHook(int retainedDays) : FileLifecycleHooks
{
    public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "garajemetria-*.json"))
            {
                var match = Regex.Match(Path.GetFileName(file), @"^garajemetria-\d+-(\d{8})(?:_\d{3,})?\.json$", RegexOptions.CultureInvariant);
                if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                    day >= DateTime.Today.AddDays(-retainedDays)) continue;
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    SelfLog.WriteLine("Expired log cleanup failed: type={0}.", exception.GetType().Name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SelfLog.WriteLine("Expired log enumeration failed: type={0}.", exception.GetType().Name);
        }
        return underlyingStream;
    }
}
