import { notificationManager } from '@kit.NotificationKit';
import { common, wantAgent, Want } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 常驻通知：显示当前这本书 / 当前这句字幕。
 *
 * 两个作用：
 *  1) 它是"音频播放长时任务"的必要配套 —— 用 SUBMODE_NORMAL_NOTIFICATION 申请长时任务时，
 *     系统要求 App 真的发一条常驻通知，否则任务会被判定无效并连带回收进程
 *     （实测：onContinuousTaskStart 之后 12 秒 onContinuousTaskStop，App 被杀）。
 *  2) 下拉通知栏就能看到当前这句字幕，是"悬浮字幕"在没有实况窗权限时的替代显示。
 */
export class NowPlayingNotification {
  private static readonly TAG: string = 'KATARU';
  private static readonly NOTIFICATION_ID: number = 18401;

  private enabled: boolean = false;
  private requested: boolean = false;
  private lastText: string = '';
  private lastUpdateMs: number = 0;

  /** 首次使用时申请通知权限（系统弹一次） */
  async ensurePermission(context: common.UIAbilityContext): Promise<boolean> {
    if (this.requested) {
      return this.enabled;
    }
    this.requested = true;

    try {
      const granted = await notificationManager.isNotificationEnabled();
      if (granted) {
        this.enabled = true;
        hilog.info(0x0000, NowPlayingNotification.TAG, 'notification already enabled');
        return true;
      }

      await notificationManager.requestEnableNotification(context);
      this.enabled = await notificationManager.isNotificationEnabled();
      hilog.info(0x0000, NowPlayingNotification.TAG, 'notification enabled: %{public}s',
        this.enabled ? 'yes' : 'no');
      return this.enabled;
    } catch (error) {
      const err = error as BusinessError;
      hilog.warn(0x0000, NowPlayingNotification.TAG, 'notification permission failed: code=%{public}d',
        err.code);
      this.enabled = false;
      return false;
    }
  }

  /** 发/更新通知（同一 id 覆盖更新） */
  async update(
    context: common.UIAbilityContext,
    bookTitle: string,
    chapterTitle: string,
    subtitle: string,
    isPlaying: boolean
  ): Promise<void> {
    if (!this.enabled) {
      return;
    }

    const text = subtitle.length > 0 ? subtitle : chapterTitle;
    const now = Date.now();
    if (text === this.lastText && now - this.lastUpdateMs < 3000) {
      return;
    }
    this.lastText = text;
    this.lastUpdateMs = now;

    try {
      const wants: Want[] = [{
        bundleName: context.abilityInfo.bundleName,
        abilityName: context.abilityInfo.name,
      }];
      const agentInfo: wantAgent.WantAgentInfo = {
        wants: wants,
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 200,
        actionFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG]
      };
      const agent = await wantAgent.getWantAgent(agentInfo);

      const request: notificationManager.NotificationRequest = {
        id: NowPlayingNotification.NOTIFICATION_ID,
        content: {
          notificationContentType: notificationManager.ContentType.NOTIFICATION_CONTENT_BASIC_TEXT,
          normal: {
            title: bookTitle,
            text: text,
            additionalText: isPlaying ? '播放中' : '已暂停'
          }
        },
        wantAgent: agent,
        // 让通知常驻、并显示在锁屏上
        isOngoing: false,
        tapDismissed: false
      };

      await notificationManager.publish(request);
    } catch (error) {
      const err = error as BusinessError;
      hilog.warn(0x0000, NowPlayingNotification.TAG, 'publish failed: code=%{public}d msg=%{public}s',
        err.code, err.message);
    }
  }

  /** 停止播放时撤掉通知 */
  async cancel(): Promise<void> {
    try {
      await notificationManager.cancel(NowPlayingNotification.NOTIFICATION_ID);
      hilog.info(0x0000, NowPlayingNotification.TAG, 'notification cancelled');
    } catch (error) {
      // 没发过就算了
    }
    this.lastText = '';
    this.lastUpdateMs = 0;
  }
}
