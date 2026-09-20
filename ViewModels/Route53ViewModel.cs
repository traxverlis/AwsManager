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
using AwsManager.Services;


namespace AwsManager.ViewModels
{
    public class Route53ViewModel : AwsResourceViewModel, IRefreshableViewModel
    {
        public static string Name => "Route 53";

        private bool _isLoading;
        private int _loadVersion;
        public bool IsLoading { get => _isLoading; set { if (SetField(ref _isLoading, value)) CommandManager.InvalidateRequerySuggested(); } }

        public ObservableCollection<HostedZoneModel> HostedZones { get; }
        private HostedZoneModel? _selectedHostedZone;
        public HostedZoneModel? SelectedHostedZone
        {
            get => _selectedHostedZone;
            set
            {
                if (SetField(ref _selectedHostedZone, value))
                {
                    if (value != null) _ = LoadRecordSetsAsync();
                    else { ++_loadVersion; ResourceRecordSets.Clear(); SelectedRecordSet = null; }
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

        public Route53ViewModel() : this(null) { }
        public Route53ViewModel(IAwsClientFactory? clientFactory, AwsContext? context = null) : base(clientFactory, context)
        {
            HostedZones = [];
            ResourceRecordSets = [];
            ConfigureFilter<ResourceRecordSetModel>(ResourceRecordSets, record => $"{record.Name} {record.Type} {record.Value} {record.Routing}");

            RefreshCommand = new AsyncRelayCommand(async _ => await LoadHostedZonesAsync(), _ => !IsLoading && Allowed("route53:ListHostedZones"));
            CreateRecordCommand = new AsyncRelayCommand(async _ => await CreateRecordAsync(), _ => SelectedHostedZone != null && !IsLoading && Allowed("route53:ChangeResourceRecordSets", ZoneArn, true));
            UpdateRecordCommand = new AsyncRelayCommand(async _ => await UpdateRecordAsync(), _ => SelectedRecordSet?.IsEditable == true && !IsLoading && Allowed("route53:ChangeResourceRecordSets", ZoneArn, true));
            DeleteRecordCommand = new AsyncRelayCommand(async _ => await DeleteRecordAsync(), _ => SelectedRecordSet?.IsEditable == true && !IsLoading && Allowed("route53:ChangeResourceRecordSets", ZoneArn, true));
        }

        private string ZoneArn => $"arn:{Partition}:route53:::hostedzone/{SelectedHostedZone?.Id.Split('/').Last()}";

        private async Task LoadHostedZonesAsync()
        {
            IsLoading = true;
            Status = "";
            var selectedZoneId = SelectedHostedZone?.Id;
            SelectedHostedZone = null;
            SelectedRecordSet = null;
            HostedZones.Clear();
            ResourceRecordSets.Clear();
            try
            {
                using var r53Client = ClientFactory.CreateRoute53Client();
                var paginator = r53Client.Paginators.ListHostedZones(new ListHostedZonesRequest());
                await foreach (var zone in paginator.HostedZones)
                {
                    HostedZones.Add(new HostedZoneModel
                    {
                        Id = zone.Id,
                        Name = zone.Name,
                        Comment = zone.Config?.Comment ?? "",
                        IsPrivateZone = zone.Config?.PrivateZone ?? false,
                        ResourceRecordSetCount = zone.ResourceRecordSetCount ?? 0
                    });
                }
                SetField(ref _selectedHostedZone, HostedZones.FirstOrDefault(zone => zone.Id == selectedZoneId), nameof(SelectedHostedZone));
                if (SelectedHostedZone != null) await LoadRecordSetsAsync();
            }
            catch (Exception ex) { ReportError(ex); }
            finally { IsLoading = false; }
        }

        public async Task<bool> SelectReferenceAsync(ResourceReference resource)
        {
            if (IsLoading) throw new InvalidOperationException("Attendez la fin du chargement DNS.");
            var zone = HostedZones.FirstOrDefault(item => item.Id == (string.IsNullOrEmpty(resource.ParentId) ? resource.Id : resource.ParentId));
            SetField(ref _selectedHostedZone, zone, nameof(SelectedHostedZone));
            if (zone == null) return false;
            await LoadRecordSetsAsync();
            if (string.IsNullOrEmpty(resource.ParentId)) return true;
            SelectedRecordSet = ResourceRecordSets.FirstOrDefault(item => ResourceNavigation.RecordId(item) == resource.Id);
            return SelectedRecordSet != null;
        }

        private async Task LoadRecordSetsAsync()
        {
            if (SelectedHostedZone == null) return;
            var zone = SelectedHostedZone;
            var version = ++_loadVersion;
            IsLoading = true;
            Status = "";
            ResourceRecordSets.Clear();
            SelectedRecordSet = null;
            try
            {
                if (!await CheckAccessAsync([new("route53:ListResourceRecordSets", ZoneArn)])) { Status = "Zone non autorisee ou non verifiee."; return; }
                if (version != _loadVersion || SelectedHostedZone != zone) return;
                using var r53Client = ClientFactory.CreateRoute53Client();
                var paginator = r53Client.Paginators.ListResourceRecordSets(new ListResourceRecordSetsRequest { HostedZoneId = zone.Id });
                await foreach (var record in paginator.ResourceRecordSets)
                {
                    if (version != _loadVersion || SelectedHostedZone != zone) return;
                    List<string> values = [];

                    if (record.ResourceRecords != null && record.ResourceRecords.Count > 0)
                    {
                        // Cas standard : enregistrement classique
                        values = [.. record.ResourceRecords.Select(r => r.Value)];
                    }
                    else if (record.AliasTarget != null && !string.IsNullOrWhiteSpace(record.AliasTarget.DNSName))
                    {
                        // Cas alias : on prend la cible
                        values.Add(record.AliasTarget.DNSName);
                    }

                    ResourceRecordSets.Add(new ResourceRecordSetModel
                    {
                        Original = record,
                        Name = record.Name ?? "",
                        Type = record.Type, // RRType est normalement non nullable
                        TTL = record.TTL ?? 0, // 0 si absent
                        ResourceRecords = values
                    });
                }
            }
            catch (Exception ex) { ReportError(ex); }
            finally { if (version == _loadVersion) IsLoading = false; }
        }

        private async Task CreateRecordAsync()
        {
            var vm = new EditRecordSetViewModel();
            var window = new EditRecordSetWindow { DataContext = vm, Owner = Application.Current.MainWindow };

            if (window.ShowDialog() == true)
            {
                var newRecord = vm.BuildRecord();
                var change = new Change(ChangeAction.CREATE, newRecord);
                await ExecuteChangeBatchAsync([change]);
            }
        }

        private async Task UpdateRecordAsync()
        {
            if (SelectedRecordSet?.IsEditable != true) return;

            var vm = new EditRecordSetViewModel(SelectedRecordSet);
            var window = new EditRecordSetWindow { DataContext = vm, Owner = Application.Current.MainWindow };

            if (window.ShowDialog() == true)
            {
                var oldRecord = vm.OriginalRecord.Original!;
                var newRecord = vm.BuildRecord();

                var deleteChange = new Change(ChangeAction.DELETE, oldRecord);
                var createChange = new Change(ChangeAction.CREATE, newRecord);

                await ExecuteChangeBatchAsync([deleteChange, createChange]);
            }
        }

        private async Task DeleteRecordAsync()
        {
            if (SelectedRecordSet?.IsEditable != true) return;
            var recordToDelete = SelectedRecordSet.Original!;
            var change = new Change(ChangeAction.DELETE, recordToDelete);
            await ExecuteChangeBatchAsync([change]);
        }

        private async Task ExecuteChangeBatchAsync(List<Change> changes)
        {
            if (SelectedHostedZone == null) return;
            var zone = SelectedHostedZone;
            if (!Confirm($"Appliquer les changements DNS dans {zone.Name} ?\n" + string.Join("\n", changes.Select(change => $"{change.Action} {change.ResourceRecordSet.Name} {change.ResourceRecordSet.Type}")))) return;
            IsLoading = true;
            try
            {
                using var r53Client = ClientFactory.CreateRoute53Client();
                var request = new ChangeResourceRecordSetsRequest
                {
                    HostedZoneId = zone.Id,
                    ChangeBatch = new ChangeBatch { Changes = changes }
                };
                var response = await r53Client.ChangeResourceRecordSetsAsync(request);
                NotificationService.Publish($"Changement DNS accepte : {response.ChangeInfo?.Status}. Propagation non encore confirmee.");
                await LoadRecordSetsAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}