// ============================================================================
// USING STATEMENTS - Import functionality from other code libraries
// ============================================================================

// Import generic collection types like Dictionary<TKey, TValue> and Queue<T>
// Dictionary: Key-value pairs for fast lookups (O(1) average time complexity)
// Queue: First-In-First-Out (FIFO) data structure for object pooling
using System.Collections.Generic;

// Import System.Diagnostics for debugging utilities (Stopwatch, Debug assertions)
// This namespace contains the default System.Diagnostics.Debug class
using System.Diagnostics;

// Import UnityEngine namespace - core Unity API for GameObjects, Components, etc.
using UnityEngine;

// ALIAS: Resolve naming conflict between System.Diagnostics.Debug and UnityEngine.Debug
// This line says "when I write 'Debug' in this file, use UnityEngine.Debug"
// Without this, the compiler wouldn't know which Debug class we mean
using Debug = UnityEngine.Debug;

// ALIAS: Resolve potential ambiguity with System.Object
// This line says "when I write 'Object' in this file, use UnityEngine.Object"
// UnityEngine.Object is the base class for all Unity objects (GameObject, Component, etc.)
// System.Object is C#'s universal base class for all .NET objects
using Object = UnityEngine.Object;

// ============================================================================
// POOLSYSTEM CLASS - Ring buffer-style object pooling for performance
// ============================================================================
//
// PURPOSE:
// Object pooling is a performance optimization technique that reuses objects
// instead of constantly creating (Instantiate) and destroying (Destroy) them.
//
// WHY POOLING MATTERS:
// - Instantiate() is expensive: Memory allocation, initialization, serialization
// - Destroy() is expensive: Garbage collection, memory cleanup, callbacks
// - Both cause CPU spikes and can lead to frame rate stuttering
// - Pooling trades memory (keeping objects around) for performance (reusing them)
//
// RING BUFFER DESIGN:
// Unlike traditional pools where you "return" objects, this uses a simpler approach:
// 1. All instances stay in a Queue (FIFO: First In, First Out)
// 2. GetInstance(): Dequeue object from front, activate it, enqueue at back
// 3. No explicit "return" needed - object automatically cycles to back of queue
// 4. When object's lifetime ends (auto-disables), it's already queued for reuse
//
// BEST USES:
// - Projectiles (bullets that disable after a few seconds)
// - Particle effects (explosions that auto-disable when finished)
// - Audio sources (sound effects that stop after playing)
// - Temporary UI elements (damage numbers, notifications)
//
// NOT SUITABLE FOR:
// - Long-lived objects (enemies, players, persistent items)
// - Objects that need explicit cleanup logic
// - Objects with complex state that's hard to reset
//
// ARCHITECTURE:
// This is a Singleton pattern - only one PoolSystem exists globally
// Other systems access it via PoolSystem.Instance
//
// FPS KIT USAGE:
// - Target.cs: Pools destruction particle effects (DestroyedEffect)
// - Weapon.cs: Pools projectiles and muzzle flash effects
// - WorldAudioPool: Pools AudioSources for 3D positioned sound effects
// - Any script can pool any prefab by calling InitPool() first
//
// ============================================================================

// Simple ring buffer style pool system. You don't need to return the object to the pool, it just get pushed back to the
// end of the queue again. So use it only for object with short lifespan (projectile, particle system that auto disable)
public class PoolSystem : MonoBehaviour
{
    // ========================================================================
    // SINGLETON INSTANCE - Global access point to the PoolSystem
    // ========================================================================

    /// <summary>
    /// Static singleton instance - allows any script to access the pool system
    /// Access via: PoolSystem.Instance.GetInstance(prefab)
    /// 
    /// SINGLETON PATTERN:
    /// - Static: Belongs to the class itself, not individual instances
    /// - One instance: Only one PoolSystem exists in the entire game
    /// - Global access: Any script can reach it without needing a reference
    /// 
    /// PROPERTY DETAILS:
    /// - get: Public - anyone can read the Instance
    /// - set: Private - only PoolSystem can modify the Instance
    /// 
    /// Auto-implemented property syntax: { get; private set; }
    /// Compiler automatically creates a hidden backing field
    /// </summary>
    public static PoolSystem Instance { get; private set; }

