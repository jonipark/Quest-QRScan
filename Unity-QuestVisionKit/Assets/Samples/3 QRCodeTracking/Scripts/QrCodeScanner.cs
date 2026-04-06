using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Rendering;
using UnityEngine;
using Meta.XR;
using System;
#if ZXING_ENABLED
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.Multi;
#endif

public enum QrCodeDetectionMode
{
    Single,
    Multiple
}

[Serializable]
public class QrCodeResult
{
    public string text;
    public Vector3[] corners;
    public Pose cameraPose;
    public PassthroughCameraAccess.CameraIntrinsics Intrinsics;
    public Vector2Int captureResolution;
}

public class QrCodeScanner : MonoBehaviour
{
#if ZXING_ENABLED
    [SerializeField] private int sampleFactor = 2;
    [SerializeField] private QrCodeDetectionMode detectionMode = QrCodeDetectionMode.Single;

    private PassthroughCameraAccess _cameraAccess;
    private RenderTexture _downsampledTexture;
    private ComputeShader _downsampleShader;
    private QRCodeReader _qrReader;
    private bool _isScanning;
    
    private static readonly int Input1 = Shader.PropertyToID("_Input");
    private static readonly int Output = Shader.PropertyToID("_Output");
    private static readonly int InputWidth = Shader.PropertyToID("_InputWidth");
    private static readonly int InputHeight = Shader.PropertyToID("_InputHeight");
    private static readonly int OutputWidth = Shader.PropertyToID("_OutputWidth");
    private static readonly int OutputHeight = Shader.PropertyToID("_OutputHeight");

    private struct CaptureFrame
    {
        public Texture Texture;
        public Pose Pose;
        public PassthroughCameraAccess.CameraIntrinsics Intrinsics;
        public Vector2Int Resolution;
    }

    private void Awake()
    {
        _cameraAccess = GetComponent<PassthroughCameraAccess>();
        Debug.Log($"[QR DEBUG] PassthroughCameraAccess found: {_cameraAccess != null}");

        _downsampleShader = Resources.Load<ComputeShader>($"Downsample");
        if (_downsampleShader == null)
        {
            Debug.LogError("Downsample.compute not found in a Resources folder.");
        }
        else
        {
            Debug.Log("[QR DEBUG] Downsample shader loaded OK");
        }

        _qrReader = new QRCodeReader();
        Debug.Log("[QR DEBUG] QRCodeReader created OK");
    }

    private void OnDestroy()
    {
        if (_downsampledTexture == null) return;
        _downsampledTexture.Release();
        Destroy(_downsampledTexture);
    }

    public async Task<QrCodeResult[]> ScanFrameAsync()
    {
        if (_isScanning || !_downsampleShader)
        {
            if (!_downsampleShader) Debug.LogWarning("[QR DEBUG] ScanFrameAsync skipped: downsample shader is null");
            return Array.Empty<QrCodeResult>();
        }

        _isScanning = true;
        try
        {
            Debug.Log("[QR DEBUG] Acquiring frame...");
            var frame = await AcquireFrameAsync();
            if (frame == null)
            {
                Debug.LogWarning("[QR DEBUG] Frame acquisition returned null");
                return Array.Empty<QrCodeResult>();
            }
            Debug.Log($"[QR DEBUG] Frame acquired: {frame.Value.Texture.width}x{frame.Value.Texture.height}");

            var (targetWidth, targetHeight) = GetTargetDimensions(frame.Value.Texture);
            if (!EnsureDownsampleTarget(targetWidth, targetHeight))
            {
                Debug.LogWarning("[QR DEBUG] EnsureDownsampleTarget failed");
                return Array.Empty<QrCodeResult>();
            }

            Debug.Log($"[QR DEBUG] Dispatching downsample to {targetWidth}x{targetHeight}");
            DispatchDownsample(frame.Value.Texture, targetWidth, targetHeight);
            var grayBytes = await ReadPixelsAsync(_downsampledTexture);
            if (grayBytes == null || grayBytes.Length == 0)
            {
                Debug.LogWarning("[QR DEBUG] GPU readback returned empty data");
                return Array.Empty<QrCodeResult>();
            }
            Debug.Log($"[QR DEBUG] Got {grayBytes.Length} gray bytes, decoding...");

            var decoded = await Task.Run(() => DecodeFrame(frame.Value, grayBytes, targetWidth, targetHeight));
            Debug.Log($"[QR DEBUG] Decode result: {(decoded != null ? decoded.Length : 0)} QR code(s) found");
            return decoded ?? Array.Empty<QrCodeResult>();
        }
        finally
        {
            _isScanning = false;
        }
    }

    private QrCodeResult ProcessDecodeResult(Result decodeResult, int targetWidth, int targetHeight, CaptureFrame frame)
    {
        var points = decodeResult.ResultPoints;
        var uvCorners = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            uvCorners[i] = new Vector3(points[i].X / targetWidth, points[i].Y / targetHeight, 0);
        }

