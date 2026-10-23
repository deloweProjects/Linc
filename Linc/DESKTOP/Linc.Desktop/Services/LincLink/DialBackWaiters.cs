using System.Collections.Concurrent;

namespace Linc.Desktop.Services;

/// <summary>
/// Channel dial-backs the PC is waiting for (v9 <c>channel.open</c>: the PC asks, the phone dials
/// back with a <c>{channel, sessionToken}</c> header), first come first served per key.
///
/// Every waiter ends by itself: when its caller gives up, after <see cref="LinkBrain.DialBackWaiterMax"/>
/// at the most, or when its session ends (<see cref="DropSession"/>). A field test of build A
/// (2026-10-23) had two waiters left behind by a request that failed before its timer ran; from
/// then on every control connection the phone made first waited out the header read, and the
/// phone, which gives the PC 5 s to speak, hung up every time.
/// </summary>
public sealed class DialBackWaiters<T> where T : class
{
    private readonly ConcurrentDictionary<(int Channel, string Token), ConcurrentQueue<TaskCompletionSource<T>>> _queues = new();
    private int _pending;

    /// <summary>Waiters still waiting (not the number of keys ever used).</summary>
    public int Pending => Volatile.Read(ref _pending);

    public Task<T> Add(int channel, string token, CancellationToken ct, TimeSpan? maxWait = null)
    {
        var waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queues.GetOrAdd((channel, token), _ => new()).Enqueue(waiter);
        Interlocked.Increment(ref _pending);
        var limit = new CancellationTokenSource(maxWait ?? LinkBrain.DialBackWaiterMax);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        linked.Token.Register(() => Cancel(waiter));
        _ = waiter.Task.ContinueWith(_ => { linked.Dispose(); limit.Dispose(); }, TaskScheduler.Default);
        return waiter.Task;
    }

    /// <summary>The oldest waiter for this dial-back that still waits, handed <paramref name="value"/>; false if none.</summary>
    public bool Complete(int channel, string token, T value)
    {
        if (!_queues.TryGetValue((channel, token), out var queue)) return false;
        while (queue.TryDequeue(out var waiter))
        {
            if (waiter.TrySetResult(value))
            {
                Interlocked.Decrement(ref _pending);
                Prune(channel, token, queue);
                return true;
            }
        }
        Prune(channel, token, queue);
        return false;
    }

    /// <summary>The link this session token belonged to ended: nothing will dial back for it.</summary>
    public void DropSession(string token)
    {
        foreach (var key in _queues.Keys.Where(k => k.Token == token).ToList())
        {
            if (_queues.TryRemove(key, out var queue))
            {
                while (queue.TryDequeue(out var waiter)) Cancel(waiter);
            }
        }
    }

    private void Cancel(TaskCompletionSource<T> waiter)
    {
        if (waiter.TrySetCanceled()) Interlocked.Decrement(ref _pending);
    }

    private void Prune(int channel, string token, ConcurrentQueue<TaskCompletionSource<T>> queue)
    {
        if (queue.IsEmpty) _queues.TryRemove(new KeyValuePair<(int, string), ConcurrentQueue<TaskCompletionSource<T>>>((channel, token), queue));
    }
}
