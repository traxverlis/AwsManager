using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using AwsManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace AwsManager.Tests;

[TestClass]
public class CloudWatchLogsTests
{
    [TestMethod]
    public async Task ViewerDeduplicatesAndStopsRepeatedTokensWithoutChangingRange()
    {
        var requests = new List<FilterLogEventsRequest>();
        var client = new Mock<IAmazonCloudWatchLogs>();
        client.Setup(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FilterLogEventsRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync(new FilterLogEventsResponse { NextToken = "same", Events = [new() { EventId = "1", Timestamp = 1, Message = "ERROR" }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchLogsClient()).Returns(client.Object);
        using var model = new AwsManager.ViewModels.CloudWatchLogsViewModel(factory.Object) { SelectedGroup = new("/aws/demo", "7 j"), Pattern = "ERROR" };
        await model.SearchAsync();
        Assert.IsTrue(model.HasMoreEvents);
        await ((AwsManager.ViewModels.AsyncRelayCommand)model.MoreEventsCommand).ExecuteAsync(null);
        Assert.AreEqual(1, model.Events.Count);
        Assert.IsFalse(model.HasMoreEvents);
        StringAssert.Contains(model.Status, "resultats partiels");
        Assert.AreEqual(requests[0].StartTime, requests[1].StartTime);
        Assert.AreEqual(requests[0].EndTime, requests[1].EndTime);
        model.Pattern = "WARN";
        Assert.AreEqual(0, model.Events.Count);
        Assert.IsNull(model.SelectedEvent);
    }

    [TestMethod]
    public async Task ViewerRejectsLateResultsAfterGroupChangeAndDispose()
    {
        var completion = new TaskCompletionSource<FilterLogEventsResponse>();
        CancellationToken requestedToken = default;
        var client = new Mock<IAmazonCloudWatchLogs>();
        client.Setup(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FilterLogEventsRequest, CancellationToken>((_, token) => requestedToken = token).Returns(completion.Task);
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchLogsClient()).Returns(client.Object);
        using var model = new AwsManager.ViewModels.CloudWatchLogsViewModel(factory.Object) { SelectedGroup = new("/aws/first", "7 j") };
        var searching = model.SearchAsync();
        model.SelectedGroup = new("/aws/second", "7 j");
        Assert.IsTrue(requestedToken.IsCancellationRequested);
        completion.SetResult(new() { Events = [new() { Message = "obsolete" }], NextToken = "old" });
        await searching;
        Assert.AreEqual(0, model.Events.Count);
        Assert.IsFalse(model.HasMoreEvents);
        completion = new();
        client.Setup(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>())).Returns(completion.Task);
        searching = model.SearchAsync();
        model.Dispose();
        completion.SetResult(new() { Events = [new() { Message = "obsolete" }] });
        await searching;
        Assert.AreEqual(0, model.Events.Count);
        Assert.IsFalse(model.SearchCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ViewerCanContinueAnEmptyPageAndReportsEventLimit()
    {
        var pageNumber = 0;
        var client = new Mock<IAmazonCloudWatchLogs>();
        client.Setup(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FilterLogEventsResponse
            {
                NextToken = $"page-{++pageNumber}",
                Events = pageNumber == 1 ? [] : Enumerable.Range((pageNumber - 2) * 200, 200)
                    .Select(index => new FilteredLogEvent { EventId = index.ToString(), Timestamp = index, Message = "event" }).ToList()
            });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchLogsClient()).Returns(client.Object);
        using var model = new AwsManager.ViewModels.CloudWatchLogsViewModel(factory.Object) { SelectedGroup = new("/aws/demo", "7 j") };
        await model.SearchAsync();
        Assert.AreEqual(0, model.Events.Count);
        Assert.IsTrue(model.HasMoreEvents);
        for (var page = 0; page < 10; page++) await ((AwsManager.ViewModels.AsyncRelayCommand)model.MoreEventsCommand).ExecuteAsync(null);
        Assert.AreEqual(2000, model.Events.Count);
        Assert.IsFalse(model.HasMoreEvents);
        StringAssert.Contains(model.Status, "resultats partiels");
    }

    [TestMethod]
    public async Task ViewerReportsIamFailureWithoutEchoingServiceMessage()
    {
        var client = new Mock<IAmazonCloudWatchLogs>();
        client.Setup(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonCloudWatchLogsException("sensitive-service-message") { ErrorCode = "AccessDeniedException" });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchLogsClient()).Returns(client.Object);
        using var model = new AwsManager.ViewModels.CloudWatchLogsViewModel(factory.Object) { SelectedGroup = new("/aws/demo", "7 j") };
        await model.SearchAsync();
        StringAssert.Contains(model.Status, "AccessDeniedException");
        Assert.IsFalse(model.Status.Contains("sensitive"));
        Assert.IsFalse(model.IsLoading);
        Assert.AreEqual(0, model.Events.Count);
    }

    [TestMethod]
    public async Task EventsPreserveQueryAndContinuationAcrossEmptyPages()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var query = new LogQuery("/aws/rds/demo", start, start.AddHours(1), "ERROR", "database/");
        var client = new Mock<IAmazonCloudWatchLogs>();
        var requests = new List<FilterLogEventsRequest>();
        client.Setup(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FilterLogEventsRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync((FilterLogEventsRequest request, CancellationToken _) => request.NextToken == null
                ? new FilterLogEventsResponse { NextToken = "next" }
                : new FilterLogEventsResponse { Events = [new() { EventId = "event-1", Timestamp = start.ToUnixTimeMilliseconds(), LogStreamName = "database/1", Message = "ERROR\nDetail" }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchLogsClient()).Returns(client.Object);
        var service = new CloudWatchLogsService(factory.Object);
        var first = await service.LoadEventsAsync(query, null, CancellationToken.None);
        Assert.AreEqual(0, first.Items.Count);
        Assert.AreEqual("next", first.NextToken);
        var second = await service.LoadEventsAsync(query, first.NextToken, CancellationToken.None);
        Assert.IsNull(second.NextToken);
        Assert.AreEqual("ERROR\nDetail", second.Items.Single().Message);
        Assert.AreEqual("ERROR Detail", second.Items.Single().Preview);
        foreach (var request in requests)
        {
            Assert.AreEqual(query.Group, request.LogGroupName);
            Assert.AreEqual(query.Start.ToUnixTimeMilliseconds(), request.StartTime);
            Assert.AreEqual(query.End.ToUnixTimeMilliseconds(), request.EndTime);
            Assert.AreEqual(query.Pattern, request.FilterPattern);
            Assert.AreEqual(query.StreamPrefix, request.LogStreamNamePrefix);
            Assert.AreEqual(200, request.Limit);
            Assert.AreEqual(false, request.Unmask);
        }
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.LoadEventsAsync(query with { End = start }, null, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => service.LoadEventsAsync(query, null, canceled.Token));
        client.Verify(service => service.FilterLogEventsAsync(It.IsAny<FilterLogEventsRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GroupsUsePrefixAndExplicitPagination()
    {
        var client = new Mock<IAmazonCloudWatchLogs>();
        client.Setup(service => service.DescribeLogGroupsAsync(It.Is<DescribeLogGroupsRequest>(request => request.LogGroupNamePrefix == "/aws/" && request.Limit == 50 && request.NextToken == "next"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeLogGroupsResponse { LogGroups = [new() { LogGroupName = "/aws/rds/demo", RetentionInDays = 7 }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(service => service.CreateCloudWatchLogsClient()).Returns(client.Object);
        var page = await new CloudWatchLogsService(factory.Object).LoadGroupsAsync("/aws/", "next", CancellationToken.None);
        Assert.AreEqual("/aws/rds/demo", page.Items.Single().Name);
        Assert.AreEqual("7 j", page.Items.Single().Retention);
        Assert.IsNull(page.NextToken);
    }
}