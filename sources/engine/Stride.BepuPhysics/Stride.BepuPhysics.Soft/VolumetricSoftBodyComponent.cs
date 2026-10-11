// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using BepuPhysics;
using BepuPhysics.Constraints;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Soft.Topology;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;

namespace Stride.BepuPhysics.Soft;

/// <summary>
/// A soft body filling the volume of a closed model, like jelly or rubber.
/// </summary>
/// <remarks>
/// The inside of the model is split in a lattice of cubic cells, particles sit on their corners and every vertex of the model follows the cell it is in.
/// </remarks>
[DataContract]
[Display("Volumetric Soft Body")]
public sealed class VolumetricSoftBodyComponent : SoftBodyComponent
{
    private static readonly Logger Logger = GlobalLogger.GetLogger(nameof(VolumetricSoftBodyComponent));

    private int _resolution = 6;
    private float _stiffness = 20f;
    private float _shearStiffness = 20f;
    private float _volumeStiffness = 20f;
    private float _damping = 1f;
    private float _maximumStretch = 1.5f;
    private float _minimumCompression = 0.4f;
    private bool _followSkeleton;
    private float _skeletonStiffness = 4f;
    private float _skeletonDamping = 1f;

    private SkeletonBinding? _skeletonBinding;
    private ConstraintHandle[] _skeletonServos = [];
    private int[] _servoParticles = [];
    private SkeletonEdge[] _skeletonEdges = [];
    private (Tetrahedron Cells, ConstraintHandle Handle)[] _skeletonVolumes = [];
    private Vector3[] _skeletonTargets = [];

    /// <summary> Creates a soft body whose particles are spheres sized from the lattice </summary>
    [SetsRequiredMembers]
    public VolumetricSoftBodyComponent()
    {
    }

    /// <summary>
    /// The amount of cells along the longest side of the model, the particle count grows with its cube.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DataMemberRange(1, 64, 1, 4, 0)]
    [DefaultValue(6)]
    public int Resolution
    {
        get => _resolution;
        set
        {
            _resolution = Math.Clamp(value, 1, 64);
            InvalidateTopology();
        }
    }

