using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class MobileGyroSender : MonoBehaviour
{
    [Header("Ag Ayarlari")]
    [SerializeField] private string targetIp = "192.168.1.100";
    [SerializeField] private int targetPort = 5005;
    [SerializeField] private float sendInterval = 0.02f;

    [Header("Rotasyon Ayarlari")]
    [SerializeField] private Vector3 deviceRotationCorrectionEuler = Vector3.zero;
    [SerializeField] private bool calibrateOnStart = true;
    [SerializeField] private KeyCode editorCalibrateKey = KeyCode.C;

    [Header("Debug")]
    [SerializeField] private bool showDebugOverlay = true;
    [SerializeField] private bool logPackets = false;

    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    private Gyroscope gyro;

    private Quaternion calibrationOffset = Quaternion.identity;
    private Quaternion lastRawRotation = Quaternion.identity;
    private Quaternion lastSentRotation = Quaternion.identity;
    private GyroPacket lastPacket;

    private bool sensorReady;
    private bool networkReady;
    private bool calibrateOnNextSample;
    private float sendTimer;
    private float lastSendRealtime;
    private string statusMessage = "Baslatiliyor...";
    private string lastErrorMessage = string.Empty;

    private void Awake()
    {
        Application.runInBackground = true;
    }

    private void Start()
    {
        lastPacket = new GyroPacket(Quaternion.identity, 0L);
        calibrateOnNextSample = calibrateOnStart;

        InitializeNetwork();
        InitializeGyroscope();
    }

    private void Update()
    {
        if (Input.GetKeyDown(editorCalibrateKey))
        {
            Calibrate();
        }

        if (!sensorReady || !networkReady)
        {
            return;
        }

        sendTimer += Time.unscaledDeltaTime;

        while (sendTimer >= sendInterval)
        {
            sendTimer -= sendInterval;
            SendCurrentOrientation();
        }
    }

    private void OnDisable()
    {
        Shutdown();
    }

    private void OnApplicationQuit()
    {
        Shutdown();
    }

    public void Calibrate()
    {
        if (!sensorReady)
        {
            statusMessage = "Kalibrasyon icin sensor hazir degil.";
            Debug.LogWarning("[MobileGyroSender] Kalibrasyon atlandi. Sensor hazir degil.");
            return;
        }

        calibrateOnNextSample = true;
        statusMessage = "Bir sonraki ornekte kalibre edilecek.";
    }

    private void InitializeNetwork()
    {
        try
        {
            IPAddress ipAddress;

            if (!IPAddress.TryParse(targetIp, out ipAddress))
            {
                IPAddress[] addresses = Dns.GetHostAddresses(targetIp);
                if (addresses == null || addresses.Length == 0)
                {
                    throw new InvalidOperationException("Hedef IP cozumlenemedi.");
                }

                ipAddress = addresses[0];
            }

            remoteEndPoint = new IPEndPoint(ipAddress, targetPort);
            udpClient = new UdpClient();
            networkReady = true;
            statusMessage = "UDP hazir.";
        }
        catch (Exception exception)
        {
            networkReady = false;
            lastErrorMessage = exception.Message;
            statusMessage = "UDP baslatilamadi.";
            Debug.LogError($"[MobileGyroSender] UDP baslatma hatasi: {exception}");
        }
    }

    private void InitializeGyroscope()
    {
        if (!SystemInfo.supportsGyroscope)
        {
            sensorReady = false;
            statusMessage = "Bu cihaz jiroskop desteklemiyor.";
            Debug.LogError("[MobileGyroSender] Bu cihaz jiroskop desteklemiyor.");
            return;
        }

        gyro = Input.gyro;
        gyro.enabled = true;
        sensorReady = gyro.enabled;

        if (!sensorReady)
        {
            statusMessage = "Jiroskop etkinlestirilemedi.";
            Debug.LogError("[MobileGyroSender] Jiroskop etkinlestirilemedi.");
            return;
        }

        statusMessage = networkReady ? "Hazir, veri gonderiliyor." : "Sensor hazir, ag bekleniyor.";
    }

    private void SendCurrentOrientation()
    {
        try
        {
            Quaternion correctedRotation = GetCorrectedDeviceRotation();

            if (calibrateOnNextSample)
            {
                // En pratik mobil kalibrasyon: kullanicinin o an tuttugu pozisyonu kimlik rotasyonu kabul ediyoruz.
                // Boylece telefondan cikan veri, "nötr tutus" referansina gore sifirlanmis oluyor.
                calibrationOffset = Quaternion.Inverse(correctedRotation);
                calibrateOnNextSample = false;
                statusMessage = "Kalibrasyon guncellendi.";
            }

            lastRawRotation = correctedRotation;
            lastSentRotation = NormalizeQuaternion(calibrationOffset * correctedRotation);
            lastPacket = new GyroPacket(lastSentRotation, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string json = JsonUtility.ToJson(lastPacket);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            udpClient.Send(payload, payload.Length, remoteEndPoint);

            lastSendRealtime = Time.realtimeSinceStartup;
            statusMessage = "Veri gonderiliyor.";
            lastErrorMessage = string.Empty;

            if (logPackets)
            {
                Debug.Log($"[MobileGyroSender] Gonderildi: {json}");
            }
        }
        catch (Exception exception)
        {
            statusMessage = "Gonderim hatasi.";
            lastErrorMessage = exception.Message;
            Debug.LogWarning($"[MobileGyroSender] UDP gonderim hatasi: {exception.Message}");
        }
    }

    private Quaternion GetCorrectedDeviceRotation()
    {
        Quaternion attitude = gyro.attitude;

        // Mobil cihaz attitude verisi sag elli koordinatta gelir.
        // Unity'nin sol elli koordinatina cevirmek icin Z ve W terslenir.
        Quaternion unityRotation = new Quaternion(attitude.x, attitude.y, -attitude.z, -attitude.w);
        Quaternion correction = Quaternion.Euler(deviceRotationCorrectionEuler);

        return NormalizeQuaternion(unityRotation * correction);
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

    private void Shutdown()
    {
        if (udpClient == null)
        {
            return;
        }

        try
        {
            udpClient.Close();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[MobileGyroSender] UDP kapatma hatasi: {exception.Message}");
        }
        finally
        {
            udpClient = null;
            networkReady = false;
        }
    }

    private void OnGUI()
    {
        if (!showDebugOverlay)
        {
            return;
        }

        GUILayout.BeginArea(new Rect(16f, 16f, Mathf.Min(Screen.width - 32f, 540f), 260f), GUI.skin.box);
        GUILayout.Label("MobileGyroSender");
        GUILayout.Label($"Hedef: {targetIp}:{targetPort}");
        GUILayout.Label($"Sensor: {(sensorReady ? "Hazir" : "Hazir Degil")}");
        GUILayout.Label($"Durum: {statusMessage}");
        GUILayout.Label($"Son gonderim: {(lastSendRealtime > 0f ? lastSendRealtime.ToString("F2") + "s" : "-")}");
        GUILayout.Label($"Son quaternion: {FormatQuaternion(lastSentRotation)}");

        if (!string.IsNullOrEmpty(lastErrorMessage))
        {
            GUILayout.Label($"Hata: {lastErrorMessage}");
        }

        if (GUILayout.Button("Calibrate", GUILayout.Height(40f)))
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
