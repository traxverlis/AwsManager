using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AwsManager.Services;

namespace AwsManager.ViewModels
{
    public abstract class AwsViewModelBase : ViewModelBase, IRefreshableViewModel
    {
        protected readonly IAwsClientFactory ClientFactory;
        protected readonly IAwsErrorHandler ErrorHandler;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetField(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(IsNotLoading));
                }
            }
        }

        public bool IsNotLoading => !IsLoading;

        protected AwsViewModelBase(IAwsClientFactory clientFactory, IAwsErrorHandler errorHandler)
        {
            ClientFactory = clientFactory;
            ErrorHandler = errorHandler;
        }

        protected async Task ExecuteWithLoadingAsync(Func<Task> action, string errorContext)
        {
            IsLoading = true;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleExceptionAsync(ex, errorContext);
            }
            finally
            {
                IsLoading = false;
            }
        }

        protected async Task<T?> ExecuteWithLoadingAsync<T>(Func<Task<T>> action, string errorContext)
        {
            IsLoading = true;
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleExceptionAsync(ex, errorContext);
                return default;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private ICommand? _refreshCommand;
        public ICommand RefreshCommand
        {
            get
            {
                if (_refreshCommand == null)
                {
                    _refreshCommand = new AsyncRelayCommand(async _ => await RefreshAsync(), _ => IsNotLoading);
                }
                return _refreshCommand;
            }
        }

        public abstract Task RefreshAsync();
    }
}