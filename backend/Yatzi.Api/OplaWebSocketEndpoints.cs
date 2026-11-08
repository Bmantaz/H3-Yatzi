using System.Net.WebSockets;
using System.Text;

namespace Yatzi.Api;

/// <summary>
/// /ws/opla med levetid, beskedgrænser, READY/adgangskontrol og
/// spilhandleren. #59 overtager den serverinterne READY-behandling. Endpointet
/// bruger samme kontroller i alle miljøer og sender aldrig ekko eller omgår parring.
/// </summary>
public static class OplaWebSocketEndpoints
{
    public const int MaxPayloadBytes = 256;
    public const int MaxCodePoints = 64;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Registrerer en præcis route for alle HTTP-metoder, også CONNECT ved HTTP/2.
    /// UseWebSockets og OplaConnectionRegistry skal være registreret i Program.
    /// </summary>
    public static IEndpointRouteBuilder MapOplaWebSocket(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/ws/opla", HandleAsync);
        return endpoints;
    }

    /// <summary>
    /// Holder requesten åben, så længe socketen bruges. Almindelig HTTP giver 400;
    /// et fyldt forbindelsesregister giver 503. Alle ressourcer frigives i finally,
    /// også ved afvist accept, serverstop og uventede fejl.
    /// </summary>
    public static async Task HandleAsync(HttpContext context, OplaConnectionRegistry registry,
        IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory,
        TimeProvider timeProvider, IOplaReadyHandler readyHandler, OplaMessageHandler messageHandler, LobbySession lobbySession)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!registry.TryReserve(out Guid connectionId))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var logger = loggerFactory.CreateLogger(nameof(OplaWebSocketEndpoints));
        WebSocket? socket = null;
        OplaConnection? connection = null;
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping);
        try
        {
            stopping.Token.ThrowIfCancellationRequested();
            // AcceptAsync har ikke et særskilt CancellationToken i .NET 8. Afbryd
            // requestens transport, hvis værten stopper under opgraderingen.
            using (stopping.Token.Register(context.Abort))
                socket = await context.WebSockets.AcceptWebSocketAsync();
            connection = new OplaConnection(connectionId, socket,
                loggerFactory.CreateLogger<OplaConnection>(), timeProvider);
            if (!registry.TryAttach(connection))
                throw new InvalidOperationException("Oplà-forbindelsens reservation er ikke længere tilgængelig.");

            closeStatus = await ReceiveAsync(socket, connection, registry, readyHandler, messageHandler,
                lobbySession, logger, timeProvider, stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Requesten eller værten stopper. Oprydning må også ske med et annulleret request-token.
            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
        }
        catch (OperationCanceledException) when (connection?.IsClosing == true)
        {
            // En overtagelses lukke-timeout kan afbryde socketens ventende I/O,
            // uden at requesten eller hele serveren er annulleret.
        }
        catch (ObjectDisposedException) when (connection?.IsClosing == true)
        {
            // Den fælles lukkeopgave kan have afsluttet transporten, mens
            // modtagelsesløkken stadig ventede. Finally ejer stadig oprydningen.
        }
        catch (WebSocketException exception)
        {
            logger.LogWarning("Oplà-forbindelsen {ConnectionId} mistede transporten: {ErrorCode}.",
                connectionId, exception.WebSocketErrorCode);
        }
        catch (IOException)
        {
            // Fx en peer, der lukker strømmen uden et WebSocket-lukkehåndtryk.
            logger.LogWarning("Oplà-forbindelsen {ConnectionId} mistede transportstrømmen.", connectionId);
        }
        finally
        {
            try
            {
                if (connection is not null)
                    await connection.CloseAsync(closeStatus, "Opla-forbindelsen afsluttes.");
            }
            finally
            {
                try { socket?.Dispose(); }
                finally
                {
                    registry.Remove(connectionId);
                    connection?.MarkCompleted();
                }
            }
        }
    }

    private static async Task<WebSocketCloseStatus> ReceiveAsync(WebSocket socket,
        OplaConnection connection, OplaConnectionRegistry registry, IOplaReadyHandler readyHandler,
        OplaMessageHandler messageHandler, LobbySession lobbySession, ILogger logger, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var rateLimiter = new OplaMessageRateLimiter(timeProvider);
        byte[] payload = new byte[MaxPayloadBytes];
        // En enkelt prøvebyte opdager overskridelsen uden at udvide den accepterede
        // payload. Den tillader også et tomt slutfragment efter præcis 256 bytes.
        byte[] overflowProbe = new byte[1];
        int length = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            int remaining = MaxPayloadBytes - length;
            var buffer = remaining > 0
                ? new ArraySegment<byte>(payload, length, remaining)
                : new ArraySegment<byte>(overflowProbe);
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return WebSocketCloseStatus.NormalClosure;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} sendte en binær besked.", connection.ConnectionId);
                return WebSocketCloseStatus.InvalidMessageType;
            }
            if (remaining == 0 && result.Count > 0)
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} overskred {MaxBytes} payload-bytes.",
                    connection.ConnectionId, MaxPayloadBytes);
                return WebSocketCloseStatus.MessageTooBig;
            }

            length += result.Count;
            if (!result.EndOfMessage) continue;

            // Hver færdig tekstbesked tæller før afkodning og formatkontrol, også
            // tom tekst, debug og ugyldigt input. Fragmenter tæller ikke hver for sig.
            if (!rateLimiter.TryRecordMessage())
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} overskred beskedraten: {Count} beskeder i et rullende sekund.",
                    connection.ConnectionId, OplaMessageRateLimiter.MaxMessagesPerSecond + 1);
                return WebSocketCloseStatus.PolicyViolation;
            }

            string text;
            try { text = Utf8.GetString(payload, 0, length); }
            catch (DecoderFallbackException)
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} sendte ugyldig UTF-8. Længde: {Length}.",
                    connection.ConnectionId, length);
                return WebSocketCloseStatus.InvalidPayloadData;
            }
            length = 0;

            // String.Length tæller UTF-16-kodeenheder. Rune-tællingen følger den
            // aftalte grænse i Unicode-kodepunkter, også for fx firebyte-emojis.
            int codePoints = text.EnumerateRunes().Count();
            if (codePoints > MaxCodePoints)
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} overskred {MaxCodePoints} Unicode-kodepunkter. Længde: {Length}.",
                    connection.ConnectionId, MaxCodePoints, codePoints);
                return WebSocketCloseStatus.MessageTooBig;
            }
            if (text.Length == 0 || text.Contains('\r') || text.Contains('\n'))
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} afviste beskeden: {Reason}. Længde: {Length}.",
                    connection.ConnectionId, text.Length == 0 ? "Tom tekst" : "Linjeskift i teksten", codePoints);
                continue;
            }
            // Debug må ikke omgå grænserne eller skjule flere linjer i én besked.
            if (text.StartsWith('#')) continue;

            cancellationToken.ThrowIfCancellationRequested();
            if (TryParseReady(text, out string? code))
            {
                if (!registry.TryMarkReady(connection.ConnectionId))
                    return WebSocketCloseStatus.NormalClosure;
                // Også genforsøg leveres til #59. READY registreres først, så
                // serverens behandler kan binde spilleren, men aldrig klienten selv.
                await readyHandler.HandleReadyAsync(connection.ConnectionId, code, cancellationToken);
                continue;
            }

            if (!registry.TryGetPlayerId(connection.ConnectionId, out Guid playerId))
            {
                logger.LogWarning("Oplà-forbindelsen {ConnectionId} afviste beskeden: READY eller parring mangler. Længde: {Length}.",
                    connection.ConnectionId, codePoints);
                continue;
            }
            // Lobbyen genkontrollerer bindingen og ordner accepten med start og
            // overtagelse. Handleren fører PlayerId til GameSession, som kontrollerer
            // spiller og handling samlet. Svaret køes før netværksafventning.
            if (!await lobbySession.HandleGameInputAsync(connection.ConnectionId, playerId, text,
                messageHandler, cancellationToken))
                return WebSocketCloseStatus.NormalClosure;
        }
        return WebSocketCloseStatus.EndpointUnavailable;
    }

    /// <summary>Genkender kun de to READY-formater; null betyder ingen kode, og nuller bevares.</summary>
    private static bool TryParseReady(string text, out string? code)
    {
        code = null;
        if (text == "READY") return true;
        if (text.Length != 10 || !text.StartsWith("READY ", StringComparison.Ordinal)) return false;
        for (int i = 6; i < 10; i++)
            if (text[i] is < '0' or > '9') return false;
        code = text[6..];
        return true;
    }
}
