using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using SSMP.Collection;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Serialization;
using SSMP.Util;
using TeamCherry.SharedUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using SyncLog = SSMP.Logging.SyncLog;
using MapZone = GlobalEnums.MapZone;
using Object = UnityEngine.Object;

// ReSharper disable AssignNullToNotNullAttribute
#pragma warning disable CS8620 // Argument cannot be used for parameter due to differences in the nullability of
// reference types.
#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

namespace SSMP.Game.Client.Save;

/// <summary>
/// Class that manages save data synchronisation.
/// </summary>
internal class SaveManager {
    /// <summary>
    /// The save data instance that contains mappings for what to sync and their indices.
    /// </summary>
    private static SaveDataMapping SaveDataMapping => SaveDataMapping.Instance;

    /// <summary>
    /// The net client instance to send save updates.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The entity manager to check whether we are scene host.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// The save changes instance to apply immediate in-world changes from received save data.
    /// </summary>
    private readonly SaveChanges _saveChanges;

    /// <summary>
    /// List of data classes for each FSM that has a persistent int/bool or geo rock attached to it.
    /// </summary>
    private readonly List<PersistentFsmData> _persistentFsmData;

    /// <summary>
    /// Dictionary of BossSequenceDoor.Completion structs in the PlayerData for comparing changes against.
    /// </summary>
    private readonly Dictionary<string?, BossSequenceDoor.Completion> _bsdCompHashes;

    /// <summary>
    /// Dictionary of BossStatue.Completion structs in the PlayerData for comparing changes against.
    /// </summary>
    private readonly Dictionary<string?, BossStatue.Completion> _bsCompHashes;

    /// <summary>
    /// Dictionary of hash codes for list variables in the PlayerData for comparing changes against.
    /// </summary>
    private readonly Dictionary<string?, int> _listHashes;

    /// <summary>
    /// Per-collectable-item cumulative amount this local player has consumed below the synced (server) totals.
    ///
    /// Collectables (CollectableItemsData) is the only Additive field whose per-item Amount can DECREASE in vanilla
    /// (use / turn-in). The outbound delta is positive-only (a consume is never networked) and the server keeps an
    /// absolute per-key SUM total that it re-broadcasts as the full absolute map to all peers including the consumer,
    /// so a naive increase-only max() apply would RESTORE an item the local player just consumed the moment anyone
    /// picks up any collectable. This APPLY-side offset records what the local player consumed so the apply never
    /// raises an item above (serverTotal - locallyConsumed). State owned entirely by the apply path; independent of
    /// _lastPlayerData / _listHashes (send gating / cache-advance), so it never affects the send side.
    /// </summary>
    private readonly Dictionary<string, int> _collectablesConsumedOffset;

    /// <summary>
    /// Per-collectable-item snapshot of the live Amount the apply path last SET each item to. Since the apply path is
    /// the only thing that RAISES live Collectables and vanilla consume is the only thing that LOWERS them, a drop of
    /// the live Amount below this baseline between applies is exactly new local consumption to fold into
    /// <see cref="_collectablesConsumedOffset"/>. Initialised to the current live Amount the first time an item is
    /// seen so a player who already holds items at connect registers no phantom consume.
    /// </summary>
    private readonly Dictionary<string, int> _collectablesAppliedTo;

    /// <summary>
    /// List of FieldInfo for fields in PlayerData that are simple values that should be synced. Used for looping
    /// over to check for changes and network those changes.
    /// </summary>
    private readonly List<FieldInfo> _playerDataSimpleSyncFields;

    /// <summary>
    /// Dictionary of variable names mapped to FieldInfo for fields in PlayerData that are compound values that should
    /// be synced. Used for resetting the instance of PlayerData for last values and checking for updates to compound
    /// values.
    /// </summary>
    private readonly List<FieldInfo> _playerDataCompoundSyncFields;

    /// <summary>
    /// PlayerData instance that contains the last values of the currently used PlayerData for comparison checking.
    /// </summary>
    private PlayerData? _lastPlayerData;

    /// <summary>
    /// Whether the connected server is running in full-synchronisation mode. Cached from the ServerInfo received on
    /// connect so the per-frame update loop can gate the one-shot baseline snapshot flush on it.
    /// </summary>
    private bool _fullSynchronisation;

    /// <summary>
    /// Whether the one-shot complete baseline snapshot of all Sync:true SyncType=Player fields has been sent for the
    /// current session. The per-frame delta path only sends fields that CHANGE during the session, so the host's
    /// per-player record would otherwise be sparse and missing fields fall back to their InitialValue on rejoin
    /// (e.g. respawnScene -> 'Tut_01', teleporting the client to the start). Flushing a full snapshot once, after the
    /// client is settled in-world, makes the host's record complete. Reset on DeregisterHooks (disconnect/leave) so a
    /// reconnect re-flushes a fresh complete snapshot.
    /// </summary>
    private bool _fullSnapshotSent;

    /// <summary>
    /// List of HashSet variables in PlayerData.
    /// </summary>
    private static readonly List<string> HashSetVariables = [
        "scenesEncounteredBench",
        "scenesEncounteredCocoon",
        "scenesMapped",
        "scenesVisited"
    ];

    /// <summary>
    /// List of named-int collection variables in PlayerData. These are per-key int maps (enemy kill counts and
    /// collectable amounts) that are shared additively via a per-key SUM on the server.
    /// </summary>
    private static readonly List<string?> NamedIntVariables = [
        "Collectables",
        "EnemyJournalKillData"
    ];

    /// <summary>
    /// Whether the player is hosting the server, which means that player specific save data is not networked
    /// to the server.
    /// </summary>
    public bool IsHostingServer { get; set; }

    public SaveManager(NetClient netClient, EntityManager entityManager) {
        _netClient = netClient;
        _entityManager = entityManager;
        _saveChanges = new SaveChanges();

        _persistentFsmData = [];
        _bsdCompHashes = new Dictionary<string?, BossSequenceDoor.Completion>();
        _bsCompHashes = new Dictionary<string?, BossStatue.Completion>();
        _listHashes = new Dictionary<string?, int>();
        _collectablesConsumedOffset = new Dictionary<string, int>();
        _collectablesAppliedTo = new Dictionary<string, int>();
        _playerDataSimpleSyncFields = [];
        _playerDataCompoundSyncFields = [];
    }

    /// <summary>
    /// Initializes the save manager by loading the save data json.
    /// </summary>
    public void Initialize() {
        _netClient.ConnectEvent += OnConnect;

        foreach (var field in typeof(PlayerData).GetFields()) {
            var fieldName = field.Name;

            if (!SaveDataMapping.PlayerDataVarProperties.TryGetValue(fieldName, out var varProps)
                || !varProps.Sync
               ) {
                continue;
            }

            var compoundField = SaveDataMapping.StringListVariables.Contains(fieldName) ||
                                SaveDataMapping.BossSequenceDoorCompletionVariables.Contains(fieldName) ||
                                SaveDataMapping.BossStatueCompletionVariables.Contains(fieldName) ||
                                SaveDataMapping.VectorListVariables.Contains(fieldName) ||
                                SaveDataMapping.IntListVariables.Contains(fieldName) ||
                                HashSetVariables.Contains(fieldName) ||
                                NamedIntVariables.Contains(fieldName);

            if (compoundField) {
                _playerDataCompoundSyncFields.Add(field);
            } else {
                _playerDataSimpleSyncFields.Add(field);
            }
        }
    }

    /// <summary>
    /// Register the relevant hooks for save-related operations.
    /// </summary>
    public void RegisterHooks() {
        SceneManager.activeSceneChanged += OnSceneChanged;

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdatePlayerData;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdatePersistents;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdateCompounds;
    }

