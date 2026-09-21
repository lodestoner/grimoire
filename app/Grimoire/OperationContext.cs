namespace Spellbook;

/// <summary>Scoped cancellation flows into synchronous provider APIs via ExecutionContext.
/// Create a scope inside worker work; dispose it before returning. Nested scopes restore their parent.</summary>
internal static class OperationContext
{
    static readonly AsyncLocal<CancellationToken> Current = new();
    public static CancellationToken Token => Current.Value;
    public static IDisposable Push(CancellationToken token) => new Scope(token);
    public static CancellationTokenSource Deadline(int timeoutMs)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(Token);
        source.CancelAfter(timeoutMs);
        return source;
    }
    sealed class Scope : IDisposable
    {
        readonly CancellationToken _previous = Current.Value;
        public Scope(CancellationToken token) => Current.Value = token;
        public void Dispose() => Current.Value = _previous;
    }
}
