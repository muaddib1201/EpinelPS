using EpinelPS.Utils;

using System.Net;

namespace EpinelPS.Networking;

public class EncryptionMiddleware(RequestDelegate next)
{
    internal const string BatchNonceHeader = "X-Epinel-Batch-Request-Nonce";
    private readonly RequestDelegate _next = next;

    public async Task Invoke(HttpContext context)
    {
        if (context.Request.Path.ToString().StartsWith("/v1") || context.Request.Path.ToString().StartsWith("/$batch"))
        {
            var x = await PacketDecryption.DecryptOrReturnContentAsync(context);
            
            if (x.Contents.Length > 0)
            {
                context.Request.Body = new MemoryStream(x.Contents);
            }
            context.Items["UserID"] = x.UserId;
            if (x.Nonce != null)
                context.Items[nameof(PacketDecryptResponse.Nonce)] = x.Nonce;
            else if (context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
                context.Request.Headers.TryGetValue(BatchNonceHeader, out var value) && value.Count == 1 && value[0]?.Length == 64)
            {
                // Only the loopback dispatcher may supply a derived batch-part identity.
                try { context.Items[nameof(PacketDecryptResponse.Nonce)] = Convert.FromHexString(value[0]!); }
                catch (FormatException) { throw new BadHttpRequestException("Invalid internal batch nonce.", 400); }
            }
        }

        await _next(context);
    }
}
