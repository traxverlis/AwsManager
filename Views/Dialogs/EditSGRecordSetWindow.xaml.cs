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
    /// <summary>
    /// Logique d'interaction pour EditRecordSetWindow.xaml
    /// </summary>
    public partial class EditSGRecordSetWindow : Window
    {
        public EditSGRecordSetWindow()
        {
            InitializeComponent();
        }
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // This sets the DialogResult to true, which can be checked by the calling code.
            // The window will be closed automatically by setting IsDefault="True" on the button
            // when DialogResult is set.
            DialogResult = true;
        }
    }
}
