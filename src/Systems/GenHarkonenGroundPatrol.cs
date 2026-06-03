using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class GenHarkonenGroundPatrol : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private readonly HashSet<long> activePatrolEntityIds = new();

    private const float PollIntervalSeconds = 240f;
    private const float PatrolChanceNight = 0.50f;
    private const float PatrolChanceDay = 0.10f;
    private const float NightStartHour = 19f;
    private const float NightEndHour = 6f;
    private const double PatrolMinDistance = 30.0;
    private const double PatrolMaxDistance = 90.0;
    private const int PatrolMinCount = 2;
    private const int PatrolMaxCount = 4;
    private const double PatrolClusterRadius = 5.0;

    private const string OfficerCode = "harkonnen-officer";
    private static readonly string[] GruntCodes = new[]
    {
        "harkonnen-soldier",
        "harkonnen-rifleman"
    };
    private const float OfficerSlotChance = 0.5f;

    private const int SubSeed = 5550987;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(OnPoll, (int)(PollIntervalSeconds * 1000));

        api.ChatCommands.Create("harkpatrol")
            .WithDescription("Spawn a Harkonen ground patrol near you.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnSpawnCommand);
    }

    private TextCommandResult OnSpawnCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        int n = SpawnPatrol(sp);
        return n > 0
            ? TextCommandResult.Success($"Spawned Harkonen patrol of {n}.")
            : TextCommandResult.Error("Couldn't find a dune sand spot to spawn the patrol.");
    }

    private void OnPoll(float dt)
    {
        PruneDeadPatrolUnits();
        if (activePatrolEntityIds.Count > 0) return;

        float hour = sapi.World.Calendar.HourOfDay;
        bool isNight = hour >= NightStartHour || hour < NightEndHour;
        float chance = isNight ? PatrolChanceNight : PatrolChanceDay;
        if (rnd.NextFloat() > chance) return;

        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;
        var picked = players[rnd.NextInt(players.Length)] as IServerPlayer;
        if (picked?.Entity == null) return;

        SpawnPatrol(picked);
    }

    private int SpawnPatrol(IServerPlayer player)
    {
        if (player?.Entity == null) return 0;

        const int MaxTries = 60;
        const int FlatRadius = 1;
        const int MaxHeightDiff = 3;
        double sx = 0, sy = 0, sz = 0;
        bool found = false;

        var probe = new BlockPos(Dimensions.NormalWorld);
        var ba = sapi.World.BlockAccessor;

        for (int i = 0; i < MaxTries && !found; i++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = PatrolMinDistance + rnd.NextDouble() * (PatrolMaxDistance - PatrolMinDistance);
            double tryX = player.Entity.Pos.X + Math.Cos(angle) * dist;
            double tryZ = player.Entity.Pos.Z + Math.Sin(angle) * dist;

            probe.Set((int)tryX, 0, (int)tryZ);
            int centerY = ba.GetRainMapHeightAt(probe);

            if (centerY <= sapi.World.SeaLevel + 1) continue;

            bool flat = true;
            for (int dx = -FlatRadius; dx <= FlatRadius && flat; dx++)
            {
                for (int dz = -FlatRadius; dz <= FlatRadius && flat; dz++)
                {
                    probe.Set((int)tryX + dx, 0, (int)tryZ + dz);
                    int probeY = ba.GetRainMapHeightAt(probe);
                    if (Math.Abs(probeY - centerY) > MaxHeightDiff) flat = false;
                }
            }
            if (!flat) continue;

            probe.Set((int)tryX, centerY, (int)tryZ);
            var bedBlock = ba.GetBlock(probe);
            if (bedBlock == null || bedBlock.Replaceable >= 6000) continue;

            sx = tryX;
            sz = tryZ;
            sy = centerY + 1;
            found = true;
        }
        if (!found)
        {
            sapi.Logger.Notification("[VSDune] Harkonen patrol skipped: no solid surface within range of {0} in {1} tries.", player.PlayerName, MaxTries);
            return 0;
        }

        long herdId = sapi.WorldManager.GetNextUniqueId();
        int patrolCount = PatrolMinCount + rnd.NextInt(PatrolMaxCount - PatrolMinCount + 1);
        bool includeOfficer = rnd.NextFloat() < OfficerSlotChance;
        int spawned = 0;

        for (int i = 0; i < patrolCount; i++)
        {
            string code = (i == 0 && includeOfficer)
                ? OfficerCode
                : GruntCodes[rnd.NextInt(GruntCodes.Length)];

            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null)
            {
                sapi.Logger.Warning("[VSDune] GenHarkonenGroundPatrol: entity type vsdune:{0} not registered, skipping.", code);
                continue;
            }
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = rnd.NextDouble() * PatrolClusterRadius;
            double ux = sx + Math.Cos(angle) * dist;
            double uz = sz + Math.Sin(angle) * dist;
            int uy = ba.GetRainMapHeightAt(new BlockPos((int)ux, 0, (int)uz));

            unit.Pos.SetPos(ux, uy + 1, uz);
            unit.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);

            if (unit is EntityAgent ea) ea.HerdId = herdId;

            sapi.World.SpawnEntity(unit);
            activePatrolEntityIds.Add(unit.EntityId);
            spawned++;
        }

        if (spawned > 0)
        {
            sapi.Logger.Notification(
                "[VSDune] Harkonen patrol of {0} seeded near {1} at ({2:F0}, {3:F0}, {4:F0}), herd {5}.",
                spawned, player.PlayerName, sx, sy, sz, herdId
            );
        }
        return spawned;
    }

    private void PruneDeadPatrolUnits()
    {
        if (activePatrolEntityIds.Count == 0) return;
        var toRemove = new List<long>();
        foreach (var id in activePatrolEntityIds)
        {
            var e = sapi.World.GetEntityById(id);
            if (e == null || !e.Alive) toRemove.Add(id);
        }
        foreach (var id in toRemove) activePatrolEntityIds.Remove(id);
    }
}
