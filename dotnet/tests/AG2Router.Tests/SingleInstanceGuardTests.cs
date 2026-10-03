using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

[CollectionDefinition("SingleInstanceGuard", DisableParallelization = true)]
public class SingleInstanceGuardTestCollection { }

[Collection("SingleInstanceGuard")]
public class SingleInstanceGuardTests
{
    private readonly SingleInstanceIpcNamespace _ipcNamespace = SingleInstanceTestIpc.CreateNamespace();

    private static async Task<NamedPipeClientStream> ConnectAndSendAsync(
        SingleInstanceIpcNamespace ipcNamespace, string command, int connectTimeoutMs = 3000)
    {
        var client = new NamedPipeClientStream(".", ipcNamespace.PipeName, PipeDirection.Out);
        await client.ConnectAsync(connectTimeoutMs);
        var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(command);
        return client;
    }

    private static async Task<NamedPipeClientStream> ConnectStalledClientAsync(SingleInstanceIpcNamespace ipcNamespace)
    {
        // Connects and deliberately sends no command line: the handler must isolate it.
        var client = new NamedPipeClientStream(".", ipcNamespace.PipeName, PipeDirection.Out);
        await client.ConnectAsync(3000);
        return client;
    }

    [Fact]
    public void ProductionNamespace_IsSessionScoped_AndMutexIsInvariant()
    {
        // Inspect constants only: never open the production endpoints in a test.
        int sessionId = Process.GetCurrentProcess().SessionId;

        Assert.Equal(@"Local\AG2Router_Session_Mutex", SingleInstanceIpcNamespace.Production.MutexName);
        Assert.Equal(SingleInstanceIpcNamespace.ForSession(sessionId).PipeName, SingleInstanceIpcNamespace.Production.PipeName);
        Assert.Equal(SingleInstanceIpcNamespace.ForSession(sessionId).MutexName, SingleInstanceIpcNamespace.Production.MutexName);

        // The historical machine-global pipe name must not remain in use.
        Assert.NotEqual("AG2Router_Session_IPC_Pipe", SingleInstanceIpcNamespace.Production.PipeName);

        Assert.NotEqual(SingleInstanceIpcNamespace.Production.MutexName, _ipcNamespace.MutexName);
        Assert.NotEqual(SingleInstanceIpcNamespace.Production.PipeName, _ipcNamespace.PipeName);
    }

    [Fact]
    public void ForSession_IsDeterministicWithinSession_AndDistinctAcrossSessions()
    {
        var first = SingleInstanceIpcNamespace.ForSession(7);
        var second = SingleInstanceIpcNamespace.ForSession(7);
        var otherSession = SingleInstanceIpcNamespace.ForSession(8);

        Assert.Equal(first.MutexName, second.MutexName);
        Assert.Equal(first.PipeName, second.PipeName);

        Assert.NotEqual(first.PipeName, otherSession.PipeName);
        // The mutex is session-scoped by its Local\ prefix, so its name is invariant.
        Assert.Equal(first.MutexName, otherSession.MutexName);
    }

    [Fact]
    public void ForSession_ReconstructsSamePipeName_AcrossIndependentInstances()
    {
        // Two independently constructed namespaces for one discriminator must agree,
        // mirroring two processes of the same session deriving the production name.
        var left = SingleInstanceIpcNamespace.ForSession(42);
        var right = new SingleInstanceIpcNamespace(@"Local\AG2Router_Session_Mutex", $"AG2Router_Session_IPC_Pipe_42");

        Assert.Equal(left.MutexName, right.MutexName);
        Assert.Equal(left.PipeName, right.PipeName);
        Assert.Equal(@"Local\AG2Router_Session_Mutex", left.MutexName);
    }

    [Fact]
    public void ProductionServerPipeOptions_RestrictToCurrentUser()
    {
        // Structural assertion of the listener's security configuration. Behavioral
        // same-user delivery is covered by every command test; genuine cross-user
        // rejection needs a second OS account and remains a documented limitation.
        Assert.True(
            (SingleInstanceGuard.ServerPipeOptions & PipeOptions.CurrentUserOnly) != 0,
            "The production listener must restrict connections to the current user.");
        Assert.True(
            (SingleInstanceGuard.ServerPipeOptions & PipeOptions.Asynchronous) != 0,
            "The production listener must remain asynchronous.");
    }

