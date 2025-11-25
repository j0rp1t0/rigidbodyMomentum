// ============================================================================
// USING STATEMENTS - Import functionality from other code libraries
// ============================================================================

// Import System.Collections namespace for basic collection types (ArrayList, etc.)
// Included by default but not heavily used in this script
using System.Collections;

// Import System.Collections.Generic for generic collections (List<T>, Dictionary<K,V>)
// Used for type-safe collections in modern C# code
using System.Collections.Generic;

// Import UnityEngine namespace - core Unity API for GameObjects, physics, rendering
// This is the foundation for all Unity scripting
using UnityEngine;

// ============================================================================
// PROJECTILE CLASS - Physics-based projectile with area damage
// ============================================================================
//
// PURPOSE:
// Represents a physical projectile (rocket, grenade, explosive bullet) that:
// - Travels using Unity physics (Rigidbody)
// - Explodes on impact or after timeout
// - Deals area-of-effect (AOE) splash damage
// - Can be pooled for performance (reused instead of destroyed)
//
// CORE MECHANICS:
// 1. Launch: Weapon fires projectile with initial force
// 2. Flight: Physics simulation moves projectile through world
// 3. Impact: Collision or timeout triggers destruction
// 4. Explosion: Area damage affects all targets in radius
// 5. Cleanup: Returns to pool for reuse
//
// PHYSICS-BASED PROJECTILE VS RAYCASTING:
// This class uses physics simulation (Rigidbody) instead of instant raycasting
// 
// Physics Projectile (this script):
// ✓ Visible projectile travels through space
// ✓ Affected by gravity (arc trajectory)
// ✓ Can miss fast-moving targets
// ✓ Travel time creates gameplay skill ceiling
// ✓ Players can see and dodge projectiles
// ✗ More expensive (physics calculations every frame)
// ✗ Can pass through thin objects at high speed
// Example weapons: Rockets, grenades, arrows, slow-moving energy balls
//
// Raycast (instant hit):
// ✓ Instant hit - no travel time
// ✓ Perfect accuracy at any range
// ✓ Never passes through objects
// ✓ Very cheap performance-wise
// ✗ No visible projectile movement
// ✗ Impossible to dodge (hitscan)
// Example weapons: Rifles, pistols, lasers, sniper rifles
//
// AREA DAMAGE SYSTEM:
// Uses Physics.OverlapSphere to detect all targets in explosion radius
// All targets within radius take full damage (no falloff in this implementation)
// Could be extended with:
// - Damage falloff (less damage at edge of radius)
// - Line-of-sight check (blocked by walls)
// - Different damage to different target types
// - Knockback/ragdoll effects
//
// POOLING INTEGRATION:
// This script is designed to work with PoolSystem for performance
// Instead of Destroy() creating/destroying projectiles:
// - Weapon.Fire() gets projectile from pool via GetInstance()
// - Projectile.Destroy() deactivates and returns to pool
// - Same projectile instance reused many times during gameplay
// - Eliminates allocation/garbage collection during combat
//
// FPS KIT ARCHITECTURE:
// Projectile works with several other systems:
// - Weapon: Launches projectiles via Launch() method
// - PoolSystem: Manages projectile reuse for performance
// - Target: Takes damage from explosions
// - WorldAudioPool: Plays positioned explosion sounds
// - Unity Physics: Handles movement, collision detection, gravity
//
// DESIGN PATTERNS:
// - Object Pool: Reused instead of created/destroyed each shot
// - Component: Attached to projectile GameObject prefab
// - Physics Simulation: Uses Rigidbody for realistic movement
// - Area of Effect: OverlapSphere for splash damage
//
// COMMON USE CASES:
// - Rocket launcher projectiles (explode on impact, large radius)
// - Grenade launcher projectiles (arc trajectory, timed explosion)
// - Energy balls (glowing projectiles with explosion VFX)
// - Mortar shells (high arc, large damage radius)
//
// ============================================================================

public class Projectile : MonoBehaviour
{
    // ========================================================================
    // STATIC COLLISION CACHE - Shared array for sphere overlap results
    // ========================================================================

    /// <summary>
    /// Static array used by Physics.OverlapSphereNonAlloc to store collision results
    /// Shared across ALL projectile instances to minimize allocations
    /// 
    /// STATIC VARIABLE:
    /// - Belongs to the CLASS, not individual instances
    /// - Only ONE copy exists in memory, shared by all Projectile objects
    /// - Persists for the entire lifetime of the game
    /// 
    /// WHY STATIC?
    /// Performance optimization to avoid garbage collection
    /// 
    /// WITHOUT STATIC (the problem):
    /// - Each projectile has its own s_SphereCastPool array
    /// - 10 rockets in flight = 10 separate 32-element arrays
    /// - Arrays get garbage collected when projectiles are destroyed
    /// - Causes allocation spikes and GC stuttering
    /// 
    /// WITH STATIC (the solution):
    /// - All projectiles share ONE array
    /// - Array created once at startup
    /// - Never garbage collected (lives forever)
    /// - Zero allocation during gameplay
    /// 
    /// ARRAY SIZE: 32 elements
    /// This is the maximum number of targets that can be damaged by one explosion
    /// If explosion hits more than 32 targets, only first 32 are processed
    /// 32 is generous - most explosions hit fewer than 10 targets
    /// 
    /// NAMING CONVENTION:
    /// s_ prefix indicates STATIC variable (Unity/C# convention)
    /// Helps distinguish static fields from instance fields (m_ prefix)
    /// 
    /// COLLIDER TYPE:
    /// Array of Collider - can hold any collider type:
    /// - BoxCollider (rectangular collision volumes)
    /// - SphereCollider (spherical collision volumes)
    /// - CapsuleCollider (pill-shaped, used for characters)
    /// - MeshCollider (complex mesh-based collision)
    /// 
    /// USAGE PATTERN:
    /// 1. Projectile explodes
    /// 2. OverlapSphereNonAlloc fills this array with nearby colliders
    /// 3. Loop through array to damage targets
    /// 4. Array gets reused by next explosion
    /// 
    /// THREAD SAFETY:
    /// NOT thread-safe - but Unity physics is single-threaded anyway
    /// Only one projectile can use this array at a time
    /// This is safe because Update() runs sequentially, not parallel
    /// 
    /// ALTERNATIVE APPROACHES:
    /// - Physics.OverlapSphere(): Returns new array each call (allocates garbage)
    /// - Instance array: Each projectile has own array (more memory, still GC)
    /// - This approach: Zero allocation, minimal memory, best performance
    /// </summary>
    static Collider[] s_SphereCastPool = new Collider[32];

    // ========================================================================
    // PUBLIC INSPECTOR FIELDS - Configure projectile behavior
    // ========================================================================

    /// <summary>
    /// Should this projectile be destroyed when it hits something?
    /// 
    /// TRUE (default):
    /// - Projectile explodes on first collision (impact detonation)
    /// - Example: Rocket that explodes when hitting wall or enemy
    /// - Realistic for most explosive projectiles
    /// 
    /// FALSE:
    /// - Projectile passes through collisions without exploding
    /// - Only explodes when timeout (TimeToDestroyed) is reached
    /// - Example: Bouncing grenade that rolls before exploding
    /// - Example: Penetrating projectile that goes through multiple enemies
    /// 
    /// GAME DESIGN USE:
    /// - Rockets: TRUE (explode on impact)
    /// - Grenades: FALSE (bounce and roll, timed explosion)
    /// - Penetrating rounds: FALSE (pierce through targets)
    /// - Sticky bombs: FALSE (attach to surface, timed explosion)
    /// 
    /// INSPECTOR:
    /// Public field appears as checkbox in Unity Inspector
    /// Level designers can configure per-weapon without code changes
    /// </summary>
    public bool DestroyedOnHit = true;

