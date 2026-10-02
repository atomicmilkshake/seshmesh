using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Casr.Core.Logging;

namespace Casr.App.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(CommandManager.InvalidateRequerySuggested);
            }
            else
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("RELAY_CMD", $"RaiseCanExecuteChanged failed: {ex.Message}");
        }
    }
}

/// <summary>
/// ICommand for async handlers. Execute is async-void by interface contract, so every
/// await is inside a try/catch that logs — an unobserved task exception must never
/// reach the dispatcher and crash the app. Re-entrancy is blocked via _isExecuting.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly string _operationName;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null, string? operationName = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _operationName = operationName ?? "async-command";
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, string? operationName = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null, operationName)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute == null || _canExecute(parameter));

    public async void Execute(object? parameter)
    {
        if (_isExecuting) return;
        _isExecuting = true;
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CasrLogger.Error("ASYNC_CMD", $"Unhandled exception in {_operationName}", ex);
        }
        finally
        {
            _isExecuting = false;
        }
    }
}
