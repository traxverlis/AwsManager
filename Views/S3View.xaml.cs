using System.Windows.Controls;

namespace AwsManager.Views
{
    /// <summary>
    /// Interaction logic for S3View.xaml
    /// </summary>
    public partial class S3View : UserControl
    {
        public S3View()
        {
            InitializeComponent();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (sender is DataGrid table && DataContext is ViewModels.S3ViewModel model)
                model.SetSelection(table.SelectedItems.OfType<Models.S3ItemModel>());
        }
    }
}
