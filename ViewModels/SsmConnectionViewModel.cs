using AwsManager.Models;
using AwsManager.Services;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AwsManager.ViewModels;
using System.Net.Sockets;
using System.Net;

namespace AwsManager.ViewModels
{
    public class SsmConnectionViewModel : AwsResourceViewModel
    {
        private CancellationTokenSource? _connection;
        private readonly WorkspaceStore _store;
        private string _presetName = "";
        private int _selectedMode;
        private SavedConnection? _selectedPreset;
        public string PresetName { get => _presetName; set { if (SetField(ref _presetName, value)) CommandManager.InvalidateRequerySuggested(); } }
        public int SelectedMode { get => _selectedMode; set => SetField(ref _selectedMode, value); }
        public IReadOnlyList<SavedConnection> Presets => _store.Snapshot.Connections.Where(item =>
            item.Target.Id == TargetInstance.InstanceId && ResourceNavigation.MatchesContext(item.Target, Context)).OrderBy(item => item.Name).ToArray();
        public SavedConnection? SelectedPreset
        {
            get => _selectedPreset;
            set { if (SetField(ref _selectedPreset, value) && value != null) ApplyPreset(value); }
        }
        public ICommand SavePresetCommand { get; }
        public ICommand DeletePresetCommand { get; }
        public ICommand NewPresetCommand { get; }
        public ICommand CancelCommand { get; }
        public Ec2InstanceModel TargetInstance { get; }

        public bool IsWindows { get; }
        public bool IsLinux => !IsWindows;

        // RDP Properties
        private int _rdpRemotePort = 3389;
        public int RdpRemotePort { get => _rdpRemotePort; set => SetField(ref _rdpRemotePort, value); }

        private int _rdpLocalPort;
        public int RdpLocalPort { get => _rdpLocalPort; set => SetField(ref _rdpLocalPort, value); }

        // Custom Port Forwarding Properties
        public ObservableCollection<PortForwardingRule> CustomRules { get; }
        public ICommand AddRuleCommand { get; }
        public ICommand RemoveRuleCommand { get; }

        // Session Commands
        public ICommand StartSshCommand { get; }
        public ICommand StartRdpTunnelCommand { get; }
        public ICommand StartCustomTunnelCommand { get; }

        public SsmConnectionViewModel(Ec2InstanceModel instance, WorkspaceStore? store = null, AwsContext? context = null) : base(context: context)
        {
            ArgumentNullException.ThrowIfNull(instance);
            _store = store ?? WorkspaceStore.Current;
            TargetInstance = instance;
            IsWindows = instance.Platform?.Contains("windows", StringComparison.OrdinalIgnoreCase) ?? false;

            try
            {
                _rdpLocalPort = FindFreePort(13389);
            }
            catch (InvalidOperationException)
            {
                _rdpLocalPort = 13389;
            }

            CustomRules = [];
            AddRuleCommand = new RelayCommand(_ => CustomRules.Add(new PortForwardingRule()));
            RemoveRuleCommand = new RelayCommand(param => { if (param is PortForwardingRule rule) CustomRules.Remove(rule); }, _ => CustomRules.Count > 0);

            StartSshCommand = new RelayCommand(StartSsh, _ => _connection == null && !_store.Snapshot.IsReadOnly && CanStartSession("SSM-SessionManagerRunShell", false));
            StartRdpTunnelCommand = new AsyncRelayCommand(StartRdpTunnel, _ => _connection == null && !_store.Snapshot.IsReadOnly && CanStartSession("AWS-StartPortForwardingSession", true));
            StartCustomTunnelCommand = new AsyncRelayCommand(StartCustomTunnel, _ => CustomRules.Count > 0 && _connection == null && !_store.Snapshot.IsReadOnly && CanStartSession("AWS-StartPortForwardingSession", true));
            CancelCommand = new RelayCommand(_ => _connection?.Cancel(), _ => _connection != null);
            SavePresetCommand = new AsyncRelayCommand(form => { SavePreset(form); return Task.CompletedTask; }, _ => Context != null && !string.IsNullOrWhiteSpace(PresetName) && _connection == null);
            DeletePresetCommand = new AsyncRelayCommand(_ =>
            {
                _store.RemoveConnection(SelectedPreset!.Id);
                SelectedPreset = null;
                PresetName = "";
                OnPropertyChanged(nameof(Presets));
                return Task.CompletedTask;
            }, _ => SelectedPreset != null && _connection == null);
            NewPresetCommand = new RelayCommand(_ => { SelectedPreset = null; PresetName = ""; }, _ => _connection == null);
        }

        private bool CanStartSession(string document, bool awsOwned) => Allowed([
            new("ssm:StartSession", Arn("ec2", $"instance/{TargetInstance.InstanceId}")),
            new("ssm:StartSession", $"arn:{Partition}:ssm:{Context?.Region}:{(awsOwned ? "" : Context?.Account)}:document/{document}")], true);

