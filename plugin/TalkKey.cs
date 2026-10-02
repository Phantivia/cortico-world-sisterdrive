using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CorticoSisterDrive
{
    /// <summary>
    /// The game's push-to-talk is the Space key (input axis "Recording"). Some stage scripts wait
    /// for that key's release before moving on. An accepted say counts as one release: the
    /// listed classes see GetButtonUp("Recording") / GetKeyUp(Space) true on the next frame.
    /// AIManager keeps reading the real key, so a synthetic release never starts a mic recording.
    /// </summary>
    internal static class TalkKey
    {
        private static int _releaseFrame = -1;

        private static readonly Type[] Gated = { typeof(TutorialController), typeof(CameraOverviewController), typeof(ParkingSceneGameManager) };

        private static readonly MethodInfo RealButtonUp = AccessTools.Method(typeof(Input), nameof(Input.GetButtonUp), new[] { typeof(string) });
        private static readonly MethodInfo RealKeyUp = AccessTools.Method(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(KeyCode) });
        private static readonly MethodInfo ShimButtonUp = AccessTools.Method(typeof(TalkKey), nameof(GetButtonUp));
        private static readonly MethodInfo ShimKeyUp = AccessTools.Method(typeof(TalkKey), nameof(GetKeyUp));

        internal static void Release() => _releaseFrame = Time.frameCount + 1;

        private static bool Released => Time.frameCount == _releaseFrame;

        public static bool GetButtonUp(string name) => Input.GetButtonUp(name) || (name == "Recording" && Released);

        public static bool GetKeyUp(KeyCode key) => Input.GetKeyUp(key) || (key == KeyCode.Space && Released);

        /// <summary>Patches every method (lambdas and iterator bodies included) of the gated classes that reads the key.</summary>
        internal static int Install(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(TalkKey), nameof(Transpiler)));
            int count = 0;
            foreach (var method in Gated.SelectMany(AllMethods))
            {
                bool reads;
                try { reads = PatchProcessor.ReadMethodBody(method).Any(op => Equals(op.Value, RealButtonUp) || Equals(op.Value, RealKeyUp)); }
                catch { continue; }
                if (!reads) continue;
                harmony.Patch(method, transpiler: transpiler);
                count++;
            }
            return count;
        }

        private static IEnumerable<MethodBase> AllMethods(Type type)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var m in type.GetMethods(all)) if (!m.IsAbstract && !m.ContainsGenericParameters) yield return m;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in AllMethods(nested)) yield return m;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.operand is MethodInfo callee)
                {
                    if (callee == RealButtonUp) ins.operand = ShimButtonUp;
                    else if (callee == RealKeyUp) ins.operand = ShimKeyUp;
                }
                yield return ins;
            }
        }
    }
}
