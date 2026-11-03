using Godot;
using Roadman.Autoload;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Scenes;

namespace Roadman.OnFoot;

/// <summary>
/// The player on foot (<c>docs/specs/player-on-foot.md</c>), a <see cref="CharacterBody3D"/> built in code: a capsule
/// on its own collision layer (<see cref="CollisionLayers.Player"/>) that collides with the map
/// (<see cref="CollisionLayers.Map"/>), and a placeholder model (body, head and a cap whose brim shows the facing).
/// Each physics frame it reads <c>move_*</c> (WASD or the left stick, radial deadzone), <c>run</c> (Shift, held),
/// <c>run_toggle</c> (the left-stick click, through <see cref="RunToggle"/>) and <c>jump</c> (Space or the south face
/// button), moves with <see cref="OnFootMotion"/>
/// relative to <see cref="Camera"/>'s yaw, applies the project's default gravity, calls <see cref="CharacterBody3D.MoveAndSlide"/>
/// and writes its position to <see cref="GameState"/>'s <c>PlayerState</c>. The origin is at the feet.
/// </summary>
public partial class Player : CharacterBody3D
{
    /// <summary>Placeholder material, until the character kit is chosen (Art direction).</summary>
    public const string PlaceholderMaterialPath = "res://materials/placeholder/player.tres";

    public const string MoveForwardAction = "move_forward";
    public const string MoveBackAction = "move_back";
    public const string MoveLeftAction = "move_left";
    public const string MoveRightAction = "move_right";
    public const string RunAction = "run";

    /// <summary>The gamepad run toggle (left-stick click); see <see cref="RunToggle"/>.</summary>
    public const string RunToggleAction = "run_toggle";
    public const string JumpAction = "jump";

    private float _facingYaw;
    private float _capsuleHeight = 1.8f;
    private float _capsuleRadius = 0.3f;

    /// <summary>Capsule height, m; eyes at about 1.7 m. Changing it on a ready player rebuilds the capsule and the model.</summary>
    [Export]
    public float CapsuleHeight
    {
        get => _capsuleHeight;
        set
        {
            _capsuleHeight = value;
            RebuildBody();
        }
    }

    /// <summary>Capsule radius, m; about shoulder width. Changing it on a ready player rebuilds the capsule and the model.</summary>
    [Export]
    public float CapsuleRadius
    {
        get => _capsuleRadius;
        set
        {
            _capsuleRadius = value;
            RebuildBody();
        }
    }

    [Export]
    public float WalkSpeed { get; set; } = 2.8f;

    [Export]
    public float RunSpeed { get; set; } = 7.5f;

    /// <summary>m/s² with input.</summary>
    [Export]
    public float Acceleration { get; set; } = 20f;

    /// <summary>m/s² without input.</summary>
    [Export]
    public float Deceleration { get; set; } = 25f;

    /// <summary>Factor (0 to 1) on the acceleration in the air.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float AirControl { get; set; } = 0.3f;

    /// <summary>Degrees per second.</summary>
    [Export]
    public float TurnSpeed { get; set; } = 720f;

    /// <summary>Jump apex above the take-off height, m (barriers are at most 0.9 m; see the spec).</summary>
    [Export]
    public float JumpHeight { get; set; } = 1.3f;

    /// <summary>
    /// The steepest walkable slope in degrees; sets <see cref="CharacterBody3D.FloorMaxAngle"/>. Above the 99.9th
    /// percentile of every walkable surface in Somero (spec, "Exports").
    /// </summary>
    [Export(PropertyHint.Range, "0,89,0.1")]
    public float FloorMaxAngleDegrees
    {
        get => Mathf.RadToDeg(FloorMaxAngle);
        set => FloorMaxAngle = Mathf.DegToRad(value);
    }

    /// <summary>Material of the placeholder model; null means <see cref="PlaceholderMaterialPath"/>.</summary>
    [Export]
    public Material? ModelMaterial { get; set; }

