using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsDune;


public class EntityFactionUnit : EntityHumanoid
{
    // WatchedAttribute keys for the Fremen player-aggro stamp.
    public const string AttrAggrievedPlayerUID = "vsdune.aggrievedByPlayerUID";
    public const string AttrAggrievedAtMs = "vsdune.aggrievedAtMs";
    public const long AggroWindowMs = 30000;
    private const float HerdRevengePropagationRadius = 30f;
    private EntityBehaviorHandInv handInv;

    public override ItemSlot RightHandItemSlot => handInv?.Inventory[0];
    public override ItemSlot LeftHandItemSlot => handInv?.Inventory[1];

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        handInv = GetBehavior<EntityBehaviorHandInv>();
    }

    public override bool ShouldReceiveDamage(DamageSource damageSource, float damage)
    {
        Entity src = damageSource.SourceEntity;
        if (src is EntityProjectile && damageSource.CauseEntity != null)
        {
            src = damageSource.CauseEntity;
        }
        if (src is EntityAgent ea && ea.HerdId != 0 && ea.HerdId == this.HerdId)
        {
            return false;
        }
        return true;
    }

    public override void OnHurt(DamageSource dmgSource, float damage)
    {
        base.OnHurt(dmgSource, damage);
        if (dmgSource == null) return; // client-side call
        if (World.Side != EnumAppSide.Server) return;

        string faction = Properties?.Attributes?["factionCode"].AsString(null);

        // Hark units feed the activity score on hit. Drives the air
        // raid escalation system in GenHarkonenActivity.
        if (faction == "harkonnen")
        {
            Entity harkAttacker = dmgSource.SourceEntity;
            if (harkAttacker is EntityProjectile && dmgSource.CauseEntity != null)
            {
                harkAttacker = dmgSource.CauseEntity;
            }
            if (harkAttacker != null)
            {
                (Api as ICoreServerAPI)?.ModLoader.GetModSystem<GenHarkonenActivity>()
                    ?.BumpFromHarkAttackedBy(harkAttacker, GenHarkonenActivity.BumpFromHarkAttacked);
            }
        }

        // Aggrieved stamp for factions that are neutral by default:
        // Fremen and Smugglers retaliate only when attacked.
        if (faction != "fremen" && faction != "smuggler") return;

        Entity attacker = dmgSource.SourceEntity;
        if (attacker is EntityProjectile && dmgSource.CauseEntity != null)
        {
            attacker = dmgSource.CauseEntity;
        }
        if (attacker is not EntityPlayer player) return;
        string uid = player.PlayerUID;
        if (string.IsNullOrEmpty(uid)) return;

        long nowMs = World.ElapsedMilliseconds;
        StampAggrieved(this, uid, nowMs);

        // Herd-revenge: nearby same-faction same-HerdId units pile on.
        if (HerdId == 0) return;
        long ownHerd = HerdId;
        string ownFaction = faction;
        World.GetEntitiesAround(Pos.XYZ, HerdRevengePropagationRadius, HerdRevengePropagationRadius, (e) =>
        {
            if (e == this) return false;
            if (e is not EntityFactionUnit unit) return false;
            if (unit.HerdId != ownHerd) return false;
            string unitFaction = unit.Properties?.Attributes?["factionCode"].AsString(null);
            if (unitFaction != ownFaction) return false;
            StampAggrieved(unit, uid, nowMs);
            return false;
        });
    }

    private static void StampAggrieved(EntityFactionUnit unit, string uid, long nowMs)
    {
        unit.WatchedAttributes.SetString(AttrAggrievedPlayerUID, uid);
        unit.WatchedAttributes.SetLong(AttrAggrievedAtMs, nowMs);
        unit.WatchedAttributes.MarkPathDirty(AttrAggrievedPlayerUID);
    }
}
