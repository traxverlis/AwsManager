using AwsManager.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class CloudWatchLogsViewModel : AwsResourceViewModel, IRefreshableViewModel, IDisposable
{
    public sealed record Period(string Name, int Minutes);
    private readonly CloudWatchLogsService _service;
    private readonly AwsSessionService _session;
    private CancellationTokenSource? _request;
    private readonly HashSet<string> _groupTokens = [];
    private readonly HashSet<string> _eventTokens = [];
    private readonly HashSet<(string Id, string Stream, DateTimeOffset Time, string Fallback)> _eventIds = [];
    private string? _groupToken;
    private string? _eventToken;
    private LogQuery? _query;
    private int _generation;
    private long _messageCharacters;
    private bool _disposed;
    private bool _isLoading;
    private string _groupPrefix = "";
    private string _pattern = "";
    private string _streamPrefix = "";
    private string _groupStatus = "";
    private string _rangeSummary = "";
    private LogGroupItem? _selectedGroup;
    private LogEventItem? _selectedEvent;
    private Period _selectedPeriod;
    public ObservableCollection<LogGroupItem> Groups { get; } = [];
    public ObservableCollection<LogEventItem> Events { get; } = [];
    public IReadOnlyList<Period> Periods { get; } = [new("15 minutes", 15), new("1 heure", 60), new("6 heures", 360), new("24 heures", 1440), new("7 jours", 10080)];
    public Period SelectedPeriod { get => _selectedPeriod; set { if (SetField(ref _selectedPeriod, value)) ResetEvents(); } }
    public string GroupPrefix { get => _groupPrefix; set { if (SetField(ref _groupPrefix, value)) ResetGroups(); } }
    public string Pattern { get => _pattern; set { if (SetField(ref _pattern, value)) ResetEvents(); } }
    public string StreamPrefix { get => _streamPrefix; set { if (SetField(ref _streamPrefix, value)) ResetEvents(); } }
    public LogGroupItem? SelectedGroup { get => _selectedGroup; set { if (SetField(ref _selectedGroup, value)) ResetEvents(); } }
    public LogEventItem? SelectedEvent { get => _selectedEvent; set { SetField(ref _selectedEvent, value); CommandManager.InvalidateRequerySuggested(); } }
    public string GroupStatus { get => _groupStatus; private set => SetField(ref _groupStatus, value); }
    public string RangeSummary { get => _rangeSummary; private set => SetField(ref _rangeSummary, value); }
    public bool IsLoading { get => _isLoading; private set { SetField(ref _isLoading, value); NotifyCommands(); } }
    public bool CanEdit => !_disposed && !IsLoading;
    public bool HasMoreGroups => !_disposed && _groupToken != null;
    public bool HasMoreEvents => !_disposed && _eventToken != null && _query != null;
    public ICommand RefreshCommand { get; }
    public ICommand MoreGroupsCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand MoreEventsCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyMessageCommand { get; }

    public CloudWatchLogsViewModel(IAwsClientFactory factory, AwsContext? context = null, AwsSessionService? session = null) : base(factory, context)
    {
        _service = new(factory);
        _session = session ?? AwsSessionService.Current;
        _selectedPeriod = Periods[1];
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => CanEdit && Allowed("logs:DescribeLogGroups"));
        MoreGroupsCommand = new AsyncRelayCommand(_ => LoadGroupsAsync(), _ => CanEdit && HasMoreGroups && Allowed("logs:DescribeLogGroups"));
        SearchCommand = new AsyncRelayCommand(_ => SearchAsync(), _ => CanEdit && SelectedGroup != null && Allowed("logs:FilterLogEvents", Arn("logs", $"log-group:{SelectedGroup.Name}:*")));
        MoreEventsCommand = new AsyncRelayCommand(_ => LoadEventsAsync(), _ => CanEdit && HasMoreEvents && SelectedGroup != null && Allowed("logs:FilterLogEvents", Arn("logs", $"log-group:{SelectedGroup.Name}:*")));
        CancelCommand = new RelayCommand(_ => _request?.Cancel(), _ => IsLoading);
        CopyMessageCommand = new AsyncRelayCommand(_ => { Clipboard.SetText(SelectedEvent!.Message); return Task.CompletedTask; }, _ => !_disposed && SelectedEvent != null);
    }

    private void ResetGroups()
    {
        Groups.Clear();
        SelectedGroup = null;
        _groupToken = null;
        _groupTokens.Clear();
        GroupStatus = "";
        ResetEvents();
    }

    private void ResetEvents()
    {
        _generation++;
        _request?.Cancel();
        _query = null;
        _eventToken = null;
        _eventTokens.Clear();
        _eventIds.Clear();
        _messageCharacters = 0;
        Events.Clear();
        SelectedEvent = null;
        RangeSummary = "";
        Status = "";
        NotifyCommands();
    }

    public async Task RefreshAsync()
    {
        if (!CanEdit) return;
        ResetGroups();
        await LoadGroupsAsync();
    }

    public async Task SearchAsync()
    {
        if (!CanEdit || SelectedGroup == null) return;
        ResetEvents();
        var end = DateTimeOffset.UtcNow;
        _query = new(SelectedGroup.Name, end.AddMinutes(-SelectedPeriod.Minutes), end, Pattern, StreamPrefix);
        RangeSummary = $"{_query.Start:yyyy-MM-dd HH:mm:ss} - {_query.End:yyyy-MM-dd HH:mm:ss} UTC";
        await LoadEventsAsync();
    }

    private Task LoadGroupsAsync() => RunAsync(async (token, generation) =>
    {
        GroupStatus = "Chargement des groupes...";
        if (!await CheckAccessAsync([new("logs:DescribeLogGroups")]) || !IsCurrent(generation, token))
        { GroupStatus = "Inventaire non autorise ou non verifie."; return; }
        var page = await _service.LoadGroupsAsync(GroupPrefix, _groupToken, token);
        if (!IsCurrent(generation, token)) return;
        foreach (var item in page.Items)
            if (!Groups.Any(group => group.Name == item.Name) && Groups.Count < 1000) Groups.Add(item);
        var next = string.IsNullOrEmpty(page.NextToken) ? null : page.NextToken;
        var repeated = next != null && !_groupTokens.Add(next);
        var limited = Groups.Count >= 1000 && next != null;
        _groupToken = repeated || limited ? null : next;
        GroupStatus = repeated ? "Pagination interrompue : jeton repete." : limited ? "1000 groupes : liste partielle, prefixe requis." :
            $"{Groups.Count} groupe(s)" + (_groupToken != null ? " - suite disponible" : "");
    }, true);

    private Task LoadEventsAsync() => RunAsync(async (token, generation) =>
    {
        if (_query == null) return;
        Status = "Recherche en cours...";
        if (!await CheckAccessAsync([new("logs:FilterLogEvents", Arn("logs", $"log-group:{_query.Group}:*"))]) || !IsCurrent(generation, token))
        { Status = "Lecture du journal non autorisee ou non verifiee."; return; }
        var page = await _service.LoadEventsAsync(_query, _eventToken, token);
        if (!IsCurrent(generation, token)) return;
        var limited = false;
        foreach (var item in page.Items)
        {
            var key = (item.Id, item.Stream, item.Time, item.Id.Length == 0 ? item.Message : "");
            if (_eventIds.Contains(key)) continue;
            if (Events.Count >= 2000 || _messageCharacters + item.Message.Length > 8 * 1024 * 1024) { limited = true; break; }
            _eventIds.Add(key);
            _messageCharacters += item.Message.Length;
            Events.Add(item);
        }
        var next = string.IsNullOrEmpty(page.NextToken) ? null : page.NextToken;
        var repeated = next != null && !_eventTokens.Add(next);
        limited |= Events.Count >= 2000 && next != null;
        _eventToken = repeated || limited ? null : next;
        Status = $"{Events.Count} evenement(s). " + (limited ? "Limite locale atteinte : resultats partiels, reduisez la periode ou affinez le filtre." :
            repeated ? "Pagination interrompue : jeton repete, resultats partiels." :
            _eventToken != null ? "Suite disponible." : "Fin des resultats pour cette periode.");
    }, false);

    private bool IsCurrent(int generation, CancellationToken token) => !_disposed && _generation == generation && !token.IsCancellationRequested;

    private async Task RunAsync(Func<CancellationToken, int, Task> action, bool groups)
    {
        if (!CanEdit) return;
        var generation = _generation;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _request = request;
        IsLoading = true;
        try { await action(request.Token, generation); }
        catch (Exception exception)
        {
            if (!_disposed && generation == _generation)
            {
                var message = exception is OperationCanceledException ? "Lecture annulee ou delai depasse." : AwsSessionService.DescribeError(exception);
                if (groups) GroupStatus = message; else Status = message;
                if (exception is not OperationCanceledException) _session.ReportError(exception, Context);
            }
        }
        finally { _request = null; IsLoading = false; }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasMoreGroups));
        OnPropertyChanged(nameof(HasMoreEvents));
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        _disposed = true;
        ResetGroups();
    }
}