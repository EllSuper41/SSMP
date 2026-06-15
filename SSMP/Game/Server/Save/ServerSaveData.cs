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
    /// The global save data for the server. E.g. broken walls, open doors, etc.
    /// </summary>
    public Dictionary<ushort, byte[]> GlobalSaveData { get; set; } = new();

    /// <summary>
    /// The player specific save data mapped to player's auth keys.
    /// </summary>
    public Dictionary<string, Dictionary<ushort, byte[]>> PlayerSaveData { get; set; } = new();

    /// <summary>
    /// Set of auth keys for players the server has seen at least once (established identity on connect). This is
    /// kept SEPARATE from <see cref="PlayerSaveData"/> on purpose: it tracks "we know this player" independently of
    /// whether we currently hold any save deltas for them, so a returning player whose deltas are momentarily
    /// missing is correctly treated as a reconnect (not a first-join). Only remote players are tracked here.
    /// </summary>
    public HashSet<string> SeenPlayers { get; set; } = new();

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

        if (!PlayerSaveData.TryGetValue(authKey, out var playerSaveData)) {
            playerSaveData = new Dictionary<ushort, byte[]>();
        }

        // Distinguish a reconnect from a first-join. A returning player we happen to hold no deltas for must NOT be
        // pushed into a fresh start (RunStartNewGame); only a player we have never seen before is "new". Identity is
        // recorded in SeenPlayers when the player first connects (see ServerManager), so even a zero-delta session
        // counts as seen.
        currentSave.NewForPlayer = !SeenPlayers.Contains(authKey);
        if (currentSave.NewForPlayer) {
            Logger.Debug("Player has not been seen before, marking as new in CurrentSave");
        }

        var saveData = new Dictionary<ushort, byte[]>(GlobalSaveData);
        foreach (var data in playerSaveData) {
            saveData[data.Key] = data.Value;
        }

        currentSave.SaveData = saveData;

        return currentSave;
    }

    /// <summary>
    /// Whether the global save data in this instance is for Steel Soul mode.
    /// </summary>
    /// <returns>True if the save data is for Steel Soul mode, false otherwise.</returns>
    public bool IsSteelSoul() {
        if (!GlobalSaveData.TryGetValue(SteelSoulIndex, out var value)) {
            return false;
        }

        return value.Length > 0 && value[0] != 0;
    }
}
