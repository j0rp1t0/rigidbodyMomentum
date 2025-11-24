// ============================================================================
// USING STATEMENTS - Import necessary Unity libraries
// ============================================================================

using System.Collections;          // Provides interfaces for collections (not used here but commonly included)
using System.Collections.Generic;  // Provides generic collection types (not used here but commonly included)
using UnityEngine;                 // Core Unity engine functionality - required for MonoBehaviour, GameObject, etc.

// ============================================================================
// REQUIRECOMPONENT ATTRIBUTE - Ensures required components exist
// ============================================================================
// [RequireComponent(typeof(T))] is a Unity attribute that enforces dependencies
// This attribute does THREE important things:
// 1. Automatically adds a Collider component when you add this script to a GameObject
// 2. Prevents the Collider from being removed in the Unity Inspector (button becomes disabled)
// 3. Shows an error message if you try to remove the Collider
//
// WHY THIS IS IMPORTANT:
// - AmmoBox needs a Collider to detect when the player touches it
// - Without a Collider, OnTriggerEnter() will never be called
// - This prevents designer errors (forgetting to add the Collider)
//
// SYNTAX NOTE:
// - typeof(Collider) gets the Type object for the Collider class
// - Collider is the base class for all Unity colliders (BoxCollider, SphereCollider, etc.)
// - Any type of collider will satisfy this requirement
// ============================================================================

[RequireComponent(typeof(Collider))]

// ============================================================================
// AMMOBOX CLASS - Pickup item that grants ammunition to the player
// ============================================================================
// This class creates a collectible pickup item that:
// - Sits in the game world at a specific location
// - Uses Unity's physics trigger system to detect when the player touches it
// - Grants a specific type and amount of ammunition to the player
// - Destroys itself after being picked up (one-time use)
//
// ARCHITECTURE: AmmoBox is part of the FPS Kit's pickup system
// It demonstrates several common game development patterns:
// - Pickup/collectible pattern (item in world that can be collected)
// - Component-based design (uses Unity's component system)
// - Trigger-based interaction (uses physics triggers, not button presses)
// - Automatic destruction after use (cleanup to prevent re-pickup)
//
// HOW IT WORKS IN THE GAME FLOW:
// 1. Level designer places AmmoBox in the scene
// 2. Designer configures ammoType (e.g., "Rifle Ammo") and amount (e.g., 30)
// 3. Player walks through the level
// 4. Player's character controller collider overlaps with AmmoBox's trigger collider
// 5. OnTriggerEnter() is called automatically by Unity's physics system
// 6. Script checks if the overlapping object is the player (has Controller component)
// 7. If it's the player, adds ammo to their inventory via Controller.ChangeAmmo()
// 8. AmmoBox destroys itself so it can't be picked up again
//
// RELATED SYSTEMS IN FPS KIT:
// - Controller: Player character with ammo inventory (has ChangeAmmo method)
// - AmmoTypeAttribute: Custom attribute that creates dropdown in Inspector
// - GameDatabase: Stores ammo type definitions (IDs, names, icons)
// - WeaponInfoUI: Updates HUD display when ammo changes
// - Weapon: Consumes ammo from player inventory when firing
// ============================================================================

public class AmmoBox : MonoBehaviour
{
    // ========================================================================
    // PUBLIC INSPECTOR FIELDS - Configure in Unity Editor
    // ========================================================================
    // These fields are "public" so they appear in the Unity Inspector
    // Level designers can set these values without touching code
    // ========================================================================

