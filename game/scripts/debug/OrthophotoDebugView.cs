using System.Collections.Generic;
using Godot;
using Roadman.Core.Maps;
using Roadman.Scenes;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Roadman.Debug;

/// <summary>
/// Debug view that drapes the pack's orthophoto over the terrain. The <c>debug_orthophoto</c> action (F2) switches
/// every loaded tile's terrain between <see cref="MapLoader.TerrainMaterial"/> and an unshaded material with that
/// tile's image from <c>debug/orthophoto/&lt;tile_id&gt;.jpg</c> in the pack. While it is on, a label in the
/// bottom-right corner shows the attribution and licence of the manifest's orthophoto credits.
/// <para>
/// <see cref="World"/> adds it in debug builds only. The image folder is <c>.gdignore</c>d, so the files are read
/// from disk with <see cref="Image.LoadFromFile"/>, never through <see cref="ResourceLoader"/>. They load on the first
/// switch-on, get mipmaps, and stay cached until the map reloads. A tile without an image keeps the placeholder.
/// </para>
/// <para>
/// The material is unshaded: the photo already has the sun's light and shadows of the flight in it, so lighting it
/// again would darken and double-shade it, and the colours would no longer match the imagery being checked. The cost
/// is that the terrain relief no longer shows through shading while the view is on.
/// </para>
/// </summary>
public partial class OrthophotoDebugView : Node
{
    public const string ToggleAction = "debug_orthophoto";

    /// <summary>Emitted when the view switches on or off.</summary>
    [Signal]
    public delegate void ToggledEventHandler(bool enabled);

    /// <summary>Emitted when switching on fails because no tile has an image; the view stays off.</summary>
    [Signal]
    public delegate void UnavailableEventHandler(string message);

    /// <summary>How long the "no orthophoto" message stays on screen, in seconds.</summary>
    [Export]
    public float MessageSeconds { get; set; } = 4f;

    [Export]
    public int FontSize { get; set; } = 14;

    /// <summary>Text outline width in pixels, so the text stays readable where the panel is light.</summary>
    [Export]
    public int OutlineSize { get; set; } = 4;

    [Export]
    public Color PanelColour { get; set; } = new(0f, 0f, 0f, 0.6f);

    /// <summary>Distance of the overlay panels from the screen edges, in pixels.</summary>
    [Export]
    public int ScreenMargin { get; set; } = 8;

    /// <summary>Canvas layer of the overlay, above the game's HUD.</summary>
    [Export]
    public int OverlayLayer { get; set; } = 100;

    /// <summary>The map loader whose tiles are switched; set before adding the node.</summary>
    public MapLoader? Loader { get; set; }

    /// <summary>
    /// Folder of the per-tile images (<c>res://</c>, <c>user://</c> or absolute). Null means the pack's
    /// <c>debug/orthophoto</c> folder. Tests point it at a temporary folder.
    /// </summary>
    public string? TextureFolder { get; set; }

    /// <summary>Credits for the attribution overlay. Null means the loaded manifest's credits.</summary>
    public IReadOnlyList<MapCredit>? Credits { get; set; }

    public bool Enabled { get; private set; }

    /// <summary>Number of tiles whose image is loaded and cached.</summary>
    public int CachedTextureCount => _materials.Count;

    public CanvasLayer Overlay { get; private set; } = null!;

    public PanelContainer AttributionPanel { get; private set; } = null!;

    public Label AttributionLabel { get; private set; } = null!;

    public PanelContainer MessagePanel { get; private set; } = null!;

    public Label MessageLabel { get; private set; } = null!;

    /// <summary>The image folder in use: <see cref="TextureFolder"/>, or the loaded pack's.</summary>
    public string ResolvedTextureFolder => TextureFolder ?? Orthophoto.Folder(Loader?.PackPath ?? "res://maps/unknown");

    /// <summary>Unshaded orthophoto materials by tile id; a tile is missing when it has no image.</summary>
    private readonly Dictionary<string, StandardMaterial3D> _materials = new(System.StringComparer.Ordinal);

