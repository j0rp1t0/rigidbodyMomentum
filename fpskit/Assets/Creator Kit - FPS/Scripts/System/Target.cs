
// Import the System.Collections namespace for basic collection types like ArrayList
using System.Collections;
// Import the System.Collections.Generic namespace for generic collections like List<T>, Dictionary<T,K>
using System.Collections.Generic;
// Import System.Numerics for advanced math operations (though Vector3 is overridden below)
using System.Numerics;
// Import UnityEngine namespace - the core Unity API for GameObjects, Components, etc.
using UnityEngine;
// Override the System.Numerics Vector3 with Unity's Vector3 class for 3D position/rotation/scale
using Vector3 = UnityEngine.Vector3;

// Declare a public class named Target that inherits from MonoBehaviour
// MonoBehaviour is Unity's base class that allows this script to be attached to GameObjects
public class Target : MonoBehaviour
{
    // Public float field - appears in Inspector, sets the target's maximum health points
    public float health = 5.0f;

    // Public int field - points awarded to player when this target is destroyed
    public int pointValue;

    // Public ParticleSystem reference - visual effect played when target is destroyed
    // ParticleSystem is Unity's component for creating particle effects like explosions, sparks, etc.
    public ParticleSystem DestroyedEffect;

    // [Header] attribute creates a section header in the Unity Inspector for organization
    [Header("Audio")]

    // Public RandomPlayer reference - custom component that plays random audio clips when hit
    // RandomPlayer appears to be a custom script from the FPS Creator Kit
    public RandomPlayer HitPlayer;

    // Public AudioSource reference - Unity component for playing audio clips
    // This plays ambient/idle sounds while the target exists
    public AudioSource IdleSource;

    // Public read-only property that returns whether this target has been destroyed
    // The => syntax creates a getter that returns the value of m_Destroyed
    public bool Destroyed => m_Destroyed;

    // Private boolean field to track destruction state - prefixed with m_ following Unity naming convention
    bool m_Destroyed = false;

    // Private float field to track current health (can be different from max health after taking damage)
    float m_CurrentHealth;

    // Awake() is a Unity lifecycle method called when the GameObject is instantiated
    // It's called before Start() and runs even if the GameObject is inactive
    void Awake()
    {
        // Call a helper function to recursively change this object and all children to the "Target" layer
        // Helpers.RecursiveLayerChange is likely a custom utility from the FPS Creator Kit
        // LayerMask.NameToLayer converts the string "Target" to the corresponding layer number
        // Layers in Unity are used for collision detection, rendering, and raycasting organization
        Helpers.RecursiveLayerChange(transform, LayerMask.NameToLayer("Target"));
    }

    // Start() is a Unity lifecycle method called before the first frame update
    // It runs after Awake() and only if the GameObject is active
    void Start()
    {
        // Check if DestroyedEffect particle system is assigned (not null)
        if (DestroyedEffect)
            // Initialize an object pool for the particle effect with 16 pre-instantiated instances
            // Object pooling improves performance by reusing objects instead of constantly creating/destroying them
            PoolSystem.Instance.InitPool(DestroyedEffect, 16);

        // Initialize current health to the maximum health value
        m_CurrentHealth = health;

        // Check if IdleSource audio component is assigned
        if (IdleSource != null)
            // Set a random starting time in the audio clip to avoid all targets playing in sync
            // Random.Range generates a random float between 0 and the clip's total length
            IdleSource.time = Random.Range(0.0f, IdleSource.clip.length);
    }

    // Public method called when this target takes damage (likely called by weapon scripts)
    // Parameter: damage amount to subtract from current health
    public void Got(float damage)
    {
        // Subtract the damage amount from current health
        m_CurrentHealth -= damage;

        // Check if HitPlayer audio component is assigned
        if (HitPlayer != null)
            // Play a random hit sound effect
            HitPlayer.PlayRandom();

        // If target still has health remaining, exit the method early (target survives)
        if (m_CurrentHealth > 0)
            return;

        // Store the target's world position before it gets destroyed
        // transform.position gets the GameObject's position in 3D world space
        Vector3 position = transform.position;

        // Comment explains why we need special audio handling for destruction
        //the audiosource of the target will get destroyed, so we need to grab a world one and play the clip through it

        // Check if HitPlayer is assigned for destruction audio
        if (HitPlayer != null)
        {
            // Get an available AudioSource from a global pool system
            // WorldAudioPool appears to be a custom system from the FPS Creator Kit
            var source = WorldAudioPool.GetWorldSFXSource();

            // Position the pooled audio source at the target's location
            source.transform.position = position;

            // Copy the pitch setting from the original audio source
            source.pitch = HitPlayer.source.pitch;

            // Play one random clip from the HitPlayer without looping
            source.PlayOneShot(HitPlayer.GetRandomClip());
        }

        // Check if destruction particle effect is assigned
        if (DestroyedEffect != null)
        {
            // Get a particle system instance from the object pool
            // GetInstance<T> is a generic method that returns a pooled object of type T
            var effect = PoolSystem.Instance.GetInstance<ParticleSystem>(DestroyedEffect);

            // Reset the particle system's time to 0 to start the effect from the beginning
            effect.time = 0.0f;

            // Start playing the particle effect
            effect.Play();

            // Position the effect at the target's location
            effect.transform.position = position;
        }

        // Mark this target as destroyed
        m_Destroyed = true;

        // Deactivate the GameObject (makes it invisible and stops all components)
        // SetActive(false) is more efficient than Destroy() and allows for object pooling
        gameObject.SetActive(false);

        // Notify the game system that a target was destroyed and award points
        // GameSystem.Instance uses the Singleton pattern to provide global game state management
        GameSystem.Instance.TargetDestroyed(pointValue);
    }
}