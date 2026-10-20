using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;
using JsonView.Contracts;
using JsonView.Core;
using Microsoft.Win32;

namespace JsonView
{
    internal sealed class DocumentPage : UserControl, IDisposable
    {
        internal readonly TextEditor Editor = new TextEditor();
        internal string DisplayName, FilePath;
        internal Encoding FileEncoding = FileStorage.EncodingFor(65001);
        internal bool Dirty, Closed, IsLoading, IsSaving, ReadFailed;
        internal long Version;
        internal JsonDocument Parsed;
        internal JsonNode SelectedNode;
        internal bool HasCurrentDocument => Parsed != null && parsedVersion == Version && !Closed;
        internal event EventHandler StateChanged;
        private long parsedVersion = -1, resultsVersion = -1;
        private readonly MainWindow owner;
        private readonly DispatcherTimer parseTimer, caretTimer;
        private readonly TreeView tree = new TreeView();
        private readonly ListBox results = new ListBox();
        private readonly TextBox expression = new TextBox { Text = "$", MinWidth = 150 };
        private readonly TextBox path = new TextBox { IsReadOnly = true, BorderThickness = new Thickness(0), FontSize = 12 };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6) };
        private readonly TextBlock position = new TextBlock { Margin = new Thickness(8, 3, 8, 5) };
        private readonly TextBlock matches = new TextBlock { Text = "查询尚未执行", Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        private readonly TabControl right = new TabControl();
        private readonly SearchPanel search;
        private readonly CheckBox exportPaths = new CheckBox { Content = "导出含路径", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) };
        private List<JsonNode> matched = new List<JsonNode>();
        private CancellationTokenSource parsing, querying, transforming;
        private bool initial, linking;
        private int errorOffset;
        private TreeNode rootItem;
        private JsonNode contextNode;
        private long contextVersion;

        internal DocumentPage(MainWindow owner)
        {
            this.owner = owner;
            var dock = new DockPanel(); Content = dock;
            var query = new DockPanel { Margin = new Thickness(6) }; DockPanel.SetDock(query, Dock.Top); dock.Children.Add(query);
            var queryActions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(queryActions, Dock.Right); query.Children.Add(queryActions);
            Button(queryActions, "执行", async (s, e) => await Query()); Button(queryActions, "取消", (s, e) => { parsing?.Cancel(); querying?.Cancel(); transforming?.Cancel(); status.Text = "操作已取消；编辑后将重新解析。"; }); Button(queryActions, "导出…", async (s, e) => await Export()); queryActions.Children.Add(exportPaths);
            query.Children.Add(expression); expression.ToolTip = "Json.NET JSONPath · Enter 执行 · 支持过滤、递归、切片"; expression.KeyDown += async (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Query(); } };
            expression.TextChanged += (s, e) => { querying?.Cancel(); resultsVersion = -1; matches.Text = "表达式已变更，请执行查询"; };
            DockPanel.SetDock(status, Dock.Bottom); dock.Children.Add(status);
            var bottom = new DockPanel(); DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom); DockPanel.SetDock(position, Dock.Left); bottom.Children.Add(position); bottom.Children.Add(path);
            var fileActions = new WrapPanel(); DockPanel.SetDock(fileActions, Dock.Top); dock.Children.Add(fileActions);
            Button(fileActions, "查找 · Ctrl+F", (s, e) => ShowFind()); Button(fileActions, "重新选择文件编码", async (s, e) => await owner.Reopen(this)); Button(fileActions, "设置保存编码", (s, e) => { int? encoding = EncodingDialog.Choose(owner, "选择保存编码（不改变当前文本）"); if (encoding.HasValue) { FileEncoding = FileStorage.EncodingFor(encoding.Value, encoding.Value == 1200 || encoding.Value == 1201); Dirty = true; Notify(); status.Text = "保存编码：" + FileEncoding.EncodingName; } }); Button(fileActions, "复制路径", (s, e) => Copy(path.Text)); Button(fileActions, "复制节点", (s, e) => { if (HasCurrentDocument && SelectedNode != null) Copy(Parsed.Raw(SelectedNode)); });
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 }); dock.Children.Add(grid);
            Editor.Margin = new Thickness(4, 0, 0, 0); grid.Children.Add(Editor); var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent }; Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
            right.Items.Add(new TabItem { Header = "结构树", Content = tree });
            var resultsDock = new DockPanel(); DockPanel.SetDock(matches, Dock.Top); resultsDock.Children.Add(matches); resultsDock.Children.Add(results); right.Items.Add(new TabItem { Header = "查询结果", Content = resultsDock }); Grid.SetColumn(right, 2); grid.Children.Add(right);
            VirtualizingPanel.SetIsVirtualizing(results, true); VirtualizingPanel.SetVirtualizationMode(results, VirtualizationMode.Recycling);
            results.DisplayMemberPath = "Label"; results.SelectionChanged += (s, e) => { var row = results.SelectedItem as ResultRow; if (row != null && HasCurrentDocument && resultsVersion == Version) { Navigate(row.Node, true); right.SelectedIndex = 0; } };
            tree.SelectedItemChanged += (s, e) => { var item = e.NewValue as TreeNode; if (item != null && item.Node != null && !linking && HasCurrentDocument) Navigate(item.Node, false); };
            var treeMenu = new ContextMenu(); var expand = new MenuItem { Header = "展开当前节点" }; expand.Click += (s, e) => { if (tree.SelectedItem is TreeNode item) item.IsExpanded = true; }; treeMenu.Items.Add(expand); var collapse = new MenuItem { Header = "折叠当前节点" }; collapse.Click += (s, e) => { if (tree.SelectedItem is TreeNode item) item.IsExpanded = false; }; treeMenu.Items.Add(collapse); tree.ContextMenu = treeMenu;
            treeMenu.SetResourceReference(BackgroundProperty, "SurfaceBrush"); treeMenu.SetResourceReference(ForegroundProperty, "TextBrush");
            List<MenuItem> nodeCommands = owner.CreateNodePluginCommands(CaptureContextNode);
            if (nodeCommands.Count > 0) { treeMenu.Items.Add(new Separator()); foreach (MenuItem command in nodeCommands) treeMenu.Items.Add(command); }
            tree.PreviewMouseRightButtonDown += (s, e) => SelectContextNode(e.OriginalSource as DependencyObject);
            tree.PreviewKeyDown += (s, e) => { if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) { contextNode = (tree.SelectedItem as TreeNode)?.Node; contextVersion = Version; } };
            Action updateCommands = () => { foreach (MenuItem command in nodeCommands) command.IsEnabled = HasCurrentDocument && contextNode != null && contextVersion == Version; };
            tree.ContextMenuOpening += (s, e) => updateCommands(); treeMenu.Opened += (s, e) => updateCommands();
            treeMenu.Closed += (s, e) => contextNode = null;
            search = SearchPanel.Install(Editor);
            search.Style = (Style)FindResource("EditorSearchStyle");
            search.SetBinding(MaxWidthProperty, new Binding("ActualWidth") { Source = Editor.TextArea });
            search.Localization = new ChineseSearchLocalization();
            parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) }; parseTimer.Tick += async (s, e) => { parseTimer.Stop(); await ParseNow(); };
            caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; caretTimer.Tick += (s, e) => { caretTimer.Stop(); CaretChanged(); };
            Editor.TextChanged += (s, e) =>
            {
                Version++; Dirty = !initial; parsing?.Cancel(); querying?.Cancel(); transforming?.Cancel();
                matches.Text = "结果已过期，请重新执行"; status.Text = "等待解析 · 树与查询结果已过期"; tree.Opacity = 0.45; path.Text = "";
                parseTimer.Stop(); parseTimer.Start(); Notify();
            };
            Editor.TextArea.Caret.PositionChanged += (s, e) => { if (!linking) { caretTimer.Stop(); caretTimer.Start(); } };
            status.MouseLeftButtonDown += (s, e) => { Editor.Select(Math.Min(errorOffset, Editor.Document.TextLength), 0); Editor.ScrollToLine(Editor.Document.GetLineByOffset(Math.Min(errorOffset, Editor.Document.TextLength)).LineNumber); };
            ApplyTheme();
        }
        internal void SetInitial(string text) { initial = true; try { Editor.Text = text; Editor.Document.UndoStack.ClearAll(); } finally { initial = false; Dirty = false; Notify(); } }
        internal void MarkSaved() { Dirty = false; Notify(); }
        internal void Notify() { StateChanged?.Invoke(this, EventArgs.Empty); }
        internal void ApplyTheme() { EditorStyle.Apply(Editor, owner.IsDark, true); }
        internal void ShowFind() { search.Open(); search.Reactivate(); }

        private void SelectContextNode(DependencyObject source)
        {
            contextNode = null; contextVersion = Version;
            while (source != null && source != tree)
            {
                var item = source as TreeNode;
                if (item != null) { item.IsSelected = true; contextNode = item.Node; return; }
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
        }
        private DocumentSnapshot CaptureContextNode()
        {
            if (!HasCurrentDocument || contextNode == null || contextVersion != Version) throw new InvalidOperationException("节点已过期或未选中，请在当前有效的 JSON 节点上右键生成实体类。");
            long version = Version;
            return new DocumentSnapshot(Parsed.Raw(contextNode), DisplayName, contextNode.Path, version, () => !Closed && Version == version);
        }

        internal async Task ParseNow()
        {
            parseTimer.Stop();
            parsing?.Cancel(); var cancellation = new CancellationTokenSource(); parsing = cancellation;
            long version = Version; var snapshot = Editor.Document.CreateSnapshot(); status.Text = "正在解析…";
            try
            {
                JsonDocument parsed = await Work.Run(() => JsonDocument.Parse(snapshot.Text, cancellation.Token), cancellation.Token);
                if (Closed || version != Version || parsing != cancellation || cancellation.IsCancellationRequested) return;
                Parsed = parsed; parsedVersion = version; tree.Items.Clear(); rootItem = new TreeNode(parsed, parsed.Root); tree.Items.Add(rootItem); rootItem.IsExpanded = true; tree.Opacity = 1;
                status.Text = "有效 JSON · " + parsed.Root.Kind + " · " + snapshot.TextLength.ToString("N0") + " 字符 · " + FileEncoding.EncodingName; CaretChanged();
            }
            catch (OperationCanceledException) { if (!Closed && parsing == cancellation && version == Version) status.Text = "解析已取消。"; }
            catch (JsonSyntaxException exception)
            {
                if (Closed || version != Version) return; errorOffset = Math.Min(exception.Offset, Editor.Document.TextLength); var location = Editor.Document.GetLocation(errorOffset); status.Text = "JSON 错误 · 第 " + location.Line + " 行，第 " + location.Column + " 列：" + exception.Message + "（点击定位）"; parsedVersion = -1;
            }
            catch (Exception exception) { if (!Closed && version == Version) status.Text = "解析失败：" + exception.Message; }
            finally { if (parsing == cancellation) parsing = null; cancellation.Dispose(); }
        }
        private void CaretChanged()
        {
            if (Closed) return; position.Text = "行 " + Editor.TextArea.Caret.Line + "，列 " + Editor.TextArea.Caret.Column;
            if (!HasCurrentDocument || linking) return;
            SelectedNode = Parsed.Locate(Editor.CaretOffset); path.Text = SelectedNode.Path;
            right.SelectedIndex = 0;
            linking = true; try { rootItem?.Reveal(SelectedNode); } finally { linking = false; }
            Editor.TextArea.Caret.BringCaretToView();
        }
        private void Navigate(JsonNode node, bool revealTree)
        {
            linking = true;
            try
            {
                SelectedNode = node; path.Text = node.Path;
                Editor.Select(node.Start, node.End - node.Start); Editor.ScrollToLine(Editor.Document.GetLineByOffset(node.Start).LineNumber);
                Editor.CaretOffset = node.Start;
                Editor.TextArea.Caret.BringCaretToView();
                if (revealTree) rootItem?.Reveal(node);
                position.Text = "行 " + Editor.TextArea.Caret.Line + "，列 " + Editor.TextArea.Caret.Column;
            }
            finally { linking = false; }
        }
        internal async Task Transform(bool pretty)
        {
            transforming?.Cancel(); var cancellation = new CancellationTokenSource(); transforming = cancellation;
            var snapshot = Editor.Document.CreateSnapshot(); long version = Version; status.Text = pretty ? "正在格式化…" : "正在压缩并移除注释…";
            try
            {
                string transformed = await Work.Run(() => { string text = snapshot.Text; JsonDocument.Parse(text, cancellation.Token); return JsonText.Transform(text, pretty, cancellation.Token); }, cancellation.Token);
                if (Closed || version != Version || cancellation.IsCancellationRequested) return;
                Editor.Document.BeginUpdate(); try { Editor.Document.Replace(0, Editor.Document.TextLength, transformed); } finally { Editor.Document.EndUpdate(); }
            }
            catch (OperationCanceledException) { if (!Closed && transforming == cancellation) status.Text = "转换已取消，原文未修改。"; }
            catch (Exception exception) { if (!Closed && version == Version) status.Text = "转换失败，原文未修改：" + exception.Message; }
            finally { if (transforming == cancellation) transforming = null; cancellation.Dispose(); }
        }
        internal async Task Query()
        {
            if (!HasCurrentDocument) { status.Text = "请等待有效 JSON 解析完成后查询。"; return; }
            querying?.Cancel(); var cancellation = new CancellationTokenSource(); querying = cancellation;
            resultsVersion = -1;
            long version = Version; JsonDocument document = Parsed; string queryText = expression.Text; matches.Text = "正在查询…"; right.SelectedIndex = 1;
            try
            {
                List<JsonNode> found = await Work.Run(() => document.Query(queryText, cancellation.Token), cancellation.Token);
                if (Closed || version != Version || querying != cancellation || cancellation.IsCancellationRequested) return;
                matched = found; resultsVersion = version;
                // 绑定轻量行而不是预先创建可视控件，长值按需预览。
                results.ItemsSource = found.Select(n => new ResultRow(document, n)).ToList();
                matches.Text = found.Count == 0 ? "表达式有效 · 0 个匹配" : found.Count.ToString("N0") + " 个匹配"; status.Text = "查询完成 · Json.NET JSONPath";
            }
            catch (OperationCanceledException) { if (!Closed && querying == cancellation) matches.Text = "查询已取消"; }
            catch (Exception exception) { if (!Closed && version == Version) { resultsVersion = -1; matches.Text = "查询失败：" + exception.Message; } }
            finally { if (querying == cancellation) querying = null; cancellation.Dispose(); }
        }
        private async Task Export()
        {
            if (!HasCurrentDocument || resultsVersion != Version) { status.Text = "当前查询结果无效或已过期，请先执行查询。"; return; }
            var dialog = new SaveFileDialog { Filter = "JSON 文件 (*.json)|*.json", FileName = "query-result.json" };
            if (dialog.ShowDialog(owner) != true) return;
            JsonDocument document = Parsed; var nodes = matched; bool includePaths = exportPaths.IsChecked == true; long version = Version;
            try
            {
                string text = await Work.Run(() => document.Export(nodes, includePaths), CancellationToken.None);
                if (Closed || version != Version || resultsVersion != version) { status.Text = "源已变化，已取消导出，请重新查询。"; return; }
                await FileStorage.WriteAsync(dialog.FileName, text, FileStorage.EncodingFor(65001)); if (!Closed) status.Text = "已导出 " + nodes.Count.ToString("N0") + " 项：" + dialog.FileName;
            }
            catch (Exception exception) { if (!Closed) status.Text = "导出失败：" + exception.Message; }
        }
        private void Copy(string text) { try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch (Exception exception) { status.Text = "复制失败：" + exception.Message; } }
        private static Button Button(Panel panel, string text, RoutedEventHandler handler) { var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), MinHeight = 34, MinWidth = 72 }; button.Click += handler; panel.Children.Add(button); return button; }
        public void Dispose() { Closed = true; parseTimer.Stop(); caretTimer.Stop(); parsing?.Cancel(); querying?.Cancel(); transforming?.Cancel(); Parsed = null; matched.Clear(); results.ItemsSource = null; tree.Items.Clear(); }

        private sealed class ResultRow
        {
            private readonly JsonDocument document;
            internal JsonNode Node { get; }
            internal ResultRow(JsonDocument document, JsonNode node) { this.document = document; Node = node; }
            public string Label => Node.Path + "\n" + Node.Kind + " · " + document.Preview(Node);
        }
        private sealed class ChineseSearchLocalization : ICSharpCode.AvalonEdit.Search.Localization
        {
            public override string MatchCaseText => "区分大小写";
            public override string MatchWholeWordsText => "全字匹配";
            public override string UseRegexText => "正则表达式";
            public override string FindNextText => "下一个";
            public override string FindPreviousText => "上一个";
            public override string ErrorText => "查找表达式错误";
            public override string NoMatchesFoundText => "没有找到匹配项";
        }
        private sealed class TreeNode : TreeViewItem
        {
            private readonly JsonDocument document;
            private readonly JsonNode container;
            private readonly int from, to;
            private bool loaded;
            internal JsonNode Node { get; }
            internal TreeNode(JsonDocument document, JsonNode node)
            {
                this.document = document; Node = node; container = node; from = 0; to = node.Children?.Count ?? 0;
                Header = (node.Parent == null ? "$" : node.Parent.Kind == "array" ? "[" + node.Index + "]" : node.Name) + "   " + document.Preview(node);
                ToolTip = "点击定位原文";
                if (to > 0) Items.Add(new TreeViewItem()); Expanded += OnExpanded;
            }
            private TreeNode(JsonDocument document, JsonNode container, int from, int to)
            {
                this.document = document; this.container = container; this.from = from; this.to = to;
                Header = "项目 " + from.ToString("N0") + " — " + (to - 1).ToString("N0") + "（按需加载）"; Items.Add(new TreeViewItem()); Expanded += OnExpanded;
            }
            private void OnExpanded(object sender, RoutedEventArgs e) { if (e.OriginalSource == this) Load(); }
            private void Load()
            {
                if (loaded) return; loaded = true; Items.Clear(); int count = to - from;
                if (count <= 200) for (int i = from; i < to; i++) Items.Add(new TreeNode(document, container.Children[i]));
                else { int chunk = 200; while ((count + chunk - 1) / chunk > 200) chunk *= 200; for (int i = from; i < to; i += chunk) Items.Add(new TreeNode(document, container, i, Math.Min(to, i + chunk))); }
            }
            internal void Reveal(JsonNode target)
            {
                if (Node == target) { IsSelected = true; BringIntoView(); return; }
                JsonNode child = target; while (child.Parent != null && child.Parent != container) child = child.Parent;
                if (child.Parent != container) return; IsExpanded = true; Load();
                foreach (TreeNode item in Items)
                {
                    if (item.Node == child) { item.Reveal(target); return; }
                    if (item.Node == null && child.Index >= item.from && child.Index < item.to) { item.Reveal(target); return; }
                }
            }
        }
    }
}