    /// <summary>Tile ids whose image was looked for, found or not, so a missing image isn't looked for again.</summary>
    private readonly HashSet<string> _attempted = new(System.StringComparer.Ordinal);

    private int _messageSerial;

    public override void _Ready()
    {
        BuildOverlay();
        if (Loader is not null)
        {
            Loader.MapLoaded += OnMapLoaded;
        }
    }

    public override void _ExitTree()
    {
        if (Loader is not null && IsInstanceValid(Loader))
        {
            Loader.MapLoaded -= OnMapLoaded;
        }

        // Moved into a screenshot viewport by AttachOverlayTo: it doesn't go with this node.
        if (IsInstanceValid(Overlay) && Overlay.GetParent() != this)
        {
            Overlay.QueueFree();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (InputMap.HasAction(ToggleAction) && @event.IsActionPressed(ToggleAction, allowEcho: false))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Switches the view; see <see cref="SetEnabled"/>.</summary>
    public bool Toggle() => SetEnabled(!Enabled);

    /// <summary>
    /// Switches the orthophoto on or off. Switching on loads any images not yet cached; when no loaded tile has
    /// one, it shows <see cref="Orthophoto.NoTexturesMessage"/>, emits <see cref="SignalName.Unavailable"/> and
    /// stays off.
    /// </summary>
    /// <returns>True when the view is now in the requested state.</returns>
    public bool SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
        {
            return true;
        }

        if (!enabled)
        {
            ApplyMaterials(orthophoto: false);
            Enabled = false;
            AttributionPanel.Visible = false;
            EmitSignal(SignalName.Toggled, false);
            return true;
        }

        LoadMissingTextures();
        if (_materials.Count == 0)
        {
            var message = Orthophoto.NoTexturesMessage(Loader?.ResolvedMapId ?? MapLoader.DefaultMapId);
            GD.PushWarning($"OrthophotoDebugView: no tile images in {ResolvedTextureFolder}. {message}");
            ShowMessage(message);
            EmitSignal(SignalName.Unavailable, message);
            return false;
        }

        ApplyMaterials(orthophoto: true);
        Enabled = true;
        UpdateAttribution();
        AttributionPanel.Visible = true;
        EmitSignal(SignalName.Toggled, true);
        return true;
    }

    /// <summary>
    /// Moves the overlay under <paramref name="parent"/>, for example a screenshot <see cref="SubViewport"/>: a
    /// <see cref="CanvasLayer"/> draws only in the viewport it is in.
    /// </summary>
    public void AttachOverlayTo(Node parent)
    {
        Overlay.GetParent()?.RemoveChild(Overlay);
        parent.AddChild(Overlay);
    }

    private void OnMapLoaded(int tileCount)
    {
        // A reload may be another pack: drop the cache and, if the view was on, switch it on for the new tiles.
        var wasEnabled = Enabled;
        Enabled = false;
        AttributionPanel.Visible = false;
        _materials.Clear();
        _attempted.Clear();
        if (wasEnabled && !SetEnabled(true))
        {
            EmitSignal(SignalName.Toggled, false);
        }
    }

    private IEnumerable<(string TileId, MeshInstance3D Terrain)> LoadedTerrain()
    {
        if (Loader?.Tiles is not { } tiles || !IsInstanceValid(tiles))
        {
            yield break;
        }

        foreach (var tile in tiles.GetChildren())
        {
            if (!tile.IsQueuedForDeletion() && MapLoader.FindTerrainMesh(tile) is { } terrain)
            {
                yield return (tile.Name.ToString(), terrain);
            }
        }
    }

    private void LoadMissingTextures()
    {
        var folder = ProjectSettings.GlobalizePath(ResolvedTextureFolder);
        var folderExists = Directory.Exists(folder);
        foreach (var (tileId, _) in LoadedTerrain())
        {
            if (!_attempted.Add(tileId) || !folderExists)
            {
                continue;
            }

            var path = Orthophoto.TexturePath(folder, tileId);
            if (!File.Exists(path))
            {
                continue;
            }

            var image = Image.LoadFromFile(path);
            if (image is null || image.IsEmpty())
            {
                GD.PushWarning($"OrthophotoDebugView: can't read {path}; tile {tileId} keeps the placeholder");
                continue;
            }

            image.GenerateMipmaps();
            _materials[tileId] = new StandardMaterial3D
            {
                ResourceName = $"Orthophoto {tileId}",
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = ImageTexture.CreateFromImage(image),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                // Clamp, so the far edge of the image doesn't bleed into the tile's border.
                TextureRepeat = false,
            };
        }
    }

    private void ApplyMaterials(bool orthophoto)
    {
        var placeholder = Loader?.TerrainMaterial;
        foreach (var (tileId, terrain) in LoadedTerrain())
        {
            terrain.MaterialOverride = orthophoto && _materials.TryGetValue(tileId, out var material) ? material : placeholder;
        }
    }

    private void UpdateAttribution()
    {
        var credits = Credits ?? Loader?.Manifest?.Credits ?? [];
        var text = Orthophoto.AttributionText(credits);
        if (text.Length == 0)
        {
            var mapId = Loader?.ResolvedMapId ?? MapLoader.DefaultMapId;
            text = $"Orthophoto credit ({Orthophoto.CreditSource}) missing from the manifest; {MapPack.RegenHint(mapId)}";
            GD.PushWarning($"OrthophotoDebugView: {text}");
        }

        AttributionLabel.Text = text;
        PlaceInBottomRight(AttributionPanel);
    }

    private void ShowMessage(string message)
    {
        MessageLabel.Text = message;
        MessagePanel.Visible = true;
        var serial = ++_messageSerial;
        if (MessageSeconds > 0f && IsInsideTree())
        {
            GetTree().CreateTimer(MessageSeconds).Timeout += () =>
            {
                if (serial == _messageSerial && IsInstanceValid(MessagePanel))
                {
                    MessagePanel.Visible = false;
                }
            };
        }
    }

    private void BuildOverlay()
    {
        Overlay = new CanvasLayer { Name = "OrthophotoOverlay", Layer = OverlayLayer };
        AddChild(Overlay);

        (AttributionPanel, AttributionLabel) = CreatePanel("Attribution", HorizontalAlignment.Right);
        Overlay.AddChild(AttributionPanel);
        PlaceInBottomRight(AttributionPanel);

        (MessagePanel, MessageLabel) = CreatePanel("Message", HorizontalAlignment.Left);
        Overlay.AddChild(MessagePanel);
        MessagePanel.Position = new Vector2(ScreenMargin, ScreenMargin);
    }

    private (PanelContainer Panel, Label Label) CreatePanel(string name, HorizontalAlignment alignment)
    {
        var style = new StyleBoxFlat { BgColor = PanelColour };
        style.SetContentMarginAll(6f);
        var panel = new PanelContainer { Name = $"{name}Panel", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", style);

        var label = new Label
        {
            Name = $"{name}Label",
            HorizontalAlignment = alignment,
            LabelSettings = new LabelSettings
            {
                FontSize = FontSize,
                FontColor = Colors.White,
                OutlineSize = OutlineSize,
                OutlineColor = Colors.Black,
            },
        };
        panel.AddChild(label);
        return (panel, label);
    }

    /// <summary>Anchors <paramref name="panel"/> to the bottom-right corner, growing up and left with its text.</summary>
    private void PlaceInBottomRight(Control panel)
    {
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.AnchorLeft = 1f;
        panel.AnchorTop = 1f;
        panel.AnchorRight = 1f;
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = -ScreenMargin;
        panel.OffsetTop = -ScreenMargin;
        panel.OffsetRight = -ScreenMargin;
        panel.OffsetBottom = -ScreenMargin;
    }
}
