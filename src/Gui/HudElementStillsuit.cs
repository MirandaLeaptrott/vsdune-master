using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsDune;

public class HudElementStillsuit : HudElement
{
    private GuiElementStatbar statbar;
    private float lastWater = -1f;
    private float lastMax = -1f;

    // ID set covers all stillsuit-pants variants. Per-variant cap lives
    // in the item JSON's stillsuitWaterMax attribute.
    private readonly HashSet<int> stillsuitPantsIds = new HashSet<int>();
    private bool variantsResolved = false;

    public override double InputOrder => 1.0;

    public HudElementStillsuit(ICoreClientAPI capi) : base(capi)
    {
        ComposeGuis();
        capi.Event.RegisterGameTickListener(OnTick, 250);
    }

    private void OnTick(float dt)
    {
        if (capi.World.Player?.Entity == null) return;
        if (capi.World.Player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return;

        if (!variantsResolved)
        {
            foreach (var code in new[] { "stillsuit-pants", "stillsuit-pants-ragged", "stillsuit-pants-enhanced" })
            {
                var it = capi.World.GetItem(new AssetLocation("vsdune", code));
                if (it != null) stillsuitPantsIds.Add(it.Id);
            }
            variantsResolved = true;
            if (stillsuitPantsIds.Count == 0) return;
        }
        if (stillsuitPantsIds.Count == 0) return;

        var charInv = capi.World.Player.InventoryManager?.GetOwnInventory("character");
        if (charInv == null) return;

        ItemSlot pantsSlot = null;
        foreach (var slot in charInv)
        {
            if (slot == null || slot.Empty) continue;
            int id = slot.Itemstack.Item?.Id ?? -1;
            if (id < 0) continue;
            if (stillsuitPantsIds.Contains(id))
            {
                pantsSlot = slot;
                break;
            }
        }
        if (pantsSlot == null)
        {
            if (IsOpened()) base.TryClose();
            lastWater = -1f;
            return;
        }

        float water = pantsSlot.Itemstack.Attributes.GetFloat("stillsuitWater", 0f);
        float max = pantsSlot.Itemstack.Collectible?.Attributes?["stillsuitWaterMax"].AsFloat(100f) ?? 100f;

        if (!IsOpened()) TryOpen();

        if (water != lastWater || max != lastMax)
        {
            statbar?.SetValues(water, 0f, max);
            lastWater = water;
            lastMax = max;
        }
    }

    private void ComposeGuis()
    {
        const float statsBarParentWidth = 850f;
        const float statsBarWidth = statsBarParentWidth * 0.30f;

        // Pale moisture-blue, slightly different from HoD's deeper teal
        // so the two bars read as separate channels at a glance.
        double[] waterColor = { 0.45, 0.75, 0.95, 0.7 };

        var statsBarBounds = new ElementBounds()
        {
            Alignment = EnumDialogArea.CenterBottom,
            BothSizing = ElementSizing.Fixed,
            fixedWidth = statsBarParentWidth,
            fixedHeight = 100
        }.WithFixedAlignmentOffset(0.0, 5.0);

        var isRight = true;
        var alignmentOffsetX = isRight ? -2.0 : 1.0;

        var stillsuitBarBounds = ElementStdBounds
            .Statbar(isRight ? EnumDialogArea.RightTop : EnumDialogArea.LeftTop, statsBarWidth)
            .WithFixedAlignmentOffset(alignmentOffsetX, -32)
            .WithFixedHeight(8);

        var parent = statsBarBounds.FlatCopy().FixedGrow(0.0, 20.0);
        var composer = capi.Gui.CreateCompo("stillsuitbar", parent);

        statbar = new GuiElementStatbar(composer.Api, stillsuitBarBounds, waterColor, isRight, false);

        composer
            .BeginChildElements(statsBarBounds)
            .AddInteractiveElement(statbar, "stillsuitstatsbar")
            .EndChildElements()
            .Compose();

        Composers["stillsuitbar"] = composer;
    }

    public override void OnOwnPlayerDataReceived()
    {
        ComposeGuis();
    }

    public override bool TryClose() => base.TryClose();

    public override bool ShouldReceiveKeyboardEvents() => false;

    public override bool Focusable => false;
}
