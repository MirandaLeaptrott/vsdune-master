using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsDune;

// Three in-fiction comms channels (player intercepts all). Observation
// Network public/neutral, Encrypted Signal is Hark (vague), Smuggler
// Comms only broadcasts when smugglers have boots on the ground.
public static class FactionChannels
{
    public const string ObservationPrefix = "[Observation Network]";
    public const string HarkonenPrefix = "[Encrypted Signal]";
    public const string SmugglerPrefix = "[Smuggler Comms]";
    public const string ScavengerPrefix = "[Scavenger Burst]";

    public static void Observation(ICoreServerAPI sapi, string message)
    {
        sapi.BroadcastMessageToAllGroups($"{ObservationPrefix} {message}", EnumChatType.Notification);
    }

    public static void Harkonen(ICoreServerAPI sapi, string message)
    {
        sapi.BroadcastMessageToAllGroups($"{HarkonenPrefix} {message}", EnumChatType.Notification);
    }

    public static void Smuggler(ICoreServerAPI sapi, string message)
    {
        sapi.BroadcastMessageToAllGroups($"{SmugglerPrefix} {message}", EnumChatType.Notification);
    }

    public static void Scavenger(ICoreServerAPI sapi, string message)
    {
        sapi.BroadcastMessageToAllGroups($"{ScavengerPrefix} {message}", EnumChatType.Notification);
    }

    // True if a vsdune entity with the given Code.Path prefix is alive
    // anywhere loaded. Used for "boots on ground" gates.
    public static bool AnyAlive(ICoreServerAPI sapi, string codePrefix)
    {
        foreach (var e in sapi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive) continue;
            if (e.Code?.Domain != "vsdune") continue;
            if (e.Code.Path == null) continue;
            if (e.Code.Path.StartsWith(codePrefix)) return true;
        }
        return false;
    }
}
