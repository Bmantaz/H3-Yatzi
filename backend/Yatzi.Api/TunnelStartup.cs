namespace Yatzi.Api;

/// <summary>
/// Validerer og logger tunnelens konfigurerede offentlige adresser ved opstart.
/// Program kalder hjælperen efter Build og før serverstart. Den ændrer hverken
/// lokal lytteadresse, Oplà-transport eller spiltilstand og foretager ingen netværkskald.
/// </summary>
internal static class TunnelStartup
{
    /// <summary>
    /// Læser Tunnel:PublicUrl fra den færdige konfiguration. Tom værdi tillader
    /// lokal brug; gyldig HTTPS-baseadresse giver én log med HTTPS- og WSS-felter.
    /// </summary>
    /// <param name="configuration">ASP.NET Cores konfiguration med lokale overskrivninger.</param>
    /// <param name="logger">Modtager Information-loggen om de konfigurerede adresser.</param>
    /// <exception cref="InvalidOperationException">
    /// En udfyldt værdi er ugyldig. Fejlen indeholder aldrig rå input eller parserfejl.
    /// </exception>
    public static void LogPublicAddresses(IConfiguration configuration, ILogger logger)
    {
        string? value = configuration["Tunnel:PublicUrl"]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            logger.LogInformation("Ingen offentlig tunneladresse er konfigureret. Serveren kan bruges lokalt.");
            return;
        }

        if (!HasValidOriginalForm(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps || address.Host.Length == 0
            || address.Port <= 0 || address.AbsolutePath != "/"
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
        {
            // En fast fejl uden rå input eller indre undtagelse beskytter også,
            // hvis nogen ved en fejl indsætter en adgangskode eller et token i URL'en.
            throw new InvalidOperationException(
                "Ugyldig Tunnel:PublicUrl. Angiv en absolut HTTPS-baseadresse med vært og eventuel gyldig port, "
                + "uden loginoplysninger, anden sti end /, query eller fragment.");
        }

        string publicUrl = address.GetLeftPart(UriPartial.Authority);
        var webSocketAddress = new UriBuilder(address)
        {
            Scheme = "wss",
            Port = address.IsDefaultPort ? -1 : address.Port,
            Path = "/ws/opla"
        };
        logger.LogInformation(
            "Konfigurerede offentlige adresser: {TunnelPublicUrl}. Oplà-endpoint: {OplaWebSocketUrl}.",
            publicUrl, webSocketAddress.Uri.AbsoluteUri);
    }

    /// <summary>Kontrollerer originalformen, før Uri kan fjerne eller ændre en angivet sti.</summary>
    private static bool HasValidOriginalForm(string value)
    {
        const string prefix = "https://";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || value.IndexOfAny(['\\', '@', '?', '#']) >= 0)
            return false;

        // Uri normaliserer fx /spil/.. til /. Kun en faktisk tom sti eller /
        // accepteres, så den normalisering må ikke omgå konfigurationskontrakten.
        int pathStart = value.IndexOf('/', prefix.Length);
        return pathStart < 0 || pathStart == value.Length - 1;
    }
}
