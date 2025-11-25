// ============================================================================
// USING STATEMENTS - Import necessary libraries
// ============================================================================

using System.Collections;          // Provides interfaces for collections
using System.Collections.Generic;  // Provides generic collection types like Queue<T>, List<T>
using System.Linq;                 // Provides LINQ query methods (not actively used in this script)
using UnityEngine;                 // Core Unity engine functionality

// Conditional compilation for Unity Editor-specific code
#if UNITY_EDITOR
using UnityEditor;                 // Unity Editor API for custom inspectors and scene GUI
#endif

// ============================================================================
// TARGETSPAWNER CLASS - Automated target spawning and movement system
// ============================================================================
// This class manages spawning and moving targets along predefined paths
// Used to create dynamic shooting gallery experiences with:
// - Timed spawning of targets
// - Multiple spawn waves/events
// - Moving targets following paths
// - Automatic lifecycle management
//
// ARCHITECTURE:
// TargetSpawner uses two-phase system:
// 1. QUEUE PHASE: Targets wait in queue with spawn timers
// 2. ACTIVE PHASE: Targets are spawned and move along path
//
// SYSTEMS INTEGRATED:
// - PathSystem: Custom path following system (BackForth, Loop, Once)
// - Target: Shootable target objects with health and scoring
// - Rigidbody: Physics-based movement
// - Queue/List: Data structures for managing target lifecycle
//
// DESIGN PATTERNS:
// - Object Pooling: Pre-instantiate targets, activate when needed
// - Queue System: FIFO spawning with timed delays
// - State Management: Queued → Active → Destroyed states
// - Component Pattern: SpawnQueueElement bundles related data
//
// USE CASES:
// - Shooting gallery: Targets pop up at intervals
// - Moving targets: Ducks/targets slide across range
// - Wave system: Multiple groups spawn in sequence
// - Training range: Predictable target patterns
// ============================================================================

public class TargetSpawner : MonoBehaviour
{
    // ========================================================================
    // SPAWNEVENT NESTED CLASS - Configuration for spawn wave
    // ========================================================================

    /// <summary>
    /// Defines a single spawn event (wave) with target type, count, and timing
    /// [System.Serializable] makes this class editable in Unity Inspector
    /// Allows designers to configure multiple spawn waves visually
    /// </summary>
    [System.Serializable]
    public class SpawnEvent
    {
        /// <summary>
        /// Prefab of target to spawn
        /// Drag target prefab from project into Inspector
        /// Should have Target component and optionally Rigidbody for movement
        /// 
        /// EXAMPLES:
        /// - Standard bullseye target
        /// - Moving duck target
        /// - Explosive barrel target
        /// - Cardboard cutout enemy
        /// </summary>
        public GameObject targetToSpawn;

        /// <summary>
        /// Number of targets to spawn in this event
        /// All targets spawn sequentially with delay between each
        /// 
        /// EXAMPLES:
        /// count = 1: Single target spawns
        /// count = 5: Five targets spawn one after another
        /// count = 10: Ten targets for extended wave
        /// 
        /// DESIGN CONSIDERATION:
        /// Higher count = longer wave duration
        /// Consider player skill when setting count
        /// Too many targets = overwhelming
        /// Too few targets = boring
        /// </summary>
        public int count;

        /// <summary>
        /// Time in seconds between spawning each target in this event
        /// Smaller values = faster spawning (harder)
        /// Larger values = slower spawning (easier)
        /// 
        /// EXAMPLES:
        /// timeBetweenSpawn = 0.5: Target every half second (rapid fire)
        /// timeBetweenSpawn = 2.0: Target every 2 seconds (moderate)
        /// timeBetweenSpawn = 5.0: Target every 5 seconds (slow, methodical)
        /// 
        /// BALANCING:
        /// Consider:
        /// - Player reload time
        /// - Target movement speed
        /// - Difficulty curve
        /// - Path length
        /// 
        /// Fast moving targets on long path = need slower spawn rate
        /// Slow moving targets on short path = can handle faster spawn rate
        /// </summary>
        public float timeBetweenSpawn;
    }

    // ========================================================================
    // PUBLIC INSPECTOR FIELDS - Designer configuration
    // ========================================================================

    /// <summary>
    /// Array of spawn events defining all target waves
    /// Each event spawns specific target type multiple times with delays
    /// Events are processed sequentially (event 0, then event 1, etc.)
    /// 
    /// CONFIGURATION IN INSPECTOR:
    /// 1. Set array size (e.g., 3 events)
    /// 2. For each event, configure:
    ///    - targetToSpawn: Drag target prefab
    ///    - count: How many to spawn
    ///    - timeBetweenSpawn: Delay between each
    /// 
    /// EXAMPLE CONFIGURATION:
    /// Event 0: Standard target, count=5, time=2.0 (warm-up wave)
    /// Event 1: Fast target, count=8, time=1.5 (difficulty increase)
    /// Event 2: Mixed targets, count=10, time=1.0 (challenging final wave)
    /// 
    /// SPAWN ORDER:
    /// All targets from Event 0 spawn first (5 targets, 2 seconds apart)
    /// Then Event 1 targets spawn (8 targets, 1.5 seconds apart)
    /// Finally Event 2 targets spawn (10 targets, 1 second apart)
    /// Total: 23 targets over ~38.5 seconds
    /// </summary>
    public SpawnEvent[] spawnEvents;

