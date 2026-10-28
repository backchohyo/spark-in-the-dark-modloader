// 성능 기록: 1분마다 Mods\ModLoader.log 에 FPS·끊김·글꼴 재생성·모드별 소요 시간을 남긴다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ModLoader
{
    public static class Perf
    {
        public static bool Enabled = true;
        class Bucket { public long Ticks; public int Calls; }
        static readonly Dictionary<string, Bucket> Buckets = new Dictionary<string, Bucket>();

        public static long Begin() { return Enabled ? Stopwatch.GetTimestamp() : 0L; }

        public static void End(string key, long start)
        {
            if (!Enabled || start == 0L) return;
            long dt = Stopwatch.GetTimestamp() - start;
            Bucket b;
            if (!Buckets.TryGetValue(key, out b)) { b = new Bucket(); Buckets[key] = b; }
            b.Ticks += dt;
            b.Calls++;
        }

        internal static string Report(float seconds)
        {
            if (Buckets.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var kv in Buckets.OrderByDescending(k => k.Value.Ticks))
            {
                double ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency;
                sb.Append("\n      ").Append(kv.Key).Append(": ").Append((ms / seconds).ToString("0.00")).Append("ms/초 (")
                  .Append((kv.Value.Calls / seconds).ToString("0")).Append("회/초)");
            }
            Buckets.Clear();
            return sb.ToString();
        }
    }

    public class PerfMonitor : MonoBehaviour
    {
        static PerfMonitor _inst;
        float _start;
        int _frames, _spikes, _bigSpikes;
        float _worst;
        readonly Dictionary<string, int> _rebuilds = new Dictionary<string, int>();

        public static void Ensure()
        {
            if (_inst) return;
            var go = new GameObject("ModLoaderPerf");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<PerfMonitor>();
        }

        void OnEnable() { Font.textureRebuilt += OnRebuilt; _start = Time.unscaledTime; }
        void OnDisable() { Font.textureRebuilt -= OnRebuilt; }

        void OnRebuilt(Font f)
        {
            string n = f ? f.name : "?";
            int c; _rebuilds.TryGetValue(n, out c); _rebuilds[n] = c + 1;
        }

        void Update()
        {
            if (!Perf.Enabled) return;
            float dt = Time.unscaledDeltaTime;
            _frames++;
            if (dt > 0.05f) _spikes++;
            if (dt > 0.2f) _bigSpikes++;
            if (dt > _worst) _worst = dt;
            float el = Time.unscaledTime - _start;
            if (el < 60f) return;
            string rb = _rebuilds.Count == 0 ? "없음" : string.Join(", ", _rebuilds.Select(k => k.Key + " " + k.Value + "회").ToArray());
            Loader.Log("[성능] 최근 " + el.ToString("0") + "초: 평균 " + (_frames / el).ToString("0") + "FPS, 50ms 넘는 프레임 " + _spikes
                + "번, 200ms 넘는 프레임 " + _bigSpikes + "번, 가장 긴 프레임 " + (_worst * 1000f).ToString("0") + "ms, 글꼴 텍스처 재생성: " + rb
                + Perf.Report(el));
            _start = Time.unscaledTime; _frames = _spikes = _bigSpikes = 0; _worst = 0f; _rebuilds.Clear();
        }
    }
}
