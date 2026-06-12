namespace SSMP.Logging;

/// <summary>
/// Structured logging channel for synchronisation diagnostics. Every line has the shape
/// <c>[SYNC:CATEGORY] event | key=value key=value ...</c> so a session log reads as a single
/// consistent trace across world-save sync, entity sync, scene-host changes and pause handling.
/// Logged at Info level so lines are visible without enabling debug logging in BepInEx.
/// </summary>
internal static class SyncLog {
    /// <summary>
    /// Master switch for the structured sync trace. Disable to silence all SyncLog output at once.
    /// </summary>
    public static bool Enabled = true;

    /// <summary>World/save data synchronisation (PlayerData vars, persistent bools/ints, geo rocks).</summary>
    public const string World = "WORLD";

    /// <summary>Server-side save update handling (accept/reject/storage decisions).</summary>
    public const string Server = "SERVER";

    /// <summary>Entity registration, spawning and scene-host role changes.</summary>
    public const string Entity = "ENTITY";

    /// <summary>Enemy knockback (recoil) synchronisation.</summary>
    public const string Knockback = "KNOCK";

    /// <summary>Multiplayer pause handling (local hero freeze/unfreeze).</summary>
    public const string Pause = "PAUSE";

    /// <summary>
    /// Write one structured trace line: <c>[SYNC:cat] message</c>.
    /// Callers build the message as <c>"event | key=value key=value"</c>.
    /// </summary>
    /// <param name="category">One of the category constants of this class.</param>
    /// <param name="message">The event description with key=value details.</param>
    public static void Log(string category, string message) {
        if (!Enabled) {
            return;
        }

        Logger.Info($"[SYNC:{category}] {message}");
    }
}
