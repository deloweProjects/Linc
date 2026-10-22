using System.Diagnostics;

namespace Linc.Desktop.Services;

/// <summary>How a race ended: the winner (or none), and why each loser lost, for one log line.</summary>
public sealed record RaceResult<T>(T? Winner, int WinnerIndex, IReadOnlyList<(int Index, string Error)> Failures, TimeSpan Elapsed)
    where T : class;

/// <summary>
/// Happy-eyeballs for Linc routes (RFC 8305's idea, linclink.md §5): start the best candidate,
/// then the next one every <c>stagger</c> while nothing has finished, or at once when one fails;
/// the first to succeed wins and every other attempt is cancelled (a late success is handed to
/// <c>discard</c> so its socket is closed, not leaked). One dead remembered address therefore costs
/// 250 ms instead of a whole connect timeout, and a fast route is never held up by a slow one.
/// Pure: the attempts do the I/O.
/// </summary>
public static class RouteRace
{
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    public static async Task<RaceResult<T>> FirstAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<T?>>> attempts,
        TimeSpan stagger,
        Action<T> discard,
        CancellationToken ct) where T : class
    {
        var clock = Stopwatch.StartNew();
        var failures = new List<(int, string)>();
        if (attempts.Count == 0)
        {
            return new RaceResult<T>(null, -1, failures, clock.Elapsed);
        }
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<(Task<T?> Task, int Index)>();
        var next = 0;
        Task? timer = null;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            if (next < attempts.Count && (timer is null || timer.IsCompleted || running.Count == 0))
            {
                var index = next++;
                running.Add((Run(attempts[index], race.Token), index));
                timer = next < attempts.Count ? Task.Delay(stagger, race.Token) : null;
            }
            if (running.Count == 0)
            {
                break; // every candidate failed
            }
            var waits = new List<Task>(running.Count + 1);
            waits.AddRange(running.Select(r => (Task)r.Task));
            if (timer is not null)
            {
                waits.Add(timer);
            }
            var done = await Task.WhenAny(waits);
            if (ReferenceEquals(done, timer))
            {
                continue; // the stagger passed: start the next candidate beside the running ones
            }
            var finished = running.First(r => ReferenceEquals(r.Task, done));
            running.Remove(finished);
            if (finished.Task.Status == TaskStatus.RanToCompletion && finished.Task.Result is { } winner)
            {
                race.Cancel();
                foreach (var (late, _) in running)
                {
                    _ = late.ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result is { } extra)
                        {
                            discard(extra);
                        }
                    }, TaskScheduler.Default);
                }
                return new RaceResult<T>(winner, finished.Index, failures, clock.Elapsed);
            }
            failures.Add((finished.Index, Describe(finished.Task)));
            timer = null; // a failure starts the next candidate now, not after the stagger
        }
        race.Cancel();
        foreach (var (late, _) in running)
        {
            _ = late.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is { } extra)
                {
                    discard(extra);
                }
            }, TaskScheduler.Default);
        }
        return new RaceResult<T>(null, -1, failures, clock.Elapsed);
    }

    private static async Task<T?> Run<T>(Func<CancellationToken, Task<T?>> attempt, CancellationToken ct) where T : class =>
        await attempt(ct).ConfigureAwait(false); // a synchronous throw becomes a failed attempt, not a failed race

    private static string Describe(Task task) => task.Status switch
    {
        TaskStatus.Canceled => "timed out",
        TaskStatus.Faulted => task.Exception?.GetBaseException().Message ?? "failed",
        _ => "no answer",
    };
}
