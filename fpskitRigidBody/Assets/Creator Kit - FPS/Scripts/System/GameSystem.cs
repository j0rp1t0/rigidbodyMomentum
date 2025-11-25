// ============================================================================
// USING STATEMENTS - These import functionality from other code libraries
// ============================================================================

using System.Collections;          // Provides interfaces for collections like lists
using System.Collections.Generic;  // Provides generic collection types like List<T>
using UnityEditor;                 // Unity's editor-specific functionality (only available in editor)
using UnityEngine;                 // Core Unity engine functionality
using UnityEngine.SceneManagement; // Handles loading and managing scenes (levels)

// Conditional compilation: This code only exists in the Unity Editor, not in builds
#if UNITY_EDITOR
using UnityEditor.SceneManagement; // Editor-specific scene management tools
#endif

// ============================================================================
// GAMESYSTEM CLASS - The central manager for game state and progression
// ============================================================================
// This class manages:
// - Level progression and episode tracking
// - Game timer and scoring
// - Target counting and destruction tracking
// - Scene transitions between levels
//
// ARCHITECTURE: GameSystem is a Singleton that coordinates with:
// - GameDatabase: Stores all episode/level data
// - Controller: Player character controller
// - PoolSystem: Object pooling for performance
// - WorldAudioPool: Audio source pooling
// - GameSystemInfo: Updates on-screen UI (timer, score)
// - MinimapUI/FullscreenMap: Map rendering
// - LevelSelectionUI: Level selection menu
// - FinalScoreUI: End-of-level results screen
// - Target: Shootable target objects
// ============================================================================

public class GameSystem : MonoBehaviour
{
    // ========================================================================
    // SINGLETON PATTERN - Ensures only one GameSystem exists in the scene
    // ========================================================================

    /// <summary>
    /// Static reference to the single GameSystem instance
    /// 'get' is public - anyone can read it (e.g., Target.cs calls GameSystem.Instance.TargetDestroyed())
    /// 'set' is private - only this class can assign it (happens in Awake)
    /// This is the Singleton pattern: ensures only one GameSystem exists globally
    /// Other scripts access this via: GameSystem.Instance.SomeMethod()
    /// </summary>
    public static GameSystem Instance { get; private set; }

    // ========================================================================
    // EPISODE AND LEVEL TRACKING - Persists between scene loads
    // ========================================================================

    /// <summary>
    /// The current episode (campaign/chapter) the player is on
    /// Static = shared across all instances and persists between scene loads
    /// When Unity loads a new scene, non-static variables reset, but static variables keep their values
    /// -1 = not initialized yet (we haven't figured out which episode we're in)
    /// This index corresponds to GameDatabase.Instance.episodes[s_CurrentEpisode]
    /// </summary>
    static int s_CurrentEpisode = -1;

    /// <summary>
    /// The current level within the current episode
    /// Static = persists when scenes reload
    /// -1 = not initialized yet
    /// This index corresponds to GameDatabase.Instance.episodes[s_CurrentEpisode].scenes[s_CurrentLevel]
    /// Example: Episode 1, Level 3 means s_CurrentEpisode=0, s_CurrentLevel=2 (arrays start at 0)
    /// </summary>
    static int s_CurrentLevel = -1;

    // ========================================================================
    // PUBLIC INSPECTOR FIELDS - Can be set in Unity Editor
    // ========================================================================
    // These fields appear in the Unity Inspector when you select the GameObject
    // Designers can drag and drop references to configure the game
    // ========================================================================

    /// <summary>
    /// Array of prefabs to instantiate when the game starts
    /// Prefabs are reusable GameObject templates stored in the project
    /// These might be managers, UI systems, or other essential game objects
    /// Set in Unity Inspector by dragging prefabs into the array
    /// Example: Could contain UI manager prefabs, audio system prefabs, etc.
    /// These are spawned in Awake() before any other initialization
    /// </summary>
    public GameObject[] StartPrefabs;

    /// <summary>
    /// Time penalty (in seconds) added for each target the player missed
    /// Default is 1.0 second per missed target
    /// Used in GetFinalTime() to calculate the final time score
    /// This encourages players to hit all targets for the best time
    /// Example: If player missed 3 targets with penalty 1.0, they get +3.0 seconds added
    /// Can be adjusted in Inspector to make the game more/less forgiving
    /// </summary>
    public float TargetMissedPenalty = 1.0f;

    /// <summary>
    /// AudioSource component that plays background music during gameplay
    /// AudioSource is Unity's component for playing sounds
    /// Set in Unity Inspector by dragging the AudioSource component reference here
    /// In FinishRun(), we swap this to play the end game sound instead of music
    /// </summary>
    public AudioSource BGMPlayer;

    /// <summary>
    /// Sound effect to play when the game/level ends
    /// AudioClip is Unity's audio file container (like MP3, WAV, etc.)
    /// Set in Unity Inspector by dragging an audio file here
    /// Played through BGMPlayer when FinishRun() is called
    /// </summary>
    public AudioClip EndGameSound;

    // ========================================================================
    // PUBLIC PROPERTIES - Read-only access to internal state
    // ========================================================================
    // Properties use the "=>" syntax for expression-bodied members
    // They provide controlled access to private fields without allowing modification
    // Other scripts can READ these values but cannot CHANGE them
    // ========================================================================

