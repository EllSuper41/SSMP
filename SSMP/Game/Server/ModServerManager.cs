using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SSMP.Game.Command.Server;
using SSMP.Game.Settings;
using SSMP.Networking.Packet;
using SSMP.Networking.Server;
using SSMP.Networking.Transport.Common;
using SSMP.Networking.Transport.HolePunch;
using SSMP.Networking.Transport.SteamP2P;
using SSMP.Networking.Transport.UDP;
using SSMP.Game.Server.Save;
using SSMP.Game.Client.Save;
using SSMP.Hooks;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Server;

/// <summary>
/// Specialization of <see cref="ServerManager"/> that adds handlers for the mod specific things.
/// </summary>
internal class ModServerManager : ServerManager {
    /// <summary>
    /// The UiManager instance for registering events for starting and stopping a server.
    /// </summary>
    private readonly UiManager _uiManager;

    /// <summary>
    /// The mod settings instance for retrieving the auth key of the local player to set player save data when
    /// hosting a server.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The settings command.
    /// </summary>
    private readonly SettingsCommand _settingsCommand;


    /// <summary>
    /// The NetServer instance to check whether the server is started.
    /// </summary>
    private readonly NetServer _netServer;

    public ModServerManager(
        NetServer netServer,
        PacketManager packetManager,
        ServerSettings serverSettings,
        UiManager uiManager,
        ModSettings modSettings
    ) : base(netServer, packetManager, serverSettings) {
        _netServer = netServer;
        _uiManager = uiManager;
        _modSettings = modSettings;
        _settingsCommand = new SettingsCommand(this, InternalServerSettings);
    }

    /// <inheritdoc />
    public override void Initialize() {
        base.Initialize();

        // Start addon loading, since all addons that are also mods should be registered during the Awake phase of
        // their MonoBehaviour
        AddonManager.LoadAddons();

        // Register handlers for UI events
        // Force full synchronisation on: this mod is built around a shared world (world/quest/entity
        // sync), there is no UI to toggle it, and without it connecting clients are wrongly prompted to
        // pick their own save slot instead of loading the host's world.
        _uiManager.RequestServerStartHostEvent += (_, port, _, transportType, _) =>
            OnRequestServerStartHost(port, fullSynchronisation: true, transportType);
        _uiManager.RequestServerStopHostEvent += OnRequestServerStopHost;
        PlayerConnectEvent += _ => UpdateMatchmakingRemotePlayerCount();
        PlayerDisconnectEvent += _ => {
            // Persist immediately while this player's accumulated ServerSaveData is still intact (it is kept on
            // disconnect, only the live mapping is removed), so a host restart never forgets a player who left.
            PersistRemotePlayersToDisk(GetActiveSaveSlot());
            UpdateMatchmakingRemotePlayerCount();
        };
        ServerShutdownEvent += () => UpdateMatchmakingRemotePlayerCount(0);

        EventHooks.GameManagerSaveGame += OnGameSave;

        // Register application quit handler
        // ModHooks.ApplicationQuitHook += Stop;
    }

    /// <summary>
    /// Callback method for when the UI requests the server to be started as a host.
    /// </summary>
    /// <param name="port">The port to start the server on.</param>
    /// <param name="fullSynchronisation">Whether full synchronisation is enabled.</param>
    /// <param name="transportType">The type of transport to use.</param>
    private void OnRequestServerStartHost(int port, bool fullSynchronisation, TransportType transportType) {
        if (fullSynchronisation) {
            // Get the global save data from the save manager, which obtains the global save data from the loaded
            // save file that the user selected
            ServerSaveData.GlobalSaveData = SaveManager.GetCurrentSaveData(true);

            // Load remote players' player-specific data from disk for the current profile ID
            var profileId = global::GameManager.instance.profileID;
            var modSavePath = Path.Combine(FileUtil.GetConfigPath(), $"user{profileId}.modsav");
            if (File.Exists(modSavePath)) {
                // Try the primary file first; on any read/deserialize failure fall back to the rolled-over backup
                // (.bak) written by the atomic save path before giving up — a single corrupt write must never
                // silently wipe ALL remote players.
                var modSaveFile = TryLoadModSaveFile(modSavePath);
                if (modSaveFile == null) {
                    var backupPath = modSavePath + ".bak";
                    if (File.Exists(backupPath)) {
                        Logging.Logger.Error(
                            $"PRIMARY remote-player save at {modSavePath} could not be read; " +
                            $"attempting backup at {backupPath}"
                        );
                        modSaveFile = TryLoadModSaveFile(backupPath);
                    }
                }

                if (modSaveFile != null) {
                    var serverSave = modSaveFile.ToServerSaveData();
                    ServerSaveData.PlayerSaveData = serverSave.PlayerSaveData;
                    ServerSaveData.SeenPlayers = serverSave.SeenPlayers;
                    if (serverSave.GlobalSaveData.Count > 0) {
                        ServerSaveData.GlobalSaveData = serverSave.GlobalSaveData;
                    }

                    Logging.Logger.Info($"Loaded remote players' save data from: {modSavePath}");
                } else {
                    ServerSaveData.PlayerSaveData = new Dictionary<string, Dictionary<ushort, byte[]>>();
                    Logging.Logger.Error(
                        $"DEGRADED: could not read remote-player save (primary OR backup) at {modSavePath}; " +
                        "initialized EMPTY player save data. Remote players may be treated as new on this host run."
                    );
                }
            } else {
                ServerSaveData.PlayerSaveData = new Dictionary<string, Dictionary<ushort, byte[]>>();
                Logging.Logger.Info(
                    $"No remote player save file found at: {modSavePath}, initialized empty player save data."
                );
            }

            // Lastly, we get the player save data from the save manager, which obtains the player save data from the
            // loaded save file that the user selected. We add this data to the server save as the local player
            ServerSaveData.PlayerSaveData[_modSettings.AuthKey!] = SaveManager.GetCurrentSaveData(false);
        }

        IEncryptedTransportServer transportServer = transportType switch {
            TransportType.Udp => new UdpEncryptedTransportServer(),
            TransportType.Steam => new SteamEncryptedTransportServer(),
            TransportType.HolePunch => CreateHolePunchServer(),
            _ => throw new ArgumentOutOfRangeException(nameof(transportType), transportType, null)
        };

        Start(port, fullSynchronisation, transportServer);
        UpdateMatchmakingRemotePlayerCount();
    }

