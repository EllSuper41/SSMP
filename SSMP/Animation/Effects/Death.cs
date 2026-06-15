using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// Animation effect for the death of the player.
/// </summary>
internal class Death : AnimationEffect {
    /// <summary>
    /// Name of the game object for the death particles.
    /// </summary>
    private const string DeathParticleObjectName = "Low Health Leak";

    /// <summary>
    /// Cached game object for the death particles.
    /// </summary>
    private static GameObject? _deathParticles;

    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        var frosted = HeroController.instance.cState.isFrostDeath;

        byte[] info = [
            (byte)(frosted ? 1 : 0)
        ];

        return info;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        // Pick the right death animation coroutine (frost if effect info is a single '1' byte).
        var routine = effectInfo is [1] ? PlayFrostDeath(playerObject) : PlayDeath(playerObject);

        // Host the coroutine on the player's OWN container (via its CoroutineCancelComponent) rather than on the
        // persistent MonoBehaviourUtil. The coroutine defers SpawnCocoonParticles/HidePlayer across several seconds;
        // if the container is recycled and re-used for a different player in that window, a coroutine on the
        // persistent runner would spawn the cocoon particles on the WRONG player. A coroutine hosted on the
        // container is stopped automatically when the container is deactivated on recycle. Fall back to the
        // persistent runner (the activeInHierarchy guards inside the coroutines still protect that path).
        var canceller = playerObject.GetComponent<CoroutineCancelComponent>();
        if (canceller != null) {
            canceller.AddCoroutine("death", canceller.StartCoroutine(routine));
        } else {
            MonoBehaviourUtil.Instance.StartCoroutine(routine);
        }
    }

    /// <summary>
    /// Plays the frost death animation.
    /// </summary>
    /// <param name="playerObject">The GameObject representing the player.</param>
    private static IEnumerator PlayFrostDeath(GameObject playerObject) {
        // Find frost death prefab
        var prefab = HeroController.instance.heroDeathFrostPrefab;
        if (prefab == null) {
            yield break;
        }

        // Play freeze audio
        var fsm = prefab.LocateMyFSM("Hero Death Anim");
        var audio = fsm.GetFirstAction<AudioPlayerOneShotSingle>("Start");
        AudioUtil.PlayAudio(audio, playerObject);

        // Spawn the two crystal growth particle systems
        var localCrystalSlow = prefab.FindGameObjectInChildren("Particle_Crystal Slow");
        var localCrystalFinal = prefab.FindGameObjectInChildren("Particle_Crystal Final");

        const float growthTime = 3f;
        var crystalSlow = EffectUtils.SpawnGlobalPoolObject(
            localCrystalSlow, 
            playerObject.transform, 
            growthTime, 
            true
        );
        var crystalFinal = EffectUtils.SpawnGlobalPoolObject(
            localCrystalFinal, 
            playerObject.transform, 
            growthTime, 
            true
        );

        if (crystalSlow == null || crystalFinal == null) {
            yield break;
        }

        // Reset their positions
        crystalSlow.transform.localPosition = new Vector3(-0.32f, 0, -2.22f);
        crystalFinal.transform.localPosition = new Vector3(-0.22f, -0.04f, -2.2f);

        yield return new WaitForSeconds(growthTime);

        // Bail if the player container was recycled/deactivated during the wait, so we never apply the rest of the
        // death sequence (HidePlayer, cocoon particles) to a container that has since been reused for another player.
        if (playerObject == null || !playerObject.activeInHierarchy) {
            yield break;
        }

        // Transition from growing the crystals to exploding them
        var localDestroyEffects = prefab.FindGameObjectInChildren("Destroy Effects");
        var destroyEffects = EffectUtils.SpawnGlobalPoolObject(
            localDestroyEffects, 
            playerObject.transform, 
            5
        );

        destroyEffects?.DestroyComponent<CameraShakeOnEnable>();

        // Hide the player. They're shown as a separate frosted object in the next step
        HidePlayer(playerObject);

        // Spawn the frosted hornet object
        const int frostedDuration = 3;
        var localFrostDeath = prefab.FindGameObjectInChildren("Hornet_Frosted");
        var frostDeath = EffectUtils.SpawnGlobalPoolObject(
            localFrostDeath, 
            playerObject.transform, 
            frostedDuration + 1.1f
        );
        if (frostDeath == null) {
            yield break;
        }

        frostDeath.transform.localPosition = playerObject.transform.position + new Vector3(0.11f, 0, -0.18f);

        // Fade out and play particles
        yield return new WaitForSeconds(frostedDuration);
        if (playerObject == null || !playerObject.activeInHierarchy) {
            yield break;
        }
        frostDeath.AddComponent<SimpleFadeOut>();
        SpawnCocoonParticles(frostDeath);
    }

    /// <summary>
    /// Plays the normal death animation.
    /// </summary>
    /// <param name="playerObject">The GameObject representing the player.</param>
    private static IEnumerator PlayDeath(GameObject playerObject) {
        // Get/create a death particle system
        if (!CreateParticles(playerObject, out var particles)) {
            yield break;
        }

        // Enable the system by turning on emission
        var particleSystem = particles.GetComponent<ParticleSystem>();
        var emission = particleSystem.emission;
        emission.enabled = true;
        
        // Refresh particle system
        particles.SetActive(false);
        particles.SetActive(true);

        // Disable leak particle emission after time, then spawn black particles
        yield return new WaitForSeconds(4);
        // Bail if the container was recycled/reused during the wait (don't spawn cocoon particles on a now-different player).
        if (playerObject == null || !playerObject.activeInHierarchy) {
            yield break;
        }
        emission.enabled = false;
        SpawnCocoonParticles(playerObject);
    }

    /// <summary>
    /// Spawns the particle system that is created when a player hits their cocoon.
    /// </summary>
    /// <param name="playerObject">The player object to spawn the particles on</param>
    private static void SpawnCocoonParticles(GameObject playerObject) {
        // Get the cocoon prefab
        var manager = GameManager.instance.GetSceneManager().GetComponent<CustomSceneManager>();
        var localCocoon = manager.heroCorpsePrefab;

        // Find the particle systems inside of it
        var localCocoonParticles = localCocoon
                                   .FindGameObjectInChildren("Core")?
                                   .FindGameObjectInChildren("Pt Spider Fall");
        if (localCocoonParticles == null) {
            Logger.Info("No particles");
            return;
        }

        // Spawn the particles
        EffectUtils.SpawnGlobalPoolObject(localCocoonParticles, playerObject.transform, 10f);
    }

    /// <summary>
    /// Attempts to locate and bind the 'Low Health Leak' GameObject to the specified player object.
    /// </summary>
    /// <param name="playerObject">The player's object.</param>
    /// <param name="deathParticles">The player's 'Low Health Leak' object, or null if not found.</param>
    /// <returns>true if the 'Low Health Leak' GameObject is successfully found and bound; otherwise, false.</returns>
    private static bool CreateParticles(
        GameObject playerObject, 
        [MaybeNullWhen(false)] out GameObject deathParticles
    ) {
        // Find the reference object
        _deathParticles ??= HeroController.instance.gameObject.FindGameObjectInChildren(DeathParticleObjectName);
        if (_deathParticles == null) {
            Logger.Warn("Could not find local Bind Effects object in hero object");
            deathParticles = null;
            return false;
        }

        // Find the existing particles for the player object
        deathParticles = playerObject.FindGameObjectInChildren(DeathParticleObjectName);

        // If not found, make it!
        if (deathParticles == null) {
            deathParticles = Object.Instantiate(_deathParticles);
            deathParticles.transform.SetParentReset(playerObject.transform);
            deathParticles.transform.SetLocalPositionZ(0.2f);
            deathParticles.name = DeathParticleObjectName;
            
            // Add timer to turn off
            var deactivator = deathParticles.AddComponent<DeactivateAfterDelay>();
            deactivator.time = 4f;

            // Ensure the particle system turns on automatically
            var particleSystem = deathParticles.GetComponent<ParticleSystem>();
            var main = particleSystem.main;
            main.playOnAwake = true;
        }

        // Particles were found or created!
        return true;
    }
}
