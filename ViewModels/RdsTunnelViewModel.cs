using AwsManager.Models;
using AwsManager.Services;
using System.Windows;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class RdsTunnelViewModel : AwsResourceViewModel, IDisposable
{
    private readonly IRdsTunnelBackend _backend;
    private readonly WorkspaceStore _store;
    private readonly AwsSessionService _session;
    private bool _contextInvalidated;
    private CancellationTokenSource? _request;
    private bool _disposed;
    private bool _isBusy;
    private RdsTunnelTarget? _target;
    private SsmRelay? _selectedRelay;
    private SavedConnection? _selectedPreset;
    private string _presetName = "";
    private string? _pendingRelay;
    private int _localPort;
    private int _openedLocalPort;
    private TrackedSessionModel? _tunnel;
    public RdsInstanceModel Database { get; }
    public RdsTunnelTarget? Target { get => _target; private set => SetField(ref _target, value); }
    public IReadOnlyList<SsmRelay> Relays { get; private set; } = [];
    public SsmRelay? SelectedRelay { get => _selectedRelay; set { SetField(ref _selectedRelay, value); CommandManager.InvalidateRequerySuggested(); } }
    public int LocalPort { get => _localPort; set => SetField(ref _localPort, value); }
    public bool IsBusy { get => _isBusy; private set { SetField(ref _isBusy, value); NotifyState(); } }
    public bool IsTunnelOpen { get { try { return _tunnel != null && !_tunnel.Process.HasExited; } catch (InvalidOperationException) { return false; } } }
    public bool CanConfigure => !_disposed && !_contextInvalidated && !IsBusy && !IsTunnelOpen;
    public string LocalAddress => _openedLocalPort == 0 ? "" : $"127.0.0.1:{_openedLocalPort}";
    public string PresetName { get => _presetName; set { SetField(ref _presetName, value); CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<SavedConnection> Presets => _store.Snapshot.Connections.Where(item => item.Mode == "RDS" && item.Target.Id == Database.DbInstanceIdentifier && ResourceNavigation.MatchesContext(item.Target, Context)).OrderBy(item => item.Name).ToArray();
    public SavedConnection? SelectedPreset { get => _selectedPreset; set { if (value != null) ApplyPreset(value); else SetField(ref _selectedPreset, null); } }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand SavePresetCommand { get; }
    public ICommand DeletePresetCommand { get; }
    public ICommand NewPresetCommand { get; }

    public RdsTunnelViewModel(RdsInstanceModel database, IAwsClientFactory? factory = null, WorkspaceStore? store = null, AwsContext? context = null, IRdsTunnelBackend? backend = null, AwsSessionService? session = null) : base(factory, context)
    {
        Database = database;
        _store = store ?? WorkspaceStore.Current;
        _session = session ?? AwsSessionService.Current;
        _session.ContextChanged += OnContextChanged;
        _backend = backend ?? new RdsTunnelBackend(ClientFactory, Context ?? throw new InvalidOperationException("Connexion AWS requise."));
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => CanConfigure && Allowed("rds:DescribeDBInstances,ec2:DescribeInstances,ssm:DescribeInstanceInformation"));
        OpenCommand = new AsyncRelayCommand(OpenAsync, _ => CanConfigure && Target?.State == "available" && SelectedRelay != null && !_store.Snapshot.IsReadOnly &&
            Allowed([new("ssm:StartSession", Arn("ec2", $"instance/{SelectedRelay.Id}")),
                new("ssm:StartSession", $"arn:{Partition}:ssm:{Context?.Region}::document/AWS-StartPortForwardingSessionToRemoteHost")], true));
        StopCommand = new AsyncRelayCommand(_ => { if (_tunnel != null) _backend.Stop(_tunnel); return Task.CompletedTask; }, _ => IsTunnelOpen);
        CancelCommand = new RelayCommand(_ => _request?.Cancel(), _ => IsBusy);
        CopyAddressCommand = new AsyncRelayCommand(_ => { Clipboard.SetText(LocalAddress); Status = "Adresse locale copiee."; return Task.CompletedTask; }, _ => IsTunnelOpen);
        SavePresetCommand = new AsyncRelayCommand(form => { SavePreset(form); return Task.CompletedTask; }, _ => CanConfigure && Target != null && SelectedRelay != null && !string.IsNullOrWhiteSpace(PresetName));
        DeletePresetCommand = new AsyncRelayCommand(_ => { _store.RemoveConnection(SelectedPreset!.Id); SelectedPreset = null; PresetName = ""; OnPropertyChanged(nameof(Presets)); return Task.CompletedTask; }, _ => CanConfigure && SelectedPreset != null);
        NewPresetCommand = new RelayCommand(_ => { SelectedPreset = null; PresetName = ""; }, _ => CanConfigure);
    }

    public async Task RefreshAsync()
    {
        if (!CanConfigure || _disposed) return;
        var relayId = _pendingRelay ?? SelectedRelay?.Id;
        IsBusy = true;
        Target = null;
        SelectedRelay = null;
        Relays = [];
        OnPropertyChanged(nameof(Relays));
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _request = request;
        try
        {
            Status = "Lecture de la base et des relais SSM...";
            var target = await _backend.LoadTargetAsync(Database.DbInstanceIdentifier, request.Token);
            var relays = await _backend.LoadRelaysAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            Target = target;
            Relays = relays.OrderByDescending(relay => target.VpcId.Length > 0 && relay.VpcId == target.VpcId).ThenBy(relay => relay.Name).ToArray();
            OnPropertyChanged(nameof(Relays));
            SelectedRelay = Relays.FirstOrDefault(relay => relay.Id == relayId);
            if (LocalPort == 0) LocalPort = target.Port <= 55535 ? target.Port + 10000 : target.Port;
            Status = Relays.Count == 0 ? "Aucun relais EC2 actif avec SSM en ligne et un agent compatible." :
                relayId != null && SelectedRelay == null ? "Le relais enregistre n'est plus disponible. Selectionnez un autre relais." :
                target.State != "available" ? $"Base indisponible : {target.State}." : $"{Relays.Count} relais compatible(s).";
            _pendingRelay = null;
        }
        catch (OperationCanceledException) { Status = "Lecture annulee ou delai depasse."; }
        catch (Exception exception) { ReportError(exception); }
        finally { _request = null; IsBusy = false; }
    }

    public void ApplyPreset(SavedConnection preset)
    {
        if (preset.Mode != "RDS" || preset.Target.Service != "RDS" || preset.Target.Id != Database.DbInstanceIdentifier || !ResourceNavigation.MatchesContext(preset.Target, Context) || preset.Tunnels.Length != 1)
            throw new ArgumentException("Cette configuration n'appartient pas a cette base et a ce contexte AWS.");
        SsmService.ValidatePorts(preset.Tunnels.Select(tunnel => (tunnel.RemotePort, tunnel.LocalPort)));
        SetField(ref _selectedPreset, preset, nameof(SelectedPreset));
        PresetName = preset.Name;
        LocalPort = preset.Tunnels[0].LocalPort;
        _pendingRelay = preset.RelayInstanceId;
        SelectedRelay = Relays.FirstOrDefault(relay => relay.Id == preset.RelayInstanceId);
    }

    private void SavePreset(object? form)
    {
        if (form is DependencyObject element && Views.FormValidation.HasErrors(element)) throw new ArgumentException("Corrigez le port local avant d'enregistrer.");
        var context = Context ?? throw new InvalidOperationException("Connexion AWS requise.");
        var target = Target ?? throw new InvalidOperationException("Actualisez la base.");
        var relay = SelectedRelay ?? throw new InvalidOperationException("Selectionnez un relais.");
        var resource = new ResourceReference(context.Profile, context.Account, context.Region, "RDS", Database.DbInstanceIdentifier, Database.DbInstanceIdentifier);
        var preset = new SavedConnection(SelectedPreset?.Id ?? Guid.NewGuid(), PresetName.Trim(), resource, "RDS", [new(target.Port, LocalPort)], relay.Id);
        _store.SaveConnection(preset);
        OnPropertyChanged(nameof(Presets));
        SelectedPreset = preset;
        Status = "Configuration RDS enregistree sur ce poste.";
    }

    private async Task OpenAsync(object? form)
    {
        try
        {
            if (form is DependencyObject element && Views.FormValidation.HasErrors(element)) throw new ArgumentException("Corrigez le port local.");
            var target = Target ?? throw new InvalidOperationException("Actualisez la base.");
            var relay = SelectedRelay ?? throw new InvalidOperationException("Selectionnez un relais.");
            var localPort = LocalPort;
            SsmService.ValidatePorts([(target.Port, localPort)]);
            IsBusy = true;
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            _request = request;
            Status = "Verification du relais et ouverture du tunnel...";
            var tunnel = await _backend.OpenAsync(target, relay, localPort, request.Token);
            if (request.IsCancellationRequested) { _backend.Stop(tunnel); request.Token.ThrowIfCancellationRequested(); }
            if (_tunnel != null) _tunnel.Process.Exited -= OnTunnelExited;
            _tunnel = tunnel;
            _openedLocalPort = localPort;
            tunnel.Process.Exited += OnTunnelExited;
            tunnel.Process.EnableRaisingEvents = true;
            if (tunnel.Process.HasExited) OnTunnelExited(tunnel.Process, EventArgs.Empty);
            else Status = "Tunnel local ouvert. L'authentification et l'acces a la base restent a verifier dans le client SQL.";
        }
        catch (OperationCanceledException) { Status = "Connexion annulee ou delai depasse."; }
        catch (Exception exception) { ReportError(exception); }
        finally { _request = null; IsBusy = false; }
    }

    private void OnTunnelExited(object? sender, EventArgs args)
    {
        void Update() { _openedLocalPort = 0; Status = "Le tunnel est ferme."; NotifyState(); }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Update);
        else Update();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(IsTunnelOpen));
        OnPropertyChanged(nameof(LocalAddress));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnContextChanged()
    {
        _contextInvalidated = true;
        _request?.Cancel();
        Status = "Le contexte AWS a change. Fermez ce dialogue et reconnectez-vous.";
        NotifyState();
    }

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        _session.ContextChanged -= OnContextChanged;
        if (_tunnel != null) _tunnel.Process.Exited -= OnTunnelExited;
    }
}