    /// <summary>
    /// Read-only property that returns the current elapsed time in seconds
    /// The "=>" creates a getter that directly returns m_Timer
    /// External code can read: float time = GameSystem.Instance.RunTime;
    /// But cannot write: GameSystem.Instance.RunTime = 5.0f; // ERROR!
    /// Used by FinalScoreUI to display the player's time
    /// </summary>
    public float RunTime => m_Timer;

    /// <summary>
    /// Read-only property for total number of positive-value targets in the level
    /// Used by UI systems to display "X/Y targets destroyed"
    /// Only counts targets with pointValue > 0 (excludes penalty targets)
    /// FinalScoreUI uses this to show completion percentage
    /// </summary>
    public int TargetCount => m_TargetCount;

    /// <summary>
    /// Read-only property for number of targets destroyed so far
    /// Increments each time TargetDestroyed() is called by Target.cs
    /// Used to calculate level completion and missed target penalties
    /// </summary>
    public int DestroyedTarget => m_TargetDestroyed;

    /// <summary>
    /// Read-only property for the player's current score
    /// Score can be positive or negative depending on target types hit
    /// Positive-value targets increase score, negative-value targets decrease it
    /// Displayed in the HUD via GameSystemInfo
    /// </summary>
    public int Score => m_Score;

    // ========================================================================
    // PRIVATE FIELDS - Internal state (m_ prefix = member variable convention)
    // ========================================================================
    // The "m_" prefix is a common C# convention for private member variables
    // This helps distinguish member variables from local variables and parameters
    // ========================================================================

    /// <summary>
    /// Tracks how many seconds have elapsed since the timer started
    /// Updated every frame in Update() when m_TimerRunning is true
    /// Time.deltaTime is added each frame to make it framerate-independent
    /// Example: At 60 FPS, deltaTime ≈ 0.0167 seconds (1/60)
    /// Displayed to player via GameSystemInfo.UpdateTimer()
    /// </summary>
    float m_Timer;

    /// <summary>
    /// Flag indicating whether the timer is currently running
    /// False = timer is paused (during menus, before start, after finish)
    /// True = timer is counting up each frame
    /// Controlled by StartTimer() and StopTimer() methods
    /// Checked every frame in Update() to decide whether to increment m_Timer
    /// </summary>
    bool m_TimerRunning = false;

    /// <summary>
    /// Total number of positive-value targets in the current level
    /// Set by RetrieveTargetsCount() at level start
    /// Only counts targets with pointValue > 0
    /// Excludes negative-value targets (penalties/obstacles to avoid)
    /// Used to calculate level completion and missed target count
    /// </summary>
    int m_TargetCount;

    /// <summary>
    /// Number of targets the player has destroyed so far in this level
    /// Incremented by TargetDestroyed() method when Target.cs reports a hit
    /// Used to track progress: if m_TargetDestroyed == m_TargetCount, level is "complete"
    /// Also used to calculate missed targets: missedCount = m_TargetCount - m_TargetDestroyed
    /// </summary>
    int m_TargetDestroyed;

    /// <summary>
    /// Player's current score - sum of all target point values hit
    /// Can be positive or negative depending on targets
    /// Positive-value targets add points, negative-value targets subtract points
    /// Displayed in UI and final score screen
    /// Updated via TargetDestroyed(int score) when targets are hit
    /// </summary>
    int m_Score = 0;

    // ========================================================================
    // AWAKE - Unity lifecycle method, called when object is created
    // ========================================================================
    // UNITY LIFECYCLE ORDER:
    // 1. Awake() - Called when script instance loads (happens first)
    // 2. OnEnable() - Called when object becomes active
    // 3. Start() - Called before first frame (happens after all Awake() calls)
    // 4. Update() - Called every frame
    //
    // Awake() runs before Start() and is used for initialization that doesn't
    // depend on other objects being initialized. Perfect for Singleton setup.
    // ========================================================================

    /// <summary>
    /// Awake is called when the script instance is being loaded
    /// This happens before Start() and before the first frame
    /// Used here for critical initialization that other objects might need:
    /// - Setting up the Singleton pattern
    /// - Spawning essential prefabs
    /// - Creating the PoolSystem
    /// Other scripts can safely access GameSystem.Instance in their Start() methods
    /// </summary>
    void Awake()
    {
        // Assign this instance to the static Instance variable
        // This completes the Singleton pattern setup
        // From this point on, any script can access this GameSystem via GameSystem.Instance
        // Example: GameSystem.Instance.TargetDestroyed(10);
        Instance = this;

        // Loop through each prefab in the StartPrefabs array
        // 'foreach' iterates over every item in a collection
        // 'var' lets the compiler infer the type (GameObject in this case)
        // This is equivalent to: for(int i=0; i<StartPrefabs.Length; i++)
        foreach (var prefab in StartPrefabs)
        {
            // Instantiate creates a new instance (copy) of a prefab in the scene
            // Prefabs are templates - Instantiate spawns them as real GameObjects
            // This spawns essential game objects like UI managers, audio systems, etc.
            // These objects will persist until explicitly destroyed or the scene unloads
            Instantiate(prefab);
        }

        // Initialize the object pooling system
        // Object pooling is a performance optimization technique:
        // - Instead of creating/destroying objects repeatedly (expensive)
        // - We create a "pool" of reusable objects and reuse them
        // - Avoids garbage collection overhead and improves performance
        // PoolSystem.Create() creates a GameObject with PoolSystem component
        // Used by Target destruction effects, bullet projectiles, particle systems, etc.
        PoolSystem.Create();
    }

