using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Serializable attribute allows this class to be shown and edited in the Unity Inspector
[System.Serializable]
public class AmmoInventoryEntry
{
    // Custom attribute for ammo type selection - creates a dropdown in the inspector
    [AmmoType]
    public int ammoType;
    // Default amount of ammo for this type
    public int amount = 0;
}

// Main controller class that handles all player input and movement
public class Controller : MonoBehaviour
{
    // NOTE: This comment reflects the original author's concern about using a Singleton pattern
    // Singleton pattern - allows any script to access this controller instance via Controller.Instance
    // This is useful for UI scripts, weapons, and other systems that need to communicate with the player
    public static Controller Instance { get; protected set; }

    // ============= CAMERA SETUP =============
    // MainCamera: The actual camera that renders the game world for the player
    public Camera MainCamera;
    // WeaponCamera: A separate camera used specifically for rendering weapons/arms (common in FPS games)
    // This prevents weapons from clipping through walls and ensures they always render on top
    public Camera WeaponCamera;

    // CameraPosition: Transform that controls where the camera is positioned and how it rotates for looking up/down
    public Transform CameraPosition;
    // WeaponPosition: Transform where weapons are attached - should be child of CameraPosition so weapons move with camera
    public Transform WeaponPosition;

    // ============= STARTING EQUIPMENT =============
    // Array of weapon prefabs that the player begins the game with
    public Weapon[] startingWeapons;

    // NOTE: This is only used at game start to initialize ammo - m_AmmoInventory is used during actual gameplay
    // Array of starting ammo amounts for different ammo types
    public AmmoInventoryEntry[] startingAmmo;

    // ============= CONTROL SETTINGS =============
    // Header attribute creates a nice section divider in the Unity Inspector
    [Header("Control Settings")]
    // MouseSensitivity: How fast the camera rotates when moving the mouse - higher = faster turning
    public float MouseSensitivity = 100.0f;
    // PlayerSpeed: How fast the character moves when walking (units per second)
    public float PlayerSpeed = 5.0f;
    // RunningSpeed: How fast the character moves when holding the run key (units per second)
    public float RunningSpeed = 7.0f;
    // JumpSpeed: Initial upward velocity when jumping (units per second) - higher = higher jumps
    public float JumpSpeed = 5.0f;

    // ============= AUDIO COMPONENTS =============
    [Header("Audio")]
    // RandomPlayer: Component that plays random footstep sounds from a collection
    public RandomPlayer FootstepPlayer;
    // Audio clips for jumping and landing actions
    public AudioClip JumpingAudioCLip;
    public AudioClip LandingAudioClip;

    // ============= PRIVATE MOVEMENT VARIABLES =============
    // Current vertical velocity (for gravity and jumping) - negative = falling, positive = rising
    float m_VerticalSpeed = 0.0f;
    // Whether the game is currently paused (affects input processing)
    bool m_IsPaused = false;
    // Index of the currently selected weapon in the m_Weapons list
    int m_CurrentWeapon;

    // Camera rotation angles - separated into vertical (up/down) and horizontal (left/right)
    float m_VerticalAngle, m_HorizontalAngle;

    // ============= PUBLIC PROPERTIES =============
    // Current movement speed - can be read by other scripts but only set by this controller
    // Auto-implemented property with private setter
    public float Speed { get; private set; } = 0.0f;

    // LockControl: When true, disables all player input (useful for cutscenes, menus, etc.)
    public bool LockControl { get; set; }
    // CanPause: When true, allows the player to open the pause menu
    public bool CanPause { get; set; } = true;

    // Grounded: Read-only property that returns whether the player is on the ground
    // Uses lambda expression for a simple getter that returns m_Grounded
    public bool Grounded => m_Grounded;

    // ============= UNITY COMPONENTS =============
    // Reference to the CharacterController component - handles collision detection and movement
    CharacterController m_CharacterController;

    // ============= GROUNDING SYSTEM =============
    // Custom grounding detection (more reliable than CharacterController.isGrounded)
    bool m_Grounded;
    // Timer used to prevent rapid grounded/not-grounded state changes on small bumps
    float m_GroundedTimer;
    // Stores the player's speed when they leave the ground (for air movement)
    float m_SpeedAtJump = 0.0f;

    // ============= WEAPON AND INVENTORY SYSTEMS =============
    // List of all weapons the player currently owns
    List<Weapon> m_Weapons = new List<Weapon>();
    // Dictionary storing ammo counts for each ammo type (key = ammo type ID, value = amount)
    Dictionary<int, int> m_AmmoInventory = new Dictionary<int, int>();

