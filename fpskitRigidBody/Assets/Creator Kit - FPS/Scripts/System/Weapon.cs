// ============================================================================
// USING STATEMENTS - Import necessary libraries
// ============================================================================

using System.Collections;          // Provides interfaces for collections
using System.Collections.Generic;  // Provides generic collection types like List<T>, Queue<T>
using System.Linq;                 // Provides LINQ query methods (not heavily used in this script)
using UnityEngine;                 // Core Unity engine functionality
using UnityEngine.Serialization;   // Helps maintain references when renaming serialized fields
using UnityEngine.UI;              // Unity UI system (not heavily used in this script)

// Conditional compilation for Unity Editor-specific code
#if UNITY_EDITOR
using UnityEditor;                 // Unity Editor API for custom inspectors and property drawers
#endif

// ============================================================================
// WEAPON CLASS - Complete weapon system for FPS gameplay
// ============================================================================
// This is one of the most complex and important scripts in the FPS Kit
// It handles ALL aspects of weapon behavior:
// - Firing mechanics (raycast hitscan vs physical projectiles)
// - Ammunition management (clip/magazine system + reserve ammo)
// - Reloading system with animations
// - Audio feedback (firing, reloading, impact sounds)
// - Visual effects (muzzle flash, bullet trails, impact particles)
// - Animation coordination (firing, reloading, idle states)
// - Object pooling for performance (projectiles, bullet trails)
// - Screen shake for impact feel
// - Weapon state management (Idle, Firing, Reloading)
//
// ARCHITECTURE:
// Weapon is attached to weapon prefabs (pistol, rifle, shotgun, etc.)
// Controller owns and manages multiple Weapon instances
// Weapon communicates with many systems:
// - Controller: Ammo inventory, player state
// - CameraShaker: Screen shake on fire
// - ImpactManager: Bullet hit effects
// - WeaponInfoUI: HUD display updates
// - PoolSystem: Object pooling for performance
// - Target: Damage application
// ============================================================================

public class Weapon : MonoBehaviour
{
    // ========================================================================
    // STATIC RAYCAST BUFFER - Shared memory optimization
    // ========================================================================

    /// <summary>
    /// Static array shared by all Weapon instances to store raycast hit results
    /// 
    /// WHY STATIC:
    /// - Static = one copy shared by ALL weapons (not one per weapon)
    /// - Saves memory - 100 weapons don't create 100 arrays
    /// - Only one weapon fires at a time, so sharing is safe
    /// 
    /// WHY ARRAY BUFFER:
    /// - Array is pre-allocated once and reused forever
    /// - Avoids garbage collection (GC) spikes from temporary allocations
    /// - Unity's RaycastNonAlloc methods fill this buffer instead of creating new arrays
    /// - Size 8 = can detect up to 8 objects hit by one raycast
    /// 
    /// PERFORMANCE BENEFIT:
    /// Without buffer: Every shot creates temporary array → GC cleanup needed → frame drops
    /// With buffer: Reuse same array forever → no GC → smooth frame rate
    /// 
    /// NOTE: Currently declared but not actively used in this script
    /// Likely reserved for future multi-hit raycast implementation
    /// </summary>
    static RaycastHit[] s_HitInfoBuffer = new RaycastHit[8];

    // ========================================================================
    // ENUMS - Define weapon behavior types
    // ========================================================================
    // Enums are like multiple-choice options for weapon configuration
    // Instead of magic numbers (0, 1, 2), we use descriptive names
    // ========================================================================

    /// <summary>
    /// Defines how the weapon fires when trigger is held
    /// </summary>
    public enum TriggerType
    {
        /// <summary>
        /// Automatic - fires continuously while trigger held (machine gun, rifle)
        /// Example: Hold mouse button → bullets keep firing rapidly
        /// </summary>
        Auto,

        /// <summary>
        /// Manual/Semi-automatic - one shot per trigger pull (pistol, shotgun)
        /// Example: Hold mouse button → fires once, must release and press again
        /// </summary>
        Manual
    }

    /// <summary>
    /// Defines how the weapon's bullets/projectiles work
    /// </summary>
    public enum WeaponType
    {
        /// <summary>
        /// Raycast/Hitscan - instant hit detection using raycasts (bullets, lasers)
        /// No travel time - ray immediately checks what's in the line of fire
        /// Pros: Instant feedback, predictable, performance-friendly
        /// Used for: Pistols, rifles, sniper rifles
        /// </summary>
        Raycast,

        /// <summary>
        /// Projectile - physical objects that travel through space (grenades, rockets)
        /// Have travel time, affected by gravity, can be dodged
        /// Pros: More realistic for thrown/launched weapons, visible trajectory
        /// Used for: Grenades, rocket launchers, grenade launchers
        /// </summary>
        Projectile
    }

    /// <summary>
    /// Tracks what the weapon is currently doing
    /// Used to prevent invalid actions (can't reload while firing, etc.)
    /// </summary>
    public enum WeaponState
    {
        /// <summary>
        /// Idle - ready to fire or reload
        /// Can transition to: Firing (if trigger pulled) or Reloading (if reload key pressed)
        /// </summary>
        Idle,

        /// <summary>
        /// Firing - currently playing firing animation and spawning bullet/projectile
        /// Cannot fire again or reload until animation completes
        /// Automatically returns to Idle when firing animation finishes
        /// </summary>
        Firing,

        /// <summary>
        /// Reloading - playing reload animation and refilling clip from reserve ammo
        /// Cannot fire or reload again until animation completes
        /// Automatically returns to Idle when reload animation finishes
        /// </summary>
        Reloading
    }

    // ========================================================================
    // ADVANCEDSETTINGS NESTED CLASS - Optional weapon customization
    // ========================================================================

    /// <summary>
    /// Contains advanced weapon tuning parameters
    /// [System.Serializable] makes this class appear in Unity Inspector
    /// Nested inside Weapon class to organize related settings together
    /// </summary>
    [System.Serializable]
    public class AdvancedSettings
    {
        /// <summary>
        /// Spread angle in degrees - how inaccurate the weapon is
        /// 0 = perfectly accurate (laser)
        /// Small values (1-5) = accurate (rifle)
        /// Large values (10-30) = inaccurate (shotgun)
        /// 
        /// IMPLEMENTATION:
        /// Random offset is applied within a cone of this angle
        /// Larger angle = bullets can deviate more from center
        /// Simulates weapon shake, recoil inaccuracy
        /// </summary>
        public float spreadAngle = 0.0f;

        /// <summary>
        /// Number of bullets/projectiles spawned per shot
        /// 1 = single bullet (pistol, rifle)
        /// Multiple = shotgun pellets, burst fire
        /// 
        /// EXAMPLES:
        /// - Pistol: 1 projectile per shot
        /// - Shotgun: 8 projectiles per shot (8 pellets spread out)
        /// - Burst rifle: 3 projectiles per shot (3-round burst)
        /// 
        /// PERFORMANCE NOTE:
        /// Each projectile is a separate raycast or physics object
        /// Higher values = more CPU/physics work per shot
        /// </summary>
        public int projectilePerShot = 1;

        /// <summary>
        /// Multiplier for camera screen shake intensity
        /// 1.0 = normal shake
        /// >1.0 = more intense shake (powerful weapons)
        /// <1.0 = less shake (lighter weapons)
        /// 0.0 = no shake
        /// 
        /// USAGE:
        /// Applied in Fire() method: CameraShaker.Shake(time, strength * screenShakeMultiplier)
        /// Designers can tune "feel" of each weapon independently
        /// </summary>
        public float screenShakeMultiplier = 1.0f;
    }

    // ========================================================================
    // PUBLIC INSPECTOR FIELDS - Weapon Configuration
    // ========================================================================
    // These appear in Unity Inspector for designers to configure each weapon
    // ========================================================================

    /// <summary>
    /// How the weapon fires when trigger is held
    /// Manual = one shot per click (pistol)
    /// Auto = continuous fire (machine gun)
    /// </summary>
    public TriggerType triggerType = TriggerType.Manual;

    /// <summary>
    /// Type of weapon implementation
    /// Raycast = instant hitscan (bullets, lasers)
    /// Projectile = physical objects (grenades, rockets)
    /// </summary>
    public WeaponType weaponType = WeaponType.Raycast;

    /// <summary>
    /// Time in seconds between shots (rate of fire)
    /// 0.1 = very fast (10 shots per second)
    /// 0.5 = moderate (2 shots per second)
    /// 1.0 = slow (1 shot per second)
    /// 
    /// FIRE RATE FORMULA:
    /// Shots per second = 1.0 / fireRate
    /// Examples:
    /// - fireRate 0.1 = 10 shots/sec (600 rounds/min)
    /// - fireRate 0.5 = 2 shots/sec (120 rounds/min)
    /// - fireRate 1.0 = 1 shot/sec (60 rounds/min)
    /// </summary>
    public float fireRate = 0.5f;

    /// <summary>
    /// Time in seconds to complete reload animation
    /// Affects gameplay pacing - longer reload = more vulnerability
    /// Should match the length of reload animation for synchronization
    /// 
    /// BALANCING:
    /// Fast weapons (pistols): 1.0-1.5 seconds
    /// Rifles: 2.0-2.5 seconds  
    /// Heavy weapons (LMG): 3.0-4.0 seconds
    /// </summary>
    public float reloadTime = 2.0f;

    /// <summary>
    /// Maximum ammunition capacity in the weapon's clip/magazine
    /// Once clip is empty, must reload from reserve ammo
    /// 
    /// EXAMPLES BY WEAPON TYPE:
    /// - Pistol: 12-15 rounds
    /// - Rifle: 30 rounds
    /// - Shotgun: 4-8 shells
    /// - SMG: 30-40 rounds
    /// - Sniper: 5-10 rounds
    /// 
    /// DESIGN CONSIDERATION:
    /// Smaller clips = more frequent reloading = more tension
    /// Larger clips = less reloading but potentially less engaging
    /// </summary>
    public int clipSize = 4;

    /// <summary>
    /// Damage dealt per hit/projectile
    /// Applied to Target.Got(damage) when hitting targets
    /// 
    /// BALANCING EXAMPLES:
    /// - Pistol: 10-20 damage (multiple shots to kill)
    /// - Rifle: 25-35 damage (fewer shots to kill)
    /// - Shotgun pellet: 10 damage x 8 pellets = 80 total at close range
    /// - Sniper: 100+ damage (one-shot kills)
    /// - Grenade: 50+ damage with area effect
    /// </summary>
    public float damage = 1.0f;

    /// <summary>
    /// Type of ammunition this weapon uses (ID from GameDatabase)
    /// -1 = no ammo type assigned (infinite ammo or needs setup)
    /// 
    /// [AmmoType] attribute creates dropdown in Inspector showing ammo names
    /// Instead of typing IDs, designers select from list:
    /// "Pistol Ammo", "Rifle Ammo", "Shotgun Ammo", etc.
    /// 
    /// AMMO SHARING:
    /// Multiple weapons can use same ammo type
    /// Example: Pistol and SMG both use "Pistol Ammo" (ID 0)
    /// Player picks up "Pistol Ammo" box → both weapons can use it
    /// </summary>
    [AmmoType]
    public int ammoType = -1;

    /// <summary>
    /// Prefab for projectile-type weapons (grenades, rockets)
    /// Only used if weaponType == WeaponType.Projectile
    /// 
    /// REQUIREMENTS:
    /// - Must have Projectile script component
    /// - Must have Rigidbody for physics
    /// - Must have Collider for collision detection
    /// 
    /// Weapon creates a pool of these at startup for performance
    /// When fired, projectile is pulled from pool, launched, then returned to pool
    /// </summary>
    public Projectile projectilePrefab;

    /// <summary>
    /// Force applied to projectile when launched
    /// Higher values = faster projectile speed
    /// Only used for projectile-type weapons
    /// 
    /// PHYSICS NOTE:
    /// Force is applied via Rigidbody.AddForce(direction * projectileLaunchForce)
    /// Actual speed depends on projectile's Rigidbody mass:
    /// - Heavy projectiles (mass 2.0): slower for same force
    /// - Light projectiles (mass 0.5): faster for same force
    /// 
    /// TYPICAL VALUES:
    /// - Grenade (thrown): 200-400
    /// - Rocket (launched): 1000-2000
    /// - Slow projectile: 100-200
    /// </summary>
    public float projectileLaunchForce = 200.0f;

    /// <summary>
    /// Transform representing weapon's muzzle/barrel end point
    /// This is where bullets/projectiles spawn from
    /// Also where muzzle flash effects appear
    /// 
    /// SETUP:
    /// Designers place an empty GameObject at the gun barrel tip
    /// Drag that GameObject into this field in Inspector
    /// 
    /// USES:
    /// - Raycast origin (bullets start here)
    /// - Projectile spawn position
    /// - Muzzle flash particle system position
    /// - Bullet trail start position
    /// 
    /// IMPORTANT:
    /// Must be child of weapon so it moves/rotates with the weapon
    /// </summary>
    public Transform EndPoint;

    /// <summary>
    /// Instance of AdvancedSettings containing optional tuning parameters
    /// Appears as a foldout section in Inspector
    /// Contains spreadAngle, projectilePerShot, screenShakeMultiplier
    /// </summary>
    public AdvancedSettings advancedSettings;

    // ====================================================================
    // ANIMATION CONFIGURATION
    // ====================================================================
    // [Header] attribute creates a section header in Inspector for organization

    [Header("Animation Clips")]

    /// <summary>
    /// Animation clip that plays when weapon fires
    /// Should show muzzle flash, weapon recoil, ejecting shell casing, etc.
    /// 
    /// SYNCHRONIZATION:
    /// Animation length should approximately match fireRate
    /// If animation is 0.2 seconds and fireRate is 0.5:
    /// - Animation plays at 0.4x speed (0.2 / 0.5)
    /// This is calculated in Selected() method
    /// </summary>
    public AnimationClip FireAnimationClip;

    /// <summary>
    /// Animation clip that plays when weapon reloads
    /// Should show magazine ejection, new magazine insertion, bolt action, etc.
    /// 
    /// SYNCHRONIZATION:
    /// Animation length should match reloadTime exactly
    /// If animation is 2.0 seconds and reloadTime is 2.0:
    /// - Animation plays at 1.0x speed (perfect sync)
    /// Speed adjustment is calculated in Selected() method
    /// </summary>
    public AnimationClip ReloadAnimationClip;

    // ====================================================================
    // AUDIO CONFIGURATION
    // ====================================================================

