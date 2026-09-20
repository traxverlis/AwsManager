using AwsManager.Models;

namespace AwsManager.Services;

public sealed record RdsTunnelTarget(string Id, string Host, int Port, string State, string VpcId);
public sealed record SsmRelay(string Id, string Name, string VpcId, string AgentVersion)
{
    public string DisplayName => $"{Name} ({Id}) | {VpcId}";
}

public interface IRdsTunnelBackend
{
    Task<RdsTunnelTarget> LoadTargetAsync(string databaseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SsmRelay>> LoadRelaysAsync(CancellationToken cancellationToken);
    Task<TrackedSessionModel> OpenAsync(RdsTunnelTarget target, SsmRelay relay, int localPort, CancellationToken cancellationToken);
    void Stop(TrackedSessionModel session);
}

public sealed class RdsTunnelBackend(IAwsClientFactory factory, AwsContext context) : IRdsTunnelBackend
{
    public static bool IsCompatibleAgent(string? version) => Version.TryParse(version, out var parsed) && parsed >= new Version(3, 1, 1374, 0);

    public async Task<RdsTunnelTarget> LoadTargetAsync(string databaseId, CancellationToken cancellationToken)
    {
        using var client = factory.CreateRdsClient();
        var response = await client.DescribeDBInstancesAsync(new Amazon.RDS.Model.DescribeDBInstancesRequest { DBInstanceIdentifier = databaseId }, cancellationToken);
        var database = response.DBInstances?.SingleOrDefault(item => item.DBInstanceIdentifier == databaseId)
            ?? throw new InvalidOperationException("Base RDS introuvable ou inaccessible.");
        if (database.Endpoint?.Address is not { Length: > 0 } host || database.Endpoint.Port is not (>= 1 and <= 65535))
            throw new InvalidOperationException("La base RDS ne fournit pas encore d'adresse et de port utilisables.");
        SsmService.ValidateRemoteHost(host);
        return new(databaseId, host, database.Endpoint.Port.Value, database.DBInstanceStatus, database.DBSubnetGroup?.VpcId ?? "");
    }

    public async Task<IReadOnlyList<SsmRelay>> LoadRelaysAsync(CancellationToken cancellationToken)
    {
        using var ssm = factory.CreateSsmClient();
        var managed = new Dictionary<string, string>();
        var request = new Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationRequest
        { Filters = [new() { Key = "PingStatus", Values = ["Online"] }] };
        do
        {
            var response = await ssm.DescribeInstanceInformationAsync(request, cancellationToken);
            foreach (var instance in response.InstanceInformationList ?? [])
                if (instance.PingStatus == "Online" && instance.InstanceId?.StartsWith("i-", StringComparison.Ordinal) == true && IsCompatibleAgent(instance.AgentVersion))
                    managed[instance.InstanceId] = instance.AgentVersion;
            request.NextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        using var ec2 = factory.CreateEc2Client();
        var instances = new Amazon.EC2.Model.DescribeInstancesRequest
        { Filters = [new("instance-state-name", ["running"])] };
        var relays = new List<SsmRelay>();
        do
        {
            var response = await ec2.DescribeInstancesAsync(instances, cancellationToken);
            foreach (var instance in (response.Reservations ?? []).SelectMany(reservation => reservation.Instances ?? []))
                if (instance.State?.Name == "running" && managed.TryGetValue(instance.InstanceId, out var agent))
                    relays.Add(new(instance.InstanceId, instance.Tags?.FirstOrDefault(tag => tag.Key == "Name")?.Value ?? instance.InstanceId, instance.VpcId ?? "", agent));
            instances.NextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(instances.NextToken));
        return relays.OrderBy(relay => relay.Name).ToArray();
    }

    public async Task<TrackedSessionModel> OpenAsync(RdsTunnelTarget target, SsmRelay relay, int localPort, CancellationToken cancellationToken)
    {
        var current = await LoadTargetAsync(target.Id, cancellationToken);
        if (current.State != "available") throw new InvalidOperationException("La base RDS n'est pas disponible.");
        if (current.Host != target.Host || current.Port != target.Port)
            throw new InvalidOperationException("L'adresse ou le port RDS a change. Actualisez la configuration avant de vous connecter.");
        using var ec2 = factory.CreateEc2Client();
        var instances = await ec2.DescribeInstancesAsync(new Amazon.EC2.Model.DescribeInstancesRequest { InstanceIds = [relay.Id] }, cancellationToken);
        if (!(instances.Reservations ?? []).SelectMany(reservation => reservation.Instances ?? []).Any(instance => instance.InstanceId == relay.Id && instance.State?.Name == "running"))
            throw new InvalidOperationException("Le relais EC2 n'est plus en cours d'execution.");
        using var ssm = factory.CreateSsmClient();
        var information = await ssm.DescribeInstanceInformationAsync(new Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationRequest
        { Filters = [new() { Key = "InstanceIds", Values = [relay.Id] }] }, cancellationToken);
        if (!(information.InstanceInformationList ?? []).Any(instance => instance.InstanceId == relay.Id && instance.PingStatus == "Online" && IsCompatibleAgent(instance.AgentVersion)))
            throw new InvalidOperationException("Le relais doit etre en ligne dans SSM avec un agent 3.1.1374.0 ou superieur.");
        cancellationToken.ThrowIfCancellationRequested();
        return await SsmService.StartTunnelAsync(context, relay.Id, current.Port, localPort, cancellationToken, current.Host);
    }

    public void Stop(TrackedSessionModel session) => SessionTrackingService.Instance.KillSession(session);
}