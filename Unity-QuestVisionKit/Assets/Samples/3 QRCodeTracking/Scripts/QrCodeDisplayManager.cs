using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

public class QrCodeDisplayManager : MonoBehaviour
{
#if ZXING_ENABLED
    private QrCodeScanner _scanner;
    private EnvironmentRaycastManager _envRaycastManager;
    private readonly Dictionary<string, MarkerController> _activeMarkers = new();
    private readonly Dictionary<string, GameObject> _activeModels = new();
    private readonly HashSet<string> _playingQrCodes = new();
    private readonly Dictionary<string, float> _cooldownQrCodes = new();
    private bool _isScanning;

    private enum QrRaycastMode
    {
        CenterOnly,
        PerCorner
    }

    [SerializeField] private QrRaycastMode raycastMode = QrRaycastMode.PerCorner;

    [Header("3D Model Display")]
    [Tooltip("Optional 3D model prefab to spawn at the QR code location. If null, uses the default marker.")]
    [SerializeField] private GameObject modelPrefab;
    [Tooltip("Offset from the QR code center (local space).")]
    [SerializeField] private Vector3 modelOffset = new Vector3(0, 0.1f, 0);
    [Tooltip("Scale of the spawned model.")]
    [SerializeField] private Vector3 modelScale = new Vector3(0.1f, 0.1f, 0.1f);

    private void Awake()
    {
        _scanner = GetComponent<QrCodeScanner>();
        _envRaycastManager = GetComponent<EnvironmentRaycastManager>();
    }

    private void Update() => RefreshMarkers();

    private int _refreshCount;
    private async void RefreshMarkers()
    {
        // Prevent concurrent async scans — this was causing race conditions
        // where multiple in-flight ScanFrameAsync calls could resolve close together
        // and duplicate-instantiate the model before _playingQrCodes was set.
        if (_isScanning)
            return;

        if (!_envRaycastManager || !_scanner)
        {
            if (_refreshCount++ % 300 == 0)
                Debug.LogWarning($"[QR DEBUG] RefreshMarkers skipped: envRaycastManager={_envRaycastManager != null}, scanner={_scanner != null}");
            return;
        }

        _isScanning = true;
        QrCodeResult[] qrResults;
        try
        {
            qrResults = await _scanner.ScanFrameAsync();
        }
        finally
        {
            _isScanning = false;
        }

        if (qrResults == null || qrResults.Length == 0)
        {
            CleanupInactiveMarkers();
            return;
        }

        Debug.Log($"[QR DEBUG] Got {qrResults.Length} QR result(s) this frame (frame={Time.frameCount})");
        foreach (var qrResult in qrResults)
        {
            if (!TryBuildMarkerPose(qrResult, out var pose, out var scale))
            {
                Debug.LogWarning($"[QR DEBUG] TryBuildMarkerPose FAILED for '{qrResult.text}' (corners={qrResult.corners?.Length}, center raycast miss?)");
                continue;
            }

            if (modelPrefab)
            {
                // Skip if animation is currently playing — do NOT re-instantiate or reset
                if (_playingQrCodes.Contains(qrResult.text))
                {
                    Debug.Log($"[QR DEBUG] Skipping '{qrResult.text}' — animation already playing (frame={Time.frameCount})");
                    continue;
                }
                if (_cooldownQrCodes.TryGetValue(qrResult.text, out var cooldownEnd) && Time.time < cooldownEnd)
                {
                    Debug.Log($"[QR DEBUG] Skipping '{qrResult.text}' — in cooldown until {cooldownEnd:F2} (now={Time.time:F2})");
                    continue;
                }
                _cooldownQrCodes.Remove(qrResult.text);
                UpdateOrCreateModel(qrResult.text, pose);
            }
            else
            {
                var marker = GetOrCreateMarker(qrResult.text);
                if (!marker)
                {
                    Debug.LogWarning($"[QR DEBUG] GetOrCreateMarker returned null for '{qrResult.text}' (MarkerPool empty?)");
                    continue;
                }
                Debug.Log($"[QR DEBUG] Updating marker '{qrResult.text}' at {pose.position}");
                marker.UpdateMarker(pose.position, pose.rotation, scale, qrResult.text);
            }
        }

        CleanupInactiveMarkers();
    }

