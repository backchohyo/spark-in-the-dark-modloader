// 로더 공용 저장 기능: 여러 모드가 같은 세이브 지점을 고치다 충돌하지 않도록 로더가 한 번만 연결하고,
// 모드는 SaveExt.Register(키, 저장, 불러오기) 로 자기 데이터를 붙인다.
// 저장되는 값은 게임 오브젝트의 JSON 끝에 "키":"값" 으로 덧붙는다 (게임은 모르는 키를 무시).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ModLoader
{
    public static class SaveExt
    {
        class Handler
        {
            public string Key;
            public Func<object, string> Save;
            public Action<object, string> Load;
        }

        static readonly List<Handler> Handlers = new List<Handler>();
        static object _lastData, _lastOwner;
        static Type _iface;
        static MethodInfo _getSave, _loadSave;
        static int _logged;

        /// <summary>key: JSON 필드 이름. save(오브젝트) 가 null 이 아니면 저장. load(오브젝트, 값 또는 null) 은 불러올 때 항상 호출.</summary>
        public static void Register(string key, Func<object, string> save, Action<object, string> load)
        {
            Handlers.RemoveAll(h => h.Key == key);
            Handlers.Add(new Handler { Key = key, Save = save, Load = load });
            Loader.Log("[저장] 모드 데이터 키 등록: " + key);
        }

        static void Resolve(object s)
        {
            if (_iface != null || s == null) return;
            foreach (var i in s.GetType().GetInterfaces())
                if (i.Name == "ISaveable") { _iface = i; break; }
            if (_iface == null) return;
            _getSave = _iface.GetMethod("GetSaveData");
            _loadSave = _iface.GetMethod("LoadSaveData");
        }

        static void Warn(Exception e)
        {
            if (_logged++ < 20) Loader.Log("[저장] 오류: " + (e.InnerException ?? e).Message);
        }

        // SaveGameSystem.SaveGame 안의 ISaveable.GetSaveData() 호출을 대체
        public static object HookGetSaveData(object s)
        {
            Resolve(s);
            object data;
            try { data = _getSave.Invoke(s, null); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            _lastData = data;
            _lastOwner = s;
            return data;
        }

        // SaveGameSystem.SaveGame 안의 JsonUtility.ToJson(object) 호출을 대체
        public static string HookToJson(object o)
        {
            string json = JsonUtility.ToJson(o);
            try
            {
                if (Handlers.Count > 0 && o != null && ReferenceEquals(o, _lastData) && _lastOwner != null)
                {
                    foreach (var h in Handlers)
                    {
                        string v = null;
                        try { v = h.Save(_lastOwner); }
                        catch (Exception e) { Warn(e); }
                        if (v != null) json = Inject(json, h.Key, v);
                    }
                }
            }
            catch (Exception e) { Warn(e); }
            finally { _lastData = null; _lastOwner = null; }
            return json;
        }

        // SaveGameSystem.LoadObjectFromSaveData 안의 ISaveable.LoadSaveData(object) 호출을 대체
        public static void HookLoadSaveData(object s, object data)
        {
            Resolve(s);
            try { _loadSave.Invoke(s, new[] { data }); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            if (Handlers.Count == 0) return;
            string json = data as string;
            foreach (var h in Handlers)
            {
                try { h.Load(s, json == null ? null : Extract(json, h.Key)); }
                catch (Exception e) { Warn(e); }
            }
        }

        // ------------------------------------------------------------ JSON 문자열 다루기
        public static string Inject(string json, string key, string value)
        {
            if (json == null) return null;
            string t = json.TrimEnd();
            if (t.Length < 2 || t[t.Length - 1] != '}') return json;
            string field = "\"" + key + "\":\"" + Escape(value) + "\"";
            string body = t.Substring(0, t.Length - 1).TrimEnd();
            return body.EndsWith("{", StringComparison.Ordinal) ? body + field + "}" : body + "," + field + "}";
        }

        public static string Extract(string json, string key)
        {
            string marker = "\"" + key + "\":\"";
            int i = json.LastIndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            var sb = new StringBuilder();
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < json.Length) { sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16)); i += 4; }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return null;
        }

        static string Escape(string v)
        {
            var sb = new StringBuilder(v.Length + 8);
            foreach (char c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
