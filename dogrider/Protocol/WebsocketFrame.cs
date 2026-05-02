using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace dogrider.Protocol;

public class WebsocketFrame
{
    public FrameType Type { get; set; }
    
    public bool Fin { get; set; }
    
    public ReadOnlySequence<byte> Payload { get; set; }

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

    public WebsocketFrame()
    {
        
    }

    public bool IsError([MaybeNullWhen(false)] out FrameError error)
    {
        error = _error;
        return _error != null;
    }
}
