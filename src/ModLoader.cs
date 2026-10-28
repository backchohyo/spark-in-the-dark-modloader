// Spark in the Dark 모드 로더
// Unity Doorstop(winhttp.dll)이 게임 시작 직전에 Doorstop.Entrypoint.Start() 를 호출한다.
// Mods 폴더의 모드 DLL 에 붙은 [Hook] 속성을 읽어 Assembly-CSharp 를 "메모리에서만" 수정한 뒤 불러온다.
// 게임 원본 파일은 전혀 수정하지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            try { ModLoader.Loader.Run(); }
            catch (Exception e) { ModLoader.Loader.Log("치명적 오류: " + e); }
        }
    }
}

namespace ModLoader
{
    public enum HookKind
    {
        Prefix = 0,         // 메서드 시작에서 훅(Args...) 호출
        PrefixSkip = 1,     // 훅(Args...) 이 true 면 원본을 건너뜀 (void 메서드)
        PrefixReturn = 2,   // 훅(Args...) 결과가 null/NaN 이 아니면 그 값을 바로 반환
        PrefixArg = 3,      // 인자 ArgIndex 를 훅(Args...) 결과로 교체
        Postfix = 4,        // 반환 직전 훅(결과, Args...) 결과를 반환 (void 메서드는 훅(Args...))
        ReplaceCall = 5,    // 메서드 안의 CallType.CallMethod 호출을 훅(원래 인자..., Args...) 로 교체
        PrefixSkipReturn = 6 // bool 훅(Args..., out 결과) 이 true 면 그 결과를 바로 반환 (값 형식 반환에도 사용 가능)
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HookAttribute : Attribute
    {
        public HookKind Kind;
        public string Type;
        public string Assembly;     // 대상 어셈블리 (생략하면 Assembly-CSharp, 예: "UnityEngine.UI")
        public string Method;
        public int Params = -1;
        public string ParamTypes;
        public int[] Args;
        public int ArgIndex = 1;
        public string CallType;
        public string CallMethod;
        public int CallParams = -1;
        public int Count = 1;

        public HookAttribute(HookKind kind, string type, string method)
        {
            Kind = kind; Type = type; Method = method;
        }
    }

    // 모드 DLL 이 로드된 직후 한 번 호출되는 static 메서드 (Unity 가 준비되기 전이므로 Unity 기능은 쓰지 말 것)
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ModInitAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class ModInfoAttribute : Attribute
    {
        public string Name, Version, Author, Description, SettingsNote;
        public ModInfoAttribute(string name, string version) { Name = name; Version = version; }
    }

    public enum ModState { Applied, Disabled, Failed, Unreadable }

    public class ModEntry
    {
        public string File;          // 파일 이름 (예: SparkMod.dll)
        public string Name, Version, Author, Description;
        public ModState State;
        public bool EnabledInConfig; // 메뉴에서 바꾼 현재 설정 (다음 실행에 적용)
        public List<string> Errors = new List<string>();
        public string Notice;        // 메뉴에 보여줄 특별 안내 (있으면 오류 문구 대신 표시)
        public string SettingsNote;  // 설정 화면 상단 안내
        public bool HasSettings;     // [Setting] 필드가 있는지
        public string FullPath;
        public Assembly Assembly;    // 로드된 어셈블리 (꺼진 모드는 설정 화면을 열 때 로드)
        public string Title { get { return string.IsNullOrEmpty(Version) ? Name : Name + " v" + Version; } }
    }

    public static class Loader
    {
        public const string Version = "1.6.0";
        public const string ConfigName = "ModLoader.cfg";
        public static readonly List<ModEntry> Mods = new List<ModEntry>();
        public static string ModsDir { get { return _modsDir; } }
        static string _logPath;
        static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        static string _modsDir;

        public static void Log(string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
                if (_logPath != null) File.AppendAllText(_logPath, line, new UTF8Encoding(false));
            }
            catch { }
        }