    // ========================================================================
    // STATIC FACTORY METHOD - Creates the PoolSystem GameObject
    // ========================================================================

    /// <summary>
    /// Factory method to create the PoolSystem singleton instance
    /// Called once during game initialization (usually in a bootstrap scene)
    /// 
    /// STATIC METHOD:
    /// - Can be called without an instance: PoolSystem.Create()
    /// - Typically called from GameManager, Bootstrapper, or Application.OnLoad
    /// 
    /// WHAT THIS METHOD DOES:
    /// 1. Creates a new GameObject named "PoolSystem" in the scene hierarchy
    /// 2. Adds a PoolSystem component to that GameObject
    /// 3. Stores the component reference in the Instance property
    /// 
    /// LIFECYCLE:
    /// - Call this ONCE at game start before any pooling is needed
    /// - If called multiple times, creates duplicate PoolSystems (usually a bug)
    /// - Consider adding DontDestroyOnLoad() if the pool should persist across scenes
    /// 
    /// DESIGN NOTE:
    /// This could be improved with null-checking:
    /// if (Instance == null) { /* create */ }
    /// Or by making PoolSystem auto-create itself (lazy initialization in Awake)
    /// </summary>
    public static void Create()
    {
        // Create a new empty GameObject in the scene
        // "PoolSystem" is the name visible in the Unity Hierarchy window
        // This helps developers identify the object during debugging
        GameObject obj = new GameObject("PoolSystem");

        // AddComponent<T>() attaches a new component of type T to the GameObject
        // Returns the newly created component instance
        // This line both creates the PoolSystem component AND assigns it to Instance
        Instance = obj.AddComponent<PoolSystem>();
    }

    // ========================================================================
    // POOL STORAGE - Dictionary mapping prefabs to their object queues
    // ========================================================================

    /// <summary>
    /// Dictionary that stores all object pools
    /// 
    /// STRUCTURE:
    /// - Key (Object): The original prefab (template object)
    /// - Value (Queue<Object>): Queue of instantiated clones of that prefab
    /// 
    /// EXAMPLE DATA:
    /// {
    ///     [BulletPrefab] => Queue: { bullet1, bullet2, bullet3, ... }
    ///     [ExplosionPrefab] => Queue: { explosion1, explosion2, explosion3, ... }
    ///     [MuzzleFlashPrefab] => Queue: { flash1, flash2, flash3, ... }
    /// }
    /// 
    /// WHY DICTIONARY?
    /// - Fast lookup: O(1) average time to find the queue for a given prefab
    /// - Flexible: Can pool any number of different prefab types
    /// - Organized: Each prefab's instances are isolated in their own queue
    /// 
    /// WHY QUEUE?
    /// - FIFO behavior: First object added is first object reused
    /// - Simple: Just Enqueue() and Dequeue() operations
    /// - Ring buffer: By dequeuing and immediately re-enqueuing, objects cycle
    /// 
    /// MEMORY CONSIDERATIONS:
    /// - This dictionary persists for the lifetime of PoolSystem
    /// - All pooled objects remain in memory even when inactive
    /// - Inactive objects consume less CPU but still use memory
    /// - Trade-off: Higher memory usage for better performance
    /// 
    /// PRIVATE ACCESS:
    /// - m_ prefix: Unity naming convention for private fields
    /// - Only PoolSystem methods can modify this dictionary
    /// - External scripts access pools through InitPool() and GetInstance()
    /// </summary>
    Dictionary<Object, Queue<Object>> m_Pools = new Dictionary<Object, Queue<Object>>();

    // ========================================================================
    // INITPOOL - Pre-allocates a pool of objects for a given prefab
    // ========================================================================

