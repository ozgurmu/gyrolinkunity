using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class DesktopGyroReceiver : MonoBehaviour
{
    [Header("Ag Ayarlari")]
    [SerializeField] private int listenPort = 5005;
    [SerializeField] private bool autoStartOnEnable = true;
    [SerializeField] private float connectionTimeout = 2f;

    [Header("Hedef")]
    [SerializeField] private Transform targetTransform;
    [SerializeField] private bool applyToLocalRotation = false;
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Kalibrasyon")]
    [SerializeField] private bool calibrateOnFirstPacket = true;
    [SerializeField] private KeyCode calibrateKey = KeyCode.C;

    [Header("Smoothing")]
    [SerializeField] private bool enableSmoothing = true;
    [SerializeField] private float smoothingSpeed = 12f;

    [Header("Debug")]
    [SerializeField] private bool showDebugOverlay = true;
    [SerializeField] private bool logPackets = false;

    private readonly object packetLock = new object();

    private UdpClient udpClient;
    private Thread receiveThread;

    private volatile bool isRunning;
    private bool hasPendingPacket;
    private bool hasValidPacket;
    private bool pendingAutoCalibration;

    private GyroPacket latestPacket = new GyroPacket(Quaternion.identity, 0L);
    private Quaternion latestReceivedRotation = Quaternion.identity;
    private Quaternion lastNetworkRotation = Quaternion.identity;
    private Quaternion calibrationOffset = Quaternion.identity;
    private Quaternion desiredRotation = Quaternion.identity;

    private string latestSender = "-";
    private string receiverStatus = "Dinleyici kapali.";
    private string pendingThreadError = string.Empty;
    private float lastReceiveRealtime = -1f;

    private void Awake()
    {
        Application.runInBackground = true;
    }

    private void Start()
    {
        if (targetTransform != null)
        {
            desiredRotation = GetCurrentTargetRotation();
        }

        pendingAutoCalibration = calibrateOnFirstPacket;
    }

    private void OnEnable()
    {
        if (autoStartOnEnable)
        {
            StartListening();
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(calibrateKey))
        {
            Calibrate();
        }

        GyroPacket packet = null;
        Quaternion networkRotation = Quaternion.identity;
        string sender = null;
        string threadError = null;
        bool receivedPacket = false;

        lock (packetLock)
        {
            if (hasPendingPacket)
            {
                packet = latestPacket;
                networkRotation = latestReceivedRotation;
                sender = latestSender;
                hasPendingPacket = false;
                receivedPacket = true;
            }

            if (!string.IsNullOrEmpty(pendingThreadError))
            {
                threadError = pendingThreadError;
                pendingThreadError = string.Empty;
            }
        }

        if (!string.IsNullOrEmpty(threadError))
        {
            Debug.LogWarning($"[DesktopGyroReceiver] Arka plan alici hatasi: {threadError}");
        }

        if (receivedPacket)
        {
            hasValidPacket = true;
            lastNetworkRotation = networkRotation;
            lastReceiveRealtime = Time.realtimeSinceStartup;
            receiverStatus = $"Veri aliniyor ({sender})";

            if (pendingAutoCalibration)
            {
                ApplyCalibration(lastNetworkRotation);
                pendingAutoCalibration = false;
            }

            desiredRotation = ComposeTargetRotation(lastNetworkRotation);

            if (logPackets && packet != null)
            {
                Debug.Log($"[DesktopGyroReceiver] Alindi: {JsonUtility.ToJson(packet)}");
            }
        }

        if (targetTransform != null && hasValidPacket)
        {
            Quaternion nextRotation = desiredRotation;

            if (enableSmoothing)
            {
                float lerpFactor = 1f - Mathf.Exp(-Mathf.Max(0.01f, smoothingSpeed) * Time.unscaledDeltaTime);
                nextRotation = Quaternion.Slerp(GetCurrentTargetRotation(), desiredRotation, lerpFactor);
            }

            SetTargetRotation(nextRotation);
        }

        if (isRunning && lastReceiveRealtime > 0f && Time.realtimeSinceStartup - lastReceiveRealtime > connectionTimeout)
        {
            receiverStatus = "Veri bekleniyor...";
        }
    }

    private void OnDisable()
    {
        StopListening();
    }

    private void OnApplicationQuit()
    {
        StopListening();
    }

    public void StartListening()
    {
        if (isRunning)
        {
            return;
        }

        try
        {
            udpClient = new UdpClient(listenPort);
            udpClient.Client.ReceiveTimeout = 500;

            isRunning = true;
            receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "DesktopGyroReceiverThread"
            };
            receiveThread.Start();

            receiverStatus = $"Port {listenPort} dinleniyor.";
        }
        catch (Exception exception)
        {
            receiverStatus = "Dinleyici baslatilamadi.";
            Debug.LogError($"[DesktopGyroReceiver] UDP dinleyici baslatma hatasi: {exception}");
        }
    }

    public void StopListening()
    {
        isRunning = false;

        try
        {
            udpClient?.Close();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[DesktopGyroReceiver] UDP kapatma hatasi: {exception.Message}");
        }
        finally
        {
            udpClient = null;
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(500);
        }

        receiveThread = null;
        receiverStatus = "Dinleyici kapali.";
    }

    public void Calibrate()
    {
        if (!hasValidPacket)
        {
            receiverStatus = "Kalibrasyon icin once veri alinmali.";
            Debug.LogWarning("[DesktopGyroReceiver] Kalibrasyon atlandi. Henuz gecerli paket yok.");
            return;
        }

        ApplyCalibration(lastNetworkRotation);
        desiredRotation = ComposeTargetRotation(lastNetworkRotation);
        receiverStatus = "Kalibrasyon guncellendi.";
    }

    private void ApplyCalibration(Quaternion networkRotation)
    {
        Quaternion currentTargetRotation = targetTransform != null ? GetCurrentTargetRotation() : Quaternion.identity;
        Quaternion modelCorrection = Quaternion.Euler(rotationOffsetEuler);

        // En pratik masaustu kalibrasyonu: o an gelen telefon rotasyonunu,
        // sahnedeki objenin mevcut rotasyonuna esliyoruz. Boylece sahne referansi korunuyor.
        calibrationOffset = NormalizeQuaternion(currentTargetRotation * Quaternion.Inverse(networkRotation * modelCorrection));
    }

    private Quaternion ComposeTargetRotation(Quaternion networkRotation)
    {
        Quaternion modelCorrection = Quaternion.Euler(rotationOffsetEuler);
        return NormalizeQuaternion(calibrationOffset * networkRotation * modelCorrection);
    }

    private Quaternion GetCurrentTargetRotation()
    {
        return applyToLocalRotation ? targetTransform.localRotation : targetTransform.rotation;
    }

    private void SetTargetRotation(Quaternion rotation)
    {
        if (applyToLocalRotation)
        {
            targetTransform.localRotation = rotation;
        }
        else
        {
            targetTransform.rotation = rotation;
        }
    }

    private void ReceiveLoop()
    {
        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remoteEndPoint);
                string json = Encoding.UTF8.GetString(data);

                GyroPacket packet = JsonUtility.FromJson<GyroPacket>(json);
                if (packet == null)
                {
                    throw new InvalidOperationException("JSON bos veya gecersiz.");
                }

                Quaternion parsedRotation = CreateSafeQuaternion(packet);

                lock (packetLock)
                {
                    latestPacket = packet;
                    latestReceivedRotation = parsedRotation;
                    latestSender = remoteEndPoint.ToString();
                    hasPendingPacket = true;
                }
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.TimedOut && isRunning)
                {
                    SetPendingThreadError(exception.Message);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception)
            {
                SetPendingThreadError(exception.Message);
            }
        }
    }

    private void SetPendingThreadError(string message)
    {
        lock (packetLock)
        {
            pendingThreadError = message;
        }
    }

    private static Quaternion CreateSafeQuaternion(GyroPacket packet)
    {
        if (!IsFinite(packet.x) || !IsFinite(packet.y) || !IsFinite(packet.z) || !IsFinite(packet.w))
        {
            throw new InvalidOperationException("Quaternion sayisal olarak gecersiz.");
        }

        Quaternion rotation = new Quaternion(packet.x, packet.y, packet.z, packet.w);
        return NormalizeQuaternion(rotation);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static Quaternion NormalizeQuaternion(Quaternion rotation)
    {
        float magnitude = Mathf.Sqrt(
            rotation.x * rotation.x +
            rotation.y * rotation.y +
            rotation.z * rotation.z +
            rotation.w * rotation.w);

        if (magnitude < 0.0001f)
        {
            return Quaternion.identity;
        }

        return new Quaternion(
            rotation.x / magnitude,
            rotation.y / magnitude,
            rotation.z / magnitude,
            rotation.w / magnitude);
    }

    private void OnGUI()
    {
        if (!showDebugOverlay)
        {
            return;
        }

        string smoothingLabel = enableSmoothing ? $"Acik ({smoothingSpeed:F1})" : "Kapali";

        GUILayout.BeginArea(new Rect(16f, 16f, 520f, 250f), GUI.skin.box);
        GUILayout.Label("DesktopGyroReceiver");
        GUILayout.Label($"Durum: {receiverStatus}");
        GUILayout.Label($"Port: {listenPort}");
        GUILayout.Label($"Gonderen: {latestSender}");
        GUILayout.Label($"Son quaternion: {FormatQuaternion(lastNetworkRotation)}");
        GUILayout.Label($"Son timestamp: {(latestPacket != null ? latestPacket.timestamp.ToString() : "-")}");
        GUILayout.Label($"Smoothing: {smoothingLabel}");

        if (GUILayout.Button("Calibrate", GUILayout.Height(32f)))
        {
            Calibrate();
        }

        GUILayout.EndArea();
    }

    private static string FormatQuaternion(Quaternion rotation)
    {
        return $"x:{rotation.x:F4} y:{rotation.y:F4} z:{rotation.z:F4} w:{rotation.w:F4}";
    }
}
