using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsDune;

public class HudElementOpenDesert : HudElement
{
    private bool visible = false;

    public override double InputOrder => 1.0;

    public HudElementOpenDesert(ICoreClientAPI capi) : base(capi)
    {
        capi.Event.RegisterGameTickListener(OnTick, 500);
    }

    private void OnTick(float dt)
    {
        var player = capi.World.Player;
        if (player?.Entity == null) return;
        if (player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return;

        // Not applicable while airborne in a thopter.
        bool inThopter = (player.Entity as EntityAgent)?.MountedOn?.MountSupplier?.OnEntity is EntityOrnithopter;
        if (inThopter) { SetVisible(false); return; }

        var pos = player.Entity.Pos.AsBlockPos;
        int sealevel = capi.World.SeaLevel;
        int rainTop = capi.World.BlockAccessor.GetRainMapHeightAt(pos);
        var underBlock = capi.World.BlockAccessor.GetBlock(new BlockPos(pos.X, pos.Y - 1, pos.Z, pos.dimension));
        bool isSand = underBlock?.BlockMaterial == EnumBlockMaterial.Sand;

        // Worm territory: flat basin sand near sealevel (where GenSandBasinFill filled). Tolerance of +4 catches the flattened landforms that sit just above sealevel. Elevated dunes and rocky highlands don't trigger.
        bool nearBasinLevel = pos.Y <= sealevel + 4;
        bool noRoof = rainTop <= pos.Y;
        SetVisible(noRoof && isSand && nearBasinLevel);
    }

    private void SetVisible(bool show)
    {
        if (show == visible) return;
        visible = show;
        if (show) { ComposeGuis(); TryOpen(); }
        else TryClose();
    }

    private void ComposeGuis()
    {
        // Sits below the shade indicator badge (which is at Y=60, h=28).
        var bounds = ElementBounds.Fixed(EnumDialogArea.RightTop, -10, 92, 110, 40);
        var line1Bounds = ElementBounds.Fixed(0, 0, 110, 20);
        var line2Bounds = ElementBounds.Fixed(0, 20, 110, 20);

        var font1 = CairoFont.WhiteSmallText()
            .WithColor(new double[] { 1.0, 0.60, 0.10, 1.0 })
            .WithOrientation(EnumTextOrientation.Center);
        var font2 = CairoFont.WhiteSmallText()
            .WithColor(new double[] { 0.90, 0.20, 0.15, 1.0 })
            .WithOrientation(EnumTextOrientation.Center);

        var composer = capi.Gui.CreateCompo("vsdune-opendesert", bounds);
        composer
            .BeginChildElements(bounds)
            .AddDynamicText("Open Desert", font1, line1Bounds, "opendesert-line1")
            .AddDynamicText("Worm Warning", font2, line2Bounds, "opendesert-line2")
            .EndChildElements()
            .Compose();

        Composers["vsdune-opendesert"] = composer;
    }

    public override void OnOwnPlayerDataReceived() { }
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
}
