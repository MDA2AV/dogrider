namespace dogrider.Server;

public interface Handler
{
    ValueTask HandleAsync(IConnection connection);
}
