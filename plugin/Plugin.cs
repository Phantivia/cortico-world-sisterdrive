using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CorticoSisterDrive
{
    /// <summary>
    /// In-game end of the Cortico sisterdrive World. Frames (JSON lines, see docs/protocol.md):
    /// requests from the World carry <c>id</c> and get one <c>reply</c>; game facts go out as
    /// <c>event</c> frames whenever the hooked game method runs.
    /// </summary>
    [BepInPlugin("ai.cortico.sisterdrive", "Cortico SisterDrive", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.1.0";
        public const int Protocol = 1;

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        private ConfigEntry<int> _port;
        private Bridge _bridge;
        private Harmony _harmony;
        private View _view;

        /// <summary>True while SendAudio2LLM runs for audio the World supplied.</summary>
        internal static bool Injecting;
        internal static string LastInputSource;
        internal static string LastContext;
        internal static long LastSayId;
        /// <summary>
        /// Voice inputs handed to the game and not yet answered, oldest first: (source, say id).
        /// The router answers them in order; a turn takes the oldest entry.
        /// </summary>
        internal static readonly System.Collections.Generic.Queue<KeyValuePair<string, long>> Unanswered = new System.Collections.Generic.Queue<KeyValuePair<string, long>>();

        private JObject _pendingTurn;
        private string _briefedPrompt;
        private float _nextPromptCheck;
        private float _pendingTurnAt;
        // The reply line follows CommitAndReturn within the same async continuation; this window
        // only bounds how long a turn without a spoken line waits before going out alone.
        private const float ReplyLineWindowSec = 0.5f;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            // The build pauses its player loop while unfocused; the bridge and the game must keep
            // running while the operator works in other windows. Update re-asserts it.
            Application.runInBackground = true;
            _port = Config.Bind("Bridge", "Port", 47631, "127.0.0.1 TCP port the Cortico World connects to.");
            _bridge = new Bridge(_port.Value);
            _harmony = new Harmony("ai.cortico.sisterdrive");
            _harmony.PatchAll(typeof(Plugin).Assembly);
            int gated = TalkKey.Install(_harmony);
            _view = new View();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo($"[sisterdrive] listening on 127.0.0.1:{_port.Value}; talk-key gates patched: {gated}");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _bridge?.Dispose();
        }

        private void Update()
        {
            // LoadingSceneManager turns it off when its loading jobs finish.
            if (!Application.runInBackground) Application.runInBackground = true;
            _view.Tick();
            if (Time.unscaledTime >= _nextPromptCheck)
            {
                _nextPromptCheck = Time.unscaledTime + 1f;
                var prompt = ModelRouter.Instance != null ? Traverse.Create(ModelRouter.Instance).Field<string>("_activePromptText").Value : null;
                // The router reloads the same prompt as a new string on reconnects; compare by value.
                if (prompt != null && !string.Equals(prompt, _briefedPrompt, StringComparison.Ordinal))
                {
                    _briefedPrompt = prompt;
                    var brief = Brief();
                    if (brief != null) Event("brief", new JObject { ["text"] = brief });
                }
            }
            while (_bridge.Inbox.TryDequeue(out var frame))
            {
                try { Handle(frame); }
                catch (Exception err)
                {
                    Log.LogError("[sisterdrive] request failed: " + err);
                    if (frame["id"] != null) Reply(frame, false, err.GetType().Name + ": " + err.Message);
                }
            }
            if (_pendingTurn != null && Time.realtimeSinceStartup - _pendingTurnAt > ReplyLineWindowSec)
            {
                Emit(_pendingTurn);
                _pendingTurn = null;
            }
        }

        // ---- outbound -------------------------------------------------------------------

        internal static void Event(string type, JObject body)
        {
            if (Instance == null) return;
            body["kind"] = "event";
            body["type"] = type;
            body["scene"] = SceneManager.GetActiveScene().name;
            body["atMs"] = (long)(Time.realtimeSinceStartup * 1000);
            Instance._bridge.Send(body);
        }

        private void Emit(JObject turn) => Event("turn", turn);

        private void Reply(JObject req, bool ok, string error = null, JObject body = null)
        {
            var frame = body ?? new JObject();
            frame["kind"] = "reply";
            frame["id"] = req["id"];
            frame["ok"] = ok;
            if (error != null) frame["error"] = error;
            _bridge.Send(frame);
        }

        internal void OnTurnCommitted(LlmTurnResult result)
        {
            if (_pendingTurn != null) Emit(_pendingTurn);
            var input = Unanswered.Count > 0 ? Unanswered.Dequeue() : new KeyValuePair<string, long>(LastInputSource, LastSayId);
            _pendingTurn = new JObject
            {
                ["source"] = input.Key,
                ["sayId"] = input.Key == "world" ? (JToken)input.Value : null,
                ["heard"] = result?.UserTranscript,
                ["reply"] = result?.Text,
                ["latencySec"] = result?.LatencySeconds ?? 0f,
                ["context"] = LastContext,
            };
            _pendingTurnAt = Time.realtimeSinceStartup;
        }

        /// <summary>Returns true when the line was folded into the pending turn.</summary>
        internal bool OnReplyLine(string text)
        {
            if (_pendingTurn == null) return false;
            _pendingTurn["line"] = text;
            Emit(_pendingTurn);
            _pendingTurn = null;
            return true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;
            _pendingTurn = null;
            Unanswered.Clear(); // inputs from the previous scene get no turn here
            Event("scene", new JObject { ["state"] = State() });
        }

        // ---- inbound --------------------------------------------------------------------

        private void Handle(JObject frame)
        {
            switch ((string)frame["kind"])
            {
                case "_connected":
                    _briefedPrompt = null; // the new client gets the current brief within a second
                    _bridge.Send(new JObject
                    {
                        ["kind"] = "hello",
                        ["protocol"] = Protocol,
                        ["plugin"] = Version,
                        ["game"] = Application.version,
                        ["state"] = State(),
                    });
                    break;
                case "config":
                    _view.GlanceEnabled = (bool?)frame["glance"] ?? true;
                    _view.MinimapEnabled = (bool?)frame["minimap"] ?? true;
                    _view.MinimapSpanMeters = (float?)frame["minimapSpanM"] ?? 60f;
                    Reply(frame, true);
                    break;
                case "say": Say(frame); break;
                case "look": StartCoroutine(Look(frame)); break;
                case "stage": Stage(frame); break;
                default: Reply(frame, false, "unknown kind " + (string)frame["kind"]); break;
            }
        }

        private void Say(JObject req)
        {
            if (GameStatus.Instance != null && GameStatus.Instance.IsPaused()) { Reply(req, false, "paused"); return; }
            // Releasing the talk key happens whether or not the game takes the audio, as with the real key.
            TalkKey.Release();
            var manager = AIManager.Instance;
            if (manager == null) { Reply(req, false, "noVoiceInput"); return; }
            if (!manager.InputAvailable) { Reply(req, false, "inputDisabled"); return; }
            var wav = Convert.FromBase64String((string)req["wav"]);
            var inFlight = ModelRouter.Instance != null && Traverse.Create(ModelRouter.Instance).Field<bool>("_playInFlight").Value;
            LastSayId = (long)req["id"];
            Injecting = true;
            try { manager.SendAudio2LLM(wav); }
            finally { Injecting = false; }
            _view.HoldGlance((float?)req["glanceSec"] ?? 0f);
            Reply(req, true, null, new JObject { ["previousTurnInFlight"] = inFlight });
        }

        private IEnumerator Look(JObject req)
        {
            int maxWidth = (int?)req["maxWidth"] ?? 960;
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            Texture2D scaled = shot;
            if (shot.width > maxWidth)
            {
                int height = shot.height * maxWidth / shot.width;
                var rt = RenderTexture.GetTemporary(maxWidth, height);
                Graphics.Blit(shot, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                scaled = new Texture2D(maxWidth, height, TextureFormat.RGB24, false);
                scaled.ReadPixels(new Rect(0, 0, maxWidth, height), 0, 0);
                scaled.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Destroy(shot);
            }
            var jpeg = scaled.EncodeToJPG(75);
            var body = new JObject
            {
                ["jpeg"] = Convert.ToBase64String(jpeg),
                ["width"] = scaled.width,
                ["height"] = scaled.height,
                ["state"] = State(),
            };
            Destroy(scaled);
            if ((bool?)req["top"] == true)
            {
                var top = _view.RenderTop((int?)req["topWidth"] ?? 512, (float?)req["topHeightM"] ?? 60f, (float?)req["topSpanM"] ?? 60f);
                if (top != null)
                {
                    body["topJpeg"] = Convert.ToBase64String(top.EncodeToJPG(75));
                    body["topSpanM"] = (float?)req["topSpanM"] ?? 60f;
                    Destroy(top);
                }
            }
            Reply(req, true, null, body);
        }

        private void Stage(JObject req)
        {
            string action = (string)req["action"];
            switch (action)
            {
                case "restart":
                    if (GameStatus.Instance == null) { Reply(req, false, "notInStage"); return; }
                    GameStatus.Instance.RestartGame();
                    break;
                case "next":
                {
                    var loader = UnityEngine.Object.FindFirstObjectByType<SceneLoader>();
                    if (loader == null || GameStatus.Instance == null) { Reply(req, false, "notInStage"); return; }
                    loader.LoadNextScene();
                    break;
                }
                case "menu":
                {
                    var loader = UnityEngine.Object.FindFirstObjectByType<SceneLoader>();
                    if (loader == null) { Reply(req, false, "noSceneLoader"); return; }
                    loader.LoadMainMenuScene();
                    break;
                }
                case "continue":
                {
                    var menu = UnityEngine.Object.FindFirstObjectByType<MainMenu>();
                    if (menu == null) { Reply(req, false, "notInMainMenu"); return; }
                    menu.StartContinue();
                    break;
                }
                case "newgame":
                {
                    // Operator-only (the World tool does not offer it): New Game without the confirm panel, from the tutorial.
                    var menu = UnityEngine.Object.FindFirstObjectByType<MainMenu>();
                    if (menu == null) { Reply(req, false, "notInMainMenu"); return; }
                    menu.StartGame();
                    break;
                }
                default: Reply(req, false, "unknown action " + action); return;
            }
            Reply(req, true);
        }

        // ---- state ----------------------------------------------------------------------

        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// The parking target (TriggerZone) relative to the car, with the quantities its completion
        /// check uses: fully inside, rear-in heading within allowedAngleError, stay time.
        /// </summary>
        private static JObject Goal(Transform car)
        {
            foreach (var zone in UnityEngine.Object.FindObjectsByType<TriggerZone>(FindObjectsSortMode.None))
            {
                var t = Traverse.Create(zone);
                if (t.Field<bool>("isCompleted").Value) continue;
                var local = car.InverseTransformPoint(zone.transform.position);
                var goal = new JObject
                {
                    ["distanceM"] = Math.Round(new Vector2(local.x, local.z).magnitude, 1),
                    ["bearingDeg"] = Math.Round(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg),
                    ["inside"] = t.Field<bool>("isFullyInside").Value,
                    ["stayedSec"] = Math.Round(t.Field<float>("currentStayTime").Value, 1),
                    ["requiredStaySec"] = t.Field<float>("requiredStayTime").Value,
                };
                var reference = t.Field<Transform>("parkingDirectionReference").Value;
                if (reference != null)
                {
                    goal["rearIn"] = true;
                    goal["headingErrorDeg"] = Math.Round(180f - Vector3.Angle(car.forward, reference.forward));
                    goal["allowedErrorDeg"] = t.Field<float>("allowedAngleError").Value;
                }
                return goal;
            }
            return null;
        }

        /// <summary>
        /// The parts of the sister model's active system prompt that state the stage goal and the
        /// action scale (#Goal, ##target_speed, ##steering_angle, ##duration), verbatim.
        /// </summary>
        internal static string Brief()
        {
            if (ModelRouter.Instance == null) return null;
            var prompt = Traverse.Create(ModelRouter.Instance).Field<string>("_activePromptText").Value;
            if (string.IsNullOrEmpty(prompt)) return null;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var header in new[] { "#Goal", "##target_speed", "##steering_angle", "##duration", "##horizontal_action", "##vertical_action" })
            {
                int at = prompt.IndexOf(header + "\n", StringComparison.Ordinal);
                if (at < 0) at = prompt.IndexOf(header, StringComparison.Ordinal);
                if (at < 0) continue;
                int end = prompt.IndexOf("\n#", at + header.Length, StringComparison.Ordinal);
                parts.Add((end < 0 ? prompt.Substring(at) : prompt.Substring(at, end - at)).Trim());
            }
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

        internal static JObject State()
        {
            var state = new JObject { ["scene"] = SceneManager.GetActiveScene().name };
            var manager = AIManager.Instance;
            state["voiceInput"] = manager != null && manager.InputAvailable;
            state["paused"] = GameStatus.Instance != null && GameStatus.Instance.IsPaused();
            state["speedUnit"] = PlayerPrefs.GetInt("IsMileUnit", 0) == 1 ? "mph" : "km/h";
            var player = GameObject.FindGameObjectWithTag("Player");
            var rb = player != null ? player.GetComponent<Rigidbody>() : null;
            // Same formula as the speedometer (VehicleStatusUI).
            if (rb != null) state["speed"] = Mathf.RoundToInt(rb.linearVelocity.magnitude * 4f);
            if (player != null) { var goal = Goal(player.transform); if (goal != null) state["goal"] = goal; }
            if (EmotionStatus.Instance != null) state["emotion"] = EmotionStatus.Instance.GetEmotion().ToString();
            if (ScoreStatus.Instance != null)
            {
                state["fine"] = ScoreStatus.Instance.GetFine();
                state["damageCount"] = ScoreStatus.Instance.GetDamageCount();
            }
            if (manager != null)
            {
                var getContext = manager.GetType().GetMethod("GetContextInfo", AnyInstance, null, Type.EmptyTypes, null);
                if (getContext != null)
                {
                    try { state["context"] = (string)getContext.Invoke(manager, null); }
                    catch (Exception err) { state["contextError"] = (err.InnerException ?? err).Message; }
                }
            }
            return state;
        }
    }
}
