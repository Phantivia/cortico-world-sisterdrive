using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CorticoSisterDrive
{
    /// <summary>
    /// AiVoiceService never destroys the clips its TTS backends create, nor the ones its sentence
    /// cache evicts; they stay in memory until a scene load unloads unused assets, so a long stage
    /// keeps one clip per spoken sentence. Clips the game synthesized are tracked here and
    /// destroyed once the audio source, the play queue and the sentence cache no longer hold them.
    /// Filler clips and RunPreparedTTS clips are the game's own assets and are never tracked.
    /// </summary>
    internal static class ClipReaper
    {
        /// <summary>Tracked clips and the last time something held them.</summary>
        private static readonly Dictionary<AudioClip, float> Owned = new Dictionary<AudioClip, float>();
        private static bool _prepared;
        private static float _nextSweep;
        private const float SweepSec = 1f;
        // Streaming synthesis plays its chunks first and adds them to the sentence cache when the
        // sentence ends; a clip stays alive this long after it was last held so the cache can take it.
        private const float GraceSec = 30f;

        internal static void Track(AudioClip clip)
        {
            if (clip != null) Owned[clip] = Time.unscaledTime;
        }

        internal static bool Prepared { set => _prepared = value; }

        internal static void Enqueued(AiVoiceService service, AudioClip clip)
        {
            if (clip == null || _prepared) return;
            var t = Traverse.Create(service);
            if (Contains(t.Field<List<AudioClip>>("fillerKoAudioClips").Value, clip)) return;
            if (Contains(t.Field<List<AudioClip>>("fillerOthersAudioClips").Value, clip)) return;
            Owned[clip] = Time.unscaledTime;
        }

        private static bool Contains(List<AudioClip> list, AudioClip clip) => list != null && list.Contains(clip);

        internal static void Sweep()
        {
            if (Owned.Count == 0 || Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + SweepSec;
            var service = AiVoiceService.Instance;
            if (service == null) return;
            var t = Traverse.Create(service);
            var held = new HashSet<AudioClip>();
            var source = t.Field<AudioSource>("audioSource").Value;
            // The source keeps its last clip after playback and while paused.
            if (source != null && source.clip != null) held.Add(source.clip);
            if (t.Field("_audioQue").GetValue() is IEnumerable queue)
                foreach (var element in queue)
                    if (element != null && Traverse.Create(element).Field<AudioClip>("clip").Value is AudioClip queued && queued != null) held.Add(queued);
            var cache = t.Field("_ttsSentenceCache").GetValue();
            if (cache != null && Traverse.Create(cache).Field<Dictionary<string, List<List<AudioClip>>>>("_cache").Value is Dictionary<string, List<List<AudioClip>>> entries)
                foreach (var variants in entries.Values)
                    if (variants != null)
                        foreach (var clips in variants)
                            if (clips != null) held.UnionWith(clips);
            var free = new List<AudioClip>();
            var now = Time.unscaledTime;
            foreach (var clip in new List<AudioClip>(Owned.Keys))
            {
                if (clip != null && held.Contains(clip)) Owned[clip] = now;
                else if (clip == null || now - Owned[clip] >= GraceSec) free.Add(clip);
            }
            foreach (var clip in free)
            {
                Owned.Remove(clip);
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }
        }
    }

    [HarmonyPatch(typeof(AiVoiceService), "EnqueAudioData")]
    internal static class EnqueueClipPatch
    {
        private static void Prefix(AiVoiceService __instance, AudioClip clip) => ClipReaper.Enqueued(__instance, clip);
    }

    [HarmonyPatch(typeof(AiVoiceService), nameof(AiVoiceService.RunPreparedTTS))]
    internal static class PreparedClipPatch
    {
        private static void Prefix() => ClipReaper.Prepared = true;

        private static Exception Finalizer(Exception __exception)
        {
            ClipReaper.Prepared = false;
            return __exception;
        }
    }

    /// <summary>Cache warm-ups synthesize without playing; their clips enter only through here.</summary>
    [HarmonyPatch(typeof(TtsSentenceCache), nameof(TtsSentenceCache.AddVariant))]
    internal static class CachedClipPatch
    {
        private static void Postfix(List<AudioClip> clips)
        {
            if (clips == null) return;
            foreach (var clip in clips) ClipReaper.Track(clip);
        }
    }
}
