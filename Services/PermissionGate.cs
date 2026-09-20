using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AwsManager.Services;

public sealed class PermissionGate : IDisposable
{
    private static readonly ConditionalWeakTable<AwsContext, PermissionGate> Gates = new();
    private readonly AwsContext _context;
    private readonly WorkspaceStore _store;
    private readonly PermissionService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<PermissionCheck, (PermissionResult Result, DateTimeOffset Date)> _cache = [];
    private readonly Dictionary<PermissionCheck, TaskCompletionSource<PermissionResult>> _pending = [];
    private bool _loading;
    private bool _disposed;
    private string? _unavailable;
    public event Action? Changed;
    public string Status
    {
        get
        {
            lock (_sync) return _disposed ? "Droits IAM : contexte ferme." : _unavailable ?? (_loading ? "Verification des droits IAM..." :
                $"Droits IAM : {_cache.Values.Count(item => item.Result.State == PermissionState.Allowed)} autorises, {_cache.Values.Count(item => item.Result.State == PermissionState.Denied)} refuses, {_cache.Values.Count(item => item.Result.State == PermissionState.Unknown)} non verifies.");
        }
    }
    public static PermissionGate? For(AwsContext? context) => context != null && Gates.TryGetValue(context, out var gate) ? gate : null;

    public PermissionGate(AwsContext context, IAwsClientFactory factory, WorkspaceStore store)
    {
        (_context, _store, _service) = (context, store, new(factory, context));
        Gates.Remove(context);
        Gates.Add(context, this);
    }

    public bool Allows(IEnumerable<PermissionCheck> checks, bool write = false)
    {
        if (_disposed || (write && _store.Snapshot.IsReadOnly)) return false;
        var tasks = checks.Distinct().Select(Request).ToArray();
        return tasks.Length > 0 && tasks.All(task => task.IsCompletedSuccessfully && task.Result.State == PermissionState.Allowed);
    }

    public async Task<bool> CheckAsync(IEnumerable<PermissionCheck> checks, bool write = false)
    {
        if (_disposed || (write && _store.Snapshot.IsReadOnly)) return false;
        var results = await Task.WhenAll(checks.Distinct().Select(Request));
        return !_disposed && (!write || !_store.Snapshot.IsReadOnly) && results.Length > 0 && results.All(result => result.State == PermissionState.Allowed);
    }

    public string Explain(IEnumerable<PermissionCheck> checks)
    {
        lock (_sync)
        {
            if (_disposed) return "Contexte AWS ferme.";
            if (_unavailable != null) return _unavailable;
            return string.Join("\n", checks.Distinct().Select(check =>
                $"{check.Action} : {(_cache.TryGetValue(check, out var item) ? item.Result.Reason : "verification en attente.")}"));
        }
    }

    private Task<PermissionResult> Request(PermissionCheck check)
    {
        lock (_sync)
        {
            if (_disposed || _unavailable != null) return Task.FromResult(new PermissionResult(PermissionState.Unknown, _unavailable ?? "Contexte ferme."));
            if (_cache.TryGetValue(check, out var cached) && DateTimeOffset.UtcNow - cached.Date < TimeSpan.FromMinutes(5))
                return Task.FromResult(cached.Result);
            if (!_pending.TryGetValue(check, out var source))
            {
                source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(check, source);
            }
            if (!_loading) { _loading = true; _ = LoadAsync(); }
            return source.Task;
        }
    }

    private async Task LoadAsync()
    {
        await Task.Yield();
        while (true)
        {
            KeyValuePair<PermissionCheck, TaskCompletionSource<PermissionResult>>[] batch;
            CancellationToken token;
            lock (_sync)
            {
                if (_disposed || _pending.Count == 0) { _loading = false; break; }
                batch = _pending.Take(100).ToArray();
                token = _lifetime.Token;
            }
            IReadOnlyDictionary<PermissionCheck, PermissionResult> results;
            string? error = null;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(45));
            try { results = await _service.EvaluateAsync(batch.Select(item => item.Key).ToArray(), request.Token); }
            catch (Exception exception)
            {
                error = exception is OperationCanceledException ? "Droits IAM non verifies : delai depasse. Actualisez les droits." :
                    $"Droits IAM non verifies : {AwsSessionService.DescribeError(exception)} Requis : iam:SimulatePrincipalPolicy et iam:GetRole pour un role assume.";
                results = batch.ToDictionary(item => item.Key, _ => new PermissionResult(PermissionState.Unknown, error));
            }
            lock (_sync)
            {
                if (_disposed) { _loading = false; break; }
                _unavailable = error;
                foreach (var item in batch)
                {
                    var result = results[item.Key];
                    _cache[item.Key] = (result, DateTimeOffset.UtcNow);
                    _pending.Remove(item.Key);
                    item.Value.TrySetResult(result);
                }
                if (_unavailable != null)
                {
                    foreach (var item in _pending.Values) item.TrySetResult(new(PermissionState.Unknown, _unavailable));
                    _pending.Clear();
                }
            }
        }
        void Notify() { if (!_disposed) { Changed?.Invoke(); CommandManager.InvalidateRequerySuggested(); } }
        if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess()) _ = app.Dispatcher.BeginInvoke(Notify);
        else Notify();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            foreach (var item in _pending.Values) item.TrySetResult(new(PermissionState.Unknown, "Contexte ferme."));
            _pending.Clear();
            _cache.Clear();
            _lifetime.Dispose();
        }
    }
}