    [Theory]
    [InlineData("ACTIVATE")]
    [InlineData("CLOSE")]
    [InlineData("EXIT")]
    public async Task Commands_AreDeliveredOnlyWithinTheirExplicitNamespace(string command)
    {
        var otherNamespace = SingleInstanceTestIpc.CreateNamespace();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int otherCommands = 0;
        await using var other = new SingleInstanceGuard(otherNamespace);
        Assert.True(other.TryAcquire(
            () => Interlocked.Increment(ref otherCommands),
            () => Interlocked.Increment(ref otherCommands),
            () => Interlocked.Increment(ref otherCommands)));

        await using var target = new SingleInstanceGuard(_ipcNamespace);
        Assert.True(target.TryAcquire(
            () => received.TrySetResult(true),
            () => received.TrySetResult(true),
            () => received.TrySetResult(true)));

        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, command));
        Assert.True(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref otherCommands));
    }

    [Fact]
    public async Task SecondaryActivation_UsesItsOwnersNamespace()
    {
        var otherNamespace = SingleInstanceTestIpc.CreateNamespace();
        int otherActivations = 0;
        await using var other = new SingleInstanceGuard(otherNamespace);
        Assert.True(other.TryAcquire(() => Interlocked.Increment(ref otherActivations)));

        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        Assert.True(primary.TryAcquire(() => activated.TrySetResult(true)));
        await using var secondary = new SingleInstanceGuard(_ipcNamespace);
        Assert.False(secondary.TryAcquire(() => { }));

        Assert.True(await activated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref otherActivations));
    }

    [Fact]
    public async Task ExitHelpers_ForAbsentNamespace_DoNotSignalOrWaitForAnotherOwner()
    {
        var absentNamespace = SingleInstanceTestIpc.CreateNamespace();
        int exitRequests = 0;
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        Assert.True(primary.TryAcquire(() => { }, onExitRequested: () => Interlocked.Increment(ref exitRequests)));

        Assert.True(SingleInstanceGuard.RequestExitAndWait(absentNamespace, timeoutMs: 100));
        Assert.True(SingleInstanceGuard.WaitForPrimaryExit(absentNamespace, timeoutMs: 100));
        Assert.Equal(0, Volatile.Read(ref exitRequests));
        // The unrelated synthetic primary must still own its namespace.
        await using var secondary = new SingleInstanceGuard(_ipcNamespace);
        Assert.False(secondary.TryAcquire(() => { }));
    }

    [Fact]
    public async Task SingleInstanceGuard_AcquiresPrimary_AndDetectsSecondary()
    {
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        var activationReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool primaryAcquired = primary.TryAcquire(() =>
        {
            activationReceivedTcs.TrySetResult(true);
        });

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Attempt secondary instance acquisition immediately in the same user session (no artificial sleep)
        await using var secondary = new SingleInstanceGuard(_ipcNamespace);
        bool secondaryAcquired = secondary.TryAcquire(() => { });

        Assert.False(secondaryAcquired, "Secondary instance must be rejected");

        // Await deterministic signal propagation with bounded timeout (5 seconds)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completedTask = await Task.WhenAny(activationReceivedTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

        Assert.True(completedTask == activationReceivedTcs.Task, "Primary instance should have received the activation signal within bounded timeout window");
        Assert.True(await activationReceivedTcs.Task, "Signal value must be true");
    }

    [Fact]
    public async Task SingleInstanceGuard_RoutesCommandsDeterministically_OverNamedPipe()
    {
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        var closeReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool primaryAcquired = primary.TryAcquire(
            onActivateRequested: () => { },
            onCloseRequested: () => closeReceivedTcs.TrySetResult(true),
            onExitRequested: () => exitReceivedTcs.TrySetResult(true)
        );

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Send CLOSE command
        bool closeSent = SingleInstanceGuard.SendCommand(_ipcNamespace, "CLOSE");
        Assert.True(closeSent, "CLOSE command should be delivered over named pipe");

        using var closeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var closeResult = await Task.WhenAny(closeReceivedTcs.Task, Task.Delay(Timeout.Infinite, closeTimeoutCts.Token));
        Assert.True(closeResult == closeReceivedTcs.Task, "Primary instance should have received CLOSE command within bounded timeout");

        // Send EXIT command
        bool exitSent = SingleInstanceGuard.SendCommand(_ipcNamespace, "EXIT");
        Assert.True(exitSent, "EXIT command should be delivered over named pipe");

        using var exitTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exitResult = await Task.WhenAny(exitReceivedTcs.Task, Task.Delay(Timeout.Infinite, exitTimeoutCts.Token));
        Assert.True(exitResult == exitReceivedTcs.Task, "Primary instance should have received EXIT command within bounded timeout");
    }

    [Fact]
    public async Task SingleInstanceGuard_RecyclesListener_ForMultipleSequentialCommands()
    {
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        var activateCount = 0;
        var closeCount = 0;
        var exitCount = 0;
        var allDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool primaryAcquired = primary.TryAcquire(
            onActivateRequested: () =>
            {
                Interlocked.Increment(ref activateCount);
                if (Volatile.Read(ref activateCount) == 2 && Volatile.Read(ref closeCount) == 1 && Volatile.Read(ref exitCount) == 1)
                {
                    allDoneTcs.TrySetResult(true);
                }
            },
            onCloseRequested: () =>
            {
                Interlocked.Increment(ref closeCount);
                if (Volatile.Read(ref activateCount) == 2 && Volatile.Read(ref closeCount) == 1 && Volatile.Read(ref exitCount) == 1)
                {
                    allDoneTcs.TrySetResult(true);
                }
            },
            onExitRequested: () =>
            {
                Interlocked.Increment(ref exitCount);
                if (Volatile.Read(ref activateCount) == 2 && Volatile.Read(ref closeCount) == 1 && Volatile.Read(ref exitCount) == 1)
                {
                    allDoneTcs.TrySetResult(true);
                }
            }
        );

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Send sequential commands back-to-back: ACTIVATE, CLOSE, EXIT, ACTIVATE
        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, "ACTIVATE"), "First ACTIVATE should succeed");
        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, "CLOSE"), "CLOSE should succeed");
        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, "EXIT"), "EXIT should succeed");
        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, "ACTIVATE"), "Second ACTIVATE should succeed");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Task.WhenAny(allDoneTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

        Assert.True(result == allDoneTcs.Task, "All 4 sequential commands must be delivered and processed within bounded timeout");
        Assert.Equal(2, Volatile.Read(ref activateCount));
        Assert.Equal(1, Volatile.Read(ref closeCount));
        Assert.Equal(1, Volatile.Read(ref exitCount));
    }

    [Fact]
    public async Task SingleInstanceGuard_DisposesCleanly_AndAllowsNewPrimaryToAcquire()
    {
        var primary1 = new SingleInstanceGuard(_ipcNamespace);
        bool acquired1 = primary1.TryAcquire(() => { });
        Assert.True(acquired1, "Primary 1 must acquire");

        // Secondary should fail while Primary 1 is active
        var secondary1 = new SingleInstanceGuard(_ipcNamespace);
        bool secondaryAcquired = secondary1.TryAcquire(() => { });
        Assert.False(secondaryAcquired, "Secondary must fail while Primary 1 active");
        await secondary1.DisposeAsync();

        // Dispose Primary 1
        await primary1.DisposeAsync();

        // Now a new Primary 2 should be able to acquire cleanly without collisions
        await using var primary2 = new SingleInstanceGuard(_ipcNamespace);
        var activate2Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool acquired2 = primary2.TryAcquire(() => activate2Tcs.TrySetResult(true));
        Assert.True(acquired2, "Primary 2 must successfully acquire after Primary 1 disposed");

        // Verify IPC still works on the new instance
        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, "ACTIVATE"), "ACTIVATE on Primary 2 should succeed");
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Task.WhenAny(activate2Tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
        Assert.True(result == activate2Tcs.Task, "Primary 2 should receive activation signal");
    }

    [Fact]
    public async Task StalledClient_CannotBlockSubsequentCommands()
    {
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        var firstActivationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActivationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activations = 0;
        Assert.True(primary.TryAcquire(() =>
        {
            int count = Interlocked.Increment(ref activations);
            if (count == 1) firstActivationTcs.TrySetResult(true);
            if (count == 2) secondActivationTcs.TrySetResult(true);
        }));

        // Client A connects and stalls without sending any command line.
        await using var stalledClient = await ConnectStalledClientAsync(_ipcNamespace);

        // Client B must be serviced while A remains stalled.
        await using var clientB = await ConnectAndSendAsync(_ipcNamespace, "ACTIVATE");
        Assert.True(await firstActivationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "A command behind a stalled client must still be delivered within a bounded interval.");

        // Client C proves the listener remains usable after A and B.
        await using var clientC = await ConnectAndSendAsync(_ipcNamespace, "ACTIVATE");
        Assert.True(await secondActivationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "The listener must remain usable after servicing a command behind a stalled client.");
        Assert.Equal(2, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task MalformedCommand_DoesNotTerminateListener()
    {
        await using var primary = new SingleInstanceGuard(_ipcNamespace);
        var activatedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(primary.TryAcquire(() => activatedTcs.TrySetResult(true)));

        await using var malformed = await ConnectAndSendAsync(_ipcNamespace, "NOT-A-COMMAND");
        await using var followUp = await ConnectAndSendAsync(_ipcNamespace, "ACTIVATE");

        Assert.True(await activatedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "The listener must keep serving valid commands after a malformed one.");
    }

    [Fact]
    public async Task Dispose_WithStalledClientConnected_CompletesBoundedAndReleasesNamespace()
    {
        var guard = new SingleInstanceGuard(_ipcNamespace);
        Assert.True(guard.TryAcquire(() => { }));

        await using var stalledClient = await ConnectStalledClientAsync(_ipcNamespace);

        // Shutdown must cancel the stalled read, drain the per-connection handler,
        // and release the namespace without deadlock or unobserved exceptions.
        await guard.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await using var successor = new SingleInstanceGuard(_ipcNamespace);
        var activatedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(successor.TryAcquire(() => activatedTcs.TrySetResult(true)));
        Assert.True(SingleInstanceGuard.SendCommand(_ipcNamespace, "ACTIVATE"),
            "A new primary must own the pipe after the previous guard shut down with a stalled client.");
        Assert.True(await activatedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
