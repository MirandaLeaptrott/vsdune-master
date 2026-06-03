using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsDune;

public class ShadeIndicatorClient : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        try
        {
            var shade = new HudElementShadeIndicator(api);
            api.Gui.RegisterDialog(shade);
            var desert = new HudElementOpenDesert(api);
            api.Gui.RegisterDialog(desert);
            api.Logger.Notification("[VSDune] Shade and open-desert HUD elements registered.");
        }
        catch (System.Exception ex)
        {
            api.Logger.Error("[VSDune] Failed to create environmental HUD: " + ex);
        }
    }
}
