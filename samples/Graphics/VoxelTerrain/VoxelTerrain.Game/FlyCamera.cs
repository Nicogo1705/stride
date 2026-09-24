// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace VoxelTerrain;

/// <summary>
/// Flies the camera: hold the right mouse button to look around, and while it is held move with
/// WASD or ZQSD (both work, on either keyboard layout) or the arrows, C and E/Space for down and
/// up, Shift to go faster. The letter keys are free for the sample's own hotkeys otherwise.
/// </summary>
public sealed class FlyCamera : SyncScript
{
    private const float MaximumPitch = MathUtil.PiOverTwo * 0.99f;

    public float MovementSpeed { get; set; } = 8f;
    public float SpeedFactor { get; set; } = 5f;
    public Vector2 MouseRotationSpeed { get; set; } = new(1f, 1f);

    public override void Update()
    {
        var deltaTime = (float)Game.UpdateTime.Elapsed.TotalSeconds;
        var translation = Vector3.Zero;
        var yaw = 0f;
        var pitch = 0f;

        var flying = Input.HasMouse && Input.IsMouseButtonDown(MouseButton.Right);
        if (Input.HasKeyboard && flying)
        {
            var dir = Vector3.Zero;
            if (Input.IsKeyDown(Keys.W) || Input.IsKeyDown(Keys.Z) || Input.IsKeyDown(Keys.Up)) dir.Z += 1;
            if (Input.IsKeyDown(Keys.S) || Input.IsKeyDown(Keys.Down)) dir.Z -= 1;
            if (Input.IsKeyDown(Keys.A) || Input.IsKeyDown(Keys.Q) || Input.IsKeyDown(Keys.Left)) dir.X -= 1;
            if (Input.IsKeyDown(Keys.D) || Input.IsKeyDown(Keys.Right)) dir.X += 1;
            if (Input.IsKeyDown(Keys.C)) dir.Y -= 1;
            if (Input.IsKeyDown(Keys.E) || Input.IsKeyDown(Keys.Space)) dir.Y += 1;

            var speed = MovementSpeed * deltaTime;
            if (Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift))
                speed *= SpeedFactor;
            if (dir.Length() > 1f)
                dir.Normalize();
            translation = dir * speed;
        }

        if (Input.HasMouse)
        {
            if (flying)
            {
                Input.LockMousePosition();
                Game.IsMouseVisible = false;
                yaw -= Input.MouseDelta.X * MouseRotationSpeed.X;
                pitch -= Input.MouseDelta.Y * MouseRotationSpeed.Y;
            }
            else
            {
                Input.UnlockMousePosition();
                Game.IsMouseVisible = true;
            }
        }

        // Yaw around the world's up, pitch in local space, the pitch kept short of straight up or down.
        var rotation = Matrix.RotationQuaternion(Entity.Transform.Rotation);
        var right = Vector3.Normalize(Vector3.Cross(rotation.Forward, Vector3.UnitY));
        var currentPitch = MathUtil.PiOverTwo - MathF.Acos(Vector3.Dot(rotation.Forward, Vector3.UnitY));
        pitch = MathUtil.Clamp(currentPitch + pitch, -MaximumPitch, MaximumPitch) - currentPitch;

        translation.Z = -translation.Z;
        Entity.Transform.Position += Vector3.TransformCoordinate(translation, rotation);
        Entity.Transform.Rotation *= Quaternion.RotationAxis(right, pitch) * Quaternion.RotationAxis(Vector3.UnitY, yaw);
    }
}
