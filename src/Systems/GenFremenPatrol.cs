using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class GenFremenPatrol : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private readonly HashSet<long> activePatrolEntityIds = new();

    private const float PollIntervalSeconds = 180f; // 3 min poll
    private const float PatrolChanceNight = 0.55f;
    private const float PatrolChanceDay = 0.12f;
    private const float NightStartHour = 19f;
    private const float NightEndHour = 6f;
    private const double PatrolMinDistance = 30.0;
    private const double PatrolMaxDistance = 90.0;
    private const int PatrolMinCount = 3;
    private const int PatrolMaxCount = 5;
    private const double PatrolClusterRadius = 4.0;
    private const float ArcherSlotChance = 0.5f; // 50% patrols include 1 archer

    private const string FremenArcherCode = "fremen-archer";
    private static readonly string[] FremenMeleeCodes = new[]
    {
        "fremen-warrior-axe",
        "fremen-warrior-knife",
        "fremen-warrior-spear"
    };

    private const int SubSeed = 8866443;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(OnPoll, (int)(PollIntervalSeconds * 1000));

        api.ChatCommands.Create("fremenpatrol")
            .WithDescription("Spawn a Fremen patrol near you.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnSpawnCommand);
    }

    private TextCommandResult OnSpawnCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        int count = SpawnPatrol(sp);
        return count > 0
            ? TextCommandResult.Success($"Spawned Fremen patrol of {count}.")
            : TextCommandResult.Error("Couldn't find a basin sand spot to spawn the patrol.");
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

        // Find a flat sandy spot at PatrolDistanceFromPlayer. Same
        // shape as the landed-encounter scan but smaller footprint.
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

            // Above sealevel only. Basin floor is the worm theater.
            if (centerY <= sapi.World.SeaLevel + 1) continue;

            // Any solid surface: sand, rock, gravel, soil all count.
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
            sapi.Logger.Notification("[VSDune] Fremen patrol skipped: no dune sand within range of {0} in {1} tries.", player.PlayerName, MaxTries);
            return 0;
        }

        // Unique HerdId so the patrol cluster sticks together via
        // stayclosetoherd AND so herd-revenge propagation in
        // EntityFactionUnit.OnHurt targets the right unit set.
        long herdId = sapi.WorldManager.GetNextUniqueId();

        int patrolCount = PatrolMinCount + rnd.NextInt(PatrolMaxCount - PatrolMinCount + 1);
        bool includeArcher = rnd.NextFloat() < ArcherSlotChance;
        int spawned = 0;

        for (int i = 0; i < patrolCount; i++)
        {
            // Slot 0 is the archer if the patrol rolls one; rest melee.
            string code = (i == 0 && includeArcher)
                ? FremenArcherCode
                : FremenMeleeCodes[rnd.NextInt(FremenMeleeCodes.Length)];

            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null)
            {
                sapi.Logger.Warning("[VSDune] GenFremenPatrol: entity type vsdune:{0} not registered, skipping.", code);
                continue;
            }
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = rnd.NextDouble() * PatrolClusterRadius;
            double ux = sx + Math.Cos(angle) * dist;
            double uz = sz + Math.Sin(angle) * dist;
            int uy = ba.GetRainMapHeightAt(new BlockPos((int)ux, 0, (int)uz));

            // Set position + scripted-spawn flag BEFORE SpawnEntity
            // so the basin filter in EntityBehaviorOutlawArrakis
            // doesn't cull the patrol on basin sand.
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
                "[VSDune] Fremen patrol of {0} seeded near {1} at ({2:F0}, {3:F0}, {4:F0}), herd {5}.",
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
