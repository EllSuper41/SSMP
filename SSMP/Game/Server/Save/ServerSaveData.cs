using System;
using System.Collections.Generic;
using SSMP.Game.Client.Save;
using SSMP.Logging;
using SSMP.Networking.Packet.Data;

namespace SSMP.Game.Server.Save;

/// <summary>
/// Class that holds save data from a server. This consists of global data relating to the world and individual
/// data specific to each player. This class is only used for storing the save data while the server is running;
/// serialization of this data to the save file is done with <see cref="ModSaveFile"/>.
/// </summary>
/// <remarks>
/// THREAD-SAFETY: the three backing collections (<see cref="_globalSaveData"/>, <see cref="_playerSaveData"/>,
/// <see cref="_seenPlayers"/>) are touched from THREE threads: the network processing thread
/// (<c>ServerManager.OnSaveUpdate</c> and friends), the per-client send/timeout thread (disconnect persist), and
/// the Unity main thread (host-start seed, native-save persist). They are PRIVATE and every read/write/enumeration
/// is funnelled through the synchronized methods below, each of which takes <see cref="_lock"/>. No caller ever
/// touches the raw collections unguarded. The lock is NEVER held across I/O, a network send, or a Unity call: the
/// persist path takes a deep-enough snapshot under the lock then releases it before serializing/writing.
/// </remarks>
internal class ServerSaveData {
    /// <summary>
    /// Name of the variable in PlayerData that denotes a Steel Soul save file.
    /// </summary>
    private const string SteelSoulVarName = "permadeathMode";

    /// <summary>
    /// The index that corresponds with the Steel Soul variable.
    /// </summary>
    private static readonly ushort SteelSoulIndex;

    /// <summary>
    /// Single private lock co-located with the data it protects. Guards every read/write/enumeration of the three
    /// collections below. It is a dedicated, non-reentrant lock that is never nested inside another lock, never held
    /// across I/O / a network send / a Unity call.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// The global save data for the server. E.g. broken walls, open doors, etc. ONLY access under <see cref="_lock"/>.
    /// </summary>
    private Dictionary<ushort, byte[]> _globalSaveData = new();

    /// <summary>
    /// The player specific save data mapped to player's auth keys. ONLY access under <see cref="_lock"/>.
    /// </summary>
    private Dictionary<string, Dictionary<ushort, byte[]>> _playerSaveData = new();

    /// <summary>
    /// Set of auth keys for players the server has seen at least once (established identity on connect). This is
    /// kept SEPARATE from <see cref="_playerSaveData"/> on purpose: it tracks "we know this player" independently of
    /// whether we currently hold any save deltas for them, so a returning player whose deltas are momentarily
    /// missing is correctly treated as a reconnect (not a first-join). Only remote players are tracked here.
    /// ONLY access under <see cref="_lock"/>.
    /// </summary>
    private HashSet<string> _seenPlayers = new();

    /// <summary>
    /// Static constructor for initializing the indices for the Steel Soul variable.
    /// </summary>
    static ServerSaveData() {
        if (!SaveDataMapping.Instance.PlayerDataIndices.TryGetValue(SteelSoulVarName, out SteelSoulIndex)) {
            Logger.Warn("Could not find index for steel soul variable");
        }
    }

    /// <summary>
    /// Get save data that contains global save data and player specific save data for the player with the given auth
    /// key.
    /// </summary>
    /// <param name="authKey">The auth key that corresponds to the player for the player specific data.</param>
    /// <returns>A dictionary mapping save data indices to byte encoded values.</returns>
    public CurrentSave GetCurrentSaveData(string authKey) {
        var currentSave = new CurrentSave();

        lock (_lock) {
            if (!_playerSaveData.TryGetValue(authKey, out var playerSaveData)) {
                playerSaveData = new Dictionary<ushort, byte[]>();
            }

            // Distinguish a reconnect from a first-join. A returning player we happen to hold no deltas for must NOT
            // be pushed into a fresh start (RunStartNewGame); only a player we have never seen before is "new".
            // Identity is recorded in SeenPlayers when the player first connects (see ServerManager), so even a
            // zero-delta session counts as seen.
            currentSave.NewForPlayer = !_seenPlayers.Contains(authKey);
            if (currentSave.NewForPlayer) {
                Logger.Debug("Player has not been seen before, marking as new in CurrentSave");
            }

            var saveData = new Dictionary<ushort, byte[]>(_globalSaveData);
            foreach (var data in playerSaveData) {
                saveData[data.Key] = data.Value;
            }

            currentSave.SaveData = saveData;
        }

        return currentSave;
    }

