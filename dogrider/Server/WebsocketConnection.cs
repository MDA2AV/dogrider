using System.Buffers;
using System.Runtime.CompilerServices;
using dogrider.Protocol;
using zerg;
using zerg.core;

namespace dogrider.Server;

internal sealed class WebsocketConnection : IConnection
{
    private readonly Connection _conn;

    private UnmanagedMemoryManager[] _heldRings = new UnmanagedMemoryManager[8];
    private int _heldCount;
    private int _firstOffset;

    private WebsocketFrame[] _frameBuffer = new WebsocketFrame[16];
    private bool _disposed;
    private bool _connectionClosed;

    public ConnectionSettings Settings { get; }

    public WebsocketConnection(Connection conn, ConnectionSettings settings)
    {
        _conn = conn;
        Settings = settings;
    }

    public async ValueTask<WebsocketFrame> ReadFrameAsync(CancellationToken token = default)
    {
        while (true)
        {
            if (_heldCount > 0)
            {
                var seq = BuildSequence();
                var frame = Frame.Decode(seq, Settings.RxBufferSize, out var consumed, out _);

                if (!IsIncomplete(frame))
                {
                    AdvanceConsumed(seq, consumed);
                    return frame;
                }
            }

            if (_connectionClosed)
            {
                return MakeClosedFrame();
            }

            var snap = await _conn.ReadAsync().ConfigureAwait(false);

            if (snap.IsClosed)
            {
                _connectionClosed = true;
                if (_heldCount == 0)
                {
                    return MakeClosedFrame();
                }
                continue;
            }

            DrainSnapshot();
            _conn.ResetRead();
        }
    }

    public async ValueTask<ArraySegment<WebsocketFrame>> ReadFramesAsync(CancellationToken token = default)
    {
        var count = 0;

        while (true)
        {
            if (_heldCount > 0)
            {
                var seq = BuildSequence();
                var remaining = seq;

                while (!remaining.IsEmpty)
                {
                    var frame = Frame.Decode(remaining, Settings.RxBufferSize, out var consumed, out _);
                    if (IsIncomplete(frame))
                    {
                        break;
                    }

                    remaining = remaining.Slice(consumed);
                    EnsureFrameCapacity(count + 1);
                    _frameBuffer[count++] = frame;
                }

                var consumedBytes = seq.Length - remaining.Length;
                if (consumedBytes > 0)
                {
                    AdvanceConsumedBytes(consumedBytes);
                }
            }

            if (count > 0)
            {
                return new ArraySegment<WebsocketFrame>(_frameBuffer, 0, count);
            }

            if (_connectionClosed)
            {
                EnsureFrameCapacity(1);
                _frameBuffer[0] = MakeClosedFrame();
                return new ArraySegment<WebsocketFrame>(_frameBuffer, 0, 1);
            }

            var snap = await _conn.ReadAsync().ConfigureAwait(false);

            if (snap.IsClosed)
            {
                _connectionClosed = true;
                if (_heldCount == 0)
                {
                    EnsureFrameCapacity(1);
                    _frameBuffer[0] = MakeClosedFrame();
                    return new ArraySegment<WebsocketFrame>(_frameBuffer, 0, 1);
                }
                continue;
            }

            DrainSnapshot();
            _conn.ResetRead();
        }
    }

    private void DrainSnapshot()
    {
        var n = _conn.SnapshotRingCount;
        EnsureRingCapacity(_heldCount + n);

        for (var i = 0; i < n; i++)
        {
            _heldRings[_heldCount++] = _conn.GetRing().AsUnmanagedMemoryManager();
        }
    }

    private ReadOnlySequence<byte> BuildSequence()
    {
        if (_heldCount == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var headMem = _firstOffset > 0
            ? _heldRings[0].Memory.Slice(_firstOffset)
            : _heldRings[0].Memory;

        if (_heldCount == 1)
        {
            return new ReadOnlySequence<byte>(headMem);
        }

        var head = new RingSegment(headMem, _heldRings[0].BufferId);
        var tail = head;

        for (var i = 1; i < _heldCount; i++)
        {
            tail = tail.Append(_heldRings[i].Memory, _heldRings[i].BufferId);
        }

        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }

    private void AdvanceConsumed(in ReadOnlySequence<byte> seq, SequencePosition consumed)
    {
        var bytes = seq.Slice(0, consumed).Length;
        if (bytes > 0)
        {
            AdvanceConsumedBytes(bytes);
        }
    }

    private void AdvanceConsumedBytes(long bytes)
    {
        var idx = 0;

        if (_heldCount > 0)
        {
            var firstAvail = _heldRings[0].Length - _firstOffset;
            if (bytes >= firstAvail)
            {
                _conn.ReturnRing(_heldRings[0].BufferId);
                bytes -= firstAvail;
                _firstOffset = 0;
                idx = 1;
            }
            else
            {
                _firstOffset += (int)bytes;
                return;
            }
        }

        while (idx < _heldCount && bytes > 0)
        {
            var len = _heldRings[idx].Length;
            if (bytes >= len)
            {
                _conn.ReturnRing(_heldRings[idx].BufferId);
                bytes -= len;
                idx++;
            }
            else
            {
                _firstOffset = (int)bytes;
                break;
            }
        }

        if (idx > 0)
        {
            var remaining = _heldCount - idx;
            if (remaining > 0)
            {
                Array.Copy(_heldRings, idx, _heldRings, 0, remaining);
            }
            for (var i = remaining; i < _heldCount; i++)
            {
                _heldRings[i] = null!;
            }
            _heldCount = remaining;
        }
    }

    private void EnsureRingCapacity(int needed)
    {
        if (_heldRings.Length < needed)
        {
            Array.Resize(ref _heldRings, Math.Max(needed, _heldRings.Length * 2));
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
    private static bool IsIncomplete(in WebsocketFrame frame) =>
        frame.IsError(out var err) && err.ErrorType == FrameErrorType.Incomplete;

    private static WebsocketFrame MakeClosedFrame() =>
        new(new FrameError("Connection closed", FrameErrorType.ConnectionClosed));

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
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;

        for (var i = 0; i < _heldCount; i++)
        {
            _conn.ReturnRing(_heldRings[i].BufferId);
            _heldRings[i] = null!;
        }
        _heldCount = 0;
        _firstOffset = 0;

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
