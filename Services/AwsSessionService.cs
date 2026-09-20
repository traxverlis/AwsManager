using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using AwsManager.ViewModels;
using System.Diagnostics;
using System.Net.Http;

namespace AwsManager.Services;

public sealed record AwsProfile(string Name, string Region, bool IsSso);
public sealed record AwsContext(string Profile, string Region, string Account, string Identity, AWSCredentials Credentials);

public interface IAwsSessionBackend
{
    IReadOnlyList<AwsProfile> ListProfiles();
    Task LoginAsync(string profile, CancellationToken cancellationToken);
    Task<AwsContext> VerifyAsync(string profile, string region, CancellationToken cancellationToken);
}

public sealed class AwsSessionBackend : IAwsSessionBackend
{
    public IReadOnlyList<AwsProfile> ListProfiles() => new CredentialProfileStoreChain().ListProfiles()
        .Select(profile => new AwsProfile(profile.Name, profile.Region?.SystemName ?? "eu-west-1",
            !string.IsNullOrWhiteSpace(profile.Options.SsoStartUrl) || !string.IsNullOrWhiteSpace(profile.Options.SsoSession)))
        .OrderBy(profile => profile.Name).ToArray();

    public async Task LoginAsync(string profile, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("aws")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "sso", "login", "--profile", profile })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("AWS CLI indisponible.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Connexion SSO refusee ou annulee. Verifiez AWS CLI et le profil.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    public async Task<AwsContext> VerifyAsync(string profile, string region, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            if (!new CredentialProfileStoreChain().TryGetAWSCredentials(profile, out var credentials))
                throw new InvalidOperationException("Profil AWS introuvable.");
            using var client = new AmazonSecurityTokenServiceClient(credentials, RegionEndpoint.GetBySystemName(region));
            var identity = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
            return new AwsContext(profile, region, identity.Account, identity.Arn, credentials);
        }, cancellationToken);
    }
}

public sealed class AwsSessionService : ViewModelBase
{
    public static AwsSessionService Current { get; } = new(new AwsSessionBackend());
    private readonly IAwsSessionBackend _backend;
    private CancellationTokenSource? _attempt;
    private AwsContext? _context;
    private string _status = "Selectionnez un profil puis verifiez la connexion.";
    private bool _isBusy;
    public AwsContext? Context { get => _context; private set { SetField(ref _context, value); OnPropertyChanged(nameof(IsConnected)); } }
    public bool IsConnected => Context != null;
    public bool IsBusy { get => _isBusy; private set { SetField(ref _isBusy, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public event Action? ContextChanged;
    public AwsSessionService(IAwsSessionBackend backend) => _backend = backend;
    public IReadOnlyList<AwsProfile> ListProfiles() => _backend.ListProfiles();

    public void Invalidate()
    {
        _attempt?.Cancel();
        Context = null;
        Status = "Connexion a verifier pour ce profil et cette region.";
        ContextChanged?.Invoke();
    }

    public void Cancel() => _attempt?.Cancel();

    public async Task ConnectAsync(string profile, string region, bool login)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(region)) return;
        IsBusy = true;
        using var attempt = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _attempt = attempt;
        Context = null;
        ContextChanged?.Invoke();
        try
        {
            if (login)
            {
                Status = "Connexion SSO en cours dans le navigateur...";
                await _backend.LoginAsync(profile, attempt.Token);
            }
            Status = "Verification de l'identite AWS...";
            var context = await _backend.VerifyAsync(profile, region, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            Context = context;
            Status = $"Connecte : {context.Account} / {context.Region}";
            ContextChanged?.Invoke();
        }
        catch (OperationCanceledException) { Status = "Connexion annulee ou delai depasse."; }
        catch (Exception exception) { Status = DescribeError(exception); }
        finally { _attempt = null; IsBusy = false; }
    }

    public static bool IsAuthenticationError(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is AmazonServiceException service && service.ErrorCode is
                "ExpiredToken" or "ExpiredTokenException" or "InvalidClientTokenId" or "UnrecognizedClientException" or "UnauthorizedException") return true;
            if (current is AmazonClientException &&
                (current.Message.Contains("SSO", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public static string DescribeError(Exception exception)
    {
        if (IsAuthenticationError(exception)) return "Session absente ou expiree. Utilisez Connexion SSO puis reessayez.";
        if (exception is AmazonServiceException service)
            return $"AWS : {service.ErrorCode ?? "erreur de service"}. Verifiez les autorisations et la ressource.";
        if (exception is System.ComponentModel.Win32Exception) return "AWS CLI introuvable. Installez AWS CLI v2 et verifiez le PATH.";
        if (exception is HttpRequestException) return "AWS injoignable. Verifiez le reseau puis reessayez.";
        return exception is InvalidOperationException or ArgumentException ? exception.Message : "Operation impossible. Verifiez la configuration et reessayez.";
    }

    public void ReportError(Exception exception, AwsContext? context)
    {
        if (!ReferenceEquals(context, Context)) return;
        if (IsAuthenticationError(exception)) Invalidate();
        Status = DescribeError(exception);
    }
}