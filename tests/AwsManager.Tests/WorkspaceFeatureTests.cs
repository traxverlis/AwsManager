using AwsManager.Models;
using AwsManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.IO;

namespace AwsManager.Tests;

[TestClass]
public class WorkspaceFeatureTests
{
    private string _folder = "";
    [TestInitialize] public void Initialize() => _folder = Path.Combine(Path.GetTempPath(), "AwsManager-tests-" + Guid.NewGuid());
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
    private string StorePath => Path.Combine(_folder, "workspace.json");
    private static ResourceReference Resource(string account = "000000000000") => new("demo", account, "eu-west-1", "EC2", "i-demo", "Demo");

    [TestMethod]
    public async Task PermissionsResolveSsoRolePathAndKeepIncompleteDecisionsUnknown()
    {
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "arn:aws:sts::000000000000:assumed-role/Demo/session", new Amazon.Runtime.AnonymousAWSCredentials());
        var iam = new Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>();
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(item => item.CreateIamClient()).Returns(iam.Object);
        const string roleArn = "arn:aws:iam::000000000000:role/aws-reserved/sso.amazonaws.com/eu-west-1/Demo";
        iam.Setup(item => item.GetRoleAsync(It.Is<Amazon.IdentityManagement.Model.GetRoleRequest>(request => request.RoleName == "Demo"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.IdentityManagement.Model.GetRoleResponse { Role = new() { RoleName = "Demo", Arn = roleArn } });
        iam.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest request, CancellationToken token) =>
            {
                Assert.AreEqual(roleArn, request.PolicySourceArn);
                Assert.AreEqual("eu-west-1", request.ContextEntries.Single(item => item.ContextKeyName == "aws:RequestedRegion").ContextKeyValues.Single());
                return new()
                {
                    EvaluationResults = [
                    new() { EvalActionName = "ec2:DescribeInstances", EvalResourceName = "*", EvalDecision = "allowed" },
                    new() { EvalActionName = "ec2:StopInstances", EvalResourceName = "*", EvalDecision = "explicitDeny" },
                    new() { EvalActionName = "ec2:StartInstances", EvalResourceName = "*", EvalDecision = "allowed", MissingContextValues = ["aws:ResourceTag/Team"] }
                ]
                };
            });
        var checks = new[] { new PermissionCheck("ec2:DescribeInstances"), new("ec2:StopInstances"), new("ec2:StartInstances"), new("ec2:TerminateInstances") };
        var results = await new PermissionService(factory.Object, context).EvaluateAsync(checks, CancellationToken.None);
        Assert.AreEqual(PermissionState.Allowed, results[checks[0]].State);
        Assert.AreEqual(PermissionState.Denied, results[checks[1]].State);
        Assert.AreEqual(PermissionState.Unknown, results[checks[2]].State);
        Assert.AreEqual(PermissionState.Unknown, results[checks[3]].State);
        Assert.IsTrue(OperationSafety.IsReadOperation("SimulatePrincipalPolicy"));
    }

    [DataTestMethod]
    [DataRow("*", "arn:${Partition}:iam::${Account}:role/${RoleNameWithPath}", false, "allowed", "allowed", false, PermissionState.Allowed)]
    [DataRow("*", null, false, "allowed", "allowed", false, PermissionState.Allowed)]
    [DataRow("*", "arn:aws:iam::000000000000:role/", false, "allowed", "allowed", false, PermissionState.Allowed)]
    [DataRow("*", "arn:aws:route53::000000000000:hostedzone/*", false, "allowed", "allowed", false, PermissionState.Allowed)]
    [DataRow("*", "arn:aws:iam::000000000000:role/", false, "explicitDeny", "allowed", false, PermissionState.Denied)]
    [DataRow("*", "arn:aws:iam::000000000000:role/", true, "allowed", "allowed", true, PermissionState.Unknown)]
    [DataRow("arn:aws:iam::000000000000:role/Target", "arn:${Partition}:iam::${Account}:role/${RoleNameWithPath}", true, "allowed", "allowed", false, PermissionState.Allowed)]
    [DataRow("arn:aws:iam::000000000000:role/Target", null, false, "allowed", "allowed", false, PermissionState.Allowed)]
    [DataRow("arn:aws:iam::000000000000:role/Target", "*", true, "allowed", "implicitDeny", false, PermissionState.Denied)]
    [DataRow("arn:aws:iam::000000000000:role/Target", "*", true, "explicitDeny", "allowed", false, PermissionState.Denied)]
    [DataRow("arn:aws:iam::000000000000:role/Target", "*", true, "allowed", "allowed", true, PermissionState.Unknown)]
    [DataRow("arn:aws:iam::000000000000:role/Target", "arn:aws:iam::000000000000:role/Other", false, "allowed", "allowed", false, PermissionState.Unknown)]
    public async Task PermissionsReadAggregateAndResourceSpecificResults(string resource, string? summaryResource, bool hasDetails,
        string decision, string resourceDecision, bool missingContext, PermissionState expected)
    {
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "arn:aws:iam::000000000000:user/Demo", new Amazon.Runtime.AnonymousAWSCredentials());
        var factory = new Mock<IAwsClientFactory>();
        var iam = SetupPermissions(factory);
        iam.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest request, CancellationToken token) =>
            {
                Assert.AreEqual(resource, request.ResourceArns.Single());
                return new()
                {
                    EvaluationResults = [new()
                    {
                        EvalActionName = request.ActionNames.Single(), EvalResourceName = summaryResource, EvalDecision = decision,
                        ResourceSpecificResults = hasDetails ? [new()
                        {
                            EvalResourceName = resource, EvalResourceDecision = resourceDecision,
                            MissingContextValues = missingContext ? ["aws:ResourceTag/Team"] : []
                        }] : []
                    }]
                };
            });
        var check = new PermissionCheck("iam:PassRole", resource, "iam:PassedToService", "ec2.amazonaws.com");
        var results = await new PermissionService(factory.Object, context).EvaluateAsync([check], CancellationToken.None);
        Assert.AreEqual(expected, results[check].State);
        if (missingContext) StringAssert.Contains(results[check].Reason, "aws:ResourceTag/Team");
    }

    [TestMethod]
    public async Task PermissionsDoNotUseAnotherResourcesDecision()
    {
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "arn:aws:iam::000000000000:user/Demo", new Amazon.Runtime.AnonymousAWSCredentials());
        var factory = new Mock<IAwsClientFactory>();
        var iam = SetupPermissions(factory);
        iam.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.IdentityManagement.Model.SimulatePrincipalPolicyResponse
            {
                EvaluationResults = [new()
                {
                    EvalActionName = "iam:PassRole", EvalResourceName = "arn:${Partition}:iam::${Account}:role/${RoleNameWithPath}", EvalDecision = "allowed",
                    ResourceSpecificResults = [new() { EvalResourceName = "arn:aws:iam::000000000000:role/Other", EvalResourceDecision = "allowed" }]
                }]
            });
        var check = new PermissionCheck("iam:PassRole", "arn:aws:iam::000000000000:role/Target");
        var results = await new PermissionService(factory.Object, context).EvaluateAsync([check], CancellationToken.None);
        Assert.AreEqual(PermissionState.Unknown, results[check].State);
    }

    [TestMethod]
    public async Task PermissionsEvaluateExactResourcesAndRejectBrokenPagination()
    {
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "arn:aws:iam::000000000000:user/Demo", new Amazon.Runtime.AnonymousAWSCredentials());
        var iam = new Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>();
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(item => item.CreateIamClient()).Returns(iam.Object);
        var check = new PermissionCheck("iam:PassRole", "arn:aws:iam::000000000000:role/Target", "iam:PassedToService", "ec2.amazonaws.com");
        iam.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest request, CancellationToken token) =>
            {
                Assert.AreEqual(check.Resource, request.ResourceArns.Single());
                Assert.AreEqual(check.ContextValue, request.ContextEntries.Single(item => item.ContextKeyName == check.ContextKey).ContextKeyValues.Single());
                return new() { IsTruncated = true, Marker = "repeated", EvaluationResults = [] };
            });
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new PermissionService(factory.Object, context).EvaluateAsync([check], CancellationToken.None));
        iam.Verify(item => item.GetRoleAsync(It.IsAny<Amazon.IdentityManagement.Model.GetRoleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task PermissionsCacheIsContextScopedAndRejectsLateResults()
    {
        var context = new AwsContext("demo", "eu-west-1", "000000000000", "arn:aws:iam::000000000000:user/Demo", new Amazon.Runtime.AnonymousAWSCredentials());
        var iam = new Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>();
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(item => item.CreateIamClient()).Returns(iam.Object);
        var response = new TaskCompletionSource<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyResponse>();
        iam.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>())).Returns(response.Task);
        var store = new WorkspaceStore(StorePath);
        using var gate = new PermissionGate(context, factory.Object, store);
        var check = new PermissionCheck("ec2:StopInstances");
        var pending = gate.CheckAsync([check]);
        Assert.IsFalse(gate.Allows([check]));
        Assert.IsNull(PermissionGate.For(context with { Region = "us-east-1" }));
        gate.Dispose();
        response.SetResult(new() { EvaluationResults = [new() { EvalActionName = check.Action, EvalResourceName = "*", EvalDecision = "allowed" }] });
        Assert.IsFalse(await pending);
        Assert.IsFalse(gate.Allows([check]));
        var executed = false;
        new AwsManager.ViewModels.RelayCommand(_ => executed = true, _ => false).Execute(null);
        Assert.IsFalse(executed);
    }

    private sealed class Backend : IAwsSessionBackend
    {
        public IReadOnlyList<AwsProfile> ListProfiles() => [new("demo", "eu-west-1", true)];
        public Task LoginAsync(string profile, CancellationToken cancellationToken) => throw new AssertFailedException();
        public Task<AwsContext> VerifyAsync(string profile, string region, CancellationToken cancellationToken) =>
            Task.FromResult(new AwsContext(profile, region, "000000000000", "arn:aws:iam::000000000000:user/Demo", new Amazon.Runtime.AnonymousAWSCredentials()));
    }
    private sealed class NoNetwork : Amazon.Runtime.HttpClientFactory
    {
        public int Calls { get; private set; }
        public override System.Net.Http.HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig)
        {
            Calls++;
            throw new AssertFailedException("Aucune requete reseau permise.");
        }
    }

    internal static Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService> SetupPermissions(Mock<IAwsClientFactory> factory,
        Func<string, string, string>? decision = null, Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>? client = null)
    {
        client ??= new();
        factory.Setup(item => item.CreateIamClient()).Returns(client.Object);
        client.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest request, CancellationToken token) => new()
            {
                EvaluationResults = request.ActionNames.Select(action => new Amazon.IdentityManagement.Model.EvaluationResult
                {
                    EvalActionName = action,
                    EvalResourceName = action switch
                    {
                        "iam:ListRoles" => "arn:aws:iam::000000000000:role/",
                        "iam:ListInstanceProfiles" => "arn:aws:iam::000000000000:instance-profile/",
                        "iam:ListPolicies" => "arn:aws:iam::000000000000:policy/",
                        "iam:ListUsers" => "arn:aws:iam::000000000000:user/",
                        "iam:ListGroups" => "arn:aws:iam::000000000000:group/",
                        "route53:ListHostedZones" => "arn:aws:route53::000000000000:hostedzone/*",
                        _ => "arn:${Partition}:service:${Region}:${Account}:resource/${Id}"
                    },
                    EvalDecision = decision?.Invoke(action, request.ResourceArns.Single()) ?? "allowed",
                    ResourceSpecificResults = request.ResourceArns.Single() == "*" ? [] : [new()
                    {
                        EvalResourceName = request.ResourceArns.Single(),
                        EvalResourceDecision = decision?.Invoke(action, request.ResourceArns.Single()) ?? "allowed"
                    }]
                }).ToList()
            });
        return client;
    }

    [TestMethod]
    public async Task PermissionsNavigationRecognizesAllAggregateDecisions()
    {
        var session = new AwsSessionService(new Backend());
        var factory = new Mock<IAwsClientFactory>();
        SetupPermissions(factory);
        using var main = new AwsManager.ViewModels.MainViewModel(session, new WorkspaceStore(StorePath), () => factory.Object);
        main.SelectedPage = main.Pages.Single(page => page.Name == "Sessions");
        await session.ConnectAsync("demo", "eu-west-1", false);
        await main.PermissionsReady;
        Assert.IsTrue(main.Pages.All(page => page.IsEnabled));
        StringAssert.Contains(main.PermissionStatus, "15 autorises, 0 refuses, 0 non verifies");
        factory.Verify(item => item.CreateEc2Client(), Times.Never);
    }

    [TestMethod]
    public async Task PermissionsGateEc2ByTargetAndReadOnlyWithoutExecutingDeniedActions()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        store.SetReadOnly(false);
        var factory = new Mock<IAwsClientFactory>();
        SetupPermissions(factory, (action, resource) => action == "ec2:DescribeInstances" ||
            action == "ec2:StartInstances" && resource.EndsWith("/i-allowed") ? "allowed" : "implicitDeny");
        using var gate = new PermissionGate(session.Context!, factory.Object, store);
        var model = new AwsManager.ViewModels.Ec2ViewModel(factory.Object, false, session.Context)
        { SelectedInstance = new() { InstanceId = "i-allowed", State = "stopped" } };
        Assert.IsFalse(model.StartInstanceCommand.CanExecute(null));
        Assert.IsTrue(await gate.CheckAsync([new("ec2:StartInstances", "arn:aws:ec2:eu-west-1:000000000000:instance/i-allowed"), new("ec2:DescribeInstances")]));
        Assert.IsTrue(model.StartInstanceCommand.CanExecute(null));
        store.SetReadOnly(true);
        Assert.IsFalse(model.StartInstanceCommand.CanExecute(null));
        Assert.IsTrue(model.RefreshCommand.CanExecute(null));
        store.SetReadOnly(false);
        model.SelectedInstance = new() { InstanceId = "i-denied", State = "stopped" };
        Assert.IsFalse(await gate.CheckAsync([new("ec2:StartInstances", "arn:aws:ec2:eu-west-1:000000000000:instance/i-denied")]));
        await ((AwsManager.ViewModels.AsyncRelayCommand)model.StartInstanceCommand).ExecuteAsync(null);
        Assert.IsFalse(model.StartInstanceCommand.CanExecute(null));
        factory.Verify(item => item.CreateEc2Client(), Times.Never);
        gate.Dispose();
        Assert.IsFalse(model.RefreshCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task PermissionsUnavailableNeverEnableActionsOrRetryAutomatically()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var factory = new Mock<IAwsClientFactory>();
        var iam = SetupPermissions(factory);
        iam.Setup(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.IdentityManagement.AmazonIdentityManagementServiceException("secret message") { ErrorCode = "AccessDenied" });
        using var gate = new PermissionGate(session.Context!, factory.Object, new WorkspaceStore(StorePath));
        Assert.IsFalse(await gate.CheckAsync([new("ec2:DescribeInstances")]));
        Assert.IsFalse(await gate.CheckAsync([new("s3:ListAllMyBuckets")]));
        Assert.IsFalse(gate.Status.Contains("secret message"));
        StringAssert.Contains(gate.Status, "non verifies");
        iam.Verify(item => item.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PermissionsNavigationAllowsOnlyGrantedIamCategoryAndClearsOnContextChange()
    {
        var session = new AwsSessionService(new Backend());
        var factory = new Mock<IAwsClientFactory>();
        var iam = SetupPermissions(factory, (action, _) => action == "iam:ListUsers" ? "allowed" : "implicitDeny");
        iam.Setup(item => item.ListUsersAsync(It.IsAny<Amazon.IdentityManagement.Model.ListUsersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.IdentityManagement.Model.ListUsersResponse { Users = [] });
        using var main = new AwsManager.ViewModels.MainViewModel(session, new WorkspaceStore(StorePath), () => factory.Object);
        await session.ConnectAsync("demo", "eu-west-1", false);
        await main.PermissionsReady;
        Assert.IsTrue(main.Pages.Single(page => page.Name == "IAM").IsEnabled);
        Assert.IsFalse(main.Pages.Single(page => page.Name == "EC2").IsEnabled);
        var model = (AwsManager.ViewModels.IamViewModel)main.CurrentViewModel!;
        Assert.AreEqual("Users", model.SelectedCategory.Id);
        Assert.AreEqual(1, model.Categories.Count(category => category.IsEnabled));
        iam.Verify(item => item.ListRolesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListRolesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        main.SelectedRegion = "us-east-1";
        Assert.IsFalse(main.Pages.Single(page => page.Name == "IAM").IsEnabled);
        Assert.IsTrue(main.Pages.Single(page => page.Name == "Sessions").IsEnabled);
        Assert.IsTrue(main.Pages.Single(page => page.Name == "Mes accès").IsEnabled);
        Assert.IsFalse(model.RefreshCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ReadOnlyBlocksSdkMutationBeforeTransportAndLogsNoPayload()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        var transport = new NoNetwork();
        using var client = OperationSafety.Attach(new Amazon.EC2.AmazonEC2Client(session.Context!.Credentials,
            new Amazon.EC2.AmazonEC2Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => client.StopInstancesAsync(new Amazon.EC2.Model.StopInstancesRequest { InstanceIds = ["i-demo"] }));
        Assert.AreEqual(0, transport.Calls);
        Assert.AreEqual("Bloquee (lecture seule)", store.Snapshot.History.Single().Result);
        Assert.IsTrue(OperationSafety.IsReadOperation("DescribeInstances"));
        Assert.IsFalse(OperationSafety.IsReadOperation("StartSession"));
        Assert.ThrowsException<ReadOnlyModeException>(() => OperationSafety.EnsureWritable(session.Context, "SSM", "StartSession", "i-demo", store));
    }

    [TestMethod]
    public async Task ReadOnlyBlocksBulkDeletionAndRuleEditingBeforeTransport()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        var transport = new NoNetwork();
        using var s3 = OperationSafety.Attach(new Amazon.S3.AmazonS3Client(session.Context!.Credentials,
            new Amazon.S3.AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => s3.DeleteObjectsAsync(new Amazon.S3.Model.DeleteObjectsRequest
        { BucketName = "bucket-demo", Objects = [new() { Key = "first" }, new() { Key = "second" }] }));
        using var ec2 = OperationSafety.Attach(new Amazon.EC2.AmazonEC2Client(session.Context.Credentials,
            new Amazon.EC2.AmazonEC2Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => ec2.ModifySecurityGroupRulesAsync(new Amazon.EC2.Model.ModifySecurityGroupRulesRequest
        { GroupId = "sg-0123456789abcdef0", SecurityGroupRules = [new() { SecurityGroupRuleId = "sgr-0123456789abcdef0", SecurityGroupRule = SecurityRuleEditor.Parse("tcp", "443", "10.0.0.0/8", "") }] }));
        Assert.AreEqual(0, transport.Calls);
        Assert.AreEqual(2, store.Snapshot.History.Length);
    }

    [TestMethod]
    public async Task ReadOnlyBlocksPatchingAndInstanceProfileChangesBeforeTransport()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        var transport = new NoNetwork();
        using var ssm = OperationSafety.Attach(new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(session.Context!.Credentials,
            new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementConfig { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        foreach (var operation in new[] { "Scan", "Install" })
            await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => ssm.SendCommandAsync(new Amazon.SimpleSystemsManagement.Model.SendCommandRequest
            { DocumentName = PatchService.DocumentName, InstanceIds = ["i-0123456789abcdef0"], Parameters = new() { ["Operation"] = [operation] } }));
        using var ec2 = OperationSafety.Attach(new Amazon.EC2.AmazonEC2Client(session.Context.Credentials,
            new Amazon.EC2.AmazonEC2Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => ec2.AssociateIamInstanceProfileAsync(new Amazon.EC2.Model.AssociateIamInstanceProfileRequest
        { InstanceId = "i-0123456789abcdef0", IamInstanceProfile = new() { Name = "demo" } }));
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => ec2.ReplaceIamInstanceProfileAssociationAsync(new Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationRequest
        { AssociationId = "iip-assoc-0123456789abcdef0", IamInstanceProfile = new() { Name = "demo" } }));
        Assert.AreEqual(0, transport.Calls);
        Assert.AreEqual(4, store.Snapshot.History.Length);
    }

    [TestMethod]
    public async Task ReadOnlyAllowsLogFilteringWithoutPersistingContent()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        store.SetReadOnly(true);
        var transport = new ResponseTransport { Body = "{\"events\":[{\"eventId\":\"1\",\"timestamp\":1,\"message\":\"private-log-content\"}]}" };
        using var client = OperationSafety.Attach(new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(session.Context!.Credentials,
            new Amazon.CloudWatchLogs.AmazonCloudWatchLogsConfig { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        var response = await client.FilterLogEventsAsync(new() { LogGroupName = "/aws/demo", FilterPattern = "private-filter" });
        Assert.AreEqual("private-log-content", response.Events.Single().Message);
        Assert.AreEqual(0, store.Snapshot.History.Length);
        Assert.IsFalse(OperationSafety.IsReadOperation("PutLogEvents"));
        Assert.IsFalse(File.ReadAllText(StorePath).Contains("private-"));
    }

    [TestMethod]
    public async Task SsmPresetRoundTripDoesNotStartSessionAndRejectsOtherAccount()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        var model = new AwsManager.ViewModels.SsmConnectionViewModel(new Ec2InstanceModel { InstanceId = "i-demo", Name = "Demo" }, store, session.Context)
        { PresetName = "Base recette", SelectedMode = 2 };
        model.CustomRules.Add(new PortForwardingRule { RemotePort = 5432, LocalPort = 15432 });
        await ((AwsManager.ViewModels.AsyncRelayCommand)model.SavePresetCommand).ExecuteAsync(null);
        var preset = new WorkspaceStore(StorePath).Snapshot.Connections.Single();
        model.CustomRules.Clear();
        model.ApplyPreset(preset);
        Assert.AreEqual(15432, model.CustomRules.Single().LocalPort);
        Assert.IsFalse(model.StartCustomTunnelCommand.CanExecute(null));
        Assert.AreEqual(0, SessionTrackingService.Instance.ActiveSessions.Count);
        Assert.ThrowsException<InvalidOperationException>(() => model.ApplyPreset(preset with { Target = preset.Target with { Account = "111111111111" } }));
        Assert.IsFalse(ResourceNavigation.MatchesContext(preset.Target with { Region = "us-east-1" }, session.Context));
    }

    [TestMethod]
    public void FavoritesAndRecentArePersistentAndAccountScoped()
    {
        var store = new WorkspaceStore(StorePath);
        Assert.IsTrue(store.Snapshot.IsReadOnly);
        store.ToggleFavorite(Resource());
        store.ToggleFavorite(Resource("111111111111"));
        store.Visit(Resource());
        store.Visit(Resource());
        store.SetReadOnly(false);
        var restored = new WorkspaceStore(StorePath);
        Assert.AreEqual(2, restored.Snapshot.Favorites.Length);
        Assert.AreEqual(1, restored.Snapshot.Recent.Length);
        Assert.IsFalse(restored.Snapshot.IsReadOnly);
        restored.ToggleFavorite(Resource());
        Assert.AreEqual("111111111111", restored.Snapshot.Favorites.Single().Account);
        Assert.IsFalse(File.ReadAllText(StorePath).Contains("Credentials"));
    }

    [TestMethod]
    public void CorruptStorageIsPreservedAndFailsClosed()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, "invalid-json");
        var store = new WorkspaceStore(StorePath);
        Assert.IsTrue(store.Snapshot.IsReadOnly);
        Assert.ThrowsException<InvalidOperationException>(() => store.SetReadOnly(false));
        Assert.AreEqual("invalid-json", File.ReadAllText(StorePath));
    }

    [TestMethod]
    public void SavedConnectionsValidatePortsAndReplaceById()
    {
        var store = new WorkspaceStore(StorePath);
        var connection = new SavedConnection(Guid.NewGuid(), "Base recette", Resource(), "Tunnels", [new(5432, 15432)]);
        store.SaveConnection(connection);
        store.SaveConnection(connection with { Name = "Base" });
        Assert.AreEqual("Base", new WorkspaceStore(StorePath).Snapshot.Connections.Single().Name);
        Assert.ThrowsException<ArgumentException>(() => store.SaveConnection(connection with { Tunnels = [new(0, 15432)] }));
        store.RemoveConnection(connection.Id);
        Assert.AreEqual(0, store.Snapshot.Connections.Length);
    }

    [TestMethod]
    public async Task RelationsPreserveContextAndLoadAllInstancePages()
    {
        var client = new Moq.Mock<Amazon.EC2.IAmazonEC2>();
        client.Setup(service => service.DescribeInstancesAsync(Moq.It.Is<Amazon.EC2.Model.DescribeInstancesRequest>(request => request.InstanceIds != null), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse
            {
                Reservations = [new() { Instances = [new()
            {
                InstanceId = "i-demo", SecurityGroups = [new() { GroupId = "sg-demo", GroupName = "application" }],
                Tags = [new("aws:autoscaling:groupName", "asg-demo")]
            }] }]
            });
        client.Setup(service => service.DescribeInstancesAsync(Moq.It.Is<Amazon.EC2.Model.DescribeInstancesRequest>(request => request.Filters != null && request.NextToken == null), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { NextToken = "next", Reservations = [new() { Instances = [new() { InstanceId = "i-first" }] }] });
        client.Setup(service => service.DescribeInstancesAsync(Moq.It.Is<Amazon.EC2.Model.DescribeInstancesRequest>(request => request.NextToken == "next"), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { Reservations = [new() { Instances = [new() { InstanceId = "i-second" }] }] });
        var factory = new Moq.Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateEc2Client()).Returns(client.Object);
        var service = new ResourceRelationsService(factory.Object);
        var related = await service.LoadAsync(Resource(), CancellationToken.None);
        Assert.AreEqual(2, related.Count);
        Assert.IsTrue(related.All(item => item.Account == Resource().Account && item.Profile == Resource().Profile && item.Region == Resource().Region));
        var instances = await service.LoadAsync(Resource() with { Service = "Groupes de sécurité", Id = "sg-demo" }, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "i-first", "i-second" }, instances.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task MonitoringUsesResourceDimensionsAndAllPagesWithoutInventingZeroes()
    {
        var end = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        var cloud = new Mock<Amazon.CloudWatch.IAmazonCloudWatch>();
        cloud.Setup(service => service.GetMetricDataAsync(It.Is<Amazon.CloudWatch.Model.GetMetricDataRequest>(request => request.NextToken == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.CloudWatch.Model.GetMetricDataResponse
            {
                NextToken = "next",
                MetricDataResults = [new()
            {
                Id = "cpu", StatusCode = Amazon.CloudWatch.StatusCode.Complete, Timestamps = [end.AddMinutes(-5)], Values = [20]
            }]
            });
        cloud.Setup(service => service.GetMetricDataAsync(It.Is<Amazon.CloudWatch.Model.GetMetricDataRequest>(request => request.NextToken == "next"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.CloudWatch.Model.GetMetricDataResponse
            {
                MetricDataResults = [new()
            {
                Id = "cpu", StatusCode = Amazon.CloudWatch.StatusCode.PartialData, Timestamps = [end.AddMinutes(-10)], Values = [10]
            }]
            });
        cloud.Setup(service => service.DescribeAlarmsAsync(It.Is<Amazon.CloudWatch.Model.DescribeAlarmsRequest>(request => request.NextToken == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.CloudWatch.Model.DescribeAlarmsResponse
            {
                NextToken = "next",
                MetricAlarms = [new()
            {
                AlarmName = "other", Namespace = "AWS/EC2", Dimensions = [new() { Name = "InstanceId", Value = "other" }]
            }]
            });
        cloud.Setup(service => service.DescribeAlarmsAsync(It.Is<Amazon.CloudWatch.Model.DescribeAlarmsRequest>(request => request.NextToken == "next"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.CloudWatch.Model.DescribeAlarmsResponse
            {
                MetricAlarms = [new()
            {
                AlarmName = "cpu-demo", Namespace = "AWS/EC2", Dimensions = [new() { Name = "InstanceId", Value = "i-demo" }], StateValue = Amazon.CloudWatch.StateValue.ALARM
            }]
            });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchClient()).Returns(cloud.Object);
        var monitoring = new MonitoringService(factory.Object);
        var metrics = await monitoring.LoadMetricsAsync(Resource(), 1, end, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 10d, 20d }, metrics[0].Samples.Select(sample => sample.Value).ToArray());
        Assert.IsTrue(metrics[0].IsPartial);
        Assert.AreEqual(0, metrics[1].Samples.Count);
        cloud.Verify(service => service.GetMetricDataAsync(It.Is<Amazon.CloudWatch.Model.GetMetricDataRequest>(request =>
            request.MetricDataQueries.All(query => query.MetricStat.Period == 300 && query.MetricStat.Metric.Namespace == "AWS/EC2" &&
                query.MetricStat.Metric.Dimensions.Single().Name == "InstanceId" && query.MetricStat.Metric.Dimensions.Single().Value == "i-demo") &&
            request.StartTime == end.AddHours(-1) && request.EndTime == end), It.IsAny<CancellationToken>()), Times.Exactly(2));
        var alarms = await monitoring.LoadAlarmsAsync(Resource(), CancellationToken.None);
        Assert.AreEqual("cpu-demo", alarms.Single().Name);
        Assert.AreEqual("ALARM", alarms.Single().State);
    }

    [TestMethod]
    public async Task MonitoringKeepsMetricsWhenAlarmPermissionIsDenied()
    {
        var cloud = new Mock<Amazon.CloudWatch.IAmazonCloudWatch>();
        cloud.Setup(service => service.GetMetricDataAsync(It.IsAny<Amazon.CloudWatch.Model.GetMetricDataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.CloudWatch.Model.GetMetricDataResponse { MetricDataResults = [] });
        cloud.Setup(service => service.DescribeAlarmsAsync(It.IsAny<Amazon.CloudWatch.Model.DescribeAlarmsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.CloudWatch.AmazonCloudWatchException("not recorded") { ErrorCode = "AccessDenied" });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchClient()).Returns(cloud.Object);
        using var model = new AwsManager.ViewModels.ResourceInspectorViewModel(Resource() with { Service = "RDS", Id = "database-demo" }, factory.Object, _ => Task.CompletedTask, true);
        await model.InitializeAsync();
        Assert.AreEqual(2, model.Charts.Count);
        Assert.IsTrue(model.Charts.All(chart => chart.Summary == "Aucune mesure"));
        StringAssert.Contains(model.AlarmStatus, "AccessDenied");
        Assert.IsFalse(model.IsLoading);
    }

    private sealed class ResponseTransport : Amazon.Runtime.HttpClientFactory
    {
        public System.Net.HttpStatusCode StatusCode { get; set; } = System.Net.HttpStatusCode.OK;
        public string Body { get; set; } = "";
        public int Calls { get; private set; }
        public override System.Net.Http.HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig) => new(new Handler(this));
        private sealed class Handler(ResponseTransport owner) : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Calls++;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(owner.StatusCode) { Content = new System.Net.Http.StringContent(owner.Body) });
            }
        }
    }

    [TestMethod]
    public async Task SdkHistoryRecordsOutcomesWithoutTagsOrErrorPayloads()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        store.SetReadOnly(false);
        var transport = new ResponseTransport { Body = "<CreateTagsResponse xmlns=\"http://ec2.amazonaws.com/doc/2016-11-15/\"><requestId>test</requestId><return>true</return></CreateTagsResponse>" };
        using var client = OperationSafety.Attach(new Amazon.EC2.AmazonEC2Client(session.Context!.Credentials,
            new Amazon.EC2.AmazonEC2Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport, MaxErrorRetry = 0 }), session.Context, session, store);
        var request = new Amazon.EC2.Model.CreateTagsRequest { Resources = ["i-demo"], Tags = [new("key", "do-not-persist-test-value")] };
        await client.CreateTagsAsync(request);
        Assert.AreEqual("Acceptee par AWS", store.Snapshot.History.Single().Result);
        transport.StatusCode = System.Net.HttpStatusCode.Forbidden;
        transport.Body = "<Response><Errors><Error><Code>UnauthorizedOperation</Code><Message>do-not-persist-error</Message></Error></Errors><RequestID>test</RequestID></Response>";
        await Assert.ThrowsExceptionAsync<Amazon.EC2.AmazonEC2Exception>(() => client.CreateTagsAsync(request));
        Assert.AreEqual("Echec", store.Snapshot.History[0].Result);
        Assert.IsFalse(File.ReadAllText(StorePath).Contains("do-not-persist"));
        var count = transport.Calls;
        session.Invalidate();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.DescribeInstancesAsync(new()));
        Assert.AreEqual(count, transport.Calls);
    }

    [TestMethod]
    public async Task FavoriteNavigationWaitsForMatchingAccountAndSelectsResource()
    {
        var session = new AwsSessionService(new Backend());
        var store = new WorkspaceStore(StorePath);
        var client = new Mock<Amazon.RDS.AmazonRDSClient>(new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.EUWest1) { CallBase = true };
        client.Setup(service => service.DescribeDBInstancesAsync(It.IsAny<Amazon.RDS.Model.DescribeDBInstancesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.RDS.Model.DescribeDBInstancesResponse { DBInstances = [new() { DBInstanceIdentifier = "database-demo" }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateRdsClient()).Returns(client.Object);
        SetupPermissions(factory);
        using var main = new AwsManager.ViewModels.MainViewModel(session, store, () => factory.Object);
        main.SelectedPage = main.Pages.Single(page => page.Name == "Mes accès");
        var resource = Resource() with { Service = "RDS", Id = "database-demo", Name = "database-demo" };
        await main.OpenResourceAsync(resource);
        Assert.IsFalse(session.IsConnected);
        Assert.IsNull(main.SelectedResource);
        await session.ConnectAsync("demo", "eu-west-1", false);
        await main.PermissionsReady;
        await main.OpenResourceAsync(resource);
        Assert.AreEqual("database-demo", main.SelectedResource?.Id);
        await ((AwsManager.ViewModels.AsyncRelayCommand)main.ToggleFavoriteCommand).ExecuteAsync(null);
        Assert.IsTrue(main.IsFavorite);
        Assert.AreEqual(resource, store.Snapshot.Favorites.Single());
        await main.OpenResourceAsync(resource with { Account = "111111111111" });
        await session.ConnectAsync("demo", "eu-west-1", false);
        await main.PermissionsReady;
        Assert.IsNull(main.SelectedResource);
        StringAssert.Contains(main.Notification, "compte verifie ne correspond pas");
    }

    [TestMethod]
    public async Task S3FavoritePreservesRepeatedSlashesAndNeverDownloads()
    {
        var client = new Mock<Amazon.S3.IAmazonS3>(MockBehavior.Strict);
        client.Setup(service => service.GetBucketLocationAsync(It.IsAny<Amazon.S3.Model.GetBucketLocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.GetBucketLocationResponse { Location = new Amazon.S3.S3Region("eu-west-1") });
        client.Setup(service => service.ListObjectsV2Async(It.Is<Amazon.S3.Model.ListObjectsV2Request>(request => request.BucketName == "bucket-demo" && request.Prefix == "nested/"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response { CommonPrefixes = ["nested//"] });
        client.Setup(service => service.Dispose());
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateS3Client(It.IsAny<string?>())).Returns(client.Object);
        var model = new AwsManager.ViewModels.S3ViewModel(factory.Object, false);
        Assert.IsTrue(await model.SelectReferenceAsync(Resource() with { Service = "S3", Id = "nested//", Name = "nested", ParentId = "bucket-demo" }));
        Assert.AreEqual("nested//", model.SelectedFile?.Key);
        client.Verify(service => service.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ReadOnlyAlsoBlocksS3UploadsAndDnsChanges()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var store = new WorkspaceStore(StorePath);
        var transport = new NoNetwork();
        using var s3 = OperationSafety.Attach(new Amazon.S3.AmazonS3Client(session.Context!.Credentials,
            new Amazon.S3.AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = "bucket-demo", Key = "file.txt", ContentBody = "do-not-persist-content" }));
        using var dns = OperationSafety.Attach(new Amazon.Route53.AmazonRoute53Client(session.Context.Credentials,
            new Amazon.Route53.AmazonRoute53Config { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = transport }), session.Context, session, store);
        await Assert.ThrowsExceptionAsync<ReadOnlyModeException>(() => dns.ChangeResourceRecordSetsAsync(new Amazon.Route53.Model.ChangeResourceRecordSetsRequest
        {
            HostedZoneId = "ZDEMO",
            ChangeBatch = new()
            {
                Changes = [new(Amazon.Route53.ChangeAction.CREATE, new Amazon.Route53.Model.ResourceRecordSet
            { Name = "demo.example.test", Type = Amazon.Route53.RRType.A, TTL = 300, ResourceRecords = [new("192.0.2.10")] })]
            }
        }));
        Assert.AreEqual(0, transport.Calls);
        Assert.AreEqual(2, store.Snapshot.History.Length);
        Assert.IsFalse(File.ReadAllText(StorePath).Contains("do-not-persist-content"));
    }

    [TestMethod]
    public void RdsPresetPersistsRelayWithoutChangingExistingSsmPresets()
    {
        var store = new WorkspaceStore(StorePath);
        var legacy = new SavedConnection(Guid.NewGuid(), "Terminal", Resource(), "Terminal", []);
        store.SaveConnection(legacy);
        var rds = new SavedConnection(Guid.NewGuid(), "Base recette", Resource() with { Service = "RDS", Id = "database-demo" }, "RDS", [new(5432, 15432)], "i-0123456789abcdef0");
        store.SaveConnection(rds);
        var restored = new WorkspaceStore(StorePath);
        Assert.AreEqual("i-0123456789abcdef0", restored.Snapshot.Connections.Single(item => item.Mode == "RDS").RelayInstanceId);
        Assert.AreEqual("", restored.Snapshot.Connections.Single(item => item.Mode == "Terminal").RelayInstanceId);
        Assert.ThrowsException<ArgumentException>(() => store.SaveConnection(rds with { RelayInstanceId = "" }));
        Assert.ThrowsException<ArgumentException>(() => store.SaveConnection(rds with { Target = Resource() }));
    }

    [TestMethod]
    public async Task RdsDialogUsesActualEndpointPortAndBlocksReadOnlyBeforeBackend()
    {
        var store = new WorkspaceStore(StorePath);
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var backend = new Mock<IRdsTunnelBackend>();
        var target = new RdsTunnelTarget("database-demo", "oracle.example.test", 1521, "available", "vpc-demo");
        var relay = new SsmRelay("i-0123456789abcdef0", "relais-demo", "vpc-demo", "3.3.0.0");
        backend.Setup(service => service.LoadTargetAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        backend.Setup(service => service.LoadRelaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { relay });
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        backend.Setup(service => service.OpenAsync(target, relay, 11521, It.IsAny<CancellationToken>())).ReturnsAsync(new TrackedSessionModel(process, "simulation"));
        using var model = new AwsManager.ViewModels.RdsTunnelViewModel(new RdsInstanceModel { DbInstanceIdentifier = target.Id }, store: store, context: session.Context, backend: backend.Object);
        await model.RefreshAsync();
        Assert.AreEqual(11521, model.LocalPort);
        Assert.IsNull(model.SelectedRelay);
        model.SelectedRelay = model.Relays.Single();
        Assert.IsFalse(model.OpenCommand.CanExecute(null));
        await ((AwsManager.ViewModels.AsyncRelayCommand)model.OpenCommand).ExecuteAsync(null);
        backend.Verify(service => service.OpenAsync(It.IsAny<RdsTunnelTarget>(), It.IsAny<SsmRelay>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        model.PresetName = "Oracle recette";
        await ((AwsManager.ViewModels.AsyncRelayCommand)model.SavePresetCommand).ExecuteAsync(null);
        Assert.AreEqual(1521, store.Snapshot.Connections.Single().Tunnels.Single().RemotePort);
        store.SetReadOnly(false);
        await ((AwsManager.ViewModels.AsyncRelayCommand)model.OpenCommand).ExecuteAsync(null);
        Assert.AreEqual("127.0.0.1:11521", model.LocalAddress);
        Assert.IsTrue(model.CopyAddressCommand.CanExecute(null));
        Assert.IsFalse(model.CanConfigure);
        model.LocalPort = 21521;
        Assert.AreEqual("127.0.0.1:11521", model.LocalAddress);
        Assert.AreEqual(0, SessionTrackingService.Instance.ActiveSessions.Count);
    }

    [TestMethod]
    public async Task RdsDialogStopsLateTunnelAfterContextInvalidation()
    {
        var store = new WorkspaceStore(StorePath);
        store.SetReadOnly(false);
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("demo", "eu-west-1", false);
        var backend = new Mock<IRdsTunnelBackend>();
        var target = new RdsTunnelTarget("database-demo", "db.example.test", 5432, "available", "vpc-demo");
        var relay = new SsmRelay("i-0123456789abcdef0", "relay", "vpc-demo", "3.3.0.0");
        backend.Setup(service => service.LoadTargetAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        backend.Setup(service => service.LoadRelaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { relay });
        var completion = new TaskCompletionSource<TrackedSessionModel>();
        CancellationToken requestedToken = default;
        backend.Setup(service => service.OpenAsync(target, relay, 15432, It.IsAny<CancellationToken>()))
            .Callback<RdsTunnelTarget, SsmRelay, int, CancellationToken>((_, _, _, token) => requestedToken = token).Returns(completion.Task);
        using var model = new AwsManager.ViewModels.RdsTunnelViewModel(new RdsInstanceModel { DbInstanceIdentifier = target.Id }, store: store, context: session.Context, backend: backend.Object, session: session);
        await model.RefreshAsync();
        model.SelectedRelay = relay;
        var opening = ((AwsManager.ViewModels.AsyncRelayCommand)model.OpenCommand).ExecuteAsync(null);
        session.Invalidate();
        Assert.IsTrue(requestedToken.IsCancellationRequested);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var tunnel = new TrackedSessionModel(process, "simulation");
        completion.SetResult(tunnel);
        await opening;
        backend.Verify(service => service.Stop(tunnel), Times.Once);
        Assert.IsFalse(model.CanConfigure);
        Assert.AreEqual("", model.LocalAddress);
    }

    [TestMethod]
    public void HistoryIsBoundedAndCanBeCleared()
    {
        var store = new WorkspaceStore(StorePath);
        for (var index = 0; index < 305; index++)
            store.Record(new(DateTimeOffset.UtcNow, "demo", "000000000000", "eu-west-1", "EC2", "StopInstances", "i-demo", "Acceptee"));
        Assert.AreEqual(300, store.Snapshot.History.Length);
        store.ClearHistory();
        Assert.AreEqual(0, new WorkspaceStore(StorePath).Snapshot.History.Length);
    }
}