    /// <summary>
    /// Lifetime in seconds before projectile automatically explodes
    /// 
    /// PURPOSE:
    /// Safety timeout to prevent projectiles from existing forever
    /// Prevents memory leaks from projectiles that miss all targets
    /// 
    /// COMMON VALUES:
    /// - 2-3 seconds: Indoor weapons (tight spaces, quick encounters)
    /// - 4-5 seconds: This default (balanced for most scenarios)
    /// - 6-10 seconds: Long-range outdoor weapons (sniper maps)
    /// 
    /// GAMEPLAY IMPACT:
    /// Too short: Projectiles disappear mid-flight at long range
    /// Too long: More objects in memory, potential performance impact
    /// 
    /// TIMED EXPLOSIVES:
    /// For grenades with DestroyedOnHit=false:
    /// - This controls the fuse timer
    /// - Example: 3.0f = grenade explodes 3 seconds after being thrown
    /// 
    /// TECHNICAL:
    /// Countdown starts in Launch() when m_TimeSinceLaunch is reset
    /// Update() checks if m_TimeSinceLaunch >= TimeToDestroyed
    /// When timeout reached, calls Destroy() to trigger explosion
    /// </summary>
    public float TimeToDestroyed = 4.0f;

    /// <summary>
    /// Explosion radius in Unity units (meters)
    /// Defines how far from impact point targets can take damage
    /// 
    /// AREA OF EFFECT (AOE):
    /// Physics.OverlapSphere checks this radius for targets
    /// All targets within this distance from explosion center take damage
    /// 
    /// COMMON VALUES:
    /// - 2-3 units: Small explosion (grenade, small rocket)
    /// - 5 units: This default (medium rocket, standard explosive)
    /// - 8-12 units: Large explosion (nuke, big missile)
    /// - 15+ units: Massive explosion (game-ending superweapon)
    /// 
    /// GAME BALANCE:
    /// Larger radius = easier to hit, more powerful, potentially overpowered
    /// Smaller radius = requires precision, skill-based, balanced
    /// 
    /// VISUAL FEEDBACK:
    /// Should match the visual explosion effect size
    /// Players expect damage radius to match what they see
    /// If mismatch: Feels unfair (killed outside visual or safe inside visual)
    /// 
    /// LEVEL DESIGN:
    /// Small rooms: Use smaller radius (avoid killing through walls)
    /// Open areas: Can use larger radius safely
    /// 
    /// EDITOR VISUALIZATION:
    /// OnDrawGizmosSelected() draws yellow wireframe sphere of this size
    /// Helps designers visualize radius when placing/testing projectiles
    /// </summary>
    public float ReachRadius = 5.0f;

    /// <summary>
    /// Damage dealt to each target within explosion radius
    /// 
    /// DAMAGE MODEL:
    /// This implementation uses FLAT DAMAGE (no falloff)
    /// All targets in radius take full damage regardless of distance
    /// 
    /// FLAT DAMAGE:
    /// - Target at center: Takes 10 damage
    /// - Target at edge (4.9 units away): Takes 10 damage
    /// - Target just outside (5.1 units away): Takes 0 damage
    /// - Simple to implement and understand
    /// - Common in arcade-style games
    /// 
    /// FALLOFF DAMAGE (not implemented, but could be added):
    /// Damage = damage * (1 - distance/ReachRadius)
    /// - Target at center: Takes 10 damage (100%)
    /// - Target halfway: Takes 5 damage (50%)
    /// - Target at edge: Takes 1 damage (10%)
    /// - More realistic and forgiving
    /// - Rewards precision without punishing near-misses
    /// 
    /// BALANCE CONSIDERATIONS:
    /// - 5-10 damage: Weak, requires multiple hits
    /// - 10-25 damage: This default (medium, 2-5 hits to kill)
    /// - 50-100 damage: Strong, instant or near-instant kill
    /// - 100+ damage: One-hit-kill weapon
    /// 
    /// RELATIONSHIP WITH TARGET HEALTH:
    /// If targets have 50 health and damage is 10:
    /// - Requires 5 direct hits to kill
    /// - High skill requirement, balanced
    /// 
    /// If targets have 10 health and damage is 10:
    /// - One-hit-kill
    /// - Low skill requirement, potentially overpowered
    /// 
    /// CONSISTENCY:
    /// Should be balanced against other weapon damages in game
    /// Explosive weapons typically do more damage than bullets
    /// </summary>
    public float damage = 10.0f;

    /// <summary>
    /// Audio clip played when projectile explodes
    /// 
    /// SOUND DESIGN:
    /// - Provides feedback that explosion occurred
    /// - Helps player judge distance to combat
    /// - Immersion and atmosphere
    /// 
    /// TYPICAL SOUNDS:
    /// - Explosion: BOOM, blast, concussive sound
    /// - Impact: CRACK, SMASH, collision sound
    /// - Energy: FZZZT, electrical discharge
    /// 
    /// AUDIO SOURCE:
    /// Played through WorldAudioPool system, not projectile's AudioSource
    /// 
    /// WHY POOLED AUDIO?
    /// Projectile gets deactivated immediately after explosion
    /// If audio was on projectile's AudioSource, it would stop abruptly
    /// WorldAudioPool provides a temporary AudioSource at the explosion location
    /// Audio finishes playing even after projectile returns to pool
    /// 
    /// SPATIAL AUDIO:
    /// 3D positioned sound at explosion location
    /// Volume decreases with distance from listener
    /// Panning based on direction (left/right speakers)
    /// 
    /// PITCH VARIATION:
    /// Destroy() randomizes pitch: Random.Range(0.8f, 1.1f)
    /// Prevents repetitive sound when multiple projectiles explode
    /// Adds variety and realism
    /// 
    /// NULL HANDLING:
    /// If null, no sound plays (silent explosion)
    /// Useful for testing or silent weapons
    /// </summary>
    public AudioClip DestroyedSound;

    /// <summary>
    /// Particle effect or GameObject spawned at explosion location
    /// 
    /// VISUAL FEEDBACK:
    /// - Explosion fireball, smoke cloud, debris
    /// - Energy burst, electric spark, magic effect
    /// - Provides visual confirmation of impact
    /// - Communicates danger zone to nearby players
    /// 
    /// TYPICAL PREFABS:
    /// - ParticleSystem: Explosion effect with fire, smoke, sparks
    /// - GameObject: Complex explosion with multiple particle systems
    /// - Light: Flash of light from explosion
    /// - Decal: Scorch mark or impact crater on ground
    /// 
    /// POOLING:
    /// This prefab is pooled in Awake() via PoolSystem.InitPool()
    /// Each explosion reuses effect from pool instead of Instantiate
    /// Effect should auto-deactivate when finished playing
    /// ParticleSystem can use "Stop Action: Disable" to auto-deactivate
    /// 
    /// POOL SIZE:
    /// InitPool(PrefabOnDestruction, 4) creates 4 instances
    /// Assumes maximum 4 explosions visible simultaneously
    /// If more than 4 explosions occur at once, pool auto-grows
    /// 
    /// LIFESPAN:
    /// Effect should have limited duration (1-3 seconds typical)
    /// ParticleSystem.duration controls how long it plays
    /// After playing, effect should disable itself or use finite particles
    /// 
    /// POSITION:
    /// effect.transform.position = explosion position
    /// Appears exactly where projectile impacted
    /// 
    /// TODO NOTE:
    /// Original comment suggests this could be pooled elsewhere
    /// Currently each projectile prefab pools its own destruction effect
    /// Alternative: Global effect manager pools common effects
    /// Would reduce duplicate pools if multiple weapons share effects
    /// </summary>
    //TODO : maybe pool that somewhere to not have to create one for each projectile.
    public GameObject PrefabOnDestruction;

    // ========================================================================
    // PRIVATE INSTANCE FIELDS - Runtime state tracking
    // ========================================================================

