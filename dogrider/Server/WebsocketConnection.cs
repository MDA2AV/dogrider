using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using dogrider.Protocol;
using Microsoft.Extensions.ObjectPool;
using zerg;
using zerg.core;

namespace dogrider.Server;

public sealed class WebsocketConnection : IConnection
{
    public static readonly ObjectPool<WebsocketFrame> FramePool =
        new DefaultObjectPool<WebsocketFrame>(new FramePoolPolicy(), 8192 * 4);
    
    private class FramePoolPolicy : PooledObjectPolicy<WebsocketFrame>
    {
        public override WebsocketFrame Create() => new();
        
        public override bool Return(WebsocketFrame context)
        {
            return true;
        }
    }
    
    private readonly Connection _conn;
    private readonly PipeReader _reader;
    private WebsocketFrame[] _frameBuffer = new WebsocketFrame[16];
    private bool _disposed;

    public ConnectionSettings Settings { get; }

    public WebsocketConnection(Connection conn, ConnectionSettings settings)
    {
        _conn = conn;
        Settings = settings;
        _reader = new ConnectionPipeReader(conn);
    }

    public async ValueTask<WebsocketFrame> ReadFrameAsync(CancellationToken token = default)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(token).ConfigureAwait(false);

            if (result is { IsCompleted: true, Buffer.IsEmpty: true })
            {
                return new WebsocketFrame(new FrameError("Connection closed", FrameErrorType.ConnectionClosed));
            }

            var sequence = result.Buffer;
            var frame = Frame.Decode(sequence, Settings.RxBufferSize, out var consumed, out var examined);

            if (frame.IsError(out var err) && err.ErrorType == FrameErrorType.Incomplete)
            {
                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(sequence.End);
                    return new WebsocketFrame(new FrameError("Connection closed", FrameErrorType.ConnectionClosed));
                }
                _reader.AdvanceTo(consumed, examined);
                continue;
            }

            _reader.AdvanceTo(consumed);
            return frame;
        }
    }

    public async ValueTask<ArraySegment<WebsocketFrame>> ReadFramesAsync(CancellationToken token = default)
    {
        var count = 0;

        while (true)
        {
            var result = await _reader.ReadAsync(token).ConfigureAwait(false);

            if (result is { IsCompleted: true, Buffer.IsEmpty: true })
            {
                EnsureFrameCapacity(count + 1);
                _frameBuffer[count++] = new WebsocketFrame(new FrameError("Connection closed", FrameErrorType.ConnectionClosed));
                
                return new ArraySegment<WebsocketFrame>(_frameBuffer, 0, count);
            }

            var sequence = result.Buffer;
            var lastConsumed = sequence.Start;
            var examined = sequence.End;

            while (true)
            {
                var frame = Frame.Decode(sequence, Settings.RxBufferSize, out var consumed, out var nextExamined);

                if (frame.IsError(out var err) && err.ErrorType == FrameErrorType.Incomplete)
                {
                    examined = nextExamined;
                    break;
                }

                EnsureFrameCapacity(count + 1);
                _frameBuffer[count++] = frame;
                lastConsumed = consumed;
                sequence = sequence.Slice(consumed);

                if (sequence.IsEmpty)
                {
                    break;
                }
            }

            if (count == 0)
            {
                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(sequence.End);
                    EnsureFrameCapacity(1);
                    _frameBuffer[count++] = new WebsocketFrame(new FrameError("Connection closed", FrameErrorType.ConnectionClosed));
                    
                    return new ArraySegment<WebsocketFrame>(_frameBuffer, 0, count);
                }
                _reader.AdvanceTo(sequence.Start, examined);
                
                continue;
            }

            _reader.AdvanceTo(lastConsumed, examined);
            
            return new ArraySegment<WebsocketFrame>(_frameBuffer, 0, count);
        }
    }

    private void EnsureFrameCapacity(int needed)
    {
        if (_frameBuffer.Length < needed)
        {
            Array.Resize(ref _frameBuffer, Math.Max(needed, _frameBuffer.Length * 2));
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlyMemory<byte> payload, FrameType opcode = FrameType.Text, bool fin = true) 
        => Frame.EncodeInto(_conn, payload.Span, ToOpcodeByte(opcode), fin);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> payload, FrameType opcode = FrameType.Text, bool fin = true) 
        => Frame.EncodeInto(_conn, payload, ToOpcodeByte(opcode), fin);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Ping() => Frame.EncodePing(_conn);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Pong(ReadOnlyMemory<byte> payload) => Frame.EncodePong(_conn, payload.Span);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Pong() => Frame.EncodePong(_conn, ReadOnlySpan<byte>.Empty);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Close(string? reason = null, ushort statusCode = 1000) => Frame.EncodeClose(_conn, statusCode, reason);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, FrameType opcode = FrameType.Text, bool fin = true, CancellationToken token = default)
    {
        Frame.EncodeInto(_conn, payload.Span, ToOpcodeByte(opcode), fin);
        
        return _conn.FlushAsync();
    }

    public ValueTask PingAsync(CancellationToken token = default)
    {
        Frame.EncodePing(_conn);
        
        return _conn.FlushAsync();
    }

    public ValueTask PongAsync(ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        Frame.EncodePong(_conn, payload.Span);
        
        return _conn.FlushAsync();
    }

    public ValueTask PongAsync(CancellationToken token = default)
    {
        Frame.EncodePong(_conn, ReadOnlySpan<byte>.Empty);
        
        return _conn.FlushAsync();
    }

    public ValueTask CloseAsync(string? reason = null, ushort statusCode = 1000, CancellationToken token = default)
    {
        Frame.EncodeClose(_conn, statusCode, reason);
        
        return _conn.FlushAsync();
    }
    
    public ValueTask FlushAsync() => _conn.FlushAsync();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _reader.Complete();
        
        return ValueTask.CompletedTask;
    }

    private static byte ToOpcodeByte(FrameType type) => type switch
    {
        FrameType.Continue => 0x00,
        FrameType.Text     => 0x01,
        FrameType.Binary   => 0x02,
        FrameType.Close    => 0x08,
        FrameType.Ping     => 0x09,
        FrameType.Pong     => 0x0A,
        _                  => throw new ArgumentException($"Cannot send frame type {type}", nameof(type)),
    };
}
