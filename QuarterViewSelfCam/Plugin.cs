using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.IO;
using ComputerysModdingUtilities;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Mark as vanilla-compatible (read-only, no networked state changes), per the Straftat convention.
[assembly: StraftatMod(isVanillaCompatible: true)]

namespace QuarterViewSelfCam;

[BepInPlugin(Guid, "SelfCam", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "erick.straftat.selfcam";

    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<KeyboardShortcut> ToggleKey;
    internal static ConfigEntry<KeyboardShortcut> PlaceKey;
    internal static ConfigEntry<KeyboardShortcut> LockKey;
    internal static ConfigEntry<KeyboardShortcut> DelayDownKey;
    internal static ConfigEntry<KeyboardShortcut> DelayUpKey;
    internal static ConfigEntry<KeyboardShortcut> ScreenshotKey;
    internal static ConfigEntry<float> Delay;
    internal static ConfigEntry<float> Fov;

    private void Awake()
    {
        Log = Logger;

        Enabled = Config.Bind("General", "Enabled", true, "Master toggle for the self-cam PIP.");
        ToggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.O),
            "Show/hide the PIP (on/off).");
        PlaceKey = Config.Bind("General", "PlaceKey", new KeyboardShortcut(KeyCode.P),
            "Drop the camera at head level; it stays there and keeps looking at your head.");
        LockKey = Config.Bind("General", "LockKey", new KeyboardShortcut(KeyCode.L),
            "Lock/unlock: toggle between tracking your head and holding the current view.");
        DelayDownKey = Config.Bind("General", "DelayDownKey", new KeyboardShortcut(KeyCode.LeftBracket),
            "Decrease replay delay ( [ ).");
        DelayUpKey = Config.Bind("General", "DelayUpKey", new KeyboardShortcut(KeyCode.RightBracket),
            "Increase replay delay ( ] ).");
        ScreenshotKey = Config.Bind("General", "ScreenshotKey", new KeyboardShortcut(KeyCode.K),
            "Save a full-resolution PNG of the self-cam view to your Pictures folder.");
        Delay = Config.Bind("Camera", "Delay", 0.0f,
            new ConfigDescription("Replay delay in seconds: the PIP shows you this many seconds in the past (0 = live). Adjust live with [ and ].", new AcceptableValueRange<float>(0f, 5f)));
        Fov = Config.Bind("Camera", "Fov", 100f,
            new ConfigDescription("Self-cam field of view (deg).", new AcceptableValueRange<float>(20f, 100f)));

        // One persistent host object so the cam survives the per-round player despawn.
        var host = new GameObject("SelfCam");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        host.AddComponent<SelfCam>();

        Log.LogInfo("SelfCam loaded.");
    }
}

/// <summary>
/// A fixed PIP camera: press the place key to drop it at your spot; it stays put in the world and
/// rotates to keep looking at you as you move. Only active in practice contexts.
/// </summary>
public class SelfCam : MonoBehaviour
{
    private const int BodyLayer = 16;     // owner body lives here
    private const int WeaponLayer = 8;    // FP arms + held weapon viewmodel (excluded)
    private const int HudLayer = 25;      // in-world HUD bits (excluded)
    private const int PipWidth = 480;
    private const int PipHeight = 270;
    private const int Margin = 16;
    private const int RingN = 150;            // replay buffer: 5s at 30Hz sampling (~110MB)
    private const float SampleInterval = 1f / 30f;
    private const float HeadHeightFallback = 1.55f; // used only if the FP camera can't be found

    private Camera _cam;
    private RawImage _image;
    private RenderTexture[] _ring;   // replay ring buffer: cam renders into these; we display a delayed one
    private float[] _ringT;          // capture time per committed slot (-1 = empty)
    private int _head;
    private float _lastStamp = -999f;
    private bool _visible = true;
    private bool _failed;
    private int _lastAllowed = -1;

    private object _currentFpc;                 // local player we last set up for (re-init on respawn)
    private PlayerHealth _health;               // owner's health (to detect death)
    private Renderer[] _parts;                  // owner's 3rd-person renderers: suit, head + ALL cosmetics
    private int _scanFrame;                     // periodic re-scan (cosmetics are applied a bit after spawn)
    private int _selfCamLayer = -1;             // a layer the FP camera does NOT draw, but the self-cam does
    private Vector3 _placedPos;                 // fixed world position of the camera
    private bool _placeRequested;
    private bool _shotRequested;
    private bool _locked;                        // true = hold current view; false = track the head
    private GameObject _marker;                 // visible marker at the placed camera position
    private Material _markerMat;
    private Text _delayText;                     // on-screen replay-delay readout

