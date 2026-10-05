using Newtonsoft.Json.Linq;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel.Equipment.FilterWheel;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class FilterWheel(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IFilterWheelMediator filterWheelMediator)
        : DeviceComponent<IFilterWheelVM, IFilterWheelConsumer, FilterWheelInfo>(
            filterWheelMediator, observatory, webSocketBus) {

        public override string DeviceType { get; } = "filterwheel";
        public override string DisplayName { get; } = "Filter Wheel";
        protected override string? RegistrationEndpoint => "filterwheels";
        
        public override bool IsBusy => LastInfo?.IsMoving == true;

        protected override async Task RegisterDeviceAsync() {
            FilterWheelInfo info = filterWheelMediator.GetInfo();
            List<int> filterIds = await RegisterFiltersAsync(info);

            await Observatory.AstraeusWebClient.PostObservatoryAsync("filterwheels", new {
                name = info.Name,
                device_id = info.DeviceId,
                last_connected = UtcTimestampNow(),
                filter_ids = filterIds
            });
        }

        private async Task<List<int>> RegisterFiltersAsync(FilterWheelInfo info) {
            List<int> filterIds = new List<int>();
            if (!TryGetDeviceViewModel<FilterWheelVM>(out FilterWheelVM? filterWheelViewModel)
                || filterWheelViewModel == null) {
                return filterIds;
            }
            ICollection<FilterInfo>? allFilters = filterWheelViewModel.GetAllFilters();
            if (allFilters == null) return filterIds;

            // Posted together. One round trip per slot in series takes most of a second over the tunnel.
            List<Task<JObject?>> posts = new List<Task<JObject?>>();
            foreach (FilterInfo filter in allFilters) {
                posts.Add(Observatory.AstraeusWebClient.PostObservatoryAsync("filters", new {
                    name = filter.Name,
                    device_id = $"{info.DeviceId}-{filter.Position}",
                    position = (int)filter.Position,
                    last_connected = UtcTimestampNow()
                }));
            }
            JObject?[] results = await Task.WhenAll(posts);
            foreach (JObject? result in results) {
                int? filterId = result?["id"]?.Value<int>();
                if (filterId.HasValue) filterIds.Add(filterId.Value);
            }
            return filterIds;
        }

        public FilterInfo GetCurrentFilter() {
            FilterWheelInfo info = filterWheelMediator.GetInfo();
            return info.SelectedFilter;
        }
        
        public override WsMessage? GetUpdateMessage() {
            if (LastInfo is null) {
                return null;
            }
            FilterInfo? selected = LastInfo.SelectedFilter;
            FilterWheelPayload payload = new FilterWheelPayload{
                DeviceId = DeviceId,
                Name = LastInfo.Name,
                IsMoving = LastInfo.IsMoving,
                CurrentPosition = selected?.Position,
                SelectedFilter = selected?.Name,
                IsConnected = true
            };
            return new FilterWheelUpdate(payload);
        }

        public override WsMessage? GetDeviceStaticInfo() {
            FilterWheelInfo info = filterWheelMediator.GetInfo();
            FilterWheelPayload payload = new FilterWheelPayload{
                DeviceId = DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                DriverVersion = info.DriverVersion,
                IsMoving = info.IsMoving,
                CurrentPosition = info.SelectedFilter?.Position,
                SelectedFilter = info.SelectedFilter?.Name
            };
            return new FilterWheelUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "change":
                    if (JsonFields.ReadShort(command.Payload, "filter", out short filterPosition)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "filter").ObserveFaults(LogCommandFault);
                        break;
                    }
                    ChangeFilterCommandAsync(command.Id, filterPosition).ObserveFaults(LogCommandFault);
                    break;
                case "fetchFilters":
                    FetchFiltersCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
            }
            return Task.CompletedTask;
        }

        private CancellationTokenSource? _changeCancellationSource;

        private async Task ChangeFilterCommandAsync(string id, short position) {
            if (!TryGetDeviceViewModel<FilterWheelVM>(out FilterWheelVM? filterWheelViewModel)) {
                await BroadcastMessageReceivedAsync(id, false, "N.I.N.A.'s filter wheel controls are not available");
                return;
            }
            FilterInfo? newFilter = filterWheelViewModel.GetAllFilters()
                ?.FirstOrDefault(filter => filter.Position == position);
            if (newFilter == null) {
                await BroadcastMessageReceivedAsync(id, false, $"There is no filter at position {position}");
                return;
            }

            await BroadcastMessageReceivedAsync(id, true);
            Log("Changing filter...");
            try {
                await filterWheelMediator.ChangeFilter(newFilter, BeginOperation(ref _changeCancellationSource))!;
                Log("Filter changed.");
            } catch (OperationCanceledException) {
                Log("Filter change aborted.");
            } catch (Exception ex) {
                LogWarning($"Changing the filter failed: {ex.Message}");
            }
        }

        private async Task FetchFiltersCommandAsync(string id) {
            await BroadcastMessageReceivedAsync(id, true);
            if (!TryGetDeviceViewModel<FilterWheelVM>(out FilterWheelVM? filterWheelViewModel)) return;
            ICollection<FilterInfo>? allFilters = filterWheelViewModel?.GetAllFilters();
            if (allFilters == null || allFilters.Count == 0) return;
            List<FilterListItem> filtersList = allFilters
                .Select(filter => new FilterListItem(filter.Position, filter.Name))
                .ToList();
            FilterListUpdate message = new FilterListUpdate(filtersList);
            await WebSocketBus.SendAsync(message);
        }

        /// <summary>
        /// Changes to the named filter for the autopilot, since the scheduler identifies filters by name.
        /// </summary>
        public async Task<bool> ChangeFilterByNameAsync(string name, CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (!TryGetDeviceViewModel<FilterWheelVM>(out FilterWheelVM? filterWheelViewModel)
                || filterWheelViewModel == null) {
                return false;
            }
            ICollection<FilterInfo>? allFilters = filterWheelViewModel.GetAllFilters();
            if (allFilters == null || allFilters.Count == 0) return false;
            FilterInfo? matchingFilter = allFilters.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (matchingFilter == null) {
                string available = string.Join(", ", allFilters.Select(filter => filter.Name));
                LogWarning(
                    $"No filter named '{name}', so leaving the current filter in place. Available: {available}.");
                return false;
            }
            await filterWheelMediator.ChangeFilter(matchingFilter, cancellationToken)!;
            return true;
        }
    }
}
