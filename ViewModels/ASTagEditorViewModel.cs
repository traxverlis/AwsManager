using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
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
    public class AutoScalingTagEditorViewModel : AwsResourceViewModel
    {
        private readonly List<ASGTagModel> _originalTags;
        private string _resourceArn = "";
        public string AutoScalingGroupName { get; set; }
        public ObservableCollection<ASGTagModel> Tags { get; set; }

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

        public AutoScalingTagEditorViewModel(string asgName)
        {
            AutoScalingGroupName = asgName;
            Tags = new ObservableCollection<ASGTagModel>();
            _originalTags = new List<ASGTagModel>();

            AddTagCommand = new RelayCommand(_ => Tags.Add(new ASGTagModel { Key = "", Value = "" }), _ => CanEdit && Allowed("autoscaling:CreateOrUpdateTags", _resourceArn, true));
            RemoveTagCommand = new RelayCommand(param => { if (param is ASGTagModel tag) Tags.Remove(tag); }, _ => CanEdit && Allowed("autoscaling:DeleteTags", _resourceArn, true));
            SaveChangesCommand = new AsyncRelayCommand(async _ => await SaveChangesAsync(), _ => CanEdit &&
                (Tags.Count == 0 || Allowed("autoscaling:CreateOrUpdateTags", _resourceArn, true)) &&
                (!_originalTags.Any(original => !Tags.Any(tag => tag.Key == original.Key)) || Allowed("autoscaling:DeleteTags", _resourceArn, true)));

            _ = LoadTagsAsync();
        }

        private async Task LoadTagsAsync()
        {
            _tagsLoaded = false;
            SetBusy(true);
            Status = "Chargement des tags...";
            try
            {
                using var asgClient = ClientFactory.CreateAutoScalingClient();
                var response = await asgClient.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest
                {
                    AutoScalingGroupNames = new List<string> { AutoScalingGroupName }
                });

                var group = response.AutoScalingGroups?.FirstOrDefault();
                if (group == null)
                {
                    Status = "Groupe Auto Scaling introuvable.";
                    return;
                }

                Tags.Clear();
                _originalTags.Clear();

                _resourceArn = group.AutoScalingGroupARN ?? "";
                foreach (var tagDescription in group.Tags ?? [])
                {
                    if (tagDescription.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)) continue;
                    var tagModel = new ASGTagModel
                    {
                        Key = tagDescription.Key,
                        Value = tagDescription.Value,
                        PropageAtLaunch = tagDescription.PropagateAtLaunch ?? false
                    };

                    Tags.Add(tagModel);
                    _originalTags.Add(new ASGTagModel
                    {
                        Key = tagModel.Key,
                        Value = tagModel.Value,
                        PropageAtLaunch = tagModel.PropageAtLaunch

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
                using var asgClient = ClientFactory.CreateAutoScalingClient();

                // Tags à supprimer
                var tagsToDelete = _originalTags
                    .Where(orig => !Tags.Any(curr => curr.Key == orig.Key))
                    .Select(t => new Tag
                    {
                        Key = t.Key,
                        ResourceId = AutoScalingGroupName,
                        ResourceType = "auto-scaling-group",
                        PropagateAtLaunch = t.PropageAtLaunch
                    })
                    .ToList();

                if (tagsToDelete.Count != 0)
                {
                    var deleteRequest = new DeleteTagsRequest
                    {
                        Tags = tagsToDelete
                    };
                    await asgClient.DeleteTagsAsync(deleteRequest);
                }

                // Tags à créer / mettre à jour
                var tagsToCreate = Tags.Select(t => new Tag
                {
                    Key = t.Key,
                    Value = t.Value,
                    ResourceId = AutoScalingGroupName,
                    ResourceType = "auto-scaling-group",
                    PropagateAtLaunch = t.PropageAtLaunch
                }).ToList();

                if (tagsToCreate.Count != 0)
                {
                    var createRequest = new CreateOrUpdateTagsRequest
                    {
                        Tags = tagsToCreate
                    };
                    await asgClient.CreateOrUpdateTagsAsync(createRequest);
                }

                await LoadTagsAsync(); // Rafraîchir la liste
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