    // ========================================================================
    // START - Unity lifecycle method, called before first frame update
    // ========================================================================
    // Start() runs after all Awake() methods have completed in the scene
    // This means all Singletons and essential systems are initialized
    // Used for initialization that depends on other objects being ready
    // ========================================================================

    /// <summary>
    /// Start is called before the first frame update
    /// All Awake() methods have completed, so other objects are ready
    /// Used here to initialize systems and detect which level we're in
    /// This includes:
    /// - Initializing audio pooling
    /// - Counting targets in the scene
    /// - Detecting current episode/level (editor only)
    /// - Initializing UI displays
    /// </summary>
    void Start()
    {
        // Initialize the audio pooling system for world sound effects
        // WorldAudioPool is a specialized system that manages a pool of AudioSource components
        // This allows multiple sounds to play simultaneously without creating new AudioSources
        // Example: When 5 targets are destroyed at once, we need 5 AudioSources
        // Without pooling, we'd create/destroy 5 AudioSources (expensive)
        // With pooling, we reuse 5 AudioSources from the pool (efficient)
        WorldAudioPool.Init();

        // Count how many targets exist in the current level
        // This method scans the scene using Resources.FindObjectsOfTypeAll<Target>()
        // Sets m_TargetCount, resets m_TargetDestroyed to 0, and resets m_Score to 0
        // Only counts targets with positive point values
        // This must happen in Start() because spawners create targets in Awake()
        RetrieveTargetsCount();

        // ========================================================================
        // EDITOR-ONLY CODE - Only compiles when running in Unity Editor
        // ========================================================================
        // The #if UNITY_EDITOR directive is conditional compilation
        // This code ONLY exists in the Unity Editor build, NOT in the final game
        // In the final game build, this entire section doesn't exist at all
        // This saves memory and improves performance
        // ========================================================================

#if UNITY_EDITOR
        // WHY THIS CODE EXISTS:
        // In the Unity Editor, developers can open any scene file directly to test it
        // Example: A designer might open "Episode2/Level3.unity" directly
        // The game needs to know "which episode and level am I in right now?"
        // In a real game, this isn't needed because scenes always load sequentially

        // This is "inefficient" because we search through all episodes/levels
        // However, this is acceptable in the editor because:
        // 1. It only runs once at level start (not every frame)
        // 2. Development convenience is more important than editor performance
        // 3. This code doesn't exist in the final game at all

        // Get the file path of the currently active scene
        // Example path: "Assets/Scenes/Episode1/Level3.unity"
        // SceneManager.GetActiveScene() returns the current scene object
        // .path gives us its file path as a string
        string currentScene = SceneManager.GetActiveScene().path;

        // Nested for loops to search through all episodes and their scenes
        // This is a "search algorithm" - we're looking for a matching scene path

        // OUTER LOOP: iterate through each episode in the game database
        // i = episode index, counting from 0
        // GameDatabase.Instance.episodes is an array of Episode objects
        // GameDatabase is a ScriptableObject that stores all game data
        // Loop continues while TWO conditions are true (AND operator):
        //   1. i < array length (haven't checked all episodes yet)
        //   2. s_CurrentEpisode < 0 (haven't found the episode yet)
        // The loop stops early if we find the episode (optimization)
        for (int i = 0; i < GameDatabase.Instance.episodes.Length && s_CurrentEpisode < 0; ++i)
        {
            // INNER LOOP: iterate through each scene (level) in this episode
            // j = level index within the current episode
            // episodes[i].scenes is an array of scene file paths (strings)
            // Example: episodes[0].scenes[2] = "Assets/Scenes/Episode1/Level3.unity"
            for (int j = 0; j < GameDatabase.Instance.episodes[i].scenes.Length; ++j)
            {
                // Check if this scene path matches our current scene
                // String comparison: does the database path equal the active scene path?
                // GameDatabase.Instance.episodes[i].scenes[j] = scene path at episode i, level j
                if (GameDatabase.Instance.episodes[i].scenes[j] == currentScene)
                {
                    // Found it! We've identified which episode and level we're in
                    // Store the episode and level indices in static variables
                    // These will persist when the scene reloads (static variables survive scene changes)
                    s_CurrentEpisode = i;
                    s_CurrentLevel = j;

                    // Exit the inner loop immediately using 'break'
                    // break stops the current loop (the j loop)
                    // The outer loop (i loop) will also stop automatically because:
                    // s_CurrentEpisode is no longer < 0, so the condition becomes false
                    break;
                }
            }
        }
        // If we finish both loops without finding the scene:
        // s_CurrentEpisode and s_CurrentLevel remain -1
        // This means we're in a "test scene" not listed in the database

#else
        // This code ONLY exists in the FINAL GAME BUILD (not in editor)
        // #else means "if the condition above is false"
        // So this runs when UNITY_EDITOR is NOT defined (i.e., in builds)
        
        // In the final game, we always start from the beginning
        // Players can't jump to arbitrary levels like developers can in the editor
        // The game flows: Menu → Episode 1 Level 1 → Episode 1 Level 2 → etc.
        // So we know exactly where we are based on what scene loaded
        
        // If episode or level hasn't been set yet (both are still -1)
        // This check uses OR logic: true if EITHER value is less than 0
        // The || operator means "or" in C# (if left OR right is true, do this)
        if(s_CurrentEpisode < 0 || s_CurrentLevel < 0)
        {
            // Initialize to the first episode (index 0) and first level (index 0)
            // In programming, arrays start at index 0, so:
            // - episode 0 = first episode (Episode 1 in UI)
            // - level 0 = first level (Level 1 in UI)
            s_CurrentEpisode = 0;
            s_CurrentLevel = 0;
        }
        // If these values are already set (>= 0), we don't change them
        // This preserves progress when loading the next level via NextLevel()
#endif

        // Update the on-screen timer display to show 0 seconds
        // GameSystemInfo.Instance is another singleton that manages HUD UI
        // It has Text components for timer and score that get updated
        // This initializes the timer text to "0.0" or similar format
        // UpdateTimer() converts the float to a string and sets it on the UI Text
        GameSystemInfo.Instance.UpdateTimer(0);
    }

