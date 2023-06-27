using Godot;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using redhatgamedev.srt.v1;
using Serilog;

// This class is autoloaded
public partial class Game : Node
{
  Serilog.Core.LoggingLevelSwitch levelSwitch = new Serilog.Core.LoggingLevelSwitch();
  public Serilog.Core.Logger _serilogger;

  private ServerConnection serverConnection;
  private LoginScreen loginScreen;

  private Stopwatch GameStopwatch = new Stopwatch();

  Boolean inGame = false;
  // UI elements
  CanvasLayer gameUI;
  TextureRect speedometer;
  TextureRect missileDisplay;
  TextureRect missileReadyIndicator;
  Texture2D missileReadyIndicatorReady = ResourceLoader.Load<Texture2D>("res://Assets/UIElements/HUD/HUD_missile_status_circle_indicator_green.png");
  Texture2D missileReadyIndicatorNotReady = ResourceLoader.Load<Texture2D>("res://Assets/UIElements/HUD/HUD_missile_status_circle_indicator_red.png");
  Texture2D missileReadyIndicatorDefault = ResourceLoader.Load<Texture2D>("res://Assets/UIElements/HUD/HUD_missile_status_circle_indicator.png");
  TextureRect gameRadar;
  Texture2D shipBlip;
  Label fasterControl;
  bool fasterControlLit;
  Label slowerControl;
  bool slowerControlLit;
  Label turnLeftControl;
  bool turnLeftControlLit;
  Label turnRightControl;
  bool turnRightControlLit;
  Label fireControl;
  bool fireControlLit;

  // radar update timer
  int radarRefreshTime = 1; // 1000ms = 1sec
  float radarRefreshTimer = 0;

  // dictionary mapping for quicker access (might not need if GetNode<> is fast enough)
  public Dictionary<String, PlayerShip> playerObjects = new Dictionary<string, PlayerShip>();
  public Dictionary<String, SpaceMissile> missileObjects = new Dictionary<string, SpaceMissile>();
  PlayerShip myShip = null;

  PackedScene PackedMissile = (PackedScene)ResourceLoader.Load("res://Scenes/SupportScenes/SpaceMissile.tscn");
  PackedScene PlayerShipThing = (PackedScene)ResourceLoader.Load("res://Scenes/SupportScenes/Player.tscn");

  public String myUuid = null;

  // Queues for processing incoming messages
  public ConcurrentQueue<GameEvent.GameObject> PlayerCreateQueue = new ConcurrentQueue<GameEvent.GameObject>();

  // this is a queue of tuples that contain a game object and a time that the update arrived
  // the time is used for lag calculations
  public ConcurrentQueue<(GameEvent.GameObject gameObject, long ingressTime)> PlayerUpdateQueue =
    new ConcurrentQueue<(GameEvent.GameObject gameObject, long ingressTime)>();

  public ConcurrentQueue<GameEvent.GameObject> PlayerDestroyQueue = new ConcurrentQueue<GameEvent.GameObject>();
  public ConcurrentQueue<GameEvent.GameObject> MissileCreateQueue = new ConcurrentQueue<GameEvent.GameObject>();
  public ConcurrentQueue<GameEvent.GameObject> MissileUpdateQueue = new ConcurrentQueue<GameEvent.GameObject>();
  public ConcurrentQueue<GameEvent.GameObject> MissileDestroyQueue = new ConcurrentQueue<GameEvent.GameObject>();

  public ConcurrentQueue<(GameEvent gameEvent, long DTDiff)> GameUpdateBufferQueue =
    new ConcurrentQueue<(GameEvent gameEvent, long DTDiff)>();

  // use auto-implemented properties?
  public UInt32 bufferMessagesCount = 2;
  public UInt32 sequenceNumber = 0;

  /* PLAYER DEFAULTS AND CONFIG */

  float PlayerDefaultThrust = 1f;
  float PlayerDefaultMaxSpeed = 5;
  float PlayerDefaultRotationThrust = 1.5f;
  public int PlayerDefaultHitPoints = 100;
  int PlayerDefaultMissileSpeed = 300;
  float PlayerDefaultMissileLife = 2;
  int PlayerDefaultMissileDamage = 25;
  int PlayerDefaultMissileReloadTime = 2;

  /* END PLAYER DEFAULTS AND CONFIG */


  /* Timers and stuff for lag calculations */

  float gameLag = 0;
  float frameTimer = 0;
  float frameMax = 1;
  long updatesProcessed = 1;
  long totalLag = 0;

  /* End timer section */

  /* Input de-spamming */
  Vector2 lastVelocity = Vector2.Zero; // The player's movement direction.
  Vector2 lastShoot = Vector2.Zero; // the player's shoot status

  // the UTC epoch milliseconds of the last time we sent a command to the server
  long lastCommandSentMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

  // how long to wait before sending another command
  long sendCommandDelayMs = 10;

  AudioStreamPlayer backgroundMusic;


