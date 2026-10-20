using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JsonView.Contracts;
using Newtonsoft.Json.Linq;

namespace JsonView
{
    internal sealed class PluginEntry
    {
        internal string Id, Name, Version, Description, Error;
        internal bool Enabled;
        internal IJsonViewPlugin Instance;
    }
    internal sealed class PluginManager
    {
        internal readonly List<PluginEntry> Entries = new List<PluginEntry>();
        internal readonly HashSet<string> Disabled;
        internal PluginManager(IEnumerable<string> disabled) { Disabled = new HashSet<string>(disabled ?? Enumerable.Empty<string>(), StringComparer.Ordinal); }
        internal void Discover(string directory)
        {
            if (!Directory.Exists(directory)) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            string[] folders;
            try { folders = Directory.GetDirectories(directory).Concat(new[] { directory }).ToArray(); }
            catch (Exception exception) { Entries.Add(new PluginEntry { Name = "插件目录", Error = exception.Message }); return; }
            foreach (string folderPath in folders)
            {
                string manifestPath = Path.Combine(folderPath, "plugin.json");
                if (!File.Exists(manifestPath)) continue;
                var entry = new PluginEntry { Name = Path.GetFileName(Path.GetDirectoryName(manifestPath)), Enabled = false };
                Entries.Add(entry);
                try
                {
                    JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                    entry.Id = (string)manifest["id"]; entry.Name = (string)manifest["name"] ?? entry.Name; entry.Version = (string)manifest["version"]; entry.Description = (string)manifest["description"];
                    if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id)) throw new InvalidDataException("插件标识为空或重复。");
                    entry.Enabled = !Disabled.Contains(entry.Id);
                    if ((int?)manifest["contractVersion"] != 1) throw new InvalidDataException("不支持的契约版本（宿主为 1）。");
                    string folder = Path.GetFullPath(Path.GetDirectoryName(manifestPath)) + Path.DirectorySeparatorChar;
                    string assemblyPath = Path.GetFullPath(Path.Combine(folder, (string)manifest["assembly"] ?? ""));
                    if (!assemblyPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath)) throw new InvalidDataException("入口文件不存在或超出插件目录。");
                    if (!entry.Enabled) continue;
                    Assembly assembly = Assembly.LoadFrom(assemblyPath);
                    Type type = assembly.GetType((string)manifest["type"] ?? "", true);
                    if (!typeof(IJsonViewPlugin).IsAssignableFrom(type)) throw new InvalidDataException("入口未实现 IJsonViewPlugin。");
                    entry.Instance = (IJsonViewPlugin)Activator.CreateInstance(type);
                }
                catch (Exception exception) { entry.Error = exception.GetBaseException().Message; }
            }
        }
    }
}
