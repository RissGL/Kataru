import { liveViewManager } from '@kit.LiveViewKit';
import { common, wantAgent, Want } from '@kit.AbilityKit';
import type { WantAgent } from '@ohos.wantAgent';
import { image } from '@kit.ImageKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 实况窗 / 灵动胶囊字幕（对应桌面版的"桌面悬浮字幕"）。
 *
 * 鸿蒙上第三方 App 想在其他应用之上显示内容，正规途径就是实况窗：
 *  - 状态栏胶囊（灵动胶囊）显示当前这句字幕
 *  - 锁屏 / 通知中心显示书本 + 当前字幕
 *
 * 限制（来自官方文档）：event 场景需要**在华为侧申请对应权限**。
 * 没申请到的时候 isLiveViewEnabled() 返回 false，这里的调用会自动跳过，
 * 不影响正常播放 —— 申请通过后不用改代码就能生效。
 */
export class LiveSubtitle {
  private static readonly TAG: string = 'KATARU';
  /** 实况窗 id（自己定，一个 App 用固定值即可） */
  private static readonly LIVE_VIEW_ID: number = 18401;

  private enabled: boolean = false;
  private started: boolean = false;
  private sequence: number = 0;
  private lastText: string = '';
  /** 点击胶囊时回到 App 的 WantAgent（实况窗必填） */
  private clickAction: WantAgent | null = null;
  /** 实况窗图标（rawfile 解码成 PixelMap，系统要求 < 30KB） */
  private iconPixel: image.PixelMap | null = null;

  /** 把 rawfile 里的 live_icon.png 解码成 PixelMap（实况窗 icon/nodeIcons 都用它） */
  async setupIcon(context: common.UIAbilityContext): Promise<void> {
    try {
      const buffer = await context.resourceManager.getRawFileContent('live_icon.png');
      const source = image.createImageSource(buffer.buffer as ArrayBuffer);
      this.iconPixel = await source.createPixelMap();
      await source.release();
      hilog.info(0x0000, LiveSubtitle.TAG, 'liveView icon ready');
    } catch (error) {
      hilog.warn(0x0000, LiveSubtitle.TAG, 'setupIcon failed: %{public}s', JSON.stringify(error));
      this.iconPixel = null;
    }
  }

