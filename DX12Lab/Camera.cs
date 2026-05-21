using System;
using System.Numerics;

namespace DX12Lab;

public class Camera
{
    public Vector3 Position = new(-10, 3, 0);
    public float Yaw = 0f;
    public float Pitch = 0f;

    public float MoveSpeed = 10f;
    public float LookSpeed = 0.003f;

    private int _lastMouseX, _lastMouseY;
    private bool _wasRightButton;

    public void Update(InputDevice input, float dt)
    {
        if (input.RightButton)
        {
            if (_wasRightButton)
            {
                Yaw -= (input.MouseX - _lastMouseX) * LookSpeed;
                Pitch += (input.MouseY - _lastMouseY) * LookSpeed;
                Pitch = Math.Clamp(Pitch, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
            }
            _lastMouseX = input.MouseX;
            _lastMouseY = input.MouseY;
        }
        _wasRightButton = input.RightButton;

        var forward = GetForward();
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

        if (input.IsKeyDown(0x57)) Position += forward * MoveSpeed * dt;
        if (input.IsKeyDown(0x53)) Position -= forward * MoveSpeed * dt;
        if (input.IsKeyDown(0x44)) Position += right * MoveSpeed * dt;
        if (input.IsKeyDown(0x41)) Position -= right * MoveSpeed * dt;
        if (input.IsKeyDown(0x20)) Position += Vector3.UnitY * MoveSpeed * dt;
        if (input.IsKeyDown(0x11)) Position -= Vector3.UnitY * MoveSpeed * dt;
    }

    private Vector3 GetForward() => new(
        MathF.Sin(Yaw) * MathF.Cos(Pitch),
        -MathF.Sin(Pitch),
        MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Vector3 Target => Position + GetForward();
}