    /// <summary>
    /// Deregister the relevant hooks for save-related operations.
    /// </summary>
    public void DeregisterHooks() {
        SceneManager.activeSceneChanged -= OnSceneChanged;

        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdatePlayerData;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdatePersistents;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdateCompounds;

        // Arm a fresh complete baseline snapshot for the next session. This hook is run on disconnect/leave
        // (ClientManager.OnDisconnect -> DeregisterHooks), so a reconnect re-flushes a full snapshot rather than
        // relying on the stale per-session flag.
        _fullSnapshotSent = false;
        _fullSynchronisation = false;

        // Drop the per-item collectable consume-offset / applied-to baselines so the next session (which begins with a
        // full SetSaveWithData resync) starts clean. Stale offsets from a prior session would otherwise wrongly
        // suppress legitimate amounts after the reconnect's full apply.
        _collectablesConsumedOffset.Clear();
        _collectablesAppliedTo.Clear();
    }

    /// <summary>
    /// Callback method for when the player connects to a server, so we can reset the player data.
    /// </summary>
    /// <param name="serverInfo">The server info received from the server.</param>
    private void OnConnect(ServerInfo serverInfo) {
        _fullSynchronisation = serverInfo.FullSynchronisation;

        if (serverInfo.FullSynchronisation) {
            ResetLastPlayerData();
        }
    }

    /// <summary>
    /// Resets the PlayerData instance that stores the last values of all synchronised fields.
    /// </summary>
    private void ResetLastPlayerData() {
        var pd = PlayerData.instance;

        // A full-sync (re)connect re-applies the entire server state via SetSaveWithData; any consume-offsets carried
        // over from a prior session would wrongly suppress that full apply. Reset them so the apply path re-baselines
        // appliedTo from the freshly-applied live values.
        _collectablesConsumedOffset.Clear();
        _collectablesAppliedTo.Clear();

        // Allocate a blank PlayerData WITHOUT running any constructor: Silksong's PlayerData() is public and
        // has side effects (SetupNewPlayerData), and the old NonPublic-ctor lookup found nothing on this game
        // so _lastPlayerData stayed null -> the compound/additive diff (HashSets + quest progress) threw an NRE
        // every frame. GetUninitializedObject gives a clean snapshot object that we then fill with synced fields.
        _lastPlayerData = (PlayerData) System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(PlayerData));

        foreach (var field in _playerDataSimpleSyncFields) {
            var value = field.GetValue(pd);
            field.SetValue(_lastPlayerData, value);
        }

