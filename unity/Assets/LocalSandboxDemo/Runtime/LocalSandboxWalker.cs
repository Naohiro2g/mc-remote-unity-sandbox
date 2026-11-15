using UnityEngine;

namespace Code2Create.UnitySandbox.LocalDemo
{
    internal sealed class LocalSandboxWalker : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern int SandboxWsIsPointerLocked();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsRequestPointerLock();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsExitPointerLock();
#endif

        private const float DoubleTapThreshold = 0.35f;
        private const float WalkSpeed = 6f;
        private const float FlyHorizontalSpeed = 8.5f;
        private const float FlyVerticalSpeed = 7.5f;

        private CharacterController body;
        private Vector3 spawn;
        private float pitch;
        private float verticalSpeed;
        private bool isFlying;
        private float lastSpaceDownTime = -10f;
        private float lastShiftDownTime = -10f;
        private float lockGraceUntil = -10f;

        internal Camera View { get; private set; }
        internal bool IsTypingChat { get; set; }
        internal bool IsFlying => isFlying;

        internal void Initialize(Vector3Int origin)
        {
            // transform.positionを足の基準点にする。
            spawn = new Vector3(origin.x, 63.04f, origin.z - 14);
            transform.position = spawn;
            body = gameObject.AddComponent<CharacterController>();
            body.height = 1.8f;
            body.radius = .28f;
            body.center = Vector3.up * .9f;
            body.skinWidth = .02f;
            body.stepOffset = .3f;
            var eye = new GameObject("Demo camera");
            eye.transform.SetParent(transform, false);
            eye.transform.localPosition = Vector3.up * 1.6f;
            View = eye.AddComponent<Camera>();
            View.nearClipPlane = .05f;
            View.farClipPlane = 600;
            View.fieldOfView = 65;
            View.clearFlags = CameraClearFlags.SolidColor;
            View.backgroundColor = new Color(.57f, .76f, .89f);
            ResetPose();
        }

        private void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (IsTypingChat)
            {
                Unlock();
                ApplyGravityOnly();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape)) Unlock();
            if (Input.GetKeyDown(KeyCode.R)) ResetPose();

#if UNITY_WEBGL && !UNITY_EDITOR
            if (Cursor.lockState == CursorLockMode.Locked &&
                Time.unscaledTime > lockGraceUntil &&
                SandboxWsIsPointerLocked() == 0)
            {
                Unlock();
            }
#endif

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                transform.Rotate(0, Input.GetAxisRaw("Mouse X") * 2, 0);
                pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * 2, -85, 85);
                View.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
            }

            var horizontal = Input.GetAxisRaw("Horizontal");
            var forward = Input.GetAxisRaw("Vertical");
            float moveSpeed = isFlying ? FlyHorizontalSpeed : WalkSpeed;
            var movement = transform.TransformDirection(Vector3.ClampMagnitude(new Vector3(horizontal, 0, forward), 1)) * moveSpeed;

            if (Input.GetKeyDown(KeyCode.Space))
            {
                if (!isFlying && Time.unscaledTime - lastSpaceDownTime <= DoubleTapThreshold)
                {
                    isFlying = true;
                    verticalSpeed = 0f;
                    lastSpaceDownTime = -10f;
                    lastShiftDownTime = -10f;
                }
                else
                {
                    lastSpaceDownTime = Time.unscaledTime;
                }
            }

            if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift))
            {
                if (isFlying && Time.unscaledTime - lastShiftDownTime <= DoubleTapThreshold)
                {
                    isFlying = false;
                    verticalSpeed = 0f;
                    lastShiftDownTime = -10f;
                    lastSpaceDownTime = -10f;
                }
                else
                {
                    lastShiftDownTime = Time.unscaledTime;
                }
            }

            if (isFlying)
            {
                float verticalDir = 0f;
                if (Input.GetKey(KeyCode.Space) && transform.position.y < 340f) verticalDir += 1f;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) verticalDir -= 1f;
                verticalSpeed = verticalDir * FlyVerticalSpeed;
            }
            else
            {
                if (body.isGrounded && verticalSpeed < 0)
                {
                    verticalSpeed = -2f;
                    if (Input.GetKeyDown(KeyCode.Space)) verticalSpeed = 6.2f;
                }
                verticalSpeed += Physics.gravity.y * Time.deltaTime;
            }

            movement.y = verticalSpeed;
            body.Move(movement * Time.deltaTime);

            if (transform.position.y < -70 || Mathf.Abs(transform.position.x) > 205 || Mathf.Abs(transform.position.z) > 205)
            {
                ResetPose();
            }
#endif
        }

        internal void LockCursor()
        {
            if (IsTypingChat) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            lockGraceUntil = Time.unscaledTime + 0.35f;
#if UNITY_WEBGL && !UNITY_EDITOR
            SandboxWsRequestPointerLock();
#endif
        }

        internal void UnlockCursor() => Unlock();

        internal void WarpToPlot(int plotIndex)
        {
            var origin = DemoCoordinates.PlotOrigin(plotIndex);
            var targetPos = new Vector3(origin.x, 63.04f, origin.z - 14);
            var lookTarget = new Vector3(origin.x, 64.5f, origin.z);
            WarpTo(targetPos, lookTarget);
        }

        internal void WarpNearCell(Vector3Int cell)
        {
            var lookTarget = DemoCoordinates.Center(cell);
            var targetPos = new Vector3(cell.x, Mathf.Max(63.04f, cell.y + 1.04f), cell.z - 8f);
            targetPos.x = Mathf.Clamp(targetPos.x, -198f, 198f);
            targetPos.z = Mathf.Clamp(targetPos.z, -198f, 198f);
            WarpTo(targetPos, lookTarget);
        }

        private void WarpTo(Vector3 targetPos, Vector3 lookTarget)
        {
            if (body == null) return;
            Vector3 flatDir = lookTarget - targetPos;
            flatDir.y = 0;
            Quaternion yaw = flatDir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(flatDir.normalized, Vector3.up) : Quaternion.identity;

            body.enabled = false;
            transform.SetPositionAndRotation(targetPos, yaw);
            body.enabled = true;
            verticalSpeed = 0;

            Vector3 eyePos = targetPos + Vector3.up * 1.6f;
            Vector3 toTarget = lookTarget - eyePos;
            float flatDist = new Vector2(toTarget.x, toTarget.z).magnitude;
            pitch = Mathf.Clamp(-Mathf.Atan2(toTarget.y, Mathf.Max(0.1f, flatDist)) * Mathf.Rad2Deg, -85f, 85f);
            View.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
        }

        private void ApplyGravityOnly()
        {
            if (body == null) return;
            if (isFlying)
            {
                verticalSpeed = 0f;
                return;
            }
            if (body.isGrounded && verticalSpeed < 0) verticalSpeed = -2f;
            verticalSpeed += Physics.gravity.y * Time.deltaTime;
            body.Move(new Vector3(0, verticalSpeed, 0) * Time.deltaTime);
        }

        internal void ResetPose()
        {
            if (body == null) return;
            isFlying = false;
            body.enabled = false;
            transform.SetPositionAndRotation(spawn, Quaternion.identity);
            body.enabled = true;
            verticalSpeed = 0;
            pitch = 8;
            View.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
        }

        private void OnApplicationFocus(bool focused) { if (!focused) Unlock(); }
        private void OnDisable() => Unlock();
        private static void Unlock()
        {
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
#if UNITY_WEBGL && !UNITY_EDITOR
                SandboxWsExitPointerLock();
#endif
            }
        }
    }
}
