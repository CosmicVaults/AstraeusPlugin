using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    [TestFixture]
    public class FlatPanelControlTests {
        ///////////// IsOperable /////////////

        // "Can operate with roof closed" says the cover clears the roof, so it may move whatever the roof reads.
        [Test]
        public void ThePanelIsOperableWhateverTheRoofReadsWhenTheSettingSaysSo() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.Settings.SetShouldOperateFlatPanelWithRoofClosed(true);
            nina.SetRoof(ShutterState.ShutterClosed);

            Assert.That(FlatPanelControl.IsOperable(nina.CreateContext()), Is.True);
        }

        // With no roof there is nothing for the cover to hit, so it is always operable.
        [Test]
        public void ThePanelIsOperableWithNoRoof() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Ignore);
            nina.SetRoof(null);

            Assert.That(FlatPanelControl.IsOperable(nina.CreateContext()), Is.True);
        }

        // Under a roof the cover moves only while the roof reads Open. Closed, moving, error or a dropped dome all hold it still.
        [TestCase(RoofMode.Operate, ShutterState.ShutterOpen, true)]
        [TestCase(RoofMode.WaitForOpen, ShutterState.ShutterOpen, true)]
        [TestCase(RoofMode.Operate, ShutterState.ShutterClosed, false)]
        [TestCase(RoofMode.Operate, ShutterState.ShutterOpening, false)]
        [TestCase(RoofMode.Operate, ShutterState.ShutterError, false)]
        [TestCase(RoofMode.WaitForOpen, null, false)]
        public void UnderARoofThePanelMovesOnlyWhileTheRoofReadsOpen(RoofMode mode, ShutterState? shutter, bool isOperable) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(mode);
            nina.SetRoof(shutter);

            Assert.That(FlatPanelControl.IsOperable(nina.CreateContext()), Is.EqualTo(isOperable));
        }

        ///////////// IsCloseWanted /////////////

        // Only OpenAndClose asks for an end-of-night close, and only when there is a panel to close: one connected, or one
        // configured so the close can reconnect it or report that it couldn't.
        [TestCase(FlatPanelMode.OpenAndClose, true, false, true)]
        [TestCase(FlatPanelMode.OpenAndClose, false, true, true)]
        [TestCase(FlatPanelMode.OpenAndClose, false, false, false)]
        [TestCase(FlatPanelMode.EnsureOpen, true, false, false)]
        [TestCase(FlatPanelMode.Ignore, true, false, false)]
        public void ACloseIsWantedOnlyInOpenAndCloseWithAPanelToClose(FlatPanelMode mode, bool isConnected, bool hasDefaultDevice,
            bool isWanted) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotFlatPanelMode(mode);
            nina.SetFlatPanel(isConnected ? CoverState.Open : null);
            if (hasDefaultDevice) nina.FlatPanel.SetDefaultDevice("Panel 1", "test");

            Assert.That(FlatPanelControl.IsCloseWanted(nina.CreateContext()), Is.EqualTo(isWanted));
        }

        ///////////// CloseBeforeRoofAsync /////////////

        // A cover that can move under a closed roof waits for the after-roof step, so the roof is never held open for it.
        [Test]
        public async Task ACoverThatMovesUnderAClosedRoofIsNotClosedBeforeTheRoof() {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterOpen, FlatPanelMode.OpenAndClose);
            nina.Settings.SetShouldOperateFlatPanelWithRoofClosed(true);

            bool didTry = await FlatPanelControl.CloseBeforeRoofAsync(nina.CreateContext(), "test", CancellationToken.None);

            Assert.That(didTry, Is.False);
            Assert.That(nina.Commands, Is.Empty);
        }

        // Only OpenAndClose asks for an end-of-night close. EnsureOpen and Ignore leave the cover where it is.
        [TestCase(FlatPanelMode.Ignore)]
        [TestCase(FlatPanelMode.EnsureOpen)]
        public async Task ACoverIsOnlyClosedInOpenAndCloseMode(FlatPanelMode mode) {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterOpen, mode);

            bool didTry = await FlatPanelControl.CloseBeforeRoofAsync(nina.CreateContext(), "test", CancellationToken.None);

            Assert.That(didTry, Is.False);
            Assert.That(nina.Commands, Is.Empty);
        }

        // With no panel connected and none configured there is nothing to close, and nothing to report either.
        [Test]
        public async Task WithNoPanelThereIsNothingToClose() {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterOpen, FlatPanelMode.OpenAndClose);
            nina.SetFlatPanel(null);

            bool didTry = await FlatPanelControl.CloseBeforeRoofAsync(nina.CreateContext(), "test", CancellationToken.None);

            Assert.That(didTry, Is.False);
            Assert.That(nina.Commands, Is.Empty);
        }

        // A roof that has already closed can't have the cover swung through it, so the before-roof close is skipped.
        [Test]
        public async Task ACoverIsNotClosedBeforeTheRoofWhenTheRoofIsNotOpen() {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterClosed, FlatPanelMode.OpenAndClose);

            bool didTry = await FlatPanelControl.CloseBeforeRoofAsync(nina.CreateContext(), "test", CancellationToken.None);

            Assert.That(didTry, Is.False);
            Assert.That(nina.Commands, Is.Empty);
        }

        // With the roof open and the mode asking for it, the cover is closed before the roof, with exactly one close sent
        // to the panel.
        [Test]
        public async Task ACoverIsClosedBeforeTheRoofWhenTheRoofIsOpen() {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterOpen, FlatPanelMode.OpenAndClose);

            bool didTry = await FlatPanelControl.CloseBeforeRoofAsync(nina.CreateContext(), "test", CancellationToken.None);

            Assert.That(didTry, Is.True);
            Assert.That(nina.Commands, Is.EqualTo(new[] { "flat panel: CloseCover" }));
        }

        ///////////// CloseAfterRoofAsync /////////////

        // A stand-down spends one timeout on the panel. If the before-roof step already tried, the after-roof step must not
        // try again.
        [Test]
        public async Task TheAfterRoofCloseIsSkippedWhenTheBeforeRoofCloseAlreadyTried() {
            FakeNina nina = ReadyToClose(RoofMode.Ignore, null, FlatPanelMode.OpenAndClose);

            await FlatPanelControl.CloseAfterRoofAsync(nina.CreateContext(), "test", hasAlreadyTried: true, CancellationToken.None);

            Assert.That(nina.Commands, Is.Empty);
        }

        // After the roof, a cover that may still move is closed. With the setting on it moves under the closed roof.
        [Test]
        public async Task TheAfterRoofCloseClosesACoverThatMayStillMove() {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterClosed, FlatPanelMode.OpenAndClose);
            nina.Settings.SetShouldOperateFlatPanelWithRoofClosed(true);

            await FlatPanelControl.CloseAfterRoofAsync(nina.CreateContext(), "test", hasAlreadyTried: false, CancellationToken.None);

            Assert.That(nina.Commands, Is.EqualTo(new[] { "flat panel: CloseCover" }));
        }

        // After the roof has closed, a cover that can't move under it is left open and reported, never driven into the roof.
        [Test]
        public async Task TheAfterRoofCloseLeavesACoverThatCannotMoveUnderTheClosedRoof() {
            FakeNina nina = ReadyToClose(RoofMode.Operate, ShutterState.ShutterClosed, FlatPanelMode.OpenAndClose);

            await FlatPanelControl.CloseAfterRoofAsync(nina.CreateContext(), "test", hasAlreadyTried: false, CancellationToken.None);

            Assert.That(nina.Commands, Is.Empty);
        }

        // A connected panel with its cover open, under the given roof, in the given mode.
        private static FakeNina ReadyToClose(RoofMode roofMode, ShutterState? shutter, FlatPanelMode panelMode) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(roofMode);
            nina.Settings.SetAutopilotFlatPanelMode(panelMode);
            nina.SetRoof(shutter);
            nina.SetFlatPanel(CoverState.Open);
            return nina;
        }
    }
}