using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.Route53;
using Amazon.Route53.Model;
using AwsManager.Models;
using AwsManager.Views.Dialogs;


namespace AwsManager.ViewModels
{
    public class Route53ViewModel : ViewModelBase, IRefreshableViewModel
    {
        public string Name => "Route 53";

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }

        public ObservableCollection<HostedZoneModel> HostedZones { get; }
        private HostedZoneModel? _selectedHostedZone;
        public HostedZoneModel? SelectedHostedZone
        {
            get => _selectedHostedZone;
            set
            {
                if (SetField(ref _selectedHostedZone, value) && value != null)
                {
                    _ = LoadRecordSetsAsync();
                }
            }
        }

        public ObservableCollection<ResourceRecordSetModel> ResourceRecordSets { get; }
        private ResourceRecordSetModel? _selectedRecordSet;
        public ResourceRecordSetModel? SelectedRecordSet
        {
            get => _selectedRecordSet;
            set => SetField(ref _selectedRecordSet, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand CreateRecordCommand { get; }
        public ICommand UpdateRecordCommand { get; }
        public ICommand DeleteRecordCommand { get; }

        public Route53ViewModel()
        {
            HostedZones = new ObservableCollection<HostedZoneModel>();
            ResourceRecordSets = new ObservableCollection<ResourceRecordSetModel>();

            RefreshCommand = new RelayCommand(async _ => await LoadHostedZonesAsync(), _ => !IsLoading);
            CreateRecordCommand = new RelayCommand(async _ => await CreateRecordAsync(), _ => SelectedHostedZone != null && !IsLoading);
            UpdateRecordCommand = new RelayCommand(async _ => await UpdateRecordAsync(), _ => SelectedRecordSet != null && !IsLoading);
            DeleteRecordCommand = new RelayCommand(async _ => await DeleteRecordAsync(), _ => SelectedRecordSet != null && !IsLoading);
        }

        private async Task LoadHostedZonesAsync()
        {
            IsLoading = true;
            HostedZones.Clear();
            ResourceRecordSets.Clear();
            try
            {
                using var r53Client = new AmazonRoute53Client();
                var paginator = r53Client.Paginators.ListHostedZones(new ListHostedZonesRequest());
                await foreach (var zone in paginator.HostedZones)
                {
                    HostedZones.Add(new HostedZoneModel
                    {
                        Id = zone.Id,
                        Name = zone.Name,
                        Comment = zone.Config.Comment ?? "",
                        IsPrivateZone = zone.Config.PrivateZone ?? false,
                        ResourceRecordSetCount = zone.ResourceRecordSetCount ?? 0
                    });
                }
            }
            catch (Exception ex) { MessageBox.Show($"Failed to load Hosted Zones: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { IsLoading = false; }
        }

        private async Task LoadRecordSetsAsync()
        {
            if (SelectedHostedZone == null) return;

            IsLoading = true;
            ResourceRecordSets.Clear();
            try
            {
                using var r53Client = new AmazonRoute53Client();
                var paginator = r53Client.Paginators.ListResourceRecordSets(new ListResourceRecordSetsRequest { HostedZoneId = SelectedHostedZone.Id });
                await foreach (var record in paginator.ResourceRecordSets)
                {
                    List<string> values = new List<string>();

                    if (record.ResourceRecords != null && record.ResourceRecords.Count > 0)
                    {
                        // Cas standard : enregistrement classique
                        values = record.ResourceRecords.Select(r => r.Value).ToList();
                    }
                    else if (record.AliasTarget != null && !string.IsNullOrWhiteSpace(record.AliasTarget.DNSName))
                    {
                        // Cas alias : on prend la cible
                        values.Add(record.AliasTarget.DNSName);
                    }

                    ResourceRecordSets.Add(new ResourceRecordSetModel
                    {
                        Name = record.Name ?? "",
                        Type = record.Type, // RRType est normalement non nullable
                        TTL = record.TTL ?? 0, // 0 si absent
                        ResourceRecords = values
                    });
                }
            }
            catch (Exception ex) { MessageBox.Show($"Failed to load Record Sets for {SelectedHostedZone.Name}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { IsLoading = false; }
        }

        private async Task CreateRecordAsync()
        {
            var vm = new EditRecordSetViewModel();
            var window = new EditRecordSetWindow { DataContext = vm };

            if (window.ShowDialog() == true)
            {
                var newRecord = new ResourceRecordSet
                {
                    Name = vm.Name,
                    Type = vm.Type,
                    TTL = vm.Ttl,
                    ResourceRecords = vm.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(v => new ResourceRecord { Value = v }).ToList()
                };
                var change = new Change(ChangeAction.CREATE, newRecord);
                await ExecuteChangeBatchAsync(new List<Change> { change });
            }
        }

        private async Task UpdateRecordAsync()
        {
            if (SelectedRecordSet == null) return;

            var vm = new EditRecordSetViewModel(SelectedRecordSet);
            var window = new EditRecordSetWindow { DataContext = vm };

            if (window.ShowDialog() == true)
            {
                var oldRecord = new ResourceRecordSet
                {
                    Name = vm.OriginalRecord.Name,
                    Type = vm.OriginalRecord.Type,
                    TTL = vm.OriginalRecord.TTL,
                    ResourceRecords = vm.OriginalRecord.ResourceRecords.Select(v => new ResourceRecord { Value = v }).ToList()
                };

                var newRecord = new ResourceRecordSet
                {
                    Name = vm.Name,
                    Type = vm.Type,
                    TTL = vm.Ttl,
                    ResourceRecords = vm.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(v => new ResourceRecord { Value = v }).ToList()
                };

                var deleteChange = new Change(ChangeAction.DELETE, oldRecord);
                var createChange = new Change(ChangeAction.CREATE, newRecord);

                await ExecuteChangeBatchAsync(new List<Change> { deleteChange, createChange });
            }
        }

        private async Task DeleteRecordAsync()
        {
            if (SelectedRecordSet == null) return;

            if (MessageBox.Show($"Are you sure you want to delete the record '{SelectedRecordSet.Name}'?", "Confirm Deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var recordToDelete = new ResourceRecordSet
                {
                    Name = SelectedRecordSet.Name,
                    Type = SelectedRecordSet.Type,
                    TTL = SelectedRecordSet.TTL,
                    ResourceRecords = SelectedRecordSet.ResourceRecords.Select(v => new ResourceRecord { Value = v }).ToList()
                };
                var change = new Change(ChangeAction.DELETE, recordToDelete);
                await ExecuteChangeBatchAsync(new List<Change> { change });
            }
        }

        private async Task ExecuteChangeBatchAsync(List<Change> changes)
        {
            if (SelectedHostedZone == null) return;
            IsLoading = true;
            try
            {
                using var r53Client = new AmazonRoute53Client();
                var request = new ChangeResourceRecordSetsRequest
                {
                    HostedZoneId = SelectedHostedZone.Id,
                    ChangeBatch = new ChangeBatch { Changes = changes }
                };
                await r53Client.ChangeResourceRecordSetsAsync(request);
                MessageBox.Show("Successfully submitted changes to Route 53.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadRecordSetsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to apply changes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}