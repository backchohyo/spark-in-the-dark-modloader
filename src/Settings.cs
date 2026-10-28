// 모드 설정: 모드는 static 필드에 [Setting] 을 붙이기만 하면 된다.
// 로더가 Mods\config\<모드파일이름>.cfg 에 저장하고, 게임 시작 시/메뉴에서 바꿀 때 필드에 바로 넣어준다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ModLoader
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SettingAttribute : Attribute
    {
        public string Section;      // 묶음 제목
        public string Label;        // 표시 이름
        public string Desc;         // 마우스를 올렸을 때 아래에 표시할 설명
        public string Unit;         // 단위 (예: "%", "개")
        public float Min = 0f, Max = 100f, Step = 1f;
        public bool Inline;         // true 면 바로 앞 설정과 같은 줄에 표시
        public string Options;      // int 필드용 선택지 "가|나|다" (값 0,1,2)
        public SettingAttribute(string section, string label) { Section = section; Label = label; }
    }

    public class SettingField
    {
        public FieldInfo Field;
        public string Key, Section, Label, Desc, Unit;
        public float Min, Max, Step;
        public bool Inline;
        public object Default;
        public string[] Options;

        public bool IsBool { get { return Field.FieldType == typeof(bool); } }
        public bool IsInt { get { return Field.FieldType == typeof(int); } }

        public object Get() { return Field.GetValue(null); }

        public float GetNumber()
        {
            object v = Get();
            if (v is int) return (int)v;
            if (v is float) return (float)v;
            if (v is double) return (float)(double)v;
            return 0f;
        }

        public void SetNumber(float v)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (IsInt) Field.SetValue(null, (int)Math.Round(v));
            else if (Field.FieldType == typeof(double)) Field.SetValue(null, (double)v);
            else Field.SetValue(null, (float)Math.Round(v, 4));
        }

        public string Format()
        {
            object v = Get();
            if (v is bool) return (bool)v ? "켬" : "끔";
            float n = GetNumber();
            if (Options != null)
            {
                int i = (int)Math.Round(n);
                return i >= 0 && i < Options.Length ? Options[i] : i.ToString(CultureInfo.InvariantCulture);
            }
            string s = IsInt || Step >= 1f ? ((int)Math.Round(n)).ToString(CultureInfo.InvariantCulture)
                                           : n.ToString("0.0##", CultureInfo.InvariantCulture);
            return s + (Unit ?? "");
        }

        public string Serialize()
        {
            object v = Get();
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public bool Parse(string s)
        {
            s = s.Trim();
            try
            {
                if (IsBool)
                {
                    bool b;
                    if (bool.TryParse(s, out b)) { Field.SetValue(null, b); return true; }
                    if (s == "1" || s == "0") { Field.SetValue(null, s == "1"); return true; }
                    return false;
                }
                float f;
                if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return false;
                SetNumber(f);
                return true;
            }
            catch { return false; }
        }
    }

    public class ModSettings
    {
        static readonly Dictionary<string, ModSettings> Cache = new Dictionary<string, ModSettings>(StringComparer.OrdinalIgnoreCase);

        public ModEntry Entry;
        public List<SettingField> Fields = new List<SettingField>();
        public string Path;
        public bool Dirty;

        public static ModSettings For(ModEntry e)
        {
            ModSettings s;
            if (Cache.TryGetValue(e.File, out s)) return s;
            s = new ModSettings { Entry = e };
            Assembly asm = Loader.GetOrLoadAssembly(e);
            s.Collect(asm);
            string dir = System.IO.Path.Combine(Loader.ModsDir, "config");
            s.Path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(e.File) + ".cfg");
            if (File.Exists(s.Path)) s.Load(); else s.Save();
            Cache[e.File] = s;
            return s;
        }

        void Collect(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (Type t in types.OrderBy(t => t.MetadataToken))
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).OrderBy(f => f.MetadataToken))
                {
                    CustomAttributeData cad = null;
                    foreach (var d in f.GetCustomAttributesData())
                        if (d.Constructor.DeclaringType.Name == "SettingAttribute") { cad = d; break; }
                    if (cad == null) continue;
                    if (f.FieldType != typeof(bool) && f.FieldType != typeof(int) && f.FieldType != typeof(float) && f.FieldType != typeof(double)) continue;
                    var sf = new SettingField { Field = f, Key = f.Name, Min = 0f, Max = 100f, Step = 1f };
                    if (cad.ConstructorArguments.Count >= 2)
                    {
                        sf.Section = cad.ConstructorArguments[0].Value as string;
                        sf.Label = cad.ConstructorArguments[1].Value as string;
                    }
                    foreach (var na in cad.NamedArguments)
                    {
                        object v = na.TypedValue.Value;
                        switch (na.MemberName)
                        {
                            case "Desc": sf.Desc = v as string; break;
                            case "Unit": sf.Unit = v as string; break;
                            case "Min": sf.Min = Convert.ToSingle(v, CultureInfo.InvariantCulture); break;
                            case "Max": sf.Max = Convert.ToSingle(v, CultureInfo.InvariantCulture); break;
                            case "Step": sf.Step = Convert.ToSingle(v, CultureInfo.InvariantCulture); break;
                            case "Inline": sf.Inline = (bool)v; break;
                            case "Section": sf.Section = v as string; break;
                            case "Label": sf.Label = v as string; break;
                            case "Options": sf.Options = ((string)v ?? "").Split('|'); break;
                        }
                    }
                    if (string.IsNullOrEmpty(sf.Label)) sf.Label = f.Name;
                    if (sf.Options != null) { sf.Min = 0; sf.Max = sf.Options.Length - 1; sf.Step = 1; }
                    sf.Default = f.GetValue(null);     // 모드 코드에 적힌 기본값
                    Fields.Add(sf);
                }
            }
        }

        public void Load()
        {
            if (!File.Exists(Path)) return;
            try
            {
                foreach (var raw in File.ReadAllLines(Path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    var f = Fields.FirstOrDefault(x => x.Key == key);
                    if (f != null && !f.Parse(line.Substring(eq + 1))) Loader.Log("[설정] " + Entry.File + " : 잘못된 값 무시 - " + line);
                }
            }
            catch (Exception e) { Loader.Log("[설정] 읽기 실패 " + Path + " : " + e.Message); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                var sb = new StringBuilder();
                sb.AppendLine("# " + Entry.Title + " 설정 - 게임 메인 메뉴 [모드] > [설정] 에서 자동으로 저장됩니다.");
                string section = null, rowLabel = null;
                foreach (var f in Fields)
                {
                    if (f.Section != section) { section = f.Section; sb.AppendLine(); sb.AppendLine("# [" + section + "]"); }
                    if (!f.Inline) rowLabel = f.Label.Replace("  ", " ");
                    string label = f.Inline ? rowLabel + " - " + f.Label : f.Label.Replace("  ", " ");
                    string range = f.IsBool ? "true/false" : f.Options != null
                        ? string.Join(", ", f.Options.Select((o, i) => i + "=" + o).ToArray())
                        : (Fmt(f.Min) + "~" + Fmt(f.Max));
                    sb.AppendLine("# " + label + " (" + range + ", 기본값 " + DefaultText(f) + ")");
                    sb.AppendLine(f.Key + "=" + f.Serialize());
                }
                File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));
                Dirty = false;
            }
            catch (Exception e) { Loader.Log("[설정] 저장 실패 " + Path + " : " + e.Message); }
        }

        static string Fmt(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static string DefaultText(SettingField f)
        {
            if (f.Default is bool) return (bool)f.Default ? "true" : "false";
            return Convert.ToString(f.Default, CultureInfo.InvariantCulture);
        }

        public bool IsDefault(SettingField f)
        {
            return Equals(f.Get(), f.Default);
        }

        public void ResetDefaults()
        {
            foreach (var f in Fields) f.Field.SetValue(null, f.Default);
            Dirty = true;
        }

        public static void SaveAllDirty()
        {
            foreach (var s in Cache.Values) if (s.Dirty) s.Save();
        }
    }
}