        public void ApplyPreset(SavedConnection preset)
        {
            if (!ResourceNavigation.MatchesContext(preset.Target, Context) || preset.Target.Id != TargetInstance.InstanceId || preset.Target.Service != "EC2")
                throw new InvalidOperationException("Cette connexion appartient a une autre instance ou un autre contexte AWS.");
            if (preset.Mode == "RDP" && !IsWindows) throw new InvalidOperationException("Cette instance n'est pas reconnue comme Windows.");
            if (preset.Mode is not ("Terminal" or "RDP" or "Tunnels")) throw new ArgumentException("Type de connexion inconnu.");
            if (preset.Mode != "Terminal") SsmService.ValidatePorts(preset.Tunnels.Select(item => (item.RemotePort, item.LocalPort)));
            if (preset.Mode == "RDP" && preset.Tunnels.Length != 1) throw new ArgumentException("Configuration RDP invalide.");
            SetField(ref _selectedPreset, preset, nameof(SelectedPreset));
            PresetName = preset.Name;
            SelectedMode = preset.Mode == "Terminal" ? 0 : preset.Mode == "RDP" ? 1 : 2;
            if (SelectedMode == 1) { RdpRemotePort = preset.Tunnels[0].RemotePort; RdpLocalPort = preset.Tunnels[0].LocalPort; }
            CustomRules.Clear();
            if (SelectedMode == 2)
                foreach (var tunnel in preset.Tunnels) CustomRules.Add(new PortForwardingRule { RemotePort = tunnel.RemotePort, LocalPort = tunnel.LocalPort });
        }

        private void SavePreset(object? form)
        {
            if (form is DependencyObject element && Views.FormValidation.HasErrors(element)) throw new ArgumentException("Corrigez les ports invalides avant d'enregistrer.");
            var context = Context ?? throw new InvalidOperationException("Connexion AWS requise.");
            var mode = SelectedMode == 0 ? "Terminal" : SelectedMode == 1 ? "RDP" : "Tunnels";
            SavedTunnel[] tunnels = SelectedMode == 0 ? [] : SelectedMode == 1 ? [new(RdpRemotePort, RdpLocalPort)] : CustomRules.Select(item => new SavedTunnel(item.RemotePort, item.LocalPort)).ToArray();
            var target = new ResourceReference(context.Profile, context.Account, context.Region, "EC2", TargetInstance.InstanceId, TargetInstance.Name);
            var preset = new SavedConnection(SelectedPreset?.Id ?? Guid.NewGuid(), PresetName.Trim(), target, mode, tunnels);
            _store.SaveConnection(preset);
            OnPropertyChanged(nameof(Presets));
            SelectedPreset = preset;
            Status = "Configuration enregistree sur ce poste.";
        }

        private static int FindFreePort(int startPort, int maxAttempts = 10000)
        {
            int port = startPort;
            int maxPort = Math.Min(startPort + maxAttempts, 65535);

            while (port <= maxPort)
            {
                try
                {
                    using TcpListener tcpListener = new(IPAddress.Loopback, port);
                    tcpListener.Start();
                    tcpListener.Stop();
                    return port;
                }
                catch (SocketException)
                {
                    ++port;
                }
            }

            throw new InvalidOperationException($"Aucun port libre trouvé entre {startPort} et {maxPort}");
        }

        private void StartSsh(object? parameter)
        {
            try
            {
                SsmService.StartShell(Context!, TargetInstance.InstanceId);
                Status = "Terminal SSM lance. La connexion est confirmee dans le terminal.";
            }
            catch (Exception ex)
            {
                ReportError(ex);
                Status = AwsSessionService.DescribeError(ex);
            }
        }

        private async Task StartRdpTunnel(object? parameter)
        {
            try
            {
                if (parameter is DependencyObject form && Views.FormValidation.HasErrors(form))
                    throw new ArgumentException("Corrigez les ports invalides.");
                using var cancellation = new CancellationTokenSource();
                _connection = cancellation;
                Status = "Ouverture du tunnel RDP...";
                var localPort = RdpLocalPort;
                await SsmService.StartTunnelAsync(Context!, TargetInstance.InstanceId, RdpRemotePort, localPort, cancellation.Token);
                Status = $"Tunnel local ouvert sur 127.0.0.1:{localPort}.";
                var start = new System.Diagnostics.ProcessStartInfo("mstsc.exe") { UseShellExecute = true };
                start.ArgumentList.Add($"/v:127.0.0.1:{localPort}");
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex)
            {
                ReportError(ex);
                Status = ex is OperationCanceledException ? "Connexion annulee." : AwsSessionService.DescribeError(ex);
            }
            finally { _connection = null; CommandManager.InvalidateRequerySuggested(); }
        }

        private async Task StartCustomTunnel(object? parameter)
        {
            var started = new List<TrackedSessionModel>();
            try
            {
                if (parameter is DependencyObject form && Views.FormValidation.HasErrors(form))
                    throw new ArgumentException("Corrigez les ports invalides.");
                var rules = CustomRules.Select(rule => (Remote: rule.RemotePort, Local: rule.LocalPort)).ToArray();
                SsmService.ValidatePorts(rules);
                foreach (var rule in rules) SsmService.CheckPortAvailable(rule.Local);
                using var cancellation = new CancellationTokenSource();
                _connection = cancellation;
                Status = "Ouverture des tunnels...";
                foreach (var rule in rules)
                    started.Add(await SsmService.StartTunnelAsync(Context!, TargetInstance.InstanceId, rule.Remote, rule.Local, cancellation.Token));
                Status = $"{started.Count} tunnel(s) local(aux) ouvert(s).";
            }
            catch (Exception ex)
            {
                foreach (var session in started) SessionTrackingService.Instance.KillSession(session);
                ReportError(ex);
                Status = ex is OperationCanceledException ? "Connexion annulee." : AwsSessionService.DescribeError(ex);
            }
            finally { _connection = null; CommandManager.InvalidateRequerySuggested(); }
        }
    }
}
