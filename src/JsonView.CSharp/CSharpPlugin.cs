using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using JsonView.Contracts;
using JsonView.Core;
using Microsoft.Win32;

namespace JsonView.CSharp
{
    /// <summary>默认实体生成插件入口。</summary>
    public sealed class CSharpPlugin : IJsonViewPlugin, IJsonNodePlugin
    {
        public string CommandText => "JSON 转 C# 实体类";
        public string NodeCommandText => "生成此节点的 C# 实体类…";
        /// <summary>打开带语法高亮及保存能力的生成窗口。</summary>
        /// <param name="context">宿主上下文。</param>
        public void Execute(IPluginContext context) { new GeneratorWindow(context).Show(); }

        /// <summary>打开仅处理指定节点快照的生成窗口。</summary>
        /// <param name="context">宿主上下文。</param>
        /// <param name="snapshot">右键所点节点的快照。</param>
        public void ExecuteForNode(IPluginContext context, DocumentSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            new GeneratorWindow(context, snapshot).Show();
        }
    }

    internal sealed class GeneratorWindow : Window
    {
        private readonly IPluginContext context;
        private readonly DocumentSnapshot nodeSnapshot;
        private readonly TextEditor preview = new TextEditor { IsReadOnly = true };
        private readonly TextBox root = new TextBox { Text = "Root", Width = 130 };
        private readonly TextBox ns = new TextBox { Text = "Generated", Width = 190 };
        private readonly ComboBox library = new ComboBox { Width = 160, SelectedIndex = 0 };
        private readonly CheckBox pascal = Toggle("Pascal Case", true), values = Toggle("可空值类型"), references = Toggle("可空引用 (C# 8+)"), ignore = Toggle("忽略 null"), records = Toggle("Record / init (C# 9+)"), lists = Toggle("IReadOnlyList<T>"), selected = Toggle("仅选中子树");
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8) };
        private readonly TextBlock source = new TextBlock { Margin = new Thickness(12, 6, 12, 6) };
        private readonly Button save, copy;
        private CancellationTokenSource pending;
        private DocumentSnapshot snapshot;
        private GenerationResult result;
        private bool closed;

        internal GeneratorWindow(IPluginContext context, DocumentSnapshot nodeSnapshot = null)
        {
            this.context = context; this.nodeSnapshot = nodeSnapshot; Owner = context.Owner; Title = "JSON 转 C# 实体类"; Width = 1060; Height = 780; MinWidth = 760; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (nodeSnapshot != null) { selected.IsChecked = true; selected.IsEnabled = false; selected.Content = "已锁定右键所选节点"; Title += " · " + nodeSnapshot.Path; }
            SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            library.Items.Add("Newtonsoft.Json"); library.Items.Add("System.Text.Json");
            var dock = new DockPanel(); Content = dock;
            var controls = new StackPanel { Margin = new Thickness(12, 10, 12, 8) }; DockPanel.SetDock(controls, Dock.Top); dock.Children.Add(controls);
            var names = new WrapPanel(); names.Children.Add(new TextBlock { Text = "根类型", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); names.Children.Add(root); names.Children.Add(new TextBlock { Text = "命名空间", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 4, 4, 4) }); names.Children.Add(ns); names.Children.Add(library); controls.Children.Add(names);
            var toggles = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) }; foreach (CheckBox box in new[] { pascal, values, references, ignore, records, lists, selected }) toggles.Children.Add(box); controls.Children.Add(toggles);
            var actions = new WrapPanel(); AddButton(actions, "生成 / 刷新预览", async (s, e) => await Generate()); copy = AddButton(actions, "复制代码", (s, e) => Clipboard.SetText(preview.Text)); save = AddButton(actions, "保存为 .cs…", async (s, e) => await Save()); AddButton(actions, "取消生成", (s, e) => { pending?.Cancel(); status.Text = "已取消；原预览仍可查看。"; }); controls.Children.Add(actions); copy.IsEnabled = save.IsEnabled = false;
            foreach (CheckBox box in new[] { pascal, values, references, ignore, records, lists, selected }) box.Click += (s, e) => Invalidate();
            root.TextChanged += (s, e) => Invalidate(); ns.TextChanged += (s, e) => Invalidate(); library.SelectionChanged += (s, e) => Invalidate();
            DockPanel.SetDock(source, Dock.Bottom); dock.Children.Add(source); DockPanel.SetDock(status, Dock.Bottom); dock.Children.Add(status);
            preview.Margin = new Thickness(12, 0, 12, 0); dock.Children.Add(preview); EditorStyle.Apply(preview, context.IsDark, false);
            context.ThemeChanged += ThemeChanged;
            Closed += (s, e) => { closed = true; pending?.Cancel(); context.ThemeChanged -= ThemeChanged; };
            Loaded += async (s, e) => await Generate();
            Activated += (s, e) => { if (snapshot != null && !snapshot.IsCurrent()) source.Text = "源文档已变更或关闭 · 当前显示之前的快照，可保存或重新生成。"; };
        }
        private void ThemeChanged(object sender, EventArgs e) { EditorStyle.Apply(preview, context.IsDark, false); }
        private void Invalidate() { pending?.Cancel(); result = null; save.IsEnabled = copy.IsEnabled = false; status.Text = "选项已更改，请重新生成预览。"; }
        private async System.Threading.Tasks.Task Generate()
        {
            pending?.Cancel(); var cancellation = new CancellationTokenSource(); pending = cancellation;
            try
            {
                snapshot = nodeSnapshot ?? context.GetSnapshot(selected.IsChecked == true);
                var options = new GenerationOptions { RootName = root.Text.Trim(), Namespace = ns.Text.Trim(), SystemTextJson = library.SelectedIndex == 1, PascalCase = pascal.IsChecked == true, NullableValues = values.IsChecked == true, NullableReferences = references.IsChecked == true, IgnoreNull = ignore.IsChecked == true, Records = records.IsChecked == true, ReadonlyLists = lists.IsChecked == true };
                string input = snapshot.Text; status.Text = "正在扫描全部样本并生成代码…"; source.Text = snapshot.Title + " · " + snapshot.Path + " · 版本 " + snapshot.Version; save.IsEnabled = copy.IsEnabled = false;
                GenerationResult generated = await Work.Run(() => EntityGenerator.Generate(input, options, cancellation.Token), cancellation.Token);
                if (closed || pending != cancellation || cancellation.IsCancellationRequested) return;
                result = generated; preview.Text = result.Code; status.Text = "根类型：" + result.RootType + "\r\n" + result.Diagnostics; save.IsEnabled = copy.IsEnabled = true;
                if (!snapshot.IsCurrent()) source.Text += " · 源已变更，结果基于旧快照";
            }
            catch (OperationCanceledException) { if (!closed && pending == cancellation) status.Text = "已取消生成。"; }
            catch (Exception exception) { if (!closed && pending == cancellation) { result = null; status.Text = "生成失败：" + exception.Message; } }
            finally { if (pending == cancellation) pending = null; cancellation.Dispose(); }
        }
        private async System.Threading.Tasks.Task Save()
        {
            if (result == null) return;
            var dialog = new SaveFileDialog { Filter = "C# 源代码 (*.cs)|*.cs", FileName = root.Text + ".cs", DefaultExt = ".cs", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            string code = preview.Text;
            try { await FileStorage.WriteAsync(dialog.FileName, code, FileStorage.EncodingFor(65001)); status.Text = "已保存：" + dialog.FileName; }
            catch (Exception exception) { status.Text = "保存失败：" + exception.Message; }
        }
        private static CheckBox Toggle(string text, bool value = false) => new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(4, 6, 14, 6), VerticalAlignment = VerticalAlignment.Center };
        private static Button AddButton(Panel panel, string text, RoutedEventHandler action) { var button = new Button { Content = text }; button.Click += action; panel.Children.Add(button); return button; }
    }
}