    // ============= UNITY LIFECYCLE METHODS =============

    // Awake() is called before Start() - used for internal initialization
    void Awake()
    {
        // Set up the Singleton pattern - makes this controller accessible globally
        Instance = this;
    }

    // Start() is called once at the beginning of the game after all objects are initialized
    void Start()
    {
        // Lock the cursor to the center of the screen and hide it (standard for FPS games)
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Initialize game state
        m_IsPaused = false;
        m_Grounded = true;

        // ============= CAMERA SETUP =============
        // Attach the main camera to the CameraPosition transform
        // SetParent(parent, worldPositionStays) - false means use local position/rotation
        MainCamera.transform.SetParent(CameraPosition, false);
        // Reset camera to local origin and rotation
        MainCamera.transform.localPosition = Vector3.zero;
        MainCamera.transform.localRotation = Quaternion.identity;

        // Get the CharacterController component attached to this GameObject
        m_CharacterController = GetComponent<CharacterController>();

        // ============= WEAPON INITIALIZATION =============
        // Give the player all their starting weapons
        for (int i = 0; i < startingWeapons.Length; ++i)
        {
            PickupWeapon(startingWeapons[i]);
        }

        // Grant starting ammo amounts
        for (int i = 0; i < startingAmmo.Length; ++i)
        {
            ChangeAmmo(startingAmmo[i].ammoType, startingAmmo[i].amount);
        }

        // Set current weapon to -1 (none selected) then switch to weapon index 0
        m_CurrentWeapon = -1;
        ChangeWeapon(0);

        // NOTE: This loop seems redundant as ChangeAmmo was already called above
        // This might be leftover code or a safeguard to ensure ammo inventory is properly initialized
        for (int i = 0; i < startingAmmo.Length; ++i)
        {
            m_AmmoInventory[startingAmmo[i].ammoType] = startingAmmo[i].amount;
        }

        // ============= CAMERA ANGLE INITIALIZATION =============
        // Start looking straight ahead (no vertical tilt)
        m_VerticalAngle = 0.0f;
        // Initialize horizontal angle to match the character's current Y rotation
        m_HorizontalAngle = transform.localEulerAngles.y;
    }

