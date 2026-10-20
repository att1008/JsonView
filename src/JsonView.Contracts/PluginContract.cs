using System;
using System.Windows;

namespace JsonView.Contracts
{
    /// <summary>只读文档快照，不共享宿主内部状态。</summary>
    public sealed class DocumentSnapshot
    {
        public string Text { get; }
        public string Title { get; }
        public string Path { get; }
        public long Version { get; }
        public Func<bool> IsCurrent { get; }

        /// <summary>创建不可变快照。</summary>
        /// <param name="text">输入文本。</param>
        /// <param name="title">源文档名。</param>
        /// <param name="path">节点路径。</param>
        /// <param name="version">源版本。</param>
        /// <param name="isCurrent">检查源是否仍为此版本。</param>
        public DocumentSnapshot(string text, string title, string path, long version, Func<bool> isCurrent)
        { Text = text; Title = title; Path = path; Version = version; IsCurrent = isCurrent; }
    }

    /// <summary>插件可访问的宿主能力，契约版本 1。</summary>
    public interface IPluginContext
    {
        Window Owner { get; }
        bool IsDark { get; }
        event EventHandler ThemeChanged;

        /// <summary>取得当前有效 JSON 快照。</summary>
        /// <param name="selectedNode">是否仅使用当前节点。</param>
        /// <returns>与当前解析版本一致的快照。</returns>
        DocumentSnapshot GetSnapshot(bool selectedNode);
    }

    /// <summary>本地可信插件入口。</summary>
    public interface IJsonViewPlugin
    {
        string CommandText { get; }

        /// <summary>在宿主界面线程执行命令。</summary>
        /// <param name="context">宿主公开能力。</param>
        void Execute(IPluginContext context);
    }

    /// <summary>可选节点命令；不改变已有插件的入口契约。</summary>
    public interface IJsonNodePlugin
    {
        /// <summary>树节点右键菜单显示的命令名称。</summary>
        string NodeCommandText { get; }

        /// <summary>针对右键所选节点的固定快照执行命令。</summary>
        /// <param name="context">宿主上下文。</param>
        /// <param name="snapshot">所点节点的只读快照。</param>
        void ExecuteForNode(IPluginContext context, DocumentSnapshot snapshot);
    }
}
