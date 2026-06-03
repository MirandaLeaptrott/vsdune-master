using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsDune;

public class SpiceSaturationClient : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        try
        {
            var hud = new HudElementSpiceSaturation(api);
            api.Gui.RegisterDialog(hud);
            api.Logger.Notification("[VSDune] Spice saturation HUD registered.");
        }
        catch (System.Exception ex)
        {
            api.Logger.Error("[VSDune] Failed to create spice saturation HUD: " + ex);
        }
    }
}
