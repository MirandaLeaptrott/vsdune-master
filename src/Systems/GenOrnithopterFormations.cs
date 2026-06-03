using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;


public class GenOrnithopterFormations : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;


    private long lastFormationSpawnedMs;
    private const float FormationCooldownSeconds = 480f; // 8 min between formations

    // Poll cadence. 60s poll; the cooldown plus the per-poll chance
    // below keep flyovers occasional rather than constant.
    private const float PollIntervalSeconds = 60f;
    private const float FormationChancePerPoll = 0.20f;
    private const double SpawnHorizontalOffset = 180.0;
    private const double SpawnAltitudeAboveTerrain = 130.0;
    private const int TerrainProbeRadius = 30;
    private const float CruiseSpeed = 7.5f;
    private const double FormationLateralSpacing = 16.0;
    private const double FormationDepthSpacing = 10.0;
    private const double FormationVerticalJitter = 2.5;

    // Limits.
    private const int MinFormationSize = 1;
    private const int MaxFormationSize = 5;

    private const int SubSeed = 951772;
    public const string AttrAmbientFlyover = "vsdune.ambientFlyover";

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(OnPoll, (int)(PollIntervalSeconds * 1000));

        api.ChatCommands.Create("ornithopter")
            .WithDescription("Spawn an ornithopter formation. Subcommands: spawn (default, near you), status.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnSpawnCommand)
            .BeginSubCommand("status").HandleWith(OnStatusCommand).EndSubCommand();
    }

    private TextCommandResult OnSpawnCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        int n = SpawnFormationFor(sp, bypassCooldown: true);
        return TextCommandResult.Success($"Spawned {n} ornithopter(s).");
    }

    private TextCommandResult OnStatusCommand(TextCommandCallingArgs args)
    {
        double now = sapi.World.ElapsedMilliseconds / 1000.0;
        double last = lastFormationSpawnedMs / 1000.0;
        double cooldownLeft = Math.Max(0, (last + FormationCooldownSeconds) - now);
        int alive = CountAliveOrnithopters();
        return TextCommandResult.Success($"Ornithopter status: alive={alive}, cooldownLeft={cooldownLeft:F0}s.");
    }

    private void OnPoll(float dt)
    {
        // Cooldown gate. Don't pile up formations.
        double nowMs = sapi.World.ElapsedMilliseconds;
        if (nowMs - lastFormationSpawnedMs < FormationCooldownSeconds * 1000.0) return;

        // Don't spawn if a formation is already in the air.
        if (CountAliveOrnithopters() > 0) return;

        if (rnd.NextFloat() > FormationChancePerPoll) return;

        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;
        var picked = players[rnd.NextInt(players.Length)] as IServerPlayer;
        if (picked?.Entity == null) return;

        SpawnFormationFor(picked, bypassCooldown: false);
    }

    private int SpawnFormationFor(IServerPlayer player, bool bypassCooldown)
    {
        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", "ornithopter-raid"));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] GenOrnithopterFormations: vsdune:ornithopter-raid entity type not registered.");
            return 0;
        }


        double bearing = rnd.NextDouble() * Math.PI * 2;
        double dirX = Math.Cos(bearing);
        double dirZ = Math.Sin(bearing);

        // Spawn origin: player + (-dir * offset). Plus altitude.
        double px = player.Entity.Pos.X;
        double pz = player.Entity.Pos.Z;
        double spawnX = px - dirX * SpawnHorizontalOffset;
        double spawnZ = pz - dirZ * SpawnHorizontalOffset;

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

        // Formation size: 1..MaxFormationSize, weighted toward middle.
        int size = MinFormationSize + rnd.NextInt(MaxFormationSize - MinFormationSize + 1);

        // Compute right-vector perpendicular to direction for lateral
        // spacing.
        double rightX = -dirZ;
        double rightZ = dirX;

        var direction = new Vec3d(dirX, 0, dirZ);
        int spawned = 0;

        for (int i = 0; i < size; i++)
        {
            // V pattern offsets. Lead bird at index 0 in the front,
            // wingmen alternating left/right behind it. Each rank
            // is one DepthSpacing further back than the last.
            int rank = (i + 1) / 2;
            int sideSign = (i % 2 == 0) ? 1 : -1;
            double lateralOff = (i == 0) ? 0 : sideSign * rank * FormationLateralSpacing;
            double depthOff = (i == 0) ? 0 : -rank * FormationDepthSpacing;
            // depthOff in dir-axis: birds further back are minus along
            // dir from the lead.
            double yJitter = (rnd.NextDouble() - 0.5) * 2 * FormationVerticalJitter;

            double x = spawnX + dirX * depthOff + rightX * lateralOff;
            double z = spawnZ + dirZ * depthOff + rightZ * lateralOff;
            double y = spawnY + yJitter;

            var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
            if (entity == null) continue;

            sapi.World.SpawnEntity(entity);
            entity.Pos.SetPos(x, y, z);
            EntityBehaviorOrnithopterFlight.SetFlight(entity, direction, CruiseSpeed);
            entity.WatchedAttributes.SetBool(AttrAmbientFlyover, true);
            spawned++;
        }

        if (spawned > 0)
        {
            if (!bypassCooldown) lastFormationSpawnedMs = (long)sapi.World.ElapsedMilliseconds;
            sapi.Logger.Notification(
                "[VSDune] Ornithopter formation of {0} seeded near {1} at ({2:F0}, {3:F0}, {4:F0}), heading ({5:F2}, {6:F2}).",
                spawned, player.PlayerName, spawnX, spawnY, spawnZ, dirX, dirZ
            );
        }
        return spawned;
    }

    private int CountAliveOrnithopters()
    {
        // Count ambient flyovers only. Raid thopters share the same
        // entity code but lack AttrAmbientFlyover, so they don't gate
        // ambient-formation spawning.
        int count = 0;
        foreach (var e in sapi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive) continue;
            if (e.Code?.Domain != "vsdune" || e.Code?.Path != "ornithopter-raid") continue;
            if (!e.WatchedAttributes.GetBool(AttrAmbientFlyover, false)) continue;
            count++;
        }
        return count;
    }
}