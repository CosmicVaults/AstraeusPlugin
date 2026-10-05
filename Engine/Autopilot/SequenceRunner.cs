using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Serialization;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// Runs a N.I.N.A. advanced-sequencer .json file. Plugins can't build a working
    /// SequencerFactory, so we borrow N.I.N.A.'s live one by reflecting into the sequence mediator's
    /// private sequenceNavigation field. This is a little naughty and could break If N.I.N.A. renames it.
    /// </summary>
    public class SequenceRunner {
        private const string NavigationFieldName = "sequenceNavigation";
        private const string Sequence2VmPropertyName = "Sequence2VM";
        private const string FactoryPropertyName = "SequencerFactory";

        private readonly ISequenceMediator? _sequenceMediator;

        private ISequencerFactory? _factory;
        private readonly object _factoryLock = new();
        public Observatory? Observatory { get; set; }

        private const string LogDevice = "autopilot";
        private void LogWarning(string message) => Observatory?.LogWarning(message, LogDevice, LogCategory.Autopilot);
        private void LogError(string message)   => Observatory?.LogError(message, LogDevice, LogCategory.Autopilot);

        public SequenceRunner(ISequenceMediator? sequenceMediator) {
            _sequenceMediator = sequenceMediator;
        }

        /// <summary>
        /// True if the check throws, so a caller that stands aside for a sequence errs toward standing aside.
        /// </summary>
        public bool IsNinaSequenceRunning() {
            if (_sequenceMediator == null) return false;
            try {
                return _sequenceMediator.Initialized
                       && (_sequenceMediator.IsAdvancedSequenceRunning()
                           || _sequenceMediator.GetAllTargetsInSimpleSequence()
                               .Any(target => target.Status == SequenceEntityStatus.RUNNING));
            } catch (Exception) {
                return true;
            }
        }

        /// <summary>
        /// False if there was nothing to run or it failed. A cancellation is rethrown so the caller sees it.
        /// </summary>
        public async Task<bool> RunAsync(string? filePath, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            // Only the file name goes in these messages. They reach the dashboard as notifications,
            // and the folder path can contain the user's name.
            string name = Path.GetFileName(filePath)!;

            if (!File.Exists(filePath)) {
                LogWarning($"Sequence file not found: {name}");
                return false;
            }

            string json;
            try {
                json = await File.ReadAllTextAsync(filePath!, cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogError($"Failed to read sequence file '{name}': {ex.Message}");
                return false;
            }

            ISequenceRootContainer? root;
            try {
                root = await BuildRootContainerAsync(json);
            } catch (Exception ex) {
                LogError($"Failed to deserialize sequence '{name}': {ex}");
                return false;
            }

            if (root == null) {
                LogError($"Could not build a runnable sequence from '{name}' " +
                         "(N.I.N.A. sequencer not ready, or not a root container). Skipping.");
                return false;
            }

            Sequencer sequencer = new Sequencer(root);
            Progress<ApplicationStatus> progress = new Progress<ApplicationStatus>();

            try {
                // Third arg true skips the validation prompt, since we run headless and can't block on a dialog.
                await sequencer.Start(progress, cancellationToken, true);
                return true;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogError($"Sequence '{name}' failed: {ex.Message}");
                return false;
            }
        }

        // Deserializes on the UI dispatcher, because sequence entities create WPF-bound objects when
        // cloned from the factory prototypes. Returns null when the factory can't be reached.
        private Task<ISequenceRootContainer?> BuildRootContainerAsync(string json) {
            ISequenceRootContainer? Work() {
                ISequencerFactory? factory = GetFactory();
                if (factory == null) return null;
                SequenceJsonConverter converter = new SequenceJsonConverter(factory);
                return converter.Deserialize(json) as ISequenceRootContainer;
            }

            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                return Task.FromResult(Work());
            }
            return dispatcher.InvokeAsync(Work).Task;
        }

        // Returns null with a warning until NINA's sequencer VM is ready.
        private ISequencerFactory? GetFactory() {
            lock (_factoryLock) {
                if (_factory != null) return _factory;

                if (_sequenceMediator == null) {
                    LogWarning("Sequence mediator unavailable, so sequences cannot run.");
                    return null;
                }

                object? navigation = _sequenceMediator.GetType()
                    .GetField(NavigationFieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(_sequenceMediator);
                if (navigation == null) {
                    LogWarning($"Could not reach N.I.N.A. sequencer navigation (field '{NavigationFieldName}'). " +
                               "Sequencer not initialized yet?");
                    return null;
                }

                object? sequenceViewModel = navigation.GetType()
                    .GetProperty(Sequence2VmPropertyName)?.GetValue(navigation);
                if (sequenceViewModel == null) {
                    LogWarning($"N.I.N.A. sequencer view model ('{Sequence2VmPropertyName}') not available yet.");
                    return null;
                }

                object? factoryValue = sequenceViewModel.GetType()
                    .GetProperty(FactoryPropertyName)?.GetValue(sequenceViewModel);
                if (factoryValue is not ISequencerFactory factory) {
                    LogWarning("N.I.N.A. sequencer factory not ready yet. Open the sequencer once, then retry.");
                    return null;
                }
                _factory = factory;
                return factory;
            }
        }
    }
}
