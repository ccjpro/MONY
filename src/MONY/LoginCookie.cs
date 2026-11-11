using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace MONY;

internal static class LoginCookie
{
    private const int PayloadLength = 25;
    private const int TokenLength = PayloadLength + 32;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    private static readonly ConcurrentDictionary<string, long> LockedWebSessions = new();

    internal static bool Valid(HttpRequest request, PasswordCredential credential, bool api)
    {
        if (!request.Cookies.TryGetValue(Name(api), out var encoded) || encoded.Length != 76)
            return false;

        byte[] token;
        try
        {
            token = WebEncoders.Base64UrlDecode(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        if (token.Length != TokenLength || token[0] != (api ? 1 : 0))
            return false;
        var expires = BinaryPrimitives.ReadInt64BigEndian(token.AsSpan(1));
        if (expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || (!api && LockedWebSessions.ContainsKey(encoded)))
            return false;

        Span<byte> signature = stackalloc byte[32];
        HMACSHA256.HashData(credential.Hash, token.AsSpan(0, PayloadLength), signature);
        return CryptographicOperations.FixedTimeEquals(token.AsSpan(PayloadLength), signature);
    }

    internal static void Issue(HttpContext context, PasswordCredential credential, bool api)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        Span<byte> token = stackalloc byte[TokenLength];
        token[0] = api ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(token[1..], expires.ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(token[9..PayloadLength]);
        HMACSHA256.HashData(credential.Hash, token[..PayloadLength], token[PayloadLength..]);

        var options = Options(context);
        options.MaxAge = Lifetime;
        options.Expires = expires;
        context.Response.Cookies.Append(Name(api), WebEncoders.Base64UrlEncode(token), options);
    }

    internal static void LockWeb(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(Name(api: false), out var token) && token.Length == 76)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var item in LockedWebSessions)
                if (item.Value <= now) LockedWebSessions.TryRemove(item.Key, out _);
            LockedWebSessions[token] = now + (long)Lifetime.TotalSeconds;
        }
        Clear(context, api: false);
    }

    internal static void Clear(HttpContext context, bool api) =>
        context.Response.Cookies.Delete(Name(api), Options(context));

    private static string Name(bool api) => api ? "MONYApi" : "MONYWeb";

    private static CookieOptions Options(HttpContext context) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax
    };
}
