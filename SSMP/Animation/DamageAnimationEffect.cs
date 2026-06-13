using System;
using System.Collections.Generic;
using System.Reflection;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using UnityEngine.Events;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation;

/// <summary>
/// Abstract base class for animation effects that can deal damage to other players.
/// </summary>
internal abstract class DamageAnimationEffect : AnimationEffect {
    /// <summary>
    /// The object layer for attacks.
    /// </summary>
    protected const int AttackLayer = (int) GlobalEnums.PhysLayers.HERO_ATTACK;

    /// <summary>
    /// Whether this effect should deal damage.
    /// </summary>
    protected bool ShouldDoDamage;

    /// <summary>
    /// Stores HP values before remote visual-only enemy hits so the hit pipeline can run without allowing remote attack
    /// replicas to directly apply PvE damage.
    /// </summary>
    private static readonly Dictionary<int, int> RemoteVisualHitHpBefore = new();

    /// <summary>
    /// Enemy <see cref="HealthManager"/> instance id -> the <c>CustomPlayerLoop.FixedUpdateCycle</c> in which a
    /// remote attack's visual replica is striking it. <see cref="ConsumeRemoteVisualHitRecoil"/> uses it to drop
    /// the recoil that strike produces — a duplicate of, and (under Silksong's flipped facing-scale convention)
    /// 180°-inverted copy of, the authoritative recoil the attacking player already networked.
    /// <para>
    /// Crucially this is a CYCLE STAMP, not a membership flag. The bracket that feeds it
    /// (StoreHp via WillDamageEnemyOptions / RestoreHp via DamagedEnemyHealthManager) is NOT balanced: StoreHp
    /// fires for every damaging replica hit, but RestoreHp only fires when the hit response is DamageEnemy — a
    /// replica landing on an invincible / blocking / i-framed / dead enemy stores a key that is never removed.
    /// A plain membership flag would then suppress that enemy's OWN real recoils forever. Because the marker is
    /// stamped with the fixed-update cycle and consumed one-shot, such an orphaned key is honored only within its
    /// own (already-finished) cycle and is ignored / purged the instant the cycle advances, so it can never
    /// permanently suppress a real recoil. Memory-wise an orphaned key is no worse than the pre-existing
    /// <see cref="RemoteVisualHitHpBefore"/> leak for the same blocked-hit case.
    /// </para>
    /// </summary>
    private static readonly Dictionary<int, int> RemoteVisualHitRecoilCycle = new();

    /// <summary>
    /// Cached delegate to store enemy HP before applying remote visual-only damage.
    /// </summary>
    private static readonly Action<HealthManager, HitInstance> WillDamageEnemyOptionsDelegate = StoreHpBeforeRemoteVisualHit;

    /// <summary>
    /// Cached delegate to restore enemy HP after applying remote visual-only damage.
    /// </summary>
    private static readonly Action<HealthManager> DamagedEnemyHealthManagerDelegate = RestoreHpAfterRemoteVisualHit;

