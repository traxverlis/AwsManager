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
    public class TagEditorViewModel : ViewModelBase
    {

        private readonly List<TagModel> _originalTags; 
        public string InstanceId { get; set; }
        public ObservableCollection<TagModel> Tags { get; set; }

        public ICommand AddTagCommand { get; }
        public ICommand RemoveTagCommand { get; }
        public ICommand SaveChangesCommand { get; }

        public TagEditorViewModel(string instanceId)
        {
            InstanceId = instanceId;
            Tags = [];
            _originalTags = [];

            AddTagCommand = new RelayCommand(_ => Tags.Add(new TagModel { Key = "New-Key", Value = "New-Value" }));
            RemoveTagCommand = new RelayCommand(param => { if (param is TagModel tag) Tags.Remove(tag); });
            SaveChangesCommand = new RelayCommand(async _ => await SaveChangesAsync());

            _ = LoadTagsAsync();
        }

        private async Task LoadTagsAsync()
        {
            try
            {
                using var ec2Client = new AmazonEC2Client();
                var response = await ec2Client.DescribeTagsAsync(new DescribeTagsRequest
                {
                    Filters =
            [
                new Filter("resource-id", [InstanceId])
            ]
                });

                Tags.Clear();
                _originalTags.Clear();

                foreach (var tagDescription in response.Tags)
                {
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
                using var ec2Client = new AmazonEC2Client();


                // Find tags to delete
                var tagsToDelete = _originalTags.Where(orig => !Tags.Any(curr => curr.Key == orig.Key)).ToList();
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
                var tagsToCreate = Tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList();
                if (tagsToCreate.Count != 0)
                {
                    var createRequest = new CreateTagsRequest
                    {
                        Resources = [InstanceId],
                        Tags = tagsToCreate
                    };
                    await ec2Client.CreateTagsAsync(createRequest);
                }

                MessageBox.Show("Tags saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadTagsAsync(); // Refresh the list
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save tags: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
