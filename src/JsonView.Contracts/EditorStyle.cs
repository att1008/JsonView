using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace JsonView.Contracts
{
    /// <summary>宿主及插件使用的轻量语法配色辅助，不属于插件入口契约。</summary>
    public static class EditorStyle
    {
        /// <summary>应用适合当前主题的高亮。</summary>
        /// <param name="editor">编辑器。</param>
        /// <param name="dark">暗色主题。</param>
        /// <param name="json">是否 JSON。</param>
        public static void Apply(TextEditor editor, bool dark, bool json)
        {
            editor.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            editor.FontSize = 14; editor.ShowLineNumbers = true;
            editor.SetResourceReference(TextEditor.BackgroundProperty, "SurfaceBrush");
            editor.SetResourceReference(TextEditor.ForegroundProperty, "TextBrush");
            editor.LineNumbersForeground = new SolidColorBrush(dark ? Color.FromRgb(133, 146, 166) : Color.FromRgb(115, 128, 148));
            editor.TextArea.SelectionBrush = new SolidColorBrush(dark ? Color.FromRgb(47, 71, 110) : Color.FromRgb(204, 225, 252));
            editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
            string colors = "<Color name='String' foreground='" + (dark ? "#A8D5AD" : "#297744") + "'/><Color name='Comment' foreground='" + (dark ? "#899BAA" : "#768392") + "' fontStyle='italic'/><Color name='Keyword' foreground='" + (dark ? "#B9A1F4" : "#8051AB") + "'/><Color name='Number' foreground='" + (dark ? "#F0BB79" : "#AD642A") + "'/><Color name='Property' foreground='" + (dark ? "#88C9F0" : "#176CA4") + "'/>";
            string rules = @"<Span color='Comment' begin='//' end='$'/><Span color='Comment' multiline='true' begin='/\*' end='\*/'/>";
            if (json) rules += @"<Span color='Property' begin='&quot;(?=(?:[^&quot;\\]|\\.)*&quot;\s*:)' end='&quot;'><RuleSet><Span begin='\\' end='.'/></RuleSet></Span>";
            rules += @"<Span color='String' begin='&quot;' end='&quot;'><RuleSet><Span begin='\\' end='.'/></RuleSet></Span><Rule color='Number'>\b-?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\b</Rule>";
            rules += json ? "<Keywords color='Keyword'><Word>true</Word><Word>false</Word><Word>null</Word></Keywords>" : "<Keywords color='Keyword'><Word>using</Word><Word>namespace</Word><Word>public</Word><Word>class</Word><Word>record</Word><Word>get</Word><Word>set</Word><Word>init</Word><Word>string</Word><Word>int</Word><Word>long</Word><Word>decimal</Word><Word>bool</Word><Word>object</Word><Word>null</Word></Keywords>";
            using (var reader = XmlReader.Create(new StringReader("<SyntaxDefinition name='JsonView' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>" + colors + "<RuleSet>" + rules + "</RuleSet></SyntaxDefinition>"))) editor.SyntaxHighlighting = HighlightingLoader.Load(reader, null);
            if (json) LongLineSupport.Attach(editor, dark);
        }
    }
}