        return new QrCodeResult
        {
            text = decodeResult.Text,
            corners = uvCorners,
            cameraPose = frame.Pose,
            Intrinsics = frame.Intrinsics,
            captureResolution = frame.Resolution
        };
    }

    private Task<byte[]> ReadPixelsAsync(RenderTexture rt)
    {
        var tcs = new TaskCompletionSource<byte[]>();

        AsyncGPUReadback.Request(rt, 0, TextureFormat.R8, request =>
        {
            if (request.hasError)
            {
                tcs.SetException(new Exception("GPU readback error."));
            }
            else
            {
                tcs.SetResult(request.GetData<byte>().ToArray());
            }
        });
        return tcs.Task;
    }

    private int _acquireAttempts;
    private async Task<CaptureFrame?> AcquireFrameAsync()
    {
        _acquireAttempts = 0;
        while (true)
        {
            _acquireAttempts++;
            if (_acquireAttempts % 60 == 1)
            {
                Debug.Log($"[QR DEBUG] AcquireFrame attempt {_acquireAttempts}: " +
                          $"cameraAccess={_cameraAccess != null}, " +
                          $"IsPlaying={(_cameraAccess ? _cameraAccess.IsPlaying : false)}");
            }

            if (_cameraAccess && _cameraAccess.IsPlaying)
            {
                var texture = _cameraAccess.GetTexture();
                if (texture)
                {
                    Debug.Log($"[QR DEBUG] Frame texture obtained after {_acquireAttempts} attempts");
                    return new CaptureFrame
                    {
                        Texture = texture,
                        Pose = _cameraAccess.GetCameraPose(),
                        Intrinsics = _cameraAccess.Intrinsics,
                        Resolution = _cameraAccess.CurrentResolution
                    };
                }
                else if (_acquireAttempts % 60 == 1)
                {
                    Debug.LogWarning("[QR DEBUG] IsPlaying=true but GetTexture() returned null");
                }
            }
            await Task.Delay(16);
        }
    }

    private (int width, int height) GetTargetDimensions(Texture texture)
    {
        var divisor = Mathf.Max(1, sampleFactor);
        return (Mathf.Max(1, texture.width / divisor), Mathf.Max(1, texture.height / divisor));
    }

    private bool EnsureDownsampleTarget(int width, int height)
    {
        if (_downsampledTexture && _downsampledTexture.width == width && _downsampledTexture.height == height)
        {
            return true;
        }

        if (_downsampledTexture)
        {
            _downsampledTexture.Release();
        }

        _downsampledTexture = new RenderTexture(width, height, 0, RenderTextureFormat.R8)
        {
            enableRandomWrite = true
        };
        _downsampledTexture.Create();
        return true;
    }

    private void DispatchDownsample(Texture source, int targetWidth, int targetHeight)
    {
        var kernel = _downsampleShader.FindKernel("CSMain");
        _downsampleShader.SetTexture(kernel, Input1, source);
        _downsampleShader.SetTexture(kernel, Output, _downsampledTexture);
        _downsampleShader.SetInt(InputWidth, source.width);
        _downsampleShader.SetInt(InputHeight, source.height);
        _downsampleShader.SetInt(OutputWidth, targetWidth);
        _downsampleShader.SetInt(OutputHeight, targetHeight);

        var threadGroupsX = Mathf.CeilToInt(targetWidth / 8f);
        var threadGroupsY = Mathf.CeilToInt(targetHeight / 8f);
        _downsampleShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
    }

    private QrCodeResult[] DecodeFrame(CaptureFrame frame, byte[] grayBytes, int targetWidth, int targetHeight)
    {
        try
        {
            var luminanceSource = new RGBLuminanceSource(grayBytes, targetWidth, targetHeight, RGBLuminanceSource.BitmapFormat.Gray8);
            var binaryBitmap = new BinaryBitmap(new HybridBinarizer(luminanceSource));

            if (detectionMode == QrCodeDetectionMode.Single)
            {
                var decodeResult = _qrReader.decode(binaryBitmap);
                if (decodeResult != null)
                {
                    return new[] { ProcessDecodeResult(decodeResult, targetWidth, targetHeight, frame) };
                }
            }
            else
            {
                var multiReader = new GenericMultipleBarcodeReader(_qrReader);
                var decodeResults = multiReader.decodeMultiple(binaryBitmap);
                if (decodeResults != null)
                {
                    var results = new List<QrCodeResult>(decodeResults.Length);
                    foreach (var decodeResult in decodeResults)
                    {
                        results.Add(ProcessDecodeResult(decodeResult, targetWidth, targetHeight, frame));
                    }
                    return results.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[QRCodeScanner] Error decoding QR code(s): {ex.Message}");
        }

        return Array.Empty<QrCodeResult>();
    }
#endif
}