    /// <summary>
    /// The type of ammunition this box contains
    /// This is an integer ID that corresponds to an ammo type in the GameDatabase
    /// 
    /// [AmmoType] ATTRIBUTE:
    /// This custom attribute creates a designer-friendly dropdown in Unity Inspector
    /// Instead of seeing a number field where you type 0, 1, 2...
    /// You see a dropdown with names like:
    /// - "Pistol Ammo"
    /// - "Rifle Ammo"  
    /// - "Shotgun Ammo"
    /// This makes it much easier for designers who don't know the ID numbers
    /// 
    /// HOW IT WORKS TECHNICALLY:
    /// - The value is stored as an int (the ammo type's unique ID number)
    /// - The ID corresponds to GameDatabase.Instance.ammoDatabase.entries[].id
    /// - AmmoTypeAttribute is defined in Weapon.cs and has a CustomPropertyDrawer
    /// - The drawer looks up all ammo types from GameDatabase at edit time
    /// - It displays entry.name in the dropdown but stores entry.id in this field
    /// - When you select "Rifle Ammo" in the dropdown, it stores the ID (e.g., 1)
    /// 
    /// EXAMPLE DATA FLOW:
    /// GameDatabase contains:
    ///   Entry 0: { id=0, name="Pistol Ammo", icon=pistolIcon }
    ///   Entry 1: { id=1, name="Rifle Ammo", icon=rifleIcon }
    ///   Entry 2: { id=2, name="Shotgun Ammo", icon=shotgunIcon }
    /// 
    /// Designer selects "Rifle Ammo" in Inspector → ammoType = 1
    /// When player picks up box → Controller.ChangeAmmo(1, amount) is called
    /// Controller looks up ammo type 1 in its inventory dictionary
    /// UI displays the rifle ammo icon and count
    /// 
    /// WHY USE INT INSTEAD OF STRING:
    /// - Integers are faster to compare than strings (performance)
    /// - Integers take less memory (4 bytes vs potentially dozens)
    /// - Integers are safer (no typos like "Riffle Ammo" vs "Rifle Ammo")
    /// - Integers allow for efficient dictionary lookups in Controller
    /// </summary>
    [AmmoType]
    public int ammoType;

    /// <summary>
    /// Amount of ammunition to grant when picked up
    /// This is the number of rounds/bullets/shells the player receives
    /// Set in Unity Inspector by the level designer
    /// 
    /// DESIGN CONSIDERATIONS:
    /// - Small pickups: 10-30 rounds (for common ammo scattered around)
    /// - Medium pickups: 30-60 rounds (for less common ammo)
    /// - Large pickups: 60+ rounds (for rare or powerful weapon ammo)
    /// 
    /// BALANCING:
    /// Level designers use this to control game difficulty:
    /// - More ammo = easier (players can shoot more)
    /// - Less ammo = harder (players must conserve ammo)
    /// - Amount can vary by weapon type (pistol ammo might be more plentiful)
    /// 
    /// CLAMPING:
    /// Controller.ChangeAmmo() clamps the total to a maximum of 999
    /// So if player has 990 rounds and picks up 30, they'll have 999 (not 1020)
    /// This prevents integer overflow and keeps UI numbers reasonable
    /// 
    /// NEGATIVE VALUES:
    /// While you could technically set a negative amount, it's not intended
    /// Negative values would subtract ammo, which doesn't make sense for a pickup
    /// If you need to remove ammo, call Controller.ChangeAmmo() directly with negative value
    /// </summary>
    public int amount;

    // ========================================================================
    // RESET METHOD - Unity Editor helper method
    // ========================================================================
    // IMPORTANT: Reset() is NOT called during gameplay!
    // This is a special Unity Editor method that runs in two situations:
    // 1. When you first add this component to a GameObject in the editor
    // 2. When you click the gear icon on the component and select "Reset"
    //
    // PURPOSE: Automatically configure the GameObject for optimal AmmoBox behavior
    // This saves designers from having to manually set up the same settings every time
    //
    // UNITY LIFECYCLE NOTE:
    // Reset() is NOT part of the normal game lifecycle (Awake, Start, Update, etc.)
    // It ONLY runs in the Unity Editor, never in a built game
    // ========================================================================

