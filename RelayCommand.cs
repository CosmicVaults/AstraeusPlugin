using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CosmicVaults.NINA.Astraeus {
    public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand {

        private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        private readonly Func<bool>? _canExecute = canExecute;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged;

        // WPF controls listen to CanExecuteChanged and can only be touched on the UI thread.
        public void RaiseCanExecuteChanged() {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
            else
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}