    /// <inheritdoc/>
    public abstract override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo);

    /// <inheritdoc/>
    public abstract override byte[]? GetEffectInfo();

    /// <summary>
    /// Sets whether this animation effect should deal damage.
    /// </summary>
    /// <param name="shouldDoDamage">The new boolean value.</param>
    public void SetShouldDoDamage(bool shouldDoDamage) {
        ShouldDoDamage = shouldDoDamage;
    }

    /// <summary>
    /// Adds a <see cref="DamageHero"/> component to the given game object that deals the given damage when the player
    /// collides with it. Also adds a <see cref="EffectOwnerComponent"/> component that indicates the owner of this
    /// object.
    /// </summary>
    /// <param name="target">The target game object to attach the component to.</param>
    /// <param name="damage">The number of mask of damage it should deal.</param>
    /// <returns>The <see cref="DamageHero"/> component that was added to the game object</returns>
    protected static DamageHero AddDamageHeroComponent(GameObject target, int damage = 1) {
        var damageHero = target.AddComponentIfNotPresent<DamageHero>();
        damageHero.damageDealt = damage;
        damageHero.OnDamagedHero = new UnityEvent();

        var identifier = target.AddComponentIfNotPresent<EffectOwnerComponent>();
        identifier.Owner = target;

        return damageHero;
    }

    /// <summary>
    /// Removes a <see cref="DamageHero"/> component from the given game object.
    /// </summary>
    /// <param name="target">The target game object to detach the component from.</param>
    private static void RemoveDamageHeroComponent(GameObject target) {
        target.DestroyComponent<DamageHero>();
    }

    /// <summary>
    /// Adds or removes a <see cref="DamageHero"/> component from the given game object,
    /// depending on the PVP and team settings.
    /// </summary>
    /// <param name="target">The target game object to attach or remove the component from.</param>
    /// <param name="damage">The number of mask of damage it should deal.</param>
    /// <returns>The <see cref="DamageHero"/> component that was added if PVP was turned on</returns>
    protected DamageHero? SetDamageHeroState(GameObject target, int damage = 1) {
        return SetDamageHeroState(target, ServerSettings.IsPvpEnabled && ShouldDoDamage, damage);
    }

    /// <summary>
    /// Adds or removes a <see cref="DamageHero"/> component from the given game object,
    /// depending on the PVP and team settings.
    /// </summary>
    /// <param name="target">The target game object to attach or remove the component from.</param>
    /// <param name="damage">The number of mask of damage it should deal.</param>
    /// <param name="doDamage">If the damager should be enabled or not</param>
    /// <returns>The <see cref="DamageHero"/> component that was added if PVP was turned on</returns>
    public static DamageHero? SetDamageHeroState(GameObject target, bool doDamage, int damage = 1) {
        if (doDamage && damage > 0) {
            return AddDamageHeroComponent(target, damage);
        }

        RemoveDamageHeroComponent(target);
        return null;
    }

    /// <summary>
    /// Fixes a remote attack's <see cref="DamageEnemies"/> components by allowing visual enemy hit reactions while
    /// preventing remote attack replicas from directly applying PvE damage or lethal enemy side effects.
    /// </summary>
    /// <param name="target">The object that may contain one or more <see cref="DamageEnemies"/> components.</param>
    protected static void FixDamageEnemies(GameObject target) {
        var damageEnemiesComponents = target.GetComponentsInChildren<DamageEnemies>(true);

        // Resolve the owning player root while the effect is still parented under the player object
        // (it may detach later, e.g. projectiles) and tag every damager with it, so enemy aggro can
        // attribute hits from this effect to the attacking player even after the effect detaches.
        // Uses a dedicated AttackOwnerComponent so it cannot be clobbered by EffectOwnerComponent.
        var ownerRoot = Game.Client.PlayerTargetRegistry.GetTrackedPlayerRoot(target);

        foreach (var damageEnemies in damageEnemiesComponents) {
            if (ownerRoot != null) {
                var ownerTag = damageEnemies.gameObject.AddComponentIfNotPresent<AttackOwnerComponent>();
                ownerTag.PlayerRoot = ownerRoot;
            }
            damageEnemies.doesNotTink = true;
            damageEnemies.doesNotTinkThroughWalls = true;
            damageEnemies.doesNotParry = true;
            damageEnemies.silkGeneration = HitSilkGeneration.None;

            damageEnemies.nonLethal = true;
            damageEnemies.deathEndDamage = false;
            damageEnemies.deathEventTarget = null;
            damageEnemies.deathEvent = string.Empty;

            damageEnemies.WillDamageEnemyOptions -= WillDamageEnemyOptionsDelegate;
            damageEnemies.WillDamageEnemyOptions += WillDamageEnemyOptionsDelegate;

            damageEnemies.DamagedEnemyHealthManager -= DamagedEnemyHealthManagerDelegate;
            damageEnemies.DamagedEnemyHealthManager += DamagedEnemyHealthManagerDelegate;

            NeuterReplicaLagHits(damageEnemies);
        }
    }

    /// <summary>
    /// Field handle for <c>DamageEnemies.lagHitOptions</c> (private). Null if the field is absent (game update).
    /// </summary>
    private static readonly FieldInfo? LagHitOptionsField =
        typeof(DamageEnemies).GetField("lagHitOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Field handle for <c>DamageEnemies.lagHitOptionsProfile</c> (private). Null if the field is absent.
    /// </summary>
    private static readonly FieldInfo? LagHitOptionsProfileField =
        typeof(DamageEnemies).GetField("lagHitOptionsProfile", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Disables lag-hits on a remote attack replica's damager. A lag-hit profile with HitCount &gt; 0 spawns a
    /// coroutine (HealthManager.DoLagHits) that re-hits the enemy on LATER fixed-update cycles by calling
    /// HealthManager.Hit directly — bypassing DamageEnemies entirely. Those re-hits would run recoil outside the
    /// single-cycle window the recoil suppression marker covers (and deal real HP damage), re-introducing the
    /// inverted-recoil bug. Replicas are visual-only, so they never need lag-hits: we point the damager at a fresh
    /// empty <see cref="LagHitOptions"/> (HitCount 0 -> <c>ShouldDoLagHits()</c> false -> DoLagHits early-returns)
    /// and clear any shared profile reference WITHOUT mutating the profile itself.
    /// </summary>
    /// <param name="damageEnemies">The replica damager to neuter.</param>
    private static void NeuterReplicaLagHits(DamageEnemies damageEnemies) {
        try {
            // Clear the profile first so the LagHits getter falls back to lagHitOptions (never mutate the shared
            // ScriptableObject profile — it is referenced by the real prefab too).
            LagHitOptionsProfileField?.SetValue(damageEnemies, null);
            // A fresh instance defaults HitCount to 0; this also guarantees LagHits is never null (DoLagHits calls
            // ShouldDoLagHits() on it unconditionally).
            LagHitOptionsField?.SetValue(damageEnemies, new LagHitOptions());
        } catch (Exception e) {
            Logger.Warn($"Could not neuter replica lag-hits: {e.Message}");
        }
    }

    /// <summary>
    /// Stores the enemy HP before a remote visual-only hit applies damage.
    /// </summary>
    /// <param name="healthManager">The health manager that is about to be damaged.</param>
    /// <param name="hitInstance">The hit instance that is about to be applied.</param>
    private static void StoreHpBeforeRemoteVisualHit(HealthManager healthManager, HitInstance hitInstance) {
        var id = healthManager.GetInstanceID();
        RemoteVisualHitHpBefore[id] = healthManager.hp;
        // Stamp this enemy as struck by a remote attack replica in the current fixed-update cycle. The replica's
        // recoil (if it produces one) runs later in the SAME cycle: DamageEnemies.LateFixedUpdate calls
        // EvaluateDamage (which fires this StoreHp) and then ProcessDamageBuffer (which applies the hit, and the
        // recoil inside HealthManager.TakeDamage) without yielding, so the cycle value matches at the recoil.
        RemoteVisualHitRecoilCycle[id] = CustomPlayerLoop.FixedUpdateCycle;
    }

    /// <summary>
    /// Restores enemy HP after a remote visual-only hit so visual reactions can play without applying PvE damage.
    /// </summary>
    /// <param name="healthManager">The health manager that was damaged by the remote visual-only hit.</param>
    private static void RestoreHpAfterRemoteVisualHit(HealthManager healthManager) {
        var instanceId = healthManager.GetInstanceID();
        // Clear the recoil marker for the balanced (damaging) case. Blocked/invincible/dead hits never reach here,
        // but their orphaned markers are rendered harmless by the per-cycle stamp in ConsumeRemoteVisualHitRecoil.
        RemoteVisualHitRecoilCycle.Remove(instanceId);
        if (!RemoteVisualHitHpBefore.Remove(instanceId, out var hpBeforeHit)) {
            return;
        }

        healthManager.hp = hpBeforeHit;
    }

    /// <summary>
    /// Returns true — and consumes the marker — iff the recoil about to run on <paramref name="enemyObject"/> is
    /// being driven by a remote player's attack VISUAL replica striking it in the CURRENT fixed-update cycle. Such
    /// a recoil duplicates — and, under Silksong's inverted facing-scale convention (FaceRight =&gt; localScale.x =
    /// -1), 180°-inverts — the authoritative recoil the attacking player already networked, so it would make the
    /// enemy recoil toward the attacker on every screen. <see cref="Game.Client.Entity.Component.KnockbackComponent"/>
    /// uses this to drop those replica-driven recoils, leaving only the networked, world-space-correct recoil.
    /// <para>
    /// The marker is stamped with the fixed-update cycle (see <see cref="RemoteVisualHitRecoilCycle"/>) and removed
    /// on lookup, so it is one-shot and cannot leak into the enemy's own later recoils: a marker orphaned by a
    /// blocked / invincible / dead-target replica hit lives in an earlier cycle and is rejected (and purged) here.
    /// </para>
    /// </summary>
    /// <param name="enemyObject">The live enemy object (host enemy or puppet) whose recoil is being evaluated.</param>
    /// <returns>True if this recoil is a current-cycle remote visual-only replica hit and must be dropped.</returns>
    internal static bool ConsumeRemoteVisualHitRecoil(GameObject? enemyObject) {
        // Fast path: the marker map is empty except during a replica's strike, so almost every recoil (real local
        // hits, networked recoil replay) short-circuits here without a GetComponent lookup.
        if (RemoteVisualHitRecoilCycle.Count == 0 || enemyObject == null) {
            return false;
        }

        var healthManager = enemyObject.GetComponent<HealthManager>();
        if (healthManager == null) {
            return false;
        }

        var id = healthManager.GetInstanceID();
        if (!RemoteVisualHitRecoilCycle.TryGetValue(id, out var cycle)) {
            return false;
        }

        // One-shot: remove regardless of the cycle check, so a stale marker self-purges and a real recoil later in
        // the same cycle is never affected by a marker we have already accounted for.
        RemoteVisualHitRecoilCycle.Remove(id);

        // Honor the marker only if it was stamped in the CURRENT cycle. An orphaned marker (from a blocked / None
        // replica hit that produced no recoil) belongs to an already-finished cycle and is dropped here harmlessly.
        return cycle == CustomPlayerLoop.FixedUpdateCycle;
    }
}
