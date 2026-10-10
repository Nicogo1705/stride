// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.Font;
using Stride.Graphics.Regression;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;

namespace Stride.Harness.Demo;

/// <summary>
/// An interactive window for a pull request's harness scenes: a scene menu, a free camera, an overlay and a capture key.
/// </summary>
/// <remarks>
/// Keys follow the AZERTY layout: Z Q S D move, A E go down and up, right mouse button looks around, the wheel or Shift
/// changes the speed, R resets the camera, Escape opens the menu, P saves the screen and the state for review.
/// </remarks>
public class DemoGame : GameTestBase
{
    private readonly List<DemoScene> _scenes;
    private readonly List<string> _log = new();
    private readonly Dictionary<Color, Material> _materials = new();
    private readonly List<Entity> _sceneEntities = new();
    private DemoScene? _current;
    private Entity _camera = null!;
    private float _yaw, _pitch, _speed = 6f;
    private bool _menuOpen = true;
    private int _menuCursor;
    private int _typedNumber;
    private double _typedAt;
    private SpriteBatch _spriteBatch = null!;
    private SpriteFont? _font, _bigFont;
    private Texture _white = null!;
    private string? _capturePath;
    private string? _flash;
    private double _flashUntil;
    private double _fps;

    /// <summary> Shown in the window title and the overlay, so two builds side by side tell which is which </summary>
    public string BuildLabel { get; }

    /// <summary> Folder the P key writes its screenshots and state files to </summary>
    public string CaptureFolder { get; }

    /// <summary> Draws the overlay and the crosshair; off for clean captures </summary>
    public bool OverlayVisible { get; set; } = true;

    public DemoGame(IEnumerable<DemoScene> scenes, string buildLabel, string captureFolder)
    {
        _scenes = scenes.ToList();
        BuildLabel = buildLabel;
        CaptureFolder = captureFolder;
        GraphicsDeviceManager.PreferredBackBufferWidth = 1600;
        GraphicsDeviceManager.PreferredBackBufferHeight = 900;
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
        GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;
        GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
        IsFixedTimeStep = false;
        GlobalLogger.GlobalMessageLogged += OnLog;
    }

    /// <summary> The scene shown, null while only the menu is up </summary>
    public DemoScene? CurrentScene => _current;

