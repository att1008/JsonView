using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace JsonView.Contracts
{
    // 超长逻辑行只排版局部文本，折叠元素仍占原文长度，不更改原文及定位偏移。
    internal sealed class LongLineSupport
    {
        private static readonly DependencyProperty InstanceProperty = DependencyProperty.RegisterAttached("LongLineSupport", typeof(LongLineSupport), typeof(LongLineSupport));
        private readonly TextEditor editor;
        private readonly VisibleColorizer colorizer;
        private IHighlightingDefinition definition;
        private bool active;
        private LongLineSupport(TextEditor editor)
        {
            this.editor = editor; colorizer = new VisibleColorizer();
            editor.TextArea.TextView.ElementGenerators.Insert(0, new LongLineGenerator(editor));
            editor.TextArea.TextView.LineTransformers.Add(colorizer);
            editor.TextChanged += (s, e) => Refresh();
            editor.TextArea.Caret.PositionChanged += (s, e) => { if (active) editor.TextArea.TextView.Redraw(); };
        }
        internal static void Attach(TextEditor editor, bool dark)
        {
            var support = (LongLineSupport)editor.GetValue(InstanceProperty);
            if (support == null) { support = new LongLineSupport(editor); editor.SetValue(InstanceProperty, support); }
            support.definition = editor.SyntaxHighlighting; support.colorizer.Dark = dark; support.Refresh();
        }
        private void Refresh()
        {
            active = editor.Document != null && editor.Document.TextLength > 16000 && editor.Document.Lines.Any(l => l.Length > 16000);
            colorizer.Active = active;
            editor.SyntaxHighlighting = active ? null : definition;
            editor.TextArea.TextView.Redraw();
        }
        private sealed class LongLineGenerator : VisualLineElementGenerator
        {
            private readonly TextEditor editor;
            internal LongLineGenerator(TextEditor editor) { this.editor = editor; }
            private int EndFor(int offset)
            {
                DocumentLine line = CurrentContext.Document.GetLineByOffset(offset);
                if (line.Length <= 16000) return -1;
                int caret = Math.Max(line.Offset, Math.Min(line.EndOffset, editor.CaretOffset));
                int prefix = line.Offset + 2048;
                int middleStart = Math.Max(prefix, caret - 2048);
                if (offset == prefix && middleStart > prefix) return middleStart;
                int suffix = Math.Max(prefix, caret + 2048);
                return offset == suffix && suffix < line.EndOffset - 256 ? line.EndOffset - 256 : -1;
            }
            public override int GetFirstInterestedOffset(int startOffset)
            {
                DocumentLine line = CurrentContext.Document.GetLineByOffset(startOffset);
                if (line.Length <= 16000) return -1;
                int caret = Math.Max(line.Offset, Math.Min(line.EndOffset, editor.CaretOffset)); int prefix = line.Offset + 2048;
                if (startOffset <= prefix && caret - 2048 > prefix) return prefix;
                int suffix = Math.Max(prefix, caret + 2048);
                return startOffset <= suffix && suffix < line.EndOffset - 256 ? suffix : -1;
            }
            public override VisualLineElement ConstructElement(int offset)
            {
                int end = EndFor(offset);
                return end > offset ? new FormattedTextElement("  … 折叠 " + (end - offset).ToString("N0") + " 字符（树/查找可定位，格式化可完整展开）…  ", end - offset) : null;
            }
        }
        private sealed class VisibleColorizer : DocumentColorizingTransformer
        {
            internal bool Active, Dark;
            private static readonly Regex Tokens = new Regex(@"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)|(?<property>""(?:[^""\\]|\\.)*""(?=\s*:))|(?<text>""(?:[^""\\]|\\.)*"")|(?<number>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|\b(?<keyword>true|false|null)\b", RegexOptions.Compiled);
            protected override void ColorizeLine(DocumentLine line)
            {
                if (!Active) return;
                int visualStart = CurrentContext.VisualLine.FirstDocumentLine.Offset;
                foreach (VisualLineElement element in CurrentContext.VisualLine.Elements.ToArray())
                {
                    if (element is FormattedTextElement) continue;
                    int start = Math.Max(line.Offset, visualStart + element.RelativeTextOffset); int end = Math.Min(line.EndOffset, visualStart + element.RelativeTextOffset + element.DocumentLength);
                    if (end <= start || end - start > 16000) continue;
                    string text = CurrentContext.Document.GetText(start, end - start);
                    foreach (Match match in Tokens.Matches(text))
                    {
                        string color = match.Groups["property"].Success ? (Dark ? "#88C9F0" : "#176CA4") : match.Groups["comment"].Success ? (Dark ? "#899BAA" : "#768392") : match.Groups["number"].Success ? (Dark ? "#F0BB79" : "#AD642A") : match.Groups["keyword"].Success ? (Dark ? "#B9A1F4" : "#8051AB") : (Dark ? "#A8D5AD" : "#297744");
                        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze();
                        ChangeLinePart(start + match.Index, start + match.Index + match.Length, part => part.TextRunProperties.SetForegroundBrush(brush));
                    }
                }
            }
        }
    }
}
