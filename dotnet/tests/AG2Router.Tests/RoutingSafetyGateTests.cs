using AG2Router.AG2.Routing;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class RoutingSafetyGateTests
{
    [Fact]
    public void InitialState_IsIdleWithNoTarget()
    {
        var gate = new RoutingSafetyGate();
        Assert.Equal(RoutingSafetyGateState.Idle, gate.State);
        Assert.Null(gate.TargetAccountId);
        Assert.Equal("Initialized", gate.LastTransitionReason);
    }

    [Fact]
    public void ValidTransitions_HappyPathLifecycle()
    {
        var gate = new RoutingSafetyGate();

        gate.Transition(RoutingSafetyGateState.LowQuotaDetected, "Quota low", "acc_target");
        Assert.Equal(RoutingSafetyGateState.LowQuotaDetected, gate.State);
        Assert.Equal("acc_target", gate.TargetAccountId);

        gate.Transition(RoutingSafetyGateState.SwitchPending, "Queued");
        Assert.Equal(RoutingSafetyGateState.SwitchPending, gate.State);

        gate.Transition(RoutingSafetyGateState.WaitingForIdle, "Activity busy");
        Assert.Equal(RoutingSafetyGateState.WaitingForIdle, gate.State);

        gate.Transition(RoutingSafetyGateState.SwitchInProgress, "Now idle, executing");
        Assert.Equal(RoutingSafetyGateState.SwitchInProgress, gate.State);

        gate.Transition(RoutingSafetyGateState.Verifying, "Verifying target");
        Assert.Equal(RoutingSafetyGateState.Verifying, gate.State);

        gate.Transition(RoutingSafetyGateState.SwitchCompleted, "Switch complete");
        Assert.Equal(RoutingSafetyGateState.SwitchCompleted, gate.State);

        gate.Transition(RoutingSafetyGateState.Cooldown, "Cooling down");
        Assert.Equal(RoutingSafetyGateState.Cooldown, gate.State);

        gate.Transition(RoutingSafetyGateState.Idle, "Ready");
        Assert.Equal(RoutingSafetyGateState.Idle, gate.State);
        Assert.Null(gate.TargetAccountId);
    }

    [Fact]
    public void ValidTransitions_RollbackAndManualRecoveryPath()
    {
        var gate = new RoutingSafetyGate();

        gate.Transition(RoutingSafetyGateState.SwitchPending, "Direct switch pending", "acc_target");
        gate.Transition(RoutingSafetyGateState.SwitchInProgress, "Executing");
        gate.Transition(RoutingSafetyGateState.SwitchFailed, "Failed switch");
        Assert.Equal(RoutingSafetyGateState.SwitchFailed, gate.State);

        gate.Transition(RoutingSafetyGateState.ManualRecoveryRequired, "Rollback failed");
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, gate.State);

        // Reset back to IDLE
        gate.Reset("Operator cleared manual recovery");
        Assert.Equal(RoutingSafetyGateState.Idle, gate.State);
        Assert.Null(gate.TargetAccountId);
    }

    [Theory]
    [InlineData(RoutingSafetyGateState.Idle, RoutingSafetyGateState.SwitchInProgress)]
    [InlineData(RoutingSafetyGateState.Idle, RoutingSafetyGateState.Verifying)]
    [InlineData(RoutingSafetyGateState.Idle, RoutingSafetyGateState.SwitchCompleted)]
    [InlineData(RoutingSafetyGateState.WaitingForIdle, RoutingSafetyGateState.SwitchCompleted)]
    [InlineData(RoutingSafetyGateState.ManualRecoveryRequired, RoutingSafetyGateState.SwitchPending)]
    public void InvalidTransition_ThrowsInvalidStateTransitionException(string from, string to)
    {
        var gate = new RoutingSafetyGate();
        if (from != RoutingSafetyGateState.Idle)
        {
            // Setup gate to 'from' state
            if (from == RoutingSafetyGateState.WaitingForIdle)
            {
                gate.Transition(RoutingSafetyGateState.SwitchPending, "Setup");
                gate.Transition(RoutingSafetyGateState.WaitingForIdle, "Setup");
            }
            else if (from == RoutingSafetyGateState.ManualRecoveryRequired)
            {
                gate.Transition(RoutingSafetyGateState.SwitchPending, "Setup");
                gate.Transition(RoutingSafetyGateState.SwitchInProgress, "Setup");
                gate.Transition(RoutingSafetyGateState.SwitchFailed, "Setup");
                gate.Transition(RoutingSafetyGateState.ManualRecoveryRequired, "Setup");
            }
        }

        Assert.Equal(from, gate.State);
        Assert.Throws<InvalidStateTransitionException>(() => gate.Transition(to, "Test illegal transition"));
    }

    [Fact]
    public void AssessActivity_WhenNull_FailsClosed()
    {
        var gate = new RoutingSafetyGate();
        var assessment = gate.AssessActivity(null);

        Assert.False(assessment.CanProceed);
        Assert.Contains("unavailable", assessment.Reason);
        Assert.Equal(0, assessment.ActiveTrajectoriesCount);
    }

    [Fact]
    public void AssessActivity_WhenBusyOrActiveTrajectories_BlocksSwitching()
    {
        var gate = new RoutingSafetyGate();

        var busyWithZero = new ActivityStatusDto("BUSY", 0, 0, DateTime.UtcNow.ToString("O"));
        var assess1 = gate.AssessActivity(busyWithZero);
        Assert.False(assess1.CanProceed);
        Assert.Contains("executing tasks", assess1.Reason);

        var idleWithTrajectories = new ActivityStatusDto("IDLE", TotalTrajectories: 5, RunningTrajectories: 2, DateTime.UtcNow.ToString("O"));
        var assess2 = gate.AssessActivity(idleWithTrajectories);
        Assert.False(assess2.CanProceed);
        Assert.Equal(2, assess2.ActiveTrajectoriesCount);
        Assert.Contains("2 running trajectories", assess2.Reason);
    }

    [Fact]
    public void AssessActivity_WhenIdleAndZeroTrajectories_AllowsSwitching()
    {
        var gate = new RoutingSafetyGate();
        var idleClean = new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O"));

        var assessment = gate.AssessActivity(idleClean);
        Assert.True(assessment.CanProceed);
        Assert.Equal(0, assessment.ActiveTrajectoriesCount);
        Assert.Contains("Safe for switching", assessment.Reason);
    }

    [Fact]
    public void Reset_RestoresIdleAndClearsTarget()
    {
        var gate = new RoutingSafetyGate();
        gate.Transition(RoutingSafetyGateState.SwitchPending, "Queued", "acc_xyz");
        Assert.Equal("acc_xyz", gate.TargetAccountId);

        gate.Reset("Cancelled by operator");
        Assert.Equal(RoutingSafetyGateState.Idle, gate.State);
        Assert.Null(gate.TargetAccountId);
        Assert.Equal("Cancelled by operator", gate.LastTransitionReason);
    }
}