    /// <summary>
    /// Movement speed of targets along the path in units per second
    /// Applies to all targets spawned by this spawner
    /// 
    /// SPEED EXAMPLES:
    /// speed = 0.5: Very slow (stationary or barely moving)
    /// speed = 1.0: Slow walking pace
    /// speed = 2.0: Moderate speed (default)
    /// speed = 5.0: Fast moving target (challenging)
    /// speed = 10.0: Very fast (expert difficulty)
    /// 
    /// BALANCING CONSIDERATIONS:
    /// - Faster targets = harder to hit
    /// - Consider path length: long path + fast speed = brief window
    /// - Match speed to target size: small target should move slower
    /// - Consider weapon types: sniper = prefer slower targets
    /// 
    /// INTERACTION WITH PATH:
    /// Speed is distance traveled per second along path waypoints
    /// Same speed feels different on different path types:
    /// - Straight path: Consistent difficulty
    /// - Curved path: Target changes direction, harder to predict
    /// - Back-and-forth path: Target reverses, provides second chances
    /// </summary>
    public float speed = 1.0f;

    /// <summary>
    /// PathSystem instance defining the movement path for targets
    /// PathSystem is a custom FPS Kit component that manages waypoint paths
    /// 
    /// PATH TYPES (configured in PathSystem):
    /// - BackForth: Targets move to end, then reverse back to start (ping-pong)
    /// - Loop: Targets move to end, then teleport back to start, repeat
    /// - Once: Targets move to end once, then stop/despawn
    /// 
    /// PATH CONFIGURATION:
    /// - Edited visually in Scene view
    /// - Add waypoints by clicking in custom inspector
    /// - Drag waypoints to position them in 3D space
    /// - Path is relative to spawner's transform (move spawner = move path)
    /// 
    /// HOW IT WORKS:
    /// 1. Designer places TargetSpawner GameObject in scene
    /// 2. In custom inspector, adds path waypoints
    /// 3. Path shows as connected line in Scene view
    /// 4. Targets follow this path when spawned
    /// 
    /// DESIGN TIPS:
    /// - Simple straight path: Good for beginner training
    /// - L-shaped path: Target crosses field of view
    /// - Circular path: Target circles around player
    /// - Zigzag path: Unpredictable movement pattern
    /// - Vertical path: Target rises/falls (elevator effect)
    /// </summary>
    public PathSystem path = new PathSystem();

    // ========================================================================
    // SPAWNQUEUEELEMENT NESTED CLASS - Internal target tracking
    // ========================================================================

    /// <summary>
    /// Private class bundling all data needed to track one target
    /// Not serializable (not saved) - only exists at runtime
    /// Each target in queue/active list has one SpawnQueueElement
    /// 
    /// PURPOSE:
    /// Groups related data together for easy management
    /// Alternative would be multiple parallel arrays (confusing, error-prone)
    /// This is cleaner: one object per target with all its data
    /// </summary>
    class SpawnQueueElement
    {
        /// <summary>
        /// The instantiated target GameObject
        /// Created in Awake() from targetToSpawn prefab
        /// Starts disabled (SetActive false), enabled when spawned
        /// 
        /// LIFECYCLE:
        /// 1. Instantiate from prefab (Awake)
        /// 2. Disable immediately (SetActive false)
        /// 3. Wait in queue with timer
        /// 4. Enable when timer reaches 0 (SetActive true)
        /// 5. Move along path until finished/destroyed
        /// 6. Remove from active list
        /// </summary>
        public GameObject obj;

        /// <summary>
        /// Reference to Target component on spawned GameObject
        /// Found using GetComponentInChildren (target might be on child)
        /// Used to check if target was destroyed by player
        /// 
        /// WHY CACHE:
        /// GetComponent is slow (searches GameObject hierarchy)
        /// Cache once in Awake, use many times in Update
        /// Significant performance improvement for many targets
        /// 
        /// USAGE:
        /// if (target.Destroyed) - skip updating destroyed targets
        /// Prevents moving targets after player shot them
        /// </summary>
        public Target target;

        /// <summary>
        /// Reference to Rigidbody component for physics-based movement
        /// Rigidbody enables collision detection and physics simulation
        /// Used with MovePosition() for smooth, collision-aware movement
        /// 
        /// WHY RIGIDBODY:
        /// - MovePosition() is physics-aware (detects collisions)
        /// - Smooth interpolation between positions
        /// - Works with Unity's physics system
        /// - Prevents targets passing through walls
        /// 
        /// ALTERNATIVE:
        /// Could use transform.position directly
        /// But that ignores physics, targets could clip through objects
        /// MovePosition is proper way to move physics objects
        /// </summary>
        public Rigidbody rb;

        /// <summary>
        /// Countdown timer until this target should spawn
        /// Starts at timeBetweenSpawn, decrements each frame
        /// When reaches 0 or below, target spawns (Dequeue called)
        /// 
        /// TIMER WORKFLOW:
        /// 1. Initialize: remainingTime = timeBetweenSpawn (e.g., 2.0)
        /// 2. Each frame: remainingTime -= Time.deltaTime
        /// 3. After 2 seconds: remainingTime = 0
        /// 4. Spawn trigger: if (remainingTime <= 0) Dequeue()
        /// 
        /// MULTIPLE TARGETS:
        /// Each target has own timer
        /// They countdown simultaneously
        /// But only first in queue is checked (Peek)
        /// Creates staggered spawning effect
        /// </summary>
        public float remainingTime;

