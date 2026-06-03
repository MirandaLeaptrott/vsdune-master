using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

// Worm threat lifecycle
public class GenVertwormScare : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private long activeWormId = -1;
    private double cooldownUntilSeconds = 0;
    private WormThreat activeThreat = null;

    public IServerNetworkChannel ShakeChannel { get; private set; }

    public const string ShakeChannelName = "vsdune.vertwormshake";

    private const float PollIntervalSeconds = 30f;
    private const float ScareChancePerPoll = 0.6f;
    private const int SpawnDistance = 70;
    private const float ScareCooldownSeconds = 240f;

    private const float WormFuseSeconds = 420f;
    private const float Callout5MinAtRemainingS = 300f;
    private const float Callout3MinAtRemainingS = 180f;
    private const float Callout1MinAtRemainingS = 60f;

    private const float ThumperChance = 0.6f;
    private const float ThumperPushMinS = 420f;
    private const float ThumperPushMaxS = 600f;

    private const int SubSeed = 8133;

    private class WormThreat
    {
        public IServerPlayer TargetPlayer;
        public double SpawnAtMs;
        public bool Fired5Min;
        public bool Fired3Min;
        public bool Fired1Min;
        public bool ThumperRolled;
    }

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        ShakeChannel = api.Network.RegisterChannel(ShakeChannelName)
            .RegisterMessageType<ScreenShakePacket>();

        api.Event.RegisterGameTickListener(OnPoll, (int)(PollIntervalSeconds * 1000));
        api.Event.RegisterGameTickListener(OnThreatTick, 1000);

        // /vertworm: spawn immediately, skip fuse + cooldown + ocean.
        api.ChatCommands.Create("vertworm")
            .WithDescription("Spawn a vertworm scare immediately (skips the 7 min fuse). Subcommands: status, reset.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnVertwormCommand)
            .BeginSubCommand("status").HandleWith(OnVertwormStatus).EndSubCommand()
            .BeginSubCommand("reset").HandleWith(OnVertwormReset).EndSubCommand();
    }

    private TextCommandResult OnVertwormStatus(TextCommandCallingArgs args)
    {
        double now = sapi.World.ElapsedMilliseconds / 1000.0;
        double cooldownLeft = System.Math.Max(0, cooldownUntilSeconds - now);

        string activeMsg;
        if (activeWormId < 0)
        {
            activeMsg = "none";
        }
        else
        {
            var ent = sapi.World.GetEntityById(activeWormId);
            if (ent == null) activeMsg = $"id={activeWormId} (entity missing, will clear on next poll)";
            else if (!ent.Alive) activeMsg = $"id={activeWormId} (dead, will clear on next poll)";
            else activeMsg = $"id={activeWormId} (alive, blocks new spawns)";
        }

        string threatMsg = "none";
        if (activeThreat != null)
        {
            double remaining = (activeThreat.SpawnAtMs - sapi.World.ElapsedMilliseconds) / 1000.0;
            threatMsg = $"player={activeThreat.TargetPlayer?.PlayerName ?? "?"}, fuseLeft={remaining:F0}s, thumperRolled={activeThreat.ThumperRolled}";
        }

        string playerOcean = "n/a";
        if (args.Caller.Player is IServerPlayer sp && sp.Entity != null)
        {
            int playerY = sp.Entity.Pos.AsBlockPos.Y;
            int sealevel = sapi.World.SeaLevel;
            playerOcean = $"playerY={playerY}, sealevel={sealevel}, onOcean={IsOnOceanSand(sp)}";
        }

        return TextCommandResult.Success(
            $"Vertworm status: active={activeMsg}, threat={threatMsg}, cooldownLeft={cooldownLeft:F0}s, {playerOcean}"
        );
    }

    private TextCommandResult OnVertwormReset(TextCommandCallingArgs args)
    {
        if (activeWormId >= 0)
        {
            var ent = sapi.World.GetEntityById(activeWormId);
            if (ent != null && ent.Alive)
            {
                ent.Die(EnumDespawnReason.Removed);
            }
        }
        activeWormId = -1;
        cooldownUntilSeconds = 0;
        activeThreat = null;
        return TextCommandResult.Success("Vertworm state cleared. Next poll can spawn.");
    }

    private TextCommandResult OnVertwormCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        // Clear any pending threat so the admin spawn doesn't race with
        // an in-flight wormsign countdown.
        activeThreat = null;
        SpawnFor(sp, bypassCooldown: true);
        return TextCommandResult.Success("Vertworm scare seeded.");
    }

    private void OnPoll(float dt)
    {
        if (activeWormId >= 0)
        {
            var ent = sapi.World.GetEntityById(activeWormId);
            if (ent == null || !ent.Alive)
            {
                activeWormId = -1;
                cooldownUntilSeconds = sapi.World.ElapsedMilliseconds / 1000.0 + ScareCooldownSeconds;
            }
            else
            {
                return;
            }
        }
        if (activeThreat != null) return;

        double now = sapi.World.ElapsedMilliseconds / 1000.0;
        if (now < cooldownUntilSeconds) return;

        if (rnd.NextFloat() > ScareChancePerPoll) return;

        var players = sapi.World.AllOnlinePlayers;
        if (players.Length == 0) return;

        var picked = players[rnd.NextInt(players.Length)] as IServerPlayer;
        if (picked?.Entity == null) return;

        if (!IsOnOceanSand(picked)) return;

        activeThreat = new WormThreat
        {
            TargetPlayer = picked,
            SpawnAtMs = sapi.World.ElapsedMilliseconds + WormFuseSeconds * 1000.0,
        };

        // Generic seeding callout always fires for the player. Detailed
        // 5/3/1 callouts in OnThreatTick are gated on faction presence
        // since the network only chatters when its own guys are around.
        FactionChannels.Observation(sapi, $"We have wormsign near {picked.PlayerName}. Approximately {WormFuseSeconds / 60f:F0} minutes.");
        sapi.Logger.Notification("[VSDune] Wormsign threat seeded for {0}, fuse {1:F0}s.", picked.PlayerName, WormFuseSeconds);
    }

    private void OnThreatTick(float dt)
    {
        if (activeThreat == null) return;

        if (activeThreat.TargetPlayer?.Entity == null ||
            activeThreat.TargetPlayer.ConnectionState != EnumClientState.Playing)
        {
            sapi.Logger.Notification("[VSDune] Wormsign threat dropped: target player offline.");
            activeThreat = null;
            return;
        }

        double nowMs = sapi.World.ElapsedMilliseconds;
        double remainingS = (activeThreat.SpawnAtMs - nowMs) / 1000.0;

        // 5/3/1 callouts only fire when factions are on the ground.
        // AnyAlive is an O(N) loaded-entity scan, so don't call it on
        // every tick: gate it behind the un-fired flag checks first.
        if (!activeThreat.Fired5Min && remainingS <= Callout5MinAtRemainingS)
        {
            activeThreat.Fired5Min = true;
            bool smug = FactionChannels.AnyAlive(sapi, "smuggler-");
            if (smug || FactionChannels.AnyAlive(sapi, "harkonnen-"))
            {
                FactionChannels.Observation(sapi, "Wormsign closing, 5 minutes out.");
                if (smug) FactionChannels.Smuggler(sapi, "Wormsign on the scope, five minutes. Move it.");
            }
        }

        if (!activeThreat.Fired3Min && remainingS <= Callout3MinAtRemainingS)
        {
            activeThreat.Fired3Min = true;
            bool smug = FactionChannels.AnyAlive(sapi, "smuggler-");
            if (smug || FactionChannels.AnyAlive(sapi, "harkonnen-"))
            {
                FactionChannels.Observation(sapi, "Wormsign closing, 3 minutes out.");
                if (smug) FactionChannels.Smuggler(sapi, "Three minutes. Wrap it up.");
            }
            if (!activeThreat.ThumperRolled)
            {
                activeThreat.ThumperRolled = true;
                TryRollThumper(activeThreat);
            }
        }

        if (!activeThreat.Fired1Min && remainingS <= Callout1MinAtRemainingS)
        {
            activeThreat.Fired1Min = true;
            bool smug = FactionChannels.AnyAlive(sapi, "smuggler-");
            if (smug || FactionChannels.AnyAlive(sapi, "harkonnen-"))
            {
                FactionChannels.Observation(sapi, "Wormsign imminent, 1 minute out.");
                if (smug) FactionChannels.Smuggler(sapi, "One minute. Get on the bird NOW.");
            }
        }

        if (remainingS <= 0)
        {
            var target = activeThreat.TargetPlayer;
            activeThreat = null;
            // Confirm only if target still on basin. Player who walked
            // off in time gets the "wormsign lost" payoff instead.
            if (target?.Entity != null && IsOnOceanSand(target))
            {
                FactionChannels.Observation(sapi, "Wormsign confirmed.");
                SpawnFor(target, bypassCooldown: false);
            }
            else
            {
                FactionChannels.Observation(sapi, "Wormsign lost. Target cleared the basin.");
            }
        }
    }

    private void TryRollThumper(WormThreat threat)
    {
        // Smugglers run the thumper play, and only when no Hark is on
        // the ground to call them off. Hark never deploys one (they'd
        // rather lose the operation than risk standard gear).
        bool smugPresent = FactionChannels.AnyAlive(sapi, "smuggler-");
        if (!smugPresent) return;
        if (FactionChannels.AnyAlive(sapi, "harkonnen-")) return;
        if (rnd.NextFloat() > ThumperChance) return;

        double pushS = ThumperPushMinS + rnd.NextDouble() * (ThumperPushMaxS - ThumperPushMinS);
        threat.SpawnAtMs += pushS * 1000.0;

        // Reset callout flags so the new countdown's marks fire fresh.
        threat.Fired5Min = false;
        threat.Fired3Min = false;
        threat.Fired1Min = false;

        FactionChannels.Smuggler(sapi, "Copy that Observation, we're dropping a Thumper to draw it away.");
        sapi.Logger.Notification("[VSDune] Thumper diversion fired (smuggler solo), worm pushed {0:F0}s.", pushS);
    }

    // Spawn a vertworm at a fixed surface position. Thumper-driven
    // summon path; basin-sand gate is enforced at placement, not here.
    public void SpawnWormAt(Vec3d pos)
    {
        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", "vertworm"));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] SpawnWormAt: vsdune:vertworm entity type not registered.");
            return;
        }
        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
        if (entity == null) return;

        double sy = sapi.World.SeaLevel - 5;
        entity.Pos.SetPos(pos.X, sy, pos.Z);
        sapi.World.SpawnEntity(entity);
        activeWormId = entity.EntityId;

        var shakePacket = new ScreenShakePacket { DurationMs = 3500, Intensity = 0.7f };
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;
            double pdx = sp.Entity.Pos.X - pos.X;
            double pdz = sp.Entity.Pos.Z - pos.Z;
            if (pdx * pdx + pdz * pdz <= 200 * 200) ShakeChannel.SendPacket(shakePacket, sp);
        }
        sapi.Logger.Notification("[VSDune] Thumper summon: worm spawned at ({0:F0}, _, {1:F0}).", pos.X, pos.Z);
    }

    private void SpawnFor(IServerPlayer player, bool bypassCooldown)
    {
        if (player?.Entity == null) return;

        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", "vertworm"));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] GenVertwormScare: vsdune:vertworm entity type not registered.");
            return;
        }

        const int MaxSpawnTries = 8;
        double sx = 0, sz = 0;
        bool foundBasin = false;
        for (int i = 0; i < MaxSpawnTries && !foundBasin; i++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double tryX = player.Entity.Pos.X + Math.Cos(angle) * SpawnDistance;
            double tryZ = player.Entity.Pos.Z + Math.Sin(angle) * SpawnDistance;
            if (ColumnIsBasinSand((int)tryX, (int)tryZ))
            {
                sx = tryX;
                sz = tryZ;
                foundBasin = true;
            }
        }
        if (!foundBasin)
        {
            sapi.Logger.Notification("[VSDune] Vertworm scare for {0} skipped: no basin column within {1} blocks in {2} tries.", player.PlayerName, SpawnDistance, MaxSpawnTries);
            return;
        }
        double sy = sapi.World.SeaLevel - 5;

        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
        if (entity == null)
        {
            sapi.Logger.Warning("[VSDune] GenVertwormScare: failed to create entity instance.");
            return;
        }

        entity.Pos.SetPos(sx, sy, sz);
        sapi.World.SpawnEntity(entity);

        activeWormId = entity.EntityId;
        if (!bypassCooldown)
        {
            cooldownUntilSeconds = sapi.World.ElapsedMilliseconds / 1000.0 + ScareCooldownSeconds;
        }

        // Tell every player within range to shake.
        var shakePacket = new ScreenShakePacket { DurationMs = 3500, Intensity = 0.7f };
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;
            double pdx = sp.Entity.Pos.X - sx;
            double pdz = sp.Entity.Pos.Z - sz;
            if (pdx * pdx + pdz * pdz <= 200 * 200)
            {
                ShakeChannel.SendPacket(shakePacket, sp);
            }
        }

        sapi.Logger.Notification("[VSDune] Vertworm scare seeded at ({0:F0}, {1:F0}, {2:F0}) for {3}.", sx, sy, sz, player.PlayerName);
    }

    private bool IsOnOceanSand(IServerPlayer player)
    {
        if (player?.Entity == null) return false;
        var pos = player.Entity.Pos.AsBlockPos;
        return ColumnIsBasinSand(pos.X, pos.Z);
    }

    private bool ColumnIsBasinSand(int x, int z)
    {
        int sealevel = sapi.World.SeaLevel;
        var probe = new BlockPos(0);
        int depth = 0;
        for (int y = sealevel; y > sealevel - 30; y--)
        {
            probe.Set(x, y, z);
            var b = sapi.World.BlockAccessor.GetBlock(probe);
            if (b == null) break;
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= 8) return true;
        }
        return false;
    }
}

[ProtoBuf.ProtoContract]
public class ScreenShakePacket
{
    [ProtoBuf.ProtoMember(1)]
    public int DurationMs;
    [ProtoBuf.ProtoMember(2)]
    public float Intensity;
}
