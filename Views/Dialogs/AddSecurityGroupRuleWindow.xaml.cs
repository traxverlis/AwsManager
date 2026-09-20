using AwsManager.ViewModels;
using System.Windows;

namespace AwsManager.Views.Dialogs
{
    public partial class AddSecurityGroupRuleWindow : Window
    {
        public AddSecurityGroupRuleWindow() : this(new SecurityRuleEditorViewModel()) { }

        public AddSecurityGroupRuleWindow(SecurityRuleEditorViewModel editor)
        {
            InitializeComponent();
            DataContext = editor;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is SecurityRuleEditorViewModel { CanSubmit: true }) DialogResult = true;
        }
    }
}
