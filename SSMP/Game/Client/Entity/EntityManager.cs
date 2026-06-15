using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SSMP.Util;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Logging;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS0618 // Type or member is obsolete

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Manager class that handles entity creation, updating, networking and destruction.
/// </summary>
internal class EntityManager {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Dictionary mapping entity IDs to their respective entity instances.
    /// </summary>
    private readonly Dictionary<ushort, Entity> _entities;

    // Buffered updates waiting on an entity that hasn't registered yet, or on scene-host determination.
    private readonly Queue<BaseEntityUpdate> _pendingUpdates;

    /// <summary>
    /// Entities locally minted by a host FSM spawn DURING the role-undetermined window (before the server's
    /// AlreadyInScene reply resolves our scene role). Each entry pairs the registered entity with the EntityType of
    /// its spawner if known (null when the spawner type cannot be resolved, e.g. EnemySpawnerComponent spawns that
    /// carry no FsmAction). On role resolution this is reconciled: if we are the CLIENT (puppet), the entries are
    /// destroyed/unregistered so the authoritative host copy is the only instance (no fragile position matching); if
    /// we are the HOST, the entries with a known spawner type are (re)broadcast via SetEntitySpawn — closing the
    /// pre-existing gap where an eventual host's window spawn was never networked. Touched only on the Unity main
    /// thread (EntitySpawnEvent and the InitializeScene* paths both run on it), so it needs no synchronization.
    /// </summary>
    private readonly List<(Entity entity, EntityType? spawnerType)> _windowedSpawns = new();

    private Hook? _findGameObjectHook;

    // Both flags are set together in InitializeSceneHost / InitializeSceneClient.
    public bool IsSceneHost { get; private set; }
    private bool _sceneRoleDetermined;

    // The last scene-host epoch applied via InitializeSceneHost / InitializeSceneClient / BecomeSceneHost.
    private uint _sceneHostEpoch;

    /// <summary>
    /// The most recently applied scene-host epoch. Used so a client-side immediate
    /// InitializeSceneClient (e.g. on local death) can pass the real current epoch instead of 0,
    /// avoiding a same-frame HP update being dropped by epoch ordering.
    /// </summary>
    public uint CurrentSceneHostEpoch => _sceneHostEpoch;

    /// <summary>
    /// Gets all currently registered active entities.
    /// </summary>
    public Dictionary<ushort, Entity>.ValueCollection ActiveEntities => _entities.Values;

    public EntityManager(NetClient netClient) {
        _netClient = netClient;
        _entities = new Dictionary<ushort, Entity>();
        _pendingUpdates = new Queue<BaseEntityUpdate>();
    }

    /// <summary>
    /// Initialize the entity manager by initializing the processor and action hooks.
    /// </summary>
    public void Initialize() {
        EntityProcessor.Initialize(_entities, _netClient);
    }

