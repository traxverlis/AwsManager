using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.RDS;
using Amazon.RDS.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using AwsManager.Models;
using AwsManager.ViewModels;


namespace AwsManager.ViewModels
{
    public class RdsTagEditorViewModel : AwsResourceViewModel
    {
        private readonly string _dbInstanceIdentifier;
        private string _resourceArn = string.Empty;
        private readonly List<TagModel> _originalTags;

        public string InstanceIdentifier => _dbInstanceIdentifier;
        public string InstanceId => _dbInstanceIdentifier;
        public ObservableCollection<TagModel> Tags { get; set; }

        public ICommand AddTagCommand { get; }
        public ICommand RemoveTagCommand { get; }
        public ICommand SaveChangesCommand { get; }
        private bool _tagsLoaded;
        private bool _busy;
        public bool CanEdit => _tagsLoaded && !_busy;
        public Task LoadTask { get; }
        private void SetBusy(bool value)
        {
            _busy = value;
            OnPropertyChanged(nameof(CanEdit));
            CommandManager.InvalidateRequerySuggested();
        }

        public RdsTagEditorViewModel(string dbInstanceIdentifier, string resourceArn, AwsManager.Services.IAwsClientFactory? factory = null) : base(factory)
        {
            _dbInstanceIdentifier = dbInstanceIdentifier;
            _resourceArn = resourceArn;
            Tags = [];
            _originalTags = [];

            AddTagCommand = new RelayCommand(_ => Tags.Add(new TagModel { Key = "", Value = "" }), _ => CanEdit && Allowed("rds:AddTagsToResource", _resourceArn, true));
            RemoveTagCommand = new RelayCommand(param => { if (param is TagModel tag) Tags.Remove(tag); }, _ => CanEdit && Allowed("rds:RemoveTagsFromResource", _resourceArn, true));
            SaveChangesCommand = new AsyncRelayCommand(async _ => await SaveChangesAsync(), _ => CanEdit &&
                (Tags.Count == 0 || Allowed("rds:AddTagsToResource", _resourceArn, true)) &&
                (!_originalTags.Any(original => !Tags.Any(tag => tag.Key == original.Key)) || Allowed("rds:RemoveTagsFromResource", _resourceArn, true)));

            LoadTask = LoadTagsAsync();
        }

        private async Task LoadTagsAsync()
        {
            _tagsLoaded = false;
            SetBusy(true);
            Status = "Chargement des tags...";
            try
            {
                using var rdsClient = ClientFactory.CreateRdsClient();

                var response = await rdsClient.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceName = _resourceArn });

                Tags.Clear();
                _originalTags.Clear();
                foreach (var tag in response.TagList ?? [])
                {
                    if (tag.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase) || tag.Key.StartsWith("rds:", StringComparison.OrdinalIgnoreCase)) continue;
                    Tags.Add(new TagModel { Key = tag.Key, Value = tag.Value });
                    _originalTags.Add(new TagModel { Key = tag.Key, Value = tag.Value });
                }
                _tagsLoaded = true;
                Status = "Tags charges.";
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally { SetBusy(false); }
        }

        private async Task SaveChangesAsync()
        {
            if (string.IsNullOrEmpty(_resourceArn))
            {
                Status = "ARN de la ressource indisponible : enregistrement impossible.";
                return;
            }

            SetBusy(true);
            try
            {
                AwsManager.Services.ResourceValidation.Tags(Tags.Select(tag => (tag.Key, tag.Value)));
                if (Tags.Any(tag => tag.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase) || tag.Key.StartsWith("rds:", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Les tags reserves ne peuvent pas etre modifies.");
                using var rdsClient = ClientFactory.CreateRdsClient();

                var tagsToRemove = _originalTags.Where(orig => !Tags.Any(curr => curr.Key == orig.Key)).Select(t => t.Key).ToList();
                if (tagsToRemove.Count != 0)
                {
                    await rdsClient.RemoveTagsFromResourceAsync(new RemoveTagsFromResourceRequest
                    {
                        ResourceName = _resourceArn,
                        TagKeys = tagsToRemove
                    });
                }

                var tagsToAddOrUpdate = Tags.Select(t => new Amazon.RDS.Model.Tag { Key = t.Key, Value = t.Value }).ToList();
                if (tagsToAddOrUpdate.Count != 0)
                {
                    await rdsClient.AddTagsToResourceAsync(new AddTagsToResourceRequest
                    {
                        ResourceName = _resourceArn,
                        Tags = tagsToAddOrUpdate
                    });
                }

                await LoadTagsAsync();
                if (_tagsLoaded) Status = "Tags enregistres.";
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally { SetBusy(false); }
        }
    }
}
