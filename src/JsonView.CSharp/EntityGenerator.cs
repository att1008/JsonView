using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JsonView.Core;
using Newtonsoft.Json;

namespace JsonView.CSharp
{
    /// <summary>实体代码生成选项。</summary>
    public sealed class GenerationOptions
    {
        public string RootName { get; set; } = "Root";
        public string Namespace { get; set; } = "Generated";
        public bool SystemTextJson { get; set; }
        public bool PascalCase { get; set; } = true;
        public bool NullableValues { get; set; }
        public bool NullableReferences { get; set; }
        public bool IgnoreNull { get; set; }
        public bool Records { get; set; }
        public bool ReadonlyLists { get; set; }
    }

    /// <summary>生成代码及样本推断诊断。</summary>
    public sealed class GenerationResult
    {
        public string Code { get; internal set; }
        public string Diagnostics { get; internal set; }
        public string RootType { get; internal set; }
    }

    /// <summary>全量样本推断并生成两种序列化器风格。</summary>
    public static class EntityGenerator
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while record init required file scoped global").Split(' '));
        private sealed class Shape
        {
            internal string Kind;
            internal bool Null;
            internal bool Missing;
            internal Shape Element;
            internal string TypeName;
            internal readonly List<KeyValuePair<string, Shape>> Fields = new List<KeyValuePair<string, Shape>>();
        }

