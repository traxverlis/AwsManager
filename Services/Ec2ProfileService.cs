using Amazon.EC2.Model;

namespace AwsManager.Services;

public sealed record Ec2ProfileState(string InstanceId, string InstanceState, string AssociationId, string ProfileArn, string AssociationState);

public sealed class Ec2ProfileService(IAwsClientFactory factory)
{
    public async Task<Ec2ProfileState> ReadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        using var client = factory.CreateEc2Client();
        var instances = await client.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = [instanceId] }, cancellationToken);
        var instance = (instances.Reservations ?? []).SelectMany(reservation => reservation.Instances ?? []).SingleOrDefault(item => item.InstanceId == instanceId)
            ?? throw new InvalidOperationException("Instance introuvable dans le contexte courant.");
        var associations = new List<IamInstanceProfileAssociation>();
        string? token = null;
        var tokens = new HashSet<string>();
        do
        {
            var page = await client.DescribeIamInstanceProfileAssociationsAsync(new DescribeIamInstanceProfileAssociationsRequest
            { Filters = [new() { Name = "instance-id", Values = [instanceId] }], NextToken = token }, cancellationToken);
            associations.AddRange((page.IamInstanceProfileAssociations ?? []).Where(item => item.State?.Value != "disassociated"));
            token = page.NextToken;
            if (!string.IsNullOrEmpty(token) && !tokens.Add(token)) throw new InvalidOperationException("Pagination EC2 repetee. Actualisez.");
        } while (!string.IsNullOrEmpty(token));
        if (associations.Count > 1) throw new InvalidOperationException("Plusieurs associations IAM en transition. Actualisez avant de continuer.");
        var association = associations.SingleOrDefault();
        if (association == null && instance.IamInstanceProfile != null)
            throw new InvalidOperationException("Association IAM en cours de propagation. Actualisez avant de continuer.");
        return new(instanceId, instance.State?.Name?.Value ?? "unknown", association?.AssociationId ?? "",
            association?.IamInstanceProfile?.Arn ?? "", association?.State?.Value ?? "");
    }

    public async Task<string> ChangeAsync(Ec2ProfileState expected, string profileArn, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileArn) || !profileArn.Contains(":instance-profile/", StringComparison.Ordinal))
            throw new ArgumentException("Selectionnez un profil d'instance IAM.");
        var current = await ReadAsync(expected.InstanceId, cancellationToken);
        if (current != expected) throw new InvalidOperationException("L'instance ou son profil IAM a change. Actualisez et confirmez a nouveau.");
        if (current.ProfileArn == profileArn) throw new InvalidOperationException("Ce profil est deja associe.");
        if (current.AssociationId.Length > 0 && (current.AssociationState != "associated" || current.InstanceState != "running"))
            throw new InvalidOperationException("Le remplacement exige une instance demarree et une association stable.");
        if (current.InstanceState is not ("running" or "stopped")) throw new InvalidOperationException("Etat de l'instance incompatible avec une association IAM.");
        using var client = factory.CreateEc2Client();
        IamInstanceProfileAssociation? result;
        if (current.AssociationId.Length == 0)
            result = (await client.AssociateIamInstanceProfileAsync(new AssociateIamInstanceProfileRequest
            { InstanceId = current.InstanceId, IamInstanceProfile = new() { Arn = profileArn } }, cancellationToken)).IamInstanceProfileAssociation;
        else
            result = (await client.ReplaceIamInstanceProfileAssociationAsync(new ReplaceIamInstanceProfileAssociationRequest
            { AssociationId = current.AssociationId, IamInstanceProfile = new() { Arn = profileArn } }, cancellationToken)).IamInstanceProfileAssociation;
        return $"Demande acceptee : {result?.State?.Value ?? "en attente"}. La propagation IAM peut prendre du temps ; verifiez les acces applicatifs et SSM.";
    }
}