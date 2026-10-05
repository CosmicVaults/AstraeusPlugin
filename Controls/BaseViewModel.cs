using NINA.Core.Utility;

namespace CosmicVaults.NINA.Astraeus.Controls {
    // Use BaseINPC's INotifyPropertyChanged as is. Don't re-declare PropertyChanged or re-list the
    // interface here. That hides BaseINPC's event, so WPF subscribes to an event nothing raises and
    // every change notification to the UI is lost.
    public class BaseViewModel : BaseINPC {
        public void RefreshAllProperties() => RaiseAllPropertiesChanged();
    }
}