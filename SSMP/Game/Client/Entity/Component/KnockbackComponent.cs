using System;
using MonoMod.RuntimeDetour;
using SSMP.Animation;
using SSMP.Logging;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the knockback (recoil) of the entity.
/// <remarks>
/// Enemy knockback is not a velocity change: <see cref="Recoil.RecoilByDirection"/> performs a positional
/// sweep in FixedUpdate and fires FSM events ("HIT LEFT/RIGHT/UP/DOWN", "RECOIL HORIZONTAL", "RECOIL") on
/// the enemy object. Syncing velocity therefore never reproduces it. Instead this component networks the
/// call to <see cref="Recoil.RecoilByDirection"/> itself.
///
/// Direction of the data flow:
/// - Scene client hitting the client (puppet) object: the local hit pipeline calls RecoilByDirection on the
///   puppet, which is allowed to run for instant local feedback (the puppet has no Rigidbody2D, so Recoil
///   falls back to transform translation). The call is networked so the scene host applies it on the host
///   object, which runs the authoritative physics and FSM events. The resulting movement and FSM state
///   changes reach all clients through the existing position and FSM sync.
/// - Scene host recoil (own hits or AI): the call is networked so scene clients replay it on their puppet
///   for crisper motion between position updates.
/// Received recoil is applied with a re-entrancy flag so it is never networked again, which prevents
/// feedback loops and double application.
/// </remarks>
internal class KnockbackComponent : EntityComponent {
    /// <summary>
    /// Host-client pair of recoil components of the entity.
    /// </summary>
    private readonly HostClientPair<Recoil> _recoil;

    /// <summary>
    /// MonoMod hook for Recoil.RecoilByDirection.
    /// </summary>
    private Hook? _recoilByDirectionHook;

    /// <summary>
    /// Whether we are currently applying a recoil that was received over the network. Used to prevent the
    /// hook from re-sending the application of received recoil.
    /// </summary>
    private bool _isApplyingReceivedRecoil;