    /// <summary>
    /// Reference to the weapon that fired this projectile
    /// 
    /// OWNERSHIP TRACKING:
    /// Stores which weapon launched this projectile
    /// Allows projectile to return itself to the weapon's pool when destroyed
    /// 
    /// POOLING ARCHITECTURE:
    /// Each weapon maintains its own projectile pool
    /// When fired: Weapon gets projectile from its pool
    /// When destroyed: Projectile calls m_Owner.ReturnProjecticle(this)
    /// Weapon adds projectile back to its available pool
    /// 
    /// WHY WEAPON-SPECIFIC POOLS?
    /// Different weapons have different projectile prefabs
    /// Rocket launcher pool contains rocket projectiles
    /// Grenade launcher pool contains grenade projectiles
    /// Cannot mix projectile types in same pool
    /// 
    /// LIFETIME:
    /// Set in Launch() when weapon fires
    /// Used in Destroy() to return projectile to correct weapon
    /// 
    /// TYPE:
    /// Weapon is the main weapon class in FPS Kit
    /// Contains fire logic, ammo management, and projectile pooling
    /// 
    /// NAMING CONVENTION:
    /// m_ prefix indicates member/instance variable (not static)
    /// </summary>
    Weapon m_Owner;

    /// <summary>
    /// Reference to the Rigidbody component for physics simulation
    /// 
    /// RIGIDBODY:
    /// Unity component that enables physics simulation
    /// Handles movement, gravity, collision response, forces
    /// 
    /// WHAT RIGIDBODY DOES:
    /// - Applies gravity (projectile falls in arc)
    /// - Responds to forces (AddForce in Launch)
    /// - Detects collisions (triggers OnCollisionEnter)
    /// - Has velocity (speed and direction)
    /// - Has angular velocity (rotation speed)
    /// - Has mass (affects physics interactions)
    /// 
    /// CACHED REFERENCE:
    /// Retrieved once in Awake() via GetComponent<Rigidbody>()
    /// Stored for repeated use (GetComponent is relatively expensive)
    /// Accessing m_Rigidbody is fast (direct reference, no lookup)
    /// 
    /// USAGE IN THIS SCRIPT:
    /// - Launch(): AddForce to propel projectile
    /// - Destroy(): Zero out velocities to stop movement
    /// 
    /// PHYSICS SETTINGS (set in Inspector on Rigidbody):
    /// - Mass: How heavy projectile is (affects force needed)
    /// - Drag: Air resistance (higher = slows faster)
    /// - Angular Drag: Rotation resistance
    /// - Use Gravity: TRUE for realistic arc trajectory
    /// - Is Kinematic: FALSE (needs physics simulation)
    /// - Interpolate: Smooth movement between physics updates
    /// - Collision Detection: Continuous for fast projectiles
    /// 
    /// PERFORMANCE:
    /// Physics simulation runs in FixedUpdate (usually 50Hz)
    /// Many projectiles = more physics calculations
    /// Consider pooling to limit active projectile count
    /// </summary>
    Rigidbody m_Rigidbody;

    /// <summary>
    /// Elapsed time since projectile was launched (in seconds)
    /// 
    /// LIFETIME TRACKING:
    /// Reset to 0.0f in Launch() when projectile is fired
    /// Incremented each frame in Update() by Time.deltaTime
    /// Used to enforce maximum lifetime (TimeToDestroyed)
    /// 
    /// TIME.DELTATIME:
    /// Time elapsed since last frame (typically 0.016s at 60 FPS)
    /// Frame-rate independent timing
    /// Example: 60 FPS = deltaTime ~0.016, 30 FPS = deltaTime ~0.033
    /// 
    /// TIMEOUT CHECK:
    /// Each frame, Update() checks: if (m_TimeSinceLaunch >= TimeToDestroyed)
    /// When condition is true, triggers Destroy() to explode projectile
    /// 
    /// EXAMPLE TIMELINE:
    /// Frame 0: Launch() called, m_TimeSinceLaunch = 0.0f
    /// Frame 1: Update adds 0.016s, m_TimeSinceLaunch = 0.016f
    /// Frame 2: Update adds 0.016s, m_TimeSinceLaunch = 0.032f
    /// ...
    /// Frame 240: m_TimeSinceLaunch = 4.001f >= 4.0f, explosion!
    /// 
    /// WHY NEEDED?
    /// Prevents projectiles from flying forever
    /// Catches projectiles that miss all targets and fly into skybox
    /// Memory management - limits active projectile count
    /// 
    /// PRECISION:
    /// Float type has sufficient precision for gameplay timing
    /// Accuracy to ~0.001 seconds is more than enough
    /// </summary>
    float m_TimeSinceLaunch;

    // ========================================================================
    // AWAKE - Unity lifecycle method for initialization
    // ========================================================================

    /// <summary>
    /// Unity lifecycle method called when script instance is loaded
    /// 
    /// EXECUTION ORDER:
    /// 1. Awake() - Called once when object is instantiated
    /// 2. OnEnable() - Called when object is activated
    /// 3. Start() - Called before first frame update (if object is active)
    /// 4. Update() - Called every frame
    /// 
    /// AWAKE VS START:
    /// - Awake: Runs even if GameObject is inactive (SetActive false)
    /// - Start: Only runs if GameObject is active
    /// - Awake: Best for getting component references, initialization
    /// - Start: Best for setup that requires other objects to be initialized
    /// 
    /// WHEN CALLED:
    /// - Scene loads and projectile prefab is in scene: Awake runs immediately
    /// - Projectile instantiated at runtime: Awake runs on Instantiate()
    /// - Projectile retrieved from pool: Awake already ran (doesn't re-run)
    /// 
    /// POOLING CONSIDERATION:
    /// Awake() only runs ONCE per projectile instance lifetime
    /// When projectile returns to pool (SetActive false), Awake doesn't re-run
    /// When projectile retrieved from pool (SetActive true), Awake doesn't re-run
    /// This is why we do one-time setup here (pool init, component cache)
    /// </summary>
    void Awake()
    {
        // ====================================================================
        // INITIALIZE DESTRUCTION EFFECT POOL
        // ====================================================================

        // Initialize the object pool for explosion effects
        // Creates 4 pre-instantiated copies of the destruction effect
        // 
        // PARAMETERS:
        // - PrefabOnDestruction: The explosion effect prefab to pool
        // - 4: Number of instances to pre-create
        // 
        // WHY POOL DESTRUCTION EFFECTS?
        // - Explosions are created/destroyed frequently (every projectile)
        // - Instantiate() is expensive (particle systems have many components)
        // - Pooling reuses effect instances for better performance
        // - Prevents garbage collection spikes during heavy combat
        // 
        // POOL SIZE REASONING:
        // 4 instances assumes maximum 4 simultaneous explosions
        // Example scenarios:
        // - 4 projectiles hit at same moment: All 4 effects play
        // - 5th projectile hits while 4 playing: Pool auto-grows to 5
        // - Earlier effects finish playing: Available for reuse
        // 
        // SINGLETON ACCESS:
        // PoolSystem.Instance uses singleton pattern
        // One global PoolSystem manages all object pools
        // .Instance is the static property to access it
        // 
        // EXECUTION TIMING:
        // Awake runs when projectile is first created
        // For pooled projectiles, this is during PoolSystem.InitPool(projectile)
        // Not during Launch() - would be redundant and slow
        // 
        // NULL CHECK CONSIDERATION:
        // If PrefabOnDestruction is null, InitPool does nothing
        // PoolSystem's InitPool should handle null gracefully
        // Or could add check: if (PrefabOnDestruction != null)
        PoolSystem.Instance.InitPool(PrefabOnDestruction, 4);

        // ====================================================================
        // CACHE RIGIDBODY COMPONENT REFERENCE
        // ====================================================================

        // Get and cache reference to Rigidbody component
        // 
        // GETCOMPONENT:
        // Searches this GameObject for a component of type Rigidbody
        // Returns reference to the component if found, null if not found
        // 
        // WHY CACHE?
        // GetComponent is relatively expensive (component lookup)
        // Calling it repeatedly in Update() or other frequent methods is wasteful
        // Cache once in Awake(), use cached reference throughout lifetime
        // 
        // PERFORMANCE COMPARISON:
        // Cached: Direct memory reference, instant access (nanoseconds)
        // GetComponent: Component lookup via type, slower (microseconds)
        // Over hundreds of frames, caching saves significant CPU time
        // 
        // COMPONENT REQUIREMENT:
        // Projectile MUST have a Rigidbody component to function
        // Without Rigidbody: No physics, no movement, no collision
        // Consider adding [RequireComponent(typeof(Rigidbody))] attribute
        // Would ensure Rigidbody is present and auto-add if missing
        // 
        // NULL HANDLING:
        // If GetComponent returns null, m_Rigidbody will be null
        // Later code (Launch, Destroy) will throw NullReferenceException
        // Could add null check: if (m_Rigidbody == null) Debug.LogError(...)
        m_Rigidbody = GetComponent<Rigidbody>();
    }

