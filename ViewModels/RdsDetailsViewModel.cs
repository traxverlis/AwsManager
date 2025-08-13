using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Amazon.RDS;
using Amazon.RDS.Model;
using AwsManager.Models;
using AwsManager.ViewModels;


namespace AwsManager.ViewModels
{
    public class RdsDetailsViewModel : ViewModelBase
    {
        public ObservableCollection<KeyValuePair<string, string>> InstanceProperties { get; }

        public RdsDetailsViewModel(RdsInstanceModel instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            InstanceProperties = new ObservableCollection<KeyValuePair<string, string>>();
            _ = LoadDetailsAsync(instance.DbInstanceIdentifier);
        }

        private async Task LoadDetailsAsync(string dbInstanceIdentifier)
        {
            try
            {
                using var rdsClient = new AmazonRDSClient();
                var response = await rdsClient.DescribeDBInstancesAsync(new DescribeDBInstancesRequest
                {
                    DBInstanceIdentifier = dbInstanceIdentifier
                });

                var fullInstanceDetails = response.DBInstances.FirstOrDefault();

                if (fullInstanceDetails != null)
                {
                    // Use reflection to populate the properties
                    foreach (PropertyInfo prop in fullInstanceDetails.GetType().GetProperties().OrderBy(p => p.Name))
                    {
                        var value = prop.GetValue(fullInstanceDetails, null);
                        string valueStr;

                        if (value == null)
                        {
                            valueStr = "<null>";
                        }
                        else if (value is System.Collections.IList list && value.GetType().IsGenericType)
                        {
                            valueStr = $"{list.Count} item(s)";
                        }
                        else
                        {
                            valueStr = value.ToString() ?? string.Empty;
                        }

                        InstanceProperties.Add(new KeyValuePair<string, string>(prop.Name, valueStr));
                    }
                }
                else
                {
                    MessageBox.Show($"Could not find details for DB instance {dbInstanceIdentifier}.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load RDS instance details: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
