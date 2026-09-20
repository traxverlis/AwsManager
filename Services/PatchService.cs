using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using AwsManager.Models;
using System.Globalization;
using System.Text.Json;

namespace AwsManager.Services;

public sealed class PatchService(IAwsClientFactory factory)
{
    public const string DocumentName = "AWS-RunPatchBaseline";

    private static async Task<List<T>> PagesAsync<T>(Func<string?, Task<(IEnumerable<T> Items, string? Next)>> read, int limit = 10000)
    {
        var result = new List<T>();
        var tokens = new HashSet<string>();
        string? token = null;
        do
        {
            var page = await read(token);
            result.AddRange(page.Items);
            token = page.Next;
            if (!string.IsNullOrEmpty(token) && (!tokens.Add(token) || result.Count >= limit))
                throw new InvalidOperationException("Pagination SSM interrompue ; inventaire incomplet.");
        } while (!string.IsNullOrEmpty(token));
        return result;
    }

    public async Task<IReadOnlyList<PatchNode>> NodesAsync(CancellationToken token, IReadOnlyList<string>? ids = null)
    {
        using var client = factory.CreateSsmClient();
        var nodes = await PagesAsync<InstanceInformation>(async next =>
        {
            var page = await client.DescribeInstanceInformationAsync(new DescribeInstanceInformationRequest
            { MaxResults = 50, NextToken = next, Filters = ids == null ? null : [new() { Key = "InstanceIds", Values = ids.ToList() }] }, token);
            return (page.InstanceInformationList ?? [], page.NextToken);
        });
        var states = new Dictionary<string, InstancePatchState>();
        foreach (var batch in nodes.Select(item => item.InstanceId).Distinct().Chunk(50))
        {
            var values = await PagesAsync<InstancePatchState>(async next =>
            {
                var page = await client.DescribeInstancePatchStatesAsync(new() { InstanceIds = batch.ToList(), MaxResults = 50, NextToken = next }, token);
                return (page.InstancePatchStates ?? [], page.NextToken);
            });
            foreach (var state in values) states[state.InstanceId] = state;
        }
        return nodes.DistinctBy(item => item.InstanceId).Select(item => new PatchNode(item.InstanceId, item.Name ?? item.ComputerName ?? item.InstanceId,
            item.PlatformName ?? "", item.PlatformVersion ?? "", item.AgentVersion ?? "", item.PingStatus?.Value ?? "Unknown", states.GetValueOrDefault(item.InstanceId))).ToArray();
    }

    public static string OperatingSystemFor(PatchNode node)
    {
        var name = node.Platform.ToLowerInvariant();
        if (name.Contains("windows server")) return "WINDOWS";
        if (name.Contains("ubuntu")) return "UBUNTU";
        if (name.Contains("debian")) return "DEBIAN";
        if (name.Contains("amazon linux")) return node.PlatformVersion.StartsWith("2023", StringComparison.Ordinal) ? "AMAZON_LINUX_2023" : node.PlatformVersion == "2" ? "AMAZON_LINUX_2" : "AMAZON_LINUX";
        if (name.Contains("red hat")) return "REDHAT_ENTERPRISE_LINUX";
        if (name.Contains("oracle linux")) return "ORACLE_LINUX";
        if (name.Contains("suse linux enterprise")) return "SUSE";
        if (name.Contains("rocky linux")) return "ROCKY_LINUX";
        if (name.Contains("almalinux")) return "ALMA_LINUX";
        if (name.Contains("centos")) return "CENTOS";
        if (name.Contains("macos") || name.Contains("mac os")) return "MACOS";
        throw new InvalidOperationException($"OS non reconnu pour le patching : {node.Id} ({node.Platform}).");
    }