    /// <summary>
    /// Creates a HolePunch server with the MmsClient for lobby cleanup on shutdown.
    /// </summary>
    private HolePunchEncryptedTransportServer CreateHolePunchServer() {
        return new HolePunchEncryptedTransportServer(_uiManager.ConnectInterface.MmsClient);
    }

    /// <inheritdoc />
    protected override void RegisterCommands() {
        base.RegisterCommands();

        CommandManager.RegisterCommand(_settingsCommand);
    }

    /// <inheritdoc />
    protected override void DeregisterCommands() {
        base.DeregisterCommands();

        CommandManager.DeregisterCommand(_settingsCommand);

        EventHooks.GameManagerSaveGame -= OnGameSave;
    }

    /// <summary>
    /// Pushes the current remote-player count to MMS heartbeat state.
    /// </summary>
    /// <param name="count">The number of players to set in the update, or -1 if the number needs to be retrieved
    /// from the server.</param>
    private void UpdateMatchmakingRemotePlayerCount(int count = -1) {
        if (count != -1) {
            _uiManager.ConnectInterface.MmsClient.SetConnectedPlayers(count);
            return;
        }

        var hostAuthKey = _modSettings.AuthKey;
        var remotePlayerCount = hostAuthKey == null
            ? 0
            : Players.Count(player => player.AuthKey != hostAuthKey);
        _uiManager.ConnectInterface.MmsClient.SetConnectedPlayers(remotePlayerCount);
    }

    /// <summary>
    /// Intercepts native save events to serialize remote player-specific save data to disk.
    /// </summary>
    /// <param name="saveSlot">The save slot index.</param>
    private void OnGameSave(int saveSlot) {
        if (!_netServer.IsStarted || !FullSynchronisation) {
            return;
        }

        Logging.Logger.Info($"Intercepted native save for slot {saveSlot}. Saving remote players' save data...");
        PersistRemotePlayersToDisk(saveSlot);
    }

    /// <summary>
    /// Called when the UI requests the host to stop. Persists remote players to disk BEFORE tearing the server
    /// down (ServerSaveData is still intact at this point) so the host's save reliably holds both players, then
    /// stops the server.
    /// </summary>
    private void OnRequestServerStopHost() {
        PersistRemotePlayersToDisk(GetActiveSaveSlot());
        Stop();
    }

    /// <summary>
    /// The save slot to persist remote-player data under. Mirrors the slot used by the load path
    /// (<see cref="OnRequestServerStartHost"/>) so a re-host reliably finds the file.
    /// </summary>
    private static int GetActiveSaveSlot() {
        var gm = global::GameManager.instance;
        return gm != null ? gm.profileID : -1;
    }

    /// <summary>
    /// Attempts to read and deserialize a <see cref="ModSaveFile"/> from the given path. Returns null (and logs)
    /// on any failure, so callers can fall back to a backup file without the corrupt read taking down the host.
    /// </summary>
    /// <param name="path">The path to read the mod save file from.</param>
    /// <returns>The deserialized <see cref="ModSaveFile"/>, or null on failure.</returns>
    private static ModSaveFile? TryLoadModSaveFile(string path) {
        try {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ModSaveFile>(json);
        } catch (Exception e) {
            Logging.Logger.Error($"Could not load remote players' save data from {path}: {e}");
            return null;
        }
    }