    // ========================================================================
    // LAUNCH - Initialize and fire the projectile
    // ========================================================================

    /// <summary>
    /// Launches the projectile from a weapon with specified direction and force
    /// Called by Weapon when firing
    /// 
    /// CALLED BY:
    /// Weapon.Fire() or similar weapon shooting method
    /// Example: weapon.GetProjectileFromPool().Launch(weapon, aimDirection, fireForce)
    /// 
    /// LAUNCH SEQUENCE:
    /// 1. Store owner weapon reference
    /// 2. Position projectile at weapon muzzle
    /// 3. Rotate projectile to aim direction
    /// 4. Activate projectile GameObject
    /// 5. Reset lifetime timer
    /// 6. Apply physics force to propel projectile
    /// 
    /// POOLING WORKFLOW:
    /// When weapon fires:
    /// 1. Weapon gets inactive projectile from pool
    /// 2. Weapon calls Launch() to configure and activate it
    /// 3. Projectile flies through world
    /// 4. Projectile explodes and deactivates itself
    /// 5. Projectile returns to weapon's pool via ReturnProjecticle()
    /// 6. Repeat - same projectile instance reused many times
    /// </summary>
    /// <param name="launcher">The weapon that fired this projectile</param>
    /// <param name="direction">World-space direction to fire (normalized vector)</param>
    /// <param name="force">Force magnitude to apply (higher = faster projectile)</param>
    public void Launch(Weapon launcher, Vector3 direction, float force)
    {
        // ====================================================================
        // STORE OWNER REFERENCE
        // ====================================================================

        // Store reference to the weapon that launched this projectile
        // Used later in Destroy() to return projectile to correct weapon's pool
        // 
        // OWNERSHIP:
        // Each weapon owns a pool of its specific projectile type
        // Projectile needs to know which weapon to return to
        // Example: Rocket launcher's pool should only contain rockets
        // 
        // WHY NEEDED:
        // In Destroy(), we call: m_Owner.ReturnProjecticle(this)
        // Weapon adds projectile back to its available pool
        // Without this reference, projectile couldn't return to correct pool
        m_Owner = launcher;

        // ====================================================================
        // POSITION PROJECTILE AT MUZZLE
        // ====================================================================

        // Set projectile position to weapon's muzzle point
        // transform.position is the GameObject's world-space position
        // 
        // GETCORRECTEDMUZZLEPLACE():
        // Weapon method that returns the exact point where projectiles spawn
        // Usually the tip of the weapon barrel or muzzle flash location
        // "Corrected" likely means adjusted for camera position or animation
        // 
        // WHY SPAWN AT MUZZLE?
        // - Visual realism: Projectile appears to come from weapon
        // - Prevents spawning inside weapon model (clipping)
        // - Avoids self-collision with player
        // - Matches muzzle flash position for visual coherence
        // 
        // ALTERNATIVE APPROACHES:
        // - Spawn at camera position (player's eye view)
        // - Spawn offset in front of weapon
        // - Spawn at raycast hit point for instant bullets
        // 
        // POOLING NOTE:
        // Projectile's transform.position may be anywhere from previous use
        // This line resets position to correct spawn point
        transform.position = launcher.GetCorrectedMuzzlePlace();

        // ====================================================================
        // AIM PROJECTILE
        // ====================================================================

        // Set projectile's forward direction to match weapon's aim
        // transform.forward is the local Z-axis (blue arrow in editor)
        // 
        // ENDPOINT:
        // launcher.EndPoint is a Transform representing weapon's aim point
        // Usually at the tip of the barrel
        // .forward is the Z-axis direction this Transform is pointing
        // 
        // WHY SET ROTATION?
        // - Visual: Projectile model should point where it's going
        // - Physics: Some projectiles use forward direction for trail effects
        // - Consistency: Matches weapon's aim direction
        // 
        // EXAMPLE:
        // If weapon is aimed to the right (+X direction):
        // - EndPoint.forward = (1, 0, 0)
        // - Projectile rotates to point right
        // - Projectile model (rocket, grenade) visually aims right
        // 
        // ALTERNATIVE:
        // Could use: transform.rotation = launcher.EndPoint.rotation
        // Would match entire rotation, not just forward direction
        // forward = only copies direction, rotation = copies full orientation
        transform.forward = launcher.EndPoint.forward;

        // ====================================================================
        // ACTIVATE PROJECTILE
        // ====================================================================

        // Enable the projectile GameObject
        // 
        // SETACTIVE(TRUE):
        // - Makes projectile visible (enables renderers)
        // - Enables all components (scripts, colliders, particle systems)
        // - Starts Update() calls each frame
        // - Enables Rigidbody physics simulation
        // - Triggers OnEnable() callback on all scripts
        // - Starts any particle effects on the projectile
        // 
        // POOLING LIFECYCLE:
        // When projectile is in pool: SetActive(false) - invisible, dormant
        // When retrieved from pool: Still inactive (for configuration)
        // During Launch(): SetActive(true) - now visible and active
        // After explosion: SetActive(false) - back to dormant pool state
        // 
        // WHY HERE?
        // Activates AFTER position and rotation are set
        // Prevents one frame of projectile appearing at wrong location
        // Ensures clean visual appearance (no teleporting visible object)
        gameObject.SetActive(true);

        // ====================================================================
        // RESET LIFETIME TIMER
        // ====================================================================

        // Reset the launch timer to zero
        // Starts counting lifetime from this moment
        // 
        // WHY RESET?
        // If this projectile was pooled and reused, m_TimeSinceLaunch
        // might contain the value from the previous flight (e.g., 4.0f)
        // Resetting ensures timer starts fresh at 0 for this launch
        // 
        // TIMER PURPOSE:
        // Update() increments this each frame: m_TimeSinceLaunch += Time.deltaTime
        // When m_TimeSinceLaunch >= TimeToDestroyed, projectile explodes
        // Prevents projectiles from existing indefinitely
        // 
        // EXAMPLE:
        // First launch: 0.0f -> 0.016 -> 0.032 -> ... -> 4.0f (explode)
        // Return to pool: m_TimeSinceLaunch still 4.0f (dormant)
        // Second launch: Reset to 0.0f -> 0.016 -> 0.032 -> ... (fresh timer)
        m_TimeSinceLaunch = 0.0f;

        // ====================================================================
        // APPLY PHYSICS FORCE
        // ====================================================================

        // Apply force to Rigidbody to propel projectile forward
        // 
        // ADDFORCE:
        // Applies a force to the Rigidbody, causing acceleration
        // Force accumulates with existing forces (gravity, drag)
        // Force is applied instantly (impulse mode by default)
        // 
        // PARAMETERS:
        // - direction: Vector3 normalized direction (which way to go)
        // - force: Float magnitude (how hard to push)
        // - direction * force: Combines into force vector
        // 
        // EXAMPLE:
        // direction = (0, 0, 1) = forward
        // force = 500
        // direction * force = (0, 0, 500)
        // Result: 500 Newtons of force applied forward
        // 
        // PHYSICS RESULT:
        // F = ma (Force = Mass * Acceleration)
        // If projectile mass is 1kg:
        // - Force 500N / Mass 1kg = 500 m/s² acceleration
        // - After 1 frame (~0.02s): Velocity ~10 m/s
        // - Gravity also applies: -9.81 m/s² downward
        // - Result: Projectile moves forward and starts arcing down
        // 
        // FORCE MAGNITUDE TUNING:
        // - Low force (100-300): Slow projectile, high arc, grenade-like
        // - Medium force (500-1000): Balanced, visible arc
        // - High force (2000+): Fast, nearly straight trajectory, rocket-like
        // 
        // DIRECTION:
        // Should be normalized (length 1.0) for predictable force application
        // Non-normalized direction causes inconsistent speeds
        // Weapon should pass Vector3.normalized direction
        // 
        // FORCEMODE:
        // Default is ForceMode.Force (continuous force over time)
        // Could specify: AddForce(direction * force, ForceMode.Impulse)
        // - Force: Continuous push (affected by mass, Time.fixedDeltaTime)
        // - Impulse: Instant velocity change (affected by mass)
        // - Acceleration: Ignores mass
        // - VelocityChange: Instant velocity change, ignores mass
        m_Rigidbody.AddForce(direction * force);
    }