    // ========================================================================
    // TIMER CONTROL METHODS
    // ========================================================================
    // These public methods allow other scripts to control the game timer
    // Called by various game systems:
    // - StartCheckpoint: Calls StartTimer() when player enters start zone
    // - EndCheckpoint: Calls StopTimer() when player reaches the end
    // - PauseMenu: Calls StopTimer() when paused, StartTimer() when resumed
    // ========================================================================

    /// <summary>
    /// Resets the timer back to zero seconds
    /// Useful for restarting a level or starting a new attempt
    /// Does NOT start the timer - it just sets the value to 0
    /// Call StartTimer() separately to begin counting
    /// Example usage: When player hits "Restart Level", reset then start timer
    /// </summary>
    public void ResetTimer()
    {
        // Set elapsed time back to zero
        // The .0f suffix indicates this is a float literal (floating-point number)
        // Without the f, C# would treat 0.0 as a double (different type)
        // float = 32-bit floating point, double = 64-bit floating point
        m_Timer = 0.0f;
    }

    /// <summary>
    /// Begins running the timer
    /// Sets the flag that tells Update() to increment m_Timer each frame
    /// Called when gameplay begins (e.g., player starts moving or hits a start trigger)
    /// Example: StartCheckpoint.cs calls this when player enters the start zone
    /// The timer will now count up every frame until StopTimer() is called
    /// </summary>
    public void StartTimer()
    {
        // Set the running flag to true
        // Now the if statement in Update() will be true
        // Update() will add Time.deltaTime to m_Timer each frame
        // This makes the timer count up in real-time
        m_TimerRunning = true;
    }

    /// <summary>
    /// Stops the timer without resetting it
    /// Preserves the current time value (doesn't set it to 0)
    /// Called when game ends, player pauses, or level completes
    /// Example: PauseMenu calls this so time doesn't advance while paused
    /// Call StartTimer() to resume counting from where it stopped
    /// </summary>
    public void StopTimer()
    {
        // Set the running flag to false
        // Now the if statement in Update() will be false
        // Update() will no longer increment m_Timer
        // Current time value is preserved in m_Timer
        // Think of this like pressing "pause" on a stopwatch
        m_TimerRunning = false;
    }

    // ========================================================================
    // FINISH RUN METHOD - Called when player completes a level
    // ========================================================================

    /// <summary>
    /// Called when the player reaches the end checkpoint
    /// Handles end-of-level sequence: music change, cursor display, final UI
    /// EndCheckpoint.cs calls this method when player enters the end trigger
    /// This is the "level complete" sequence:
    /// 1. Change music to victory sound
    /// 2. Show cursor (player needs to click UI buttons)
    /// 3. Disable pause menu (level is over, can't pause)
    /// 4. Display final score screen
    /// </summary>
    public void FinishRun()
    {
        // Change the background music to the end game sound
        // BGMPlayer is the AudioSource component (set in Inspector)
        // .clip is the AudioClip (audio file) it will play
        // We're swapping from background music to a victory/completion sound
        BGMPlayer.clip = EndGameSound;

        // Turn off music looping - we only want to play the end sound once
        // By default, background music loops continuously during gameplay
        // .loop is a boolean: true = loop forever, false = play once and stop
        // We set it to false because victory sounds shouldn't loop
        BGMPlayer.loop = false;

        // Start playing the end game sound immediately
        // Play() begins audio playback on the AudioSource
        // This will play EndGameSound once (because loop is false)
        BGMPlayer.Play();

        // Show the mouse cursor so player can interact with UI
        // During gameplay, the cursor is hidden and locked to screen center (for FPS camera)
        // DisplayCursor(true) also pauses player controls so they can't move
        // Controller.Instance is the player controller singleton
        // This enables the cursor and disables player movement/shooting
        Controller.Instance.DisplayCursor(true);

        // Prevent the player from opening the pause menu
        // CanPause is a boolean flag in Controller that enables/disables ESC menu
        // We disable it here because the level is over - nothing to pause
        // The player should interact with the final score UI instead
        Controller.Instance.CanPause = false;

        // Show the final score screen UI
        // FinalScoreUI.Instance is another singleton managing the end screen
        // Display() activates the GameObject and populates it with stats:
        // - Targets destroyed (X/Y format)
        // - Time spent
        // - Penalty for missed targets
        // - Final time (time + penalties)
        // - Final score
        FinalScoreUI.Instance.Display();
    }

    // ========================================================================
    // NEXT LEVEL METHOD - Handles progression to the next level/episode
    // ========================================================================
    // This method is complex because it handles:
    // 1. Editor test scenes (scenes not in the database)
    // 2. Normal level progression (level 1 → level 2 → level 3)
    // 3. Episode transitions (last level of episode 1 → first level of episode 2)
    // 4. Game completion wrapping (last level → first level)
    // 5. Different scene loading APIs for editor vs build
    // ========================================================================