    /// <summary>
    /// Register the hooks for entity-related operations.
    /// </summary>
    public void RegisterHooks() {
        FsmActionHooks.RegisterHooks();
        MusicComponent.RegisterHooks();

        EntityFsmActions.EntitySpawnEvent += OnGameObjectSpawned;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged += OnSceneChanged;

        _findGameObjectHook = new Hook(
            typeof(FindGameObject).GetMethod(
                nameof(FindGameObject.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ),
            OnFindGameObject
        );
    }

    /// <summary>
    /// Deregister the hooks for entity-related operations.
    /// </summary>
    public void DeregisterHooks() {
        FsmActionHooks.DeregisterHooks();
        MusicComponent.DeregisterHooks();

        EntityFsmActions.EntitySpawnEvent -= OnGameObjectSpawned;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnSceneChanged;

        _findGameObjectHook?.Dispose();
        _findGameObjectHook = null;

        ClearEntities();
    }

    /// <summary>
    /// Initializes the entity manager if we are the scene host.
    /// </summary>
    public void InitializeSceneHost(uint sceneHostEpoch = 0) {
        SyncLog.Log(SyncLog.Entity,
            $"scene role = HOST | epoch={sceneHostEpoch} entities={_entities.Count} (simulating + broadcasting)");
        IsSceneHost = true;
        _sceneHostEpoch = sceneHostEpoch;
        foreach (var entity in _entities.Values) entity.InitializeHost(sceneHostEpoch);
        _sceneRoleDetermined = true;

        // We are the scene host: any entity our FSM spawned during the role-undetermined window is a legitimate
        // host-owned spawn that was deferred (never networked). Broadcast each one now so puppets receive it. Spawns
        // whose spawner type could not be resolved (null) are skipped, preserving today's networking scope.
        foreach (var (entity, spawnerType) in _windowedSpawns) {
            if (!spawnerType.HasValue) continue;

            SyncLog.Log(SyncLog.Entity,
                $"send entitySpawn (windowed, role resolved HOST) | spawner={spawnerType.Value} " +
                $"spawned={entity.Type} id={entity.Id}");
            _netClient.UpdateManager.SetEntitySpawn(entity.Id, spawnerType.Value, entity.Type);
        }

        _windowedSpawns.Clear();

        DrainPendingUpdates();
    }

    /// <summary>
    /// Initializes the entity manager if we are a scene client.
    /// </summary>
    public void InitializeSceneClient(uint sceneHostEpoch = 0) {
        SyncLog.Log(SyncLog.Entity,
            $"scene role = CLIENT | epoch={sceneHostEpoch} entities={_entities.Count} (puppets, receiving)");
        IsSceneHost = false;
        _sceneHostEpoch = sceneHostEpoch;
        foreach (var entity in _entities.Values) entity.InitializeClient(sceneHostEpoch);
        _sceneRoleDetermined = true;

        // We are a puppet: any entity our FSM spawned during the role-undetermined window is an ORPHAN — the real
        // scene host independently spawned the same minion with its own authoritative ID and delivers it via the
        // AlreadyInScene EntitySpawnList (or a later SetEntitySpawn). Destroy and unregister our local copy so the
        // host's copy is the only instance, eliminating the duplicate by identity (no fragile spawner+position
        // matching).
        foreach (var (entity, _) in _windowedSpawns) {
            SyncLog.Log(SyncLog.Entity,
                $"destroy windowed orphan (role resolved CLIENT) | spawned={entity.Type} id={entity.Id}");
            entity.Destroy();
            _entities.Remove(entity.Id);
        }

        _windowedSpawns.Clear();

        DrainPendingUpdates();
    }

    /// <summary>
    /// Updates the entity manager if we become the scene host.
    /// </summary>
    public void BecomeSceneHost(uint sceneHostEpoch = 0) {
        SyncLog.Log(SyncLog.Entity,
            $"scene role = HOST (transferred) | epoch={sceneHostEpoch} entities={_entities.Count}");
        IsSceneHost = true;
        _sceneHostEpoch = sceneHostEpoch;
        foreach (var entity in _entities.Values) entity.MakeHost(sceneHostEpoch);

        // Immediately refresh targeting fields for all active enemies
        GamePatcher.ForceImmediateRetarget();
    }

    /// <summary>
    /// Attempts to spawn a networked entity. No-ops if the ID is already registered (assumed spawned by action).
    /// </summary>
    public void SpawnEntity(ushort id, EntityType spawningType, EntityType spawnedType) {
        SyncLog.Log(SyncLog.Entity, $"recv entitySpawn | id={id} spawner={spawningType} spawned={spawnedType}");

        if (_entities.ContainsKey(id)) {
            Logger.Info($"  Entity with ID {id} already exists, assuming it has been spawned by action");
            return;
        }

        // Any entity of the same type works as a template — FSMs and components are identical across instances.
        var templateEntity = _entities.Values.FirstOrDefault(e => e.Type == spawningType);
        if (templateEntity == null) {
            Logger.Warn("Could not find entity with same type for spawning");
            return;
        }

        var spawnedObject = EntitySpawner.SpawnEntityGameObject(
            spawningType,
            spawnedType,
            templateEntity.Object.Client,
            templateEntity.GetClientFsms()
        );

        var processor = new EntityProcessor {
            GameObject = spawnedObject,
            IsSceneHost = IsSceneHost,
            IsSceneHostDetermined = _sceneRoleDetermined,
            LateLoad = true,
            SpawnedId = id
        }.Process();

        if (!processor.Success) {
            Logger.Warn($"Could not process game object of spawned entity: {spawnedObject.name}");
        }
    }

    /// <summary>
    /// Applies an unreliable entity update (position, scale, animation).
    /// Returns false and buffers the update if the entity isn't ready yet.
    /// </summary>
    public bool HandleEntityUpdate(EntityUpdate update, bool alreadyInSceneUpdate = false) {
        // Scene host owns entity state; updates from peers are ignored.
        if (IsSceneHost) return true;

        if (!_entities.TryGetValue(update.Id, out var entity) || !_sceneRoleDetermined) {
            LogBufferingReason(update.Id);
            _pendingUpdates.Enqueue(update);
            return false;
        }

        if (update.UpdateTypes.Contains(EntityUpdateType.Position))
            entity.UpdatePosition(update.Position);

        if (update.UpdateTypes.Contains(EntityUpdateType.Scale))
            entity.UpdateScale(update.Scale);

        if (update.UpdateTypes.Contains(EntityUpdateType.Animation))
            entity.UpdateAnimation(
                update.AnimationId,
                (tk2dSpriteAnimationClip.WrapMode) update.AnimationWrapMode,
                alreadyInSceneUpdate
            );

        return true;
    }

    /// <summary>
    /// Applies a reliable entity update (active state, host FSM data, generic data).
    /// Returns false and buffers the update if the entity isn't ready yet.
    /// </summary>
    public bool HandleReliableEntityUpdate(ReliableEntityUpdate update, bool alreadyInSceneUpdate = false) {
        if (!_entities.TryGetValue(update.Id, out var entity) || !_sceneRoleDetermined) {
            LogBufferingReason(update.Id);
            _pendingUpdates.Enqueue(update);
            return false;
        }

        // Active state and host FSM are driven by the scene host; clients are consumers only.
        if (!IsSceneHost) {
            if (update.UpdateTypes.Contains(EntityUpdateType.Active))
                entity.UpdateIsActive(update.IsActive);

            if (update.UpdateTypes.Contains(EntityUpdateType.HostFsm))
                entity.UpdateHostFsmData(update.HostFsmData);
        }

        if (update.UpdateTypes.Contains(EntityUpdateType.Data))
            entity.UpdateData(update.GenericData, alreadyInSceneUpdate);

        return true;
    }

    /// <summary>
    /// Callback method for when the scene changes. Will clear existing entities and start checking for
    /// new entities.
    /// </summary>
    /// <param name="oldScene">The old scene.</param>
    /// <param name="newScene">The new scene.</param>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        Logger.Info("Scene changed, clearing registered entities");
        ClearEntities();

        if (!_netClient.IsConnected) return;

        _sceneRoleDetermined = false;
        FindEntitiesInScene(newScene, lateLoad: false);
        DrainPendingUpdates();
    }

