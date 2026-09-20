using AwsManager.ViewModels;
using System.Windows;

namespace AwsManager.Views.Dialogs;

public partial class RdsTunnelWindow : Window
{
    public RdsTunnelWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is RdsTunnelViewModel model) await model.RefreshAsync(); };
        Closed += (_, _) => { if (DataContext is RdsTunnelViewModel model) model.Dispose(); };
    }
}