    /// <summary>
    /// Loads the next level in sequence
    /// Handles multiple scenarios:
    /// - Normal progression: Current level → next level in same episode
    /// - Episode transition: Last level of episode → first level of next episode  
    /// - Game wrap: Last level of last episode → first level of first episode
    /// - Editor test scenes: Reload current scene (not in database)
    /// Called by UI buttons (Next Level button) or automatic progression after level completion
    /// </summary>
    public void NextLevel()
    {
        // ========================================================================
        // EDITOR-ONLY: Handle test scenes not in database
        // ========================================================================
#if UNITY_EDITOR
        // In the Unity Editor, check if we're in a "test scene"
        // Test scenes are development scenes that aren't listed in the game database
        // Example: A designer creates "TestRoom.unity" to test a new mechanic
        // If episode or level is -1, we never found this scene in the database (see Start method)
        // The Start() method searched through all database scenes and didn't find a match
        if (s_CurrentEpisode < 0 || s_CurrentLevel < 0)
        {
            // We're in a test scene not part of the normal game flow
            // So "next level" doesn't make sense - instead we reload the current scene
            // This lets developers test a level repeatedly without it trying to advance
            // Example: Designer presses "Next Level" button → scene reloads for another test

            // Get the current scene's file path
            // EditorSceneManager is the editor version of SceneManager with more features
            // GetActiveScene() returns the currently loaded scene
            // .path gives us the file path (e.g., "Assets/Scenes/TestRoom.unity")
            var asyncOp = EditorSceneManager.LoadSceneAsyncInPlayMode(
                EditorSceneManager.GetActiveScene().path,  // Scene to load (current scene)
                new LoadSceneParameters(LoadSceneMode.Single)  // Single = replace current scene
            );
            // LoadSceneAsyncInPlayMode loads the scene in the background (async)
            // Async loading doesn't freeze the game - it loads over several frames
            // LoadSceneMode.Single means replace the current scene (not additive)
            // Returns an AsyncOperation for tracking progress (we don't use it here)

            // Exit the method early - don't run the normal level progression code below
            // 'return' stops the function immediately and goes back to the caller
            return;
        }
#endif
        // If we get here, we're either in a build or in a database-listed scene in editor

        // ====================================================================
        // INCREMENT LEVEL - Move to next level
        // ====================================================================

        // Add 1 to the current level index
        // If we're on level 2 (index 2), this makes it 3
        // += is shorthand for: s_CurrentLevel = s_CurrentLevel + 1
        // ++ would also work here: s_CurrentLevel++
        s_CurrentLevel += 1;

        // ====================================================================
        // CHECK IF WE'VE FINISHED ALL LEVELS IN THIS EPISODE
        // ====================================================================

        // Check if the new level index is beyond the last level in this episode
        // episodes[s_CurrentEpisode] = the current episode object
        // .scenes.Length = total number of levels in this episode
        // Example: if there are 5 levels (indices 0-4), Length = 5
        // If s_CurrentLevel is now 5, we've gone past the last level (index 4)
        if (GameDatabase.Instance.episodes[s_CurrentEpisode].scenes.Length <= s_CurrentLevel)
        {
            // We've completed all levels in this episode!
            // Time to move to the next episode

            // Reset level to first level of the next episode
            // Level index 0 = first level (Level 1 in UI)
            s_CurrentLevel = 0;

            // Move to the next episode
            // If we were on episode 1 (index 1), we're now on episode 2 (index 2)
            s_CurrentEpisode += 1;
        }
        // If we're still within the episode's level count, we just move to next level
        // Example: Episode has 5 levels, we finished level 3, now load level 4

        // ====================================================================
        // CHECK IF WE'VE FINISHED ALL EPISODES (COMPLETED THE GAME)
        // ====================================================================

        // Check if we've gone past the last episode
        // episodes.Length = total number of episodes in the game
        // Example: if there are 3 episodes (indices 0-2), Length = 3
        // If s_CurrentEpisode is now 3, we've gone past the last episode (index 2)
        if (s_CurrentEpisode >= GameDatabase.Instance.episodes.Length)
        {
            // We've completed the entire game!
            // Wrap back to the first episode to allow infinite replays
            // This creates a loop: Episode 3 Level 5 → Episode 1 Level 1
            // Players can keep playing instead of the game ending permanently
            s_CurrentEpisode = 0;
        }
        // Now s_CurrentEpisode and s_CurrentLevel point to the scene we want to load

        // ====================================================================
        // LOAD THE SCENE - Different behavior for Editor vs Build
        // ====================================================================
        // We use different scene loading methods depending on if we're in:
        // - Editor: Use EditorSceneManager (more features, can load non-build scenes)
        // - Build: Use SceneManager (faster, simpler, only works with built scenes)

#if UNITY_EDITOR
        // EDITOR VERSION: Use EditorSceneManager for more control and features
        // This allows loading scenes that might not be in the build settings
        // Useful for developers who are testing levels in isolation

        // Load the scene asynchronously (loads in background without freezing)
        // "Async" means the loading happens over multiple frames
        // episodes[s_CurrentEpisode] = the episode we want
        // .scenes[s_CurrentLevel] = the scene path string for the level we want
        // Example path: "Assets/Scenes/Episode2/Level3.unity"
        var op = EditorSceneManager.LoadSceneAsyncInPlayMode(
            GameDatabase.Instance.episodes[s_CurrentEpisode].scenes[s_CurrentLevel],
            new LoadSceneParameters(LoadSceneMode.Single)
        // LoadSceneMode.Single = replace the current scene (not additive)
        // Additive would keep the current scene and add the new one on top
        );
        // Returns AsyncOperation for tracking load progress (we don't use it)

#else
        // BUILD VERSION: Use standard SceneManager
        // This is simpler and more optimized for the final game
        // Only works with scenes that are included in Build Settings
        // Scenes not in Build Settings are not compiled into the game at all
        
        // LoadScene is synchronous in builds - loads immediately
        // "Synchronous" means it loads all at once and may cause a brief freeze
        // This is acceptable because builds are optimized and load faster
        // Uses the scene path from the game database
        SceneManager.LoadScene(GameDatabase.Instance.episodes[s_CurrentEpisode].scenes[s_CurrentLevel]);
        // Note: No return value - scene loads and replaces current scene immediately
#endif
        // After this line, the new scene will load
        // When the new scene loads, GameSystem's Start() will run again
        // But s_CurrentEpisode and s_CurrentLevel will persist (they're static)
    }

