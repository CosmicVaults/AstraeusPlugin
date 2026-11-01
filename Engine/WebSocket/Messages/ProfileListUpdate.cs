using System.Collections.Generic;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages
{
    public sealed record ProfileListItem(
        string id,
        string name,
        string? description,
        bool isActive
    );

    public sealed record ProfileListUpdate : WsMessage
    {
        public ProfileListUpdate(
            IReadOnlyList<ProfileListItem> profiles,
            string? activeProfileId
        ) : base(
            type: "update",
            device: "profile",
            context: "profileList",
            payload: ToJsonElement(new
            {
                profiles,
                activeProfile = activeProfileId
            })
        )
        { }
    }
}