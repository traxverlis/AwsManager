using AwsManager.Models;
using AwsManager.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class PatchViewModel : AwsResourceViewModel, IRefreshableViewModel, IDisposable
{
    public sealed record OperationChoice(string Id, string Name);
    private readonly PatchService _service;
    private readonly WorkspaceStore _store;
    private readonly Func<string, bool> _confirm;
    private CancellationTokenSource? _request;
    private PatchNode[] _selection = [];
    private PatchPlan? _plan;
    private string? _commandToken;
    private readonly HashSet<string> _commandTokens = [];
    private bool _disposed;
    private bool _isLoading;
    private bool _allowReboot;
    private bool _acknowledged;
    private string _operation = "Scan";
    private string _concurrency = "1";
    private string _maxErrors = "0";
    private string _preview = "";
    private string _patchDetails = "";
    private string _validation = "";
    private int _selectedTab;
    private PatchCommandItem? _selectedCommand;
    private PatchInvocationItem? _selectedInvocation;
    private PatchNode? _selectedNode;
    public ObservableCollection<PatchNode> Nodes { get; } = [];
    public ObservableCollection<PatchCommandItem> Commands { get; } = [];
    public ObservableCollection<PatchInvocationItem> Invocations { get; } = [];
    public IReadOnlyList<OperationChoice> Operations { get; } = [new("Scan", "Analyse"), new("Install", "Installation")];
    public string Operation { get => _operation; set { if (SetField(ref _operation, value)) { _allowReboot = false; OnPropertyChanged(nameof(AllowReboot)); OnPropertyChanged(nameof(IsInstall)); InvalidatePlan(); } } }
    public bool IsInstall => Operation == "Install";
    public bool AllowReboot { get => _allowReboot; set { if (SetField(ref _allowReboot, value)) InvalidatePlan(); } }
    public string Concurrency { get => _concurrency; set { if (SetField(ref _concurrency, value)) InvalidatePlan(); } }
    public string MaxErrors { get => _maxErrors; set { if (SetField(ref _maxErrors, value)) InvalidatePlan(); } }
    public bool Acknowledged { get => _acknowledged; set { SetField(ref _acknowledged, value); Notify(); } }
    public int SelectedTab { get => _selectedTab; set => SetField(ref _selectedTab, value); }
    public PatchNode? SelectedNode { get => _selectedNode; set { SetField(ref _selectedNode, value); PatchDetails = ""; Notify(); } }
    public PatchCommandItem? SelectedCommand { get => _selectedCommand; set { SetField(ref _selectedCommand, value); Invocations.Clear(); SelectedInvocation = null; Notify(); } }
    public PatchInvocationItem? SelectedInvocation { get => _selectedInvocation; set { SetField(ref _selectedInvocation, value); OnPropertyChanged(nameof(CommandOutput)); } }
    public string CommandOutput => SelectedInvocation?.Output ?? "";
    public string Preview { get => _preview; private set => SetField(ref _preview, value); }
    public string PatchDetails { get => _patchDetails; private set => SetField(ref _patchDetails, value); }
    public string ValidationMessage { get => _validation; private set => SetField(ref _validation, value); }
    public bool IsLoading { get => _isLoading; private set { SetField(ref _isLoading, value); Notify(); } }
    public bool CanEdit => !_disposed && !IsLoading;
    private PatchNode[] Targets => _selection.Where(item => Nodes.Contains(item) && FilteredItems!.Contains(item)).DistinctBy(item => item.Id).ToArray();
    public int SelectionCount => Targets.Length;
    public string SelectionSummary => $"{SelectionCount} machine(s) selectionnee(s)";
    public bool CanPrepare => CanEdit && ValidationMessage.Length == 0 && Allowed(PreparationChecks());
    public bool CanSubmit => CanEdit && _plan != null && !_store.Snapshot.IsReadOnly && (!IsInstall || Acknowledged) && Allowed(SubmissionChecks(), true);
    public ICommand RefreshCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand PatchDetailsCommand { get; }
    public ICommand RefreshCommandsCommand { get; }
    public ICommand MoreCommandsCommand { get; }
    public ICommand RefreshInvocationsCommand { get; }
    public ICommand CancelCommand { get; }

    public PatchViewModel(IAwsClientFactory factory, WorkspaceStore? store = null, AwsContext? context = null, Func<string, bool>? confirm = null) : base(factory, context)
    {
        _service = new(factory);
        _store = store ?? WorkspaceStore.Current;
        _confirm = confirm ?? Confirm;
        ConfigureFilter<PatchNode>(Nodes, item => $"{item.Id} {item.Name} {item.Platform} {item.PingStatus} {item.Compliance} {item.Baseline}");
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => CanEdit && Allowed("ssm:DescribeInstanceInformation,ssm:DescribeInstancePatchStates"));
        PrepareCommand = new AsyncRelayCommand(_ => PrepareAsync(), _ => CanPrepare);
        SubmitCommand = new AsyncRelayCommand(_ => SubmitAsync(), _ => CanSubmit);
        PatchDetailsCommand = new AsyncRelayCommand(_ => ReadPatchDetailsAsync(), _ => CanEdit && SelectedNode != null && Allowed("ssm:DescribeInstancePatches"));
        RefreshCommandsCommand = new AsyncRelayCommand(_ => LoadCommandsAsync(true), _ => CanEdit && Allowed("ssm:ListCommands"));
        MoreCommandsCommand = new AsyncRelayCommand(_ => LoadCommandsAsync(false), _ => CanEdit && !string.IsNullOrEmpty(_commandToken) && Allowed("ssm:ListCommands"));
        RefreshInvocationsCommand = new AsyncRelayCommand(_ => LoadInvocationsAsync(), _ => CanEdit && SelectedCommand != null && Allowed("ssm:ListCommandInvocations"));
        CancelCommand = new RelayCommand(_ => _request?.Cancel(), _ => IsLoading);
        _store.Changed += Notify;
        PropertyChanged += OnCriteriaChanged;
        Notify();
    }

    private IEnumerable<PermissionCheck> PreparationChecks()
    {
        yield return new("ssm:DescribeInstanceInformation");
        yield return new("ssm:DescribeInstancePatchStates");
        yield return new("ssm:GetDefaultPatchBaseline");
        yield return new("ssm:GetPatchBaselineForPatchGroup");
        yield return new("ssm:GetPatchBaseline");
        if (Targets.Any(target => target.Id.StartsWith("i-"))) yield return new("ec2:DescribeInstances");
        foreach (var target in Targets.Where(target => target.Id.StartsWith("mi-")))
            yield return new("ssm:ListTagsForResource", Arn("ssm", $"managed-instance/{target.Id}"));
    }

    private IEnumerable<PermissionCheck> SubmissionChecks() => PreparationChecks().Concat(
        (_plan?.Targets ?? []).Select(target => new PermissionCheck("ssm:SendCommand", target.Id.StartsWith("i-")
            ? Arn("ec2", $"instance/{target.Id}") : Arn("ssm", $"managed-instance/{target.Id}"))))
        .Append(new("ssm:SendCommand", $"arn:{Partition}:ssm:{Context?.Region}::document/{PatchService.DocumentName}"));

    private void OnCriteriaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchText)) InvalidatePlan();
    }

    public void SetSelection(IEnumerable<PatchNode> selection) { _selection = selection.ToArray(); InvalidatePlan(); }
    private void InvalidatePlan()
    {
        _plan = null;
        Preview = "";
        _acknowledged = false;
        OnPropertyChanged(nameof(Acknowledged));
        Notify();
    }

    public async Task RefreshAsync()
    {
        if (!CanEdit) return;
        SetSelection([]);
        SelectedNode = null;
        Nodes.Clear();
        await RunAsync(async token =>
        {
            var nodes = await _service.NodesAsync(token);
            token.ThrowIfCancellationRequested();
            foreach (var node in nodes) Nodes.Add(node);
            Status = $"{Nodes.Count} machine(s). Conformite au dernier releve, pas en temps reel.";
        });
    }

    private async Task PrepareAsync()
    {
        if (!CanPrepare) return;
        var ids = Targets.Select(item => item.Id).ToArray();
        var operation = Operation;
        var reboot = IsInstall && AllowReboot ? "RebootIfNeeded" : "NoReboot";
        var concurrency = int.Parse(Concurrency);
        var errors = int.Parse(MaxErrors);
        InvalidatePlan();
        await RunAsync(async token =>
        {
            var plan = await _service.PrepareAsync(ids, operation, reboot, concurrency, errors, token);
            token.ThrowIfCancellationRequested();
            _plan = plan;
            var text = new StringBuilder().AppendLine($"{plan.Operation} | {plan.Targets.Count} machine(s) | {plan.RebootOption}")
                .AppendLine($"Concurrence : {plan.Concurrency} | Erreurs tolerees : {plan.MaxErrors} | Execution max. : 60 min")
                .AppendLine("Baselines de groupe ou par defaut de l'OS ; aucun remplacement Quick Setup ni politique modifiee.")
                .AppendLine("Les depots de mises a jour doivent etre accessibles. Le statut SSM Online ne le garantit pas.").AppendLine();
            foreach (var target in plan.Targets) text.AppendLine($"{target.Name} ({target.Id}) | {target.OperatingSystem}\nGroupe : {(target.Group.Length == 0 ? "defaut OS" : target.Group)} | {target.BaselineName} ({target.BaselineId})\n");
            foreach (var baseline in plan.Targets.DistinctBy(item => item.BaselineId)) text.AppendLine($"Baseline {baseline.BaselineName}\n{baseline.BaselineDefinition}\n");
            Preview = text.ToString();
            SelectedTab = 1;
            Status = "Preparation valable 5 minutes. Baselines et cibles relues avant envoi. Lecture seule : consultation uniquement.";
        });
    }

    private async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        var plan = _plan!;
        var targets = string.Join("\n", plan.Targets.Select(item => $"{item.Name} ({item.Id}) - {item.BaselineName} ({item.BaselineId})"));
        var warning = plan.Operation == "Install" ? "INSTALLATION : les services peuvent etre interrompus meme sans redemarrage. Aucun retour arriere automatique." : "ANALYSE distante : aucun correctif installe ni redemarrage.";
        if (!_confirm($"{warning}\n{targets}\n\nRedemarrage : {plan.RebootOption}\nConcurrence : {plan.Concurrency} | Erreurs tolerees : {plan.MaxErrors}\nUne commande envoyee continue dans AWS apres fermeture de l'application. Continuer ?")) return;
        _plan = null;
        await RunAsync(async token =>
        {
            if (Context != null) OperationSafety.EnsureWritable(Context, "SSM", "SendCommand", targets, _store);
            try
            {
                var commandId = await _service.SubmitAsync(plan, token);
                if (Context != null) OperationSafety.Record(Context, "SSM", $"Patch{plan.Operation}", commandId, "Acceptee ; resultat a suivre", _store);
                if (_disposed) return;
                var command = new PatchCommandItem(commandId, plan.Operation, "Acceptee", DateTime.UtcNow, plan.Targets.Count, 0, 0, plan.RebootOption);
                Commands.Insert(0, command);
                SelectedCommand = command;
                SelectedTab = 2;
                Status = $"Commande acceptee : {commandId}. Actualisez les executions ; l'acceptation n'est pas un succes d'installation.";
            }
            catch
            {
                if (!_disposed) Status = "Envoi non confirme. Verifiez les executions AWS avant toute nouvelle tentative ; aucun renvoi automatique.";
                throw;
            }
        }, preserveStatus: true);
    }

    private Task ReadPatchDetailsAsync()
    {
        var target = SelectedNode!;
        PatchDetails = "";
        return RunAsync(async token =>
        {
            var detail = await _service.PatchDetailsAsync(target.Id, token);
            token.ThrowIfCancellationRequested();
            if (SelectedNode == target) PatchDetails = detail;
            Status = $"Correctifs du dernier releve : {target.Id}.";
        });
    }

    private Task LoadCommandsAsync(bool reset)
    {
        if (reset) { Commands.Clear(); SelectedCommand = null; _commandToken = null; _commandTokens.Clear(); }
        return RunAsync(async token =>
        {
            var page = await _service.CommandsAsync(_commandToken, token);
            token.ThrowIfCancellationRequested();
            foreach (var item in page.Items) if (!Commands.Any(existing => existing.Id == item.Id)) Commands.Add(item);
            var repeated = !string.IsNullOrEmpty(page.NextToken) && !_commandTokens.Add(page.NextToken);
            var limited = Commands.Count >= 1000 && !string.IsNullOrEmpty(page.NextToken);
            _commandToken = repeated || limited ? null : page.NextToken;
            Status = $"{Commands.Count} commande(s) AWS-RunPatchBaseline du compte et de la region. " + (repeated || limited ? "Liste partielle : limite atteinte." : !string.IsNullOrEmpty(_commandToken) ? "Suite disponible." : "Fin de liste.");
        });
    }

    private Task LoadInvocationsAsync()
    {
        var command = SelectedCommand!;
        return RunAsync(async token =>
        {
            var items = await _service.InvocationsAsync(command.Id, token);
            token.ThrowIfCancellationRequested();
            if (SelectedCommand != command) return;
            Invocations.Clear();
            SelectedInvocation = null;
            foreach (var item in items) Invocations.Add(item);
            Status = $"{items.Count} execution(s) pour {command.Id}. Les resultats peuvent apparaitre avec retard ; sorties AWS potentiellement tronquees.";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool preserveStatus = false)
    {
        if (!CanEdit) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _request = request;
        IsLoading = true;
        Status = "Requete SSM en cours...";
        try { await action(request.Token); }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                var previous = Status;
                ReportError(exception);
                if (preserveStatus) Status += " " + previous;
            }
        }
        finally { _request = null; IsLoading = false; }
    }

    private void Notify()
    {
        try
        {
            if (!int.TryParse(Concurrency, out var concurrency) || !int.TryParse(MaxErrors, out var errors)) throw new ArgumentException("Concurrence et erreurs tolerees doivent etre des nombres entiers.");
            PatchService.ValidateOptions(Targets.Select(item => item.Id).ToArray(), Operation, IsInstall && AllowReboot ? "RebootIfNeeded" : "NoReboot", concurrency, errors);
            ValidationMessage = "";
        }
        catch (ArgumentException exception) { ValidationMessage = exception.Message; }
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanSubmit));
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        _disposed = true;
        _store.Changed -= Notify;
        PropertyChanged -= OnCriteriaChanged;
        _request?.Cancel();
        Nodes.Clear(); Commands.Clear(); Invocations.Clear();
        SelectedInvocation = null; SelectedNode = null; SelectedCommand = null;
        _selection = []; InvalidatePlan();
    }
}