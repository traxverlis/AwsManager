using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Amazon.EC2;
using Amazon.EC2.Model;
using AwsManager.Models;
using AwsManager.ViewModels;


namespace AwsManager.ViewModels
{
    public class InstanceDetailsViewModel : ViewModelBase
    {
        public ObservableCollection<KeyValuePair<string, string>> InstanceProperties { get; }

        public InstanceDetailsViewModel(Ec2InstanceModel instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            InstanceProperties = new ObservableCollection<KeyValuePair<string, string>>();
            _ = LoadDetailsAsync(instance.InstanceId);
        }

        private async Task LoadDetailsAsync(string instanceId)
        {
            try
            {
                using var ec2Client = new AmazonEC2Client();
                var response = await ec2Client.DescribeInstancesAsync(new DescribeInstancesRequest
                {
                    InstanceIds = new List<string> { instanceId }
                });

                var fullInstanceDetails = response.Reservations.FirstOrDefault()?.Instances.FirstOrDefault();

                if (fullInstanceDetails != null)
                {
                    // Use reflection to populate the properties
                    foreach (PropertyInfo prop in fullInstanceDetails.GetType().GetProperties().OrderBy(p => p.Name))
                    {
                        var value = prop.GetValue(fullInstanceDetails, null);
                        string valueStr;

                        if (value == null)
                        {
                            valueStr = "null";
                        }
                        else if (value is System.Collections.IList list && value.GetType().IsGenericType)
                        {
                            valueStr = $"{list.Count} item(s)";
                        }
                        else
                        {
                            valueStr = value.ToString() ?? "";
                        }

                        InstanceProperties.Add(new KeyValuePair<string, string>(prop.Name, valueStr));
                    }
                }
                else
                {
                    MessageBox.Show($"Could not find details for instance {instanceId}.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load instance details: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
