using System.Collections.Generic;
using Newtonsoft.Json;
using SSMP.Game.Client.Save;
using SSMP.Util;

// ReSharper disable MemberHidesStaticFromOuterClass
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider
// adding the 'required' modifier or declaring as nullable.

namespace SSMP.Game.Server.Save;

/// <summary>
/// Class for serialization and deserialization of save data from a server to the local save file.
/// See <see cref="ServerSaveData"/> for the representation of the same data of the running server.
/// </summary>
internal class ModSaveFile {
    /// <summary>
    /// The player specific save data mapped to player's auth keys.
    /// </summary>
    [JsonProperty("playerSaveData")]
    public Dictionary<string, SaveData> PlayerSaveData { get; set; } = new();

    /// <summary>
    /// The global save data for the server. E.g. broken walls, open doors, etc.
    /// </summary>
    [JsonProperty("globalSaveData")]
    public SaveData GlobalSaveData { get; set; } = new();

    /// <summary>
    /// Auth keys for players the server has seen before. Persisted so a returning player is recognised as a
    /// reconnect rather than a first-join across host restarts. May be null/absent in pre-existing (older) save
    /// files; see <see cref="ToServerSaveData"/> for the back-compat seeding.
    /// </summary>
    [JsonProperty("seenPlayers")]
    public List<string> SeenPlayers { get; set; } = new();

    /// <summary>
    /// Convert this class to an encoded ServerSaveData.
    /// </summary>
    /// <returns>The converted ServerSaveData instance.</returns>
    public virtual ServerSaveData ToServerSaveData() {
        // Build the three collections locally, then hand them to the (synchronized) ServerSaveData via its bulk
        // seed API. This runs at host-start on the main thread before the processing thread exists, so there is no
        // active race, but the data is still funnelled through the locked API and never touches raw fields.
        var globalSaveData = EncodeUtil.ConvertToServerSaveData(GlobalSaveData);

        var playerSaveData = new Dictionary<string, Dictionary<ushort, byte[]>>();
        foreach (var authKey in PlayerSaveData.Keys) {
            playerSaveData[authKey] = EncodeUtil.ConvertToServerSaveData(PlayerSaveData[authKey]);
        }

        // Back-compat: older save files have no "seenPlayers" field (SeenPlayers stays an empty list after
        // deserialization). To be safe always, seed the seen-players set from the union of any persisted seen
        // players and every auth key that already has stored player save data — so a player who has data on disk
        // is always treated as known, never thrown into a fresh start.
        var seenPlayers = new HashSet<string>();
        if (SeenPlayers != null) {
            foreach (var authKey in SeenPlayers) {
                seenPlayers.Add(authKey);
            }
        }

        foreach (var authKey in PlayerSaveData.Keys) {
            seenPlayers.Add(authKey);
        }

        var serverSaveData = new ServerSaveData();
        serverSaveData.SeedGlobal(globalSaveData);
        serverSaveData.ReplacePlayerData(playerSaveData);
        serverSaveData.ReplaceSeen(seenPlayers);

        return serverSaveData;
    }

    /// <summary>
    /// Get an instance of this class with the decoded data from the given ServerSaveData.
    /// </summary>
    /// <param name="serverSaveData">The encoded ServerSaveData. This MUST be a DETACHED snapshot produced by
    /// <see cref="ServerSaveData.SnapshotForPersist"/> — never the live instance, since this method reads the raw
    /// collections directly (via the Unlocked accessors) without taking the save lock.</param>
    /// <returns>An instance of this class.</returns>
    public static ModSaveFile FromServerSaveData(ServerSaveData serverSaveData) {
        // Reads the raw collections directly: only ever invoked on a detached snapshot (see SnapshotForPersist),
        // so no other thread touches these dictionaries and no lock is required here.
        var modSaveFile = new ModSaveFile {
            GlobalSaveData = EncodeUtil.ConvertFromServerSaveData(serverSaveData.RawGlobalSaveData)
        };

        var playerSaveData = serverSaveData.RawPlayerSaveData;
        foreach (var authKey in playerSaveData.Keys) {
            var saveData = EncodeUtil.ConvertFromServerSaveData(playerSaveData[authKey]);
            // Store the entries in the player save data dictionary of the instance
            modSaveFile.PlayerSaveData[authKey] = saveData;
        }

        // Persist the set of seen players so reconnects are distinguishable from first-joins across host restarts.
        modSaveFile.SeenPlayers = new List<string>(serverSaveData.RawSeenPlayers);

        return modSaveFile;
    }

