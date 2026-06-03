using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VsDune;


public class BlockDewdrop : Block
{
    private static MethodInfo modifyThirstMethod;
    private static bool reflectionResolved;

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.Side == EnumAppSide.Server)
        {
            ApplyHydration(byPlayer.Entity);

            world.PlaySoundAt(
                new AssetLocation("game:sounds/player/drink"),
                blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5,
                byPlayer, true, 24f, 0.9f
            );

            world.BlockAccessor.SetBlock(0, blockSel.Position);
            world.BlockAccessor.TriggerNeighbourBlockUpdate(blockSel.Position);
        }
        return true;
    }

    private void ApplyHydration(EntityAgent entity)
    {
        if (entity == null) return;

        float amount = Attributes?["hydration"]?.AsFloat(30f) ?? 30f;
        var thirstBehavior = entity.GetBehavior("thirst");
        if (thirstBehavior == null) return;

        if (!reflectionResolved)
        {
            modifyThirstMethod = thirstBehavior.GetType().GetMethod(
                "ModifyThirst",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(float), typeof(float) },
                null
            );
            reflectionResolved = true;
        }

        modifyThirstMethod?.Invoke(thirstBehavior, new object[] { amount, 0f });
    }
}