    private void Awake()
    {
        _ring = new RenderTexture[RingN];
        _ringT = new float[RingN];
        for (int i = 0; i < RingN; i++)
        {
            _ring[i] = new RenderTexture(PipWidth, PipHeight, 16);
            _ringT[i] = -1f;
        }

        var camGo = new GameObject("SelfCamCamera");
        camGo.transform.SetParent(transform, false);
        _cam = camGo.AddComponent<Camera>();
        _cam.enabled = false;

        var canvasGo = new GameObject("SelfCamCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000; // draw above the game HUD

        var imgGo = new GameObject("SelfCamImage");
        imgGo.transform.SetParent(canvasGo.transform, false);
        _image = imgGo.AddComponent<RawImage>();
        _image.texture = _ring[0];
        _image.raycastTarget = false;

        // Anchor to bottom-right with a margin.
        var rt = _image.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(PipWidth, PipHeight);
        rt.anchoredPosition = new Vector2(-Margin, Margin);

        // Replay-delay readout, just above the PIP.
        var txtGo = new GameObject("SelfCamDelayText");
        txtGo.transform.SetParent(canvasGo.transform, false);
        _delayText = txtGo.AddComponent<Text>();
        _delayText.font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Liberation Sans", "DejaVu Sans", "sans-serif" }, 16);
        _delayText.fontSize = 16;
        _delayText.fontStyle = FontStyle.Bold;
        _delayText.color = Color.white;
        _delayText.alignment = TextAnchor.LowerRight;
        _delayText.raycastTarget = false;
        _delayText.horizontalOverflow = HorizontalWrapMode.Overflow;
        var trt = _delayText.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1f, 0f);
        trt.sizeDelta = new Vector2(PipWidth, 22);
        trt.anchoredPosition = new Vector2(-Margin, Margin + PipHeight + 2);

        // A small glowing marker so you can see where the camera was dropped.
        _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _marker.name = "SelfCamMarker";
        var col = _marker.GetComponent<Collider>();
        if (col != null) Destroy(col); // no gameplay interference (stays vanilla-compatible)
        _marker.transform.SetParent(transform, false);
        _marker.transform.localScale = Vector3.one * 0.2f;
        var mr = _marker.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        _markerMat = mr.material;
        _markerMat.EnableKeyword("_EMISSION");

        Hide();
    }

    private void Update()
    {
        if (_failed) return;
        if (Plugin.ToggleKey.Value.IsDown()) _visible = !_visible;
        if (Plugin.PlaceKey.Value.IsDown()) _placeRequested = true;
        if (Plugin.LockKey.Value.IsDown()) _locked = !_locked;
        if (Plugin.DelayDownKey.Value.IsDown()) Plugin.Delay.Value = Mathf.Max(0f, Plugin.Delay.Value - 0.25f);
        if (Plugin.DelayUpKey.Value.IsDown()) Plugin.Delay.Value = Mathf.Min(5f, Plugin.Delay.Value + 0.25f);
        if (Plugin.ScreenshotKey.Value.IsDown()) _shotRequested = true;
    }