    // ========================================================================
    // RETRIEVE TARGETS COUNT - Scans scene for all targets
    // ========================================================================
    // This method is crucial for game logic - it determines how many targets
    // exist so we can track when the level is complete
    // It must handle several edge cases:
    // 1. Targets can be inactive (spawners create them disabled)
    // 2. In editor, prefab instances are included (must filter out)
    // 3. Negative-value targets shouldn't count toward completion
    // ========================================================================

    /// <summary>
    /// Counts all positive-value targets in the current scene
    /// Called at level start to initialize target tracking
    /// Only counts targets with pointValue > 0 (excludes penalty targets)
    /// Handles edge cases:
    /// - Spawners create inactive target pools (we count those too)
    /// - Editor includes prefabs in search results (we filter those out)
    /// - Negative-value targets are obstacles to avoid (don't count)
    /// Sets up initial state: resets destroyed count and score to 0
    /// </summary>
    void RetrieveTargetsCount()
    {
        // Find ALL Target components in the entire scene
        // Resources.FindObjectsOfTypeAll<T>() finds all objects of type T
        // This is a powerful but slow method that searches everywhere:
        // - Active GameObjects in the scene
        // - Inactive GameObjects in the scene
        // - Prefab instances loaded in memory (editor only)
        // - Even GameObjects marked as hidden
        // Returns an array of all Target scripts found
        // Target is a script/component that can be attached to GameObjects
        var targets = Resources.FindObjectsOfTypeAll<Target>();

        // Initialize a counter for positive-value targets
        // We'll increment this as we find valid targets
        // This will become the final m_TargetCount value
        int count = 0;

        // IMPORTANT NOTE: Spawner objects create targets and disable them immediately
        // Spawners are objects that spawn targets dynamically during gameplay
        // They create a pool of Target GameObjects and set them to inactive (disabled)
        // When needed, spawners activate a target from the pool
        // So this method finds both:
        // - Active targets already visible in the scene
        // - Inactive pooled targets waiting to be spawned
        // We need to count both types, but filter carefully to avoid double-counting

        // Loop through each Target component found in the scene
        // 'foreach' iterates over every element in the array
        // 't' is each individual Target (temporary variable for this loop)
        foreach (var t in targets)
        {
            // ========================================================================
            // EDITOR-ONLY: Filter out prefab instances
            // ========================================================================
#if UNITY_EDITOR
            // In the Unity Editor, Resources.FindObjectsOfTypeAll also returns PREFABS
            // Prefabs are template objects stored in the project folder, not in the scene
            // Example: "Assets/Prefabs/Target.prefab" - this is a template, not a scene object
            // We need to exclude prefabs from our count to avoid counting them twice:
            // - Once as a prefab
            // - Once as the scene instance created from that prefab

            // Check if this target's GameObject belongs to a valid scene
            // Every GameObject in a scene has a reference to that scene
            // Prefabs don't belong to any scene - they're project assets
            // .scene gets the scene this GameObject belongs to
            // .IsValid() returns false if the scene is invalid (meaning it's a prefab)
            // ! is the NOT operator - reverses the boolean
            // So "!scene.IsValid()" means "if the scene is NOT valid"
            if (!t.gameObject.scene.IsValid())
            {
                // This Target is part of a prefab, not a scene object - skip it!
                // 'continue' jumps to the next iteration of the foreach loop
                // The code below won't run for this target
                // We move on to the next target in the array
                continue;
            }
            // If we get here, the scene is valid, so this is a real scene Target
#endif

            // Filter targets by point value
            // Only count targets with POSITIVE point values
            // Target.cs has a public int pointValue field set in the Inspector
            // Positive values (1, 10, 50, etc.) = targets that give points
            // Negative values (-10, -20, etc.) = penalty targets that subtract points
            // Zero (0) = neutral targets (no point change)
            // We only want to count positive targets toward level completion
            // Penalty targets are obstacles to avoid, not objectives to complete
            if (t.pointValue > 0)
            {
                // This is a valid target that counts toward level completion
                // Increment our counter
                // += 1 is shorthand for: count = count + 1
                // Same as: count++
                count += 1;
            }
            // If pointValue <= 0, we don't increment count
            // These targets exist in the scene but don't count toward completion
        }
        // After the loop completes, 'count' contains the number of positive targets

        // ====================================================================
        // INITIALIZE LEVEL STATE - Set counters for new level
        // ====================================================================

        // Store the total number of positive-value targets found
        // This becomes the denominator in "X/Y targets destroyed"
        m_TargetCount = count;

        // Reset destroyed target counter to zero
        // New level = no targets hit yet
        // As player destroys targets, TargetDestroyed() increments this
        m_TargetDestroyed = 0;

        // Reset score to zero for the new level
        // Each level starts with score of 0
        // Score accumulates as player hits targets
        m_Score = 0;

        // Update the on-screen score display to show 0
        // GameSystemInfo is the HUD UI manager singleton
        // It has a Text component that displays the score
        // UpdateScore(0) sets the text to "0" or "Score: 0" depending on format
        GameSystemInfo.Instance.UpdateScore(0);

        // Initialize the level selection UI system
        // LevelSelectionUI is the menu that lets players pick episodes/levels
        // Init() creates buttons for each episode and level from GameDatabase
        // This populates the episode and level selection menus
        // Called here to ensure UI is ready if player opens the menu
        LevelSelectionUI.Instance.Init();
    }

