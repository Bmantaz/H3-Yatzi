using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester tunnelindstillingens validering og strukturerede logning uden netværk.
/// Hver test bruger sin egen konfiguration og logger; globale miljøvariabler ændres ikke.
/// </summary>
public class TunnelStartupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void MissingAddress_AllowsLocalUseAndLogsOnce(string? value)
    {
        using var logs = new RecordingLoggerProvider(nameof(TunnelStartup));
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(logs));

        TunnelStartup.LogPublicAddresses(Configuration(value), logging.CreateLogger(nameof(TunnelStartup)));

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Ingen offentlig tunneladresse", entry.Message);
        Assert.False(entry.Properties.ContainsKey("TunnelPublicUrl"));
        Assert.False(entry.Properties.ContainsKey("OplaWebSocketUrl"));
    }

    [Theory]
    [InlineData("https://yatzi.example.invalid", "https://yatzi.example.invalid", "wss://yatzi.example.invalid/ws/opla")]
    [InlineData("https://yatzi.example.invalid/", "https://yatzi.example.invalid", "wss://yatzi.example.invalid/ws/opla")]
    [InlineData("  https://yatzi.example.invalid/ \t", "https://yatzi.example.invalid", "wss://yatzi.example.invalid/ws/opla")]
    [InlineData("https://yatzi.example.invalid:443", "https://yatzi.example.invalid", "wss://yatzi.example.invalid/ws/opla")]
    [InlineData("https://yatzi.example.invalid:443/", "https://yatzi.example.invalid", "wss://yatzi.example.invalid/ws/opla")]
    [InlineData("https://yatzi.example.invalid:8443/", "https://yatzi.example.invalid:8443", "wss://yatzi.example.invalid:8443/ws/opla")]
    [InlineData("HTTPS://YATZI.EXAMPLE.INVALID/", "https://yatzi.example.invalid", "wss://yatzi.example.invalid/ws/opla")]
    [InlineData("https://[::1]:8443/", "https://[::1]:8443", "wss://[::1]:8443/ws/opla")]
    public void ValidAddress_LogsNormalizedHttpsAndWebSocketFieldsOnce(string value, string publicUrl, string webSocketUrl)
    {
        using var logs = new RecordingLoggerProvider(nameof(TunnelStartup));
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(logs));

        TunnelStartup.LogPublicAddresses(Configuration(value), logging.CreateLogger(nameof(TunnelStartup)));

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(publicUrl, entry.Properties["TunnelPublicUrl"]);
        Assert.Equal(webSocketUrl, entry.Properties["OplaWebSocketUrl"]);
        Assert.Contains("Konfigurerede offentlige adresser", entry.Message);
    }

    [Theory]
    [InlineData("yatzi.example.invalid")]
    [InlineData("/ws/opla")]
    [InlineData("http://yatzi.example.invalid")]
    [InlineData("wss://yatzi.example.invalid")]
    [InlineData("ftp://yatzi.example.invalid")]
    [InlineData("https://")]
    [InlineData("https:///yatzi.example.invalid")]
    [InlineData("https://yatzi.example.invalid:0")]
    [InlineData("https://yatzi.example.invalid:-1")]
    [InlineData("https://yatzi.example.invalid:65536")]
    [InlineData("https://yatzi.example.invalid:port")]
    [InlineData("https://yatzi.example.invalid/ws/opla")]
    [InlineData("https://yatzi.example.invalid/spil/..")]
    [InlineData("https://yatzi.example.invalid/.")]
    [InlineData("https://yatzi.example.invalid/%2e")]
    [InlineData("https://yatzi.example.invalid/%2e%2e/")]
    [InlineData("https://yatzi.example.invalid//")]
    [InlineData("https://yatzi.example.invalid?spil=1")]
    [InlineData("https://yatzi.example.invalid?")]
    [InlineData("https://yatzi.example.invalid#del")]
    [InlineData("https://yatzi.example.invalid#")]
    [InlineData("https://user@yatzi.example.invalid")]
    [InlineData("https://@yatzi.example.invalid")]
    [InlineData("https://user:password@yatzi.example.invalid")]
    [InlineData("https://yatzi.example.invalid\\")]
    [InlineData("https://yat zi.example.invalid")]
    [InlineData("https://yatzi.\nexample.invalid")]
    [InlineData("https://yatzi.example.invalid\0")]
    public void InvalidAddress_ThrowsConfigurationErrorWithoutLogging(string value)
    {
        using var logs = new RecordingLoggerProvider(nameof(TunnelStartup));
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(logs));

        var error = Assert.Throws<InvalidOperationException>(() =>
            TunnelStartup.LogPublicAddresses(Configuration(value), logging.CreateLogger(nameof(TunnelStartup))));

        Assert.Contains("Tunnel:PublicUrl", error.Message);
        Assert.Contains("HTTPS-baseadresse", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(logs.Entries);
    }

    [Theory]
    [InlineData("https://user:DO_NOT_LOG_THIS@yatzi.example.invalid")]
    [InlineData("https://yatzi.example.invalid?token=DO_NOT_LOG_THIS")]
    [InlineData("DO_NOT_LOG_THIS")]
    public void InvalidAddress_DoesNotExposeInputInExceptionOrLogs(string value)
    {
        using var logs = new RecordingLoggerProvider(nameof(TunnelStartup));
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(logs));

        var error = Assert.Throws<InvalidOperationException>(() =>
            TunnelStartup.LogPublicAddresses(Configuration(value), logging.CreateLogger(nameof(TunnelStartup))));

        Assert.DoesNotContain("DO_NOT_LOG_THIS", error.ToString());
        Assert.DoesNotContain(value, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Empty(logs.Entries);
    }

    private static IConfiguration Configuration(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null) values.Add("Tunnel:PublicUrl", value);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