  public void LoadConfig()
  {
    _serilogger.Information("Game.cs: Configuring");

    var clientConfig = new ConfigFile();

    // try to load user preference config first
    Error err = clientConfig.Load("user://client.cfg");
    if (err != Error.Ok)
    {
      _serilogger.Information("Game.cs: Local user config not found, defaulting to built-in");
      err = clientConfig.Load("Resources/client.cfg");
    }

    int DesiredLogLevel = 3;

    // if the file was loaded successfully, read the vars
    if (err == Error.Ok)
    {
      DesiredLogLevel = (int)clientConfig.GetValue("game", "log_level");
    }

    // pull values from env -- will get nulls if any vars are not set
    String envLogLevel = System.Environment.GetEnvironmentVariable("SRT_LOG_LEVEL");

    // override any loaded config with env
    if (envLogLevel != null) DesiredLogLevel = int.Parse(envLogLevel);

    switch (DesiredLogLevel)
    {
      case 0:
        _serilogger.Information("Game.cs: Setting minimum log level to: Fatal");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Fatal;
        break;
      case 1:
        _serilogger.Information("Game.cs: Setting minimum log level to: Error");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Error;
        break;
      case 2:
        _serilogger.Information("Game.cs: Setting minimum log level to: Warning");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Warning;
        break;
      case 3:
        _serilogger.Information("Game.cs: Setting minimum log level to: Information");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Information;
        break;
      case 4:
        _serilogger.Information("Game.cs: Setting minimum log level to: Debug");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
        break;
      case 5:
        _serilogger.Information("Game.cs: Setting minimum log level to: Verbose");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Verbose;
        break;
      default:
        _serilogger.Information("Game.cs: Unknown log level specified, defaulting to: Information");
        levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
        break;
    }

  }

  // Called when the node enters the scene tree for the first time.
  public override void _Ready()
  {
    frameTimer = 0;
    GameStopwatch.Start();
    levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Information;
    _serilogger = new LoggerConfiguration().MinimumLevel
      .ControlledBy(levelSwitch).WriteTo
      .Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}").CreateLogger();
    _serilogger.Information("Space Ring Things (SRT) Game Client v???");

    LoadConfig();

    // fetch nodes we need
    gameUI = GetNode<CanvasLayer>("GUI");
    speedometer = gameUI.GetNode<TextureRect>("Speedometer");
    missileDisplay = gameUI.GetNode<TextureRect>("Missile");
    missileReadyIndicator = gameUI.GetNode<TextureRect>("Missile/MissileReadyIndicator");
    gameRadar = gameUI.GetNode<TextureRect>("Radar");
    fasterControl = gameUI.GetNode<Label>("ControlIndicators/ControlsBox/Faster");
    slowerControl = gameUI.GetNode<Label>("ControlIndicators/ControlsBox/Slower");
    turnLeftControl = gameUI.GetNode<Label>("ControlIndicators/ControlsBox/TurnLeft");
    turnRightControl = gameUI.GetNode<Label>("ControlIndicators/ControlsBox/TurnRight");
    fireControl = gameUI.GetNode<Label>("ControlIndicators/ControlsBox/FireButton");
    backgroundMusic = GetNode<AudioStreamPlayer>("AudioStreamPlayer");


    PackedScene packedAuthScene = (PackedScene)ResourceLoader.Load("res://Scenes/LoginScreen.tscn");
    loginScreen = (LoginScreen)packedAuthScene.Instantiate();
    AddChild(loginScreen);

    // wait a notification the login flow
    loginScreen.LoginSuccess += OnLoginSuccess;

