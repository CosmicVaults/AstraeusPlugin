using System.ComponentModel.Composition;
using System.Windows;

namespace CosmicVaults.NINA.Astraeus {
    [Export(typeof(ResourceDictionary))]
    partial class Options : ResourceDictionary {

        public Options() {
            InitializeComponent();
        }
    }
}