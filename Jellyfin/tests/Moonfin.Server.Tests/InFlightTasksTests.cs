using Moonfin.Server.Helpers;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Custom rows resolve through <see cref="InFlightTasks{T}"/>, so a slow list keeps resolving
/// after the client that asked for it gives up, and a retry joins it instead of starting over.
/// </summary>
public sealed class InFlightTasksTests
{
    [Fact]
    public async Task ASecondCallerJoinsTheRunningTask()
    {
        var tasks = new InFlightTasks<int>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        Task<int> Start()
        {
            starts++;
            return gate.Task;
        }

        var first = tasks.GetOrStart("row", Start);
        var second = tasks.GetOrStart("row", Start);
        gate.SetResult(7);

        Assert.Same(first, second);
        Assert.Equal(7, await second);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task DifferentKeysRunSeparately()
    {
        var tasks = new InFlightTasks<int>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = tasks.GetOrStart("row:userA", () => gate.Task);
        var second = tasks.GetOrStart("row:userB", () => Task.FromResult(2));

        Assert.NotSame(first, second);
        Assert.Equal(2, await second);
        gate.SetResult(1);
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task AFinishedKeyStartsAgain()
    {
        var tasks = new InFlightTasks<int>();
        var starts = 0;
        Task<int> Start() => Task.FromResult(++starts);

        Assert.Equal(1, await tasks.GetOrStart("row", Start));
        Assert.Equal(2, await tasks.GetOrStart("row", Start));
    }

    [Fact]
    public async Task AFailedRunFreesItsKey()
    {
        var tasks = new InFlightTasks<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tasks.GetOrStart("row", () => Task.FromException<int>(new InvalidOperationException())));

        Assert.Equal(3, await tasks.GetOrStart("row", () => Task.FromResult(3)));
    }

    [Fact]
    public async Task AStartThatThrowsFailsTheTaskAndFreesItsKey()
    {
        var tasks = new InFlightTasks<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tasks.GetOrStart("row", () => throw new InvalidOperationException()));

        Assert.Equal(3, await tasks.GetOrStart("row", () => Task.FromResult(3)));
    }

    [Fact]
    public async Task ACallerThatStopsWaitingLeavesTheRunGoing()
    {
        var tasks = new InFlightTasks<int>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new CancellationTokenSource();

        var run = tasks.GetOrStart("row", () => gate.Task);
        var wait = run.WaitAsync(client.Token);
        client.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(run.IsCompleted);

        gate.SetResult(5);
        Assert.Equal(5, await run);
    }
}
