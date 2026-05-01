using dogrider.Protocol;
using dogrider.Server;

namespace dogrider.Demo;

internal static class Program
{
    private static async Task Main()
    {
        await using var server = new DogriderServer(
            ip: "0.0.0.0",
            port: 8080,
            reactorCount: Math.Max(1, 16),
            handler: new EchoHandler());

        server.Start();
        
        Console.WriteLine("dogrider listening on ws://0.0.0.0:8080/");

        var stop = new TaskCompletionSource();
        
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; stop.TrySetResult(); 
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        
        await stop.Task;
        await server.StopAsync();
    }
}

internal sealed class EchoHandler : Handler
{
    public async ValueTask HandleAsync(IConnection connection)
    {
        while (true)
        {
            var frames = await connection.ReadFramesAsync();

            foreach (var frame in frames)
            {
                if (frame.IsError(out var err))
                {
                    if (err.ErrorType is FrameErrorType.ConnectionClosed)
                    {
                        return;
                    }
                    
                    await connection.CloseAsync(reason: err.Message, statusCode: 1002);
                    
                    return;
                }

                switch (frame.Type)
                {
                    case FrameType.Text:
                        
                        connection.Write(frame.Data);
                        break;
                    
                    case FrameType.Binary:
                        
                        connection.Write(frame.Data, FrameType.Binary);
                        break;
                    
                    case FrameType.Ping:
                        
                        connection.Pong(frame.Data);
                        break;
                    
                    case FrameType.Close:
                        
                        await connection.CloseAsync();
                        return;
                    
                    case FrameType.Pong:
                    case FrameType.Continue:
                        break;
                }
            }

            await connection.FlushAsync();
        }
    }
}
