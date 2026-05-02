using System.Buffers;
using System.Text;
using dogrider.Http;
using dogrider.Protocol;
using zerg;
using zerg.Engine;
using zerg.Engine.Configs;

namespace dogrider.Server;

public sealed class Dogrider : IAsyncDisposable
{
    private readonly Engine _engine;
    private readonly Handler _handler;
    
    private readonly ConnectionSettings _settings;
    private readonly CancellationTokenSource _stopCts = new();
    
    private Task? _acceptLoop;

    public Dogrider(EngineOptions engineOptions, Handler handler, ConnectionSettings? settings = null)
    {
        _engine = new Engine(engineOptions);
        _handler = handler;
        _settings = settings ?? new ConnectionSettings();
    }

    public Dogrider(string ip, ushort port, int reactorCount, Handler handler, ConnectionSettings? settings = null)
        : this(new EngineOptions { Ip = ip, Port = port, ReactorCount = reactorCount }, handler, settings) { }

    public void Start()
    {
        _engine.Listen();
        _acceptLoop = Task.Run(() => RunAcceptLoopAsync(_stopCts.Token));
    }

    public async ValueTask StopAsync()
    {
        await _stopCts.CancelAsync();
        
        _engine.Stop();
        
        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                /* ignore */ 
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        _engine.Stop();
        _stopCts.Dispose();
        
        return ValueTask.CompletedTask;
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _engine.ServerRunning)
        {
            Connection? conn;
            try
            {
                conn = await _engine.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; 
            }

            if (conn == null)
            {
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(conn), ct);
        }
    }

    private async Task HandleConnectionAsync(Connection conn)
    {
        try
        {
            var key = await ReadHandshakeKeyAsync(conn).ConfigureAwait(false);
            if (key == null)
            {
                await WriteSimpleHttpResponseAsync(conn, "400 Bad Request").ConfigureAwait(false);
                return;
            }

            await WriteHandshakeResponseAsync(conn, key).ConfigureAwait(false);

            await using var ws = new WebsocketConnection(conn, _settings);
            await _handler.HandleAsync(ws).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[dogrider] Connection {conn.ClientFd} failed: {ex.Message}");
        }
    }

    private static async ValueTask<string?> ReadHandshakeKeyAsync(Connection conn)
    {
        const int MaxHandshakeBytes = 16 * 1024;

        var buf = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var bufLen = 0;

        try
        {
            while (true)
            {
                var snap = await conn.ReadAsync().ConfigureAwait(false);
                if (snap.IsClosed)
                {
                    return null;
                }

                var rings = conn.GetAllSnapshotRingsAsUnmanagedMemory(snap);
                try
                {
                    foreach (var r in rings)
                    {
                        var span = r.GetSpan();
                        
                        if (bufLen + span.Length > MaxHandshakeBytes)
                        {
                            return null;
                        }
                        
                        EnsureCapacity(ref buf, bufLen + span.Length);
                        span.CopyTo(buf.AsSpan(bufLen));
                        bufLen += span.Length;
                    }
                }
                finally
                {
                    foreach (var r in rings) conn.ReturnRing(r.BufferId);
                    conn.ResetRead();
                }

                var seq = new ReadOnlySequence<byte>(buf, 0, bufLen);
                var result = HandshakeParser.TryParse(seq, out var key, out _);

                switch (result)
                {
                    case HandshakeParser.Result.Ok: return key;
                    case HandshakeParser.Result.BadRequest: return null;
                    case HandshakeParser.Result.Incomplete: continue;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static ValueTask WriteHandshakeResponseAsync(Connection conn, string key)
    {
        var accept = Handshake.CreateAcceptKey(key);
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        
        var bytes = Encoding.ASCII.GetBytes(response);
        conn.Write(bytes.AsSpan());
        
        return conn.FlushAsync();
    }

    private static ValueTask WriteSimpleHttpResponseAsync(Connection conn, string status)
    {
        var response = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        conn.Write(bytes.AsSpan());
        
        return conn.FlushAsync();
    }

    private static void EnsureCapacity(ref byte[] buf, int needed)
    {
        if (buf.Length >= needed)
        {
            return;
        }
        
        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buf.Length * 2));
        
        Buffer.BlockCopy(buf, 0, bigger, 0, buf.Length);
        
        ArrayPool<byte>.Shared.Return(buf);
        
        buf = bigger;
    }
}