    // ========================================================================
    // ONCOLLISIONENTER - Unity physics collision callback
    // ========================================================================

    /// <summary>
    /// Unity callback triggered when this object collides with another
    /// 
    /// PHYSICS EVENT:
    /// Called automatically by Unity's physics system
    /// Happens during physics update (FixedUpdate, typically 50Hz)
    /// 
    /// REQUIREMENTS FOR TRIGGER:
    /// 1. This GameObject has Collider (projectile's collision shape)
    /// 2. This GameObject has Rigidbody (provides physics simulation)
    /// 3. Other GameObject has Collider (what we're hitting)
    /// 4. At least one Collider is NOT marked as trigger
    /// 5. Rigidbody is NOT kinematic (allows physics simulation)
    /// 6. Collision layers allow interaction (layer collision matrix)
    /// 
    /// COLLISION VS TRIGGER:
    /// - OnCollisionEnter: Physical collision with impact force
    ///   Used for: Bouncing, blocking, realistic impacts
    ///   Example: Grenade bouncing off walls
    /// 
    /// - OnTriggerEnter: Overlap detection without physics response
    ///   Used for: Pickups, damage zones, detection areas
    ///   Example: Walking through ammo box pickup
    /// 
    /// THIS SCRIPT:
    /// Uses OnCollisionEnter for realistic projectile impacts
    /// Projectile physically collides with walls, enemies, ground
    /// 
    /// COLLISION PARAMETER:
    /// Contains data about the collision:
    /// - other.collider: The collider we hit
    /// - other.gameObject: The GameObject we hit
    /// - other.contacts: Array of contact points
    /// - other.relativeVelocity: Impact velocity
    /// - other.impulse: Force of impact
    /// </summary>
    /// <param name="other">Collision data about what we hit</param>
    void OnCollisionEnter(Collision other)
    {
        // ====================================================================
        // CHECK DESTRUCTION MODE
        // ====================================================================

        // Check if projectile should explode on impact
        // 
        // DESTROYEDONHIT:
        // Public bool set in Inspector or code
        // - True: Explode immediately on first collision (rockets)
        // - False: Bounce/pass through collisions (grenades, penetrating rounds)
        // 
        // CONTROL FLOW:
        // if (true): Execute Destroy() - explode projectile
        // if (false): Skip Destroy() - projectile continues flying
        // 
        // USE CASES:
        // 
        // ROCKETS (DestroyedOnHit = true):
        // - Flies straight until hitting anything
        // - First collision: Immediate explosion
        // - Realistic rocket behavior
        // 
        // GRENADES (DestroyedOnHit = false):
        // - Bounces off walls and floor
        // - Ricochets and rolls around
        // - Explodes only when timer runs out
        // - Allows strategic banking shots
        // 
        // PENETRATING ROUNDS (DestroyedOnHit = false):
        // - Passes through multiple enemies
        // - Continues until timeout
        // - High-skill weapon design
        // 
        // PARAMETER UNUSED:
        // 'other' collision data is not used in this implementation
        // Could be extended to check what was hit:
        // - if (other.gameObject.CompareTag("Enemy")) { explode }
        // - if (other.gameObject.layer == wallLayer) { bounce }
        // - Different behavior based on collision target
        if (DestroyedOnHit)
        {
            // Trigger explosion sequence
            // Deals area damage, plays effects, returns to pool
            Destroy();
        }

        // If DestroyedOnHit is false:
        // Method ends, projectile continues flying
        // Rigidbody physics handles bounce naturally
        // Collision provides realistic impact response
        // Projectile will eventually timeout and call Destroy() from Update()
    }

    // ========================================================================
    // DESTROY - Explosion sequence and cleanup
    // ========================================================================