    [Header("Audio Clips")]

    /// <summary>
    /// Sound effect played when weapon fires
    /// Examples: gunshot bang, laser zap, grenade launcher thump
    /// 
    /// AUDIO VARIATION:
    /// Pitch is randomized (0.7-1.0) in Fire() method
    /// Prevents repetitive sound fatigue
    /// Makes rapid fire sound more natural and less robotic
    /// </summary>
    public AudioClip FireAudioClip;

    /// <summary>
    /// Sound effect played when weapon reloads
    /// Examples: magazine click, shell insertion, bolt rack
    /// 
    /// AUDIO VARIATION:
    /// Pitch is randomized (0.7-1.0) in Reload() method
    /// Adds variety to the reload sound
    /// </summary>
    public AudioClip ReloadAudioClip;

    // ====================================================================
    // VISUAL EFFECTS CONFIGURATION
    // ====================================================================

    [Header("Visual Settings")]

    /// <summary>
    /// Prefab for bullet trail visual effect (raycast weapons only)
    /// Creates a line from muzzle to hit point to visualize bullet path
    /// 
    /// REQUIREMENTS:
    /// - Must be a LineRenderer component
    /// - Pooled for performance (created in Awake, reused many times)
    /// 
    /// HOW IT WORKS:
    /// 1. Weapon fires raycast
    /// 2. Get trail from pool
    /// 3. Set LineRenderer positions: [muzzle position, hit position]
    /// 4. Trail animates forward then fades
    /// 5. Return trail to pool for reuse
    /// 
    /// VISUAL EFFECT:
    /// Creates tracer/bullet streak effect common in FPS games
    /// Helps player see where they're shooting
    /// Makes weapons feel more impactful
    /// </summary>
    public LineRenderer PrefabRayTrail;

    /// <summary>
    /// If true, weapon GameObject deactivates when out of ammo
    /// Useful for limited-use weapons like grenades
    /// 
    /// BEHAVIOR:
    /// When both clip AND reserve ammo reach 0:
    /// - GameObject.SetActive(false) is called
    /// - Weapon disappears from player's hands
    /// - Player automatically switches to another weapon
    /// - Weapon reappears if player collects more ammo
    /// 
    /// USE CASES:
    /// - Grenades: Limited to pickup count, disable when out
    /// - Regular guns: Keep enabled (show empty weapon model)
    /// </summary>
    public bool DisabledOnEmpty;

    // ====================================================================
    // UI DISPLAY CONFIGURATION
    // ====================================================================

    [Header("Visual Display")]

    /// <summary>
    /// Optional custom ammo display component for this weapon
    /// Allows weapons to have unique ammo UI (e.g., grenade counter)
    /// 
    /// IMPLEMENTATION:
    /// AmmoDisplay is an abstract base class
    /// Concrete implementations show ammo in different ways:
    /// - Text display: "30/120"
    /// - Icon grid: Show individual bullets/grenades as icons
    /// - Progress bar: Visual bar showing ammo percentage
    /// 
    /// If null, weapon uses default WeaponInfoUI instead
    /// UpdateAmount(current, max) is called when clip changes
    /// </summary>
    public AmmoDisplay AmmoDisplay;

    // ========================================================================
    // PUBLIC PROPERTIES - Controlled access to internal state
    // ========================================================================

    /// <summary>
    /// Property for trigger state (mouse button down)
    /// Getter: Returns current m_TriggerDown value
    /// Setter: Updates m_TriggerDown and resets m_ShotDone when released
    /// 
    /// WHY A PROPERTY:
    /// Allows Controller to set trigger state: weapon.triggerDown = Input.GetMouseButton(0)
    /// Setter logic ensures m_ShotDone resets when trigger released
    /// This prevents multiple shots from single click on Manual weapons
    /// 
    /// CONTROL FLOW:
    /// 1. Controller sets triggerDown = true (mouse pressed)
    /// 2. Weapon fires once, sets m_ShotDone = true
    /// 3. Even though triggerDown stays true, won't fire again (already shot)
    /// 4. Controller sets triggerDown = false (mouse released)
    /// 5. Setter resets m_ShotDone = false
    /// 6. Now ready to fire again on next trigger press
    /// </summary>
    public bool triggerDown
    {
        // Getter: Return current trigger state
        get { return m_TriggerDown; }

        // Setter: Update trigger state and reset shot flag when released
        set
        {
            m_TriggerDown = value;

            // If trigger was released (value is false)
            // Reset m_ShotDone so next trigger pull can fire again
            // Critical for Manual trigger type to work correctly
            if (!m_TriggerDown) m_ShotDone = false;
        }
    }

    /// <summary>
    /// Read-only property exposing current weapon state
    /// Expression-bodied property (=>) - shorthand for simple getters
    /// External code can check: if (weapon.CurrentState == WeaponState.Idle)
    /// Cannot be set from outside - only Weapon controls its state
    /// </summary>
    public WeaponState CurrentState => m_CurrentState;

    /// <summary>
    /// Read-only property exposing current clip ammo count
    /// Used by UI to display "30/120" (30 in clip, 120 in reserve)
    /// WeaponInfoUI reads this to update HUD display
    /// </summary>
    public int ClipContent => m_ClipContent;

    /// <summary>
    /// Read-only property exposing the player controller that owns this weapon
    /// Allows Projectile scripts to reference player when needed
    /// Set once in PickedUp() method, never changes afterward
    /// </summary>
    public Controller Owner => m_Owner;

    // ========================================================================
    // PRIVATE FIELDS - Internal weapon state
    // ========================================================================
    // m_ prefix indicates private member variables
    // ========================================================================

    /// <summary>
    /// Reference to the Controller (player) that picked up this weapon
    /// Set in PickedUp() method when player acquires weapon
    /// Used to access player's ammo inventory via m_Owner.GetAmmo() and m_Owner.ChangeAmmo()
    /// </summary>
    Controller m_Owner;

    /// <summary>
    /// Reference to the Animator component that controls weapon animations
    /// Found in Awake() using GetComponentInChildren<Animator>()
    /// Used to trigger animations: m_Animator.SetTrigger("fire")
    /// Also tracks animation states to know when firing/reloading completes
    /// </summary>
    Animator m_Animator;

    /// <summary>
    /// Current state of the weapon (Idle, Firing, or Reloading)
    /// Updated in UpdateControllerState() based on animator state
    /// Controls what actions are allowed:
    /// - Idle: Can fire or reload
    /// - Firing: Cannot fire or reload (wait for animation)
    /// - Reloading: Cannot fire or reload (wait for animation)
    /// </summary>
    WeaponState m_CurrentState;

    /// <summary>
    /// Flag tracking if weapon has fired during current trigger press
    /// Only used for Manual trigger type (semi-automatic)
    /// 
    /// PURPOSE:
    /// Prevents firing multiple times from single mouse click
    /// Without this, weapon would fire every frame while button held
    /// 
    /// WORKFLOW:
    /// 1. Trigger pressed: m_ShotDone = false
    /// 2. Fire() is called: m_ShotDone = true
    /// 3. While still pressed: Fire() does nothing (m_ShotDone == true)
    /// 4. Trigger released: m_ShotDone = false (ready for next shot)
    /// </summary>
    bool m_ShotDone;

    /// <summary>
    /// Timer counting down after each shot (enforces fire rate)
    /// -1.0 or <= 0 = ready to fire
    /// > 0 = still cooling down, cannot fire yet
    /// 
    /// HOW IT WORKS:
    /// 1. Fire() is called: m_ShotTimer = fireRate (e.g., 0.5)
    /// 2. Each frame in Update(): m_ShotTimer -= Time.deltaTime
    /// 3. After 0.5 seconds: m_ShotTimer <= 0
    /// 4. Now ready to fire again
    /// 
    /// PREVENTS:
    /// Firing faster than fireRate allows
    /// Example: fireRate = 0.5 (max 2 shots/sec)
    /// Even if trigger pulled rapidly, shots are limited to every 0.5 seconds
    /// </summary>
    float m_ShotTimer = -1.0f;

    /// <summary>
    /// Current state of trigger (mouse button) - true when held down
    /// Set via triggerDown property from Controller
    /// Checked in UpdateControllerState() to determine if weapon should fire
    /// </summary>
    bool m_TriggerDown;

    /// <summary>
    /// Current number of bullets/rounds in the weapon's clip/magazine
    /// Initialized to clipSize in Awake()
    /// Decremented by 1 each shot in Fire()
    /// Replenished from reserve ammo in Reload()
    /// When reaches 0, weapon must reload before firing again
    /// </summary>
    int m_ClipContent;

    /// <summary>
    /// Reference to AudioSource component for playing weapon sounds
    /// Found in Awake() using GetComponentInChildren<AudioSource>()
    /// Used to play:
    /// - Fire sounds: m_Source.PlayOneShot(FireAudioClip)
    /// - Reload sounds: m_Source.PlayOneShot(ReloadAudioClip)
    /// Pitch is randomized for variety
    /// </summary>
    AudioSource m_Source;

    /// <summary>
    /// Cached corrected muzzle position in world space
    /// Used to account for different FOV between weapon camera and main camera
    /// Ensures visual effects spawn at correct position
    /// Updated when needed, not currently actively used in this version
    /// </summary>
    Vector3 m_ConvertedMuzzlePos;

    // ========================================================================
    // ACTIVE TRAIL TRACKING - Visual effect management
    // ========================================================================

    /// <summary>
    /// Nested class tracking a single active bullet trail effect
    /// Used to animate LineRenderer trails moving through space
    /// </summary>
    class ActiveTrail
    {
        /// <summary>
        /// The LineRenderer component displaying this trail
        /// Pulled from object pool when shot fires
        /// Returned to pool when trail expires
        /// </summary>
        public LineRenderer renderer;

        /// <summary>
        /// Direction the trail is moving (normalized vector)
        /// Used to animate trail forward each frame
        /// Makes trail appear to travel through space
        /// </summary>
        public Vector3 direction;

        /// <summary>
        /// Time in seconds until trail should disappear
        /// Starts at 0.3 seconds in RaycastShot()
        /// Decremented in Update()
        /// When reaches 0, trail is disabled and removed from list
        /// </summary>
        public float remainingTime;
    }

    /// <summary>
    /// List of all currently active bullet trail effects
    /// Trails are added in RaycastShot() when weapon fires
    /// Updated in Update() - moved forward and fade out
    /// Removed from list when remainingTime reaches 0
    /// 
    /// WHY A LIST:
    /// Weapon can fire multiple shots before first trail disappears
    /// Need to track and update all active trails simultaneously
    /// List allows dynamic adding/removing of trails
    /// </summary>
    List<ActiveTrail> m_ActiveTrails = new List<ActiveTrail>();

    // ========================================================================
    // PROJECTILE POOLING - Performance optimization
    // ========================================================================

    /// <summary>
    /// Queue storing reusable projectile instances for projectile weapons
    /// 
    /// OBJECT POOLING PATTERN:
    /// Instead of Instantiate new projectile every shot (expensive)
    /// And Destroy it after impact (expensive, causes garbage collection)
    /// We create a pool of projectiles once and reuse them:
    /// 1. Awake: Create pool of projectiles, all disabled
    /// 2. Fire: Dequeue projectile, enable it, launch it
    /// 3. Impact: Projectile disables itself, calls ReturnProjectile()
    /// 4. Return: Enqueue projectile back into pool for reuse
    /// 
    /// QUEUE VS LIST:
    /// Queue is FIFO (First In, First Out) - perfect for this use
    /// Enqueue adds to end, Dequeue removes from front
    /// Ensures fair distribution - all projectiles get used evenly
    /// 
    /// PERFORMANCE BENEFIT:
    /// No Instantiate/Destroy calls during gameplay
    /// No garbage collection spikes
    /// Smooth framerate even with rapid fire weapons
    /// </summary>
    Queue<Projectile> m_ProjectilePool = new Queue<Projectile>();

    // ========================================================================
    // ANIMATOR PARAMETER NAME HASHES - Performance optimization
    // ========================================================================

    /// <summary>
    /// Pre-calculated hash of "fire" animator parameter name
    /// Animator.StringToHash converts string to integer hash
    /// 
    /// WHY USE HASHES:
    /// String comparison is slow: if (stateName == "fire")
    /// Integer comparison is fast: if (stateHash == fireNameHash)
    /// Hash is calculated once in initialization, used many times per frame
    /// 
    /// USAGE:
    /// UpdateControllerState() checks animator state:
    /// if (info.shortNameHash == fireNameHash) → weapon is firing
    /// 
    /// CRITICAL:
    /// Hash must match animator state name exactly (case-sensitive!)
    /// If animator state is "Fire" but we hash "fire", won't match
    /// </summary>
    int fireNameHash = Animator.StringToHash("fire");

    /// <summary>
    /// Pre-calculated hash of "reload" animator parameter name
    /// Same optimization as fireNameHash
    /// Used to detect when weapon is in reload animation state
    /// </summary>
    int reloadNameHash = Animator.StringToHash("reload");


    // ========================================================================
    // AWAKE - Unity lifecycle method for initialization
    // ========================================================================
    // Awake() is called when the script instance is being loaded
    // Runs before Start(), before the first frame
    // Used here to cache component references and set up object pools
    // ========================================================================

