using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsDune;


public class EntityBehaviorOutlawArrakis : EntityBehavior
{
    // Set on entity BEFORE SpawnEntity to bypass the basin filter.
    public const string AttrScriptedSpawn = "vsdune.scriptedSpawn";

    // FleeRange sits above the thopter's FleeOnWormRadius (60 in
    // GenOrnithopterRaid) so outlaws react first and get a head start
    // toward the thopter before it begins takeoff.
    private const float FleeRange = 80f;
    private const double FleePushSpeed = 0.06;
    private const double RetreatPushSpeed = 0.09;
    private const double ThopterRetreatRange = 80.0;
    private const double BoardingRange = 3.0;

    private static AssetLocation vertwormCode = new AssetLocation("vsdune", "vertworm");

    public EntityBehaviorOutlawArrakis(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
    }

    // >= BasinSandDepthThreshold sand under sealevel = former lake or
    // ocean. Mirrors EntityBehaviorVertwormAI.IsOnBasinSand and
    // GenSpiceBlow.IsDeepOceanColumn (their working basin detection).
    private const int BasinSandDepthThreshold = 4;
    private const int BasinSandDepthScan = 30;

    public override void OnEntitySpawn()
    {
        base.OnEntitySpawn();
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (entity.World == null) return;

        if (entity.WatchedAttributes.GetBool(AttrScriptedSpawn, false)) return;

        var bp = entity.Pos.AsBlockPos;
        if (ColumnIsBasinSand(bp.X, bp.Z))
        {
            entity.World.Logger.Notification(
                "[VSDune] Basin cull: {0} at ({1}, {2}, {3}).",
                entity.Code?.Path ?? "?", bp.X, bp.Y, bp.Z
            );
            entity.Die(EnumDespawnReason.Removed);
        }
    }

    private bool ColumnIsBasinSand(int x, int z)
    {
        int sealevel = entity.World.SeaLevel;

        // Above-sealevel terrain (dunes, rock ridges) is never a basin.
        // Check the surface height before scanning underground sand.
        var surfaceProbe = new BlockPos(x, 0, z);
        int rainTop = entity.World.BlockAccessor.GetRainMapHeightAt(surfaceProbe);
        if (rainTop > sealevel + 1) return false;

        var probe = new BlockPos(0);
        int depth = 0;
        int floor = System.Math.Max(0, sealevel - BasinSandDepthScan);
        for (int y = sealevel; y > floor; y--)
        {
            probe.Set(x, y, z);
            var b = entity.World.BlockAccessor.GetBlock(probe);
            if (b == null) break;
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= BasinSandDepthThreshold) return true;
        }
        return false;
    }

    // Throttle worm and thopter scans to once per WormScanIntervalMs.
    private const long WormScanIntervalMs = 500;
    private long lastWormScanMs = 0;
    private Entity cachedWorm = null;
    private Entity cachedThopter = null;

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.Alive) return;

        long now = entity.World.ElapsedMilliseconds;
        if (cachedWorm != null && !cachedWorm.Alive) cachedWorm = null;
        if (now - lastWormScanMs >= WormScanIntervalMs)
        {
            cachedWorm = FindNearbyVertworm();
            if (cachedWorm == null) cachedThopter = null;
            lastWormScanMs = now;
        }
        var worm = cachedWorm;
        if (worm == null) return;

        double dxc = entity.Pos.X - worm.Pos.X;
        double dzc = entity.Pos.Z - worm.Pos.Z;
        if (dxc * dxc + dzc * dzc > FleeRange * FleeRange)
        {
            cachedWorm = null;
            cachedThopter = null;
            return;
        }

        // Fremen don't fly. They flee the worm on foot via the
        // away-push fallback, not toward a thopter they can't board.
        var path = entity.Code?.Path;
        bool isFremen = path != null && path.StartsWith("fremen-");

        if (!isFremen)
        {
            // Boardable = ModeLanded or ModeTakeoff. The 7.33s takeoff
            // animation is our boarding grace window.
            string thopterMode = cachedThopter == null ? "" :
                cachedThopter.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, "");
            bool boardable = thopterMode == EntityBehaviorOrnithopterFlight.ModeLanded ||
                             thopterMode == EntityBehaviorOrnithopterFlight.ModeTakeoff;
            if (cachedThopter == null || !cachedThopter.Alive || !boardable)
            {
                cachedThopter = FindNearbyBoardableThopter();
            }
        }

        if (cachedThopter != null)
        {
            // Run TO the thopter. Despawning at boarding range stands
            // in for the full mount-the-seat sequence (future work).
            double tdx = cachedThopter.Pos.X - entity.Pos.X;
            double tdz = cachedThopter.Pos.Z - entity.Pos.Z;
            double tdist = Math.Sqrt(tdx * tdx + tdz * tdz);
            if (tdist <= BoardingRange)
            {
                entity.Die(EnumDespawnReason.Removed);
                return;
            }
            if (tdist > 0.01)
            {
                double inv = 1.0 / tdist;
                entity.Pos.Motion.X += tdx * inv * RetreatPushSpeed;
                entity.Pos.Motion.Z += tdz * inv * RetreatPushSpeed;
            }
            return;
        }

        // No thopter (or Fremen): push directly away from the worm.
        double dx = entity.Pos.X - worm.Pos.X;
        double dz = entity.Pos.Z - worm.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist < 0.01) return;

        double inv2 = 1.0 / dist;
        entity.Pos.Motion.X += dx * inv2 * FleePushSpeed;
        entity.Pos.Motion.Z += dz * inv2 * FleePushSpeed;
    }

    private Entity FindNearbyVertworm()
    {
        Entity found = null;
        double bestSq = FleeRange * FleeRange;
        entity.World.GetEntitiesAround(entity.Pos.XYZ, FleeRange, FleeRange,
            (e) =>
            {
                if (e == null || e == entity || !e.Alive) return false;
                if (!vertwormCode.Equals(e.Code)) return false;
                double dx = e.Pos.X - entity.Pos.X;
                double dz = e.Pos.Z - entity.Pos.Z;
                double distSq = dx * dx + dz * dz;
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    found = e;
                }
                return false;
            });
        return found;
    }

    private Entity FindNearbyBoardableThopter()
    {
        Entity bestThopter = null;
        double bestSq = ThopterRetreatRange * ThopterRetreatRange;
        entity.World.GetEntitiesAround(entity.Pos.XYZ, (float)ThopterRetreatRange, (float)ThopterRetreatRange,
            (e) =>
            {
                if (e == null || !e.Alive) return false;
                if (e is not EntityOrnithopter) return false;
                string mode = e.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, "");
                if (mode != EntityBehaviorOrnithopterFlight.ModeLanded &&
                    mode != EntityBehaviorOrnithopterFlight.ModeTakeoff) return false;
                double dx = e.Pos.X - entity.Pos.X;
                double dz = e.Pos.Z - entity.Pos.Z;
                double distSq = dx * dx + dz * dz;
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    bestThopter = e;
                }
                return false;
            });
        return bestThopter;
    }

    public override string PropertyName() => "vsdune.outlawarrakis";
}
