using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonView.Core
{
    /// <summary>保留原文区间的 JSON 节点，避免数值在查看时被重新序列化。</summary>
    public sealed class JsonNode
    {
        public string Name { get; internal set; }
        public string Kind { get; internal set; }
        public int Start { get; internal set; }
        public int End { get; internal set; }
        public int KeyStart { get; internal set; }
        public int Index { get; internal set; }
        public JsonNode Parent { get; internal set; }
        public List<JsonNode> Children { get; internal set; }
        public bool IsContainer => Children != null;
        public string Path => Parent == null ? "$" : Parent.Path + (Parent.Kind == "array" ? "[" + Index + "]" : "['" + EscapePath(Name) + "']");

        /// <summary>转义 Json.NET JSONPath 的属性名。</summary>
        /// <param name="name">原始属性名。</param>
        /// <returns>带转义的属性名。</returns>
        public static string EscapePath(string name)
        {
            return name.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\b", "\\b").Replace("\f", "\\f");
        }
    }

    /// <summary>带原文错误位置的语法异常。</summary>
    public sealed class JsonSyntaxException : Exception
    {
        public int Offset { get; }
        public JsonSyntaxException(string message, int offset) : base(message) { Offset = offset; }
    }

    /// <summary>严格 JSON 加注释的已验证文档。</summary>
    public sealed class JsonDocument
    {
        public string Text { get; }
        public JsonNode Root { get; }
        private JsonDocument(string text, JsonNode root) { Text = text; Root = root; }

        /// <summary>解析原文并构建位置索引。</summary>
        /// <param name="text">原始文本。</param>
        /// <param name="cancellation">取消信号。</param>
        /// <returns>有效文档。</returns>
        public static JsonDocument Parse(string text, CancellationToken cancellation = default(CancellationToken))
        {
            var parser = new Parser(text ?? "", cancellation);
            JsonNode root = parser.ReadValue(null, null, 0, 0);
            parser.SkipTrivia();
            if (parser.Position != text.Length) parser.Fail("根值后出现多余内容");
            return new JsonDocument(text, root);
        }

        /// <summary>获取节点的未修改原文。</summary>
        /// <param name="node">节点。</param>
        /// <returns>包含注释的节点文本。</returns>
        public string Raw(JsonNode node) => Text.Substring(node.Start, node.End - node.Start);

        /// <summary>获取短预览，完整内容仍保存在原文。</summary>
        /// <param name="node">节点。</param>
        /// <returns>节点摘要。</returns>
        public string Preview(JsonNode node)
        {
            if (node.IsContainer) return (node.Kind == "array" ? "数组" : "对象") + " · " + node.Children.Count.ToString("N0") + " 项";
            int length = Math.Min(120, node.End - node.Start);
            return Text.Substring(node.Start, length).Replace("\r", " ").Replace("\n", " ") + (length < node.End - node.Start ? "…" : "");
        }

        /// <summary>按偏移定位最内层节点，空白及注释归属容器。</summary>
        /// <param name="offset">UTF-16 字符偏移。</param>
        /// <returns>对应节点。</returns>
        public JsonNode Locate(int offset)
        {
            JsonNode current = Root;
            while (current.Children != null && current.Children.Count != 0)
            {
                List<JsonNode> children = current.Children;
                int low = 0, high = children.Count - 1, found = -1;
                while (low <= high)
                {
                    int middle = low + (high - low) / 2;
                    if (children[middle].KeyStart <= offset) { found = middle; low = middle + 1; }
                    else high = middle - 1;
                }
                if (found < 0 || offset >= children[found].End) break;
                current = children[found];
            }
            return current;
        }

        /// <summary>运行 Json.NET 方言查询，结果关联原始节点。</summary>
        /// <param name="expression">JSONPath 表达式。</param>
        /// <param name="cancellation">取消信号。</param>
        /// <returns>完整匹配列表。</returns>
        public List<JsonNode> Query(string expression, CancellationToken cancellation = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("请输入 JSONPath 表达式。");
            JToken tree = BuildToken(Root, cancellation);
            var results = new List<JsonNode>();
            foreach (JToken token in tree.SelectTokens(expression))
            {
                cancellation.ThrowIfCancellationRequested();
                JsonNode node = token.Annotation<JsonNode>();
                if (node != null) results.Add(node);
            }
            return results;
        }

        private JToken BuildToken(JsonNode node, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            JToken value;
            if (node.Kind == "object")
            {
                var obj = new JObject();
                foreach (JsonNode child in node.Children) obj.Add(child.Name, BuildToken(child, cancellation));
                value = obj;
            }
            else if (node.Kind == "array")
            {
                var array = new JArray();
                foreach (JsonNode child in node.Children) array.Add(BuildToken(child, cancellation));
                value = array;
            }
            else if (node.Kind == "number")
            {
                string raw = Raw(node);
                if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) value = new JValue(BigInteger.Parse(raw, CultureInfo.InvariantCulture));
                else
                {
                    decimal number;
                    if (!Numbers.TryDecimal(raw, out number)) throw new InvalidOperationException("数值超出精确查询范围：" + node.Path + "。原文、格式化和导出不受影响。");
                    value = new JValue(number);
                }
            }
            else if (node.Kind == "string") value = new JValue(JsonConvert.DeserializeObject<string>(Raw(node)));
            else if (node.Kind == "boolean") value = new JValue(Text[node.Start] == 't');
            else value = JValue.CreateNull();
            value.AddAnnotation(node);
            return value;
        }

        /// <summary>从完整匹配结果生成标准 JSON，保持数值原文。</summary>
        /// <param name="nodes">匹配节点。</param>
        /// <param name="includePaths">是否包含路径和类型。</param>
        /// <param name="cancellation">取消信号。</param>
        /// <returns>导出文本。</returns>
        public string Export(IEnumerable<JsonNode> nodes, bool includePaths, CancellationToken cancellation = default(CancellationToken))
        {
            var output = new StringBuilder("[");
            bool first = true;
            foreach (JsonNode node in nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!first) output.Append(',');
                first = false;
                if (includePaths) output.Append("{\"path\":").Append(JsonConvert.ToString(node.Path)).Append(",\"type\":").Append(JsonConvert.ToString(node.Kind)).Append(",\"value\":");
                output.Append(JsonText.Transform(Raw(node), false, cancellation));
                if (includePaths) output.Append('}');
            }
            return output.Append(']').ToString();
        }

        private sealed class Parser
        {
            private readonly string text;
            private readonly CancellationToken cancellation;
            internal int Position;
            internal Parser(string text, CancellationToken cancellation) { this.text = text; this.cancellation = cancellation; }
            internal void Fail(string message) { throw new JsonSyntaxException(message, Position); }
            private char Current => Position < text.Length ? text[Position] : '\0';
            internal void SkipTrivia()
            {
                while (Position < text.Length)
                {
                    if ((Position & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    char c = Current;
                    if (c == ' ' || c == '\r' || c == '\n' || c == '\t') { Position++; continue; }
                    if (c != '/' || Position + 1 >= text.Length) break;
                    if (text[Position + 1] == '/') { Position += 2; while (Position < text.Length && Current != '\n' && Current != '\r') { Check(); Position++; } }
                    else if (text[Position + 1] == '*')
                    {
                        Position += 2;
                        while (Position + 1 < text.Length && !(Current == '*' && text[Position + 1] == '/')) { Check(); Position++; }
                        if (Position + 1 >= text.Length) Fail("块注释未结束");
                        Position += 2;
                    }
                    else break;
                }
            }
            private void Check() { if ((Position & 4095) == 0) cancellation.ThrowIfCancellationRequested(); }
            private string ReadString(bool decode)
            {
                int start = Position++;
                while (Position < text.Length)
                {
                    Check();
                    char c = text[Position++];
                    if (c == '"') return decode ? JsonConvert.DeserializeObject<string>(text.Substring(start, Position - start)) : null;
                    if (c < 32) Fail("字符串中包含未转义的控制字符");
                    if (c != '\\') continue;
                    if (Position >= text.Length) Fail("字符串转义未结束");
                    c = text[Position++];
                    if (c == 'u')
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            if (Position >= text.Length || !Uri.IsHexDigit(Current)) Fail("Unicode 转义需要 4 个十六进制数字");
                            Position++;
                        }
                    }
                    else if ("\"\\/bfnrt".IndexOf(c) < 0) Fail("非法字符串转义");
                }
                Fail("字符串未结束"); return null;
            }
            internal JsonNode ReadValue(JsonNode parent, string name, int keyStart, int depth)
            {
                cancellation.ThrowIfCancellationRequested();
                if (depth > 256) Fail("嵌套深度超过支持上限 256");
                SkipTrivia();
                var node = new JsonNode { Parent = parent, Name = name, KeyStart = parent == null ? Position : keyStart, Start = Position, Index = parent == null ? 0 : parent.Children.Count };
                char c = Current;
                if (c == '{' || c == '[')
                {
                    bool obj = c == '{'; char close = obj ? '}' : ']';
                    node.Kind = obj ? "object" : "array"; node.Children = new List<JsonNode>();
                    HashSet<string> names = obj ? new HashSet<string>(StringComparer.Ordinal) : null;
                    Position++; SkipTrivia();
                    if (Current != close)
                    {
                        while (true)
                        {
                            SkipTrivia(); int key = Position; string property = null;
                            if (obj)
                            {
                                if (Current != '"') Fail("属性名必须使用双引号");
                                property = ReadString(true);
                                if (!names.Add(property)) throw new JsonSyntaxException("重复属性名：" + property, key);
                                SkipTrivia(); if (Current != ':') Fail("属性名后缺少冒号"); Position++;
                            }
                            node.Children.Add(ReadValue(node, property, key, depth + 1));
                            SkipTrivia();
                            if (Current == close) break;
                            if (Current != ',') Fail("元素之间缺少逗号或容器未结束");
                            Position++; SkipTrivia(); if (Current == close) Fail("不允许尾随逗号");
                        }
                    }
                    if (Current != close) Fail("容器未结束");
                    Position++;
                }
                else if (c == '"') { node.Kind = "string"; ReadString(false); }
                else if (c == 't' || c == 'f' || c == 'n')
                {
                    string literal = c == 't' ? "true" : c == 'f' ? "false" : "null";
                    if (Position + literal.Length > text.Length || string.CompareOrdinal(text, Position, literal, 0, literal.Length) != 0) Fail("非法字面量");
                    Position += literal.Length; node.Kind = c == 'n' ? "null" : "boolean";
                }
                else
                {
                    node.Kind = "number";
                    if (Current == '-') Position++;
                    if (Current == '0') Position++;
                    else { if (Current < '1' || Current > '9') Fail("此处需要 JSON 值"); Digits(); }
                    if (Current == '.') { Position++; if (!Digit()) Fail("小数点后缺少数字"); Digits(); }
                    if (Current == 'e' || Current == 'E') { Position++; if (Current == '+' || Current == '-') Position++; if (!Digit()) Fail("指数缺少数字"); Digits(); }
                }
                node.End = Position;
                return node;
            }
            private bool Digit() => Current >= '0' && Current <= '9';
            private void Digits() { while (Digit()) { Check(); Position++; } }
        }
    }

    /// <summary>数值精确性辅助。</summary>
    public static class Numbers
    {
        /// <summary>仅在 decimal 没有发生舍入时转换。</summary>
        /// <param name="raw">数字原文。</param>
        /// <param name="value">转换结果。</param>
        /// <returns>是否精确可表示。</returns>
        public static bool TryDecimal(string raw, out decimal value)
        {
            return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Key(raw) == Key(value.ToString("G29", CultureInfo.InvariantCulture));
        }
        private static string Key(string raw)
        {
            string sign = raw.StartsWith("-", StringComparison.Ordinal) ? "-" : "";
            string text = raw.TrimStart('-'); int exp = 0;
            int e = text.IndexOfAny(new[] { 'e', 'E' });
            if (e >= 0) { if (!int.TryParse(text.Substring(e + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out exp)) return raw; text = text.Substring(0, e); }
            int dot = text.IndexOf('.'); if (dot >= 0) { exp -= text.Length - dot - 1; text = text.Remove(dot, 1); }
            text = text.TrimStart('0'); if (text.Length == 0) return "0";
            int trimmed = text.Length - text.TrimEnd('0').Length; text = text.TrimEnd('0');
            return sign + text + ":" + ((long)exp + trimmed).ToString(CultureInfo.InvariantCulture);
        }
    }
}
