using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using System.Text;
using System.Text.Json;

namespace AwsManager.Services;

public sealed record IamResource(string Kind, string Name, string Arn, string Path);
public sealed record IamPage(IReadOnlyList<IamResource> Items, string? NextToken);
public sealed record InstanceProfileItem(string Name, string Arn, string RoleArn)
{
    public string DisplayName => $"{Name} | {RoleArn.Split('/').LastOrDefault()}";
}

public sealed class IamService(IAwsClientFactory factory)
{
    public async Task<IamPage> ListAsync(string kind, string? marker, CancellationToken token)
    {
        using var client = factory.CreateIamClient();
        switch (kind)
        {
            case "Roles":
                var roles = await client.ListRolesAsync(new() { Marker = marker, MaxItems = 100 }, token);
                return new((roles.Roles ?? []).Select(item => new IamResource(kind, item.RoleName, item.Arn, item.Path)).ToArray(), Next(roles.IsTruncated, roles.Marker, marker));
            case "Profiles":
                var profiles = await client.ListInstanceProfilesAsync(new() { Marker = marker, MaxItems = 100 }, token);
                return new((profiles.InstanceProfiles ?? []).Select(item => new IamResource(kind, item.InstanceProfileName, item.Arn, item.Path)).ToArray(), Next(profiles.IsTruncated, profiles.Marker, marker));
            case "Policies":
                var policies = await client.ListPoliciesAsync(new ListPoliciesRequest { Scope = PolicyScopeType.All, Marker = marker, MaxItems = 100 }, token);
                return new((policies.Policies ?? []).Select(item => new IamResource(kind, item.PolicyName, item.Arn, item.Path)).ToArray(), Next(policies.IsTruncated, policies.Marker, marker));
            case "Users":
                var users = await client.ListUsersAsync(new() { Marker = marker, MaxItems = 100 }, token);
                return new((users.Users ?? []).Select(item => new IamResource(kind, item.UserName, item.Arn, item.Path)).ToArray(), Next(users.IsTruncated, users.Marker, marker));
            case "Groups":
                var groups = await client.ListGroupsAsync(new() { Marker = marker, MaxItems = 100 }, token);
                return new((groups.Groups ?? []).Select(item => new IamResource(kind, item.GroupName, item.Arn, item.Path)).ToArray(), Next(groups.IsTruncated, groups.Marker, marker));
            default: throw new ArgumentException("Categorie IAM inconnue.");
        }
    }

    private static string? Next(bool? truncated, string? next, string? previous)
    {
        if (truncated != true) return null;
        if (string.IsNullOrEmpty(next) || next == previous) throw new InvalidOperationException("Pagination IAM incoherente ; liste incomplete.");
        return next;
    }

    private static async Task<List<T>> AllAsync<T>(Func<string?, Task<(IEnumerable<T> Items, bool? More, string? Marker)>> read)
    {
        var result = new List<T>();
        var markers = new HashSet<string>();
        string? marker = null;
        do
        {
            var page = await read(marker);
            result.AddRange(page.Items);
            marker = Next(page.More, page.Marker, marker);
            if (marker != null && (!markers.Add(marker) || result.Count >= 10000)) throw new InvalidOperationException("Limite de lecture IAM atteinte ; resultats incomplets.");
        } while (marker != null);
        return result;
    }