    private void LateUpdate()
    {
        if (_failed) return;
        try
        {
            bool allowed = PracticeContextAllowed();
            if ((allowed ? 1 : 0) != _lastAllowed)
            {
                _lastAllowed = allowed ? 1 : 0;
                bool offline = false, test = false;
                try { offline = PauseManager.Instance != null && PauseManager.Instance.nonSteamworksTransport; } catch { }
                try { test = SceneMotor.Instance != null && SceneMotor.Instance.testMap; } catch { }
                Plugin.Log.LogInfo($"[SelfCam] gate: allowed={allowed} offline={offline} testMap={test} scene={SceneManager.GetActiveScene().name}");
            }
            if (!Plugin.Enabled.Value || !_visible || !allowed || MenuOpen())
            {
                Hide();
                return;
            }

            var fpc = Settings.Instance != null ? Settings.Instance.localPlayer : null;
            if (fpc == null)
            {
                RestoreBody(); // owner despawned (death / round end): give the body back to the game
                Hide();
                return;
            }

            var main = fpc.playerCamera;
            // Head reference (the FP camera sits at the head); used to place and to aim the self-cam.
            Vector3 head = main != null ? main.transform.position : fpc.transform.position + Vector3.up * HeadHeightFallback;

            // New local player (spawn/respawn): reset, cache health, auto-place at head.
            if (!ReferenceEquals(fpc, _currentFpc))
            {
                _currentFpc = fpc;
                _placedPos = head;
                _parts = null;
                _health = fpc.GetComponent<PlayerHealth>() ?? fpc.GetComponentInParent<PlayerHealth>() ?? fpc.GetComponentInChildren<PlayerHealth>(true);
            }

            // Dead/dying: hand the body back to the game so the death-cam / ragdoll renders normally.
            if (_health != null && _health.health <= 0f)
            {
                RestoreBody();
                Hide();
                return;
            }

            // Collect everything on the 3rd-person character: suit, head, and ANY cosmetic (hat, cig,
            // modded cosmetics...). General by design — we EXCLUDE by category (FP arms/weapon, HUD,
            // collision meshes, VFX) and keep the rest. Re-scan periodically since cosmetics are dressed
            // a moment after spawn (networked).
            if (_parts == null || ++_scanFrame >= 30)
            {
                _scanFrame = 0;
                var list = new System.Collections.Generic.List<Renderer>();
                foreach (var r in fpc.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;          // skip VFX / particles / sprites
                    int L = r.gameObject.layer;
                    if (L == WeaponLayer || L == HudLayer) continue;                          // FP arms/weapon, HUD
                    if (r.name.EndsWith("_Col", System.StringComparison.Ordinal)) continue;   // collision meshes
                    if (r.name.IndexOf("upression", System.StringComparison.Ordinal) >= 0) continue; // suppression overlay (in-game VFX)
                    if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                    list.Add(r);
                }
                _parts = list.ToArray();
            }

            if (_placeRequested)
            {
                _placeRequested = false;
                _placedPos = head;
            }

            if (main != null)
            {
                // Pick a render layer the FP camera does NOT draw, so the body shows only in the self-cam.
                if (_selfCamLayer < 0)
                    for (int i = 31; i >= 8; i--)
                        if (i != BodyLayer && (main.cullingMask & (1 << i)) == 0) { _selfCamLayer = i; break; }

                // Force the owner's body meshes on (the game hides them in FP) and move them to the
                // self-cam-only layer so they appear in the PIP but never in first person.
                if (_parts != null && _selfCamLayer >= 0)
                {
                    Transform root = fpc.transform;
                    foreach (var r in _parts)
                    {
                        if (r == null) continue;
                        r.gameObject.layer = _selfCamLayer;
                        // Activate the whole chain up to the player, so cosmetics whose parent the game
                        // disabled for first-person (hat/cig/...) still show in the self-cam.
                        for (var t = r.transform; t != null && !ReferenceEquals(t, root); t = t.parent)
                            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    }
                }

                _cam.cullingMask = main.cullingMask | (1 << BodyLayer) | (_selfCamLayer >= 0 ? (1 << _selfCamLayer) : 0);
                _cam.clearFlags = main.clearFlags;
                _cam.backgroundColor = main.backgroundColor;
                _cam.nearClipPlane = main.nearClipPlane;
                _cam.farClipPlane = main.farClipPlane;

            }
            _cam.fieldOfView = Plugin.Fov.Value;

            // Fixed position. Track the head unless locked (then hold the current view).
            _cam.transform.position = _placedPos;
            if (!_locked)
            {
                Vector3 dir = head - _placedPos;
                if (dir.sqrMagnitude > 1e-4f)
                    _cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            }

            // Replay: the cam renders into the ring; we display the frame from `Delay` seconds ago.
            float now = Time.time;
            float want = now - Plugin.Delay.Value;
            int best = -1; float bestErr = float.MaxValue;
            for (int i = 0; i < RingN; i++)
            {
                if (i == _head || _ringT[i] < 0f) continue;          // skip the empty + in-progress slots
                float err = Mathf.Abs(_ringT[i] - want);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best >= 0) _image.texture = _ring[best];
            _cam.targetTexture = _ring[_head];                        // this frame renders into the head slot
            if (now - _lastStamp >= SampleInterval)                   // commit at a fixed rate (fps-independent)
            {
                _ringT[_head] = now;
                _lastStamp = now;
                _head = (_head + 1) % RingN;
            }

            if (_shotRequested) { _shotRequested = false; CaptureScreenshot(); }

            Show();
        }
        catch (System.Exception e)
        {
            _failed = true;
            Hide();
            Plugin.Log.LogError("QuarterView SelfCam disabled after error: " + e);
        }
    }