    /// <summary>
    /// Triggers projectile explosion with area damage and effects
    /// Called when projectile hits something OR lifetime expires
    /// 
    /// NAMING NOTE:
    /// Method name "Destroy" is misleading - doesn't actually destroy GameObject
    /// Better name would be "Explode" or "Detonate"
    /// GameObject is deactivated and returned to pool, not destroyed
    /// 
    /// EXPLOSION SEQUENCE:
    /// 1. Store explosion position
    /// 2. Spawn visual effect (fire, smoke)
    /// 3. Detect all targets in explosion radius
    /// 4. Apply damage to each target
    /// 5. Deactivate projectile GameObject
    /// 6. Reset physics velocities
    /// 7. Return projectile to weapon's pool
    /// 8. Play explosion sound at location
    /// 
    /// CALLED FROM:
    /// - OnCollisionEnter(): When projectile hits and DestroyedOnHit is true
    /// - Update(): When m_TimeSinceLaunch >= TimeToDestroyed (timeout)
    /// 
    /// PERFORMANCE:
    /// This method contains several expensive operations:
    /// - Physics.OverlapSphere: Checks collision within radius
    /// - GetComponent: Searches for Target component on each collider
    /// - PoolSystem.GetInstance: Retrieves effect from pool
    /// However, only called once per projectile lifetime (not per frame)
    /// </summary>
    void Destroy()
    {
        // ====================================================================
        // STORE EXPLOSION POSITION
        // ====================================================================

        // Capture projectile's current world position before deactivation
        // 
        // WHY STORE THIS?
        // After gameObject.SetActive(false), transform.position becomes undefined
        // Need position for:
        // - Spawning explosion effect at impact point
        // - Calculating damage radius from explosion center
        // - Playing audio at explosion location
        // 
        // TRANSFORM.POSITION:
        // World-space coordinates (global position in scene)
        // Vector3 with X, Y, Z components in Unity units (meters)
        // Example: (10.5, 2.3, -5.8) means 10.5m right, 2.3m up, 5.8m back
        // 
        // LOCAL VARIABLE:
        // 'position' is stack-allocated (very fast)
        // Only exists during this method execution
        // Automatically cleaned up when method ends
        Vector3 position = transform.position;

        // ====================================================================
        // SPAWN EXPLOSION VISUAL EFFECT
        // ====================================================================

        // Retrieve explosion effect from object pool
        // 
        // GETINSTANCE:
        // Generic method that returns pooled GameObject
        // <GameObject> specifies return type (type-safe)
        // PrefabOnDestruction is the explosion effect prefab
        // 
        // POOLING BENEFIT:
        // Instead of: Instantiate(PrefabOnDestruction)
        // Uses pool: Reuses existing effect instance
        // No allocation, no garbage collection, much faster
        // 
        // EFFECT TYPES:
        // Common explosion effects:
        // - ParticleSystem with fire, smoke, debris
        // - Multiple particle systems (fireball + shockwave + sparks)
        // - Animated sprite explosion
        // - Light component for flash
        // 
        // POOL INITIALIZED:
        // Awake() called: PoolSystem.Instance.InitPool(PrefabOnDestruction, 4)
        // So pool already contains 4 pre-created effect instances
        // GetInstance reuses one of those instances
        var effect = PoolSystem.Instance.GetInstance<GameObject>(PrefabOnDestruction);

        // Position effect at explosion location
        // Effect appears exactly where projectile hit
        // transform is the effect's Transform component
        // position is the Vector3 we stored earlier
        effect.transform.position = position;

        // Activate the effect GameObject
        // Makes effect visible and starts particle systems playing
        // 
        // SETACTIVE(TRUE):
        // - Enables all components (ParticleSystems start playing)
        // - Triggers OnEnable() callbacks
        // - Makes effect visible in scene
        // 
        // EFFECT LIFECYCLE:
        // 1. Retrieved from pool (may be inactive)
        // 2. Position set to explosion point
        // 3. SetActive(true) - effect plays
        // 4. ParticleSystem finishes (1-3 seconds typical)
        // 5. Effect should auto-deactivate (Stop Action: Disable)
        // 6. Inactive effect waits in pool for reuse
        // 
        // AUTO-DEACTIVATION:
        // ParticleSystem should be configured in Inspector:
        // - Stop Action: Disable
        // - Or use script to disable after duration
        // Without auto-deactivation, effect stays visible forever (bug)
        effect.SetActive(true);

        // ====================================================================
        // DETECT TARGETS IN EXPLOSION RADIUS
        // ====================================================================

        // Find all colliders within explosion radius using sphere cast
        // 
        // PHYSICS.OVERLAPSPHERENONALLOC:
        // Performs sphere overlap check and fills provided array
        // More efficient than OverlapSphere (no array allocation)
        // 
        // PARAMETERS:
        // - position: Center of explosion sphere (where projectile hit)
        // - ReachRadius: Radius of explosion (5.0f default)
        // - s_SphereCastPool: Static array to store results (size 32)
        // - 1<<10: Layer mask filter (only check specific layer)
        // 
        // SPHERE OVERLAP:
        // Imagine invisible sphere at explosion point
        // Checks which colliders intersect with this sphere
        // All targets within radius are detected
        // 
        // LAYER MASK: 1<<10
        // Binary left shift operator
        // 1<<10 = 1 shifted left 10 bits = 0000010000000000 (binary)
        // Equals 1024 in decimal
        // Corresponds to layer 10 (Target layer)
        // 
        // WHY LAYER MASK?
        // Performance: Don't check irrelevant objects
        // Without mask: Checks walls, floor, props (unnecessary)
        // With mask: Only checks Target layer (enemies, destructibles)
        // 
        // LAYER 10:
        // Assumes targets are on layer 10
        // Should match Target.Awake() setting layer to "Target"
        // If targets on different layer, this won't detect them (bug)
        // 
        // RETURN VALUE:
        // 'count' is number of colliders found
        // Maximum is 32 (size of s_SphereCastPool array)
        // If more than 32 targets in radius, only first 32 are processed
        // 
        // NONALLOC PERFORMANCE:
        // OverlapSphere: Creates new array each call (garbage)
        // OverlapSphereNonAlloc: Reuses provided array (zero garbage)
        // Static array shared across all projectiles (optimal)
        int count = Physics.OverlapSphereNonAlloc(position, ReachRadius, s_SphereCastPool, 1 << 10);

        // ====================================================================
        // APPLY DAMAGE TO ALL TARGETS
        // ====================================================================

        // Loop through all detected colliders and damage their targets
        // 
        // FOR LOOP:
        // i = 0: First collider in array
        // i < count: Loop until we've processed all detected colliders
        // ++i: Increment i after each iteration (pre-increment for efficiency)
        // 
        // COUNT:
        // Number of colliders actually found (may be less than 32)
        // Example: If 3 targets in radius, count = 3, loop runs 3 times
        // Empty array positions beyond count contain null or old data (ignored)
        for (int i = 0; i < count; ++i)
        {
            // ================================================================
            // GET TARGET COMPONENT
            // ================================================================

            // Retrieve Target component from the collider's GameObject
            // 
            // S_SPHERECASTPOOL[i]:
            // Access collider at index i in the static array
            // This is one of the colliders found by OverlapSphereNonAlloc
            // 
            // GETCOMPONENT<TARGET>:
            // Searches the GameObject for a Target component
            // Returns reference if found, null if not found
            // 
            // TARGET:
            // Script component that represents damageable objects
            // Has health, death effects, score values
            // Has Got(damage) method to apply damage
            // 
            // NULL POSSIBILITY:
            // If collider's GameObject doesn't have Target component:
            // - GetComponent returns null
            // - Next line (t.Got) throws NullReferenceException
            // - Game crashes or logs error
            // 
            // BUG POTENTIAL:
            // This code assumes all colliders on layer 10 have Target component
            // If any layer 10 object lacks Target, this crashes
            // Better: Add null check: if (t != null) { t.Got(damage); }
            Target t = s_SphereCastPool[i].GetComponent<Target>();

            // ================================================================
            // APPLY DAMAGE
            // ================================================================

            // Call Target's damage method to reduce its health
            // 
            // T.GOT(DAMAGE):
            // Target's public method for receiving damage
            // - Subtracts damage from current health
            // - Plays hit sound/effects
            // - If health <= 0: Destroys target, plays death effect
            // - Awards points to player
            // 
            // DAMAGE:
            // Public float field (default 10.0f)
            // All targets in radius take same damage (flat damage model)
            // No falloff based on distance in this implementation
            // 
            // EXAMPLE:
            // Explosion at (0, 0, 0) with ReachRadius 5.0, damage 10.0
            // Target A at (1, 0, 0) - distance 1: Takes 10 damage
            // Target B at (4, 0, 0) - distance 4: Takes 10 damage  
            // Target C at (6, 0, 0) - distance 6: Takes 0 damage (outside radius)
            // 
            // DAMAGE FALLOFF (not implemented):
            // Could calculate distance and reduce damage:
            // float distance = Vector3.Distance(position, t.transform.position);
            // float falloff = 1.0f - (distance / ReachRadius);
            // t.Got(damage * falloff);
            // Would reward precise hits (more damage at center)
            t.Got(damage);

            // Loop continues to next collider
            // All targets in radius receive damage
        }

        // ====================================================================
        // DEACTIVATE PROJECTILE
        // ====================================================================

        // Disable projectile GameObject
        // 
        // SETACTIVE(FALSE):
        // - Hides projectile (invisible)
        // - Disables all components (no Update calls)
        // - Disables renderer (not drawn)
        // - Disables collider (no more collisions)
        // - Disables Rigidbody simulation (physics stops)
        // - Triggers OnDisable() callbacks
        // 
        // POOLING:
        // Projectile enters dormant state, ready for pool reuse
        // Still exists in memory (not destroyed)
        // Much faster than Destroy(gameObject) and Instantiate
        // 
        // TIMING:
        // Deactivated AFTER explosion effects are spawned
        // If deactivated first, couldn't access transform.position
        gameObject.SetActive(false);

        // ====================================================================
        // RESET PHYSICS VELOCITIES
        // ====================================================================

        // Zero out linear velocity (movement speed and direction)
        // 
        // LINEARVELOCITY:
        // Modern Unity property (replaces old 'velocity')
        // Vector3 representing speed in meters/second
        // Example: (10, 0, 0) = moving 10 m/s to the right
        // 
        // VECTOR3.ZERO:
        // Static constant: (0, 0, 0)
        // Represents no movement in any direction
        // 
        // WHY RESET?
        // When projectile is reused from pool:
        // - Old velocity would make it start moving immediately
        // - Would fly off before Launch() is called
        // - Resetting ensures projectile starts stationary
        // 
        // EXAMPLE:
        // First flight: linearVelocity = (20, 5, 0) when explodes
        // Reset to: (0, 0, 0)
        // Second flight: Launch() applies new force to stationary projectile
        // Clean start without momentum from previous flight
        m_Rigidbody.linearVelocity = Vector3.zero;

        // Zero out angular velocity (rotation speed)
        // 
        // ANGULARVELOCITY:
        // Vector3 representing rotation speed in radians/second
        // Each component is rotation around that axis
        // Example: (0, 3.14, 0) = spinning around Y-axis at π rad/s
        // 
        // WHY RESET?
        // Projectile might be spinning from previous flight
        // Collision impacts can cause rotation
        // Resetting ensures next launch starts without spinning
        // 
        // VISUAL IMPACT:
        // Without reset: Projectile might launch already spinning
        // With reset: Clean launch, rotation only from new physics
        m_Rigidbody.angularVelocity = Vector3.zero;

        // ====================================================================
        // RETURN TO WEAPON POOL
        // ====================================================================

        // Notify owner weapon that projectile is available for reuse
        // 
        // M_OWNER:
        // Reference to Weapon that launched this projectile
        // Set in Launch() when projectile was fired
        // 
        // RETURNPROJECTILE(THIS):
        // Weapon method that adds projectile back to its available pool
        // 'this' keyword passes reference to current Projectile instance
        // 
        // WEAPON'S POOL MANAGEMENT:
        // Each weapon maintains list of available projectiles
        // When fired: Remove from available list
        // When destroyed: Add back to available list
        // Next fire: Reuse from available list
        // 
        // WHY WEAPON-SPECIFIC?
        // Different weapons have different projectile types
        // Rocket launcher pool contains rockets
        // Grenade launcher pool contains grenades
        // Cannot mix types in same pool
        // 
        // POOLING CYCLE:
        // 1. Weapon.Fire() gets projectile from available pool
        // 2. Weapon.Fire() calls projectile.Launch()
        // 3. Projectile flies and explodes
        // 4. Projectile.Destroy() calls m_Owner.ReturnProjecticle(this)
        // 5. Weapon adds projectile back to available pool
        // 6. Repeat - same instance reused many times
        m_Owner.ReturnProjecticle(this);

        // ====================================================================
        // PLAY EXPLOSION SOUND
        // ====================================================================

        // Get temporary AudioSource from world audio pool
        // 
        // WORLDAUDIOPOOL:
        // System for playing positioned 3D sounds
        // Provides temporary AudioSource components
        // Similar to PoolSystem but specialized for audio
        // 
        // GETWORLDSFXSOURCE():
        // Returns available AudioSource from pool
        // AudioSource is pre-configured for 3D spatial audio
        // 
        // WHY POOLED AUDIO?
        // Projectile is deactivated (SetActive false above)
        // If audio was on projectile, it would stop immediately
        // Pooled source continues playing after projectile deactivates
        // Sound completes naturally, then source returns to pool
        var source = WorldAudioPool.GetWorldSFXSource();

        // Position audio source at explosion location
        // 3D spatial audio requires source position in world
        // 
        // 3D AUDIO EFFECTS:
        // - Volume: Louder when close, quieter when far
        // - Panning: Left/right speakers based on direction
        // - Doppler: Pitch shift if listener moving relative to source
        // - Rolloff: How quickly volume decreases with distance
        // 
        // POSITION:
        // Captured earlier before projectile deactivation
        // Ensures audio plays at impact point, not (0,0,0)
        source.transform.position = position;

        // Randomize audio pitch for variety
        // 
        // PITCH:
        // Audio playback speed multiplier
        // 1.0 = normal speed/pitch
        // 0.8 = 80% speed = lower pitch
        // 1.1 = 110% speed = higher pitch
        // 
        // RANDOM.RANGE(0.8F, 1.1F):
        // Returns random float between 0.8 and 1.1
        // Creates ±10% pitch variation
        // 
        // WHY RANDOMIZE?
        // Multiple explosions with same sound gets repetitive
        // Slight pitch variation adds realism and variety
        // Brain perceives as different sound, more interesting
        // 
        // EXAMPLE:
        // Explosion 1: pitch 0.85 - slightly lower/deeper
        // Explosion 2: pitch 1.05 - slightly higher/sharper
        // Explosion 3: pitch 0.92 - near normal
        // Each sounds slightly different, less monotonous
        source.pitch = Random.Range(0.8f, 1.1f);

        // Play explosion sound clip once
        // 
        // PLAYONESHOT:
        // Plays audio clip once from beginning
        // Doesn't interrupt other sounds on same source
        // Automatically stops when clip finishes
        // 
        // DESTROYEDSOUND:
        // AudioClip assigned in Inspector
        // Contains explosion sound effect
        // Typical: WAV or MP3 file of explosion, boom, blast
        // 
        // AUDIO SOURCE LIFECYCLE:
        // 1. Get source from WorldAudioPool
        // 2. Position source at explosion
        // 3. Configure pitch for variety
        // 4. Play sound clip (1-3 seconds typical)
        // 5. Sound finishes playing
        // 6. WorldAudioPool automatically returns source to pool
        // 7. Source reused for next explosion sound
        // 
        // SPATIALIZATION:
        // 3D AudioSource settings (set in WorldAudioPool):
        // - Spatial Blend: 1.0 (fully 3D)
        // - Volume Rolloff: Logarithmic
        // - Min Distance: Hearing range start
        // - Max Distance: Hearing range end
        // Player hears explosion from correct direction and distance
        source.PlayOneShot(DestroyedSound);
    }

