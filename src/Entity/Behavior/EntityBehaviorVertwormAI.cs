using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;


public class EntityBehaviorVertwormAI : EntityBehavior
{
    private enum State { Emerging, Roaring, Sinking, Traveling, Hunting, Eating, Despawning }

    private enum HuntPhase
    {
        Dive,             // surface to deep
        ApproachToRoar,   // tunnel until within RoarRange of target
        RoarRise,         // rise to surface peak
        RoarHold,         // peak with mouthopen
        RoarSink,         // back to deep
        ApproachToAttack, // tunnel until within AttackRange
        AttackRise,       // rise to surface peak for horizontal lunge
        AttackLunge,      // forward motion + damage along the worm path
    }

    private State state = State.Emerging;
    private float stateTimer;
    private float dustEmitTimer;
    private Entity target;
    private bool justEntered = true;
    private Vec3d trailPos;
    private double despawnStartY;
    private int killCount = 0;

    private HuntPhase huntPhase = HuntPhase.Dive;
    private float huntPhaseTimer = 0f;
    private const float EmergeDuration = 2.5f;
    private const float RoarDuration = 8.0f;
    private const float SinkDuration = 1.2f;
    private const float EatingDuration = 2.0f;
    private const float DespawnDuration = 1.5f;
    // Worm goes home after this many kills so it isn't immortal.
    private const int MaxKillsBeforeDespawn = 4;

    // Traveling speed when chasing target on surface (per tick).
    private const float TravelSpeed = 0.15f;
    // Distance gates for the Hunt phase chain.
    private const float DiveTriggerRange = 50f;
    private const float RoarRange = 40f;
    private const float AttackRange = 6f;

    // Per-phase durations within Hunting. RoarHold is the dramatic pause before the underground chase begins.
    private const float DiveDuration = 1.5f;
    private const float RoarRiseDuration = 1.2f;
    private const float RoarHoldDuration = 6.0f;
    private const float RoarSinkDuration = 1.5f;
    private const float AttackRiseDuration = 1.0f;
    private const float AttackLungeDuration = 2.0f;
    private const float HuntTunnelSpeed = 0.25f; // blocks/tick underground

    // Kill-site crater: sand blocks carved out at the eruption point
    // when AttackLunge fires. Persistent mark on the basin.
    private const int KillCraterRadius = 3;
    private const int KillCraterDepthBelow = 3;
    private const float WormToothDropIntervalS = 8f;
    private const float WormToothDropChance = 0.20f;
    private float toothDropTimer = 0f;
    private const float ScareEmergePosY = 0f;
    private const float AttackPeakPosY = -1f;
    private const float UndergroundDepth = 5f;
    private const float DeepHuntDepth = 12f;

    private const float ChaseLooseRange = 100f;
    private const float KillRange = 5f;
    private const float KillDamage = 200f;

    private const float DustEmitInterval = 0.35f;

    public EntityBehaviorVertwormAI(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
    }

