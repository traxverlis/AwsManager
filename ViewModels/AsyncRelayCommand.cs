using System.Windows.Input;
using AwsManager.Services;

namespace AwsManager.ViewModels;

public sealed class AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public bool IsExecuting { get; private set; }
    public bool CanExecute(object? parameter) => !IsExecuting && (canExecute?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        IsExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(parameter); }
        catch (Exception exception) { NotificationService.Publish(AwsSessionService.DescribeError(exception)); }
        finally { IsExecuting = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}