    /// <summary>
    /// Called when weapon prefab is instantiated
    /// Sets up component references and initializes object pools
    /// Runs once per weapon instance, before the weapon is used
    /// </summary>
    void Awake()
    {
        // ====================================================================
        // COMPONENT REFERENCE CACHING
        // ====================================================================
        // Cache component references in Awake() for performance
        // GetComponent is slow - calling it every frame causes performance issues
        // Cache once in Awake, use cached reference many times

        // Find the Animator component on this weapon or its children
        // GetComponentInChildren searches this GameObject and all children
        // Returns first Animator found in the hierarchy
        // 
        // WHY CHILDREN:
        // Weapon structure is typically:
        // - WeaponRoot (this script attached here)
        //   - Model (3D mesh)
        //     - Animator (attached to model for animations)
        //   - MuzzleFlash (particle system)
        //   - EndPoint (empty GameObject marking barrel tip)
        // 
        // Animator is often on child object with the 3D model
        // GetComponentInChildren finds it automatically regardless of hierarchy
        m_Animator = GetComponentInChildren<Animator>();

        // Find the AudioSource component on this weapon or its children
        // Similar reasoning as Animator - often attached to child object
        // Used to play fire and reload sound effects
        // 
        // AUDIO SETUP:
        // AudioSource should be configured in prefab:
        // - Spatial Blend: 1.0 (fully 3D) for positional audio
        // - Volume: 0.5-1.0 depending on weapon
        // - Play On Awake: false (we control when sounds play)
        m_Source = GetComponentInChildren<AudioSource>();

        // Initialize clip content to maximum capacity
        // When player picks up weapon, it starts with full clip
        // Reserve ammo is handled separately by Controller
        // 
        // EXAMPLE:
        // If clipSize = 30, m_ClipContent starts at 30
        // Player can fire 30 times before needing to reload
        m_ClipContent = clipSize;

        // ====================================================================
        // RAYCAST TRAIL OBJECT POOLING
        // ====================================================================
        // Initialize pool for bullet trail visual effects (if applicable)

        // Check if this weapon has a bullet trail effect assigned
        // null check prevents errors if trail effect isn't needed (melee weapon)
        if (PrefabRayTrail != null)
        {
            // Define pool size as constant
            // 16 trails = enough for rapid fire without running out
            // Const = value cannot change, optimized by compiler
            // 
            // SIZING LOGIC:
            // Fast automatic weapon: 10 shots/sec
            // Trails last 0.3 seconds
            // Max simultaneous trails = 10 * 0.3 = 3
            // 16 provides comfortable buffer (5x the minimum)
            const int trailPoolSize = 16;

            // Initialize the pool in the global PoolSystem
            // PoolSystem.Instance is a singleton managing all pooled objects
            // InitPool creates 16 LineRenderer instances, all disabled
            // When weapon fires, we pull from this pool instead of Instantiate
            // 
            // PERFORMANCE BENEFIT:
            // Without pooling: Each shot → Instantiate trail → Destroy trail → GC
            // With pooling: Pull from pool → use → return to pool → no GC
            // Result: Smooth performance even with rapid fire
            PoolSystem.Instance.InitPool(PrefabRayTrail, trailPoolSize);
        }

        // ====================================================================
        // PROJECTILE OBJECT POOLING
        // ====================================================================
        // Initialize pool for physical projectiles (if applicable)

        // Check if this is a projectile weapon (grenades, rockets)
        // Only create projectile pool if projectilePrefab is assigned
        if (projectilePrefab != null)
        {
            // POOL SIZE CALCULATION
            // Base the pool size on clip size with minimum of 4
            // Why minimum 4? Weapons with clipSize of 1 (grenades) can have
            // multiple projectiles in flight before first one explodes
            // 
            // Example scenarios:
            // - Grenade: clipSize=1, but player throws 3 before first explodes
            // - Rocket: clipSize=1, can fire second before first hits
            // - Shotgun projectiles: clipSize=8, shoots pellets as projectiles
            // 
            // Mathf.Max returns the larger of two values
            // This ensures pool is never smaller than 4
            // Then multiply by projectilePerShot for multi-shot weapons (shotgun)
            // 
            // CALCULATION EXAMPLES:
            // - Grenade (clipSize=1, projectilePerShot=1): Max(4,1)*1 = 4
            // - Rocket (clipSize=4, projectilePerShot=1): Max(4,4)*1 = 4  
            // - Shotgun (clipSize=8, projectilePerShot=8): Max(4,8)*8 = 64
            int size = Mathf.Max(4, clipSize) * advancedSettings.projectilePerShot;

            // Create the projectile pool using a simple for loop
            // Unlike PoolSystem, weapon manages its own projectile pool
            // This is because projectiles need to know which weapon fired them
            for (int i = 0; i < size; ++i)
            {
                // Instantiate creates a new projectile from the prefab
                // This is expensive (slow), which is why we do it once in Awake
                // Not during gameplay when performance matters
                Projectile p = Instantiate(projectilePrefab);

                // Immediately disable the projectile
                // SetActive(false) makes it invisible and stops all scripts
                // Projectile waits in pool until needed
                // When fired, we'll SetActive(true) to activate it
                p.gameObject.SetActive(false);

                // Add projectile to the queue (FIFO data structure)
                // Enqueue adds to the back of the queue
                // When we fire, Dequeue removes from front of queue
                // This ensures all projectiles get used fairly/evenly
                m_ProjectilePool.Enqueue(p);
            }
            // After this loop, we have a pool of pre-created projectiles
            // Ready to be activated when weapon fires
            // No need to Instantiate during gameplay!
        }

        // At this point, weapon is fully initialized and ready to use
        // Component references cached
        // Object pools created
        // Clip is full
        // Weapon is ready to be picked up by player
    }

    // ========================================================================
    // PICKEDUP - Called when player acquires this weapon
    // ========================================================================

    /// <summary>
    /// Called by Controller when player picks up this weapon
    /// Establishes the ownership relationship between weapon and player
    /// </summary>
    /// <param name="c">The Controller (player) that picked up this weapon</param>
    public void PickedUp(Controller c)
    {
        // Store reference to the player controller
        // This allows weapon to:
        // - Access player's ammo inventory: m_Owner.GetAmmo(ammoType)
        // - Deduct ammo when firing: m_Owner.ChangeAmmo(ammoType, -1)
        // - Check player state: m_Owner.Speed, m_Owner.Grounded
        // 
        // LIFECYCLE:
        // 1. Player walks over weapon pickup or starts with weapon
        // 2. Controller.PickupWeapon() is called
        // 3. Weapon prefab is instantiated
        // 4. weapon.PickedUp(this) is called
        // 5. Now weapon knows who owns it
        // 
        // Once set, m_Owner never changes (weapon stays with player)
        m_Owner = c;

        // That's it! Very simple method
        // All the complex pickup logic is in Controller.PickupWeapon()
        // This method just establishes the relationship
    }

    // ========================================================================
    // PUTAWAY - Called when switching away from this weapon
    // ========================================================================

    /// <summary>
    /// Called when player switches to a different weapon
    /// Cleans up weapon state and stops visual effects
    /// Prepares weapon to be hidden from view
    /// </summary>
    public void PutAway()
    {
        // ====================================================================
        // RESET ANIMATOR STATE
        // ====================================================================

        // Reset animator to default state/pose
        // WriteDefaultValues() returns all animated properties to their defaults
        // This prevents weapon from being "stuck" in an animation frame
        // 
        // WHY NEEDED:
        // Without this, weapon might be holstered while:
        // - Mid-fire animation (hammer pulled back, muzzle flash visible)
        // - Mid-reload animation (magazine out, hands repositioned)
        // When weapon is selected again, it would start in wrong state
        // WriteDefaultValues ensures clean slate
        // 
        // EFFECT:
        // All animator parameters reset
        // Weapon returns to idle pose
        // Any ongoing animation is interrupted
        m_Animator.WriteDefaultValues();

        // ====================================================================
        // CLEANUP ACTIVE BULLET TRAILS
        // ====================================================================

        // Loop through all currently active bullet trail effects
        // We need to disable them because weapon is being hidden
        // Leaving trails visible would look wrong (bullets from invisible weapon)
        for (int i = 0; i < m_ActiveTrails.Count; ++i)
        {
            // Get reference to current trail in the list
            // var type inference - compiler knows it's ActiveTrail
            var activeTrail = m_ActiveTrails[i];

            // Disable the LineRenderer GameObject
            // This makes the trail invisible immediately
            // Trail is returned to pool for reuse later
            // 
            // WHY DISABLE:
            // - Player shouldn't see bullets from holstered weapon
            // - Prevents visual glitch of trails appearing from wrong location
            // - Cleans up the scene visually
            m_ActiveTrails[i].renderer.gameObject.SetActive(false);
        }

        // Clear the list of active trails
        // Clear() removes all elements from list
        // List now has Count of 0
        // 
        // WHY CLEAR:
        // List is now empty, ready for when weapon is selected again
        // No leftover trail references from previous use
        // Clean state for next selection
        m_ActiveTrails.Clear();

        // After this method:
        // - Animator is reset to defaults
        // - All bullet trails are hidden
        // - Weapon is ready to be hidden (SetActive false in Controller)
        // - State is clean for next time weapon is selected
    }

    // ========================================================================
    // SELECTED - Called when player switches TO this weapon
    // ========================================================================

    /// <summary>
    /// Called when player switches to this weapon
    /// Most complex initialization method - handles many edge cases
    /// Updates UI, checks ammo, adjusts animations, sets initial state
    /// </summary>
    public void Selected()
    {
        // ====================================================================
        // CHECK AMMO AVAILABILITY
        // ====================================================================

        // Get how much reserve ammo player has for this weapon type
        // m_Owner.GetAmmo() looks up ammo count in player's inventory dictionary
        // Returns 0 if player has no ammo of this type
        // 
        // EXAMPLE:
        // If weapon uses "Rifle Ammo" (ammoType = 1)
        // And player has 120 rifle rounds in reserve
        // ammoRemaining = 120
        var ammoRemaining = m_Owner.GetAmmo(ammoType);

        // ====================================================================
        // HANDLE EMPTY WEAPON DISABLING
        // ====================================================================

        // Check if weapon should be disabled when completely out of ammo
        // DisabledOnEmpty is typically true for limited-use weapons (grenades)
        // And false for normal guns (show empty gun model)
        if (DisabledOnEmpty)
        {
            // Check if player has ANY ammo for this weapon
            // Either in clip OR in reserve
            // 
            // LOGIC:
            // ammoRemaining == 0: No reserve ammo
            // m_ClipContent == 0: No ammo in clip
            // Both true = completely out of ammo
            // 
            // If player has ammo (either location), weapon stays active
            // If player has NO ammo anywhere, weapon deactivates
            // 
            // EXAMPLE USE CASE - GRENADES:
            // - Player has 3 grenades (clipSize=1, so can throw 3 times)
            // - Throws all 3: clip=0, reserve=0
            // - SetActive(false) hides grenade from hands
            // - Player auto-switches to another weapon
            // - If player picks up more grenades: weapon reactivates
            gameObject.SetActive(ammoRemaining != 0 || m_ClipContent != 0);
        }

        // ====================================================================
        // SYNCHRONIZE ANIMATION SPEEDS
        // ====================================================================
        // Match animation playback speed to gameplay timing
        // Ensures animations complete exactly when action completes

        // FIRE ANIMATION SPEED ADJUSTMENT
        // Check if fire animation clip is assigned
        if (FireAnimationClip != null)
        {
            // Calculate speed multiplier for fire animation
            // 
            // FORMULA: animationLength / gameplayTime
            // 
            // EXAMPLE 1 - SLOW ANIMATION:
            // Animation length: 0.3 seconds
            // Fire rate: 0.5 seconds (gameplay timing)
            // Speed: 0.3 / 0.5 = 0.6
            // Result: Animation plays at 60% speed (slowed down)
            // Animation takes 0.5 seconds (matches fire rate)
            // 
            // EXAMPLE 2 - FAST ANIMATION:
            // Animation length: 0.8 seconds  
            // Fire rate: 0.5 seconds
            // Speed: 0.8 / 0.5 = 1.6
            // Result: Animation plays at 160% speed (sped up)
            // Animation takes 0.5 seconds (matches fire rate)
            // 
            // WHY ADJUST:
            // Designer creates animation at whatever speed feels good
            // Then adjusts fireRate for gameplay balance
            // This auto-scales animation to match
            // 
            // SetFloat sets animator parameter that controls playback speed
            // Animator controller must have "fireSpeed" parameter
            m_Animator.SetFloat("fireSpeed", FireAnimationClip.length / fireRate);
        }

        // RELOAD ANIMATION SPEED ADJUSTMENT
        // Same logic as fire animation, but for reloading
        if (ReloadAnimationClip != null)
        {
            // Calculate and set reload animation speed
            // Matches reload animation length to reloadTime gameplay value
            // Ensures reload completes when animation completes
            // 
            // EXAMPLE:
            // Animation: 2.5 seconds
            // Reload time: 2.0 seconds
            // Speed: 2.5 / 2.0 = 1.25 (play 25% faster)
            // Animation now takes exactly 2.0 seconds
            m_Animator.SetFloat("reloadSpeed", ReloadAnimationClip.length / reloadTime);
        }

        // ====================================================================
        // SET INITIAL WEAPON STATE
        // ====================================================================

        // Set weapon to Idle state
        // Not firing, not reloading, ready for player input
        // This is the "ready to use" state
        m_CurrentState = WeaponState.Idle;

        // Reset trigger state
        // triggerDown property setter also resets m_ShotDone
        // Ensures weapon doesn't fire immediately when selected
        // 
        // WITHOUT THIS:
        // If player held fire button while switching weapons
        // New weapon would fire immediately (usually not desired)
        triggerDown = false;

        // Explicitly reset shot done flag as safety measure
        // Already handled by triggerDown setter, but being explicit
        // Defensive programming - ensures clean state
        m_ShotDone = false;

        // ====================================================================
        // UPDATE UI DISPLAYS
        // ====================================================================
        // Tell UI systems to update for this weapon

        // Update weapon name text on HUD
        // Shows "Pistol", "Rifle", "Shotgun", etc.
        // Uses weapon's name property (from GameObject.name)
        WeaponInfoUI.Instance.UpdateWeaponName(this);

        // Update clip ammo display
        // Shows current clip content like "30" or "8"
        // Part of "30/120" display (30 in clip)
        WeaponInfoUI.Instance.UpdateClipInfo(this);

        // Update reserve ammo display
        // Shows total ammo in reserve like "120"
        // Part of "30/120" display (120 in reserve)
        WeaponInfoUI.Instance.UpdateAmmoAmount(m_Owner.GetAmmo(ammoType));

        // Update custom ammo display if weapon has one
        // Some weapons have special UI (grenade counter, etc.)
        // AmmoDisplay is abstract base class, could be any implementation
        // Check if not null before calling
        if (AmmoDisplay)
        {
            // UpdateAmount shows current/max like progress bar or icon grid
            // Examples: "5/8" grenades, visual bullet icons, progress bar
            AmmoDisplay.UpdateAmount(m_ClipContent, clipSize);
        }

        // ====================================================================
        // HANDLE AUTO-RELOAD EDGE CASE
        // ====================================================================
        // Special case: Empty clip but player now has ammo

        // Check if clip is empty AND reserve has ammo
        // 
        // SCENARIO:
        // 1. Player was using rifle, ran out of ALL rifle ammo
        // 2. Clip empty (0), reserve empty (0)
        // 3. Player switched to pistol
        // 4. Player found rifle ammo box, picked it up
        // 5. Reserve now has ammo (30), but clip still empty (0)
        // 6. Player switches back to rifle
        // 7. We detect: clip=0, reserve=30 → auto-reload!
        // 
        // WHY AUTO-RELOAD:
        // Without this, player switches to empty weapon
        // They have ammo but can't fire without manual reload
        // This does the reload automatically for better UX
        if (m_ClipContent == 0 && ammoRemaining != 0)
        {
            // Comment from original code explains this exact scenario
            // "this can only happen if the weapon ammo reserve was empty and we 
            // picked some since then. So directly reload the clip when weapon is selected"

            // Calculate how much ammo to move from reserve to clip
            // Take minimum of: reserve ammo OR clip capacity
            // 
            // EXAMPLES:
            // Scenario A: reserve=30, clipSize=30 → chargeInClip=30 (fill clip)
            // Scenario B: reserve=15, clipSize=30 → chargeInClip=15 (partial fill)
            // 
            // Mathf.Min returns smaller of two values
            // Prevents trying to load more than clip can hold
            // Prevents trying to load more than player has
            int chargeInClip = Mathf.Min(ammoRemaining, clipSize);

            // Add ammo to clip
            // += adds to current value (currently 0, so becomes chargeInClip)
            m_ClipContent += chargeInClip;

            // Update custom ammo display if present
            // Show new clip content after auto-reload
            if (AmmoDisplay)
                AmmoDisplay.UpdateAmount(m_ClipContent, clipSize);

            // Deduct ammo from player's reserve
            // Negative value means subtract (ChangeAmmo can add or subtract)
            // Example: If chargeInClip=30, call ChangeAmmo(ammoType, -30)
            // Reserve goes from 30 to 0
            m_Owner.ChangeAmmo(ammoType, -chargeInClip);

            // Update UI to show new clip content
            // Now displays "30" instead of "0"
            WeaponInfoUI.Instance.UpdateClipInfo(this);
        }

        // ====================================================================
        // TRIGGER SELECTION ANIMATION
        // ====================================================================

        // Tell animator to play weapon draw/equip animation
        // SetTrigger activates a trigger parameter in animator
        // Animator transitions from idle to "selected" state
        // Plays animation of weapon being drawn from holster
        // 
        // ANIMATION EXAMPLES:
        // - Pistol: Slides into view from bottom of screen
        // - Rifle: Swings up into ready position
        // - Grenade: Hand reaches off-screen and brings grenade into view
        // 
        // After animation completes, weapon is ready to use
        m_Animator.SetTrigger("selected");

        // At this point, weapon is fully prepared and visible
        // Player can now fire or reload
    }

