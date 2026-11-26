using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The work gate is what stops two live runs from doubling the PSE Edge traffic and the model
/// spend, and from racing to write the same (date, horizon). It must admit exactly one caller,
/// refuse the rest immediately, and be reusable once the job finishes.
/// </summary>
public sealed class DeskWorkGateTests
{
    [Fact]
    public void An_idle_gate_admits_the_first_caller_and_the_release_makes_it_available_again()
    {
        var gate = new DeskWorkGate();

        Assert.True(gate.TryEnter());
        gate.Exit();

        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void A_second_caller_is_refused_while_the_first_job_is_running()
    {
        // Refused, not queued: the endpoint answers 409 immediately instead of holding a request
        // open for the several minutes a live session takes.
        var gate = new DeskWorkGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task Exactly_one_of_eight_concurrent_callers_wins()
    {
        var gate = new DeskWorkGate();
        var winners = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;

            if (gate.TryEnter())
            {
                Interlocked.Increment(ref winners);
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(racers);

        Assert.Equal(1, winners);
    }

    [Fact]
    public void A_release_without_a_matching_enter_is_a_programming_error_not_a_silent_no_op()
    {
        // The controller only calls Exit() inside the finally of a successful TryEnter. If a future
        // edit moves it out, this throws instead of quietly inflating the semaphore's count.
        var gate = new DeskWorkGate();

        Assert.Throws<SemaphoreFullException>(() => gate.Exit());
    }
}
