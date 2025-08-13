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
    public class RdsTagEditorViewModel : ViewModelBase
    {
        private string _dbInstanceIdentifier;
        private string _resourceArn = string.Empty;
        private readonly List<TagModel> _originalTags;

        public string InstanceIdentifier => _dbInstanceIdentifier;
        public ObservableCollection<TagModel> Tags { get; set; }

        public ICommand AddTagCommand { get; }
        public ICommand RemoveTagCommand { get; }
        public ICommand SaveChangesCommand { get; }

        public RdsTagEditorViewModel(string dbInstanceIdentifier)
        {
            _dbInstanceIdentifier = dbInstanceIdentifier;
            Tags = new ObservableCollection<TagModel>();
            _originalTags = new List<TagModel>();

            AddTagCommand = new RelayCommand(_ => Tags.Add(new TagModel { Key = "New-Key", Value = "New-Value" }));
            RemoveTagCommand = new RelayCommand(param => { if (param is TagModel tag) Tags.Remove(tag); });
            SaveChangesCommand = new RelayCommand(async _ => await SaveChangesAsync());

            _ = LoadTagsAsync();
        }

        private async Task LoadTagsAsync()
        {
            try
            {
                using var rdsClient = new AmazonRDSClient();

                // Construct the ARN
                var region = rdsClient.Config.RegionEndpoint.SystemName;
                using var stsClient = new AmazonSecurityTokenServiceClient();
                var identity = await stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest());
                var accountId = identity.Account;
                _resourceArn = $"arn:aws:rds:{region}:{accountId}:db:{_dbInstanceIdentifier}";

                var response = await rdsClient.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceName = _resourceArn });

                Tags.Clear();
                foreach (var tag in response.TagList)
                {
                    Tags.Add(new TagModel { Key = tag.Key, Value = tag.Value });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load RDS tags: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveChangesAsync()
        {
            if (string.IsNullOrEmpty(_resourceArn))
            {
                MessageBox.Show("Resource ARN could not be determined. Cannot save tags.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                using var rdsClient = new AmazonRDSClient();

                var tagsToRemove = _originalTags.Where(orig => !Tags.Any(curr => curr.Key == orig.Key)).Select(t => t.Key).ToList();
                if (tagsToRemove.Any())
                {
                    await rdsClient.RemoveTagsFromResourceAsync(new RemoveTagsFromResourceRequest
                    {
                        ResourceName = _resourceArn,
                        TagKeys = tagsToRemove
                    });
                }

                var tagsToAddOrUpdate = Tags.Select(t => new Amazon.RDS.Model.Tag { Key = t.Key, Value = t.Value }).ToList();
                if (tagsToAddOrUpdate.Any())
                {
                    await rdsClient.AddTagsToResourceAsync(new AddTagsToResourceRequest
                    {
                        ResourceName = _resourceArn,
                        Tags = tagsToAddOrUpdate
                    });
                }

                MessageBox.Show("Tags saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadTagsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save RDS tags: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
