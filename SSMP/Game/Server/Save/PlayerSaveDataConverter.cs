using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SSMP.Game.Client.Save;
using SSMP.Logging;

namespace SSMP.Game.Server.Save;

/// <summary>
/// JSON converter class to handle converting a list of entries for a mod save file into and from JSON.
/// </summary>
public class PlayerSaveDataConverter : JsonConverter {
    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        if (value == null) {
            return;
        }
        
        var entries = (ModSaveFile.PlayerDataEntries) value;
        
        // Create a JSON object as the basis for the list of entries
        var jObject = new JObject();

        // Then for each entry we add the name and value as a property to the object
        foreach (var entry in entries) {
            // The use of the 'serializer' in the FromObject is important to ensure we allow earlier defined converters
            // from acting on nested objects in the value of the entry (such as Unity's Vector3 being handled by
            // the modding API's Vector3 converter)
            jObject.Add(entry.Name!, JToken.FromObject(entry.Value!, serializer));
        }

        // Finally, write the JSON object to the writer
        jObject.WriteTo(writer);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) {
        // We know that our JSON will have a JSON object, so we can read it
        var jObject = JObject.Load(reader);

        var entries = new ModSaveFile.PlayerDataEntries();

        // Loop over all properties of the object, since these are the entries in our ModSaveFile
        foreach (var prop in jObject.Properties()) {
            // Create the entry with the name only
            var entry = new ModSaveFile.PlayerDataEntry {
                Name = prop.Name
            };

            // Find the variable properties that correspond to the PlayerData variable name from this JSON property's
            // name
            if (!SaveDataMapping.Instance.PlayerDataVarProperties.TryGetValue(prop.Name, out var varProps)) {
                Logger.Warn($"Could not deserialize ModSaveFile.Entry, because variable '{prop.Name}' has no variable properties, skipping");
                continue;
            }

            if (!varProps.Sync) {
                Logger.Debug($"Variable properties for '{prop.Name}' indicate no sync, skipping");
                continue;
            }

            // From the variable properties, we obtain the type for the value of this JSON property
            var typeString = varProps.VarType;
            var type = ResolveVarType(typeString);

            if (type == null) {
                Logger.Warn($"Could not deserialize ModSaveFile.Entry, because var type '{typeString}' could not be found, skipping");
                continue;
            }

            // Then we can convert the JSON property's value
            // The use of the 'serializer' here is important to ensure we allow earlier defined converters from acting
            // on nested objects in the value of the entry (such as Unity's Vector3 being handled by the modding API's
            // Vector3 converter)
            entry.Value = prop.Value.ToObject(type, serializer);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Resolve a save-data VarType string to a CLR <see cref="Type"/>.
    ///
    /// Some Sync:true VarType strings are synthetic / bare game-type names that <see cref="Type.GetType(string)"/>
    /// cannot resolve: 'CollectableItemsData' and 'EnemyJournalKillData' are top-level types living in Assembly-CSharp
    /// (neither the executing SSMP assembly nor mscorlib), and 'HashSet&lt;string&gt;' is not valid CLR type-name
    /// syntax at all. Without an explicit mapping these resolve to null and the entry is silently dropped on read-back,
    /// losing shared quest progress / scene discovery sets across a host restart.
    ///
    /// These exact synthetic strings mirror the set that <see cref="SSMP.Util.EncodeUtil"/> already special-cases in
    /// its encode/decode switches; they must stay in lockstep with the VarType strings in save-data.json and must NOT
    /// be renamed. Everything else falls back to <see cref="Type.GetType(string)"/> (System.* and the fully-qualified
    /// SSMP.* strings already resolve), and an unknown future type still degrades gracefully via the null guard in
    /// the caller.
    /// </summary>
    private static Type? ResolveVarType(string typeString) {
        switch (typeString) {
            case "HashSet<string>":
                return typeof(HashSet<string>);
            case "CollectableItemsData":
                return typeof(CollectableItemsData);
            case "EnemyJournalKillData":
                return typeof(EnemyJournalKillData);
            default:
                return Type.GetType(typeString);
        }
    }

    /// <inheritdoc />
    public override bool CanConvert(Type objectType) {
        return objectType == typeof(ModSaveFile.PlayerDataEntry);
    }
}
