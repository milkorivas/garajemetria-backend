using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

namespace GarajeMetria.Api.Diagnostics;

// Do not discard third-party exception events: replace their potentially sensitive
// message, stack and properties with a minimal diagnostic before writing JSON.
public sealed class SafeExceptionJsonFormatter : ITextFormatter
{
    private static readonly MessageTemplate Template = new MessageTemplateParser().Parse(
        "Exception observed: exceptionType={ExceptionType}, causeType={CauseType}");
    private static readonly string[] SafeProperties = ["SourceContext", "RequestId", "TraceId", "Application", "ProcessId"];
    private readonly CompactJsonFormatter formatter = new();

    public void Format(LogEvent logEvent, TextWriter output)
    {
        if (logEvent.Exception is { } exception)
        {
            var properties = new List<LogEventProperty>
            {
                new("ExceptionType", new ScalarValue(exception.GetType().Name)),
                new("CauseType", new ScalarValue(exception.GetBaseException().GetType().Name))
            };
            foreach (var key in SafeProperties)
                if (logEvent.Properties.TryGetValue(key, out var value)) properties.Add(new(key, value));
            logEvent = new LogEvent(logEvent.Timestamp, logEvent.Level, null, Template, properties,
                logEvent.TraceId ?? default, logEvent.SpanId ?? default);
        }
        formatter.Format(logEvent, output);
    }
}
