using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Localization.Settings;

namespace CorticoSisterDrive
{
    // Each patch turns one game method call into one event frame. Event types are listed in
    // docs/protocol.md; payload fields are the method's own arguments or the state it changed.

    [HarmonyPatch(typeof(AIManager), nameof(AIManager.SendAudio2LLM))]
    internal static class VoiceInputPatch
    {
        private static void Prefix(byte[] audioData)
        {
            Plugin.LastInputSource = Plugin.Injecting ? "world" : "mic";
            if (!Plugin.Injecting)
                Plugin.Event("micInput", new JObject { ["bytes"] = audioData?.Length ?? 0 });
        }
    }

    [HarmonyPatch(typeof(AIManager), "SendToLlmAsync")]
    internal static class ContextPatch
    {
        private static void Prefix(string context) => Plugin.LastContext = context;
    }

    [HarmonyPatch(typeof(AIManager), "CheckRequestLimitReached")]
    internal static class RateLimitPatch
    {
        private static void Postfix(bool __result)
        {
            if (__result) Plugin.Event("rateLimited", new JObject { ["source"] = Plugin.LastInputSource });
        }
    }

    [HarmonyPatch(typeof(ModelRouter), "CommitAndReturn")]
    internal static class TurnPatch
    {
        private static void Prefix(LlmTurnResult result) => Plugin.Instance?.OnTurnCommitted(result);
    }

    [HarmonyPatch(typeof(ModelRouter), "ApplyPlayResult")]
    internal static class TurnFailedPatch
    {
        private static void Prefix(LlmTurnResult result)
        {
            if (result != null && !result.Success)
                Plugin.Event("turnFailed", new JObject { ["source"] = Plugin.LastInputSource, ["error"] = result.ErrorMessage, ["latencySec"] = result.LatencySeconds });
        }
    }

    [HarmonyPatch(typeof(AiVoiceService), nameof(AiVoiceService.RunTTS))]
    internal static class LinePatch
    {
        private static void Prefix(string text, float? requestTime)
        {
            // AIManager passes requestTime only for the reply to a voice turn.
            if (requestTime.HasValue && Plugin.Instance != null && Plugin.Instance.OnReplyLine(text)) return;
            Plugin.Event("line", new JObject { ["text"] = text });
        }
    }

    [HarmonyPatch(typeof(AiVoiceService), nameof(AiVoiceService.RunPreparedTTS))]
    internal static class PreparedLinePatch
    {
        private static void Prefix(string subtitle) => Plugin.Event("line", new JObject { ["text"] = subtitle });
    }

    [HarmonyPatch(typeof(UIManager), nameof(UIManager.ShowGameOverUI))]
    internal static class GameOverPatch
    {
        private static void Prefix(GameObject ___GameOverUI, GameObject ___MissionCompleteUI)
        {
            if (___GameOverUI != null && !___GameOverUI.activeSelf && !___MissionCompleteUI.activeSelf)
                Plugin.Event("stageEnd", new JObject { ["result"] = "fail", ["state"] = Plugin.State() });
        }
    }

    [HarmonyPatch(typeof(UIManager), nameof(UIManager.ShowMissionCompleteUI))]
    internal static class MissionCompletePatch
    {
        private static void Prefix(GameObject ___GameOverUI, GameObject ___MissionCompleteUI)
        {
            if (___MissionCompleteUI != null && !___GameOverUI.activeSelf && !___MissionCompleteUI.activeSelf)
                Plugin.Event("stageEnd", new JObject { ["result"] = "clear", ["state"] = Plugin.State() });
        }
    }

    [HarmonyPatch(typeof(MissionPanel), nameof(MissionPanel.SetMission))]
    internal static class MissionPatch
    {
        private static void Prefix(string mission) => Plugin.Event("mission", new JObject { ["text"] = mission });
    }

    [HarmonyPatch(typeof(RoadTestMissionTexts), nameof(RoadTestMissionTexts.ShowGuide))]
    internal static class RoadTestGuidePatch
    {
        private static void Prefix(string situation) =>
            Plugin.Event("guide", new JObject { ["key"] = situation, ["text"] = Localized("RoadTest", situation) });

        internal static string Localized(string table, string key)
        {
            try { return LocalizationSettings.StringDatabase.GetLocalizedString(table, key); }
            catch { return null; }
        }
    }

    [HarmonyPatch(typeof(TutorialUIManager), nameof(TutorialUIManager.ShowGuideUI))]
    internal static class TutorialGuidePatch
    {
        private static void Prefix(string key) =>
            Plugin.Event("guide", new JObject { ["key"] = key, ["text"] = RoadTestGuidePatch.Localized("Tutorial", key) });
    }

    [HarmonyPatch(typeof(MessagePopup), nameof(MessagePopup.Show))]
    internal static class PopupPatch
    {
        private static void Prefix(string message) => Plugin.Event("popup", new JObject { ["text"] = message });
    }

    [HarmonyPatch(typeof(RoadTestSituationPrompt), "OnTriggerEnter")]
    internal static class RoadTestSituationPatch
    {
        private static void Prefix(bool ___isPlayerIn, out bool __state) => __state = ___isPlayerIn;

        private static void Postfix(bool __state, bool ___isPlayerIn, string ___prompt2make)
        {
            if (!__state && ___isPlayerIn) Plugin.Event("situation", new JObject { ["text"] = ___prompt2make });
        }
    }

    [HarmonyPatch(typeof(ParkingSituationPrompt), "OnTriggerEnter")]
    internal static class ParkingSituationPatch
    {
        private static void Prefix(ParkingSituationPrompt __instance, out bool __state) => __state = __instance.isPlayerIn;

        private static void Postfix(ParkingSituationPrompt __instance, bool __state, string ___prompt2make)
        {
            if (!__state && __instance.isPlayerIn) Plugin.Event("situation", new JObject { ["text"] = ___prompt2make });
        }
    }

    [HarmonyPatch(typeof(ScoreStatus), nameof(ScoreStatus.AddFine))]
    internal static class FinePatch
    {
        private static void Postfix(ScoreStatus __instance, float amount) =>
            Plugin.Event("fine", new JObject { ["amount"] = amount, ["total"] = __instance.GetFine() });
    }

    [HarmonyPatch(typeof(ScoreStatus), nameof(ScoreStatus.AddDamageCount))]
    internal static class DamagePatch
    {
        private static void Postfix(ScoreStatus __instance) =>
            Plugin.Event("damage", new JObject { ["count"] = __instance.GetDamageCount() });
    }

    [HarmonyPatch(typeof(IllegalActionTimeoutUI), nameof(IllegalActionTimeoutUI.ShowIllegalActionTimeoutUI))]
    internal static class IllegalActionPatch
    {
        private static void Prefix(IllegalActionTimeoutUI __instance, float timeLeft)
        {
            if (!__instance.gameObject.activeSelf) Plugin.Event("illegalAction", new JObject { ["timeLeftSec"] = timeLeft });
        }
    }

    [HarmonyPatch(typeof(EmotionStatus), nameof(EmotionStatus.SetEmotion))]
    internal static class EmotionPatch
    {
        private static void Prefix(EmotionStatus __instance, Emotion emotion)
        {
            var before = __instance.GetEmotion();
            if (before != emotion) Plugin.Event("emotion", new JObject { ["from"] = before.ToString(), ["to"] = emotion.ToString() });
        }
    }
}
