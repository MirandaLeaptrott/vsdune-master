using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class BlockEntityThopterWreck : BlockEntity
{
    private bool initialized;
    private bool smokes;
    private string spawnFaction = "none"; // "scavenger" | "fremen" | "hark" | "none"
    private bool spawned;

    private const float SmokeChance = 0.35f;
    private const float ScavengerChance = 0.25f;
    private const float FremenChance = 0.20f;
    private const float HarkChance = 0.10f;
    private const float PlayerApproachRange = 30f;
    private const int SpawnCount = 3;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        if (!initialized)
        {
            var rnd = api.World.Rand;
            smokes = rnd.NextDouble() < SmokeChance;
            double r = rnd.NextDouble();
            if (r < ScavengerChance) spawnFaction = "scavenger";
            else if (r < ScavengerChance + FremenChance) spawnFaction = "fremen";
            else if (r < ScavengerChance + FremenChance + HarkChance) spawnFaction = "hark";
            else spawnFaction = "none";
            initialized = true;
            MarkDirty();
        }

        if (api.Side == EnumAppSide.Server)
        {
            RegisterGameTickListener(OnServerTick, 4000);
        }
        if (api.Side == EnumAppSide.Client && smokes)
        {
            RegisterGameTickListener(OnClientSmokeTick, 1000);
        }
    }

    private void OnServerTick(float dt)
    {
        if (spawned || spawnFaction == "none") return;
        var sapi = Api as ICoreServerAPI;
        if (sapi == null) return;

        var center = new Vec3d(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5);
        IServerPlayer nearest = null;
        double bestSq = PlayerApproachRange * PlayerApproachRange;
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p?.Entity == null) continue;
            double dx = p.Entity.Pos.X - center.X;
            double dz = p.Entity.Pos.Z - center.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq < bestSq) { bestSq = distSq; nearest = p as IServerPlayer; }
        }
        if (nearest == null) return;

        spawned = true;
        MarkDirty();
        if (spawnFaction == "hark")
        {
            // Hark variant flies in to investigate by air rather than
            // ground-spawning. The air patrol system handles approach,
            // landing, and the disembark squad.
            sapi.ModLoader.GetModSystem<GenHarkonenAirPatrol>()?.DispatchInvestigation(center);
        }
        else
        {
            TriggerFactionSpawn(sapi, center);
        }
    }

    private void OnClientSmokeTick(float dt)
    {
        if (!smokes || Api == null) return;
        var p = new SimpleParticleProperties(
            2, 5,
            ColorUtil.ToRgba(180, 90, 90, 90),
            new Vec3d(Pos.X + 0.3, Pos.Y + 1.4, Pos.Z + 0.3),
            new Vec3d(Pos.X + 0.7, Pos.Y + 1.8, Pos.Z + 0.7),
            new Vec3f(-0.05f, 0.25f, -0.05f),
            new Vec3f(0.05f, 0.6f, 0.05f),
            3.5f,
            -0.04f,
            0.5f, 1.1f,
            EnumParticleModel.Quad
        );
        p.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -28);
        p.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0.5f);
        Api.World.SpawnParticles(p);
    }

    private void TriggerFactionSpawn(ICoreServerAPI sapi, Vec3d center)
    {
        string[] codes;
        switch (spawnFaction)
        {
            case "scavenger":
                codes = new[] { "scavenger-thug", "scavenger-spearman", "scavenger-sniper" };
                break;
            case "fremen":
                codes = new[] { "fremen-warrior-axe", "fremen-warrior-knife", "fremen-warrior-spear", "fremen-archer" };
                break;
            // Hark variant is handled by GenHarkonenAirPatrol.DispatchInvestigation
            // in OnServerTick before reaching here.
            default:
                return;
        }

        var rnd = sapi.World.Rand;
        long herdId = sapi.WorldManager.GetNextUniqueId();
        int count = 0;
        for (int i = 0; i < SpawnCount; i++)
        {
            string code = codes[rnd.Next(codes.Length)];
            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null) continue;
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = 2 + rnd.NextDouble() * 5;
            double ux = center.X + Math.Cos(angle) * dist;
            double uz = center.Z + Math.Sin(angle) * dist;
            int uy = sapi.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)ux, 0, (int)uz));

            unit.Pos.SetPos(ux, uy + 1, uz);
            unit.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);
            if (unit is EntityAgent ea) ea.HerdId = herdId;

            sapi.World.SpawnEntity(unit);
            count++;
        }

        sapi.Logger.Notification("[VSDune] Wreck at ({0}, {1}, {2}) spawned {3} {4} (herd {5}).",
            Pos.X, Pos.Y, Pos.Z, count, spawnFaction, herdId);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        initialized = tree.GetBool("initialized", false);
        smokes = tree.GetBool("smokes", false);
        spawnFaction = tree.GetString("spawnFaction", "none");
        spawned = tree.GetBool("spawned", false);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetBool("initialized", initialized);
        tree.SetBool("smokes", smokes);
        tree.SetString("spawnFaction", spawnFaction);
        tree.SetBool("spawned", spawned);
    }
}
