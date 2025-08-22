using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AwsManager.Views.Dialogs
{
    public partial class AddSecurityGroupRuleWindow : Window
{
    public AddSecurityGroupRuleWindow()
    {
        InitializeComponent();
    }

    public string RuleType => (string)((System.Windows.Controls.ComboBoxItem)RuleTypeComboBox.SelectedItem).Content;
    public string Protocol => ProtocolTextBox.Text;
    public string PortRange => PortRangeTextBox.Text;
    public string Cidr => CidrTextBox.Text;
    public string Description => DescriptionTextBox.Text;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Protocol) || string.IsNullOrWhiteSpace(PortRange) || string.IsNullOrWhiteSpace(Cidr))
        {
            MessageBox.Show("Protocol, Port Range, and CIDR are required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
}
