using Amazon;
using Amazon.AutoScaling;
using Amazon.EC2;
using Amazon.RDS;
using Amazon.Route53;
using Amazon.S3;
using Amazon.SecurityToken;
using Amazon.SimpleSystemsManagement;

namespace AwsManager.Services;

public sealed class AwsClientFactory : IAwsClientFactory
{
    private readonly AwsContext? _context;
    private readonly AwsSessionService _session;
    private readonly WorkspaceStore _store;
    public AwsClientFactory(AwsContext? context, AwsSessionService? session = null, WorkspaceStore? store = null)
        => (_context, _session, _store) = (context, session ?? AwsSessionService.Current, store ?? WorkspaceStore.Current);
    public AwsContext Context
    {
        get
        {
            if (_context == null || !ReferenceEquals(_context, _session.Context))
                throw new InvalidOperationException("Le contexte AWS a change. Fermez cette fenetre et reconnectez-vous depuis la page courante.");
            return _context;
        }
    }
    private RegionEndpoint Region => RegionEndpoint.GetBySystemName(Context.Region);
    private T Guard<T>(T client) where T : Amazon.Runtime.AmazonServiceClient => OperationSafety.Attach(client, Context, _session, _store);
    public IAmazonEC2 CreateEc2Client() => Guard(new AmazonEC2Client(Context.Credentials, Region));
    public IAmazonS3 CreateS3Client(string? region = null) => Guard(new AmazonS3Client(Context.Credentials, region == null ? Region : RegionEndpoint.GetBySystemName(region)));
    public IAmazonSimpleSystemsManagement CreateSsmClient() => Guard(new AmazonSimpleSystemsManagementClient(Context.Credentials, Region));
    public Amazon.IdentityManagement.IAmazonIdentityManagementService CreateIamClient() => Guard(new Amazon.IdentityManagement.AmazonIdentityManagementServiceClient(Context.Credentials, Region));
    public IAmazonRDS CreateRdsClient() => Guard(new AmazonRDSClient(Context.Credentials, Region));
    public IAmazonAutoScaling CreateAutoScalingClient() => Guard(new AmazonAutoScalingClient(Context.Credentials, Region));
    public IAmazonRoute53 CreateRoute53Client() => Guard(new AmazonRoute53Client(Context.Credentials, Region));
    public IAmazonSecurityTokenService CreateStsClient() => Guard(new AmazonSecurityTokenServiceClient(Context.Credentials, Region));
    public Amazon.CloudWatch.IAmazonCloudWatch CreateCloudWatchClient() => Guard(new Amazon.CloudWatch.AmazonCloudWatchClient(Context.Credentials, Region));
    public Amazon.CloudWatchLogs.IAmazonCloudWatchLogs CreateCloudWatchLogsClient() => Guard(new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(Context.Credentials, Region));
}