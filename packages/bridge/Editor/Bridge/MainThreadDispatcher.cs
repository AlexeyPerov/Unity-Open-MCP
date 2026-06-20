using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _queue = new();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= ProcessQueue;
            EditorApplication.update += ProcessQueue;
        }

        private static void ProcessQueue()
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        public static void Enqueue(Action action)
        {
            _queue.Enqueue(action);
        }

        public static Task<T> EnqueueAsync<T>(Func<T> action, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            });

            var timer = new Timer(_ => tcs.TrySetException(new TimeoutException()), null, timeoutMs, Timeout.Infinite);
            tcs.Task.ContinueWith(_ => timer.Dispose());

            return tcs.Task;
        }
    }
}
