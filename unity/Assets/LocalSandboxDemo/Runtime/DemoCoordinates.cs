using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("Code2Create.UnitySandbox.LocalDemo.Editor")]

namespace Code2Create.UnitySandbox.LocalDemo
{
    // 右手系のMinecraft座標（+X東, +Y上, +Z南 / -Z北）と左手系のUnity座標（Z反転）の変換と描画試作用の計算。
    internal static class DemoCoordinates
    {
        internal static Vector3 McToUnityPos(Vector3 mcPos) => new Vector3(mcPos.x, mcPos.y, -mcPos.z);
        internal static Vector3 UnityToMcPos(Vector3 unityPos) => new Vector3(unityPos.x, unityPos.y, -unityPos.z);
        internal static Vector3 McToUnityDir(Vector3 mcDir) => new Vector3(mcDir.x, mcDir.y, -mcDir.z);
        internal static Vector3 UnityToMcDir(Vector3 unityDir) => new Vector3(unityDir.x, unityDir.y, -unityDir.z);

        internal static Vector3Int Cell(Vector3 unityPoint) => Vector3Int.FloorToInt(UnityToMcPos(unityPoint));

        internal static Vector3 Center(Vector3Int mcCell) =>
            new Vector3(mcCell.x + 0.5f, mcCell.y + 0.5f, -(mcCell.z + 0.5f));

        internal static void BoxCenterAndSize(
            Vector3Int mcMin,
            Vector3Int mcMax,
            out Vector3 unityCenter,
            out Vector3 size)
        {
            size = new Vector3(mcMax.x - mcMin.x + 1f, mcMax.y - mcMin.y + 1f, mcMax.z - mcMin.z + 1f);
            unityCenter = new Vector3(
                (mcMin.x + mcMax.x + 1) * 0.5f,
                (mcMin.y + mcMax.y + 1) * 0.5f,
                -(mcMin.z + mcMax.z + 1) * 0.5f);
        }

        internal static Vector3Int HitCell(RaycastHit hit) => Cell(hit.point - hit.normal * 0.001f);
        internal static Vector3Int Relative(Vector3Int cell, Vector3Int origin) => cell - origin;

        internal static float UnityEulerYToMcYaw(float unityEulerY)
        {
            float yaw = (unityEulerY - 180f) % 360f;
            if (yaw >= 180f) yaw -= 360f;
            else if (yaw < -180f) yaw += 360f;
            return yaw == 0f ? 0f : yaw;
        }

        internal static float McYawToUnityEulerY(float mcYaw)
        {
            float eulerY = (mcYaw + 180f) % 360f;
            if (eulerY < 0f) eulerY += 360f;
            return eulerY;
        }

        internal static float CompassAzimuthDegrees(Vector3 unityForward)
        {
            Vector3 mcFwd = UnityToMcDir(unityForward);
            return (Mathf.Atan2(mcFwd.x, -mcFwd.z) * Mathf.Rad2Deg + 360f) % 360f;
        }

        internal static Vector3Int PlotOrigin(int index)
        {
            if (index < 0 || index > 8) throw new System.ArgumentOutOfRangeException(nameof(index));
            return new Vector3Int((index % 3 - 1) * 100, 0, (index / 3 - 1) * 100);
        }
    }
}
