using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VsDune;


public class BlockEntitySpicesandDrying : BlockEntity
{
    private double dryingCompletesAtTotalHours;
    private bool initialized;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        if (!initialized)
        {
            float dryingHours = Block?.Attributes?["dryingHours"]?.AsFloat(6f) ?? 6f;
            dryingCompletesAtTotalHours = api.World.Calendar.TotalHours + dryingHours;
            initialized = true;
            MarkDirty();
        }

        // 10s tick, plenty often for a multi-hour drying window.
        RegisterGameTickListener(OnTick, 10000);
    }

    private void OnTick(float dt)
    {
        if (Api.Side != EnumAppSide.Server) return;
        if (Api.World.Calendar.TotalHours < dryingCompletesAtTotalHours) return;

        var dryCode = Block.CodeWithVariant("wetness", "dry");
        var dryBlock = Api.World.GetBlock(dryCode);
        if (dryBlock == null) return;

        // SetBlock destroys this block entity; no further ticks fire.
        Api.World.BlockAccessor.SetBlock(dryBlock.BlockId, Pos);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        dryingCompletesAtTotalHours = tree.GetDouble("dryingCompletesAtTotalHours");
        initialized = tree.GetInt("initialized") > 0;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetDouble("dryingCompletesAtTotalHours", dryingCompletesAtTotalHours);
        tree.SetInt("initialized", initialized ? 1 : 0);
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        if (Api == null || !initialized) return;

        double remaining = dryingCompletesAtTotalHours - Api.World.Calendar.TotalHours;
        if (remaining > 0)
        {
            dsc.AppendLine($"Drying in: {remaining:F1} game hours");
        }
        else
        {
            dsc.AppendLine("Ready to dry on next tick.");
        }
    }
}