    // Update() is called once per frame - handles all input and continuous updates
    void Update()
    {
        // ============= MENU INPUT =============
        // Check if pause is allowed and the Menu button (usually Escape) was pressed this frame
        // GetButtonDown returns true only on the frame the button was first pressed
        if (CanPause && Input.GetButtonDown("Menu"))
        {
            // Display the pause menu (uses another Singleton pattern)
            PauseMenu.Instance.Display();
        }

        // Toggle fullscreen map visibility based on whether Map button is held down
        // GetButton returns true while the button is held (unlike GetButtonDown)
        FullscreenMap.Instance.gameObject.SetActive(Input.GetButton("Map"));

        // ============= GROUNDING DETECTION =============
        // Store previous grounding state to detect when the player lands
        bool wasGrounded = m_Grounded;
        // Flag to track if the player just lost contact with the ground
        bool loosedGrounding = false;

        // Custom grounding system - Unity's CharacterController.isGrounded can flicker on small steps
        // We only consider the player "not grounded" if they've been off the ground for at least 0.5 seconds
        if (!m_CharacterController.isGrounded)
        {
            // If we think we're grounded but CharacterController says we're not
            if (m_Grounded)
            {
                // Increment the timer
                m_GroundedTimer += Time.deltaTime;
                // After 0.5 seconds of being "not grounded", actually mark as not grounded
                if (m_GroundedTimer >= 0.5f)
                {
                    loosedGrounding = true;
                    m_Grounded = false;
                }
            }
        }
        else
        {
            // CharacterController says we're grounded, so reset timer and mark as grounded
            m_GroundedTimer = 0.0f;
            m_Grounded = true;
        }

        // Reset speed and movement vector for this frame
        Speed = 0;
        Vector3 move = Vector3.zero;

        // ============= PLAYER INPUT PROCESSING =============
        // Only process input if the game isn't paused and controls aren't locked
        if (!m_IsPaused && !LockControl)
        {
            // ============= JUMPING =============
            // Check for jump input - only allow jumping if grounded
            // GetButtonDown ensures jump only triggers once per press (prevents bunny hopping)
            if (m_Grounded && Input.GetButtonDown("Jump"))
            {
                // Set upward velocity
                m_VerticalSpeed = JumpSpeed;
                // Immediately mark as not grounded to prevent immediate re-jumping
                m_Grounded = false;
                loosedGrounding = true;
                // Play jump sound with slight pitch variation (0.8f to 1.1f)
                FootstepPlayer.PlayClip(JumpingAudioCLip, 0.8f, 1.1f);
            }

            // ============= RUNNING/WALKING SPEED =============
            // Can only run if the current weapon is idle (not firing/reloading) and Run button is held
            bool running = m_Weapons[m_CurrentWeapon].CurrentState == Weapon.WeaponState.Idle && Input.GetButton("Run");
            // Choose speed based on whether player is running
            float actualSpeed = running ? RunningSpeed : PlayerSpeed;

            // If the player just left the ground, store their current speed for air movement
            if (loosedGrounding)
            {
                m_SpeedAtJump = actualSpeed;
            }

            // ============= MOVEMENT INPUT =============
            // Get horizontal movement input (A/D keys or arrow keys)
            // Get vertical movement input (W/S keys or arrow keys)
            // GetAxis returns smoothed input (-1 to 1), GetAxisRaw returns immediate input (-1, 0, or 1)
            move = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxisRaw("Vertical"));

            // Normalize diagonal movement to prevent faster movement when moving diagonally
            // sqrMagnitude is more efficient than magnitude for comparison
            if (move.sqrMagnitude > 1.0f)
                move.Normalize();

            // Use current speed if grounded, or the speed when jumping if in air
            // This prevents players from changing their air speed mid-jump
            float usedSpeed = m_Grounded ? actualSpeed : m_SpeedAtJump;

            // Scale movement by speed and frame time (Time.deltaTime)
            // Time.deltaTime makes movement frame-rate independent
            move = move * usedSpeed * Time.deltaTime;

            // Convert movement from local space (relative to player) to world space
            // TransformDirection applies the player's rotation to the movement vector
            move = transform.TransformDirection(move);

            // Apply the movement using CharacterController (handles collision detection)
            m_CharacterController.Move(move);

            // ============= MOUSE LOOK - HORIZONTAL (LEFT/RIGHT) =============
            // Get horizontal mouse movement and apply sensitivity
            float turnPlayer = Input.GetAxis("Mouse X") * MouseSensitivity;
            // Add to current horizontal angle
            m_HorizontalAngle = m_HorizontalAngle + turnPlayer;

            // Keep angle within 0-360 degrees to prevent floating point precision issues
            if (m_HorizontalAngle > 360) m_HorizontalAngle -= 360.0f;
            if (m_HorizontalAngle < 0) m_HorizontalAngle += 360.0f;

            // Apply horizontal rotation to the player's body (Y-axis rotation)
            Vector3 currentAngles = transform.localEulerAngles;
            currentAngles.y = m_HorizontalAngle;
            transform.localEulerAngles = currentAngles;

            // ============= MOUSE LOOK - VERTICAL (UP/DOWN) =============
            // Get vertical mouse movement (negated because mouse Y is inverted compared to camera rotation)
            var turnCam = -Input.GetAxis("Mouse Y");
            turnCam = turnCam * MouseSensitivity;
            // Clamp vertical angle to prevent over-rotation (can't look completely upside down)
            m_VerticalAngle = Mathf.Clamp(turnCam + m_VerticalAngle, -89.0f, 89.0f);
            // Apply vertical rotation to the camera position (X-axis rotation)
            currentAngles = CameraPosition.transform.localEulerAngles;
            currentAngles.x = m_VerticalAngle;
            CameraPosition.transform.localEulerAngles = currentAngles;

            // ============= WEAPON INPUT =============
            // Send left mouse button state to the current weapon
            // GetMouseButton(0) returns true while left mouse button is held
            m_Weapons[m_CurrentWeapon].triggerDown = Input.GetMouseButton(0);

            // Calculate current speed for other systems (like footstep audio)
            // Divide by PlayerSpeed * Time.deltaTime to get a normalized speed value
            Speed = move.magnitude / (PlayerSpeed * Time.deltaTime);

            // ============= RELOAD INPUT =============
            // Check if reload button is held and trigger weapon reload
            if (Input.GetButton("Reload"))
                m_Weapons[m_CurrentWeapon].Reload();

            // ============= WEAPON SWITCHING - MOUSE WHEEL =============
            // Mouse wheel down - switch to previous weapon
            if (Input.GetAxis("Mouse ScrollWheel") < 0)
            {
                ChangeWeapon(m_CurrentWeapon - 1);
            }
            // Mouse wheel up - switch to next weapon
            else if (Input.GetAxis("Mouse ScrollWheel") > 0)
            {
                ChangeWeapon(m_CurrentWeapon + 1);
            }

            // ============= WEAPON SWITCHING - NUMBER KEYS =============
            // Check number keys 0-9 for direct weapon selection
            for (int i = 0; i < 10; ++i)
            {
                // Check if the number key was pressed this frame
                // KeyCode.Alpha0 is the '0' key, Alpha1 is '1', etc.
                if (Input.GetKeyDown(KeyCode.Alpha0 + i))
                {
                    int num = 0;
                    // Special case: '0' key selects weapon slot 10 (if it exists)
                    if (i == 0)
                        num = 10;
                    else
                        // Keys 1-9 select weapon slots 0-8 respectively
                        num = i - 1;

                    // Only switch if the weapon slot exists
                    if (num < m_Weapons.Count)
                    {
                        ChangeWeapon(num);
                    }
                }
            }
        }

