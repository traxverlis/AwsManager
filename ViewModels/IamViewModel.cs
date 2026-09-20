using AwsManager.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class IamViewModel : AwsResourceViewModel, IRefreshableViewModel, IDisposable
{
    public sealed class Category(string id, string name) : ViewModelBase
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Action => Id == "Profiles" ? "iam:ListInstanceProfiles" : $"iam:List{Id}";
        private bool _isEnabled = true;
        public bool IsEnabled { get => _isEnabled; internal set => SetField(ref _isEnabled, value); }
        private string _accessHint = "";
        public string AccessHint { get => _accessHint; internal set => SetField(ref _accessHint, value); }
    }
    private readonly IamService _service;
    private readonly PermissionGate? _permissions;
    private CancellationTokenSource? _request;
    private readonly HashSet<string> _markers = [];
    private string? _marker;
    private int _generation;
    private bool _disposed;
    private bool _isLoading;
    private string _details = "";
    private Category _category;
    private IamResource? _selectedResource;
    public IReadOnlyList<Category> Categories { get; } = [new("Roles", "Roles"), new("Profiles", "Profils EC2"), new("Policies", "Politiques"), new("Users", "Utilisateurs"), new("Groups", "Groupes")];
    public ObservableCollection<IamResource> Items { get; } = [];
    public Category SelectedCategory { get => _category; set { if (value.IsEnabled && SetField(ref _category, value)) _ = RefreshAsync(); } }
    public IamResource? SelectedResource { get => _selectedResource; set { if (SetField(ref _selectedResource, value)) { Details = ""; if (value != null) _ = ReadDetailsAsync(value); } } }
    public string Details { get => _details; private set => SetField(ref _details, value); }
    public string Scope => $"Compte {Context?.Account ?? ""} | IAM global | Consultation";
    public bool IsLoading { get => _isLoading; private set { SetField(ref _isLoading, value); OnPropertyChanged(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanEdit => !_disposed && !IsLoading;
    public ICommand RefreshCommand { get; }
    public ICommand MoreCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand CancelCommand { get; }

    public IamViewModel(IAwsClientFactory factory, AwsContext? context = null) : base(factory, context)
    {
        _service = new(factory);
        _permissions = PermissionGate.For(Context);
        _category = Categories[0];
        UpdateCategories();
        _category = Categories.FirstOrDefault(category => category.IsEnabled) ?? Categories[0];
        if (_permissions != null) _permissions.Changed += UpdateCategories;
        ConfigureFilter<IamResource>(Items, item => $"{item.Name} {item.Arn} {item.Path}");
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => CanEdit && Allowed(SelectedCategory.Action));
        MoreCommand = new AsyncRelayCommand(_ => LoadPageAsync(), _ => CanEdit && _marker != null && Allowed(SelectedCategory.Action));
        DetailsCommand = new AsyncRelayCommand(_ => ReadDetailsAsync(SelectedResource!), _ => CanEdit && SelectedResource != null && Allowed(DetailChecks(SelectedResource)));
        CancelCommand = new RelayCommand(_ => _request?.Cancel(), _ => IsLoading);
    }

    private void UpdateCategories()
    {
        foreach (var category in Categories)
        {
            category.IsEnabled = Allowed(category.Action);
            category.AccessHint = _permissions?.Explain([new(category.Action)]) ?? "";
        }
    }

    private static PermissionCheck[] DetailChecks(IamResource resource) => (resource.Kind switch
    {
        "Roles" => "GetRole,ListInstanceProfilesForRole,ListAttachedRolePolicies,ListRolePolicies,GetRolePolicy",
        "Users" => "GetUser,ListGroupsForUser,ListAttachedUserPolicies,ListUserPolicies,GetUserPolicy",
        "Groups" => "GetGroup,ListAttachedGroupPolicies,ListGroupPolicies,GetGroupPolicy",
        "Profiles" => "GetInstanceProfile",
        "Policies" => "GetPolicy,GetPolicyVersion,ListEntitiesForPolicy",
        _ => ""
    }).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(action => new PermissionCheck("iam:" + action, resource.Arn)).ToArray();

    public Task RefreshAsync()
    {
        _request?.Cancel();
        SelectedResource = null;
        Details = "";
        Items.Clear();
        _marker = null;
        _markers.Clear();
        return LoadPageAsync();
    }

    private Task LoadPageAsync() => RunAsync(async (token, generation) =>
    {
        if (!await CheckAccessAsync([new(SelectedCategory.Action)]) || !Current(token, generation))
        { Status = "Inventaire IAM non autorise ou non verifie."; return; }
        var page = await _service.ListAsync(SelectedCategory.Id, _marker, token);
        if (!Current(token, generation)) return;
        foreach (var item in page.Items) if (!Items.Any(existing => existing.Arn == item.Arn)) Items.Add(item);
        var repeated = page.NextToken != null && !_markers.Add(page.NextToken);
        var limited = Items.Count >= 10000 && page.NextToken != null;
        _marker = repeated || limited ? null : page.NextToken;
        Status = $"{Items.Count} ressource(s) chargee(s). " + (repeated || limited ? "Lecture interrompue : liste incomplete." : _marker != null ? "Suite disponible ; recherche sur les lignes chargees." : "Inventaire termine.");
    });

    private Task ReadDetailsAsync(IamResource resource) => RunAsync(async (token, generation) =>
    {
        Details = "";
        if (!await CheckAccessAsync(DetailChecks(resource)) || !Current(token, generation))
        { Status = "Details IAM non autorises ou non verifies."; return; }
        var detail = await _service.DetailsAsync(resource, token);
        if (!Current(token, generation) || SelectedResource != resource) return;
        Details = detail;
        Status = "Permissions declarees ; les SCP, limites, politiques de ressources et conditions peuvent restreindre les droits effectifs.";
    });

    private bool Current(CancellationToken token, int generation) => !_disposed && !token.IsCancellationRequested && generation == _generation;
    private async Task RunAsync(Func<CancellationToken, int, Task> action)
    {
        if (_disposed) return;
        _request?.Cancel();
        var generation = ++_generation;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _request = request;
        IsLoading = true;
        Status = "Lecture IAM...";
        try { await action(request.Token, generation); }
        catch (Exception exception) { if (!_disposed && generation == _generation) { if (exception is OperationCanceledException) Status = "Lecture annulee ou delai depasse."; else ReportError(exception); } }
        finally { if (generation == _generation) { _request = null; IsLoading = false; } }
    }

    public void Dispose()
    {
        if (_permissions != null) _permissions.Changed -= UpdateCategories;
        _disposed = true;
        _generation++;
        _request?.Cancel();
        Items.Clear();
        SelectedResource = null;
        Details = "";
    }
}