    // ========================================================================
    // FIRE - Main method for shooting the weapon
    // ========================================================================

    /// <summary>
    /// Attempts to fire the weapon
    /// Handles validation, state changes, ammo consumption, effects, and hit detection
    /// Called from UpdateControllerState() based on trigger input
    /// </summary>
    public void Fire()
    {
        // ====================================================================
        // FIRING VALIDATION - Check if weapon CAN fire
        // ====================================================================
        // Multiple conditions must be met to fire
        // If ANY condition fails, early exit (return) prevents firing

        // Condition 1: Must be in Idle state (not already firing or reloading)
        // Condition 2: Shot timer must have expired (enforces fire rate)
        // Condition 3: Must have ammo in clip (at least 1 bullet)
        // 
        // Logical OR (||): If ANY condition is true, entire statement is true
        // If statement is true, return early (don't fire)
        // 
        // EXAMPLES OF PREVENTED FIRING:
        // - m_CurrentState = Firing: Can't fire again until animation completes
        // - m_ShotTimer = 0.3: Still cooling down, too soon to fire again
        // - m_ClipContent = 0: No ammo, need to reload first
        if (m_CurrentState != WeaponState.Idle || m_ShotTimer > 0 || m_ClipContent == 0)
            return; // Exit method immediately, weapon doesn't fire

        // If we reach here, all conditions passed - weapon will fire!

        // ====================================================================
        // AMMO CONSUMPTION
        // ====================================================================

        // Consume one round from clip
        // -= 1 subtracts 1 from current value
        // Example: clip had 30, now has 29
        // 
        // NOTE: We check m_ClipContent > 0 above, so this is safe
        // Won't go negative because we validated first
        m_ClipContent -= 1;

        // ====================================================================
        // FIRE RATE ENFORCEMENT
        // ====================================================================

        // Reset shot timer to enforce fire rate delay
        // Weapon cannot fire again until this timer counts down to 0
        // Timer is decremented in Update() method
        // 
        // EXAMPLE:
        // fireRate = 0.5 seconds
        // After firing, m_ShotTimer = 0.5
        // Update() subtracts Time.deltaTime each frame
        // After 0.5 seconds, m_ShotTimer = 0
        // Now can fire again (timer <= 0 check passes)
        m_ShotTimer = fireRate;

        // ====================================================================
        // UPDATE UI DISPLAYS
        // ====================================================================

        // Update custom ammo display if weapon has one
        // Shows new clip count after consuming ammo
        if (AmmoDisplay)
            AmmoDisplay.UpdateAmount(m_ClipContent, clipSize);

        // Update standard weapon info UI
        // Updates clip count in HUD (30 → 29)
        WeaponInfoUI.Instance.UpdateClipInfo(this);

        // ====================================================================
        // STATE CHANGE
        // ====================================================================

        // Set weapon state to Firing
        // This happens immediately (before animation frame advances)
        // Prevents firing again until state returns to Idle
        // 
        // IMPORTANT NOTE FROM ORIGINAL CODE:
        // "the state will only change next frame, so we set it right now."
        // Animator state changes happen during its update cycle
        // But we need the state changed NOW to prevent double-firing
        // So we manually set it here, before animator updates
        m_CurrentState = WeaponState.Firing;

        // ====================================================================
        // TRIGGER FIRE ANIMATION
        // ====================================================================

        // Tell animator to play firing animation
        // SetTrigger activates "fire" trigger parameter
        // Animator transitions to fire state
        // 
        // ANIMATION INCLUDES:
        // - Weapon recoil/kick
        // - Muzzle flash particle effect
        // - Shell casing ejection
        // - Weapon shake
        m_Animator.SetTrigger("fire");

        // ====================================================================
        // PLAY FIRE SOUND EFFECT
        // ====================================================================

        // Randomize pitch for audio variety
        // Without this, rapid fire sounds robotic and repetitive
        // 
        // Random.Range(0.7f, 1.0f) returns random float between 0.7 and 1.0
        // 0.7 = lower pitch (deeper sound)
        // 1.0 = normal pitch (original sound)
        // 
        // VARIATION EXAMPLE:
        // Shot 1: pitch = 0.85 (slightly lower)
        // Shot 2: pitch = 0.93 (almost normal)
        // Shot 3: pitch = 0.71 (noticeably lower)
        // Result: Each shot sounds slightly different, more realistic
        m_Source.pitch = Random.Range(0.7f, 1.0f);

        // Play the fire sound effect
        // PlayOneShot plays clip once without interrupting other sounds
        // Good for gunshots because multiple can overlap
        // 
        // VS Play():
        // Play() would restart sound if already playing
        // PlayOneShot() allows sounds to stack/overlap
        // Important for rapid fire weapons
        m_Source.PlayOneShot(FireAudioClip);

        // ====================================================================
        // SCREEN SHAKE EFFECT
        // ====================================================================

        // Shake camera to give impact feedback
        // Makes weapon feel more powerful
        // 
        // PARAMETERS:
        // First parameter (0.2f): Duration in seconds
        // Second parameter: Intensity multiplied by weapon's setting
        // 
        // CALCULATION:
        // Base intensity: 0.05 (small shake)
        // Multiplied by advancedSettings.screenShakeMultiplier
        // 
        // EXAMPLES:
        // Pistol (multiplier=0.5): 0.05 * 0.5 = 0.025 (gentle shake)
        // Rifle (multiplier=1.0): 0.05 * 1.0 = 0.05 (normal shake)
        // Shotgun (multiplier=2.0): 0.05 * 2.0 = 0.1 (strong shake)
        // 
        // CameraShaker.Instance is singleton managing screen shake
        CameraShaker.Instance.Shake(0.2f, 0.05f * advancedSettings.screenShakeMultiplier);

        // ====================================================================
        // WEAPON TYPE BRANCHING - Execute appropriate fire method
        // ====================================================================

        // Check weapon type and call corresponding method
        // This is where raycast and projectile weapons diverge
        if (weaponType == WeaponType.Raycast)
        {
            // RAYCAST WEAPON (instant hit)
            // Fire multiple raycasts based on projectilePerShot setting
            // 
            // WHY LOOP:
            // Shotguns fire multiple pellets (projectilePerShot = 8)
            // Each pellet needs separate raycast with different spread
            // Single-shot weapons have projectilePerShot = 1 (one iteration)
            // 
            // EXAMPLES:
            // Pistol (projectilePerShot=1): Loop runs once
            // Shotgun (projectilePerShot=8): Loop runs 8 times
            for (int i = 0; i < advancedSettings.projectilePerShot; ++i)
            {
                // Fire one raycast
                // Each iteration has random spread
                // Creates spread pattern for shotguns
                RaycastShot();
            }
        }
        else
        {
            // PROJECTILE WEAPON (physical projectile)
            // Launch projectile(s) from pool
            // Handles grenades, rockets, etc.
            // ProjectileShot() contains loop for multiple projectiles
            ProjectileShot();
        }

        // After this method completes:
        // - Ammo consumed (clip decreased by 1)
        // - Timer set (enforcing fire rate)
        // - State changed to Firing
        // - Animation playing
        // - Sound playing
        // - Camera shaking
        // - Bullet/projectile spawned and traveling
        // 
        // Weapon will return to Idle state when animation completes
        // (handled in UpdateControllerState method)
    }

    // ========================================================================
    // RAYCASTSHOT - Implements instant-hit weapon mechanics
    // ========================================================================

