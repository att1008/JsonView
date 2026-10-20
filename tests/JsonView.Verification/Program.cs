using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JsonView;
using JsonView.Contracts;
using JsonView.Core;
using JsonView.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonView.Verification
{
    internal static class Program
    {
        private static int assertions;
        private static string artifacts;
        private static string largeText;
        [STAThread]
        private static int Main(string[] args)
        {
            artifacts = Path.GetFullPath(args.Length == 0 ? "artifacts/verification" : args[0]); Directory.CreateDirectory(artifacts);
            try
            {
                CoreChecks(); FileChecks().GetAwaiter().GetResult(); GeneratorChecks(); Performance(); UiChecks();
                Console.WriteLine("PASS · " + assertions + " checks"); return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
        private static void Check(bool condition, string name) { assertions++; if (!condition) throw new InvalidOperationException("FAIL: " + name); }
        private static void Reject(string text) { try { JsonDocument.Parse(text); throw new InvalidOperationException("接受了非法输入：" + text); } catch (JsonSyntaxException) { assertions++; } }
        private static void CoreChecks()
        {
            foreach (string text in new[] { "{}", "[]", "\"hello\"", "12", "true", "false", "null", "/*header*/ {\"a\":1 //tail\n} /*end*/" }) Check(JsonDocument.Parse(text).Root != null, "根值/注释");
            foreach (string text in new[] { "", "//only", "{\"a\":1,}", "[1,]", "{'a':1}", "{a:1}", "01", "+1", "1.", "1e", "NaN", "true false", "{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "\"bad\\x\"", "\"bad\n\"", "/*end", "[" }) Reject(text);
            string raw = "/* hi */ {\"items\":[{\"name\":\"甲\",\"age\":17},{\"name\":\"乙\",\"age\":21},{\"name\":\"丙\",\"age\":28}],\"a.b\":{\"q'\\\\x\":true},\"num\":1234567890123456789012345678901234567890,\"fraction\":0.123456789012345678901234567890123,\"url\":\"https://x/*ok*/\"}";
            JsonDocument document = JsonDocument.Parse(raw);
            string compact = JsonText.Transform(raw, false); string pretty = JsonText.Transform(raw, true);
            Check(!compact.StartsWith("/*"), "压缩移除注释"); Check(pretty.Contains("/* hi */"), "格式化保留注释");
            Check(compact.Contains("1234567890123456789012345678901234567890") && compact.Contains("0.123456789012345678901234567890123"), "数值原文无损");
            Check(JsonText.Transform(pretty, false) == compact, "格式化/压缩往返"); JsonDocument.Parse(pretty);
            // 查询引擎对超 decimal 精确范围小数显式拒绝，不能悄悄舍入。
            try { document.Query("$"); throw new Exception("查询未拒绝不可精确表示的小数"); } catch (InvalidOperationException) { assertions++; }
            var queryDoc = JsonDocument.Parse("{\"items\":[{\"name\":\"甲\",\"age\":17},{\"name\":\"乙\",\"age\":21},{\"name\":\"丙\",\"age\":28}],\"a.b\":{\"q'\\\\x\":true},\"\":false,\"中文\":null}");
            Check(queryDoc.Query("$").Count == 1, "根查询"); Check(queryDoc.Query("$..name").Count == 3, "递归查询");
            Check(queryDoc.Query("$.items[?(@.age >= 18)]").Count == 2, "过滤查询"); Check(queryDoc.Query("$.items[0:3:2]").Count == 2, "切片查询");
            Check(queryDoc.Query("$.items[*].name").Count == 3, "通配符"); Check(queryDoc.Query("$.none").Count == 0, "无匹配");
            foreach (JsonNode node in queryDoc.Root.Children.SelectMany(n => n.Children ?? new System.Collections.Generic.List<JsonNode> { n })) Check(queryDoc.Query(node.Path).Single() == node, "路径回查 " + node.Path);
            JsonNode special = queryDoc.Root.Children[1].Children[0]; Check(queryDoc.Query(special.Path).Single() == special, "转义路径");
            Check(queryDoc.Locate(special.Start) == special && queryDoc.Locate(special.KeyStart) == special, "双向位置索引");
            Check(document.Export(new[] { document.Root.Children[2] }, false).Contains("1234567890123456789012345678901234567890"), "导出数值无损");
            Check(JArray.Parse(queryDoc.Export(queryDoc.Query("$..name"), true)).Count == 3, "路径记录导出");
            Check(queryDoc.Export(new JsonNode[0], false) == "[]", "空结果导出");
            using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); try { JsonDocument.Parse(raw, cancellation.Token); throw new Exception("未取消"); } catch (OperationCanceledException) { assertions++; } }
            Reject(new string('[', 260) + "0" + new string(']', 260));
            Console.WriteLine("Core checks passed");
        }
        private static async Task FileChecks()
        {
            foreach (int codePage in new[] { 65001, 936, 54936, 1200, 1201 })
            {
                string text = "{\"中文\":\"编码测试\"}\r\n"; string path = Path.Combine(artifacts, "encoding-" + codePage + ".json"); var encoding = FileStorage.EncodingFor(codePage, codePage == 1200 || codePage == 1201);
                await FileStorage.WriteAsync(path, text, encoding); FileContent read = await FileStorage.ReadAsync(path, codePage == 936 || codePage == 54936 ? codePage : 0); Check(read.Text == text, "编码往返 " + codePage);
                await FileStorage.WriteAsync(path, text + " ", encoding); Check((await FileStorage.ReadAsync(path, codePage)).Text == text + " ", "安全替换/手工编码 " + codePage);
            }
            string protectedPath = Path.Combine(artifacts, "protected.json"); await FileStorage.WriteAsync(protectedPath, "original", FileStorage.EncodingFor(65001));
            try { await FileStorage.WriteAsync(protectedPath, "\U0001F600", FileStorage.EncodingFor(936)); throw new Exception("GBK 未拒绝不能编码的字符"); } catch (EncoderFallbackException) { assertions++; }
            Check(File.ReadAllText(protectedPath) == "original", "写入失败保护原文件");
            Check(Directory.GetFiles(artifacts, "*.tmp").Length == 0, "临时文件清理"); Console.WriteLine("File checks passed");
        }
        private static void GeneratorChecks()
        {
            const string input = "{\"user_id\":123,\"user_name\":\"Alice\",\"enabled\":false,\"tags\":[\"a\",\"b\"],\"scores\":[1,2],\"child\":{\"x\":1}}";
            string compiler = FindCompiler();
            string framework = Environment.GetEnvironmentVariable("JSONVIEW_FRAMEWORK_REFERENCES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.7.2");
            if (!File.Exists(Path.Combine(framework, "mscorlib.dll"))) throw new DirectoryNotFoundException("找不到 net472 参考程序集，请安装 Developer Pack 或设置 JSONVIEW_FRAMEWORK_REFERENCES。");
            int sequence = 0;
            foreach (bool systemText in new[] { false, true })
            foreach (int mask in new[] { 0, 1, 2, 4, 8, 16, 32, 63 })
            {
                var options = new GenerationOptions { SystemTextJson = systemText, PascalCase = (mask & 1) != 0, NullableValues = (mask & 2) != 0, NullableReferences = (mask & 4) != 0, IgnoreNull = (mask & 8) != 0, Records = (mask & 16) != 0, ReadonlyLists = (mask & 32) != 0 };
                GenerationResult generated = EntityGenerator.Generate(input, options); string prefix = Path.Combine(artifacts, "model-" + ++sequence);
                string compatibility = options.Records ? "\r\nnamespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }\r\n" : "";
                File.WriteAllText(prefix + ".cs", generated.Code + compatibility, new UTF8Encoding(false));
                string[] references = { Path.Combine(framework, "mscorlib.dll"), Path.Combine(framework, "System.dll"), Path.Combine(framework, "System.Core.dll"), typeof(JsonConvert).Assembly.Location, typeof(System.Text.Json.JsonSerializer).Assembly.Location, typeof(System.Memory<>).Assembly.Location, typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly.Location, typeof(System.Threading.Tasks.ValueTask).Assembly.Location, typeof(System.Buffers.ArrayPool<>).Assembly.Location, Path.Combine(framework, "Facades", "netstandard.dll") };
                var info = new ProcessStartInfo(compiler) { Arguments = "/nologo /target:library /langversion:9 /noconfig /nostdlib+ /out:\"" + prefix + ".dll\" " + string.Join(" ", references.Distinct().Select(r => "/r:\"" + r + "\"")) + " \"" + prefix + ".cs\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process process = Process.Start(info)) { string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode == 0, "生成代码编译：" + output); }
                Assembly assembly = Assembly.LoadFrom(prefix + ".dll"); Type type = assembly.GetType("Generated.Root", true);
                object model = systemText ? System.Text.Json.JsonSerializer.Deserialize(input, type) : JsonConvert.DeserializeObject(input, type);
                string userId = options.PascalCase ? "UserId" : "user_id"; Check(Convert.ToInt32(type.GetProperty(userId).GetValue(model)) == 123, "模型反序列化");
                string name = options.PascalCase ? "UserName" : "user_name"; type.GetProperty(name).SetValue(model, null);
                string json = systemText ? System.Text.Json.JsonSerializer.Serialize(model, type) : JsonConvert.SerializeObject(model); JObject roundTrip = JObject.Parse(json);
                Check((int)roundTrip["user_id"] == 123 && (bool)roundTrip["enabled"] == false, "原始名称/默认值往返");
                Check(options.IgnoreNull ? roundTrip.Property("user_name") == null : roundTrip.Property("user_name") != null, "忽略 null");
            }
            GenerationResult merged = EntityGenerator.Generate("[{\"a\":1},{\"b\":2,\"a\":null}]", new GenerationOptions()); Check(merged.Code.Contains(" B ") && merged.Diagnostics.Contains("缺失"), "全样本字段合并");
            var conflict = EntityGenerator.Generate("{\"user_id\":1,\"userId\":2,\"\":null,\"class\":3,\"a\\\"b\":4}", new GenerationOptions()); Check(conflict.Code.Contains("UserId2") && conflict.Code.Contains("JsonProperty"), "名称消歧");
            Check(EntityGenerator.Generate("[[],[1,2]]", new GenerationOptions()).RootType == "List<List<int>>", "空数组不覆盖其他样本类型");
            Console.WriteLine("Generated models compiled and round-tripped: " + sequence);
        }
        private static string FindCompiler()
        {
            string configured = Environment.GetEnvironmentVariable("JSONVIEW_CSC");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!File.Exists(configured)) throw new FileNotFoundException("JSONVIEW_CSC 指定的编译器不存在。", configured);
                return Path.GetFullPath(configured);
            }
            string locator = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(locator))
            {
                var info = new ProcessStartInfo(locator) { Arguments = "-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\Roslyn\\csc.exe", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd(); process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidOperationException("vswhere 查找编译器失败：" + error);
                    string found = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists);
                    if (found != null) return found;
                }
            }
            throw new FileNotFoundException("需要支持 C# 9 的 Visual Studio Roslyn 编译器。请安装相关生成工具，或设置 JSONVIEW_CSC 为 csc.exe 完整路径。");
        }
        private static void Performance()
        {
            var builder = new StringBuilder("[");
            for (int i = 0; i < 120000; i++) { if (i != 0) builder.Append(','); builder.Append("{\"id\":").Append(i).Append(",\"payload\":\"").Append('x', 230).Append("\"}"); }
            string text = builder.Append(']').ToString(); largeText = text; var watch = Stopwatch.StartNew(); JsonDocument document = JsonDocument.Parse(text); long parse = watch.ElapsedMilliseconds;
            watch.Restart(); var found = document.Query("$[119999]"); long query = watch.ElapsedMilliseconds;
            watch.Restart(); var generated = EntityGenerator.Generate(text, new GenerationOptions()); long generation = watch.ElapsedMilliseconds;
            Check(document.Root.Children.Count == 120000 && found.Single().Index == 119999, "30 MB / 12 万元素解析查询"); Check(generated.Code.Contains("Payload"), "大文件全样本生成");
            Process.GetCurrentProcess().Refresh(); string metrics = "UTF8 bytes=" + Encoding.UTF8.GetByteCount(text) + "\r\nitems=120000\r\nparse_ms=" + parse + "\r\nquery_ms=" + query + "\r\ngeneration_ms=" + generation + "\r\nworking_set=" + Process.GetCurrentProcess().WorkingSet64;
            File.WriteAllText(Path.Combine(artifacts, "performance.txt"), metrics); Console.WriteLine(metrics);
        }
        private static void UiChecks()
        {
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var manager = new PluginManager(new string[0]); manager.Discover(Path.GetFullPath("src/JsonView/bin/Release/net472/Plugins")); Check(manager.Entries.Count == 1 && manager.Entries[0].Instance != null, "默认 DLL 插件实际发现与加载");
            var disabled = new PluginManager(new[] { "jsonview.csharp" }); disabled.Discover(Path.GetFullPath("src/JsonView/bin/Release/net472/Plugins")); Check(disabled.Entries[0].Instance == null && !disabled.Entries[0].Enabled, "禁用插件不执行加载");
            var window = new MainWindow(Path.Combine(artifacts, "ui-settings-" + Guid.NewGuid().ToString("N") + ".json"), Path.GetFullPath("src/JsonView/bin/Release/net472/Plugins")); app.MainWindow = window;
            Exception failure = null;
            window.Loaded += async (s, e) =>
            {
                try
                {
                    DocumentPage page = window.ActivePage; page.SetInitial("{\n  // 双向定位与查询\n  \"users\": [{\"user_id\": 1, \"name\": \"Alice\"}, {\"user_id\": 2, \"name\": \"Bob\"}],\n  \"enabled\": true,\n  \"tags\": [\"WPF\", \"JSONPath\"]\n}");
                    await page.ParseNow(); Check(page.HasCurrentDocument, "WPF 解析"); await page.Query(); Check(window.GetSnapshot(false).Text.Contains("users"), "插件快照");
                    page.Editor.CaretOffset = page.Editor.Text.IndexOf("Alice", StringComparison.Ordinal); await Task.Delay(180); Check(page.SelectedNode.Path.Contains("name"), "光标反向定位树");
                    JsonNode target = page.Parsed.Root.Children[0].Children[1]; typeof(DocumentPage).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, new object[] { target, true }); Check(page.Editor.CaretOffset == target.Start && page.Editor.SelectionLength == target.End - target.Start, "树到文本区间高亮与起点定位");
                    Capture(window, "light.png");
                    await SearchLayoutChecks(window, page);
                    typeof(MainWindow).GetField("preferences", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window).GetType().GetProperty("Dark").SetValue(typeof(MainWindow).GetField("preferences", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window), true); window.ApplyTheme();
                    Capture(window, "dark.png");
                    page.ShowFind(); window.UpdateLayout(); Capture(window, "search-dark.png");
                    ((ICSharpCode.AvalonEdit.Search.SearchPanel)typeof(DocumentPage).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page)).Close();
                    new CSharpPlugin().Execute(window); await Task.Delay(700); Window generator = window.OwnedWindows.Cast<Window>().First(); Check(generator.Title.Contains("C#"), "生成插件窗口");
                    await (Task)generator.GetType().GetMethod("Generate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(generator, null);
                    Check(((ICSharpCode.AvalonEdit.TextEditor)generator.GetType().GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(generator)).Text.Contains("class Root"), "重复生成及高亮文本预览");
                    Capture(generator, "generator.png"); generator.Close();
                    await NodeMenuChecks(window, page);
                    JsonDocument old = page.Parsed; page.Editor.AppendText(" "); Check(!page.HasCurrentDocument, "修改使旧数据失效"); await page.ParseNow(); Check(page.HasCurrentDocument && page.Parsed != old, "新版本解析");
                    await page.Transform(false); Check(!page.Editor.Text.Contains("// 双向"), "WPF 压缩移除注释"); page.Editor.Undo(); Check(page.Editor.Text.Contains("// 双向"), "转换单步撤销");
                    var watch = Stopwatch.StartNew(); DocumentPage large = window.NewDocument(largeText); await large.ParseNow(); Check(large.HasCurrentDocument && large.Parsed.Root.Children.Count == 120000, "大文件实际 WPF 解析和树加载");
                    large.Editor.CaretOffset = large.Editor.Document.TextLength - 10;
                    var navigationWait = Stopwatch.StartNew();
                    while ((!large.SelectedNode.Path.Contains("119999") || large.Editor.HorizontalOffset <= 0) && navigationWait.ElapsedMilliseconds < 5000) await Task.Delay(25);
                    Check(large.SelectedNode.Path.Contains("119999"), "大文件末尾反向定位");
                    Check(large.Editor.HorizontalOffset > 0, "超长行定位同步水平滚动");
                    Check(large.Editor.Text == largeText, "超长行折叠不修改原文");
                    Capture(window, "large-line.png");
                    File.AppendAllText(Path.Combine(artifacts, "performance.txt"), "\r\nwpf_large_load_and_navigation_ms=" + watch.ElapsedMilliseconds + "\r\nui_working_set=" + Process.GetCurrentProcess().WorkingSet64);
                    large.Dispose();
                    foreach (var tab in window.Documents.Items.Cast<System.Windows.Controls.TabItem>()) ((DocumentPage)tab.Content).MarkSaved();
                }
                catch (Exception exception) { failure = exception; }
                finally { foreach (var tab in window.Documents.Items.Cast<System.Windows.Controls.TabItem>()) ((DocumentPage)tab.Content).MarkSaved(); window.Close(); app.Shutdown(); }
            };
            window.Show(); app.Run(); if (failure != null) throw new Exception("WPF 验证失败", failure); Console.WriteLine("WPF checks passed");
        }
        private static async Task SearchLayoutChecks(MainWindow window, DocumentPage page)
        {
            page.ShowFind(); await Task.Delay(80); window.UpdateLayout();
            var search = (ICSharpCode.AvalonEdit.Search.SearchPanel)typeof(DocumentPage).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
            var input = (TextBox)search.Template.FindName("PART_searchTextBox", search);
            input.Text = "user_id"; search.FindNext();
            Check(page.Editor.SelectedText == "user_id" && search.SearchPattern == "user_id", "自定义查找模板输入绑定及查找");
            int offset = page.Editor.SelectionStart; search.FindNext(); Check(page.Editor.SelectionStart != offset, "查找下一个"); search.FindPrevious(); Check(page.Editor.SelectionStart == offset, "查找上一个");
            Check(Descendants<Button>(search).Count() == 3, "查找完整包含上下项和关闭按钮");
            CheckSearchBounds(search); Capture(window, "search-light.png");
            page.Width = 620; page.FontSize = 18; window.Width = 900; window.UpdateLayout(); await Task.Delay(80); window.UpdateLayout();
            CheckSearchBounds(search);
            var actions = Descendants<WrapPanel>(page).First(p => p.Children.OfType<Button>().Any(b => (string)b.Content == "重新选择文件编码"));
            Check(actions.Children.OfType<Button>().Select(b => Math.Round(b.TranslatePoint(new Point(), actions).Y)).Distinct().Count() > 1, "操作栏空间不足时换行");
            foreach (Button button in actions.Children.OfType<Button>()) CheckButtonContent(button);
            Grid grid = Descendants<Grid>(page).First(g => g.Children.Contains(page.Editor)); grid.ColumnDefinitions[0].Width = new GridLength(260); window.UpdateLayout(); await Task.Delay(80); window.UpdateLayout();
            CheckSearchBounds(search); Check(search.ActualWidth <= page.Editor.TextArea.ActualWidth + 1, "窄编辑列查找面板不超出可视宽度"); Capture(window, "search-narrow.png");
            Check(page.Editor.Text.Contains("users"), "查找和布局调整不改写文档");
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star); page.ClearValue(FrameworkElement.WidthProperty); page.ClearValue(Control.FontSizeProperty); window.Width = 1320; window.UpdateLayout();
            var close = Descendants<Button>(search).Single(b => (string)b.Content == "×");
            ((RoutedCommand)close.Command).Execute(null, search); Check(search.IsClosed, "查找关闭按钮命令");
        }
        private static void CheckSearchBounds(ICSharpCode.AvalonEdit.Search.SearchPanel search)
        {
            foreach (FrameworkElement control in Descendants<FrameworkElement>(search).Where(c => c is Button || c is TextBox || c is CheckBox))
            {
                Rect bounds = control.TransformToAncestor(search).TransformBounds(new Rect(control.RenderSize));
                Check(bounds.Left >= -1 && bounds.Right <= search.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= search.ActualHeight + 1, "查找控件完整显示：" + control.GetType().Name);
                if (control is Button button) CheckButtonContent(button);
            }
        }
        private static void CheckButtonContent(Button button)
        {
            string text = button.Content as string; if (text == null) return;
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(button).PixelsPerDip);
            Check(button.ActualWidth + 1 >= formatted.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right + 2, "按钮文字宽度完整：" + text);
        }
        private static async Task NodeMenuChecks(MainWindow window, DocumentPage page)
        {
            var tree = (TreeView)typeof(DocumentPage).GetField("tree", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
            var rootItem = (TreeViewItem)tree.Items[0]; var users = (TreeViewItem)rootItem.Items[0]; users.IsExpanded = true; window.UpdateLayout();
            var first = (TreeViewItem)users.Items[0]; var second = (TreeViewItem)users.Items[1]; first.IsSelected = true;
            var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            second.RaiseEvent(rightClick); Check(second.IsSelected && page.SelectedNode == page.Parsed.Root.Children[0].Children[1], "右键自动选中所点节点而非之前节点");
            tree.ContextMenu.PlacementTarget = second; tree.ContextMenu.IsOpen = true; await Task.Delay(60);
            // 通过实际注册的菜单命令执行，验证插件入口没有绕过节点快照。
            var command = tree.ContextMenu.Items.OfType<MenuItem>().Single(m => ((string)m.Header).Contains("生成此节点"));
            Check(command.IsEnabled, "有效真实节点启用右键插件命令");
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(200);
            Window generator = window.OwnedWindows.Cast<Window>().Single();
            await (Task)generator.GetType().GetMethod("Generate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(generator, null);
            var preview = (ICSharpCode.AvalonEdit.TextEditor)generator.GetType().GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(generator);
            Check(generator.Title.Contains("[1]") && preview.Text.Contains("UserId") && preview.Text.Contains("Name") && !preview.Text.Contains(" Enabled ") && !preview.Text.Contains(" Users "), "右键只生成选中子树，不包含父级或兄弟字段");
            var selected = (CheckBox)generator.GetType().GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(generator);
            Check(selected.IsChecked == true && !selected.IsEnabled, "右键入口自动锁定子树范围");
            Capture(generator, "node-generator.png");
            DocumentPage other = window.NewDocument("{\"unrelated\":true}"); await other.ParseNow();
            await (Task)generator.GetType().GetMethod("Generate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(generator, null);
            Check(preview.Text.Contains("UserId") && !preview.Text.Contains("Unrelated"), "切换标签再生成仍使用原节点快照");
            window.Documents.SelectedItem = window.Documents.Items.Cast<TabItem>().Single(tab => tab.Content == page);
            generator.Close(); other.MarkSaved();
            tree.ContextMenu.IsOpen = false; await Task.Delay(60);
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            tree.ContextMenu.IsOpen = true; await Task.Delay(60); Check(!command.IsEnabled, "树空白处不沿用上次节点生成"); tree.ContextMenu.IsOpen = false; await Task.Delay(60);
            second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            page.Editor.AppendText(" "); await page.ParseNow(); tree.ContextMenu.IsOpen = true; await Task.Delay(60); Check(!command.IsEnabled, "原文重新解析后旧右键节点失效"); tree.ContextMenu.IsOpen = false;
        }
        private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i); if (child is T found) yield return found;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        private static void Capture(Window window, string name)
        {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(artifacts, name))) encoder.Save(file);
        }
    }
}
