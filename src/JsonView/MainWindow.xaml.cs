using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JsonView.Contracts;
using JsonView.Core;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace JsonView
{
    public partial class MainWindow : Window, IPluginContext
    {
        private sealed class Preferences { public bool Dark { get; set; } public List<string> Disabled { get; set; } = new List<string>(); }
        private readonly string preferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsonView", "settings.json");
        private Preferences preferences = new Preferences();
        private readonly PluginManager plugins;
        private bool allowClose, closing;
        private int untitled;
        public bool IsDark => preferences.Dark;
        Window IPluginContext.Owner => this;
        public event EventHandler ThemeChanged;
        internal DocumentPage ActivePage => (Documents.SelectedItem as TabItem)?.Content as DocumentPage;

        /// <summary>创建 JSON 查看器主窗口。</summary>
        public MainWindow() : this(null) { }
        internal MainWindow(string settingsPath, string pluginDirectory = null)
        {
            if (settingsPath != null) preferencesPath = settingsPath;
            InitializeComponent();
            try { if (File.Exists(preferencesPath)) preferences = JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(preferencesPath)) ?? new Preferences(); }
            catch (Exception exception) { AppStatus.Text = "偏好设置读取失败，使用默认值：" + exception.Message; }
            plugins = new PluginManager(preferences.Disabled); plugins.Discover(pluginDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins")); ApplyTheme();
            NewDocument(); Closing += OnClosing;
            PreviewKeyDown += async (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                if (e.Key == Key.N) { NewDocument(); e.Handled = true; }
                else if (e.Key == Key.O) { e.Handled = true; await ChooseFiles(); }
                else if (e.Key == Key.S) { e.Handled = true; if (ActivePage != null) await SavePage(ActivePage, false); }
                else if (e.Key == Key.W) { e.Handled = true; if (ActivePage != null) await ClosePage(ActivePage); }
                else if (e.Key == Key.F && ActivePage != null) { e.Handled = true; ActivePage.ShowFind(); }
            };
            DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Drop += async (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Handled = true; foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop)) await OpenFileAsync(file); } };
            Loaded += async (s, e) => { foreach (string file in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists)) await OpenFileAsync(file); };
        }

        internal DocumentPage NewDocument(string text = "")
        {
            var page = new DocumentPage(this) { DisplayName = "未命名 " + ++untitled };
            AddPage(page); page.SetInitial(text); return page;
        }
        private void AddPage(DocumentPage page)
        {
            var header = new DockPanel(); var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(10, 0, 0, 0), ToolTip = "关闭标签 · Ctrl+W" };
            header.Children.Add(label); header.Children.Add(close);
            var tab = new TabItem { Header = header, Content = page };
            page.StateChanged += (s, e) => { label.Text = page.DisplayName + (page.Dirty ? " •" : ""); tab.ToolTip = page.FilePath ?? "未保存文档"; };
            close.Click += async (s, e) => { e.Handled = true; await ClosePage(page); };
            label.Text = page.DisplayName; Documents.Items.Add(tab); Documents.SelectedItem = tab;
        }

        /// <summary>异步打开文件，重复路径激活原有页。</summary>
        /// <param name="path">文件路径。</param>
        /// <returns>打开任务。</returns>
        public async Task OpenFileAsync(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                DocumentPage existing = Documents.Items.Cast<TabItem>().Select(t => (DocumentPage)t.Content).FirstOrDefault(p => string.Equals(p.FilePath, full, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { Documents.SelectedItem = Documents.Items.Cast<TabItem>().First(t => t.Content == existing); return; }
                var page = new DocumentPage(this) { FilePath = full, DisplayName = Path.GetFileName(full) }; AddPage(page);
                await ReadIntoPage(page, 0);
            }
            catch (Exception exception) { ShowError(exception); }
        }
        private async Task ReadIntoPage(DocumentPage page, int codePage)
        {
            page.IsLoading = true; page.ReadFailed = true; page.Editor.IsReadOnly = true; AppStatus.Text = "正在读取：" + page.FilePath;
            try
            {
                FileContent content = await FileStorage.ReadAsync(page.FilePath, codePage);
                if (!page.Closed) { page.FileEncoding = content.Encoding; page.ReadFailed = false; page.SetInitial(content.Text); AppStatus.Text = "已打开 · " + content.Encoding.EncodingName + " · " + page.FilePath; }
            }
            catch (DecoderFallbackException)
            {
                if (page.Closed) return;
                int? chosen = EncodingDialog.Choose(this, "无法按此编码读取，请选择 GBK / GB18030 或其他编码。");
                if (chosen.HasValue) await ReadIntoPage(page, chosen.Value);
                else AppStatus.Text = "未读取文件，可通过“编码重开”再次选择。";
            }
            catch (Exception exception) { ShowError(exception); }
            finally { page.IsLoading = false; if (!page.Closed) page.Editor.IsReadOnly = false; }
        }
        internal async Task Reopen(DocumentPage page)
        {
            if (page.FilePath == null || page.IsLoading || !await MayClose(page)) return;
            int? encoding = EncodingDialog.Choose(this, "选择文件编码重新打开");
            if (encoding.HasValue) await ReadIntoPage(page, encoding.Value);
        }

        internal async Task<bool> SavePage(DocumentPage page, bool saveAs)
        {
            if (page.IsLoading || page.IsSaving) return false;
            if (page.ReadFailed && !saveAs) { ShowError(new IOException("文件尚未成功读取。请先通过编码重开，或另存为其他文件。")); return false; }
            string path = page.FilePath;
            if (saveAs || path == null)
            {
                var dialog = new SaveFileDialog { Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*", FileName = page.FilePath == null ? "document.json" : Path.GetFileName(page.FilePath), DefaultExt = ".json" };
                if (dialog.ShowDialog(this) != true) return false; path = dialog.FileName;
                if (Documents.Items.Cast<TabItem>().Select(t => (DocumentPage)t.Content).Any(p => p != page && string.Equals(p.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) { ShowError(new IOException("此路径已在另一个标签页打开，请选择其他文件名。")); return false; }
            }
            string text = page.Editor.Text; long version = page.Version; Encoding encoding = page.FileEncoding; page.IsSaving = true;
            try
            {
                await FileStorage.WriteAsync(path, text, encoding);
                bool unchanged = page.Version == version && ReferenceEquals(page.FileEncoding, encoding);
                if (!page.Closed) { page.FilePath = Path.GetFullPath(path); page.DisplayName = Path.GetFileName(path); if (unchanged) page.MarkSaved(); else page.Notify(); }
                AppStatus.Text = "已保存：" + path; return unchanged;
            }
            catch (EncoderFallbackException) { ShowError(new IOException("当前编码无法表示部分字符。请在编码菜单切换为 UTF-8 后保存。")); return false; }
            catch (Exception exception) { ShowError(exception); return false; }
            finally { page.IsSaving = false; }
        }
        private async Task<bool> MayClose(DocumentPage page)
        {
            if (page.IsSaving) { AppStatus.Text = "正在保存，请稍后关闭。"; return false; }
            if (!page.Dirty) return true;
            MessageBoxResult answer = MessageBox.Show(this, "是否保存“" + page.DisplayName + "”的修改？", "未保存内容", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SavePage(page, false));
        }
        private async Task ClosePage(DocumentPage page)
        {
            if (!await MayClose(page)) return; page.Dispose();
            TabItem tab = Documents.Items.Cast<TabItem>().FirstOrDefault(t => t.Content == page); if (tab != null) Documents.Items.Remove(tab);
            if (Documents.Items.Count == 0 && !closing) NewDocument();
        }
        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (allowClose) return; e.Cancel = true; if (closing) return; closing = true; IsEnabled = false;
            foreach (DocumentPage page in Documents.Items.Cast<TabItem>().Select(t => (DocumentPage)t.Content).ToList()) if (!await MayClose(page)) { closing = false; IsEnabled = true; return; }
            foreach (TabItem tab in Documents.Items) ((DocumentPage)tab.Content).Dispose();
            await SavePreferences(); allowClose = true; Close();
        }
        private async Task ChooseFiles()
        {
            var dialog = new OpenFileDialog { Filter = "JSON / JSONC (*.json;*.jsonc)|*.json;*.jsonc|所有文件 (*.*)|*.*", Multiselect = true };
            if (dialog.ShowDialog(this) == true) foreach (string file in dialog.FileNames) await OpenFileAsync(file);
        }
        private void NewClick(object sender, RoutedEventArgs e) { NewDocument(); }
        private async void OpenClick(object sender, RoutedEventArgs e) { await ChooseFiles(); }
        private async void SaveClick(object sender, RoutedEventArgs e) { if (ActivePage != null) await SavePage(ActivePage, false); }
        private async void SaveAsClick(object sender, RoutedEventArgs e) { if (ActivePage != null) await SavePage(ActivePage, true); }
        private async void FormatClick(object sender, RoutedEventArgs e) { if (ActivePage != null) await ActivePage.Transform(true); }
        private async void CompactClick(object sender, RoutedEventArgs e) { if (ActivePage != null) await ActivePage.Transform(false); }
        private async void ThemeClick(object sender, RoutedEventArgs e) { preferences.Dark = !preferences.Dark; ApplyTheme(); await SavePreferences(); }
        internal void ApplyTheme()
        {
            string[] keys = { "BackgroundBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "HoverBrush" };
            string[] colors = preferences.Dark ? new[] { "#141C28", "#1C2736", "#DDE6F3", "#98A9C0", "#344256", "#80B1FF", "#2B3B52" } : new[] { "#F3F5F8", "#FFFFFF", "#25334A", "#6B788C", "#DAE1EC", "#3779D6", "#E8EFFA" };
            for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            Application.Current.Resources[SystemColors.WindowBrushKey] = Application.Current.Resources["SurfaceBrush"];
            Application.Current.Resources[SystemColors.WindowTextBrushKey] = Application.Current.Resources["TextBrush"];
            Application.Current.Resources[SystemColors.ControlBrushKey] = Application.Current.Resources["BackgroundBrush"];
            Application.Current.Resources[SystemColors.ControlTextBrushKey] = Application.Current.Resources["TextBrush"];
            Application.Current.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Application.Current.Resources["HoverBrush"];
            Application.Current.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Application.Current.Resources["TextBrush"];
            ThemeButton.Content = preferences.Dark ? "切换浅色" : "切换暗色";
            foreach (TabItem tab in Documents.Items) ((DocumentPage)tab.Content).ApplyTheme();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        private async Task SavePreferences()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)); preferences.Disabled = plugins.Disabled.ToList(); await FileStorage.WriteAsync(preferencesPath, JsonConvert.SerializeObject(preferences, Formatting.Indented), FileStorage.EncodingFor(65001)); }
            catch (Exception exception) { AppStatus.Text = "偏好设置保存失败：" + exception.Message; }
        }
        private void PluginsClick(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { Background = (Brush)FindResource("SurfaceBrush"), Foreground = (Brush)FindResource("TextBrush") };
            foreach (PluginEntry entry in plugins.Entries.Where(p => p.Instance != null))
            {
                var command = new MenuItem { Header = entry.Instance.CommandText };
                command.Click += (s, args) => { try { entry.Instance.Execute(this); } catch (Exception exception) { ShowError(exception); } }; menu.Items.Add(command);
            }
            menu.Items.Add(new Separator()); var manage = new MenuItem { Header = "插件管理…" }; manage.Click += (s, args) => ManagePlugins(); menu.Items.Add(manage); menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
        }
        private void ManagePlugins()
        {
            var window = new Window { Owner = this, Title = "插件管理 · 修改后重启生效", Width = 650, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(18) }; window.Content = new ScrollViewer { Content = panel };
            panel.Children.Add(new TextBlock { Text = "本地可信 DLL · Plugins 子目录 · 安装、更新与启用状态重启生效", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            foreach (PluginEntry entry in plugins.Entries)
            {
                var box = new CheckBox { Content = entry.Name + "  " + entry.Version, IsChecked = !plugins.Disabled.Contains(entry.Id ?? ""), IsEnabled = !string.IsNullOrEmpty(entry.Id), Margin = new Thickness(0, 8, 0, 4) };
                box.Click += async (s, e) => { if (box.IsChecked == true) plugins.Disabled.Remove(entry.Id); else plugins.Disabled.Add(entry.Id); await SavePreferences(); };
                panel.Children.Add(box); panel.Children.Add(new TextBlock { Text = (entry.Error ?? (entry.Instance == null ? "已禁用" : "已加载")) + "\n" + entry.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 12) });
            }
            window.ShowDialog();
        }
        /// <summary>为插件提供当前有效数据快照。</summary>
        /// <param name="selectedNode">是否仅取选中子树。</param>
        /// <returns>带版本的只读快照。</returns>
        public DocumentSnapshot GetSnapshot(bool selectedNode)
        {
            DocumentPage page = ActivePage;
            if (page == null || !page.HasCurrentDocument) throw new InvalidOperationException("请先输入有效 JSON 并等待解析完成。");
            JsonNode node = selectedNode ? page.SelectedNode ?? page.Parsed.Root : page.Parsed.Root;
            long version = page.Version;
            return new DocumentSnapshot(page.Parsed.Raw(node), page.DisplayName, node.Path, version, () => !page.Closed && page.Version == version);
        }
        internal List<MenuItem> CreateNodePluginCommands(Func<DocumentSnapshot> getSnapshot)
        {
            var commands = new List<MenuItem>();
            foreach (IJsonNodePlugin plugin in plugins.Entries.Select(p => p.Instance).OfType<IJsonNodePlugin>())
            {
                var command = new MenuItem { Header = plugin.NodeCommandText };
                command.Click += (s, e) => { try { plugin.ExecuteForNode(this, getSnapshot()); } catch (Exception exception) { ShowError(exception); } };
                commands.Add(command);
            }
            return commands;
        }
        internal void ShowError(Exception exception) { MessageBox.Show(this, exception.Message, "JsonView", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal static class EncodingDialog
    {
        internal static int? Choose(Window owner, string title)
        {
            var window = new Window { Owner = owner, Title = title, Width = 510, Height = 205, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
            panel.Children.Add(new TextBlock { Text = "无 BOM 文件的编码可能无法自动识别，请选择正确编码。", TextWrapping = TextWrapping.Wrap });
            var combo = new ComboBox { ItemsSource = new[] { "UTF-8", "GBK", "GB18030", "UTF-16 LE", "UTF-16 BE" }, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(combo);
            var ok = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right }; ok.Click += (s, e) => window.DialogResult = true; panel.Children.Add(ok);
            return window.ShowDialog() == true ? (int?)new[] { 65001, 936, 54936, 1200, 1201 }[combo.SelectedIndex] : null;
        }
    }
}