    /// <summary>
    /// Called when component is first added or reset in Unity Editor
    /// Automatically configures the GameObject with correct settings for an AmmoBox
    /// This is a convenience method to prevent setup errors
    /// </summary>
    void Reset()
    {
        // ====================================================================
        // SET COLLISION LAYER
        // ====================================================================

        // Set this GameObject to the "PlayerCollisionOnly" layer
        // 
        // WHAT ARE LAYERS:
        // Unity Layers are like categories or tags for GameObjects
        // They control which objects can interact with each other
        // Example layers: Default, Player, Enemy, IgnoreRaycast, etc.
        // 
        // gameObject is a reference to the GameObject this script is attached to
        // Every MonoBehaviour has a "gameObject" property automatically
        // 
        // .layer is an integer representing which layer the GameObject is on
        // Layers are stored as integers (0-31) but have string names
        // 
        // LayerMask.NameToLayer("string") converts a layer name to its integer ID
        // Example: "PlayerCollisionOnly" might return 8 (if that's layer 8)
        // 
        // WHY "PlayerCollisionOnly" LAYER:
        // This layer is configured in Unity's physics settings to:
        // 1. Only collide with the Player layer (ignores enemies, projectiles, etc.)
        // 2. Prevents ammo boxes from colliding with bullets or grenades
        // 3. Prevents ammo boxes from blocking enemy line of sight
        // 4. Optimizes physics - fewer collision checks = better performance
        // 
        // LAYER MATRIX:
        // Unity has a Layer Collision Matrix (Edit > Project Settings > Physics)
        // It's a grid showing which layers can collide with which other layers
        // Example setup for AmmoBox:
        //   PlayerCollisionOnly ✓ Player (can collide)
        //   PlayerCollisionOnly ✗ Enemy (can't collide)
        //   PlayerCollisionOnly ✗ Projectile (can't collide)
        // 
        // PERFORMANCE BENEFIT:
        // By limiting collision checks, we reduce CPU load
        // In a level with 100 ammo boxes and 50 enemies:
        // - Without layer filtering: 5000+ collision checks per frame
        // - With layer filtering: Only checks against 1 player = much faster
        gameObject.layer = LayerMask.NameToLayer("PlayerCollisionOnly");

        // ====================================================================
        // CONFIGURE COLLIDER AS TRIGGER
        // ====================================================================

        // Get the Collider component and set it to be a trigger
        // 
        // GetComponent<T>() is a Unity method that:
        // - Searches for a component of type T on this GameObject
        // - Returns the component if found, or null if not found
        // - In this case, we're guaranteed to have a Collider because of [RequireComponent]
        // 
        // WHAT IS A TRIGGER:
        // Unity colliders have two modes:
        // 1. SOLID COLLIDER (isTrigger = false):
        //    - Objects bounce off it
        //    - Applies physics forces
        //    - Blocks movement
        //    - Calls OnCollisionEnter/Stay/Exit
        //    Example: Walls, floors, solid objects
        // 
        // 2. TRIGGER COLLIDER (isTrigger = true):
        //    - Objects pass through it
        //    - No physics forces applied
        //    - Doesn't block movement
        //    - Calls OnTriggerEnter/Stay/Exit instead
        //    Example: Pickup items, checkpoint zones, trigger volumes
        // 
        // WHY USE TRIGGER FOR AMMO BOX:
        // - Player can walk through it (doesn't block path)
        // - Detects when player enters the collider volume
        // - Doesn't apply physics forces that would push player around
        // - Player can easily collect it by walking over/through it
        // - No bouncing or pushing like a physics object
        // 
        // COMPARISON TO SOLID COLLIDER:
        // If isTrigger was false (solid):
        // - Player would bump into the ammo box and stop
        // - Box might get pushed around the level
        // - Player would have to precisely touch it to pick it up
        // - Could block doorways or narrow passages (bad design!)
        // 
        // With isTrigger = true:
        // - Player walks through the ammo box smoothly
        // - Box stays in place (not affected by physics)
        // - Easy to collect - just walk near it
        // - Won't block level geometry or player movement
        // 
        // CALLBACK METHOD:
        // Setting isTrigger = true means:
        // - OnTriggerEnter() will be called when something enters the collider
        // - OnTriggerStay() would be called each frame while inside
        // - OnTriggerExit() would be called when leaving the collider
        // We use OnTriggerEnter() below to detect player pickup
        GetComponent<Collider>().isTrigger = true;

        // After this method completes:
        // - GameObject is on "PlayerCollisionOnly" layer
        // - Collider is set to trigger mode
        // - AmmoBox is ready to detect player and be picked up
        // - Designer doesn't have to manually configure these settings
    }

