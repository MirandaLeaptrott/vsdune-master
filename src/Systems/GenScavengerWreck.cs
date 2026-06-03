using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;
public class GenScavengerWreck : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private int wreckBlockId = 0;
    private readonly HashSet<BlockPos> knownWrecks = new();
    private readonly Dictionary<BlockPos, long> wreckCooldowns = new();
    private readonly HashSet<long> activeGuardIds = new();

    private const float PollIntervalSeconds = 90f;
    private const float NightSpawnChance = 0.9f;
    private const float DaySpawnChance = 0.1f;
    private const float NightStartHour = 19f;
    private const float NightEndHour = 6f;
    private const int MinGuards = 2;
    private const int MaxGuards = 3;
    private const double GuardSpawnSpread = 4.0;
    private const double PlayerSearchRadius = 220.0;
    private const double NearestGuardCheckRadius = 24.0;
    private const long WreckCooldownMs = 600_000;
    private const int SubSeed = 4422775;

    private static readonly string[] ScavengerCodes = new[]
    {
        "scavenger-spearman",
        "scavenger-sniper",
        "scavenger-thug"
    };

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.SaveGameLoaded += ResolveWreckBlock;
        api.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        api.Event.RegisterGameTickListener(OnPoll, (int)(PollIntervalSeconds * 1000));

        api.ChatCommands.Create("scavengerwreck")
            .WithDescription("Force scavenger guards at the nearest known wreck (admin).")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith(OnForceSpawn);
    }

    private void ResolveWreckBlock()
    {
        var block = sapi.World.GetBlock(new AssetLocation("vsdune", "thopterwreck"));
        wreckBlockId = block?.BlockId ?? 0;
        if (wreckBlockId == 0)
        {
            sapi.Logger.Warning("[VSDune] GenScavengerWreck: vsdune:thopterwreck not registered. Wreck guards disabled.");
        }
    }

    private void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
    {
        if (wreckBlockId == 0) return;
        if (chunks == null || chunks.Length == 0) return;

        var mapChunk = chunks[0]?.MapChunk;
        if (mapChunk == null) return;
        var rainmap = mapChunk.RainHeightMap;
        if (rainmap == null) return;

        const int chunksize = GlobalConstants.ChunkSize;
        int baseX = chunkCoord.X * chunksize;
        int baseZ = chunkCoord.Y * chunksize;
        var pos = new BlockPos(Dimensions.NormalWorld);
        var ba = sapi.World.BlockAccessor;

        for (int dx = 0; dx < chunksize; dx++)
        {
            for (int dz = 0; dz < chunksize; dz++)
            {
                int y = rainmap[dz * chunksize + dx];
                if (y <= 1) continue;
                // GenThopterWreck places at surface+1; rainmap may or may not include the wreck block, so check both y and y+1.
                for (int probe = 0; probe <= 1; probe++)
                {
                    pos.Set(baseX + dx, y + probe, baseZ + dz);
                    var b = ba.GetBlock(pos);
                    if (b != null && b.BlockId == wreckBlockId)
                    {
                        knownWrecks.Add(pos.Copy());
                        break;
                    }
                }
            }
        }
    }

    private void OnPoll(float dt)
    {
        if (wreckBlockId == 0) return;
        PruneDeadGuards();
        if (knownWrecks.Count == 0) return;

        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;

        float spawnChance = IsNight() ? NightSpawnChance : DaySpawnChance;

        long now = sapi.World.ElapsedMilliseconds;
        var candidates = new List<BlockPos>();
        foreach (var w in knownWrecks)
        {
            if (wreckCooldowns.TryGetValue(w, out var until) && until > now) continue;
            if (!HasPlayerNear(w, PlayerSearchRadius)) continue;
            if (HasGuardNear(w, NearestGuardCheckRadius)) continue;
            candidates.Add(w);
        }
        if (candidates.Count == 0) return;

        if (rnd.NextFloat() > spawnChance) return;

        var wreckPos = candidates[rnd.NextInt(candidates.Count)];
        int spawned = SpawnGuardsAt(wreckPos);
        wreckCooldowns[wreckPos] = now + WreckCooldownMs;
        if (spawned > 0)
        {
            sapi.Logger.Notification("[VSDune] {0} scavenger guards spawned at wreck ({1}, {2}, {3}).",
                spawned, wreckPos.X, wreckPos.Y, wreckPos.Z);
        }
    }

    private bool IsNight()
    {
        float hour = sapi.World.Calendar.HourOfDay;
        return hour >= NightStartHour || hour < NightEndHour;
    }

    private bool HasPlayerNear(BlockPos pos, double r)
    {
        double r2 = r * r;
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            var pe = p?.Entity;
            if (pe == null) continue;
            double dx = pe.Pos.X - (pos.X + 0.5);
            double dz = pe.Pos.Z - (pos.Z + 0.5);
            if (dx * dx + dz * dz <= r2) return true;
        }
        return false;
    }

    private bool HasGuardNear(BlockPos pos, double r)
    {
        double r2 = r * r;
        foreach (var id in activeGuardIds)
        {
            var e = sapi.World.GetEntityById(id);
            if (e == null || !e.Alive) continue;
            double dx = e.Pos.X - (pos.X + 0.5);
            double dz = e.Pos.Z - (pos.Z + 0.5);
            if (dx * dx + dz * dz <= r2) return true;
        }
        return false;
    }

    private int SpawnGuardsAt(BlockPos wreckPos)
    {
        long herdId = sapi.WorldManager.GetNextUniqueId();
        int count = MinGuards + rnd.NextInt(MaxGuards - MinGuards + 1);
        int spawned = 0;
        var ba = sapi.World.BlockAccessor;

        for (int i = 0; i < count; i++)
        {
            string code = ScavengerCodes[rnd.NextInt(ScavengerCodes.Length)];
            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null) continue;
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = 1.5 + rnd.NextDouble() * GuardSpawnSpread;
            int sx = wreckPos.X + (int)Math.Round(Math.Cos(angle) * dist);
            int sz = wreckPos.Z + (int)Math.Round(Math.Sin(angle) * dist);
            int sy = ba.GetRainMapHeightAt(new BlockPos(sx, 0, sz));
            if (sy <= sapi.World.SeaLevel + 1) continue;

            unit.Pos.SetPos(sx + 0.5, sy + 1, sz + 0.5);
            unit.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);
            if (unit is EntityAgent ea) ea.HerdId = herdId;

            sapi.World.SpawnEntity(unit);
            activeGuardIds.Add(unit.EntityId);
            spawned++;
        }
        return spawned;
    }

    private void PruneDeadGuards()
    {
        if (activeGuardIds.Count == 0) return;
        var toRemove = new List<long>();
        foreach (var id in activeGuardIds)
        {
            var e = sapi.World.GetEntityById(id);
            if (e == null || !e.Alive) toRemove.Add(id);
        }
        foreach (var id in toRemove) activeGuardIds.Remove(id);
    }

    private TextCommandResult OnForceSpawn(TextCommandCallingArgs args)
    {
        if (wreckBlockId == 0) return TextCommandResult.Error("vsdune:thopterwreck not registered.");
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller.");
        if (knownWrecks.Count == 0) return TextCommandResult.Error("No wrecks have loaded yet.");

        BlockPos best = null;
        double bestD2 = double.MaxValue;
        double px = sp.Entity.Pos.X, pz = sp.Entity.Pos.Z;
        foreach (var w in knownWrecks)
        {
            double dx = w.X - px, dz = w.Z - pz;
            double d2 = dx * dx + dz * dz;
            if (d2 < bestD2) { bestD2 = d2; best = w; }
        }
        if (best == null) return TextCommandResult.Error("No wrecks found.");
        int n = SpawnGuardsAt(best);
        wreckCooldowns[best] = sapi.World.ElapsedMilliseconds + WreckCooldownMs;
        return n > 0
            ? TextCommandResult.Success($"Spawned {n} guards at wreck ({best.X}, {best.Y}, {best.Z}).")
            : TextCommandResult.Error("Could not place guards (no solid ground near wreck).");
    }
}