    /// <summary>
    /// False while something else owns the controls (the debug camera): no move, run, run-toggle or jump input, but the
    /// body still falls under gravity and decelerates. <see cref="RunToggle"/> keeps its state.
    /// </summary>
    public bool InputEnabled { get; set; } = true;

    /// <summary>The camera whose yaw makes movement camera-relative; null means yaw 0 (forward is −Z, north).</summary>
    public PlayerCamera? Camera { get; set; }

    /// <summary>
    /// The gamepad run toggle, updated every physics frame from <see cref="RunToggleAction"/> and whether the move vector
    /// is non-zero. Running is on while <see cref="RunAction"/> is held or this is on.
    /// </summary>
    public RunToggle RunToggle { get; } = new();

    /// <summary>The movement maths; its values are set from the exports every physics frame.</summary>
    public OnFootMotion Motion { get; } = new();

    /// <summary>The project's default gravity, m/s² (<c>physics/3d/default_gravity</c>).</summary>
    public float Gravity { get; private set; } = 9.8f;

    /// <summary>The take-off speed for <see cref="JumpHeight"/> under <see cref="Gravity"/>, m/s.</summary>
    public float JumpSpeed => (float)OnFootMotion.JumpVelocity(JumpHeight, Gravity);

    public CollisionShape3D CollisionShape { get; private set; } = null!;

    /// <summary>The placeholder model; turned to <see cref="FacingYaw"/>.</summary>
    public Node3D Model { get; private set; } = null!;

    /// <summary>The facing yaw in radians (Godot's convention: yaw 0 looks along −Z). The capsule doesn't turn.</summary>
    public float FacingYaw
    {
        get => _facingYaw;
        set
        {
            _facingYaw = (float)OnFootMotion.WrapAngle(value);
            if (Model is not null)
            {
                Model.Rotation = new Vector3(0f, _facingYaw, 0f);
            }
        }
    }

    public Player()
    {
        FloorMaxAngleDegrees = 45f;

        // The spec's FloorSnapLength export is the native CharacterBody3D property: more than the 0.25 m step at the
        // world edge and the 0.05 m road edges.
        FloorSnapLength = 0.3f;
    }

    public override void _Ready()
    {
        CollisionLayer = CollisionLayers.Player;
        CollisionMask = CollisionLayers.Map;
        Gravity = ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f).AsSingle();
        ApplyExports();

