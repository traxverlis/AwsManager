using Amazon.Runtime;
using System.Collections;

namespace AwsManager.Services;

public sealed class ReadOnlyModeException() : InvalidOperationException("Mode lecture seule : desactivez-le pour modifier AWS ou ouvrir une session SSM.");

public static class OperationSafety
{
    public static bool IsReadOperation(string operation) => operation.StartsWith("Describe", StringComparison.Ordinal) ||
        operation.StartsWith("List", StringComparison.Ordinal) || operation.StartsWith("Get", StringComparison.Ordinal) ||
        operation.StartsWith("Head", StringComparison.Ordinal) || operation is "FilterLogEvents" or "SimulatePrincipalPolicy";

    public static void EnsureWritable(AwsContext context, string service, string operation, string resource, WorkspaceStore? store = null)
    {
        store ??= WorkspaceStore.Current;
        if (!store.Snapshot.IsReadOnly) return;
        Record(context, service, operation, resource, "Bloquee (lecture seule)", store);
        throw new ReadOnlyModeException();
    }

    public static void Record(AwsContext context, string service, string operation, string resource, string result, WorkspaceStore? store = null)
        => (store ?? WorkspaceStore.Current).Record(new(DateTimeOffset.UtcNow, context.Profile, context.Account, context.Region, service, operation, resource, result));

    public static T Attach<T>(T client, AwsContext context, AwsSessionService session, WorkspaceStore store) where T : AmazonServiceClient
    {
        client.BeforeRequestEvent += (_, args) =>
        {
            if (!ReferenceEquals(session.Context, context)) throw new InvalidOperationException("Le contexte AWS a change. Rouvrez cette ressource.");
            if (args is WebServiceRequestEventArgs request && !IsReadOperation(Operation(request.Request)))
                EnsureWritable(context, request.ServiceName, Operation(request.Request), Resource(request.Request), store);
        };
        client.AfterResponseEvent += (_, args) =>
        {
            if (args is WebServiceResponseEventArgs response && !IsReadOperation(Operation(response.Request)))
                Record(context, response.ServiceName, Operation(response.Request), Resource(response.Request), "Acceptee par AWS", store);
        };
        client.ExceptionEvent += (_, args) =>
        {
            if (args is WebServiceExceptionEventArgs failure && failure.Exception is not ReadOnlyModeException)
                Record(context, failure.ServiceName, Operation(failure.Request), Resource(failure.Request),
                    failure.Exception is OperationCanceledException ? "Annulee" : "Echec", store);
        };
        return client;
    }

    private static string Operation(object request) => request.GetType().Name.Replace("Request", "", StringComparison.Ordinal);
    private static string Resource(object request)
    {
        var names = new[] { "InstanceIds", "DBInstanceIdentifier", "ResourceName", "GroupId", "AutoScalingGroupName", "HostedZoneId", "BucketName", "Key", "Resources" };
        return string.Join(" | ", names.Select(name => request.GetType().GetProperty(name)?.GetValue(request))
            .Select(value => value is string text ? text : value is IEnumerable items ? string.Join(", ", items.OfType<string>().Take(10)) : "")
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}