    // ========================================================================
    // ONTRIGGERENTER - Unity physics callback method
    // ========================================================================
    // AUTOMATIC CALLING:
    // Unity's physics system automatically calls this method when:
    // 1. This GameObject has a Collider with isTrigger = true (we set this in Reset)
    // 2. Another GameObject with a Collider enters this trigger volume
    // 3. At least one of the GameObjects has a Rigidbody component
    //    (The player's CharacterController acts like a Rigidbody for triggers)
    //
    // METHOD SIGNATURE:
    // - void = returns nothing
    // - OnTriggerEnter = specific name Unity looks for (case-sensitive!)
    // - Collider other = the collider that entered our trigger
    //   "other" is a reference to the OTHER object's collider, not ours
    //
    // WHEN THIS IS CALLED:
    // - Player walks into the ammo box area
    // - Player's CharacterController's collider overlaps this trigger
    // - Unity detects the overlap and calls this method ONCE
    // - "other" parameter contains the player's collider
    //
    // FRAME TIMING:
    // - Called during Unity's physics update step (FixedUpdate phase)
    // - May be called multiple times per frame if multiple colliders enter
    // - Only called ONCE when entering, not continuously while inside
    //   (For continuous detection, use OnTriggerStay instead)
    //
    // COLLIDER MUST HAVE:
    // For this to work, the player must have:
    // - A Collider component (CharacterController in this case)
    // - CharacterController is a special collider type for characters
    // - It handles movement, slopes, stairs automatically
    // - Acts like a capsule-shaped collider for trigger detection
    // ========================================================================

