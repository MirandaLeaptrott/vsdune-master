using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VsDune;

public class ItemSpice : Item
{
    private const double SaturationPerEat = 0.20;

    protected override void tryEatStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        base.tryEatStop(secondsUsed, slot, byEntity);

        if (byEntity.World.Side != EnumAppSide.Server) return;
        if (secondsUsed < 0.95f) return;
        if (byEntity is not EntityPlayer) return;

        SpiceSaturationSystem.Add(byEntity, SaturationPerEat);
    }
}
