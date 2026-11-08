using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Fremkalder acceptfejl, transportfejl og annullering ved endpointets faktiske fejlgrænse.</summary>
public class OplaWebSocketLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptFailure_ReleasesReservationAndPropagatesUnexpectedErrors(bool unexpected)
    {
        var registry = new OplaConnectionRegistry();
        using var lifetime = new TestApplicationLifetime();
        var feature = new TestWebSocketFeature(() => Task.FromException<WebSocket>(unexpected
            ? new InvalidOperationException("testfejl")
            : new WebSocketException(WebSocketError.ConnectionClosedPrematurely)));
        var context = Context(feature);
        var handling = HandleAsync(context, registry, lifetime);
        if (unexpected) await Assert.ThrowsAsync<InvalidOperationException>(() => handling);
        else await handling;
        Assert.Equal(1, feature.AcceptCount);
        Assert.Equal(0, registry.Count);
        Assert.True(registry.TryReserve(out _));
    }

    [Fact]
    public async Task PendingAccept_OccupiesAReservationWithoutExposingSession()
    {
        var registry = new OplaConnectionRegistry();
        using var lifetime = new TestApplicationLifetime();
        var accepted = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feature = new TestWebSocketFeature(() => accepted.Task);
        using var socket = new ControlledWebSocket
        {
            ReceiveAction = (_, _) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true))
        };
        var handling = HandleAsync(Context(feature), registry, lifetime);
        Assert.Equal(1, registry.Count);
        Assert.Empty(registry.GetConnections());
        accepted.SetResult(socket);
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, registry.Count);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestCancellationOrServerStop_DisposesAndCompletesSession(bool stopServer)
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var socket = new ControlledWebSocket();
        var context = Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket)));
        context.RequestAborted = request.Token;
        var handling = HandleAsync(context, registry, lifetime);
        await socket.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var connection = Assert.Single(registry.GetConnections());
        Assert.False(connection.Completion.IsCompleted);
        if (stopServer) lifetime.StopApplication();
        else request.Cancel();
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(connection.IsClosing);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(0, registry.Count);
        Assert.False(await connection.SendAsync("for sent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveFailure_AlwaysDisposesAndReleases(bool unexpected)
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        OplaConnection? connection = null;
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (_, _) =>
            {
                connection = Assert.Single(registry.GetConnections());
                return Task.FromException<WebSocketReceiveResult>(unexpected
                    ? new InvalidOperationException("testfejl")
                    : new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
            }
        };
        var handling = HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))), registry, lifetime);
        if (unexpected) await Assert.ThrowsAsync<InvalidOperationException>(() => handling);
        else await handling;
        Assert.NotNull(connection);
        Assert.True(connection.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task SocketAbortsDuringIncompleteMessage_NoReadyOrReplyAndNoLeakedSlot()
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var ready = new RecordingReadyHandler();
        int receives = 0;
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (buffer, _) =>
            {
                if (receives++ > 0)
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                buffer.Array![buffer.Offset] = (byte)'R';
                return Task.FromResult(new WebSocketReceiveResult(1, WebSocketMessageType.Text, false));
            }
        };
        await HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))), registry, lifetime,
            readyHandler: ready);
        Assert.Empty(socket.Sent);
        Assert.Empty(ready.Calls);
        Assert.Equal(0, registry.Count);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task CancellationBeforeAccept_StillReleasesReservation()
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        request.Cancel();
        var feature = new TestWebSocketFeature(() => throw new InvalidOperationException("Må ikke kaldes"));
        var context = Context(feature);
        context.RequestAborted = request.Token;
        await HandleAsync(context, registry, lifetime);
        Assert.Equal(0, feature.AcceptCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task CloseFailure_StillDisposesAndReleasesSlot()
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (_, _) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)),
            CloseAction = _ => throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely)
        };
        await HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))), registry, lifetime);
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task StreamFailure_IsHandledAndReleasesSlot()
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (_, _) => throw new IOException("testfejl")
        };
        await HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))), registry, lifetime);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task FragmentedMessage_UsesFinalFragmentTimestamp()
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var clock = new ManualTimestampTimeProvider();
        var ready = new RecordingReadyHandler();
        int received = 0;
        // Fyld kvoten ved t=0. READY begynder ved t=0,5 og slutter ved t=1,
        // hvor de første 20 beskeder er udløbet. Et tomt mellemfragment tæller ikke.
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (buffer, _) =>
            {
                int frame = received++;
                if (frame == 23)
                    return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                if (frame == 20 || frame == 22) clock.Advance(TimeSpan.FromMilliseconds(500));
                string text = frame < 20 ? "READY" : frame == 20 ? "REA" : frame == 21 ? "" : "DY";
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                bytes.CopyTo(buffer.Array!, buffer.Offset);
                return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text,
                    frame < 20 || frame == 22));
            }
        };
        await HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))), registry, lifetime, clock, ready)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(24, received);
        Assert.Equal(21, ready.Calls.Count);
        Assert.Empty(socket.Sent);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.SentCloseStatus);
        Assert.Equal(0, registry.Count);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task CloseDuringReady_RejectsLateBindingAndCompletesCleanup()
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var ready = new RecordingReadyHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ready.HandleAction = async call =>
        {
            Assert.True(registry.TryBindPlayer(call.ConnectionId, Guid.NewGuid()));
            entered.SetResult();
            await release.Task.WaitAsync(call.Token);
            Assert.False(registry.TryBindPlayer(call.ConnectionId, Guid.NewGuid()));
        };
        int received = 0;
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (buffer, _) =>
            {
                if (received++ > 0)
                    return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                byte[] bytes = Encoding.UTF8.GetBytes("READY");
                bytes.CopyTo(buffer.Array!, buffer.Offset);
                return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
            }
        };
        var handling = HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))),
            registry, lifetime, readyHandler: ready);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var connection = Assert.Single(registry.GetConnections());
        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        Assert.Equal(OplaConnectionState.Closed, connection.State);
        Assert.False(registry.TryGetPlayerId(connection.ConnectionId, out _));
        release.SetResult();
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, registry.Count);
        Assert.True(connection.Completion.IsCompletedSuccessfully);
        Assert.Single(ready.Calls);
        Assert.Empty(socket.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringReady_RevokesBindingAndReleasesSlot(bool stopServer)
    {
        using var lifetime = new TestApplicationLifetime();
        using var request = new CancellationTokenSource();
        var registry = new OplaConnectionRegistry();
        var ready = new RecordingReadyHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ready.HandleAction = async call =>
        {
            // En anden tråd skal kunne binde spilleren, mens READY-behandleren
            // afventes. Det ville blokere, hvis transporten holdt registerets lås.
            Assert.True(await Task.Run(() => registry.TryBindPlayer(call.ConnectionId, Guid.NewGuid())));
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
        };
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (buffer, _) =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes("READY 0012");
                bytes.CopyTo(buffer.Array!, buffer.Offset);
                return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
            }
        };
        var context = Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket)));
        context.RequestAborted = request.Token;
        var handling = HandleAsync(context, registry, lifetime, readyHandler: ready);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var connection = Assert.Single(registry.GetConnections());
        Assert.Equal(OplaConnectionState.Paired, connection.State);
        if (stopServer) lifetime.StopApplication();
        else request.Cancel();
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(ready.Calls).Token.IsCancellationRequested);
        Assert.Equal(OplaConnectionState.Closed, connection.State);
        Assert.False(registry.TryGetPlayerId(connection.ConnectionId, out _));
        Assert.True(connection.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, registry.Count);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Empty(socket.Sent);
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("io")]
    [InlineData("unexpected")]
    public async Task ReadyFailure_CleansUpAndIsNeverRetried(string kind)
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var ready = new RecordingReadyHandler
        {
            HandleAction = _ => Task.FromException(kind switch
            {
                "websocket" => new WebSocketException(WebSocketError.ConnectionClosedPrematurely),
                "io" => new IOException("testfejl"),
                _ => new InvalidOperationException("testfejl")
            })
        };
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (buffer, _) =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes("READY");
                bytes.CopyTo(buffer.Array!, buffer.Offset);
                return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
            }
        };
        var handling = HandleAsync(Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket))),
            registry, lifetime, readyHandler: ready);
        if (kind == "unexpected") await Assert.ThrowsAsync<InvalidOperationException>(() => handling);
        else await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(ready.Calls);
        Assert.Equal(0, registry.Count);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task CloseDeadline_AbortsDisposesAndCompletesEndpointCleanup()
    {
        using var lifetime = new TestApplicationLifetime();
        var registry = new OplaConnectionRegistry();
        var clock = new ControlledDeadlineTimeProvider();
        var socket = new ControlledWebSocket
        {
            ReceiveAction = (_, _) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)),
            CloseAction = token => Task.Delay(Timeout.Infinite, token)
        };
        var context = Context(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket)));
        var handling = HandleAsync(context, registry, lifetime, clock);
        await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var connection = Assert.Single(registry.GetConnections());
        Assert.Equal(1, registry.Count); // Pladsen tilhører stadig den lukkende socket.
        Assert.Equal(TimeSpan.FromSeconds(5), clock.DueTime);
        clock.Expire();
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(connection.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(0, registry.Count);
    }

    private static DefaultHttpContext Context(TestWebSocketFeature feature)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpWebSocketFeature>(feature);
        return context;
    }

    private static Task HandleAsync(HttpContext context, OplaConnectionRegistry registry,
        TestApplicationLifetime lifetime, TimeProvider? timeProvider = null, IOplaReadyHandler? readyHandler = null)
    {
        var gameSession = new GameSession();
        return OplaWebSocketEndpoints.HandleAsync(context, registry, lifetime,
            NullLoggerFactory.Instance, timeProvider ?? TimeProvider.System, readyHandler ?? new RecordingReadyHandler(),
            new OplaMessageHandler(gameSession, NullLogger<OplaMessageHandler>.Instance), new LobbySession(registry, gameSession));
    }
}
