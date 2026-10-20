namespace GarajeMetria.Infrastructure.Authentication;

public sealed class DirectoryOptions
{
    public string[] Servers { get; set; } = [];
    public int Port { get; set; }
    public string SearchBase { get; set; } = "";
    public string ServiceBindDn { get; set; } = "";
    public string ServicePassword { get; set; } = "";
    public int TimeoutSeconds { get; set; }
    public string AdministratorGroupDn { get; set; } = "";
    public string AdvisorGroupDn { get; set; } = "";
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string AccessAudience { get; set; } = "";
    public string RefreshAudience { get; set; } = "";
    public int? ClockSkewSeconds { get; set; }
    public string SigningCertificatePath { get; set; } = "";
    public string SigningCertificatePassword { get; set; } = "";
    public string SigningKeyId { get; set; } = "";
    public VerificationKeyOptions[] VerificationKeys { get; set; } = [];
}

public sealed class VerificationKeyOptions
{
    public string KeyId { get; set; } = "";
    public string CertificatePath { get; set; } = "";
}
