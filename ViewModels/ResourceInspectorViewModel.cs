using AwsManager.Models;
using AwsManager.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class MetricChart
{
    public string Name { get; }
    public string Summary { get; }
    public PlotModel Plot { get; }
    public MetricChart(MetricSeriesData data)
    {
        Name = data.Name;
        Summary = data.Samples.Count == 0 ? (data.IsPartial ? "Mesures indisponibles (partiel)" : "Aucune mesure") : $"{data.Samples[^1].Value:0.##} {data.Unit}" + (data.IsPartial ? " (partiel)" : "");
        Plot = new PlotModel { PlotAreaBorderColor = OxyColor.Parse("#DCE3E8"), TextColor = OxyColor.Parse("#65737E"), DefaultFont = "Segoe UI", DefaultFontSize = 11 };
        Plot.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "HH:mm", Title = "UTC", Minimum = DateTimeAxis.ToDouble(data.Start), Maximum = DateTimeAxis.ToDouble(data.End), IsZoomEnabled = false, IsPanEnabled = false });
        Plot.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = data.Unit, Minimum = 0, Maximum = data.Unit == "%" ? 100 : double.NaN, MajorGridlineStyle = LineStyle.Dot, MajorGridlineColor = OxyColor.Parse("#DCE3E8"), IsZoomEnabled = false, IsPanEnabled = false });
        var series = new LineSeries { Color = OxyColor.Parse(data.Unit == "%" ? "#007E6C" : "#B47F27"), StrokeThickness = 2, MarkerType = MarkerType.Circle, MarkerSize = 2 };
        DateTime? previous = null;
        foreach (var point in data.Samples)
        {
            if (previous.HasValue && point.Time - previous.Value > TimeSpan.FromMinutes(7.5)) series.Points.Add(DataPoint.Undefined);
            series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(point.Time), point.Value));
            previous = point.Time;
        }
        Plot.Series.Add(series);
    }
}

public sealed class ResourceInspectorViewModel : ViewModelBase, IDisposable
{
    private readonly ResourceRelationsService _relations;
    private readonly MonitoringService _monitoring;
    private readonly AwsSessionService? _session;
    private readonly AwsContext? _context;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private int _selectedTab;
    private int _hours = 1;
    private bool _isLoading;
    private string _status = "";
    private string _alarmStatus = "";
    public ResourceReference Resource { get; }
    public bool HasMonitoring => Resource.Service is "EC2" or "RDS";
    public int[] Periods { get; } = [1, 6, 24];
    public int Hours { get => _hours; set { if (SetField(ref _hours, value) && _initialized) _ = RefreshAsync(); } }
    public int SelectedTab { get => _selectedTab; set { if (SetField(ref _selectedTab, value) && _initialized) _ = RefreshAsync(); } }
    public bool IsLoading { get => _isLoading; private set { SetField(ref _isLoading, value); OnPropertyChanged(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsLoading;
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string AlarmStatus { get => _alarmStatus; private set => SetField(ref _alarmStatus, value); }
    public IReadOnlyList<ResourceReference> Related { get; private set; } = [];
    public IReadOnlyList<MetricChart> Charts { get; private set; } = [];
    public IReadOnlyList<ResourceAlarm> Alarms { get; private set; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand OpenRelatedCommand { get; }
    public event Action? CloseRequested;

    public ResourceInspectorViewModel(ResourceReference resource, IAwsClientFactory factory, Func<ResourceReference, Task> open, bool monitoring = false, AwsSessionService? session = null)
    {
        Resource = resource;
        _session = session;
        _context = session?.Context;
        _relations = new(factory);
        _monitoring = new(factory);
        _selectedTab = monitoring && HasMonitoring ? 1 : 0;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => IsIdle && (PermissionGate.For(_context) is not { } gate ||
            (SelectedTab == 0 ? gate.Allows(RelationChecks(Resource.Service)) : gate.Allows([new("cloudwatch:GetMetricData")]) | gate.Allows([new("cloudwatch:DescribeAlarms")]))));
        OpenRelatedCommand = new AsyncRelayCommand(async item =>
        {
            if (item is not ResourceReference related) return;
            CloseRequested?.Invoke();
            await open(related);
        }, _ => IsIdle);
    }

    internal static PermissionCheck[] RelationChecks(string service) => (service switch
    {
        "EC2" => "ec2:DescribeInstances,ec2:DescribeSecurityGroups,autoscaling:DescribeAutoScalingGroups",
        "RDS" => "rds:DescribeDBInstances,ec2:DescribeSecurityGroups",
        "Auto Scaling" => "autoscaling:DescribeAutoScalingGroups,ec2:DescribeInstances",
        "Groupes de sécurité" => "ec2:DescribeInstances",
        _ => ""
    }).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(action => new PermissionCheck(action)).ToArray();
    private Task<bool> CheckAsync(PermissionCheck[] checks) => PermissionGate.For(_context)?.CheckAsync(checks) ?? Task.FromResult(true);

    public async Task InitializeAsync() { _initialized = true; await RefreshAsync(); }
    public async Task RefreshAsync()
    {
        if (IsLoading || _lifetime.IsCancellationRequested) return;
        IsLoading = true;
        Status = "Chargement...";
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            if (SelectedTab == 0)
            {
                Related = [];
                OnPropertyChanged(nameof(Related));
                if (!await CheckAsync(RelationChecks(Resource.Service))) { Status = "Relations non autorisees ou non verifiees."; return; }
                request.Token.ThrowIfCancellationRequested();
                Related = await _relations.LoadAsync(Resource, request.Token);
                OnPropertyChanged(nameof(Related));
                Status = Related.Count == 0 ? "Aucune relation trouvee." : $"{Related.Count} ressource(s) liee(s).";
            }
            else
            {
                Charts = [];
                Alarms = [];
                AlarmStatus = "";
                OnPropertyChanged(nameof(Charts));
                OnPropertyChanged(nameof(Alarms));
                try
                {
                    if (!await CheckAsync([new("cloudwatch:GetMetricData")])) throw new InvalidOperationException("Acces non autorise ou non verifie.");
                    request.Token.ThrowIfCancellationRequested();
                    var metrics = await _monitoring.LoadMetricsAsync(Resource, Hours, DateTime.UtcNow, request.Token);
                    Charts = metrics.Select(metric => new MetricChart(metric)).ToArray();
                    OnPropertyChanged(nameof(Charts));
                    Status = $"Mesures lues a {DateTime.Now:HH:mm:ss} (pas de 5 min).";
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { Status = "Metriques : " + DescribeError(exception); }
                try
                {
                    if (!await CheckAsync([new("cloudwatch:DescribeAlarms")])) throw new InvalidOperationException("Acces non autorise ou non verifie.");
                    request.Token.ThrowIfCancellationRequested();
                    Alarms = await _monitoring.LoadAlarmsAsync(Resource, request.Token);
                    OnPropertyChanged(nameof(Alarms));
                    AlarmStatus = Alarms.Count == 0 ? "Aucune alarme metrique liee." : $"{Alarms.Count} alarme(s) metrique(s).";
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { AlarmStatus = "Alarmes : " + DescribeError(exception); }
            }
        }
        catch (OperationCanceledException) { Status = "Lecture annulee ou delai depasse."; }
        catch (Exception exception) { Status = DescribeError(exception); }
        finally { IsLoading = false; }
    }
    private string DescribeError(Exception exception)
    {
        if (_context != null) _session?.ReportError(exception, _context);
        return AwsSessionService.DescribeError(exception);
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}