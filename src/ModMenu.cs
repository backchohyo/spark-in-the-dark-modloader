// 메인 메뉴의 [모드] 버튼과 모드 관리 화면
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ModLoader
{
    public static class ModMenu
    {
        // UI.Screens.StartMenu.Start() 끝에서 호출된다 (로더가 메모리 패치로 연결)
        public static void Attach(MonoBehaviour startMenu)
        {
            try { PerfMonitor.Ensure(); } catch { }
            try
            {
                GameObject menuObj = GetField<GameObject>(startMenu, "MenuObj");
                if (!menuObj) { Loader.Log("모드 메뉴: MenuObj 를 찾지 못함"); return; }

                Button anchor = null;
                foreach (var b in menuObj.GetComponentsInChildren<Button>(true))
                    if (b.name == "btnSettings") { anchor = b; break; }
                if (!anchor) anchor = GetField<Button>(startMenu, "AchievementsButton");
                if (!anchor) { Loader.Log("모드 메뉴: 기준 버튼을 찾지 못함"); return; }

                Transform parent = anchor.transform.parent;
                if (parent.Find("btnMods")) return;

                GameObject go = UnityEngine.Object.Instantiate(anchor.gameObject, parent, false);
                go.name = "btnMods";
                foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if (c && c.GetType().Name == "LocalizedText") UnityEngine.Object.DestroyImmediate(c);
                go.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex() + 1);
                go.SetActive(true);

                Text label = go.GetComponentInChildren<Text>(true);
                if (label) label.text = "모드";

                var host = new GameObject("ModLoaderMenu");
                var ui = host.AddComponent<ModMenuBehaviour>();
                ui.Init(startMenu, menuObj, label ? label.font : null);

                Button btn = go.GetComponent<Button>();
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(ui.Open);
            }
            catch (Exception e) { Loader.Log("모드 메뉴 오류: " + e); }
        }

        internal static T GetField<T>(object o, string name) where T : class
        {
            if (o == null) return null;
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o) as T;
        }

        internal static Color GetColor(object o, string name, Color def)
        {
            FieldInfo f = o == null ? null : o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f != null && f.FieldType == typeof(Color) ? (Color)f.GetValue(o) : def;
        }
    }

    public class ModMenuBehaviour : MonoBehaviour
    {
        MonoBehaviour _startMenu;
        GameObject _menuObj;
        Font _font;
        bool _open;
        Vector2 _scroll;
        bool _stylesReady;
        GUIStyle _title, _sub, _name, _status, _desc, _check, _row, _button, _footer;
        Texture2D _bg, _rowHover, _line, _btnBg, _btnHover;
        Color _white = new Color(0.86f, 0.84f, 0.80f), _dark = new Color(0.45f, 0.43f, 0.40f), _highlight = new Color(0.93f, 0.72f, 0.36f);
        static readonly Color Green = new Color(0.55f, 0.80f, 0.45f), Red = new Color(0.90f, 0.40f, 0.35f);

        public void Init(MonoBehaviour startMenu, GameObject menuObj, Font font)
        {
            _startMenu = startMenu;
            _menuObj = menuObj;
            _font = font;
            _white = ModMenu.GetColor(startMenu, "WhiteColor", _white);
            _dark = ModMenu.GetColor(startMenu, "DarkColor", _dark);
            _highlight = ModMenu.GetColor(startMenu, "HighlightColor", _highlight);
        }

        public void Open()
        {
            try
            {
                if (_menuObj) _menuObj.SetActive(false);
                Image logo = ModMenu.GetField<Image>(_startMenu, "LogoImage");
                if (logo) logo.color = _dark;
            }
            catch (Exception e) { Loader.Log("모드 메뉴 열기 오류: " + e.Message); }
            _scroll = Vector2.zero;
            _open = true;
        }

        void Close()
        {
            _open = false;
            try
            {
                MethodInfo show = _startMenu ? _startMenu.GetType().GetMethod("ShowMenu", BindingFlags.Public | BindingFlags.Instance) : null;
                if (show != null) show.Invoke(_startMenu, null);
                else if (_menuObj) _menuObj.SetActive(true);
            }
            catch (Exception e)
            {
                Loader.Log("모드 메뉴 닫기 오류: " + e.Message);
                if (_menuObj) _menuObj.SetActive(true);
            }
        }

        void Update()
        {
            // ESC 등으로 게임이 메뉴를 다시 열었다면 이 화면은 닫는다
            if (_open && _menuObj && _menuObj.activeSelf) _open = false;
        }

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        GUIStyle Style(int size, Color color, FontStyle fs = FontStyle.Normal)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = fs, wordWrap = true, richText = false, clipping = TextClipping.Overflow };
            if (_font) s.font = _font;
            s.normal.textColor = color;
            return s;
        }

        void BuildStyles()
        {
            _bg = Tex(new Color(0.03f, 0.03f, 0.035f, 0.94f));
            _rowHover = Tex(new Color(1f, 1f, 1f, 0.05f));
            _line = Tex(new Color(_dark.r, _dark.g, _dark.b, 0.6f));
            _btnBg = Tex(new Color(1f, 1f, 1f, 0.04f));
            _btnHover = Tex(new Color(1f, 1f, 1f, 0.10f));
            _title = Style(32, _white);
            _sub = Style(17, _dark);
            _name = Style(21, _white);
            _status = Style(16, _dark);
            _desc = Style(16, _dark);
            _footer = Style(16, _highlight);
            _check = Style(22, _highlight);
            _check.alignment = TextAnchor.MiddleCenter;
            _row = new GUIStyle(GUIStyle.none);
            _row.hover.background = _rowHover;
            _button = new GUIStyle(GUI.skin.button) { fontSize = 19, alignment = TextAnchor.MiddleCenter };
            if (_font) _button.font = _font;
            _button.normal.background = _btnBg; _button.normal.textColor = _white;
            _button.hover.background = _btnHover; _button.hover.textColor = _highlight;
            _button.active.background = _btnHover; _button.active.textColor = _highlight;
            _button.border = new RectOffset(0, 0, 0, 0);
            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!_open) return;
            try { DrawMenu(); }
            catch (Exception e) { Loader.Log("모드 메뉴 그리기 오류: " + e); _open = false; if (_menuObj) _menuObj.SetActive(true); }
        }

        ModSettings _page;          // null 이면 모드 목록, 아니면 해당 모드 설정 화면
        Vector2 _setScroll;
        string _hoverDesc;
        GUIStyle _section, _small, _value, _valueChanged, _mini;

        void DrawMenu()
        {
            if (!_stylesReady) BuildStyles();
            if (_section == null)
            {
                _section = Style(18, _highlight);
                _small = Style(16, _white);
                _value = Style(17, _white); _value.alignment = TextAnchor.MiddleCenter; _value.wordWrap = false;
                _valueChanged = new GUIStyle(_value); _valueChanged.normal.textColor = _highlight;
                _mini = new GUIStyle(_button) { fontSize = 18 };
            }
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                ev.Use();
                if (_page != null) ClosePage(); else Close();
                return;
            }

            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float vw = Screen.width / scale, vh = 1080f;
            float pw = Mathf.Min(_page != null ? 1000f : 900f, vw - 80f), ph = _page != null ? 860f : 680f;
            var panel = new Rect((vw - pw) / 2f, (vh - ph) / 2f, pw, ph);
            GUI.DrawTexture(panel, _bg);
            GUI.DrawTexture(new Rect(panel.x, panel.y, pw, 1), _line);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1, pw, 1), _line);

            if (_page != null) DrawSettings(panel, pw);
            else DrawList(panel, pw);
        }

        void DrawList(Rect panel, float pw)
        {
            float x = panel.x + 44f, w = pw - 88f, y = panel.y + 32f;
            GUI.Label(new Rect(x, y, w, 50f), "모드 관리", _title); y += 52f;
            GUI.Label(new Rect(x, y, w, 30f), "체크한 모드만 켜집니다. 켜고 끄는 것은 게임을 다시 시작하면 적용됩니다.", _sub); y += 38f;
            GUI.DrawTexture(new Rect(x, y, w, 1), _line); y += 10f;

            float listH = panel.yMax - 130f - y;
            var mods = Loader.Mods;
            if (mods.Count == 0)
            {
                GUI.Label(new Rect(x, y + 20f, w, 70f), "Mods 폴더에 모드가 없습니다.\n모드 DLL 파일을 Mods 폴더에 넣고 게임을 다시 시작하세요.", _desc);
            }
            else
            {
                float rowH = 100f;
                var view = new Rect(0, 0, w - 24f, rowH * mods.Count);
                _scroll = GUI.BeginScrollView(new Rect(x, y, w, listH), _scroll, view);
                for (int i = 0; i < mods.Count; i++)
                {
                    ModEntry m = mods[i];
                    var r = new Rect(0, i * rowH, view.width, rowH - 8f);
                    bool canSet = m.HasSettings && m.State != ModState.Unreadable;
                    var toggleRect = canSet ? new Rect(r.x, r.y, r.width - 130f, r.height) : r;
                    if (canSet && GUI.Button(new Rect(r.xMax - 112f, r.y + (r.height - 40f) / 2f, 108f, 40f), "설정", _mini))
                    {
                        try { _page = ModSettings.For(m); _setScroll = Vector2.zero; }
                        catch (Exception e) { Loader.Log("[설정] 설정 화면을 열 수 없음: " + e.Message); }
                    }
                    if (GUI.Button(toggleRect, GUIContent.none, _row))
                    {
                        m.EnabledInConfig = !m.EnabledInConfig;
                        Loader.SaveConfig();
                    }
                    GUI.Label(new Rect(r.x + 4f, r.y + 8f, 36f, 36f), m.EnabledInConfig ? "■" : "□", _check);
                    float tx = r.x + 52f, tw = toggleRect.width - 58f;
                    string author = string.IsNullOrEmpty(m.Author) ? "" : "   · " + m.Author;
                    GUI.Label(new Rect(tx, r.y + 6f, tw, 34f), m.Title + author, _name);
                    Color sc; string st = StatusText(m, out sc);
                    var ss = new GUIStyle(_status); ss.normal.textColor = sc;
                    GUI.Label(new Rect(tx, r.y + 38f, tw, 28f), st, ss);
                    string desc = string.IsNullOrEmpty(m.Description) ? m.File : m.Description + "  (" + m.File + ")";
                    GUI.Label(new Rect(tx, r.y + 62f, tw, 28f), desc, _desc);
                    GUI.DrawTexture(new Rect(r.x, r.yMax + 3f, r.width, 1), _line);
                }
                GUI.EndScrollView();
            }

            float by = panel.yMax - 106f;
            bool pending = false;
            foreach (var m in mods) if (IsPending(m)) { pending = true; break; }
            GUI.Label(new Rect(x, by, w, 30f), pending ? "※ 바뀐 설정이 있습니다. 게임을 다시 시작하면 적용됩니다." : "", _footer);
            by += 36f;
            float bw = (w - 20f) / 2f, bh = 44f;
            if (GUI.Button(new Rect(x, by, bw, bh), "모드 폴더 열기", _button))
                Application.OpenURL("file:///" + (Loader.ModsDir ?? "").Replace('\\', '/'));
            if (GUI.Button(new Rect(x + bw + 20f, by, bw, bh), "뒤로", _button))
                Close();
        }

        void ClosePage()
        {
            if (_page != null && _page.Dirty) _page.Save();
            _page = null;
        }

        void OnDisable() { ModSettings.SaveAllDirty(); }
        void OnApplicationQuit() { ModSettings.SaveAllDirty(); }

        // ---------------------------------------------------------------- 설정 화면
        void DrawSettings(Rect panel, float pw)
        {
            ModSettings ms = _page;
            float x = panel.x + 44f, w = pw - 88f, y = panel.y + 28f;
            GUI.Label(new Rect(x, y, w, 48f), ms.Entry.Title + " 설정", _title); y += 48f;
            string note = string.IsNullOrEmpty(ms.Entry.SettingsNote) ? "바꾼 값은 자동으로 저장됩니다." : ms.Entry.SettingsNote;
            float nh = Mathf.Max(24f, _sub.CalcHeight(new GUIContent(note), w));
            GUI.Label(new Rect(x, y, w, nh), note, _sub); y += nh + 8f;
            GUI.DrawTexture(new Rect(x, y, w, 1), _line); y += 8f;

            // 줄 단위로 묶기 (Inline 은 앞 줄에 붙음)
            var rows = new List<List<SettingField>>();
            foreach (var f in ms.Fields)
            {
                if (f.Inline && rows.Count > 0 && rows[rows.Count - 1][0].Section == f.Section) rows[rows.Count - 1].Add(f);
                else rows.Add(new List<SettingField> { f });
            }
            const float rowH = 40f, secH = 46f;
            float contentH = 0f; string sec = null;
            foreach (var row in rows)
            {
                if (row[0].Section != sec) { sec = row[0].Section; contentH += secH; }
                contentH += rowH;
            }

            float listH = panel.yMax - 140f - y;
            var view = new Rect(0, 0, w - 24f, contentH + 10f);
            _hoverDesc = null;
            _setScroll = GUI.BeginScrollView(new Rect(x, y, w, listH), _setScroll, view);
            float cy = 0f; sec = null;
            Vector2 mouse = Event.current.mousePosition;
            foreach (var row in rows)
            {
                if (row[0].Section != sec)
                {
                    sec = row[0].Section;
                    GUI.Label(new Rect(0, cy + 12f, view.width, 30f), sec ?? "", _section);
                    GUI.DrawTexture(new Rect(0, cy + secH - 4f, view.width, 1), _line);
                    cy += secH;
                }
                var rr = new Rect(0, cy, view.width, rowH);
                if (rr.Contains(mouse))
                {
                    GUI.DrawTexture(rr, _rowHover);
                    foreach (var f in row) if (!string.IsNullOrEmpty(f.Desc)) { _hoverDesc = f.Desc; break; }
                }
                DrawRow(ms, row, rr);
                cy += rowH;
            }
            GUI.EndScrollView();

            float by = panel.yMax - 118f;
            GUI.Label(new Rect(x, by, w, 44f), _hoverDesc ?? "−/+ 를 누르면 값이 바뀝니다. Shift 를 누른 채로 누르면 10배씩 바뀝니다.", _hoverDesc != null ? _small : _desc);
            by += 50f;
            float bw = (w - 20f) / 2f, bh = 44f;
            if (GUI.Button(new Rect(x, by, bw, bh), "기본값으로 되돌리기", _button)) { ms.ResetDefaults(); ms.Save(); }
            if (GUI.Button(new Rect(x + bw + 20f, by, bw, bh), "뒤로", _button)) ClosePage();
        }

        void DrawRow(ModSettings ms, List<SettingField> row, Rect rr)
        {
            float cx = rr.x + 6f;
            for (int i = 0; i < row.Count; i++)
            {
                SettingField f = row[i];
                bool first = i == 0;
                if (f.IsBool)
                {
                    bool on = (bool)f.Get();
                    float bw = first ? (row.Count == 1 ? rr.width - 12f : 250f) : 190f;
                    var br = new Rect(cx, rr.y + 2f, bw, rr.height - 4f);
                    if (GUI.Button(br, GUIContent.none, _row)) { f.Field.SetValue(null, !on); ms.Dirty = true; ms.Save(); }
                    GUI.Label(new Rect(br.x, br.y + 3f, 30f, 30f), on ? "■" : "□", _check);
                    var ls = new GUIStyle(first ? _small : _desc); ls.normal.textColor = on ? _white : _dark; ls.wordWrap = false;
                    GUI.Label(new Rect(br.x + 34f, br.y + 6f, bw - 36f, 28f), f.Label, ls);
                    cx += bw + 10f;
                }
                else
                {
                    float labelW;
                    if (first && row.Count == 1)
                    {
                        // 단독 숫자 줄: 이름은 왼쪽, 조절 버튼은 오른쪽 끝
                        labelW = rr.width - 12f - (f.Options != null ? 320f : 200f);
                    }
                    else labelW = first ? 214f : 64f;
                    var ls = new GUIStyle(first ? _small : _desc); ls.wordWrap = false;
                    GUI.Label(new Rect(cx, rr.y + 8f, labelW, 28f), f.Label, ls);
                    cx += labelW;
                    if (f.Options != null)
                    {
                        // 선택지: ◀ 이름 ▶ (끝에서 처음으로 순환)
                        float vw2 = 230f;
                        int cur = (int)Math.Round(f.GetNumber()), cnt = f.Options.Length;
                        float bx = first && row.Count == 1 ? rr.xMax - 6f - (vw2 + 68f) : cx;
                        if (GUI.Button(new Rect(bx, rr.y + 5f, 34f, 30f), "◀", _mini)) { f.SetNumber((cur - 1 + cnt) % cnt); ms.Dirty = true; ms.Save(); }
                        GUI.Label(new Rect(bx + 34f, rr.y + 5f, vw2, 30f), f.Format(), ms.IsDefault(f) ? _value : _valueChanged);
                        if (GUI.Button(new Rect(bx + 34f + vw2, rr.y + 5f, 34f, 30f), "▶", _mini)) { f.SetNumber((cur + 1) % cnt); ms.Dirty = true; ms.Save(); }
                        cx = bx + vw2 + 68f + 16f;
                        continue;
                    }
                    float step = f.Step * (Event.current.shift ? 10f : 1f);
                    if (GUI.Button(new Rect(cx, rr.y + 5f, 34f, 30f), "−", _mini)) { f.SetNumber(f.GetNumber() - step); ms.Dirty = true; ms.Save(); }
                    GUI.Label(new Rect(cx + 34f, rr.y + 5f, 98f, 30f), f.Format(), ms.IsDefault(f) ? _value : _valueChanged);
                    if (GUI.Button(new Rect(cx + 132f, rr.y + 5f, 34f, 30f), "+", _mini)) { f.SetNumber(f.GetNumber() + step); ms.Dirty = true; ms.Save(); }
                    cx += 166f + 16f;
                }
            }
        }

        static bool IsPending(ModEntry m)
        {
            if (m.State == ModState.Applied) return !m.EnabledInConfig;
            if (m.State == ModState.Disabled) return m.EnabledInConfig;
            return false;
        }

        string StatusText(ModEntry m, out Color c)
        {
            switch (m.State)
            {
                case ModState.Applied:
                    if (m.EnabledInConfig) { c = Green; return "적용 중"; }
                    c = _highlight; return "다시 시작하면 꺼집니다";
                case ModState.Disabled:
                    if (m.EnabledInConfig) { c = _highlight; return "다시 시작하면 켜집니다"; }
                    c = _dark; return "꺼짐";
                case ModState.Failed:
                    c = Red; return string.IsNullOrEmpty(m.Notice) ? "오류: 현재 게임 버전과 맞지 않아 적용되지 않았습니다 (ModLoader.log 참고)" : m.Notice;
                default:
                    c = Red; return "오류: 모드 파일을 읽을 수 없습니다";
            }
        }
    }
}