    public override void OnGameTick(float dt)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.Alive) return;

        // Hunting / Eating / Despawning own their sequences and don't
        // need a re-acquire mid-flow.
        bool canHaveNoTarget = state == State.Hunting || state == State.Eating || state == State.Despawning;
        if (!canHaveNoTarget && (target == null || !target.Alive))
        {
            target = FindNearestTarget();
            if (target == null)
            {
                // Smooth sink instead of pop on no-target despawn.
                TransitionTo(State.Despawning);
                return;
            }
            // Reset trail head to current column so a re-acquired chase
            // continues from where the worm actually is.
            trailPos = new Vec3d(entity.Pos.X, entity.World.SeaLevel - UndergroundDepth, entity.Pos.Z);
        }

        stateTimer += dt;
        switch (state)
        {
            case State.Emerging: TickEmerging(dt); break;
            case State.Roaring: TickRoaring(dt); break;
            case State.Sinking: TickSinking(dt); break;
            case State.Traveling: TickTraveling(dt); break;
            case State.Hunting: TickHunting(dt); break;
            case State.Eating: TickEating(dt); break;
            case State.Despawning: TickDespawning(dt); break;
        }
        justEntered = false;
    }

    private void TickEmerging(float dt)
    {
        if (justEntered)
        {
            // First tick: the big show. Loud sound + huge dust burst.
            entity.World.PlaySoundAt(
                new AssetLocation("game:sounds/effect/largeexplosion"),
                entity.Pos.X, entity.Pos.Y, entity.Pos.Z,
                null, false, 384f, 1.4f
            );
            EmitGiantDustBurst(entity.Pos.X, entity.World.SeaLevel + 1, entity.Pos.Z);
            entity.AnimManager.StartAnimation("mouthopen");
        }
        int sealevel = entity.World.SeaLevel;
        float t = GameMath.Clamp(stateTimer / EmergeDuration, 0f, 1f);
        float ease = 1f - (1f - t) * (1f - t); // quadratic ease-out
        double targetY = (sealevel - UndergroundDepth) + (UndergroundDepth + ScareEmergePosY) * ease;
        entity.Pos.Y = targetY;

        // Face the player while emerging.
        FaceTarget();

        if (stateTimer >= EmergeDuration)
        {
            TransitionTo(State.Roaring);
        }
    }

    private void TickRoaring(float dt)
    {
        // Hold at peak with mouth open. Periodic dust plumes around the
        // base read as "the sand is breathing."
        FaceTarget();

        dustEmitTimer += dt;
        if (dustEmitTimer >= DustEmitInterval)
        {
            dustEmitTimer = 0f;
            EmitGroundDustMound(entity.Pos.X, entity.World.SeaLevel + 1, entity.Pos.Z, spread: 8f);
        }

        if (stateTimer >= RoarDuration)
        {
            TransitionTo(State.Sinking);
        }
    }

    private void TickSinking(float dt)
    {
        FaceTarget();
        int sealevel = entity.World.SeaLevel;
        float t = GameMath.Clamp(stateTimer / SinkDuration, 0f, 1f);
        double startY = sealevel + ScareEmergePosY;
        double endY = sealevel - UndergroundDepth;
        entity.Pos.Y = startY + (endY - startY) * t;

        if (stateTimer >= SinkDuration)
        {
            // Trail starts under the worm's last position.
            trailPos = new Vec3d(entity.Pos.X, sealevel - UndergroundDepth, entity.Pos.Z);
            TransitionTo(State.Traveling);
        }
    }

    private void TickTraveling(float dt)
    {
        if (target == null || !target.Alive)
        {
            TransitionTo(State.Despawning);
            return;
        }

        int sealevel = entity.World.SeaLevel;

        // Move trail head toward target on the surface.
        double dx = target.Pos.X - trailPos.X;
        double dz = target.Pos.Z - trailPos.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);
        if (horizDist > 0.5)
        {
            double inv = 1.0 / horizDist;
            trailPos.X += dx * inv * TravelSpeed;
            trailPos.Z += dz * inv * TravelSpeed;
        }
        entity.Pos.X = trailPos.X;
        entity.Pos.Z = trailPos.Z;
        FaceTarget();

        // Non-basin column: dive immediately (no surface visual or
        // anim). Basin: ride at attack peak with the horizontal travel
        // animation, plus dust trail.
        bool onBasin = ColumnIsBasinSand((int)trailPos.X, (int)trailPos.Z);
        if (!onBasin)
        {
            entity.Pos.Y = sealevel - UndergroundDepth;
            if (entity.AnimManager.IsAnimationActive("horizontaltravel"))
            {
                entity.AnimManager.StopAnimation("horizontaltravel");
            }
        }
        else
        {
            entity.Pos.Y = AttackPeakY(sealevel);
            if (!entity.AnimManager.IsAnimationActive("horizontaltravel"))
            {
                entity.AnimManager.StartAnimation("horizontaltravel");
                entity.World.Logger.Notification("[VSDune] Vertworm: started horizontaltravel anim at Pos.Y={0:F1} (sealevel={1}).", entity.Pos.Y, sealevel);
            }

            dustEmitTimer += dt;
            if (dustEmitTimer >= DustEmitInterval)
            {
                dustEmitTimer = 0f;
                int surfY = entity.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)trailPos.X, 0, (int)trailPos.Z));
                EmitGroundDustMound(trailPos.X, surfY + 1, trailPos.Z, spread: 4f);
                SendApproachShake(trailPos.X, trailPos.Z);
            }

            // Periodic tooth-drop roll (only while travelling on basin).
            toothDropTimer += dt;
            if (toothDropTimer >= WormToothDropIntervalS)
            {
                toothDropTimer = 0f;
                if (entity.World.Rand.NextDouble() < WormToothDropChance)
                {
                    DropWormTooth();
                }
            }
        }

        // Target ran too far: despawn.
        if (horizDist > ChaseLooseRange)
        {
            TransitionTo(State.Despawning);
            return;
        }

        // Within dive trigger range AND target on basin: kick off Hunt.
        if (horizDist <= DiveTriggerRange && IsOnBasinSand(target))
        {
            entity.AnimManager.StopAnimation("horizontaltravel");
            TransitionTo(State.Hunting);
        }
    }

    private double AttackPeakY(int sealevel)
    {
        return sealevel + AttackPeakPosY;
    }

    // Column-only variant of IsOnBasinSand for trail-position gating.
    // Same sand-depth heuristic, raw X/Z instead of a target entity.
    private bool ColumnIsBasinSand(int x, int z)
    {
        int sealevel = entity.World.SeaLevel;
        var probe = new BlockPos(0);
        int depth = 0;
        for (int y = sealevel; y > sealevel - 30; y--)
        {
            probe.Set(x, y, z);
            var b = entity.World.BlockAccessor.GetBlock(probe);
            if (b == null) break;
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= 8) return true;
        }
        return false;
    }

    private void TickHunting(float dt)
    {
        if (target == null || !target.Alive)
        {
            TransitionTo(State.Despawning);
            return;
        }

        int sealevel = entity.World.SeaLevel;
        huntPhaseTimer += dt;

        double dx = target.Pos.X - trailPos.X;
        double dz = target.Pos.Z - trailPos.Z;
        double horizDist = Math.Sqrt(dx * dx + dz * dz);

        switch (huntPhase)
        {
            case HuntPhase.Dive:
            {
                float t = GameMath.Clamp(huntPhaseTimer / DiveDuration, 0f, 1f);
                float ease = 1f - (1f - t) * (1f - t);
                double startY = AttackPeakY(sealevel);
                double endY = sealevel - DeepHuntDepth;
                entity.Pos.Y = startY + (endY - startY) * ease;
                if (huntPhaseTimer >= DiveDuration) SetHuntPhase(HuntPhase.ApproachToRoar);
                break;
            }

            case HuntPhase.ApproachToRoar:
            {
                // Tunnel underground toward target until inside RoarRange.
                if (horizDist > 0.5)
                {
                    double inv = 1.0 / horizDist;
                    trailPos.X += dx * inv * HuntTunnelSpeed;
                    trailPos.Z += dz * inv * HuntTunnelSpeed;
                    entity.Pos.X = trailPos.X;
                    entity.Pos.Z = trailPos.Z;
                }
                entity.Pos.Y = sealevel - DeepHuntDepth;

                dustEmitTimer += dt;
                if (dustEmitTimer >= DustEmitInterval)
                {
                    dustEmitTimer = 0f;
                    int surfY = entity.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)target.Pos.X, 0, (int)target.Pos.Z));
                    EmitGroundDustMound(target.Pos.X, surfY + 1, target.Pos.Z, spread: 4f);
                    SendScreenShakeNear(target.Pos.X, target.Pos.Z, 0.3f, 600);
                }
                if (horizDist <= RoarRange)
                {
                    entity.AnimManager.StartAnimation(new AnimationMetaData { Code = "mouthopen", Animation = "mouthopen", AnimationSpeed = 1.0f }.Init());
                    entity.World.PlaySoundAt(
                        new AssetLocation("game:sounds/effect/largeexplosion"),
                        entity.Pos.X, entity.Pos.Y, entity.Pos.Z,
                        null, false, 384f, 1.2f
                    );
                    EmitGiantDustBurst(entity.Pos.X, sealevel + 1, entity.Pos.Z);
                    SetHuntPhase(HuntPhase.RoarRise);
                }
                break;
            }

            case HuntPhase.RoarRise:
            {
                float t = GameMath.Clamp(huntPhaseTimer / RoarRiseDuration, 0f, 1f);
                float ease = 1f - (1f - t) * (1f - t);
                double startY = sealevel - DeepHuntDepth;
                double endY = AttackPeakY(sealevel);
                entity.Pos.Y = startY + (endY - startY) * ease;
                FaceTarget();
                if (huntPhaseTimer >= RoarRiseDuration) SetHuntPhase(HuntPhase.RoarHold);
                break;
            }

            case HuntPhase.RoarHold:
            {
                entity.Pos.Y = AttackPeakY(sealevel);
                FaceTarget();
                if (huntPhaseTimer >= RoarHoldDuration) SetHuntPhase(HuntPhase.RoarSink);
                break;
            }

            case HuntPhase.RoarSink:
            {
                float t = GameMath.Clamp(huntPhaseTimer / RoarSinkDuration, 0f, 1f);
                double startY = AttackPeakY(sealevel);
                double endY = sealevel - DeepHuntDepth;
                entity.Pos.Y = startY + (endY - startY) * t;
                if (huntPhaseTimer >= RoarSinkDuration) SetHuntPhase(HuntPhase.ApproachToAttack);
                break;
            }

            case HuntPhase.ApproachToAttack:
            {
                if (horizDist > 0.5)
                {
                    double inv = 1.0 / horizDist;
                    trailPos.X += dx * inv * HuntTunnelSpeed;
                    trailPos.Z += dz * inv * HuntTunnelSpeed;
                    entity.Pos.X = trailPos.X;
                    entity.Pos.Z = trailPos.Z;
                }
                entity.Pos.Y = sealevel - DeepHuntDepth;
                if (horizDist <= AttackRange)
                {
                    entity.AnimManager.StartAnimation(new AnimationMetaData { Code = "horizontalattack", Animation = "horizontalattack", AnimationSpeed = 1.0f }.Init());
                    CarveKillCrater((int)entity.Pos.X, (int)entity.Pos.Z);
                    EmitGiantDustBurst(entity.Pos.X, sealevel + 1, entity.Pos.Z);
                    SetHuntPhase(HuntPhase.AttackRise);
                }
                break;
            }

            case HuntPhase.AttackRise:
            {
                float t = GameMath.Clamp(huntPhaseTimer / AttackRiseDuration, 0f, 1f);
                float ease = 1f - (1f - t) * (1f - t);
                double startY = sealevel - DeepHuntDepth;
                double endY = AttackPeakY(sealevel);
                entity.Pos.Y = startY + (endY - startY) * ease;
                FaceTarget();
                if (huntPhaseTimer >= AttackRiseDuration) SetHuntPhase(HuntPhase.AttackLunge);
                break;
            }

            case HuntPhase.AttackLunge:
            {
                // Horizontal forward lunge through the target column.
                entity.Pos.Y = AttackPeakY(sealevel);
                FaceTarget();
                if (horizDist > 0.5)
                {
                    double inv = 1.0 / horizDist;
                    double forward = TravelSpeed * 2f;
                    trailPos.X += dx * inv * forward;
                    trailPos.Z += dz * inv * forward;
                    entity.Pos.X = trailPos.X;
                    entity.Pos.Z = trailPos.Z;
                }

                if (target.Alive)
                {
                    // Bite reach is measured from the mouth (head), not the
                    // worm's center, so the target is hit when the head
                    // reaches it rather than the midsection.
                    Vec3d mouth = GetMouthPos();
                    double tdx = target.Pos.X - mouth.X;
                    double tdz = target.Pos.Z - mouth.Z;
                    if (tdx * tdx + tdz * tdz <= KillRange * KillRange)
                    {
                        target.ReceiveDamage(new DamageSource
                        {
                            Source = EnumDamageSource.Entity,
                            SourceEntity = entity,
                            Type = EnumDamageType.PiercingAttack,
                            DamageTier = 5,
                        }, KillDamage);
                    }
                }

                if (huntPhaseTimer >= AttackLungeDuration)
                {
                    killCount++;
                    if (killCount >= MaxKillsBeforeDespawn)
                    {
                        TransitionTo(State.Despawning);
                    }
                    else
                    {
                        target = null;
                        TransitionTo(State.Eating);
                    }
                }
                break;
            }
        }
    }

    private void SetHuntPhase(HuntPhase next)
    {
        huntPhase = next;
        huntPhaseTimer = 0f;
    }

    // World position of the "mouthpoint" attachment point. Mirrors the
    // attachment-point-to-world transform from vanilla
    // EntityBoatConstruction; falls back to the worm center if absent.
    private Vec3d GetMouthPos()
    {
        var apap = entity.AnimManager?.Animator?.GetAttachmentPointPose("mouthpoint");
        if (apap == null) return entity.Pos.XYZ;

        var mat = new Matrixf();
        mat.RotateY(entity.Pos.Yaw + GameMath.PIHALF);
        apap.Mul(mat);
        var off = mat.TransformVector(new Vec4f(0, 0, 0, 1));
        return new Vec3d(entity.Pos.X + off.X, entity.Pos.Y + off.Y, entity.Pos.Z + off.Z);
    }

    private void TickEating(float dt)
    {
        if (justEntered)
        {
            // Replay mouthopen so the bite-and-chew beat reads visually.
            entity.AnimManager.StartAnimation("mouthopen");
        }
        // Hold at the kill peak Y, no movement.
        if (stateTimer >= EatingDuration)
        {
            TransitionTo(State.Traveling);
        }
    }

    private void TickDespawning(float dt)
    {
        // Sink from the despawn-entry Y down past UndergroundDepth so
        // the model animates out cleanly rather than popping.
        // despawnStartY was snapshotted on transition.
        int sealevel = entity.World.SeaLevel;
        double targetY = sealevel - UndergroundDepth - 12;
        float t = GameMath.Clamp(stateTimer / DespawnDuration, 0f, 1f);
        entity.Pos.Y = despawnStartY + (targetY - despawnStartY) * t;

        if (stateTimer >= DespawnDuration)
        {
            entity.Die(EnumDespawnReason.Expire);
        }
    }

    private void TransitionTo(State newState)
    {
        if (newState == State.Despawning)
        {
            despawnStartY = entity.Pos.Y;
        }
        if (newState == State.Hunting)
        {
            // Reset to Dive so re-entering Hunting (after Eating) starts
            // the whole 50/40/30 chain over instead of resuming mid-loop.
            huntPhase = HuntPhase.Dive;
            huntPhaseTimer = 0f;
        }
        state = newState;
        stateTimer = 0f;
        dustEmitTimer = 0f;
        justEntered = true;
    }

    private void FaceTarget()
    {
        if (target == null || !target.Alive) return;
        double dx = target.Pos.X - entity.Pos.X;
        double dz = target.Pos.Z - entity.Pos.Z;
        if (dx * dx + dz * dz > 0.01)
        {
            entity.Pos.Yaw = (float)Math.Atan2(dx, dz);
        }
    }

    // Search radius for raid NPCs. Players are globally scanned (few
    // of them, cheap) but NPCs are bounded to keep the per-call cost
    // reasonable. 100b covers the raid landing footprint comfortably.
    private const float NpcScanRange = 100f;

    private Entity FindNearestTarget()
    {
        Entity nearest = null;
        double bestSq = double.MaxValue;

        foreach (var p in entity.World.AllOnlinePlayers)
        {
            if (p?.Entity == null || !p.Entity.Alive) continue;
            double dx = p.Entity.Pos.X - entity.Pos.X;
            double dz = p.Entity.Pos.Z - entity.Pos.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq < bestSq)
            {
                bestSq = distSq;
                nearest = p.Entity;
            }
        }

        // Closure capture: bestOverall/bestOverallSq mutated in predicate.
        Entity bestOverall = nearest;
        double bestOverallSq = bestSq;
        entity.World.GetEntitiesAround(entity.Pos.XYZ, NpcScanRange, NpcScanRange,
            (e) =>
            {
                if (e == null || e == entity || !e.Alive) return false;
                // Players were globally scanned above; skip the duplicate.
                if (e is EntityPlayer) return false;
                // Only flesh-and-blood threats. Vehicles, items, etc.
                // are not EntityAgent.
                if (e is not EntityAgent) return false;
                var path = e.Code?.Path;
                // Fremen walk silent on the sand (Dune canon).
                if (path != null && path.StartsWith("fremen-")) return false;
                // Other worms aren't prey.
                if (path == "vertworm") return false;
                double dx = e.Pos.X - entity.Pos.X;
                double dz = e.Pos.Z - entity.Pos.Z;
                double distSq = dx * dx + dz * dz;
                if (distSq < bestOverallSq)
                {
                    bestOverallSq = distSq;
                    bestOverall = e;
                }
                return false;
            });
        return bestOverall;
    }

    private bool IsOnBasinSand(Entity e)
    {
        // 8 blocks of sand under feet = basin (lake or ocean fill).
        var pos = e.Pos.AsBlockPos;
        int sealevel = entity.World.SeaLevel;
        var probe = new BlockPos(0);
        int depth = 0;
        for (int y = sealevel; y > sealevel - 30; y--)
        {
            probe.Set(pos.X, y, pos.Z);
            var b = entity.World.BlockAccessor.GetBlock(probe);
            if (b == null) break;
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= 8) return true;
        }
        return false;
    }

    private void CarveKillCrater(int cx, int cz)
    {
        // Punch out sand in a small disc around the eruption point so
        // the kill site leaves a persistent mark. Stops at any non-sand
        // block (rock substrate stays intact).
        if (entity.Api is not ICoreServerAPI sapi) return;
        int sealevel = entity.World.SeaLevel;
        var pos = new BlockPos(0);
        int radius = KillCraterRadius;
        int radiusSq = radius * radius;
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (dx * dx + dz * dz > radiusSq) continue;
                int wx = cx + dx;
                int wz = cz + dz;
                for (int dy = -KillCraterDepthBelow; dy <= 1; dy++)
                {
                    pos.Set(wx, sealevel + dy, wz);
                    var b = entity.World.BlockAccessor.GetBlock(pos);
                    if (b == null) continue;
                    if (b.BlockMaterial != EnumBlockMaterial.Sand) continue;
                    entity.World.BlockAccessor.SetBlock(0, pos);
                }
            }
        }
    }

    private void DropWormTooth()
    {
        if (entity.Api is not ICoreServerAPI sapi) return;
        var toothItem = sapi.World.GetItem(new AssetLocation("vsdune", "wormtooth"));
        if (toothItem == null) return;
        int surfY = entity.World.BlockAccessor.GetRainMapHeightAt(
            new BlockPos((int)trailPos.X, 0, (int)trailPos.Z)
        );
        var dropPos = new Vec3d(trailPos.X + 0.5, surfY + 1.0, trailPos.Z + 0.5);
        sapi.World.SpawnItemEntity(new ItemStack(toothItem), dropPos);
    }

    // Proximity-scaled rumble for grounded players only. Intensity builds
    // from 0 at MaxShakeRadius down to 0.5 at point-blank. Players mounted
    // (thopter, etc.) or airborne are skipped.
    private const float MaxShakeRadius = 80f;
    private void SendApproachShake(double x, double z)
    {
        if (entity.Api is not ICoreServerAPI sapi) return;
        var scare = sapi.ModLoader.GetModSystem<GenVertwormScare>();
        if (scare?.ShakeChannel == null) return;
        float maxSq = MaxShakeRadius * MaxShakeRadius;
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;
            // Grounded, unmounted players only.
            if (!sp.Entity.OnGround) continue;
            if ((sp.Entity as EntityAgent)?.MountedOn != null) continue;
            double pdx = sp.Entity.Pos.X - x;
            double pdz = sp.Entity.Pos.Z - z;
            float distSq = (float)(pdx * pdx + pdz * pdz);
            if (distSq > maxSq) continue;
            float t = 1f - distSq / maxSq;          // 0 at edge, 1 at center
            float intensity = 0.15f + t * 0.85f;    // 0.15 at edge, 1.0 at center
            var packet = new ScreenShakePacket { DurationMs = 600, Intensity = intensity };
            scare.ShakeChannel.SendPacket(packet, sp);
        }
    }

    private void SendScreenShakeNear(double x, double z, float intensity, int durationMs)
    {
        if (entity.Api is not ICoreServerAPI sapi) return;
        var scare = sapi.ModLoader.GetModSystem<GenVertwormScare>();
        if (scare?.ShakeChannel == null) return;
        var packet = new ScreenShakePacket { DurationMs = durationMs, Intensity = intensity };
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;
            double pdx = sp.Entity.Pos.X - x;
            double pdz = sp.Entity.Pos.Z - z;
            if (pdx * pdx + pdz * pdz <= 200 * 200)
            {
                scare.ShakeChannel.SendPacket(packet, sp);
            }
        }
    }

    private void EmitGiantDustBurst(double x, double y, double z)
    {
        // Tan dust blast for the worm bursting through the sand. Bigger
        // than the spice-blow shockwave, and tan rather than purple.
        var p = new SimpleParticleProperties(
            900, 1500,
            ColorUtil.ToRgba(220, 190, 160, 100),
            new Vec3d(x - 4, y - 0.5, z - 4),
            new Vec3d(x + 4, y + 4, z + 4),
            new Vec3f(-12f, 1f, -12f),
            new Vec3f(12f, 8f, 12f),
            16f,
            -0.4f,
            1.5f, 3.5f,
            EnumParticleModel.Quad
        );
        p.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -10);
        p.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2.5f);
        entity.World.SpawnParticles(p);
    }

    private void EmitGroundDustMound(double x, double y, double z, float spread)
    {
        // Continuous dust trail, smaller than the burst but still
        // visible from a distance. Emitted at the surface above the
        // chasing trail head so it reads as the sand bulging up.
        var p = new SimpleParticleProperties(
            50, 80,
            ColorUtil.ToRgba(200, 170, 140, 85),
            new Vec3d(x - spread, y, z - spread),
            new Vec3d(x + spread, y + 1.0, z + spread),
            new Vec3f(-1.8f, 0.8f, -1.8f),
            new Vec3f(1.8f, 3.0f, 1.8f),
            7f,
            -0.04f,
            1.2f, 2.4f,
            EnumParticleModel.Quad
        );
        p.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -15);
        p.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0.6f);
        entity.World.SpawnParticles(p);
    }

    public override string PropertyName() => "vsdune.vertwormai";
}
