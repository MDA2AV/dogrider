using System.Security.Cryptography;
using System.Text;

namespace dogrider.Protocol;

public static class Handshake
{
    private const string MagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static string CreateAcceptKey(string clientKey)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(clientKey + MagicGuid));
        
        return Convert.ToBase64String(hash);
    }
}