    /// <summary>
    /// Initializes an object pool for a specific prefab
    /// Must be called BEFORE GetInstance() for that prefab
    /// 
    /// PARAMETERS:
    /// - prefab: The template object to clone (GameObject, Component, ParticleSystem, etc.)
    /// - size: How many instances to pre-create (pool capacity)
    /// 
    /// WHEN TO CALL:
    /// - During scene initialization (Start method of game systems)
    /// - Before the first time you'll need an instance of this prefab
    /// - Example: Target.Start() calls InitPool(DestroyedEffect, 16)
    /// 
    /// POOL SIZE GUIDELINES:
    /// - Too small: Pool runs out, creates new instances at runtime (defeats purpose)
    /// - Too large: Wastes memory on unused objects
    /// - Rule of thumb: Maximum number of objects active simultaneously + small buffer
    /// - Example: If max 8 bullets on screen at once, pool 12-16 bullets
    /// 
    /// PERFORMANCE:
    /// - This method is slow (creates 'size' objects via Instantiate)
    /// - Run it during loading screens or level initialization
    /// - Never call during gameplay (causes frame rate stutter)
    /// 
    /// THREAD SAFETY:
    /// - Not thread-safe - call only from main Unity thread
    /// - Dictionary operations are not atomic
    /// </summary>
    /// <param name="prefab">The prefab to pool (template object)</param>
    /// <param name="size">Number of instances to pre-create</param>
    public void InitPool(UnityEngine.Object prefab, int size)
    {
        // ====================================================================
        // DUPLICATE POOL CHECK - Prevent initializing the same pool twice
        // ====================================================================

        // Check if a pool already exists for this prefab
        // ContainsKey() returns true if the dictionary has an entry with this key
        // 
        // WHY THIS CHECK?
        // - Prevents creating duplicate pools (memory waste)
        // - Prevents clearing existing pools accidentally
        // - Example: Two scripts both call InitPool(BulletPrefab, 10)
        //   First call: Creates pool with 10 bullets
        //   Second call: Would recreate pool, orphaning the first 10 bullets
        //   With this check: Second call is silently ignored (idempotent)
        // 
        // DESIGN CONSIDERATION:
        // This silently returns without warning. Alternatives:
        // - Log warning: Debug.LogWarning("Pool already initialized")
        // - Resize pool: Clear and recreate with new size
        // - Merge: Add more instances to existing pool
        if (m_Pools.ContainsKey(prefab))
            // Early return - exit the method without doing anything
            // Pool already exists, so initialization is unnecessary
            return;

        // ====================================================================
        // CREATE QUEUE - Initialize the FIFO data structure for this pool
        // ====================================================================

        // Create a new Queue to hold object instances
        // Queue<T> is a generic FIFO (First-In-First-Out) collection
        // 
        // QUEUE OPERATIONS:
        // - Enqueue(item): Add item to the back of the queue
        // - Dequeue(): Remove and return item from the front of the queue
        // - Count: Number of items currently in queue
        // 
        // VISUALIZATION:
        // Front [obj1] [obj2] [obj3] [obj4] Back
        //       ↑ Dequeue removes from here
        //                                  ↑ Enqueue adds here
        // 
        // RING BUFFER BEHAVIOR:
        // When we GetInstance():
        // 1. Dequeue obj1 from front
        // 2. Activate and return obj1
        // 3. Enqueue obj1 to back
        // Result: [obj2] [obj3] [obj4] [obj1]
        Queue<Object> queue = new Queue<Object>();

        // ====================================================================
        // PRE-POPULATE POOL - Create all objects upfront
        // ====================================================================

        // Loop 'size' times to create initial pool population
        // This is the expensive operation that should happen during initialization
        // Not during gameplay
        for (int i = 0; i < size; ++i)
        {
            // ================================================================
            // INSTANTIATE CLONE - Create a copy of the prefab
            // ================================================================

            // Instantiate() clones the prefab into the scene
            // Returns a UnityEngine.Object (could be GameObject, Component, etc.)
            // 
            // WHAT INSTANTIATE DOES:
            // 1. Memory allocation for the new object
            // 2. Deep copy of all components and children
            // 3. Serialization of properties from prefab
            // 4. Initialization callbacks (Awake, OnEnable if active)
            // 5. Registry in Unity's object tracking system
            // 
            // PERFORMANCE:
            // - Expensive operation (milliseconds per call)
            // - Why we pool: To do this once, not every time we need an object
            // 
            // OBJECT HIERARCHY:
            // If prefab is a GameObject with children, all children are cloned too
            // If prefab is a Component, its GameObject (and children) are cloned
            var o = Instantiate(prefab);

            // ================================================================
            // DEACTIVATE OBJECT - Start pooled objects as inactive
            // ================================================================

            // Call helper method to disable the object
            // Inactive objects:
            // - Don't render (invisible)
            // - Don't run Update(), FixedUpdate(), LateUpdate()
            // - Don't process physics (colliders disabled)
            // - Don't receive input events
            // - Still exist in memory (ready for quick reactivation)
            // 
            // WHY START INACTIVE?
            // Pooled objects shouldn't do anything until they're requested
            // Example: A pooled bullet shouldn't appear or move until spawned
            SetActive(o, false);

            // ================================================================
            // ADD TO QUEUE - Store the inactive object in the pool
            // ================================================================

            // Enqueue() adds the object to the back of the queue
            // First iteration: queue = [obj0]
            // Second iteration: queue = [obj0, obj1]
            // After loop: queue = [obj0, obj1, obj2, ..., objN-1]
            // 
            // All objects are now:
            // 1. Created in memory
            // 2. Inactive (not updating or rendering)
            // 3. Queued and ready for use
            queue.Enqueue(o);
        }

        // ====================================================================
        // REGISTER POOL - Add the queue to the dictionary
        // ====================================================================

        // Store the queue in the dictionary, keyed by the prefab
        // Dictionary indexer syntax: dictionary[key] = value
        // 
        // AFTER THIS LINE:
        // m_Pools = {
        //     [prefab] => queue containing 'size' inactive objects
        // }
        // 
        // Now GetInstance(prefab) can find and use this queue
        m_Pools[prefab] = queue;
    }

