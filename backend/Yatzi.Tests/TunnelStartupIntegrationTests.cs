using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Yatzi.Tests;

/// <summary>
/// Tester opstart gennem den rigtige app-pipeline med isoleret konfiguration.
/// Konfigurerede adresser er eksempler; hverken offentlig tunnel eller hardware kontaktes.
/// </summary>
public class TunnelStartupIntegrationTests
{
    private static readonly string ApplicationCategory = typeof(Program).Assembly.GetName().Name!;

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task EmptyAddress_StartsLocallyAndReportsHealth(string environment)
    {
        using var logs = new RecordingLoggerProvider(ApplicationCategory);
        using var factory = CreateFactory(environment, "", logs);
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");

        Assert.Equal("ok", health.GetProperty("status").GetString());
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Ingen offentlig tunneladresse", entry.Message);
        Assert.False(entry.Properties.ContainsKey("TunnelPublicUrl"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ValidAddress_LogsConfiguredFieldsAndKeepsLocalHealthAvailable(string environment)
    {
        using var logs = new RecordingLoggerProvider(ApplicationCategory);
        using var factory = CreateFactory(environment, " https://yatzi.example.invalid:8443/ ", logs);
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");

        Assert.Equal("ok", health.GetProperty("status").GetString());
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("https://yatzi.example.invalid:8443", entry.Properties["TunnelPublicUrl"]);
        Assert.Equal("wss://yatzi.example.invalid:8443/ws/opla", entry.Properties["OplaWebSocketUrl"]);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void InvalidAddress_PreventsStartupWithoutExposingInput(string environment)
    {
        using var logs = new RecordingLoggerProvider(ApplicationCategory);
        using var factory = CreateFactory(environment, "https://user:DO_NOT_LOG_THIS@yatzi.example.invalid", logs);

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Tunnel:PublicUrl", error.ToString());
        Assert.DoesNotContain("DO_NOT_LOG_THIS", error.ToString());
        Assert.Empty(logs.Entries);
    }

    private static WebApplicationFactory<Program> CreateFactory(string environment, string value, RecordingLoggerProvider logs)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment(environment)
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Tunnel:PublicUrl"] = value }))
            .ConfigureLogging(logging => logging.AddProvider(logs)));
}