    // ========================================================================
    // UPDATE - Unity lifecycle method called every frame
    // ========================================================================

    /// <summary>
    /// Unity lifecycle method called once per frame
    /// 
    /// EXECUTION FREQUENCY:
    /// - 60 FPS: Called 60 times per second (~0.016s interval)
    /// - 30 FPS: Called 30 times per second (~0.033s interval)
    /// - Variable: Actual frequency depends on performance
    /// 
    /// PURPOSE:
    /// Tracks projectile lifetime and triggers timeout explosion
    /// 
    /// FRAME-RATE INDEPENDENCE:
    /// Time.deltaTime adjusts for variable frame rates
    /// Timer increments by real time, not frame count
    /// 
    /// PERFORMANCE:
    /// Very lightweight - one addition, one comparison per frame
    /// Minimal CPU impact even with many active projectiles
    /// 
    /// ALTERNATIVE APPROACHES:
    /// - Coroutine: yield return new WaitForSeconds(TimeToDestroyed)
    /// - Invoke: Invoke("Destroy", TimeToDestroyed)
    /// - Timer system: Centralized timer manager
    /// This approach is simple and sufficient for this use case
    /// </summary>
    void Update()
    {
        // ====================================================================
        // INCREMENT LIFETIME TIMER
        // ====================================================================

        // Add elapsed frame time to lifetime counter
        // 
        // TIME.DELTATIME:
        // Time in seconds since last frame
        // Variable based on frame rate and performance
        // 
        // EXAMPLES:
        // - 60 FPS: deltaTime ≈ 0.0167 seconds (1/60)
        // - 30 FPS: deltaTime ≈ 0.0333 seconds (1/30)
        // - 120 FPS: deltaTime ≈ 0.0083 seconds (1/120)
        // 
        // FRAME-RATE INDEPENDENCE:
        // Fast PC (120 FPS): Many small deltaTime increments
        // Slow PC (30 FPS): Fewer large deltaTime increments
        // Result: Timer reaches 4.0 seconds in same real-world time
        // 
        // ACCUMULATION:
        // Frame 1: m_TimeSinceLaunch = 0.000 + 0.016 = 0.016
        // Frame 2: m_TimeSinceLaunch = 0.016 + 0.016 = 0.032
        // Frame 3: m_TimeSinceLaunch = 0.032 + 0.017 = 0.049
        // ... (continues each frame)
        // Frame 240: m_TimeSinceLaunch ≈ 4.0 seconds
        // 
        // += OPERATOR:
        // Compound assignment: m_TimeSinceLaunch = m_TimeSinceLaunch + Time.deltaTime
        // Adds deltaTime to existing value and stores result
        m_TimeSinceLaunch += Time.deltaTime;

        // ====================================================================
        // CHECK FOR TIMEOUT
        // ====================================================================

        // Check if projectile has existed longer than maximum lifetime
        // 
        // COMPARISON:
        // >= means "greater than or equal to"
        // True when m_TimeSinceLaunch reaches or exceeds TimeToDestroyed
        // 
        // TIMETODESTROYED:
        // Public field (default 4.0f seconds)
        // Maximum projectile lifetime before forced explosion
        // 
        // CONDITION MET:
        // Example: m_TimeSinceLaunch = 4.001, TimeToDestroyed = 4.0
        // 4.001 >= 4.0 evaluates to TRUE
        // Triggers Destroy() to explode projectile
        // 
        // WHY NEEDED?
        // Prevents projectiles from flying forever
        // Catches projectiles that:
        // - Miss all targets
        // - Fly off map into skybox
        // - Glitch through collision geometry
        // - Travel too far from play area
        // 
        // MEMORY MANAGEMENT:
        // Without timeout: Projectiles accumulate infinitely
        // With timeout: Projectiles self-clean after max lifetime
        // Keeps active projectile count bounded
        // 
        // GAMEPLAY:
        // For rockets (DestroyedOnHit=true):
        // - Usually hit something before timeout
        // - Timeout is backup safety measure
        // 
        // For grenades (DestroyedOnHit=false):
        // - Timeout IS the explosion trigger
        // - Grenades bounce/roll until this timer expires
        // - Timer acts as grenade fuse
        if (m_TimeSinceLaunch >= TimeToDestroyed)
        {
            // Trigger explosion sequence
            // Same as collision-triggered explosion
            // Deals area damage, plays effects, returns to pool
            Destroy();
        }

        // If condition false, method ends, Update() runs again next frame
        // Timer continues incrementing until timeout or collision
    }

