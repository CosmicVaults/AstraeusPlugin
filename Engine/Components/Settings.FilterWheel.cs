using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        /// <summary>A null offset leaves the filter's own offset alone.</summary>
        private sealed record IncomingFilter(short Position, string Name, int? FocusOffset);

        // Set while ApplyFocusOffsets writes several FilterInfo.FocusOffset values, so the per-item
        // PropertyChanged handlers do not push the wheel once per filter.
        private bool _isFilterWheelPushSuppressed;

        private DebouncedPush? _filterWheelSettingsPush;

        private async Task HandleFilterWheelUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IFilterWheelSettings filterWheelSettings = Profile!.FilterWheelSettings;
            bool hasFilters = data.TryGetProperty("filters", out JsonElement filters)
                              && filters.ValueKind != JsonValueKind.Null;
            Dictionary<short, IncomingFilter>? incomingFilters = hasFilters ? ReadIncomingFilters(filters) : null;

            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Bool("disable_guiding_on_filter_change",
                    value => filterWheelSettings.DisableGuidingOnFilterChange = value)
                .Custom("filters", hasFilters, incomingFilters != null,
                    () => ApplyIncomingFilters(filterWheelSettings, incomingFilters!));
            if (!await TryApplyServerUpdateAsync(update, "filter wheel", PushFilterWheelSettingsAsync)) return;

            Log("Filter wheel settings updated via WS: " +
                $"disableGuiding={filterWheelSettings.DisableGuidingOnFilterChange}", device: "filterwheel");
        }

        /// <summary>
        /// Null when the list has the wrong shape, which rejects the whole update. Entries without a
        /// position or a name are skipped.
        /// </summary>
        private static Dictionary<short, IncomingFilter>? ReadIncomingFilters(JsonElement filters) {
            if (filters.ValueKind != JsonValueKind.Array) return null;

            Dictionary<short, IncomingFilter> incomingFilters = new Dictionary<short, IncomingFilter>();
            foreach (JsonElement filterElement in filters.EnumerateArray()) {
                JsonFieldState positionState = JsonFields.ReadShort(filterElement, "position", out short position);
                JsonFieldState nameState = JsonFields.ReadString(filterElement, "name", out string name);
                JsonFieldState offsetState =
                    JsonFields.ReadRoundedInt(filterElement, "focus_offset", out int focusOffset);

                bool hasInvalidField = positionState == JsonFieldState.Invalid
                                       || nameState == JsonFieldState.Invalid
                                       || offsetState == JsonFieldState.Invalid;
                if (hasInvalidField) return null;
                if (positionState == JsonFieldState.Absent || nameState == JsonFieldState.Absent) continue;

                // An absent offset means leave it alone. The Filter Wheel card's Save sends only position
                // and name, and mustn't zero what NINA or the server set.
                int? incomingOffset = null;
                if (offsetState == JsonFieldState.Valid) incomingOffset = focusOffset;
                incomingFilters[position] = new IncomingFilter(position, name, incomingOffset);
            }
            return incomingFilters;
        }

        private static void ApplyIncomingFilters(IFilterWheelSettings filterWheelSettings,
            Dictionary<short, IncomingFilter> incomingFilters) {
            ObserveAllCollection<FilterInfo> filters = filterWheelSettings.FilterWheelFilters;

            List<FilterInfo> removedFilters = filters
                .Where(filter => !incomingFilters.ContainsKey(filter.Position))
                .ToList();
            foreach (FilterInfo removedFilter in removedFilters) {
                filters.Remove(removedFilter);
            }

            foreach (IncomingFilter incomingFilter in incomingFilters.Values) {
                FilterInfo? existingFilter =
                    filters.FirstOrDefault(filter => filter.Position == incomingFilter.Position);
                if (existingFilter == null) {
                    int initialOffset = incomingFilter.FocusOffset ?? 0;
                    filters.Add(new FilterInfo(incomingFilter.Name, initialOffset, incomingFilter.Position));
                    continue;
                }
                existingFilter.Name = incomingFilter.Name;
                if (incomingFilter.FocusOffset is int focusOffset) existingFilter.FocusOffset = focusOffset;
            }
        }

        internal Task PushFilterWheelSettingsAsync() {
            if (_isFilterWheelPushSuppressed) return Task.CompletedTask;
            IFilterWheelSettings filterWheelSettings = Profile!.FilterWheelSettings;
            List<FilterPayload> filters = filterWheelSettings.FilterWheelFilters
                .Select(filter => new FilterPayload {
                    Position = filter.Position,
                    Name = filter.Name,
                    FocusOffset = filter.FocusOffset,
                    IsAutofocusFilter = filter.AutoFocusFilter
                })
                .ToList();
            FilterWheelSettingsPayload payload = new FilterWheelSettingsPayload {
                DisableGuidingOnFilterChange = filterWheelSettings.DisableGuidingOnFilterChange,
                Filters = filters
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "filterwheel"));
        }

        /// <summary>
        /// For Smart Autofocus. Writes the server's offset table into NINA's filter settings so they stay
        /// right outside the autopilot. False if the server couldn't be reached, leaving the profile untouched.
        /// </summary>
        internal async Task<bool> PullAutofocusOffsetsAsync() {
            IReadOnlyDictionary<string, double>? offsets =
                await Observatory.AstraeusWebClient.GetAutofocusOffsetsAsync();
            if (offsets == null) {
                LogWarning("Smart Autofocus: offset table unavailable, so N.I.N.A.'s filter offsets are left as they are.",
                    device: "filterwheel");
                return false;
            }
            int changedCount = ApplyFocusOffsets(offsets);
            Log($"Smart Autofocus: {offsets.Count} offset(s) from the server, {changedCount} changed in N.I.N.A.",
                device: "filterwheel");
            return true;
        }

        /// <summary>Returns how many filters changed.</summary>
        internal int ApplyFocusOffsets(IReadOnlyDictionary<string, double> offsets) {
            ObserveAllCollection<FilterInfo> filters = Profile!.FilterWheelSettings.FilterWheelFilters;
            int changedCount = 0;
            _isFilterWheelPushSuppressed = true;
            try {
                foreach ((string name, double offset) in offsets) {
                    FilterInfo? filter = filters.FirstOrDefault(
                        candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (filter == null) continue;
                    int targetOffset = (int)Math.Round(offset);
                    if (filter.FocusOffset == targetOffset) continue;
                    filter.FocusOffset = targetOffset;
                    changedCount++;
                }
            } finally {
                _isFilterWheelPushSuppressed = false;
            }
            if (changedCount > 0) ScheduleFilterWheelPush();
            return changedCount;
        }

        private void ScheduleFilterWheelPush() {
            _filterWheelSettingsPush ??= CreatePush(PushFilterWheelSettingsAsync, "filter wheel");
            _filterWheelSettingsPush.Schedule();
        }

        private void OnFilterWheelSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            ScheduleFilterWheelPush();
        }

        private void OnFilterInfoChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            if (_isFilterWheelPushSuppressed) return;
            ScheduleFilterWheelPush();
        }

        private void OnFilterCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) {
            if (eventArgs.OldItems != null) {
                foreach (INotifyPropertyChanged filter in eventArgs.OldItems) {
                    filter.PropertyChanged -= OnFilterInfoChanged;
                }
            }
            if (eventArgs.NewItems != null) {
                foreach (INotifyPropertyChanged filter in eventArgs.NewItems) {
                    filter.PropertyChanged += OnFilterInfoChanged;
                }
            }
            ScheduleFilterWheelPush();
        }

        internal void SubscribeFilterItems() {
            ObserveAllCollection<FilterInfo> filters = Profile!.FilterWheelSettings.FilterWheelFilters;
            foreach (INotifyPropertyChanged filter in filters) {
                filter.PropertyChanged += OnFilterInfoChanged;
            }
            filters.CollectionChanged += OnFilterCollectionChanged;
        }

        internal void UnsubscribeFilterItems() {
            ObserveAllCollection<FilterInfo> filters = Profile!.FilterWheelSettings.FilterWheelFilters;
            filters.CollectionChanged -= OnFilterCollectionChanged;
            foreach (INotifyPropertyChanged filter in filters) {
                filter.PropertyChanged -= OnFilterInfoChanged;
            }
        }
    }
}
