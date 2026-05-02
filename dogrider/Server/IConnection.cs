using dogrider.Protocol;

namespace dogrider.Server;

public interface IConnection : IAsyncDisposable
{
    ConnectionSettings Settings { get; }

    ValueTask<WebsocketFrame> ReadFrameAsync(CancellationToken token = default);
    
    ValueTask<ArraySegment<WebsocketFrame>> ReadFramesAsync(CancellationToken token = default);

    void Write(ReadOnlyMemory<byte> payload, FrameType opcode = FrameType.Text, bool fin = true);

    void Write(ReadOnlySpan<byte> payload, FrameType opcode = FrameType.Text, bool fin = true);
    
    void Ping();

    void Pong(ReadOnlyMemory<byte> payload);

    void Pong();

    void Close(string? reason = null, ushort statusCode = 1000);
    
    ValueTask WriteAsync(ReadOnlyMemory<byte> payload, FrameType opcode = FrameType.Text, bool fin = true,
        CancellationToken token = default);

    ValueTask PingAsync(CancellationToken token = default);

    ValueTask PongAsync(ReadOnlyMemory<byte> payload, CancellationToken token = default);

    ValueTask PongAsync(CancellationToken token = default);

    ValueTask CloseAsync(string? reason = null, ushort statusCode = 1000, CancellationToken token = default);

    ValueTask FlushAsync();
}
