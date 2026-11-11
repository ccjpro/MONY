using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MONY;

internal static class DashboardServer
{
    internal const int DefaultWebPort = 2417;
    internal const int DefaultApiPort = 2727;

    internal static WebApplication Create(MonitorServices services, MonitoringSession session, bool apiOnly, bool lan, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (lan)
                options.ListenAnyIP(port);
            else
                options.ListenLocalhost(port);

            options.AddServerHeader = false;
        });

        var app = builder.Build();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            var crossSite = context.Request.Headers["Sec-Fetch-Site"] == "cross-site" &&
                context.Request.Headers["Sec-Fetch-Mode"] != "navigate";
            var settings = services.Settings.Current;
            var options = settings.Options;
            var proxied = !apiOnly && IsCloudflareTunnel(context);
            if ((!proxied && !IsLocalHostAddress(context, port)) || crossSite)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (proxied)
                context.Request.Scheme = "https";

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; " +
                "manifest-src 'self'; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

            var enabled = apiOnly ? options.ApiEnabled : options.WebEnabled;
            var allowLan = apiOnly ? options.ApiLan : options.WebLan;
            var credential = apiOnly ? settings.ApiPassword : settings.WebPassword;
            var remote = context.Connection.RemoteIpAddress;
            if (!enabled || (!allowLan && remote is not null && !IPAddress.IsLoopback(Normalize(remote))))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var requiresPassword = apiOnly ? !PublicLoginRoute(context.Request, apiOnly) :
                context.GetEndpoint()?.Metadata.GetMetadata<SettingsAccess>() is not null;
            if (credential is not null && requiresPassword &&
                !LoginCookie.Valid(context.Request, credential, apiOnly) && !CheckPassword(context.Request, credential))
            {
                if (!apiOnly && context.GetEndpoint()?.Metadata.GetMetadata<SettingsAccess>()?.ShowLogin == true)
                {
                    await Page("login").ExecuteAsync(context);
                }
                else if (apiOnly && HttpMethods.IsGet(context.Request.Method) &&
                    context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    var target = LoginTarget(context.Request.Path, apiOnly);
                    context.Response.Redirect("/login" + QueryString.Create("next", target));
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                return;
            }
            await next(context);
        });

        IResult RawData()
        {
            var snapshot = session.Snapshot;
            var options = services.Settings.Current.Options;
            return Results.Json(new RawMonitorData(snapshot.Version, options.ComputerName, options.PreferredIp,
                snapshot.Paused, snapshot.Sample, snapshot.Metrics[0].Detail));
        }

        IResult Page(string name, bool offline = false)
        {
            var preference = services.Settings.Current.Options.Language;
            var html = ResourceText($"Web.{name}.html").Replace("<html lang=\"zh-CN\">",
                $"<html lang=\"zh-CN\" data-language=\"{preference}\">", StringComparison.Ordinal);
            if (offline) html = html.Replace("<html ", "<html data-offline=\"true\" ", StringComparison.Ordinal);
            if (apiOnly && name == "login")
                html = html.Replace("id=\"login-back\"", "id=\"login-back\" hidden", StringComparison.Ordinal);
            return Results.Text(html, "text/html; charset=utf-8");
        }

        bool English(HttpContext context) =>
            UiText.IsEnglish(services.Settings.Current.Options.Language,
                context.Request.Query["language"].FirstOrDefault() ?? context.Request.Headers.AcceptLanguage.ToString());

        app.MapGet("/login", () => Page("login"));
        IResult LocaleScript() => Results.Text("const translations = " + UiText.Source + ";\n" + ResourceText("Web.i18n.js"),
            "text/javascript; charset=utf-8");
        app.MapGet("/i18n.js", LocaleScript);
        app.MapGet("/locale.js", LocaleScript);
        app.MapGet("/locale-v3.js", LocaleScript);
        app.MapGet("/locale-v4.js", LocaleScript);
        Static("/overview.css", "Web.dashboard.css", "text/css; charset=utf-8");
        Static("/overview-v2.css", "Web.dashboard.css", "text/css; charset=utf-8");
        Static("/overview-v3.css", "Web.dashboard.css", "text/css; charset=utf-8");
        Static("/overview-v4.css", "Web.dashboard.css", "text/css; charset=utf-8");
        Static("/overview-v5.css", "Web.dashboard.css", "text/css; charset=utf-8");
        Static("/login.js", "Web.login.js", "text/javascript; charset=utf-8");
        Static("/dashboard.css", "Web.dashboard.css", "text/css; charset=utf-8");
        Static("/brand.svg", "Web.brand.svg", "image/svg+xml");
        Static("/favicon.ico", "Assets.MONY.ico", "image/x-icon");
        Static("/favicon-32.png", "Web.favicon-32.png", "image/png");
        Static("/apple-touch-icon.png", "Web.apple-touch-icon.png", "image/png");
        Static("/apple-touch-icon-v2.png", "Web.apple-touch-icon.png", "image/png");
        app.MapPost("/api/login", (HttpContext context, LoginRequest request) =>
        {
            if (!SameOrigin(context.Request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var settings = services.Settings.Current;
            var credential = apiOnly ? settings.ApiPassword : settings.WebPassword;
            if (credential is null)
                return Results.NoContent();
            if (request.Password is null || !CheckTextPassword(request.Password, credential))
                return Results.Json(new { message = "密码不正确。" }, statusCode: StatusCodes.Status401Unauthorized);
            LoginCookie.Issue(context, credential, apiOnly);
            return Results.NoContent();
        });

        void Static(string route, string name, string type)
        {
            app.MapMethods(route, ["GET", "HEAD"], (HttpContext context) =>
            {
                if (route.StartsWith("/favicon", StringComparison.Ordinal) || route.StartsWith("/apple-touch-icon", StringComparison.Ordinal))
                    context.Response.Headers.CacheControl = "public, max-age=86400";
                return Resource(name, type);
            });
        }

        app.MapGet("/api/dashboard", (HttpContext context) => Results.Json(services.Dashboard(localOnly: apiOnly, english: English(context))));
        if (apiOnly)
        {
            app.MapGet("/", RawData);
            app.MapGet("/api/data", RawData);
            return app;
        }

        app.MapGet("/", () => Page("index"));
        app.MapGet("/full", () => Page("full"));
        app.MapGet("/settings", () => Page("settings")).WithMetadata(new SettingsAccess(ShowLogin: true));
        app.MapGet("/full.js", () => Resource("Web.full.js", "text/javascript; charset=utf-8"));
        app.MapGet("/full.css", () => Resource("Web.full.css", "text/css; charset=utf-8"));
        Static("/dashboard.js", "Web.dashboard.js", "text/javascript; charset=utf-8");
        Static("/overview.js", "Web.dashboard.js", "text/javascript; charset=utf-8");
        Static("/overview-v2.js", "Web.dashboard.js", "text/javascript; charset=utf-8");
        Static("/overview-v3.js", "Web.dashboard.js", "text/javascript; charset=utf-8");
        Static("/overview-v4.js", "Web.dashboard.js", "text/javascript; charset=utf-8");
        Static("/alerts.js", "Web.alerts.js", "text/javascript; charset=utf-8");
        Static("/alerts-v2.js", "Web.alerts.js", "text/javascript; charset=utf-8");
        Static("/pwa.js", "Web.pwa.js", "text/javascript; charset=utf-8");
        Static("/manifest.webmanifest", "Web.manifest.webmanifest", "application/manifest+json; charset=utf-8");
        Static("/pwa-192.png", "Web.pwa-192.png", "image/png");
        Static("/pwa-512.png", "Web.pwa-512.png", "image/png");
        app.MapGet("/offline", () => Page("index", offline: true));
        app.MapGet("/sw.js", () =>
        {
            using var reader = new StreamReader(typeof(DashboardServer).Assembly
                .GetManifestResourceStream("MONY.Web.sw.js")!);
            return Results.Text($"const cacheName = \"mony-shell-{Program.Version}\";\n" + reader.ReadToEnd(),
                "text/javascript; charset=utf-8");
        });
        app.MapGet("/settings.js", () => Resource("Web.settings.js", "text/javascript; charset=utf-8"));
        app.MapGet("/api/settings", () => Results.Json(services.View())).WithMetadata(new SettingsAccess());
        app.MapPost("/api/settings/lock", (HttpContext context) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            LoginCookie.LockWeb(context);
            return Results.NoContent();
        }).WithMetadata(new SettingsAccess());
        app.MapGet("/api/alerts", (HttpContext context) =>
        {
            var visitor = context.Connection.RemoteIpAddress;
            if (IsCloudflareTunnel(context) &&
                IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(), out var forwarded))
                visitor = forwarded;
            var english = English(context);
            return Results.Json(services.Alerts.ForBrowser(visitor).Select(alert => new
            {
                alert.Id, alert.Time, Message = alert.Text(english)
            }).ToArray());
        });
        IResult ApplySettings(HttpContext context, Func<SettingsSnapshot> apply)
        {
            if (!SameOrigin(context.Request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var previousPassword = services.Settings.Current.WebPassword;
                var saved = apply();
                if (!ReferenceEquals(previousPassword, saved.WebPassword))
                {
                    if (saved.WebPassword is { } password)
                        LoginCookie.Issue(context, password, api: false);
                    else
                        LoginCookie.Clear(context, api: false);
                }
                context.Response.OnCompleted(() =>
                {
                    services.Refresh(saved);
                    return Task.CompletedTask;
                });
                return Results.Json(services.View());
            }
            catch (SettingsConflictException)
            {
                return Results.Conflict(new { message = "设置已在另一端修改，请重新加载。" });
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { message = error.Message });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
            {
                return Results.Json(new { message = "无法应用设置，请检查当前用户的权限。" }, statusCode: 500);
            }
        }
        app.MapPost("/api/settings", (HttpContext context, SettingsChange change) =>
            ApplySettings(context, () => services.Settings.Save(change))).WithMetadata(new SettingsAccess());
        app.MapPatch("/api/settings", (HttpContext context, SettingUpdate change) =>
            ApplySettings(context, () => services.Settings.Update(change))).WithMetadata(new SettingsAccess());
        app.MapPost("/api/targets", async (HttpContext context, RemoteConnection connection) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                await services.Remotes.ConnectAsync(connection, context.RequestAborted);
                return Results.Json(services.View());
            }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            { return Results.Json(new { message = "无法保存监控连接。" }, statusCode: 500); }
        }).WithMetadata(new SettingsAccess());
        app.MapDelete("/api/targets/{id}", (HttpContext context, string id) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { services.Remotes.Remove(id); return Results.Json(services.View()); }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Results.Json(new { message = "无法删除监控连接。" }, statusCode: 500); }
        }).WithMetadata(new SettingsAccess());
        app.MapPost("/api/target", (HttpContext context, TargetSelection selection) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { services.Remotes.Select(selection.Id); return Results.Json(services.Dashboard(english: English(context))); }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
        });
        app.MapPost("/api/pause", (HttpContext context, PauseRequest change) =>
        {
            if (!SameOrigin(context.Request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (change.Paused is not bool paused) return Results.BadRequest();
            services.Remotes.SetPaused(paused);
            return Results.Json(services.Dashboard(english: English(context)));
        });
        return app;
    }

    private sealed record SettingsAccess(bool ShowLogin = false);

    private static bool SameOrigin(HttpRequest request) =>
        request.Headers.Origin.Count == 0 ||
        string.Equals(request.Headers.Origin, $"{request.Scheme}://{request.Host.Host}" +
            (request.Host.Port is int port && port != (request.IsHttps ? 443 : 80) ? $":{port}" : ""), StringComparison.OrdinalIgnoreCase);

    private static bool IsCloudflareTunnel(HttpContext context)
    {
        // Only a connector on this computer can supply the trusted forwarding headers.
        var host = context.Request.Host;
        var headers = context.Request.Headers;
        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(Normalize(remote)) &&
            host.Port is null or 443 && host.Host.Contains('.') && Uri.CheckHostName(host.Host) == UriHostNameType.Dns &&
            string.Equals(headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase) &&
            IPAddress.TryParse(headers["CF-Connecting-IP"].ToString(), out _) &&
            headers["CF-Ray"].Count == 1 && !string.IsNullOrWhiteSpace(headers["CF-Ray"]);
    }

    private static bool IsLocalHostAddress(HttpContext context, int port)
    {
        var host = context.Request.Host;
        if ((host.Port ?? 80) != port || context.Connection.LocalIpAddress is not { } local)
            return false;
        local = Normalize(local);
        if (host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.IsLoopback(local);

        return IPAddress.TryParse(host.Host.Trim('[', ']'), out var address) &&
            Normalize(address).GetAddressBytes().AsSpan().SequenceEqual(local.GetAddressBytes());
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string LoginTarget(PathString path, bool apiOnly) =>
        path.Value?.TrimEnd('/').ToLowerInvariant() switch
        {
            "/settings" when !apiOnly => "/settings",
            "/full" when !apiOnly => "/full",
            "/api/data" when apiOnly => "/api/data",
            "/api/dashboard" when apiOnly => "/api/dashboard",
            _ => "/"
        };

    private static bool PublicLoginRoute(HttpRequest request, bool apiOnly)
    {
        if (HttpMethods.IsPost(request.Method))
            return request.Path == "/api/login";
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        return request.Path.Value switch
        {
            "/login" or "/login.js" or "/i18n.js" or "/locale.js" or "/locale-v3.js" or "/locale-v4.js" or
                "/overview.css" or "/overview-v2.css" or "/overview-v3.css" or "/overview-v4.css" or "/overview-v5.css" or "/dashboard.css" or "/brand.svg" or "/favicon.ico" or
                "/favicon-32.png" or "/apple-touch-icon.png" or "/apple-touch-icon-v2.png" => true,
            "/offline" or "/dashboard.js" or "/overview.js" or "/overview-v2.js" or "/pwa.js" or "/sw.js" or "/manifest.webmanifest" or
                "/pwa-192.png" or "/pwa-512.png" => !apiOnly,
            _ => false
        };
    }

    private static bool CheckTextPassword(string password, PasswordCredential credential)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        try { return credential.Verify(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool CheckPassword(HttpRequest request, PasswordCredential credential)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) || header.Parameter is not { } encoded)
            return false;

        var decoded = new byte[encoded.Length];
        try
        {
            return Convert.TryFromBase64String(encoded, decoded, out var length) &&
                decoded.AsSpan(0, length).StartsWith("pulse:"u8) && credential.Verify(decoded.AsSpan(6, length - 6));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static string ResourceText(string name)
    {
        using var reader = new StreamReader(typeof(DashboardServer).Assembly.GetManifestResourceStream($"MONY.{name}")!);
        return reader.ReadToEnd();
    }

    private static IResult Resource(string name, string contentType) =>
        Results.File(typeof(DashboardServer).Assembly.GetManifestResourceStream($"MONY.{name}")!, contentType);

    internal sealed record LoginRequest(string? Password);
    internal sealed record PauseRequest(bool? Paused);
}
