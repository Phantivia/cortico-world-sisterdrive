using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CorticoSisterDrive
{
    /// <summary>
    /// Conditional plans: the sister model may answer with
    /// <c>plan: [{speed: 10, steer: 0, until: distance 15}, {steer: 0.6, until: turned 80}, ...]</c>.
    /// Each step sets speed and steering on the scene's car controller and holds until its
    /// condition is met in game; then the next step starts. A new instruction that carries a plan
    /// or the game's own actions cancels the running plan, as a new utterance does in the game.
    /// </summary>
    internal static class Plan
    {
        internal sealed class Step
        {
            public float? Speed;
            public float? Steer;
            public string Until = "none";
            public float Value;
            public string Source;
        }

        /// <summary>Appended to every driving-stage prompt (numeric and lever formats).</summary>
        internal const string PromptSection = @"

#计划动作（哥哥一次交代好几步时使用）
- 哥哥常常一次说一串要按位置或时机去做的事，例如「往前开，开十米左右往右打，车头摆正就回正」「倒车两米再停」。这时不要输出原来的动作项，改为输出 plan：一步一个动作，每步写清做到什么时候进入下一步。只有一个立刻要做的动作时，照原来的格式输出即可。
- 格式：plan: [{speed: 速度|none, steer: 方向|none, until: 条件}, ...]
  - speed：目标速度，单位 km/h。1~30 前进，0 刹车，-1~-15 倒车，none 保持当前速度。
  - steer：方向盘比例，-1~1。负数向左，正数向右，0 回正，none 保持当前方向。
  - until：这一步做到什么时候。只能用以下几种：seconds N（N 秒后）、distance N（这一步开出 N 米后）、turned N（这一步车头转过 N 度后）、stopped（车停稳后）、goal N（离目标车位不到 N 米后）、none（一直保持，等哥哥下一句）。
- 最后一步通常是 until: none。
- 用 plan 时，妹妹的回话和转写这两项仍按原格式输出。
- 例：哥哥说「往前慢慢开，开十米左右往右打，车头摆正就回正」→ plan: [{speed: 10, steer: 0, until: distance 10}, {speed: none, steer: 0.6, until: turned 80}, {speed: none, steer: 0, until: none}]
- 例：哥哥说「倒车两米再停」→ plan: [{speed: -5, steer: 0, until: distance 2}, {speed: 0, steer: none, until: none}]";

        private static readonly Regex PlanRx = new Regex(@"plan\s*:\s*\[(.*)\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex StepRx = new Regex(@"\{([^}]*)\}", RegexOptions.Singleline);
        private static readonly Regex SpeedRx = new Regex(@"speed\s*:\s*(-?[\d.]+|none)", RegexOptions.IgnoreCase);
        private static readonly Regex SteerRx = new Regex(@"steer\s*:\s*(-?[\d.]+|none)", RegexOptions.IgnoreCase);
        private static readonly Regex UntilRx = new Regex(@"until\s*:\s*(seconds|distance|turned|stopped|goal|none)\s*(-?[\d.]+)?", RegexOptions.IgnoreCase);
        private static readonly Regex LegacyNumericRx = new Regex(@"actions\s*:\s*\[[^\]]*\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex LegacyLeverRx = new Regex(@"(horizontal_action|vertical_action)\s*:\s*\w*", RegexOptions.IgnoreCase);
        private static readonly Regex LegacyActionPresentRx = new Regex(@"target_speed\s*:\s*-?\d|steering_angle\s*:\s*-?\d|(horizontal_action|vertical_action)\s*:\s*(left|right|center|go_forward|brake|go_back)", RegexOptions.IgnoreCase);

        private static Coroutine _running;
        private static int _planId;

        /// <summary>
        /// Called with the model reply before the game parses it. A plan is taken out of the text
        /// together with any legacy action items, so the game parses only the spoken reply.
        /// </summary>
        internal static void OnReply(LlmTurnResult result, long sayId)
        {
            if (result?.Text == null) return;
            var match = PlanRx.Match(result.Text);
            if (!match.Success)
            {
                if (LegacyActionPresentRx.IsMatch(result.Text)) Cancel("replaced");
                return;
            }
            var steps = Parse(match.Groups[1].Value);
            string text = result.Text.Remove(match.Index, match.Length);
            text = LegacyNumericRx.Replace(text, "");
            text = LegacyLeverRx.Replace(text, "");
            result.Text = text;
            if (steps.Count == 0) return;
            Start(steps, sayId);
        }

        private static List<Step> Parse(string body)
        {
            var steps = new List<Step>();
            foreach (Match m in StepRx.Matches(body))
            {
                var s = new Step { Source = m.Groups[1].Value.Trim() };
                s.Speed = Number(SpeedRx.Match(s.Source));
                s.Steer = Number(SteerRx.Match(s.Source));
                if (s.Steer.HasValue) s.Steer = Mathf.Clamp(s.Steer.Value, -1f, 1f);
                var u = UntilRx.Match(s.Source);
                if (u.Success)
                {
                    s.Until = u.Groups[1].Value.ToLowerInvariant();
                    if (u.Groups[2].Success) float.TryParse(u.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out s.Value);
                }
                steps.Add(s);
            }
            return steps;
        }

        private static float? Number(Match m)
        {
            if (!m.Success || m.Groups[1].Value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            return float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (float?)null;
        }

        internal static void Cancel(string reason)
        {
            if (_running == null) return;
            Plugin.Instance.StopCoroutine(_running);
            _running = null;
            Plugin.Event("plan", new JObject { ["phase"] = "cancelled", ["planId"] = _planId, ["reason"] = reason });
        }

        private static void Start(List<Step> steps, long sayId)
        {
            Cancel("replaced");
            // The scene manager's own action sequence from an earlier turn stops too.
            var manager = AIManager.Instance;
            if (manager != null)
            {
                var field = AccessTools.Field(manager.GetType(), "_actionCoroutine");
                if (field?.GetValue(manager) is Coroutine c) { manager.StopCoroutine(c); field.SetValue(manager, null); }
            }
            _planId++;
            var list = new JArray();
            foreach (var s in steps) list.Add(Describe(s));
            Plugin.Event("plan", new JObject { ["phase"] = "started", ["planId"] = _planId, ["sayId"] = sayId, ["steps"] = list });
            _running = Plugin.Instance.StartCoroutine(Run(steps, _planId));
        }

        private static string Describe(Step s) =>
            $"speed {(s.Speed.HasValue ? s.Speed.Value.ToString(CultureInfo.InvariantCulture) : "none")}, steer {(s.Steer.HasValue ? s.Steer.Value.ToString(CultureInfo.InvariantCulture) : "none")}, until {s.Until}{(s.Until is "seconds" or "distance" or "turned" or "goal" ? " " + s.Value.ToString(CultureInfo.InvariantCulture) : "")}";

        private static IEnumerator Run(List<Step> steps, int planId)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var player = GameObject.FindGameObjectWithTag("Player");
                if (player == null) { _running = null; yield break; }
                Apply(player, step);
                Plugin.Event("plan", new JObject { ["phase"] = "step", ["planId"] = planId, ["index"] = i + 1, ["count"] = steps.Count, ["step"] = Describe(step) });
                if (step.Until == "none") break;
                var car = player.transform;
                var rb = player.GetComponent<Rigidbody>();
                Vector3 last = car.position;
                float lastYaw = car.eulerAngles.y, distance = 0f, turned = 0f, started = Time.time;
                while (true)
                {
                    yield return null;
                    if (car == null) { _running = null; yield break; }
                    var p = car.position;
                    distance += new Vector2(p.x - last.x, p.z - last.z).magnitude;
                    turned += Mathf.Abs(Mathf.DeltaAngle(lastYaw, car.eulerAngles.y));
                    last = p;
                    lastYaw = car.eulerAngles.y;
                    if (Done(step, Time.time - started, distance, turned, rb, car)) break;
                }
            }
            _running = null;
            Plugin.Event("plan", new JObject { ["phase"] = "done", ["planId"] = planId, ["state"] = Plugin.State() });
        }

        private static bool Done(Step step, float elapsed, float distance, float turned, Rigidbody rb, Transform car)
        {
            switch (step.Until)
            {
                case "seconds": return elapsed >= step.Value;
                case "distance": return distance >= step.Value;
                case "turned": return turned >= step.Value;
                case "stopped": return elapsed > 0.5f && rb != null && rb.linearVelocity.magnitude < 0.3f;
                case "goal":
                    var goal = Plugin.GoalOf(car);
                    return goal != null && (double)goal["distanceM"] <= step.Value;
                default: return true;
            }
        }

        /// <summary>Speed and steering go through the scene's car controller, as the game's own actions do.</summary>
        private static void Apply(GameObject player, Step step)
        {
            foreach (var mb in player.GetComponents<MonoBehaviour>())
            {
                var type = mb.GetType();
                var setSpeed = type.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrNull("SetTargetSpeed");
                var setSteer = type.GetMethod("SetSteeringAngle", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(float) }, null);
                if (setSpeed == null || setSteer == null) continue;
                if (step.Speed.HasValue)
                {
                    var ps = setSpeed.GetParameters();
                    var args = ps.Length == 2 ? new object[] { step.Speed.Value, step.Steer.HasValue && step.Steer.Value != 0f } : new object[] { step.Speed.Value };
                    setSpeed.Invoke(mb, args);
                }
                if (step.Steer.HasValue) setSteer.Invoke(mb, new object[] { step.Steer.Value });
                return;
            }
            Plugin.Log.LogWarning("[sisterdrive] plan: no car controller with SetTargetSpeed/SetSteeringAngle on the player");
        }

        private static MethodInfo FirstOrNull(this MethodInfo[] methods, string name)
        {
            foreach (var m in methods)
                if (m.Name == name && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(float)) return m;
            return null;
        }
    }

    [HarmonyPatch(typeof(AIPromptLoader), nameof(AIPromptLoader.LoadPrompt))]
    internal static class PlanPromptPatch
    {
        private static void Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            if (__result.Contains("target_speed") || __result.Contains("horizontal_action"))
                __result += Plan.PromptSection;
        }
    }
}