        /// <summary>
        /// PathSystem.PathData instance tracking target's progress along path
        /// Stores current position, which nodes target is between, direction
        /// 
        /// PATHDATA CONTENTS:
        /// - position: Current 3D position on path
        /// - currentNode: Index of last waypoint passed
        /// - nextNode: Index of next waypoint moving toward
        /// - direction: 1 (forward) or -1 (backward) for BackForth paths
        /// 
        /// USAGE:
        /// PathSystem.Move(pathData, distance) updates pathData
        /// Then: rb.MovePosition(pathData.position) moves target
        /// Each target has own pathData (independent progress)
        /// 
        /// EXAMPLE:
        /// Path: [0, 0, 0] → [10, 0, 0] → [10, 10, 0]
        /// pathData starts: position=[0,0,0], currentNode=0, nextNode=1
        /// After moving: position=[5,0,0], currentNode=0, nextNode=1 (halfway to node 1)
        /// After more: position=[10,0,0], currentNode=1, nextNode=2 (reached node 1)
        /// Continues until path complete
        /// </summary>
        public PathSystem.PathData pathData = new PathSystem.PathData();
    }

    // ========================================================================
    // PRIVATE FIELDS - Internal state management
    // ========================================================================

    /// <summary>
    /// Queue of targets waiting to spawn
    /// Queue is FIFO data structure: First In, First Out
    /// 
    /// QUEUE OPERATIONS:
    /// - Enqueue(item): Add item to back of queue
    /// - Dequeue(): Remove and return item from front of queue
    /// - Peek(): Look at front item without removing it
    /// - Count: Number of items in queue
    /// 
    /// SPAWNER WORKFLOW:
    /// 1. Awake: Fill queue with all targets (all events)
    /// 2. Update: Check front target's timer (Peek)
    /// 3. Timer expired: Remove from queue (Dequeue), activate target
    /// 4. Repeat until queue empty
    /// 
    /// WHY QUEUE:
    /// Perfect for timed sequential spawning
    /// Automatically maintains spawn order
    /// Front item is always next to spawn
    /// Simple, efficient data structure for this pattern
    /// 
    /// EXAMPLE STATE:
    /// Queue: [Target1(0.5s), Target2(2.0s), Target3(2.0s)]
    /// After 0.5s: Spawn Target1
    /// Queue: [Target2(1.5s), Target3(2.0s)]
    /// After 1.5s: Spawn Target2
    /// Queue: [Target3(0.5s)]
    /// After 0.5s: Spawn Target3
    /// Queue: [] (empty)
    /// </summary>
    Queue<SpawnQueueElement> m_SpawnQueue;

    /// <summary>
    /// List of currently active (spawned and moving) targets
    /// List allows random access, addition, and removal
    /// 
    /// LIST OPERATIONS:
    /// - Add(item): Append item to end of list
    /// - RemoveAt(index): Remove item at specific index
    /// - Count: Number of items in list
    /// - [i]: Access item at index i
    /// 
    /// ACTIVE TARGET WORKFLOW:
    /// 1. Target timer expires: Dequeue from m_SpawnQueue
    /// 2. Add to m_ActiveElements
    /// 3. Each frame: Update position along path
    /// 4. Path complete or destroyed: Remove from list
    /// 
    /// WHY LIST INSTEAD OF ARRAY:
    /// - Dynamic size (can add/remove as needed)
    /// - Don't know how many targets will be active at once
    /// - Easy removal: RemoveAt(i) without manual shifting
    /// 
    /// ITERATION PATTERN:
    /// for (int i = 0; i < m_ActiveElements.Count; ++i)
    /// Can safely remove items during iteration
    /// Decrement i after removal to check next item correctly
    /// 
    /// TYPICAL STATE:
    /// Early: m_ActiveElements = [Target1, Target2] (two active)
    /// Target1 destroyed: m_ActiveElements = [Target2] (one active)
    /// Target3 spawns: m_ActiveElements = [Target2, Target3] (two active)
    /// Continues until all targets finished/destroyed
    /// </summary>
    List<SpawnQueueElement> m_ActiveElements;

    // ========================================================================
    // AWAKE - Initialization and setup
    // ========================================================================

