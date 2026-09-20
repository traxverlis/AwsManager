using AwsManager.Models;
using System.IO;
using System.Text.Json;

namespace AwsManager.Services;

public sealed class WorkspaceStore
{
    public static WorkspaceStore Current { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AwsManager", "workspace.json"));
    private readonly object _gate = new();
    private readonly string _path;
    private WorkspaceData _data = new();
    private bool _canSave = true;
    public string Error { get; private set; } = "";
    public event Action? Changed;
    public WorkspaceData Snapshot { get { lock (_gate) return _data; } }

    public WorkspaceStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<WorkspaceData>(File.ReadAllText(path)) ?? throw new JsonException();
            if (data.Version != 1 || data.Favorites == null || data.Recent == null || data.Connections == null || data.History == null ||
                data.Favorites.Any(item => item == null) || data.Recent.Any(item => item == null) ||
                data.Connections.Any(item => item == null || item.Target == null || item.Tunnels == null) || data.History.Any(item => item == null))
                throw new JsonException();
            _data = data;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _canSave = false;
            Error = "Stockage local illisible ou incompatible. Le fichier est conserve ; lecture seule activee.";
        }
    }

    public void SetReadOnly(bool value) => Update(data => data with { IsReadOnly = value });
    public void ToggleFavorite(ResourceReference resource) => Update(data => data with
    {
        Favorites = data.Favorites.Any(item => item.Matches(resource))
            ? data.Favorites.Where(item => !item.Matches(resource)).ToArray()
            : data.Favorites.Append(resource).TakeLast(200).ToArray()
    });
    public void Visit(ResourceReference resource) => Update(data => data with
    {
        Recent = new[] { resource }.Concat(data.Recent.Where(item => !item.Matches(resource))).Take(30).ToArray()
    });
    public void SaveConnection(SavedConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 100 ||
            connection.Mode is not ("Terminal" or "RDP" or "Tunnels" or "RDS")) throw new ArgumentException("Nom ou type de connexion invalide.");
        if (connection.Mode == "RDS")
        {
            if (connection.Target.Service != "RDS" || connection.Tunnels.Length != 1 ||
                !System.Text.RegularExpressions.Regex.IsMatch(connection.RelayInstanceId ?? "", "^i-[0-9a-f]+$"))
                throw new ArgumentException("Une connexion RDS exige une base, un relais EC2 et un tunnel.");
        }
        else if (connection.Target.Service != "EC2") throw new ArgumentException("Cette connexion exige une instance EC2.");
        if (connection.Mode != "Terminal") SsmService.ValidatePorts(connection.Tunnels.Select(tunnel => (tunnel.RemotePort, tunnel.LocalPort)));
        if (connection.Mode == "RDP" && connection.Tunnels.Length != 1) throw new ArgumentException("RDP attend un seul tunnel.");
        Update(data => data with { Connections = data.Connections.Where(item => item.Id != connection.Id).Append(connection).TakeLast(200).ToArray() });
    }
    public void RemoveConnection(Guid id) => Update(data => data with { Connections = data.Connections.Where(item => item.Id != id).ToArray() });
    public void ClearHistory() => Update(data => data with { History = [] });
    public void ClearRecent() => Update(data => data with { Recent = [] });
    public void Record(ActivityEntry entry)
    {
        try { Update(data => data with { History = new[] { entry }.Concat(data.History).Take(300).ToArray() }); }
        catch (InvalidOperationException) { NotifyChanged(); }
    }

    private void Update(Func<WorkspaceData, WorkspaceData> update)
    {
        lock (_gate)
        {
            if (!_canSave) throw new InvalidOperationException(Error);
            var next = update(_data);
            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, true);
                _data = next;
                Error = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Error = "Impossible d'enregistrer les preferences locales.";
                throw new InvalidOperationException(Error, exception);
            }
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(() => Changed?.Invoke());
        else Changed?.Invoke();
    }
}