    public static void ValidateOptions(IReadOnlyList<string> ids, string operation, string reboot, int concurrency, int maxErrors)
    {
        if (ids.Count is < 1 or > 50 || ids.Distinct().Count() != ids.Count || ids.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Selectionnez entre 1 et 50 machines distinctes.");
        if (operation is not ("Scan" or "Install") || reboot is not ("NoReboot" or "RebootIfNeeded")) throw new ArgumentException("Operation ou option de redemarrage invalide.");
        if (concurrency < 1 || concurrency > ids.Count || maxErrors < 0 || maxErrors >= ids.Count) throw new ArgumentException("Concurrence : 1 au nombre de machines ; erreurs tolerees : 0 au nombre de machines moins 1.");
        if (operation == "Scan" && reboot != "NoReboot") throw new ArgumentException("Le scan ne redemarre pas les machines.");
    }

    public async Task<PatchPlan> PrepareAsync(IReadOnlyList<string> ids, string operation, string reboot, int concurrency, int maxErrors, CancellationToken token)
    {
        ValidateOptions(ids, operation, reboot, concurrency, maxErrors);
        var nodes = await NodesAsync(token, ids);
        var targets = new List<PatchTarget>();
        using var client = factory.CreateSsmClient();
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var node = nodes.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException($"Machine SSM introuvable : {id}.");
            if (node.PingStatus != "Online") throw new InvalidOperationException($"Machine SSM hors ligne : {id}.");
            if (!Version.TryParse(node.AgentVersion, out var agent) || agent < new Version(2, 0, 834, 0)) throw new InvalidOperationException($"Agent SSM trop ancien ou version inconnue : {id}.");
            var os = OperatingSystemFor(node);
            var group = await PatchGroupAsync(id, token);
            var baselineId = group.Length == 0
                ? (await client.GetDefaultPatchBaselineAsync(new() { OperatingSystem = os }, token)).BaselineId
                : (await client.GetPatchBaselineForPatchGroupAsync(new() { OperatingSystem = os, PatchGroup = group }, token)).BaselineId;
            if (string.IsNullOrEmpty(baselineId)) throw new InvalidOperationException($"Aucune baseline resolue pour {id}.");
            var baseline = await client.GetPatchBaselineAsync(new() { BaselineId = baselineId }, token);
            if (baseline.OperatingSystem?.Value != os) throw new InvalidOperationException($"La baseline de {id} ne correspond pas a son OS.");
            if (operation == "Install" && (node.Patch?.Operation?.Value != "Scan" || node.Patch.OperationEndTime is not { } scanned ||
                scanned.ToUniversalTime() < DateTime.UtcNow.AddHours(-24) || scanned.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5) ||
                baseline.ModifiedDate?.ToUniversalTime() > scanned.ToUniversalTime() ||
                node.Baseline != baselineId || !string.IsNullOrEmpty(node.Patch.InstallOverrideList)))
                throw new InvalidOperationException($"{id} : effectuez un scan de moins de 24 heures avec la baseline {baselineId}, sans liste de remplacement, avant installation.");
            var definition = JsonSerializer.Serialize(new
            {
                baseline.ModifiedDate,
                baseline.Description,
                baseline.ApprovalRules,
                baseline.GlobalFilters,
                baseline.ApprovedPatches,
                baseline.ApprovedPatchesComplianceLevel,
                baseline.ApprovedPatchesEnableNonSecurity,
                baseline.RejectedPatches,
                baseline.RejectedPatchesAction,
                baseline.Sources,
                baseline.AvailableSecurityUpdatesComplianceStatus
            }, new JsonSerializerOptions { WriteIndented = true });
            targets.Add(new(id, node.Name, os, group, baselineId, baseline.Name, definition));
        }
        return new(targets, operation, reboot, concurrency, maxErrors, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
    }

