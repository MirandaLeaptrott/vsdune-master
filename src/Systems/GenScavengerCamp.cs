using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsDune;
public class GenScavengerCamp : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private readonly List<CampRecord> activeCamps = new();

    private const float PollIntervalSeconds = 300f;
    private const float NightSpawnChance = 0.9f;
    private const float DaySpawnChance = 0.1f;
    private const float NightStartHour = 19f;
    private const float NightEndHour = 6f;
    private const int MaxActiveCamps = 3;
    private const int MinOccupants = 2;
    private const int MaxOccupants = 4;
    private const double CampClusterRadius = 4.0;
    private const double CampMinDistance = 60.0;
    private const double CampMaxDistance = 180.0;
    private const int SubSeed = 8821517;

    private static readonly string[] ScavengerCodes = new[]
    {
        "scavenger-spearman",
        "scavenger-sniper",
        "scavenger-thug"
    };

    private class CampRecord
    {
        public BlockPos Center;
        public int FirepitBlockId;
        public List<long> OccupantIds = new();
    }

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(OnPoll, (int)(PollIntervalSeconds * 1000));

        api.ChatCommands.Create("scavengercamp")
            .WithDescription("Spawn a scavenger camp near you (admin).")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller.");
                var camp = TrySeedCamp(sp);
                return camp != null
                    ? TextCommandResult.Success($"Scavenger camp seeded at ({camp.Center.X}, {camp.Center.Y}, {camp.Center.Z}).")
                    : TextCommandResult.Error("Couldn't find a solid spot for the camp.");
            });
    }

    private void OnPoll(float dt)
    {
        PruneDeadCamps();
        if (activeCamps.Count >= MaxActiveCamps) return;

        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;

        float spawnChance = IsNight() ? NightSpawnChance : DaySpawnChance;
        if (rnd.NextFloat() > spawnChance) return;

        var picked = players[rnd.NextInt(players.Length)] as IServerPlayer;
        if (picked?.Entity == null) return;

        TrySeedCamp(picked);
    }

    private bool IsNight()
    {
        float hour = sapi.World.Calendar.HourOfDay;
        return hour >= NightStartHour || hour < NightEndHour;
    }

    private CampRecord TrySeedCamp(IServerPlayer player)
    {
        const int MaxTries = 60;
        const int FlatRadius = 1;
        const int MaxHeightDiff = 3;
        var ba = sapi.World.BlockAccessor;
        var probe = new BlockPos(Dimensions.NormalWorld);
        double cx = 0, cz = 0;
        int cy = 0;
        bool found = false;

        for (int i = 0; i < MaxTries && !found; i++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = CampMinDistance + rnd.NextDouble() * (CampMaxDistance - CampMinDistance);
            double tx = player.Entity.Pos.X + Math.Cos(angle) * dist;
            double tz = player.Entity.Pos.Z + Math.Sin(angle) * dist;

            probe.Set((int)tx, 0, (int)tz);
            int centerY = ba.GetRainMapHeightAt(probe);
            if (centerY <= sapi.World.SeaLevel + 1) continue;

            bool flat = true;
            for (int dx = -FlatRadius; dx <= FlatRadius && flat; dx++)
            {
                for (int dz = -FlatRadius; dz <= FlatRadius && flat; dz++)
                {
                    probe.Set((int)tx + dx, 0, (int)tz + dz);
                    int py = ba.GetRainMapHeightAt(probe);
                    if (Math.Abs(py - centerY) > MaxHeightDiff) flat = false;
                }
            }
            if (!flat) continue;

            probe.Set((int)tx, centerY, (int)tz);
            var bed = ba.GetBlock(probe);
            if (bed == null || bed.Replaceable >= 6000) continue;

            cx = tx; cz = tz; cy = centerY + 1;
            found = true;
        }

        if (!found)
        {
            sapi.Logger.Notification("[VSDune] Scavenger camp skipped: no flat solid surface near {0}.", player.PlayerName);
            return null;
        }

        // Place firepit-cold so the BE initializes, then load fuel and ignite. igniteFuel handles the swap to firepit-lit.
        var firepitColdBlock = sapi.World.GetBlock(new AssetLocation("game", "firepit-cold"))
                              ?? sapi.World.GetBlock(new AssetLocation("game", "firepit-extinct"));
        int firepitId = firepitColdBlock?.BlockId ?? 0;
        var firepitPos = new BlockPos((int)cx, cy, (int)cz);
        if (firepitId != 0)
        {
            ba.SetBlock(firepitId, firepitPos);
            // Defer fuel+ignite so the BE has time to initialize after SetBlock. Immediate GetBlockEntity can return null on the same tick.
            var capturedPos = firepitPos.Copy();
            sapi.Event.RegisterCallback(_ => TryLightFirepit(capturedPos), 500);
        }

        var camp = new CampRecord { Center = firepitPos.Copy(), FirepitBlockId = firepitId };
        long herdId = sapi.WorldManager.GetNextUniqueId();
        int occupants = MinOccupants + rnd.NextInt(MaxOccupants - MinOccupants + 1);

        for (int i = 0; i < occupants; i++)
        {
            string code = ScavengerCodes[rnd.NextInt(ScavengerCodes.Length)];
            var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (bType == null) continue;
            var unit = sapi.World.ClassRegistry.CreateEntity(bType);
            if (unit == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = 1.5 + rnd.NextDouble() * CampClusterRadius;
            int sx = (int)cx + (int)Math.Round(Math.Cos(angle) * dist);
            int sz = (int)cz + (int)Math.Round(Math.Sin(angle) * dist);
            int sy = ba.GetRainMapHeightAt(new BlockPos(sx, 0, sz));
            if (sy <= sapi.World.SeaLevel + 1) continue;

            unit.Pos.SetPos(sx + 0.5, sy + 1, sz + 0.5);
            unit.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);
            if (unit is EntityAgent ea) ea.HerdId = herdId;

            sapi.World.SpawnEntity(unit);
            camp.OccupantIds.Add(unit.EntityId);
        }

        if (camp.OccupantIds.Count == 0)
        {
            if (firepitId != 0) ba.SetBlock(0, firepitPos);
            return null;
        }

        activeCamps.Add(camp);
        sapi.Logger.Notification("[VSDune] Scavenger camp ({0} occupants) seeded near {1} at ({2}, {3}, {4}).",
            camp.OccupantIds.Count, player.PlayerName, firepitPos.X, firepitPos.Y, firepitPos.Z);
        return camp;
    }

    // Loads a sizable firewood stack into the camp firepit and ignites it. The fire keeps re-igniting from the queued fuel as each piece burns out, so a fresh camp lasts hours rather than minutes.
    private void TryLightFirepit(BlockPos pos)
    {
        var be = sapi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityFirepit;
        if (be == null) return;

        var firewoodItem = sapi.World.GetItem(new AssetLocation("game", "firewood"));
        if (firewoodItem == null) return;

        be.fuelStack = new ItemStack(firewoodItem, 16);
        be.igniteFuel();
    }

    // A camp is "dead" when none of its occupants are alive. Players clearing all the scavengers retires the camp slot.
    private void PruneDeadCamps()
    {
        if (activeCamps.Count == 0) return;
        var toRemove = new List<CampRecord>();
        foreach (var c in activeCamps)
        {
            bool anyAlive = false;
            foreach (var id in c.OccupantIds)
            {
                var e = sapi.World.GetEntityById(id);
                if (e != null && e.Alive) { anyAlive = true; break; }
            }
            if (!anyAlive) toRemove.Add(c);
        }
        foreach (var c in toRemove) activeCamps.Remove(c);
    }
}
