using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace VsDune;


public class VsDuneOrnithopterInput : ModSystem
{
    public const string ChannelName = "vsdune.ornithopter.input";
    public const string HotkeyCode = "vsduneEject";

    private ICoreClientAPI capi;

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<OrnithopterEjectPacket>()
            .SetMessageHandler<OrnithopterEjectPacket>(OnEjectReceived);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<OrnithopterEjectPacket>();

        api.Input.RegisterHotKey(
            HotkeyCode,
            "Ornithopter eject",
            GlKeys.Y,
            HotkeyType.CharacterControls
        );
        api.Input.SetHotKeyHandler(HotkeyCode, OnEjectHotkey);
    }

    private bool OnEjectHotkey(KeyCombination key)
    {
        if (capi == null) return false;
        var player = capi.World.Player;
        var mount = player?.Entity?.MountedOn;
        if (mount == null) return false;
        if (!mount.CanControl) return false; // pilot seat only

        var mountEntity = mount.Entity;
        if (mountEntity?.GetBehavior<EntityBehaviorOrnithopterFlight>() == null) return false;

        capi.Network.GetChannel(ChannelName).SendPacket(new OrnithopterEjectPacket());
        return true;
    }

    private void OnEjectReceived(IServerPlayer fromPlayer, OrnithopterEjectPacket packet)
    {
        var pilot = fromPlayer?.Entity;
        var mount = pilot?.MountedOn;
        if (mount == null) return;
        if (!mount.CanControl) return;

        var mountEntity = mount.Entity;
        if (mountEntity == null) return;
        if (mountEntity.GetBehavior<EntityBehaviorOrnithopterFlight>() == null) return;

      
        pilot.TryUnmount();
    }
}


[ProtoContract]
public class OrnithopterEjectPacket
{
}
