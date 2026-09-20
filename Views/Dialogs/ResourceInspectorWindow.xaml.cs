using AwsManager.ViewModels;
using System.Windows;

namespace AwsManager.Views.Dialogs;

public partial class ResourceInspectorWindow : Window
{
    public ResourceInspectorWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is not ResourceInspectorViewModel model) return;
            model.CloseRequested += Close;
            await model.InitializeAsync();
        };
        Closed += (_, _) =>
        {
            if (DataContext is not ResourceInspectorViewModel model) return;
            model.CloseRequested -= Close;
            model.Dispose();
        };
    }
}