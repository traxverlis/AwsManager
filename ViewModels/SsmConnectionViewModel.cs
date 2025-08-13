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
    public class SsmConnectionViewModel : ViewModelBase
    {
        private readonly SsmService _ssmService;
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



        public SsmConnectionViewModel(Ec2InstanceModel instance)
        {
            _ssmService = new SsmService();
            TargetInstance = instance;
            IsWindows = instance.Platform.Contains("windows", StringComparison.OrdinalIgnoreCase);

            //_rdpLocalPort = new Random().Next(50000, 60000);
            _rdpLocalPort = FindFreePort(13389);
            CustomRules = new ObservableCollection<PortForwardingRule>();
            AddRuleCommand = new RelayCommand(_ => CustomRules.Add(new PortForwardingRule()));
            RemoveRuleCommand = new RelayCommand(param => { if (param is PortForwardingRule rule) CustomRules.Remove(rule); }, _ => CustomRules.Count > 0);

            StartSshCommand = new RelayCommand(StartSsh);
            StartRdpTunnelCommand = new RelayCommand(StartRdpTunnel);
            StartCustomTunnelCommand = new RelayCommand(StartCustomTunnel, _ => CustomRules.Count > 0);
        }
        private static int FindFreePort(int startPort)
        {
            int port = startPort;
            while (true)
            {
                try
                {
                    TcpListener tcpListener = new(IPAddress.Loopback, port);
                    tcpListener.Start();
                    tcpListener.Stop();
                    return port;
                }
                catch (SocketException)
                {
                    ++port;
                }
            }
        }

        private void StartSsh(object? parameter)
        {
            _ssmService.StartSshSession(TargetInstance.InstanceId);
            MessageBox.Show($"SSH session process started for {TargetInstance.InstanceId}.\nCheck your terminal windows.", "Action: SSH", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void StartRdpTunnel(object? parameter)
        {
            _ssmService.StartPortForwardingSession(TargetInstance.InstanceId, RdpRemotePort, RdpLocalPort);
            MessageBox.Show($"RDP tunnel process started for {TargetInstance.InstanceId}.\nRemote Port: {RdpRemotePort}\nLocal Port: {RdpLocalPort}\n\nYou can now connect your RDP client to localhost:{RdpLocalPort}", "Action: RDP Tunnel", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void StartCustomTunnel(object? parameter)
        {
            foreach (var rule in CustomRules)
            {
                _ssmService.StartPortForwardingSession(TargetInstance.InstanceId, rule.RemotePort, rule.LocalPort);
            }
            MessageBox.Show($"{CustomRules.Count} custom tunnel process(es) started for {TargetInstance.InstanceId}", "Action: Custom Tunnel", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
