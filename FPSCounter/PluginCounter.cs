﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace FPSCounter
{
    internal static class PluginCounter
    {
        private static readonly Dictionary<BepInPlugin, KeyValuePair<MovingAverage, List<Stopwatch>>> _averages = new Dictionary<BepInPlugin, KeyValuePair<MovingAverage, List<Stopwatch>>>();
        private static readonly Dictionary<Type, KeyValuePair<BepInPlugin, Stopwatch>> _timers = new Dictionary<Type, KeyValuePair<BepInPlugin, Stopwatch>>();
        private static Harmony _harmonyInstance;

        private static bool _running;
        private static Action _stopAction;

        public static string StringOutput { get; private set; }

        public static void Start(MonoBehaviour mb, BaseUnityPlugin thisPlugin)
        {
            if (_running) return;
            _running = true;

            if (_harmonyInstance == null)
                _harmonyInstance = new Harmony(FrameCounter.GUID);

            var hookCount = 0;

            // Hook unity event methods on all plugins
            var baseType = typeof(MonoBehaviour);
            var unityMethods = new[] { "FixedUpdate", "Update", "LateUpdate", "OnGUI" };
            foreach (var baseUnityPlugin in Chainloader.Plugins.Where(x => x != null && x != thisPlugin))
            {
                var timer = new Stopwatch();

                var pluginBehaviours = SafeGetTypes(baseUnityPlugin.GetType().Assembly).Where(x => baseType.IsAssignableFrom(x) && !x.IsAbstract).ToList();
                foreach (var pluginBehaviour in pluginBehaviours)
                {
                    foreach (var unityMethod in unityMethods)
                    {
                        var methodInfo = pluginBehaviour.GetMethod(unityMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (methodInfo == null) continue;

                        if (!_timers.ContainsKey(pluginBehaviour))
                        {
                            var bepInPlugin = baseUnityPlugin.Info.Metadata;
                            _timers[pluginBehaviour] = new KeyValuePair<BepInPlugin, Stopwatch>(bepInPlugin, timer);

                            if (!_averages.TryGetValue(bepInPlugin, out var kvp))
                            {
                                kvp = new KeyValuePair<MovingAverage, List<Stopwatch>>(new MovingAverage(60), new List<Stopwatch>());
                                _averages.Add(bepInPlugin, kvp);
                            }
                            kvp.Value.Add(timer);
                        }

                        try
                        {
                            _harmonyInstance.Patch(methodInfo, new HarmonyMethod(AccessTools.Method(typeof(PluginCounter), nameof(Pre))), new HarmonyMethod(AccessTools.Method(typeof(PluginCounter), nameof(Post))));
                            hookCount += 1;
                        }
                        catch (Exception ex)
                        {
                            FrameCounter.Logger.LogError(ex);
                        }
                    }
                }
            }

            var co = mb.StartCoroutine(CollectLoop());
            _stopAction = () => mb.StopCoroutine(co);

            FrameCounter.Logger.LogDebug($"Attached timers to {hookCount} unity methods in {Chainloader.Plugins.Count} plugins");
        }

        public static void Stop()
        {
            if (!_running) return;

            _harmonyInstance.UnpatchAll(_harmonyInstance.Id);

            _running = false;

            foreach (var timer in _timers)
                timer.Value.Value.Reset();
            _timers.Clear();
            _averages.Clear();

            _stopAction();

            StringOutput = null;
        }

        private static IEnumerator CollectLoop()
        {
            var nanosecPerTick = 1000L * 1000L * 100L / Stopwatch.Frequency;
            var msScale = 1f / (nanosecPerTick * 1000f);
            var cutoffTicks = nanosecPerTick * 100;

            while (true)
            {
                yield return new WaitForEndOfFrame();

                var toShow = _averages.Select(kvp =>
                {
                    var tickSum = kvp.Value.Value.Sum(x => x.ElapsedTicks);
                    var ma = kvp.Value.Key;
                    ma.Sample(tickSum);
                    return new { plugin = kvp.Key.GUID, avg = ma.GetAverage() };
                })
                .Where(x => x.avg > cutoffTicks)
                .OrderByDescending(x => x.avg)
                .Take(5)
                .ToList();

                if (toShow.Count == 0)
                    StringOutput = "No slow plugins";
                else
                    StringOutput = string.Join(" | ", toShow.Select(timer => $"{timer.plugin}: {timer.avg * msScale,5:0.00}ms").ToArray());

                foreach (var timer in _timers)
                    timer.Value.Value.Reset();
            }
        }

        private static void Post(MonoBehaviour __instance, MethodInfo __originalMethod)
        {
            if (!FrameCounter.FrameCounterHelper.CanProcessOnGui && __originalMethod.Name == "OnGUI") return;

            if (_timers.TryGetValue(__instance.GetType(), out var watch))
                watch.Value.Stop();
        }

        private static void Pre(MonoBehaviour __instance, MethodInfo __originalMethod)
        {
            if (!FrameCounter.FrameCounterHelper.CanProcessOnGui && __originalMethod.Name == "OnGUI") return;

            if (_timers.TryGetValue(__instance.GetType(), out var watch))
                watch.Value.Start();
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly ass)
        {
            try
            {
                return ass.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(x => x != null);
            }
        }
    }
}
