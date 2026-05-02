using Microsoft.Extensions.ObjectPool;

namespace dogrider.Protocol;

public static partial class Frame
{
    public static readonly ObjectPool<WebsocketFrame> Pool =
        new DefaultObjectPool<WebsocketFrame>(new FramePoolPolicy(), 8192 * 4);
    
    private class FramePoolPolicy : PooledObjectPolicy<WebsocketFrame>
    {
        public override WebsocketFrame Create() => new();
        
        public override bool Return(WebsocketFrame context)
        {
            return true;
        }
    }

    public static WebsocketFrame GetErrorFrame(FrameError frameError) 
        => Pool.Get().SetError(frameError);
}