    /// <summary>
    /// Callback method for when a scene is loaded.
    /// </summary>
    /// <param name="scene">The scene that is loaded.</param>
    /// <param name="mode">The load scene mode.</param>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        var activeScene = SceneManager.GetActiveScene().name;

        // Boss/boss-defeated scenes share a name prefix with the base scene; skip unrelated additively-loaded scenes.
        if (!scene.name.StartsWith(activeScene) || scene.name.Equals(activeScene)) return;

        Logger.Info($"Additional scene loaded ({scene.name}), looking for entities");
        FindEntitiesInScene(scene, lateLoad: true);
        DrainPendingUpdates();
    }

    /// <summary>
    /// Find entities to register in the given scene.
    /// </summary>
    /// <param name="scene">The scene to find entities in.</param>
    /// <param name="lateLoad">Whether this scene was loaded late.</param>
    private void FindEntitiesInScene(Scene scene, bool lateLoad) {
        var objects = CollectEntityCandidates(scene);

        foreach (var obj in objects) {
            new EntityProcessor {
                GameObject = obj,
                IsSceneHost = IsSceneHost,
                IsSceneHostDetermined = _sceneRoleDetermined,
                LateLoad = lateLoad
            }.Process();
        }
    }

    /// <summary>
    /// Gathers all GameObjects in the scene that are candidates for entity registration.
    /// Handles EnemyDeathEffects corpse pre-instantiation and several component-driven
    /// object types (Climber, Walker, BigCentipede, CameraLockArea, DreamPlatform).
    /// </summary>
    private static IEnumerable<GameObject> CollectEntityCandidates(Scene scene) {
        var fromDeathEffects = Object.FindObjectsOfType<EnemyDeathEffects>()
                                     .Where(e => e.gameObject.scene == scene)
                                     .SelectMany(ExpandDeathEffects);

        var fromFsms = Object.FindObjectsOfType<PlayMakerFSM>(true)
                             .Where(fsm => fsm.gameObject.scene == scene)
                             .Select(fsm => fsm.gameObject);

        var fromComponents = new[] {
            Object.FindObjectsOfType<Climber>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<Walker>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<BigCentipede>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<CameraLockArea>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<DreamPlatform>(true).Select(c => c.gameObject),
        }.SelectMany(x => x);

        return fromDeathEffects
               // Expand each object to itself and all children
               .Concat(fromFsms)
               .SelectMany(obj => obj == null ? Array.Empty<GameObject>() : obj.GetChildren().Prepend(obj))
               .Concat(fromComponents)
               .Where(obj => obj.scene == scene)
               .Distinct();
    }


    private static IEnumerable<GameObject> ExpandDeathEffects(EnemyDeathEffects deathEffects) {
        try {
            deathEffects.PreInstantiate();
        } catch (Exception) {
            // PersonalObjectPool objects can't be pre-instantiated this early; fall back to the root only.
            return [deathEffects.gameObject];
        }

        // TODO: CorpsePrefab is a prefab reference, may not be compatible with original code.
        var corpse = deathEffects.CorpsePrefab;
        return [deathEffects.gameObject, corpse];
    }

    /// <summary>
    /// Callback method for when a game object is spawned from an existing entity.
    /// </summary>
    /// <param name="details">The entity spawn details containing how the entity was spawned.</param>
    /// <returns>Whether an entity was registered from this spawn.</returns>
    private bool OnGameObjectSpawned(EntitySpawnDetails details) {
        if (_entities.Values.Any(e => e.Object.Host == details.GameObject)) {
            Logger.Debug("Spawned object was already a registered entity");
            return true;
        }

        var processor = new EntityProcessor {
            GameObject = details.GameObject,
            IsSceneHost = IsSceneHost,
            IsSceneHostDetermined = _sceneRoleDetermined,
            LateLoad = true
        }.Process();

        if (!processor.Success) return false;

        // Role-undetermined window: we cannot yet know whether this local FSM spawn belongs to us (we resolve to
        // scene host) or to the real host (we resolve to puppet). DEFER the keep/destroy/network decision instead of
        // dropping it. Keep the Process() result so the entity exists if we turn out to be host, but DO NOT broadcast
        // SetEntitySpawn yet. Record it (with the spawner type if resolvable) so InitializeSceneHost/Client can
        // reconcile by identity on resolution: a puppet destroys it (the host's authoritative copy wins), an eventual
        // host broadcasts it (closing the gap where window spawns were never networked).
        if (!_sceneRoleDetermined) {
            var topLevelWindow = processor.Entities[0];

            // Only FsmAction spawns carry an Action from which a spawner registry entry (and EntityType) can be
            // resolved; EnemySpawnerComponent/SpawnJar spawns lack it, so they record a null spawner type and are
            // skipped by the host rebroadcast (matching today's scope where only FsmAction spawns are networked).
            EntityType? spawnerType = null;
            if (details.Type == EntitySpawnType.FsmAction
                && EntityRegistry.TryGetEntry(details.Action.Fsm.GameObject, out var windowEntry)) {
                spawnerType = windowEntry.Type;
            }

            _windowedSpawns.Add((topLevelWindow, spawnerType));
            SyncLog.Log(SyncLog.Entity,
                $"spawn DEFERRED (role undetermined) | spawned={details.GameObject.name}({topLevelWindow.Type}) " +
                $"id={topLevelWindow.Id} spawnerType={(spawnerType.HasValue ? spawnerType.Value.ToString() : "unknown")}");
            return true;
        }

        if (!IsSceneHost) {
            Logger.Warn("Game object was spawned while not scene host, this shouldn't happen");
            return false;
        }

        if (details.Type != EntitySpawnType.FsmAction) {
            Logger.Error($"Invalid EntitySpawnDetails type: {details.Type}");
            return false;
        }

        if (!EntityRegistry.TryGetEntry(details.Action.Fsm.GameObject, out var entry)) {
            SyncLog.Log(SyncLog.Entity,
                $"spawn NOT registered | object={details.GameObject.name} reason=no-registry-entry-for-spawner");
            return false;
        }

        var topLevel = processor.Entities[0];
        SyncLog.Log(SyncLog.Entity,
            $"send entitySpawn | spawner={details.Action.Fsm.GameObject.name}({entry.Type}) " +
            $"spawned={details.GameObject.name}({topLevel.Type}) id={topLevel.Id}");
        _netClient.UpdateManager.SetEntitySpawn(topLevel.Id, entry.Type, topLevel.Type);

        return true;
    }

    /// <summary>
    /// Replays buffered updates for entities that are now registered and whose scene role is known.
    /// Updates that still can't be applied are left in the queue.
    /// </summary>
    private void DrainPendingUpdates() {
        // Iterate a snapshot count; newly buffered updates (HandleEntityUpdate returning false) stay in the queue.
        var count = _pendingUpdates.Count;
        for (var i = 0; i < count; i++) {
            var update = _pendingUpdates.Dequeue();

            var applied = update switch {
                EntityUpdate eu => HandleEntityUpdate(eu),
                ReliableEntityUpdate r => HandleReliableEntityUpdate(r),
                _ => true
            };

            if (!applied) {
                // Still not applicable; it was re-enqueued by Handle*..nothing to do.
            }
        }
    }

    /// <summary>
    /// Clears all the registered entities, and resets static components.
    /// </summary>
    private void ClearEntities() {
        foreach (var entity in _entities.Values) entity.Destroy();
        _entities.Clear();
        _pendingUpdates.Clear();
        // Windowed spawns are entities in _entities (destroyed above); just drop the deferred-reconcile references so
        // nothing leaks across scenes.
        _windowedSpawns.Clear();
        MusicComponent.ClearInstance();
    }

    private void LogBufferingReason(ushort id) {
        var reason = _sceneRoleDetermined
            ? $"Could not find entity ({id}) to apply update for; storing update for now"
            : "Scene host is not determined yet to apply update; storing update for now";
        Logger.Debug(reason);
    }

    // Patch: intercept FindGameObject.OnEnter so that entities the system has made inactive are still findable.
    private void OnFindGameObject(Action<FindGameObject> orig, FindGameObject self) {
        orig(self);

        if (self.store.Value != null) return;

        // This particular FSM state finds a Roller via tag; letting our code handle it breaks Blocker Control logic.
        if (self.State.Name == "Can Roller?" && self.Fsm.Name == "Blocker Control") return;

        Logger.Debug($"OnFindGameObject, find failed: looking for '{self.objectName.Value}'");

        // Tag-based finds won't match our entities by name.
        if (self.withTag.Value != "Untagged") return;

        foreach (var entity in _entities.Values) {
            var host = entity.Object.Host;
            if (host != null && host.name == self.objectName.Value) {
                self.store.Value = host;
                Logger.Debug($"  Name matches host object of entity: ({entity.Id}, {entity.Type})");
                return;
            }
        }

        Logger.Debug("  Name did not match any entity");
    }
}