    /// <summary>
    /// Whether the global save data in this instance is for Steel Soul mode.
    /// </summary>
    /// <returns>True if the save data is for Steel Soul mode, false otherwise.</returns>
    public bool IsSteelSoul() {
        lock (_lock) {
            if (!_globalSaveData.TryGetValue(SteelSoulIndex, out var value)) {
                return false;
            }

            return value.Length > 0 && value[0] != 0;
        }
    }

    /// <summary>
    /// Sets (creating if absent) the per-player value for the given auth key and index.
    /// </summary>
    public void SetPlayerValue(string authKey, ushort index, byte[] value) {
        lock (_lock) {
            if (!_playerSaveData.TryGetValue(authKey, out var playerSaveData)) {
                playerSaveData = new Dictionary<ushort, byte[]>();
                _playerSaveData[authKey] = playerSaveData;
            }

            playerSaveData[index] = value;
        }
    }

    /// <summary>
    /// Removes all per-player save data for the given auth key (e.g. Steel Soul death wipe).
    /// </summary>
    public void RemovePlayer(string authKey) {
        lock (_lock) {
            _playerSaveData.Remove(authKey);
        }
    }

    /// <summary>
    /// Returns a COPY of the per-player save data for the given auth key, or false if none exists.
    /// </summary>
    public bool TryGetPlayerData(string authKey, out Dictionary<ushort, byte[]> copy) {
        lock (_lock) {
            if (_playerSaveData.TryGetValue(authKey, out var data)) {
                copy = new Dictionary<ushort, byte[]>(data);
                return true;
            }

            copy = new Dictionary<ushort, byte[]>();
            return false;
        }
    }

    /// <summary>
    /// Stores the given per-player save data for the given auth key. Takes a defensive copy under the lock.
    /// </summary>
    public void SetPlayerData(string authKey, Dictionary<ushort, byte[]> data) {
        lock (_lock) {
            _playerSaveData[authKey] = new Dictionary<ushort, byte[]>(data);
        }
    }

    /// <summary>
    /// Atomically read-modify-writes a global save value under the lock. The <paramref name="merge"/> delegate is
    /// invoked with the CURRENT stored bytes (or null if absent) and returns the NEW bytes to store, OR null to
    /// ABORT (leave the stored value untouched). The whole decode/merge/encode runs inside the lock so two
    /// concurrent additive deltas cannot lose an update. IMPORTANT: <paramref name="merge"/> must NOT call back into
    /// any locking ServerSaveData method (non-reentrant lock) and must return a FRESH array (never mutate the
    /// supplied bytes in place).
    /// </summary>
    /// <returns>The new value that was stored (so callers can broadcast it after the lock is released), or null if
    /// the merge aborted and nothing was stored.</returns>
    public byte[]? WriteGlobal(ushort index, Func<byte[]?, byte[]?> merge) {
        lock (_lock) {
            _globalSaveData.TryGetValue(index, out var current);
            var result = merge(current);
            if (result != null) {
                _globalSaveData[index] = result;
            }

            return result;
        }
    }

    /// <summary>
    /// Stores the given global save value for the given index (non-additive store).
    /// </summary>
    public void SetGlobalValue(ushort index, byte[] value) {
        lock (_lock) {
            _globalSaveData[index] = value;
        }
    }

    /// <summary>
    /// Records that the server has seen the given (remote) player at least once.
    /// </summary>
    public void AddSeen(string authKey) {
        lock (_lock) {
            _seenPlayers.Add(authKey);
        }
    }

    /// <summary>
    /// Forgets that the server has seen the given player (e.g. Steel Soul death wipe), so a reconnect is treated
    /// as a first-join again.
    /// </summary>
    public void RemoveSeen(string authKey) {
        lock (_lock) {
            _seenPlayers.Remove(authKey);
        }
    }

    /// <summary>
    /// Bulk-replaces the per-player save data map (host-start seed from a loaded .modsav, or the empty fallback).
    /// </summary>
    public void ReplacePlayerData(Dictionary<string, Dictionary<ushort, byte[]>> playerSaveData) {
        lock (_lock) {
            _playerSaveData = playerSaveData;
        }
    }

    /// <summary>
    /// Bulk-replaces the seen-players set (host-start seed from a loaded .modsav).
    /// </summary>
    public void ReplaceSeen(HashSet<string> seenPlayers) {
        lock (_lock) {
            _seenPlayers = seenPlayers;
        }
    }

