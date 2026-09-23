using System.Windows.Input;

namespace IPhoneMirror.App.ViewModels;

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    // Validate eagerly so Execute never raises NullReferenceException on a null capture.
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

