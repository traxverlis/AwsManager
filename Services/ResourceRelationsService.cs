using AwsManager.Models;

namespace AwsManager.Services;

public sealed class ResourceRelationsService(IAwsClientFactory factory)
{
    public async Task<IReadOnlyList<ResourceReference>> LoadAsync(ResourceReference resource, CancellationToken cancellationToken)
    {
        var result = new List<ResourceReference>();
        void Add(string service, string id, string? name = null) => result.Add(resource with { Service = service, Id = id, Name = string.IsNullOrEmpty(name) ? id : name, ParentId = "" });
        switch (resource.Service)
        {
            case "EC2":
                using (var client = factory.CreateEc2Client())
                {
                    var response = await client.DescribeInstancesAsync(new Amazon.EC2.Model.DescribeInstancesRequest { InstanceIds = [resource.Id] }, cancellationToken);
                    foreach (var instance in (response.Reservations ?? []).SelectMany(reservation => reservation.Instances ?? []))
                    {
                        foreach (var group in instance.SecurityGroups ?? []) Add("Groupes de sécurité", group.GroupId, group.GroupName);
                        foreach (var tag in (instance.Tags ?? []).Where(tag => tag.Key == "aws:autoscaling:groupName")) Add("Auto Scaling", tag.Value);
                    }
                }
                break;
            case "RDS":
                using (var client = factory.CreateRdsClient())
                {
                    var response = await client.DescribeDBInstancesAsync(new Amazon.RDS.Model.DescribeDBInstancesRequest { DBInstanceIdentifier = resource.Id }, cancellationToken);
                    foreach (var instance in response.DBInstances ?? [])
                        foreach (var group in instance.VpcSecurityGroups ?? []) Add("Groupes de sécurité", group.VpcSecurityGroupId);
                }
                break;
            case "Auto Scaling":
                using (var client = factory.CreateAutoScalingClient())
                {
                    var response = await client.DescribeAutoScalingGroupsAsync(new Amazon.AutoScaling.Model.DescribeAutoScalingGroupsRequest { AutoScalingGroupNames = [resource.Id] }, cancellationToken);
                    foreach (var group in response.AutoScalingGroups ?? [])
                        foreach (var instance in group.Instances ?? []) Add("EC2", instance.InstanceId);
                }
                break;
            case "Groupes de sécurité":
                using (var client = factory.CreateEc2Client())
                {
                    var request = new Amazon.EC2.Model.DescribeInstancesRequest
                    { Filters = [new Amazon.EC2.Model.Filter("instance.group-id", [resource.Id])] };
                    do
                    {
                        var response = await client.DescribeInstancesAsync(request, cancellationToken);
                        foreach (var instance in (response.Reservations ?? []).SelectMany(reservation => reservation.Instances ?? []))
                            Add("EC2", instance.InstanceId, instance.Tags?.FirstOrDefault(tag => tag.Key == "Name")?.Value);
                        request.NextToken = response.NextToken;
                    } while (!string.IsNullOrEmpty(request.NextToken));
                }
                break;
            default: throw new ArgumentException("Relations non disponibles pour ce service.");
        }
        return result.DistinctBy(item => (item.Service, item.Id)).OrderBy(item => item.Service).ThenBy(item => item.Name).ToArray();
    }
}