    public async Task<IReadOnlyList<InstanceProfileItem>> ProfilesAsync(CancellationToken token)
    {
        using var client = factory.CreateIamClient();
        var profiles = await AllAsync<InstanceProfile>(async marker =>
        {
            var response = await client.ListInstanceProfilesAsync(new() { Marker = marker, MaxItems = 100 }, token);
            return (response.InstanceProfiles ?? [], response.IsTruncated, response.Marker);
        });
        return profiles.Select(item => new InstanceProfileItem(item.InstanceProfileName, item.Arn, item.Roles?.SingleOrDefault()?.Arn ?? ""))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task VerifyProfileAsync(InstanceProfileItem expected, CancellationToken token)
    {
        using var client = factory.CreateIamClient();
        var actual = (await client.GetInstanceProfileAsync(new() { InstanceProfileName = expected.Name }, token)).InstanceProfile;
        if (actual?.Arn != expected.Arn || string.IsNullOrEmpty(expected.RoleArn) || actual.Roles?.SingleOrDefault()?.Arn != expected.RoleArn)
            throw new InvalidOperationException("Le profil ou son role a change. Actualisez avant de confirmer.");
    }

    public static string FormatDocument(string document)
    {
        var decoded = document.TrimStart().StartsWith('{') ? document : Uri.UnescapeDataString(document);
        using var parsed = JsonDocument.Parse(decoded);
        return JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> DetailsAsync(IamResource resource, CancellationToken token)
    {
        using var client = factory.CreateIamClient();
        var output = new StringBuilder().AppendLine(resource.Name).AppendLine(resource.Arn).AppendLine();
        switch (resource.Kind)
        {
            case "Roles":
                var role = (await client.GetRoleAsync(new() { RoleName = resource.Name }, token)).Role;
                output.AppendLine(role.Description).AppendLine($"Limite de permissions : {role.PermissionsBoundary?.PermissionsBoundaryArn ?? "aucune"}")
                    .AppendLine("Relation de confiance").AppendLine(FormatDocument(role.AssumeRolePolicyDocument));
                var profiles = await AllAsync<InstanceProfile>(async marker => { var page = await client.ListInstanceProfilesForRoleAsync(new() { RoleName = resource.Name, Marker = marker }, token); return (page.InstanceProfiles ?? [], page.IsTruncated, page.Marker); });
                output.AppendLine().AppendLine("Profils d'instance").AppendLine(string.Join("\n", profiles.Select(item => item.Arn)));
                await AppendPermissionsAsync(client, resource, output, token);
                break;
            case "Users":
                var user = (await client.GetUserAsync(new GetUserRequest { UserName = resource.Name }, token)).User;
                output.AppendLine($"Limite de permissions : {user.PermissionsBoundary?.PermissionsBoundaryArn ?? "aucune"}");
                var groups = await AllAsync<Group>(async marker => { var page = await client.ListGroupsForUserAsync(new() { UserName = resource.Name, Marker = marker }, token); return (page.Groups ?? [], page.IsTruncated, page.Marker); });
                output.AppendLine("Groupes (permissions heritees)").AppendLine(string.Join("\n", groups.Select(item => item.GroupName)));
                await AppendPermissionsAsync(client, resource, output, token);
                break;
            case "Groups":
                var users = await AllAsync<User>(async marker => { var page = await client.GetGroupAsync(new() { GroupName = resource.Name, Marker = marker }, token); return (page.Users ?? [], page.IsTruncated, page.Marker); });
                output.AppendLine("Membres").AppendLine(string.Join("\n", users.Select(item => item.UserName)));
                await AppendPermissionsAsync(client, resource, output, token);
                break;
            case "Profiles":
                var profile = (await client.GetInstanceProfileAsync(new() { InstanceProfileName = resource.Name }, token)).InstanceProfile;
                output.AppendLine("Roles").AppendLine(string.Join("\n", (profile.Roles ?? []).Select(item => item.Arn)));
                foreach (var item in profile.Roles ?? []) output.AppendLine("Relation de confiance").AppendLine(FormatDocument(item.AssumeRolePolicyDocument));
                break;
            case "Policies":
                var policy = (await client.GetPolicyAsync(new() { PolicyArn = resource.Arn }, token)).Policy;
                output.AppendLine(policy.Description).AppendLine($"Version par defaut : {policy.DefaultVersionId} | Attachements : {policy.AttachmentCount}");
                var version = await client.GetPolicyVersionAsync(new() { PolicyArn = resource.Arn, VersionId = policy.DefaultVersionId }, token);
                output.AppendLine(FormatDocument(version.PolicyVersion.Document));
                var entities = await AllAsync<string>(async marker =>
                {
                    var page = await client.ListEntitiesForPolicyAsync(new() { PolicyArn = resource.Arn, Marker = marker }, token);
                    return ((page.PolicyRoles ?? []).Select(item => $"Role : {item.RoleName}").Concat((page.PolicyUsers ?? []).Select(item => $"Utilisateur : {item.UserName}")).Concat((page.PolicyGroups ?? []).Select(item => $"Groupe : {item.GroupName}")), page.IsTruncated, page.Marker);
                });
                output.AppendLine().AppendLine("Entites associees").AppendLine(string.Join("\n", entities));
                break;
            default: throw new ArgumentException("Categorie IAM inconnue.");
        }
        return output.ToString();
    }

    private static async Task AppendPermissionsAsync(IAmazonIdentityManagementService client, IamResource resource, StringBuilder output, CancellationToken token)
    {
        var attached = await AllAsync<AttachedPolicyType>(async marker =>
        {
            if (resource.Kind == "Roles") { var page = await client.ListAttachedRolePoliciesAsync(new() { RoleName = resource.Name, Marker = marker }, token); return (page.AttachedPolicies ?? [], page.IsTruncated, page.Marker); }
            if (resource.Kind == "Users") { var page = await client.ListAttachedUserPoliciesAsync(new() { UserName = resource.Name, Marker = marker }, token); return (page.AttachedPolicies ?? [], page.IsTruncated, page.Marker); }
            var group = await client.ListAttachedGroupPoliciesAsync(new() { GroupName = resource.Name, Marker = marker }, token); return (group.AttachedPolicies ?? [], group.IsTruncated, group.Marker);
        });
        output.AppendLine().AppendLine("Politiques gerees attachees").AppendLine(string.Join("\n", attached.Select(item => $"{item.PolicyName} | {item.PolicyArn}")));
        var inline = await AllAsync<string>(async marker =>
        {
            if (resource.Kind == "Roles") { var page = await client.ListRolePoliciesAsync(new() { RoleName = resource.Name, Marker = marker }, token); return (page.PolicyNames ?? [], page.IsTruncated, page.Marker); }
            if (resource.Kind == "Users") { var page = await client.ListUserPoliciesAsync(new() { UserName = resource.Name, Marker = marker }, token); return (page.PolicyNames ?? [], page.IsTruncated, page.Marker); }
            var group = await client.ListGroupPoliciesAsync(new() { GroupName = resource.Name, Marker = marker }, token); return (group.PolicyNames ?? [], group.IsTruncated, group.Marker);
        });
        foreach (var name in inline)
        {
            var document = resource.Kind switch
            {
                "Roles" => (await client.GetRolePolicyAsync(new() { RoleName = resource.Name, PolicyName = name }, token)).PolicyDocument,
                "Users" => (await client.GetUserPolicyAsync(new() { UserName = resource.Name, PolicyName = name }, token)).PolicyDocument,
                _ => (await client.GetGroupPolicyAsync(new() { GroupName = resource.Name, PolicyName = name }, token)).PolicyDocument
            };
            output.AppendLine().AppendLine($"Politique inline : {name}").AppendLine(FormatDocument(document));
        }
    }
}