    /// <summary>
    /// Serializable save data that contains PlayerData and SceneData similar to the HK save file.
    /// </summary>
    public class SaveData {
        /// <summary>
        /// PlayerData entries that use a custom serialization.
        /// <seealso cref="PlayerSaveDataConverter"/>
        /// </summary>
        [JsonProperty("playerData")]
        public PlayerDataEntries PlayerDataEntries { get; set; } = [];

        /// <summary>
        /// SceneData instance that contains geo rocks and persistent items.
        /// </summary>
        [JsonProperty("sceneData")]
        public SceneData SceneData { get; set; } = new();
    }

    /// <summary>
    /// Serializable SceneData class that contains geo rocks, persistent integers, and persistent booleans.
    /// </summary>
    public class SceneData {
        /// <summary>
        /// List of individual geo rocks.
        /// </summary>
        [JsonProperty("geoRocks")]
        public List<GeoRockData> GeoRockData { get; set; } = [];

        /// <summary>
        /// List of persistent booleans.
        /// </summary>
        [JsonProperty("persistentBoolItems")]
        public List<PersistentBoolData> PersistentBoolData { get; set; } = [];

        /// <summary>
        /// List of persistent integers.
        /// </summary>
        [JsonProperty("persistentIntItems")]
        public List<PersistentIntData> PersistentIntData { get; set; } = [];
    }

    /// <summary>
    /// Base class for serializable scene data items, such as geo rocks or persistent items.
    /// <seealso cref="GeoRockData"/>
    /// <seealso cref="PersistentBoolData"/>
    /// <seealso cref="PersistentIntData"/>
    /// </summary>
    public class SceneDataItem {
        /// <summary>
        /// The ID of the item.
        /// </summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>
        /// The scene name the item is in.
        /// </summary>
        [JsonProperty("sceneName")]
        public string SceneName { get; set; }

        /// <summary>
        /// Get a persistent item key for this item with the correct ID and scene name.
        /// </summary>
        /// <returns>An instance of <see cref="PersistentItemKey"/>.</returns>
        public PersistentItemKey GetKey() {
            return new PersistentItemKey {
                Id = Id,
                SceneName = SceneName
            };
        }
    }

    /// <summary>
    /// Serializable geo rock data.
    /// </summary>
    public class GeoRockData : SceneDataItem {
        /// <summary>
        /// The number of hits left for this geo rock.
        /// </summary>
        [JsonProperty("hitsLeft")]
        public int HitsLeft { get; set; }
    }

    /// <summary>
    /// Serializable persistent boolean.
    /// </summary>
    public class PersistentBoolData : SceneDataItem {
        /// <summary>
        /// Whether the item was activated.
        /// </summary>
        [JsonProperty("activated")]
        public bool Activated { get; set; }
    }

    /// <summary>
    /// Serializable persistent integer.
    /// </summary>
    public class PersistentIntData : SceneDataItem {
        /// <summary>
        /// The value of the item.
        /// </summary>
        [JsonProperty("value")]
        public int Value { get; set; }
    }

    /// <summary>
    /// List of <see cref="PlayerDataEntry"/> with a custom converter to make sure JSON (de)serialization is handled correctly.
    /// </summary>
    [JsonConverter(typeof(PlayerSaveDataConverter))]
    public class PlayerDataEntries : List<PlayerDataEntry>;

    /// <summary>
    /// A single entry for a mod save file with a name and corresponding value that can be any type.
    /// </summary>
    public class PlayerDataEntry {
        /// <summary>
        /// The name of the PlayerData variable.
        /// </summary>
        public string? Name { get; init; }

        /// <summary>
        /// The value of the PlayerData variable as an object.
        /// </summary>
        public object? Value { get; set; }
    }
}
