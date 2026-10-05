using System.Windows;
using System.Windows.Controls;

namespace CosmicVaults.NINA.Astraeus.Controls {
    public partial class AutopilotSettingsView : UserControl {
        public AutopilotSettingsView() {
            InitializeComponent();
            // Repopulate when the page becomes visible. By then N.I.N.A. has finished scanning equipment,
            // which it hasn't at plugin init or the first Loaded.
            IsVisibleChanged += (_, eventArgs) => {
                if (eventArgs.NewValue is true) (DataContext as AutopilotSettingsViewModel)?.RefreshDevices();
            };
        }

        // Refresh a row's device list as its dropdown opens, so it lists what's available at that moment.
        private void DeviceCombo_DropDownOpened(object sender, System.EventArgs eventArgs) {
            ((sender as ComboBox)?.DataContext as DeviceSelectorRow)?.Refresh();
        }
    }
}