    /// <summary>
    /// Called automatically by Unity when another collider enters this trigger
    /// Detects if the player touched the ammo box and grants them ammunition
    /// </summary>
    /// <param name="other">The Collider component that entered our trigger
    /// This could be:
    /// - The player's CharacterController
    /// - An enemy's collider (we'll ignore this)
    /// - A projectile's collider (we'll ignore this)
    /// - Any other collider in the game (we'll ignore this)
    /// We only care about the player, so we check for the Controller component
    /// </param>
    void OnTriggerEnter(Collider other)
    {
        // ====================================================================
        // PLAYER DETECTION - Check if the colliding object is the player
        // ====================================================================

        // Try to get the Controller component from the GameObject that entered
        // 
        // BREAKDOWN OF THIS LINE:
        // - 'other' is the Collider that entered our trigger (passed as parameter)
        // - '.GetComponent<Controller>()' searches for a Controller script on that GameObject
        // - GetComponent returns the Controller if found, or null if not found
        // - We store the result in variable 'c' (short for controller)
        // 
        // WHY THIS WORKS FOR PLAYER DETECTION:
        // - Only the player GameObject has a Controller component
        // - Enemies don't have Controller (they use different AI scripts)
        // - Projectiles don't have Controller (they use projectile scripts)
        // - Environment objects don't have Controller
        // - So if GetComponent<Controller>() returns non-null, it's the player!
        // 
        // TYPE:
        // Controller is the main player controller class in the FPS Kit
        // It's a MonoBehaviour that handles:
        // - Player movement (walking, running, jumping)
        // - Camera control (mouse look)
        // - Weapon management (switching, firing)
        // - Ammo inventory (the dictionary that stores ammo amounts)
        // 
        // NULL HANDLING:
        // If the entering collider is NOT the player, GetComponent returns null
        // Example scenarios:
        // - Enemy walks through (their layer might not even allow collision, but just in case)
        // - A bullet passes through (shouldn't happen due to layers, but defensive programming)
        // - Another pickup item somehow overlaps (shouldn't happen but good to check)
        // The if statement below will catch null and do nothing
        Controller c = other.GetComponent<Controller>();

        // ====================================================================
        // NULL CHECK - Verify we found the player
        // ====================================================================

        // Check if we successfully found a Controller component
        // 
        // if (c != null) means "if c is NOT null" / "if we found a Controller"
        // 
        // WHY THIS CHECK IS NECESSARY:
        // GetComponent can return null for several reasons:
        // 1. The GameObject doesn't have a Controller component (not the player)
        // 2. The GameObject is destroyed or invalid
        // 3. The component is disabled (though that's unlikely with Controller)
        // 
        // Without this check, the next line would cause a NullReferenceException
        // Example error: "NullReferenceException: Object reference not set to an instance"
        // This crashes the game or prints errors in the console
        // 
        // CONTROL FLOW:
        // - If c is null (not the player): Skip the code block, do nothing
        // - If c is not null (is the player): Execute the code block
        // 
        // This is called "defensive programming" or "null safety"
        // Always check if GetComponent returned something before using it!
        if (c != null)
        {
            // ================================================================
            // GRANT AMMO TO PLAYER
            // ================================================================

            // Call the ChangeAmmo method on the Controller to add ammo
            // 
            // BREAKDOWN:
            // - 'c' is the Controller component (the player's inventory manager)
            // - '.ChangeAmmo()' is a public method on Controller that modifies ammo
            // - First parameter: ammoType (the ID of which ammo to change)
            // - Second parameter: amount (how much to add/subtract)
            // 
            // WHAT HAPPENS IN CONTROLLER.CHANGEAMMO():
            // 1. Controller has a dictionary: Dictionary<int, int> m_AmmoInventory
            // 2. The dictionary maps ammo type IDs to ammo counts
            //    Example: { 0: 45, 1: 120, 2: 18 }
            //    Meaning: Pistol=45 rounds, Rifle=120 rounds, Shotgun=18 shells
            // 3. ChangeAmmo looks up the ammoType in the dictionary
            // 4. It adds 'amount' to the current count
            // 5. It clamps the result between 0 and 999 (min and max)
            // 6. It updates the UI to show the new ammo count
            // 7. If the current weapon uses this ammo type, it may re-enable the weapon
            // 
            // EXAMPLE SCENARIO:
            // Player has:
            //   Rifle ammo (type 1): 45 rounds
            // 
            // Player picks up ammo box with:
            //   ammoType = 1 (rifle ammo)
            //   amount = 30
            // 
            // ChangeAmmo(1, 30) is called:
            //   1. Looks up type 1 in dictionary: currently 45
            //   2. Adds 30: 45 + 30 = 75
            //   3. Clamps to [0, 999]: 75 is in range, keep 75
            //   4. Updates dictionary: type 1 now has 75 rounds
            //   5. If rifle is equipped, updates UI to show "75"
            // 
            // NEGATIVE AMOUNTS:
            // While not used in AmmoBox, ChangeAmmo also supports negative amounts
            // Example: Weapon firing calls ChangeAmmo(ammoType, -1) to consume 1 bullet
            // The clamping ensures it never goes below 0
            // 
            // WHY USE A METHOD INSTEAD OF DIRECT DICTIONARY ACCESS:
            // ChangeAmmo() handles several important tasks:
            // - Creates the dictionary entry if it doesn't exist (first time getting this ammo)
            // - Clamps values to prevent negative ammo or overflow
            // - Updates the UI automatically
            // - Re-enables weapons that were out of ammo
            // - Plays sound effects (if implemented)
            // Direct dictionary access would skip all this logic and cause bugs!
            c.ChangeAmmo(ammoType, amount);

            // ================================================================
            // DESTROY PICKUP - Remove from game world
            // ================================================================

            // Destroy this GameObject to remove the ammo box from the game
            // 
            // Destroy(GameObject obj) is a Unity method that:
            // - Marks the GameObject for destruction
            // - Removes it from the scene hierarchy
            // - Frees up memory
            // - Stops all scripts on the GameObject
            // - Removes all child GameObjects
            // 
            // gameObject (lowercase 'g') is a built-in property that:
            // - Every MonoBehaviour has automatically
            // - References the GameObject this script is attached to
            // - Gives access to the GameObject's properties and methods
            // 
            // WHY DESTROY:
            // Once the player picks up the ammo, we need to remove it because:
            // 1. Player shouldn't be able to pick it up again (would be infinite ammo)
            // 2. Visual feedback - item disappears to show it was collected
            // 3. Performance - fewer objects in scene = better frame rate
            // 4. Memory - destroyed objects free up RAM
            // 
            // TIMING OF DESTRUCTION:
            // Destroy() doesn't delete the object immediately!
            // - The object is marked for destruction
            // - It's actually destroyed at the end of the current frame
            // - This is after all Update() methods have finished
            // - Scripts can still reference it until end of frame
            // - After that, accessing it gives NullReferenceException
            // 
            // IMMEDIATE DESTRUCTION:
            // If you need instant destruction, use DestroyImmediate()
            // But this is dangerous and usually only for Editor scripts!
            // For gameplay, always use Destroy() - it's safer
            // 
            // ALTERNATIVE APPROACHES:
            // Some games don't destroy pickups, they:
            // 1. Disable them: gameObject.SetActive(false)
            // 2. Move them far away: transform.position = new Vector3(0, -1000, 0)
            // 3. Start a respawn timer: Invoke("Respawn", 30.0f)
            // 4. Use object pooling: PoolSystem.Instance.Return(this)
            // 
            // But for this simple ammo box, destruction is the cleanest approach
            // The designer places a finite number of ammo boxes in the level
            // Once collected, they're gone permanently (until level restart)
            Destroy(gameObject);

            // After this line:
            // - Player has more ammo of the specified type
            // - UI shows the updated ammo count
            // - This GameObject will be destroyed at end of frame
            // - Players see the ammo box disappear (collected!)
            // - OnTriggerEnter won't be called again (object is gone)
        }
        // If c was null (not the player), we do nothing:
        // - The collider passes through harmlessly
        // - AmmoBox stays in the world
        // - Waits for the actual player to collect it
    }
}

