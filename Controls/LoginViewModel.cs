using CosmicVaults.NINA.Astraeus.Engine;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Controls {
    public class LoginViewModel : BaseViewModel {
        public string StatusMessage {
            get => _statusMessage;
            set {
                if (_statusMessage == value) return;
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }

        public bool IsAuthenticated => _authenticator.IsAuthenticated;

        /// <summary>True only when a sign-in can start, so the button hides mid-flight.</summary>
        public bool IsSignedOut => !_authenticator.IsAuthenticated && !_authenticator.IsSigningIn;

        public bool IsSigningIn => _authenticator.IsSigningIn;

        public string AccountLabel =>
            _authenticator.IsAuthenticated
                ? $"Signed in as {_authenticator.SignedInAs ?? "your Astraeus account"}"
                : "Not signed in.";

        // So a screenshot of the options page says which server this build talks to.
        public string ServerLabel => $"Server: {ServerEndpoints.Host}";

        public RelayCommand SignInCommand { get; }
        public RelayCommand SignOutCommand { get; }
        public RelayCommand CancelCommand { get; }

        private string _statusMessage = string.Empty;
        private CancellationTokenSource? _signInCancellation;

        private readonly Authenticator _authenticator;
        private readonly Observatory _observatory;

        private void Log(string message) => _observatory.Log(message, "system", LogCategory.System);

        public LoginViewModel(Authenticator authenticator, Observatory observatory) {
            _authenticator = authenticator;
            _observatory = observatory;
            _authenticator.PropertyChanged += AuthenticatorOnPropertyChanged;
            _observatory.UpdateRequired += () => StatusMessage = Observatory.UpdateRequiredMessage;
            if (_observatory.IsUpdateRequired) _statusMessage = Observatory.UpdateRequiredMessage;
            SignInCommand  = new RelayCommand(async () => await SignInAsync(), () => IsSignedOut);
            SignOutCommand = new RelayCommand(async () => await SignOutAsync(), () => _authenticator.IsAuthenticated);
            CancelCommand  = new RelayCommand(CancelSignIn, () => _authenticator.IsSigningIn);
        }

        private void AuthenticatorOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            // The PKCE flow completes on an HttpListener thread, so these can arrive off the UI thread.
            // RelayCommand marshals itself, and OnPropertyChanged is safe because WPF bindings dispatch.
            SignInCommand.RaiseCanExecuteChanged();
            SignOutCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsAuthenticated));
            OnPropertyChanged(nameof(IsSignedOut));
            OnPropertyChanged(nameof(IsSigningIn));
            OnPropertyChanged(nameof(AccountLabel));
        }

        private async Task SignInAsync() {
            // A refused version can't sign in either, so say why instead of opening the browser.
            if (_observatory.IsUpdateRequired) {
                StatusMessage = Observatory.UpdateRequiredMessage;
                return;
            }
            _signInCancellation?.Cancel();
            _signInCancellation?.Dispose();
            _signInCancellation = new CancellationTokenSource();

            StatusMessage = "Waiting for sign-in in your browser...";
            try {
                bool isSignedIn = await _authenticator.SignInAsync(_signInCancellation.Token);
                StatusMessage = isSignedIn ? "Signed in."
                    : _observatory.IsUpdateRequired ? Observatory.UpdateRequiredMessage
                    : "Sign-in did not complete.";
            } catch (Exception ex) {
                StatusMessage = $"Sign-in failed: {ex.Message}";
            }
        }

        private void CancelSignIn() {
            _signInCancellation?.Cancel();
            StatusMessage = "Sign-in cancelled.";
        }

        // Runs as the command's async void, so nothing may escape it.
        private async Task SignOutAsync() {
            try {
                await _authenticator.SignOutAsync();
                StatusMessage = "Signed out.";
                Log(StatusMessage);
            } catch (Exception ex) {
                StatusMessage = $"Sign-out failed: {ex.Message}";
            }
        }
    }
}
