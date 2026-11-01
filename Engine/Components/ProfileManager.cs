using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Components
{
    public class ProfileManager(Observatory observatory, IWebSocketBus webSocketBus) : ServiceComponent(observatory, webSocketBus)
    {
        public override string DeviceType => "profile";
        public override LogCategory DefaultLogCategory => LogCategory.System;
        public override WsMessage? GetUpdateMessage() => null;
        
        private IProfileService ProfileService => Observatory.Settings.ProfileService;
       
        private int _isSwitching;
        public bool IsSwitching => Volatile.Read(ref _isSwitching) == 1;

        ///////////// Lifecycle /////////////
        public override async Task Awake()
        {
            await base.Awake();
            // Here rather than in Start, so the socket's first open is not missed.
            WebSocketBus.Connected += OnConnected;
        }

        public override async Task Start()
        {
            await base.Start();
            ProfileService.ProfileChanged += OnProfileChanged;
        }

        public override Task Destroy()
        {
            if (IsDestroyed) return Task.CompletedTask;
            WebSocketBus.Connected -= OnConnected;
            ProfileService.ProfileChanged -= OnProfileChanged;
            return base.Destroy();
        }
        
        ///////////// Commands /////////////
        protected override Task HandleCommandAsync(WsCommand command)
        {
            switch (command.Action)
            {
                case "switch":
                    SwitchCommandAsync(command).ObserveFaults(LogCommandFault);
                    break;
                case "fetchProfiles":
                    FetchProfilesCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
            }
            return Task.CompletedTask;
        }
        
        private async Task FetchProfilesCommandAsync(string id)
        {
            await BroadcastMessageReceivedAsync(id, true);
            await PublishProfileListAsync();
        }
        
        ////////// Switch Command ////////////
        ///
        
         private async Task SwitchCommandAsync(WsCommand command)
        {
            if (JsonFields.ReadString(command.Payload, "id", out string idText) != JsonFieldState.Valid
                || !Guid.TryParse(idText, out Guid profileId))
            {
                await RejectMalformedCommandAsync(command, "id");
                return;
            }
            ProfileMeta? target = SnapshotProfiles().FirstOrDefault(meta => meta.Id == profileId);
            if (target == null)
            {
                await BroadcastMessageReceivedAsync(command.Id, false, "That profile is not in N.I.N.A.'s list");
                // The dashboard's list is out of date, so it gets the current one.
                await PublishProfileListAsync();
                return;
            }
            if (ProfileService.ActiveProfile?.Id == profileId)
            {
                await BroadcastMessageReceivedAsync(command.Id, true, $"'{target.Name}' is already the active profile");
                await PublishProfileListAsync();
                return;
            }
            if (DescribeBlocker() is { } reason)
            {
                await BroadcastMessageReceivedAsync(command.Id, false, reason);
                return;
            }
            if (Interlocked.CompareExchange(ref _isSwitching, 1, 0) == 1)
            {
                await BroadcastMessageReceivedAsync(command.Id, false, "A profile switch is already in progress");
                return;
            }

            await BroadcastMessageReceivedAsync(command.Id, true);
            // Off the socket's dispatch loop. A disconnect can block in a driver, and SelectProfile waits on
            // N.I.N.A.'s UI thread.
            Task.Run(() => SwitchAsync(target)).ObserveFaults(LogCommandFault);
        }

        // The reason a switch has to wait, or null when it can go ahead.
        private string? DescribeBlocker()
        {
            if (IsAutopilotRunning) return "The autopilot is running, so the profile can't be changed now";
            if (Observatory.SequenceRunner?.IsNinaSequenceRunning() == true)
            {
                return "A N.I.N.A. sequence is running, so the profile can't be changed now";
            }
            foreach (IDeviceComponent device in Observatory.GetComponents<IDeviceComponent>())
            {
                if (device.FindOpenConnectPrompt() is { } prompt)
                {
                    return $"N.I.N.A.'s \"{prompt}\" window is open for the {device.DisplayName}, " +
                           "so the profile can't be changed until it is answered";
                }
            }
            return null;
        }

        private async Task SwitchAsync(ProfileMeta target)
        {
            try
            {
                Log($"Switching N.I.N.A. profile to '{target.Name}'.");
                // Try disconnecting every device. Switch will fail if a device can not be disconnected
                foreach (IDeviceComponent device in Observatory.GetComponents<IDeviceComponent>())
                {
                    if (device.IsConnected && !await device.TryDisconnectAsync())
                    {
                        LogError($"The {device.DisplayName} did not disconnect, so the profile was not changed.");
                        return;
                    }
                }

                bool isSelected = ProfileService.SelectProfile(target);
                // False also comes back when another plugin's handler threw after the switch was made.
                if (!isSelected && ProfileService.ActiveProfile?.Id != target.Id)
                {
                    LogError($"N.I.N.A. could not load the '{target.Name}' profile. It may be open in another N.I.N.A.");
                    return;
                }
                if (!isSelected)
                {
                    LogWarning($"The profile changed to '{target.Name}', but part of N.I.N.A.'s profile change " +
                               "failed. N.I.N.A.'s log has the details.");
                }

                // A disabled plugin closes the socket, and the dashboard that asked could not switch back.
                if (!Observatory.Settings.IsPluginEnabled())
                {
                    Observatory.Settings.SetIsPluginEnabled(true);
                    Observatory.Cadence.Signal();
                    LogWarning($"Astraeus was switched off in the '{target.Name}' profile, so it has been switched on.");
                }
                Log($"Switched N.I.N.A. profile to '{target.Name}'.");
            }
            finally
            {
                Volatile.Write(ref _isSwitching, 0);
            }
        }
        
        ///////////// Events /////////////
        ///
        
        private void OnProfileChanged(object? sender, EventArgs eventArgs) =>
            PublishProfileListAsync().ObserveFaults(exception =>
                LogWarning($"Could not send the profile list: {exception.Message}"));
     
        private async void OnConnected()
        {
            try
            {
                await Task.Delay(PushOnConnectDelay, Observatory.Clock);
                await PublishProfileListAsync();
            }
            catch (Exception ex)
            {
                LogWarning($"Could not send the profile list: {ex.Message}");
            }
        }
        
        
        private async Task PublishProfileListAsync()
        {
            Guid? activeId = ProfileService.ActiveProfile?.Id;
            List<ProfileListItem> profiles = SnapshotProfiles()
                .Select(meta => new ProfileListItem(
                    meta.Id.ToString(),
                    meta.Name,
                    string.IsNullOrEmpty(meta.Description) ? null : meta.Description,
                    meta.Id == activeId))
                .ToList();
            await WebSocketBus.SendAsync(new ProfileListUpdate(profiles, activeId?.ToString()));
        }
        
        // N.I.N.A. changes this list on its UI thread, so it is copied there.
        private List<ProfileMeta> SnapshotProfiles()
        {
            List<ProfileMeta> profiles = [];
            InvokeOnUiThread(() => profiles = ProfileService.Profiles.ToList());
            return profiles;
        }
    }
}