    /// <summary>
    /// Seeds the global save data (host-start base from SceneData + PlayerData server fields).
    /// </summary>
    public void SeedGlobal(Dictionary<ushort, byte[]> globalSaveData) {
        lock (_lock) {
            _globalSaveData = globalSaveData;
        }
    }

    /// <summary>
    /// Overlays a prior-session (.modsav) global save data dictionary ON TOP of the seeded base, PER KEY, using the
    /// supplied <paramref name="mergeAdditive"/> delegate for genuine Additive PlayerData fields and a wholesale
    /// store otherwise. This mirrors the host-start merge in <c>ModServerManager</c> but runs the whole iteration
    /// under the lock so the seeded base dictionary is never enumerated/mutated unguarded.
    /// </summary>
    /// <param name="modSaveGlobal">The prior-session global save data to overlay.</param>
    /// <param name="isAdditive">Predicate that returns the PlayerData variable name when the index is a genuine
    /// Additive field (and null otherwise), so the caller decides which keys are merged vs replaced.</param>
    /// <param name="mergeAdditive">Merge function (name, baseBytes, modBytes) => merged bytes.</param>
    public void MergeGlobalFromModSave(
        Dictionary<ushort, byte[]> modSaveGlobal,
        Func<ushort, string?> isAdditive,
        Func<string, byte[], byte[], byte[]> mergeAdditive
    ) {
        lock (_lock) {
            foreach (var kv in modSaveGlobal) {
                var index = kv.Key;
                var modBytes = kv.Value;

                var name = isAdditive(index);
                if (name != null && _globalSaveData.TryGetValue(index, out var baseBytes)) {
                    _globalSaveData[index] = mergeAdditive(name, baseBytes, modBytes);
                } else {
                    _globalSaveData[index] = modBytes;
                }
            }
        }
    }

    /// <summary>
    /// Takes a deep-enough snapshot of all save data under the lock and returns a DETACHED <see cref="ModSaveFile"/>
    /// built from it. The lock is held ONLY for the in-memory copy (a few microseconds); the returned ModSaveFile is
    /// produced from a private copy that no other thread touches, so the caller can safely JSON-serialize and write
    /// it to disk OUTSIDE the lock. The live ServerSaveData is never passed to
    /// <see cref="ModSaveFile.FromServerSaveData"/>.
    /// </summary>
    /// <remarks>
    /// The copy is "deep-enough": the outer dictionaries and every inner per-player dictionary are fresh, but the
    /// leaf <c>byte[]</c> values are SHARED. That is safe because save values are immutable-by-replacement — every
    /// write replaces the array reference (<c>dict[k] = newArray</c>) and never mutates an existing array in place
    /// (grep-confirmed; the additive merge always produces a fresh array via EncodeUtil).
    /// </remarks>
    public ModSaveFile SnapshotForPersist() {
        ServerSaveData detached;

        lock (_lock) {
            detached = new ServerSaveData {
                _globalSaveData = new Dictionary<ushort, byte[]>(_globalSaveData),
                _seenPlayers = new HashSet<string>(_seenPlayers),
                _playerSaveData = new Dictionary<string, Dictionary<ushort, byte[]>>(_playerSaveData.Count)
            };

            foreach (var pair in _playerSaveData) {
                detached._playerSaveData[pair.Key] = new Dictionary<ushort, byte[]>(pair.Value);
            }
        }

        // Built from a private copy no other thread can touch — safe to serialize/write outside the lock.
        return ModSaveFile.FromServerSaveData(detached);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // RAW accessors. These bypass the lock and return the LIVE backing collections. They are INTERNAL and may ONLY
    // be used on an instance that NO other thread can touch: (1) a detached snapshot built by SnapshotForPersist, and
    // (2) a brand-new instance built on the main thread at host-start (ModSaveFile.ToServerSaveData) before the
    // network processing thread exists. NEVER use these on the running server's live ServerSaveData.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Raw (unlocked) access to the global save data dictionary. See the RAW accessors note above for the contract.
    /// </summary>
    internal Dictionary<ushort, byte[]> RawGlobalSaveData => _globalSaveData;

    /// <summary>
    /// Raw (unlocked) access to the per-player save data dictionary. See the RAW accessors note above.
    /// </summary>
    internal Dictionary<string, Dictionary<ushort, byte[]>> RawPlayerSaveData => _playerSaveData;

    /// <summary>
    /// Raw (unlocked) access to the seen-players set. See the RAW accessors note above.
    /// </summary>
    internal HashSet<string> RawSeenPlayers => _seenPlayers;
}