        // ============= GRAVITY SYSTEM =============
        // Apply gravity to vertical speed - 10.0f is gravity strength (units/sec²)
        // Time.deltaTime makes gravity frame-rate independent
        m_VerticalSpeed = m_VerticalSpeed - 10.0f * Time.deltaTime;

        // Clamp maximum fall speed to prevent infinitely fast falling
        if (m_VerticalSpeed < -10.0f)
            m_VerticalSpeed = -10.0f; // max fall speed

        // Create vertical movement vector and apply it
        var verticalMove = new Vector3(0, m_VerticalSpeed * Time.deltaTime, 0);
        // Move() returns collision flags indicating what was hit
        var flag = m_CharacterController.Move(verticalMove);

        // If we hit something below us (ground), stop falling
        // CollisionFlags.Below indicates collision with the ground
        if ((flag & CollisionFlags.Below) != 0)
            m_VerticalSpeed = 0;

        // ============= LANDING AUDIO =============
        // If we just landed (weren't grounded last frame but are now), play landing sound
        if (!wasGrounded && m_Grounded)
        {
            FootstepPlayer.PlayClip(LandingAudioClip, 0.8f, 1.1f);
        }
    }

    // ============= CURSOR/PAUSE CONTROL =============
    /// <summary>
    /// Controls cursor visibility and pause state - used by menus and UI systems
    /// </summary>
    /// <param name="display">True to show cursor and pause, false to hide cursor and unpause</param>
    public void DisplayCursor(bool display)
    {
        m_IsPaused = display;
        // Set cursor lock mode: Locked = center of screen and invisible, None = free movement
        Cursor.lockState = display ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = display;
    }

    // ============= WEAPON MANAGEMENT =============
    /// <summary>
    /// Adds a weapon to the player's inventory or grants ammo if they already have it
    /// </summary>
    /// <param name="prefab">The weapon prefab to add</param>
    void PickupWeapon(Weapon prefab)
    {
        // TODO: The comment reflects the original author's concern about using name comparison
        // Check if we already have this weapon type (compares by name)
        if (m_Weapons.Exists(weapon => weapon.name == prefab.name))
        {
            // If we already have the weapon, just grant ammo instead
            ChangeAmmo(prefab.ammoType, prefab.clipSize);
        }
        else
        {
            // Create a new instance of the weapon
            var w = Instantiate(prefab, WeaponPosition, false);
            // Preserve the original name (Instantiate adds "(Clone)" to the name)
            w.name = prefab.name;
            // Position weapon at the weapon mount point
            w.transform.localPosition = Vector3.zero;
            w.transform.localRotation = Quaternion.identity;
            // Hide the weapon initially (only current weapon should be visible)
            w.gameObject.SetActive(false);

            // Initialize the weapon with a reference to this controller
            w.PickedUp(this);

            // Add to weapon list
            m_Weapons.Add(w);
        }
    }

    /// <summary>
    /// Switches to a different weapon by index, with wraparound support
    /// </summary>
    /// <param name="number">Index of weapon to switch to</param>
    void ChangeWeapon(int number)
    {
        // Hide current weapon if one is selected
        if (m_CurrentWeapon != -1)
        {
            // Tell current weapon it's being put away (may trigger holster animation)
            m_Weapons[m_CurrentWeapon].PutAway();
            // Hide the weapon GameObject
            m_Weapons[m_CurrentWeapon].gameObject.SetActive(false);
        }

        // Update current weapon index
        m_CurrentWeapon = number;

        // Handle wraparound - if index is negative, wrap to last weapon
        if (m_CurrentWeapon < 0)
            m_CurrentWeapon = m_Weapons.Count - 1;
        // If index is too high, wrap to first weapon
        else if (m_CurrentWeapon >= m_Weapons.Count)
            m_CurrentWeapon = 0;

        // Show and activate new weapon
        m_Weapons[m_CurrentWeapon].gameObject.SetActive(true);
        // Tell weapon it was selected (may trigger draw animation)
        m_Weapons[m_CurrentWeapon].Selected();
    }

    // ============= AMMO SYSTEM =============
    /// <summary>
    /// Gets the current amount of ammo for a specific ammo type
    /// </summary>
    /// <param name="ammoType">The ammo type ID to check</param>
    /// <returns>Amount of ammo available, or 0 if none</returns>
    public int GetAmmo(int ammoType)
    {
        int value = 0;
        // TryGetValue safely gets the value without throwing an exception if key doesn't exist
        m_AmmoInventory.TryGetValue(ammoType, out value);

        return value;
    }

    /// <summary>
    /// Modifies the amount of ammo for a specific type (can add or subtract)
    /// </summary>
    /// <param name="ammoType">The ammo type ID to modify</param>
    /// <param name="amount">Amount to add (positive) or remove (negative)</param>
    public void ChangeAmmo(int ammoType, int amount)
    {
        // Ensure the ammo type exists in the dictionary
        if (!m_AmmoInventory.ContainsKey(ammoType))
            m_AmmoInventory[ammoType] = 0;

        // Store previous amount to detect if we gained ammo for an empty weapon
        var previous = m_AmmoInventory[ammoType];
        // Update ammo amount with clamping (0 minimum, 999 maximum)
        m_AmmoInventory[ammoType] = Mathf.Clamp(m_AmmoInventory[ammoType] + amount, 0, 999);

        // If current weapon uses this ammo type, update UI and potentially re-enable weapon
        if (m_Weapons[m_CurrentWeapon].ammoType == ammoType)
        {
            // If we had no ammo but just gained some, re-enable the weapon
            if (previous == 0 && amount > 0)
            {
                // Re-select the weapon to update its state
                m_Weapons[m_CurrentWeapon].Selected();
            }

            // Update the weapon info UI with new ammo count
            WeaponInfoUI.Instance.UpdateAmmoAmount(GetAmmo(ammoType));
        }
    }

    /// <summary>
    /// Called by animation events to play footstep sounds during walking animations
    /// </summary>
    public void PlayFootstep()
    {
        FootstepPlayer.PlayRandom();
    }
}

