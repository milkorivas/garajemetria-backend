using GarajeMetria.Application.Authentication;

namespace GarajeMetria.Application.Lpr;

public sealed class LprOptions
{
    public int MaxEventAgeMinutes { get; set; } = 30;
    public int MaxFutureMinutes { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int LockTimeoutSeconds { get; set; } = 5;
    public int RateLimitWindowSeconds { get; set; } = 60;
    public string RateLimitNamespace { get; set; } = "";
    public int EventsPerDevice { get; set; } = 120;
    public int EventsPerIp { get; set; } = 1200;
    public int AdminWritesPerUser { get; set; } = 30;
    public int TokensPerClient { get; set; } = 10;
    public int TokensPerIp { get; set; } = 120;

    public RatePolicy Policy(string name) => new(name, name switch
    {
        "lpr:events-device" => EventsPerDevice,
        "lpr:events-ip" => EventsPerIp,
        "lpr:tokens-client" => TokensPerClient,
        "lpr:tokens-ip" => TokensPerIp,
        "lpr:admin-user" => AdminWritesPerUser,
        _ => throw new ArgumentException("Unknown LPR policy.", nameof(name))
    }, RateLimitWindowSeconds);
}
