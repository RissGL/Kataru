using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AudioBookPlayer.Diagnostics
{
    /// <summary>
    /// 轻量"黑匣子"：记录最近做了哪些耗时操作，并在 UI 线程卡住时自动落盘。
    ///
    /// 换书卡死这类问题在别人机器上很难复现，光说"卡住了"没有任何线索。
    /// 这个类做两件事：
    ///   1. 业务代码进入/退出关键操作时打标记（Enter/Exit），只保留最近若干条；
    ///   2. 后台线程每 0.5 秒 ping 一次 UI 线程，连续超时就说明界面卡住了，
    ///      把"卡住时正在做什么"写进 activity.log。
    /// </summary>
    public static class ActivityLog
    {
        private const int MaxRecords = 40;
        private const int StallThresholdMs = 2000;
        private const int PingTimeoutMs = 800;

        private static readonly object Gate = new();
        private static readonly LinkedList<string> Recent = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static readonly AsyncLocal<Stack<string>?> ScopeStack = new();
        private static Thread? _watchdog;
        private static volatile bool _running;
        private static int _stallReports;

        /// <summary>当前正在进行的操作（最内层）。</summary>
        private static string? CurrentOperation
        {
            get
            {
                var stack = ScopeStack.Value;
                return stack is { Count: > 0 } ? stack.Peek() : null;
            }
        }

        /// <summary>开始一个操作（配合 using 使用）。</summary>
        public static IDisposable Enter(string name)
        {
            var stack = ScopeStack.Value ??= new Stack<string>();
            stack.Push(name);
            return new Scope(name);
        }

        /// <summary>直接记一条（不需要配对退出）。</summary>
        public static void Note(string message)
        {
            lock (Gate)
            {
                Recent.AddLast($"[{Clock.Elapsed.TotalSeconds,8:0.000}s] {message}");
                while (Recent.Count > MaxRecords)
                {
                    Recent.RemoveFirst();
                }
            }
        }

        /// <summary>启动看门狗（UI 线程卡住时自动写日志）。</summary>
        public static void StartWatchdog(string logPath)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _watchdog = new Thread(() => WatchdogLoop(logPath))
            {
                IsBackground = true,
                Name = "KATARU.Watchdog",
            };
            _watchdog.Start();
        }

        public static void StopWatchdog() => _running = false;

        private static void WatchdogLoop(string logPath)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;

            while (_running)
            {
                Thread.Sleep(500);

                if (dispatcher == null)
                {
                    continue;
                }

                var completed = false;
                try
                {
                    var operation = dispatcher.BeginInvoke(new Action(() => completed = true));
                    var deadline = Environment.TickCount64 + PingTimeoutMs;

                    while (!completed && Environment.TickCount64 < deadline)
                    {
                        Thread.Sleep(50);
                    }

                    if (completed)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                // UI 线程超过 800ms 没响应：再等一会儿，确认不是一瞬间的抖动
                Thread.Sleep(StallThresholdMs - PingTimeoutMs);

                if (completed)
                {
                    continue;
                }

                if (Interlocked.Increment(ref _stallReports) > 5)
                {
                    continue; // 避免卡住时疯狂写文件
                }

                WriteReport(logPath, "界面卡住");
            }
        }

        private static void WriteReport(string logPath, string reason)
        {
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {reason}");
                builder.AppendLine($"当前操作：{CurrentOperation ?? "(空闲)"}");
                builder.AppendLine("--- 最近的操作 ---");
                lock (Gate)
                {
                    foreach (var line in Recent)
                    {
                        builder.AppendLine(line);
                    }
                }

                builder.AppendLine();
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, builder.ToString(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // 记录失败就算了，不能因为日志把程序搞挂
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private bool _done;

            public Scope(string name) => _name = name;

            public void Dispose()
            {
                if (_done)
                {
                    return;
                }

                _done = true;
                _stopwatch.Stop();

                var stack = ScopeStack.Value;
                if (stack is { Count: > 0 } && stack.Peek() == _name)
                {
                    stack.Pop();
                }

                var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
                Note(elapsed >= 200
                    ? $"完成：{_name}（{elapsed:0} ms）"
                    : $"完成：{_name}（{elapsed:0} ms）");

                if (elapsed >= 1000)
                {
                    Note($"⚠ 这一步花了 {elapsed:0} ms：{_name}");
                }
            }
        }
    }
}