    /// <summary>
    /// Fires a single raycast shot with spread
    /// Handles hit detection, damage application, impact effects, and bullet trails
    /// Called once per shot for single-fire weapons, multiple times for shotguns
    /// </summary>
    void RaycastShot()
    {
        // ====================================================================
        // SPREAD CALCULATION - Weapon inaccuracy simulation
        // ====================================================================

        // Calculate spread ratio relative to camera field of view
        // This makes spread angle work correctly at any FOV
        // 
        // FORMULA: spreadAngle / camera FOV
        // 
        // WHY DIVIDE BY FOV:
        // Spread angle is in world space degrees
        // But we need viewport space offset (0-1 range)
        // Dividing by FOV converts world angle to viewport ratio
        // 
        // EXAMPLE:
        // spreadAngle = 5 degrees
        // Camera FOV = 60 degrees
        // spreadRatio = 5 / 60 = 0.0833
        // This means spread can deviate up to 8.33% from center in viewport space
        // 
        // BENEFIT:
        // Works correctly regardless of camera FOV
        // Zoom in (lower FOV) = spread looks tighter
        // Zoom out (higher FOV) = spread looks wider
        // But actual angle stays same
        float spreadRatio = advancedSettings.spreadAngle / Controller.Instance.MainCamera.fieldOfView;

        // Generate random 2D offset within a circle
        // Random.insideUnitCircle returns Vector2 with length <= 1.0
        // Points are uniformly distributed inside circle
        // 
        // EXAMPLES:
        // Result 1: (0.5, 0.3) - offset to upper right
        // Result 2: (-0.7, -0.2) - offset to lower left
        // Result 3: (0.1, -0.8) - offset mostly down
        // 
        // Multiply by spreadRatio to scale to weapon's spread
        // 
        // SPREAD PATTERNS:
        // No spread (0): All shots hit exact center
        // Small spread (2-5): Tight grouping, good accuracy
        // Large spread (10-30): Wide grouping, shotgun pattern
        Vector2 spread = spreadRatio * Random.insideUnitCircle;

        // ====================================================================
        // RAYCAST SETUP
        // ====================================================================

        // Declare variable to store raycast hit information
        // RaycastHit is a struct containing:
        // - point: World position where ray hit
        // - normal: Surface normal at hit point (perpendicular to surface)
        // - distance: Distance from ray origin to hit point
        // - collider: The Collider that was hit
        // - etc.
        RaycastHit hit;

        // Create ray from camera through viewport point with spread
        // 
        // BREAKDOWN:
        // Vector3.one = (1, 1, 1)
        // Vector3.one * 0.5f = (0.5, 0.5, 0.5)
        // This is viewport center (center of screen)
        // 
        // (Vector3)spread casts Vector2 to Vector3 (adds z=0)
        // Adding spread offset moves ray origin from center
        // 
        // ViewportPointToRay converts viewport coords (0-1) to world space ray
        // Returns Ray with origin and direction
        // 
        // COORDINATE SYSTEMS:
        // Viewport: (0,0) = bottom-left, (1,1) = top-right, (0.5,0.5) = center
        // Spread offset: (-0.1, 0.05) might be slightly left and up from center
        // World: Ray starts at camera, points through offset screen position
        Ray r = Controller.Instance.MainCamera.ViewportPointToRay(Vector3.one * 0.5f + (Vector3)spread);

        // Calculate default hit position (if ray doesn't hit anything)
        // Ray origin + ray direction * distance
        // If nothing hit, assume bullet traveled 200 units forward
        // This is where bullet trail will end if no collision
        // 
        // WHY 200:
        // Far enough to look like bullet went "far away"
        // But not so far that calculations become imprecise
        // Typical weapon range is much less, so this is safe default
        Vector3 hitPosition = r.origin + r.direction * 200.0f;

        // ====================================================================
        // PERFORM RAYCAST - Check what bullet hit
        // ====================================================================

        // Cast ray into scene and check if it hit something
        // 
        // PARAMETERS:
        // r: The ray to cast (origin + direction)
        // out hit: Output parameter - filled with hit info if something hit
        // 1000.0f: Maximum ray distance (don't check beyond 1000 units)
        // ~(1 << 9): Layer mask - what layers to check
        // QueryTriggerInteraction.Ignore: Don't hit trigger colliders
        // 
        // LAYER MASK EXPLAINED:
        // (1 << 9) creates bitmask with bit 9 set: 0000001000000000
        // This represents layer 9
        // ~ inverts all bits: 1111110111111111
        // Result: Check ALL layers EXCEPT layer 9
        // Layer 9 is typically "Ignore Raycast" layer
        // 
        // RETURN VALUE:
        // true: Ray hit something, 'hit' struct contains details
        // false: Ray hit nothing, 'hit' struct is invalid
        // 
        // We use the return value as condition for if statement
        if (Physics.Raycast(r, out hit, 1000.0f, ~(1 << 9), QueryTriggerInteraction.Ignore))
        {
            // RAY HIT SOMETHING - Process the hit

            // ================================================================
            // GET HIT SURFACE MATERIAL
            // ================================================================

            // Try to get Renderer component from hit object or its children
            // Renderer contains material information for impact effects
            // 
            // GetComponentInChildren searches object and children
            // Returns null if no Renderer found
            // 
            // WHY CHILDREN:
            // Collider might be on parent object
            // Renderer (visual) might be on child object
            // This finds the renderer regardless of hierarchy
            Renderer renderer = hit.collider.GetComponentInChildren<Renderer>();

            // Play appropriate impact effect based on surface material
            // ImpactManager.Instance is singleton managing hit effects
            // 
            // PARAMETERS:
            // hit.point: World position where bullet hit
            // hit.normal: Surface normal (direction perpendicular to surface)
            // renderer == null ? null : renderer.sharedMaterial: Material or null
            // 
            // TERNARY OPERATOR EXPLAINED:
            // condition ? valueIfTrue : valueIfFalse
            // If renderer is null: pass null to PlayImpact
            // If renderer exists: pass renderer.sharedMaterial
            // 
            // WHY sharedMaterial:
            // .sharedMaterial is the original material asset
            // .material creates instance copy (we don't need that)
            // ImpactManager uses material to determine effect type:
            // - Wood material → wood splinter particles + wood impact sound
            // - Metal material → spark particles + metal clang sound
            // - Dirt material → dust particles + dirt impact sound
            // - null (no material) → default impact effect
            ImpactManager.Instance.PlayImpact(hit.point, hit.normal, renderer == null ? null : renderer.sharedMaterial);

            // ================================================================
            // ADJUST HIT POSITION FOR VISUAL ACCURACY
            // ================================================================

            // Check if hit was far enough away to need correction
            // If too close (< 5 units), trail would look weird
            // 
            // PROBLEM AT CLOSE RANGE:
            // Weapon camera uses different FOV than main camera
            // This causes parallax/alignment issues at close range
            // Bullet trail would appear disconnected from muzzle
            // 
            // SOLUTION:
            // If hit distance > 5, use actual hit point (accurate)
            // If hit distance <= 5, use default position (prevents visual glitch)
            // 
            // 5 units is threshold where visual error becomes noticeable
            if (hit.distance > 5.0f)
                hitPosition = hit.point;

            // ================================================================
            // TARGET DAMAGE APPLICATION
            // ================================================================

            // Check if hit object is on layer 10 (Target layer)
            // Layer 10 is configured as the layer for shootable targets
            // 
            // WHY LAYER CHECK:
            // Fast way to identify targets without GetComponent
            // Layer is just an integer comparison (very fast)
            // GetComponent is slower (searches for script component)
            // 
            // ALTERNATIVE APPROACH:
            // Could do: Target t = hit.collider.GetComponent<Target>();
            // Then: if (t != null) t.Got(damage);
            // But layer check is faster
            if (hit.collider.gameObject.layer == 10)
            {
                // Hit object is a target - apply damage

                // Get Target component from the hit object
                // We know it exists because layer 10 = targets
                // GameObject.GetComponent<T> finds component of type T
                Target target = hit.collider.gameObject.GetComponent<Target>();

                // Apply damage to target
                // Got(damage) is Target's method for taking damage
                // Target will:
                // - Subtract damage from health
                // - Play hit sound/particles
                // - Destroy itself if health reaches 0
                // - Notify GameSystem of destruction
                target.Got(damage);
            }
        }
        // If raycast hit nothing:
        // - hitPosition stays as default (200 units forward)
        // - No impact effects play
        // - No damage applied
        // - Bullet trail will show bullet going off into distance

        // ====================================================================
        // BULLET TRAIL VISUAL EFFECT
        // ====================================================================

        // Check if weapon has bullet trail effect configured
        // null check prevents error if trail not needed/assigned
        if (PrefabRayTrail != null)
        {
            // ================================================================
            // CREATE TRAIL LINE POSITIONS
            // ================================================================

            // Create array of 2 Vector3 positions for LineRenderer
            // Array initializer syntax: new Type[] { value1, value2, ... }
            // 
            // Position 0: Trail start (weapon muzzle)
            // Position 1: Trail end (hit point or default far position)
            // 
            // GetCorrectedMuzzlePlace() returns muzzle position
            // Corrected for FOV differences between cameras
            // Ensures trail appears to come from visible muzzle flash
            var pos = new Vector3[] { GetCorrectedMuzzlePlace(), hitPosition };

            // ================================================================
            // GET TRAIL FROM OBJECT POOL
            // ================================================================

            // Retrieve LineRenderer from pool instead of Instantiate
            // PoolSystem.Instance.GetInstance<T> gets pooled object of type T
            // If pool has available object: returns that
            // If pool empty: creates new instance automatically
            // 
            // PERFORMANCE:
            // This avoids Instantiate/Destroy which causes GC spikes
            // Trail is reused many times over gameplay session
            var trail = PoolSystem.Instance.GetInstance<LineRenderer>(PrefabRayTrail);

            // ================================================================
            // CONFIGURE AND DISPLAY TRAIL
            // ================================================================

            // Activate trail GameObject (make visible)
            // Pulled from pool in disabled state
            // SetActive(true) enables it
            trail.gameObject.SetActive(true);

            // Set LineRenderer positions to our calculated array
            // SetPositions(Vector3[]) sets all points at once
            // Trail now draws line from muzzle to hit point
            // 
            // VISUAL RESULT:
            // Bright line appears from gun barrel to target
            // Represents bullet streak/tracer
            trail.SetPositions(pos);

            // ================================================================
            // ADD TO ACTIVE TRAILS LIST FOR ANIMATION
            // ================================================================

            // Create new ActiveTrail object to track this trail
            // Object initializer syntax: new Class() { field = value, ... }
            // 
            // FIELDS:
            // remainingTime: How long trail stays visible (0.3 seconds)
            // direction: Normalized direction trail moves (forward motion)
            // renderer: Reference to LineRenderer for updating position
            // 
            // DIRECTION CALCULATION:
            // (pos[1] - pos[0]) = vector from start to end
            // .normalized = convert to unit vector (length = 1)
            // Used to animate trail moving forward
            // 
            // ADD TO LIST:
            // m_ActiveTrails.Add() appends to list
            // Update() will animate this trail each frame
            // After 0.3 seconds, Update() removes it from list
            m_ActiveTrails.Add(new ActiveTrail()
            {
                remainingTime = 0.3f,
                direction = (pos[1] - pos[0]).normalized,
                renderer = trail
            });
        }

        // After RaycastShot completes:
        // - Ray has been cast through world
        // - Any hit has been detected
        // - Impact effects have played
        // - Damage has been applied to targets
        // - Bullet trail is visible and animating
        // 
        // Multiple RaycastShots can be in progress simultaneously
        // (shotgun fires 8 at once, each has own trail)
    }

    // ========================================================================
    // PROJECTILESHOT - Implements physical projectile weapons
    // ========================================================================

    /// <summary>
    /// Fires physical projectile(s) from object pool
    /// Used for grenades, rockets, grenade launchers
    /// Handles spread, object pooling, and launching
    /// </summary>
    void ProjectileShot()
    {
        // Loop for each projectile to fire
        // Usually 1 for single projectiles (grenade, rocket)
        // Could be multiple for spread projectiles
        // 
        // EXAMPLES:
        // Grenade launcher: projectilePerShot = 1 (one grenade)
        // Cluster bomb: projectilePerShot = 3 (three mini-bombs)
        for (int i = 0; i < advancedSettings.projectilePerShot; ++i)
        {
            // ================================================================
            // CALCULATE SPREAD ANGLE
            // ================================================================

            // Generate random spread angle within weapon's spread range
            // Random.Range(min, max) returns random float between min and max
            // 
            // spreadAngle * 0.5f: Half the total spread cone
            // Example: spreadAngle = 10 degrees
            // Spread range: 0 to 5 degrees
            // 
            // WHY HALF:
            // Full spread creates cone of (spreadAngle * 0.5) in each direction
            // So random value 0 to 5 can go up/down/left/right from center
            // Total cone is still 10 degrees (5 in each direction)
            float angle = Random.Range(0.0f, advancedSettings.spreadAngle * 0.5f);

            // Convert angle to 2D direction offset
            // Random.insideUnitCircle: Random point in circle (x,y in range -1 to 1)
            // Mathf.Tan: Tangent function converts angle to slope
            // Mathf.Deg2Rad: Converts degrees to radians (math functions use radians)
            // 
            // MATHEMATICAL EXPLANATION:
            // Tangent of angle gives us opposite/adjacent ratio
            // In our case: distance from center / distance forward
            // This creates proper spread cone geometry
            // 
            // RESULT:
            // angleDir is 2D vector representing spread offset
            // Small angles = small offset (tight grouping)
            // Large angles = large offset (wide spread)
            Vector2 angleDir = Random.insideUnitCircle * Mathf.Tan(angle * Mathf.Deg2Rad);

            // ================================================================
            // CALCULATE LAUNCH DIRECTION
            // ================================================================

            // Start with weapon's forward direction
            // EndPoint.transform.forward is where weapon is pointing
            // Cast Vector2 angleDir to Vector3 (adds z=0)
            // Add offset to forward direction to create spread
            // 
            // VISUALIZATION:
            // Forward: (0, 0, 1) pointing straight ahead
            // angleDir: (0.1, 0.05, 0) slight right and up
            // dir: (0.1, 0.05, 1) pointing slightly right-up-forward
            Vector3 dir = EndPoint.transform.forward + (Vector3)angleDir;

            // Normalize direction vector to unit length
            // Normalize() converts vector to length 1.0
            // Preserves direction, standardizes magnitude
            // 
            // WHY NORMALIZE:
            // Force calculation depends on direction length
            // Without normalize: weird launch velocities based on spread
            // With normalize: consistent velocity regardless of spread
            // All projectiles travel at same speed, just different directions
            dir.Normalize();

            // ================================================================
            // GET PROJECTILE FROM POOL
            // ================================================================

            // Remove projectile from front of queue
            // Dequeue() returns and removes first item in queue (FIFO)
            // 
            // OBJECT POOL WORKFLOW:
            // 1. Awake: Created pool of disabled projectiles
            // 2. Fire: Dequeue gets one from pool
            // 3. Launch: Projectile is activated and flies
            // 4. Impact: Projectile disables itself
            // 5. Return: ReturnProjecticle() enqueues it back to pool
            // 6. Next fire: Same projectile is reused
            // 
            // PERFORMANCE:
            // No Instantiate (slow)
            // No Destroy (slow, causes GC)
            // Just reuse existing objects (fast, no GC)
            var p = m_ProjectilePool.Dequeue();

            // ================================================================
            // ACTIVATE AND LAUNCH PROJECTILE
            // ================================================================

            // Enable projectile GameObject (make visible and active)
            // Pulled from pool in disabled state
            // SetActive(true) activates it
            // Projectile scripts start running
            p.gameObject.SetActive(true);

            // Launch the projectile
            // Launch() is method on Projectile script that:
            // 1. Positions projectile at weapon muzzle
            // 2. Applies physics force in direction
            // 3. Starts flight timer
            // 
            // PARAMETERS:
            // this: Reference to this weapon (projectile knows who launched it)
            // dir: Normalized direction vector (where to fly)
            // projectileLaunchForce: Force to apply (determines speed)
            // 
            // PHYSICS:
            // Projectile has Rigidbody
            // Launch calls Rigidbody.AddForce(dir * projectileLaunchForce)
            // Projectile begins flying through world
            // Affected by gravity, can collide with objects
            p.Launch(this, dir, projectileLaunchForce);
        }

        // After ProjectileShot completes:
        // - One or more projectiles are flying through air
        // - Each has physics and collision detection
        // - Will explode/deactivate on impact or timeout
        // - Will return to pool for reuse
    }

    // ========================================================================
    // RETURNPROJECTICLE - Object pool return method
    // ========================================================================

    /// <summary>
    /// Returns projectile to pool for reuse
    /// Called by Projectile script when it "destroys" itself
    /// Part of object pooling pattern for performance
    /// </summary>
    /// <param name="p">The projectile to return to pool</param>
    public void ReturnProjecticle(Projectile p)
    {
        // Add projectile back to queue for reuse
        // Enqueue() adds item to back of queue
        // 
        // OBJECT LIFECYCLE:
        // Fire: Dequeue projectile (remove from front) → Launch it
        // Impact: Projectile disables itself → Calls this method
        // Return: Enqueue projectile (add to back) → Ready for reuse
        // 
        // QUEUE BEHAVIOR (FIFO):
        // First projectile fired is first one reused
        // Ensures fair distribution - all projectiles get used evenly
        // Prevents one projectile from being reused constantly
        // 
        // IMPORTANT:
        // Projectile must be disabled before calling this
        // Projectile.Destroy() handles disable, then calls this
        // If projectile was still active, would see weird behavior
        m_ProjectilePool.Enqueue(p);

        // Simple method but critical for performance
        // Without this, would need to Instantiate new projectiles
        // With this, projectiles are reused indefinitely
    }

    // ========================================================================
    // RELOAD - Refill clip from reserve ammo
    // ========================================================================