    /// <summary>
    /// Serializes remote players' save data to disk for the given save slot. Called on the host's native save,
    /// on every player disconnect, and on host stop — so a host restart restores BOTH players' full progress,
    /// not only whatever happened to be captured at the last bench save. World (GlobalSaveData) flags and the
    /// seen-players set are ALWAYS written; if there is no remote per-player data this run, any existing per-player
    /// entries already on disk are preserved rather than clobbered with an empty map. The write itself is atomic
    /// (temp file + swap, previous-good rolled into .bak) so a partial write can never wipe a good save.
    /// </summary>
    /// <param name="saveSlot">The save slot index to write under.</param>
    private void PersistRemotePlayersToDisk(int saveSlot) {
        if (!FullSynchronisation || saveSlot < 0) {
            return;
        }

        try {
            // Create a copy of ServerSaveData for serialization
            var modSaveFile = ModSaveFile.FromServerSaveData(ServerSaveData);

            // Filter out the host player's auth key to avoid duplicate/redundant data in the remote players' file.
            // SeenPlayers is meant to track REMOTE players only, so drop the host from both maps.
            var hostAuthKey = _modSettings.AuthKey;
            if (hostAuthKey != null) {
                modSaveFile.PlayerSaveData.Remove(hostAuthKey);
                modSaveFile.SeenPlayers.Remove(hostAuthKey);
            }

            var configPath = FileUtil.GetConfigPath();
            if (!Directory.Exists(configPath)) {
                Directory.CreateDirectory(configPath);
            }

            var modSavePath = Path.Combine(configPath, $"user{saveSlot}.modsav");

            // Never clobber good per-player data with an empty set (e.g. a player who connected and dropped before
            // sending any save data). BUT we must still always persist GlobalSaveData — losing world flags is worse
            // than re-writing the same per-player map. So when there is no remote per-player data to write, preserve
            // whatever per-player entries (and seen players) already exist on disk and only refresh GlobalSaveData.
            if (modSaveFile.PlayerSaveData.Count == 0) {
                var existing = TryLoadModSaveFile(modSavePath);
                if (existing != null) {
                    // Keep the existing per-player data and union the seen-player sets; refresh world data only.
                    modSaveFile.PlayerSaveData = existing.PlayerSaveData;
                    if (existing.SeenPlayers != null) {
                        foreach (var seen in existing.SeenPlayers) {
                            if (seen != hostAuthKey && !modSaveFile.SeenPlayers.Contains(seen)) {
                                modSaveFile.SeenPlayers.Add(seen);
                            }
                        }
                    }

                    Logging.Logger.Info(
                        "No remote player save data to persist; preserving existing per-player data and " +
                        "refreshing global save data only");
                } else {
                    Logging.Logger.Info(
                        "No remote player save data to persist and no existing file; writing global save data only");
                }
            }

            var json = JsonConvert.SerializeObject(modSaveFile, Formatting.Indented);
            WriteFileAtomic(modSavePath, json);

            Logging.Logger.Info(
                $"Remote players' save data written to {modSavePath} ({modSaveFile.PlayerSaveData.Count} player(s))");
        } catch (Exception e) {
            Logging.Logger.Error($"Could not save remote players' save data to disk: {e}");
        }
    }

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="path"/> atomically: the content is written to a temporary
    /// file first, then swapped into place. On the same volume this is atomic and rolls the previous-good file into
    /// a .bak, so a partial/interrupted write can never silently wipe an existing good save. The existing good file
    /// is only ever removed AFTER the fully-written temp file is in hand.
    /// </summary>
    /// <param name="path">The destination path.</param>
    /// <param name="json">The content to write.</param>
    private static void WriteFileAtomic(string path, string json) {
        var tmpPath = path + ".tmp";
        var bakPath = path + ".bak";

        // Write the full content to the temp file first. If this throws, the destination is left untouched.
        File.WriteAllText(tmpPath, json);

        if (File.Exists(path)) {
            try {
                // Atomic on the same volume: swaps tmp into place and rolls the previous-good file into .bak.
                File.Replace(tmpPath, path, bakPath);
            } catch (Exception e) {
                // File.Replace can fail (e.g. cross-volume, transient lock). Fall back to delete+move, but only
                // now that tmp is fully written — so we never delete the existing good file without a replacement.
                // Promote the current good file to .bak FIRST so that if the move itself throws, the previous
                // save is still recoverable (the load path falls back to .bak).
                Logging.Logger.Warn(
                    $"Atomic File.Replace failed for {path}, falling back to backup+move: {e.Message}");
                try { File.Copy(path, bakPath, true); } catch { /* best-effort backup */ }
                File.Delete(path);
                File.Move(tmpPath, path);
            }
        } else {
            // No existing file to protect; just move the temp file into place.
            File.Move(tmpPath, path);
        }
    }
}