    /// <summary>
    /// How fast the edges of the cells return to their rest length, in hertz.
    /// </summary>
    /// <remarks> Values above half the rate of the solver's substeps cannot be honored </remarks>
    [Display(category: CategorySoftBody)]
    [DefaultValue(20f)]
    public float Stiffness
    {
        get => _stiffness;
        set
        {
            _stiffness = value;
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// How fast the diagonals of the cells return to their rest length, in hertz, lower values let the body shear and twist more easily.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DefaultValue(20f)]
    public float ShearStiffness
    {
        get => _shearStiffness;
        set
        {
            _shearStiffness = value;
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// How fast squashed or stretched cells recover their volume, in hertz, zero lets the volume change freely.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DefaultValue(20f)]
    public float VolumeStiffness
    {
        get => _volumeStiffness;
        set
        {
            _volumeStiffness = MathF.Max(0f, value);
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// The damping ratio of every spring, one stops oscillations the fastest without overshooting.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DefaultValue(1f)]
    public float Damping
    {
        get => _damping;
        set
        {
            _damping = value;
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// How long the edges of the cells may get relative to their rest length, whatever pulls on them.
    /// </summary>
    /// <remarks>
    /// The springs alone give in to any force strong enough, this keeps the cells from being torn apart; zero removes the limit.
    /// </remarks>
    [Display(category: CategorySoftBody)]
    [DefaultValue(1.5f)]
    public float MaximumStretch
    {
        get => _maximumStretch;
        set
        {
            _maximumStretch = value <= 0f ? 0f : MathF.Max(1f, value);
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// How short the edges of the cells may get relative to their rest length, which keeps cells from collapsing and turning inside out.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    [DefaultValue(0.4f)]
    public float MinimumCompression
    {
        get => _minimumCompression;
        set
        {
            _minimumCompression = Math.Clamp(value, 0f, 1f);
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// Whether the particles are pulled toward where the animated skeleton of the model carries them, like flesh around bones.
    /// </summary>
    /// <remarks>
    /// The entity is then moved by whatever moves it, such as a script or an animation, instead of following the particles.
    /// Each particle is tied to the two bones nearest to it in the bind pose; it needs a model with a skeleton.
    /// </remarks>
    [Display(category: CategorySoftBody)]
    [DefaultValue(false)]
    public bool FollowSkeleton
    {
        get => _followSkeleton;
        set
        {
            _followSkeleton = value;
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// How fast particles return to where the skeleton carries them, in hertz; lower values make the body wobble and lag behind more.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DefaultValue(4f)]
    public float SkeletonStiffness
    {
        get => _skeletonStiffness;
        set
        {
            _skeletonStiffness = value;
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// The damping ratio of the pull toward the skeleton, below one the body overshoots and wobbles.
    /// </summary>
    [Display(category: CategorySoftBody)]
    [DefaultValue(1f)]
    public float SkeletonDamping
    {
        get => _skeletonDamping;
        set
        {
            _skeletonDamping = value;
            TryUpdateFeatures();
        }
    }

    // Over half the diagonal of a cell face, so no ray or small object slips between four neighboring particles
    internal override float DefaultRadiusFraction => 0.72f;

    internal override bool MovesEntity => _skeletonServos.Length == 0;

    internal override SoftBodyTopology BuildTopology(SourceMesh mesh) => LatticeBuilder.Build(mesh, _resolution, DefaultRadiusFraction);

    internal override void AddConstraints(global::BepuPhysics.Simulation simulation, ReadOnlySpan<BodyHandle> particles, ReadOnlySpan<bool> pinned, SoftBodyTopology topology)
    {
        bool follow = PrepareSkeletonBinding(topology);
        var edges = follow ? new List<SkeletonEdge>(topology.Edges.Length) : null;

        var stretch = Spring(_stiffness, _damping);
        var shear = Spring(_shearStiffness, _damping);
        var limit = Spring(MathF.Max(60f, _stiffness * 3f), 1f);
        foreach (var edge in topology.Edges)
        {
            var length = System.Numerics.Vector3.Distance(Position(simulation, particles, edge.A), Position(simulation, particles, edge.B));
            var distance = simulation.Solver.Add(particles[edge.A], particles[edge.B], new CenterDistanceConstraint(length, edge.Kind == EdgeKind.Stretch ? stretch : shear));

            // The edges along the lattice axes are enough to bound the shape of every cell
            ConstraintHandle bound = default;
            bool bounded = false;
            if (edge.Kind == EdgeKind.Stretch && (_maximumStretch > 0f || _minimumCompression > 0f))
            {
                var maximum = _maximumStretch > 0f ? length * _maximumStretch : float.MaxValue;
                bound = simulation.Solver.Add(particles[edge.A], particles[edge.B], new CenterDistanceLimit(length * _minimumCompression, maximum, limit));
                bounded = true;
            }
            edges?.Add(new SkeletonEdge(edge.A, edge.B, edge.Kind == EdgeKind.Shear, distance, bounded, bound));
        }

        var volumes = follow ? new List<(Tetrahedron, ConstraintHandle)>() : null;
        if (_volumeStiffness > 0f)
        {
            var volume = Spring(_volumeStiffness, _damping);
            foreach (var t in topology.Tetrahedra)
            {
                var handle = simulation.Solver.Add(particles[t.A], particles[t.B], particles[t.C], particles[t.D], new VolumeConstraint(Position(simulation, particles, t.A), Position(simulation, particles, t.B), Position(simulation, particles, t.C), Position(simulation, particles, t.D), volume));
                volumes?.Add((t, handle));
            }
        }

        if (follow)
            AddSkeletonServos(simulation, particles, pinned, edges!, volumes!);
    }

    internal override void BeforeStep(float deltaTime)
    {
        if (_skeletonServos.Length == 0 || ModelNodes.Refresh(Entity) is not { } skeleton || Simulation is null)
            return;

        var nodes = skeleton.NodeTransformations;
        bool moved = false;
        for (int i = 0; i < _skeletonTargets.Length; i++)
        {
            var target = _skeletonBinding!.Target(i, nodes);
            moved |= Vector3.DistanceSquared(target, _skeletonTargets[i]) > 1e-10f;
            _skeletonTargets[i] = target;
        }

        // A sleeping body would not see the animation, and an unchanged pose needs no update
        if (moved == false)
            return;
        Awake = true;

        var solver = Simulation.Simulation.Solver;
        var pull = Spring(_skeletonStiffness, _skeletonDamping);
        for (int s = 0; s < _skeletonServos.Length; s++)
            solver.ApplyDescription(_skeletonServos[s], new OneBodyLinearServo { Target = _skeletonTargets[_servoParticles[s]].ToNumeric(), SpringSettings = pull, ServoSettings = ServoSettings.Default });

        // The springs and volumes rest at the animated pose, so they only resist being pushed away from it, not the animation itself
        var stretch = Spring(_stiffness, _damping);
        var shear = Spring(_shearStiffness, _damping);
        var limit = Spring(MathF.Max(60f, _stiffness * 3f), 1f);
        foreach (var edge in _skeletonEdges)
        {
            var length = Vector3.Distance(_skeletonTargets[edge.A], _skeletonTargets[edge.B]);
            solver.ApplyDescription(edge.Distance, new CenterDistanceConstraint(length, edge.Shear ? shear : stretch));
            if (edge.HasLimit)
            {
                var maximum = _maximumStretch > 0f ? length * _maximumStretch : float.MaxValue;
                solver.ApplyDescription(edge.Limit, new CenterDistanceLimit(length * _minimumCompression, maximum, limit));
            }
        }

        var volume = Spring(_volumeStiffness, _damping);
        foreach (var (t, handle) in _skeletonVolumes)
            solver.ApplyDescription(handle, new VolumeConstraint(_skeletonTargets[t.A].ToNumeric(), _skeletonTargets[t.B].ToNumeric(), _skeletonTargets[t.C].ToNumeric(), _skeletonTargets[t.D].ToNumeric(), volume));
    }

    /// <summary> Ties the particles to the skeleton when <see cref="FollowSkeleton"/> is set, false otherwise or without a skeleton </summary>
    private bool PrepareSkeletonBinding(SoftBodyTopology topology)
    {
        _skeletonServos = [];
        _servoParticles = [];
        _skeletonEdges = [];
        _skeletonVolumes = [];
        _skeletonTargets = [];
        _skeletonBinding = null;
        if (_followSkeleton == false)
            return false;

        if (SourceModel?.Skeleton is not { } skeleton || SkeletonBinding.Build(skeleton, topology.RestPositions) is not { } binding)
        {
            Logger.Warning($"{nameof(VolumetricSoftBodyComponent)} on '{Entity?.Name}' follows the skeleton of a model which has no bone");
            return false;
        }
        _skeletonBinding = binding;
        return true;
    }

    /// <summary> One servo per free particle pulling it toward its place on the skeleton </summary>
    private void AddSkeletonServos(global::BepuPhysics.Simulation simulation, ReadOnlySpan<BodyHandle> particles, ReadOnlySpan<bool> pinned, List<SkeletonEdge> edges, List<(Tetrahedron, ConstraintHandle)> volumes)
    {
        var servos = new List<ConstraintHandle>(particles.Length);
        var servoParticles = new List<int>(particles.Length);
        _skeletonTargets = new Vector3[particles.Length];
        var pull = Spring(_skeletonStiffness, _skeletonDamping);
        for (int i = 0; i < particles.Length; i++)
        {
            var position = simulation.Bodies[particles[i]].Pose.Position;
            _skeletonTargets[i] = position.ToStride();
            if (pinned[i])
                continue;
            servos.Add(simulation.Solver.Add(particles[i], new OneBodyLinearServo { Target = position, SpringSettings = pull, ServoSettings = ServoSettings.Default }));
            servoParticles.Add(i);
        }
        _skeletonServos = servos.ToArray();
        _servoParticles = servoParticles.ToArray();
        _skeletonEdges = edges.ToArray();
        _skeletonVolumes = volumes.ToArray();
    }

    /// <summary> A spring of the lattice whose rest length follows the animated skeleton, with its stretch limit when it has one </summary>
    private readonly record struct SkeletonEdge(int A, int B, bool Shear, ConstraintHandle Distance, bool HasLimit, ConstraintHandle Limit);

    private static System.Numerics.Vector3 Position(global::BepuPhysics.Simulation simulation, ReadOnlySpan<BodyHandle> particles, int particle) => simulation.Bodies[particles[particle]].Pose.Position;
}