    // ========================================================================
    // ONDRAWGIZMOSSELECTED - Unity editor visualization
    // ========================================================================

    /// <summary>
    /// Unity editor method for drawing debug visualization
    /// Only runs in Unity Editor, not in builds
    /// 
    /// EXECUTION:
    /// Called when this GameObject is selected in hierarchy/scene view
    /// Runs continuously while selected (multiple times per second)
    /// Only visible in Scene view, not Game view
    /// 
    /// PURPOSE:
    /// Visualizes explosion radius during level design and testing
    /// Helps designers understand projectile damage area
    /// 
    /// GIZMOS:
    /// Debug drawing API for Unity Editor visualization
    /// Draws shapes, lines, icons in Scene view
    /// Used for debugging and level design tools
    /// 
    /// GIZMOS VS DRAWGIZMOS:
    /// - OnDrawGizmos(): Always draws, even when not selected
    /// - OnDrawGizmosSelected(): Only draws when selected
    /// This uses Selected to reduce visual clutter
    /// 
    /// NOT IN BUILD:
    /// Gizmo code is stripped from builds automatically
    /// Zero performance cost in shipped game
    /// Only exists in editor for development
    /// 
    /// COLOR:
    /// Gizmos.color can be set before drawing
    /// Default is yellow (good visibility on most backgrounds)
    /// Could set: Gizmos.color = Color.red;
    /// </summary>
    void OnDrawGizmosSelected()
    {
        // ====================================================================
        // DRAW EXPLOSION RADIUS SPHERE
        // ====================================================================

        // Draw wireframe sphere representing damage radius
        // 
        // GIZMOS.DRAWWIRESPHERE:
        // Draws hollow sphere outline (not solid)
        // Wireframe is easier to see through
        // Doesn't obstruct view of scene objects
        // 
        // PARAMETERS:
        // - transform.position: Center of sphere (projectile location)
        // - ReachRadius: Radius in Unity units (default 5.0)
        // 
        // VISUALIZATION:
        // Yellow wireframe sphere around projectile
        // Size matches actual damage radius
        // Helps designers:
        // - See how far explosion reaches
        // - Test if damage radius is balanced
        // - Visualize coverage area in level
        // - Compare different projectile radii
        // 
        // EXAMPLE USES:
        // Level designer places projectile in scene
        // Selects it in hierarchy
        // Sees yellow sphere showing damage area
        // Adjusts ReachRadius to balance for room size
        // Larger rooms = can use larger radius safely
        // Smaller rooms = need smaller radius to avoid wall-through damage
        // 
        // REAL-TIME UPDATE:
        // As ReachRadius changes in Inspector
        // Sphere size updates immediately
        // Provides instant visual feedback
        // 
        // COLOR:
        // Default yellow from Gizmos (no color set)
        // Good contrast on most backgrounds
        // If needed, could set: Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, ReachRadius);
    }
}

// ============================================================================
// END OF PROJECTILE CLASS
// ============================================================================
//
// SUMMARY: Projectile is a physics-based explosive projectile system
//
// KEY RESPONSIBILITIES:
// - Physics-based flight using Rigidbody and forces
// - Collision detection via OnCollisionEnter
// - Area-of-effect damage using sphere overlap
// - Visual/audio effects via pooling systems
// - Automatic cleanup via timeout or impact
// - Pool integration for performance optimization
//
// DESIGN PATTERNS:
// - Component: Attached to projectile GameObject prefab
// - Object Pool: Reused instances instead of create/destroy
// - Physics Simulation: Rigidbody for realistic trajectory
// - Area Damage: Sphere overlap for splash damage
// - Factory: Launch() configures and activates pooled instances
//
// PERFORMANCE OPTIMIZATIONS:
// - Static collision array (s_SphereCastPool) - zero allocation
// - OverlapSphereNonAlloc instead of OverlapSphere - no GC
// - Cached Rigidbody reference - fast repeated access
// - Pooled explosion effects - no Instantiate during gameplay
// - Pooled audio sources - no AudioSource creation
// - SetActive instead of Destroy - instant deactivation
//
// PHYSICS CONFIGURATION (SET IN INSPECTOR):
// Rigidbody:
// - Mass: 1.0 (affects force needed to accelerate)
// - Drag: 0.0 (no air resistance for fast projectiles)
// - Angular Drag: 0.05 (some rotation damping)
// - Use Gravity: TRUE (realistic arc trajectory)
// - Is Kinematic: FALSE (needs physics simulation)
// - Interpolate: Interpolate (smooth movement)
// - Collision Detection: Continuous (prevent fast projectile tunneling)
//
// Collider:
// - Is Trigger: FALSE (physical collision, not trigger)
// - Size: Match projectile visual size
//
// DAMAGE MODEL:
// Current: FLAT damage (all targets in radius take full damage)
// No distance falloff implemented
// 
// Possible extensions:
// - Linear falloff: damage * (1 - distance/radius)
// - Quadratic falloff: damage * (1 - distance²/radius²)
// - Line-of-sight: Raycast to check wall blocking
// - Damage types: Different damage vs different target types
// - Knockback: Apply force to damaged objects
// - Stun/status effects: Apply debuffs to targets
//
// FPS KIT INTEGRATION:
// - Weapon: Launches projectiles via Launch(), maintains pool
// - Target: Takes damage from explosions via Got() method
// - PoolSystem: Manages projectile and effect reuse
// - WorldAudioPool: Plays positioned explosion sounds
// - GameSystem: Could track projectile stats (fired, hit rate)
//
// COMMON ISSUES AND FIXES:
// 
// Projectiles pass through walls:
// - Solution: Set Rigidbody Collision Detection to Continuous
// - Solution: Ensure walls have colliders
// - Solution: Check layer collision matrix
//
// Projectiles don't explode:
// - Check: DestroyedOnHit is true (for impact explosion)
// - Check: TimeToDestroyed > 0 (for timeout explosion)
// - Check: OnCollisionEnter is triggered (requires proper colliders)
//
// Explosions miss targets:
// - Check: Targets are on layer 10
// - Check: ReachRadius is large enough
// - Check: Targets have colliders
// - Add debug: Draw sphere in Scene view to visualize radius
//
// No sound/effects:
// - Check: DestroyedSound assigned in Inspector
// - Check: PrefabOnDestruction assigned in Inspector
// - Check: PoolSystem.Instance exists and initialized
// - Check: WorldAudioPool exists and initialized
//
// EXTENSION IDEAS:
// - Homing projectiles: Rotate toward nearest target in Update()
// - Bouncing projectiles: Use Physics Material with bounciness
// - Sticky projectiles: Disable kinematic on collision, parent to hit object
// - Trail renderer: Add TrailRenderer component for rocket trail
// - Guidance system: Laser-guided with raycast steering
// - Cluster munitions: Spawn child projectiles on explosion
// - EMP effect: Disable enemy abilities in radius
// - Damage over time: Fire that burns targets for X seconds
// - Shield penetration: Ignore certain colliders/layers
// - Velocity-based damage: damage * velocity.magnitude for ramming
//
// ============================================================================