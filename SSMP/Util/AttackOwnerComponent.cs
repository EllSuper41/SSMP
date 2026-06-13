using UnityEngine;

namespace SSMP.Util;

/// <summary>
/// Tags an attack damager object with the player root that owns it, so enemy aggro can attribute a
/// connected hit to the correct player even when the damager has detached from the player hierarchy
/// (projectiles, silk skills). Distinct from <see cref="EffectOwnerComponent"/> to avoid two writers
/// fighting over a single field.
/// </summary>
public class AttackOwnerComponent : MonoBehaviour {
    /// <summary>
    /// The player root object that produced this attack.
    /// </summary>
    public GameObject? PlayerRoot;
}
