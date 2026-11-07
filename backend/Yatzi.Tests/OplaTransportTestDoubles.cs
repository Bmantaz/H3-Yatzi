using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Kontrollerede transportobjekter til fejl, samtidighed og lukketid. Tasks fungerer
/// som startporte, så testene ikke afhænger af netværksporte eller faste ventepauser.
/// </summary>
internal sealed class ControlledWebSocket : WebSocket
{
    private WebSocketState _state = WebSocketState.Open;
    private int _activeSends;
    private int _maxConcurrentSends;
    public Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>>? ReceiveAction { get; set; }
    public Func<CancellationToken, Task>? SendAction { get; set; }
    public Func<CancellationToken, Task>? CloseAction { get; set; }
    public ConcurrentQueue<byte[]> Sent { get; } = new();
    public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaxConcurrentSends => _maxConcurrentSends;
    public int CloseCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int AbortCount { get; private set; }
    public override WebSocketCloseStatus? CloseStatus => SentCloseStatus;
    public WebSocketCloseStatus? SentCloseStatus { get; private set; }
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        ReceiveStarted.TrySetResult();
        if (ReceiveAction is not null) return await ReceiveAction(buffer, cancellationToken);
        await Aborted.Task.WaitAsync(cancellationToken);
        throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        int active = Interlocked.Increment(ref _activeSends);
        int previous;
        do { previous = _maxConcurrentSends; }
        while (active > previous && Interlocked.CompareExchange(ref _maxConcurrentSends, active, previous) != previous);
        Sent.Enqueue(buffer.ToArray());
        SendStarted.TrySetResult();
        try
        {
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Assert.True(endOfMessage);
            if (SendAction is not null) await SendAction(cancellationToken);
        }
        finally { Interlocked.Decrement(ref _activeSends); }
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        CloseCount++;
        SentCloseStatus = closeStatus;
        CloseStarted.TrySetResult();
        if (CloseAction is not null) await CloseAction(cancellationToken);
        _state = WebSocketState.Closed;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        AbortCount++;
        _state = WebSocketState.Aborted;
        Aborted.TrySetResult();
    }

    public override void Dispose()
    {
        DisposeCount++;
        _state = WebSocketState.Closed;
    }
}

/// <summary>Styrer monotone tidsstempler uafhængigt af væguret uden ventepauser.</summary>
internal sealed class ManualTimestampTimeProvider : TimeProvider
{
    private long _timestamp;
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}

/// <summary>Opsamler kun den valgte komponents logs og strukturerede felter.</summary>
internal sealed class RecordingLoggerProvider(string category) : ILoggerProvider
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);
    public ConcurrentQueue<Entry> Entries { get; } = new();
    private readonly Channel<Entry> _recorded = Channel.CreateUnbounded<Entry>();
    public ValueTask<Entry> ReadAsync(CancellationToken token) => _recorded.Reader.ReadAsync(token);
    public ILogger CreateLogger(string categoryName) => categoryName == category
        ? new RecordingLogger(this) : NullLogger.Instance;
    public void Dispose() { }

    private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            var entry = new Entry(logLevel, formatter(state, exception), properties);
            owner.Entries.Enqueue(entry);
            owner._recorded.Writer.TryWrite(entry);
        }
    }
}

/// <summary>
/// READY-testgrænse: registrerer indgang og afslutning, og kan udføre kontrolleret
/// serverbinding. Signalerne erstatter ekko og ventepauser; findes kun i tests.
/// </summary>
internal sealed class RecordingReadyHandler : IOplaReadyHandler
{
    public sealed record Call(Guid ConnectionId, string? Code, CancellationToken Token);
    public ConcurrentQueue<Call> Calls { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Call> _completed = Channel.CreateUnbounded<Call>();
    public Func<Call, Task>? HandleAction { get; set; }
    public ValueTask<Call> ReadAsync(CancellationToken token) => _completed.Reader.ReadAsync(token);

    public async Task HandleReadyAsync(Guid connectionId, string? code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new Call(connectionId, code, cancellationToken);
        Calls.Enqueue(call);
        Started.TrySetResult();
        if (HandleAction is not null) await HandleAction(call);
        _completed.Writer.TryWrite(call);
    }
}

/// <summary>Udløser CTS-fristen eksplicit og registrerer den bestilte varighed.</summary>
internal sealed class ControlledDeadlineTimeProvider : TimeProvider
{
    private ControlledTimer? _timer;
    public TimeSpan DueTime { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        DueTime = dueTime;
        _timer = new ControlledTimer(callback, state);
        return _timer;
    }

    public void Expire() => _timer!.Fire();

    private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _stopping.Cancel();
    public void Dispose() => _stopping.Dispose();
}

internal sealed class TestWebSocketFeature(Func<Task<WebSocket>> accept) : IHttpWebSocketFeature
{
    public bool IsWebSocketRequest => true;
    public int AcceptCount { get; private set; }
    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
    {
        AcceptCount++;
        return accept();
    }
}