  /**
   * 准备"点击胶囊回到 App"的 WantAgent。
   * 实况窗的 primary.clickAction 是必填项，缺了会报 401 Parameter error。
   */
  async setupClickAction(context: common.UIAbilityContext): Promise<void> {
    try {
      const wants: Want[] = [{
        bundleName: context.abilityInfo.bundleName,
        abilityName: context.abilityInfo.name,
      }];
      const info: wantAgent.WantAgentInfo = {
        wants: wants,
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 0,
        actionFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG]
      };
      this.clickAction = await wantAgent.getWantAgent(info);
      hilog.info(0x0000, LiveSubtitle.TAG, 'liveView clickAction ready');
    } catch (error) {
      hilog.warn(0x0000, LiveSubtitle.TAG, 'setupClickAction failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 查询是否具备实况窗能力（需要华为侧审批通过） */
  async isEnabled(): Promise<boolean> {
    try {
      this.enabled = await liveViewManager.isLiveViewEnabled();
      hilog.info(0x0000, LiveSubtitle.TAG, 'liveView enabled: %{public}s',
        this.enabled ? 'yes' : 'no (需要申请实况窗权限)');
      return this.enabled;
    } catch (error) {
      const err = error as BusinessError;
      hilog.warn(0x0000, LiveSubtitle.TAG, 'isLiveViewEnabled failed: code=%{public}d msg=%{public}s',
        err.code, err.message);
      this.enabled = false;
      return false;
    }
  }

  /** 开始显示字幕实况窗 */
  async start(bookTitle: string, chapterTitle: string, subtitle: string): Promise<void> {
    const ok = await this.isEnabled();
    if (!ok) {
      return;
    }

    try {
      this.sequence = 1;
      this.lastText = subtitle;
      const liveView = this.buildLiveView(bookTitle, chapterTitle, subtitle, this.sequence);
      const result = await liveViewManager.startLiveView(liveView);
      this.started = true;
      hilog.info(0x0000, LiveSubtitle.TAG, 'liveView started: %{public}s', JSON.stringify(result));
    } catch (error) {
      const err = error as BusinessError;
      hilog.warn(0x0000, LiveSubtitle.TAG, 'startLiveView failed: code=%{public}d msg=%{public}s',
        err.code, err.message);
    }
  }

  /** 字幕变了就更新胶囊里的文字 */
  async update(bookTitle: string, chapterTitle: string, subtitle: string): Promise<void> {
    if (!this.started || subtitle === this.lastText) {
      return;
    }

    try {
      this.sequence++;
      this.lastText = subtitle;
      const liveView = this.buildLiveView(bookTitle, chapterTitle, subtitle, this.sequence);
      await liveViewManager.updateLiveView(liveView);
    } catch (error) {
      const err = error as BusinessError;
      // sequence 必须递增，失败就重开一次
      if (err.code === 1600002) {
        this.started = false;
        await this.start(bookTitle, chapterTitle, subtitle);
        return;
      }
      hilog.warn(0x0000, LiveSubtitle.TAG, 'updateLiveView failed: code=%{public}d', err.code);
    }
  }

  /** 结束播放时收起实况窗 */
  async stop(chapterTitle: string): Promise<void> {
    if (!this.started) {
      return;
    }

    try {
      this.sequence++;
      const liveView = this.buildLiveView('KATARU', chapterTitle, '', this.sequence);
      await liveViewManager.stopLiveView(liveView);
      hilog.info(0x0000, LiveSubtitle.TAG, 'liveView stopped');
    } catch (error) {
      hilog.warn(0x0000, LiveSubtitle.TAG, 'stopLiveView failed: %{public}s', JSON.stringify(error));
    } finally {
      this.started = false;
      this.lastText = '';
    }
  }

  /**
   * 组装实况窗数据。
   * event 用 PROGRESS（进度展示类场景），胶囊用纯文字类型显示当前这句字幕。
   */
  private buildLiveView(
    bookTitle: string,
    chapterTitle: string,
    subtitle: string,
    sequence: number
  ): liveViewManager.LiveView {
    const safeTitle = bookTitle.length > 0 ? bookTitle.substring(0, 200) : 'KATARU';
    const safeSubtitle = subtitle.length > 0 ? subtitle.substring(0, 400) : (chapterTitle.length > 0 ? chapterTitle : '播放中');

    const content: liveViewManager.RichText[] = [{
      text: safeSubtitle
    }];

    const capsule: liveViewManager.TextCapsule = {
      type: liveViewManager.CapsuleType.CAPSULE_TYPE_TEXT,
      status: 1,
      icon: this.iconPixel !== null ? this.iconPixel : 'live_icon.png',
      backgroundColor: '#FF121011',
      isContentDisplayed: true,
      title: safeSubtitle,
      content: safeTitle
    };

    // PROGRESS 场景必须有 layoutData，否则系统报 401
    // 用默认文字模板：只要标题 + 当前这句字幕，不需要进度节点图标
    const layout: liveViewManager.ProgressLayout = {
      layoutType: liveViewManager.LayoutType.LAYOUT_TYPE_DEFAULT,
      progress: 0
    };

    const primary: liveViewManager.PrimaryData = {
      title: safeTitle,
      content: content,
      layoutData: layout
    };
    if (this.clickAction !== null) {
      primary.clickAction = this.clickAction;
    }

    const data: liveViewManager.LiveViewData = {
      primary: primary,
      capsule: capsule
    };

    const liveView: liveViewManager.LiveView = {
      id: LiveSubtitle.LIVE_VIEW_ID,
      event: 'PROGRESS',
      sequence: sequence,
      isMute: true,
      liveViewData: data
    };

    return liveView;
  }
}
