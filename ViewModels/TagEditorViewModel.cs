using Amazon.EC2.Model;
using Amazon.EC2;
using AwsManager.Models;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;


namespace AwsManager.ViewModels
{
    public class TagEditorViewModel : AwsResourceViewModel
    {

        private readonly List<TagModel> _originalTags;
        public string InstanceId { get; set; }
        public ObservableCollection<TagModel> Tags { get; set; }

        public ICommand AddTagCommand { get; }
        public ICommand RemoveTagCommand { get; }
        public ICommand SaveChangesCommand { get; }
        private bool _tagsLoaded;
        private bool _busy;
        public bool CanEdit => _tagsLoaded && !_busy;
        private void SetBusy(bool value)
        {
            _busy = value;
            OnPropertyChanged(nameof(CanEdit));
            CommandManager.InvalidateRequerySuggested();
        }

        public TagEditorViewModel(string instanceId)
        {
            InstanceId = instanceId;
            Tags = [];
            _originalTags = [];

            AddTagCommand = new RelayCommand(_ => Tags.Add(new TagModel { Key = "", Value = "" }), _ => CanEdit && Allowed("ec2:CreateTags", Arn("ec2", $"instance/{InstanceId}"), true));
            RemoveTagCommand = new RelayCommand(param => { if (param is TagModel tag) Tags.Remove(tag); }, _ => CanEdit && Allowed("ec2:DeleteTags", Arn("ec2", $"instance/{InstanceId}"), true));
            SaveChangesCommand = new AsyncRelayCommand(async _ => await SaveChangesAsync(), _ => CanEdit &&
                (Tags.Count == 0 || Allowed("ec2:CreateTags", Arn("ec2", $"instance/{InstanceId}"), true)) &&
                (!_originalTags.Any(original => !Tags.Any(tag => tag.Key == original.Key)) || Allowed("ec2:DeleteTags", Arn("ec2", $"instance/{InstanceId}"), true)));

            _ = LoadTagsAsync();
        }

        private async Task LoadTagsAsync()
        {
            _tagsLoaded = false;
            SetBusy(true);
            Status = "Chargement des tags...";
            try
            {
                using var ec2Client = ClientFactory.CreateEc2Client();
                var response = await ec2Client.DescribeTagsAsync(new DescribeTagsRequest
                {
                    Filters =
            [
                new Filter("resource-id", [InstanceId])
            ]
                });

                Tags.Clear();
                _originalTags.Clear();

                foreach (var tagDescription in response.Tags ?? [])
                {
                    if (tagDescription.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)) continue;
                    var tagModel = new TagModel
                    {
                        Key = tagDescription.Key,
                        Value = tagDescription.Value
                    };

                    Tags.Add(tagModel);
                    _originalTags.Add(new TagModel
                    {
                        Key = tagModel.Key,
                        Value = tagModel.Value
                    });
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
            SetBusy(true);
            try
            {
                AwsManager.Services.ResourceValidation.Tags(Tags.Select(tag => (tag.Key, tag.Value)));
                if (Tags.Any(tag => tag.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Les tags reserves ne peuvent pas etre modifies.");
                using var ec2Client = ClientFactory.CreateEc2Client();

                // Filtrer les tags réservés AWS
                var originalFiltered = _originalTags
                    .Where(t => !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var currentFiltered = Tags
                    .Where(t => !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
                    .ToList();



                // Find tags to delete
                var tagsToDelete = originalFiltered
                    .Where(orig => !currentFiltered.Any(curr => curr.Key == orig.Key))
                    .ToList();

                if (tagsToDelete.Count != 0)
                {
                    var deleteRequest = new DeleteTagsRequest
                    {
                        Resources = [InstanceId],
                        Tags = [.. tagsToDelete.Select(t => new Tag { Key = t.Key, Value = t.Value })]
                    };
                    await ec2Client.DeleteTagsAsync(deleteRequest);
                }

                // Find tags to create or update
                var tagsToCreate = currentFiltered
                    .Select(t => new Tag { Key = t.Key, Value = t.Value })
                    .ToList();

                if (tagsToCreate.Count != 0)
                {
                    var createRequest = new CreateTagsRequest
                    {
                        Resources = [InstanceId],
                        Tags = tagsToCreate
                    };
                    await ec2Client.CreateTagsAsync(createRequest);
                }

                await LoadTagsAsync(); // Refresh the list
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
