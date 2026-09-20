using AwsManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AwsManager.Tests;

[TestClass]
public class LiveReadOnlyTests
{
    [TestMethod]
    [TestCategory("LiveReadOnly")]
    public async Task ExplicitlyEnabledSdkReadOnlyProbe()
    {
        var profile = Environment.GetEnvironmentVariable("AWSMANAGER_LIVE_PROFILE");
        var region = Environment.GetEnvironmentVariable("AWSMANAGER_LIVE_REGION");
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(region))
            Assert.Inconclusive("Recette AWS desactivee par defaut ; profil et region explicitement requis.");
        var session = new AwsSessionService(new AwsSessionBackend());
        await session.ConnectAsync(profile!, region!, false);
        Assert.IsTrue(session.IsConnected, session.Status);
        var factory = new AwsClientFactory(session.Context, session);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var ec2 = factory.CreateEc2Client();
        using var ssm = factory.CreateSsmClient();
        using var rds = factory.CreateRdsClient();
        using var s3 = factory.CreateS3Client();
        using var scaling = factory.CreateAutoScalingClient();
        using var dns = factory.CreateRoute53Client();
        await ec2.DescribeInstancesAsync(new Amazon.EC2.Model.DescribeInstancesRequest { MaxResults = 5 }, cancellation.Token);
        await ec2.DescribeSecurityGroupsAsync(new Amazon.EC2.Model.DescribeSecurityGroupsRequest { MaxResults = 5 }, cancellation.Token);
        await ec2.DescribeSecurityGroupRulesAsync(new Amazon.EC2.Model.DescribeSecurityGroupRulesRequest { MaxResults = 5 }, cancellation.Token);
        await ssm.DescribeInstanceInformationAsync(new Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationRequest { MaxResults = 5 }, cancellation.Token);
        await rds.DescribeDBInstancesAsync(new Amazon.RDS.Model.DescribeDBInstancesRequest { MaxRecords = 20 }, cancellation.Token);
        await s3.ListBucketsAsync(new Amazon.S3.Model.ListBucketsRequest { MaxBuckets = 10 }, cancellation.Token);
        await scaling.DescribeAutoScalingGroupsAsync(new Amazon.AutoScaling.Model.DescribeAutoScalingGroupsRequest { MaxRecords = 1 }, cancellation.Token);
        await dns.ListHostedZonesAsync(new Amazon.Route53.Model.ListHostedZonesRequest { MaxItems = "1" }, cancellation.Token);
    }
}