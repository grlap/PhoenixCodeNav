using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CodeNav.Portal;

public static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        string accessToken = CreateAccessToken();
        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            ApplySecurityHeaders(context.Response);

            if (!IsLoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "invalid_host",
                        message = "The Operations Portal accepts loopback requests only.",
                        retryable = false
                    }
                });
                return;
            }

            if (!HasAllowedOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "invalid_origin",
                        message = "The request origin does not match this portal session.",
                        retryable = false
                    }
                });
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !HasBearerToken(context.Request, accessToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "unauthorized",
                        message = "A valid portal session token is required.",
                        retryable = false
                    }
                });
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = static context =>
            {
                context.Context.Response.Headers.CacheControl = "no-store";
            }
        });

        app.MapGet("/healthz", static () => Results.Ok(new
        {
            status = "ok",
            portalVersion = "0.1.0-preview",
            apiVersion = 1
        }));

        app.MapGet("/api/v1/bootstrap", static () => Results.Ok(PortalFixtures.Bootstrap()));
        app.MapGet("/api/v1/operations", static () => Results.Ok(PortalFixtures.Operations()));
        app.MapGet("/api/v1/events", static () => Results.Ok(PortalFixtures.Events()));
        app.MapMethods(
            "/api/{**path}",
            ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
            static () => Results.NotFound(new
            {
                error = new
                {
                    code = "route_not_found",
                    message = "The requested diagnostic route does not exist.",
                    retryable = false
                }
            }));
        app.MapFallbackToFile("index.html");

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? "http://127.0.0.1";
            Console.WriteLine();
            Console.WriteLine("Phoenix Operations Portal");
            Console.WriteLine($"Open {address}/#token={accessToken}");
            Console.WriteLine("Fixture mode - no Phoenix runtime dependency");
            Console.WriteLine();
        });

        await app.RunAsync();
    }

    private static string CreateAccessToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool HasBearerToken(HttpRequest request, string accessToken)
    {
        string authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string supplied = authorization[prefix.Length..];
        byte[] expectedBytes = System.Text.Encoding.UTF8.GetBytes(accessToken);
        byte[] suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAllowedOrigin(HttpRequest request)
    {
        string origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !IsLoopbackHost(uri.Host))
        {
            return false;
        }

        return uri.Port == request.HttpContext.Connection.LocalPort;
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
            + "connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.CacheControl = "no-store";
    }
}
