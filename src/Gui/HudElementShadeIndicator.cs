using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsDune;


public class HudElementShadeIndicator : HudElement
{
    private bool inShade;
    private bool lastInShade = false;
    private bool everSet = false;

    public override double InputOrder => 1.0;

    public HudElementShadeIndicator(ICoreClientAPI capi) : base(capi)
    {
        ComposeGuis();
        capi.Event.RegisterGameTickListener(OnTick, 250);
    }

    private void OnTick(float dt)
    {
        var player = capi.World.Player;
        if (player?.Entity == null) return;
        if (player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return;

        // Thopter cockpit counts as shade regardless of rainmap.
        bool inThopter = (player.Entity as EntityAgent)?.MountedOn?.MountSupplier?.OnEntity is EntityOrnithopter;

        var pos = player.Entity.Pos.AsBlockPos;
        int rainTop = capi.World.BlockAccessor.GetRainMapHeightAt(pos);
        bool nowInShade = inThopter || rainTop > pos.Y;

        if (!everSet || nowInShade != lastInShade)
        {
            inShade = nowInShade;
            lastInShade = nowInShade;
            everSet = true;
            ComposeGuis();
        }
    }

    private void ComposeGuis()
    {
        // Small badge at top-right, just below the hotbar widget area.
        var bounds = ElementBounds.Fixed(EnumDialogArea.RightTop, -10, 60, 90, 28);

        var textBounds = ElementBounds.Fixed(0, 0, 90, 28);

        string label = inShade ? "Shade" : "Sun";
        double[] textColor = inShade
            ? new double[] { 0.75, 0.85, 0.95, 1.0 }  // cool blue
            : new double[] { 1.0, 0.65, 0.30, 1.0 };  // warm orange

        var font = CairoFont.WhiteSmallText()
            .WithColor(textColor)
            .WithOrientation(EnumTextOrientation.Center);

        var composer = capi.Gui.CreateCompo("vsdune-shadeindicator", bounds);
        composer
            .BeginChildElements(bounds)
            .AddDynamicText(label, font, textBounds, "shadelabel")
            .EndChildElements()
            .Compose();

        Composers["vsdune-shadeindicator"] = composer;
        TryOpen();
    }

    public override void OnOwnPlayerDataReceived()
    {
        ComposeGuis();
        TryOpen();
    }

    // Always visible. Ambient awareness.
    public override bool TryClose() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
}