    /// <summary>
    /// Non-interactive check: loads every scene for a few frames (overlay and fonts drawn), then saves one P capture
    /// and checks both of its files exist; returns the capture's path without extension. Throws on the first failure.
    /// </summary>
    public async System.Threading.Tasks.Task<string> SmokeTest(int framesPerScene = 5)
    {
        ScreenShotAutomationEnabled = false;
        while (_camera is null)
            await Script.NextFrame();

        foreach (var scene in _scenes)
        {
            Load(scene);
            for (int i = 0; i < framesPerScene; i++)
                await Script.NextFrame();
        }

        var path = Path.Combine(CaptureFolder, "P-smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _capturePath = path;
        for (int i = 0; i < 3 && _capturePath is not null; i++)
            await Script.NextFrame();
        if (File.Exists(path + ".png") == false || File.Exists(path + ".txt") == false)
            throw new InvalidOperationException($"The P capture did not write {path}.png and {path}.txt");
        return path;
    }

    protected override async System.Threading.Tasks.Task LoadContent()
    {
        await base.LoadContent();
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _white = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.White });
        try
        {
            // Dynamic fonts are read from the content database, not from the system: copy Windows' Arial there
            InstallSystemFont("fonts/Arial.ttf", "arial.ttf");
            InstallSystemFont("fonts/Arial Bold.ttf", "arialbd.ttf");
            _font = Font.NewDynamic(17, "Arial", FontStyle.Regular);
            _bigFont = Font.NewDynamic(26, "Arial", FontStyle.Bold);
        }
        catch (Exception e)
        {
            _font = _bigFont = null;
            Console.WriteLine("Demo overlay: Arial unavailable, falling back to the debug font. " + e.Message);
        }
    }

    private void InstallSystemFont(string url, string fileName)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
        using var target = Content.FileProvider.OpenStream(url, Stride.Core.IO.VirtualFileMode.Create, Stride.Core.IO.VirtualFileAccess.Write);
        using var stream = File.OpenRead(source);
        stream.CopyTo(target);
    }

    // The root scene is only there once the game runs, as in the other game tests
    private void SetUpView()
    {
        Window.Title = $"Harness demo - {BuildLabel}";
        var compositor = GraphicsCompositorHelper.CreateDefault(true, clearColor: new Color4(0.62f, 0.72f, 0.85f, 1f), graphicsProfile: GraphicsProfile.Level_11_0);
        SceneSystem.GraphicsCompositor = compositor;
        var root = SceneSystem.SceneInstance.RootScene.Entities;

        var sun = new Entity { new LightComponent { Type = new LightDirectional { Shadow = { Enabled = true, Size = LightShadowMapSize.Large } }, Intensity = 2.5f } };
        sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.6f, -0.95f, 0f);
        root.Add(sun);
        root.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(new Color3(0.85f, 0.9f, 1f)) }, Intensity = 0.35f } });
        _camera = new Entity { new CameraComponent { Slot = compositor.Cameras[0].ToSlotId(), NearClipPlane = 0.05f, FarClipPlane = 500f } };
        root.Add(_camera);
        OnViewReady();
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (_camera is null)
        {
            if (SceneSystem.SceneInstance?.RootScene is null)
                return;
            SetUpView();
        }
        var seconds = (float)gameTime.Elapsed.TotalSeconds;
        if (seconds > 0f)
            _fps = _fps * 0.95 + 0.05 / seconds;

        if (Input.IsKeyPressed(Keys.Escape))
            _menuOpen = !_menuOpen || _current is null;

        if (_menuOpen)
        {
            UpdateMenu();
            return;
        }

        UpdateCamera(seconds);
        if (Input.IsKeyPressed(Keys.Back) && _current is { } scene)
            Load(scene);
        if (Input.IsKeyPressed(Keys.V) && DebugViews.Count > 1)
        {
            DebugView = (DebugView + 1) % DebugViews.Count;
            OnDebugViewChanged(DebugView);
            Flash("Vue debug : " + DebugViews[DebugView]);
        }
        if (Input.IsKeyPressed(Keys.P))
            _capturePath = Path.Combine(CaptureFolder, "P-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        UpdateSubject(seconds);
    }

    /// <summary> Input specific to what the demo shows (physics interaction…), called when the menu is closed </summary>
    protected virtual void UpdateSubject(float seconds) { }

    /// <summary> Lines about the subject for the overlay and the P file (timings, counts, toggles) </summary>
    protected virtual IEnumerable<string> SubjectStatus() => Enumerable.Empty<string>();

    /// <summary> Extra lines for the P file only (measurements too long for the overlay) </summary>
    protected virtual IEnumerable<string> SubjectReport() => Enumerable.Empty<string>();

    /// <summary> Key help for the subject, shown under the camera keys </summary>
    protected virtual string SubjectKeys => string.Empty;

    /// <summary> Called every frame just before the scene is rendered, after the update and the physics step: refresh debug views here </summary>
    protected virtual void BeforeDraw() { }

    // Driving the demo from a script, through the simulated keyboard and mouse of the test harness (not in interactive mode)

    /// <summary> Shows the window although the game runs with simulated input </summary>
    public void ShowWindow() => MakeWindowVisibleOnRun = true;

    /// <summary> Places the camera at <paramref name="eye"/>, looking at <paramref name="target"/> </summary>
    public void SetCamera(Vector3 eye, Vector3 target)
    {
        var direction = Vector3.Normalize(target - eye);
        _yaw = MathF.Atan2(-direction.X, -direction.Z);
        _pitch = MathF.Asin(direction.Y);
        _camera.Transform.Position = eye;
        _camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(_yaw, _pitch, 0f);
    }

    /// <summary> Saves the screen and the state as the P key does, to the given path without extension </summary>
    public async System.Threading.Tasks.Task CaptureTo(string pathWithoutExtension)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pathWithoutExtension)!);
        _capturePath = pathWithoutExtension;
        while (_capturePath is not null)
            await Script.NextFrame();
    }

    public async System.Threading.Tasks.Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
            await Script.NextFrame();
    }

    /// <summary> Presses and releases a key on the simulated keyboard </summary>
    public async System.Threading.Tasks.Task Press(Keys key)
    {
        KeyboardSimulated.SimulateDown(key);
        await Frames(2);
        KeyboardSimulated.SimulateUp(key);
        await Frames(2);
    }

    /// <summary> Holds a key down for a number of frames </summary>
    public async System.Threading.Tasks.Task Hold(Keys key, int frames)
    {
        KeyboardSimulated.SimulateDown(key);
        await Frames(frames);
        KeyboardSimulated.SimulateUp(key);
        await Frames(1);
    }

    /// <summary> Shows a scene as if it was picked in the menu </summary>
    public void LoadScene(DemoScene scene) => Load(scene);

    /// <summary> The scenes of the menu </summary>
    public IReadOnlyList<DemoScene> Scenes => _scenes;

    /// <summary> Called once the camera and lights exist, before the first scene: add views that span every scene here </summary>
    protected virtual void OnViewReady() { }

    /// <summary> The debug views V cycles through, the first one being « Off » </summary>
    protected virtual IReadOnlyList<string> DebugViews { get; } = new[] { "Off" };

    /// <summary> Index of the current debug view in <see cref="DebugViews"/> </summary>
    protected int DebugView { get; private set; }

    /// <summary> Called when V changes the debug view, and after each scene load so the view applies to the new scene </summary>
    protected virtual void OnDebugViewChanged(int view) { }

    /// <summary> How the crosshair at the center of the screen is drawn </summary>
    protected enum CrosshairState { Idle, Target, Blocked, Holding }

    /// <summary> The crosshair's state, for demos where the center of the screen aims at something </summary>
    protected virtual CrosshairState Crosshair => CrosshairState.Idle;

    /// <summary> What the crosshair aims at, one line for the overlay, null for nothing </summary>
    protected virtual string? AimDescription => null;

    /// <summary> Called after a scene is built, before its first frame </summary>
    protected virtual void OnSceneLoaded(DemoScene scene) { }

    /// <summary> Called before the entities of a scene are removed </summary>
    protected virtual void OnSceneUnloading(DemoScene scene) { }

    /// <summary> Shows a short message in the overlay </summary>
    protected void Flash(string message)
    {
        _flash = message;
        _flashUntil = UpdateTime.Total.TotalSeconds + 2.5;
    }

    /// <summary> The camera entity, rays for picking start from it </summary>
    protected Entity CameraEntity => _camera;

    /// <summary> A plain colored material, shared by color </summary>
    public Material MaterialOf(Color color)
    {
        if (!_materials.TryGetValue(color, out var material))
        {
            var descriptor = new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(color.ToColor4())),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                },
            };
            _materials[color] = material = Material.New(GraphicsDevice, descriptor);
        }
        return material;
    }

    /// <summary> A procedural model with a plain color </summary>
    public Model Shape(PrimitiveProceduralModelBase shape, Color color)
    {
        var model = new ProceduralModelDescriptor(shape).GenerateModel(Services);
        model.Materials.Clear();
        model.Materials.Add(new MaterialInstance(MaterialOf(color)));
        return model;
    }

    /// <summary> Adds an entity that belongs to the current scene, removed when the scene changes </summary>
    public void AddToScene(Entity entity)
    {
        SceneSystem.SceneInstance.RootScene.Entities.Add(entity);
        _sceneEntities.Add(entity);
    }

    private void Load(DemoScene scene)
    {
        if (_current is not null)
            OnSceneUnloading(_current);
        foreach (var entity in _sceneEntities)
            SceneSystem.SceneInstance.RootScene.Entities.Remove(entity);
        _sceneEntities.Clear();

        _current = scene;
        scene.Build(this);
        ResetCamera();
        _menuOpen = false;
        OnSceneLoaded(scene);
        OnDebugViewChanged(DebugView);
    }

    private void UpdateMenu()
    {
        if (Input.IsKeyPressed(Keys.Down))
            _menuCursor = (_menuCursor + 1) % _scenes.Count;
        if (Input.IsKeyPressed(Keys.Up))
            _menuCursor = (_menuCursor + _scenes.Count - 1) % _scenes.Count;
        if (Input.IsKeyPressed(Keys.Enter))
            Load(_scenes[_menuCursor]);

        // Scene numbers are typed digit by digit: a number loads once no longer number can start with it, or after a short pause
        for (int digit = 0; digit <= 9; digit++)
        {
            if (Input.IsKeyPressed(Keys.D0 + digit) || Input.IsKeyPressed(Keys.NumPad0 + digit))
            {
                _typedNumber = _typedNumber * 10 + digit;
                _typedAt = UpdateTime.Total.TotalSeconds;
                if (_typedNumber >= 1 && _typedNumber <= _scenes.Count)
                    _menuCursor = _typedNumber - 1;
                if (_typedNumber * 10 > _scenes.Count)
                    LoadTyped();
            }
        }
        if (_typedNumber > 0 && UpdateTime.Total.TotalSeconds - _typedAt > 0.7)
            LoadTyped();

        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var mouse = Input.MousePosition * new Vector2(BackBufferSize.X, BackBufferSize.Y);
            foreach (var (index, area) in MenuLayout())
            {
                if (area.Contains(mouse))
                    Load(_scenes[index]);
            }
        }
    }

    private void LoadTyped()
    {
        var number = _typedNumber;
        _typedNumber = 0;
        if (number >= 1 && number <= _scenes.Count)
            Load(_scenes[number - 1]);
    }

    private void UpdateCamera(float seconds)
    {
        if (Input.IsKeyPressed(Keys.R))
            ResetCamera();

        if (Input.IsMouseButtonDown(MouseButton.Right))
        {
            _yaw -= Input.MouseDelta.X * 3f;
            _pitch = Math.Clamp(_pitch - Input.MouseDelta.Y * 3f, -1.5f, 1.5f);
        }
        if (Input.IsMouseButtonDown(MouseButton.Left) == false)
            _speed = Math.Clamp(_speed * MathF.Pow(1.2f, Input.MouseWheelDelta), 0.5f, 100f);

        var rotation = Quaternion.RotationYawPitchRoll(_yaw, _pitch, 0f);
        var move = Vector3.Zero;
        // Stride's keys are virtual keys: Keys.Z is the key labeled Z on the active layout, so this is AZERTY's ZQSD
        if (Input.IsKeyDown(Keys.Z)) move -= Vector3.UnitZ;
        if (Input.IsKeyDown(Keys.S)) move += Vector3.UnitZ;
        if (Input.IsKeyDown(Keys.Q)) move -= Vector3.UnitX;
        if (Input.IsKeyDown(Keys.D)) move += Vector3.UnitX;
        var vertical = (Input.IsKeyDown(Keys.E) ? 1f : 0f) - (Input.IsKeyDown(Keys.A) ? 1f : 0f);
        var speed = _speed * (Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift) ? 4f : 1f);
        _camera.Transform.Position += (Vector3.Transform(move, rotation) + Vector3.UnitY * vertical) * speed * seconds;
        _camera.Transform.Rotation = rotation;
    }

    private void ResetCamera()
    {
        if (_current is null)
            return;
        var direction = Vector3.Normalize(_current.Target - _current.Eye);
        _yaw = MathF.Atan2(-direction.X, -direction.Z);
        _pitch = MathF.Asin(direction.Y);
        _camera.Transform.Position = _current.Eye;
        _camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(_yaw, _pitch, 0f);
    }

    private Int2 BackBufferSize => new(GraphicsDevice.Presenter.BackBuffer.Width, GraphicsDevice.Presenter.BackBuffer.Height);

    private IEnumerable<(int Index, RectangleF Area)> MenuLayout()
    {
        float y = 110f;
        string? group = null;
        for (int i = 0; i < _scenes.Count; i++)
        {
            if (_scenes[i].Group != group)
            {
                group = _scenes[i].Group;
                y += 34f;
            }
            yield return (i, new RectangleF(60f, y, 900f, 26f));
            y += 28f;
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_camera is not null && _current is not null)
            BeforeDraw();
        base.Draw(gameTime);

        GraphicsContext.CommandList.SetRenderTargetAndViewport(null, GraphicsDevice.Presenter.BackBuffer);
        _spriteBatch.Begin(GraphicsContext);
        if (_menuOpen)
            DrawMenu();
        else if (OverlayVisible)
        {
            DrawOverlay();
            DrawCrosshair();
        }
        _spriteBatch.End();

        if (_capturePath is { } path)
        {
            _capturePath = null;
            Directory.CreateDirectory(CaptureFolder);
            SaveTexture(GraphicsDevice.Presenter.BackBuffer, path + ".png");
            File.WriteAllText(path + ".txt", StateReport(), Encoding.UTF8);
            Flash("P -> enregistré : " + Path.GetFileName(path));
        }
    }

    // A cross with a dark outline, readable on light and dark backgrounds
    private void DrawCrosshair()
    {
        var center = new Vector2(BackBufferSize.X / 2f, BackBufferSize.Y / 2f);
        var (color, size) = Crosshair switch
        {
            CrosshairState.Target => (Color.Yellow, 14f),
            CrosshairState.Blocked => (Color.Gray, 8f),
            CrosshairState.Holding => (Color.Cyan, 12f),
            _ => (Color.White, 8f),
        };
        void Bar(float x, float y, float width, float height)
        {
            _spriteBatch.Draw(_white, new RectangleF(center.X + x - 1, center.Y + y - 1, width + 2, height + 2), new Color(0, 0, 0, 200));
            _spriteBatch.Draw(_white, new RectangleF(center.X + x, center.Y + y, width, height), color);
        }
        if (Crosshair == CrosshairState.Holding)
        {
            // A square ring: something is held
            Bar(-size, -size, size * 2, 2);
            Bar(-size, size - 2, size * 2, 2);
            Bar(-size, -size, 2, size * 2);
            Bar(size - 2, -size, 2, size * 2);
        }
        else
        {
            Bar(-size, -1, size * 2, 2);
            Bar(-1, -size, 2, size * 2);
            if (Crosshair == CrosshairState.Target)
            {
                // Corner ticks: something grabbable is under the crosshair
                foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                {
                    Bar(dx * (size + 4) - (dx < 0 ? 0 : 6), dy * (size + 4) - (dy < 0 ? 0 : 2), 6, 2);
                    Bar(dx * (size + 4) - (dx < 0 ? 0 : 2), dy * (size + 4) - (dy < 0 ? 0 : 6), 2, 6);
                }
            }
        }
    }

    // Arial when available, the engine's debug font otherwise
    private void Text(SpriteFont? font, string text, Vector2 position, Color color)
    {
        if (font is not null)
            _spriteBatch.DrawString(font, text, position, color);
        else
            DebugTextSystem.Print(text, new Int2((int)position.X, (int)position.Y), color);
    }

    private void DrawMenu()
    {
        var size = BackBufferSize;
        _spriteBatch.Draw(_white, new RectangleF(0, 0, size.X, size.Y), new Color(0, 0, 0, 200));
        Text(_bigFont, $"Scènes - {BuildLabel}", new Vector2(60, 40), Color.White);
        Text(_font, "Numéro (un ou deux chiffres), clic ou flèches + Entrée pour choisir ; Échap pour fermer le menu", new Vector2(60, 78), Color.LightGray);

        string? group = null;
        foreach (var (index, area) in MenuLayout())
        {
            var scene = _scenes[index];
            if (scene.Group != group)
            {
                group = scene.Group;
                Text(_font, group.ToUpperInvariant(), new Vector2(area.X, area.Y - 30f), Color.Gold);
            }
            var selected = index == _menuCursor;
            if (selected)
                _spriteBatch.Draw(_white, area, new Color(235, 235, 235, 235));
            Text(_font, $"{index + 1,2}. " + scene.Name, new Vector2(area.X + 8, area.Y + 3), selected ? new Color(20, 20, 20) : Color.Gainsboro);
        }
    }

    private void DrawOverlay()
    {
        var lines = OverlayLines().ToList();
        var height = 14f + lines.Count * 21f;
        var width = 20f + lines.Max(line => _font?.MeasureString(line).X ?? line.Length * 8f);
        _spriteBatch.Draw(_white, new RectangleF(8, 8, width, height), new Color(0, 0, 0, 150));
        Text(_bigFont, BuildLabel, new Vector2(BackBufferSize.X - 40 - (_bigFont?.MeasureString(BuildLabel).X ?? BuildLabel.Length * 9f), 16), Color.Gold);
        float y = 15f;
        foreach (var line in lines)
        {
            Text(_font, line, new Vector2(18, y), Color.White);
            y += 21f;
        }
        if (_flash is not null && UpdateTime.Total.TotalSeconds < _flashUntil)
            Text(_bigFont, _flash, new Vector2(18, BackBufferSize.Y - 50), Color.LightGreen);
    }

    private IEnumerable<string> OverlayLines()
    {
        if (_current is not null)
        {
            yield return $"{_current.Group} - {_current.Name}";
            yield return "À regarder : " + _current.Description;
        }
        foreach (var line in SubjectStatus())
            yield return line;
        yield return string.Format(CultureInfo.InvariantCulture, "{0:0} FPS  |  {1} - {2}  |  {3}x{4}", _fps, GraphicsDevice.Platform, GraphicsDevice.Adapter.Description, BackBufferSize.X, BackBufferSize.Y);
        if (AimDescription is { } aim)
            yield return "Vise : " + aim;
        if (DebugViews.Count > 1)
            yield return "Vue debug (V) : " + DebugViews[DebugView];
        yield return "Z Q S D / A E : bouger  |  clic droit : regarder  |  molette, Maj : vitesse  |  R : caméra  |  Retour : réinitialiser  |  Échap : menu  |  P : capture" + (DebugViews.Count > 1 ? "  |  V : vue debug" : "");
        if (SubjectKeys.Length > 0)
            yield return SubjectKeys;
    }

    private string StateReport()
    {
        var text = new StringBuilder();
        var position = _camera.Transform.Position;
        text.AppendLine(CultureInfo.InvariantCulture, $"Build : {BuildLabel}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Scène : {_current?.Group} - {_current?.Name}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Caméra : position ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}), yaw {_yaw:0.####}, pitch {_pitch:0.####}");
        text.AppendLine(CultureInfo.InvariantCulture, $"API : {GraphicsDevice.Platform}, adaptateur : {GraphicsDevice.Adapter.Description}, {BackBufferSize.X}x{BackBufferSize.Y}, {_fps:0} FPS");
        text.AppendLine("Vue debug : " + DebugViews[DebugView] + "  |  réticule : " + Crosshair + (AimDescription is { } aim ? "  |  vise : " + aim : string.Empty));
        foreach (var line in SubjectStatus())
            text.AppendLine(line);
        if (_current is not null)
        {
            foreach (var line in _current.State())
                text.AppendLine(line);
        }
        foreach (var line in SubjectReport())
            text.AppendLine(line);
        text.AppendLine();
        text.AppendLine("Dernières lignes du log :");
        lock (_log)
        {
            foreach (var line in _log)
                text.AppendLine(line);
        }
        return text.ToString();
    }

    private void OnLog(ILogMessage message)
    {
        lock (_log)
        {
            _log.Add($"[{message.Type}] {message.Module}: {message.Text}");
            if (_log.Count > 60)
                _log.RemoveAt(0);
        }
    }

    protected override void Destroy()
    {
        GlobalLogger.GlobalMessageLogged -= OnLog;
        base.Destroy();
    }
}

/// <summary> One entry of the demo's menu </summary>
public abstract class DemoScene
{
    /// <summary> Menu section: « Captures » (the screenshot harness), « Bench », « Cas limites », « Contrôle » </summary>
    public abstract string Group { get; }

    public abstract string Name { get; }

    /// <summary> What to look at, one line </summary>
    public abstract string Description { get; }

    /// <summary> Where the camera starts, and what it looks at </summary>
    public abstract Vector3 Eye { get; }

    public abstract Vector3 Target { get; }

    /// <summary> Adds the scene's entities with <see cref="DemoGame.AddToScene"/> </summary>
    public abstract void Build(DemoGame game);

    /// <summary> Lines specific to the scene for the P file </summary>
    public virtual IEnumerable<string> State() => Enumerable.Empty<string>();
}
