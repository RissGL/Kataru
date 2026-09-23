using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AudioBookPlayer.Windows
{
    /// <summary>一个全局热键动作，可以带多个候选组合（前一个被占用就试下一个）。</summary>
    public sealed class HotkeyDefinition
    {
        public HotkeyDefinition(string action, string description, params (ModifierKeys Modifiers, Key Key)[] candidates)
        {
            Action = action;
            Description = description;
            Candidates = candidates;
        }

        /// <summary>动作标识（ViewModel 按这个分发）。</summary>
        public string Action { get; }

        /// <summary>界面 / 设置里显示的说明。</summary>
        public string Description { get; }

        /// <summary>候选组合，按优先级排列。</summary>
        public IReadOnlyList<(ModifierKeys Modifiers, Key Key)> Candidates { get; }
    }

    /// <summary>已经注册成功的热键。</summary>
    public sealed class RegisteredHotkey
    {
        public RegisteredHotkey(HotkeyDefinition definition, ModifierKeys modifiers, Key key)
        {
            Definition = definition;
            Modifiers = modifiers;
            Key = key;
        }

        public HotkeyDefinition Definition { get; }

        public ModifierKeys Modifiers { get; }

        public Key Key { get; }

        public string Action => Definition.Action;

        public string Description => Definition.Description;

        public string GestureText => FormatGesture(Modifiers, Key);

        public static string FormatGesture(ModifierKeys modifiers, Key key)
        {
            var parts = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                parts.Add("Ctrl");
            }

            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                parts.Add("Alt");
            }

            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                parts.Add("Shift");
            }

            if (modifiers.HasFlag(ModifierKeys.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(FormatKey(key));
            return string.Join(" + ", parts);
        }

        private static string FormatKey(Key key) => key switch
        {
            Key.Space => "空格",
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.MediaPlayPause => "播放/暂停键",
            Key.MediaNextTrack => "下一曲键",
            Key.MediaPreviousTrack => "上一曲键",
            Key.MediaStop => "停止键",
            _ => key.ToString(),
        };
    }

    /// <summary>
    /// 全局热键：注册到主窗口的 HWND 上，所以程序不在前台时也能用（一边在别的软件里干活一边控制播放）。
    ///
    /// 用 RegisterHotKey 而不是键盘钩子，不需要管理员权限，也不会被安全软件当成键盘记录器。
    /// 每个动作可以给多个候选组合，被别的程序占用就自动退到下一个（Ctrl+Alt+方向键经常被显卡驱动抢走）。
    /// </summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const int FirstId = 0x4150; // 'AP'

        private readonly List<(int Id, HotkeyDefinition Definition, ModifierKeys Modifiers, Key Key)> _registered =
            new List<(int, HotkeyDefinition, ModifierKeys, Key)>();

        private readonly List<HotkeyDefinition> _failed = new List<HotkeyDefinition>();
        private IntPtr _handle;
        private HwndSource? _source;
        private bool _disposed;

        /// <summary>热键被按下。参数是动作标识。</summary>
        public event EventHandler<string>? Pressed;

        /// <summary>注册失败（所有候选都被占用）的动作。</summary>
        public IReadOnlyList<HotkeyDefinition> Failed => _failed;

        /// <summary>实际生效的热键。</summary>
        public IReadOnlyList<RegisteredHotkey> Registered
        {
            get
            {
                var list = new List<RegisteredHotkey>(_registered.Count);
                foreach (var (_, definition, modifiers, key) in _registered)
                {
                    list.Add(new RegisteredHotkey(definition, modifiers, key));
                }

                return list;
            }
        }

        /// <summary>把热键挂到某个窗口上（通常用主窗口）。</summary>
        public bool Attach(Window window)
        {
            _handle = Win32WindowHelper.GetHandle(window);
            if (_handle == IntPtr.Zero)
            {
                return false;
            }

            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WndProc);
            return _source != null;
        }

        /// <summary>注册一批热键，返回成功数量。</summary>
        public int Register(IEnumerable<HotkeyDefinition> definitions)
        {
            UnregisterAll();

            if (_handle == IntPtr.Zero)
            {
                return 0;
            }

            var id = FirstId;
            foreach (var definition in definitions)
            {
                var ok = false;

                foreach (var (modifiers, key) in definition.Candidates)
                {
                    var native = ToNativeModifiers(modifiers) | NativeMethods.MOD_NOREPEAT;
                    if (NativeMethods.RegisterHotKey(_handle, id, native, (uint)KeyInterop.VirtualKeyFromKey(key)))
                    {
                        _registered.Add((id, definition, modifiers, key));
                        id++;
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    _failed.Add(definition);
                }
            }

            return _registered.Count;
        }

        public void UnregisterAll()
        {
            if (_handle != IntPtr.Zero)
            {
                foreach (var (id, _, _, _) in _registered)
                {
                    NativeMethods.UnregisterHotKey(_handle, id);
                }
            }

            _registered.Clear();
            _failed.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnregisterAll();

            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != NativeMethods.WM_HOTKEY)
            {
                return IntPtr.Zero;
            }

            var id = wParam.ToInt32();
            foreach (var (registeredId, definition, _, _) in _registered)
            {
                if (registeredId == id)
                {
                    Pressed?.Invoke(this, definition.Action);
                    handled = true;
                    break;
                }
            }

            return IntPtr.Zero;
        }

        private static uint ToNativeModifiers(ModifierKeys modifiers)
        {
            uint result = 0;
            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                result |= NativeMethods.MOD_ALT;
            }

            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                result |= NativeMethods.MOD_CONTROL;
            }

            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                result |= NativeMethods.MOD_SHIFT;
            }

            if (modifiers.HasFlag(ModifierKeys.Windows))
            {
                result |= NativeMethods.MOD_WIN;
            }

            return result;
        }
    }

    /// <summary>内置的默认热键。</summary>
    public static class DefaultHotkeys
    {
        public const string TogglePlay = "toggle-play";
        public const string SkipBack = "skip-back";
        public const string SkipForward = "skip-forward";
        public const string ToggleOverlay = "toggle-overlay";
        public const string ToggleMoveMode = "toggle-move";
        public const string ToggleReading = "toggle-reading";

        public static IReadOnlyList<HotkeyDefinition> Create(double skipSeconds)
        {
            var skip = $"{(int)skipSeconds} 秒";
            var ctrlAlt = ModifierKeys.Control | ModifierKeys.Alt;
            var ctrlAltShift = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
            var ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;

            return new[]
            {
                // 媒体键优先：这正是音乐播放器该抢的键，冲突也最少
                new HotkeyDefinition(
                    TogglePlay, "播放 / 暂停",
                    (ModifierKeys.None, Key.MediaPlayPause),
                    (ctrlAlt, Key.Space)),

                new HotkeyDefinition(
                    SkipBack, $"快退 {skip}",
                    (ctrlAltShift, Key.Left),
                    (ctrlAlt, Key.Left),
                    (ctrlShift, Key.Left)),

                new HotkeyDefinition(
                    SkipForward, $"快进 {skip}",
                    (ctrlAltShift, Key.Right),
                    (ctrlAlt, Key.Right),
                    (ctrlShift, Key.Right)),

                new HotkeyDefinition(
                    ToggleOverlay, "显示 / 隐藏桌面字幕",
                    (ctrlAlt, Key.S),
                    (ctrlAltShift, Key.S)),

                new HotkeyDefinition(
                    ToggleMoveMode, "移动字幕 / 锁定鼠标穿透",
                    (ctrlAlt, Key.M), (ctrlAltShift, Key.M)),

                new HotkeyDefinition(
                    ToggleReading, "播放 / 阅读模式切换",
                    (ctrlAlt, Key.R), (ctrlAltShift, Key.R)),
            };
        }
    }
}