    private static Vector2 ToViewport(Vector2 uv) => new(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));

    private static Ray BuildWorldRay(QrCodeResult result, Vector2 uv)
    {
        var viewport = ToViewport(uv);
        var intrinsics = result.Intrinsics;
        var sensorResolution = (Vector2)intrinsics.SensorResolution;
        var currentResolution = (Vector2)result.captureResolution;
        if (currentResolution == Vector2.zero)
        {
            currentResolution = sensorResolution;
        }

        var crop = ComputeSensorCrop(sensorResolution, currentResolution);
        var sensorPoint = new Vector2(
            crop.x + crop.width * viewport.x,
            crop.y + crop.height * viewport.y);

        var localDirection = new Vector3(
            (sensorPoint.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x,
            (sensorPoint.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y,
            1f).normalized;

        var worldDirection = result.cameraPose.rotation * localDirection;
        return new Ray(result.cameraPose.position, worldDirection);
    }

    private static Rect ComputeSensorCrop(Vector2 sensorResolution, Vector2 currentResolution)
    {
        if (sensorResolution == Vector2.zero)
        {
            return new Rect(0, 0, currentResolution.x, currentResolution.y);
        }

        var scaleFactor = new Vector2(
            currentResolution.x / sensorResolution.x,
            currentResolution.y / sensorResolution.y);
        var maxScale = Mathf.Max(scaleFactor.x, scaleFactor.y);
        if (maxScale <= 0)
        {
            maxScale = 1f;
        }
        scaleFactor /= maxScale;

        return new Rect(
            sensorResolution.x * (1f - scaleFactor.x) * 0.5f,
            sensorResolution.y * (1f - scaleFactor.y) * 0.5f,
            sensorResolution.x * scaleFactor.x,
            sensorResolution.y * scaleFactor.y);
    }

    private static Vector3 ProjectOntoPlane(Plane plane, Ray ray, float fallbackDistance)
    {
        return plane.Raycast(ray, out var planeDistance)
            ? ray.GetPoint(planeDistance)
            : ray.GetPoint(fallbackDistance);
    }

    private bool TryBuildMarkerPose(QrCodeResult result, out Pose pose, out Vector3 scale)
    {
        pose = default;
        scale = default;

        if (result?.corners == null || result.corners.Length < 3)
        {
            return false;
        }

        // ZXing returns 3 finder pattern points for QR codes.
        // Estimate the 4th corner: p3 = p0 + (p1 - p0) + (p2 - p0) (parallelogram)
        var uvs = new Vector2[4];
        for (var i = 0; i < result.corners.Length && i < 4; i++)
        {
            uvs[i] = new Vector2(result.corners[i].x, result.corners[i].y);
        }
        if (result.corners.Length == 3)
        {
            uvs[3] = uvs[0] + (uvs[1] - uvs[0]) + (uvs[2] - uvs[0]);
        }

        var centerUV = (uvs[0] + uvs[1] + uvs[2] + uvs[3]) * 0.25f;

        var centerRay = BuildWorldRay(result, centerUV);
        if (!_envRaycastManager.Raycast(centerRay, out var centerHit))
        {
            return false;
        }

        var distance = Vector3.Distance(centerRay.origin, centerHit.point);
        var plane = new Plane(centerHit.normal, centerHit.point);
        var worldCorners = new Vector3[4];

        for (var i = 0; i < 4; i++)
        {
            var ray = BuildWorldRay(result, uvs[i]);
            if (raycastMode == QrRaycastMode.PerCorner && _envRaycastManager.Raycast(ray, out var cornerHit))
            {
                worldCorners[i] = cornerHit.point;
            }
            else
            {
                worldCorners[i] = ProjectOntoPlane(plane, ray, distance);
            }
        }

        var center = (worldCorners[0] + worldCorners[1] + worldCorners[2] + worldCorners[3]) * 0.25f;

        var up = (worldCorners[1] - worldCorners[0]).normalized;
        var right = (worldCorners[2] - worldCorners[1]).normalized;
        var normal = -Vector3.Cross(up, right).normalized;
        var rotation = Quaternion.LookRotation(normal, up);

        var width = Vector3.Distance(worldCorners[0], worldCorners[1]);
        var height = Vector3.Distance(worldCorners[0], worldCorners[3]);
        var scaleFactor = 1.5f;
        scale = new Vector3(width * scaleFactor, height * scaleFactor, 1f);
        pose = new Pose(center, rotation);
        return true;
    }

    private void UpdateOrCreateModel(string key, Pose pose)
    {
        if (_activeModels.TryGetValue(key, out var model) && model)
        {
            // Model already exists — do NOT update position/rotation every frame.
            // Per-frame SetPositionAndRotation on the model root fights with the
            // Animator when the clip has root-level keyframes, causing the animation
            // to appear frozen or to reset each frame.
            Debug.Log($"[QR DEBUG] Model already exists for '{key}', skipping position update (frame={Time.frameCount})");
            return;
        }

        // Spawn the full prefab (includes all child objects)
        Debug.Log($"[QR DEBUG] === INSTANTIATING model for '{key}' at frame={Time.frameCount}, time={Time.time:F2} ===");
        model = Instantiate(modelPrefab);
        model.transform.localScale = modelScale;
        var spawnPos = pose.position + pose.rotation * modelOffset;
        model.transform.SetPositionAndRotation(spawnPos, pose.rotation);

        // The 1_Final prefab has a MarkerController that auto-deactivates the
        // GameObject after 2 seconds (designed for 2D marker overlays, not 3D models).
        // Destroy it so it doesn't kill the model mid-animation.
        var markerController = model.GetComponent<MarkerController>();
        if (markerController)
        {
            Debug.Log($"[QR DEBUG] Removing MarkerController from model '{key}' (would auto-deactivate after 2s)");
            Destroy(markerController);
        }

        model.SetActive(true);
        Debug.Log($"[QR DEBUG] Model SetActive(true) for '{key}' at pos={spawnPos}, rot={pose.rotation.eulerAngles}");
        _activeModels[key] = model;
        _playingQrCodes.Add(key);

        // Play animation once, then destroy
        var animator = model.GetComponentInChildren<Animator>();
        if (animator)
        {
            animator.speed = 1f;
            // Explicitly start the "Scene" state from normalizedTime=0.
            // Without this, the Animator may not evaluate on the first frame after
            // Instantiate+SetActive during an async callback mid-Update, leaving
            // children at their prefab default scale (0,0,0).
            animator.Play("Scene", 0, 0f);
            animator.Update(0f);
            Debug.Log($"[QR DEBUG] Animator found on '{key}': controller={animator.runtimeAnimatorController?.name}, speed={animator.speed}, forced Play('Scene',0,0)");
            StartCoroutine(PlayAnimationOnceAndDestroy(key, model, animator));
        }
        else
        {
            Debug.LogWarning($"[QR DEBUG] No Animator found on model for '{key}', will auto-destroy in 5s");
            StartCoroutine(DestroyAfterDelay(key, model, 5f));
        }
    }

    private IEnumerator PlayAnimationOnceAndDestroy(string key, GameObject model, Animator animator)
    {
        var runtimeController = animator.runtimeAnimatorController;
        float clipLength = 0f;
        string clipName = null;

        if (runtimeController != null && runtimeController.animationClips.Length > 0)
        {
            var clip = runtimeController.animationClips[0];
            clipLength = clip.length;
            clipName = clip.name;
            Debug.Log($"[QR DEBUG] Controller '{runtimeController.name}' has {runtimeController.animationClips.Length} clip(s). Using '{clipName}' length={clipLength:F2}s");
        }

        if (clipLength <= 0f)
        {
            Debug.LogWarning($"[QR DEBUG] No animation clips found for '{key}', destroying in 5s");
            yield return new WaitForSeconds(5f);
        }
        else
        {
            Debug.Log($"[QR DEBUG] >>> Animation START '{clipName}' ({clipLength:F2}s) for '{key}' at time={Time.time:F2}, frame={Time.frameCount}");

            // Let the default Animator state play — do NOT call animator.Play() or Rebind()
            // which would restart it. Just wait for the full duration.
            var startTime = Time.time;
            var elapsed = 0f;
            while (elapsed < clipLength)
            {
                // Safety: if the model was destroyed externally, bail out
                if (!model || !animator)
                {
                    Debug.LogWarning($"[QR DEBUG] !!! Model or Animator destroyed externally for '{key}' at elapsed={elapsed:F2}s");
                    yield break;
                }

                // Heartbeat every 2 seconds so we can verify the coroutine is alive
                var newElapsed = Time.time - startTime;
                if (Mathf.FloorToInt(newElapsed / 2f) > Mathf.FloorToInt(elapsed / 2f))
                {
                    var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                    Debug.Log($"[QR DEBUG] ... heartbeat '{key}': elapsed={newElapsed:F2}/{clipLength:F2}s, " +
                              $"animatorEnabled={animator.enabled}, modelActive={model.activeSelf}, " +
                              $"stateHash={stateInfo.shortNameHash}, normalizedTime={stateInfo.normalizedTime:F3}");
                }
                elapsed = newElapsed;
                yield return null;
            }

            Debug.Log($"[QR DEBUG] >>> Animation END '{clipName}' for '{key}' at time={Time.time:F2}, totalElapsed={elapsed:F2}s");
        }

        Debug.Log($"[QR DEBUG] Destroying model for '{key}' at time={Time.time:F2}");
        if (model) Destroy(model);
        _activeModels.Remove(key);
        _playingQrCodes.Remove(key);
        _cooldownQrCodes[key] = Time.time + 3f;
        Debug.Log($"[QR DEBUG] Cooldown set for '{key}' until {Time.time + 3f:F2}");
    }

    private IEnumerator DestroyAfterDelay(string key, GameObject model, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (model) Destroy(model);
        _activeModels.Remove(key);
        _playingQrCodes.Remove(key);
        _cooldownQrCodes[key] = Time.time + 3f;
    }

    private MarkerController GetOrCreateMarker(string key)
    {
        if (_activeMarkers.TryGetValue(key, out var marker))
        {
            return marker;
        }

        var markerGo = MarkerPool.Instance ? MarkerPool.Instance.GetMarker() : null;
        if (!markerGo)
        {
            return null;
        }

        marker = markerGo.GetComponent<MarkerController>();
        if (!marker)
        {
            return null;
        }

        _activeMarkers[key] = marker;
        return marker;
    }

    private void CleanupInactiveMarkers()
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in _activeMarkers)
        {
            if (!kvp.Value || !kvp.Value.gameObject.activeSelf)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _activeMarkers.Remove(key);
        }

        // Cleanup destroyed model references (but do NOT remove models that are still playing)
        var modelKeysToRemove = new List<string>();
        foreach (var kvp in _activeModels)
        {
            if (!kvp.Value)
            {
                Debug.LogWarning($"[QR DEBUG] CleanupInactiveMarkers: model for '{kvp.Key}' is null/destroyed — removing from _activeModels (playing={_playingQrCodes.Contains(kvp.Key)})");
                modelKeysToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in modelKeysToRemove)
        {
            _activeModels.Remove(key);
        }
    }
#endif
}
