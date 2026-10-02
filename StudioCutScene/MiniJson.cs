using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StudioCutScene
{
    /// <summary>
    /// 自己的小 JSON 解析器，取代 Unity 的 JsonUtility。
    ///
    /// 換掉的理由：JsonUtility 解不出物件陣列時「不報錯、直接給空陣列」，
    /// 症狀是設定檔明明對卻顯示 0 段，完全查不出原因。
    /// 這支解析失敗一定丟出明確訊息（第幾行、什麼字元）。
    ///
    /// 額外支援（JsonUtility 都沒有，但手改設定檔很需要）：
    ///   - // 與 /* */ 註解
    ///   - 陣列 / 物件的結尾多餘逗號
    ///   - UTF-8 BOM
    /// </summary>
    public class JNode
    {
        public const int NUL = 0, OBJ = 1, ARR = 2, STR = 3, NUM = 4, BOOL = 5;

        public int Kind;
        public Dictionary<string, JNode> Obj;
        public List<JNode> Arr;
        public string Str;
        public double Num;
        public bool Bool;

        public static readonly JNode Null = new JNode { Kind = NUL };

        public JNode Get(string key)
        {
            if (Kind != OBJ || Obj == null) return null;
            JNode v;
            return Obj.TryGetValue(key, out v) ? v : null;
        }

        public int Count { get { return Kind == ARR && Arr != null ? Arr.Count : 0; } }
        public JNode At(int i)
        {
            if (Kind != ARR || Arr == null || i < 0 || i >= Arr.Count) return null;
            return Arr[i];
        }

        // --- 取值，缺了就用預設 ---
        public float F(string key, float def)
        {
            var n = Get(key);
            if (n == null) return def;
            if (n.Kind == NUM) return (float)n.Num;
            if (n.Kind == STR) { float f; if (float.TryParse(n.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f; }
            return def;
        }
        public int I(string key, int def)
        {
            var n = Get(key);
            if (n == null || n.Kind != NUM) return def;
            return (int)Math.Round(n.Num);
        }
        public string S(string key, string def)
        {
            var n = Get(key);
            if (n == null) return def;
            if (n.Kind == STR) return n.Str;
            if (n.Kind == NUM) return n.Num.ToString(CultureInfo.InvariantCulture);
            return def;
        }
        public bool B(string key, bool def)
        {
            var n = Get(key);
            if (n == null) return def;
            if (n.Kind == BOOL) return n.Bool;
            if (n.Kind == NUM) return n.Num != 0d;
            return def;
        }
        public string[] SArr(string key)
        {
            var n = Get(key);
            if (n == null || n.Kind != ARR) return new string[0];
            var outp = new string[n.Arr.Count];
            for (int i = 0; i < n.Arr.Count; i++)
            {
                var e = n.Arr[i];
                outp[i] = e != null && e.Kind == STR ? e.Str : "";
            }
            return outp;
        }
    }

    public static class MiniJson
    {
        public static JNode Parse(string text)
        {
            if (text == null) throw new FormatException("內容是空的");
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            var p = new P(text);
            p.SkipWs();
            JNode n = p.Value();
            p.SkipWs();
            if (!p.End) throw p.Err("結尾有多餘內容");
            return n;
        }

        class P
        {
            readonly string s;
            int i;

            public P(string src) { s = src; i = 0; }
            public bool End { get { return i >= s.Length; } }

            public FormatException Err(string msg)
            {
                int line = 1, col = 1;
                for (int k = 0; k < i && k < s.Length; k++)
                {
                    if (s[k] == '\n') { line++; col = 1; }
                    else col++;
                }
                return new FormatException("第 " + line + " 行第 " + col + " 字: " + msg);
            }

            public void SkipWs()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                    if (c == '/' && i + 1 < s.Length)
                    {
                        if (s[i + 1] == '/')
                        {
                            while (i < s.Length && s[i] != '\n') i++;
                            continue;
                        }
                        if (s[i + 1] == '*')
                        {
                            i += 2;
                            while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                            i += 2;
                            continue;
                        }
                    }
                    break;
                }
            }

            public JNode Value()
            {
                SkipWs();
                if (End) throw Err("預期一個值，但檔案結束了");
                char c = s[i];
                if (c == '{') return ObjectNode();
                if (c == '[') return ArrayNode();
                if (c == '"') return new JNode { Kind = JNode.STR, Str = StringLit() };
                if (c == 't' || c == 'f') return BoolNode();
                if (c == 'n') { Expect("null"); return JNode.Null; }
                return NumberNode();
            }

            JNode ObjectNode()
            {
                var n = new JNode { Kind = JNode.OBJ, Obj = new Dictionary<string, JNode>() };
                i++; // {
                SkipWs();
                if (!End && s[i] == '}') { i++; return n; }
                while (true)
                {
                    SkipWs();
                    if (!End && s[i] == '}') { i++; break; }      // 容忍結尾逗號
                    if (End || s[i] != '"') throw Err("預期屬性名稱（要用雙引號）");
                    string key = StringLit();
                    SkipWs();
                    if (End || s[i] != ':') throw Err("屬性 \"" + key + "\" 後面少了冒號");
                    i++;
                    n.Obj[key] = Value();
                    SkipWs();
                    if (End) throw Err("物件沒有收尾的 }");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; break; }
                    throw Err("預期 , 或 }");
                }
                return n;
            }

            JNode ArrayNode()
            {
                var n = new JNode { Kind = JNode.ARR, Arr = new List<JNode>() };
                i++; // [
                SkipWs();
                if (!End && s[i] == ']') { i++; return n; }
                while (true)
                {
                    SkipWs();
                    if (!End && s[i] == ']') { i++; break; }      // 容忍結尾逗號
                    n.Arr.Add(Value());
                    SkipWs();
                    if (End) throw Err("陣列沒有收尾的 ]");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; break; }
                    throw Err("預期 , 或 ]");
                }
                return n;
            }

            JNode BoolNode()
            {
                if (s[i] == 't') { Expect("true"); return new JNode { Kind = JNode.BOOL, Bool = true }; }
                Expect("false");
                return new JNode { Kind = JNode.BOOL, Bool = false };
            }

            void Expect(string lit)
            {
                if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                    throw Err("預期 " + lit);
                i += lit.Length;
            }

            JNode NumberNode()
            {
                int st = i;
                if (!End && (s[i] == '-' || s[i] == '+')) i++;
                while (!End && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'
                                || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
                    i++;
                string raw = s.Substring(st, i - st);
                double d;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    i = st;
                    throw Err("看不懂的數值 \"" + raw + "\"");
                }
                return new JNode { Kind = JNode.NUM, Num = d };
            }

            string StringLit()
            {
                var sb = new StringBuilder();
                i++; // "
                while (true)
                {
                    if (End) throw Err("字串沒有收尾的雙引號");
                    char c = s[i++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Err("字串結尾是孤立的反斜線");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw Err("\\u 後面不足四位");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default:
                            // 不認得的跳脫就原樣保留，路徑裡的單反斜線才不會爆掉
                            sb.Append('\\').Append(e);
                            break;
                    }
                }
                return sb.ToString();
            }
        }
    }
}