    /// <summary>
    /// Reloads weapon by transferring ammo from reserve to clip
    /// Handles validation, animations, sounds, and ammo management
    /// Can be called manually (R key) or automatically (clip empty)
    /// </summary>
    public void Reload()
    {
        // ====================================================================
        // RELOAD VALIDATION - Check if weapon CAN reload
        // ====================================================================

        // Condition 1: Must be in Idle state (not firing or already reloading)
        // Condition 2: Clip must not already be full
        // 
        // If either condition fails, early exit prevents reload
        // Logical OR (||): If ANY condition is true, entire statement is true
        // 
        // EXAMPLES OF PREVENTED RELOADS:
        // - m_CurrentState = Firing: Can't reload while shooting
        // - m_CurrentState = Reloading: Can't reload while already reloading
        // - m_ClipContent = clipSize: Clip is full, no need to reload
        if (m_CurrentState != WeaponState.Idle || m_ClipContent == clipSize)
            return; // Exit immediately, don't reload

        // ====================================================================
        // CHECK RESERVE AMMO AVAILABILITY
        // ====================================================================

        // Get how much ammo player has in reserve
        // m_Owner.GetAmmo(ammoType) looks up ammo count in inventory
        // Returns 0 if player has no ammo of this type
        int remainingBullet = m_Owner.GetAmmo(ammoType);

        // Check if player has NO reserve ammo
        // Can't reload if there's nothing to reload with
        if (remainingBullet == 0)
        {
            // Player is completely out of ammo for this weapon

            // Check if weapon should be disabled when empty
            // DisabledOnEmpty is true for limited-use weapons (grenades)
            if (DisabledOnEmpty)
            {
                // Disable weapon GameObject
                // Makes weapon disappear from player's hands
                // Player will auto-switch to another weapon
                // 
                // USE CASE:
                // Player throws last grenade
                // Clip empty, reserve empty
                // Grenade weapon hides
                // Player switches to gun automatically
                gameObject.SetActive(false);
            }

            // Exit method - can't reload without ammo
            // Note: If DisabledOnEmpty is false, weapon stays visible but empty
            // Player sees empty weapon (good feedback that they're out)
            return;
        }

        // If we reach here:
        // - Weapon is in Idle state
        // - Clip is not full
        // - Player has reserve ammo
        // Reload can proceed!

        // ====================================================================
        // PLAY RELOAD SOUND EFFECT
        // ====================================================================

        // Check if reload sound is assigned
        // null check prevents error if sound not configured
        if (ReloadAudioClip != null)
        {
            // Randomize pitch for audio variety
            // Same as fire sound - prevents repetitive audio fatigue
            // Random.Range(0.7f, 1.0f) = pitch between 70% and 100% of original
            // 
            // VARIATION EXAMPLES:
            // Reload 1: pitch = 0.88 (slightly lower)
            // Reload 2: pitch = 0.95 (almost normal)
            // Reload 3: pitch = 0.73 (noticeably lower)
            // Makes each reload sound slightly different
            m_Source.pitch = Random.Range(0.7f, 1.0f);

            // Play reload sound once
            // PlayOneShot allows sound to complete even if weapon fires again
            // Won't be interrupted by fire sounds
            // 
            // SOUND DESIGN:
            // Reload sounds are often longer (1-2 seconds)
            // Include: magazine out, new mag in, bolt rack, etc.
            // Should match timing of reload animation
            m_Source.PlayOneShot(ReloadAudioClip);
        }

        // ====================================================================
        // CALCULATE AMMO TRANSFER AMOUNT
        // ====================================================================

        // Calculate how much ammo to move from reserve to clip
        // Two constraints:
        // 1. Can't take more than player has in reserve
        // 2. Can't load more than clip can hold
        // 
        // FORMULA:
        // chargeInClip = minimum of (reserve ammo, remaining clip space)
        // 
        // clipSize - m_ClipContent = empty space in clip
        // 
        // EXAMPLES:
        // 
        // Scenario A - Partial clip, plenty of reserve:
        // clipSize = 30, m_ClipContent = 15, reserve = 120
        // Empty space = 30 - 15 = 15
        // chargeInClip = Min(120, 15) = 15
        // Result: Fill clip to full (15 + 15 = 30)
        // 
        // Scenario B - Empty clip, limited reserve:
        // clipSize = 30, m_ClipContent = 0, reserve = 10
        // Empty space = 30 - 0 = 30
        // chargeInClip = Min(10, 30) = 10
        // Result: Partial fill (0 + 10 = 10)
        // 
        // Scenario C - Nearly full clip:
        // clipSize = 30, m_ClipContent = 28, reserve = 50
        // Empty space = 30 - 28 = 2
        // chargeInClip = Min(50, 2) = 2
        // Result: Top off clip (28 + 2 = 30)
        // 
        // Mathf.Min returns smaller of two values
        // Ensures we never exceed either constraint
        int chargeInClip = Mathf.Min(remainingBullet, clipSize - m_ClipContent);

        // ====================================================================
        // UPDATE WEAPON STATE
        // ====================================================================

        // Set weapon state to Reloading
        // Happens immediately (before animation frame advances)
        // Prevents firing or reloading again until state returns to Idle
        // 
        // COMMENT FROM ORIGINAL CODE:
        // "the state will only change next frame, so we set it right now."
        // Animator updates its state during update cycle
        // But we need state changed NOW to prevent invalid actions
        // Manual state change ensures correct behavior
        m_CurrentState = WeaponState.Reloading;

        // ====================================================================
        // ADD AMMO TO CLIP
        // ====================================================================

        // Add calculated ammo amount to clip
        // += adds to current value
        // 
        // EXAMPLES (from scenarios above):
        // A: m_ClipContent = 15 + 15 = 30 (full)
        // B: m_ClipContent = 0 + 10 = 10 (partial)
        // C: m_ClipContent = 28 + 2 = 30 (full)
        m_ClipContent += chargeInClip;

        // Update custom ammo display if weapon has one
        // Shows new clip count after reload
        if (AmmoDisplay)
            AmmoDisplay.UpdateAmount(m_ClipContent, clipSize);

        // ====================================================================
        // TRIGGER RELOAD ANIMATION
        // ====================================================================

        // Tell animator to play reload animation
        // SetTrigger activates "reload" trigger parameter
        // Animator transitions to reload state
        // 
        // ANIMATION SHOULD INCLUDE:
        // - Magazine ejection (old mag falls out)
        // - Hand reaching to belt/pocket for new mag
        // - New magazine insertion
        // - Bolt/slide action
        // - Return to ready position
        // 
        // Animation length should match reloadTime value
        // Speed was adjusted in Selected() method
        m_Animator.SetTrigger("reload");

        // ====================================================================
        // DEDUCT AMMO FROM RESERVE
        // ====================================================================

        // Subtract ammo from player's reserve inventory
        // Negative value means subtract (ChangeAmmo can add or subtract)
        // 
        // EXAMPLES:
        // chargeInClip = 15, call ChangeAmmo(ammoType, -15)
        // Reserve: 120 → 105
        // 
        // chargeInClip = 10, call ChangeAmmo(ammoType, -10)
        // Reserve: 10 → 0
        // 
        // ChangeAmmo handles:
        // - Updating inventory dictionary
        // - Clamping to valid range [0, 999]
        // - Updating UI displays
        m_Owner.ChangeAmmo(ammoType, -chargeInClip);

        // ====================================================================
        // UPDATE UI DISPLAY
        // ====================================================================

        // Update weapon info UI with new clip count
        // Shows updated ammo like "30/105" instead of old "15/120"
        // Gives immediate feedback that reload occurred
        WeaponInfoUI.Instance.UpdateClipInfo(this);

        // After Reload() completes:
        // - State set to Reloading (prevents other actions)
        // - Clip refilled from reserve
        // - Reserve decreased by amount moved to clip
        // - Reload animation playing
        // - Reload sound playing
        // - UI updated with new values
        // 
        // Weapon will return to Idle state when animation completes
        // (detected and handled in UpdateControllerState method)
        // Then player can fire or reload again
    }

    // ========================================================================
    // UPDATE - Unity lifecycle method called every frame
    // ========================================================================

    /// <summary>
    /// Called once per frame by Unity
    /// Handles per-frame updates:
    /// - Player state synchronization
    /// - Fire rate timer countdown
    /// - Bullet trail animation
    /// Runs at framerate speed (typically 60 times per second)
    /// </summary>
    void Update()
    {
        // ====================================================================
        // UPDATE WEAPON STATE AND HANDLE INPUT
        // ====================================================================

        // Call state management method
        // UpdateControllerState handles:
        // - Animator state → WeaponState synchronization
        // - Player movement data → animator parameters
        // - Trigger input → firing logic
        // - Auto-reload when clip empties
        // 
        // This is called first because state affects everything else
        // Must know current state before doing other updates
        UpdateControllerState();

        // ====================================================================
        // FIRE RATE TIMER COUNTDOWN
        // ====================================================================

        // Check if fire rate timer is active (> 0)
        // Timer prevents firing faster than fire rate allows
        if (m_ShotTimer > 0)
        {
            // Decrement timer by frame time
            // Time.deltaTime = seconds since last frame
            // 
            // EXAMPLE AT 60 FPS:
            // Time.deltaTime ≈ 0.0167 seconds (1/60)
            // Frame 1: m_ShotTimer = 0.5 - 0.0167 = 0.4833
            // Frame 2: m_ShotTimer = 0.4833 - 0.0167 = 0.4666
            // ... (continues for ~30 frames)
            // Frame 30: m_ShotTimer = 0.0167 - 0.0167 = 0
            // 
            // After 0.5 seconds (30 frames at 60fps):
            // Timer reaches 0, weapon can fire again
            // 
            // FRAME RATE INDEPENDENCE:
            // At 30 FPS: deltaTime ≈ 0.0333, takes ~15 frames
            // At 120 FPS: deltaTime ≈ 0.0083, takes ~60 frames
            // But total time is always 0.5 seconds real-time
            m_ShotTimer -= Time.deltaTime;
        }
        // When timer reaches 0 or below, weapon can fire again
        // Check in Fire() method validates timer <= 0

        // ====================================================================
        // BULLET TRAIL ANIMATION
        // ====================================================================

        // Declare array to hold trail positions
        // Array size 2 = start position and end position
        // Used to get and set LineRenderer positions
        // Reused for each trail to avoid allocation
        Vector3[] pos = new Vector3[2];

        // Loop through all active bullet trails
        // Each trail needs to:
        // 1. Move forward (animated motion)
        // 2. Count down lifetime
        // 3. Be removed when expired
        // 
        // IMPORTANT LOOP NOTE:
        // We might remove items during iteration
        // When we remove item at index i, next item shifts to index i
        // So we decrement i (i--) after removal to check same index again
        for (int i = 0; i < m_ActiveTrails.Count; ++i)
        {
            // Get reference to current trail
            // Direct reference (not copy) so modifications affect list item
            var activeTrail = m_ActiveTrails[i];

            // ================================================================
            // GET CURRENT TRAIL POSITIONS
            // ================================================================

            // Get current start and end positions of trail
            // GetPositions(array) fills provided array with LineRenderer points
            // pos[0] = start point
            // pos[1] = end point
            // 
            // WHY GET POSITIONS:
            // We need current positions to calculate new positions
            // Trail moves forward each frame
            activeTrail.renderer.GetPositions(pos);

            // ================================================================
            // UPDATE TRAIL LIFETIME
            // ================================================================

            // Decrement remaining time by frame duration
            // Trail fades out over 0.3 seconds (set in RaycastShot)
            // 
            // EXAMPLE:
            // Initial: remainingTime = 0.3
            // Frame 1: remainingTime = 0.3 - 0.0167 = 0.2833
            // Frame 2: remainingTime = 0.2833 - 0.0167 = 0.2666
            // ... continues for ~18 frames
            // Frame 18: remainingTime = 0.0167 - 0.0167 = 0
            // 
            // When reaches 0, trail is removed (see below)
            activeTrail.remainingTime -= Time.deltaTime;

            // ================================================================
            // ANIMATE TRAIL FORWARD
            // ================================================================

            // Move both trail points forward in direction of travel
            // Creates illusion of bullet streak moving through space
            // 
            // FORMULA: newPosition = currentPosition + direction * speed * time
            // 
            // direction: Normalized vector (length 1) from RaycastShot
            // 50.0f: Speed in units per second (how fast trail moves)
            // Time.deltaTime: Frame time for smooth animation
            // 
            // EXAMPLE CALCULATION AT 60 FPS:
            // direction = (0, 0, 1) pointing forward
            // deltaTime = 0.0167 seconds
            // Movement = (0,0,1) * 50 * 0.0167 = (0, 0, 0.835)
            // Trail moves 0.835 units forward this frame
            // 
            // AT 60 FPS:
            // 50 units/sec * 0.0167 sec = 0.835 units per frame
            // Trail travels 50 units in 1 second
            // 
            // WHY MOVE BOTH POINTS:
            // Start and end move together
            // Trail length stays constant
            // Entire trail slides forward as a unit
            // Creates smooth bullet streak effect
            pos[0] += activeTrail.direction * 50.0f * Time.deltaTime;
            pos[1] += activeTrail.direction * 50.0f * Time.deltaTime;

            // Apply new positions to LineRenderer
            // SetPositions updates the visual trail
            // Trail now drawn at new forward position
            m_ActiveTrails[i].renderer.SetPositions(pos);

            // ================================================================
            // CHECK FOR TRAIL EXPIRATION
            // ================================================================

            // Check if trail lifetime has expired
            // remainingTime <= 0 means trail should disappear
            if (m_ActiveTrails[i].remainingTime <= 0.0f)
            {
                // TRAIL HAS EXPIRED - Remove it

                // Disable the LineRenderer GameObject
                // Makes trail invisible
                // GameObject goes back into pool for reuse
                m_ActiveTrails[i].renderer.gameObject.SetActive(false);

                // Remove trail from active list
                // RemoveAt(i) removes item at index i
                // All items after i shift down by one index
                // List.Count decreases by 1
                // 
                // EXAMPLE:
                // Before: List has items at indices [0, 1, 2, 3]
                // RemoveAt(1) removes item 1
                // After: Items [0, 2, 3] become indices [0, 1, 2]
                // Item that WAS at index 2 is NOW at index 1
                m_ActiveTrails.RemoveAt(i);

                // Decrement loop counter
                // This is CRITICAL for correct iteration!
                // 
                // WHY DECREMENT:
                // We removed item at index i
                // Next item shifted down to index i
                // Loop will increment i (++i in for statement)
                // So we need to check index i again (which has new item)
                // Decrementing cancels out the increment
                // 
                // EXAMPLE ITERATION:
                // i = 1, remove item at 1, item at 2 moves to 1
                // i--, so i = 0
                // Loop does ++i, so i = 1 again
                // Now we check the item that moved to index 1
                // 
                // WITHOUT THIS:
                // We'd skip the item that moved down
                // Could miss trails that should be removed
                i--;
            }
        }

        // After Update() completes each frame:
        // - Weapon state synchronized with animator
        // - Player state reflected in animations
        // - Trigger input processed
        // - Fire rate timer counted down
        // - All bullet trails animated forward
        // - Expired trails removed and recycled
    }

    // ========================================================================
    // UPDATECONTROLLERSTATE - State machine and input handling
    // ========================================================================

