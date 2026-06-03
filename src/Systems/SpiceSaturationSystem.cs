using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class SpiceSaturationSystem : ModSystem
{
    private ICoreServerAPI sapi;

    public const string AttrPath = "spiceSaturation";

   
    private const float TickIntervalSeconds = 5f;

    // Per-tick deltas: ~16 real-time minutes basin exposure to fill,
    // ~2.75 hours off-basin to fully drain. Saturation is a state that
    // persists once acquired.
    private const float AccumulatePerTick = 0.005f;
    private const float DecayPerTick = 0.0005f;

    // Softer sand-depth threshold than the spice-blow ocean check, so
    // saturation also ramps near deeplakes / largelake.
    private const int NearBasinThreshold = 8;
    private const int NearBasinScanDepth = 50;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.RegisterGameTickListener(OnTick, (int)(TickIntervalSeconds * 1000));


        api.Event.PlayerJoin += (player) =>
        {
            if (player.Entity == null) return;
            if (!player.Entity.WatchedAttributes.HasAttribute(AttrPath))
            {
                SetSaturation(player.Entity, 0.0);
            }
        };

        api.ChatCommands.Create("spice")
            .WithDescription("Inspect or set spice saturation. Subcommands: get, set <0..1>, clear.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("get").HandleWith(OnSpiceGet).EndSubCommand()
            .BeginSubCommand("set").WithArgs(api.ChatCommands.Parsers.Float("value")).HandleWith(OnSpiceSet).EndSubCommand()
            .BeginSubCommand("clear").HandleWith(OnSpiceClear).EndSubCommand();
    }

    private TextCommandResult OnSpiceGet(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        return TextCommandResult.Success($"Spice saturation: {GetSaturation(sp.Entity):F3}");
    }

    private TextCommandResult OnSpiceSet(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        double v = (float)args.Parsers[0].GetValue();
        SetSaturation(sp.Entity, GameMath.Clamp(v, 0.0, 1.0));
        return TextCommandResult.Success($"Spice saturation set to {GetSaturation(sp.Entity):F3}");
    }

    private TextCommandResult OnSpiceClear(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        SetSaturation(sp.Entity, 0.0);
        return TextCommandResult.Success("Spice saturation cleared.");
    }


    public static double GetSaturation(EntityAgent entity)
    {
        if (entity?.WatchedAttributes == null) return 0.0;
        return entity.WatchedAttributes.GetDouble(AttrPath, 0.0);
    }

    public static void SetSaturation(EntityAgent entity, double value)
    {
        if (entity?.WatchedAttributes == null) return;
        entity.WatchedAttributes.SetDouble(AttrPath, GameMath.Clamp(value, 0.0, 1.0));
        entity.WatchedAttributes.MarkPathDirty(AttrPath);
    }

    public static void Add(EntityAgent entity, double delta)
    {
        SetSaturation(entity, GetSaturation(entity) + delta);
    }

    private void OnTick(float dt)
    {
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;
            if (sp.WorldData?.CurrentGameMode != EnumGameMode.Survival) continue;

            bool shouldAccumulate = IsExposedAndNearBasin(sp);
            double current = GetSaturation(sp.Entity);
            double next = shouldAccumulate
                ? current + AccumulatePerTick
                : current - DecayPerTick;
            SetSaturation(sp.Entity, next);
        }
    }

    private bool IsExposedAndNearBasin(IServerPlayer player)
    {
        var pos = player.Entity.Pos.AsBlockPos;
        int rainTop = sapi.World.BlockAccessor.GetRainMapHeightAt(pos);
        if (rainTop > pos.Y) return false;

        int sealevel = sapi.World.SeaLevel;
        int floor = System.Math.Max(0, sealevel - NearBasinScanDepth);
        var probe = new BlockPos(Dimensions.NormalWorld);
        int depth = 0;
        for (int y = sealevel; y > floor; y--)
        {
            probe.Set(pos.X, y, pos.Z);
            var b = sapi.World.BlockAccessor.GetBlock(probe);
            if (b == null) break;
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= NearBasinThreshold) return true;
        }
        return false;
    }
}