        /// <summary>生成完整 C# 文件。</summary>
        /// <param name="text">严格 JSON 加注释输入。</param>
        /// <param name="options">输出选项。</param>
        /// <param name="cancellation">取消信号。</param>
        /// <returns>代码和诊断。</returns>
        public static GenerationResult Generate(string text, GenerationOptions options, CancellationToken cancellation = default(CancellationToken))
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!Identifier(options.RootName) || Keywords.Contains(options.RootName)) throw new ArgumentException("根类型名称必须是合法且非关键字的 C# 标识符。");
            if (string.IsNullOrWhiteSpace(options.Namespace) || options.Namespace.Split('.').Any(n => !Identifier(n) || Keywords.Contains(n))) throw new ArgumentException("命名空间必须由合法的 C# 标识符组成。");
            JsonDocument document = JsonDocument.Parse(text, cancellation);
            var warnings = new HashSet<string>(StringComparer.Ordinal);
            Shape root = Infer(document.Root, document, warnings, cancellation);
            var objects = new List<Shape>(); var names = new HashSet<string>(new[] { "JToken", "JsonElement", "JsonProperty", "JsonPropertyAttribute", "JsonPropertyName", "JsonPropertyNameAttribute", "JsonIgnore", "JsonIgnoreAttribute", "JsonIgnoreCondition", "NullValueHandling", "List", "IReadOnlyList" }, StringComparer.Ordinal);
            Assign(root, options.RootName, options, objects, names, cancellation);
            Diagnose(root, options, warnings);
            var code = new StringBuilder();
            code.Append("// 由 JsonView 根据 JSON 样本生成。\r\n");
            if (options.Records) code.Append("// 需要 C# 9+；旧框架使用 init 时请提供 IsExternalInit 兼容定义。\r\n");
            if (options.NullableReferences) code.Append("#nullable enable\r\n");
            code.Append("using System.Collections.Generic;\r\n");
            code.Append(options.SystemTextJson ? "using System.Text.Json;\r\nusing System.Text.Json.Serialization;\r\n" : "using Newtonsoft.Json;\r\nusing Newtonsoft.Json.Linq;\r\n");
            string rootType = Type(root, options);
            code.Append("\r\n// 根 JSON 类型：").Append(rootType).Append("\r\nnamespace ").Append(options.Namespace).Append("\r\n{\r\n");
            foreach (Shape obj in objects)
            {
                cancellation.ThrowIfCancellationRequested();
                code.Append("    public ").Append(options.Records ? "record " : "class ").Append(obj.TypeName).Append("\r\n    {\r\n");
                var members = new HashSet<string>(StringComparer.Ordinal) { obj.TypeName, "Clone", "EqualityContract", "PrintMembers", "Equals", "GetHashCode", "ToString", "Deconstruct" };
                foreach (KeyValuePair<string, Shape> field in obj.Fields)
                {
                    string member = Unique(Name(field.Key, options.PascalCase), members);
                    bool mapped = options.PascalCase || member != field.Key;
                    if (options.SystemTextJson)
                    {
                        if (mapped) code.Append("        [JsonPropertyName(").Append(JsonConvert.ToString(field.Key)).Append(")]\r\n");
                        if (options.IgnoreNull && CanBeNull(field.Value, options)) code.Append("        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]\r\n");
                    }
                    else if (mapped || options.IgnoreNull)
                    {
                        code.Append("        [JsonProperty(");
                        if (mapped) code.Append(JsonConvert.ToString(field.Key));
                        if (options.IgnoreNull) code.Append(mapped ? ", " : "").Append("NullValueHandling = NullValueHandling.Ignore");
                        code.Append(")]\r\n");
                    }
                    string fieldType = Type(field.Value, options);
                    code.Append("        public ").Append(fieldType).Append(' ').Append(member).Append(options.Records ? " { get; init; }" : " { get; set; }");
                    if (options.NullableReferences && !options.NullableValues && field.Value.Null && !Reference(field.Value)) warnings.Add("样本中存在 null 值类型，请开启可空值类型或调整生成模型。");
                    if ((field.Value.Null || field.Value.Missing) && !options.NullableValues && !Reference(field.Value)) warnings.Add("存在缺失或 null 的值类型字段；关闭可空值类型后不能保留这些差异。");
                    if ((field.Value.Null || field.Value.Missing) && !options.NullableReferences && Reference(field.Value)) warnings.Add("存在缺失或 null 的引用字段；当前没有生成可空引用标注。");
                    code.Append("\r\n\r\n");
                }
                code.Append("    }\r\n\r\n");
            }
            code.Append("}\r\n");
            if (objects.Count == 0) warnings.Add("根值没有可推断的实体对象，参见代码中的根 JSON 类型说明。");
            if (options.Records) warnings.Add("Record/init 需要 C# 9+；net472 等旧框架消费项目需检查 IsExternalInit。不要重复定义已有兼容类型。");
            warnings.Add("生成类型仅反映输入样本；请在业务项目中确认约束及所选 JSON 库依赖。");
            return new GenerationResult { Code = code.ToString(), RootType = rootType, Diagnostics = string.Join("\r\n", warnings.OrderBy(w => w, StringComparer.Ordinal)) };
        }

        private static Shape Infer(JsonNode node, JsonDocument document, HashSet<string> warnings, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var shape = new Shape { Kind = node.Kind };
            if (node.Kind == "null") { shape.Null = true; return shape; }
            if (node.Kind == "object") foreach (JsonNode child in node.Children) shape.Fields.Add(new KeyValuePair<string, Shape>(child.Name, Infer(child, document, warnings, cancellation)));
            else if (node.Kind == "array")
            {
                foreach (JsonNode child in node.Children) shape.Element = Merge(shape.Element, Infer(child, document, warnings, cancellation), warnings, cancellation);
                if (shape.Element == null) shape.Element = new Shape { Kind = "unknown" };
            }
            else if (node.Kind == "number")
            {
                string raw = document.Raw(node); long number; decimal fraction;
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) shape.Kind = number >= int.MinValue && number <= int.MaxValue ? "int" : "long";
                else if (Numbers.TryDecimal(raw, out fraction)) shape.Kind = "decimal";
                else { shape.Kind = "raw"; warnings.Add("超出常规精确数值范围的字段使用库原生 JSON 类型，请检查消费端数值读取策略。"); }
            }
            return shape;
        }
        private static Shape Merge(Shape left, Shape right, HashSet<string> warnings, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (left == null) return right;
            if (left.Kind == "unknown") return right;
            if (right.Kind == "unknown") return left;
            if (right.Kind == "null") { left.Null = true; return left; }
            if (left.Kind == "null") { right.Null = true; return right; }
            left.Null |= right.Null;
            if (left.Kind != right.Kind)
            {
                if (Numeric(left.Kind) && Numeric(right.Kind)) left.Kind = left.Kind == "decimal" || right.Kind == "decimal" ? "decimal" : "long";
                else { left.Kind = "raw"; warnings.Add("发现不兼容的混合类型，使用所选库的 JToken/JsonElement 保存 JSON。"); }
                return left;
            }
            if (left.Kind == "array") left.Element = Merge(left.Element, right.Element, warnings, cancellation);
            if (left.Kind == "object")
            {
                for (int i = 0; i < left.Fields.Count; i++)
                {
                    var field = left.Fields[i]; int index = right.Fields.FindIndex(f => f.Key == field.Key);
                    if (index < 0) { field.Value.Missing = true; warnings.Add("对象数组存在字段缺失，已合并所有样本字段。"); }
                    else left.Fields[i] = new KeyValuePair<string, Shape>(field.Key, Merge(field.Value, right.Fields[index].Value, warnings, cancellation));
                }
                foreach (var field in right.Fields) if (!left.Fields.Any(f => f.Key == field.Key)) { field.Value.Missing = true; left.Fields.Add(field); warnings.Add("对象数组存在字段缺失，已合并所有样本字段。"); }
            }
            return left;
        }
        private static bool Numeric(string kind) => kind == "int" || kind == "long" || kind == "decimal";
        private static void Diagnose(Shape shape, GenerationOptions options, HashSet<string> warnings)
        {
            if (shape.Kind == "null") warnings.Add("仅有 null 的位置无法推断业务类型，使用 object。");
            if (shape.Kind == "unknown") warnings.Add("空数组没有可用元素样本，使用 object 元素类型。");
            if (shape.Null && !options.NullableValues && Numeric(shape.Kind)) warnings.Add("样本元素包含 null，关闭可空值类型后不能保留该差异。");
            if (shape.Kind == "array") Diagnose(shape.Element, options, warnings);
            if (shape.Kind == "object") foreach (var field in shape.Fields) Diagnose(field.Value, options, warnings);
        }
        private static bool Reference(Shape shape) => shape.Kind == "object" || shape.Kind == "array" || shape.Kind == "string" || shape.Kind == "null" || shape.Kind == "unknown";
        private static bool CanBeNull(Shape shape, GenerationOptions options) => Reference(shape) || options.NullableValues || (shape.Kind == "raw" && !options.SystemTextJson);
        private static string Type(Shape shape, GenerationOptions options)
        {
            string type;
            switch (shape.Kind)
            {
                case "object": type = shape.TypeName; break;
                case "array": type = (options.ReadonlyLists ? "IReadOnlyList<" : "List<") + Type(shape.Element, options) + ">"; break;
                case "boolean": type = "bool"; break;
                case "string": type = "string"; break;
                case "int": case "long": case "decimal": type = shape.Kind; break;
                case "raw": type = options.SystemTextJson ? "JsonElement" : "JToken"; break;
                default: type = "object"; break;
            }
            bool reference = Reference(shape) || (shape.Kind == "raw" && !options.SystemTextJson);
            return type + ((reference ? options.NullableReferences : options.NullableValues) ? "?" : "");
        }
        private static void Assign(Shape shape, string suggested, GenerationOptions options, List<Shape> objects, HashSet<string> names, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (shape.Kind == "object")
            {
                shape.TypeName = Unique(Name(suggested, options.PascalCase), names); objects.Add(shape);
                foreach (var field in shape.Fields) Assign(field.Value, field.Key + (field.Value.Kind == "array" ? "Item" : ""), options, objects, names, cancellation);
            }
            else if (shape.Kind == "array") Assign(shape.Element, suggested, options, objects, names, cancellation);
        }
        private static string Unique(string candidate, HashSet<string> names) { string result = candidate; int i = 2; while (!names.Add(result)) result = candidate + i++; return result; }
        private static bool Identifier(string text) => !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"^[\p{L}_][\p{L}\p{Nd}_]*$");
        private static string Name(string text, bool pascal)
        {
            string clean = Regex.Replace(text, @"[^\p{L}\p{Nd}_]", "_");
            if (pascal) clean = string.Concat(clean.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));
            if (clean.Length == 0) clean = "Value";
            if (char.IsDigit(clean[0]) || Keywords.Contains(clean)) clean = "_" + clean;
            return clean;
        }
    }
}
