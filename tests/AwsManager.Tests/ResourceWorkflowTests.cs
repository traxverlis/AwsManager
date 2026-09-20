using Amazon.RDS;
using Amazon.RDS.Model;
using AwsManager.Services;
using AwsManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace AwsManager.Tests;

[TestClass]
public class ResourceWorkflowTests
{
    private const string ResourceArn = "arn:aws:rds:eu-west-1:000000000000:db:demo";

    [TestMethod]
    public async Task IamDisposedViewRejectsLateInventory()
    {
        var reply = new TaskCompletionSource<Amazon.IdentityManagement.Model.ListRolesResponse>();
        var client = new Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>();
        client.Setup(service => service.ListRolesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListRolesRequest>(), It.IsAny<CancellationToken>())).Returns(reply.Task);
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateIamClient()).Returns(client.Object);
        var model = new IamViewModel(factory.Object);
        var loading = model.RefreshAsync();
        model.Dispose();
        reply.SetResult(new() { Roles = [new() { RoleName = "late", Arn = "arn:aws:iam::000000000000:role/late", Path = "/" }] });
        await loading;
        Assert.AreEqual(0, model.Items.Count);
        Assert.AreEqual("", model.Details);
    }

    [TestMethod]
    public async Task IamProfilesArePaginatedAndChangedRolesAreRejected()
    {
        var client = new Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>();
        client.Setup(service => service.ListInstanceProfilesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListInstanceProfilesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Amazon.IdentityManagement.Model.ListInstanceProfilesRequest request, CancellationToken _) => new()
            {
                IsTruncated = request.Marker == null,
                Marker = request.Marker == null ? "next" : null,
                InstanceProfiles = [new() { InstanceProfileName = request.Marker == null ? "first" : "second", Arn = "arn:aws:iam::000000000000:instance-profile/demo", Roles = [new() { Arn = "arn:aws:iam::000000000000:role/demo" }] }]
            });
        client.Setup(service => service.GetInstanceProfileAsync(It.IsAny<Amazon.IdentityManagement.Model.GetInstanceProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.IdentityManagement.Model.GetInstanceProfileResponse { InstanceProfile = new() { Arn = "arn:aws:iam::000000000000:instance-profile/demo", Roles = [new() { Arn = "arn:aws:iam::000000000000:role/changed" }] } });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateIamClient()).Returns(client.Object);
        var service = new IamService(factory.Object);
        var profiles = await service.ProfilesAsync(CancellationToken.None);
        Assert.AreEqual(2, profiles.Count);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.VerifyProfileAsync(profiles[0], CancellationToken.None));
        using var document = System.Text.Json.JsonDocument.Parse(IamService.FormatDocument("%7B%22value%22%3A%22a%2Bb%22%7D"));
        Assert.AreEqual("a+b", document.RootElement.GetProperty("value").GetString());
        Assert.AreEqual(0, client.Invocations.Count(call => !call.Method.Name.StartsWith("List") && !call.Method.Name.StartsWith("Get") && call.Method.Name != "Dispose"));
    }

    [DataTestMethod]
    [DataRow(true, "running")]
    [DataRow(false, "stopped")]
    public async Task Ec2ProfileChangeUsesAssociationOrReplacementWithoutDetach(bool existing, string state)
    {
        var client = new Mock<Amazon.EC2.IAmazonEC2>();
        client.Setup(service => service.DescribeInstancesAsync(It.IsAny<Amazon.EC2.Model.DescribeInstancesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { Reservations = [new() { Instances = [new() { InstanceId = "i-demo", State = new() { Name = state } }] }] });
        client.Setup(service => service.DescribeIamInstanceProfileAssociationsAsync(It.IsAny<Amazon.EC2.Model.DescribeIamInstanceProfileAssociationsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeIamInstanceProfileAssociationsResponse { IamInstanceProfileAssociations = existing ? [new() { AssociationId = "iip-demo", State = "associated", IamInstanceProfile = new() { Arn = "arn:aws:iam::000000000000:instance-profile/old" } }] : [] });
        client.Setup(service => service.AssociateIamInstanceProfileAsync(It.IsAny<Amazon.EC2.Model.AssociateIamInstanceProfileRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.AssociateIamInstanceProfileResponse());
        client.Setup(service => service.ReplaceIamInstanceProfileAssociationAsync(It.IsAny<Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationResponse());
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateEc2Client()).Returns(client.Object);
        var service = new Ec2ProfileService(factory.Object);
        var initial = await service.ReadAsync("i-demo");
        await service.ChangeAsync(initial, "arn:aws:iam::000000000000:instance-profile/new");
        if (existing)
            client.Verify(service => service.ReplaceIamInstanceProfileAssociationAsync(It.Is<Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationRequest>(request => request.AssociationId == "iip-demo" && request.IamInstanceProfile.Arn.EndsWith("/new")), It.IsAny<CancellationToken>()), Times.Once);
        else
            client.Verify(service => service.AssociateIamInstanceProfileAsync(It.Is<Amazon.EC2.Model.AssociateIamInstanceProfileRequest>(request => request.InstanceId == "i-demo" && request.IamInstanceProfile.Arn.EndsWith("/new")), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(service => service.DisassociateIamInstanceProfileAsync(It.IsAny<Amazon.EC2.Model.DisassociateIamInstanceProfileRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ChangeAsync(initial with { AssociationId = "changed" }, "arn:aws:iam::000000000000:instance-profile/new"));
    }

    [TestMethod]
    public async Task S3BulkDeletionBatchesOnlySelectedFilesAndPreservesFailedKeys()
    {
        var client = new Mock<Amazon.S3.IAmazonS3>();
        client.Setup(service => service.GetBucketLocationAsync(It.IsAny<Amazon.S3.Model.GetBucketLocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.GetBucketLocationResponse { Location = new Amazon.S3.S3Region("eu-west-1") });
        client.Setup(service => service.ListObjectsV2Async(It.IsAny<Amazon.S3.Model.ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response());
        var requests = new List<Amazon.S3.Model.DeleteObjectsRequest>();
        client.Setup(service => service.DeleteObjectsAsync(It.IsAny<Amazon.S3.Model.DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Amazon.S3.Model.DeleteObjectsRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync((Amazon.S3.Model.DeleteObjectsRequest request, CancellationToken _) =>
            {
                var response = new Amazon.S3.Model.DeleteObjectsResponse
                {
                    DeletedObjects = request.Objects.Where(item => item.Key != "folder//file-0").Select(item => new Amazon.S3.Model.DeletedObject { Key = item.Key }).ToList(),
                    DeleteErrors = request.Objects.Where(item => item.Key == "folder//file-0").Select(item => new Amazon.S3.Model.DeleteError { Key = item.Key, Code = "AccessDenied" }).ToList()
                };
                if (response.DeleteErrors.Count > 0) throw new Amazon.S3.DeleteObjectsException(response);
                return response;
            });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateS3Client(It.IsAny<string?>())).Returns(client.Object);
        var confirmations = new List<string>();
        var model = new S3ViewModel(factory.Object, false, message => { confirmations.Add(message); return true; });
        await ((AsyncRelayCommand)model.OpenItemCommand).ExecuteAsync(new AwsManager.Models.S3ItemModel { Name = "demo-bucket", ItemType = "Bucket" });
        var files = Enumerable.Range(0, 1001).Select(index => new AwsManager.Models.S3ItemModel { Key = $"folder//file-{index}", Name = $"file-{index}", ItemType = "File" }).ToArray();
        foreach (var file in files) model.Items.Add(file);
        var folder = new AwsManager.Models.S3ItemModel { Key = "subfolder/", ItemType = "Folder" };
        var unselected = new AwsManager.Models.S3ItemModel { Key = "unselected", ItemType = "File" };
        model.Items.Add(folder);
        model.Items.Add(unselected);
        model.SelectedFile = files[0];
        model.SetSelection(files.Concat([folder, model.Items[0]]));
        Assert.IsFalse(model.DownloadFileCommand.CanExecute(null));
        Assert.IsFalse(model.PresignFileCommand.CanExecute(null));
        Assert.IsTrue(model.DeleteFileCommand.CanExecute(null));
        await ((AsyncRelayCommand)model.DeleteFileCommand).ExecuteAsync(null);
        Assert.AreEqual(1, confirmations.Count);
        StringAssert.Contains(confirmations[0], "1001 fichier(s)");
        CollectionAssert.AreEqual(new[] { 1000, 1 }, requests.Select(request => request.Objects.Count).ToArray());
        Assert.IsTrue(requests.All(request => request.BucketName == "demo-bucket" && request.Quiet == false && request.Objects.All(item => item.VersionId == null)));
        Assert.IsTrue(model.Items.Contains(files[0]) && model.Items.Contains(folder) && model.Items.Contains(unselected));
        Assert.IsFalse(model.Items.Contains(files[1]));
        Assert.AreEqual(1, model.SelectedFileCount);
        StringAssert.Contains(model.Status, "AccessDenied");
        StringAssert.Contains(model.Status, "1000/1001");
    }

    [TestMethod]
    public async Task S3DeletionCancellationNeverCallsAwsDelete()
    {
        var client = new Mock<Amazon.S3.IAmazonS3>();
        client.Setup(service => service.GetBucketLocationAsync(It.IsAny<Amazon.S3.Model.GetBucketLocationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.S3.Model.GetBucketLocationResponse());
        client.Setup(service => service.ListObjectsV2Async(It.IsAny<Amazon.S3.Model.ListObjectsV2Request>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response());
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateS3Client(It.IsAny<string?>())).Returns(client.Object);
        var model = new S3ViewModel(factory.Object, false, _ => false);
        await ((AsyncRelayCommand)model.OpenItemCommand).ExecuteAsync(new AwsManager.Models.S3ItemModel { Name = "demo-bucket", ItemType = "Bucket" });
        var file = new AwsManager.Models.S3ItemModel { Key = "file", ItemType = "File" };
        model.Items.Add(file);
        model.SetSelection([file]);
        await model.DeleteFileAsync();
        Assert.IsTrue(model.Items.Contains(file));
        client.Verify(service => service.DeleteObjectsAsync(It.IsAny<Amazon.S3.Model.DeleteObjectsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task S3BulkDeletionStopsOnRequestFailureAndKeepsUnconfirmedFiles()
    {
        var client = new Mock<Amazon.S3.IAmazonS3>();
        client.Setup(service => service.GetBucketLocationAsync(It.IsAny<Amazon.S3.Model.GetBucketLocationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.S3.Model.GetBucketLocationResponse());
        client.Setup(service => service.ListObjectsV2Async(It.IsAny<Amazon.S3.Model.ListObjectsV2Request>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response());
        var calls = 0;
        client.Setup(service => service.DeleteObjectsAsync(It.IsAny<Amazon.S3.Model.DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Amazon.S3.Model.DeleteObjectsRequest request, CancellationToken _) =>
            {
                if (++calls == 2) throw new System.Net.Http.HttpRequestException("simulated failure");
                return new Amazon.S3.Model.DeleteObjectsResponse { DeletedObjects = request.Objects.Select(item => new Amazon.S3.Model.DeletedObject { Key = item.Key }).ToList() };
            });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateS3Client(It.IsAny<string?>())).Returns(client.Object);
        var model = new S3ViewModel(factory.Object, false, _ => true);
        await ((AsyncRelayCommand)model.OpenItemCommand).ExecuteAsync(new AwsManager.Models.S3ItemModel { Name = "demo-bucket", ItemType = "Bucket" });
        var files = Enumerable.Range(0, 2001).Select(index => new AwsManager.Models.S3ItemModel { Key = $"file-{index}", ItemType = "File" }).ToArray();
        foreach (var file in files) model.Items.Add(file);
        model.SetSelection(files);
        await model.DeleteFileAsync();
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1001, model.SelectedFileCount);
        Assert.IsTrue(model.Items.Contains(files[1000]) && model.Items.Contains(files[2000]));
        Assert.IsFalse(model.IsLoading);
        StringAssert.Contains(model.Status, "1000/2001");
        StringAssert.Contains(model.Status, "Actualisez");
    }

    private static Mock<Amazon.EC2.AmazonEC2Client> RuleClient()
    {
        var client = new Mock<Amazon.EC2.AmazonEC2Client>(new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.EUWest1) { CallBase = true };
        client.Setup(service => service.DescribeSecurityGroupsAsync(It.IsAny<Amazon.EC2.Model.DescribeSecurityGroupsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.DescribeSecurityGroupsResponse());
        client.Setup(service => service.DescribeSecurityGroupRulesAsync(It.IsAny<Amazon.EC2.Model.DescribeSecurityGroupRulesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.DescribeSecurityGroupRulesResponse());
        return client;
    }

    [TestMethod]
    public async Task SecurityRuleEditUsesOriginalIdWithoutRevocation()
    {
        var client = RuleClient();
        Amazon.EC2.Model.ModifySecurityGroupRulesRequest? submitted = null;
        client.Setup(service => service.ModifySecurityGroupRulesAsync(It.IsAny<Amazon.EC2.Model.ModifySecurityGroupRulesRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Amazon.EC2.Model.ModifySecurityGroupRulesRequest, CancellationToken>((request, _) => submitted = request).ReturnsAsync(new Amazon.EC2.Model.ModifySecurityGroupRulesResponse());
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateEc2Client()).Returns(client.Object);
        var original = new AwsManager.Models.SecurityGroupRuleModel { GroupId = "sg-0123456789abcdef0", RuleId = "sgr-0123456789abcdef0", Type = "Egress", Protocol = "tcp", PortRange = "443", SourceOrDestination = "2001:db8::/64", Description = "initial" };
        string? confirmation = null;
        var model = new SecurityGroupViewModel(factory.Object, false, editor =>
        {
            Assert.IsTrue(editor.IsEditing);
            Assert.IsTrue(editor.IsEgress);
            editor.From = "8443";
            editor.To = "8443";
            editor.Description = "updated";
            return true;
        }, message => { confirmation = message; return true; });
        await ((AsyncRelayCommand)model.EditRuleCommand).ExecuteAsync(original);
        Assert.IsNotNull(submitted);
        Assert.AreEqual(original.GroupId, submitted.GroupId);
        Assert.AreEqual(original.RuleId, submitted.SecurityGroupRules.Single().SecurityGroupRuleId);
        var rule = submitted.SecurityGroupRules.Single().SecurityGroupRule;
        Assert.AreEqual(8443, rule.FromPort);
        Assert.AreEqual("2001:db8::/64", rule.CidrIpv6);
        Assert.AreEqual("updated", rule.Description);
        StringAssert.Contains(confirmation!, "Avant");
        StringAssert.Contains(confirmation!, "Apres");
        Assert.AreEqual(0, client.Invocations.Count(call => call.Method.Name.StartsWith("Revoke", StringComparison.Ordinal)));
    }

    [DataTestMethod]
    [DataRow("Ingress")]
    [DataRow("Egress")]
    public async Task SecurityRuleAdditionUsesRequestedDirection(string direction)
    {
        var client = RuleClient();
        client.Setup(service => service.AuthorizeSecurityGroupIngressAsync(It.IsAny<Amazon.EC2.Model.AuthorizeSecurityGroupIngressRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.AuthorizeSecurityGroupIngressResponse());
        client.Setup(service => service.AuthorizeSecurityGroupEgressAsync(It.IsAny<Amazon.EC2.Model.AuthorizeSecurityGroupEgressRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.AuthorizeSecurityGroupEgressResponse());
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateEc2Client()).Returns(client.Object);
        var model = new SecurityGroupViewModel(factory.Object, false, editor =>
        {
            Assert.AreEqual(direction, editor.Direction);
            editor.SelectedPreset = editor.Presets.Single(preset => preset.Name == "Oracle");
            editor.SourceKind = "group";
            editor.Source = "sg-abcdef01234567890";
            return true;
        }, _ => true)
        { SelectedSecurityGroup = new() { GroupId = "sg-0123456789abcdef0" } };
        await ((AsyncRelayCommand)model.AddRuleCommand).ExecuteAsync(direction);
        var mutation = client.Invocations.Single(call => call.Method.Name.StartsWith("Authorize", StringComparison.Ordinal));
        Assert.AreEqual($"AuthorizeSecurityGroup{direction}Async", mutation.Method.Name);
        var permissions = mutation.Arguments[0] is Amazon.EC2.Model.AuthorizeSecurityGroupIngressRequest ingress ? ingress.IpPermissions : ((Amazon.EC2.Model.AuthorizeSecurityGroupEgressRequest)mutation.Arguments[0]).IpPermissions;
        Assert.AreEqual(1521, permissions.Single().FromPort);
        Assert.AreEqual("sg-abcdef01234567890", permissions.Single().UserIdGroupPairs.Single().GroupId);
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task SecurityRuleInvalidOrUnconfirmedNeverCreatesClient(bool valid, bool confirmed)
    {
        var factory = new Mock<IAwsClientFactory>(MockBehavior.Strict);
        var model = new SecurityGroupViewModel(factory.Object, false, editor =>
        {
            editor.SelectedPreset = editor.Presets[0];
            editor.Source = valid ? "10.0.0.0/8" : "invalid";
            return true;
        }, _ => confirmed)
        { SelectedSecurityGroup = new() { GroupId = "sg-0123456789abcdef0" } };
        await ((AsyncRelayCommand)model.AddRuleCommand).ExecuteAsync("Ingress");
        factory.Verify(service => service.CreateEc2Client(), Times.Never);
    }

    [TestMethod]
    public async Task RdsTagSaveWaitsForSuccessfulInitialRead()
    {
        var pending = new TaskCompletionSource<ListTagsForResourceResponse>();
        var client = new Mock<IAmazonRDS>();
        client.Setup(service => service.ListTagsForResourceAsync(It.IsAny<ListTagsForResourceRequest>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateRdsClient()).Returns(client.Object);
        var editor = new RdsTagEditorViewModel("demo", ResourceArn, factory.Object);
        Assert.IsFalse(editor.SaveChangesCommand.CanExecute(null));
        Assert.IsFalse(editor.CanEdit);
        pending.SetException(new InvalidOperationException("Lecture simulee indisponible"));
        await editor.LoadTask;
        Assert.IsFalse(editor.SaveChangesCommand.CanExecute(null));
        Assert.IsFalse(editor.CanEdit);
        client.Verify(service => service.AddTagsToResourceAsync(It.IsAny<AddTagsToResourceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RdsTagDeletionUsesOriginalKeysAndActualArn()
    {
        var client = new Mock<IAmazonRDS>();
        client.Setup(service => service.ListTagsForResourceAsync(It.IsAny<ListTagsForResourceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListTagsForResourceResponse { TagList = [new Tag { Key = "obsolete", Value = "old" }, new Tag { Key = "aws:reserved", Value = "unchanged" }] });
        client.Setup(service => service.RemoveTagsFromResourceAsync(It.IsAny<RemoveTagsFromResourceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RemoveTagsFromResourceResponse());
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateRdsClient()).Returns(client.Object);
        var editor = new RdsTagEditorViewModel("demo", ResourceArn, factory.Object);
        await editor.LoadTask;
        Assert.AreEqual(1, editor.Tags.Count);
        editor.Tags.Clear();
        await ((AsyncRelayCommand)editor.SaveChangesCommand).ExecuteAsync(null);
        client.Verify(service => service.RemoveTagsFromResourceAsync(It.Is<RemoveTagsFromResourceRequest>(request => request.ResourceName == ResourceArn && request.TagKeys.Count == 1 && request.TagKeys[0] == "obsolete"), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(service => service.AddTagsToResourceAsync(It.IsAny<AddTagsToResourceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RdsLoadsAllPagesAndToleratesMissingOptionalFields()
    {
        var client = new Mock<AmazonRDSClient>(new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.EUWest1) { CallBase = true };
        client.Setup(service => service.DescribeDBInstancesAsync(It.Is<DescribeDBInstancesRequest>(request => request.Marker == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeDBInstancesResponse { DBInstances = [new DBInstance { DBInstanceIdentifier = "first" }], Marker = "next" });
        client.Setup(service => service.DescribeDBInstancesAsync(It.Is<DescribeDBInstancesRequest>(request => request.Marker == "next"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeDBInstancesResponse { DBInstances = [new DBInstance { DBInstanceIdentifier = "second" }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateRdsClient()).Returns(client.Object);
        var model = new RdsViewModel(factory.Object, false);
        await ((AsyncRelayCommand)model.RefreshCommand).ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { "first", "second" }, model.Instances.Select(instance => instance.DbInstanceIdentifier).ToArray());
        Assert.IsFalse(model.IsLoading);
    }

    [TestMethod]
    public async Task RdsRelayInventoryPaginatesAndExcludesOfflineOrOldAgents()
    {
        var ssm = new Mock<Amazon.SimpleSystemsManagement.IAmazonSimpleSystemsManagement>();
        ssm.Setup(service => service.DescribeInstanceInformationAsync(It.Is<Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationRequest>(request => request.NextToken == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationResponse
            {
                NextToken = "next",
                InstanceInformationList =
            [
                new() { InstanceId = "i-aaaaaaaa", PingStatus = "Online", AgentVersion = "3.1.1374.0" },
                new() { InstanceId = "i-bbbbbbbb", PingStatus = "Online", AgentVersion = "3.1.1000.0" }
            ]
            });
        ssm.Setup(service => service.DescribeInstanceInformationAsync(It.Is<Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationRequest>(request => request.NextToken == "next"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationResponse
            {
                InstanceInformationList =
            [new() { InstanceId = "i-cccccccc", PingStatus = "ConnectionLost", AgentVersion = "3.3.0.0" }, new() { InstanceId = "i-dddddddd", PingStatus = "Online", AgentVersion = "3.3.0.0" }]
            });
        var ec2 = new Mock<Amazon.EC2.IAmazonEC2>();
        ec2.Setup(service => service.DescribeInstancesAsync(It.Is<Amazon.EC2.Model.DescribeInstancesRequest>(request => request.NextToken == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse
            {
                NextToken = "next",
                Reservations = [new() { Instances =
            [new() { InstanceId = "i-aaaaaaaa", State = new() { Name = "running" } }, new() { InstanceId = "i-bbbbbbbb", State = new() { Name = "running" } }] }]
            });
        ec2.Setup(service => service.DescribeInstancesAsync(It.Is<Amazon.EC2.Model.DescribeInstancesRequest>(request => request.NextToken == "next"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse
            {
                Reservations = [new() { Instances =
            [new() { InstanceId = "i-cccccccc", State = new() { Name = "running" } }, new() { InstanceId = "i-dddddddd", State = new() { Name = "running" } }] }]
            });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateEc2Client()).Returns(ec2.Object);
        factory.Setup(service => service.CreateSsmClient()).Returns(ssm.Object);
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "fake", new Amazon.Runtime.AnonymousAWSCredentials());
        var relays = await new RdsTunnelBackend(factory.Object, context).LoadRelaysAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "i-aaaaaaaa", "i-dddddddd" }, relays.Select(relay => relay.Id).ToArray());
        Assert.IsFalse(RdsTunnelBackend.IsCompatibleAgent("unknown"));
    }

    [TestMethod]
    public async Task RdsTunnelRefusesChangedEndpointBeforeLaunchingAnything()
    {
        var rds = new Mock<IAmazonRDS>();
        rds.Setup(service => service.DescribeDBInstancesAsync(It.IsAny<DescribeDBInstancesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeDBInstancesResponse { DBInstances = [new() { DBInstanceIdentifier = "demo", DBInstanceStatus = "available", Endpoint = new() { Address = "new.example.test", Port = 5432 } }] });
        var factory = new Mock<IAwsClientFactory>(MockBehavior.Strict);
        factory.Setup(service => service.CreateRdsClient()).Returns(rds.Object);
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "fake", new Amazon.Runtime.AnonymousAWSCredentials());
        var service = new RdsTunnelBackend(factory.Object, context);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.OpenAsync(new("demo", "old.example.test", 5432, "available", "vpc-demo"), new("i-aaa", "relay", "vpc-demo", "3.3.0.0"), 15432, CancellationToken.None));
        factory.Verify(service => service.CreateEc2Client(), Times.Never);
    }

    [TestMethod]
    public async Task DnsRefreshWaitsForRestoredZoneRecords()
    {
        var client = new Mock<Amazon.Route53.AmazonRoute53Client>(new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.EUWest1) { CallBase = true };
        var records = new TaskCompletionSource<Amazon.Route53.Model.ListResourceRecordSetsResponse>();
        client.Setup(service => service.ListHostedZonesAsync(It.IsAny<Amazon.Route53.Model.ListHostedZonesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.Route53.Model.ListHostedZonesResponse { HostedZones = [new Amazon.Route53.Model.HostedZone { Id = "zone", Name = "example.test." }] });
        client.SetupSequence(service => service.ListResourceRecordSetsAsync(It.IsAny<Amazon.Route53.Model.ListResourceRecordSetsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.Route53.Model.ListResourceRecordSetsResponse { ResourceRecordSets = [] })
            .Returns(records.Task);
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateRoute53Client()).Returns(client.Object);
        var model = new Route53ViewModel(factory.Object) { SelectedHostedZone = new AwsManager.Models.HostedZoneModel { Id = "zone" } };
        var refresh = ((AsyncRelayCommand)model.RefreshCommand).ExecuteAsync(null);
        Assert.IsTrue(model.IsLoading);
        Assert.IsFalse(model.CreateRecordCommand.CanExecute(null));
        records.SetResult(new Amazon.Route53.Model.ListResourceRecordSetsResponse { ResourceRecordSets = [] });
        await refresh;
        Assert.IsFalse(model.IsLoading);
        Assert.AreEqual("zone", model.SelectedHostedZone?.Id);
    }
}