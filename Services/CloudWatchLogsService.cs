using Amazon.CloudWatchLogs.Model;

namespace AwsManager.Services;

public sealed record LogGroupItem(string Name, string Retention);
public sealed record LogEventItem(string Id, DateTimeOffset Time, string Stream, string Message)
{
    public string Preview => Message[..Math.Min(Message.Length, 400)].Replace('\r', ' ').Replace('\n', ' ');
    public string TimeUtc => Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
}
public sealed record LogQuery(string Group, DateTimeOffset Start, DateTimeOffset End, string Pattern, string StreamPrefix);
public sealed record LogPage<T>(IReadOnlyList<T> Items, string? NextToken);

public sealed class CloudWatchLogsService(IAwsClientFactory factory)
{
    public async Task<LogPage<LogGroupItem>> LoadGroupsAsync(string prefix, string? nextToken, CancellationToken cancellationToken)
    {
        if (prefix.Length > 512) throw new ArgumentException("Le prefixe du groupe est limite a 512 caracteres.");
        cancellationToken.ThrowIfCancellationRequested();
        using var client = factory.CreateCloudWatchLogsClient();
        var response = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest
        { LogGroupNamePrefix = string.IsNullOrEmpty(prefix) ? null : prefix, Limit = 50, NextToken = nextToken }, cancellationToken);
        return new((response.LogGroups ?? []).Select(group => new LogGroupItem(group.LogGroupName, group.RetentionInDays.HasValue ? $"{group.RetentionInDays} j" : "Sans expiration")).ToArray(), response.NextToken);
    }

    public async Task<LogPage<LogEventItem>> LoadEventsAsync(LogQuery query, string? nextToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Group)) throw new ArgumentException("Selectionnez un groupe de journaux.");
        if (query.Start >= query.End || query.Start < DateTimeOffset.UnixEpoch) throw new ArgumentException("La periode de recherche est invalide.");
        if (query.Pattern.Length > 1024 || query.StreamPrefix.Length > 512) throw new ArgumentException("Le filtre ou le prefixe du flux est trop long.");
        cancellationToken.ThrowIfCancellationRequested();
        using var client = factory.CreateCloudWatchLogsClient();
        var response = await client.FilterLogEventsAsync(new FilterLogEventsRequest
        {
            LogGroupName = query.Group,
            StartTime = query.Start.ToUnixTimeMilliseconds(),
            EndTime = query.End.ToUnixTimeMilliseconds(),
            FilterPattern = string.IsNullOrWhiteSpace(query.Pattern) ? null : query.Pattern,
            LogStreamNamePrefix = string.IsNullOrEmpty(query.StreamPrefix) ? null : query.StreamPrefix,
            Limit = 200,
            NextToken = nextToken,
            Unmask = false
        }, cancellationToken);
        return new((response.Events ?? []).Select(item => new LogEventItem(item.EventId ?? "", DateTimeOffset.FromUnixTimeMilliseconds(item.Timestamp ?? 0), item.LogStreamName ?? "", item.Message ?? "")).ToArray(), response.NextToken);
    }
}