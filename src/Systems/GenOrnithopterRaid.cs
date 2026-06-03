using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsDune;

public class GenOrnithopterRaid : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;


    private readonly Dictionary<long, RaidContext> activeRaids = new();


    private const float OutcomeNobodyChance = 0.30f;
    private const float OutcomeHarkOnlyChance = 0.40f;
    private const float OutcomeSmugglerOnlyChance = 0.12f;
    private const float OutcomeBothChance = 0.18f;
    private const float SecondFactionDelaySeconds = 35f;
    private const float HarkScanRadiusForBuggerOff = 90f;
    private const float HarkPairChance = 0.30f;
    private const int HarkSingleGroundCount = 4;   // 1 officer + 3 grunts
    private const int HarkPairGroundCount = 5;     // per bird; total 10 across two
    private const float HarkPairSecondBirdDelaySeconds = 8f;
    private const float SmugglerPairChance = 0.20f;
    private const int SmugglerSingleGroundCount = 4;
    private const int SmugglerPairGroundCount = 5;
    private const float SmugglerPairSecondBirdDelaySeconds = 8f;
    private const float SmugglerRetreatLeadSeconds = 8f;
    private const float ApproachDelaySeconds = 45f;
    private const double SpawnDistance = 150.0;       // horizontal start distance
    private const double SpawnAltitudeAbove = 80.0;   // above terrain at start
    private const double LandingRingMin = 14.0;       // not ON the spice sand
    private const double LandingRingMax = 22.0;       // not too far either
    private const double SecondFactionLandingRingMin = 80.0;
    private const double SecondFactionLandingRingMax = 140.0;
    private const int LandingFlatRadius = 4;
    private const int LandingMaxHeightDiff = 1;
    private const int LandingMaxTries = 24;
    private const float WormScanIntervalSeconds = 2.5f;
    private const double FleeOnWormRadius = 60.0;
    private const long WormBoardingWindowMs = 15000;
    private const double WormPanicRadius = 30.0;

    // Harvest tuning.
    private const float HarvestTickSeconds = 0.5f;
    private const double HarvestMotionPerTick = 0.04;
    private const double HarvestReachBlocks = 2.5;     // close enough to break
    private const double HarvestPauseRadius = 18.0;    // a player this close pauses harvest
    private const float HarvestMaxDurationSeconds = 300f; // safety cap (5 min)
    private const float HarvestMinDurationSeconds = 45f;
    private const string HarkonnenOfficerCode = "harkonnen-officer";
    private static readonly string[] HarkonnenGruntCodes = new[]
    {
        "harkonnen-soldier",
        "harkonnen-rifleman"
    };
    private const string SmugglerOfficerCode = "smuggler-officer";
    private static readonly string[] SmugglerGruntCodes = new[]
    {
        "smuggler-soldier",
        "smuggler-rifleman"
    };

    private const int SubSeed = 4421199;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        GenSpiceBlow.OnDetonate += OnSpiceBlowDetonate;
        api.Event.RegisterGameTickListener(OnWormScanTick, (int)(WormScanIntervalSeconds * 1000));
        api.Event.RegisterGameTickListener(OnHarvestTick, (int)(HarvestTickSeconds * 1000));

        api.ChatCommands.Create("thopterraid")
            .WithDescription("Force a thopter raid centered at your position.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnForceRaidCommand);
    }

    public override void Dispose()
    {
        GenSpiceBlow.OnDetonate -= OnSpiceBlowDetonate;
        base.Dispose();
    }

    private TextCommandResult OnForceRaidCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        // Admin command always fires a full both-factions arrival so
        // the tester sees the bugger-off check happen.
        var capturedCenter = new Vec3d(sp.Entity.Pos.X, sp.Entity.Pos.Y, sp.Entity.Pos.Z);
        ScheduleBothFactions(capturedCenter);
        return TextCommandResult.Success($"Thopter raid scheduled at your position. ETA ~{ApproachDelaySeconds:F0}s. Smuggler second-wave will bugger off if outnumbered.");
    }

    private void OnSpiceBlowDetonate(Vec3d center, float magnitude)
    {
        FactionChannels.Harkonen(sapi, "Anomaly registered. Encrypted vector withheld.");

        var capturedCenter = new Vec3d(center.X, center.Y, center.Z);
        float r = rnd.NextFloat();
        float t = OutcomeNobodyChance;
        if (r < t) return;
        t += OutcomeHarkOnlyChance;
        if (r < t) { ScheduleSingleFaction(capturedCenter, isHarkonen: true); return; }
        t += OutcomeSmugglerOnlyChance;
        if (r < t) { ScheduleSingleFaction(capturedCenter, isHarkonen: false); return; }
        ScheduleBothFactions(capturedCenter);
    }

    private void ScheduleSingleFaction(Vec3d center, bool isHarkonen)
    {
        if (isHarkonen) ScheduleHarkonenArrival(center, landFar: false, announceFirst: true);
        else ScheduleSmugglerArrival(center, landFar: false, announceFirst: true, retreatOnHark: false);
    }

    private void ScheduleBothFactions(Vec3d center)
    {
        bool smugglerFirst = rnd.NextFloat() < 0.5f;
        float secondDelay = ApproachDelaySeconds + SecondFactionDelaySeconds;
        if (smugglerFirst)
        {
            ScheduleSmugglerArrival(center, landFar: false, announceFirst: true, retreatOnHark: true);
            ScheduleHarkonenSecondWave(center, secondDelay);
        }
        else
        {
            ScheduleHarkonenArrival(center, landFar: false, announceFirst: true);
            sapi.Event.RegisterCallback((dt) =>
            {
                FactionChannels.Smuggler(sapi, "Smuggler vector approaching the crater.");
                StartRaid(center, isHarkonen: false, announce: false, landFar: true, buggerOffCheck: true, groundCount: SmugglerSingleGroundCount, withOfficer: true, withPilot: true);
            }, (int)(secondDelay * 1000));
        }
    }

    // Hark second wave (Smuggler was first). Smuggler retreat fires
    // SmugglerRetreatLeadSeconds before Hark lands.
    private void ScheduleHarkonenSecondWave(Vec3d center, float secondDelayAfterDetonate)
    {
        float retreatAt = secondDelayAfterDetonate - SmugglerRetreatLeadSeconds;
        sapi.Event.RegisterCallback((dt) => TriggerSmugglerRetreat(center), (int)(Math.Max(0f, retreatAt) * 1000));
        sapi.Event.RegisterCallback((dt) =>
        {
            FactionChannels.Harkonen(sapi, "Hark thopter inbound on the blow site.");
            ScheduleHarkonenArrival(center, landFar: false, announceFirst: false);
        }, (int)(secondDelayAfterDetonate * 1000));
    }

    // Find any active Smuggler raid near this spice center and tell
    // them to leave. Baron tolerates Smugglers only if they stay clear.
    private void TriggerSmugglerRetreat(Vec3d spiceCenter)
    {
        const double ScanRadiusSq = 200.0 * 200.0;
        foreach (var ctx in activeRaids.Values)
        {
            if (ctx.OfficerCode != SmugglerOfficerCode && ctx.GruntCodes != SmugglerGruntCodes) continue;
            double dx = ctx.SpiceCenter.X - spiceCenter.X;
            double dz = ctx.SpiceCenter.Z - spiceCenter.Z;
            if (dx * dx + dz * dz > ScanRadiusSq) continue;
            FactionChannels.Smuggler(sapi, "Hark vector inbound. We're out.");
            BeginLeaving(ctx, "Hark incoming");
        }
    }

    private void ScheduleHarkonenArrival(Vec3d center, bool landFar, bool announceFirst)
    {
        bool pair = rnd.NextFloat() < HarkPairChance;
        if (!pair)
        {
            sapi.Event.RegisterCallback((dt) => StartRaid(center, isHarkonen: true, announce: announceFirst, landFar: landFar, buggerOffCheck: false, groundCount: HarkSingleGroundCount, withOfficer: true, withPilot: true), (int)(ApproachDelaySeconds * 1000));
            return;
        }
        sapi.Event.RegisterCallback((dt) => StartRaid(center, isHarkonen: true, announce: announceFirst, landFar: landFar, buggerOffCheck: false, groundCount: HarkPairGroundCount, withOfficer: true, withPilot: true), (int)(ApproachDelaySeconds * 1000));
        sapi.Event.RegisterCallback((dt) => StartRaid(center, isHarkonen: true, announce: false, landFar: true, buggerOffCheck: false, groundCount: HarkPairGroundCount, withOfficer: false, withPilot: true), (int)((ApproachDelaySeconds + HarkPairSecondBirdDelaySeconds) * 1000));
    }

    private void ScheduleSmugglerArrival(Vec3d center, bool landFar, bool announceFirst, bool retreatOnHark)
    {
        if (announceFirst) FactionChannels.Smuggler(sapi, "Vector locked, headed for the blow. Keep it quick.");
        bool pair = rnd.NextFloat() < SmugglerPairChance;
        if (!pair)
        {
            sapi.Event.RegisterCallback((dt) => StartRaid(center, isHarkonen: false, announce: announceFirst, landFar: landFar, buggerOffCheck: false, groundCount: SmugglerSingleGroundCount, withOfficer: true, withPilot: true), (int)(ApproachDelaySeconds * 1000));
            return;
        }
        sapi.Event.RegisterCallback((dt) => StartRaid(center, isHarkonen: false, announce: announceFirst, landFar: landFar, buggerOffCheck: false, groundCount: SmugglerPairGroundCount, withOfficer: true, withPilot: true), (int)(ApproachDelaySeconds * 1000));
        sapi.Event.RegisterCallback((dt) => StartRaid(center, isHarkonen: false, announce: false, landFar: true, buggerOffCheck: false, groundCount: SmugglerPairGroundCount, withOfficer: false, withPilot: true), (int)((ApproachDelaySeconds + SmugglerPairSecondBirdDelaySeconds) * 1000));
    }

    // Cinematic entry: scripted convergence beat. /vsdune cinematic
    // calls this for Hark close + Smuggler far.
    public void DispatchScriptedRaid(Vec3d center, bool isHarkonen, bool landFar, float delaySeconds, bool announce)
    {
        var captured = new Vec3d(center.X, center.Y, center.Z);
        int groundCount = isHarkonen ? HarkSingleGroundCount : SmugglerSingleGroundCount;
        sapi.Event.RegisterCallback((dt) => StartRaid(captured, isHarkonen, announce: announce, landFar: landFar, buggerOffCheck: false, groundCount: groundCount, withOfficer: true, withPilot: true), (int)(delaySeconds * 1000));
    }

    private void StartRaid(Vec3d spiceCenter, bool isHarkonen, bool announce, bool landFar, bool buggerOffCheck, int groundCount, bool withOfficer, bool withPilot)
    {
        // Faction is decided by the caller so the OnSpiceBlowDetonate
        // second-faction roll can pick the opposite of the primary.
        string thopterCode = isHarkonen ? "ornithopter-raid" : "ornithopter";
        string factionName = isHarkonen ? "Harkonen" : "Smugglers";

        var entityType = sapi.World.GetEntityType(new AssetLocation("vsdune", thopterCode));
        if (entityType == null)
        {
            sapi.Logger.Warning("[VSDune] GenOrnithopterRaid: vsdune:{0} entity not registered.", thopterCode);
            return;
        }

        // Find a flat sandy landing spot. Second-faction raids land
        // far so they read as a separate arrival converging on the spice.
        Vec3d landingSpot = FindLandingSpot(spiceCenter, landFar);
        if (landingSpot == null) return;

        double bearing = rnd.NextDouble() * Math.PI * 2;
        double sx = landingSpot.X - Math.Cos(bearing) * SpawnDistance;
        double sz = landingSpot.Z - Math.Sin(bearing) * SpawnDistance;
        double sy = landingSpot.Y + SpawnAltitudeAbove;

        var entity = sapi.World.ClassRegistry.CreateEntity(entityType);
        if (entity == null)
        {
            sapi.Logger.Warning("[VSDune] StartRaid: CreateEntity returned null for vsdune:{0}", thopterCode);
            return;
        }

        // SpawnEntity reinitializes the entity, so position and
        // WatchedAttributes must be set AFTER the call.
        EntityBehaviorOrnithopterFlight.SetApproachTarget(entity, landingSpot);
        sapi.World.SpawnEntity(entity);
        entity.Pos.SetPos(sx, sy, sz);

        if (announce)
        {
            sapi.BroadcastMessageToAllGroups(
                string.Format(
                    "[Observation Network] Spice blow registered at ({0:F0}, {1:F0}, {2:F0}). Dispatching ground team.",
                    spiceCenter.X, spiceCenter.Y, spiceCenter.Z
                ),
                EnumChatType.Notification
            );
        }

        var ctx = new RaidContext
        {
            ThopterId = entity.EntityId,
            LandingSpot = landingSpot,
            SpiceCenter = spiceCenter,
            Disembarked = false,
            BuggerOffCheck = buggerOffCheck,
            GroundSquadCount = groundCount,
            WithOfficer = withOfficer && isHarkonen,
            WithPilotPassenger = withPilot && isHarkonen,
            OfficerCode = isHarkonen ? HarkonnenOfficerCode : SmugglerOfficerCode,
            GruntCodes = isHarkonen ? HarkonnenGruntCodes : SmugglerGruntCodes
        };
        activeRaids[entity.EntityId] = ctx;

        entity.WatchedAttributes.RegisterModifiedListener(EntityBehaviorOrnithopterFlight.AttrMode, () => OnThopterModeChanged(ctx.ThopterId));

        if (ctx.WithPilotPassenger) DeferSpawnAndMountPilot(entity, ctx);
    }

    // Pilot rides the entire raid and is killable through the cockpit
    // selectionbox. Drops follow the grunt's normal table.
    private void DeferSpawnAndMountPilot(Entity thopter, RaidContext ctx)
    {
        long thopterId = thopter.EntityId;
        sapi.Event.RegisterCallback((dt) => TrySpawnPilot(thopterId, ctx, attemptsRemaining: 3), 250);
    }

    private void TrySpawnPilot(long thopterId, RaidContext ctx, int attemptsRemaining)
    {
        var thopter = sapi.World.GetEntityById(thopterId);
        if (thopter == null || !thopter.Alive) return;

        var seatable = thopter.GetBehavior<EntityBehaviorSeatable>();
        IMountableSeat pilotSeat = null;
        if (seatable?.Seats != null)
        {
            foreach (var seat in seatable.Seats)
            {
                if (seat != null && seat.CanControl) { pilotSeat = seat; break; }
            }
        }
        if (pilotSeat == null)
        {
            if (attemptsRemaining > 0)
            {
                sapi.Event.RegisterCallback((dt) => TrySpawnPilot(thopterId, ctx, attemptsRemaining - 1), 250);
            }
            return;
        }

        string code = ctx.GruntCodes[rnd.NextInt(ctx.GruntCodes.Length)];
        var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
        if (bType == null) return;
        var pilot = sapi.World.ClassRegistry.CreateEntity(bType);
        if (pilot is not EntityAgent agent) return;

        agent.Pos.SetPos(thopter.Pos.X, thopter.Pos.Y, thopter.Pos.Z);
        agent.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);
        sapi.World.SpawnEntity(agent);

        if (!agent.TryMount(pilotSeat))
        {
            // Mount still failed: despawn the pilot so it doesn't fall
            // out of the sky as a free-floating grunt.
            sapi.Logger.Warning("[VSDune] TrySpawnPilot: TryMount failed for {0} onto thopter {1}, despawning pilot.", code, thopter.EntityId);
            agent.Die(EnumDespawnReason.Removed);
            return;
        }
        ctx.OutlawIds.Add(agent.EntityId);
    }

    private void OnThopterModeChanged(long thopterId)
    {
        if (!activeRaids.TryGetValue(thopterId, out var ctx)) return;
        var thopter = sapi.World.GetEntityById(thopterId);
        if (thopter == null || !thopter.Alive)
        {
            activeRaids.Remove(thopterId);
            return;
        }

        string mode = thopter.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, "");
        // Disembark only at ModeLanded so we don't drop mid-anim.
        if (mode == EntityBehaviorOrnithopterFlight.ModeLanded && !ctx.Disembarked)
        {
            if (ctx.BuggerOffCheck)
            {
                // Smuggler second-wave: count Hark on the ground. If
                // we don't outnumber them, take off without dropping.
                int harkCount = CountAliveHarkNear(ctx.SpiceCenter, HarkScanRadiusForBuggerOff);
                int intendedSmugglerCount = (ctx.WithOfficer && ctx.OfficerCode != null ? 1 : 0) + ctx.GroundSquadCount;
                if (intendedSmugglerCount <= harkCount)
                {
                    FactionChannels.Smuggler(sapi, "Hark's deep here. Scrubbing approach.");
                    sapi.Logger.Notification("[VSDune] Smuggler bugger-off: intended {0} vs hark {1} at ({2:F0}, _, {3:F0}).",
                        intendedSmugglerCount, harkCount, ctx.SpiceCenter.X, ctx.SpiceCenter.Z);
                    var flee = thopter.Pos.XYZ.SubCopy(ctx.SpiceCenter).Normalize();
                    flee.Y = 0.3;
                    EntityBehaviorOrnithopterFlight.BeginTakeoffThenFlee(thopter, flee, 14f);
                    activeRaids.Remove(thopterId);
                    return;
                }
            }
            ctx.Disembarked = true;
            SpawnGroundOutlaws(ctx);
        }
        else if (mode == EntityBehaviorOrnithopterFlight.ModeFleeing)
        {
            // Thopter will despawn on its own.
            activeRaids.Remove(thopterId);
        }
    }

    private int CountAliveHarkNear(Vec3d center, float radius)
    {
        var hits = sapi.World.GetEntitiesAround(center, radius, radius, (e) =>
        {
            if (e == null || !e.Alive) return false;
            if (e is not EntityAgent) return false;
            string path = e.Code?.Path;
            return path != null && path.StartsWith("harkonnen-");
        });
        return hits.Length;
    }

    private void OnHarvestTick(float dt)
    {
        if (activeRaids.Count == 0) return;

        // Snapshot the keys so we can mutate activeRaids inside the
        // loop (Leaving transitions remove entries).
        var ids = new List<long>(activeRaids.Keys);
        foreach (var thopterId in ids)
        {
            if (!activeRaids.TryGetValue(thopterId, out var ctx)) continue;
            if (ctx.Stage != RaidStage.Harvesting) continue;

            TickHarvestForRaid(ctx);
        }
    }

    private void TickHarvestForRaid(RaidContext ctx)
    {
        double now = sapi.World.ElapsedMilliseconds / 1000.0;
        if (now - ctx.HarvestStartedSec > HarvestMaxDurationSeconds)
        {
            BeginLeaving(ctx, "timeout");
            return;
        }

        // Count surviving outlaws. If they're all dead, the thopter
        // takes off empty (the bird was just a delivery vehicle).
        int alive = 0;
        foreach (var oid in ctx.OutlawIds)
        {
            var oe = sapi.World.GetEntityById(oid);
            if (oe != null && oe.Alive) alive++;
        }
        if (alive == 0)
        {
            BeginLeaving(ctx, "all outlaws killed");
            return;
        }

        var nextSpice = FindClosestSpicesand(ctx.SpiceCenter, ctx.CraterRadius);
        if (nextSpice == null)
        {
            if (now - ctx.HarvestStartedSec >= HarvestMinDurationSeconds)
            {
                BeginLeaving(ctx, "spice crater fully harvested");
            }
            return;
        }

        foreach (var oid in ctx.OutlawIds)
        {
            var oe = sapi.World.GetEntityById(oid);
            if (oe == null || !oe.Alive) continue;
            if (oe is not EntityAgent outlaw) continue;

            if (PlayerNearby(outlaw.Pos.XYZ, HarvestPauseRadius)) continue;

            // Each outlaw heads to its own nearest spicesand block so
            // they spread across the crater rather than dogpile one.
            var spice = FindClosestSpicesandTo(outlaw.Pos.XYZ, ctx.SpiceCenter, ctx.CraterRadius);
            if (spice == null) continue;

            double dx = (spice.X + 0.5) - outlaw.Pos.X;
            double dz = (spice.Z + 0.5) - outlaw.Pos.Z;
            double dist = Math.Sqrt(dx * dx + dz * dz);

            if (dist <= HarvestReachBlocks)
            {
                // SetBlock(0, ...) wipes to air without firing vanilla
                // OnBlockBroken drops. Outlaws shouldn't leave loot.
                sapi.World.BlockAccessor.SetBlock(0, spice);
                continue;
            }

            if (dist > 0.001)
            {
                double inv = 1.0 / dist;
                outlaw.Pos.Motion.X += dx * inv * HarvestMotionPerTick;
                outlaw.Pos.Motion.Z += dz * inv * HarvestMotionPerTick;
                outlaw.Pos.Yaw = (float)Math.Atan2(dx, dz);
            }
        }
    }

    private BlockPos FindClosestSpicesand(Vec3d center, int radius)
    {
        return FindClosestSpicesandTo(center, center, radius);
    }

    private BlockPos FindClosestSpicesandTo(Vec3d from, Vec3d craterCenter, int radius)
    {

        BlockPos best = null;
        double bestDsq = double.MaxValue;

        var ba = sapi.World.BlockAccessor;
        int cx = (int)craterCenter.X;
        int cz = (int)craterCenter.Z;

        var probe = new BlockPos(Dimensions.NormalWorld);
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (dx * dx + dz * dz > radius * radius) continue;
                int wx = cx + dx;
                int wz = cz + dz;
                int wy = ba.GetRainMapHeightAt(new BlockPos(wx, 0, wz));

                for (int wyProbe = wy; wyProbe >= wy - 6 && wyProbe >= 1; wyProbe--)
                {
                    probe.Set(wx, wyProbe, wz);
                    var b = ba.GetBlock(probe);
                    if (b?.Code?.Path != null && b.Code.Path.StartsWith("spicesand"))
                    {
                        double pdx = (wx + 0.5) - from.X;
                        double pdz = (wz + 0.5) - from.Z;
                        double dsq = pdx * pdx + pdz * pdz;
                        if (dsq < bestDsq)
                        {
                            bestDsq = dsq;
                            best = new BlockPos(wx, wyProbe, wz);
                        }
                        break; // one spice per column; move on
                    }
                }
            }
        }
        return best;
    }

    private bool PlayerNearby(Vec3d pos, double radius)
    {
        double rsq = radius * radius;
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p?.Entity == null) continue;
            double pdx = p.Entity.Pos.X - pos.X;
            double pdz = p.Entity.Pos.Z - pos.Z;
            if (pdx * pdx + pdz * pdz <= rsq) return true;
        }
        return false;
    }

    private void BeginLeaving(RaidContext ctx, string reason)
    {
        var thopter = sapi.World.GetEntityById(ctx.ThopterId);
        if (thopter == null || !thopter.Alive)
        {
            activeRaids.Remove(ctx.ThopterId);
            return;
        }

        ctx.Stage = RaidStage.Leaving;

        // "Mount up": despawn surviving outlaws at the thopter. Visual
        // approximation; a full walk-to-seat sequence is future work.
        foreach (var oid in ctx.OutlawIds)
        {
            var oe = sapi.World.GetEntityById(oid);
            if (oe != null && oe.Alive)
            {
                oe.Die(EnumDespawnReason.Removed);
            }
        }


        double dx = thopter.Pos.X - ctx.SpiceCenter.X;
        double dz = thopter.Pos.Z - ctx.SpiceCenter.Z;
        double hlen = Math.Sqrt(dx * dx + dz * dz);
        if (hlen < 0.01) { dx = 1; dz = 0; hlen = 1; }
        double inv = 1.0 / hlen;
        var fleeDir = new Vec3d(dx * inv * 0.7, 0.6, dz * inv * 0.7);

        // Full takeoff anim plays before the flight behavior switches
        // to fleeing using the direction we just pre-set.
        EntityBehaviorOrnithopterFlight.BeginTakeoffThenFlee(thopter, fleeDir);
    }

    private void SpawnGroundOutlaws(RaidContext ctx)
    {
        // First slot is the officer if this bird carries one; rest are
        // grunts. Pilot in seat is separate from this ground squad.
        for (int i = 0; i < ctx.GroundSquadCount; i++)
        {
            string code = (i == 0 && ctx.WithOfficer && ctx.OfficerCode != null)
                ? ctx.OfficerCode
                : ctx.GruntCodes[rnd.NextInt(ctx.GruntCodes.Length)];

            SpawnOneOutlaw(ctx, code);
        }

        ctx.HarvestStartedSec = sapi.World.ElapsedMilliseconds / 1000.0;
        ctx.Stage = RaidStage.Harvesting;
    }

    private void SpawnOneOutlaw(RaidContext ctx, string code)
    {
        var bType = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
        if (bType == null)
        {
            sapi.Logger.Warning("[VSDune] SpawnGroundOutlaws: entity type vsdune:{0} not found, skipping.", code);
            return;
        }
        var outlaw = sapi.World.ClassRegistry.CreateEntity(bType);
        if (outlaw == null) return;

        double angle = rnd.NextDouble() * Math.PI * 2;
        double dist = 2 + rnd.NextDouble() * 3;
        double ox = ctx.LandingSpot.X + Math.Cos(angle) * dist;
        double oz = ctx.LandingSpot.Z + Math.Sin(angle) * dist;
        int oy = sapi.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)ox, 0, (int)oz));

        outlaw.Pos.SetPos(ox, oy + 1, oz);
        outlaw.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);

        sapi.World.SpawnEntity(outlaw);

        ctx.OutlawIds.Add(outlaw.EntityId);
    }

    private void OnWormScanTick(float dt)
    {
        if (activeRaids.Count == 0) return;

        var toFlee = new List<long>();
        foreach (var kv in activeRaids)
        {
            var thopter = sapi.World.GetEntityById(kv.Key);
            if (thopter == null || !thopter.Alive) continue;

            bool wormNearby = false;
            Vec3d wormPos = null;
            double thopterX = thopter.Pos.X, thopterZ = thopter.Pos.Z;
            var nearWorms = sapi.World.GetEntitiesAround(thopter.Pos.XYZ, (float)FleeOnWormRadius, 200f, (e) =>
            {
                if (e is not VertwormEntity || !e.Alive) return false;
                double dxw = e.Pos.X - thopterX;
                double dzw = e.Pos.Z - thopterZ;
                return dxw * dxw + dzw * dzw <= FleeOnWormRadius * FleeOnWormRadius;
            });
            if (nearWorms.Length > 0)
            {
                wormNearby = true;
                wormPos = nearWorms[0].Pos.XYZ;
            }
            if (!wormNearby)
            {
                kv.Value.WormFleeArmedMs = 0;
                continue;
            }

            long nowMs = sapi.World.ElapsedMilliseconds;
            double dx = thopter.Pos.X - wormPos.X;
            double dz = thopter.Pos.Z - wormPos.Z;
            double hlen = Math.Sqrt(dx * dx + dz * dz);
            bool wormInPanicRange = hlen <= WormPanicRadius;

            // First detection: arm the timer and broadcast. Don't take
            // off yet so outlaws can board.
            if (kv.Value.WormFleeArmedMs == 0)
            {
                kv.Value.WormFleeArmedMs = nowMs;
                FactionChannels.Observation(sapi, "Wormsign at the crater. Squads, board.");
                if (!wormInPanicRange) continue;
            }

            long elapsed = nowMs - kv.Value.WormFleeArmedMs;
            if (!wormInPanicRange && elapsed < WormBoardingWindowMs) continue;

            // Boarding window elapsed or worm is on us: take off now.
            if (hlen < 0.01) { dx = 1; dz = 0; hlen = 1; }
            double inv = 1.0 / hlen;
            var fleeDir = new Vec3d(dx * inv * 0.7, 0.6, dz * inv * 0.7);
            EntityBehaviorOrnithopterFlight.BeginTakeoffThenFlee(thopter, fleeDir);
            toFlee.Add(kv.Key);
        }

        foreach (var id in toFlee) activeRaids.Remove(id);
    }

    private Vec3d FindLandingSpot(Vec3d spiceCenter, bool landFar = false)
    {
        var probe = new BlockPos(Dimensions.NormalWorld);
        var ba = sapi.World.BlockAccessor;

        double ringMin = landFar ? SecondFactionLandingRingMin : LandingRingMin;
        double ringMax = landFar ? SecondFactionLandingRingMax : LandingRingMax;

        for (int i = 0; i < LandingMaxTries; i++)
        {
            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = ringMin + rnd.NextDouble() * (ringMax - ringMin);
            double tryX = spiceCenter.X + Math.Cos(angle) * dist;
            double tryZ = spiceCenter.Z + Math.Sin(angle) * dist;
            probe.Set((int)tryX, 0, (int)tryZ);
            int centerY = ba.GetRainMapHeightAt(probe);

            bool flat = true;
            for (int dx = -LandingFlatRadius; dx <= LandingFlatRadius && flat; dx++)
            {
                for (int dz = -LandingFlatRadius; dz <= LandingFlatRadius && flat; dz++)
                {
                    probe.Set((int)tryX + dx, 0, (int)tryZ + dz);
                    int probeY = ba.GetRainMapHeightAt(probe);
                    if (Math.Abs(probeY - centerY) > LandingMaxHeightDiff) flat = false;
                }
            }
            if (!flat) continue;

            // Bed must be sand AND must NOT be spicesand: the thopter
            // lands next to the crater, not on it.
            probe.Set((int)tryX, centerY, (int)tryZ);
            var bedBlock = ba.GetBlock(probe);
            if (bedBlock == null) continue;
            if (bedBlock.BlockMaterial != EnumBlockMaterial.Sand) continue;
            if (bedBlock.Code?.Path?.Contains("spicesand") == true) continue;

            return new Vec3d(tryX, centerY + 1, tryZ);
        }
        return null;
    }

    private enum RaidStage
    {
        Inbound,     // thopter is flying in / landing
        Harvesting,  // outlaws are walking around breaking spicesand
        Leaving,     // outlaws despawned, thopter takeoff -> flee in progress
    }

    private class RaidContext
    {
        public long ThopterId;
        public Vec3d LandingSpot;
        public Vec3d SpiceCenter;
        public bool Disembarked;
        public bool BuggerOffCheck;
        public int GroundSquadCount;
        public bool WithOfficer;
        public bool WithPilotPassenger;
        public long WormFleeArmedMs;
        public RaidStage Stage = RaidStage.Inbound;
        public double HarvestStartedSec;
        public int CraterRadius = 32;
        public string OfficerCode;
        public string[] GruntCodes;
        public List<long> OutlawIds = new();
    }
}