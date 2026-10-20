using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace JsonView.Core
{
    /// <summary>文件原文与实际编码。</summary>
    public sealed class FileContent
    {
        public string Text { get; set; }
        public Encoding Encoding { get; set; }
    }

    /// <summary>严格解码和安全异步写入。</summary>
    public static class FileStorage
    {
        /// <summary>构建不会静默替换无效字符的编码。</summary>
        /// <param name="codePage">编码页。</param>
        /// <param name="bom">是否输出 BOM。</param>
        /// <returns>严格编码。</returns>
        public static Encoding EncodingFor(int codePage, bool bom = false)
        {
            if (codePage == 65001) return new UTF8Encoding(bom, true);
            if (codePage == 1200 || codePage == 1201) return new UnicodeEncoding(codePage == 1201, bom, true);
            return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        /// <summary>异步读取文件；无 BOM 默认严格 UTF-8，可手工选择 GBK/GB18030。</summary>
        /// <param name="path">文件路径。</param>
        /// <param name="codePage">强制编码页，0 表示识别 BOM。</param>
        /// <returns>文本和编码。</returns>
        public static async Task<FileContent> ReadAsync(string path, int codePage = 0)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                var prefix = new byte[4]; int count = await stream.ReadAsync(prefix, 0, 4).ConfigureAwait(false); stream.Position = 0;
                Encoding encoding = EncodingFor(codePage == 0 ? 65001 : codePage);
                if (codePage == 0)
                {
                    if (count >= 4 && ((prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 254 && prefix[3] == 255) || (prefix[0] == 255 && prefix[1] == 254 && prefix[2] == 0 && prefix[3] == 0))) throw new IOException("暂不支持 UTF-32，请选择支持的编码。");
                    if (count >= 3 && prefix[0] == 239 && prefix[1] == 187 && prefix[2] == 191) { encoding = EncodingFor(65001, true); stream.Position = 3; }
                    else if (count >= 2 && prefix[0] == 255 && prefix[1] == 254) { encoding = EncodingFor(1200, true); stream.Position = 2; }
                    else if (count >= 2 && prefix[0] == 254 && prefix[1] == 255) { encoding = EncodingFor(1201, true); stream.Position = 2; }
                }
                else
                {
                    bool utf8Bom = codePage == 65001 && count >= 3 && prefix[0] == 239 && prefix[1] == 187 && prefix[2] == 191;
                    bool utf16Bom = count >= 2 && ((codePage == 1200 && prefix[0] == 255 && prefix[1] == 254) || (codePage == 1201 && prefix[0] == 254 && prefix[1] == 255));
                    if (utf8Bom || utf16Bom) { encoding = EncodingFor(codePage, true); stream.Position = utf8Bom ? 3 : 2; }
                }
                using (var reader = new StreamReader(stream, encoding, false, 65536))
                    return new FileContent { Text = await reader.ReadToEndAsync().ConfigureAwait(false), Encoding = encoding };
            }
        }

        /// <summary>同目录临时写入后替换；失败保留原文件。</summary>
        /// <param name="path">目标路径。</param>
        /// <param name="text">完整文本。</param>
        /// <param name="encoding">目标编码。</param>
        /// <returns>写入任务。</returns>
        public static async Task WriteAsync(string path, string text, Encoding encoding)
        {
            string fullPath = Path.GetFullPath(path); string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                using (var writer = new StreamWriter(stream, encoding, 65536)) { await writer.WriteAsync(text).ConfigureAwait(false); await writer.FlushAsync().ConfigureAwait(false); }
                if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null); else File.Move(temporary, fullPath);
            }
            finally
            {
                // 临时文件清理属于尽力而为，不覆盖原始写入异常。
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException exception) { Debug.WriteLine(exception.Message); }
                catch (UnauthorizedAccessException exception) { Debug.WriteLine(exception.Message); }
            }
        }
    }
}
