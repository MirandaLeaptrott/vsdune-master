using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsDune;


public class StillsuitClientSystem : ModSystem
{
    private ICoreClientAPI capi;
    private IClientNetworkChannel sipChannel;
    private HudElementStillsuit hud;

    public const string SipHotkeyCode = "vsdune.stillsuitsip";

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        sipChannel = api.Network.RegisterChannel(StillsuitSystem.SipPacketChannel)
            .RegisterMessageType<SipRequestPacket>();

        // Default Shift+R. G was the obvious choice but vanilla binds
        // it to sit; players would couch when they meant to sip.
        api.Input.RegisterHotKey(SipHotkeyCode, "Sip stillsuit reclaim", GlKeys.R, HotkeyType.CharacterControls, shiftPressed: true);
        api.Input.SetHotKeyHandler(SipHotkeyCode, OnSipHotkey);

        hud = new HudElementStillsuit(api);
        api.Gui.RegisterDialog(hud);
    }

    private bool OnSipHotkey(KeyCombination comb)
    {
        if (capi.World.Player?.Entity == null) return false;
        sipChannel.SendPacket(new SipRequestPacket());
        return true;
    }
}
