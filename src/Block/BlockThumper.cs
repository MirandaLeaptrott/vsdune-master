using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsDune;

public class BlockThumper : Block
{
    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

        var below = blockSel.Position.DownCopy();
        var b = world.BlockAccessor.GetBlock(below);
        if (b == null || b.BlockMaterial != EnumBlockMaterial.Sand)
        {
            failureCode = "vsdune:notbasinsand";
            return false;
        }
        return true;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.Side == EnumAppSide.Server)
        {
            var be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityThumper;
            be?.ActivateBy(byPlayer);
        }
        return true;
    }
}