        foreach (var field in _playerDataCompoundSyncFields) {
            var value = field.GetValue(pd);
            // PlayerData.instance can still have null compound fields here when hosting/joining from the main
            // menu before a save is loaded. Snapshot null as-is rather than crashing OnConnect in
            // GetCompoundCopy; the value is picked up by the normal change diff once it becomes non-null in-game.
            field.SetValue(_lastPlayerData, value == null ? null : GetCompoundCopy(value));
        }
    }

    /// <summary>
    /// Update hook to check for changes in the PlayerData instance.
    /// </summary>
    private void OnUpdatePlayerData() {
        var pd = PlayerData.instance;
        if (_lastPlayerData == null) {
            return;
        }

        var gm = global::GameManager.instance;
        if (!gm) {
            return;
        }

        if (gm.GameState == GameState.MAIN_MENU) {
            return;
        }

        // One-shot complete baseline flush. The loop below only networks fields that CHANGE during the session, so
        // any Sync:true SyncType=Player field that never changes is never sent and the host's per-player record stays
        // sparse -> on rejoin the client adopts that sparse record and the holes fall back to InitialValue (e.g.
        // respawnScene -> 'Tut_01', teleporting the client to the start of the game). Once we are actually in-play
        // (GameState.PLAYING, NOT loading/cutscene/menu), connected to a full-sync server as a non-host client, and the
        // adopted in-game save is live, force-send a COMPLETE snapshot of every synced Player field's CURRENT value so
        // the host's record becomes complete. Gated by _fullSnapshotSent so it runs exactly once per session and cannot
        // spam every frame. Placed AFTER the MAIN_MENU guard (and after adoption/load via SetSaveWithData) so it sends
        // the real adopted in-game values, never menu/default values.
        if (!_fullSnapshotSent
            && !IsHostingServer
            && _fullSynchronisation
            && _netClient.IsConnected
            && gm.GameState == GameState.PLAYING) {
            FlushFullPlayerSnapshot();
            _fullSnapshotSent = true;
        }

        foreach (var field in _playerDataSimpleSyncFields) {
            var currentValue = field.GetValue(pd);
            var lastValue = field.GetValue(_lastPlayerData);

            if (Equals(currentValue, lastValue)) {
                continue;
            }

            Logger.Debug($"PlayerData value changed from: {lastValue} to {currentValue}");

            field.SetValue(_lastPlayerData, currentValue);

            if (field.FieldType == typeof(int)) {
                CheckSendSaveUpdate(
                    field.Name,
                    () => EncodeSaveDataValue(field.Name, currentValue),
                    () => {
                        var delta = (int) currentValue - (int) lastValue;
                        return EncodeSaveDataValue(field.Name, delta);
                    }
                );
            } else {
                CheckSendSaveUpdate(field.Name, () => EncodeSaveDataValue(field.Name, currentValue));
            }
        }
    }

    /// <summary>
    /// Force-send a COMPLETE snapshot of every Sync:true SyncType=Player simple field's CURRENT value, regardless of
    /// whether it changed this session. Reuses the SAME synced-field set and the SAME low-level send path as the
    /// per-frame delta loop (<see cref="CheckSendSaveUpdate"/> -> UpdateManager.SetSaveUpdate); it does NOT introduce a
    /// new packet type. The per-frame loop's "value unchanged" early-return lives in its OWN body (the Equals check in
    /// <see cref="OnUpdatePlayerData"/>), so calling CheckSendSaveUpdate directly here bypasses ONLY that guard while
    /// still honoring everything CheckSendSaveUpdate enforces (not connected, permadeath, !Sync, scene-host gate, index
    /// lookup). Deliberately passes NO deltaEncodeFunc so additive fields send their ABSOLUTE current value (a delta
    /// here would be wrong/zero). Only simple Player fields are flushed; the compound fields keep their existing
    /// per-frame additive delta handling in <see cref="OnUpdateCompounds"/>.
    /// </summary>
    private void FlushFullPlayerSnapshot() {
        var pd = PlayerData.instance;

        Logger.Info($"Flushing full player save snapshot ({_playerDataSimpleSyncFields.Count} fields)");

        foreach (var field in _playerDataSimpleSyncFields) {
            var currentValue = field.GetValue(pd);

            // Keep the last-values snapshot in sync with what we just force-sent so the delta loop below does not
            // immediately re-send the same value, mirroring how OnUpdatePlayerData advances _lastPlayerData.
            field.SetValue(_lastPlayerData, currentValue);

            // No delta func: send the absolute current value even for additive fields.
            CheckSendSaveUpdate(field.Name, () => EncodeSaveDataValue(field.Name, currentValue));
        }
    }

    /// <summary>
    /// Callback method for when the scene changes. Used to check for GeoRock, PersistentInt and PersistentBool
    /// instances in the scene.
    /// </summary>
    /// <param name="oldScene">The old scene.</param>
    /// <param name="newScene">The new scene.</param>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        _persistentFsmData.Clear();

        foreach (var geoRock in Object.FindObjectsByType<GeoRock>(FindObjectsSortMode.None)) {
            var geoRockObject = geoRock.gameObject;

            if (geoRockObject.scene != newScene) {
                continue;
            }

            var persistentItemData = new PersistentItemKey {
                Id = geoRockObject.name,
                SceneName = global::GameManager.GetBaseSceneName(geoRockObject.scene.name)
            };

            Logger.Info($"Found Geo Rock in scene: {persistentItemData}");

            var fsm = geoRock.GetComponent<PlayMakerFSM>();
            if (!fsm) {
                Logger.Info("  Could not find FSM belonging to Geo Rock object, skipping");
                continue;
            }

            var fsmInt = fsm.FsmVariables.GetFsmInt("Hits");

            var persistentFsmData = new PersistentFsmData {
                PersistentItemKey = persistentItemData,
                GetCurrentInt = () => fsmInt.Value,
                SetCurrentInt = value => fsmInt.Value = value,
                LastIntValue = fsmInt.Value
            };

            _persistentFsmData.Add(persistentFsmData);
        }

        foreach (var persistentBoolItem in Object.FindObjectsByType<PersistentBoolItem>(FindObjectsSortMode.None)) {
            var itemObject = persistentBoolItem.gameObject;

            if (itemObject.scene != newScene) {
                continue;
            }

            var persistentItemData = new PersistentItemKey {
                Id = itemObject.name,
                SceneName = global::GameManager.GetBaseSceneName(itemObject.scene.name)
            };

            Logger.Info($"Found persistent bool in scene: {persistentItemData}");

            Func<bool>? getCurrentBoolFunc = null;
            Action<bool>? setCurrentBoolAction = null;

            var fsm = FSMUtility.FindFSMWithPersistentBool(itemObject.GetComponents<PlayMakerFSM>());
            if (fsm) {
                var fsmBool = fsm.FsmVariables.GetFsmBool("Activated");
                getCurrentBoolFunc = () => fsmBool.Value;
                setCurrentBoolAction = value => fsmBool.Value = value;
            }

            var vinePlatform = itemObject.GetComponent<VinePlatform>();
            if (vinePlatform) {
                getCurrentBoolFunc = () => vinePlatform.activated;
                setCurrentBoolAction = value => vinePlatform.activated = value;
            }

            var breakable = itemObject.GetComponent<Breakable>();
            if (breakable) {
                getCurrentBoolFunc = () => breakable.isBroken;
                setCurrentBoolAction = value => breakable.isBroken = value;
            }

            var dreamPlant = itemObject.GetComponent<DreamPlant>();
            if (dreamPlant) {
                getCurrentBoolFunc = () => dreamPlant.completed;
                setCurrentBoolAction = value => dreamPlant.completed = value;
            }

            var dreamPlantOrb = itemObject.GetComponent<DreamPlantOrb>();
            if (dreamPlantOrb) {
                getCurrentBoolFunc = () => dreamPlantOrb.pickedUp;
                setCurrentBoolAction = value => dreamPlantOrb.pickedUp = value;
            }

            if (getCurrentBoolFunc == null || setCurrentBoolAction == null) {
                continue;
            }

            var persistentFsmData = new PersistentFsmData {
                PersistentItemKey = persistentItemData,
                GetCurrentBool = getCurrentBoolFunc,
                SetCurrentBool = setCurrentBoolAction,
                LastBoolValue = getCurrentBoolFunc.Invoke()
            };

            _persistentFsmData.Add(persistentFsmData);
        }

        foreach (var persistentIntItem in Object.FindObjectsByType<PersistentIntItem>(FindObjectsSortMode.None)) {
            var itemObject = persistentIntItem.gameObject;

            if (itemObject.scene != newScene) {
                continue;
            }

            var persistentItemData = new PersistentItemKey {
                Id = itemObject.name,
                SceneName = global::GameManager.GetBaseSceneName(itemObject.scene.name)
            };

            Logger.Info($"Found persistent int in scene: {persistentItemData}");

            var fsm = FSMUtility.FindFSMWithPersistentBool(itemObject.GetComponents<PlayMakerFSM>());
            if (!fsm) {
                Logger.Info("  Could not find FSM belonging to persistent int object, skipping");
                continue;
            }

            var fsmInt = fsm.FsmVariables.GetFsmInt("Value");

            var persistentFsmData = new PersistentFsmData {
                PersistentItemKey = persistentItemData,
                GetCurrentInt = () => fsmInt.Value,
                SetCurrentInt = value => fsmInt.Value = value,
                LastIntValue = fsmInt.Value
            };

            _persistentFsmData.Add(persistentFsmData);
        }
    }

    /// <summary>
    /// Checks if a save update should be sent and send it using the encode function to encode the value of the
    /// changed variable.
    /// </summary>
    /// <param name="name">The name of the variable that was changed.</param>
    /// <param name="encodeFunc">Function to encode the value of the variable to a byte array.</param>
    /// <param name="deltaEncodeFunc">Function to encode the delta value of the variable of the type is applicable.
    /// </param>
    private void CheckSendSaveUpdate(string name, Func<byte[]> encodeFunc, Func<byte[]>? deltaEncodeFunc = null) {
        // If we are not connected or the 'permadeathMode' is 2, meaning we have broken/lost Steel Soul
        if (!_netClient.IsConnected || PlayerData.instance.GetInt("permadeathMode") == 2) {
            return;
        }

        if (!SaveDataMapping.PlayerDataVarProperties.TryGetValue(name, out var varProps)) {
            Logger.Info($"Not in save data values, not sending save update ({name})");
            return;
        }

        if (!varProps.Sync) {
            Logger.Info($"Value should not sync, not sending save update ({name})");
            return;
        }

        // If we should do the scene host check and the player is not scene host, skip sending
        if (!varProps.IgnoreSceneHost && !_entityManager.IsSceneHost) {
            Logger.Info($"Not scene host, but required, not sending save update ({name})");
            return;
        }

        if (!SaveDataMapping.PlayerDataIndices.TryGetValue(name, out var index)) {
            Logger.Info($"Cannot find save data index, not sending save update ({name})");
            return;
        }

        Func<byte[]> toUseEncodeFunc;
        if (varProps.Additive && deltaEncodeFunc != null) {
            toUseEncodeFunc = deltaEncodeFunc;
        } else {
            toUseEncodeFunc = encodeFunc;
        }

        SyncLog.Log(SyncLog.World,
            $"send playerData | var={name} index={index} policy={varProps.SyncType} " +
            $"additive={varProps.Additive} ignoreSceneHost={varProps.IgnoreSceneHost}");

        _netClient.UpdateManager.SetSaveUpdate(
            index,
            toUseEncodeFunc.Invoke()
        );
    }

    /// <summary>
    /// Called every unity update. Used to check for changes in the GeoRock/PersistentInt/PersistentBool FSMs.
    /// </summary>
    private void OnUpdatePersistents() {
        using var enumerator = _persistentFsmData.GetEnumerator();

        while (enumerator.MoveNext()) {
            var persistentFsmData = enumerator.Current;
            if (persistentFsmData == null) {
                continue;
            }

            if (persistentFsmData.IsInt) {
                var value = persistentFsmData.GetCurrentInt.Invoke();
                if (value == persistentFsmData.LastIntValue) {
                    continue;
                }

                persistentFsmData.LastIntValue = value;

                var itemData = persistentFsmData.PersistentItemKey;

                Logger.Info($"Value for {itemData} changed to: {value}");

                if (!_netClient.IsConnected) {
                    continue;
                }

                if (SaveDataMapping.GeoRockBools.TryGetValue(itemData, out var shouldSync) && shouldSync) {
                    // Geo rocks are per-player currency pickups (SyncType.Player on the server),
                    // so breaking one is sent regardless of who the scene host is

                    if (!SaveDataMapping.GeoRockIndices.TryGetValue(itemData, out var index)) {
                        Logger.Info(
                            $"Cannot find geo rock save data index, not sending save update ({itemData.Id}, {itemData.SceneName})"
                        );
                        continue;
                    }

                    SyncLog.Log(SyncLog.World,
                        $"send geoRock | id={itemData.Id} scene={itemData.SceneName} hits={value} policy=Player");

                    _netClient.UpdateManager.SetSaveUpdate(
                        index,
                        [(byte) value]
                    );
                } else if (
                    SaveDataMapping.PersistentIntVarProperties.TryGetValue(itemData, out var varProps) &&
                    varProps.Sync
                ) {
                    // If we should do the scene host check and the player is not scene host, skip sending
                    if (!varProps.IgnoreSceneHost && !_entityManager.IsSceneHost) {
                        Logger.Info(
                            $"Not scene host, not sending persistent int save update ({itemData.Id}, {itemData.SceneName})"
                        );
                        continue;
                    }

                    if (!SaveDataMapping.PersistentIntIndices.TryGetValue(itemData, out var index)) {
                        Logger.Info(
                            $"Cannot find persistent int save data index, not sending save update ({itemData.Id}, {itemData.SceneName})"
                        );
                        continue;
                    }

                    SyncLog.Log(SyncLog.World,
                        $"send persistentInt | id={itemData.Id} scene={itemData.SceneName} value={value} " +
                        $"policy={varProps.SyncType} ignoreSceneHost={varProps.IgnoreSceneHost}");

                    _netClient.UpdateManager.SetSaveUpdate(
                        index,
                        [(byte) value]
                    );
                } else {
                    Logger.Info("Cannot find persistent int/geo rock data bool, not sending save update");
                }
            } else {
                var value = persistentFsmData.GetCurrentBool.Invoke();
                if (value == persistentFsmData.LastBoolValue) {
                    continue;
                }

                persistentFsmData.LastBoolValue = value;

                var itemData = persistentFsmData.PersistentItemKey;

                Logger.Info($"Value for {itemData} changed to: {value}");

                if (!_netClient.IsConnected) {
                    continue;
                }

                if (!SaveDataMapping.PersistentBoolVarProperties.TryGetValue(itemData, out var varProps) ||
                    !varProps.Sync) {
                    Logger.Info(
                        $"Not in persistent bool save data values or false in sync props, not sending save update ({itemData.Id}, {itemData.SceneName})"
                    );
                    continue;
                }

                // If we should do the scene host check and the player is not scene host, skip sending
                if (!varProps.IgnoreSceneHost && !_entityManager.IsSceneHost) {
                    Logger.Info(
                        $"Not scene host, not sending persistent bool save update ({itemData.Id}, {itemData.SceneName})"
                    );
                    continue;
                }

                if (!SaveDataMapping.PersistentBoolIndices.TryGetValue(itemData, out var index)) {
                    Logger.Info(
                        $"Cannot find persistent bool save data index, not sending save update ({itemData.Id}, {itemData.SceneName})"
                    );
                    continue;
                }

                SyncLog.Log(SyncLog.World,
                    $"send persistentBool | id={itemData.Id} scene={itemData.SceneName} value={value} " +
                    $"policy={varProps.SyncType} ignoreSceneHost={varProps.IgnoreSceneHost}");

                _netClient.UpdateManager.SetSaveUpdate(
                    index,
                    BitConverter.GetBytes(value)
                );
            }
        }
    }

    /// <summary>
    /// Called every unity update. Used to check for changes in non-primitive variables in the PlayerData.
    /// </summary>
    private void OnUpdateCompounds() {
        // The compound diff needs the last-values snapshot; if it isn't ready yet, skip rather than NRE per frame.
        if (_lastPlayerData == null) {
            return;
        }

        void CheckUpdates<TVar, TCheck>(
            List<string?> variableNames,
            Dictionary<string?, TCheck> checkDict,
            Func<TVar, TCheck> newCheckFunc,
            Func<TCheck, TCheck, bool> changeFunc,
            Func<object, object, byte[]> deltaEncodeFunc = null
        ) {
            foreach (var varName in variableNames) {
                // Get current value from player data based on the variable name
                var currentValue = (TVar) typeof(PlayerData).GetField(varName).GetValue(PlayerData.instance);
                // Get the value with which to check whether this current value is new or not
                // In some cases this is the same value, in others this is a hash of the value
                var currentCheckValue = newCheckFunc.Invoke(currentValue);

                // Check if the dictionary contains the last value to check against
                if (!checkDict.TryGetValue(varName, out var lastCheckValue)) {
                    // If not, we put the value in the dictionary and continue
                    checkDict[varName] = currentCheckValue;
                    continue;
                }

                // Invoke the change function to check whether there is a difference between the current and last value
                if (!changeFunc(currentCheckValue, lastCheckValue)) {
                    continue;
                }

                Logger.Debug($"Compound variable ({varName}) changed value");

                // Since the value changed, we update it in the dictionary
                checkDict[varName] = currentCheckValue;

                if (deltaEncodeFunc == null) {
                    CheckSendSaveUpdate(varName, () => EncodeSaveDataValue(varName, currentValue));
                } else {
                    var lastValue = _lastPlayerData.GetVariable<TVar>(varName);

                    CheckSendSaveUpdate(
                        varName,
                        () => EncodeSaveDataValue(varName, currentValue),
                        () => deltaEncodeFunc.Invoke(currentValue, lastValue)
                    );

                    // Also update the current value in the PlayerData instance for last values
                    // We copy the value, because otherwise it will be updated whenever the list is updated
                    _lastPlayerData.SetVariable(varName, (TVar) GetCompoundCopy(currentValue));
                }
            }
        }

        CheckUpdates<List<string>, int>(
            SaveDataMapping.StringListVariables,
            _listHashes,
            GetListHashCode,
            (hash1, hash2) => hash1 != hash2,
            (currentValue, lastValue) => {
                var currentList = currentValue as List<string> ?? [];
                var lastList = lastValue as List<string> ?? [];

                // TODO: also allow for negative updates, where something is deleted from the list
                // this also holds for the other two lambdas below
                var deltaList = currentList.Except(lastList).ToList();

                Logger.Debug(
                    $"String list var updated, currentList: {string.Join(", ", currentList)}, lastList: {string.Join(", ", lastList)}, deltaList: {string.Join(", ", deltaList)}"
                );

                return EncodeSaveDataValue(null, deltaList);
            }
        );

        CheckUpdates<BossSequenceDoor.Completion, BossSequenceDoor.Completion>(
            SaveDataMapping.BossSequenceDoorCompletionVariables,
            _bsdCompHashes,
            bsdComp => bsdComp,
            (b1, b2) =>
                b1.canUnlock != b2.canUnlock ||
                b1.unlocked != b2.unlocked ||
                b1.completed != b2.completed ||
                b1.allBindings != b2.allBindings ||
                b1.noHits != b2.noHits ||
                b1.boundNail != b2.boundNail ||
                b1.boundShell != b2.boundShell ||
                b1.boundCharms != b2.boundCharms ||
                b1.boundSoul != b2.boundSoul
        );

        CheckUpdates<BossStatue.Completion, BossStatue.Completion>(
            SaveDataMapping.BossStatueCompletionVariables,
            _bsCompHashes,
            bsComp => bsComp,
            (b1, b2) =>
                b1.hasBeenSeen != b2.hasBeenSeen ||
                b1.isUnlocked != b2.isUnlocked ||
                b1.completedTier1 != b2.completedTier1 ||
                b1.completedTier2 != b2.completedTier2 ||
                b1.completedTier3 != b2.completedTier3 ||
                b1.seenTier3Unlock != b2.seenTier3Unlock ||
                b1.usingAltVersion != b2.usingAltVersion
        );

        CheckUpdates<List<Vector3>, int>(
            SaveDataMapping.VectorListVariables,
            _listHashes,
            GetListHashCode,
            (hash1, hash2) => hash1 != hash2,
            (currentValue, lastValue) => {
                var currentList = currentValue as List<Vector3> ?? [];
                var lastList = lastValue as List<Vector3> ?? [];

                var deltaList = currentList.Except(lastList).ToList();

                Logger.Debug(
                    $"Vector3 list var updated, currentList: {string.Join(", ", currentList)}, lastList: {string.Join(", ", lastList)}, deltaList: {string.Join(", ", deltaList)}"
                );

                return EncodeSaveDataValue(null, deltaList);
            }
        );

        CheckUpdates<List<int>, int>(
            SaveDataMapping.IntListVariables,
            _listHashes,
            GetListHashCode,
            (hash1, hash2) => hash1 != hash2,
            (currentValue, lastValue) => {
                var currentList = currentValue as List<int> ?? [];
                var lastList = lastValue as List<int> ?? [];

                var deltaList = currentList.Except(lastList).ToList();

                Logger.Debug(
                    $"Integer list var updated, currentList: {string.Join(", ", currentList)}, lastList: {string.Join(", ", lastList)}, deltaList: {string.Join(", ", deltaList)}"
                );

                return EncodeSaveDataValue(null, deltaList);
            }
        );

        CheckUpdates<HashSet<string>, int>(
            HashSetVariables,
            _listHashes,
            hashSet => GetListHashCode(hashSet?.ToList()),
            (hash1, hash2) => hash1 != hash2,
            (currentValue, lastValue) => {
                var currentSet = currentValue as HashSet<string> ?? [];
                var lastSet = lastValue as HashSet<string> ?? [];

                var deltaList = currentSet.Except(lastSet).ToList();

                Logger.Debug(
                    $"HashSet string var updated, currentSet: {string.Join(", ", currentSet)}, lastSet: {string.Join(", ", lastSet)}, deltaList: {string.Join(", ", deltaList)}"
                );

                return EncodeSaveDataValue(null, deltaList);
            }
        );

        // Enemy journal kill counts. Per-key SUM on the server; Kills never decrease so all deltas are positive.
        CheckUpdates<EnemyJournalKillData, int>(
            [NamedIntVariables[1]],
            _listHashes,
            killData => GetNamedIntMapHashCode(GetKillDataMap(killData)),
            (hash1, hash2) => hash1 != hash2,
            (currentValue, lastValue) => {
                var currentMap = GetKillDataMap(currentValue as EnemyJournalKillData);
                var lastMap = GetKillDataMap(lastValue as EnemyJournalKillData);

                // Delta carries only the positive increase per key.
                var delta = new EnemyJournalKillData();
                foreach (var pair in currentMap) {
                    lastMap.TryGetValue(pair.Key, out var lastKills);
                    var diff = pair.Value - lastKills;
                    if (diff > 0) {
                        delta.RecordKillData(pair.Key, new EnemyJournalKillData.KillData { Kills = diff });
                    }
                }

                Logger.Debug($"EnemyJournalKillData var updated, delta entries: {delta.Dictionary?.Count ?? 0}");

                return EncodeSaveDataValue(null, delta);
            }
        );

        // Collectable amounts. Per-key SUM on the server. Outbound is POSITIVE-ONLY: local turn-in consumption
        // (a decrease) is never shared, but the snapshot still advances so future increases delta correctly.
        CheckUpdates<CollectableItemsData, int>(
            [NamedIntVariables[0]],
            _listHashes,
            collectables => GetNamedIntMapHashCode(GetCollectablesMap(collectables)),
            (hash1, hash2) => hash1 != hash2,
            (currentValue, lastValue) => {
                var currentMap = GetCollectablesMap(currentValue as CollectableItemsData);
                var lastMap = GetCollectablesMap(lastValue as CollectableItemsData);

                // Delta carries only positive per-item Amount increases (skip negatives = local turn-in consume).
                var delta = new CollectableItemsData();
                foreach (var pair in currentMap) {
                    lastMap.TryGetValue(pair.Key, out var lastAmount);
                    var diff = pair.Value - lastAmount;
                    if (diff > 0) {
                        delta.SetData(pair.Key, new CollectableItemsData.Data { Amount = diff });
                    }
                }

                Logger.Debug($"Collectables var updated, positive delta entries: {delta.Enumerate().Count()}");

                return EncodeSaveDataValue(null, delta);
            }
        );
    }

    /// <summary>
    /// Callback method for when a save update is received.
    /// </summary>
    /// <param name="saveUpdate">The save update that was received.</param>
    public void UpdateSaveWithData(SaveUpdate saveUpdate) {
        var index = saveUpdate.SaveDataIndex;
        var value = saveUpdate.Value;

        SyncLog.Log(SyncLog.World, $"recv update | index={index} name={ResolveSaveDataName(index)} bytes={value.Length}");

        UpdateSaveWithData(index, value);
    }

    /// <summary>
    /// Resolves a save data index to a human-readable name (PlayerData var, geo rock or persistent item)
    /// for logging purposes.
    /// </summary>
    /// <param name="index">The save data index.</param>
    /// <returns>A readable name, or "?" if the index is unknown.</returns>
    private static string ResolveSaveDataName(ushort index) {
        if (SaveDataMapping.PlayerDataIndices.TryGetValue(index, out var pdName)) {
            return pdName;
        }

        if (SaveDataMapping.GeoRockIndices.TryGetValue(index, out var geoRock)) {
            return $"geoRock:{geoRock.Id}@{geoRock.SceneName}";
        }

        if (SaveDataMapping.PersistentBoolIndices.TryGetValue(index, out var pbItem)) {
            return $"persistentBool:{pbItem.Id}@{pbItem.SceneName}";
        }

        if (SaveDataMapping.PersistentIntIndices.TryGetValue(index, out var piItem)) {
            return $"persistentInt:{piItem.Id}@{piItem.SceneName}";
        }

        return "?";
    }

    /// <summary>
    /// Set the save data from the given CurrentSave by overriding all values.
    /// </summary>
    /// <param name="currentSave">The save data to set.</param>
    public void SetSaveWithData(CurrentSave currentSave) {
        if (IsHostingServer) {
            Logger.Info("Received current save, but player is hosting, not updating");
            return;
        }

        SyncLog.Log(SyncLog.World,
            $"recv currentSave | entries={currentSave.SaveData.Count} newForPlayer={currentSave.NewForPlayer} - applying all");

        foreach (var (index, value) in currentSave.SaveData) {
            UpdateSaveWithData(index, value);
        }

        SyncLog.Log(SyncLog.World, "currentSave applied");
    }

    /// <summary>
    /// Update the local save with the given data (index and encoded value).
    /// </summary>
    /// <param name="index">The index of the save data.</param>
    /// <param name="encodedValue">A byte array containing the encoded value of the save data.</param>
    /// <exception cref="NotImplementedException">Thrown when the type belonging to the save data cannot be decoded
    /// due to a missing implementation.</exception>
    private void UpdateSaveWithData(ushort index, byte[] encodedValue) {
        var pd = PlayerData.instance;
        var sceneData = SceneData.instance;

        if (SaveDataMapping.PlayerDataIndices.TryGetValue(index, out var name)) {
            if (CheckPlayerSpecificHosting(SaveDataMapping.PlayerDataVarProperties, name)) {
                return;
            }

            Logger.Info($"Received save update ({index}, {name})");

            var decodedObject = DecodeSaveDataValue(name, encodedValue);

            switch (decodedObject) {
                case bool decodedBool:
                    _lastPlayerData?.SetBool(name, decodedBool);
                    pd.SetBool(name, decodedBool);
                    break;
                case float decodedFloat:
                    _lastPlayerData?.SetFloat(name, decodedFloat);
                    pd.SetFloat(name, decodedFloat);
                    break;
                case int decodedInt:
                    _lastPlayerData?.SetInt(name, decodedInt);
                    pd.SetInt(name, decodedInt);
                    break;
                case long decodedLong:
                    _lastPlayerData?.SetVariable(name, decodedLong);
                    pd.SetVariable(name, decodedLong);
                    break;
                case ulong decodedULong:
                    _lastPlayerData?.SetVariable(name, decodedULong);
                    pd.SetVariable(name, decodedULong);
                    break;
                case uint decodedUInt:
                    _lastPlayerData?.SetVariable(name, decodedUInt);
                    pd.SetVariable(name, decodedUInt);
                    break;
                case short decodedShort:
                    _lastPlayerData?.SetVariable(name, decodedShort);
                    pd.SetVariable(name, decodedShort);
                    break;
                case ushort decodedUShort:
                    _lastPlayerData?.SetVariable(name, decodedUShort);
                    pd.SetVariable(name, decodedUShort);
                    break;
                case double decodedDouble:
                    _lastPlayerData?.SetVariable(name, decodedDouble);
                    pd.SetVariable(name, decodedDouble);
                    break;
                case string decodedString:
                    _lastPlayerData?.SetString(name, decodedString);
                    pd.SetString(name, decodedString);
                    break;
                case Vector2 decodedVec2:
                    _lastPlayerData?.SetVariable(name, decodedVec2);
                    pd.SetVariable(name, decodedVec2);
                    break;
                case Vector3 decodedVec3:
                    _lastPlayerData?.SetVector3(name, decodedVec3);
                    pd.SetVector3(name, decodedVec3);
                    break;
                case List<string> decodedStringList:
                    // First set the new string list hash, so we don't trigger an update and subsequently a feedback
                    // loop
                    _listHashes[name] = GetListHashCode(decodedStringList);
                    _lastPlayerData?.SetVariable(name, (List<string>) GetCompoundCopy(decodedStringList));
                    pd.SetVariable(name, decodedStringList);
                    break;
                case BossSequenceDoor.Completion decodedBsdComp:
                    // First set the new bsdComp obj in the dict, so we don't trigger an update and subsequently a
                    // feedback loop
                    _bsdCompHashes[name] = decodedBsdComp;
                    _lastPlayerData?.SetVariable(name, (BossSequenceDoor.Completion) GetCompoundCopy(decodedBsdComp));
                    pd.SetVariable(name, decodedBsdComp);
                    break;
                case BossStatue.Completion decodedBsComp:
                    // First set the new bsComp obj in the dict, so we don't trigger an update and subsequently a
                    // feedback loop
                    _bsCompHashes[name] = decodedBsComp;
                    _lastPlayerData?.SetVariable(name, (BossStatue.Completion) GetCompoundCopy(decodedBsComp));
                    pd.SetVariable(name, decodedBsComp);
                    break;
                case List<Vector3> decodedVec3List:
                    // First set the new string list hash, so we don't trigger an update and subsequently a feedback
                    // loop
                    _listHashes[name] = GetListHashCode(decodedVec3List);
                    _lastPlayerData?.SetVariable(name, (List<Vector3>) GetCompoundCopy(decodedVec3List));
                    pd.SetVariable(name, decodedVec3List);
                    break;
                case MapZone decodedMapZone:
                    _lastPlayerData?.SetVariable(name, decodedMapZone);
                    pd.SetVariable(name, decodedMapZone);
                    break;
                case List<int> decodedIntList:
                    // First set the new string list hash, so we don't trigger an update and subsequently a feedback
                    // loop
                    _listHashes[name] = GetListHashCode(decodedIntList);
                    _lastPlayerData?.SetVariable(name, (List<int>) GetCompoundCopy(decodedIntList));
                    pd.SetVariable(name, decodedIntList);
                    break;
                case byte[] decodedBytes: {
                    var copy = decodedBytes.ToArray();
                    _lastPlayerData?.SetVariable(name, copy.ToArray());
                    pd.SetVariable(name, copy);
                    break;
                }
                case HashSet<string> decodedHashSet: {
                    var listRepresentation = decodedHashSet.ToList();
                    _listHashes[name] = GetListHashCode(listRepresentation);
                    _lastPlayerData?.SetVariable(name, (HashSet<string>) GetCompoundCopy(decodedHashSet));
                    pd.SetVariable(name, decodedHashSet);
                    break;
                }
                case EnemyJournalKillData decodedKillData: {
                    // INCREASE-ONLY apply: for each incoming key, raise Kills to max(current, incoming). Never
                    // decrease, never absolute-set (save-corruption safety). Only Kills is wired; HasBeenSeen stays.
                    var live = pd.GetVariable<EnemyJournalKillData>(name);
                    if (live == null) {
                        break;
                    }

                    var killsRaised = false;
                    if (decodedKillData.Dictionary != null) {
                        foreach (var entry in decodedKillData.Dictionary) {
                            var currentKill = live.GetKillData(entry.Key);
                            if (entry.Value.Kills > currentKill.Kills) {
                                currentKill.Kills = entry.Value.Kills;
                                live.RecordKillData(entry.Key, currentKill);
                                killsRaised = true;
                            }
                        }
                    }

                    // Bust the quest manager's accepted/active-set caches so a quest that became completable
                    // from the remote-driven progress reflects it without waiting for a scene transition.
                    if (killsRaised) {
                        QuestManager.IncrementVersion();
                    }

                    // Pre-seed the hash from the POST-merge live value so OnUpdateCompounds does not echo.
                    _listHashes[name] = GetNamedIntMapHashCode(GetKillDataMap(live));
                    _lastPlayerData?.SetVariable(name, (EnemyJournalKillData) GetCompoundCopy(live));
                    break;
                }
                case CollectableItemsData decodedCollectables: {
                    // INCREASE-ONLY apply with a per-item LOCAL-CONSUME offset. The server broadcasts the FULL
                    // absolute SUM map to every peer including the consumer, so a naive max(current, incoming) would
                    // RESTORE an item this player just consumed (use/turn-in) the moment anyone picks up any
                    // collectable. To prevent that, never raise an item above (incoming - locallyConsumed): we track
                    // how much this player has consumed below the synced totals and subtract it before the raise.
                    // Only Amount is wired; IsSeenMask/AmountWhileHidden stay per-player.
                    var live = pd.GetVariable<CollectableItemsData>(name);
                    if (live == null) {
                        break;
                    }

                    var amountRaised = false;
                    foreach (var entry in decodedCollectables.Enumerate()) {
                        var key = entry.Key;
                        var incoming = entry.Value.Amount;
                        var currentData = live.GetData(key);
                        var liveAmount = currentData.Amount;

                        // appliedPrev: the live Amount this apply path last set the item to. First sighting baselines
                        // to the current live so a pre-owned item is not mistaken for a consume.
                        if (!_collectablesAppliedTo.TryGetValue(key, out var appliedPrev)) {
                            appliedPrev = liveAmount;
                        }

                        // New local consumption since the last apply = how far live dropped below that baseline.
                        // The apply path is the only raiser and vanilla consume the only lowerer, so a positive drop
                        // is exactly new local consumption.
                        var newConsume = appliedPrev - liveAmount;
                        if (newConsume < 0) {
                            newConsume = 0;
                        }

                        _collectablesConsumedOffset.TryGetValue(key, out var offset);
                        offset += newConsume;
                        _collectablesConsumedOffset[key] = offset;

                        // Never raise above (serverTotal - locallyConsumed); clamp at 0.
                        var target = incoming - offset;
                        if (target < 0) {
                            target = 0;
                        }

                        if (target > liveAmount) {
                            currentData.Amount = target;
                            live.SetData(key, currentData);
                            liveAmount = target;
                            amountRaised = true;
                        }

                        // Post-apply baseline for the next round's consume detection.
                        _collectablesAppliedTo[key] = liveAmount > target ? liveAmount : target;
                    }

                    // Bust the quest manager's accepted/active-set caches so a quest that became completable
                    // from the remote-driven progress reflects it without waiting for a scene transition.
                    if (amountRaised) {
                        QuestManager.IncrementVersion();
                    }

                    // Pre-seed the hash from the POST-merge live value so OnUpdateCompounds does not echo.
                    _listHashes[name] = GetNamedIntMapHashCode(GetCollectablesMap(live));
                    _lastPlayerData?.SetVariable(name, (CollectableItemsData) GetCompoundCopy(live));
                    break;
                }
                default: {
                    if (decodedObject.GetType().IsEnum) {
                        _lastPlayerData?.SetVariable(name, decodedObject);
                        pd.SetVariable(name, decodedObject);
                    } else {
                        throw new ArgumentException($"Could not decode type: {decodedObject.GetType()}");
                    }

                    break;
                }
            }

            _saveChanges.ApplyPlayerDataSaveChange(name);
        }

        if (SaveDataMapping.GeoRockIndices.TryGetValue(index, out var itemData)) {
            var value = encodedValue[0];

            Logger.Info($"Received geo rock save update: {itemData.Id}, {itemData.SceneName}, {value}");

            foreach (var persistentFsmData in _persistentFsmData) {
                var existingItemData = persistentFsmData.PersistentItemKey;

                if (existingItemData.Id == itemData.Id && existingItemData.SceneName == itemData.SceneName) {
                    persistentFsmData.SetCurrentInt.Invoke(value);
                    persistentFsmData.LastIntValue = value;
                }
            }

            sceneData.SaveMyState(
                new GeoRockData {
                    id = itemData.Id,
                    sceneName = itemData.SceneName,
                    hitsLeft = value
                }
            );
        } else if (SaveDataMapping.PersistentBoolIndices.TryGetValue(index, out itemData)) {
            if (CheckPlayerSpecificHosting(SaveDataMapping.PersistentBoolVarProperties, itemData)) {
                return;
            }

            var value = encodedValue[0] == 1;

            Logger.Info($"Received persistent bool save update: {itemData.Id}, {itemData.SceneName}, {value}");

            foreach (var persistentFsmData in _persistentFsmData) {
                var existingItemData = persistentFsmData.PersistentItemKey;

                if (existingItemData.Id == itemData.Id && existingItemData.SceneName == itemData.SceneName) {
                    Logger.Debug($"Setting last bool value for {existingItemData} to {value}");
                    persistentFsmData.SetCurrentBool.Invoke(value);
                    persistentFsmData.LastBoolValue = value;
                }
            }

            // Persist into SceneData so the change survives even when this player is NOT in the affected scene
            // (e.g. a wall/door/lever broken by another player in a different room). The live-object loop above
            // only matches when we are already in that scene; without writing SceneData the broken state would be
            // lost and the object would be intact again when this player later walks in. Mirrors how the game's own
            // PersistentBoolItem.SaveValue persists (SceneData.instance.PersistentBools.SetValue).
            SceneData.instance.PersistentBools.SetValue(new PersistentItemData<bool> {
                SceneName = itemData.SceneName,
                ID = itemData.Id,
                Value = value
            });
        } else if (SaveDataMapping.PersistentIntIndices.TryGetValue(index, out itemData)) {
            if (CheckPlayerSpecificHosting(SaveDataMapping.PersistentIntVarProperties, itemData)) {
                return;
            }

            var value = (int) encodedValue[0];
            // Add a special case for the -1 value that some persistent ints might have
            // 255 is never used in the byte space, so we use it for compact networking
            if (value == 255) {
                value = -1;
            }

            Logger.Info($"Received persistent int save update: {itemData.Id}, {itemData.SceneName}, {value}");

            foreach (var persistentFsmData in _persistentFsmData) {
                var existingItemData = persistentFsmData.PersistentItemKey;

                if (existingItemData.Id == itemData.Id && existingItemData.SceneName == itemData.SceneName) {
                    persistentFsmData.SetCurrentInt.Invoke(value);
                    persistentFsmData.LastIntValue = value;
                }
            }

            // Persist into SceneData so the change survives when this player is not currently in the scene
            // (see the persistent-bool branch above for the full rationale).
            SceneData.instance.PersistentInts.SetValue(new PersistentItemData<int> {
                SceneName = itemData.SceneName,
                ID = itemData.Id,
                Value = value
            });
        }

        // Do the checks for whether the player is hosting and the received save data is player specific and should
        // thus be ignored. Returns true if the data should be ignored, false otherwise.
        bool CheckPlayerSpecificHosting<TKey>(Dictionary<TKey, SaveDataMapping.VarProperties> dict, TKey value) {
            if (!IsHostingServer) {
                return false;
            }

            if (!dict.TryGetValue(value, out var varProps)) {
                return true;
            }

            if (varProps.SyncType != SaveDataMapping.SyncType.Player) {
                return false;
            }

            Logger.Info($"Received player specific save update ({index}, {name}), but player is hosting");
            return true;
        }
    }

    /// <summary>
    /// Encode a save data value by first recasting HK/Unity internal types to SSMP types and then using the EncodeUtil.
    /// </summary>
    /// <param name="name">The name of the save data variable.</param>
    /// <param name="value">The object to encode, which should be part of save data.</param>
    /// <returns>A byte array containing the encoded data.</returns>
    private static byte[] EncodeSaveDataValue(string? name, object? value) {
        // First cast HK or Unity internal types to SSMP types, this is to make sure we can use our internal
        // EncodeUtil to encode all types. This util is also used on the server side, where (in the case of the
        // standalone server) we have no reference of HK or Unity internal types
        var casted = value switch {
            Vector2 v => (Math.Vector2) v,
            Vector3 v => (Math.Vector3) v,
            MapZone m => (Serialization.MapZone) m,
            BossStatue.Completion c => (BossStatueCompletion) c,
            BossSequenceDoor.Completion c => (BossSequenceDoorCompletion) c,
            List<Vector3> l => l.Select(v => (Math.Vector3) v).ToList(),
            _ => value
        };

        return EncodeUtil.EncodeSaveDataValue(casted, name);
    }

    /// <summary>
    /// Decode a save data value by first using the EncodeUtil and then recasting SSMP types to SS/Unity internal types. 
    /// </summary>
    /// <param name="name">The name of the save data variable.</param>
    /// <param name="encodedValue">A byte array containing the encoded data.</param>
    /// <returns>The decoded object.</returns>
    private static object? DecodeSaveDataValue(string? name, byte[] encodedValue) {
        var val = EncodeUtil.DecodeSaveDataValue(name, encodedValue);

        // Now we cast SSMP types to SS or Unity internal types, this is to make sure we can use our internal
        // EncodeUtil to decode all types. This util is also used on the server side, where (in the case of the
        // standalone server) we have no reference of SS or Unity internal types
        return val switch {
            Math.Vector2 v => (Vector2) v,
            Math.Vector3 v => (Vector3) v,
            Serialization.MapZone m => (MapZone) m,
            BossStatueCompletion c => (BossStatue.Completion) c,
            BossSequenceDoorCompletion c => (BossSequenceDoor.Completion) c,
            List<Math.Vector3> l => l.Select(v => (Vector3) v).ToList(),
            int i when name != null && typeof(PlayerData).GetField(name)?.FieldType is { IsEnum: true } t => Enum
                .ToObject(t, i),
            _ => val
        };
    }

    /// <summary>
    /// Get the current save data as a dictionary with mapped indices and encoded values. This returns the global save
    /// data if the <paramref name="server"/> boolean is set. Otherwise, it returns the player save data.
    /// </summary>
    /// <returns>A dictionary with mapped indices and byte-encoded values.</returns>
    public static Dictionary<ushort, byte[]> GetCurrentSaveData(bool server) {
        var pd = PlayerData.instance;

        var saveData = new Dictionary<ushort, byte[]>();

        void AddToSaveData<TCollection, TLookup>(
            IEnumerable<TCollection> enumerable,
            Func<TCollection, TLookup> keyFunc,
            object syncMapping,
            BiLookup<TLookup, ushort> indexMapping,
            Func<TCollection, object> valueFunc
        ) {
            foreach (var collectionValue in enumerable) {
                var key = keyFunc.Invoke(collectionValue);

                if (syncMapping is Dictionary<TLookup, bool> boolMapping) {
                    if (!boolMapping.TryGetValue(key, out var shouldSync) || !shouldSync) {
                        continue;
                    }

                    // Since all geo rocks are server data, we need to check whether we are actually trying to get
                    // server data or not and continue appropriately
                    if (!server) {
                        continue;
                    }
                } else if (syncMapping is Dictionary<TLookup, SaveDataMapping.VarProperties> syncPropMapping) {
                    if (!syncPropMapping.TryGetValue(key, out var varProps)) {
                        continue;
                    }

                    // Skip values that are not supposed to be synced, or ones that have the property that it is
                    // server data. Since we will not require the hosting player's save data on the server.
                    if (!varProps.Sync) {
                        continue;
                    }

                    // Check whether the sync type corresponds with the server parameter. If it is server data, but
                    // we are trying to get player data, we continue
                    if ((varProps.SyncType == SaveDataMapping.SyncType.Server) != server) {
                        continue;
                    }
                }

                if (!indexMapping.TryGetValue(key, out var index)) {
                    continue;
                }

                var value = valueFunc.Invoke(collectionValue);

                saveData.Add(index, EncodeSaveDataValue(key as string, value));
            }
        }

        AddToSaveData(
            typeof(PlayerData).GetFields(),
            fieldInfo => fieldInfo.Name,
            SaveDataMapping.PlayerDataVarProperties,
            SaveDataMapping.PlayerDataIndices,
            fieldInfo => fieldInfo.GetValue(pd)
        );

        // Seed the host's PRE-EXISTING world geometry (broken walls, opened gates, drained levers, etc.) into the
        // GlobalSaveData so a connecting client receives the host's authoritative world baseline — not just the live
        // session deltas. We enumerate the loaded save's SceneData persistent bool/int collections directly (these
        // are the live Silksong API; the old HK1 'sd.persistentBoolItems' list no longer exists). Geo rocks are
        // SyncType.Player and are deliberately NOT seeded here.
        //
        // ENCODING NOTE: the byte layout MUST be byte-identical to the live update path (OnUpdatePersistents /
        // UpdateSaveWithData), otherwise the client decodes garbage. The live path bypasses EncodeSaveDataValue and
        // writes a single raw byte for both bools and ints:
        //   bool -> BitConverter.GetBytes(bool) == [0x01]/[0x00], decoded as encodedValue[0] == 1
        //   int  -> [(byte) value], decoded as (int) encodedValue[0] with 255 remapped to -1
        // We mirror that exactly below; do NOT route these through EncodeSaveDataValue.
        if (server) {
            var sceneData = SceneData.instance;

            if (sceneData != null) {
                foreach (var item in sceneData.PersistentBools.serializedList) {
                    // Normalize the scene the same way the live key does (GameManager.GetBaseSceneName), so seeded
                    // keys collide-merge with live keys instead of producing duplicate/missed entries.
                    var key = new PersistentItemKey {
                        Id = item.ID,
                        SceneName = global::GameManager.GetBaseSceneName(item.SceneName)
                    };

                    if (!SaveDataMapping.PersistentBoolVarProperties.TryGetValue(key, out var varProps)) {
                        continue;
                    }

                    // Only seed values that are synced AND are server (world) data.
                    if (!varProps.Sync || varProps.SyncType != SaveDataMapping.SyncType.Server) {
                        continue;
                    }

                    if (!SaveDataMapping.PersistentBoolIndices.TryGetValue(key, out var index)) {
                        continue;
                    }

                    // Raw single byte, byte-identical to the live send path (BitConverter.GetBytes(bool)).
                    saveData[index] = new[] { (byte) (item.Value ? 1 : 0) };
                }

                foreach (var item in sceneData.PersistentInts.serializedList) {
                    var key = new PersistentItemKey {
                        Id = item.ID,
                        SceneName = global::GameManager.GetBaseSceneName(item.SceneName)
                    };

                    if (!SaveDataMapping.PersistentIntVarProperties.TryGetValue(key, out var varProps)) {
                        continue;
                    }

                    if (!varProps.Sync || varProps.SyncType != SaveDataMapping.SyncType.Server) {
                        continue;
                    }

                    if (!SaveDataMapping.PersistentIntIndices.TryGetValue(key, out var index)) {
                        continue;
                    }

                    // Raw single byte, byte-identical to the live send path ([(byte) value]). Values 0..254 map to
                    // themselves; -1 encodes as (byte)(-1) == 255, which the receiver remaps back to -1.
                    saveData[index] = new[] { (byte) item.Value };
                }
            }
        }

        return saveData;
    }

    /// <summary>
    /// Get the hash code of the combined values in a list.
    /// </summary>
    /// <param name="list">The list to calculate the hash code for.</param>
    /// <returns>0 if the list is empty or null, otherwise a hash code matching the specific order of values in the list.
    /// </returns>
    private static int GetListHashCode<T>(List<T>? list) {
        if (list == null || list.Count == 0) {
            return 0;
        }

        return list
               .Select(item => item?.GetHashCode() ?? 0)
               .Aggregate((total, nextCode) => total ^ nextCode);
    }

    /// <summary>
    /// Get a snapshot of the per-item Amount values in a <see cref="CollectableItemsData"/> as a name-to-int map.
    /// </summary>
    private static Dictionary<string, int> GetCollectablesMap(CollectableItemsData? collectables) {
        var map = new Dictionary<string, int>();
        if (collectables == null) {
            return map;
        }

        foreach (var entry in collectables.Enumerate()) {
            map[entry.Key] = entry.Value.Amount;
        }

        return map;
    }

    /// <summary>
    /// Get a snapshot of the per-enemy Kills values in an <see cref="EnemyJournalKillData"/> as a name-to-int map.
    /// </summary>
    private static Dictionary<string, int> GetKillDataMap(EnemyJournalKillData? killData) {
        var map = new Dictionary<string, int>();
        if (killData?.Dictionary == null) {
            return map;
        }

        foreach (var entry in killData.Dictionary) {
            map[entry.Key] = entry.Value.Kills;
        }

        return map;
    }

    /// <summary>
    /// Get a hash code for a named-int map that changes when any key is added or any value changes. Unlike
    /// <see cref="GetListHashCode{T}"/> this incorporates both the key and the int value of each entry so that an
    /// increment to an existing key (e.g. kills 3 -> 4) is detected.
    /// </summary>
    private static int GetNamedIntMapHashCode(Dictionary<string, int> map) {
        if (map.Count == 0) {
            return 0;
        }

        return map
               .Select(pair => (pair.Key?.GetHashCode() ?? 0) * 397 ^ pair.Value)
               .Aggregate((total, nextCode) => total ^ nextCode);
    }

    /// <summary>
    /// Get a copy of the given object for compound objects in the PlayerData, such as string lists, integer lists,
    /// completion for boss sequences or boss doors, etc.
    /// </summary>
    /// <param name="value">The object value to get a copy from.</param>
    /// <returns>The copy of the given value.</returns>
    /// <exception cref="ArgumentException">Thrown when a copy cannot be made, due to the given value being null or
    /// of a non-compound or non-PlayerData type.</exception>
    private static object GetCompoundCopy(object value) {
        switch (value) {
            case null:
                throw new ArgumentException("Cannot get copy of null");
            case List<string> stringListValue:
                return new List<string>(stringListValue);
            case List<int> intListValue:
                return new List<int>(intListValue);
            case List<Vector3> vecListValue:
                return new List<Vector3>(vecListValue);
            case BossSequenceDoor.Completion bsdComp:
                return new BossSequenceDoor.Completion {
                    canUnlock = bsdComp.canUnlock,
                    unlocked = bsdComp.unlocked,
                    completed = bsdComp.completed,
                    allBindings = bsdComp.allBindings,
                    noHits = bsdComp.noHits,
                    boundNail = bsdComp.boundNail,
                    boundShell = bsdComp.boundShell,
                    boundCharms = bsdComp.boundCharms,
                    boundSoul = bsdComp.boundSoul,
                    viewedBossSceneCompletions = bsdComp.viewedBossSceneCompletions == null
                        ? []
                        : [..bsdComp.viewedBossSceneCompletions]
                };
            case BossStatue.Completion bsComp:
                return new BossStatue.Completion {
                    hasBeenSeen = bsComp.hasBeenSeen,
                    isUnlocked = bsComp.isUnlocked,
                    completedTier1 = bsComp.completedTier1,
                    completedTier2 = bsComp.completedTier2,
                    completedTier3 = bsComp.completedTier3,
                    seenTier3Unlock = bsComp.seenTier3Unlock,
                    usingAltVersion = bsComp.usingAltVersion
                };
            case HashSet<string> hashSetStringValue:
                return new HashSet<string>(hashSetStringValue);
            case CollectableItemsData collectablesValue: {
                var copy = new CollectableItemsData();
                foreach (var entry in collectablesValue.Enumerate()) {
                    copy.SetData(entry.Key, entry.Value);
                }

                return copy;
            }
            case EnemyJournalKillData killDataValue: {
                var copy = new EnemyJournalKillData();
                if (killDataValue.Dictionary != null) {
                    foreach (var entry in killDataValue.Dictionary) {
                        copy.RecordKillData(entry.Key, entry.Value);
                    }
                }

                return copy;
            }
            default:
                throw new ArgumentException($"Cannot get copy of value with type: {value.GetType()}");
        }
    }
}
