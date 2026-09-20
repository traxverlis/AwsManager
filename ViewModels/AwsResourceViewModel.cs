using AwsManager.Services;
using System.Collections;
using System.ComponentModel;
using System.Windows.Data;

namespace AwsManager.ViewModels;

public abstract class AwsResourceViewModel : ViewModelBase
{
    protected AwsContext? Context { get; }
    private IAwsClientFactory? _clientFactory;
    protected AwsResourceViewModel(IAwsClientFactory? clientFactory = null, AwsContext? context = null)
        => (_clientFactory, Context) = (clientFactory, context ?? (clientFactory as AwsClientFactory)?.Context ?? AwsSessionService.Current.Context);
    protected IAwsClientFactory ClientFactory => _clientFactory ??= new AwsClientFactory(Context);
    protected bool Allowed(string actions, string resource = "*", bool write = false)
        => Allowed(actions.Split(',').Select(action => new PermissionCheck(action, resource)), write);
    protected bool Allowed(IEnumerable<PermissionCheck> checks, bool write = false)
        => PermissionGate.For(Context)?.Allows(checks, write) ?? true;
    protected Task<bool> CheckAccessAsync(IEnumerable<PermissionCheck> checks, bool write = false)
        => PermissionGate.For(Context)?.CheckAsync(checks, write) ?? Task.FromResult(true);
    protected string Partition => Context?.Identity.StartsWith("arn:") == true ? Amazon.Arn.Parse(Context.Identity).Partition : "aws";
    protected string Arn(string service, string resource, bool global = false)
        => $"arn:{Partition}:{service}:{(global ? "" : Context?.Region)}:{Context?.Account}:{resource}";
    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) FilteredItems?.Refresh(); } }
    public ICollectionView? FilteredItems { get; private set; }
    private string _status = "";
    public string Status { get => _status; protected set => SetField(ref _status, value); }
    protected void ConfigureFilter<T>(IEnumerable items, Func<T, string> text)
    {
        FilteredItems = CollectionViewSource.GetDefaultView(items);
        FilteredItems.Filter = item => string.IsNullOrWhiteSpace(SearchText) ||
            item is T model && text(model).Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
    protected void ReportError(Exception exception)
    {
        AwsSessionService.Current.ReportError(exception, Context);
        Status = AwsSessionService.DescribeError(exception);
        NotificationService.Publish(Status);
    }
    protected bool Confirm(string message) => new NotificationService().Confirm(
        $"{message}\n\nProfil : {Context?.Profile}\nCompte : {Context?.Account}\nRegion : {Context?.Region}");
}