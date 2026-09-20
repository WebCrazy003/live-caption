using System.Collections.Concurrent;

namespace LocalCaption.Core.Captions;

/// <summary>
/// A single-threaded message pump: one thread drains a queue of callbacks, so work posted
/// here runs strictly one at a time and in order.
/// </summary>
/// <remarks>
/// <para>This is the Core-layer stand-in for Swift's <c>@MainActor</c>. It matters that it
/// is a <see cref="SynchronizationContext"/> and not a lock: at every <c>await</c> the
/// continuation is posted back here, which frees the thread to run other queued work in the
/// meantime. That reentrancy is exactly how a Swift actor behaves, and
/// <see cref="CaptionPipeline"/> depends on it — an interim result must be able to publish
/// while a slow final decode is still in flight.</para>
/// <para>In the WPF app this is replaced by the window's own
/// <c>DispatcherSynchronizationContext</c> (SPEC-WINDOWS.md §6.2), so pipeline state is
/// affine to the UI thread and no marshalling is needed to touch the view models. Core
/// keeps its own implementation so the logic layer stays free of WPF and testable on the
/// Mac.</para>
/// </remarks>
public sealed class SerialDispatcher : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SerialDispatcher(string name = "caption-pipeline")
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>True when the caller is already on the pump thread.</summary>
    public bool IsCurrent => Thread.CurrentThread == _thread;

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception e)
            {
                // A callback that throws must not kill the pump — that would strand the
                // session with no captions and no way to stop cleanly.
                UnhandledException?.Invoke(e);
            }
        }
    }

    /// <summary>Raised when queued work throws. Without a handler the exception is dropped.</summary>
    public event Action<Exception>? UnhandledException;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_queue.IsAddingCompleted) return;
        try { _queue.Add((d, state)); } catch (InvalidOperationException) { /* disposed mid-post */ }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsCurrent) { d(state); return; }
        InvokeAsync(() => d(state)).GetAwaiter().GetResult();
    }

    /// <summary>Run <paramref name="action"/> on the pump thread and await its completion.</summary>
    public Task InvokeAsync(Action action)
    {
        if (IsCurrent)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception e) { return Task.FromException(e); }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
        }, null);
        return completion.Task;
    }

    /// <summary>Run <paramref name="func"/> on the pump thread and await its result.</summary>
    public async Task<T> InvokeAsync<T>(Func<T> func)
    {
        T result = default!;
        await InvokeAsync(() => { result = func(); }).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Run an async <paramref name="func"/> that starts on the pump thread, and await the
    /// task it returns. The inner work keeps running on this context across its awaits.
    /// </summary>
    public async Task InvokeAsync(Func<Task> func)
    {
        var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { completion.TrySetResult(func()); }
            catch (Exception e) { completion.TrySetException(e); }
        }, null);
        await (await completion.Task.ConfigureAwait(false)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrent) _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