    // ========================================================================
    // GETINSTANCE - Retrieve an object from the pool (or create if needed)
    // ========================================================================

    /// <summary>
    /// Gets an active instance from the pool for the specified prefab
    /// 
    /// GENERIC METHOD:
    /// - <T>: Type parameter (ParticleSystem, GameObject, AudioSource, etc.)
    /// - where T:Object: Constraint - T must inherit from UnityEngine.Object
    /// - Returns T: Ensures type safety (returns exactly the type you ask for)
    /// 
    /// USAGE EXAMPLES:
    /// var bullet = PoolSystem.Instance.GetInstance<GameObject>(bulletPrefab);
    /// var explosion = PoolSystem.Instance.GetInstance<ParticleSystem>(explosionPrefab);
    /// var audio = PoolSystem.Instance.GetInstance<AudioSource>(audioSourcePrefab);
    /// 
    /// RING BUFFER BEHAVIOR:
    /// 1. Dequeue object from front of queue
    /// 2. Activate the object (make visible and functional)
    /// 3. Enqueue same object to back of queue
    /// 4. Return the now-active object to caller
    /// 
    /// IMPORTANT:
    /// - InitPool() MUST be called first for this prefab
    /// - Object will cycle back to pool automatically
    /// - No explicit "return to pool" needed
    /// - Object should auto-deactivate when done (particle finishes, timer expires)
    /// 
    /// POOL EXHAUSTION:
    /// If queue is empty (all objects in use):
    /// - Creates new instance on-the-fly (expensive!)
    /// - New instance also gets added to queue for future use
    /// - Pool automatically grows to meet demand
    /// 
    /// PERFORMANCE:
    /// - Best case: O(1) dequeue from existing pool (microseconds)
    /// - Worst case: O(n) instantiate new object (milliseconds)
    /// - Goal: Size pools so worst case never happens during gameplay
    /// </summary>
    /// <typeparam name="T">Type of object to return (must inherit UnityEngine.Object)</typeparam>
    /// <param name="prefab">The prefab whose pool to query</param>
    /// <returns>An active instance of type T, or null if pool not initialized</returns>
    public T GetInstance<T>(Object prefab) where T : Object
    {
        // ====================================================================
        // FIND POOL - Attempt to retrieve the queue for this prefab
        // ====================================================================

        // Declare a variable to receive the queue if found
        // out parameter: TryGetValue will assign to this variable
        Queue<Object> queue;

        // TryGetValue() is safer than direct dictionary access
        // 
        // COMPARISON:
        // Unsafe:  queue = m_Pools[prefab];  // Throws exception if key not found
        // Safe:    if (m_Pools.TryGetValue(prefab, out queue)) { ... }
        // 
        // RETURNS:
        // - true: Key exists, queue is assigned the corresponding value
        // - false: Key doesn't exist, queue remains null (or default value)
        // 
        // WHY TRY-GET PATTERN?
        // Combines lookup and null-check in one efficient operation
        // Avoids two dictionary lookups (ContainsKey + indexer access)
        if (m_Pools.TryGetValue(prefab, out queue))
        {
            // ================================================================
            // POOL FOUND - Retrieve or create an instance
            // ================================================================

            // Declare variable to hold the object instance we'll return
            // Type is Object (base class) because queue stores UnityEngine.Object
            // We'll cast to specific type T at the end
            Object obj;

            // ================================================================
            // CHECK QUEUE STATUS - Do we have available instances?
            // ================================================================

            // Check if queue has any instances available
            // Count property returns the number of items in the queue
            // 
            // Count > 0: We have pre-created instances ready to use (FAST PATH)
            // Count == 0: All instances are currently in use, need to create new (SLOW PATH)
            if (queue.Count > 0)
            {
                // ============================================================
                // FAST PATH - Reuse existing object from pool
                // ============================================================

                // Dequeue() removes and returns the object at the front of the queue
                // 
                // BEFORE: queue = [obj1, obj2, obj3]
                // AFTER:  queue = [obj2, obj3]
                // RESULT: obj = obj1
                // 
                // This is the core pool operation - instant reuse with no allocation
                // Object already exists in memory, just needs to be activated
                // 
                // PERFORMANCE: O(1) constant time operation (microseconds)
                obj = queue.Dequeue();
            }
            else
            {
                // ============================================================
                // SLOW PATH - Pool exhausted, create new instance
                // ============================================================

                // All pre-created instances are currently active/in-use
                // Need to dynamically grow the pool by creating a new instance
                // 
                // WHY THIS HAPPENS:
                // - Pool sized too small for gameplay demand
                // - Unusual spike in object usage
                // - Example: Player fires rapidly, 20 bullets active but only 16 pooled
                // 
                // PERFORMANCE IMPACT:
                // Instantiate() is expensive (milliseconds vs microseconds)
                // This defeats the purpose of pooling during this frame
                // But at least future frames can reuse this new instance
                // 
                // SOLUTION:
                // Increase InitPool size parameter to prevent this code path
                // Profile to find the actual maximum concurrent usage
                obj = Instantiate(prefab);
            }

            // ================================================================
            // ACTIVATE OBJECT - Make it visible and functional
            // ================================================================

            // Call helper method to enable the object
            // 
            // WHAT ACTIVATION DOES:
            // - Makes object visible (renderers turn on)
            // - Starts Update() calls each frame
            // - Enables colliders for physics interactions
            // - Triggers OnEnable() callbacks on all components
            // - Plays particle systems (if object is ParticleSystem)
            // - Resumes audio (if object is AudioSource)
            // 
            // OBJECT STATE TRANSITION:
            // Before: Inactive object sitting in pool (invisible, not updating)
            // After: Active object in scene (visible, running, functional)
            SetActive(obj, true);

            // ================================================================
            // RE-ENQUEUE - Add object back to pool for future use
            // ================================================================

            // Enqueue the object to the back of the queue
            // This implements the "ring buffer" pattern
            // 
            // BEFORE: queue = [obj2, obj3]
            // AFTER:  queue = [obj2, obj3, obj1]
            // 
            // RING BUFFER VISUALIZATION:
            // Step 1: [obj1, obj2, obj3] - Initial state
            // Step 2: Dequeue obj1 -> [obj2, obj3] - obj1 removed
            // Step 3: Activate obj1 (now in use by game)
            // Step 4: Enqueue obj1 -> [obj2, obj3, obj1] - obj1 added to back
            // 
            // AUTOMATIC CYCLING:
            // When obj1 finishes its task (particle ends, bullet expires, etc.):
            // - It automatically deactivates itself
            // - Remains in queue at back, ready for next GetInstance() call
            // - Eventually cycles back to front as other objects are used
            // 
            // WHY THIS WORKS:
            // Objects have short lifespans and self-deactivate
            // By the time obj1 reaches the front of the queue again,
            // it has long since finished its task and is ready for reuse
            // 
            // NO EXPLICIT RETURN NEEDED:
            // Unlike traditional pools with ReturnToPool() methods,
            // this design is simpler - objects are always "in the pool"
            // even when active, just waiting at the back of the line
            queue.Enqueue(obj);

            // ================================================================
            // CAST AND RETURN - Convert to requested type and return to caller
            // ================================================================

            // Cast the UnityEngine.Object to the specific type T requested
            // 
            // as operator:
            // - Safe cast - returns null if cast fails (instead of exception)
            // - Example: obj as ParticleSystem
            //   Success: Returns the object as ParticleSystem type
            //   Failure: Returns null if obj is not a ParticleSystem
            // 
            // WHY THIS IS SAFE:
            // Prefab is consistent - if InitPool was called with ParticleSystem prefab,
            // all instances in the queue are ParticleSystem (or compatible derived types)
            // 
            // RETURN VALUE:
            // Caller receives an active, ready-to-use instance of type T
            // Example: ParticleSystem explosion = GetInstance<ParticleSystem>(explosionPrefab)
            // Now 'explosion' is active, positioned, and playing
            return obj as T;
        }

        // ====================================================================
        // ERROR CASE - Pool not initialized for this prefab
        // ====================================================================

        // Execution reaches here only if TryGetValue returned false
        // This means InitPool() was never called for this prefab
        // 
        // ERROR MESSAGE:
        // Log an error to Unity console so developer knows what went wrong
        // Red error message in console makes this easy to spot during testing
        // 
        // COMMON CAUSES:
        // - Forgot to call InitPool() during Start/Awake
        // - Typo in prefab reference (different variable)
        // - Initialization order issue (GetInstance called before InitPool)
        // 
        // FIX:
        // Add InitPool(prefab, size) before first GetInstance(prefab) call
        // Example: In Start() method, before gameplay begins
        UnityEngine.Debug.LogError("No pool was init with this prefab");

        // Return null to indicate failure
        // Caller must handle null case or game will crash with NullReferenceException
        // 
        // BETTER DESIGN ALTERNATIVES:
        // - Auto-initialize pool with default size on first GetInstance
        // - Throw exception instead of returning null (fail fast)
        // - Include prefab name in error message for easier debugging
        return null;
    }

