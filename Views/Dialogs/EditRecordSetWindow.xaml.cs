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
    public partial class EditRecordSetWindow : Window
    {
        public EditRecordSetWindow()
        {
            InitializeComponent();
        }
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is not AwsManager.ViewModels.EditRecordSetViewModel editor) return;
                if (FormValidation.HasErrors(this))
                    throw new ArgumentException("Corrigez les champs invalides.");
                editor.BuildRecord();
                DialogResult = true;
            }
            catch (ArgumentException exception) { ValidationText.Text = exception.Message; }
        }
    }
}
