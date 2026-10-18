namespace Jarvis.Core;

/// <summary>Flows across awaits and tool calls so all shared gateway users keep workflow priority.</summary>
public static class ModelWorkContext
{
    private static readonly AsyncLocal<bool> Background = new();
    public static bool IsBackground => Background.Value;

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

public sealed record WorkQueueSnapshot(int Active, int ForegroundWaiting, int BackgroundWaiting, int Capacity);

/// <summary>A bounded async limiter with FIFO ordering within each priority and bounded starvation.</summary>
public sealed class ModelWorkScheduler
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _foreground = new();
    private readonly LinkedList<Waiter> _background = new();
    private readonly int _capacity;
    private readonly int _maxWaiting;
    private int _active;
    private int _foregroundStreak;

    public ModelWorkScheduler(int capacity = 1, int maxWaiting = 64)
    {
        if (capacity is < 1 or > 4 || maxWaiting < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _maxWaiting = maxWaiting;
    }

    public WorkQueueSnapshot Snapshot
    {
        get { lock (_gate) return new(_active, _foreground.Count, _background.Count, _capacity); }
    }

    public async Task<IDisposable> AcquireAsync(bool background, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (_active < _capacity && _foreground.Count + _background.Count == 0)
            {
                _active++;
                return new Lease(this);
            }
            if (_foreground.Count + _background.Count >= _maxWaiting)
                throw new InvalidOperationException("Jarvis's work queue is full. Let existing work finish before adding more.");
            waiter = new Waiter(this, background, cancellationToken);
            waiter.Node = (background ? _background : _foreground).AddLast(waiter);
        }

        using var registration = cancellationToken.UnsafeRegister(
            static state => { var item = (Waiter)state!; item.Owner.Cancel(item); }, waiter);
        return await waiter.Source.Task.ConfigureAwait(false);
    }

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

    private sealed class Waiter(ModelWorkScheduler owner, bool background, CancellationToken token)
    {
        public ModelWorkScheduler Owner { get; } = owner;
        public bool Background { get; } = background;
        public CancellationToken Token { get; } = token;
        public LinkedListNode<Waiter>? Node { get; set; }
        public TaskCompletionSource<IDisposable> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(ModelWorkScheduler owner) : IDisposable
    {
        private ModelWorkScheduler? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

/// <summary>Planner, vision, notes, and Brain share this gateway and therefore the same inference budget.</summary>
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

    public async Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, ModelRoute route, CancellationToken cancellationToken)
    {
        using var lease = await Scheduler.AcquireAsync(ModelWorkContext.IsBackground, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await inner.CompleteAsync(request, route, cancellationToken).ConfigureAwait(false);
    }
}
