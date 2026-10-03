using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Security;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using AG2Router.Core.Validation;
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
        Action<TimeSpan>? onPollingIntervalChanged = null,
        Action<TimeSpan, long>? onPollingIntervalChangedWithGeneration = null,
        Func<TimeSpan, long, Task>? onPollingIntervalChangedAsync = null,
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

        // 3. Security Middleware: Reject any non-loopback connections and enforce origin/Sec-Fetch-Site boundaries
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

            // Global check for Sec-Fetch-Site: cross-site across all /api routes
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Cross-site requests forbidden.", cancellationToken);
                return;
            }

            // Reject foreign Origin whenever Origin is present
            string origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedLoopbackOrigin(origin, context.Connection.LocalPort))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Unauthorized origin.", cancellationToken);
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
                return Results.Ok(SanitizeStatusQuota(statusProvider()));
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
                return Results.Ok(new AccountsListDto(Array.Empty<AccountMetadata>(), 0, null));
            }

            var accounts = await accountStore.ListAccountsAsync();
            var activeId = await accountStore.GetActiveAccountIdAsync();

            var enriched = new List<AccountMetadata>(accounts.Count);
            foreach (var acc in accounts)
            {
                bool hasVaulted = await HasAvailableSessionAsync(sessionVault, acc.Id);

                enriched.Add(acc with
                {
                    HasVaultedSession = hasVaulted,
                    IsActive = acc.Id == activeId
                });
            }

            return Results.Ok(new AccountsListDto(enriched, enriched.Count, activeId));
        });

        // POST /api/accounts - Register account metadata
        _app.MapPost("/api/accounts", async (HttpContext context) =>
        {
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "Cross-site requests forbidden." }, statusCode: StatusCodes.Status403Forbidden);
            }
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
                input = input with { HasVaultedSession = false };
                AccountTextValidator.Validate(input.Name, input.Alias, input.Notes);
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
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
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
                AccountTextValidator.Validate(options?.Name, options?.Alias, options?.Notes);
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

        // PATCH /api/accounts/{id} - Update or clear account alias
        _app.MapPatch("/api/accounts/{id}", async (string id, HttpContext context) =>
        {
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "Cross-site requests forbidden." }, statusCode: StatusCodes.Status403Forbidden);
            }
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

            UpdateAccountAliasRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<UpdateAccountAliasRequest>(cancellationToken: context.RequestAborted);
            }
            catch (Exception)
            {
                return Results.Json(new { error = "Invalid JSON payload" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (request == null)
            {
                return Results.Json(new { error = "Invalid JSON payload" }, statusCode: StatusCodes.Status400BadRequest);
            }

            try { AccountTextValidator.Validate(alias: request.Alias); }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            string? normalizedAlias = string.IsNullOrWhiteSpace(request.Alias) ? "" : request.Alias.Trim();
            var updated = await accountStore.UpdateAccountAsync(id, new UpdateAccountInput(Alias: normalizedAlias));
            if (updated == null)
            {
                return Results.Json(new { error = "Account not found" }, statusCode: StatusCodes.Status404NotFound);
            }

            bool hasVaulted = await HasAvailableSessionAsync(sessionVault, updated.Id);
            var activeId = await accountStore.GetActiveAccountIdAsync();
            var enriched = updated with
            {
                HasVaultedSession = hasVaulted,
                IsActive = updated.Id == activeId
            };

            return Results.Ok(new { success = true, account = enriched });
        });

        // DELETE /api/accounts/{id} - Remove account metadata and vaulted session
        _app.MapDelete("/api/accounts/{id}", async (string id, HttpContext context) =>
        {
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Unauthorized origin." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "Cross-site requests forbidden." }, statusCode: StatusCodes.Status403Forbidden);
            }
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

            if (sessionVault is not AG2Router.AG2.Vault.SessionVault concreteVault)
                return Results.Json(new { error = "Session vault is not configured." }, statusCode: StatusCodes.Status501NotImplemented);
            try
            {
                var removal = await new AccountRemovalService(accountStore, concreteVault)
                    .RemoveAsync(id, context.RequestAborted);
                return Results.Ok(new { success = true, removedId = id, vaultRecordDeleted = removal.VaultRecordDeleted });
            }
            catch (AccountRemovalConflictException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (KeyNotFoundException)
            {
                return Results.Json(new { error = "Account not found." }, statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception)
            {
                return Results.Json(new { error = "Account removal failed; existing state requires verification." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
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

        // A separate same-origin browser/native intent request keeps the token off status surfaces.
        _app.MapPost("/api/switching/intent", (HttpContext context) =>
        {
            if (!string.Equals(context.Request.Headers["X-AG2-Intent-Request"], "1", StringComparison.Ordinal))
                return Results.Json(new { error = "Switch intent request header is required." },
                    statusCode: StatusCodes.Status403Forbidden);
            context.Response.Headers["X-AG2-Switch-Token"] = switchIntentToken;
            return Results.Ok(new { ready = true });
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

            ManualSwitchToken? manualSwitchToken = autoRouter?.NotifyManualSwitchStarted(id);
            NativeSwitchResult result;
            try
            {
                result = await switchCoordinator.SwitchAsync(id, context.RequestAborted);
            }
            catch (Exception)
            {
                var failureResult = new NativeSwitchResult(
                    null, false, SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
                    id, null, null, null, "Switch outcome could not be proven; manual recovery is required.", [],
                    DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"),
                    ManualRecoveryRequired: true);

                if (manualSwitchToken.HasValue)
                    await autoRouter!.NotifyManualSwitchCompletedAsync(manualSwitchToken.Value, failureResult);
                return Results.Json(new { error = "Switch request failed; inspect switching status before retrying." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            if (manualSwitchToken.HasValue)
            {
                await autoRouter!.NotifyManualSwitchCompletedAsync(manualSwitchToken.Value, result);
            }
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

        // POST /api/switching/resolve-quarantine - Proof-based operator journal resolution
        _app.MapPost("/api/switching/resolve-quarantine", async (HttpContext context) =>
        {
            if (switchCoordinator == null)
            {
                return Results.Json(new { error = "Native account switching is not configured." },
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            if (!IsAllowedMutationOrigin(context))
            {
                return Results.Json(new { error = "Resolution origin is not authorized." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            string suppliedToken = context.Request.Headers["X-AG2-Switch-Token"].ToString();
            if (!FixedTimeEquals(suppliedToken, switchIntentToken))
            {
                return Results.Json(new { error = "Switch authorization is required." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            ResolveQuarantineRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ResolveQuarantineRequest>(
                    cancellationToken: context.RequestAborted);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "A valid resolution confirmation request is required." },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (request?.Confirm != true)
            {
                return Results.Json(new { error = "Confirmation is required to resolve switch quarantine." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            JournalResolutionResult result;
            try
            {
                result = await switchCoordinator.ResolveQuarantinedJournalAsync(context.RequestAborted);
            }
            catch (Exception)
            {
                return Results.Json(new { error = "An unexpected error occurred during quarantine resolution." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            string safeMessage = (result.Status, result.ReasonCode) switch
            {
                (JournalResolutionStatus.NoJournal, _) => "No switch journal present.",
                (JournalResolutionStatus.CleanCleanupCompleted, "CLEAN_RECORDED_REMOVED") => "Switch journal cleaned up successfully.",
                (JournalResolutionStatus.CleanCleanupCompleted, "CLEAN_COMMITTED_PRECOMMIT_REMOVED") => "Switch journal cleaned up successfully; target metadata already active.",
                (JournalResolutionStatus.CleanCleanupCompleted, _) => "Switch journal cleaned up successfully.",
                (JournalResolutionStatus.ResolvedRestartRequired, _) => "Switch journal successfully resolved and removed. Application restart is required before normal routing resumes.",
                (JournalResolutionStatus.NotResolvable, "CORRUPT_JOURNAL") => "The switch journal is corrupted and cannot be resolved automatically.",
                (JournalResolutionStatus.NotResolvable, "UNSUPPORTED_VERSION") => "The switch journal uses an unsupported schema version and cannot be resolved automatically.",
                (JournalResolutionStatus.NotResolvable, _) => "The switch journal cannot be resolved automatically.",
                (JournalResolutionStatus.ProofFailed, "LIVE_IDENTITY_UNAVAILABLE") => "Live Antigravity identity is unavailable; start Antigravity and ensure an account is active before resolving.",
                (JournalResolutionStatus.ProofFailed, "LIVE_IDENTITY_MISMATCH") => "Live Antigravity identity does not match the active account metadata.",
                (JournalResolutionStatus.ProofFailed, "NO_ACTIVE_ACCOUNT") => "No active account is set in metadata.",
                (JournalResolutionStatus.ProofFailed, "ACTIVE_ACCOUNT_NOT_FOUND") => "The active account was not found in metadata.",
                (JournalResolutionStatus.ProofFailed, "ACCOUNT_NOT_ENROLLED") => "The active account is not validly enrolled or has no vaulted session.",
                (JournalResolutionStatus.ProofFailed, "CREDENTIAL_MISSING") => "Windows Credential Manager entry is missing or empty.",
                (JournalResolutionStatus.ProofFailed, "VAULT_SESSION_MISSING") => "Vaulted session for the active account is missing or empty.",
                (JournalResolutionStatus.ProofFailed, "CREDENTIAL_MISMATCH") => "Windows Credential Manager payload does not match the vaulted session.",
                (JournalResolutionStatus.ProofFailed, "CONCURRENT_MUTATION") => "State changed concurrently during resolution proof.",
                (JournalResolutionStatus.ProofFailed, _) => "Account state coherence could not be verified.",
                (JournalResolutionStatus.PersistenceFailure, "LOCK_TIMEOUT") => "Lock acquisition timed out during journal resolution.",
                (JournalResolutionStatus.PersistenceFailure, "LEASE_ACQUISITION_FAILED") => "Cross-process lease acquisition failed.",
                (JournalResolutionStatus.PersistenceFailure, "IO_ERROR") => "An I/O error occurred while accessing the switch journal.",
                (JournalResolutionStatus.PersistenceFailure, "UNSUPPORTED_PLATFORM") => "Conditional switch journal deletion is not supported on this platform.",
                (JournalResolutionStatus.PersistenceFailure, "DELETE_FAILED") => "Failed to delete the switch journal file.",
                (JournalResolutionStatus.PersistenceFailure, _) => "A persistence failure occurred during journal resolution.",
                _ => "An unexpected resolution state occurred."
            };

            string? safeReasonCode = (result.Status, result.ReasonCode) switch
            {
                (JournalResolutionStatus.NoJournal, null) => null,
                (JournalResolutionStatus.NoJournal, "NO_JOURNAL") => "NO_JOURNAL",
                (JournalResolutionStatus.ResolvedRestartRequired, null) => null,
                (JournalResolutionStatus.CleanCleanupCompleted, "CLEAN_RECORDED_REMOVED") => "CLEAN_RECORDED_REMOVED",
                (JournalResolutionStatus.CleanCleanupCompleted, "CLEAN_COMMITTED_PRECOMMIT_REMOVED") => "CLEAN_COMMITTED_PRECOMMIT_REMOVED",
                (JournalResolutionStatus.CleanCleanupCompleted, null) => null,
                (JournalResolutionStatus.NotResolvable, "CORRUPT_JOURNAL") => "CORRUPT_JOURNAL",
                (JournalResolutionStatus.NotResolvable, "UNSUPPORTED_VERSION") => "UNSUPPORTED_VERSION",
                (JournalResolutionStatus.ProofFailed, "LIVE_IDENTITY_UNAVAILABLE") => "LIVE_IDENTITY_UNAVAILABLE",
                (JournalResolutionStatus.ProofFailed, "LIVE_IDENTITY_MISMATCH") => "LIVE_IDENTITY_MISMATCH",
                (JournalResolutionStatus.ProofFailed, "NO_ACTIVE_ACCOUNT") => "NO_ACTIVE_ACCOUNT",
                (JournalResolutionStatus.ProofFailed, "ACTIVE_ACCOUNT_NOT_FOUND") => "ACTIVE_ACCOUNT_NOT_FOUND",
                (JournalResolutionStatus.ProofFailed, "ACCOUNT_NOT_ENROLLED") => "ACCOUNT_NOT_ENROLLED",
                (JournalResolutionStatus.ProofFailed, "CREDENTIAL_MISSING") => "CREDENTIAL_MISSING",
                (JournalResolutionStatus.ProofFailed, "VAULT_SESSION_MISSING") => "VAULT_SESSION_MISSING",
                (JournalResolutionStatus.ProofFailed, "CREDENTIAL_MISMATCH") => "CREDENTIAL_MISMATCH",
                (JournalResolutionStatus.ProofFailed, "CONCURRENT_MUTATION") => "CONCURRENT_MUTATION",
                (JournalResolutionStatus.PersistenceFailure, "LOCK_TIMEOUT") => "LOCK_TIMEOUT",
                (JournalResolutionStatus.PersistenceFailure, "LEASE_ACQUISITION_FAILED") => "LEASE_ACQUISITION_FAILED",
                (JournalResolutionStatus.PersistenceFailure, "IO_ERROR") => "IO_ERROR",
                (JournalResolutionStatus.PersistenceFailure, "UNSUPPORTED_PLATFORM") => "UNSUPPORTED_PLATFORM",
                (JournalResolutionStatus.PersistenceFailure, "DELETE_FAILED") => "DELETE_FAILED",
                (_, null) => null,
                _ => "UNKNOWN"
            };

            var safeResponse = new JournalResolutionResult(
                Status: result.Status,
                Message: safeMessage,
                CoherentAccountId: result.CoherentAccountId,
                RestartRequired: result.RestartRequired,
                ReasonCode: safeReasonCode
            );

            int statusCode = safeResponse.Status switch
            {
                JournalResolutionStatus.NoJournal => StatusCodes.Status200OK,
                JournalResolutionStatus.CleanCleanupCompleted => StatusCodes.Status200OK,
                JournalResolutionStatus.ResolvedRestartRequired => StatusCodes.Status200OK,
                JournalResolutionStatus.NotResolvable => StatusCodes.Status409Conflict,
                JournalResolutionStatus.ProofFailed when safeResponse.ReasonCode == "LIVE_IDENTITY_UNAVAILABLE" => StatusCodes.Status503ServiceUnavailable,
                JournalResolutionStatus.ProofFailed => StatusCodes.Status409Conflict,
                JournalResolutionStatus.PersistenceFailure => StatusCodes.Status500InternalServerError,
                _ => StatusCodes.Status500InternalServerError
            };

            return Results.Json(safeResponse, statusCode: statusCode);
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
                RouterConfigValidator.Validate(body);
                var (updated, generation) = autoRouter.UpdateConfigWithGeneration(body);
                if (body.PollingIntervalMs > 0)
                {
                    var interval = TimeSpan.FromMilliseconds(body.PollingIntervalMs);
                    if (onPollingIntervalChangedAsync != null)
                    {
                        await onPollingIntervalChangedAsync(interval, generation);
                    }
                    else if (onPollingIntervalChangedWithGeneration != null)
                    {
                        onPollingIntervalChangedWithGeneration(interval, generation);
                    }
                    else
                    {
                        onPollingIntervalChanged?.Invoke(interval);
                    }
                }
                return Results.Ok(new { success = true, config = updated });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = AG2Security.RedactSensitiveText(ex.Message) }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Read-only durable candidate quota evidence, with backend-owned freshness classification.
        _app.MapGet("/api/router/candidate-evidence", async (HttpContext context) =>
        {
            if (autoRouter == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            try
            {
                return Results.Ok(await autoRouter.GetCandidateEvidenceStatusAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
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

    private static bool IsAllowedLoopbackOrigin(string origin, int localPort) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        IsAllowedLoopbackHost(uri.Host) &&
        uri.Port == localPort;

    private static bool IsAllowedMutationOrigin(HttpContext context)
    {
        string origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return IsAllowedLoopbackOrigin(origin, context.Connection.LocalPort);
    }

    private static async Task<bool> HasAvailableSessionAsync(ISessionVault? vault, string accountId)
    {
        if (vault == null) return false;
        try
        {
            var session = await vault.GetSessionAsync(accountId).ConfigureAwait(false);
            if (session == null) return false;
            try { return session.Length > 0; }
            finally { CryptographicOperations.ZeroMemory(session); }
        }
        catch
        {
            return false;
        }
    }

    private static SystemStatusDto SanitizeStatusQuota(SystemStatusDto status)
    {
        if (status.Telemetry is not { } telemetry) return status;
        var quota = telemetry.Quota;
        if (quota != null)
        {
            quota = quota with
            {
                Models = quota.Models.Select(model => model with
                {
                    RemainingFraction = FiniteOrNull(model.RemainingFraction)
                }).ToArray(),
                CanonicalModels = quota.CanonicalModels?.Select(model => model with
                {
                    RemainingFraction = FiniteOrNull(model.RemainingFraction)
                }).ToArray()
            };
        }
        return status with { Telemetry = telemetry with
        {
            Quota = quota,
            TotalAvailableQuotaPercent = FiniteOrNull(telemetry.TotalAvailableQuotaPercent)
        } };
    }

    private static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

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
