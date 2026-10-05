using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>
    /// An ASCOM switch hub and its ports. It isn't called Switch because ASCOM and N.I.N.A. use that
    /// word for a single port (ISwitch), and those are what the dashboard draws.
    /// </summary>
    public class SwitchHub(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        ISwitchMediator switchMediator)
        : DeviceComponent<ISwitchVM, ISwitchConsumer, SwitchInfo>(switchMediator, observatory, webSocketBus) {

        public override string DeviceType { get; } = "switch";
        public override string DisplayName { get; } = "Switch";

        /// <summary>
        /// How often a reading is filed for the history graphs. Just under the loop's 60s idle tick,
        /// since a 60s check against a 60s tick aliases to one sample every two minutes.
        /// </summary>
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(55);

        /// <summary>N.I.N.A.'s own tolerance for "the switch reached the value it was asked for".</summary>
        private const double SwitchTolerance = 0.00001;

        private const int NotFoundIndex = -1;

        private DateTime _lastSampleAt = DateTime.MinValue;

        // Sets in progress. Non-zero speeds up the dashboard tick while a port is moving, like a slew
        // or a filter change does.
        private int _setsInFlight;

        public override bool IsBusy => Volatile.Read(ref _setsInFlight) > 0;

        ///////////// Telemetry /////////////

        public override WsMessage? GetUpdateMessage() {
            SwitchPayload? payload = BuildReadingPayload();
            return payload is null ? null : new SwitchUpdate(payload, "dashboard");
        }

        /// <summary>
        /// Files one reading a minute on the durable path so a night's voltages and currents are
        /// recorded even with nobody watching. GetUpdateMessage only runs while a browser tab is open.
        /// Weather does the same.
        /// </summary>
        public override async Task LateUpdate() {
            // Before base.LateUpdate(), which nulls LastInfo.
            if (Observatory.IsSocketOpen && LastInfo is not null && IsConnected
                && DateTime.UtcNow - _lastSampleAt >= SampleInterval) {
                if (BuildReadingPayload() is { } payload) {
                    _lastSampleAt = DateTime.UtcNow;
                    await WebSocketBus.SendAsync(new SwitchUpdate(payload, "event"));
                }
            }
            await base.LateUpdate();
        }

        /// <summary>Id and value only. The dashboard merges these onto the static frame by id.</summary>
        private SwitchPayload? BuildReadingPayload() {
            if (LastInfo is null) return null;
            List<ISwitch> ports = AllSwitches(LastInfo);
            if (ports.Count == 0) return null;
            return new SwitchPayload {
                DeviceId = DeviceId,
                IsConnected = true,
                Switches = ports
                    .Select(port => new SwitchItem { Id = port.Id, Value = DevicePayload.CleanValue(port.Value) })
                    .ToList()
            };
        }

        public override WsMessage? GetDeviceStaticInfo() {
            // GetInfo returns null when N.I.N.A. has no switch view model registered.
            if (switchMediator.GetInfo() is not { } info) return null;
            SwitchPayload payload = new SwitchPayload {
                DeviceId = info.DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                Switches = AllSwitches(info).Select(Describe).ToList()
            };
            return new SwitchUpdate(payload);
        }

        private static SwitchItem Describe(ISwitch port) {
            IWritableSwitch? writable = port as IWritableSwitch;
            return new SwitchItem {
                Id = port.Id,
                Name = port.Name,
                Description = port.Description,
                Value = DevicePayload.CleanValue(port.Value),
                IsWritable = writable is not null,
                IsBoolean = writable is not null && IsBooleanSwitch(writable),
                Minimum = writable is null ? null : DevicePayload.CleanValue(writable.Minimum),
                Maximum = writable is null ? null : DevicePayload.CleanValue(writable.Maximum),
                StepSize = writable is null ? null : DevicePayload.CleanValue(writable.StepSize)
            };
        }

        ///////////// Commands /////////////

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                // Off the receive loop like the flat panel's. Each sends its acknowledgement first,
                // then makes a driver call that's synchronous inside N.I.N.A. and can block for as
                // long as it likes.
                case "setValue":
                    SetValueAsync(command).ObserveFaults(LogCommandFault);
                    break;
                case "toggleSwitch":
                    ToggleSwitchAsync(command).ObserveFaults(LogCommandFault);
                    break;
                default:
                    LogWarning($"Unhandled switch action: {command.Action}");
                    break;
            }
            return Task.CompletedTask;
        }

        private async Task SetValueAsync(WsCommand command) {
            if (!TryReadSwitchId(command.Payload, out short id)) {
                await BroadcastMessageReceivedAsync(command.Id, false, "No switch id in the request");
                return;
            }
            if (!command.Payload.TryGetProperty("value", out JsonElement element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetDouble(out double value)) {
                await BroadcastMessageReceivedAsync(command.Id, false, "No value in the request");
                return;
            }
            await ApplyAsync(command.Id, id, value);
        }

        private async Task ToggleSwitchAsync(WsCommand command) {
            if (!TryReadSwitchId(command.Payload, out short id)) {
                await BroadcastMessageReceivedAsync(command.Id, false, "No switch id in the request");
                return;
            }
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(command.Id, false, "No switch hub is connected");
                return;
            }
            if (!TryFindWritable(id, out _, out IWritableSwitch? port)) {
                await BroadcastMessageReceivedAsync(command.Id, false, NotSettable(id));
                return;
            }
            if (!IsBooleanSwitch(port)) {
                await BroadcastMessageReceivedAsync(command.Id, false,
                    $"\"{port.Name}\" is not an on/off switch. Set a value on it instead");
                return;
            }
            await ApplyAsync(command.Id, id, port.Value == 0 ? 1 : 0);
        }

        /// <summary>
        /// N.I.N.A.'s SetSwitchValue returns a plain Task and drops the success flag, so we check the
        /// value ourselves.
        /// </summary>
        private async Task ApplyAsync(string? commandId, short id, double value) {
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(commandId, false, "No switch hub is connected");
                return;
            }
            // The index is the position in the writable list, not the ASCOM id (see TryFindWritable).
            if (!TryFindWritable(id, out int index, out IWritableSwitch? port)) {
                await BroadcastMessageReceivedAsync(commandId, false, NotSettable(id));
                return;
            }
            // A step of zero, NaN or infinity makes N.I.N.A.'s value snapping produce NaN (x % 0 doesn't
            // throw). That NaN goes to the driver, and the check for a value that never arrived compares
            // against NaN and passes, so it would report success having set nothing. Refuse it up front.
            if (!IsUsableStep(port.StepSize)) {
                LogWarning($"Refusing to set \"{port.Name}\": the driver reports a step size of {port.StepSize}.");
                await BroadcastMessageReceivedAsync(commandId, false,
                    $"\"{port.Name}\" reports a step size of {port.StepSize}, " +
                    "which is not a size anything can be set to");
                return;
            }
            if (double.IsNaN(value) || value < port.Minimum || value > port.Maximum) {
                await BroadcastMessageReceivedAsync(commandId, false,
                    $"{value} is outside \"{port.Name}\"'s range of {port.Minimum} to {port.Maximum}");
                return;
            }

            await BroadcastMessageReceivedAsync(commandId, true);
            Interlocked.Increment(ref _setsInFlight);
            try {
                Log($"Setting \"{port.Name}\" to {value}...");
                await switchMediator.SetSwitchValue((short)index, value, NoProgress, ComponentToken);
            } catch (OperationCanceledException) {
                Log($"Setting \"{port.Name}\" was abandoned.");
                return;
            } catch (Exception ex) {
                LogError($"Could not set \"{port.Name}\": {ex.Message}");
                return;
            } finally {
                Interlocked.Decrement(ref _setsInFlight);
            }

            // If the hub disconnected mid-set, the checks below mean nothing. The port object is still
            // here but nothing reads it.
            if (!IsConnected) {
                LogWarning($"The switch hub disconnected while setting \"{port.Name}\".");
                return;
            }

            // Compare with TargetValue, not the requested value. N.I.N.A. snaps to the driver's step, so
            // asking for 37 on a step of 5 lands on 35, and that isn't a failure.
            if (Math.Abs(port.Value - port.TargetValue) > SwitchTolerance) {
                LogError($"\"{port.Name}\" did not reach {port.TargetValue}. It still reads {port.Value}.");
                return;
            }
            Log($"\"{port.Name}\" is {port.Value}.");

            // Warn only. The plugin can't know which port feeds what, so this never refuses. But
            // switching the mount off mid-run is worth a line in the log and on the dashboard.
            if (Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot)
                && autopilot.IsRunning)
                LogWarning($"\"{port.Name}\" was changed to {port.Value} while the autopilot is running.");
        }

        ///////////// Helpers /////////////

        /// <summary>
        /// Maps an ASCOM id to its index in the writable list, which N.I.N.A.'s SetSwitchValue wants. They
        /// differ on any hub that mixes sensors with outputs, and only the id is a stable key.
        /// </summary>
        private bool TryFindWritable(short id, out int index, [NotNullWhen(true)] out IWritableSwitch? port) {
            index = NotFoundIndex;
            port = null;
            ReadOnlyCollection<IWritableSwitch>? writable = switchMediator.GetInfo()?.WritableSwitches;
            if (writable is null) return false;
            for (int i = 0; i < writable.Count; i++) {
                if (writable[i].Id != id) continue;
                index = i;
                port = writable[i];
                return true;
            }
            return false;
        }

        private static string NotSettable(short id) => $"Switch {id} is not one this hub can set";

        /// <summary>
        /// Both collections are null once the hub disconnects, because DeviceInfo.Reset copies every
        /// writable property from a fresh instance.
        /// </summary>
        private static List<ISwitch> AllSwitches(SwitchInfo info) {
            List<ISwitch> ports = new List<ISwitch>();
            if (info.WritableSwitches is { } writable) ports.AddRange(writable);
            if (info.ReadonlySwitches is { } readOnly) ports.AddRange(readOnly);
            ports.Sort((left, right) => left.Id.CompareTo(right.Id));
            return ports;
        }

        /// <summary>
        /// The same test N.I.N.A.'s SwitchTemplateSelector uses to pick a toggle over a slider, so the
        /// dashboard and N.I.N.A.'s equipment page agree.
        /// </summary>
        private static bool IsBooleanSwitch(IWritableSwitch port)
            => port.Minimum == 0 && port.Maximum == 1 && port.StepSize == 1;

        private static bool IsUsableStep(double step)
            => !double.IsNaN(step) && !double.IsInfinity(step) && step > 0;

        private static bool TryReadSwitchId(JsonElement payload, out short id) {
            id = 0;
            return payload.TryGetProperty("id", out JsonElement element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt16(out id);
        }
    }
}
