using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using AwsManager.Models;

namespace AwsManager.Services;

public sealed record MetricSample(DateTime Time, double Value);
public sealed record MetricSeriesData(string Name, string Unit, DateTime Start, DateTime End, IReadOnlyList<MetricSample> Samples, bool IsPartial);
public sealed record ResourceAlarm(string Name, string State, DateTime? Updated);

public sealed class MonitoringService(IAwsClientFactory factory)
{
    public async Task<IReadOnlyList<MetricSeriesData>> LoadMetricsAsync(ResourceReference resource, int hours, DateTime endUtc, CancellationToken cancellationToken)
    {
        if (resource.Service is not ("EC2" or "RDS") || hours is not (1 or 6 or 24)) throw new ArgumentException("Periode ou ressource non prise en charge.");
        var start = endUtc.AddHours(-hours);
        var specs = new[]
        {
            (Id: "cpu", Metric: "CPUUtilization", Name: "CPU moyen", Unit: "%", Statistic: "Average"),
            (Id: "secondary", Metric: resource.Service == "RDS" ? "DatabaseConnections" : "NetworkIn",
                Name: resource.Service == "RDS" ? "Connexions RDS" : "Reseau entrant / 5 min", Unit: resource.Service == "RDS" ? "connexions" : "Mio", Statistic: resource.Service == "RDS" ? "Average" : "Sum")
        };
        var samples = specs.ToDictionary(spec => spec.Id, _ => new List<MetricSample>());
        var partial = new HashSet<string>();
        using var client = factory.CreateCloudWatchClient();
        var request = new GetMetricDataRequest
        {
            StartTime = start, EndTime = endUtc, ScanBy = ScanBy.TimestampAscending, MaxDatapoints = 1000,
            MetricDataQueries = specs.Select(spec => new MetricDataQuery
            {
                Id = spec.Id, ReturnData = true,
                MetricStat = new MetricStat
                {
                    Period = 300, Stat = spec.Statistic,
                    Metric = new Metric
                    {
                        Namespace = "AWS/" + resource.Service, MetricName = spec.Metric,
                        Dimensions = [new Dimension { Name = resource.Service == "EC2" ? "InstanceId" : "DBInstanceIdentifier", Value = resource.Id }]
                    }
                }
            }).ToList()
        };
        do
        {
            var response = await client.GetMetricDataAsync(request, cancellationToken);
            foreach (var result in response.MetricDataResults ?? [])
            {
                if (!samples.TryGetValue(result.Id, out var values)) continue;
                if (result.StatusCode != StatusCode.Complete) partial.Add(result.Id);
                var timestamps = result.Timestamps ?? [];
                var measurements = result.Values ?? [];
                if (timestamps.Count != measurements.Count) partial.Add(result.Id);
                values.AddRange(timestamps.Zip(measurements, (time, value) => new MetricSample(DateTime.SpecifyKind(time, DateTimeKind.Utc),
                    result.Id == "secondary" && resource.Service == "EC2" ? value / 1048576d : value)));
            }
            request.NextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        return specs.Select(spec => new MetricSeriesData(spec.Name, spec.Unit, start, endUtc,
            samples[spec.Id].DistinctBy(sample => sample.Time).OrderBy(sample => sample.Time).ToArray(), partial.Contains(spec.Id))).ToArray();
    }

    public async Task<IReadOnlyList<ResourceAlarm>> LoadAlarmsAsync(ResourceReference resource, CancellationToken cancellationToken)
    {
        using var client = factory.CreateCloudWatchClient();
        var alarms = new List<ResourceAlarm>();
        var request = new DescribeAlarmsRequest { AlarmTypes = [AlarmType.MetricAlarm], MaxRecords = 100 };
        bool Matches(string? space, IEnumerable<Dimension>? dimensions) => space == "AWS/" + resource.Service &&
            dimensions?.Any(dimension => dimension.Name == (resource.Service == "EC2" ? "InstanceId" : "DBInstanceIdentifier") && dimension.Value == resource.Id) == true;
        do
        {
            var response = await client.DescribeAlarmsAsync(request, cancellationToken);
            foreach (var alarm in response.MetricAlarms ?? [])
                if (Matches(alarm.Namespace, alarm.Dimensions) || alarm.Metrics?.Any(query => Matches(query.MetricStat?.Metric?.Namespace, query.MetricStat?.Metric?.Dimensions)) == true)
                    alarms.Add(new(alarm.AlarmName, alarm.StateValue?.Value ?? "UNKNOWN", alarm.StateUpdatedTimestamp));
            request.NextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        return alarms.OrderBy(alarm => alarm.Name).ToArray();
    }
}