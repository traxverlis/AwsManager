using Amazon.EC2;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;

namespace AwsManager.Services
{
    public interface IAwsClientFactory
    {
        IAmazonEC2 CreateEc2Client();
        IAmazonS3 CreateS3Client(string? region = null);
        IAmazonSimpleSystemsManagement CreateSsmClient();
        Amazon.IdentityManagement.IAmazonIdentityManagementService CreateIamClient();
        Amazon.RDS.IAmazonRDS CreateRdsClient();
        Amazon.AutoScaling.IAmazonAutoScaling CreateAutoScalingClient();
        Amazon.Route53.IAmazonRoute53 CreateRoute53Client();
        Amazon.SecurityToken.IAmazonSecurityTokenService CreateStsClient();
        Amazon.CloudWatch.IAmazonCloudWatch CreateCloudWatchClient();
        Amazon.CloudWatchLogs.IAmazonCloudWatchLogs CreateCloudWatchLogsClient();
    }
}