    /// <summary>
    /// Called when spawner is created
    /// Handles all initialization:
    /// - Path setup
    /// - Target pre-instantiation (object pooling)
    /// - Queue population
    /// - First target spawn
    /// 
    /// EXECUTION ORDER:
    /// Awake runs before Start, before first frame
    /// All Awake methods in scene run before any Start methods
    /// Used for initialization that doesn't depend on other objects
    /// </summary>
    void Awake()
    {
        // ====================================================================
        // INITIALIZE PATH SYSTEM
        // ====================================================================

        // Initialize path with spawner's transform as reference
        // Path waypoints are stored in local space (relative to spawner)
        // Init() converts local waypoints to world space positions
        // 
        // WHY LOCAL SPACE:
        // Designer can move spawner in scene
        // Path moves with spawner (stays relative)
        // If path was world space, moving spawner wouldn't move path
        // 
        // CONVERSION:
        // Local waypoint: (5, 0, 0) relative to spawner
        // If spawner at world position (10, 0, 0)
        // World waypoint: (15, 0, 0) absolute position
        // 
        // transform is reference to this GameObject's Transform
        // PathSystem uses it to calculate world positions
        path.Init(transform);

        // ====================================================================
        // CREATE SPAWN QUEUE
        // ====================================================================

        // Instantiate new empty queue
        // Will be filled with all targets from all spawn events
        // Generic syntax: Queue<Type> creates queue holding that type
        m_SpawnQueue = new Queue<SpawnQueueElement>();

        // ====================================================================
        // POPULATE QUEUE WITH ALL TARGETS FROM ALL EVENTS
        // ====================================================================

        // Loop through each spawn event configured in Inspector
        // foreach iterates over array items automatically
        // 'var e' means "infer type" - compiler knows it's SpawnEvent
        foreach (var e in spawnEvents)
        {
            // Loop to create specified count of targets for this event
            // If count = 5, loop runs 5 times
            // Creates 5 targets of this type with same timing
            for (int i = 0; i < e.count; ++i)
            {
                // ============================================================
                // CREATE AND CONFIGURE SPAWN ELEMENT
                // ============================================================

                // Create new SpawnQueueElement using object initializer syntax
                // new ClassName() { field = value, field = value }
                // More readable than multiple assignment lines
                SpawnQueueElement element = new SpawnQueueElement()
                {
                    // Instantiate target from prefab
                    // Creates new GameObject as copy of prefab
                    // This is "object pooling lite" - pre-create all targets
                    // Better would be true pooling (reuse destroyed targets)
                    // But for finite number of targets, pre-instantiation works
                    obj = Instantiate(e.targetToSpawn),

                    // Set spawn timer to event's delay setting
                    // First target gets full delay
                    // Each subsequent target also gets full delay
                    // Creates evenly spaced spawning
                    // 
                    // TIMING EXAMPLE:
                    // Event: count=3, timeBetweenSpawn=2.0
                    // Target 1: remainingTime=2.0 (spawns after 2s)
                    // Target 2: remainingTime=2.0 (spawns after 4s total)
                    // Target 3: remainingTime=2.0 (spawns after 6s total)
                    // Because each waits for previous to spawn first
                    remainingTime = e.timeBetweenSpawn
                };

                // ============================================================
                // CACHE COMPONENT REFERENCES
                // ============================================================

                // Get Rigidbody component for physics movement
                // GetComponent searches GameObject for component type
                // Returns component if found, null if not found
                // Rigidbody should be on root GameObject of target prefab
                element.rb = element.obj.GetComponent<Rigidbody>();

                // Get Target component from GameObject or children
                // GetComponentInChildren searches object and all descendants
                // Target script might be on child object (e.g., separate hit box)
                // Returns first Target found in hierarchy
                element.target = element.obj.GetComponentInChildren<Target>();

                // ============================================================
                // PREPARE TARGET FOR QUEUING
                // ============================================================

                // Disable target GameObject initially
                // SetActive(false) makes object invisible and inactive
                // All scripts on object stop running
                // Target waits in queue in disabled state
                // 
                // WHY DISABLE:
                // - Not visible to player yet (hasn't spawned)
                // - Doesn't consume update cycles (performance)
                // - Doesn't collide with anything (inactive physics)
                // Will be enabled (SetActive true) when spawned
                element.obj.SetActive(false);

                // Position target at spawner's location
                // transform.position is this spawner's world position
                // Target starts at spawn point, then moves along path
                // All targets start from same origin point
                element.obj.transform.position = transform.position;

                // Rotate target to match spawner's rotation
                // transform.rotation is this spawner's orientation
                // Ensures target faces correct direction initially
                // Important if target model has forward direction
                element.obj.transform.rotation = transform.rotation;

                // ============================================================
                // INITIALIZE PATH DATA
                // ============================================================

                // Initialize pathData for this target
                // InitData() sets:
                // - position: Start of path (first waypoint)
                // - currentNode: 0 (at first waypoint)
                // - nextNode: 1 (moving toward second waypoint)
                // - direction: 1 (forward along path)
                // 
                // Each target gets own pathData (independent movement)
                // Multiple targets can be at different points on same path
                path.InitData(element.pathData);

                // ============================================================
                // ADD TO SPAWN QUEUE
                // ============================================================

                // Add element to back of queue
                // Enqueue() is Queue's add method
                // First target from first event goes to front
                // Last target from last event goes to back
                // 
                // QUEUE ORDER EXAMPLE:
                // Event 0: 2 targets → Queue: [E0T0, E0T1]
                // Event 1: 3 targets → Queue: [E0T0, E0T1, E1T0, E1T1, E1T2]
                // Spawning order: E0T0, E0T1, E1T0, E1T1, E1T2
                m_SpawnQueue.Enqueue(element);
            }
        }
        // After all loops, queue contains all targets from all events
        // In order: Event 0 targets, then Event 1, then Event 2, etc.

        // ====================================================================
        // POST-SETUP VALIDATION
        // ====================================================================

        // Check if queue is empty (no spawn events or all counts were 0)
        // Count property returns number of items in queue
        if (m_SpawnQueue.Count == 0)
        {
            // NO TARGETS TO SPAWN - Destroy this spawner
            // 
            // WHY DESTROY:
            // Spawner with no targets serves no purpose
            // Wastes memory and update cycles
            // Clean up to avoid clutter
            // 
            // SCENARIO:
            // Designer accidentally left spawner with empty event array
            // Or set all counts to 0
            // Script detects and self-destructs
            // 
            // Destroy() marks GameObject for destruction at end of frame
            // gameObject is reference to this GameObject
            Destroy(gameObject);
        }
        else
        {
            // QUEUE HAS TARGETS - Initialize active list and spawn first

            // Create empty list for active targets
            // Generic syntax: List<Type> creates list holding that type
            m_ActiveElements = new List<SpawnQueueElement>();

            // Spawn the first target immediately
            // Dequeue() removes first item from queue and activates it
            // First target appears instantly (no waiting)
            // Subsequent targets spawn based on their timers
            // 
            // WHY SPAWN FIRST IMMEDIATELY:
            // Better game feel - action starts right away
            // Alternative would be waiting timeBetweenSpawn before first target
            // That creates awkward delay at start
            // This way, targets appear at: 0s, 2s, 4s, 6s... (not 2s, 4s, 6s...)
            Dequeue();
        }

        // After Awake completes:
        // - Path initialized and ready
        // - All targets pre-instantiated in queue
        // - First target spawned and moving
        // - System ready to spawn remaining targets over time
    }

