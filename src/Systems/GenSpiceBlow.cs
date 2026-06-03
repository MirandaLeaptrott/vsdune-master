using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;


public class GenSpiceBlow : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;
    private int spicesandWetBlockId;

    private readonly List<PendingBlow> pendingBlows = new();

    // Test-phase tuning: frequent polls + high chance so blows happen
    // reliably during a normal play session.
    private const float PollIntervalSeconds = 60f;
    private const float SpawnChancePerPoll = 0.20f;
    private const int MinDistanceFromPlayer = 60;
    private const int MaxDistanceFromPlayer = 400;
    private const float MinFuseSeconds = 90f;
    private const float MaxFuseSeconds = 300f;
    private const int ConversionRadius = 12;
    private const float BlastRadius = 12f;
    private const float InjureRadius = 20f;
    private const float CraterDepthFactor = 0.35f;
    private const float MinMagnitude = 0.55f;
    private const float MaxMagnitude = 1.6f;

    // Locality / pacing limits
    private const int MinSeparationBlocks = 250;
    private const float AreaCooldownSeconds = 1800f;

    // Ocean-vs-lake by sand-column depth
    private const int OceanDepthThreshold = 15;
    // Cap how far we walk to keep the per-candidate cost bounded.
    private const int OceanDepthMaxScan = 50;

    private const int SubSeed = 5511;

    private class PendingBlow
    {
        public BlockPos Center;
        public Vec3d ParticleAnchor;
        public float SecondsRemaining;
        public float Magnitude;
    }

    private class RecentBlow
    {
        public BlockPos Center;
        public double SeededAtTotalSeconds;
    }

    private readonly List<RecentBlow> recentBlows = new();

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(PollForNewBlows, (int)(PollIntervalSeconds * 1000));
        api.Event.RegisterGameTickListener(TickPendingBlows, 500);

        // /spiceblow [fuseSeconds]
        api.ChatCommands.Create("spiceblow")
            .WithDescription("Seed an immediate spice blow at your surface position. Optional argument is fuse in seconds (default 10).")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalInt("fuseSeconds", 10))
            .HandleWith(OnSpiceBlowCommand);
    }

    private TextCommandResult OnSpiceBlowCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player?.Entity == null)
        {
            return TextCommandResult.Error("Need a player caller to seed a spice blow.");
        }

        int fuse = (int)args.Parsers[0].GetValue();
        if (fuse < 1) fuse = 1;

        int worldX = (int)player.Entity.Pos.X;
        int worldZ = (int)player.Entity.Pos.Z;
        int? maybeY = sapi.WorldManager.GetSurfacePosY(worldX, worldZ);
        if (maybeY == null)
        {
            return TextCommandResult.Error("Could not resolve surface Y at your position.");
        }

        var center = new BlockPos(worldX, maybeY.Value, worldZ, 0);
        var anchor = new Vec3d(worldX + 0.5, maybeY.Value + 1.2, worldZ + 0.5);
        float magnitude = MinMagnitude + (float)rnd.NextDouble() * (MaxMagnitude - MinMagnitude);

        pendingBlows.Add(new PendingBlow
        {
            Center = center,
            ParticleAnchor = anchor,
            SecondsRemaining = fuse,
            Magnitude = magnitude,
        });
       
        recentBlows.Add(new RecentBlow
        {
            Center = center,
            SeededAtTotalSeconds = sapi.World.ElapsedMilliseconds / 1000.0,
        });

        sapi.Logger.Notification("[VSDune] /spiceblow seeded at ({0}, {1}, {2}) for {3}, fuse {4}s, magnitude {5:F2}", worldX, maybeY.Value, worldZ, player.PlayerName, fuse, magnitude);
        return TextCommandResult.Success($"Spice blow seeded at ({worldX}, {maybeY.Value}, {worldZ}), fuse {fuse}s, magnitude {magnitude:F2}");
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        if (api.Side != EnumAppSide.Server) return;

        var spicesandWet = api.World.GetBlock(new AssetLocation("vsdune", "spicesand-wet"));
        spicesandWetBlockId = spicesandWet?.BlockId ?? 0;
        if (spicesandWetBlockId == 0)
        {
            api.Logger.Warning("[VSDune] GenSpiceBlow: vsdune:spicesand-wet not resolved. Detonations will still play effects but won't convert blocks.");
        }
    }

    private void PollForNewBlows(float dt)
    {
        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;

        // Drop expired recent-blow entries before doing the cooldown
        double nowSeconds = sapi.World.ElapsedMilliseconds / 1000.0;
        for (int i = recentBlows.Count - 1; i >= 0; i--)
        {
            if (nowSeconds - recentBlows[i].SeededAtTotalSeconds > AreaCooldownSeconds)
            {
                recentBlows.RemoveAt(i);
            }
        }

        // One-at-a-time gate: blow must detonate before another rolls.
        if (pendingBlows.Count > 0) return;

        if (rnd.NextFloat() > SpawnChancePerPoll) return;

        var player = players[rnd.NextInt(players.Length)];
        if (player.Entity == null) return;

        int sealevel = sapi.World.SeaLevel;

        // Try multiple positions before giving up. That is what she said.
        const int TriesPerPoll = 25;
        for (int attempt = 0; attempt < TriesPerPoll; attempt++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double distance = MinDistanceFromPlayer + rnd.NextDouble() * (MaxDistanceFromPlayer - MinDistanceFromPlayer);
            int worldX = (int)(player.Entity.Pos.X + Math.Cos(angle) * distance);
            int worldZ = (int)(player.Entity.Pos.Z + Math.Sin(angle) * distance);

            int? maybeY = sapi.WorldManager.GetSurfacePosY(worldX, worldZ);
            if (maybeY == null) continue;
            int worldY = maybeY.Value;
            if (worldY <= 1 || worldY >= sapi.WorldManager.MapSizeY - 5) continue;

            // Basin-only: surface must be at sealevel
            if (worldY < sealevel - 1 || worldY > sealevel + 1) continue;

            // Ocean-only (no lakes)
            if (!IsDeepOceanColumn(worldX, sealevel, worldZ)) continue;

            // Area cooldown
            bool tooClose = false;
            for (int j = 0; j < recentBlows.Count; j++)
            {
                int rdx = recentBlows[j].Center.X - worldX;
                int rdz = recentBlows[j].Center.Z - worldZ;
                if (rdx * rdx + rdz * rdz < MinSeparationBlocks * MinSeparationBlocks)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose) continue;

            var center = new BlockPos(worldX, worldY, worldZ, 0);
            var anchor = new Vec3d(worldX + 0.5, worldY + 1.2, worldZ + 0.5);

            float fuse = MinFuseSeconds + (float)rnd.NextDouble() * (MaxFuseSeconds - MinFuseSeconds);
            float magnitude = MinMagnitude + (float)rnd.NextDouble() * (MaxMagnitude - MinMagnitude);
            pendingBlows.Add(new PendingBlow
            {
                Center = center,
                ParticleAnchor = anchor,
                SecondsRemaining = fuse,
                Magnitude = magnitude,
            });
            recentBlows.Add(new RecentBlow
            {
                Center = center,
                SeededAtTotalSeconds = nowSeconds,
            });

            sapi.Logger.Notification("[VSDune] Spice blow seeded at ({0}, {1}, {2}), fuse {3:F0}s, magnitude {4:F2}", worldX, worldY, worldZ, fuse, magnitude);
            return;
        }

        // No basin found within range this poll; try again next poll.
    }

    private bool IsDeepOceanColumn(int worldX, int sealevel, int worldZ)
    {
        // Walk down from sealevel
        var pos = new BlockPos(Dimensions.NormalWorld);
        int floor = System.Math.Max(0, sealevel - OceanDepthMaxScan);
        int depth = 0;
        for (int y = sealevel; y > floor; y--)
        {
            pos.Set(worldX, y, worldZ);
            var b = sapi.World.BlockAccessor.GetBlock(pos);
            if (b == null) break;
            // Sand-material covers both sand versions
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= OceanDepthThreshold) return true;
        }
        return false;
    }

    private void TickPendingBlows(float dt)
    {
        for (int i = pendingBlows.Count - 1; i >= 0; i--)
        {
            var blow = pendingBlows[i];
            blow.SecondsRemaining -= dt;

            EmitSmokeParticles(blow.ParticleAnchor);

            if (blow.SecondsRemaining <= 0)
            {
                Detonate(blow);
                pendingBlows.RemoveAt(i);
            }
        }
    }

    private void EmitSmokeParticles(Vec3d anchor)
    {
        // Plume of purple smoke during the fuse
        var p = new SimpleParticleProperties(
            40, 70,
            ColorUtil.ToRgba(230, 130, 50, 180),
            new Vec3d(anchor.X - 1.5, anchor.Y, anchor.Z - 1.5),
            new Vec3d(anchor.X + 1.5, anchor.Y + 0.5, anchor.Z + 1.5),
            new Vec3f(-0.1f, 0.6f, -0.1f),
            new Vec3f(0.1f, 1.6f, 0.1f),
            10f,
            -0.04f,
            0.6f, 1.4f,
            EnumParticleModel.Quad
        );
        p.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -25);
        p.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0.5f);
        sapi.World.SpawnParticles(p);
    }

    // Fires after every successful detonation
    public static event System.Action<Vec3d, float> OnDetonate;

    private void Detonate(PendingBlow blow) => DetonateInternal(blow, fireEvent: true);

    // Trigger an immediate blow at a fixed surface position
    public void TriggerBlowAt(BlockPos surfacePos, float magnitude, bool fireEvent)
    {
        if (surfacePos == null) return;
        DetonateInternal(new PendingBlow
        {
            Center = surfacePos.Copy(),
            ParticleAnchor = new Vec3d(surfacePos.X + 0.5, surfacePos.Y + 1, surfacePos.Z + 0.5),
            SecondsRemaining = 0f,
            Magnitude = magnitude
        }, fireEvent);
    }

    private void DetonateInternal(PendingBlow blow, bool fireEvent)
    {
        sapi.Logger.Notification("[VSDune] Spice blow detonating at ({0}, {1}, {2}), magnitude {3:F2}", blow.Center.X, blow.Center.Y, blow.Center.Z, blow.Magnitude);

        EmitMushroomCloud(blow.ParticleAnchor, blow.Magnitude);

        // Loud rumble for landmark distance. Sound volume scales mildly
        // with magnitude so a small puff doesn't sound identical to a
        // landmark blow.
        sapi.World.PlaySoundAt(
            new AssetLocation("game:sounds/effect/largeexplosion"),
            blow.Center.X + 0.5, blow.Center.Y + 1, blow.Center.Z + 0.5,
            null, false, 256f, 0.7f + 0.4f * blow.Magnitude
        );

        // Vanilla explosion handles entity knockback / damage in
        // injureRadius. blastRadius stays at 1 so vanilla doesn't carve
        // a noisy sphere into the terrain that would fight our parabolic
        // bowl carve below. injureRadius scales with magnitude so bigger
        // blows are also more dangerous to be near.
        sapi.World.CreateExplosion(blow.Center, EnumBlastType.OreBlast, 1, InjureRadius * blow.Magnitude, 0f, null);

        CarveSpiceCrater(blow.Center, blow.Magnitude);

        if (!fireEvent) return;
        // Fired last so subscribers see the final world state (sand
        // converted, crater carved).
        try { OnDetonate?.Invoke(new Vec3d(blow.Center.X + 0.5, blow.Center.Y + 0.5, blow.Center.Z + 0.5), blow.Magnitude); }
        catch (System.Exception ex)
        {
            sapi.Logger.Error("[VSDune] OnDetonate subscriber threw: " + ex);
        }
    }

    private void EmitMushroomCloud(Vec3d anchor, float magnitude)
    {
        // Three-layer detonation visual (vanilla CreateExplosion VFX is
        // suppressed at blastRadius 1 so the crater stays clean):
        // 1. Ground shockwave: tan dust ring kicked outward at the base.
        // 2. Stem: narrow dense purple column rising.
        // 3. Cap: wide purple bloom 8-18 blocks above ground, drifts
        //    outward to form the mushroom crown.
        // All three scale by magnitude.

        float m = magnitude;

        var shockwave = new SimpleParticleProperties(
            (int)(900 * m), (int)(1500 * m),
            ColorUtil.ToRgba(220, 200, 170, 200),
            new Vec3d(anchor.X - 1.5 * m, anchor.Y - 0.5, anchor.Z - 1.5 * m),
            new Vec3d(anchor.X + 1.5 * m, anchor.Y + 1.0, anchor.Z + 1.5 * m),
            new Vec3f(-14f * m, 0.3f, -14f * m),
            new Vec3f(14f * m, 2.5f, 14f * m),
            18f,
            -0.5f,
            1.4f, 2.8f,
            EnumParticleModel.Quad
        );
        shockwave.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -12);
        shockwave.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2.5f * m);
        sapi.World.SpawnParticles(shockwave);

        var stem = new SimpleParticleProperties(
            (int)(700 * m), (int)(1200 * m),
            ColorUtil.ToRgba(255, 130, 50, 210),
            new Vec3d(anchor.X - 2.5 * m, anchor.Y, anchor.Z - 2.5 * m),
            new Vec3d(anchor.X + 2.5 * m, anchor.Y + 3, anchor.Z + 2.5 * m),
            new Vec3f(-1f, 6f, -1f),
            new Vec3f(1f, 14f, 1f),
            20f,
            -0.4f,
            1.5f, 3.5f,
            EnumParticleModel.Quad
        );
        stem.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -8);
        stem.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2.5f * m);
        sapi.World.SpawnParticles(stem);

        var cap = new SimpleParticleProperties(
            (int)(1500 * m), (int)(2400 * m),
            ColorUtil.ToRgba(255, 130, 50, 190),
            new Vec3d(anchor.X - 10 * m, anchor.Y + 8, anchor.Z - 10 * m),
            new Vec3d(anchor.X + 10 * m, anchor.Y + 18, anchor.Z + 10 * m),
            new Vec3f(-6f * m, 0.5f, -6f * m),
            new Vec3f(6f * m, 4f, 6f * m),
            22f,
            -0.2f,
            2.5f, 6f,
            EnumParticleModel.Quad
        );
        cap.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -7);
        cap.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 3f * m);
        sapi.World.SpawnParticles(cap);

        // Legacy burst layered on top of the shockwave/stem/cap rework.
        // The combo gives a denser, more chaotic eruption than either alone.
        var legacy = new SimpleParticleProperties(
            (int)(500 * m), (int)(800 * m),
            ColorUtil.ToRgba(255, 130, 50, 180),
            new Vec3d(anchor.X - 8 * m, anchor.Y, anchor.Z - 8 * m),
            new Vec3d(anchor.X + 8 * m, anchor.Y + 28, anchor.Z + 8 * m),
            new Vec3f(-3f, 6f, -3f),
            new Vec3f(3f, 14f, 3f),
            12f,
            -0.25f,
            1.5f, 4f,
            EnumParticleModel.Quad
        );
        legacy.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -10);
        legacy.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 1.5f);
        sapi.World.SpawnParticles(legacy);
    }

    private void CarveSpiceCrater(BlockPos center, float magnitude)
    {
        if (spicesandWetBlockId == 0) return;

        // Scale crater by magnitude so a small puff leaves a 9-10 block
        // bowl while a large blow leaves a near-30-block landmark crater.
        int radius = Math.Max(4, (int)(ConversionRadius * magnitude));
        int maxDepth = Math.Max(2, (int)(BlastRadius * CraterDepthFactor * magnitude));
        int radiusSq = radius * radius;

        var pos = new BlockPos(Dimensions.NormalWorld);

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int distSq = dx * dx + dz * dz;
                if (distSq > radiusSq) continue;

                int wx = center.X + dx;
                int wz = center.Z + dz;

                int? maybeY = sapi.WorldManager.GetSurfacePosY(wx, wz);
                if (maybeY == null) continue;
                int surfaceY = maybeY.Value;
                if (surfaceY <= 1) continue;

                // Parabolic bowl: depth = maxDepth at center, taper to 0
                // at the radius edge. This gives a clean, recognizable
                // crater silhouette rather than a flat-painted spot.
                float distRel = (float)Math.Sqrt(distSq) / radius;
                int bowlDepth = (int)(maxDepth * (1f - distRel * distRel));
                if (bowlDepth < 1) continue;

                int bowlBottom = surfaceY - bowlDepth + 1;
                if (bowlBottom < 2) bowlBottom = 2;

                // Carve everything from the surface down to one above the
                // bowl bottom into air, removing the original sand stack.
                for (int wy = surfaceY; wy > bowlBottom; wy--)
                {
                    pos.Set(wx, wy, wz);
                    sapi.World.BlockAccessor.SetBlock(0, pos);
                }

                // Floor of the bowl is fresh wet spice sand. The walls of
                // the crater (left as their original block from before the
                // blast) frame the purple bottom for visual contrast.
                pos.Set(wx, bowlBottom, wz);
                sapi.World.BlockAccessor.SetBlock(spicesandWetBlockId, pos);
            }
        }
    }
}
