using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace CorticoSisterDrive
{
    /// <summary>
    /// Camera presentation the plugin adds on top of the game's own cameras.
    ///
    /// Glance: while the sister speaks, or while a line the World sent is being heard, the
    /// gameplay camera turns level to the left (driver seat) and eases back afterwards. The turn is applied
    /// only for the duration of that camera's render (beginCameraRendering → endCameraRendering),
    /// so the game's camera scripts keep reading and writing their own rotation unchanged.
    ///
    /// Top view: a plugin-owned camera above the player car, heading up, rendered on demand
    /// into a texture for screenshots; it never draws to the screen.
    /// </summary>
    internal sealed class View
    {
        internal bool GlanceEnabled = true;

        private const float EaseSec = 0.35f;
        private const float GlanceYawDeg = 65f;
        private const float AfterSpeechHoldSec = 0.4f;

        private float _weight;
        private float _holdUntil;
        private Camera _turned;
        private Quaternion _savedRotation;
        private AudioSource _sisterVoice;
        private Camera _topCamera;

        internal View()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        /// <summary>Keeps the glance up for at least <paramref name="seconds"/> from now.</summary>
        internal void HoldGlance(float seconds) => _holdUntil = Mathf.Max(_holdUntil, Time.unscaledTime + seconds);

        internal void Tick()
        {
            bool speaking = SisterSpeaking();
            if (speaking) HoldGlance(AfterSpeechHoldSec);
            float target = GlanceEnabled && Time.unscaledTime < _holdUntil ? 1f : 0f;
            _weight = Mathf.MoveTowards(_weight, target, Time.unscaledDeltaTime / EaseSec);
        }

        private bool SisterSpeaking()
        {
            if (_sisterVoice == null && AiVoiceService.Instance != null)
                _sisterVoice = Traverse.Create(AiVoiceService.Instance).Field<AudioSource>("audioSource").Value;
            return _sisterVoice != null && _sisterVoice.isPlaying;
        }

        /// <summary>The camera drawing the player's view: enabled, on screen, highest depth.</summary>
        internal static Camera GameplayCamera()
        {
            Camera best = null;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture != null || !cam.isActiveAndEnabled) continue;
                if (best == null || cam.depth > best.depth) best = cam;
            }
            return best;
        }

        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_weight <= 0f || cam != GameplayCamera()) return;
            float eased = Mathf.SmoothStep(0f, 1f, _weight);
            _turned = cam;
            _savedRotation = cam.transform.rotation;
            // The sister sits in the driver seat to the left; a level turn keeps the pitch.
            cam.transform.rotation = Quaternion.AngleAxis(-GlanceYawDeg * eased, Vector3.up) * _savedRotation;
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _turned) return;
            cam.transform.rotation = _savedRotation;
            _turned = null;
        }

        /// <summary>Renders the top view into a new texture; null when there is no player car.</summary>
        internal Texture2D RenderTop(int width, float heightMeters, float spanMeters)
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player == null) return null;
            if (_topCamera == null)
            {
                var go = new GameObject("CorticoTopView") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                _topCamera = go.AddComponent<Camera>();
                _topCamera.enabled = false;
                _topCamera.orthographic = true;
                _topCamera.clearFlags = CameraClearFlags.Skybox;
                _topCamera.nearClipPlane = 0.3f;
            }
            int height = width;
            var pos = player.transform.position;
            float yaw = player.transform.eulerAngles.y;
            _topCamera.transform.SetPositionAndRotation(pos + Vector3.up * heightMeters, Quaternion.Euler(90f, yaw, 0f));
            _topCamera.farClipPlane = heightMeters + 50f;
            _topCamera.orthographicSize = spanMeters / 2f;
            var rt = RenderTexture.GetTemporary(width, height, 24);
            _topCamera.targetTexture = rt;
            _topCamera.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            _topCamera.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }
    }
}
