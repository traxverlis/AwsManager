namespace AwsManager.Models;

public sealed record ResourceReference(string Profile, string Account, string Region, string Service, string Id, string Name, string ParentId = "")
{
    public bool Matches(ResourceReference other) => Profile == other.Profile && Account == other.Account &&
        Region == other.Region && Service == other.Service && Id == other.Id && ParentId == other.ParentId;
}

public sealed record SavedTunnel(int RemotePort, int LocalPort);
public sealed record SavedConnection(Guid Id, string Name, ResourceReference Target, string Mode, SavedTunnel[] Tunnels, string RelayInstanceId = "");
public sealed record ActivityEntry(DateTimeOffset Time, string Profile, string Account, string Region, string Service, string Operation, string Resource, string Result);

public sealed record WorkspaceData
{
    public int Version { get; init; } = 1;
    public bool IsReadOnly { get; init; } = true;
    public ResourceReference[] Favorites { get; init; } = [];
    public ResourceReference[] Recent { get; init; } = [];
    public SavedConnection[] Connections { get; init; } = [];
    public ActivityEntry[] History { get; init; } = [];
}