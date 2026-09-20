using AwsManager.Models;
using AwsManager.Services;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class WorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceStore _store;
    public IReadOnlyList<ResourceReference> Favorites => _store.Snapshot.Favorites;
    public IReadOnlyList<ResourceReference> Recent => _store.Snapshot.Recent;
    public IReadOnlyList<SavedConnection> Connections => _store.Snapshot.Connections;
    public IReadOnlyList<ActivityEntry> History => _store.Snapshot.History;
    public string StorageError => _store.Error;
    public ICommand OpenCommand { get; }
    public ICommand RemoveFavoriteCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ClearRecentCommand { get; }
    public ICommand OpenConnectionCommand { get; }
    public ICommand RemoveConnectionCommand { get; }

    public WorkspaceViewModel(WorkspaceStore store, Func<ResourceReference, Task> open, Func<SavedConnection, Task> connect)
    {
        _store = store;
        OpenConnectionCommand = new AsyncRelayCommand(item => item is SavedConnection connection ? connect(connection) : Task.CompletedTask);
        RemoveConnectionCommand = new AsyncRelayCommand(item => { if (item is SavedConnection connection) _store.RemoveConnection(connection.Id); return Task.CompletedTask; });
        OpenCommand = new AsyncRelayCommand(item => item is ResourceReference resource ? open(resource) : Task.CompletedTask);
        RemoveFavoriteCommand = new AsyncRelayCommand(item => { if (item is ResourceReference resource) _store.ToggleFavorite(resource); return Task.CompletedTask; });
        ClearHistoryCommand = new AsyncRelayCommand(_ => { _store.ClearHistory(); return Task.CompletedTask; });
        ClearRecentCommand = new AsyncRelayCommand(_ => { _store.ClearRecent(); return Task.CompletedTask; });
        _store.Changed += Refresh;
    }
    private void Refresh()
    {
        OnPropertyChanged(nameof(Favorites));
        OnPropertyChanged(nameof(Recent));
        OnPropertyChanged(nameof(Connections));
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(StorageError));
    }
    public void Dispose() => _store.Changed -= Refresh;
}