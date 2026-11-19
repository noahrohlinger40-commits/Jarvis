// ModelWorkScheduler.cs: the waiting line in front of the language model. Local models can only handle a few
// requests at once, so ModelGatewayFactory wraps the real gateway in ScheduledModelGateway (bottom of this file).
// Each model call must first get a "lease" from ModelWorkScheduler; when all slots are busy, callers wait here.
// User-facing ("foreground") calls are served before background ones such as off-turn thinking.
namespace Jarvis.Core;

/// <summary>Flows across awaits and tool calls so all shared gateway users keep workflow priority.</summary>
// Marks the current chain of async work as "background". AsyncLocal means the flag follows the work across
// awaits and into the tool calls it starts, so every model call made inside is queued as background.
public static class ModelWorkContext
{
    private static readonly AsyncLocal<bool> Background = new();
    public static bool IsBackground => Background.Value;

    // Turns the background flag on until the returned object is disposed, then restores the previous value.
    public static IDisposable EnterBackground()
    {
        var previous = Background.Value;
        Background.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Background.Value = previous;
        }
    }
}

// A point-in-time count of running and waiting model requests, used for the status text.
public sealed record WorkQueueSnapshot(int Active, int ForegroundWaiting, int BackgroundWaiting, int Capacity);

/// <summary>A bounded async limiter with FIFO ordering within each priority and bounded starvation.</summary>
public sealed class ModelWorkScheduler
{
    // Lock protecting every field below; all queue changes happen inside lock (_gate).
    private readonly object _gate = new();
    // Callers waiting for a slot, oldest first, one list per priority.
    private readonly LinkedList<Waiter> _foreground = new();
    private readonly LinkedList<Waiter> _background = new();
    private readonly int _capacity;
    private readonly int _maxWaiting;
    // How many requests currently hold a slot.
    private int _active;
    // How many foreground requests were let through in a row while background work was waiting (see Release).
    private int _foregroundStreak;

    public ModelWorkScheduler(int capacity = 1, int maxWaiting = 64)
    {
        // Capacity is limited to 1-4 slots; maxWaiting is how many callers may queue before new ones are rejected.
        if (capacity is < 1 or > 4 || maxWaiting < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _maxWaiting = maxWaiting;
    }

    public WorkQueueSnapshot Snapshot
    {
        get { lock (_gate) return new(_active, _foreground.Count, _background.Count, _capacity); }
    }

    // Waits for a free slot and returns a lease; disposing the lease gives the slot back. Cancelling while
    // waiting removes the caller from the queue.
    public async Task<IDisposable> AcquireAsync(bool background, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            // Fast path: a slot is free and nobody is waiting, so take it immediately.
            if (_active < _capacity && _foreground.Count + _background.Count == 0)
            {
                _active++;
                return new Lease(this);
            }
            // The line is too long: refuse instead of letting it grow without limit.
            if (_foreground.Count + _background.Count >= _maxWaiting)
                throw new InvalidOperationException("Jarvis's work queue is full. Let existing work finish before adding more.");
            waiter = new Waiter(this, background, cancellationToken);
            waiter.Node = (background ? _background : _foreground).AddLast(waiter);
        }

        // If the caller cancels while queued, pull its entry out of the line.
        using var registration = cancellationToken.UnsafeRegister(
            static state => { var item = (Waiter)state!; item.Owner.Cancel(item); }, waiter);
        return await waiter.Source.Task.ConfigureAwait(false);
    }

    // Removes a queued caller and cancels its wait. Does nothing if it has already been given a slot.
    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return; // Already granted; the caller releases its lease normally.
            (waiter.Background ? _background : _foreground).Remove(waiter.Node);
            waiter.Node = null;
            waiter.Source.TrySetCanceled(waiter.Token);
        }
    }

    // Frees one slot and hands slots to waiting callers. Foreground goes first, but after 4 foreground grants in a
    // row a waiting background request gets a turn, so background work is never starved forever.
    private void Release()
    {
        lock (_gate)
        {
            _active--;
            while (_active < _capacity && _foreground.Count + _background.Count > 0)
            {
                var takeBackground = _background.Count > 0 && (_foreground.Count == 0 || _foregroundStreak >= 4);
                var queue = takeBackground ? _background : _foreground;
                var waiter = queue.First!.Value;
                queue.RemoveFirst();
                waiter.Node = null;
                // Count only while background work waits; earlier foreground grants must not pre-spend its turn.
                _foregroundStreak = takeBackground || _background.Count == 0 ? 0 : Math.Min(4, _foregroundStreak + 1);
                _active++;
                waiter.Source.TrySetResult(new Lease(this));
            }
        }
    }

    // One queued caller: its priority, its place in the list, and the task that completes when it gets a slot.
    private sealed class Waiter(ModelWorkScheduler owner, bool background, CancellationToken token)
    {
        public ModelWorkScheduler Owner { get; } = owner;
        public bool Background { get; } = background;
        public CancellationToken Token { get; } = token;
        public LinkedListNode<Waiter>? Node { get; set; }
        // RunContinuationsAsynchronously keeps the waiting code from running inside our lock when the slot is granted.
        public TaskCompletionSource<IDisposable> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A held slot. Dispose gives it back, and only the first Dispose counts (Interlocked.Exchange).
    private sealed class Lease(ModelWorkScheduler owner) : IDisposable
    {
        private ModelWorkScheduler? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

/// <summary>Planner, vision, notes, and Brain share this gateway and therefore the same inference budget.</summary>
// Decorator: same interface as the real gateway, but every call first waits for a slot from the scheduler.
public sealed class ScheduledModelGateway(IModelGateway inner, int maxConcurrentRequests = 1, ModelWorkScheduler? scheduler = null) : IModelGateway
{
    public ModelWorkScheduler Scheduler { get; } = scheduler ?? new(maxConcurrentRequests);
    public bool IsAvailable => inner.IsAvailable;
    public string Status
    {
        get
        {
            var state = Scheduler.Snapshot;
            return $"{inner.Status}; model requests {state.Active}/{state.Capacity} active, "
                + $"{state.ForegroundWaiting} foreground and {state.BackgroundWaiting} background queued";
        }
    }

    // Waits for a slot (background or foreground depending on the current context), then runs the real call.
    // The slot is released automatically when the "using" scope ends.
    public async Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, ModelRoute route, CancellationToken cancellationToken)
    {
        using var lease = await Scheduler.AcquireAsync(ModelWorkContext.IsBackground, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await inner.CompleteAsync(request, route, cancellationToken).ConfigureAwait(false);
    }
}
