using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class GenHarkonenAirPatrol : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    // Distinguishes Hark patrol thopters from raid thopters / ambient
    // flyovers in shared loaded-entity scans.
    public const string AttrHarkonenPatrol = "vsdune.harkonenPatrol";

    private const float PollIntervalSeconds = 180f;
    private const float PatrolChanceNight = 0.55f;
    private const float PatrolChanceDay = 0.15f;
    private const float NightStartHour = 19f;
    private const float NightEndHour = 6f;
    private const float PatrolCooldownSeconds = 360f;
    private const double SpawnHorizontalOffset = 220.0;
    private const double SpawnAltitudeAboveTerrain = 60.0;
    private const float CruiseSpeed = 6.5f;
    private const int TerrainProbeRadius = 30;

    private const float DetectionTickIntervalSeconds = 1.0f;
    private const int DetectionStripRadius = 2;
    private const int DetectionMaxDepth = 30;
    private const float DetectionRebumpCooldownSec = 3.0f;

    private const double BackupSpawnDistance = 180.0;
    private const double BackupSpawnAltitudeAbove = 70.0;
    private const double BackupLandingRingMin = 50.0;
    private const double BackupLandingRingMax = 110.0;
    private const int BackupLandingFlatRadius = 4;
    private const int BackupLandingMaxHeightDiff = 1;
    private const int BackupLandingMaxTries = 16;
    private const int BackupGroundCount = 3;

    private const string HarkonenOfficerCode = "harkonnen-officer";
    private static readonly string[] HarkonenGruntCodes = new[]
    {
        "harkonnen-soldier",
        "harkonnen-rifleman"
    };

    private long lastPatrolMs = 0;

    // Per-entity rebump cooldown so a stationary target doesn't drain
    // score-bumps every detection tick.
    private readonly Dictionary<long, long> lastBumpMsByEntityId = new();

    // Live registry of Hark patrol thopter ids. Detection tick walks
    // this small set instead of iterating every loaded entity in the
    // world. Stale ids are swept lazily when GetEntityById misses.
    private readonly HashSet<long> activePatrolThopterIds = new();

    // Backfill happens once on first detection tick to catch any
    // thopters loaded from a save (the registry is memory-only).
    private bool registryBackfilled;

    private const int SubSeed = 4477812;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(OnPatrolPoll, (int)(PollIntervalSeconds * 1000));
        api.Event.RegisterGameTickListener(OnDetectionTick, (int)(DetectionTickIntervalSeconds * 1000));

        api.ChatCommands.Create("harkpatrolair")
            .WithDescription("Spawn a Harkonen air patrol overhead. Subcommands: status.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnSpawnCommand)
            .BeginSubCommand("status").HandleWith(OnStatusCommand).EndSubCommand();
    }

    private TextCommandResult OnSpawnCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        bool ok = SpawnAirPatrol(sp, bypassCooldown: true);
        return ok ? TextCommandResult.Success("Harkonen air patrol seeded.") : TextCommandResult.Error("Couldn't spawn air patrol.");
    }

    private TextCommandResult OnStatusCommand(TextCommandCallingArgs args)
    {
        int patrols = CountActivePatrols();
        double cooldownLeft = Math.Max(0, (lastPatrolMs + PatrolCooldownSeconds * 1000.0 - sapi.World.ElapsedMilliseconds) / 1000.0);
        return TextCommandResult.Success($"Hark air patrols active: {patrols}, cooldown left: {cooldownLeft:F0}s.");
    }

    private void OnPatrolPoll(float dt)
    {
        double nowMs = sapi.World.ElapsedMilliseconds;
        if (nowMs - lastPatrolMs < PatrolCooldownSeconds * 1000.0) return;
        if (CountActivePatrols() > 0) return;

        float hour = sapi.World.Calendar.HourOfDay;
        bool isNight = hour >= NightStartHour || hour < NightEndHour;
        float chance = isNight ? PatrolChanceNight : PatrolChanceDay;
        if (rnd.NextFloat() > chance) return;

        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;
        var picked = players[rnd.NextInt(players.Length)] as IServerPlayer;
        if (picked?.Entity == null) return;

        SpawnAirPatrol(picked, bypassCooldown: false);
    }

    private bool SpawnAirPatrol(IServerPlayer player, bool bypassCooldown)
    {
        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", "ornithopter-raid"));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] GenHarkonenAirPatrol: vsdune:ornithopter-raid not registered.");
            return false;
        }

        double bearing = rnd.NextDouble() * Math.PI * 2;
        double dirX = Math.Cos(bearing);
        double dirZ = Math.Sin(bearing);

        double spawnX = player.Entity.Pos.X - dirX * SpawnHorizontalOffset;
        double spawnZ = player.Entity.Pos.Z - dirZ * SpawnHorizontalOffset;

        var probe = new BlockPos(Dimensions.NormalWorld);
        var ba = sapi.World.BlockAccessor;
        probe.Set((int)spawnX, 0, (int)spawnZ);
        int maxTerrainY = ba.GetRainMapHeightAt(probe);
        int step = TerrainProbeRadius / 3;
        for (int dx = -TerrainProbeRadius; dx <= TerrainProbeRadius; dx += step)
        {
            for (int dz = -TerrainProbeRadius; dz <= TerrainProbeRadius; dz += step)
            {
                probe.Set((int)spawnX + dx, 0, (int)spawnZ + dz);
                int probeY = ba.GetRainMapHeightAt(probe);
                if (probeY > maxTerrainY) maxTerrainY = probeY;
            }
        }
        double spawnY = maxTerrainY + SpawnAltitudeAboveTerrain;

        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
        if (entity == null) return false;

        sapi.World.SpawnEntity(entity);
        entity.Pos.SetPos(spawnX, spawnY, spawnZ);
        EntityBehaviorOrnithopterFlight.SetFlight(entity, new Vec3d(dirX, 0, dirZ), CruiseSpeed);
        entity.WatchedAttributes.SetBool(AttrHarkonenPatrol, true);
        activePatrolThopterIds.Add(entity.EntityId);

        if (!bypassCooldown) lastPatrolMs = (long)sapi.World.ElapsedMilliseconds;

        FactionChannels.Harkonen(sapi, "Patrol on station. Scanning vector.");
        sapi.Logger.Notification("[VSDune] Hark air patrol seeded near {0} at ({1:F0}, {2:F0}, {3:F0}).",
            player.PlayerName, spawnX, spawnY, spawnZ);
        return true;
    }

    private int CountActivePatrols()
    {
        int count = 0;
        foreach (var e in sapi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive) continue;
            if (e.Code?.Domain != "vsdune") continue;
            if (e.Code?.Path != "ornithopter-raid" && e.Code?.Path != "ornithopter") continue;
            if (!e.WatchedAttributes.GetBool(AttrHarkonenPatrol, false)) continue;
            count++;
        }
        return count;
    }

    private void OnDetectionTick(float dt)
    {
        var activity = sapi.ModLoader.GetModSystem<GenHarkonenActivity>();
        if (activity == null) return;

        // One-shot backfill: pick up thopters loaded from save that
        // weren't in the registry at server start.
        if (!registryBackfilled)
        {
            registryBackfilled = true;
            foreach (var e in sapi.World.LoadedEntities.Values)
            {
                if (e == null || !e.Alive) continue;
                if (e.Code?.Domain != "vsdune") continue;
                if (e.Code?.Path != "ornithopter-raid" && e.Code?.Path != "ornithopter") continue;
                if (!e.WatchedAttributes.GetBool(AttrHarkonenPatrol, false)) continue;
                activePatrolThopterIds.Add(e.EntityId);
            }
        }

        if (activePatrolThopterIds.Count == 0) return;

        long nowMs = sapi.World.ElapsedMilliseconds;

        // Walk the patrol registry; collect any ids that no longer
        // resolve to a live entity so we can drop them after the loop.
        List<long> stale = null;
        foreach (var id in activePatrolThopterIds)
        {
            var thopter = sapi.World.GetEntityById(id);
            if (thopter == null || !thopter.Alive)
            {
                (stale ??= new List<long>()).Add(id);
                continue;
            }
            ScanColumnUnder(thopter, activity, nowMs);
        }
        if (stale != null) foreach (var id in stale) activePatrolThopterIds.Remove(id);

        if (lastBumpMsByEntityId.Count > 0)
        {
            List<long> bumpStale = null;
            foreach (var kv in lastBumpMsByEntityId)
            {
                if (nowMs - kv.Value > 30_000) (bumpStale ??= new List<long>()).Add(kv.Key);
            }
            if (bumpStale != null) foreach (var id in bumpStale) lastBumpMsByEntityId.Remove(id);
        }
    }

    private void ScanColumnUnder(Entity thopter, GenHarkonenActivity activity, long nowMs)
    {
        double tx = thopter.Pos.X;
        double tz = thopter.Pos.Z;
        double ty = thopter.Pos.Y;

        // Spatial query rather than iterating every loaded entity. Box
        // is centered half-way down the detection depth.
        var center = new Vec3d(tx, ty - DetectionMaxDepth * 0.5, tz);
        float horRange = DetectionStripRadius + 0.5f;
        float vertRange = DetectionMaxDepth * 0.5f + 0.5f;

        var candidates = sapi.World.GetEntitiesAround(center, horRange, vertRange, (ent) =>
        {
            if (ent == null || ent == thopter || !ent.Alive) return false;
            if (ent is not EntityAgent) return false;
            string path = ent.Code?.Path;
            if (path != null && path.StartsWith("harkonnen-")) return false;
            return ent.Pos.Y < ty;
        });

        foreach (var ent in candidates)
        {
            if (HasRoofBetween(ent.Pos.X, ent.Pos.Y, ent.Pos.Z, ty)) continue;

            lastBumpMsByEntityId.TryGetValue(ent.EntityId, out long last);
            if (nowMs - last < DetectionRebumpCooldownSec * 1000) continue;
            lastBumpMsByEntityId[ent.EntityId] = nowMs;

            if (ent is EntityPlayer ep)
            {
                if (ep.Player is IServerPlayer sp) activity.Bump(sp, GenHarkonenActivity.BumpFromDetection);
            }
            else
            {
                activity.BumpFromHarkAttackedBy(ent, GenHarkonenActivity.BumpFromDetection);
            }
        }
    }

    private bool HasRoofBetween(double x, double yBottom, double z, double yTop)
    {
        var probe = new BlockPos(Dimensions.NormalWorld);
        int xi = (int)x;
        int zi = (int)z;
        int yStart = (int)Math.Ceiling(yBottom) + 1;
        int yEnd = (int)Math.Floor(yTop);
        for (int y = yStart; y <= yEnd; y++)
        {
            probe.Set(xi, y, zi);
            var b = sapi.World.BlockAccessor.GetBlock(probe);
            if (b == null) continue;
            // Replaceable >= 6000 is air-ish; anything below is solid roof.
            if (b.Replaceable < 6000) return true;
        }
        return false;
    }

    // Called by GenHarkonenActivity when score crosses threshold.
    // Lands a Hark thopter on dune sand near the player and disembarks
    // a ground squad.
    public void DispatchBackupRaid(IServerPlayer player)
    {
        if (player?.Entity == null) return;

        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", "ornithopter-raid"));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] DispatchBackupRaid: vsdune:ornithopter-raid not registered.");
            return;
        }

        Vec3d landingSpot = FindDuneLandingSpot(new Vec3d(player.Entity.Pos.X, player.Entity.Pos.Y, player.Entity.Pos.Z));
        if (landingSpot == null)
        {
            sapi.Logger.Notification("[VSDune] DispatchBackupRaid: no dune landing spot near {0}.", player.PlayerName);
            return;
        }

        double bearing = rnd.NextDouble() * Math.PI * 2;
        double sx = landingSpot.X - Math.Cos(bearing) * BackupSpawnDistance;
        double sz = landingSpot.Z - Math.Sin(bearing) * BackupSpawnDistance;
        double sy = landingSpot.Y + BackupSpawnAltitudeAbove;

        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
        if (entity == null) return;
        EntityBehaviorOrnithopterFlight.SetApproachTarget(entity, landingSpot);
        sapi.World.SpawnEntity(entity);
        entity.Pos.SetPos(sx, sy, sz);
        entity.WatchedAttributes.SetBool(AttrHarkonenPatrol, true);
        activePatrolThopterIds.Add(entity.EntityId);

        FactionChannels.Harkonen(sapi, $"Backup en route. ETA short, ground team converging on {player.PlayerName}.");
        sapi.Logger.Notification("[VSDune] Hark backup raid inbound for {0}, landing at ({1:F0}, {2:F0}, {3:F0}).",
            player.PlayerName, landingSpot.X, landingSpot.Y, landingSpot.Z);

        long craftId = entity.EntityId;
        Vec3d landingCopy = new Vec3d(landingSpot.X, landingSpot.Y, landingSpot.Z);
        entity.WatchedAttributes.RegisterModifiedListener(EntityBehaviorOrnithopterFlight.AttrMode, () =>
        {
            var thopter = sapi.World.GetEntityById(craftId);
            if (thopter == null || !thopter.Alive) return;
            string mode = thopter.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, "");
            if (mode == EntityBehaviorOrnithopterFlight.ModeLanded)
            {
                SpawnBackupGroundSquad(landingCopy);
            }
        });
    }

    // Called by BlockEntityThopterWreck when a "hark" variant wreck
    // first detects a player. Lands an investigation thopter on dune
    // sand near the wreck and disembarks a ground squad.
    public void DispatchInvestigation(Vec3d wreckPos)
    {
        if (wreckPos == null) return;

        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", "ornithopter-raid"));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] DispatchInvestigation: vsdune:ornithopter-raid not registered.");
            return;
        }

        Vec3d landingSpot = FindDuneLandingSpot(wreckPos);
        if (landingSpot == null)
        {
            sapi.Logger.Notification("[VSDune] DispatchInvestigation: no dune landing spot near wreck at ({0:F0}, {1:F0}, {2:F0}).",
                wreckPos.X, wreckPos.Y, wreckPos.Z);
            return;
        }

        double bearing = rnd.NextDouble() * Math.PI * 2;
        double sx = landingSpot.X - Math.Cos(bearing) * BackupSpawnDistance;
        double sz = landingSpot.Z - Math.Sin(bearing) * BackupSpawnDistance;
        double sy = landingSpot.Y + BackupSpawnAltitudeAbove;

        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
        if (entity == null) return;
        EntityBehaviorOrnithopterFlight.SetApproachTarget(entity, landingSpot);
        sapi.World.SpawnEntity(entity);
        entity.Pos.SetPos(sx, sy, sz);
        entity.WatchedAttributes.SetBool(AttrHarkonenPatrol, true);
        activePatrolThopterIds.Add(entity.EntityId);

        FactionChannels.Harkonen(sapi, $"Wreck site at ({wreckPos.X:F0}, {wreckPos.Z:F0}). Investigation team inbound.");
        sapi.Logger.Notification("[VSDune] Hark investigation thopter inbound to wreck at ({0:F0}, {1:F0}, {2:F0}), landing ({3:F0}, {4:F0}, {5:F0}).",
            wreckPos.X, wreckPos.Y, wreckPos.Z, landingSpot.X, landingSpot.Y, landingSpot.Z);

        long craftId = entity.EntityId;
        Vec3d landingCopy = new Vec3d(landingSpot.X, landingSpot.Y, landingSpot.Z);
        entity.WatchedAttributes.RegisterModifiedListener(EntityBehaviorOrnithopterFlight.AttrMode, () =>
        {
            var thopter = sapi.World.GetEntityById(craftId);
            if (thopter == null || !thopter.Alive) return;
            string mode = thopter.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, "");
            if (mode == EntityBehaviorOrnithopterFlight.ModeLanded)
            {
                SpawnBackupGroundSquad(landingCopy);
            }
        });
    }

    private void SpawnBackupGroundSquad(Vec3d landingSpot)
    {
        var ba = sapi.World.BlockAccessor;
        for (int i = 0; i < BackupGroundCount; i++)
        {
            string code = (i == 0)
                ? HarkonenOfficerCode
                : HarkonenGruntCodes[rnd.NextInt(HarkonenGruntCodes.Length)];

            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null) continue;
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = 2 + rnd.NextDouble() * 4;
            double ux = landingSpot.X + Math.Cos(angle) * dist;
            double uz = landingSpot.Z + Math.Sin(angle) * dist;
            int uy = ba.GetRainMapHeightAt(new BlockPos((int)ux, 0, (int)uz));

            unit.Pos.SetPos(ux, uy + 1, uz);
            unit.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);
            sapi.World.SpawnEntity(unit);
        }
    }

    private Vec3d FindDuneLandingSpot(Vec3d nearPos)
    {
        var probe = new BlockPos(Dimensions.NormalWorld);
        var ba = sapi.World.BlockAccessor;
        int sealevel = sapi.World.SeaLevel;

        for (int i = 0; i < BackupLandingMaxTries; i++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = BackupLandingRingMin + rnd.NextDouble() * (BackupLandingRingMax - BackupLandingRingMin);
            double tryX = nearPos.X + Math.Cos(angle) * dist;
            double tryZ = nearPos.Z + Math.Sin(angle) * dist;
            probe.Set((int)tryX, 0, (int)tryZ);
            int centerY = ba.GetRainMapHeightAt(probe);
            if (centerY <= sealevel + 1) continue;

            bool flat = true;
            for (int dx = -BackupLandingFlatRadius; dx <= BackupLandingFlatRadius && flat; dx++)
            {
                for (int dz = -BackupLandingFlatRadius; dz <= BackupLandingFlatRadius && flat; dz++)
                {
                    probe.Set((int)tryX + dx, 0, (int)tryZ + dz);
                    int probeY = ba.GetRainMapHeightAt(probe);
                    if (Math.Abs(probeY - centerY) > BackupLandingMaxHeightDiff) flat = false;
                }
            }
            if (!flat) continue;

            probe.Set((int)tryX, centerY, (int)tryZ);
            var bedBlock = ba.GetBlock(probe);
            if (bedBlock == null) continue;
            if (bedBlock.BlockMaterial != EnumBlockMaterial.Sand) continue;

            return new Vec3d(tryX, centerY + 1, tryZ);
        }
        return null;
    }
}