    // ========================================================================
    // SETACTIVE - Helper method to activate/deactivate Unity objects
    // ========================================================================

    /// <summary>
    /// Internal helper method to set active state of Unity objects
    /// Handles both GameObjects and Components uniformly
    /// 
    /// WHY THIS METHOD EXISTS:
    /// UnityEngine.Object can be:
    /// 1. GameObject - has SetActive() method directly
    /// 2. Component (ParticleSystem, AudioSource, etc.) - must access .gameObject first
    /// 
    /// This method abstracts that difference so pool logic doesn't need to care
    /// 
    /// STATIC METHOD:
    /// - Doesn't need instance data (no 'this' reference)
    /// - Utility function that could be in a separate Helpers class
    /// - Marked static for efficiency (no instance method overhead)
    /// </summary>
    /// <param name="obj">The Unity object to activate/deactivate</param>
    /// <param name="active">True to activate, false to deactivate</param>
    static void SetActive(Object obj, bool active)
    {
        // ====================================================================
        // DECLARE GAMEOBJECT REFERENCE - Will hold the target GameObject
        // ====================================================================

        // Initialize to null - we'll assign the proper GameObject below
        // Why null? Ensures compiler knows variable is initialized before use
        // 
        // GameObject is Unity's fundamental scene object type
        // Every object in the scene hierarchy is a GameObject
        // Components (scripts, renderers, colliders) attach to GameObjects
        GameObject go = null;

        // ====================================================================
        // TYPE CHECK - Is obj a Component or GameObject?
        // ====================================================================

        // Check if obj is a Component (or any derived type)
        // 
        // is operator:
        // - Type checking: Returns true if obj is a Component
        // - Pattern matching: If true, creates 'component' variable with casted value
        // 
        // INHERITANCE HIERARCHY:
        // UnityEngine.Object (base class for all Unity objects)
        // ├─ GameObject
        // └─ Component (base class for all components)
        //    ├─ Transform
        //    ├─ MonoBehaviour
        //    │  └─ (all custom scripts)
        //    ├─ Renderer
        //    ├─ Collider
        //    ├─ ParticleSystem
        //    ├─ AudioSource
        //    └─ ... (many more)
        // 
        // EXAMPLES:
        // - obj is ParticleSystem: TRUE (ParticleSystem inherits from Component)
        // - obj is AudioSource: TRUE (AudioSource inherits from Component)
        // - obj is Transform: TRUE (Transform inherits from Component)
        // - obj is GameObject: FALSE (GameObject doesn't inherit from Component)
        if (obj is Component component)
        {
            // ================================================================
            // COMPONENT PATH - Extract GameObject from Component
            // ================================================================

            // Every Component has a .gameObject property
            // This references the GameObject that the Component is attached to
            // 
            // EXAMPLE:
            // If obj is a ParticleSystem component attached to "Explosion" GameObject
            // component.gameObject returns reference to the "Explosion" GameObject
            // 
            // WHY THIS WORKS:
            // Components cannot exist independently - they MUST be on a GameObject
            // Unity enforces this - cannot have an orphaned Component
            // So .gameObject is always valid (never null for valid Components)
            go = component.gameObject;
        }
        else
        {
            // ================================================================
            // GAMEOBJECT PATH - Object is already a GameObject
            // ================================================================

            // obj must be a GameObject if it's not a Component
            // Cast using 'as' operator for safe conversion
            // 
            // as operator:
            // - Safe cast that returns null on failure (instead of throwing exception)
            // - Example: obj as GameObject
            //   If obj is GameObject: Returns obj as GameObject type
            //   If obj is not GameObject: Returns null
            // 
            // IN THIS CASE:
            // We already know obj is not a Component (else clause)
            // And we know obj is UnityEngine.Object (method parameter type)
            // So it MUST be GameObject (only other option in our use case)
            // Therefore this cast will always succeed (never null)
            go = obj as GameObject;
        }

        // ====================================================================
        // SET ACTIVE STATE - Actually enable/disable the GameObject
        // ====================================================================

        // Call GameObject.SetActive() to change activation state
        // 
        // WHAT SETACTIVE DOES:
        // 
        // SetActive(true) - ACTIVATION:
        // - Makes GameObject visible in scene
        // - Enables all components (renderers, colliders, scripts)
        // - Calls OnEnable() on all MonoBehaviour scripts
        // - Starts Update(), FixedUpdate(), LateUpdate() calls
        // - Re-enables particle systems, audio sources, animations
        // - Re-enables physics (rigidbodies, colliders)
        // 
        // SetActive(false) - DEACTIVATION:
        // - Makes GameObject invisible (disappears from scene view)
        // - Disables all components
        // - Calls OnDisable() on all MonoBehaviour scripts
        // - Stops Update(), FixedUpdate(), LateUpdate() calls
        // - Pauses particle systems, audio sources, animations
        // - Disables physics (no collisions, no gravity)
        // - BUT object still exists in memory (not destroyed)
        // 
        // PERFORMANCE:
        // - SetActive is fast (microseconds)
        // - Much faster than Destroy() + Instantiate()
        // - This is the core of why pooling works
        // 
        // HIERARCHY BEHAVIOR:
        // SetActive affects all children recursively
        // If you deactivate parent GameObject:
        // - All children become inactive too (even if their SetActive is true)
        // - Child active states are remembered
        // - When parent reactivates, children return to their remembered states

        go.SetActive(active);
    }
}

