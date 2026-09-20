using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace AwsManager.Services
{
    public class SsmService
    {
        public static ProcessStartInfo CreateStartInfo(AwsContext context, string instanceId, int? remotePort = null, int? localPort = null, string? remoteHost = null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(instanceId, "^(i|mi)-[0-9a-f]+$"))
                throw new ArgumentException("Identifiant d'instance invalide.");
            var start = new ProcessStartInfo("aws") { UseShellExecute = false };
            foreach (var argument in new[] { "ssm", "start-session", "--target", instanceId, "--profile", context.Profile, "--region", context.Region })
                start.ArgumentList.Add(argument);
            if (remotePort.HasValue || localPort.HasValue || remoteHost != null)
            {
                ValidatePorts([(remotePort ?? 0, localPort ?? 0)]);
                if (remoteHost != null) ValidateRemoteHost(remoteHost);
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.ArgumentList.Add("--document-name");
                start.ArgumentList.Add(remoteHost == null ? "AWS-StartPortForwardingSession" : "AWS-StartPortForwardingSessionToRemoteHost");
                start.ArgumentList.Add("--parameters");
                var parameters = new Dictionary<string, string[]>
                {
                    ["portNumber"] = [remotePort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    ["localPortNumber"] = [localPort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                };
                if (remoteHost != null) parameters["host"] = [remoteHost];
                start.ArgumentList.Add(System.Text.Json.JsonSerializer.Serialize(parameters));
            }
            return start;
        }

        public static void ValidateRemoteHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host != host.Trim() || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ArgumentException("Hote distant invalide : indiquez un nom DNS ou une adresse IP, sans URL ni port.");
        }

        public static void ValidatePorts(IEnumerable<(int Remote, int Local)> rules)
        {
            var entries = rules.ToArray();
            if (entries.Length == 0 || entries.Any(rule => rule.Remote is < 1 or > 65535 || rule.Local is < 1 or > 65535))
                throw new ArgumentException("Ports locaux et distants : de 1 a 65535.");
            if (entries.Select(rule => rule.Local).Distinct().Count() != entries.Length)
                throw new ArgumentException("Chaque tunnel doit utiliser un port local different.");
        }

        public static void CheckPortAvailable(int port)
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
        }

        public static Models.TrackedSessionModel StartShell(AwsContext context, string instanceId)
        {
            _ = new AwsClientFactory(context).Context;
            OperationSafety.EnsureWritable(context, "SSM", "StartSession", instanceId);
            try
            {
                var process = Process.Start(CreateStartInfo(context, instanceId)) ?? throw new InvalidOperationException("AWS CLI non demarree.");
                var session = SessionTrackingService.Instance.AddSession(process, $"Shell SSM | {instanceId} | {context.Profile} | {context.Region}");
                OperationSafety.Record(context, "SSM", "StartSession", instanceId, "Terminal lance (connexion a verifier)");
                return session;
            }
            catch { OperationSafety.Record(context, "SSM", "StartSession", instanceId, "Echec"); throw; }
        }

        public static async Task<Models.TrackedSessionModel> StartTunnelAsync(AwsContext context, string instanceId, int remotePort, int localPort, CancellationToken cancellationToken, string? remoteHost = null)
        {
            _ = new AwsClientFactory(context).Context;
            var operation = remoteHost == null ? "StartTunnel" : "StartRemoteHostTunnel";
            var resource = remoteHost == null ? instanceId : $"{instanceId} -> {remoteHost}:{remotePort}";
            OperationSafety.EnsureWritable(context, "SSM", operation, resource);
            try
            {
                var session = await StartTunnelCoreAsync(context, instanceId, remotePort, localPort, cancellationToken, remoteHost);
                OperationSafety.Record(context, "SSM", operation, resource, "Tunnel ouvert");
                return session;
            }
            catch (Exception exception)
            {
                OperationSafety.Record(context, "SSM", operation, resource, exception is OperationCanceledException ? "Annulee" : "Echec");
                throw;
            }
        }

        private static async Task<Models.TrackedSessionModel> StartTunnelCoreAsync(AwsContext context, string instanceId, int remotePort, int localPort, CancellationToken cancellationToken, string? remoteHost)
        {
            ValidatePorts([(remotePort, localPort)]);
            CheckPortAvailable(localPort);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = CreateStartInfo(context, instanceId, remotePort, localPort, remoteHost), EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data?.Contains($"Port {localPort} opened", StringComparison.OrdinalIgnoreCase) == true)
                    ready.TrySetResult();
            };
            process.ErrorDataReceived += (_, _) => { };
            process.Exited += (_, _) => ready.TrySetException(new InvalidOperationException("Le processus SSM s'est arrete. Verifiez la session AWS, les droits SSM et session-manager-plugin."));
            process.Start();
            var session = SessionTrackingService.Instance.AddSession(process, $"{instanceId} | localhost:{localPort} -> {remoteHost ?? instanceId}:{remotePort} | {context.Profile} | {context.Region}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
                if (process.HasExited) throw new InvalidOperationException("Le tunnel SSM a ete ferme.");
                return session;
            }
            catch
            {
                SessionTrackingService.Instance.KillSession(session);
                throw;
            }
        }
    }
}
