using Amazon.SimpleSystemsManagement.Model;

namespace AwsManager.Models;

public sealed record PatchNode(string Id, string Name, string Platform, string PlatformVersion, string AgentVersion, string PingStatus, InstancePatchState? Patch)
{
    public string Baseline => Patch?.BaselineId ?? "";
    public int? Missing => Patch?.MissingCount;
    public int? Critical => Patch?.CriticalNonCompliantCount;
    public int? Failed => Patch?.FailedCount;
    public int? PendingReboot => Patch?.InstalledPendingRebootCount;
    public string LastOperation => Patch?.OperationEndTime is { } time ? $"{Patch.Operation} | {time.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "Jamais analyse";
    public string Compliance => Patch?.OperationEndTime == null || string.IsNullOrEmpty(Baseline) ? "Inconnu" :
        (Missing ?? 0) + (Failed ?? 0) + (PendingReboot ?? 0) + (Patch.InstalledRejectedCount ?? 0) > 0 ? "Non conforme" : "Conforme au releve";
}

public sealed record PatchTarget(string Id, string Name, string OperatingSystem, string Group, string BaselineId, string BaselineName, string BaselineDefinition);
public sealed record PatchPlan(IReadOnlyList<PatchTarget> Targets, string Operation, string RebootOption, int Concurrency, int MaxErrors, string SnapshotId, DateTimeOffset Created);
public sealed record PatchCommandItem(string Id, string Operation, string Status, DateTime? Requested, int Targets, int Completed, int Errors, string RebootOption);
public sealed record PatchCommandPage(IReadOnlyList<PatchCommandItem> Items, string? NextToken);
public sealed record PatchInvocationItem(string InstanceId, string Name, string Status, string Output);