    /// <summary>
    /// Fail-closed practice gate. STRAFTAT gives no reliable way to tell a private custom match from
    /// public matchmaking once you're on a real fighting map — `lobbyType` is forcibly rewritten to
    /// Private mid-match and is host-only (never synced to clients). So we allow ONLY the unambiguous,
    /// client-synced, persistent practice contexts: the tutorial/offline (`nonSteamworksTransport`) and
    /// the exploration/sandbox/test maps (`testMap`). Everything else — any real networked match, menus,
    /// or anything undetermined — is OFF.
    /// </summary>
    private static bool PracticeContextAllowed()
    {
        try
        {
            var pm = PauseManager.Instance;
            if (pm != null && pm.nonSteamworksTransport) return true;   // tutorial / offline / solo
            var sm = SceneMotor.Instance;
            if (sm != null && sm.testMap) return true;                  // exploration / sandbox / test map (synced to all clients)
            return false;                                               // real networked match or undetermined -> OFF
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Put the owner's body meshes back on their normal layer so the death-cam / ragdoll renders them.</summary>
    private void RestoreBody()
    {
        if (_parts == null) return;
        foreach (var r in _parts)
            if (r != null && r.gameObject.layer == _selfCamLayer)
                r.gameObject.layer = BodyLayer;
        _parts = null;
    }

    /// <summary>True when a pause / menu screen is up, so the PIP steps out from over it.</summary>
    private static bool MenuOpen()
    {
        try
        {
            var p = PauseManager.Instance;
            return p != null && (p.pause || p.inVictoryMenu || p.onEndRoundScreen);
        }
        catch { return false; }
    }

    /// <summary>
    /// Save a PNG of exactly what the PIP shows. Live (delay 0): re-render the current pose at full
    /// screen resolution. Delayed: copy the buffered replay frame being displayed (PIP resolution).
    /// </summary>
    private void CaptureScreenshot()
    {
        try
        {
            byte[] png;
            int w, h;

            if (Plugin.Delay.Value <= 0.001f)
            {
                // LIVE — full-resolution re-render of the current pose.
                w = Screen.width; h = Screen.height;
                if (w < 320) w = 1920;
                if (h < 240) h = 1080;

                var shotRT = new RenderTexture(w, h, 24);
                var prevTarget = _cam.targetTexture;
                var prevActive = RenderTexture.active;
                _cam.targetTexture = shotRT;
                _cam.aspect = (float)w / h;
                _cam.Render();
                RenderTexture.active = shotRT;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                _cam.targetTexture = prevTarget;
                _cam.ResetAspect();
                png = tex.EncodeToPNG();
                Destroy(tex);
                shotRT.Release();
                Destroy(shotRT);
            }
            else
            {
                // DELAYED — copy the buffered frame currently shown in the PIP (cannot be re-rendered at full res).
                var src = _image.texture as RenderTexture;
                if (src == null) { Plugin.Log.LogWarning("SelfCam screenshot: no buffered frame yet."); return; }
                w = src.width; h = src.height;
                var prevActive = RenderTexture.active;
                RenderTexture.active = src;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                png = tex.EncodeToPNG();
                Destroy(tex);
            }

            string dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(dir)) dir = Application.persistentDataPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"SelfCam_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
            File.WriteAllBytes(file, png);
            Plugin.Log.LogInfo($"SelfCam screenshot saved: {file} ({w}x{h})");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError("SelfCam screenshot failed: " + e);
        }
    }

    private void Show()
    {
        _cam.enabled = true;
        _image.enabled = true;
        if (_delayText != null)
        {
            _delayText.enabled = true;
            _delayText.text = Plugin.Delay.Value <= 0.001f ? "LIVE" : $"-{Plugin.Delay.Value:0.00}s";
        }
        if (_marker != null)
        {
            _marker.transform.position = _placedPos;
            if (!_marker.activeSelf) _marker.SetActive(true);
            // cyan = tracking your head, red = locked/holding view
            Color c = _locked ? new Color(1f, 0.35f, 0.2f) : new Color(0.2f, 0.9f, 1f);
            _markerMat.color = c;
            _markerMat.SetColor("_EmissionColor", c * 2f);
        }
    }

    private void Hide()
    {
        if (_cam != null) _cam.enabled = false;
        if (_image != null) _image.enabled = false;
        if (_delayText != null) _delayText.enabled = false;
        if (_marker != null && _marker.activeSelf) _marker.SetActive(false);
        if (_ringT != null) { for (int i = 0; i < _ringT.Length; i++) _ringT[i] = -1f; _head = 0; _lastStamp = -999f; }
    }

    private void OnDestroy()
    {
        if (_cam != null) _cam.targetTexture = null;
        if (_ring != null)
            foreach (var rt in _ring)
                if (rt != null) { rt.Release(); Destroy(rt); }
    }
}

