using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class GenFremenSpiceResponse : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private const float ResponseChance = 0.30f;
    private const float ApproachDelaySeconds = 60f;
    private const double SpawnRingMin = 80.0;
    private const double SpawnRingMax = 180.0;
    private const double SpawnRingMaxFallback = 380.0;
    private const int PatrolMinCount = 3;
    private const int PatrolMaxCount = 5;
    private const double PatrolClusterRadius = 4.0;
    private const float ArcherSlotChance = 0.5f;

    private const string FremenArcherCode = "fremen-archer";
    private static readonly string[] FremenMeleeCodes = new[]
    {
        "fremen-warrior-axe",
        "fremen-warrior-knife",
        "fremen-warrior-spear"
    };

    private const int SubSeed = 9912004;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        GenSpiceBlow.OnDetonate += OnSpiceBlowDetonate;
    }

    public override void Dispose()
    {
        GenSpiceBlow.OnDetonate -= OnSpiceBlowDetonate;
        base.Dispose();
    }

    private void OnSpiceBlowDetonate(Vec3d center, float magnitude)
    {
        if (rnd.NextFloat() >= ResponseChance) return;

        var captured = new Vec3d(center.X, center.Y, center.Z);
        sapi.Event.RegisterCallback((dt) => SpawnFremenResponse(captured), (int)(ApproachDelaySeconds * 1000));
    }

    private void SpawnFremenResponse(Vec3d craterCenter)
    {
        const int MaxTries = 24;
        const int FlatRadius = 2;
        const int MaxHeightDiff = 1;
        double sx = 0, sy = 0, sz = 0;
        bool found = false;

        var probe = new BlockPos(Dimensions.NormalWorld);
        var ba = sapi.World.BlockAccessor;
        int sealevel = sapi.World.SeaLevel;

        // Pass 1: strict dune perimeter in the inner ring.
        found = TryFindSpot(craterCenter, probe, ba, sealevel, SpawnRingMin, SpawnRingMax, MaxTries, FlatRadius, MaxHeightDiff, duneOnly: true, out sx, out sy, out sz);

        // Pass 2: widen the search ring but stay dune-only, so a crater
        // ringed by open basin skips rather than dropping Fremen onto the
        // dry seafloor.
        if (!found)
        {
            found = TryFindSpot(craterCenter, probe, ba, sealevel, SpawnRingMax, SpawnRingMaxFallback, MaxTries * 2, FlatRadius, MaxHeightDiff, duneOnly: true, out sx, out sy, out sz);
        }

        if (!found)
        {
            sapi.Logger.Notification("[VSDune] Fremen spice response skipped: no usable sand within {0} of crater ({1:F0},{2:F0}).", SpawnRingMaxFallback, craterCenter.X, craterCenter.Z);
            return;
        }

        long herdId = sapi.WorldManager.GetNextUniqueId();
        int patrolCount = PatrolMinCount + rnd.NextInt(PatrolMaxCount - PatrolMinCount + 1);
        bool includeArcher = rnd.NextFloat() < ArcherSlotChance;
        int spawned = 0;

        for (int i = 0; i < patrolCount; i++)
        {
            string code = (i == 0 && includeArcher)
                ? FremenArcherCode
                : FremenMeleeCodes[rnd.NextInt(FremenMeleeCodes.Length)];

            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null)
            {
                sapi.Logger.Warning("[VSDune] GenFremenSpiceResponse: entity type vsdune:{0} not registered, skipping.", code);
                continue;
            }
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = rnd.NextDouble() * PatrolClusterRadius;
            double ux = sx + Math.Cos(angle) * dist;
            double uz = sz + Math.Sin(angle) * dist;
            int uy = ba.GetRainMapHeightAt(new BlockPos((int)ux, 0, (int)uz));

            // Scripted-spawn flag bypasses any basin filter edge case
            // on dune so a one-block sealevel seam doesn't cull us.
            unit.Pos.SetPos(ux, uy + 1, uz);
            unit.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);

            if (unit is EntityAgent ea) ea.HerdId = herdId;

            sapi.World.SpawnEntity(unit);
            spawned++;
        }

        if (spawned > 0)
        {
            sapi.Logger.Notification(
                "[VSDune] Fremen spice response: {0} seeded at ({1:F0}, {2:F0}, {3:F0}) responding to crater at ({4:F0}, {5:F0}), herd {6}.",
                spawned, sx, sy, sz, craterCenter.X, craterCenter.Z, herdId
            );
        }
    }

    private bool TryFindSpot(Vec3d center, BlockPos probe, IBlockAccessor ba, int sealevel,
        double ringMin, double ringMax, int maxTries, int flatRadius, int maxHeightDiff,
        bool duneOnly, out double sx, out double sy, out double sz)
    {
        sx = 0; sy = 0; sz = 0;
        for (int i = 0; i < maxTries; i++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double distance = ringMin + rnd.NextDouble() * (ringMax - ringMin);
            double tryX = center.X + Math.Cos(angle) * distance;
            double tryZ = center.Z + Math.Sin(angle) * distance;

            probe.Set((int)tryX, 0, (int)tryZ);
            int centerY = ba.GetRainMapHeightAt(probe);
            if (duneOnly && centerY <= sealevel + 1) continue;

            bool flat = true;
            for (int dx = -flatRadius; dx <= flatRadius && flat; dx++)
            {
                for (int dz = -flatRadius; dz <= flatRadius && flat; dz++)
                {
                    probe.Set((int)tryX + dx, 0, (int)tryZ + dz);
                    int probeY = ba.GetRainMapHeightAt(probe);
                    if (Math.Abs(probeY - centerY) > maxHeightDiff) flat = false;
                }
            }
            if (!flat) continue;

            // Any solid surface: sand, rock, gravel, soil all count.
            probe.Set((int)tryX, centerY, (int)tryZ);
            var bedBlock = ba.GetBlock(probe);
            if (bedBlock == null || bedBlock.Replaceable >= 6000) continue;

            sx = tryX;
            sz = tryZ;
            sy = centerY + 1;
            return true;
        }
        return false;
    }
}