    private async Task<string> PatchGroupAsync(string id, CancellationToken token)
    {
        IEnumerable<(string Key, string Value)> tags;
        if (id.StartsWith("i-", StringComparison.Ordinal))
        {
            using var ec2 = factory.CreateEc2Client();
            var result = await ec2.DescribeInstancesAsync(new() { InstanceIds = [id] }, token);
            var instance = (result.Reservations ?? []).SelectMany(item => item.Instances ?? []).SingleOrDefault(item => item.InstanceId == id)
                ?? throw new InvalidOperationException($"Instance EC2 introuvable : {id}.");
            if (instance.State?.Name?.Value != "running") throw new InvalidOperationException($"L'instance {id} n'est pas demarree.");
            tags = (instance.Tags ?? []).Select(item => (item.Key, item.Value));
        }
        else
        {
            using var ssm = factory.CreateSsmClient();
            var result = await ssm.ListTagsForResourceAsync(new() { ResourceType = "ManagedInstance", ResourceId = id }, token);
            tags = (result.TagList ?? []).Select(item => (item.Key, item.Value));
        }
        var groups = tags.Where(item => item.Key is "Patch Group" or "PatchGroup").Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (groups.Length > 1 || groups.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException($"Tags PatchGroup ambigus ou vides : {id}.");
        return groups.SingleOrDefault() ?? "";
    }

    public async Task<string> SubmitAsync(PatchPlan plan, CancellationToken token)
    {
        if (DateTimeOffset.UtcNow - plan.Created > TimeSpan.FromMinutes(5)) throw new InvalidOperationException("Preparation expiree. Preparez a nouveau l'operation.");
        var current = await PrepareAsync(plan.Targets.Select(item => item.Id).ToArray(), plan.Operation, plan.RebootOption, plan.Concurrency, plan.MaxErrors, token);
        if (!current.Targets.SequenceEqual(plan.Targets)) throw new InvalidOperationException("Les machines, groupes ou baselines ont change. Nouvelle preparation et confirmation requises.");
        using var client = factory.CreateSsmClient();
        if (client.Config is AmazonSimpleSystemsManagementConfig configuration) configuration.MaxErrorRetry = 0;
        var response = await client.SendCommandAsync(new SendCommandRequest
        {
            DocumentName = DocumentName,
            InstanceIds = plan.Targets.Select(item => item.Id).ToList(),
            Comment = $"AwsManager - correctifs {plan.Operation}",
            MaxConcurrency = plan.Concurrency.ToString(CultureInfo.InvariantCulture),
            MaxErrors = plan.MaxErrors.ToString(CultureInfo.InvariantCulture),
            TimeoutSeconds = 600,
            Parameters = new() { ["Operation"] = [plan.Operation], ["RebootOption"] = [plan.RebootOption], ["Snapshot-ID"] = [plan.SnapshotId], ["StepTimeoutSeconds"] = ["3600"] }
        }, token);
        return response.Command?.CommandId ?? throw new InvalidOperationException("Identifiant de commande absent. Verifiez Run Command avant de reessayer.");
    }

    public async Task<string> PatchDetailsAsync(string id, CancellationToken token)
    {
        using var client = factory.CreateSsmClient();
        var patches = await PagesAsync<PatchComplianceData>(async next =>
        {
            var page = await client.DescribeInstancePatchesAsync(new() { InstanceId = id, MaxResults = 50, NextToken = next }, token);
            return (page.Patches ?? [], page.NextToken);
        }, 5000);
        return string.Join("\n", patches.OrderBy(item => item.State?.Value).Select(item => $"{item.State} | {item.Severity} | {item.KBId} | {item.Title}"));
    }

    public async Task<PatchCommandPage> CommandsAsync(string? next, CancellationToken token)
    {
        using var client = factory.CreateSsmClient();
        var page = await client.ListCommandsAsync(new ListCommandsRequest { MaxResults = 50, NextToken = next, Filters = [new() { Key = "DocumentName", Value = DocumentName }] }, token);
        return new((page.Commands ?? []).Where(item => item.DocumentName == DocumentName).Select(item => new PatchCommandItem(item.CommandId,
            item.Parameters?.GetValueOrDefault("Operation")?.FirstOrDefault() ?? "", item.StatusDetails ?? item.Status?.Value ?? "Unknown", item.RequestedDateTime,
            item.TargetCount ?? 0, item.CompletedCount ?? 0, item.ErrorCount ?? 0, item.Parameters?.GetValueOrDefault("RebootOption")?.FirstOrDefault() ?? "RebootIfNeeded")).ToArray(), page.NextToken);
    }

    public async Task<IReadOnlyList<PatchInvocationItem>> InvocationsAsync(string commandId, CancellationToken token)
    {
        using var client = factory.CreateSsmClient();
        var invocations = await PagesAsync<CommandInvocation>(async next =>
        {
            var page = await client.ListCommandInvocationsAsync(new ListCommandInvocationsRequest { CommandId = commandId, Details = true, MaxResults = 50, NextToken = next }, token);
            return (page.CommandInvocations ?? [], page.NextToken);
        });
        return invocations.Where(item => item.DocumentName == DocumentName).Select(item => new PatchInvocationItem(item.InstanceId, item.InstanceName,
            item.StatusDetails ?? item.Status?.Value ?? "Unknown", string.Join("\n", (item.CommandPlugins ?? []).Select(plugin => $"{plugin.Name} | {plugin.StatusDetails} | code {plugin.ResponseCode}\n{plugin.Output}")))).ToArray();
    }
}