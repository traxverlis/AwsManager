using Amazon.IdentityManagement.Model;

namespace AwsManager.Services;

public enum PermissionState { Unknown, Allowed, Denied }
public sealed record PermissionCheck(string Action, string Resource = "*", string ContextKey = "", string ContextValue = "", string? Region = null);
public sealed record PermissionResult(PermissionState State, string Reason);

public sealed class PermissionService(IAwsClientFactory factory, AwsContext context)
{
    private string? _principal;

    public async Task<IReadOnlyDictionary<PermissionCheck, PermissionResult>> EvaluateAsync(
        IReadOnlyList<PermissionCheck> checks, CancellationToken token)
    {
        using var client = factory.CreateIamClient();
        if (_principal == null)
        {
            var identity = Amazon.Arn.Parse(context.Identity);
            if (identity.AccountId != context.Account) throw new InvalidOperationException("Identite IAM incoherente.");
            if (identity.Service == "iam" && (identity.Resource.StartsWith("user/") || identity.Resource.StartsWith("role/")))
                _principal = context.Identity;
            else if (identity.Service == "sts" && identity.Resource.StartsWith("assumed-role/"))
            {
                var roleName = identity.Resource.Split('/')[1];
                var role = (await client.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, token)).Role;
                var roleArn = Amazon.Arn.Parse(role.Arn);
                if (role.RoleName != roleName || roleArn.AccountId != context.Account || roleArn.Partition != identity.Partition ||
                    roleArn.Service != "iam" || !roleArn.Resource.StartsWith("role/"))
                    throw new InvalidOperationException("Le role retourne ne correspond pas a la session.");
                _principal = role.Arn;
            }
            else throw new InvalidOperationException("Ce type d'identite ne peut pas etre simule.");
        }

        var results = checks.Distinct().ToDictionary(check => check,
            _ => new PermissionResult(PermissionState.Unknown, "AWS n'a retourne aucune decision pour cette action."));
        string RegionFor(PermissionCheck check) => check.Region ?? (check.Action.StartsWith("iam:") || check.Action.StartsWith("route53:")
            ? Amazon.Arn.Parse(_principal).Partition switch { "aws-cn" => "cn-north-1", "aws-us-gov" => "us-gov-west-1", _ => "us-east-1" } : context.Region);
        foreach (var group in results.Keys.GroupBy(check => (check.Resource, check.ContextKey, check.ContextValue, Region: RegionFor(check))))
            foreach (var batch in group.Chunk(100))
            {
                var entries = new List<ContextEntry>
            {
                new() { ContextKeyName = "aws:RequestedRegion", ContextKeyType = "string", ContextKeyValues = [group.Key.Region] },
                new() { ContextKeyName = "aws:PrincipalArn", ContextKeyType = "string", ContextKeyValues = [_principal] }
            };
                if (group.Key.ContextKey.Length != 0)
                    entries.Add(new() { ContextKeyName = group.Key.ContextKey, ContextKeyType = "string", ContextKeyValues = [group.Key.ContextValue] });
                string? marker = null;
                var seen = new HashSet<string>();
                do
                {
                    token.ThrowIfCancellationRequested();
                    var response = await client.SimulatePrincipalPolicyAsync(new SimulatePrincipalPolicyRequest
                    {
                        PolicySourceArn = _principal,
                        ActionNames = batch.Select(check => check.Action).ToList(),
                        ResourceArns = [group.Key.Resource],
                        ContextEntries = entries,
                        Marker = marker,
                        MaxItems = 100
                    }, token);
                    foreach (var evaluation in response.EvaluationResults ?? [])
                    {
                        foreach (var check in batch.Where(item => string.Equals(item.Action, evaluation.EvalActionName, StringComparison.OrdinalIgnoreCase)))
                            results[check] = ReadDecision(check, evaluation);
                    }
                    if (response.IsTruncated != true) break;
                    marker = response.Marker;
                    if (string.IsNullOrEmpty(marker) || !seen.Add(marker) || seen.Count > 100)
                        throw new InvalidOperationException("Evaluation IAM incomplete (pagination).");
                } while (true);
            }
        token.ThrowIfCancellationRequested();
        return results;
    }

    private static PermissionResult ReadDecision(PermissionCheck check, EvaluationResult evaluation)
    {
        var resources = evaluation.ResourceSpecificResults ?? [];
        var matching = resources.Where(item => item.EvalResourceName == check.Resource).ToArray();
        if (check.Resource != "*" && resources.Count != 0 && matching.Length == 0)
            return new(PermissionState.Unknown, "AWS n'a retourne aucune decision pour la ressource demandee.");
        if (check.Resource != "*" && resources.Count == 0 && !string.IsNullOrEmpty(evaluation.EvalResourceName) &&
            evaluation.EvalResourceName != "*" && evaluation.EvalResourceName != check.Resource &&
            !evaluation.EvalResourceName.Contains("${", StringComparison.Ordinal))
            return new(PermissionState.Unknown, "La ressource retournee par AWS ne correspond pas a la demande.");

        var details = check.Resource == "*" ? resources.ToArray() : matching;
        var missing = (evaluation.MissingContextValues ?? []).Concat(details.SelectMany(item => item.MissingContextValues ?? []))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            return new(PermissionState.Unknown, $"Conditions IAM non renseignees : {string.Join(", ", missing)}.");

        var decisions = details.Select(item => item.EvalResourceDecision?.Value).Prepend(evaluation.EvalDecision?.Value).ToArray();
        var denied = evaluation.PermissionsBoundaryDecisionDetail?.AllowedByPermissionsBoundary == false ||
            evaluation.OrganizationsDecisionDetail?.AllowedByOrganizations == false ||
            details.Any(item => item.PermissionsBoundaryDecisionDetail?.AllowedByPermissionsBoundary == false) ||
            decisions.Any(decision => decision is "implicitDeny" or "explicitDeny");
        if (denied) return new(PermissionState.Denied, "Non autorise par la simulation IAM.");
        return decisions.All(decision => decision == "allowed")
            ? new(PermissionState.Allowed, "Autorise par la simulation IAM ; controle final par AWS.")
            : new(PermissionState.Unknown, "Decision IAM absente ou inconnue.");
    }
}