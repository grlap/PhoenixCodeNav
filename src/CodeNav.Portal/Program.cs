using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CodeNav.Portal;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseOptions(args, out PortalOptions options, out string? optionError))
        {
            Console.Error.WriteLine(optionError);
            Console.Error.WriteLine(
                "Usage: PhoenixCodeNav.Portal [--workspace-root <dir>] [--launcher]");
            return 2;
        }
        if (options.Help)
        {
            Console.Error.WriteLine(
                "Usage: PhoenixCodeNav.Portal [--workspace-root <dir>] [--launcher]");
            return 0;
        }

        string? workspaceRoot = options.WorkspaceRoot is null
            ? null
            : Path.GetFullPath(options.WorkspaceRoot);
        if (workspaceRoot is not null && !Directory.Exists(workspaceRoot))
        {
            Console.Error.WriteLine($"Workspace root not found: {workspaceRoot}");
            return 2;
        }

        PortalLaunchCoordinator? launchCoordinator = null;
        if (options.Launcher)
        {
            workspaceRoot ??= Directory.GetCurrentDirectory();
            try
            {
                launchCoordinator = await PortalLaunchCoordinator.AcquireAsync(
                    workspaceRoot,
                    CancellationToken.None).ConfigureAwait(false);
                if (!launchCoordinator.IsOwner)
                {
                    Console.Out.WriteLine(PortalLaunchCoordinator.Serialize(
                        launchCoordinator.ReusedHandshake!));
                    Console.Out.Flush();
                    await launchCoordinator.DisposeAsync().ConfigureAwait(false);
                    return 0;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Portal launcher coordination failed: {ex.Message}");
                return 1;
            }
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        if (options.Launcher)
            builder.Logging.ClearProviders();
        else
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(services =>
        {
            ILogger<PortalDataSource> logger =
                services.GetRequiredService<ILogger<PortalDataSource>>();
            return workspaceRoot is null
                ? new PortalDataSource(logger)
                : new PortalDataSource([workspaceRoot], logger);
        });
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<PortalDataSource>());

        string accessToken = CreateSessionSecret();
        string launchSessionId = CreateSessionSecret();
        await using WebApplication app = builder.Build();

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

        app.MapGet("/healthz", () => Results.Ok(new PortalHealthStatus(
            "ok",
            PortalDataSource.PortalVersion,
            ApiVersion: 1,
            PortalLaunchCoordinator.ProtocolVersion,
            Environment.ProcessId,
            launchSessionId,
            ReadOnly: true)));

        app.MapGet("/api/v1/bootstrap", static (PortalDataSource source) =>
            Results.Ok(source.Bootstrap()));
        app.MapGet("/api/v1/operations", Operations);
        app.MapGet("/api/v1/events", Events);
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

        try
        {
            await app.StartAsync().ConfigureAwait(false);
            IServer server = app.Services.GetRequiredService<IServer>();
            PortalDataSource source =
                app.Services.GetRequiredService<PortalDataSource>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? "http://127.0.0.1";
            string url = $"{address.TrimEnd('/')}/#token={accessToken}";
            if (launchCoordinator is not null)
            {
                PortalLaunchHandshake handshake =
                    await launchCoordinator.PublishStartedAsync(
                        url,
                        source.WorkspaceCount,
                        launchSessionId,
                        CancellationToken.None).ConfigureAwait(false);
                Console.Out.WriteLine(PortalLaunchCoordinator.Serialize(handshake));
                Console.Out.Flush();
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Phoenix Operations Portal");
                Console.WriteLine($"Open {url}");
                Console.WriteLine(
                    $"Read-only telemetry view configured for {source.WorkspaceCount} workspace(s)");
                Console.WriteLine();
            }

            await app.WaitForShutdownAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (launchCoordinator is not null)
                await launchCoordinator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryParseOptions(
        string[] args,
        out PortalOptions options,
        out string? error)
    {
        string? workspaceRoot = null;
        bool launcher = false;
        bool help = false;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--launcher":
                    launcher = true;
                    break;
                case "--workspace-root" or "-w":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        options = default;
                        error = "--workspace-root requires a directory.";
                        return false;
                    }
                    workspaceRoot = args[i];
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    options = default;
                    error = $"Unknown argument: {args[i]}";
                    return false;
            }
        }

        options = new PortalOptions(workspaceRoot, launcher, help);
        return true;
    }

    private static IResult Operations(HttpRequest request, PortalDataSource source)
    {
        if (!PortalOperationQuery.TryParse(
                request.Query,
                out PortalOperationQuery query,
                out string? error))
        {
            return InvalidQuery(error);
        }
        try
        {
            return Results.Ok(source.Operations(query));
        }
        catch (PortalCursorExpiredException)
        {
            return CursorExpired();
        }
    }

    private static IResult Events(HttpRequest request, PortalDataSource source)
    {
        if (!PortalEventQuery.TryParse(
                request.Query,
                out PortalEventQuery query,
                out string? error))
        {
            return InvalidQuery(error);
        }
        try
        {
            return Results.Ok(source.Events(query));
        }
        catch (PortalCursorExpiredException)
        {
            return CursorExpired();
        }
    }

    private static IResult InvalidQuery(string? message) =>
        Results.BadRequest(new
        {
            error = new
            {
                code = "invalid_query",
                message = message ?? "The query is invalid.",
                retryable = false
            }
        });

    private static IResult CursorExpired() =>
        Results.BadRequest(new
        {
            error = new
            {
                code = "cursor_expired",
                message = "The cursor is outside this portal session, filter, or retained window.",
                retryable = true
            }
        });

    private static string CreateSessionSecret()
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

    private readonly record struct PortalOptions(
        string? WorkspaceRoot,
        bool Launcher,
        bool Help);
}
