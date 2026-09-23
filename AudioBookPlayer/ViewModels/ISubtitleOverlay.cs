using System;

namespace AudioBookPlayer.ViewModels
{
    /// <summary>
    /// 悬浮字幕窗口对 ViewModel 暴露的能力。
    /// ViewModel 通过这个接口控制窗口，而不用直接依赖 Window 类型。
    /// </summary>
    public interface ISubtitleOverlay
    {
        /// <summary>显示字幕窗口（不会抢焦点）。</summary>
        void ShowOverlay();

        /// <summary>隐藏字幕窗口。</summary>
        void HideOverlay();

        /// <summary>按当前的外观 / 位置设置重新摆放窗口。</summary>
        void ApplyPlacement();
    }
}