// ============================================================================
// END OF POOLSYSTEM CLASS
// ============================================================================
//
// SUMMARY: PoolSystem is a performance optimization using the ring buffer pattern
//
// KEY RESPONSIBILITIES:
// - Pre-create object instances (InitPool)
// - Reuse instances instead of creating new ones (GetInstance)
// - Automatically cycle objects through queue (ring buffer)
// - Handle both GameObjects and Components uniformly (SetActive helper)
// - Dynamically grow pools if demand exceeds initial size
//
// DESIGN PATTERNS:
// - Singleton: Global instance accessible via PoolSystem.Instance
// - Object Pool: Reuse expensive-to-create objects
// - Ring Buffer: Queue that automatically cycles objects
// - Factory: GetInstance acts as factory method for pooled objects
//
// PERFORMANCE BENEFITS:
// - Eliminates Instantiate() calls during gameplay (expensive)
// - Eliminates Destroy() calls during gameplay (expensive)
// - Reduces garbage collection pressure (no object creation/destruction)
// - Trades memory (keeping objects around) for speed (reusing them)
// - Prevents frame rate stuttering from allocation spikes
//
// FPS KIT INTEGRATION:
// - Target.cs: Pools destruction particle effects
// - Weapon.cs: Pools projectiles and muzzle flashes
// - WorldAudioPool: Pools AudioSources for positioned sound effects
// - GameSystem.Create(): Should call PoolSystem.Create() during init
//
// USAGE PATTERN:
// 1. Game initialization: PoolSystem.Create()
// 2. Scene setup: InitPool(prefab, size) for each pooled type
// 3. Runtime: GetInstance<T>(prefab) whenever you need an instance
// 4. Object auto-deactivates when done (particle finishes, timer expires)
// 5. Repeat step 3 - objects automatically cycle for reuse
//
// LIMITATIONS:
// - Only suitable for short-lived objects that auto-deactivate
// - Not suitable for objects needing explicit cleanup/return logic
// - Not thread-safe (Unity APIs are main-thread only anyway)
// - No built-in reset/cleanup - objects must handle their own state reset
//
// IMPROVEMENTS POSSIBLE:
// - Add null check to Create() to prevent duplicates
// - Add object reset callback interface (IPoolable.OnSpawn/OnDespawn)
// - Add pool statistics (GetPoolInfo, GetActiveCount, GetTotalCount)
// - Add warm-up option (pre-activate objects once to initialize)
// - Add max pool size limits (prevent unbounded growth)
// - Add DontDestroyOnLoad for cross-scene persistence
// - Better error messages with prefab names
// - Auto-initialize pools on first GetInstance with default size
//
// ============================================================================