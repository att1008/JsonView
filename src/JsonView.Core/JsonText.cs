using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace JsonView.Core
{
    /// <summary>基于原始词法片段格式化，保留数字及字符串写法。</summary>
    public static class JsonText
    {
        /// <summary>格式化或压缩已校验的 JSON。</summary>
        /// <param name="text">原文。</param>
        /// <param name="pretty">是否格式化并保留注释。</param>
        /// <param name="cancellation">取消信号。</param>
        /// <returns>转换结果。</returns>
        public static string Transform(string text, bool pretty, CancellationToken cancellation = default(CancellationToken))
        {
            var output = new StringBuilder(text.Length); int depth = 0;
            Action newline = () => { while (output.Length > 0 && output[output.Length - 1] == ' ') output.Length--; if (output.Length > 0 && output[output.Length - 1] != '\n') output.Append("\r\n"); output.Append(' ', depth * 2); };
            foreach (string token in Tokens(text, cancellation))
            {
                bool comment = token.StartsWith("//", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal);
                if (!pretty) { if (!comment) output.Append(token); continue; }
                if (comment) { if (output.Length > 0 && output[output.Length - 1] != ' ' && output[output.Length - 1] != '\n') output.Append(' '); output.Append(token); newline(); }
                else if (token == "{" || token == "[") { output.Append(token); depth++; newline(); }
                else if (token == "}" || token == "]") { depth--; newline(); output.Append(token); }
                else if (token == ",") { output.Append(','); newline(); }
                else if (token == ":") output.Append(": ");
                else output.Append(token);
            }
            return output.ToString().TrimEnd();
        }
        private static IEnumerable<string> Tokens(string text, CancellationToken cancellation)
        {
            int p = 0;
            while (p < text.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                if (char.IsWhiteSpace(text[p])) { p++; continue; }
                int start = p; char c = text[p++];
                if (c == '"')
                {
                    while (p < text.Length) { if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested(); char next = text[p++]; if (next == '\\') p++; else if (next == '"') break; }
                }
                else if (c == '/' && p < text.Length && text[p] == '/') { p++; while (p < text.Length && text[p] != '\r' && text[p] != '\n') { if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested(); p++; } }
                else if (c == '/' && p < text.Length && text[p] == '*') { p++; while (p + 1 < text.Length && !(text[p] == '*' && text[p + 1] == '/')) { if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested(); p++; } p += 2; }
                else if ("{}[],:".IndexOf(c) < 0) { while (p < text.Length && !char.IsWhiteSpace(text[p]) && "{}[],:/".IndexOf(text[p]) < 0) p++; }
                yield return text.Substring(start, p - start);
            }
        }
    }
}