    /// <summary>
    /// Synchronizes weapon state with animator and player
    /// Handles trigger input and state transitions
    /// Called every frame from Update()
    /// This is the "brain" that coordinates everything
    /// </summary>
    void UpdateControllerState()
    {
        // ====================================================================
        // SYNC PLAYER STATE TO ANIMATOR
        // ====================================================================
        // Pass player movement data to animator for responsive animations

        // Set animator's "speed" parameter to player's movement speed
        // m_Owner.Speed is normalized speed (0 = stopped, 1 = full speed)
        // Animator uses this to blend walk/run animations
        // 
        // ANIMATION BLENDING:
        // Speed 0: Idle animation (standing still)
        // Speed 0.5: Walk animation (slow movement)
        // Speed 1.0: Run animation (full speed)
        // Animator smoothly blends between states
        m_Animator.SetFloat("speed", m_Owner.Speed);

        // Set animator's "grounded" parameter to player's ground state
        // m_Owner.Grounded = true when player touching ground
        // m_Owner.Grounded = false when player in air (jumping/falling)
        // Animator uses this for jump/land animations
        // 
        // ANIMATION STATES:
        // Grounded = true: Normal weapon animations (walk, run, idle)
        // Grounded = false: Jump weapon animations (weapon bounces, different pose)
        m_Animator.SetBool("grounded", m_Owner.Grounded);

        // ====================================================================
        // READ ANIMATOR STATE
        // ====================================================================

        // Get current animator state information
        // GetCurrentAnimatorStateInfo(0) gets state of layer 0 (base layer)
        // Returns AnimatorStateInfo struct containing:
        // - shortNameHash: Integer hash of current state name
        // - normalizedTime: Progress through animation (0-1)
        // - length: Animation length in seconds
        // - etc.
        // 
        // LAYER 0:
        // Most animators use layer 0 as base layer
        // Contains idle, fire, reload animations
        // Higher layers (1, 2, etc.) for additive animations
        var info = m_Animator.GetCurrentAnimatorStateInfo(0);

        // ====================================================================
        // DETERMINE WEAPON STATE FROM ANIMATOR
        // ====================================================================
        // Animator state names → WeaponState enum

        // Declare variable for new weapon state
        // Will be assigned based on animator state
        WeaponState newState;

        // Check animator state and determine weapon state
        // info.shortNameHash is integer hash of state name
        // fireNameHash and reloadNameHash were calculated in field initialization
        // Hash comparison is very fast (integer comparison)
        // 
        // STATE DETECTION:
        if (info.shortNameHash == fireNameHash)
        {
            // Animator is in "fire" state
            // Weapon is currently firing
            // Fire animation is playing
            newState = WeaponState.Firing;
        }
        else if (info.shortNameHash == reloadNameHash)
        {
            // Animator is in "reload" state
            // Weapon is currently reloading
            // Reload animation is playing
            newState = WeaponState.Reloading;
        }
        else
        {
            // Animator is in any other state (idle, draw, etc.)
            // Consider weapon idle and ready to use
            // Can fire or reload from this state
            newState = WeaponState.Idle;
        }

        // ====================================================================
        // DETECT STATE TRANSITIONS
        // ====================================================================
        // Check if state changed since last frame

        // Compare new state to current state
        // If different, a transition occurred
        if (newState != m_CurrentState)
        {
            // STATE TRANSITION DETECTED

            // Store old state before changing
            // Need to know what we transitioned FROM
            // Used to trigger appropriate actions
            var oldState = m_CurrentState;

            // Update current state to new state
            // This changes the weapon's state for next frame
            // Other methods (like Fire) check m_CurrentState
            m_CurrentState = newState;

            // ================================================================
            // HANDLE FIRING COMPLETION
            // ================================================================
            // Special logic when transitioning FROM Firing state

            // Check if we just finished firing
            // oldState == WeaponState.Firing means we WERE firing
            // Now we're transitioning to different state (likely Idle)
            if (oldState == WeaponState.Firing)
            {
                // JUST FINISHED FIRING
                // Fire animation completed and returned to idle

                // Check if clip is now empty
                // If m_ClipContent == 0, we fired last bullet
                if (m_ClipContent == 0)
                {
                    // Clip is empty - automatically reload
                    // Reload() will handle:
                    // - Checking if reserve ammo exists
                    // - Playing reload animation
                    // - Transferring ammo from reserve to clip
                    // - Updating UI
                    // 
                    // AUTO-RELOAD BENEFITS:
                    // Player doesn't have to manually reload after emptying clip
                    // Smoother gameplay flow
                    // Still requires time (reload animation)
                    // Mimics real-world tactical reload behavior
                    Reload();
                }
            }

            // Could add more transition handlers here
            // Example: if (oldState == WeaponState.Reloading) { ... }
            // Currently only handling Firing → Other transitions
        }

        // ====================================================================
        // HANDLE TRIGGER INPUT AND FIRING
        // ====================================================================
        // Process player's trigger (fire button) input

        // Check if trigger is currently held down
        // triggerDown is set by Controller from Input.GetMouseButton(0)
        // Updated every frame in Controller.Update()
        if (triggerDown)
        {
            // TRIGGER IS HELD DOWN
            // Player wants to fire (or keep firing)

            // Branch based on trigger type
            if (triggerType == TriggerType.Manual)
            {
                // MANUAL/SEMI-AUTOMATIC MODE
                // One shot per trigger pull (pistol, shotgun)
                // 
                // PROBLEM TO SOLVE:
                // triggerDown stays true while button held
                // Without protection, Fire() would be called every frame
                // At 60 FPS, that's 60 shots per second!
                // 
                // SOLUTION:
                // Use m_ShotDone flag to track if we already fired

                // Check if we haven't fired yet this trigger press
                if (!m_ShotDone)
                {
                    // First frame of trigger press
                    // Haven't fired yet for this press

                    // Set flag to prevent firing again
                    // Now m_ShotDone = true
                    // Won't enter this block again until trigger released
                    m_ShotDone = true;

                    // Attempt to fire weapon
                    // Fire() does its own validation
                    // Might not actually fire if:
                    // - Not in Idle state
                    // - Fire rate timer not expired
                    // - Clip is empty
                    Fire();
                }
                // If m_ShotDone is true, we do nothing
                // Weapon already fired for this trigger press
                // Player must release and press again to fire next shot

                // RESET MECHANISM:
                // triggerDown property setter resets m_ShotDone = false
                // This happens when Controller sets triggerDown = false
                // So releasing trigger prepares weapon for next shot
            }
            else // triggerType == TriggerType.Auto
            {
                // AUTOMATIC MODE
                // Continuous fire while trigger held (machine gun, rifle)
                // 
                // SIMPLE LOGIC:
                // Just call Fire() every frame while trigger held
                // Fire() handles rate limiting via m_ShotTimer
                // Fire() won't actually fire if timer hasn't expired
                // 
                // EXAMPLE AT FIRE RATE 0.1:
                // Frame 1: Trigger pressed, Fire() succeeds, timer = 0.1
                // Frame 2-6: Fire() called but returns early (timer > 0)
                // Frame 7: Timer reaches 0, Fire() succeeds again, timer = 0.1
                // Repeat...
                // 
                // RESULT:
                // Weapon fires at constant rate (0.1 sec intervals)
                // As long as trigger held and ammo available
                // 
                // Fire rate timer provides automatic rate limiting
                // No need for m_ShotDone flag in auto mode
                Fire();
            }
        }
        // If triggerDown is false, player isn't trying to fire
        // No action needed

        // After UpdateControllerState() completes:
        // - Animator parameters updated with player state
        // - Weapon state synchronized with animator
        // - State transitions detected and handled
        // - Auto-reload triggered if appropriate
        // - Trigger input processed and firing handled
    }

    // ========================================================================
    // GETCORRECTEDMUZZLEPLACE - Camera FOV correction for visual effects
    // ========================================================================

    /// <summary>
    /// Calculates corrected muzzle position accounting for camera FOV differences
    /// Ensures visual effects (trails, projectiles) appear aligned with visible muzzle
    /// This is advanced math solving a specific visual problem in FPS games
    /// </summary>
    /// <returns>Corrected world position where effects should spawn</returns>
    public Vector3 GetCorrectedMuzzlePlace()
    {
        // ====================================================================
        // THE PROBLEM THIS SOLVES
        // ====================================================================
        // 
        // FPS games typically use TWO cameras:
        // 1. Main Camera: Renders world (enemies, environment)
        //    FOV: 60-90 degrees (normal perspective)
        // 
        // 2. Weapon Camera: Renders ONLY weapon and arms
        //    FOV: 40-50 degrees (narrower FOV)
        //    Why? Prevents weapon clipping through walls
        //    Makes weapon look bigger/cooler
        // 
        // VISUAL MISALIGNMENT:
        // Muzzle flash (on weapon) rendered by Weapon Camera
        // Bullet trail (in world) rendered by Main Camera
        // Different FOVs cause different perspective projections
        // Muzzle position in Weapon Camera ≠ same position in Main Camera
        // Result: Trail appears disconnected from muzzle
        // 
        // SOLUTION:
        // Convert muzzle position through both camera spaces
        // Find equivalent position in Main Camera that aligns visually

        // ====================================================================
        // CONVERSION PROCESS
        // ====================================================================
        // Mathematical transformation through coordinate spaces:
        // World → Weapon View → Weapon Clip → Main Clip → Main View → World

        // Start with muzzle's world position
        // EndPoint.position is the Transform's position in world space
        // This is the actual 3D position of the muzzle in the game world
        Vector3 position = EndPoint.position;

        // ====================================================================
        // STEP 1: World Space → Weapon Camera Screen Space
        // ====================================================================
        // WorldToScreenPoint converts 3D world position to 2D screen position
        // Uses Weapon Camera's projection
        // 
        // RESULT:
        // position now in screen space (pixel coordinates)
        // Example: (960, 540, 5.0)
        // x=960: 960 pixels from left edge
        // y=540: 540 pixels from bottom edge
        // z=5.0: 5 units in front of camera (depth)
        // 
        // IMPORTANT:
        // Screen position is the same regardless of FOV
        // If muzzle appears at pixel (960, 540) in Weapon Camera
        // We want effect to appear at same screen pixel in Main Camera
        position = Controller.Instance.WeaponCamera.WorldToScreenPoint(position);

        // ====================================================================
        // STEP 2: Weapon Camera Screen → Main Camera World Space
        // ====================================================================
        // ScreenToWorldPoint converts 2D screen position back to 3D world
        // Uses Main Camera's projection
        // 
        // MAGIC HAPPENS HERE:
        // Same screen coordinates (960, 540, 5.0)
        // But interpreted through Main Camera's different FOV
        // Results in different world position
        // This new position LOOKS like it's at the muzzle from Main Camera's view
        // 
        // RESULT:
        // position is now corrected world position
        // Bullet trail spawned here will align with visible muzzle flash
        // Even though cameras have different FOVs
        position = Controller.Instance.MainCamera.ScreenToWorldPoint(position);

        // Return the corrected position
        // Use this for spawning:
        // - Bullet trails (LineRenderer start point)
        // - Projectiles (initial position)
        // - Impact effects if spawned from weapon
        // - Any effect that needs to align with muzzle
        return position;

        // ====================================================================
        // WHY THIS WORKS
        // ====================================================================
        // 
        // ANALOGY:
        // Imagine looking at your hand through two lenses:
        // - Narrow lens (Weapon Camera): Hand looks big and close
        // - Wide lens (Main Camera): Hand looks normal size
        // Both see the same screen position (e.g., center)
        // But hand is actually at different distances in each view
        // 
        // This function finds where hand "should be" in wide lens view
        // To match where it appears in narrow lens view
        // 
        // MATHEMATICAL EXPLANATION:
        // Different FOVs = different projection matrices
        // Screen position is invariant across cameras at same location
        // But world position for same screen position varies by projection
        // By converting screen→world with different projection:
        // We find equivalent world position that projects to same screen position
        // 
        // RESULT:
        // Effects in Main Camera appear aligned with muzzle in Weapon Camera
        // Player sees cohesive visual (bullet comes from gun barrel)
        // Not disconnected (bullet appears offset from barrel)
    }
}

// ============================================================================
// SEPARATE CLASSES - Attribute and base class for ammo system
// ============================================================================
// These classes support the weapon system but are defined separately
// ============================================================================

// ============================================================================
// AMMOTYPEATTRIBUTE - Custom property attribute for Inspector
// ============================================================================

/// <summary>
/// Custom attribute for marking integer fields as ammo type selectors
/// Triggers custom property drawer in Unity Editor
/// Converts integer field to user-friendly dropdown showing ammo names
/// </summary>
public class AmmoTypeAttribute : PropertyAttribute
{
    // EMPTY CLASS - No properties or methods needed
    // This is a "marker attribute"
    // Just its presence triggers special editor behavior
    // 
    // USAGE:
    // [AmmoType]
    // public int ammoType;
    // 
    // Unity Editor sees [AmmoType] attribute
    // Looks for CustomPropertyDrawer for AmmoTypeAttribute
    // Renders field using that drawer instead of default int field
    // 
    // BENEFITS:
    // Designer sees dropdown: "Pistol Ammo", "Rifle Ammo", etc.
    // Not confusing integer: 0, 1, 2, etc.
    // Prevents errors (typos in IDs)
    // Self-documenting (dropdown shows all available types)
}

// ============================================================================
// AMMODISPLAY - Abstract base class for custom ammo UI
// ============================================================================

/// <summary>
/// Abstract base class for weapon-specific ammo displays
/// Allows each weapon type to have custom UI representation
/// Examples: text counter, icon grid, progress bar, grenade icons
/// </summary>
public abstract class AmmoDisplay : MonoBehaviour
{
    // ABSTRACT METHOD - Must be implemented by derived classes
    // No implementation here (no method body)
    // 
    /// <summary>
    /// Updates the display with current ammo information
    /// Called when clip content changes (fire, reload)
    /// </summary>
    /// <param name="current">Current ammo in clip</param>
    /// <param name="max">Maximum clip capacity</param>
    public abstract void UpdateAmount(int current, int max);

    // IMPLEMENTATION EXAMPLES:
    // 
    // Class TextAmmoDisplay : AmmoDisplay
    // {
    //     public Text ammoText;
    //     public override void UpdateAmount(int current, int max)
    //     {
    //         ammoText.text = $"{current}/{max}";
    //     }
    // }
    // 
    // Class IconGridAmmoDisplay : AmmoDisplay
    // {
    //     public Image[] bulletIcons;
    //     public override void UpdateAmount(int current, int max)
    //     {
    //         for(int i = 0; i < bulletIcons.Length; i++)
    //         {
    //             bulletIcons[i].enabled = (i < current);
    //         }
    //     }
    // }
    // 
    // BENEFITS:
    // - Flexibility: Each weapon can have unique display style
    // - Extensibility: Add new display types without changing Weapon.cs
    // - Polymorphism: Weapon.cs calls UpdateAmount() without knowing concrete type
}

