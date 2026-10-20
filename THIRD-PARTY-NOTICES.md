# Third-party notices

本文件列出 JsonView 使用的第三方组件及其许可证。第三方许可证不代表 JsonView 自身已采用相同许可证。

## 应用运行时组件

| 组件 | 版本 | 许可证 | 项目 |
| --- | --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT | https://github.com/icsharpcode/AvalonEdit |
| Newtonsoft.Json | 13.0.4 | MIT | https://github.com/JamesNK/Newtonsoft.Json |

完整版权和许可文本：

- [AvalonEdit](licenses/AvalonEdit-LICENSE.txt)
- [Newtonsoft.Json](licenses/Newtonsoft.Json-LICENSE.txt)

AvalonEdit 许可证来自 NuGet 包所记录的源代码提交 862415d51eddc9eac93f462dbc522ffbf929cd52；Newtonsoft.Json 许可证来自所用 NuGet 包。

## 验证工程组件

验证工程还通过 NuGet 引用 System.Text.Json 10.0.12 及其 Microsoft/.NET 运行时辅助依赖，用于验证生成模型。这些组件不随 JsonView 应用发布包分发。

- [System.Text.Json / .NET runtime](https://github.com/dotnet/runtime)
- [MIT 许可文本](licenses/dotnet-runtime-LICENSE.txt)

各 NuGet 包的许可证元数据及附带的第三方声明同样适用。
