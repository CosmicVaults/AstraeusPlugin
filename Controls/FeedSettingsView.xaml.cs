using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CosmicVaults.NINA.Astraeus.Controls {
    public partial class FeedSettingsView : UserControl {
        private FeedSettingsViewModel? _subscribedViewModel;

        public FeedSettingsView() {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>
        /// PasswordBox has no bindable Password, so the typed value is pushed to the view model by hand.
        /// The stored password never goes back into the box. It starts blank and typing replaces it.
        /// </summary>
        private void FeedPasswordBox_PasswordChanged(object sender, RoutedEventArgs eventArgs) {
            if (DataContext is FeedSettingsViewModel viewModel) {
                string typedPassword = FeedPasswordBox.Password;
                viewModel.Password = string.IsNullOrEmpty(typedPassword) ? null : typedPassword;
            }
        }

        // Clearing the password in the view model (the Clear button or a settings update) has to empty
        // the box too, or it keeps showing dots for a password that's gone.
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs eventArgs) {
            if (_subscribedViewModel != null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = DataContext as FeedSettingsViewModel;
            if (_subscribedViewModel != null) _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            if (eventArgs.PropertyName == nameof(FeedSettingsViewModel.HasPassword) &&
                _subscribedViewModel is { HasPassword: false } &&
                !string.IsNullOrEmpty(FeedPasswordBox.Password))
                FeedPasswordBox.Clear();
        }
    }
}
