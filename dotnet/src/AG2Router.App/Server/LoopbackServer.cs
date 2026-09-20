using System.IO;
using System.Net;
using AG2Router.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AG2Router.App.Server;

/// <summary>
/// Embedded in-process ASP.NET Core loopback HTTP host.
/// Binds strictly to 127.0.0.1 on an ephemeral or configured port, serving the dashboard
/// UI assets from wwwroot and truthful un-migrated API endpoints.
/// </summary>
public class LoopbackServer : IAsyncDisposable
{
    private WebApplication? _app;
    private int _boundPort;
    private string _boundUrl = string.Empty;

    public int BoundPort => _boundPort;
    public string BoundUrl => _boundUrl;

    public async Task StartAsync(int requestedPort = 0, Func<SystemStatusDto>? statusProvider = null, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // 1. Enforce strict IPv4 loopback binding on Kestrel
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(IPAddress.Loopback, requestedPort);
            serverOptions.AddServerHeader = false;
            serverOptions.Limits.MaxConcurrentConnections = 30;
            serverOptions.Limits.MaxRequestBodySize = 4 * 1024 * 1024; // 4 MB limit
        });

        // 2. Add routing and static file support
        builder.Services.AddRouting();

        _app = builder.Build();

        // 3. Security Middleware: Reject any non-loopback connections
        _app.Use(async (context, next) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Loopback access only.", cancellationToken);
                return;
            }

            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Cache-Control", "no-store");
            await next();
        });

        // 4. Static files from wwwroot (staged from src/ui)
        var wwwrootDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwrootDir))
        {
            var fileProvider = new PhysicalFileProvider(wwwrootDir);
            _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            _app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
        }

        // 5. Minimal API Routes for Dashboard Compatibility
        // GET /api/status - Live or fallback telemetry status
        _app.MapGet("/api/status", () =>
        {
            if (statusProvider != null)
            {
                return Results.Ok(statusProvider());
            }

            var status = new SystemStatusDto(
                Status: "ok",
                Ag2: new Ag2StatusDto(
                    Connected: false,
                    Status: "OFFLINE",
                    Activity: new ActivityStatusDto("OFFLINE", 0, 0, DateTime.UtcNow.ToString("o")),
                    Message: "Waiting for Antigravity 2"
                ),
                Router: new RouterStatusDto(
                    State: "IDLE",
                    AutoSwitchEnabled: false,
                    ActiveAccountId: null,
                    ActiveAccountEmail: null,
                    PendingTargetAccountId: null,
                    LastEvaluatedAt: null,
                    LastDecisionReason: "Native shell foundation active. Telemetry not yet connected.",
                    Config: new RouterConfigDto()
                ),
                Telemetry: null
            );
            return Results.Ok(status);
        });

        // GET /api/accounts - Accounts list
        _app.MapGet("/api/accounts", () =>
        {
            return Results.Ok(new AccountsListDto(Array.Empty<AccountMetadata>()));
        });

        // GET /api/config - Current configuration
        _app.MapGet("/api/config", () =>
        {
            return Results.Ok(new { config = new RouterConfigDto() });
        });

        // GET /api/switching/status - Switching state machine status
        _app.MapGet("/api/switching/status", () =>
        {
            return Results.Ok(new SwitchStatusContainerDto(new SwitchingStatusDto(
                ActiveTransactionId: null,
                CurrentState: "IDLE",
                LastResult: null
            )));
        });

        // Mutation endpoints: Expose explicit safe un-migrated / forbidden behaviors
        _app.MapPost("/api/accounts/enroll-current", () =>
            Results.Json(new { error = "Account enrollment is not implemented in native shell foundation." }, statusCode: StatusCodes.Status501NotImplemented));

        _app.MapPost("/api/accounts/{id}/switch-plan", (string id) =>
            Results.Json(new { error = "Switch planning is not implemented in native shell foundation." }, statusCode: StatusCodes.Status501NotImplemented));

        _app.MapPost("/api/accounts/{id}/switch", (string id) =>
            Results.Json(new { error = "Live account switching execution is not authorized in this runtime mode." }, statusCode: StatusCodes.Status403Forbidden));

        _app.MapDelete("/api/accounts/{id}", (string id) =>
            Results.Json(new { error = "Account deletion is not implemented in native shell foundation." }, statusCode: StatusCodes.Status501NotImplemented));

        _app.MapPost("/api/config", () =>
            Results.Json(new { error = "Configuration updates are not implemented in native shell foundation." }, statusCode: StatusCodes.Status501NotImplemented));

        // 6. Start Kestrel asynchronously without blocking
        await _app.StartAsync(cancellationToken);

        // 7. Discover the dynamically assigned ephemeral port
        var server = _app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        var boundAddress = addressesFeature?.Addresses.FirstOrDefault();

        if (boundAddress != null && Uri.TryCreate(boundAddress, UriKind.Absolute, out var uri))
        {
            _boundPort = uri.Port;
            _boundUrl = $"http://127.0.0.1:{_boundPort}";
        }
        else
        {
            _boundPort = requestedPort;
            _boundUrl = $"http://127.0.0.1:{_boundPort}";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app != null)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await _app.StopAsync(linked.Token);
            }
            catch
            {
                // Gracefully swallow timeout on shutdown
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
