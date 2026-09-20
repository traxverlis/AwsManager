using AwsManager.Models;
using AwsManager.ViewModels;
using System.Windows.Controls;

namespace AwsManager.Views;

public partial class PatchView : UserControl
{
    public PatchView() => InitializeComponent();
    private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is DataGrid grid && DataContext is PatchViewModel model) model.SetSelection(grid.SelectedItems.OfType<PatchNode>());
    }
}