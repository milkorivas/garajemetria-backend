using Microsoft.Extensions.Logging;

namespace GarajeMetria.Application.Diagnostics;

public static class SafeExceptionLogging
{
    // Exception messages, Data and stack traces can contain credentials or provider details.
    // Only application-owned operation names, exception types and numeric codes are recorded.
    public static void LogSafeException(this ILogger logger, LogLevel level, string operation, Exception exception, int? errorCode = null) =>
        logger.Log(level, "Exception observed: operation={Operation}, exceptionType={ExceptionType}, causeType={CauseType}, errorCode={ErrorCode}",
            operation, exception.GetType().Name, exception.GetBaseException().GetType().Name, errorCode);
}