    // ========================================================================
    // UPDATE - Unity lifecycle method, called every frame
    // ========================================================================
    // UNITY LIFECYCLE:
    // 1. Awake() - Once when object is created
    // 2. Start() - Once before first frame
    // 3. Update() - EVERY FRAME (typically 60 times per second)
    // 4. LateUpdate() - After all Update() calls
    // 5. OnDestroy() - When object is destroyed
    //
    // Update() runs once per frame (typically 60 FPS = 60 times per second)
    // Used here for per-frame updates: timer increment and UI updates
    // Keep Update() fast - slow code here will cause framerate drops!
    // ========================================================================

    /// <summary>
    /// Called once per frame by Unity's game loop
    /// Handles per-frame updates that need to happen continuously:
    /// - Timer increment (if running)
    /// - Minimap update with player position
    /// - Fullscreen map update (if visible)
    /// Runs at framerate speed: 60 FPS = called 60 times per second
    /// At 60 FPS, Update() runs approximately every 16.67 milliseconds
    /// </summary>
    void Update()
    {
        // ====================================================================
        // TIMER UPDATE
        // ====================================================================

        // Check if the timer should be running right now
        // This flag is controlled by StartTimer() and StopTimer() methods
        // True = timer is active (level in progress)
        // False = timer is paused (in menu, level complete, etc.)
        if (m_TimerRunning)
        {
            // Add elapsed time since last frame to the timer
            // Time.deltaTime = time in seconds since last frame
            // This is Unity's way of making things frame-rate independent
            // Example calculations at different frame rates:
            // - 60 FPS: deltaTime ≈ 0.0167 seconds (1/60)
            // - 30 FPS: deltaTime ≈ 0.0333 seconds (1/30)
            // - 120 FPS: deltaTime ≈ 0.0083 seconds (1/120)
            // So if the game runs at 30 FPS, we add bigger chunks of time
            // And if it runs at 120 FPS, we add smaller chunks of time
            // But the timer advances at the same REAL WORLD speed regardless!
            // This makes the timer advance 1 second per real-world second
            m_Timer += Time.deltaTime;

            // Update the on-screen timer display with the new time
            // Shows the current time to the player in the HUD
            // GameSystemInfo is the UI manager singleton
            // UpdateTimer() converts the float to a formatted string
            // Example: 45.7 becomes "45.7" or "00:45.7" depending on format
            // The UI Text component displays this to the player
            GameSystemInfo.Instance.UpdateTimer(m_Timer);
        }
        // If m_TimerRunning is false, we skip this block
        // The timer doesn't advance when paused

        // ====================================================================
        // PLAYER POSITION TRACKING - For minimap and UI
        // ====================================================================

        // Get the player's transform component
        // Transform contains position, rotation, and scale of a GameObject
        // Controller.Instance is the player character controller singleton
        // Controller.cs is the script that handles player movement and shooting
        // .transform is a property that every MonoBehaviour has
        // It gives us access to the GameObject's Transform component
        // We cache this in a variable because we use it multiple times below
        Transform playerTransform = Controller.Instance.transform;

        // ====================================================================
        // UI UPDATES - Update maps with player position
        // ====================================================================
        // Maps need to update every frame so the player icon moves smoothly
        // as the player walks around the level
        // ====================================================================

        // Update the minimap with the player's current position
        // MinimapUI displays a small map in the corner of the screen
        // UpdateForPlayerTransform() does several things:
        // 1. Renders the level geometry from above
        // 2. Positions the player icon at the correct location
        // 3. Rotates the player icon to match player facing direction
        // This makes the player icon move on the minimap as they walk around
        // Called every frame so movement appears smooth
        MinimapUI.Instance.UpdateForPlayerTransform(playerTransform);

        // Check if the fullscreen map is currently visible/active
        // FullscreenMap is a larger, more detailed map shown when player presses M
        // .gameObject gets the GameObject this script is attached to
        // .activeSelf returns true if the GameObject is active (visible)
        // We only update the fullscreen map if it's being displayed
        // This saves performance - no need to render it when it's hidden
        if (FullscreenMap.Instance.gameObject.activeSelf)
        {
            // Update the fullscreen map with player position
            // This is similar to the minimap but larger and more detailed
            // UpdateForPlayerTransform() renders the map and updates player icon
            // Only called when the map is visible to save CPU/GPU resources
            FullscreenMap.Instance.UpdateForPlayerTransform(playerTransform);
        }
        // If the fullscreen map is hidden, we skip this update
    }

