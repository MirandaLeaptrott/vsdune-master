using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsDune;

public class BlockEntityThumper : BlockEntity
{
    private bool active;
    private long activatedAtMs;
    private long lastThumpMs;
    private bool wormSpawned;

    private const long WormSummonDelayMs = 105_000;
    private const long ThumpIntervalMs = 1000;
    private const float ThumpSoundRange = 32f;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
        {
            RegisterGameTickListener(OnServerTick, 250);
        }
        else
        {
            RegisterGameTickListener(OnClientTick, 250);
        }
    }

    public void ActivateBy(IPlayer player)
    {
        if (active) return;
        active = true;
        activatedAtMs = Api.World.ElapsedMilliseconds;
        lastThumpMs = 0;
        wormSpawned = false;
        MarkDirty(true);
        Api.World.PlaySoundAt(
            new AssetLocation("game:sounds/toggleswitch"),
            Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5,
            null, true, 16f, 1.0f
        );
    }

    // Client drives the looping "active" animation 
    private void OnClientTick(float dt)
    {
        if (!active) return;
        var animUtil = GetBehavior<BEBehaviorAnimatable>()?.animUtil;
        if (animUtil?.animator == null) return; // animator builds in OnTesselation
        if (!animUtil.activeAnimationsByAnimCode.ContainsKey("active"))
        {
            // 20 frames at 30fps = 0.667s at speed 1. Scale to match ThumpIntervalMs (1s).
            animUtil.StartAnimation(new AnimationMetaData { Code = "active", Animation = "active", AnimationSpeed = 0.667f });
        }
    }

    // Without InitializeAnimator, animUtil.animator stays null and
    // StartAnimation silently no-ops. Init lazily on first render.
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        var animUtil = GetBehavior<BEBehaviorAnimatable>()?.animUtil;
        if (animUtil != null && animUtil.animator == null)
        {
            animUtil.InitializeAnimator("vsdune-thumper", null, null, new Vec3f(0, 0, 0));
        }
        return base.OnTesselation(mesher, tessThreadTesselator);
    }

    private void OnServerTick(float dt)
    {
        if (!active) return;

        long now = Api.World.ElapsedMilliseconds;
        long elapsed = now - activatedAtMs;
        if (elapsed < 0)
        {
            activatedAtMs = now;
            elapsed = 0;
        }

        if (now - lastThumpMs >= ThumpIntervalMs)
        {
            lastThumpMs = now;
            Api.World.PlaySoundAt(
                new AssetLocation("game:sounds/block/heavymetal-hit"),
                Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5,
                null, true, ThumpSoundRange, 0.95f
            );
        }

        if (!wormSpawned && elapsed >= WormSummonDelayMs)
        {
            wormSpawned = true;
            var scare = (Api as ICoreServerAPI)?.ModLoader.GetModSystem<GenVertwormScare>();
            scare?.SpawnWormAt(new Vec3d(Pos.X + 0.5, Pos.Y, Pos.Z + 0.5));
            Api.World.BlockAccessor.SetBlock(0, Pos);
        }
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        active = tree.GetBool("active");
        activatedAtMs = tree.GetLong("activatedAtMs");
        wormSpawned = tree.GetBool("wormSpawned");
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBool("active", active);
        tree.SetLong("activatedAtMs", activatedAtMs);
        tree.SetBool("wormSpawned", wormSpawned);
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        if (!active)
        {
            dsc.AppendLine("Inactive. Right-click to start.");
            return;
        }
        long remaining = WormSummonDelayMs - (Api.World.ElapsedMilliseconds - activatedAtMs);
        if (remaining > 0) dsc.AppendLine($"Pulsing. Wormsign in {remaining / 1000.0:F0}s.");
        else dsc.AppendLine("Wormsign imminent.");
    }
}
