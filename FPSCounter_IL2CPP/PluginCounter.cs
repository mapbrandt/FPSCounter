using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace FPSCounter
{
    internal enum PluginStatsSortMode
    {
        Average,
        LastFrame,
        MaxSpike,
        Calls
    }

    internal static class PluginCounter
    {
        private const int PLUGIN_OUTPUT_SIZE = 40000;
        private const int ROW_LENGTH_PADDING = 280;
        private const int HARMONY_REFRESH_INTERVAL_FRAMES = 300;
        private const string NO_PLUGINS = "No measured plugins";

        private static readonly Dictionary<string, PluginStats> _pluginsByGuid = new Dictionary<string, PluginStats>();
        private static readonly Dictionary<Type, PluginStats> _pluginsByType = new Dictionary<Type, PluginStats>();
        private static readonly Dictionary<Assembly, PluginStats> _singlePluginByAssembly = new Dictionary<Assembly, PluginStats>();
        private static readonly HashSet<Assembly> _multiPluginAssemblies = new HashSet<Assembly>();
        private static readonly Dictionary<MethodBase, PluginStats> _pluginsByPatchMethod = new Dictionary<MethodBase, PluginStats>();
        private static readonly HashSet<MethodBase> _patchedMethods = new HashSet<MethodBase>();
        private static readonly List<PluginStats> _sortedPlugins = new List<PluginStats>();
        private static readonly StringBuilder _logBuilder = new StringBuilder(4096);
        private static Harmony _harmonyInstance;

        private static bool _running;
        private static int _framesUntilHarmonyRefresh;
        private static FixedString _fString;

        public static string StringOutput { get; private set; }
        public static int StringOutputLength { get; private set; }

        public static void Start(string thisPluginGuid)
        {
            if (_running) return;
            _running = true;

            var sw = Stopwatch.StartNew();

            if (_harmonyInstance == null)
                _harmonyInstance = new Harmony(FrameCounter.GUID);

            if (_fString == null)
                _fString = new FixedString(PLUGIN_OUTPUT_SIZE);

            var hookCount = 0;
            var pluginCount = 0;

            // Hook unity event methods on all plugins
            var baseType = typeof(MonoBehaviour);
            var unityMethods = new[] { "FixedUpdate", "Update", "LateUpdate", "OnGUI" };
            foreach (var baseUnityPlugin in IL2CPPChainloader.Instance.Plugins.Where(x => x.Key != thisPluginGuid).Select(x => x.Value))
            {
                if (baseUnityPlugin.Instance == null) continue;
                pluginCount++;

                var stats = GetOrAddPlugin(baseUnityPlugin.Metadata);
                var pluginAssembly = baseUnityPlugin.Instance.GetType().Assembly;
                RegisterPluginAssembly(pluginAssembly, stats);

                var pluginBehaviours = SafeGetTypes(pluginAssembly).Where(x => baseType.IsAssignableFrom(x) && !x.IsAbstract).ToList();
                foreach (var pluginBehaviour in pluginBehaviours)
                {
                    if (_pluginsByType.ContainsKey(pluginBehaviour)) continue;

                    var registeredType = false;

                    foreach (var unityMethod in unityMethods)
                    {
                        var methodInfo = pluginBehaviour.GetMethod(unityMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (methodInfo == null) continue;
                        if (_patchedMethods.Contains(methodInfo)) continue;

                        try
                        {
                            _harmonyInstance.Patch(methodInfo, new HarmonyMethod(AccessTools.Method(typeof(PluginCounter), nameof(Pre))), new HarmonyMethod(AccessTools.Method(typeof(PluginCounter), nameof(Post))));
                            hookCount += 1;
                            _patchedMethods.Add(methodInfo);

                            if (!registeredType)
                            {
                                _pluginsByType[pluginBehaviour] = stats;
                                registeredType = true;
                            }
                            stats.AddUnityHook();
                        }
                        catch (Exception ex)
                        {
                            FrameCounter.Logger.LogError(ex);
                        }
                    }
                }
            }
            var harmonyHookCount = HookHarmonyPatchMethods();
            _framesUntilHarmonyRefresh = HARMONY_REFRESH_INTERVAL_FRAMES;

            FrameCounter.Logger.LogDebug($"Attached timers to {hookCount} unity methods and {harmonyHookCount} Harmony patch methods of {_pluginsByGuid.Count} plugins in {sw.ElapsedMilliseconds}ms (scanned {pluginCount} plugins)");
        }

        public static void Stop()
        {
            if (!_running) return;

            _harmonyInstance.UnpatchSelf();

            _running = false;
            _pluginsByType.Clear();
            _pluginsByGuid.Clear();
            _singlePluginByAssembly.Clear();
            _multiPluginAssemblies.Clear();
            _pluginsByPatchMethod.Clear();
            _patchedMethods.Clear();
            _sortedPlugins.Clear();
            _logBuilder.Length = 0;
            _framesUntilHarmonyRefresh = 0;

            if (_fString != null)
                _fString.builder.Length = 0;

            StringOutput = null;
            StringOutputLength = 0;
        }

        public static void CollectFrame(long frameTicks, float msScale)
        {
            if (!_running) return;

            RefreshHarmonyPatchMethodsIfNeeded();

            var maxLines = Clamp(FrameCounter.PluginStatsMaxLines, 1, 100);
            var builder = _fString.builder;
            builder.Length = 0;

            _sortedPlugins.Clear();
            foreach (var stats in _pluginsByGuid.Values)
            {
                stats.SampleFrame();
                _sortedPlugins.Add(stats);
            }

            if (_sortedPlugins.Count == 0)
            {
                builder.Append(NO_PLUGINS);
            }
            else
            {
                SortPlugins(FrameCounter.CurrentPluginStatsSortMode);
                AppendPluginRows(builder, msScale, maxLines, "\n", true);
            }

            StringOutputLength = builder.Length;
            StringOutput = _fString.PopValue();
            LogHitchIfNeeded(frameTicks, msScale, maxLines);
        }

        private static PluginStats GetOrAddPlugin(BepInPlugin metadata)
        {
            PluginStats stats;
            if (!_pluginsByGuid.TryGetValue(metadata.GUID, out stats))
            {
                stats = new PluginStats(metadata);
                _pluginsByGuid.Add(metadata.GUID, stats);
            }
            return stats;
        }

        private static void LogHitchIfNeeded(long frameTicks, float msScale, int maxLines)
        {
            var thresholdMs = FrameCounter.HitchLogThresholdMs;
            if (thresholdMs <= 0f) return;

            var frameMs = frameTicks * msScale;
            if (frameMs < thresholdMs) return;

            _logBuilder.Length = 0;
            _logBuilder.Append("Frame hitch ");
            _logBuilder.Concat(frameMs, 2, 0);
            _logBuilder.Append("ms | Top plugins: ");

            if (_sortedPlugins.Count == 0)
            {
                _logBuilder.Append(NO_PLUGINS);
            }
            else
            {
                SortPlugins(PluginStatsSortMode.LastFrame);
                AppendPluginRows(_logBuilder, msScale, maxLines, "; ", false);
            }

            FrameCounter.Logger.LogWarning(_logBuilder.ToString());
        }

        private static int AppendPluginRows(StringBuilder builder, float msScale, int maxLines, string separator, bool enforceCapacity)
        {
            var appended = 0;

            for (var i = 0; i < _sortedPlugins.Count && appended < maxLines; i++)
            {
                var stats = _sortedPlugins[i];
                var estimatedLength = stats.Guid.Length + ROW_LENGTH_PADDING + (appended > 0 ? separator.Length : 0);
                if (enforceCapacity && builder.Length + estimatedLength >= builder.MaxCapacity)
                    break;

                if (appended > 0)
                    builder.Append(separator);

                AppendPluginRow(builder, stats, msScale);
                appended++;
            }

            return appended;
        }

        private static void AppendPluginRow(StringBuilder builder, PluginStats stats, float msScale)
        {
            builder.Append(stats.Guid);

            if (stats.HookCount == 0)
            {
                builder.Append(" | UNMEASURED | HOOKS U/H 000/000");
                return;
            }

            builder.Append(" | AVG ");
            builder.Concat(stats.AverageTicks * msScale, 2, 0);
            builder.Append("ms | LAST ");
            builder.Concat(stats.LastTotalTicks * msScale, 2, 0);
            builder.Append("ms | MAX ");
            builder.Concat(stats.MaxSpikeTicks * msScale, 2, 0);
            builder.Append("ms | CALLS ");
            builder.Concat(stats.LastCallCount, 3, '0');
            builder.Append(" | HOOKS U/H ");
            builder.Concat(stats.UnityHookCount, 3, '0');
            builder.Append("/");
            builder.Concat(stats.HarmonyHookCount, 3, '0');
            builder.Append(" | F/U/L/G ");
            builder.Concat(stats.LastFixedTicks * msScale, 2, 0);
            builder.Append("/");
            builder.Concat(stats.LastUpdateTicks * msScale, 2, 0);
            builder.Append("/");
            builder.Concat(stats.LastLateTicks * msScale, 2, 0);
            builder.Append("/");
            builder.Concat(stats.LastOnGuiTicks * msScale, 2, 0);
            builder.Append("ms | PATCH ");
            builder.Concat(stats.LastHarmonyPatchTicks * msScale, 2, 0);
            builder.Append("ms");
        }

        private static void SortPlugins(PluginStatsSortMode sortMode)
        {
            switch (sortMode)
            {
                case PluginStatsSortMode.Average:
                    _sortedPlugins.Sort(CompareByAverage);
                    break;
                case PluginStatsSortMode.LastFrame:
                    _sortedPlugins.Sort(CompareByLastFrame);
                    break;
                case PluginStatsSortMode.Calls:
                    _sortedPlugins.Sort(CompareByCalls);
                    break;
                case PluginStatsSortMode.MaxSpike:
                default:
                    _sortedPlugins.Sort(CompareByMaxSpike);
                    break;
            }
        }

        private static int CompareByAverage(PluginStats left, PluginStats right)
        {
            var result = right.AverageTicks.CompareTo(left.AverageTicks);
            if (result != 0) return result;

            result = right.LastTotalTicks.CompareTo(left.LastTotalTicks);
            if (result != 0) return result;

            return string.Compare(left.Guid, right.Guid, StringComparison.Ordinal);
        }

        private static int CompareByLastFrame(PluginStats left, PluginStats right)
        {
            var result = right.LastTotalTicks.CompareTo(left.LastTotalTicks);
            if (result != 0) return result;

            result = right.AverageTicks.CompareTo(left.AverageTicks);
            if (result != 0) return result;

            return string.Compare(left.Guid, right.Guid, StringComparison.Ordinal);
        }

        private static int CompareByMaxSpike(PluginStats left, PluginStats right)
        {
            var result = right.MaxSpikeTicks.CompareTo(left.MaxSpikeTicks);
            if (result != 0) return result;

            result = right.LastTotalTicks.CompareTo(left.LastTotalTicks);
            if (result != 0) return result;

            return string.Compare(left.Guid, right.Guid, StringComparison.Ordinal);
        }

        private static int CompareByCalls(PluginStats left, PluginStats right)
        {
            var result = right.LastCallCount.CompareTo(left.LastCallCount);
            if (result != 0) return result;

            result = right.LastTotalTicks.CompareTo(left.LastTotalTicks);
            if (result != 0) return result;

            return string.Compare(left.Guid, right.Guid, StringComparison.Ordinal);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static void RefreshHarmonyPatchMethodsIfNeeded()
        {
            _framesUntilHarmonyRefresh--;
            if (_framesUntilHarmonyRefresh > 0) return;

            _framesUntilHarmonyRefresh = HARMONY_REFRESH_INTERVAL_FRAMES;
            var hookCount = HookHarmonyPatchMethods();
            if (hookCount > 0)
                FrameCounter.Logger.LogDebug($"Attached timers to {hookCount} new Harmony patch methods");
        }

        private static void RegisterPluginAssembly(Assembly assembly, PluginStats stats)
        {
            PluginStats existingStats;
            if (_singlePluginByAssembly.TryGetValue(assembly, out existingStats))
            {
                if (existingStats != stats)
                {
                    _singlePluginByAssembly.Remove(assembly);
                    _multiPluginAssemblies.Add(assembly);
                }
            }
            else if (!_multiPluginAssemblies.Contains(assembly))
            {
                _singlePluginByAssembly.Add(assembly, stats);
            }
        }

        private static int HookHarmonyPatchMethods()
        {
            var hookCount = 0;
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                var patches = Harmony.GetPatchInfo(original);
                if (patches == null) continue;

                hookCount += HookHarmonyPatches(patches.Prefixes);
                hookCount += HookHarmonyPatches(patches.Postfixes);
                hookCount += HookHarmonyPatches(patches.Finalizers);
            }
            return hookCount;
        }

        private static int HookHarmonyPatches(IEnumerable<Patch> patches)
        {
            var hookCount = 0;
            foreach (var patch in patches)
            {
                var patchMethod = patch.PatchMethod;
                if (patchMethod == null) continue;
                if (_patchedMethods.Contains(patchMethod)) continue;

                var stats = GetStatsForPatch(patch);
                if (stats == null) continue;

                try
                {
                    _harmonyInstance.Patch(patchMethod, new HarmonyMethod(AccessTools.Method(typeof(PluginCounter), nameof(PreHarmonyPatch))), new HarmonyMethod(AccessTools.Method(typeof(PluginCounter), nameof(PostHarmonyPatch))));
                    _patchedMethods.Add(patchMethod);
                    _pluginsByPatchMethod[patchMethod] = stats;
                    stats.AddHarmonyHook();
                    hookCount++;
                }
                catch (Exception ex)
                {
                    FrameCounter.Logger.LogError(ex);
                }
            }
            return hookCount;
        }

        private static PluginStats GetStatsForPatch(Patch patch)
        {
            PluginStats stats;
            if (!string.IsNullOrEmpty(patch.owner) && _pluginsByGuid.TryGetValue(patch.owner, out stats))
                return stats;

            var declaringType = patch.PatchMethod.DeclaringType;
            if (declaringType == null) return null;

            if (_singlePluginByAssembly.TryGetValue(declaringType.Assembly, out stats))
                return stats;

            return null;
        }

        private static void Post(MonoBehaviour __instance, MethodInfo __originalMethod, long __state)
        {
            if (__state == 0) return;

            PluginStats stats;
            if (_pluginsByType.TryGetValue(__originalMethod.DeclaringType, out stats))
                stats.AddSample(__originalMethod.Name, Stopwatch.GetTimestamp() - __state);
        }

        private static void Pre(MonoBehaviour __instance, MethodInfo __originalMethod, out long __state)
        {
            __state = 0;

            if (!FrameCounter.FrameCounterComponent.FrameCounterHelper.CanProcessOnGui && __originalMethod.Name == "OnGUI") return;
            if (!_pluginsByType.ContainsKey(__originalMethod.DeclaringType)) return;

            __state = Stopwatch.GetTimestamp();
        }

        private static void PostHarmonyPatch(MethodBase __originalMethod, long __state)
        {
            if (__state == 0) return;

            PluginStats stats;
            if (_pluginsByPatchMethod.TryGetValue(__originalMethod, out stats))
                stats.AddHarmonyPatchSample(Stopwatch.GetTimestamp() - __state);
        }

        private static void PreHarmonyPatch(MethodBase __originalMethod, out long __state)
        {
            __state = 0;

            if (!_pluginsByPatchMethod.ContainsKey(__originalMethod)) return;

            __state = Stopwatch.GetTimestamp();
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

        private sealed class PluginStats
        {
            private readonly MovingAverage _average = new MovingAverage(60);

            public PluginStats(BepInPlugin metadata)
            {
                Guid = metadata.GUID;
            }

            public string Guid { get; }
            public long AverageTicks { get; private set; }
            public long LastTotalTicks { get; private set; }
            public long MaxSpikeTicks { get; private set; }
            public int LastCallCount { get; private set; }
            public int UnityHookCount { get; private set; }
            public int HarmonyHookCount { get; private set; }
            public long LastFixedTicks { get; private set; }
            public long LastUpdateTicks { get; private set; }
            public long LastLateTicks { get; private set; }
            public long LastOnGuiTicks { get; private set; }
            public long LastHarmonyPatchTicks { get; private set; }

            private long _currentTotalTicks;
            private int _currentCallCount;
            private long _currentFixedTicks;
            private long _currentUpdateTicks;
            private long _currentLateTicks;
            private long _currentOnGuiTicks;
            private long _currentHarmonyPatchTicks;

            public int HookCount
            {
                get { return UnityHookCount + HarmonyHookCount; }
            }

            public void AddUnityHook()
            {
                UnityHookCount++;
            }

            public void AddHarmonyHook()
            {
                HarmonyHookCount++;
            }

            public void AddSample(string methodName, long elapsedTicks)
            {
                if (elapsedTicks <= 0) return;

                Interlocked.Add(ref _currentTotalTicks, elapsedTicks);
                Interlocked.Increment(ref _currentCallCount);

                switch (methodName)
                {
                    case "FixedUpdate":
                        Interlocked.Add(ref _currentFixedTicks, elapsedTicks);
                        break;
                    case "Update":
                        Interlocked.Add(ref _currentUpdateTicks, elapsedTicks);
                        break;
                    case "LateUpdate":
                        Interlocked.Add(ref _currentLateTicks, elapsedTicks);
                        break;
                    case "OnGUI":
                        Interlocked.Add(ref _currentOnGuiTicks, elapsedTicks);
                        break;
                }
            }

            public void AddHarmonyPatchSample(long elapsedTicks)
            {
                if (elapsedTicks <= 0) return;

                Interlocked.Add(ref _currentTotalTicks, elapsedTicks);
                Interlocked.Add(ref _currentHarmonyPatchTicks, elapsedTicks);
                Interlocked.Increment(ref _currentCallCount);
            }

            public void SampleFrame()
            {
                LastTotalTicks = Interlocked.Exchange(ref _currentTotalTicks, 0);
                LastCallCount = Interlocked.Exchange(ref _currentCallCount, 0);
                LastFixedTicks = Interlocked.Exchange(ref _currentFixedTicks, 0);
                LastUpdateTicks = Interlocked.Exchange(ref _currentUpdateTicks, 0);
                LastLateTicks = Interlocked.Exchange(ref _currentLateTicks, 0);
                LastOnGuiTicks = Interlocked.Exchange(ref _currentOnGuiTicks, 0);
                LastHarmonyPatchTicks = Interlocked.Exchange(ref _currentHarmonyPatchTicks, 0);

                _average.Sample(LastTotalTicks);
                AverageTicks = _average.GetAverage();

                if (LastTotalTicks > MaxSpikeTicks)
                    MaxSpikeTicks = LastTotalTicks;
            }
        }
    }
}