    // ========================================================================
    // DEQUEUE - Spawn next target from queue
    // ========================================================================

    /// <summary>
    /// Removes next target from queue and activates it
    /// Called from Awake (first target) and Update (subsequent targets)
    /// Simple but critical method - transitions target from queued to active
    /// </summary>
    void Dequeue()
    {
        // ====================================================================
        // REMOVE FROM QUEUE
        // ====================================================================

        // Remove and get first item from queue
        // Dequeue() is Queue's remove method
        // Returns the item and removes it from queue
        // Queue.Count decreases by 1
        // Next item moves to front
        // 
        // QUEUE STATE CHANGE:
        // Before: [Target1, Target2, Target3] (Count=3)
        // After:  [Target2, Target3] (Count=2, Target1 returned)
        var e = m_SpawnQueue.Dequeue();

        // ====================================================================
        // ACTIVATE TARGET
        // ====================================================================

        // Enable the target GameObject
        // SetActive(true) makes object visible and active
        // All scripts start running (Update, physics, etc.)
        // Colliders become active (can be hit by raycasts/bullets)
        // Renderers show model to player
        // 
        // VISUAL EFFECT:
        // Target suddenly appears at spawn position
        // If path starts off-screen, target slides into view
        // If path starts on-screen, target pops into existence
        // Could add spawn effect here (particle system, sound)
        e.obj.SetActive(true);

        // ====================================================================
        // ADD TO ACTIVE LIST
        // ====================================================================

        // Add to list of active targets
        // Add() appends to end of list
        // List.Count increases by 1
        // Now Update() will move this target each frame
        // 
        // ACTIVE LIST STATE:
        // Before: [ActiveTarget1, ActiveTarget2] (Count=2)
        // After:  [ActiveTarget1, ActiveTarget2, e] (Count=3)
        m_ActiveElements.Add(e);

        // After Dequeue completes:
        // - Target removed from spawn queue
        // - Target visible and active
        // - Target added to active list
        // - Target will start moving in next Update call
    }

    // ========================================================================
    // UPDATE - Frame-by-frame logic
    // ========================================================================