    //loginScreen.Connect("loginSuccess", new Callable(this, "_on_login_success"));
  }

  public void displayGameOverScreen()
  {
    _serilogger.Verbose($"Game.cs: hide the GUI");
    CanvasLayer ourGui = GetNode<CanvasLayer>("GUI");
    ourGui.Hide();

    PackedScene packedGameOverScene = (PackedScene)ResourceLoader.Load("res://Scenes/GameOverScreen.tscn");
    GameOverScreen gameOver = (GameOverScreen)packedGameOverScene.Instantiate();
    AddChild(gameOver);
  }

  public void initializeGameUI()
  {
    // we are now in the game
    inGame = true;

    // load the ship blip texture
    shipBlip = ResourceLoader.Load<Texture2D>("res://Assets/UIElements/HUD/ship_blip.png");

    // TODO: should we be adding the GUI to the scene instead of displaying its elements?
    // find the HUD to show its elements
    gameUI.Show();
  }

  // called to update camera and 2d listener for audio
  void updateWhoAmI()
  {
    // check if our UUID is set -- would be set after join, but it's possible
    // we are receiving messages and creates, etc. before we have joined
    if (myUuid != null && inGame == true)
    {
      // check if our uuid is in the playership dict
      if (playerObjects.TryGetValue(myUuid, out myShip))
      {
        Node2D playerForCamera = myShip;
        Camera2D playerCamera = playerForCamera.GetNode<Camera2D>("Camera2D");
        AudioListener2D theListener = playerForCamera.GetNode<AudioListener2D>("AudioListener2D");

        if (!playerCamera.IsCurrent()) { playerCamera.MakeCurrent(); }
        if (!theListener.IsCurrent()) { theListener.MakeCurrent(); }
      }
    }
  }
  void updateGameUI()
  {
    // update the speedometer for our player
    speedometer.GetNode<Label>("SpeedLabel").Text = myShip.CurrentVelocity.ToString("n2");

    // if we have a missile, or we're not ready to reload, set the indicator to
    // red, otherwise set the indicator to green
    if ((myShip.MyMissile != null) || (!myShip.MissileReady))
    {
      missileReadyIndicator.Texture = missileReadyIndicatorNotReady;
    }
    else
    {
      missileReadyIndicator.Texture = missileReadyIndicatorReady;
    }
  }

  void updateGameRadar()
  {
    // the radar circle is approximately 280x280 and its center is
    // approximately 169,215 on the image

    // delete all the radar blips
    // TODO: the performance on this is probably terrible
    deleteChildren(gameRadar);

    // iterate over the player objects
    foreach (KeyValuePair<String, PlayerShip> entry in playerObjects)
    {
      String player = entry.Key;
      PlayerShip playerShip = entry.Value;

      // don't draw ourselves
      if (player == myUuid) continue;
      _serilogger.Verbose($"Game.cs: Drawing radar dot for {player}");

      _serilogger.Verbose($"Game.cs: Player {player} is at position {playerShip.Position.X}:{playerShip.Position.Y}");

      float deltaX = myShip.Position.X - playerShip.Position.X;
      float deltaY = myShip.Position.Y - playerShip.Position.Y;

      _serilogger.Verbose($"Game.cs: Relative position to player is {deltaX}:{deltaY}");

      // scale the relative position where 10,000 is the edge of the radar circle
      float scaledX = (deltaX / 10000) * (280 / 2);
      float scaledY = (deltaY / 10000) * (280 / 2);

      // x and y are "upside down" for some reason
      float finalX = (scaledX * -1) + 169;
      float finalY = (scaledY * -1) + 215;

      _serilogger.Verbose($"Game.cs: Scaled position to player is {scaledX}:{scaledY}");

      // add a blip at the scaled location offset from the center
      Sprite2D newBlip = new Sprite2D();
      newBlip.Texture = shipBlip;
      newBlip.Offset = new Vector2(finalX, finalY);

      gameRadar.AddChild(newBlip);
    }
  }

  void ProcessGameUpdateBuffer()
  {
    // if the GameEventBufferQueue is shorter than the bufferMessagesCount, return
    if (GameUpdateBufferQueue.Count < bufferMessagesCount)
    {
      _serilogger.Verbose("Game.cs: GameUpdateBufferQueue is too short, returning");
      return;
    }

    // since we made it here, we have at least bufferMessageCount in the queue,
    // so we can dequeue the first event on the GameEventBufferQueue and process it
    if (GameUpdateBufferQueue.TryDequeue(out (GameEvent theEvent, long theDTDiff) tuple))
    {
      // we found something to dequeue, so process the objects in the message
      _serilogger.Verbose("Game.cs: GameUpdateBufferQueue has something, processing...");
      GameEvent dequeuedEvent = tuple.theEvent;
      long DTDiff = tuple.theDTDiff;

      // updates may have many objects to handle
      // iterate over the GameObjects in egeb and handle each
      foreach (GameEvent.GameObject gameObject in dequeuedEvent.GameObjects)
      {
        // TODO: i don't think this new tuple is required since we just created the tuple above when we dequeued
        // make a new tuple of the gameObject and the dtdiff
        (GameEvent.GameObject go, long diffTime) newTuple = (gameObject, DTDiff);

        if (gameObject.Uuid == null || gameObject.Uuid.Length < 1) // TODO: any additional validation goes here
        {
          _serilogger.Warning("Game.cs: got update event with invalid UUID, IGNORING...");
          return;
        }

        switch (gameObject.GameObjectType)
        {
          case GameEvent.GameObjectType.GameObjectTypePlayer:
            _serilogger.Verbose($"Game.cs: Got update for player {gameObject.Uuid}");
            // send the whole tuple instead of the egeb part so that we can calculate the true
            // time later
            PlayerUpdateQueue.Enqueue(newTuple);
            break;

          case GameEvent.GameObjectType.GameObjectTypeMissile:
            // TODO: probably should modify the missile stuff to take the dtdiff as well
            _serilogger.Verbose($"Game.cs: Got update for missile {gameObject.Uuid} owner {gameObject.OwnerUuid}");
            MissileUpdateQueue.Enqueue(gameObject);
            break;
        }
      }
    }
  }

  void ProcessPlayerCreate()
  {
    GameEvent.GameObject gameObject = null;
    while (PlayerCreateQueue.TryDequeue(out gameObject))
    {
      if (null == gameObject)
      {
        // this should probably be impossible
        _serilogger.Debug($"Game.cs: Dequeuing player create message for null!? SKIPPING!");
        continue;
      }

      _serilogger.Debug($"Game.cs: Dequeuing player create message for {gameObject.Uuid}");
      PlayerShip newShip = CreateShipForUUID(gameObject.Uuid);
      newShip.UpdateFromGameEventBuffer(gameObject);
    }
  }

  void ProcessPlayerUpdate()
  {
    while (PlayerUpdateQueue.TryDequeue(out (GameEvent.GameObject gameObject, long ingressTime) gameUpdateTuple))
    {
      if (null == gameUpdateTuple.gameObject)
      {
        // this should probably be impossible
        _serilogger.Debug($"Game.cs: Dequeuing player update message for null!? SKIPPING!");
        continue;
      }

      _serilogger.Verbose($"Game.cs: Dequeuing player update message for {gameUpdateTuple.gameObject.Uuid}");
      long localDT = DateTimeOffset.Now.ToUnixTimeMilliseconds();
      long DTDiff = localDT - gameUpdateTuple.ingressTime;

      _serilogger.Verbose($"Game.cs: Msg DT: {gameUpdateTuple.ingressTime} / Current DT: {localDT} / Diff: {DTDiff}");
      totalLag += DTDiff;
      updatesProcessed += 1;

      PlayerShip ship = UpdateShipWithUUID(gameUpdateTuple.gameObject.Uuid);
      ship.UpdateFromGameEventBuffer(gameUpdateTuple.gameObject);
    }
  }

  void ProcessPlayerDestroy()
  {
    while (PlayerDestroyQueue.TryDequeue(out GameEvent.GameObject gameObject))
    {
      if (null == gameObject)
      {
        // this should probably be impossible
        _serilogger.Debug($"Game.cs: Dequeuing player destroy message for null!? SKIPPING!");
        continue;
      }

      _serilogger.Debug($"Game.cs: Dequeuing player destroy message for {gameObject.Uuid}");
      DestroyShipWithUUID(gameObject.Uuid, gameObject.HitPoints);
    }
  }

  void ProcessMissileCreate()
  {
    while (MissileCreateQueue.TryDequeue(out GameEvent.GameObject gameObject))
    {
      if (null == gameObject)
      {
        // this should probably be impossible
        _serilogger.Debug($"Game.cs: Dequeuing missile create message for null!? SKIPPING!");
        continue;
      }

      _serilogger.Debug($"Game.cs: Dequeuing missile create message for {gameObject.Uuid} owner {gameObject.OwnerUuid}");
      SpaceMissile newMissile = CreateMissileForUUID(gameObject);
      newMissile.UpdateFromGameEventBuffer(gameObject);
    }
  }

  void ProcessMissileUpdate()
  {
    while (MissileUpdateQueue.TryDequeue(out GameEvent.GameObject gameObject))
    {
      if (null == gameObject)
      {
        // this should probably be impossible
        _serilogger.Debug($"Game.cs: Dequeuing missile update message for null!? SKIPPING!");
        continue;
      }

      _serilogger.Verbose($"Game.cs: Dequeuing missile update message for {gameObject.Uuid} owner {gameObject.OwnerUuid}");
      SpaceMissile missile = UpdateMissileWithUUID(gameObject);
      missile.UpdateFromGameEventBuffer(gameObject);
    }
  }

  void ProcessMissileDestroy()
  {
    while (MissileDestroyQueue.TryDequeue(out GameEvent.GameObject gameObject))
    {
      if (null == gameObject)
      {
        // this should probably be impossible
        _serilogger.Debug($"Game.cs: Dequeuing missile destroy message for null!? SKIPPING!");
        continue;
      }

      _serilogger.Debug($"Game.cs: Dequeuing missile destroy message for {gameObject.Uuid} owner {gameObject.OwnerUuid}");
      DestroyMissileWithUUID(gameObject.Uuid);
    }
  }

  public void ProcessAnnounce(Security security)
  {
    _serilogger.Debug($"Game.cs: Processing received announce message for {security.Uuid}");

    PlayerDefaultThrust = (float)security.ShipThrust;
    PlayerDefaultMaxSpeed = (float)security.MaxSpeed;
    PlayerDefaultRotationThrust = (float)security.RotationThrust;
    PlayerDefaultHitPoints = (int)security.HitPoints;
    PlayerDefaultMissileSpeed = (int)security.MissileSpeed;
    PlayerDefaultMissileLife = (float)security.MissileLife;
    PlayerDefaultMissileDamage = (int)security.MissileDamage;
    PlayerDefaultMissileReloadTime = (int)security.MissileReload;

    _serilogger.Debug($"Game.cs: Player Thrust:       {PlayerDefaultThrust}");
    _serilogger.Debug($"Game.cs: Player Speed:        {PlayerDefaultMaxSpeed}");
    _serilogger.Debug($"Game.cs: Player Rotation:     {PlayerDefaultRotationThrust}");
    _serilogger.Debug($"Game.cs: Player HP:           {PlayerDefaultHitPoints}");
    _serilogger.Debug($"Game.cs: Missile Speed:       {PlayerDefaultMissileSpeed}");
    _serilogger.Debug($"Game.cs: Missile Life:        {PlayerDefaultMissileLife}");
    _serilogger.Debug($"Game.cs: Missile Damage:      {PlayerDefaultMissileDamage}");
    _serilogger.Debug($"Game.cs: Missile Reload Time: {PlayerDefaultMissileReloadTime}");
  }

  void ProcessControlLights()
  {
    _serilogger.Verbose($"Game.cs: Processing control lights");
    ToggleControlLight(fasterControl, fasterControlLit);
    ToggleControlLight(slowerControl, slowerControlLit);
    ToggleControlLight(turnLeftControl, turnLeftControlLit);
    ToggleControlLight(turnRightControl, turnRightControlLit);
    ToggleControlLight(fireControl, fireControlLit);
  }

  void ToggleControlLight(Label toControl, bool status)
  {
    if (status)
    {
      toControl.Modulate = new Color(2.5f, 2.5f, 2.5f, 1);
    }
    else
    {
      toControl.Modulate = new Color(1, 1, 1, 1);
    }
  }

  public override void _Process(double delta)
  {
    PlayBackgroundMusic();

    frameTimer += (float)delta;
    if (frameTimer >= frameMax)
    {
      gameLag = totalLag / updatesProcessed;
      _serilogger.Debug($"Game.cs: Current processing lag: {gameLag}ms");
      updatesProcessed = 1;
      totalLag = 0;
      frameTimer = 0;
    }
    updateWhoAmI();

    ProcessGameUpdateBuffer();

    // we may eventually need to throw away some of these if the FPS starts slowing
    ProcessPlayerCreate();
    ProcessPlayerUpdate();
    ProcessMissileCreate();
    ProcessMissileUpdate();
    ProcessMissileDestroy();

    var velocity = Vector2.Zero; // The player's movement direction.
    var shoot = Vector2.Zero; // the player's shoot status

    // check for inputs once we are ingame
    // TODO: this seems like a horrible and inefficient way to do this. We should probably
    // start the game in the login screen scene and then completely switch to the game scene
    // instead of doing this, no?
    if (inGame)
    {
      if (Input.IsActionPressed("rotate_right"))
      {
        velocity.X += 1;
        turnRightControlLit = true;
      }
      else
      {
        turnRightControlLit = false;
      }

      if (Input.IsActionPressed("rotate_left"))
      {
        velocity.X -= 1;
        turnLeftControlLit = true;
      }
      else
      {
        turnLeftControlLit = false;
      }

      if (Input.IsActionPressed("thrust_forward"))
      {
        velocity.Y += 1;
        fasterControlLit = true;
      }
      else
      {
        fasterControlLit = false;
      }

      if (Input.IsActionPressed("thrust_reverse"))
      {
        velocity.Y -= 1;
        slowerControlLit = true;
      }
      else
      {
        slowerControlLit = false;
      }

      if (Input.IsActionPressed("fire"))
      {
        shoot.Y = 1;
        fireControlLit = true;
      }
      else
      {
        fireControlLit = false;
      }

      if ((velocity.Length() > 0) || (shoot.Length() > 0)) ProcessInputEvent(velocity, shoot);

      ProcessControlLights();

      if (myShip != null)
      {
        updateGameUI();
      }

      // https://gdscript.com/solutions/godot-timing-tutorial/
      // check if we should update the debug UI, which itself should only be done if
      // we are in a graphical mode
      // TODO: only if in graphical debug mode
      // TODO: should also probably use timer node
      radarRefreshTimer += (float)delta;
      if (radarRefreshTimer >= radarRefreshTime)
      {
        radarRefreshTimer = 0;
        _serilogger.Verbose($"Game.cs: Updating radar");
        updateGameRadar();
      }
    }

    ProcessPlayerDestroy();
  }

  public bool JoinGameAsPlayer(string myUuid)
  {
    // TODO: if not connected, try again to connect to server
    _serilogger.Information($"Game.cs: Sending join with UUID: {myUuid}");

    // construct a join message
    Security scb = new Security();

    //scb.Uuid = ServerConnection.UUID;
    scb.Uuid = myUuid;

    scb.security_type = Security.SecurityType.SecurityTypeJoin;
    serverConnection.SendSecurity(scb);

    return true; // TODO: this can't always be true

    // TODO: send my name and player preferences over to the severside
  }

  void QuitGame()
  {
    // our UUID was set, so we should send a leave message to be polite
    Security scb = new Security();
    scb.Uuid = myUuid;
    scb.security_type = Security.SecurityType.SecurityTypeLeave;
    serverConnection.SendSecurity(scb);
  }

  /// <summary>
  /// Called when the network processes a game event with a create ship event
  ///
  /// </summary>
  /// <param name="uuid"></param>
  /// <returns>the created ship instance</returns>
  public PlayerShip CreateShipForUUID(string uuid)
  {
    _serilogger.Debug("Game.cs: CreateShipForUUID: " + uuid);
    // TODO: check to ensure it doesn't already exist

    PlayerShip shipInstance;

    // TODO: we might need to do something in the case where we end up creating ships before the announce message
    //       has been processed. Right now the code will create a ship as soon as it receives an update for a ship
    //       it doesn't know about, but that could happen before we've received the announce. It's nice to
    //       see ships moving around on the login screen. maybe we need to re-initialize all the known ships on
    //       joining
    if (!playerObjects.TryGetValue(uuid, out shipInstance))
    {
      // we didn't find a matching ship in the playerObjects dictionary, so create a new instance
      Node2D playerShipThingInstance = (Node2D)PlayerShipThing.Instantiate();
      shipInstance = playerShipThingInstance.GetNode<PlayerShip>("PlayerShip"); // get the PlayerShip (a CharacterBody2D) child node
      shipInstance.uuid = uuid;

      // set the instance defaults to match what we learned from the announce
      _serilogger.Debug("Game.cs: Setting ship instance starting values to defaults");
      shipInstance.Thrust = PlayerDefaultThrust;
      shipInstance.MaxSpeed = PlayerDefaultMaxSpeed;
      shipInstance.RotationThrust = PlayerDefaultRotationThrust;
      shipInstance.HitPoints = PlayerDefaultHitPoints;
      shipInstance.MissileSpeed = PlayerDefaultMissileSpeed;
      shipInstance.MissileLife = PlayerDefaultMissileLife;
      shipInstance.MissileDamage = PlayerDefaultMissileDamage;

      _serilogger.Debug("Game.cs: Adding ship to scene tree");
      AddChild(playerShipThingInstance);

      // if the player is not us and we are ingame, play the warp in sound
      if (inGame == true && uuid != myUuid) shipInstance.GetNode<AudioStreamPlayer2D>("WarpInSound").Play();
    }
    else return shipInstance;

    // TODO: this is inconsistent with the way the server uses the playerObjects array
    // where the server is using the ShipThing, this is using the PlayerShip. It may
    // or may not be significant down the line
    playerObjects.Add(uuid, shipInstance);

    return shipInstance;
  }

  /// <summary>
  /// Called when the network processes a game event with a update ship event
  /// </summary>
  /// <param name="uuid"></param>
  /// <returns>the ship instance</returns>
  // TODO: wouldn't it be more appropriate to call this GetShipFromUUID ? since we're fetching the ship.
  public PlayerShip UpdateShipWithUUID(string uuid)
  {
    _serilogger.Verbose("Game.cs: UpdateShipWithUUID");
    PlayerShip shipInstance;
    if (!playerObjects.TryGetValue(uuid, out shipInstance))
    {
      // must've joined before us - so we didn't get the create event, create it
      _serilogger.Debug("Game.cs: UpdateShipWithUUID: ship doesn't exist, creating " + uuid);
      shipInstance = this.CreateShipForUUID(uuid);
    }
    return shipInstance;
  }

  /// <summary>Called when a ship destroy message is received</summary>
  /// <param name="uuid"></param>
  /// <returns>nothing</returns>
  public void DestroyShipWithUUID(string uuid, int hitPoints)
  {
    PlayerShip shipInstance;

    // if we don't find anything, do nothing, since there's nothing displayed yet to remove
    if (playerObjects.TryGetValue(uuid, out shipInstance))
    {
      _serilogger.Debug($"Game.cs: checking hitpoints for {uuid}");
      if (hitPoints <= 0)
      {
        // TODO: would need to play the explode animation
        _serilogger.Debug($"Game.cs: hitpoints for {uuid} is <= 0, exploding");
        shipInstance.GetNode<AudioStreamPlayer2D>("ExplodeSound").Play();
      }
      else
      {
        _serilogger.Debug($"Game.cs: hitpoints for {uuid} is > 0, warp out");
        shipInstance.GetNode<AudioStreamPlayer2D>("WarpOutSound").Play();
      }

      _serilogger.Debug($"Game.cs: Expiring player {uuid}");

      // if we are removing ourselves, we should set ingame to false
      if (uuid == myUuid)
      {
        inGame = false;
        _serilogger.Debug($"Game.cs: destroy is for our player, display game over screen");
        displayGameOverScreen();
      }

      // remove our ship from the known ship objects
      playerObjects.Remove(uuid);
    }
  }

  /// <summary>
  /// Called when the network processes a game event with a create missile event
  ///
  /// </summary>
  /// <param name="uuid"></param>
  /// <returns>the created missile instance</returns>
  public SpaceMissile CreateMissileForUUID(GameEvent.GameObject gameObject)
  {
    // check if a key already exists for the uuid and return that missile if it does
    SpaceMissile existingMissile;
    if (missileObjects.TryGetValue(gameObject.Uuid, out existingMissile))
    {
      _serilogger.Debug($"Game.cs: Existing missile found for UUID: {gameObject.Uuid}");

      // check if it's our own missile that we're finally getting the create for
      if (existingMissile.uuid == myShip.MyMissile.uuid)
      {
        _serilogger.Debug($"Game.cs: Missile create was for our own existing missile");
      }
      return existingMissile;
    }

    // TODO: refactor missile setup into a dedicated function to prevent all this duplicated code
    // set up the new missile
    SpaceMissile missileInstance = (SpaceMissile)PackedMissile.Instantiate();

    // set the missile's UUID to the message's UUID
    missileInstance.uuid = gameObject.Uuid;

    // missiles have owners, so find the right player (hopefully)
    // TODO: this could conceivably blow up if we got missile information before we got player information
    missileInstance.MyPlayer = playerObjects[gameObject.OwnerUuid];

    // players own missiles, inversely
    missileInstance.MyPlayer.MyMissile = missileInstance;

    missileObjects.Add(gameObject.Uuid, missileInstance);
    missileInstance.GlobalPosition = new Vector2(gameObject.PositionX, gameObject.PositionY);
    missileInstance.RotationDegrees = gameObject.Angle;
    _serilogger.Debug("Game.cs: Adding missile to scene tree");
    AddChild(missileInstance);
    //missileInstance.GetNode<AudioStreamPlayer>("FireSound").Play();

    // just in case we need to use it later
    missileInstance.AddToGroup("missiles");

    // Run the missile animation
    _serilogger.Debug($"Game.cs: Starting missile animation for {missileInstance.uuid}");
    AnimatedSprite2D missileFiringAnimation = (AnimatedSprite2D)missileInstance.GetNode("Animations");
    missileFiringAnimation.Frame = 0;
    missileFiringAnimation.Play("launch");
    return missileInstance;
  }

  void CreateLocalMissileForUUID(string uuid)
  {
    // check if we already have a missile and return if we do
    if (myShip.MyMissile != null)
    {
      _serilogger.Debug($"Game.cs: Missile already exists, aborting.");
      return;
    }

    // check if we are not ready and return if we are not
    if (!myShip.MissileReady)
    {
      _serilogger.Debug($"Game.cs: Missile not ready, aborting.");
      return;
    }

    _serilogger.Debug($"Game.cs: Setting missile to not ready");
    myShip.MissileReady = false;
    myShip.MissileReloadCountdown = myShip.MissileReloadTime;

    _serilogger.Debug($"Game.cs: Creating local missile with uuid {uuid}");
    // set up the new missile
    PackedScene packedMissile = (PackedScene)ResourceLoader.Load("res://Scenes/SupportScenes/SpaceMissile.tscn");

    SpaceMissile missileInstance = (SpaceMissile)packedMissile.Instantiate();

    // set the missile's UUID to the message's UUID
    missileInstance.uuid = uuid;

    // missiles have owners, so find the right player (hopefully)
    // TODO: this could conceivably blow up if we got missile information before we got player information
    missileInstance.MyPlayer = myShip;

    // players own missiles, inversely
    missileInstance.MyPlayer.MyMissile = missileInstance;

    missileObjects.Add(uuid, missileInstance);

    // just in case we need to use it later
    missileInstance.AddToGroup("missiles");

    // missile should point in the same direction as the ship
    missileInstance.Rotation = myShip.Rotation;

    // TODO: need to offset this to the front of the ship
    // start at our position
    missileInstance.Position = myShip.GlobalPosition;

    // negative direction is "up"
    Vector2 offset = new Vector2(0, -60);

    // rotate the offset to match the current ship heading
    offset = offset.Rotated(myShip.Rotation);
    missileInstance.Position = missileInstance.Position + offset;

    _serilogger.Debug("Game.cs: Adding missile to scene tree");
    AddChild(missileInstance);

    // Run the missile animation
    _serilogger.Debug($"Game.cs: Starting missile animation for {missileInstance.uuid}");
    AnimatedSprite2D missileFiringAnimation = (AnimatedSprite2D)missileInstance.GetNode("Animations");
    missileFiringAnimation.Frame = 0;
    missileFiringAnimation.Play("launch");
  }

  /// <summary>
  /// Called when the network processes a game event with an update missile event
  /// </summary>
  /// <param name="uuid"></param>
  /// <returns>the missile instance</returns>
  public SpaceMissile UpdateMissileWithUUID(GameEvent.GameObject gameObject)
  {
    SpaceMissile missileInstance;
    if (!missileObjects.TryGetValue(gameObject.Uuid, out missileInstance))
    {
      // the missile existed before we started, so we didn't get the create event
      _serilogger.Debug($"Game.cs: UpdateMissileWithUUID: missile doesn't exist, creating {gameObject.Uuid} with owner {gameObject.OwnerUuid}");
      missileInstance = this.CreateMissileForUUID(gameObject);
    }

    return missileInstance;
  }


  /// <summary>Called when a missile destroy message is received</summary>
  /// <param name="uuid"></param>
  /// <returns>nothing</returns>
  public void DestroyMissileWithUUID(string uuid)
  {
    SpaceMissile missileInstance;

    // see if we know about the missile by checking the missileObjects array
    // if we don't, do nothing, since there's nothing displayed yet to remove
    if (missileObjects.TryGetValue(uuid, out missileInstance))
    {
      _serilogger.Debug($"Game.cs: DestroyMissileWithUUID Expiring {uuid} with sequence {sequenceNumber}");
      missileInstance.Expire(sequenceNumber);
    }
    else
    {
      _serilogger.Debug($"Game.cs: DestroyMissileWithUUID TryGetValue for uuid {uuid} failed!");
    }
  }

  void ProcessInputEvent(Vector2 velocity, Vector2 shoot)
  {
    long timeNow = DateTimeOffset.Now.ToUnixTimeMilliseconds();
    long timeDiff = timeNow - lastCommandSentMs;

    // if the last input is the same as the current input, check how long it's been
    // since we sent a command message
    if (lastVelocity == velocity || lastShoot == shoot)
    {
      _serilogger.Verbose("Game.cs: Last command same as this command");
      _serilogger.Verbose($"Game.cs: last: {lastCommandSentMs} now: {timeNow} diff: {timeDiff}");
      if (timeNow - lastCommandSentMs < sendCommandDelayMs)
      {
        _serilogger.Verbose("Game.cs: Should NOT send another command because of the window");
        return;
      }
    }

    lastVelocity = velocity;
    lastShoot = shoot;

    lastCommandSentMs = timeNow;

    // there was some kind of input, so construct a message to send to the server
    Command cb = new Command();
    cb.Uuid = myUuid;

    if ((velocity.Length() > 0) || (shoot.Length() > 0))
    {
      if (velocity.Length() > 0)
      {
        _serilogger.Verbose("Game.cs: Got move command");
        cb.command_type = Command.CommandType.CommandTypeMove;
        cb.InputX = (int)velocity.X;
        cb.InputY = (int)velocity.Y;
        serverConnection.SendCommand(cb);
      }

      // only process a shoot command if we don't already have our own missile
      if ((shoot.Length() > 0) && (myShip.MyMissile == null) && (myShip.MissileReady))
      {
        _serilogger.Verbose("Game.cs: Got shoot command");
        cb.command_type = Command.CommandType.CommandTypeShoot;

        // suggest a UUID for our new missile
        cb.MissileUuid = System.Guid.NewGuid().ToString();

        // TODO: should we wrap this in some kind of try/catch?
        // send the shoot command
        serverConnection.SendCommand(cb);

        // instantiate the missile locally
        CreateLocalMissileForUUID(cb.MissileUuid);
      }
    }

  }

  public override void _Notification(int what)
  {
    // when the game window is closed, you get this notification
    if (what == NotificationWMCloseRequest)
    {
      _serilogger.Information("Game.cs: Got quit notification");

      // stop the background music
      backgroundMusic.Stop();

      // check if our UUID is set. If it isn't, we don't have to send a leave
      // message for our player, so we can just return
      if (myUuid == null) return;
      QuitGame();
    }
  }

  void deleteChildren(Node theNode)
  {
    foreach (Node n in theNode.GetChildren())
    {
      theNode.RemoveChild(n);
      n.QueueFree();
    }
  }

  void OnLoginSuccess(string userId)
  {
    // user is authenticated. Now we try to connect to the server
    serverConnection = new ServerConnection();
    this.AddChild(serverConnection);
    this.myUuid = userId;

    serverConnection.ServerConnected += OnServerConnected;
    //serverConnection.Connect("isServerConnected", new Callable(this, "_on_is_server_connected"));
  }

  void OnServerConnected(bool isServerConnected)
  {
    // At this point the user is authenticated and the server is connected. Now we can go to the game
    if (isServerConnected && this.myUuid != null)
    {
      bool success = this.JoinGameAsPlayer(this.myUuid);

      if (success)
      {
        this.initializeGameUI();
        return;
      }

      _serilogger.Information("Game.cs: join failed TODO tell player why");
    }
  }

  void PlayBackgroundMusic()
  {
    if (inGame)
    {
      // if background music is playing, do nothing
      if (backgroundMusic.Playing)
      {
        return;
      }

      // we should be playing the music if we're in-game
      backgroundMusic.Play();
    }
    else // we're not in game, so stop the music
    {
      backgroundMusic.Stop();
    }
  }
}