/*
==============================================================================
UNITY 6 INPUT SYSTEM DIFFERENCES
==============================================================================

This script uses Unity's LEGACY INPUT SYSTEM (UnityEngine.Input class). 
With Unity 6, the NEW INPUT SYSTEM is now the default, though legacy input is still supported.

KEY DIFFERENCES between Legacy Input (used here) and New Input System:

1. INPUT DETECTION:
   Legacy: Input.GetAxis("Horizontal"), Input.GetButtonDown("Jump")
   New:    playerInput.actions["Move"].ReadValue<Vector2>(), jumpAction.WasPressedThisFrame()

2. SETUP:
   Legacy: Configure input in Edit > Project Settings > Input Manager
   New:    Create Input Action Assets (.inputactions files) with visual editor

3. MULTI-DEVICE SUPPORT:
   Legacy: Limited gamepad support, manual device switching
   New:    Built-in support for multiple controllers, automatic device switching

4. REBINDING:
   Legacy: Requires custom UI and code for key remapping
   New:    Built-in rebinding system with UI components

5. MOBILE/TOUCH:
   Legacy: Separate touch input handling required
   New:    Unified input handling across all platforms

6. PERFORMANCE:
   Legacy: Polls input every frame
   New:    Event-driven system, only processes when input changes

TO CONVERT THIS SCRIPT TO NEW INPUT SYSTEM:
1. Install Input System package (Window > Package Manager)
2. Create Input Actions asset with actions for Move, Look, Jump, etc.
3. Replace Input.GetAxis() calls with InputAction.ReadValue()
4. Replace Input.GetButtonDown() with InputAction.WasPressedThisFrame()
5. Add PlayerInput component and configure it

COMPATIBILITY:
- Both systems can coexist using Project Settings > Player > Active Input Handling: "Both"
- Legacy input works fine in Unity 6 but isn't the recommended approach for new projects
- Consider migrating to New Input System for future Unity versions and better features

==============================================================================
*/