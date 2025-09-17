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
    public class AutoScalingTagEditorViewModel : ViewModelBase
    {
        private readonly List<ASGTagModel> _originalTags;
        public string AutoScalingGroupName { get; set; }
        public ObservableCollection<ASGTagModel> Tags { get; set; }

        public ICommand AddTagCommand { get; }
        public ICommand RemoveTagCommand { get; }
        public ICommand SaveChangesCommand { get; }

        public AutoScalingTagEditorViewModel(string asgName)
        {
            AutoScalingGroupName = asgName;
            Tags = new ObservableCollection<ASGTagModel>();
            _originalTags = new List<ASGTagModel>();

            AddTagCommand = new RelayCommand(_ => Tags.Add(new ASGTagModel { Key = "New-Key", Value = "New-Value" }));
            RemoveTagCommand = new RelayCommand(param => { if (param is ASGTagModel tag) Tags.Remove(tag); });
            SaveChangesCommand = new RelayCommand(async _ => await SaveChangesAsync());

            _ = LoadTagsAsync();
        }

        private async Task LoadTagsAsync()
        {
            try
            {
                using var asgClient = new AmazonAutoScalingClient();
                var response = await asgClient.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest
                {
                    AutoScalingGroupNames = new List<string> { AutoScalingGroupName }
                });

                var group = response.AutoScalingGroups.FirstOrDefault();
                if (group == null)
                {
                    MessageBox.Show($"Auto Scaling Group {AutoScalingGroupName} not found.",
                                    "Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                    return;
                }

                Tags.Clear();
                _originalTags.Clear();

                foreach (var tagDescription in group.Tags)
                {
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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load tags: {ex.Message}",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        private async Task SaveChangesAsync()
        {
            try
            {
                using var asgClient = new AmazonAutoScalingClient();

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

                MessageBox.Show("Tags saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadTagsAsync(); // Rafraîchir la liste
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save tags: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