        CollisionShape = new CollisionShape3D { Name = "CollisionShape" };
        AddChild(CollisionShape);
        BuildBody();
    }

    public override void _PhysicsProcess(double delta)
    {
        ApplyExports();

        var move = InputEnabled
            ? Input.GetVector(MoveLeftAction, MoveRightAction, MoveForwardAction, MoveBackAction)
            : Vector2.Zero;
        // With input off no press is fed and the stick reads zero; that zero isn't a stop of the player's own, so the
        // toggle keeps its state through it.
        if (InputEnabled)
        {
            RunToggle.Update(Input.IsActionJustPressed(RunToggleAction), move != Vector2.Zero);
        }
        else
        {
            RunToggle.ResetMovement();
        }

        var run = InputEnabled && (Input.IsActionPressed(RunAction) || RunToggle.On);
        var onFloor = IsOnFloor();
        var cameraYaw = Camera is not null && IsInstanceValid(Camera) ? Camera.Yaw : 0f;

        var velocity = Velocity;
        var step = Motion.Step(move.X, move.Y, cameraYaw, run, onFloor, velocity.X, velocity.Z, _facingYaw, delta);
        velocity.X = (float)step.VelocityX;
        velocity.Z = (float)step.VelocityZ;
        FacingYaw = (float)step.FacingYaw;

        if (!onFloor)
        {
            velocity.Y -= Gravity * (float)delta;
        }
        else if (InputEnabled && Input.IsActionJustPressed(JumpAction))
        {
            // On the floor only: no double jump and no buffered press from the air.
            velocity.Y = JumpSpeed;
        }

        Velocity = velocity;
        MoveAndSlide();
        CommitPosition();
    }

    public override void _Notification(int what)
    {
        // The actions release when the window loses focus; the gamepad run toggle stays as it was (spec, "Edge cases").
        if (what == NotificationApplicationFocusOut)
        {
            RunToggle.ResetMovement();
        }
    }

    /// <summary>
    /// Puts the body at <paramref name="position"/> (its feet) with zero velocity and the given facing, and commits the
    /// position. The node must be in the tree.
    /// </summary>
    public void PlaceAt(Vector3 position, float facingYaw)
    {
        GlobalPosition = position;
        Velocity = Vector3.Zero;
        FacingYaw = facingYaw;
        CommitPosition();
    }

    /// <summary>Writes the global position to <c>GameState.Instance.Player</c> (when the autoload exists).</summary>
    public void CommitPosition()
    {
        var position = GlobalPosition;
        GameState.Instance?.Player.SetPosition(new LocalPoint(position.X, position.Y, position.Z));
    }

    /// <summary>Sets the capsule shape from the exports and replaces the placeholder model with one of that size.</summary>
    private void BuildBody()
    {
        CollisionShape.Shape = new CapsuleShape3D { Radius = CapsuleRadius, Height = CapsuleHeight };
        CollisionShape.Position = new Vector3(0f, CapsuleHeight / 2f, 0f);

        if (Model is not null && IsInstanceValid(Model))
        {
            RemoveChild(Model);
            Model.QueueFree();
        }

        Model = CreateModel(ModelMaterial ?? GD.Load<Material>(PlaceholderMaterialPath));
        AddChild(Model);
        FacingYaw = _facingYaw;
    }

    /// <summary>Applies a changed capsule size once the node is ready (before that, <see cref="_Ready"/> builds it).</summary>
    private void RebuildBody()
    {
        if (IsNodeReady() && CollisionShape is not null && IsInstanceValid(CollisionShape))
        {
            BuildBody();
        }
    }

    private void ApplyExports()
    {
        Motion.WalkSpeed = WalkSpeed;
        Motion.RunSpeed = RunSpeed;
        Motion.Acceleration = Acceleration;
        Motion.Deceleration = Deceleration;
        Motion.AirControl = AirControl;
        Motion.TurnSpeed = TurnSpeed;
    }

    /// <summary>Body, head and a cap whose brim points along −Z (the front), scaled to <see cref="CapsuleHeight"/>.</summary>
    private Node3D CreateModel(Material material)
    {
        var scale = CapsuleHeight / 1.8f;
        var model = new Node3D { Name = "Model" };

        var bodyHeight = 1.35f * scale;
        model.AddChild(new MeshInstance3D
        {
            Name = "Body",
            Mesh = new CapsuleMesh { Radius = CapsuleRadius * 0.9f, Height = bodyHeight, Material = material },
            Position = new Vector3(0f, bodyHeight / 2f, 0f),
        });

        var headRadius = 0.15f * scale;
        var headY = bodyHeight + headRadius;
        model.AddChild(new MeshInstance3D
        {
            Name = "Head",
            Mesh = new SphereMesh { Radius = headRadius, Height = headRadius * 2f, Material = material },
            Position = new Vector3(0f, headY, 0f),
        });

        model.AddChild(new MeshInstance3D
        {
            Name = "Cap",
            Mesh = new CylinderMesh
            {
                TopRadius = headRadius * 1.02f,
                BottomRadius = headRadius * 1.05f,
                Height = headRadius * 0.6f,
                Material = material,
            },
            Position = new Vector3(0f, headY + (headRadius * 0.55f), 0f),
        });

        model.AddChild(new MeshInstance3D
        {
            Name = "CapBrim",
            Mesh = new BoxMesh { Size = new Vector3(headRadius * 1.6f, 0.03f * scale, headRadius * 1.3f), Material = material },
            Position = new Vector3(0f, headY + (headRadius * 0.3f), -headRadius * 1.25f),
        });

        model.AddChild(new MeshInstance3D
        {
            Name = "Nose",
            Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.06f, 0.08f) * scale, Material = material },
            Position = new Vector3(0f, headY - (headRadius * 0.1f), -headRadius),
        });

        return model;
    }
}