// ============================================================================
// UNITY EDITOR CODE - Only included in Editor builds
// ============================================================================
// Everything from here down only exists in Unity Editor
// Not included in final game build
// Used for custom Inspector UI and property drawers
// ============================================================================

#if UNITY_EDITOR

// ============================================================================
// AMMOTYPEDRAWER - Custom property drawer for AmmoType fields
// ============================================================================

/// <summary>
/// Custom property drawer that renders integer AmmoType fields as dropdowns
/// Replaces default integer field with dropdown showing ammo names
/// Makes Inspector more user-friendly for designers
/// </summary>
[CustomPropertyDrawer(typeof(AmmoTypeAttribute))]
public class AmmoTypeDrawer : PropertyDrawer
{
    // ========================================================================
    // ONGUI - Renders custom field in Inspector
    // ========================================================================

    /// <summary>
    /// Called by Unity Editor to draw the property field
    /// Replaces default int field with custom dropdown
    /// </summary>
    /// <param name="position">Rectangle area where field should be drawn</param>
    /// <param name="property">SerializedProperty containing the integer value</param>
    /// <param name="label">Label to display ("Ammo Type")</param>
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        // ====================================================================
        // GET AMMO DATABASE
        // ====================================================================

        // Access global ammo database from GameDatabase singleton
        // GameDatabase.Instance.ammoDatabase contains all ammo type definitions
        // Each entry has: id (int), name (string), icon (Sprite)
        AmmoDatabase ammoDB = GameDatabase.Instance.ammoDatabase;

        // ====================================================================
        // VALIDATE DATABASE EXISTS AND HAS ENTRIES
        // ====================================================================

        // Check if database is null or empty
        // Prevents errors when database not set up yet
        if (ammoDB.entries == null || ammoDB.entries.Length == 0)
        {
            // DATABASE NOT CONFIGURED - Show error message

            // EditorGUI.HelpBox displays message box in Inspector
            // position: Where to draw the box
            // message: Error text to display
            // MessageType.Error: Red error icon and styling
            // 
            // BENEFIT:
            // Designer immediately sees they need to configure database
            // Not cryptic error in console
            // Clear actionable message
            EditorGUI.HelpBox(position, "Please define at least 1 ammo type in the Game Database", MessageType.Error);
        }
        else
        {
            // DATABASE EXISTS - Render dropdown

            // ================================================================
            // FIND CURRENT SELECTION
            // ================================================================

            // Get current value stored in property
            // property.intValue is the integer ID currently stored
            // Example: 1 (represents "Rifle Ammo")
            int currentID = property.intValue;

            // Initialize current index to -1 (not found)
            // Will be set to array index of matching entry
            int currentIdx = -1;

            // ================================================================
            // BUILD DROPDOWN OPTIONS ARRAY
            // ================================================================

            // COMMENT FROM ORIGINAL CODE:
            // "this is pretty ineffective, maybe find a way to cache that if 
            // prove to take too much time"
            // 
            // OPTIMIZATION NOTE:
            // This rebuilds array every time Inspector updates
            // Could cache result, but only runs in Editor so not critical
            // String arrays are cheap, database rarely changes

            // Create array to hold ammo names for dropdown
            // Size matches number of entries in database
            string[] names = new string[ammoDB.entries.Length];

            // Loop through all ammo entries
            for (int i = 0; i < ammoDB.entries.Length; ++i)
            {
                // Copy entry name to names array
                // Example: names[0] = "Pistol Ammo"
                // Example: names[1] = "Rifle Ammo"
                names[i] = ammoDB.entries[i].name;

                // Check if this entry's ID matches current value
                // If match found, store this index as current selection
                if (ammoDB.entries[i].id == currentID)
                    currentIdx = i;
            }
            // After loop:
            // names[] contains all ammo type names for dropdown
            // currentIdx is index of currently selected entry (-1 if not found)

            // ================================================================
            // DETECT VALUE CHANGES
            // ================================================================

            // Begin change check
            // EditorGUI.BeginChangeCheck() starts monitoring for changes
            // Any GUI element drawn between Begin and End is monitored
            // EditorGUI.EndChangeCheck() returns true if value changed
            EditorGUI.BeginChangeCheck();

            // Draw popup dropdown field
            // EditorGUI.Popup displays dropdown with options
            // 
            // PARAMETERS:
            // position: Where to draw the dropdown
            // "Ammo Type": Label text
            // currentIdx: Currently selected index in dropdown
            // names: Array of strings to show in dropdown
            // 
            // RETURN VALUE:
            // Index of selected item after user interaction
            // If user changes selection, returns new index
            // If user doesn't change, returns currentIdx
            // 
            // EXAMPLE:
            // names = ["Pistol Ammo", "Rifle Ammo", "Shotgun Ammo"]
            // currentIdx = 1 ("Rifle Ammo" is selected)
            // User clicks dropdown, selects "Shotgun Ammo"
            // Returns 2 (index of "Shotgun Ammo")
            int idx = EditorGUI.Popup(position, "Ammo Type", currentIdx, names);

            // Check if value changed
            // EndChangeCheck() returns true if dropdown selection changed
            if (EditorGUI.EndChangeCheck())
            {
                // USER CHANGED SELECTION - Update property

                // Get ID of newly selected entry
                // idx is array index, ammoDB.entries[idx].id is the ID
                // Example: User selected "Shotgun Ammo" (index 2)
                // ammoDB.entries[2].id might be 2
                // Set property.intValue to 2
                // 
                // This updates the serialized field in the Inspector
                // Weapon.ammoType now stores the new ID
                // Change is saved to the asset/scene
                property.intValue = ammoDB.entries[idx].id;
            }

            // After OnGUI completes:
            // - Dropdown displayed with current selection
            // - If user changed selection, property updated
            // - Change automatically saved by Unity's serialization
        }
    }
}

// ============================================================================
// WEAPONEDITOR - Custom Inspector for Weapon component
// ============================================================================

/// <summary>
/// Custom Inspector for Weapon component
/// Provides better organization and conditional field display
/// Shows/hides fields based on weapon configuration
/// Example: ProjectilePrefab only shows for projectile weapons
/// </summary>
[CustomEditor(typeof(Weapon))]
public class WeaponEditor : Editor
{
    // ========================================================================
    // SERIALIZED PROPERTY REFERENCES
    // ========================================================================
    // Cache references to all serialized properties
    // Used to draw fields in custom layout
    // More efficient than FindProperty() in OnInspectorGUI()

    SerializedProperty m_TriggerTypeProp;
    SerializedProperty m_WeaponTypeProp;
    SerializedProperty m_FireRateProp;
    SerializedProperty m_ReloadTimeProp;
    SerializedProperty m_ClipSizeProp;
    SerializedProperty m_DamageProp;
    SerializedProperty m_AmmoTypeProp;
    SerializedProperty m_ProjectilePrefabProp;
    SerializedProperty m_ProjectileLaunchForceProp;
    SerializedProperty m_EndPointProp;
    SerializedProperty m_AdvancedSettingsProp;
    SerializedProperty m_FireAnimationClipProp;
    SerializedProperty m_ReloadAnimationClipProp;
    SerializedProperty m_FireAudioClipProp;
    SerializedProperty m_ReloadAudioClipProp;
    SerializedProperty m_PrefabRayTrailProp;
    SerializedProperty m_AmmoDisplayProp;
    SerializedProperty m_DisabledOnEmpty;

    // ========================================================================
    // ONENABLE - Called when Inspector is opened
    // ========================================================================

    /// <summary>
    /// Called when this Inspector is enabled
    /// Caches all SerializedProperty references
    /// One-time setup for efficient Inspector rendering
    /// </summary>
    void OnEnable()
    {
        // Find and cache all serialized property references
        // serializedObject is the Weapon component being inspected
        // FindProperty("name") gets SerializedProperty for field "name"
        // 
        // WHY CACHE:
        // FindProperty is somewhat expensive
        // OnInspectorGUI is called many times per second (on repaint)
        // Cache once in OnEnable, reuse in OnInspectorGUI
        // Much more efficient

        m_TriggerTypeProp = serializedObject.FindProperty("triggerType");
        m_WeaponTypeProp = serializedObject.FindProperty("weaponType");
        m_FireRateProp = serializedObject.FindProperty("fireRate");
        m_ReloadTimeProp = serializedObject.FindProperty("reloadTime");
        m_ClipSizeProp = serializedObject.FindProperty("clipSize");
        m_DamageProp = serializedObject.FindProperty("damage");
        m_AmmoTypeProp = serializedObject.FindProperty("ammoType");
        m_ProjectilePrefabProp = serializedObject.FindProperty("projectilePrefab");
        m_ProjectileLaunchForceProp = serializedObject.FindProperty("projectileLaunchForce");
        m_EndPointProp = serializedObject.FindProperty("EndPoint");
        m_AdvancedSettingsProp = serializedObject.FindProperty("advancedSettings");
        m_FireAnimationClipProp = serializedObject.FindProperty("FireAnimationClip");
        m_ReloadAnimationClipProp = serializedObject.FindProperty("ReloadAnimationClip");
        m_FireAudioClipProp = serializedObject.FindProperty("FireAudioClip");
        m_ReloadAudioClipProp = serializedObject.FindProperty("ReloadAudioClip");
        m_PrefabRayTrailProp = serializedObject.FindProperty("PrefabRayTrail");
        m_AmmoDisplayProp = serializedObject.FindProperty("AmmoDisplay");
        m_DisabledOnEmpty = serializedObject.FindProperty("DisabledOnEmpty");
    }

    // ========================================================================
    // ONINSPECTORGUI - Renders custom Inspector layout
    // ========================================================================

    /// <summary>
    /// Called to render the Inspector GUI
    /// Creates custom layout with conditional field display
    /// Provides better organization than default Inspector
    /// </summary>
    public override void OnInspectorGUI()
    {
        // Update serialized object
        // Reads latest values from the component
        // Must call before drawing any fields
        // Ensures Inspector shows current values
        serializedObject.Update();

        // ====================================================================
        // DRAW CORE WEAPON PROPERTIES
        // ====================================================================
        // Draw basic weapon configuration fields
        // These are always visible regardless of weapon type

        // EditorGUILayout.PropertyField draws a property with default layout
        // Automatically handles the correct field type:
        // - Enum fields show as dropdowns
        // - Float fields show as number fields
        // - Reference fields show as object pickers
        // 
        // BENEFIT:
        // Don't need to manually create each field type
        // Unity handles it automatically based on property type

        EditorGUILayout.PropertyField(m_TriggerTypeProp);
        EditorGUILayout.PropertyField(m_WeaponTypeProp);
        EditorGUILayout.PropertyField(m_FireRateProp);
        EditorGUILayout.PropertyField(m_ReloadTimeProp);
        EditorGUILayout.PropertyField(m_ClipSizeProp);
        EditorGUILayout.PropertyField(m_DamageProp);
        EditorGUILayout.PropertyField(m_AmmoTypeProp);

        // ====================================================================
        // CONDITIONAL FIELDS - PROJECTILE TYPE
        // ====================================================================
        // Only show projectile-specific fields for projectile weapons

        // Check weapon type
        // m_WeaponTypeProp.intValue is the enum value as integer
        // (int)Weapon.WeaponType.Projectile converts enum to integer
        // If they match, weapon is projectile type
        if (m_WeaponTypeProp.intValue == (int)Weapon.WeaponType.Projectile)
        {
            // PROJECTILE WEAPON - Show projectile fields

            // Draw projectile prefab field
            // Designer drags grenade/rocket prefab here
            EditorGUILayout.PropertyField(m_ProjectilePrefabProp);

            // Draw launch force field
            // Designer sets how fast projectile flies
            EditorGUILayout.PropertyField(m_ProjectileLaunchForceProp);
        }
        // If weapon is raycast type, these fields are hidden
        // Keeps Inspector clean - only show relevant options

        // ====================================================================
        // DRAW REMAINING PROPERTIES
        // ====================================================================
        // Fields that are relevant for all weapon types

        EditorGUILayout.PropertyField(m_EndPointProp);

        // Draw advanced settings with custom label and foldout
        // true parameter makes it foldable (expandable section)
        // new GUIContent("Advance Settings") changes the label text
        // Note: "Advance" appears to be typo, should be "Advanced"
        EditorGUILayout.PropertyField(m_AdvancedSettingsProp, new GUIContent("Advance Settings"), true);

        EditorGUILayout.PropertyField(m_FireAnimationClipProp);
        EditorGUILayout.PropertyField(m_ReloadAnimationClipProp);
        EditorGUILayout.PropertyField(m_FireAudioClipProp);
        EditorGUILayout.PropertyField(m_ReloadAudioClipProp);

        // ====================================================================
        // CONDITIONAL FIELDS - RAYCAST TYPE
        // ====================================================================
        // Only show ray trail field for raycast weapons

        // Check if weapon is raycast type
        if (m_WeaponTypeProp.intValue == (int)Weapon.WeaponType.Raycast)
        {
            // RAYCAST WEAPON - Show bullet trail field

            // Draw ray trail prefab field
            // Designer drags LineRenderer prefab here
            // Creates bullet streak visual effect
            EditorGUILayout.PropertyField(m_PrefabRayTrailProp);
        }
        // Projectile weapons don't need trails (projectile itself is visible)

        // Draw final fields (always visible)
        EditorGUILayout.PropertyField(m_AmmoDisplayProp);
        EditorGUILayout.PropertyField(m_DisabledOnEmpty);

        // ====================================================================
        // APPLY CHANGES
        // ====================================================================

        // Apply any modifications made in Inspector
        // Writes changes back to the component
        // Must call after drawing fields
        // Ensures changes are saved
        // 
        // WITHOUT THIS:
        // Changes made in Inspector would be lost
        // Component would revert to old values
        // 
        // WITH THIS:
        // Changes are saved to asset/scene
        // Undo/redo works correctly
        serializedObject.ApplyModifiedProperties();

        // After OnInspectorGUI completes:
        // - Inspector rendered with custom layout
        // - Only relevant fields shown based on weapon type
        // - All changes saved to component
        // - Undo/redo system updated
    }
}

#endif // UNITY_EDITOR

