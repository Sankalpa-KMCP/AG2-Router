using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppHost = AG2Router.App.App;
using AG2Router.App.Services;
using AG2Router.App.Views;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class DesktopStatusDispatcherTests
{
    [Fact]
    public async Task QueuedCallback_WhenRouterInvalidatedToNull_RendersAuthoritativeNullNotStaleReady()
    {
        // F2 Regression:
        // StatusUpdated delivers an earlier READY snapshot.
        // A dispatcher callback is queued to execute later.
        // In the meantime, the authoritative router invalidates PoolStatus to null.
        // When the older queued callback executes, it MUST resolve CurrentStatus at consumption time
        // and render null, NOT revert to stale READY.

        var readyPool = new CandidatePoolStatusDto(true, CandidatePoolReasonCodes.Ready, "Candidate ready", 2, 2, 2);
        var autoRouter = new MockAutoRouter();
        autoRouter.Status = autoRouter.Status with { AutoSwitchEnabled = true, State = "IDLE", PoolStatus = readyPool };

        var adapter = new MockAG2Adapter();
        await using var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: autoRouter);

        SystemStatusDto? renderedStatus = null;
        string? renderedTooltip = null;

        // Create the production dispatcher callback capturing the consumption-time status provider
        var queuedCallback = AppHost.CreateDesktopStatusUpdateCallback(
            () => coordinator.CurrentStatus,
            status => renderedStatus = status,
            tooltip => renderedTooltip = tooltip
        );

        // Verify that before invalidation, coordinator has READY
        Assert.NotNull(coordinator.CurrentStatus.Router.PoolStatus);
        Assert.Equal(CandidatePoolReasonCodes.Ready, coordinator.CurrentStatus.Router.PoolStatus!.ReasonCode);

        // Now, before the queued callback executes on the UI thread, the router invalidates PoolStatus to null
        autoRouter.Status = autoRouter.Status with { PoolStatus = null };

        // Pre-fix defect: A callback capturing the old event payload would apply READY here.
        // Post-fix: The callback executes and resolves CurrentStatus at consumption time.
        queuedCallback();

        // Verification:
        // 1. Rendered status for QuickStatus receives authoritative null
        Assert.NotNull(renderedStatus);
        Assert.Null(renderedStatus!.Router.PoolStatus);

        // 2. Tray tooltip reflects current null PoolStatus (router state only, e.g. "Auto: IDLE"),
        // without stale pool text ("Auto: Pool exhausted" or stale pool reasons)
        Assert.NotNull(renderedTooltip);
        Assert.Contains("Auto: IDLE", renderedTooltip);
        Assert.DoesNotContain("exhausted", renderedTooltip, StringComparison.OrdinalIgnoreCase);

        // 3. QuickStatus auto-switch formatter with null poolStatus produces active without stale message
        var (autoText, autoTip) = QuickStatusWindow.FormatAutoSwitchStatus(renderedStatus.Router.AutoSwitchEnabled, renderedStatus.Router.PoolStatus);
        Assert.Null(autoTip);
    }

    [Fact]
    public async Task QueuedCallback_WhenRouterUpdatedToNewerNonNull_RendersNewerStatusNotStaleReady()
    {
        // F2 Regression for newer non-null state:
        // Callback queued when router had READY.
        // Router state transitions to ALL_EXHAUSTED.
        // Queued callback executes and renders ALL_EXHAUSTED, NOT READY.

        var readyPool = new CandidatePoolStatusDto(true, CandidatePoolReasonCodes.Ready, "Candidate ready", 2, 2, 2);
        var exhaustedPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.AllExhausted, "All exhausted", 2, 0, 0);

        var autoRouter = new MockAutoRouter();
        autoRouter.Status = autoRouter.Status with
        {
            AutoSwitchEnabled = true,
            PoolStatus = readyPool
        };

        var adapter = new MockAG2Adapter();
        await using var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: autoRouter);

        SystemStatusDto? renderedStatus = null;
        string? renderedTooltip = null;

        var queuedCallback = AppHost.CreateDesktopStatusUpdateCallback(
            () => coordinator.CurrentStatus,
            status => renderedStatus = status,
            tooltip => renderedTooltip = tooltip
        );

        // Router transitions to newer non-null exhausted state
        autoRouter.Status = autoRouter.Status with { PoolStatus = exhaustedPool };

        // Execute queued callback
        queuedCallback();

        Assert.NotNull(renderedStatus);
        Assert.NotNull(renderedStatus!.Router.PoolStatus);
        Assert.Equal(CandidatePoolReasonCodes.AllExhausted, renderedStatus.Router.PoolStatus!.ReasonCode);

        Assert.NotNull(renderedTooltip);
        Assert.Contains("Auto: Pool exhausted", renderedTooltip);

        var (autoText, autoTip) = QuickStatusWindow.FormatAutoSwitchStatus(renderedStatus.Router.AutoSwitchEnabled, renderedStatus.Router.PoolStatus);
        Assert.Equal("Pool Exhausted", autoText);
        Assert.Equal("All exhausted", autoTip);
    }

    [Fact]
    public async Task MultipleQueuedCallbacks_WhenExecutedInAnyOrder_AlwaysRenderCurrentAuthoritativeState()
    {
        // Challenge: Event A queued, Event B queued, router updates again, callbacks execute in delayed/unusual order.
        // Proves that no callback applies an obsolete captured PoolStatus over a newer current state.

        var readyPool = new CandidatePoolStatusDto(true, CandidatePoolReasonCodes.Ready, "Candidate ready", 2, 2, 2);
        var cooldownPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.AllInCooldown, "In cooldown", 2, 0, 0);

        var autoRouter = new MockAutoRouter();
        autoRouter.Status = autoRouter.Status with { AutoSwitchEnabled = true, PoolStatus = readyPool };

        var adapter = new MockAG2Adapter();
        await using var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: autoRouter);

        var statusHistory = new List<SystemStatusDto>();
        var tooltipHistory = new List<string>();

        // Callback 1 queued when router is READY
        var callback1 = AppHost.CreateDesktopStatusUpdateCallback(
            () => coordinator.CurrentStatus,
            statusHistory.Add,
            tooltipHistory.Add
        );

        // Router updates to COOLDOWN
        autoRouter.Status = autoRouter.Status with { PoolStatus = cooldownPool };

        // Callback 2 queued when router is COOLDOWN
        var callback2 = AppHost.CreateDesktopStatusUpdateCallback(
            () => coordinator.CurrentStatus,
            statusHistory.Add,
            tooltipHistory.Add
        );

        // Router updates to null (e.g. invalidation from config mutation or epoch reset)
        autoRouter.Status = autoRouter.Status with { PoolStatus = null };

        // Callback 3 queued when router is null
        var callback3 = AppHost.CreateDesktopStatusUpdateCallback(
            () => coordinator.CurrentStatus,
            statusHistory.Add,
            tooltipHistory.Add
        );

        // Callbacks execute in reverse or delayed order: 2, then 1, then 3
        callback2();
        callback1();
        callback3();

        Assert.Equal(3, statusHistory.Count);
        Assert.Equal(3, tooltipHistory.Count);

        // Because all callbacks read CurrentStatus at consumption time, every single one of them
        // rendered the current authoritative null state, NEVER reviving READY or COOLDOWN
        foreach (var status in statusHistory)
        {
            Assert.Null(status.Router.PoolStatus);
        }

        foreach (var tooltip in tooltipHistory)
        {
            Assert.Contains("Auto: IDLE", tooltip);
            Assert.DoesNotContain("cooling", tooltip, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exhausted", tooltip, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void QueuedCallback_WhenCoordinatorIsNullOrShuttingDown_SafelyNoOps()
    {
        bool quickStatusCalled = false;
        bool trayCalled = false;

        var callback = AppHost.CreateDesktopStatusUpdateCallback(
            () => null, // simulates coordinator null during shutdown
            _ => quickStatusCalled = true,
            _ => trayCalled = true
        );

        // Must safely execute without throwing
        callback();

        Assert.False(quickStatusCalled);
        Assert.False(trayCalled);
    }

    [Fact]
    public void UpdateDesktopPresentation_DeliversSameSnapshotToQuickStatusAndTray()
    {
        var status = new SystemStatusDto(
            "ok",
            new Ag2StatusDto(true, "HEALTHY", null, "Connected"),
            new RouterStatusDto("HEALTHY", false, "user@example.com", "user@example.com", null, null, "Active",
                new RouterConfigDto(),
                PoolStatus: null),
            new TelemetryDto(null, null, null, null, null)
        );

        SystemStatusDto? receivedStatus = null;
        string? receivedTooltip = null;

        AppHost.UpdateDesktopPresentation(
            status,
            s => receivedStatus = s,
            t => receivedTooltip = t
        );

        Assert.Same(status, receivedStatus);
        Assert.NotNull(receivedTooltip);
        Assert.Equal("AG2 Router - HEALTHY (Auto: Off)", receivedTooltip);
    }
}
