using System.Net.WebSockets;
using System.Text;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Tester afsendelsesrækkefølge og lukning gennem kontrollerede Tasks og tid.</summary>
public class OplaConnectionTests
{
    [Fact]
    public void Constructor_RejectsMissingIdentityOrSocket()
    {
        using var socket = new ControlledWebSocket();
        Assert.Throws<ArgumentException>(() => new OplaConnection(Guid.Empty, socket));
        Assert.Throws<ArgumentNullException>(() => new OplaConnection(Guid.NewGuid(), null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ROLL \t")]
    [InlineData("æøå 🎲")]
    public async Task Send_PreservesUtf8AndWhitespace(string text)
    {
        using var socket = new ControlledWebSocket();
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        Assert.True(await connection.SendAsync(text));
        Assert.Equal(text, Encoding.UTF8.GetString(Assert.Single(socket.Sent)));
    }

    [Fact]
    public async Task Send_SerializesOneSocketButDoesNotBlockAnother()
    {
        using var first = new ControlledWebSocket();
        using var second = new ControlledWebSocket();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SendAction = token => release.Task.WaitAsync(token);
        var one = new OplaConnection(Guid.NewGuid(), first);
        var two = new OplaConnection(Guid.NewGuid(), second);
        var firstSend = one.SendAsync("først");
        await first.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waitingSend = one.SendAsync("derefter");
        Assert.False(waitingSend.IsCompleted);
        Assert.Single(first.Sent);
        Assert.True(await two.SendAsync("uafhængig"));
        release.SetResult();
        Assert.All(await Task.WhenAll(firstSend, waitingSend), Assert.True);
        Assert.Equal(1, first.MaxConcurrentSends);
        Assert.Equal(new[] { "først", "derefter" }, first.Sent.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task CanceledSend_DoesNotReachSocketOrConsumeLock()
    {
        using var socket = new ControlledWebSocket();
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendAsync("annulleret", canceled.Token));
        Assert.Empty(socket.Sent);
        Assert.True(await connection.SendAsync("stadig åben"));
    }

    [Fact]
    public async Task Close_StopsNewSendsAndDoesNotDisposeSocket()
    {
        using var socket = new ControlledWebSocket();
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "igen");
        Assert.True(connection.IsClosing);
        Assert.False(await connection.SendAsync("for sent"));
        Assert.Equal(1, socket.CloseCount);
        Assert.Equal(0, socket.DisposeCount);
        Assert.False(connection.Completion.IsCompleted); // Endpointet skal stadig frigive registerpladsen.
    }

    [Fact]
    public async Task Close_AbortsUnresponsiveHandshakeAtFiveSecondDeadline()
    {
        using var socket = new ControlledWebSocket();
        var clock = new ControlledDeadlineTimeProvider();
        socket.CloseAction = token => Task.Delay(Timeout.Infinite, token);
        var connection = new OplaConnection(Guid.NewGuid(), socket, timeProvider: clock);
        var closing = connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.DueTime);
        Assert.False(closing.IsCompleted);
        clock.Expire();
        await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, socket.AbortCount);
        Assert.False(await connection.SendAsync("for sent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_DeadlineAlsoCoversWaitingForSendLock(bool internalCancellation)
    {
        using var socket = new ControlledWebSocket();
        var clock = new ControlledDeadlineTimeProvider();
        socket.SendAction = async _ =>
        {
            await socket.Aborted.Task;
            if (internalCancellation) throw new OperationCanceledException("Intern I/O-annullering");
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        };
        var connection = new OplaConnection(Guid.NewGuid(), socket, timeProvider: clock);
        var send = connection.SendAsync("blokeret");
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closing = connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        Assert.Equal(TimeSpan.FromSeconds(5), clock.DueTime);
        clock.Expire();
        await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(0, socket.CloseCount);
    }

    [Fact]
    public async Task Close_NetworkFailureAbortsWithoutDisposal()
    {
        using var socket = new ControlledWebSocket
        {
            CloseAction = _ => Task.FromException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely))
        };
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(0, socket.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentCloseCalls_AwaitTheSameHandshake()
    {
        using var socket = new ControlledWebSocket();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.CloseAction = token => release.Task.WaitAsync(token);
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        var first = connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "først");
        await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "igen");
        Assert.Same(first, second);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, socket.CloseCount);
    }

    [Fact]
    public async Task Close_StreamFailureAlsoAborts()
    {
        using var socket = new ControlledWebSocket { CloseAction = _ => throw new IOException("testfejl") };
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        Assert.Equal(1, socket.AbortCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_DisposalIsOnlyExpectedWhenSocketIsAlreadyTerminal(bool terminal)
    {
        using var socket = new ControlledWebSocket();
        socket.CloseAction = _ =>
        {
            if (terminal) socket.Abort();
            throw new ObjectDisposedException("Afsluttet lukkehåndtryk");
        };
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        if (terminal)
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        else
            await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test"));
        Assert.Equal(1, socket.CloseCount);
        Assert.Equal(0, socket.DisposeCount);
    }

    [Fact]
    public async Task CancellationDuringSend_PropagatesAndReleasesSendLock()
    {
        using var socket = new ControlledWebSocket { SendAction = token => Task.Delay(Timeout.Infinite, token) };
        using var request = new CancellationTokenSource();
        var connection = new OplaConnection(Guid.NewGuid(), socket);
        var sending = connection.SendAsync("første", request.Token);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        socket.SendAction = null;
        Assert.True(await connection.SendAsync("næste"));
    }
}