    /// <summary>
    /// Called every frame by Unity (typically 60 times per second)
    /// Handles two main responsibilities:
    /// 1. Spawn timer management (check if next target should spawn)
    /// 2. Active target movement (update all spawned targets along path)
    /// </summary>
    void Update()
    {
        // ====================================================================
        // SPAWN TIMER SYSTEM
        // ====================================================================
        // Check if more targets waiting to spawn
        // Decrement timer on next target
        // Spawn when timer expires

        // Check if queue has targets waiting to spawn
        // Count > 0 means at least one target still queued
        // When queue empty (Count = 0), skip this section
        if (m_SpawnQueue.Count > 0)
        {
            // Get reference to next target without removing it
            // Peek() looks at front item but leaves it in queue
            // Different from Dequeue() which removes it
            // 
            // WHY PEEK:
            // We want to check/update timer without spawning yet
            // Only spawn when timer reaches 0
            // Peek lets us access item without committing to spawn
            var elem = m_SpawnQueue.Peek();

            // Decrement spawn timer by frame time
            // Time.deltaTime = seconds since last frame
            // Makes timer frame-rate independent
            // 
            // TIMER COUNTDOWN:
            // Frame 1: remainingTime = 2.0 - 0.0167 = 1.9833
            // Frame 2: remainingTime = 1.9833 - 0.0167 = 1.9666
            // ... (continues for ~120 frames at 60 FPS)
            // Frame 120: remainingTime = 0.0167 - 0.0167 = 0
            // 
            // FRAME RATE INDEPENDENCE:
            // At 60 FPS: 120 frames × 0.0167s = 2.0 seconds
            // At 30 FPS: 60 frames × 0.0333s = 2.0 seconds
            // At 120 FPS: 240 frames × 0.0083s = 2.0 seconds
            // Real-world time is always same!
            elem.remainingTime -= Time.deltaTime;

            // Check if timer expired (reached 0 or below)
            // <= 0 catches both exact 0 and slight overshoot
            // Overshoot possible due to frame time variations
            if (elem.remainingTime <= 0)
            {
                // TIMER EXPIRED - Spawn this target
                // Dequeue() removes from queue and activates
                // Next target (if any) moves to front of queue
                // Its timer starts counting down next frame
                Dequeue();
            }
        }
        // If queue empty, this section does nothing
        // All targets have spawned, just update active ones

        // ====================================================================
        // ACTIVE TARGET MOVEMENT SYSTEM
        // ====================================================================
        // Move all active targets along their paths
        // Handle path completion and target destruction

        // Calculate how far targets should move this frame
        // Formula: distance = speed × time
        // 
        // DISTANCE CALCULATION:
        // speed = 2.0 units per second
        // Time.deltaTime = 0.0167 seconds (at 60 FPS)
        // distanceToGo = 2.0 × 0.0167 = 0.0334 units
        // 
        // FRAME RATE INDEPENDENCE:
        // At 60 FPS: 0.0334 units × 60 frames = 2.0 units per second
        // At 30 FPS: 0.0667 units × 30 frames = 2.0 units per second
        // Speed stays constant regardless of framerate
        float distanceToGo = speed * Time.deltaTime;

        // Loop through all currently active targets
        // Standard for loop with index for safe removal during iteration
        // 
        // LOOP PATTERN:
        // for (int i = 0; i < Count; ++i)
        // i starts at 0, increments each iteration
        // Continues while i < Count (stops when i reaches Count)
        // ++i increments i after each iteration
        for (int i = 0; i < m_ActiveElements.Count; ++i)
        {
            // Get reference to current target element
            // [i] is array/list indexing - gets item at position i
            // Direct reference (not copy) so we can modify it
            var currentElem = m_ActiveElements[i];

            // ================================================================
            // CHECK IF TARGET DESTROYED
            // ================================================================

            // Check if target was already destroyed by player
            // Target.Destroyed is property that returns true when target health reaches 0
            // 
            // WHY CHECK:
            // Player might shoot target while it's moving
            // Destroyed targets shouldn't continue moving
            // Wastes CPU to update position of destroyed object
            // Also prevents visual glitch (destroyed target sliding around)
            if (currentElem.target.Destroyed)
            {
                // Target destroyed - skip to next iteration
                // continue jumps to next loop iteration immediately
                // Skips all code below for this target
                // Target stays in m_ActiveElements list (could be cleaned up)
                // But since it's destroyed, not updating it is sufficient
                continue;
            }
            // If target not destroyed, continue with movement

            // ================================================================
            // MOVE TARGET ALONG PATH
            // ================================================================

            // Move target along path by calculated distance
            // PathSystem.Move() updates pathData and returns event
            // 
            // PARAMETERS:
            // currentElem.pathData: Current position and progress on path
            // distanceToGo: How far to move this frame (e.g., 0.0334 units)
            // 
            // WHAT MOVE DOES:
            // 1. Calculate direction to next waypoint
            // 2. Move pathData.position toward next waypoint
            // 3. Check if reached next waypoint
            // 4. If reached, advance to next node
            // 5. Handle path end (depends on path type)
            // 6. Return event describing what happened
            // 
            // RETURN VALUES:
            // PathEvent.Nothing: Normal movement, nothing special
            // PathEvent.ChangedDirection: Reached end, reversed (BackForth)
            // PathEvent.Finished: Reached end, path complete (Once)
            // 
            // PATHDATA UPDATE:
            // Before: position=(5,0,0), currentNode=0, nextNode=1
            // After:  position=(5.0334,0,0), currentNode=0, nextNode=1
            // (Moved 0.0334 units toward node 1)
            var evt = path.Move(currentElem.pathData, distanceToGo);

            // ================================================================
            // HANDLE PATH EVENTS
            // ================================================================
            // React to path completion or direction changes

            // Switch statement branches on enum value
            // Like series of if-else but cleaner for multiple options
            switch (evt)
            {
                case PathSystem.PathEvent.Finished:
                    // PATH COMPLETED - Remove target from active list

                    // This occurs when:
                    // - Path type is "Once"
                    // - Target reached final waypoint
                    // - Target is done, should be removed

                    // Remove target from active list
                    // RemoveAt(i) removes item at index i
                    // All items after i shift down one position
                    // List.Count decreases by 1
                    // 
                    // REMOVAL EXAMPLE:
                    // Before: [Target0, Target1, Target2] (i=1)
                    // RemoveAt(1)
                    // After:  [Target0, Target2] (Target1 removed, Target2 moved to index 1)
                    m_ActiveElements.RemoveAt(i);

                    // Decrement loop counter
                    // CRITICAL FOR CORRECT ITERATION
                    // 
                    // WHY DECREMENT:
                    // We removed item at index i
                    // Item that was at i+1 is now at i
                    // Loop will do ++i next, making it i+1
                    // So we'd skip the item that moved to i
                    // 
                    // FIX:
                    // Decrement i (i--)
                    // Loop does ++i
                    // Net effect: i stays same
                    // Next iteration checks item that moved to current i
                    // 
                    // ITERATION EXAMPLE:
                    // i=1, remove item, i-- makes i=0
                    // Loop does ++i, so i=1 again
                    // Now we process item that moved to position 1
                    i--;

                    // Break exits switch statement
                    // Continue to next loop iteration
                    break;

                default:
                    // ALL OTHER EVENTS (Nothing, ChangedDirection)
                    // Normal movement - update target position

                    // Move target's Rigidbody to new path position
                    // MovePosition() is physics-aware movement method
                    // 
                    // WHY MOVEPOSITION:
                    // - Respects physics system
                    // - Smooth interpolation
                    // - Collision detection works correctly
                    // - Won't clip through walls/objects
                    // 
                    // VS TRANSFORM.POSITION:
                    // transform.position = instant teleport (ignores physics)
                    // rb.MovePosition = physics-aware movement
                    // 
                    // currentElem.pathData.position is new position from Path.Move()
                    // Rigidbody smoothly moves to that position
                    currentElem.rb.MovePosition(currentElem.pathData.position);

                    // Break exits switch statement
                    // Continue to next loop iteration
                    break;
            }
        }

        // After Update completes:
        // - Next queued target's timer decremented
        // - If timer expired, target spawned
        // - All active targets moved along path
        // - Finished targets removed from active list
        // - Destroyed targets skipped (not moved)
        // 
        // Next frame: Repeat process
    }
}

