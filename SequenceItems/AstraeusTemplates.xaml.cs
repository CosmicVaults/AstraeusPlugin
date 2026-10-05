using System.ComponentModel.Composition;
using System.Windows;

namespace CosmicVaults.NINA.Astraeus.SequenceItems {
    [Export(typeof(ResourceDictionary))]
    public partial class AstraeusTemplates : ResourceDictionary {
        public AstraeusTemplates() {
            InitializeComponent();
        }
    }
}
