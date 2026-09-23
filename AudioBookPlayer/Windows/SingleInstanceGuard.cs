using System;
using System.Threading;

namespace AudioBookPlayer.Windows
{
    /// <summary>
    /// 单实例守卫。
    ///
    /// 用命名 Mutex 判断是不是已经有 KATARU 在跑（包括收进托盘的情况）。
    /// 如果已经有一个，就给它广播一条注册消息，让它把主窗口从托盘里唤起来，然后本次启动直接退出——
    /// 不会出现两个托盘图标、两个字幕窗口、两套全局热键打架。
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        /// <summary>Local\ 前缀：同一台机器同一个用户下唯一（多用户登录各自独立，互不影响）。</summary>
        private const string MutexName = @"Local\KATARU.SingleInstance.v1";

        /// <summary>把窗口叫出来用的命名事件（比广播窗口消息可靠：不依赖主窗口的 HWND 还在不在）。</summary>
        private const string ActivateEventName = @"Local\KATARU.Activate.v1";

        private Mutex? _mutex;
        private bool _owned;
        private bool _disposed;
        private EventWaitHandle? _activateSignal;
        private Thread? _listenThread;

        public SingleInstanceGuard()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                IsFirstInstance = createdNew;
                _owned = createdNew;
            }
            catch (Exception)
            {
                // 拿不到 Mutex（极端权限情况）也不该拦住启动，退化成"允许多开"
                _mutex = null;
                IsFirstInstance = true;
                _owned = false;
            }
        }

        /// <summary>是不是第一个实例（true 才继续启动）。</summary>
        public bool IsFirstInstance { get; }

        /// <summary>
        /// 开始等"另一个实例叫我出来"。
        ///
        /// 用后台线程等命名事件，而不是只靠广播窗口消息：主窗口收到托盘里之后，
        /// 只靠 HWND 广播不一定叫得醒（而且窗口隐藏时更不可靠），那样用户看到的就是"双击图标闪一下没了"。
        /// </summary>
        public void ListenForActivation(Action onActivate)
        {
            if (!IsFirstInstance || onActivate == null)
            {
                return;
            }

            try
            {
                _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

                _listenThread = new Thread(() =>
                {
                    while (!_disposed)
                    {
                        try
                        {
                            if (!_activateSignal.WaitOne(500))
                            {
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        if (_disposed)
                        {
                            return;
                        }

                        try
                        {
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(onActivate);
                        }
                        catch (Exception)
                        {
                            // 程序正在退出，忽略
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "KATARU.ActivateListener",
                };

                _listenThread.Start();
            }
            catch (Exception)
            {
                // 事件建不出来也不影响使用，还有窗口消息广播那条路
            }
        }

        /// <summary>
        /// 通知已经在跑的那个实例把窗口显示出来。
        /// 两条路一起走：命名事件（主）+ 广播注册消息（兜底）。
        /// </summary>
        public static void SignalExistingInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
                {
                    using (handle)
                    {
                        handle.Set();
                    }
                }
            }
            catch (Exception)
            {
                // 落到广播
            }

            if (ShowWindowMessage != 0)
            {
                NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>自定义的"把主窗口叫出来"消息（全局唯一，注册一次）。</summary>
        public static int ShowWindowMessage { get; } = NativeMethods.RegisterWindowMessage("KATARU_SHOW_MAIN_WINDOW_9F2C");

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _activateSignal?.Set(); // 叫醒等待线程，让它自己退出
                _activateSignal?.Dispose();
                _activateSignal = null;
            }
            catch (Exception)
            {
                // 忽略
            }

            if (_mutex != null)
            {
                if (_owned)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // 不是本线程持有，忽略
                    }
                }

                _mutex.Dispose();
                _mutex = null;
            }
        }
    }
}
