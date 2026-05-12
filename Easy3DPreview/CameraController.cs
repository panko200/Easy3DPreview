using System;
using System.Numerics;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// マウス入力からカメラの View 行列と Projection 行列を計算する。
/// YMM4 座標系: X+ = 右, Y+ = 下, Z+ = 手前
/// </summary>
internal sealed class CameraController
{
    // ── カメラ状態 ──
    private Vector3 _target = Vector3.Zero;        // 注視点
    private float _distance = 1000f;                // 注視点からの距離
    private float _yaw = 0f;                        // 水平回転 (ラジアン)
    private float _pitch = 0f;                      // 垂直回転 (ラジアン)
    private float _roll = 0f;                       // Z回転（ロール） (ラジアン)

    // ── 投影 ──
    private bool _isOrthographic = false;
    private float _fovDegrees = 60f;
    private float _orthoScale = 1f;                 // 平行投影のスケール

    // ── 操作感 ──
    private const float RotateSensitivity = 0.005f;
    private const float PanSensitivity = 1.0f;
    private const float ZoomStep = 50f;
    private const float MinDistance = 10f;
    private const float MaxDistance = 100000f;

    /// <summary>現在の投影モード</summary>
    public bool IsOrthographic
    {
        get => _isOrthographic;
        set => _isOrthographic = value;
    }

    /// <summary>カメラ位置</summary>
    public Vector3 CameraPosition => _target + GetCameraOffset();

    /// <summary>
    /// カメラをデフォルト位置にリセット。
    /// </summary>
    public void Reset()
    {
        _target = Vector3.Zero;
        _distance = 1000f;
        _yaw = 0f;
        _pitch = 0f;
        _roll = 0f;
        _orthoScale = 1f;
    }

    /// <summary>
    /// マウスホイールによるズーム。
    /// </summary>
    public void Zoom(float delta)
    {
        float factor = 1f - delta * 0.001f;
        _distance = Math.Clamp(_distance * factor, MinDistance, MaxDistance);
        _orthoScale = Math.Clamp(_orthoScale * factor, 0.01f, 100f);
    }

    /// <summary>
    /// 右クリック+ドラッグによるオービット回転。
    /// </summary>
    public void Orbit(float deltaX, float deltaY)
    {
        _yaw -= deltaX * RotateSensitivity;
        _pitch -= deltaY * RotateSensitivity;

        // ピッチを ±89度 に制限
        float limit = MathF.PI / 2f - 0.01f;
        _pitch = Math.Clamp(_pitch, -limit, limit);
    }

    /// <summary>
    /// 右クリック+中ボタン等によるZ回転（ロール）。
    /// </summary>
    public void AddRoll(float deltaAngle)
    {
        _roll -= deltaAngle * RotateSensitivity;
    }

    /// <summary>
    /// 中ボタン+ドラッグによるパン移動。
    /// </summary>
    public void Pan(float deltaX, float deltaY)
    {
        var (right, up, _) = GetCameraAxes();
        float scale = _distance * PanSensitivity * 0.001f;
        _target -= right * deltaX * scale;
        _target += up * deltaY * scale;
    }

    /// <summary>
    /// View 行列を計算する。
    /// </summary>
    public Matrix4x4 GetViewMatrix()
    {
        var eye = CameraPosition;
        var target = _target;

        var forward = Vector3.Normalize(target - eye);
        var worldUp = new Vector3(0f, -1f, 0f);

        var right = Vector3.Normalize(Vector3.Cross(worldUp, forward));
        var up = Vector3.Cross(forward, right);

        if (_roll != 0f)
        {
            var rollMatrix = Matrix4x4.CreateFromAxisAngle(forward, _roll);
            right = Vector3.TransformNormal(right, rollMatrix);
            up = Vector3.TransformNormal(up, rollMatrix);
        }

        var view = new Matrix4x4(
            right.X,   up.X,   forward.X,   0f,
            right.Y,   up.Y,   forward.Y,   0f,
            right.Z,   up.Z,   forward.Z,   0f,
            -Vector3.Dot(right, eye), -Vector3.Dot(up, eye), -Vector3.Dot(forward, eye), 1f
        );

        return view;
    }

    /// <summary>
    /// Projection 行列を計算する。
    /// </summary>
    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        if (_isOrthographic)
        {
            float halfW = _distance * _orthoScale * aspectRatio * 0.5f;
            float halfH = _distance * _orthoScale * 0.5f;
            return CreateOrthographicMatrix(halfW * 2f, halfH * 2f, 1f, 200000f);
        }
        else
        {
            float fovRad = _fovDegrees * MathF.PI / 180f;
            return CreatePerspectiveMatrix(fovRad, aspectRatio, 1f, 200000f);
        }
    }

    // ═══════════════════════════════════════════════════════
    // 内部ヘルパー
    // ═══════════════════════════════════════════════════════

    private Vector3 GetCameraOffset()
    {
        // 球面座標: yaw は XZ 平面の回転、pitch は仰角
        // YMM4 座標系: Z+ = 手前
        // デフォルト (yaw=0, pitch=0): カメラは Z+ 方向にオフセット（手前に配置）
        float x = _distance * MathF.Sin(_yaw) * MathF.Cos(_pitch);
        float y = _distance * MathF.Sin(_pitch);  // Y+ = 下
        float z = _distance * MathF.Cos(_yaw) * MathF.Cos(_pitch);  // Z+ = 手前

        return new Vector3(x, y, z);
    }

    private (Vector3 right, Vector3 up, Vector3 forward) GetCameraAxes()
    {
        var eye = CameraPosition;
        var forward = Vector3.Normalize(_target - eye);
        var worldUp = new Vector3(0f, -1f, 0f);
        var right = Vector3.Normalize(Vector3.Cross(worldUp, forward));
        var up = Vector3.Cross(forward, right);

        if (_roll != 0f)
        {
            var rollMatrix = Matrix4x4.CreateFromAxisAngle(forward, _roll);
            right = Vector3.TransformNormal(right, rollMatrix);
            up = Vector3.TransformNormal(up, rollMatrix);
        }

        return (right, up, forward);
    }

    /// <summary>
    /// 透視投影行列（左手系、Z前方 = 正）を構築。
    /// </summary>
    private static Matrix4x4 CreatePerspectiveMatrix(float fovY, float aspect, float nearZ, float farZ)
    {
        float h = 1f / MathF.Tan(fovY * 0.5f);
        float w = h / aspect;
        float range = farZ / (farZ - nearZ);

        return new Matrix4x4(
            w,  0f, 0f,            0f,
            0f, h,  0f,            0f,
            0f, 0f, range,         1f,
            0f, 0f, -range * nearZ, 0f
        );
    }

    /// <summary>
    /// 平行投影行列を構築。
    /// </summary>
    private static Matrix4x4 CreateOrthographicMatrix(float width, float height, float nearZ, float farZ)
    {
        float range = 1f / (farZ - nearZ);

        return new Matrix4x4(
            2f / width, 0f,          0f,            0f,
            0f,         2f / height, 0f,            0f,
            0f,         0f,          range,         0f,
            0f,         0f,          -nearZ * range, 1f
        );
    }
}
