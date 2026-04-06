using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

public class QrCodeDisplayManager : MonoBehaviour
{
#if ZXING_ENABLED
    [Serializable]
    public class QrPrefabMapping
    {
        [Tooltip("The QR code text value to match (e.g. \"wine_01\").")]
        public string qrValue;
        [Tooltip("The prefab to spawn when this QR code is detected.")]
        public GameObject prefab;
    }

    private QrCodeScanner _scanner;
    private EnvironmentRaycastManager _envRaycastManager;
    private readonly Dictionary<string, MarkerController> _activeMarkers = new();
    private readonly Dictionary<string, GameObject> _activeModels = new();
    private readonly HashSet<string> _playingQrCodes = new();
    private readonly Dictionary<string, float> _cooldownQrCodes = new();
    private Dictionary<string, GameObject> _prefabLookup;
    private bool _isScanning;

    private enum QrRaycastMode
    {
        CenterOnly,
        PerCorner
    }

    [SerializeField] private QrRaycastMode raycastMode = QrRaycastMode.PerCorner;

    [Header("QR → Prefab Mapping")]
    [Tooltip("Map QR code text values to prefabs. Configure in Inspector.")]
    [SerializeField] private List<QrPrefabMapping> qrPrefabMappings = new();
    [Tooltip("Fallback prefab when no mapping matches. Leave empty to use default marker.")]
    [SerializeField] private GameObject fallbackPrefab;

    [Header("3D Model Display")]
    [Tooltip("Offset from the QR code center (local space).")]
    [SerializeField] private Vector3 modelOffset = new Vector3(0, 0.1f, 0);
    [Tooltip("Scale of the spawned model.")]
    [SerializeField] private Vector3 modelScale = new Vector3(0.1f, 0.1f, 0.1f);

    private void Awake()
    {
        _scanner = GetComponent<QrCodeScanner>();
        _envRaycastManager = GetComponent<EnvironmentRaycastManager>();

        // Build fast lookup from the serialized list
        _prefabLookup = new Dictionary<string, GameObject>();
        foreach (var mapping in qrPrefabMappings)
        {
            if (string.IsNullOrEmpty(mapping.qrValue) || !mapping.prefab)
            {
                Debug.LogWarning($"[QR] Skipping invalid mapping: qrValue='{mapping.qrValue}', prefab={mapping.prefab}");
                continue;
            }
            if (!_prefabLookup.TryAdd(mapping.qrValue, mapping.prefab))
                Debug.LogWarning($"[QR] Duplicate mapping for '{mapping.qrValue}' — keeping first");
        }
        Debug.Log($"[QR] Loaded {_prefabLookup.Count} QR→prefab mapping(s)");
    }

    private GameObject GetPrefabForQr(string qrText)
    {
        if (_prefabLookup.TryGetValue(qrText, out var prefab))
            return prefab;
        return fallbackPrefab;
    }

    private void Update() => RefreshMarkers();

    private async void RefreshMarkers()
    {
        // Prevent concurrent async scans — this was causing race conditions
        // where multiple in-flight ScanFrameAsync calls could resolve close together
        // and duplicate-instantiate the model before _playingQrCodes was set.
        if (_isScanning)
            return;

        if (!_envRaycastManager || !_scanner)
            return;

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

        foreach (var qrResult in qrResults)
        {
            if (!TryBuildMarkerPose(qrResult, out var pose, out var scale))
                continue;

            var prefab = GetPrefabForQr(qrResult.text);
            if (prefab)
            {
                // Skip if animation is currently playing — do NOT re-instantiate or reset
                if (_playingQrCodes.Contains(qrResult.text))
                    continue;
                if (_cooldownQrCodes.TryGetValue(qrResult.text, out var cooldownEnd) && Time.time < cooldownEnd)
                    continue;
                _cooldownQrCodes.Remove(qrResult.text);
                UpdateOrCreateModel(qrResult.text, pose, prefab);
            }
            else
            {
                var marker = GetOrCreateMarker(qrResult.text);
                if (marker)
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

    private void UpdateOrCreateModel(string key, Pose pose, GameObject prefab)
    {
        if (_activeModels.TryGetValue(key, out var model) && model)
        {
            return;
        }

        Debug.Log($"[QR] Spawning '{prefab.name}' for QR '{key}' (frame={Time.frameCount})");
        model = Instantiate(prefab);
        model.transform.localScale = modelScale;
        var spawnPos = pose.position + pose.rotation * modelOffset;
        model.transform.SetPositionAndRotation(spawnPos, pose.rotation);

        // Remove MarkerController if present — it auto-deactivates after 2s
        var markerController = model.GetComponent<MarkerController>();
        if (markerController)
            Destroy(markerController);

        model.SetActive(true);
        _activeModels[key] = model;
        _playingQrCodes.Add(key);

        // Attach mood particles based on the QR key
        AttachParticles(key, model.transform);

        var animator = model.GetComponentInChildren<Animator>();
        if (animator)
        {
            animator.speed = 1f;
            animator.Play("Scene", 0, 0f);
            animator.Update(0f);
            StartCoroutine(PlayAnimationOnceAndDestroy(key, model, animator));
        }
        else
        {
            Debug.LogWarning($"[QR] No Animator on '{key}', auto-destroy in 5s");
            StartCoroutine(DestroyAfterDelay(key, model, 5f));
        }
    }

    private static void AttachParticles(string qrKey, Transform parent)
    {
        // Guard: don't duplicate if particles already exist on this instance
        string childName = qrKey switch
        {
            "wine_01" => "Wine01Particles",
            "wine_02" => "Wine02Particles",
            _ => null,
        };

        if (childName == null)
            return;

        if (parent.Find(childName))
            return; // already attached

        if (qrKey == "wine_01")
            WineParticleFactory.CreateWine01Particles(parent);
        else
            WineParticleFactory.CreateWine02Particles(parent);

        Debug.Log($"[QR] Attached '{childName}' to model for '{qrKey}'");
    }

    private IEnumerator PlayAnimationOnceAndDestroy(string key, GameObject model, Animator animator)
    {
        var runtimeController = animator.runtimeAnimatorController;
        float clipLength = 0f;

        if (runtimeController != null && runtimeController.animationClips.Length > 0)
            clipLength = runtimeController.animationClips[0].length;

        if (clipLength <= 0f)
        {
            Debug.LogWarning($"[QR] No animation clips for '{key}', destroying in 5s");
            yield return new WaitForSeconds(5f);
        }
        else
        {
            Debug.Log($"[QR] Animation playing for '{key}' ({clipLength:F2}s)");
            var startTime = Time.time;
            while (Time.time - startTime < clipLength)
            {
                if (!model || !animator)
                    yield break;
                yield return null;
            }
        }

        Debug.Log($"[QR] Animation done, destroying model for '{key}'");
        if (model) Destroy(model);
        _activeModels.Remove(key);
        _playingQrCodes.Remove(key);
        _cooldownQrCodes[key] = Time.time + 3f;
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

        var modelKeysToRemove = new List<string>();
        foreach (var kvp in _activeModels)
        {
            if (!kvp.Value)
                modelKeysToRemove.Add(kvp.Key);
        }
        foreach (var key in modelKeysToRemove)
        {
            _activeModels.Remove(key);
        }
    }
#endif
}
