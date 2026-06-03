using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsDune;

public class HudElementSpiceSaturation : HudElement
{
    private GuiElementStatbar statbar;
    private float lastValue = -1f;

    public override double InputOrder => 1.0;

    public HudElementSpiceSaturation(ICoreClientAPI capi) : base(capi)
    {
        ComposeGuis();
        capi.Event.RegisterGameTickListener(OnTick, 250);
    }

    private void OnTick(float dt)
    {
        if (capi.World.Player?.Entity == null) return;
        if (capi.World.Player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return;

        // Saturation is stored as Double server side (so the EdenInstinct
        // harmony patch can keep using GetDouble). Cast to float for the
        // statbar API.
        float v = (float)capi.World.Player.Entity.WatchedAttributes.GetDouble(SpiceSaturationSystem.AttrPath, 0.0);
        if (v != lastValue)
        {
            statbar?.SetValues(v, 0f, 1f);
            lastValue = v;
        }
    }

    private void ComposeGuis()
    {
        const float statsBarParentWidth = 850f;
        const float statsBarWidth = statsBarParentWidth * 0.41f;
        double[] spiceColor = { 0.95, 0.55, 0.15, 0.75 };

        var statsBarBounds = new ElementBounds()
        {
            Alignment = EnumDialogArea.CenterBottom,
            BothSizing = ElementSizing.Fixed,
            fixedWidth = statsBarParentWidth,
            fixedHeight = 100
        }.WithFixedAlignmentOffset(0.0, 5.0);

        bool isRight = true;
        double alignmentOffsetX = isRight ? -2.0 : 1.0;

        var spiceBarBounds = ElementStdBounds
            .Statbar(isRight ? EnumDialogArea.RightTop : EnumDialogArea.LeftTop, statsBarWidth)
            .WithFixedAlignmentOffset(alignmentOffsetX, -48)
            .WithFixedHeight(10);

        var parent = statsBarBounds.FlatCopy().FixedGrow(0.0, 20.0);
        var composer = capi.Gui.CreateCompo("spicesaturationbar", parent);

        statbar = new GuiElementStatbar(composer.Api, spiceBarBounds, spiceColor, isRight, false);

        composer
            .BeginChildElements(statsBarBounds)
            .AddInteractiveElement(statbar, "spicesaturationstatsbar")
            .EndChildElements()
            .Compose();

        Composers["spicesaturationbar"] = composer;

        // TryOpen here (end of ComposeGuis) matches HoD's pattern.
        TryOpen();
    }

    public override void OnOwnPlayerDataReceived()
    {
        ComposeGuis();
        TryOpen();
    }

    // Unlike the stillsuit bar, this one is always visible. The player
    // always has a saturation value, so hiding it would be confusing.
    public override bool TryClose() => false;

    public override bool ShouldReceiveKeyboardEvents() => false;

    public override bool Focusable => false;
}
