using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace dogrider.Protocol;

public readonly struct WebsocketFrame
{
    public FrameType Type { get; }
    
    public bool Fin { get; }
    
    public ReadOnlySequence<byte> Payload { get; }

    private readonly FrameError? _error;
    
    public ReadOnlyMemory<byte> Data => Payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : Payload.ToArray();

    public WebsocketFrame(FrameType type, ReadOnlySequence<byte> payload, bool fin)
    {
        Type = type;
        Fin = fin;
        Payload = payload;
        _error = null;
    }

    public WebsocketFrame(FrameError error)
    {
        Type = FrameType.Error;
        Fin = true;
        Payload = ReadOnlySequence<byte>.Empty;
        _error = error;
    }

    public bool IsError([MaybeNullWhen(false)] out FrameError error)
    {
        error = _error;
        return _error != null;
    }
}
