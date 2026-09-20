using AwsManager.ViewModels;
using System.Windows;

namespace AwsManager.Views.Dialogs;

public partial class Ec2ProfileWindow : Window
{
    public Ec2ProfileWindow(Ec2ProfileViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) => await model.RefreshAsync();
        Closed += (_, _) => model.Dispose();
    }
}