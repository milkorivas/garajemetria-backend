using GarajeMetria.Api.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace GarajeMetria.IntegrationTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void HttpConfigurationAcceptsCustomOriginsWithoutImplicitProductionDomains()
    {
        var configuration = HttpConfiguration("https://portal.example.test", "https://localhost:7040", "https://localhost:44304");
        var options = ApiConfiguration.ReadHttpOptions(configuration);
        Assert.Equal(new[] { "https://portal.example.test", "https://localhost:7040", "https://localhost:44304" }, options.AllowedOrigins);
        foreach (var origin in options.AllowedOrigins)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Origin = origin;
            Assert.True(OriginRules.IsAllowed(context.Request, options));
        }
        foreach (var origin in new[] { "https://garajemetria.com", "https://api.garajemetria.com", "https://portal.example.test:8443" })
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Origin = origin;
            Assert.False(OriginRules.IsAllowed(context.Request, options));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://portal.example.test")]
    [InlineData("https://*.example.test")]
    [InlineData("https://portal.example.test/path")]
    public void HttpConfigurationRejectsMissingOrInvalidOrigins(string origin) =>
        Assert.Throws<InvalidOperationException>(() => ApiConfiguration.ReadHttpOptions(
            HttpConfiguration(string.IsNullOrEmpty(origin) ? [] : [origin])));

    [Theory]
    [InlineData("Http:DataProtectionKeyPath", "")]
    [InlineData("Http:DataProtectionKeyPath", "relative-keys")]
    [InlineData("Http:DataProtectionCertificatePath", "")]
    [InlineData("Http:TrustedProxies:0", "synthetic-invalid-proxy")]
    [InlineData("Http:AllowedOrigins:0", "https://localhost:44304/")]
    public void HttpConfigurationIdentifiesTheInvalidFieldWithoutExposingItsValue(string field, string value)
    {
        var configuration = HttpConfiguration("https://localhost:44304");
        configuration[field] = value;
        var error = Assert.Throws<InvalidOperationException>(() => ApiConfiguration.ReadHttpOptions(configuration));
        Assert.Contains(field, error.Message);
        if (value.Length > 0) Assert.DoesNotContain(value, error.Message);
    }

    private static IConfiguration HttpConfiguration(params string[] origins)
    {
        var values = new Dictionary<string, string?>
        {
            ["Http:DataProtectionKeyPath"] = Path.GetTempPath(),
            ["Http:DataProtectionCertificatePath"] = Path.Combine(Path.GetTempPath(), "synthetic-test.pfx")
        };
        for (var i = 0; i < origins.Length; i++) values[$"Http:AllowedOrigins:{i}"] = origins[i];
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void MissingAuthenticationConfigurationCannotSupplySilentDefaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:DefaultDomain"] = "TEST" }).Build();
        Assert.Throws<InvalidOperationException>(() => ApiConfiguration.ReadAuthenticationOptions(configuration));
    }

    [Fact]
    public void PrivateConfigurationAndKeysCannotBePlacedInsideContentRoot()
    {
        var root = Path.GetFullPath(".");
        Assert.True(ApiConfiguration.IsInside(root, Path.Combine(root, "private.json")));
        Assert.True(ApiConfiguration.IsInside(root, root));
        Assert.False(ApiConfiguration.IsInside(root, Path.Combine(root, "..", "private.json")));
    }

    [Theory]
    [InlineData("https://garajemetria.com", true)]
    [InlineData("https://garajemetria.com/", false)]
    [InlineData("https://garajemetria.com/path", false)]
    [InlineData("http://garajemetria.com", false)]
    [InlineData("null", false)]
    [InlineData("https://user:password@garajemetria.com", false)]
    [InlineData("https://garajemetria.com https://untrusted.test", false)]
    public void ConfiguredOriginsAreExplicitHttpsOrigins(string origin, bool valid) =>
        Assert.Equal(valid, OriginRules.IsConfiguredOrigin(origin));
}
