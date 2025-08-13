using System.Windows.Input;

namespace AwsManager.ViewModels
{
    public interface IRefreshableViewModel
    {
        ICommand RefreshCommand { get; }
    }
}
