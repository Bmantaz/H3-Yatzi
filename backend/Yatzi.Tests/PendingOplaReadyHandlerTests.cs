using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Standarden giver aldrig parring; kun #59 eller testværten kan binde en spiller.</summary>
public class PendingOplaReadyHandlerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("0012")]
    public async Task HandleReady_DoesNotGrantAccess(string? code)
    {
        var registry = new OplaConnectionRegistry();
        using var socket = new ControlledWebSocket();
        Assert.True(registry.TryReserve(out var id));
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Assert.True(registry.TryMarkReady(id));
        var handler = new PendingOplaReadyHandler();
        await handler.HandleReadyAsync(id, code, CancellationToken.None);
        await handler.HandleReadyAsync(id, code, CancellationToken.None);
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(registry.TryGetPlayerId(id, out _));
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task HandleReady_RespectsCancellation()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var handler = new PendingOplaReadyHandler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.HandleReadyAsync(Guid.NewGuid(), "0012", stopping.Token));
    }
}
