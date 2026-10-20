using System;
using System.Threading;
using System.Threading.Tasks;

namespace JsonView.Core
{
    /// <summary>在可取消的独立后台线程运行 CPU 工作，不占用界面线程。</summary>
    public static class Work
    {
        /// <summary>运行 CPU 操作。</summary>
        /// <typeparam name="T">返回类型。</typeparam>
        /// <param name="action">操作。</param>
        /// <param name="cancellation">取消信号。</param>
        /// <returns>完成任务。</returns>
        public static Task<T> Run<T>(Func<T> action, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { cancellation.ThrowIfCancellationRequested(); T result = action(); cancellation.ThrowIfCancellationRequested(); completion.TrySetResult(result); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }) { IsBackground = true, Name = "JsonView.Work" };
            thread.Start(); return completion.Task;
        }
    }
}