// ============================================================================
// UNITY EDITOR CODE - Custom Inspector and Scene GUI
// ============================================================================
// Everything from here only exists in Unity Editor
// Not compiled into final game build
// Provides visual tools for level designers
// ============================================================================

#if UNITY_EDITOR

// ============================================================================
// MOVINGPLATFORMEDITOR - Custom Inspector for TargetSpawner
// ============================================================================

/// <summary>
/// Custom editor providing visual path editing in Scene view
/// Extends default Inspector with path manipulation tools
/// Note: Class is named "MovingPlatformEditor" but edits TargetSpawner
/// Likely copy-paste from MovingPlatform script (minor naming inconsistency)
/// </summary>
[CustomEditor(typeof(TargetSpawner))]
public class MovingPlatformEditor : Editor
{
    // ========================================================================
    // CACHED REFERENCE
    // ========================================================================

    /// <summary>
    /// Cached reference to the TargetSpawner being edited
    /// Set in OnEnable, used in Inspector and Scene GUI methods
    /// Avoids repeated casting of 'target' property
    /// </summary>
    TargetSpawner m_TargetSpawner;

    // ========================================================================
    // ONENABLE - Called when Inspector is opened
    // ========================================================================

    /// <summary>
    /// Called when this editor is enabled (Inspector opened)
    /// Caches reference to target object being edited
    /// </summary>
    void OnEnable()
    {
        // Cast 'target' to TargetSpawner type and cache it
        // 'target' is inherited property from Editor class
        // Points to the object being inspected
        // 'as' operator safely casts (returns null if cast fails)
        // 
        // WHY CACHE:
        // OnInspectorGUI and OnSceneGUI called many times per second
        // Casting every time is inefficient
        // Cache once, reuse many times
        m_TargetSpawner = target as TargetSpawner;
    }

    // ========================================================================
    // ONINSPECTORGUI - Renders Inspector interface
    // ========================================================================

    /// <summary>
    /// Called to render the Inspector window
    /// Draws default fields plus custom speed field and path editor
    /// Provides designer-friendly interface for configuring spawner
    /// </summary>
    public override void OnInspectorGUI()
    {
        // Draw default Inspector GUI first
        // base.OnInspectorGUI() draws all public fields automatically
        // Shows: spawnEvents array, speed float, path object
        // We could override completely, but building on default is easier
        base.OnInspectorGUI();

        // ====================================================================
        // CUSTOM SPEED FIELD WITH UNDO SUPPORT
        // ====================================================================

        // Begin change detection
        // EditorGUI.BeginChangeCheck() starts monitoring for changes
        // Any GUI drawn between Begin and End is monitored
        // EndChangeCheck() returns true if value changed
        EditorGUI.BeginChangeCheck();

        // Draw float field for speed
        // FloatField creates number input field in Inspector
        // 
        // PARAMETERS:
        // "Speed": Label text displayed before field
        // m_TargetSpawner.speed: Current value to display
        // 
        // RETURN VALUE:
        // Returns the value after user interaction
        // If user changed it, returns new value
        // If user didn't change it, returns same value
        // 
        // VISUAL:
        // Shows: "Speed: [2.0]" with editable number field
        float newSpeed = EditorGUILayout.FloatField("Speed", m_TargetSpawner.speed);

        // Check if value changed
        // EndChangeCheck() returns true if user modified the field
        if (EditorGUI.EndChangeCheck())
        {
            // VALUE CHANGED - Apply change with undo support

            // Record object state for undo system
            // Undo.RecordObject captures current state before change
            // 
            // PARAMETERS:
            // target: Object being modified (the TargetSpawner)
            // "Changed Speed": Description shown in undo menu
            // 
            // UNDO SYSTEM:
            // Without this: Change is permanent, can't undo
            // With this: User can press Ctrl+Z to undo change
            // Unity stores "before" state, can revert to it
            // 
            // EXAMPLE:
            // User changes speed 2.0 → 5.0
            // RecordObject captures "speed was 2.0"
            // User presses Ctrl+Z
            // Unity restores "speed = 2.0"
            Undo.RecordObject(target, "Changed Speed");

            // Apply new speed value
            // This actually changes the spawner's speed
            // Change is saved to scene/prefab automatically
            m_TargetSpawner.speed = newSpeed;
        }

        // ====================================================================
        // VISUAL SPACING
        // ====================================================================

        // Add vertical space in Inspector
        // EditorGUILayout.Separator() adds small gap between sections
        // Makes Inspector easier to read with visual grouping
        // Two separators = double spacing (medium gap)
        EditorGUILayout.Separator();
        EditorGUILayout.Separator();

        // ====================================================================
        // PATH INSPECTOR GUI
        // ====================================================================

        // Draw path editor controls in Inspector
        // PathSystem.InspectorGUI() is method on PathSystem class
        // Draws custom UI for managing path waypoints
        // 
        // TYPICAL PATH INSPECTOR:
        // - Path Type dropdown (BackForth, Loop, Once)
        // - Waypoint list showing all nodes
        // - Add/Remove waypoint buttons
        // - Coordinates for each waypoint
        // 
        // PARAMETER:
        // m_TargetSpawner.transform: Reference for local-to-world conversion
        // Path waypoints are relative to spawner position
        // Transform needed to display world positions correctly
        m_TargetSpawner.path.InspectorGUI(m_TargetSpawner.transform);
    }