// ============================================================================
// END OF AMMOBOX CLASS
// ============================================================================
// 
// SUMMARY: AmmoBox is a simple but essential pickup system
// 
// KEY CONCEPTS DEMONSTRATED:
// - RequireComponent attribute for enforcing dependencies
// - Trigger colliders for pass-through detection
// - Layer-based collision filtering for performance
// - GetComponent for finding specific scripts on GameObjects
// - Null checking for safe component access
// - Destroy for removing objects from the game world
// - Reset method for automatic configuration in Unity Editor
//
// GAME DESIGN PATTERN:
// This implements the classic "pickup item" pattern used in countless games:
// 1. Item exists in world (AmmoBox GameObject with this script)
// 2. Player enters trigger volume (OnTriggerEnter is called)
// 3. Player receives benefit (ammo added to inventory)
// 4. Item disappears (Destroy removes it from world)
//
// VARIATIONS OF THIS PATTERN:
// You can easily modify this script for other pickup types:
// - Health pickup: c.ChangeHealth(amount)
// - Weapon pickup: c.PickupWeapon(weaponPrefab)
// - Key/collectible: c.AddKey(keyType)
// - Power-up: c.ApplyPowerUp(powerUpType, duration)
//
// EXTENSION IDEAS:
// - Add pickup sound effect: AudioSource.PlayClipAtPoint(pickupSound, transform.position)
// - Add pickup particle effect: Instantiate(pickupEffect, transform.position, Quaternion.identity)
// - Add floating/rotating animation: transform.Rotate(Vector3.up * Time.deltaTime * rotationSpeed)
// - Add respawn timer: Instead of Destroy, disable and re-enable after delay
// - Add max ammo check: Only pick up if player needs ammo (if GetAmmo(ammoType) < maxAmmo)
// - Add pickup prompt UI: Show "Press E to pickup" when player is near
//
// RELATED FPS KIT SYSTEMS:
// - Controller.cs: Player controller with ChangeAmmo() method
// - Weapon.cs: Defines AmmoTypeAttribute for Inspector dropdown
// - GameDatabase.cs: Stores ammo type definitions (IDs, names, icons)
// - WeaponInfoUI.cs: Updates HUD when ammo changes
// - PoolSystem.cs: Could be used to pool ammo boxes instead of destroying
//
// PHYSICS REQUIREMENTS:
// For this script to work properly:
// - This GameObject needs: Collider (with isTrigger=true) ✓ [RequireComponent ensures this]
// - Player GameObject needs: Collider or CharacterController ✓ [Controller has CharacterController]
// - At least one needs: Rigidbody or CharacterController ✓ [CharacterController counts]
// - Layer collision matrix must allow "PlayerCollisionOnly" to collide with "Player" layer ✓
//
// ============================================================================