    public KnockbackComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<Recoil> recoil
    ) : base(netClient, entityId, gameObject) {
        _recoil = recoil;

        var recoilByDirectionMethod = typeof(Recoil).GetMethod(
            nameof(Recoil.RecoilByDirection),
            [typeof(int), typeof(float)]
        );

        if (recoilByDirectionMethod == null) {
            throw new MissingMethodException(
                typeof(Recoil).FullName,
                $"{nameof(Recoil.RecoilByDirection)}(int, float)"
            );
        }

        _recoilByDirectionHook = new Hook(recoilByDirectionMethod, RecoilOnRecoilByDirection);
    }

    /// <summary>
    /// Callback method for when recoil by direction is triggered on a recoil instance.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The recoil instance.</param>
    /// <param name="attackDirection">The cardinal direction of the attack (0=Right, 1=Up, 2=Left, 3=Down).</param>
    /// <param name="attackMagnitude">The magnitude multiplier of the attack.</param>
    private void RecoilOnRecoilByDirection(
        Action<Recoil, int, float> orig,
        Recoil self,
        int attackDirection,
        float attackMagnitude
    ) {
        var isOurs = self == _recoil.Host || self == _recoil.Client;

        // A remote player's attack is visualized on this machine by instantiating a live replica of their slash
        // (SlashBase.Play -> DamageAnimationEffect.FixDamageEnemies). That replica's DamageEnemies still strikes
        // the live enemy here and would drive a SECOND recoil on top of the authoritative one the attacking
        // player already networked. Worse, the replica's direction is computed from the remote-player puppet's
        // scale, and under Silksong's inverted facing convention (FaceRight => localScale.x = -1) that sign
        // disagrees with the live attacker, so the duplicate comes out 180° inverted — the enemy recoils TOWARD
        // the attacker on every screen. Drop it entirely: skip the apply (do not call orig) AND never network
        // it. The correct recoil for this enemy arrives over the network and is applied in Update(). This runs
        // before orig() so the spurious positional recoil is never applied. The check reuses the exact per-enemy
        // bracket that FixDamageEnemies uses to roll back the replica's damage, so it inherits its correctness.
        // Never suppress a recoil we are replaying from the network: that one is the authoritative,
        // world-space-correct recoil and must always be applied (it is also timed outside any replica's
        // StoreHp/RestoreHp bracket, so this guard is belt-and-suspenders for the invariant).
        if (isOurs && !_isApplyingReceivedRecoil) {
            var enemyObject = self == _recoil.Host ? GameObject.Host : GameObject.Client;
            if (DamageAnimationEffect.IsApplyingRemoteVisualHitTo(enemyObject)) {
                SyncLog.Log(SyncLog.Knockback,
                    $"suppress replica recoil | entity={EntityName()} dir={attackDirection} " +
                    "(remote attack visual; authoritative recoil arrives over network)");
                return;
            }
        }

        orig(self, attackDirection, attackMagnitude);

        if (!isOurs) {
            return;
        }

        if (_isApplyingReceivedRecoil) {
            return;
        }

        // Only network organic recoil on the object that is live on this side: the host object when we are
        // the scene host, the client (puppet) object when we are a scene client
        var isLiveObject = self == _recoil.Host ? !IsControlled : IsControlled;
        if (!isLiveObject) {
            return;
        }

        // Only network if the recoil ACTUALLY took effect. RecoilByDirection early-returns, leaving
        // state == Ready (IsRecoiling == false), when recoil is blocked for this direction
        // (IsLeft/Right/Up/DownBlocked) or otherwise suppressed. Those block flags are driven by the
        // enemy's FSM, which is DISABLED on puppets, so blindly replaying a suppressed recoil would jerk
        // the puppet around while the authoritative enemy stands firm — exactly the case for recoil-immune
        // bosses during their attacks. IsRecoiling (state == Recoiling || Frozen) is the authoritative
        // "it actually recoiled / froze in place" signal.
        if (!self.IsRecoiling) {
            return;
        }

        var data = new EntityNetworkData {
            Type = EntityComponentType.Knockback
        };

        data.Packet.Write((byte) attackDirection);
        data.Packet.Write(attackMagnitude);

        SendData(data);

        SyncLog.Log(SyncLog.Knockback,
            $"send recoil | entity={EntityName()} dir={attackDirection} magnitude={attackMagnitude} " +
            $"side={(IsControlled ? "puppet(local-hit)" : "host-object")}");
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        // Recoil is a transient impulse; the server caches the last data per component type and replays it
        // to players entering the scene, which would cause a phantom knockback on scene entry
        if (alreadyInSceneUpdate) {
            return;
        }

        var attackDirection = data.Packet.ReadByte();
        var attackMagnitude = data.Packet.ReadFloat();

        var recoil = IsControlled ? _recoil.Client : _recoil.Host;
        var gameObject = IsControlled ? GameObject.Client : GameObject.Host;

        if (recoil == null || gameObject == null || !gameObject.activeInHierarchy) {
            return;
        }

        SyncLog.Log(SyncLog.Knockback,
            $"apply recoil | entity={EntityName()} dir={attackDirection} magnitude={attackMagnitude} " +
            $"side={(IsControlled ? "puppet" : "host-object(authoritative)")}");

        _isApplyingReceivedRecoil = true;
        try {
            recoil.RecoilByDirection(attackDirection, attackMagnitude);
        } finally {
            _isApplyingReceivedRecoil = false;
        }
    }

    /// <summary>
    /// Readable entity identifier for trace logging.
    /// </summary>
    private string EntityName() {
        var hostObject = GameObject.Host;
        return hostObject != null ? hostObject.name : "?";
    }

    /// <inheritdoc />
    public override void Destroy() {
        _recoilByDirectionHook?.Dispose();
        _recoilByDirectionHook = null;
    }
}
