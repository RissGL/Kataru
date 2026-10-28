import { avSession } from '@kit.AVSessionKit';
import { image } from '@kit.ImageKit';
import { common, wantAgent, Want } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 媒体会话（对应桌面版"任务栏/托盘播放控制"的那一套）。
 *
 * 作用：
 *  - 让 App 退到后台/锁屏后继续播放（鸿蒙要求有 AVSession 才允许后台音频）
 *  - 在系统播控中心/锁屏显示当前**书本名 + 当前这句字幕 + 进度**
 *  - 接收系统的播放/暂停/上一章/下一章/拖动进度
 *
 * 这就是"悬浮字幕"在鸿蒙上不需要审批就能做到的那一半：字幕会出现在锁屏和播控卡片里。
 */
export class MediaSession {
  private static readonly TAG: string = 'KATARU';

  private session: avSession.AVSession | null = null;
  private activated: boolean = false;
  private lastSubtitle: string = '';
  private lastUpdateMs: number = 0;
  /** 上一次设置过的封面，避免每秒重复传 PixelMap */
  private lastArtwork: image.PixelMap | null = null;

  /** 创建并激活会话 */
  async start(
    context: common.UIAbilityContext,
    onPlay: () => void,
    onPause: () => void,
    onPlayNext: () => void,
    onPlayPrevious: () => void,
    onSeek: (positionMs: number) => void
  ): Promise<void> {
    if (this.session !== null) {
      return;
    }

    try {
      const session = await avSession.createAVSession(context, 'kataru_audio', 'audio');
      this.session = session;

      session.on('play', () => {
        hilog.info(0x0000, MediaSession.TAG, 'avsession: play');
        onPlay();
      });
      session.on('pause', () => {
        hilog.info(0x0000, MediaSession.TAG, 'avsession: pause');
        onPause();
      });
      session.on('stop', () => {
        hilog.info(0x0000, MediaSession.TAG, 'avsession: stop');
        onPause();
      });
      session.on('playNext', () => {
        hilog.info(0x0000, MediaSession.TAG, 'avsession: playNext');
        onPlayNext();
      });
      session.on('playPrevious', () => {
        hilog.info(0x0000, MediaSession.TAG, 'avsession: playPrevious');
        onPlayPrevious();
      });
      session.on('seek', (timeMs: number) => {
        hilog.info(0x0000, MediaSession.TAG, 'avsession: seek %{public}d', timeMs);
        onSeek(timeMs);
      });

      await session.activate();
      this.activated = true;
      hilog.info(0x0000, MediaSession.TAG, 'avsession activated');
    } catch (error) {
      const err = error as BusinessError;
      hilog.error(0x0000, MediaSession.TAG, 'avsession start failed: code=%{public}d msg=%{public}s',
        err.code, err.message);
      this.session = null;
    }
  }

  /**
   * 更新播控中心显示的内容。
   *
   * 关键点：把**当前这句字幕**塞进 artist 字段 —— 这样锁屏/播控卡片上就能看到字幕，
   * 相当于桌面版的悬浮字幕在系统层的等价物。
   */
  async update(
    bookTitle: string,
    chapterTitle: string,
    subtitle: string,
    positionMs: number,
    durationMs: number,
    isPlaying: boolean,
    artwork: image.PixelMap | null = null
  ): Promise<void> {
    const session = this.session;
    if (session === null) {
      return;
    }

    // 字幕变化不频繁时不必每次都刷（系统对频率也有限制）
    const now = Date.now();
    const subtitleChanged = subtitle !== this.lastSubtitle;
    if (!subtitleChanged && now - this.lastUpdateMs < 1000) {
      return;
    }
    this.lastSubtitle = subtitle;
    this.lastUpdateMs = now;

    const safeSubtitleLog = subtitle.length > 24 ? subtitle.substring(0, 24) : subtitle;

    try {
      const metadata: avSession.AVMetadata = {
        assetId: chapterTitle.length > 0 ? chapterTitle : bookTitle,
        title: bookTitle,
        artist: subtitle.length > 0 ? subtitle : chapterTitle,
        album: chapterTitle,
      };
      // 封面：灵动胶囊/锁屏/播控中心显示的图就是这张（不设的话系统只能显示应用图标）
      if (artwork !== null && artwork !== undefined && this.lastArtwork !== artwork) {
        metadata.mediaImage = artwork;
        this.lastArtwork = artwork;
      }
      await session.setAVMetadata(metadata);

      const state: avSession.AVPlaybackState = {
        state: isPlaying
          ? avSession.PlaybackState.PLAYBACK_STATE_PLAY
          : avSession.PlaybackState.PLAYBACK_STATE_PAUSE,
        position: {
          elapsedTime: positionMs,
          updateTime: now
        },
        speed: 1.0
      };
      await session.setAVPlaybackState(state);
      hilog.info(0x0000, MediaSession.TAG,
        'avsession updated: title=%{public}s / subtitle=%{public}s / %{public}dms',
        bookTitle, safeSubtitleLog, positionMs);
    } catch (error) {
      const err = error as BusinessError;
      hilog.warn(0x0000, MediaSession.TAG, 'avsession update failed: code=%{public}d', err.code);
    }
  }

  /** 设置"点播控卡片能回到 App" */
  async setLaunchAbility(context: common.UIAbilityContext): Promise<void> {
    const session = this.session;
    if (session === null) {
      return;
    }

    try {
      const wants: Want[] = [{
        bundleName: context.abilityInfo.bundleName,
        abilityName: context.abilityInfo.name,
      }];
      const wantAgentInfo: wantAgent.WantAgentInfo = {
        wants: wants,
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 0,
        actionFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG]
      };
      const agent = await wantAgent.getWantAgent(wantAgentInfo);
      await session.setLaunchAbility(agent);
      hilog.info(0x0000, MediaSession.TAG, 'avsession launch ability set');
    } catch (error) {
      hilog.warn(0x0000, MediaSession.TAG, 'setLaunchAbility failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 释放 */
  async stop(): Promise<void> {
    const session = this.session;
    this.session = null;
    this.activated = false;

    if (session === null) {
      return;
    }

    try {
      await session.destroy();
      hilog.info(0x0000, MediaSession.TAG, 'avsession destroyed');
    } catch (error) {
      hilog.warn(0x0000, MediaSession.TAG, 'avsession destroy failed: %{public}s', JSON.stringify(error));
    }
  }

  get isActive(): boolean {
    return this.activated;
  }
}
