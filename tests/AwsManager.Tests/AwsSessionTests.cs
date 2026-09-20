using Amazon.Runtime;
using AwsManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AwsManager.Tests;

[TestClass]
public class AwsSessionTests
{
    private sealed class Backend : IAwsSessionBackend
    {
        public int Logins { get; private set; }
        public int Verifications { get; private set; }
        public bool FailLogin { get; init; }
        public TaskCompletionSource? Pending { get; init; }
        public IReadOnlyList<AwsProfile> ListProfiles() => [new("test", "eu-west-1", true)];
        public async Task LoginAsync(string profile, CancellationToken cancellationToken)
        {
            Logins++;
            if (FailLogin) throw new InvalidOperationException("Echec simule");
            if (Pending != null) await Pending.Task;
        }
        public Task<AwsContext> VerifyAsync(string profile, string region, CancellationToken cancellationToken)
        {
            Verifications++;
            return Task.FromResult(new AwsContext(profile, region, "000000000000", "test", new AnonymousAWSCredentials()));
        }
    }

    [TestMethod]
    public void StartupDoesNotCallAws()
    {
        var backend = new Backend();
        var session = new AwsSessionService(backend);
        Assert.IsFalse(session.IsConnected);
        Assert.AreEqual(0, backend.Verifications);
    }

    [TestMethod]
    public async Task LoginRequiresVerifiedIdentity()
    {
        var backend = new Backend();
        var session = new AwsSessionService(backend);
        await session.ConnectAsync("test", "eu-west-1", true);
        Assert.IsTrue(session.IsConnected);
        Assert.AreEqual(1, backend.Logins);
        Assert.AreEqual(1, backend.Verifications);
    }

    [TestMethod]
    public async Task FailedLoginDoesNotVerifyOrConnect()
    {
        var backend = new Backend { FailLogin = true };
        var session = new AwsSessionService(backend);
        await session.ConnectAsync("test", "eu-west-1", true);
        Assert.IsFalse(session.IsConnected);
        Assert.IsFalse(session.IsBusy);
        Assert.AreEqual(0, backend.Verifications);
    }

    [TestMethod]
    public async Task RepeatedLoginIsSingleFlight()
    {
        var pending = new TaskCompletionSource();
        var backend = new Backend { Pending = pending };
        var session = new AwsSessionService(backend);
        var first = session.ConnectAsync("test", "eu-west-1", true);
        await session.ConnectAsync("test", "eu-west-1", true);
        pending.SetResult();
        await first;
        Assert.AreEqual(1, backend.Logins);
    }

    [TestMethod]
    public async Task ContextChangeRejectsDelayedLogin()
    {
        var pending = new TaskCompletionSource();
        var session = new AwsSessionService(new Backend { Pending = pending });
        var connection = session.ConnectAsync("old", "eu-west-1", true);
        session.Invalidate();
        pending.SetResult();
        await connection;
        Assert.IsFalse(session.IsConnected);
    }

    [TestMethod]
    public async Task OldFactoryCannotCreateClientsAfterContextChange()
    {
        var session = new AwsSessionService(new Backend());
        await session.ConnectAsync("old", "eu-west-1", false);
        var factory = new AwsClientFactory(session.Context, session);
        session.Invalidate();
        Assert.ThrowsException<InvalidOperationException>(() => factory.CreateEc2Client());
    }

    [TestMethod]
    public void AccessDeniedIsNotAuthenticationExpiry()
    {
        Assert.IsFalse(AwsSessionService.IsAuthenticationError(new AmazonServiceException("denied") { ErrorCode = "AccessDenied" }));
        Assert.IsTrue(AwsSessionService.IsAuthenticationError(new AmazonServiceException("expired") { ErrorCode = "ExpiredToken" }));
    }

    [TestMethod]
    public async Task NonSsoVerificationNeverStartsLogin()
    {
        var backend = new Backend();
        await new AwsSessionService(backend).ConnectAsync("test", "eu-west-1", false);
        Assert.AreEqual(0, backend.Logins);
        Assert.AreEqual(1, backend.Verifications);
    }
}