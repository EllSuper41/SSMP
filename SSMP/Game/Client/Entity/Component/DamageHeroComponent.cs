using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the damage that an entity deals to the player.
internal class DamageHeroComponent : EntityComponent {
    /// <summary>
    /// The host-client pair of <see cref="DamageHero"/> unity components of the entity.
    /// </summary>
    private readonly HostClientPair<DamageHero> _damageHero;

    /// <summary>
    /// The last value of damage dealt for the damage hero.
    /// </summary>
    private int _lastDamageDealt;

    /// <summary>
    /// The real (host-authored) damage value, cached at construction so it can be restored when we resolve to scene
    /// host. While <see cref="_rolePending"/> is set, the live damage is forced to 0.
    /// </summary>
    private readonly int _realDamageDealt;

    /// <summary>
    /// Whether this entity's scene role is still undetermined (set true at construction only for entities found
    /// during the role-undetermined window; false for post-resolution/networked spawns). During the window the host
    /// object may be left live with no networked owner, so its contact damage is neutralized (set to 0, no network
    /// effect) to stop a non-owner from damaging the local hero. On resolution to host the real damage is restored;
    /// on resolution to puppet the host object is disabled by Entity.InitializeClient so leaving damage at 0 is
    /// harmless. When false at construction (the default), no neutralization happens — puppet/host contact damage is
    /// unchanged.
    /// </summary>
    private bool _rolePending;

    public DamageHeroComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<DamageHero> damageHero,
        bool rolePending = false
    ) : base(netClient, entityId, gameObject) {
        _damageHero = damageHero;
        _realDamageDealt = damageHero.Host.damageDealt;
        _rolePending = rolePending;
        _lastDamageDealt = damageHero.Host.damageDealt;

        if (_rolePending) {
            // Neutralize contact damage during the role-undetermined window: a live, non-owned host enemy must not
            // damage the local hero before its role is known. Zeroing damageDealt has no network effect (OnUpdate
            // early-returns while IsControlled, which is true during the window). Restored on resolution to host.
            _damageHero.Host.damageDealt = 0;
            if (_damageHero.Client != null) {
                _damageHero.Client.damageDealt = 0;
            }

            _lastDamageDealt = 0;
        }

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Callback method to check for damage hero updates.
    /// </summary>
    private void OnUpdate() {
        if (IsControlled) {
            return;
        }

        if (GameObject.Host == null) {
            return;
        }

        var newDamageDealt = _damageHero.Host.damageDealt;
        if (newDamageDealt != _lastDamageDealt) {
            _lastDamageDealt = newDamageDealt;
            
            var data = new EntityNetworkData {
                Type = EntityComponentType.DamageHero
            };
            data.Packet.Write((byte) newDamageDealt);

            SendData(data);
        }
    }

    /// <inheritdoc />
    public override void InitializeHost(uint sceneHostEpoch) {
        // Role resolved to HOST: if we neutralized contact damage during the window, restore the real value now and
        // keep _lastDamageDealt in sync so OnUpdate does not send a spurious change for the restore itself. When the
        // role was already known at construction (not pending), this is a no-op preserving the original behavior.
        if (_rolePending) {
            _rolePending = false;

            if (_damageHero.Host != null) {
                _damageHero.Host.damageDealt = _realDamageDealt;
            }

            if (_damageHero.Client != null) {
                _damageHero.Client.damageDealt = _realDamageDealt;
            }

            _lastDamageDealt = _realDamageDealt;
        }
    }

    /// <inheritdoc />
    public override void InitializeClient(uint sceneHostEpoch) {
        // Role resolved to CLIENT (puppet): leave damage at 0. Entity.InitializeClient disables the host object, so
        // the neutralized damage is harmless; the puppet's damage is driven by networked DamageHero updates.
        _rolePending = false;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var damageDealt = data.Packet.ReadByte();
        _damageHero.Host.damageDealt = damageDealt;
        _damageHero.Client.damageDealt = damageDealt;
    }

    /// <inheritdoc />
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
