using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using AwsManager.Models;
using AwsManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace AwsManager.Tests;

[TestClass]
public class PatchWorkflowTests
{
    private const string NodeId = "mi-0123456789abcdef0";
    private const string BaselineId = "pb-0123456789abcdef0";
    private const string CommandId = "00000000-0000-4000-8000-000000000001";
    internal static (PatchService Service, Mock<IAmazonSimpleSystemsManagement> Client) Setup(bool scanned = true)
    {
        var client = new Mock<IAmazonSimpleSystemsManagement>();
        client.Setup(service => service.DescribeInstanceInformationAsync(It.IsAny<DescribeInstanceInformationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeInstanceInformationResponse { InstanceInformationList = [new() { InstanceId = NodeId, PlatformName = "Microsoft Windows Server 2022", AgentVersion = "3.3.0.0", PingStatus = "Online", Name = "recette" }] });
        client.Setup(service => service.DescribeInstancePatchStatesAsync(It.IsAny<DescribeInstancePatchStatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeInstancePatchStatesResponse { InstancePatchStates = scanned ? [new() { InstanceId = NodeId, Operation = "Scan", OperationEndTime = DateTime.UtcNow, BaselineId = BaselineId, MissingCount = 3 }] : [] });
        client.Setup(service => service.ListTagsForResourceAsync(It.IsAny<ListTagsForResourceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ListTagsForResourceResponse());
        client.Setup(service => service.GetDefaultPatchBaselineAsync(It.IsAny<GetDefaultPatchBaselineRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetDefaultPatchBaselineResponse { BaselineId = BaselineId });
        client.Setup(service => service.GetPatchBaselineAsync(It.IsAny<GetPatchBaselineRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetPatchBaselineResponse { BaselineId = BaselineId, Name = "Recette", OperatingSystem = "WINDOWS" });
        client.Setup(service => service.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SendCommandResponse { Command = new() { CommandId = CommandId } });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateSsmClient()).Returns(client.Object);
        return (new(factory.Object), client);
    }

    [DataTestMethod]
    [DataRow("Scan", "NoReboot")]
    [DataRow("Install", "NoReboot")]
    [DataRow("Install", "RebootIfNeeded")]
    public async Task PatchCommandHasExplicitTargetsAndConservativeControls(string operation, string reboot)
    {
        var (service, client) = Setup();
        var plan = await service.PrepareAsync([NodeId], operation, reboot, 1, 0, CancellationToken.None);
        client.Verify(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.AreEqual(CommandId, await service.SubmitAsync(plan, CancellationToken.None));
        client.Verify(api => api.SendCommandAsync(It.Is<SendCommandRequest>(request => request.DocumentName == PatchService.DocumentName &&
            request.InstanceIds.SequenceEqual(new[] { NodeId }) && (request.Targets == null || request.Targets.Count == 0) && request.MaxConcurrency == "1" && request.MaxErrors == "0" &&
            request.Parameters["Operation"].Single() == operation && request.Parameters["RebootOption"].Single() == reboot && request.Parameters["Snapshot-ID"].Single() == plan.SnapshotId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PatchInstallationRequiresRecentMatchingScanAndStableBaseline()
    {
        var (service, client) = Setup(false);
        var nodes = await service.NodesAsync(CancellationToken.None);
        Assert.AreEqual("Inconnu", nodes.Single().Compliance);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.PrepareAsync([NodeId], "Install", "NoReboot", 1, 0, CancellationToken.None));
        var plan = await service.PrepareAsync([NodeId], "Scan", "NoReboot", 1, 0, CancellationToken.None);
        client.Setup(api => api.GetPatchBaselineAsync(It.IsAny<GetPatchBaselineRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetPatchBaselineResponse { Name = "Changed", OperatingSystem = "WINDOWS", BaselineId = BaselineId });
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.SubmitAsync(plan, CancellationToken.None));
        client.Verify(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void PatchOptionsRejectBroadOrInvalidTargets()
    {
        Assert.ThrowsException<ArgumentException>(() => PatchService.ValidateOptions([], "Install", "NoReboot", 1, 0));
        Assert.ThrowsException<ArgumentException>(() => PatchService.ValidateOptions([NodeId, NodeId], "Scan", "NoReboot", 1, 0));
        Assert.ThrowsException<ArgumentException>(() => PatchService.ValidateOptions([NodeId], "Scan", "RebootIfNeeded", 1, 0));
        Assert.ThrowsException<ArgumentException>(() => PatchService.ValidateOptions([NodeId], "Install", "NoReboot", 2, 0));
    }

    [DataTestMethod]
    [DataRow("offline")]
    [DataRow("old-agent")]
    [DataRow("stale-scan")]
    [DataRow("other-baseline")]
    [DataRow("override")]
    [DataRow("modified-baseline")]
    [DataRow("previous-install")]
    public async Task PatchInstallationRejectsUnsafePrerequisites(string condition)
    {
        var (service, client) = Setup();
        client.Setup(api => api.DescribeInstanceInformationAsync(It.IsAny<DescribeInstanceInformationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeInstanceInformationResponse { InstanceInformationList = [new() { InstanceId = NodeId, Name = "recette", PlatformName = "Microsoft Windows Server 2022", PingStatus = condition == "offline" ? "ConnectionLost" : "Online", AgentVersion = condition == "old-agent" ? "1.0.0.0" : "3.3.0.0" }] });
        client.Setup(api => api.DescribeInstancePatchStatesAsync(It.IsAny<DescribeInstancePatchStatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeInstancePatchStatesResponse { InstancePatchStates = [new() { InstanceId = NodeId, Operation = condition == "previous-install" ? "Install" : "Scan", OperationEndTime = DateTime.UtcNow.AddHours(condition == "stale-scan" ? -48 : -1), BaselineId = condition == "other-baseline" ? "pb-11111111111111111" : BaselineId, InstallOverrideList = condition == "override" ? "s3://bucket-demo/patches.yml" : null }] });
        if (condition == "modified-baseline")
            client.Setup(api => api.GetPatchBaselineAsync(It.IsAny<GetPatchBaselineRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetPatchBaselineResponse { Name = "Recette", BaselineId = BaselineId, OperatingSystem = "WINDOWS", ModifiedDate = DateTime.UtcNow });
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.PrepareAsync([NodeId], "Install", "NoReboot", 1, 0, CancellationToken.None));
        client.Verify(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task PatchEc2UsesItsTaggedBaselineAndRejectsConflictingTags()
    {
        const string instanceId = "i-0123456789abcdef0";
        var (_, client) = Setup(false);
        client.Setup(api => api.DescribeInstanceInformationAsync(It.IsAny<DescribeInstanceInformationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DescribeInstanceInformationResponse
        { InstanceInformationList = [new() { InstanceId = instanceId, Name = "recette", AgentVersion = "3.3.0.0", PingStatus = "Online", PlatformName = "Microsoft Windows Server 2022" }] });
        client.Setup(api => api.GetPatchBaselineForPatchGroupAsync(It.Is<GetPatchBaselineForPatchGroupRequest>(request => request.PatchGroup == "Recette" && request.OperatingSystem == "WINDOWS"), It.IsAny<CancellationToken>())).ReturnsAsync(new GetPatchBaselineForPatchGroupResponse { BaselineId = BaselineId });
        var ec2 = new Mock<Amazon.EC2.IAmazonEC2>();
        var instance = new Amazon.EC2.Model.Instance { InstanceId = instanceId, State = new() { Name = "running" }, Tags = [new() { Key = "PatchGroup", Value = "Recette" }] };
        ec2.Setup(api => api.DescribeInstancesAsync(It.IsAny<Amazon.EC2.Model.DescribeInstancesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { Reservations = [new() { Instances = [instance] }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(api => api.CreateSsmClient()).Returns(client.Object);
        factory.Setup(api => api.CreateEc2Client()).Returns(ec2.Object);
        var service = new PatchService(factory.Object);
        var plan = await service.PrepareAsync([instanceId], "Scan", "NoReboot", 1, 0, CancellationToken.None);
        Assert.AreEqual("Recette", plan.Targets.Single().Group);
        client.Verify(api => api.GetDefaultPatchBaselineAsync(It.IsAny<GetDefaultPatchBaselineRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        instance.Tags.Add(new() { Key = "Patch Group", Value = "Production" });
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.SubmitAsync(plan, CancellationToken.None));
        client.Verify(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task PatchInventoryReadsAllPagesWithoutInventingCompliance()
    {
        var (service, client) = Setup(false);
        const string secondId = "mi-11111111111111111";
        client.Setup(api => api.DescribeInstanceInformationAsync(It.IsAny<DescribeInstanceInformationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DescribeInstanceInformationRequest request, CancellationToken _) => new()
            {
                NextToken = request.NextToken == null ? "next" : null,
                InstanceInformationList = [new() { InstanceId = request.NextToken == null ? NodeId : secondId, PlatformName = "Ubuntu", PlatformVersion = "22.04", PingStatus = "Online" }]
            });
        client.Setup(api => api.DescribeInstancePatchStatesAsync(It.IsAny<DescribeInstancePatchStatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DescribeInstancePatchStatesRequest request, CancellationToken _) => request.NextToken == null
                ? new() { NextToken = "states-next", InstancePatchStates = [] }
                : new() { InstancePatchStates = [new() { InstanceId = secondId, BaselineId = BaselineId, Operation = "Scan", OperationEndTime = DateTime.UtcNow, MissingCount = 1 }] });
        var nodes = await service.NodesAsync(CancellationToken.None);
        Assert.AreEqual(2, nodes.Count);
        Assert.AreEqual("Inconnu", nodes.Single(item => item.Id == NodeId).Compliance);
        Assert.AreEqual("Non conforme", nodes.Single(item => item.Id == secondId).Compliance);
        client.Verify(api => api.DescribeInstancePatchStatesAsync(It.Is<DescribeInstancePatchStatesRequest>(request => request.InstanceIds.Count == 2), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task PatchUncertainSubmissionCannotBeReplayedFromSamePreparation()
    {
        var (_, client) = Setup();
        client.Setup(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new System.Net.Http.HttpRequestException("simulated lost reply"));
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateSsmClient()).Returns(client.Object);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"awsmanager-patch-{Guid.NewGuid()}.json");
        try
        {
            var store = new WorkspaceStore(path);
            store.SetReadOnly(false);
            using var model = new AwsManager.ViewModels.PatchViewModel(factory.Object, store, confirm: _ => true);
            await model.RefreshAsync();
            model.SetSelection(model.Nodes);
            await ((AwsManager.ViewModels.AsyncRelayCommand)model.PrepareCommand).ExecuteAsync(null);
            var submit = (AwsManager.ViewModels.AsyncRelayCommand)model.SubmitCommand;
            await submit.ExecuteAsync(null);
            await submit.ExecuteAsync(null);
            Assert.IsFalse(model.CanSubmit);
            StringAssert.Contains(model.Status, "aucun renvoi automatique");
            client.Verify(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [TestMethod]
    public async Task PatchViewRequiresPreparationWritableModeAndConfirmation()
    {
        var (_, client) = Setup();
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateSsmClient()).Returns(client.Object);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"awsmanager-patch-{Guid.NewGuid()}.json");
        try
        {
            var store = new WorkspaceStore(path);
            var confirms = 0;
            using var model = new AwsManager.ViewModels.PatchViewModel(factory.Object, store, confirm: _ => { confirms++; return false; });
            await model.RefreshAsync();
            model.SetSelection(model.Nodes);
            Assert.IsTrue(model.CanPrepare);
            Assert.IsFalse(model.CanSubmit);
            await ((AwsManager.ViewModels.AsyncRelayCommand)model.PrepareCommand).ExecuteAsync(null);
            StringAssert.Contains(model.Preview, BaselineId);
            Assert.IsFalse(model.CanSubmit);
            store.SetReadOnly(false);
            Assert.IsTrue(model.CanSubmit);
            await ((AwsManager.ViewModels.AsyncRelayCommand)model.SubmitCommand).ExecuteAsync(null);
            Assert.AreEqual(1, confirms);
            client.Verify(api => api.SendCommandAsync(It.IsAny<SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            model.Operation = "Install";
            Assert.IsFalse(model.CanSubmit);
            await ((AwsManager.ViewModels.AsyncRelayCommand)model.PrepareCommand).ExecuteAsync(null);
            Assert.IsFalse(model.CanSubmit);
            model.Acknowledged = true;
            Assert.IsTrue(model.CanSubmit);
            model.MaxErrors = "invalid";
            Assert.IsFalse(model.CanPrepare);
            Assert.IsFalse(model.CanSubmit);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }
}