        // ------------------------------------------------------------ 설정 파일 (꺼둔 모드 목록)
        public static HashSet<string> ReadDisabled()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = Path.Combine(_modsDir, ConfigName);
                if (!File.Exists(p)) return set;
                foreach (var raw in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("disabled=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(9).Trim();
                        if (v.Length > 0) set.Add(v);
                    }
                }
            }
            catch (Exception e) { Log("설정 파일 읽기 실패: " + e.Message); }
            return set;
        }

        public static bool SaveConfig()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Spark in the Dark 모드 로더 설정");
                sb.AppendLine("# 게임 메인 메뉴의 [모드] 화면에서 자동으로 관리됩니다.");
                sb.AppendLine("# disabled=파일이름.dll 줄이 있으면 그 모드는 다음 실행부터 꺼집니다.");
                foreach (var m in Mods)
                    if (!m.EnabledInConfig) sb.AppendLine("disabled=" + m.File);
                File.WriteAllText(Path.Combine(_modsDir, ConfigName), sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e) { Log("설정 저장 실패: " + e.Message); return false; }
        }

        public static void Run()
        {
            string exe = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            if (string.IsNullOrEmpty(exe)) return;
            string gameDir = Path.GetDirectoryName(exe);
            _modsDir = Path.Combine(gameDir, "Mods");
            if (!Directory.Exists(_modsDir)) return;
            _logPath = Path.Combine(_modsDir, "ModLoader.log");
            try { File.WriteAllText(_logPath, "", new UTF8Encoding(false)); } catch { }
            Log("Spark in the Dark 모드 로더 v" + Version);

            string managed = Path.Combine(Path.Combine(gameDir, Path.GetFileNameWithoutExtension(exe) + "_Data"), "Managed");
            if (!Directory.Exists(managed)) managed = Environment.GetEnvironmentVariable("DOORSTOP_MANAGED_FOLDER_DIR");
            string acsPath = Path.Combine(managed ?? "", "Assembly-CSharp.dll");
            if (!File.Exists(acsPath)) { Log("Assembly-CSharp.dll 을 찾지 못함: " + acsPath); return; }

            string selfPath = SafeFull(Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH") ?? "");
            var modFiles = Directory.GetFiles(_modsDir, "*.dll")
                .Where(f => !string.Equals(Path.GetFullPath(f), selfPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
            HashSet<string> disabled = ReadDisabled();

            Loaded[typeof(Loader).Assembly.GetName().Name] = typeof(Loader).Assembly;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);
            resolver.AddSearchDirectory(_modsDir);
            string selfDir = Path.GetDirectoryName(selfPath);
            if (!string.IsNullOrEmpty(selfDir)) resolver.AddSearchDirectory(selfDir);
            var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };

            ModuleDefinition game = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(acsPath)), rp);
            var targets = new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase) { { "Assembly-CSharp", game } };
            var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<string, ModuleDefinition> getTarget = name =>
            {
                name = string.IsNullOrEmpty(name) ? "Assembly-CSharp" : name;
                ModuleDefinition md;
                if (targets.TryGetValue(name, out md)) return md;
                string p = Path.Combine(managed, name + ".dll");
                if (!File.Exists(p)) throw new Exception("대상 어셈블리 없음: " + name);
                md = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(p)), rp);
                targets[name] = md;
                return md;
            };
            var modNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modModules = new List<KeyValuePair<ModEntry, ModuleDefinition>>();
            foreach (var f in modFiles)
            {
                var entry = new ModEntry { File = Path.GetFileName(f), Name = Path.GetFileNameWithoutExtension(f), FullPath = f };
                entry.EnabledInConfig = !disabled.Contains(entry.File);
                Mods.Add(entry);
                try
                {
                    var mm = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(f)), rp);
                    ReadInfo(mm, entry);
                    entry.HasSettings = mm.GetTypes().Any(t => t.Fields.Any(fd => fd.IsStatic && fd.CustomAttributes.Any(ca => ca.AttributeType.Name == "SettingAttribute")));
                    modModules.Add(new KeyValuePair<ModEntry, ModuleDefinition>(entry, mm));
                    modNames.Add(mm.Assembly.Name.Name);
                }
                catch (Exception e)
                {
                    entry.State = ModState.Unreadable;
                    entry.Errors.Add("DLL 을 읽을 수 없음: " + e.Message);
                    Log("[건너뜀] " + entry.File + " : DLL 을 읽을 수 없음 (" + e.Message + ")");
                }
            }

            var already = game.AssemblyReferences.FirstOrDefault(r => modNames.Contains(r.Name) || r.Name == typeof(Loader).Assembly.GetName().Name);
            if (already != null)
            {
                Log("Assembly-CSharp.dll 이 이미 다른 방식으로 수정되어 있습니다 (" + already.Name + " 참조).");
                Log("스팀에서 '게임 파일 무결성 검사'로 원본을 복구한 뒤 다시 실행하세요. 이번 실행에서는 모드를 적용하지 않습니다.");
                return;
            }

            // 로더 공용 저장 기능 (모드들이 세이브에 자기 데이터를 붙일 수 있게)
            ModuleDefinition selfModule = null;
            bool saveOk = false;
            try
            {
                selfModule = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(selfPath)), rp);
                var se = selfModule.GetType("ModLoader.SaveExt");
                var specs = new[]
                {
                    new HookSpec { Hook = se.Methods.First(m => m.Name == "HookGetSaveData"), Kind = HookKind.ReplaceCall, Type = "Management.SaveGameSystem", Method = "SaveGame", Params = 1, CallType = "Management.ISaveable", CallMethod = "GetSaveData", CallParams = 0, Count = 1 },
                    new HookSpec { Hook = se.Methods.First(m => m.Name == "HookToJson"), Kind = HookKind.ReplaceCall, Type = "Management.SaveGameSystem", Method = "SaveGame", Params = 1, CallType = "UnityEngine.JsonUtility", CallMethod = "ToJson", CallParams = 1, Count = 1 },
                    new HookSpec { Hook = se.Methods.First(m => m.Name == "HookLoadSaveData"), Kind = HookKind.ReplaceCall, Type = "Management.SaveGameSystem", Method = "LoadObjectFromSaveData", Params = 3, CallType = "Management.ISaveable", CallMethod = "LoadSaveData", CallParams = 1, Count = 1 },
                };
                var plansSave = specs.Select(sp => Patcher.Prepare(game, sp)).ToList();
                foreach (var pl in plansSave) Patcher.Apply(pl);
                dirty.Add("Assembly-CSharp");
                saveOk = true;
            }
            catch (Exception e) { Log("공용 저장 기능을 연결하지 못했습니다 (모드 데이터가 저장되지 않을 수 있음): " + e.Message); }

            var applied = new List<string>();
            foreach (var kv in modModules)
            {
                ModEntry entry = kv.Key;
                ModuleDefinition mm = kv.Value;
                string title = entry.Title + " (" + entry.File + ")";
                if (mm.Assembly.Name.Name == "SparkMod")
                {
                    entry.State = ModState.Failed;
                    entry.Notice = "예전 이름의 파일입니다. Modifiers.dll 로 바뀌었으니 이 파일(SparkMod.dll)은 삭제해 주세요.";
                    entry.Errors.Add(entry.Notice);
                    Log("[건너뜀] " + title + " : " + entry.Notice);
                    continue;
                }
                if (!entry.EnabledInConfig)
                {
                    entry.State = ModState.Disabled;
                    Log("[꺼짐] " + title);
                    continue;
                }
                List<HookSpec> hooks;
                try { hooks = CollectHooks(mm); }
                catch (Exception e)
                {
                    entry.State = ModState.Failed;
                    entry.Errors.Add("훅 정보 오류: " + e.Message);
                    Log("[건너뜀] " + title + " : 훅 정보 오류 - " + e.Message);
                    continue;
                }
                var plans = new List<HookPlan>();
                foreach (var h in hooks)
                {
                    try { plans.Add(Patcher.Prepare(getTarget(h.Assembly), h)); }
                    catch (Exception e) { entry.Errors.Add(h.Describe() + " → " + e.Message); }
                }
                if (entry.Errors.Count > 0)
                {
                    entry.State = ModState.Failed;
                    Log("[건너뜀] " + title + " : 게임 버전과 맞지 않는 훅이 있어 이 모드를 적용하지 않습니다.");
                    foreach (var er in entry.Errors) Log("    - " + er);
                    continue;
                }
                foreach (var p in plans) { Patcher.Apply(p); dirty.Add(p.Game.Assembly.Name.Name); }
                entry.State = ModState.Applied;
                applied.Add(Path.Combine(_modsDir, entry.File));
                Log("[적용] " + title + " (훅 " + plans.Count + "개)");
            }

            // 메인 메뉴에 [모드] 버튼을 붙이는 로더 자체 훅
            bool menuOk = false;
            try
            {
                var self = selfModule ?? ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(selfPath)), rp);
                var attach = self.GetType("ModLoader.ModMenu").Methods.First(m => m.Name == "Attach");
                var spec = new HookSpec { Hook = attach, Kind = HookKind.Postfix, Type = "UI.Screens.StartMenu", Method = "Start", Params = 0, Args = new[] { 0 } };
                Patcher.Apply(Patcher.Prepare(game, spec));
                dirty.Add("Assembly-CSharp");
                menuOk = true;
            }
            catch (Exception e) { Log("모드 메뉴를 메인 메뉴에 붙이지 못했습니다: " + e.Message); }

            if (applied.Count == 0 && !menuOk && !saveOk) { Log("적용된 모드가 없어 원본 그대로 실행합니다."); return; }

            // Assembly-CSharp 가 참조하는 다른 어셈블리(예: UnityEngine.UI)를 먼저 올린다
            string dump = Environment.GetEnvironmentVariable("SPARK_MODLOADER_DUMP");
            foreach (var name in dirty.OrderBy(n => n.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                var ms = new MemoryStream();
                targets[name].Write(ms);
                if (!string.IsNullOrEmpty(dump))
                    File.WriteAllBytes(name == "Assembly-CSharp" ? dump : Path.Combine(Path.GetDirectoryName(dump), name + ".patched.dll"), ms.ToArray());
                Assembly patched = Assembly.Load(ms.ToArray());
                Loaded[patched.GetName().Name] = patched;
                if (name != "Assembly-CSharp") Log("메모리 패치: " + name);
            }
            foreach (var f in applied)
            {
                Assembly a = Assembly.LoadFrom(f);
                Loaded[a.GetName().Name] = a;
                ModEntry e = Mods.First(x => x.FullPath == f);
                e.Assembly = a;
                if (e.HasSettings)
                {
                    try { ModSettings.For(e); }
                    catch (Exception ex) { Log("[설정] " + e.File + " 설정 적용 실패: " + ex.Message); }
                }
                CallModInit(e, a);
            }
            Log("완료: 모드 " + applied.Count + "개 로드됨" + (menuOk ? " / 메인 메뉴에 [모드] 버튼 추가" : ""));
        }

        static void CallModInit(ModEntry e, Assembly a)
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    bool has = false;
                    try
                    {
                        foreach (var d in m.GetCustomAttributesData()) if (d.Constructor.DeclaringType.Name == "ModInitAttribute") { has = true; break; }
                        if (has && m.GetParameters().Length != 0) has = false;
                    }
                    catch { has = false; }
                    if (!has) continue;
                    try { m.Invoke(null, null); }
                    catch (Exception ex) { Log("[초기화] " + e.File + " " + t.Name + "." + m.Name + " 실패: " + (ex.InnerException ?? ex).Message); }
                }
        }

        static string SafeFull(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p); } catch { return p; }
        }

        // 꺼져 있는 모드의 설정 화면을 열 때만 사용 (훅은 적용하지 않음)
        public static Assembly GetOrLoadAssembly(ModEntry e)
        {
            if (e.Assembly != null) return e.Assembly;
            e.Assembly = Assembly.LoadFrom(e.FullPath);
            return e.Assembly;
        }

        static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                Assembly a;
                if (Loaded.TryGetValue(name, out a)) return a;
                if (_modsDir != null)
                {
                    string p = Path.Combine(_modsDir, name + ".dll");
                    if (File.Exists(p)) { a = Assembly.LoadFrom(p); Loaded[name] = a; return a; }
                }
            }
            catch { }
            return null;
        }

        static void ReadInfo(ModuleDefinition mm, ModEntry e)
        {
            var ca = mm.Assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "ModInfoAttribute");
            if (ca == null) return;
            if (ca.ConstructorArguments.Count >= 2)
            {
                e.Name = (string)ca.ConstructorArguments[0].Value ?? e.Name;
                e.Version = (string)ca.ConstructorArguments[1].Value;
            }
            foreach (var na in ca.Fields.Concat(ca.Properties))
            {
                if (na.Name == "Author") e.Author = (string)na.Argument.Value;
                else if (na.Name == "Description") e.Description = (string)na.Argument.Value;
                else if (na.Name == "SettingsNote") e.SettingsNote = (string)na.Argument.Value;
            }
        }

        static List<HookSpec> CollectHooks(ModuleDefinition mm)
        {
            var list = new List<HookSpec>();
            foreach (var t in mm.GetTypes())
                foreach (var m in t.Methods)
                    foreach (var ca in m.CustomAttributes)
                    {
                        if (ca.AttributeType.Name != "HookAttribute") continue;
                        var h = new HookSpec { Hook = m };
                        h.Kind = (HookKind)Convert.ToInt32(ca.ConstructorArguments[0].Value);
                        h.Type = (string)ca.ConstructorArguments[1].Value;
                        h.Method = (string)ca.ConstructorArguments[2].Value;
                        foreach (var na in ca.Fields.Concat(ca.Properties))
                        {
                            object v = na.Argument.Value;
                            switch (na.Name)
                            {
                                case "Params": h.Params = Convert.ToInt32(v); break;
                                case "ParamTypes": h.ParamTypes = (string)v; break;
                                case "Args": h.Args = ((CustomAttributeArgument[])v).Select(x => Convert.ToInt32(x.Value)).ToArray(); break;
                                case "ArgIndex": h.ArgIndex = Convert.ToInt32(v); break;
                                case "CallType": h.CallType = (string)v; break;
                                case "CallMethod": h.CallMethod = (string)v; break;
                                case "CallParams": h.CallParams = Convert.ToInt32(v); break;
                                case "Count": h.Count = Convert.ToInt32(v); break;
                                case "Assembly": h.Assembly = (string)v; break;
                            }
                        }
                        if (!m.IsStatic) throw new Exception(m.Name + " 은 static 이어야 합니다");
                        list.Add(h);
                    }
            return list;
        }
    }

    public class HookSpec
    {
        public MethodDefinition Hook;
        public HookKind Kind;
        public string Type, Method, ParamTypes, CallType, CallMethod, Assembly;
        public int Params = -1, ArgIndex = 1, CallParams = -1, Count = 1;
        public int[] Args;

        public string Describe()
        {
            return Kind + " " + Type + "." + Method + (Kind == HookKind.ReplaceCall ? " (" + CallType + "." + CallMethod + ")" : "") + " ← " + Hook.Name;
        }
    }

    public class HookPlan
    {
        public HookSpec Spec;
        public ModuleDefinition Game;
        public MethodDefinition Target;
        public MethodReference HookRef;
        public int[] Args;
    }

    public static class Patcher
    {
        // ------------------------------------------------------------ 준비(검증만, 수정 없음)
        public static HookPlan Prepare(ModuleDefinition game, HookSpec s)
        {
            TypeDefinition type = game.GetType(s.Type);
            if (type == null) throw new Exception("타입 없음");
            var cands = type.Methods.Where(m => m.Name == s.Method && m.HasBody
                && (s.Params < 0 || m.Parameters.Count == s.Params)
                && (s.ParamTypes == null || ParamTypesMatch(m, s.ParamTypes))).ToList();
            if (cands.Count != 1) throw new Exception("대상 메서드 " + cands.Count + "개 일치");
            var target = cands[0];

            int[] args = s.Args ?? DefaultArgs(target, s.Kind);
            int maxArg = target.Parameters.Count + (target.HasThis ? 0 : -1);
            foreach (int a in args) if (a < 0 || a > maxArg) throw new Exception("인자 번호 범위 밖: " + a);

            var plan = new HookPlan { Spec = s, Game = game, Target = target, Args = args, HookRef = ImportHook(game, s.Hook) };
            var hookParams = plan.HookRef.Parameters.Select(p => p.ParameterType.FullName).ToList();
            string hookRet = plan.HookRef.ReturnType.FullName;
            var argTypes = args.Select(a => ArgType(target, a).FullName).ToList();
            string targetRet = target.ReturnType.FullName;

            switch (s.Kind)
            {
                case HookKind.Prefix:
                    Expect(hookRet == "System.Void", "훅은 void 여야 함");
                    ExpectParams(hookParams, argTypes);
                    break;
                case HookKind.PrefixSkip:
                    Expect(hookRet == "System.Boolean", "훅은 bool 을 반환해야 함");
                    Expect(targetRet == "System.Void", "대상이 void 가 아님");
                    ExpectParams(hookParams, argTypes);
                    break;
                case HookKind.PrefixReturn:
                    Expect(hookRet == targetRet, "반환 형식 불일치");
                    Expect(hookRet == "System.Single" || hookRet == "System.Double" || !target.ReturnType.IsValueType, "null/NaN 으로 판단할 수 없는 반환 형식");
                    ExpectParams(hookParams, argTypes);
                    break;
                case HookKind.PrefixSkipReturn:
                    Expect(hookRet == "System.Boolean", "훅은 bool 을 반환해야 함");
                    Expect(targetRet != "System.Void", "대상이 void 임 (PrefixSkip 을 쓰세요)");
                    ExpectParams(hookParams, argTypes.Concat(new[] { targetRet + "&" }).ToList());
                    break;
                case HookKind.PrefixArg:
                    Expect(s.ArgIndex >= (target.HasThis ? 1 : 0) && s.ArgIndex <= maxArg, "ArgIndex 범위 밖");
                    Expect(hookRet == ArgType(target, s.ArgIndex).FullName, "반환 형식이 인자 형식과 다름");
                    ExpectParams(hookParams, argTypes);
                    break;
                case HookKind.Postfix:
                    if (targetRet == "System.Void")
                    {
                        Expect(hookRet == "System.Void", "훅은 void 여야 함");
                        ExpectParams(hookParams, argTypes);
                    }
                    else
                    {
                        Expect(hookRet == targetRet, "반환 형식 불일치");
                        ExpectParams(hookParams, new[] { targetRet }.Concat(argTypes).ToList());
                    }
                    break;
                case HookKind.ReplaceCall:
                    {
                        var hits = FindCalls(target, s);
                        if (s.Count > 0) Expect(hits.Count == s.Count, "호출 " + hits.Count + "곳 발견 (예상 " + s.Count + ")");
                        else Expect(hits.Count > 0, "호출을 찾지 못함");
                        var mr = (MethodReference)hits[0].Operand;
                        var stack = new List<string>();
                        if (mr.HasThis) stack.Add(mr.DeclaringType.FullName);
                        stack.AddRange(mr.Parameters.Select(p => p.ParameterType.FullName));
                        Expect(hookRet == mr.ReturnType.FullName, "반환 형식 불일치");
                        ExpectParams(hookParams, stack.Concat(argTypes).ToList());
                        break;
                    }
            }
            return plan;
        }

        static bool ParamTypesMatch(MethodDefinition m, string types)
        {
            var names = types.Split(',').Select(x => x.Trim()).ToArray();
            if (names.Length != m.Parameters.Count) return false;
            for (int i = 0; i < names.Length; i++)
                if (m.Parameters[i].ParameterType.Name != names[i] && m.Parameters[i].ParameterType.FullName != names[i]) return false;
            return true;
        }

        static int[] DefaultArgs(MethodDefinition m, HookKind kind)
        {
            if (kind == HookKind.ReplaceCall) return new int[0];
            if (kind == HookKind.Postfix) return m.HasThis ? new[] { 0 } : new int[0];
            int start = m.HasThis ? 0 : 0;
            int count = m.Parameters.Count + (m.HasThis ? 1 : 0);
            return Enumerable.Range(start, count).ToArray();
        }

        static TypeReference ArgType(MethodDefinition m, int index)
        {
            if (m.HasThis) return index == 0 ? (TypeReference)m.DeclaringType : m.Parameters[index - 1].ParameterType;
            return m.Parameters[index].ParameterType;
        }

        static void Expect(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        static void ExpectParams(List<string> actual, List<string> expected)
        {
            if (actual.Count != expected.Count)
                throw new Exception("훅 인자 개수 " + actual.Count + " (필요 " + expected.Count + ")");
            for (int i = 0; i < actual.Count; i++)
                if (actual[i] != expected[i] && expected[i] != "System.Object" && actual[i] != "System.Object")
                {
                    // 상속 관계(예: Weapon 을 HandItem 으로 받기)는 허용
                    if (!IsAssignable(actual[i], expected[i]))
                        throw new Exception("훅 인자 " + i + " 형식 " + actual[i] + " ≠ " + expected[i]);
                }
        }

        static ModuleDefinition _gameForTypes;
        static bool IsAssignable(string to, string from)
        {
            if (_gameForTypes == null) return false;
            TypeDefinition t = _gameForTypes.GetType(from);
            while (t != null)
            {
                if (t.FullName == to) return true;
                if (t.Interfaces.Any(i => i.InterfaceType.FullName == to)) return true;
                if (t.BaseType == null) break;
                t = t.BaseType.Scope == _gameForTypes ? _gameForTypes.GetType(t.BaseType.FullName) : null;
                if (t == null) return to == "UnityEngine.MonoBehaviour" || to == "UnityEngine.Component" || to == "UnityEngine.Object";
            }
            return false;
        }

        static List<Instruction> FindCalls(MethodDefinition target, HookSpec s)
        {
            return target.Body.Instructions.Where(i => (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt)
                && i.Operand is MethodReference mr && mr.Name == s.CallMethod
                && (mr.DeclaringType.FullName == s.CallType || mr.DeclaringType.Name == s.CallType)
                && (s.CallParams < 0 || mr.Parameters.Count == s.CallParams)).ToList();
        }

        static TypeReference MapType(ModuleDefinition game, TypeReference t)
        {
            if (t is ByReferenceType br) return new ByReferenceType(MapType(game, br.ElementType));
            if (t is ArrayType at) return new ArrayType(MapType(game, at.ElementType), at.Rank);
            if (t is GenericInstanceType git)
            {
                var g = new GenericInstanceType(MapType(game, git.ElementType));
                foreach (var ga in git.GenericArguments) g.GenericArguments.Add(MapType(game, ga));
                return g;
            }
            string scopeName = t.Scope is AssemblyNameReference an ? an.Name : (t.Scope is ModuleDefinition md ? md.Assembly.Name.Name : null);
            if (scopeName == game.Assembly.Name.Name)
            {
                var def = game.GetType(t.FullName);
                if (def == null) throw new Exception("게임에 없는 형식: " + t.FullName);
                return def;
            }
            return game.ImportReference(t);
        }

        static MethodReference ImportHook(ModuleDefinition game, MethodDefinition hook)
        {
            _gameForTypes = game;
            var mr = new MethodReference(hook.Name, MapType(game, hook.ReturnType), game.ImportReference(hook.DeclaringType)) { HasThis = false };
            foreach (var p in hook.Parameters) mr.Parameters.Add(new ParameterDefinition(MapType(game, p.ParameterType)));
            return mr;
        }

        // ------------------------------------------------------------ 적용
        public static void Apply(HookPlan p)
        {
            var m = p.Target;
            m.Body.SimplifyMacros();
            var il = m.Body.GetILProcessor();
            switch (p.Spec.Kind)
            {
                case HookKind.Prefix:
                    {
                        var seq = LoadArgs(m, p.Args);
                        seq.Add(il.Create(OpCodes.Call, p.HookRef));
                        InsertPrefix(m, seq);
                        break;
                    }
                case HookKind.PrefixSkip:
                    {
                        var first = m.Body.Instructions[0];
                        var seq = LoadArgs(m, p.Args);
                        seq.Add(il.Create(OpCodes.Call, p.HookRef));
                        seq.Add(il.Create(OpCodes.Brfalse, first));
                        seq.Add(il.Create(OpCodes.Ret));
                        InsertPrefix(m, seq);
                        break;
                    }
                case HookKind.PrefixReturn:
                    {
                        var first = m.Body.Instructions[0];
                        var seq = LoadArgs(m, p.Args);
                        seq.Add(il.Create(OpCodes.Call, p.HookRef));
                        string rt = m.ReturnType.FullName;
                        if (rt == "System.Single" || rt == "System.Double")
                        {
                            var v = new VariableDefinition(m.ReturnType);
                            m.Body.Variables.Add(v);
                            m.Body.InitLocals = true;
                            seq.Add(il.Create(OpCodes.Stloc, v));
                            seq.Add(il.Create(OpCodes.Ldloc, v));
                            seq.Add(il.Create(OpCodes.Ldloc, v));
                            seq.Add(il.Create(OpCodes.Bne_Un, first));   // NaN 이면 원본 진행
                            seq.Add(il.Create(OpCodes.Ldloc, v));
                            seq.Add(il.Create(OpCodes.Ret));
                        }
                        else
                        {
                            var pop = il.Create(OpCodes.Pop);
                            seq.Add(il.Create(OpCodes.Dup));
                            seq.Add(il.Create(OpCodes.Brfalse, pop));
                            seq.Add(il.Create(OpCodes.Ret));
                            seq.Add(pop);
                        }
                        InsertPrefix(m, seq);
                        break;
                    }
                case HookKind.PrefixSkipReturn:
                    {
                        var first = m.Body.Instructions[0];
                        var v = new VariableDefinition(m.ReturnType);
                        m.Body.Variables.Add(v);
                        m.Body.InitLocals = true;
                        var seq = LoadArgs(m, p.Args);
                        seq.Add(il.Create(OpCodes.Ldloca, v));
                        seq.Add(il.Create(OpCodes.Call, p.HookRef));
                        seq.Add(il.Create(OpCodes.Brfalse, first));
                        seq.Add(il.Create(OpCodes.Ldloc, v));
                        seq.Add(il.Create(OpCodes.Ret));
                        InsertPrefix(m, seq);
                        break;
                    }
                case HookKind.PrefixArg:
                    {
                        var seq = LoadArgs(m, p.Args);
                        seq.Add(il.Create(OpCodes.Call, p.HookRef));
                        seq.Add(il.Create(OpCodes.Starg, ParamOf(m, p.Spec.ArgIndex)));
                        InsertPrefix(m, seq);
                        break;
                    }
                case HookKind.Postfix:
                    {
                        var rets = m.Body.Instructions.Where(i => i.OpCode.Code == Code.Ret).ToList();
                        foreach (var r in rets)
                        {
                            // ret 명령 자리를 재사용해 분기 대상을 유지한다
                            var seq = LoadArgs(m, p.Args);
                            seq.Add(il.Create(OpCodes.Call, p.HookRef));
                            seq.Add(il.Create(OpCodes.Ret));
                            ReplaceInPlace(il, r, seq);
                        }
                        break;
                    }
                case HookKind.ReplaceCall:
                    {
                        foreach (var ins in FindCalls(m, p.Spec))
                        {
                            var seq = LoadArgs(m, p.Args);
                            seq.Add(il.Create(OpCodes.Call, p.HookRef));
                            ReplaceInPlace(il, ins, seq);
                        }
                        break;
                    }
            }
            m.Body.OptimizeMacros();
        }

        static void ReplaceInPlace(ILProcessor il, Instruction at, List<Instruction> seq)
        {
            at.OpCode = seq[0].OpCode;
            at.Operand = seq[0].Operand;
            var prev = at;
            for (int k = 1; k < seq.Count; k++) { il.InsertAfter(prev, seq[k]); prev = seq[k]; }
        }

        static void InsertPrefix(MethodDefinition m, List<Instruction> seq)
        {
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            foreach (var ins in seq) il.InsertBefore(first, ins);
        }

        static ParameterDefinition ParamOf(MethodDefinition m, int index)
        {
            return m.HasThis ? m.Parameters[index - 1] : m.Parameters[index];
        }

        static List<Instruction> LoadArgs(MethodDefinition m, int[] args)
        {
            var list = new List<Instruction>();
            foreach (int a in args)
            {
                if (m.HasThis && a == 0) list.Add(Instruction.Create(OpCodes.Ldarg_0));
                else list.Add(Instruction.Create(OpCodes.Ldarg, ParamOf(m, a)));
            }
            return list;
        }
    }
}
