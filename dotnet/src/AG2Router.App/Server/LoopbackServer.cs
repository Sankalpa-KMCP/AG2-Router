using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Security;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using AG2Router.Windows.Lifecycle;
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

    public async Task StartAsync(
        int requestedPort = 0,
        Func<SystemStatusDto>? statusProvider = null,
        IAccountStore? accountStore = null,
        ISessionVault? sessionVault = null,
        AccountEnrollmentService? enrollmentService = null,
        INativeAccountSwitchCoordinator? switchCoordinator = null,
        INativeAutoRouter? autoRouter = null,
        IAutostartService? autostartService = null,
        CancellationToken cancellationToken = default)
    {
        string switchIntentToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
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
            if (!IsAllowedLoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Invalid loopback host.", cancellationToken);
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
                Router: autoRouter?.GetStatus() ?? new RouterStatusDto(
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

        // GET /api/accounts - Enriched accounts list
        _app.MapGet("/api/accounts", async () =>
        {
            if (accountStore == null)
            {
                return Results.Ok(new AccountsListDto(Array.Empty<AccountMetadata>()));
            }

            var accounts = await accountStore.ListAccountsAsync();
            var activeId = await accountStore.GetActiveAccountIdAsync();

            var enriched = new List<AccountMetadata>(accounts.Count);
            foreach (var acc in accounts)
            {
                bool hasVaulted = acc.HasVaultedSession;
                if (!hasVaulted && sessionVault != null)
                {
                    hasVaulted = await sessionVault.HasSessionAsync(acc.Id);
                }

                enriched.Add(acc with
                {
                    HasVaultedSession = hasVaulted,
                    IsActive = acc.Id == activeId
                });
            }

            return Results.Ok(new AccountsListDto(enriched));
        });

        // POST /api/accounts - Register account metadata
        _app.MapPost("/api/accounts", async (HttpContext context) =>
        {
            if (accountStore == null)
            {
                return Results.Json(new { error = "Account store is not configured." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            CreateAccountInput? input;
            try
            {
                input = await context.Request.ReadFromJsonAsync<CreateAccountInput>();
            }
            catch (Exception)
            {
                return Results.Json(new { error = "Invalid JSON payload" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (input == null || string.IsNullOrWhiteSpace(input.Email) || !input.Email.Contains('@'))
            {
                return Results.Json(new { error = "A valid email address is required." }, statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var created = await accountStore.AddAccountAsync(input);
                return Results.Json(new { account = created }, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = AG2Security.RedactSensitiveText(ex.Message) }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // POST /api/accounts/enroll-current - Safely enroll active account from telemetry and WinCred
        _app.MapPost("/api/accounts/enroll-current", async (HttpContext context) =>
        {
            if (enrollmentService == null)
            {
                return Results.Json(new { error = "Account enrollment service is not configured" }, statusCode: StatusCodes.Status501NotImplemented);
            }

            EnrollmentOptions? options = null;
            if (context.Request.ContentLength > 0)
            {
                try
                {
                    options = await context.Request.ReadFromJsonAsync<EnrollmentOptions>();
                }
                catch (Exception)
                {
                    return Results.Json(new { error = "Invalid JSON payload" }, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            try
            {
                var result = await enrollmentService.EnrollCurrentAccountAsync(options);
                return Results.Ok(new
                {
                    success = result.Success,
                    account = result.Account,
                    isNew = result.IsNew,
                    message = result.Message
                });
            }
            catch (AccountEnrollmentException ex)
            {
                return Results.Json(new { error = AG2Security.RedactSensitiveText(ex.Message) }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = AG2Security.RedactSensitiveText(ex.Message) }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // DELETE /api/accounts/{id} - Remove account metadata and vaulted session
        _app.MapDelete("/api/accounts/{id}", async (string id) =>
        {
            if (accountStore == null)
            {
                return Results.Json(new { error = "Account store is not configured." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\'))
            {
                return Results.Json(new { error = "Account ID required" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var existing = await accountStore.GetAccountAsync(id);
            if (existing == null)
            {
                return Results.Json(new { error = "Account not found" }, statusCode: StatusCodes.Status404NotFound);
            }

            bool removed = await accountStore.RemoveAccountAsync(id);
            bool vaultDeleted = false;
            if (sessionVault != null)
            {
                vaultDeleted = await sessionVault.RemoveSessionAsync(id);
            }

            return Results.Ok(new
            {
                success = removed,
                removedId = id,
                vaultRecordDeleted = vaultDeleted
            });
        });

        // GET /api/switching/status - Switching state machine status
        _app.MapGet("/api/switching/status", (HttpContext context) =>
        {
            if (switchCoordinator == null)
            {
                return Results.Ok(new SwitchStatusContainerDto(new SwitchingStatusDto(
                    ActiveTransactionId: null,
                    CurrentState: "IDLE",
                    LastResult: null
                )));
            }
            // Same-origin dashboard clients obtain this per-launch token. A cross-origin form
            // cannot read it or attach the required custom header.
            context.Response.Headers["X-AG2-Switch-Token"] = switchIntentToken;
            var status = switchCoordinator.GetStatus();
            if (status.LastResult != null)
            {
                status = status with
                {
                    LastResult = status.LastResult with
                    {
                        Message = AG2Security.RedactSensitiveText(
                            AG2Security.SanitizeCommandLine(status.LastResult.Message))
                    }
                };
            }
            return Results.Ok(new { status });
        });

        // POST /api/accounts/{id}/switch-plan - Dry-run evaluation
        _app.MapPost("/api/accounts/{id}/switch-plan", (string id) =>
            Results.Json(new { error = "Switch planner service is not configured" }, statusCode: StatusCodes.Status501NotImplemented));

        // POST /api/accounts/{id}/switch - Explicit user-requested native switch only.
        _app.MapPost("/api/accounts/{id}/switch", async (string id, HttpContext context) =>
        {
            if (switchCoordinator == null)
            {
                return Results.Json(new { error = "Native account switching is not configured." },
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\'))
            {
                return Results.Json(new { error = "Account ID required" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string suppliedToken = context.Request.Headers["X-AG2-Switch-Token"].ToString();
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Explicit switch origin is not authorized." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (!FixedTimeEquals(suppliedToken, switchIntentToken))
            {
                return Results.Json(new { error = "Explicit switch authorization is required." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            ExplicitSwitchRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ExplicitSwitchRequest>(
                    cancellationToken: context.RequestAborted);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "A valid explicit switch confirmation is required." },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (request?.Confirm != true)
            {
                return Results.Json(new { error = "Explicit switch confirmation is required." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await switchCoordinator.SwitchAsync(id, context.RequestAborted);
            result = result with
            {
                Message = AG2Security.RedactSensitiveText(
                    AG2Security.SanitizeCommandLine(result.Message))
            };
            int statusCode = result.Code switch
            {
                SwitchResultCodes.Success => StatusCodes.Status200OK,
                SwitchResultCodes.TargetNotFound => StatusCodes.Status404NotFound,
                SwitchResultCodes.TelemetryUnavailable => StatusCodes.Status503ServiceUnavailable,
                SwitchResultCodes.SwitchFailedRollbackFailed => StatusCodes.Status500InternalServerError,
                SwitchResultCodes.Cancelled => StatusCodes.Status408RequestTimeout,
                SwitchResultCodes.SwitchFailedRolledBack => StatusCodes.Status500InternalServerError,
                SwitchResultCodes.TargetNotVaulted or
                SwitchResultCodes.AlreadyActive or
                SwitchResultCodes.Ag2Busy or
                SwitchResultCodes.UnsafeProcess or
                SwitchResultCodes.SwitchInProgress => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError
            };
            return Results.Json(result, statusCode: statusCode);
        });

        // GET /api/config
        _app.MapGet("/api/config", () =>
        {
            var cfg = autoRouter?.GetConfig() ?? new RouterConfigDto();
            return Results.Ok(new { config = cfg });
        });

        // POST /api/config
        _app.MapPost("/api/config", async (HttpContext context) =>
        {
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (autoRouter == null)
            {
                return Results.Json(new { error = "Auto-router is not configured." }, statusCode: StatusCodes.Status501NotImplemented);
            }
            try
            {
                var body = await context.Request.ReadFromJsonAsync<RouterConfigDto>(cancellationToken: context.RequestAborted);
                if (body == null)
                {
                    return Results.Json(new { error = "Invalid configuration payload." }, statusCode: StatusCodes.Status400BadRequest);
                }
                var updated = autoRouter.UpdateConfig(body);
                return Results.Ok(new { config = updated });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = AG2Security.RedactSensitiveText(ex.Message) }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // GET /api/router/status
        _app.MapGet("/api/router/status", () =>
        {
            var r = autoRouter?.GetStatus() ?? new RouterStatusDto(
                State: RoutingSafetyGateState.Idle,
                AutoSwitchEnabled: false,
                ActiveAccountId: null,
                ActiveAccountEmail: null,
                PendingTargetAccountId: null,
                LastEvaluatedAt: null,
                LastDecisionReason: "Router not configured",
                Config: new RouterConfigDto()
            );
            return Results.Ok(new { router = r });
        });

        // POST /api/router/reset-recovery
        _app.MapPost("/api/router/reset-recovery", (HttpContext context) =>
        {
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (autoRouter == null)
            {
                return Results.Json(new { error = "Auto-router is not configured." }, statusCode: StatusCodes.Status501NotImplemented);
            }
            autoRouter.ResetManualRecovery();
            return Results.Ok(new { success = true, router = autoRouter.GetStatus() });
        });

        // GET /api/settings/autostart
        _app.MapGet("/api/settings/autostart", () =>
        {
            if (autostartService == null)
            {
                return Results.Ok(new { enabled = false, supported = false });
            }
            return Results.Ok(new { enabled = autostartService.IsAutostartEnabled(), supported = true });
        });

        // POST /api/settings/autostart
        _app.MapPost("/api/settings/autostart", async (HttpContext context) =>
        {
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (autostartService == null)
            {
                return Results.Json(new { error = "Autostart service is not configured." }, statusCode: StatusCodes.Status501NotImplemented);
            }
            try
            {
                var body = await context.Request.ReadFromJsonAsync<AutostartRequest>(cancellationToken: context.RequestAborted);
                if (body == null)
                {
                    return Results.Json(new { error = "Payload required." }, statusCode: StatusCodes.Status400BadRequest);
                }
                autostartService.SetAutostartEnabled(body.Enabled);
                return Results.Ok(new { enabled = autostartService.IsAutostartEnabled(), supported = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = AG2Security.RedactSensitiveText(ex.Message) }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

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

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        try
        {
            return suppliedBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static bool IsAllowedLoopbackHost(string? host) =>
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedMutationOrigin(HttpContext context)
    {
        string origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            IsAllowedLoopbackHost(uri.Host) &&
            uri.Port == context.Connection.LocalPort;
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

public record AutostartRequest(bool Enabled);