    // ========================================================================
    // ONSCENEGUI - Renders Scene view handles
    // ========================================================================

    /// <summary>
    /// Called to render custom Scene view GUI
    /// Draws visual path in Scene view for manipulation
    /// Allows designers to drag waypoints in 3D space
    /// Private access is fine - Unity calls it via reflection
    /// </summary>
    private void OnSceneGUI()
    {
        // Draw path visualization and handles in Scene view
        // PathSystem.SceneGUI() is method on PathSystem class
        // Creates visual representation of path with interactive handles
        // 
        // TYPICAL PATH SCENE GUI:
        // - Line connecting all waypoints (path visualization)
        // - Sphere handles at each waypoint (draggable)
        // - Direction indicators (arrows showing movement direction)
        // - Labels showing waypoint numbers
        // 
        // INTERACTION:
        // Designer clicks and drags sphere handles
        // Waypoint position updates in real-time
        // Path line updates to show new path shape
        // Changes reflected in Inspector waypoint list
        // 
        // PARAMETER:
        // m_TargetSpawner.transform: Reference for coordinate conversion
        // Handles appear in world space but stored in local space
        // Transform provides conversion between spaces
        // 
        // VISUAL RESULT:
        // Designer sees path as colored line through level
        // Can shape path by dragging waypoints
        // Immediate visual feedback of target path
        // Much easier than typing coordinates in Inspector
        m_TargetSpawner.path.SceneGUI(m_TargetSpawner.transform);
    }
}

#endif // UNITY_EDITOR

// ============================================================================
// END OF TARGETSPAWNER.CS
// ============================================================================
//
// SUMMARY: TargetSpawner manages timed spawning and path-following targets
//
// KEY RESPONSIBILITIES:
// 1. Sequential target spawning with configurable delays
// 2. Path-based target movement (straight, curved, complex paths)
// 3. Multiple spawn waves with different target types
// 4. Performance optimization via pre-instantiation
// 5. Target lifecycle management (queue → active → finished/destroyed)
//
// DESIGN PATTERNS:
// - Queue: FIFO spawning with timers
// - Object Pooling: Pre-instantiate targets for performance
// - State Management: Queued vs Active states
// - Component Pattern: SpawnQueueElement bundles related data
// - Custom Editor: Visual path editing in Scene view
//
// USAGE WORKFLOW:
// 1. Place TargetSpawner GameObject in scene
// 2. Configure spawn events in Inspector
// 3. Edit path visually in Scene view (drag waypoints)
// 4. Test spawning by entering Play mode
// 5. Adjust timing and speed for desired difficulty
//
// INTEGRATION WITH FPS KIT:
// - PathSystem: Provides path following logic
// - Target: Shootable objects with health and scoring
// - Rigidbody: Physics-based movement
// - GameSystem: Tracks destroyed targets for completion
//
// PERFORMANCE CONSIDERATIONS:
// - Pre-instantiates all targets (avoids runtime Instantiate calls)
// - Caches component references (avoids repeated GetComponent)
// - Skips updates on destroyed targets (saves CPU)
// - Uses MovePosition for physics-aware movement
//
// EXTENSION IDEAS:
// - True object pooling (reuse destroyed targets)
// - Random spawn positions within area
// - Random path variations
// - Speed ramping (targets get faster over time)
// - Spawn limits (max active targets at once)
// - Respawning (targets reappear after timeout)
// - Difficulty scaling based on player performance
// - Sound effects on spawn (pop-up sound)
// - Visual effects on spawn (puff of smoke)
//
// ============================================================================