using AwsManager.Models;
using AwsManager.ViewModels;

namespace AwsManager.Services;

public static class ResourceNavigation
{
    public static bool MatchesContext(ResourceReference resource, AwsContext? context) => context != null &&
        resource.Profile == context.Profile && resource.Account == context.Account && resource.Region == context.Region;

    public static ResourceReference? Capture(ViewModelBase? model, AwsContext? context)
    {
        if (context == null) return null;
        ResourceReference Reference(string service, string id, string name, string parent = "") => new(context.Profile, context.Account, context.Region, service, id, name, parent);
        return model switch
        {
            Ec2ViewModel { SelectedInstance: { } item } => Reference("EC2", item.InstanceId, string.IsNullOrEmpty(item.Name) ? item.InstanceId : item.Name),
            RdsViewModel { SelectedInstance: { } item } => Reference("RDS", item.DbInstanceIdentifier, item.DbInstanceIdentifier),
            AutoScalingViewModel { SelectedGroup: { } item } => Reference("Auto Scaling", item.AutoScalingGroupName, item.AutoScalingGroupName),
            SecurityGroupViewModel { SelectedSecurityGroup: { } item } => Reference("Groupes de sécurité", item.GroupId, item.GroupName),
            S3ViewModel { SelectedFile: { ItemType: "Bucket" } item } => Reference("S3", item.Name, item.Name),
            S3ViewModel { SelectedFile: { ItemType: "File" or "Folder" } item } source => Reference("S3", item.Key, item.Name, source.CurrentBucket),
            Route53ViewModel { SelectedHostedZone: { } zone, SelectedRecordSet: { } item } => Reference("Route 53", RecordId(item), item.Name, zone.Id),
            Route53ViewModel { SelectedHostedZone: { } zone } => Reference("Route 53", zone.Id, zone.Name),
            _ => null
        };
    }

    public static string RecordId(ResourceRecordSetModel item) => $"{item.Name}|{item.Type}|{item.Original?.SetIdentifier}";

    public static async Task<bool> SelectAsync(AwsResourceViewModel model, ResourceReference resource)
    {
        if (model is IRefreshableViewModel { RefreshCommand: AsyncRelayCommand { IsExecuting: true } })
            throw new InvalidOperationException("Attendez la fin du chargement puis rouvrez cette ressource.");
        model.SearchText = "";
        switch (model)
        {
            case Ec2ViewModel source:
                source.SelectedInstance = source.Instances.FirstOrDefault(item => item.InstanceId == resource.Id);
                return source.SelectedInstance != null;
            case RdsViewModel source:
                source.SelectedInstance = source.Instances.FirstOrDefault(item => item.DbInstanceIdentifier == resource.Id);
                return source.SelectedInstance != null;
            case AutoScalingViewModel source:
                source.SelectedGroup = source.AutoScalingGroups.FirstOrDefault(item => item.AutoScalingGroupName == resource.Id);
                return source.SelectedGroup != null;
            case SecurityGroupViewModel source:
                source.SelectedSecurityGroup = source.SecurityGroups.FirstOrDefault(item => item.GroupId == resource.Id);
                return source.SelectedSecurityGroup != null;
            case S3ViewModel source: return await source.SelectReferenceAsync(resource);
            case Route53ViewModel source: return await source.SelectReferenceAsync(resource);
            default: return false;
        }
    }
}