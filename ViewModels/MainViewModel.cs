using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows;
using Amazon.Runtime.CredentialManagement;
using AwsManager.Views.Dialogs;
using AwsManager.Services;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        public sealed class Page(string name, string icon, Func<ViewModelBase> create) : ViewModelBase
        {
            public string Name { get; } = name;
            public string Icon { get; } = icon;
            public Func<ViewModelBase> Create { get; } = create;
            private bool _isEnabled;
            public bool IsEnabled { get => _isEnabled; internal set => SetField(ref _isEnabled, value); }
            private string _accessHint = "Connexion AWS requise.";
            public string AccessHint { get => _accessHint; internal set => SetField(ref _accessHint, value); }
        }
        private readonly Dictionary<Page, ViewModelBase> _pages = [];
        private readonly AwsSessionService _session;
        private readonly WorkspaceStore _store;
        private readonly Func<IAwsClientFactory> _factory;
        private PermissionGate? _permissions;
        public Task PermissionsReady { get; private set; } = Task.CompletedTask;
        public string PermissionStatus => _permissions?.Status ?? "Droits IAM : connexion requise.";
        public string ConnectionPrompt => Session.IsConnected ? "Acces AWS non verifie ou refuse" : "Connexion AWS requise";
        public ICommand RefreshPermissionsCommand { get; }
        private readonly Dictionary<Page, Task> _pageLoads = [];
        private ResourceReference? _pendingResource;
        private SavedConnection? _pendingConnection;
        public ResourceReference? SelectedResource => ResourceNavigation.Capture(CurrentViewModel, Session.Context);
        public bool IsFavorite => SelectedResource is { } resource && _store.Snapshot.Favorites.Any(item => item.Matches(resource));
        public bool IsReadOnly
        {
            get => _store.Snapshot.IsReadOnly;
            set
            {
                try { _store.SetReadOnly(value); }
                catch (Exception exception) { Notification = AwsSessionService.DescribeError(exception); OnPropertyChanged(nameof(IsReadOnly)); }
            }
        }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand ShowRelationsCommand { get; }
        public ICommand ShowMonitoringCommand { get; }
        public AwsSessionService Session => _session;
        public ObservableCollection<AwsProfile> AwsProfiles { get; } = [];
        public IReadOnlyList<string> Regions { get; } = Amazon.RegionEndpoint.EnumerableAllRegions.Select(region => region.SystemName).Order().ToArray();
        public IReadOnlyList<Page> Pages { get; }
        private Page? _selectedPage;
        public Page? SelectedPage { get => _selectedPage; set { if (value?.IsEnabled == false && Session.IsConnected) return; if (SetField(ref _selectedPage, value)) ActivatePage(); } }
        private ViewModelBase? _currentViewModel;
        public ViewModelBase? CurrentViewModel
        {
            get => _currentViewModel;
            private set
            {
                if (_currentViewModel != null) _currentViewModel.PropertyChanged -= OnResourceChanged;
                SetField(ref _currentViewModel, value);
                if (_currentViewModel != null) _currentViewModel.PropertyChanged += OnResourceChanged;
                OnPropertyChanged(nameof(ShowConnection));
                OnStoreChanged();
            }
        }
        public bool ShowConnection => CurrentViewModel == null;
        private AwsProfile? _selectedAwsProfile;
        public AwsProfile? SelectedAwsProfile
        {
            get => _selectedAwsProfile;
            set
            {
                if (!SetField(ref _selectedAwsProfile, value)) return;
                SelectedRegion = value?.Region ?? "eu-west-1";
                Session.Invalidate();
                OnPropertyChanged(nameof(CanLogin));
            }
        }
        private string _selectedRegion = "eu-west-1";
        public string SelectedRegion { get => _selectedRegion; set { if (SetField(ref _selectedRegion, value)) Session.Invalidate(); } }
        private string _notification = "";
        public string Notification { get => _notification; set => SetField(ref _notification, value); }
        private bool _isConnectionExpanded = true;
        public bool IsConnectionExpanded
        {
            get => _isConnectionExpanded;
            private set
            {
                if (SetField(ref _isConnectionExpanded, value)) OnPropertyChanged(nameof(IsConnectionCollapsed));
            }
        }
        public bool IsConnectionCollapsed => !IsConnectionExpanded;
        public bool CanLogin => SelectedAwsProfile?.IsSso == true && !Session.IsBusy;
        public ICommand ToggleConnectionPanelCommand { get; }
        public ICommand LoginCommand { get; }
        public ICommand VerifyCommand { get; }
        public ICommand CancelLoginCommand { get; }
        public ICommand ReloadProfilesCommand { get; }
        public ICommand DismissNotificationCommand { get; }
        public ICommand QuitCommand { get; }
        public ICommand HelpCommand { get; }

        public MainViewModel() : this(AwsSessionService.Current) { }
        public MainViewModel(AwsSessionService session, WorkspaceStore? store = null, Func<IAwsClientFactory>? clientFactory = null)
        {
            _session = session;
            _store = store ?? WorkspaceStore.Current;
            IAwsClientFactory Factory() => clientFactory?.Invoke() ?? new AwsClientFactory(Session.Context, Session, _store);
            _factory = Factory;
            RefreshPermissionsCommand = new AsyncRelayCommand(async _ => { OnContextChanged(); await PermissionsReady; }, _ => Session.IsConnected && !Session.IsBusy && PermissionsReady.IsCompleted);
            Pages =
            [
                new("EC2", "Server", () => new Ec2ViewModel(Factory(), false, Session.Context)),
                new("S3", "FolderOutline", () => new S3ViewModel(Factory(), false, context: Session.Context)),
                new("RDS", "Database", () => new RdsViewModel(Factory(), false, Session.Context)),
                new("Auto Scaling", "ChartLine", () => new AutoScalingViewModel(Factory(), false, Session.Context)),
                new("Groupes de sécurité", "ShieldOutline", () => new SecurityGroupViewModel(Factory(), false, context: Session.Context)),
                new("IAM", "ShieldAccount", () => new IamViewModel(Factory(), Session.Context)),
                new("Correctifs SSM", "Update", () => new PatchViewModel(Factory(), _store, Session.Context)),
                new("Route 53", "Dns", () => new Route53ViewModel(Factory(), Session.Context)),
                new("Logs", "TextBoxSearchOutline", () => new CloudWatchLogsViewModel(Factory(), Session.Context, Session)),
                new("Sessions", "Console", () => new LiveSessionsViewModel()),
                new("Mes accès", "StarOutline", () => new WorkspaceViewModel(_store, OpenResourceAsync, OpenConnectionAsync))
            ];
            ToggleFavoriteCommand = new AsyncRelayCommand(_ => { _store.ToggleFavorite(SelectedResource!); return Task.CompletedTask; }, _ => SelectedResource != null);
            ShowRelationsCommand = new RelayCommand(_ => ShowInspector(false), _ => SelectedResource?.Service is "EC2" or "RDS" or "Auto Scaling" or "Groupes de sécurité" &&
                _permissions?.Allows(ResourceInspectorViewModel.RelationChecks(SelectedResource.Service)) == true);
            ShowMonitoringCommand = new RelayCommand(_ => ShowInspector(true), _ => SelectedResource?.Service is "EC2" or "RDS" &&
                _permissions != null && (_permissions.Allows([new("cloudwatch:GetMetricData")]) | _permissions.Allows([new("cloudwatch:DescribeAlarms")])));
            _store.Changed += OnStoreChanged;
            ToggleConnectionPanelCommand = new RelayCommand(_ => IsConnectionExpanded = !IsConnectionExpanded, _ => Session.IsConnected && !Session.IsBusy);
            LoginCommand = new AsyncRelayCommand(async _ => await Session.ConnectAsync(SelectedAwsProfile!.Name, SelectedRegion, true), _ => CanLogin);
            VerifyCommand = new AsyncRelayCommand(async _ => await Session.ConnectAsync(SelectedAwsProfile!.Name, SelectedRegion, false), _ => SelectedAwsProfile != null && !Session.IsBusy);
            CancelLoginCommand = new RelayCommand(_ => Session.Cancel(), _ => Session.IsBusy);
            ReloadProfilesCommand = new RelayCommand(_ => LoadProfiles(), _ => !Session.IsBusy);
            DismissNotificationCommand = new RelayCommand(_ => Notification = "");
            QuitCommand = new RelayCommand(_ => Application.Current.MainWindow?.Close());
            HelpCommand = new RelayCommand(Help);
            NotificationService.Published += OnNotification;
            Session.ContextChanged += OnContextChanged;
            Session.PropertyChanged += OnSessionChanged;
            OnPermissionsChanged();
            LoadProfiles();
            SelectedPage = Pages[0];
        }

        private void OnNotification(string message) => Notification = message;
        private void OnContextChanged()
        {
            if (_permissions != null) { _permissions.Changed -= OnPermissionsChanged; _permissions.Dispose(); }
            _permissions = Session.Context is { } context ? new PermissionGate(context, _factory(), _store) : null;
            if (_permissions != null) _permissions.Changed += OnPermissionsChanged;
            IsConnectionExpanded = !Session.IsConnected;
            foreach (var page in _pages.Values.OfType<IDisposable>()) page.Dispose();
            _pages.Clear();
            _pageLoads.Clear();
            OnPermissionsChanged();
            ActivatePage();
            PermissionsReady = Session.Context is { } active ? LoadPermissionsAsync(active) : Task.CompletedTask;
        }

        private static PermissionCheck[] PageChecks(string name) => (name switch
        {
            "EC2" => "ec2:DescribeInstances",
            "S3" => "s3:ListAllMyBuckets",
            "RDS" => "rds:DescribeDBInstances",
            "Auto Scaling" => "autoscaling:DescribeAutoScalingGroups",
            "Groupes de sécurité" => "ec2:DescribeSecurityGroups,ec2:DescribeSecurityGroupRules",
            "IAM" => "iam:ListRoles,iam:ListInstanceProfiles,iam:ListPolicies,iam:ListUsers,iam:ListGroups",
            "Correctifs SSM" => "ssm:DescribeInstanceInformation,ssm:DescribeInstancePatchStates",
            "Route 53" => "route53:ListHostedZones",
            "Logs" => "logs:DescribeLogGroups",
            _ => ""
        }).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(action => new PermissionCheck(action)).ToArray();

        private void OnPermissionsChanged()
        {
            foreach (var page in Pages)
            {
                var checks = PageChecks(page.Name);
                page.IsEnabled = checks.Length == 0 || (_permissions != null && (page.Name == "IAM"
                    ? checks.Select(check => _permissions.Allows([check])).ToArray().Any(allowed => allowed)
                    : _permissions.Allows(checks)));
                page.AccessHint = checks.Length == 0 ? "Fonction locale." : _permissions?.Explain(checks) ?? "Connexion AWS requise.";
            }
            OnPropertyChanged(nameof(PermissionStatus));
            OnPropertyChanged(nameof(ConnectionPrompt));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task LoadPermissionsAsync(AwsContext context)
        {
            var gate = _permissions!;
            await gate.CheckAsync(Pages.SelectMany(page => PageChecks(page.Name)));
            if (!ReferenceEquals(Session.Context, context) || !ReferenceEquals(_permissions, gate)) return;
            OnPermissionsChanged();
            if (SelectedPage?.IsEnabled != true)
                SelectedPage = Pages.FirstOrDefault(page => page.IsEnabled && PageChecks(page.Name).Length != 0);
            else ActivatePage();
            if (Session.IsConnected && _pendingResource is { } resource)
            {
                _pendingResource = null;
                var connection = _pendingConnection;
                _pendingConnection = null;
                if (ResourceNavigation.MatchesContext(resource, Session.Context))
                    _ = connection != null && connection.Target.Matches(resource) ? OpenConnectionAsync(connection) : OpenResourceAsync(resource);
                else Notification = "Le compte verifie ne correspond pas a cet acces. Ouverture annulee.";
            }
        }
        private void OnStoreChanged()
        {
            if (!string.IsNullOrEmpty(_store.Error)) Notification = _store.Error;
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(SelectedResource));
            CommandManager.InvalidateRequerySuggested();
        }
        private void OnResourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName?.StartsWith("Selected", StringComparison.Ordinal) != true) return;
            OnStoreChanged();
            if (SelectedResource is not { } resource) return;
            try { _store.Visit(resource); }
            catch (InvalidOperationException exception) { Notification = exception.Message; }
        }

        private void ShowInspector(bool monitoring)
        {
            if (SelectedResource is not { } resource || Session.Context is not { } context) return;
            var model = new ResourceInspectorViewModel(resource, new AwsClientFactory(context, Session, _store), OpenResourceAsync, monitoring, Session);
            new ResourceInspectorWindow { DataContext = model, Owner = Application.Current.MainWindow }.ShowDialog();
        }

        public async Task OpenConnectionAsync(SavedConnection connection)
        {
            try
            {
                if (!ResourceNavigation.MatchesContext(connection.Target, Session.Context))
                {
                    _pendingConnection = connection;
                    await OpenResourceAsync(connection.Target);
                    return;
                }
                await OpenResourceAsync(connection.Target);
                if (!ResourceNavigation.MatchesContext(connection.Target, Session.Context)) return;
                if (connection.Mode == "RDS" && CurrentViewModel is RdsViewModel { SelectedInstance: { } database } && database.DbInstanceIdentifier == connection.Target.Id)
                {
                    var model = new RdsTunnelViewModel(database, new AwsClientFactory(Session.Context, Session, _store), _store, Session.Context, session: Session);
                    model.ApplyPreset(connection);
                    new RdsTunnelWindow { DataContext = model, Owner = Application.Current.MainWindow }.ShowDialog();
                }
                else if (CurrentViewModel is Ec2ViewModel { SelectedInstance: { } instance } && instance.InstanceId == connection.Target.Id)
                {
                    var model = new SsmConnectionViewModel(instance, _store, Session.Context);
                    model.ApplyPreset(connection);
                    new SsmConnectionWindow { DataContext = model, Owner = Application.Current.MainWindow }.ShowDialog();
                }
            }
            catch (Exception exception) { Notification = AwsSessionService.DescribeError(exception); }
        }

        public async Task OpenResourceAsync(ResourceReference resource)
        {
            try
            {
                if (!ResourceNavigation.MatchesContext(resource, Session.Context))
                {
                    var profile = AwsProfiles.FirstOrDefault(item => item.Name == resource.Profile);
                    if (profile == null) throw new InvalidOperationException("Le profil de cet acces n'existe plus sur ce poste.");
                    SelectedAwsProfile = profile;
                    SelectedRegion = resource.Region;
                    Session.Invalidate();
                    _pendingResource = resource;
                    Notification = $"Verifiez la connexion au compte {resource.Account} pour ouvrir {resource.Name}.";
                    return;
                }
                var context = Session.Context;
                _pendingResource = null;
                _pendingConnection = null;
                var page = Pages.FirstOrDefault(item => item.Name == resource.Service) ?? throw new InvalidOperationException("Service non pris en charge.");
                if (!page.IsEnabled) { Notification = page.AccessHint; return; }
                SelectedPage = page;
                if (_pageLoads.TryGetValue(page, out var loading)) await loading;
                if (!ReferenceEquals(context, Session.Context) || SelectedPage != page) return;
                if (CurrentViewModel is AwsResourceViewModel model && await ResourceNavigation.SelectAsync(model, resource))
                    _store.Visit(resource);
                else Notification = "Ressource absente de l'inventaire ou inaccessible. Verifiez les autorisations puis actualisez.";
            }
            catch (Exception exception) { Notification = AwsSessionService.DescribeError(exception); }
        }
        private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            OnPropertyChanged(nameof(CanLogin));
            CommandManager.InvalidateRequerySuggested();
        }
        public void Dispose()
        {
            if (_permissions != null) { _permissions.Changed -= OnPermissionsChanged; _permissions.Dispose(); _permissions = null; }
            _store.Changed -= OnStoreChanged;
            if (_currentViewModel != null) _currentViewModel.PropertyChanged -= OnResourceChanged;
            foreach (var page in _pages.Values.OfType<IDisposable>()) page.Dispose();
            NotificationService.Published -= OnNotification;
            Session.ContextChanged -= OnContextChanged;
            Session.PropertyChanged -= OnSessionChanged;
            Session.Cancel();
        }

        private void ActivatePage()
        {
            CurrentViewModel = null;
            if (SelectedPage == null || !SelectedPage.IsEnabled || (!Session.IsConnected && SelectedPage.Name is not ("Sessions" or "Mes accès"))) return;
            if (!_pages.TryGetValue(SelectedPage, out var page))
            {
                page = SelectedPage.Create();
                _pages[SelectedPage] = page;
                if (page is IRefreshableViewModel { RefreshCommand: AsyncRelayCommand refresh })
                    _pageLoads[SelectedPage] = refresh.ExecuteAsync(null);
            }
            CurrentViewModel = page;
        }

        private void LoadProfiles()
        {
            try
            {
                var previous = SelectedAwsProfile?.Name;
                AwsProfiles.Clear();
                foreach (var profile in Session.ListProfiles()) AwsProfiles.Add(profile);
                SelectedAwsProfile = AwsProfiles.FirstOrDefault(profile => profile.Name == previous) ?? AwsProfiles.FirstOrDefault();
                if (AwsProfiles.Count == 0) Notification = "Aucun profil AWS configuré sur ce poste.";
            }
            catch (Exception exception) { Notification = AwsSessionService.DescribeError(exception); }
        }

        private void Help(object? parameter)
        {
                        var helpWindow = new HelpWindow(parameter as string)
            {
                Owner = Application.Current.MainWindow
            };

            helpWindow.Show();
        }
    }
}
