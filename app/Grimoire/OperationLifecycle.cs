namespace Spellbook;

/// <summary>UI-thread-owned operation slot. Guard and acquisition are synchronous; no lock is needed.
/// Workers receive only the lease token. All state transitions must stay on the creating thread.</summary>
internal sealed class OperationLifecycle
{
    readonly int _ownerThread = Environment.CurrentManagedThreadId;
    Lease? _active;
    bool _quitting;
    public bool IsQuitting { get { RequireOwner(); return _quitting; } }

    void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Operation lifecycle must be used on its owning UI thread.");
    }

    public Lease? TryBegin()
    {
        RequireOwner();
        if (_quitting || _active != null) return null;
        return _active = new Lease(this);
    }

    public void CancelActive() { RequireOwner(); _active?.Cancel(); }
    public Task StopAsync()
    {
        RequireOwner();
        _quitting = true;
        _active?.Cancel();
        return _active?.Finished.Task ?? Task.CompletedTask;
    }

    internal sealed class Lease : IDisposable
    {
        readonly OperationLifecycle _owner;
        readonly CancellationTokenSource _source = new(TimeSpan.FromMinutes(5));
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool _disposed;
        internal Lease(OperationLifecycle owner) => _owner = owner;
        public CancellationToken Token => _source.Token;
        public void Cancel() { _owner.RequireOwner(); _source.Cancel(); }
        public void Dispose()
        {
            _owner.RequireOwner();
            if (_disposed) return;
            _disposed = true;
            _owner._active = null;
            _source.Dispose();
            Finished.TrySetResult();
        }
    }
}