    // ========================================================================
    // GET FINAL TIME - Calculates final time with penalties
    // ========================================================================

    /// <summary>
    /// Calculates the final time including penalties for missed targets
    /// Formula: actual time + (missed targets × penalty per target)
    /// Example: 60 seconds with 3 missed targets at 1.0s penalty = 63.0s final time
    /// Used for scoring and leaderboards - faster times with fewer misses are better
    /// Called by FinalScoreUI to display the adjusted time on the results screen
    /// Players are encouraged to hit all targets to avoid time penalties
    /// </summary>
    /// <returns>Final time in seconds including all penalties</returns>
    public float GetFinalTime()
    {
        // Calculate how many targets were missed
        // Formula: Total targets - Targets destroyed = Targets missed
        // Example: 10 total targets, destroyed 8, missed 2
        // If player destroyed all targets, missedTarget = 0 (no penalty)
        int missedTarget = m_TargetCount - m_TargetDestroyed;

        // Calculate total time penalty for missed targets
        // Each missed target adds TargetMissedPenalty seconds
        // Example: 2 missed targets × 1.0 seconds = 2.0 seconds penalty
        // Example: 5 missed targets × 1.5 seconds = 7.5 seconds penalty
        // If no targets were missed, penalty = 0
        float penalty = missedTarget * TargetMissedPenalty;

        // Return final time: actual time + penalty
        // Example: 60.5 seconds + 2.0 seconds penalty = 62.5 seconds final time
        // This encourages players to hit all targets to get the best time
        // Players can speedrun but will be penalized for skipping targets
        // Perfect run = actual time (no penalty)
        return m_Timer + penalty;
    }

    // ========================================================================
    // TARGET DESTROYED - Called when player hits a target
    // ========================================================================

    /// <summary>
    /// Called by Target.cs when a target is destroyed
    /// This is the callback method that Target scripts invoke when hit by a bullet
    /// Updates two pieces of state:
    /// 1. Increments destroyed target count (for completion tracking)
    /// 2. Adds/subtracts points to/from score (for scoring system)
    /// Also updates the UI to reflect new score immediately
    /// Target.cs calls: GameSystem.Instance.TargetDestroyed(pointValue);
    /// </summary>
    /// <param name="score">Point value of the destroyed target
    /// - Positive values (10, 50, 100) = add points to score
    /// - Negative values (-10, -20) = subtract points from score (penalty)
    /// - Zero (0) = no score change (neutral target)</param>
    public void TargetDestroyed(int score)
    {
        // Increment the count of destroyed targets
        // Used to track progress toward level completion
        // Also used to calculate missed targets: m_TargetCount - m_TargetDestroyed
        // += 1 is shorthand for: m_TargetDestroyed = m_TargetDestroyed + 1
        // Same as: m_TargetDestroyed++ or m_TargetDestroyed = m_TargetDestroyed + 1
        m_TargetDestroyed += 1;

        // Add the target's point value to the total score
        // The += operator adds the value on the right to the variable on the left
        // If score is positive: adds points (reward for hitting target)
        // If score is negative: subtracts points (penalty for hitting wrong target)
        // If score is zero: no change to score
        // Example 1: current score 100, hit 10-point target, new score = 110
        // Example 2: current score 100, hit -5-point target, new score = 95
        // Example 3: current score 100, hit 0-point target, new score = 100
        m_Score += score;

        // Update the on-screen score display with the new total
        // Shows the player their current score in real-time
        // GameSystemInfo is the HUD UI manager singleton
        // UpdateScore() sets the score text to display the new value
        // Example: If m_Score is now 150, the UI might show "Score: 150"
        // This gives immediate feedback to the player about their performance
        GameSystemInfo.Instance.UpdateScore(m_Score);
    }
}

// ============================================================================
// END OF GAMESYSTEM CLASS
// ============================================================================
// 
// SUMMARY: GameSystem is the backbone of the FPS Kit's game flow
// 
// KEY RESPONSIBILITIES:
// - Level/Episode progression via NextLevel()
// - Timer management via StartTimer(), StopTimer(), ResetTimer()
// - Score tracking via TargetDestroyed()
// - Target counting via RetrieveTargetsCount()
// - Scene transitions and loading
// - UI coordination with other systems
//
// ARCHITECTURE PATTERN:
// This uses the Singleton pattern with the Instance property
// Other systems access GameSystem via GameSystem.Instance.MethodName()
//
// SYSTEMS IT COORDINATES WITH:
// - GameDatabase: ScriptableObject storing episode/level data
// - Controller: Player character movement and input
// - PoolSystem: Object pooling for performance optimization
// - WorldAudioPool: Audio source pooling for sound effects
// - GameSystemInfo: HUD display (timer, score)
// - MinimapUI/FullscreenMap: Map rendering and display
// - LevelSelectionUI: Episode and level selection menu
// - FinalScoreUI: End-of-level results screen
// - Target: Shootable target objects in the level
// - StartCheckpoint: Trigger that starts the timer
// - EndCheckpoint: Trigger that ends the level
// - PauseMenu: Pause menu that stops the timer
//
// DESIGN PATTERNS USED:
// - Singleton: Ensures only one GameSystem exists globally
// - Object Pooling: Reuses objects instead of creating/destroying
// - Observer: Targets call back to GameSystem when destroyed
// - State Machine: Tracks game state (episode, level, score, time)
//
// ============================================================================