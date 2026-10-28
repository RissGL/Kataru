import { formBindingData, formProvider } from '@kit.FormKit';
import { common } from '@kit.AbilityKit';
import { preferences } from '@kit.ArkData';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 往桌面卡片推当前字幕。
 *
 * 卡片（SubtitleCardAbility）在 onAddForm 里把 formId 写进 kataru_forms，
 * 这里读出来逐个 updateForm —— 桌面那张卡就常驻显示正在读的这一句。
 */
export class SubtitleCardPusher {
  private static readonly TAG: string = 'KATARU';
  private static readonly PREFS: string = 'kataru_forms';
  /** 系统对卡片刷新有频率限制，这里自己再节流一下 */
  private static readonly MIN_INTERVAL_MS: number = 2000;
  private lastPushMs: number = 0;
  private lastText: string = '';

  /** 有没有卡片在桌面上 */
  hasCards(context: common.UIAbilityContext): boolean {
    return this.readFormIds(context).length > 0;
  }

  /** 推一次（字幕变了才推，且不超过 2 秒一次） */
  async push(
    context: common.UIAbilityContext,
    title: string,
    subtitle: string,
    secondary: string,
    state: string,
    force: boolean = false
  ): Promise<void> {
    const now = Date.now();
    if (!force && subtitle === this.lastText && now - this.lastPushMs < SubtitleCardPusher.MIN_INTERVAL_MS) {
      return;
    }
    if (!force && now - this.lastPushMs < SubtitleCardPusher.MIN_INTERVAL_MS) {
      return;
    }

    const ids = this.readFormIds(context);
    if (ids.length === 0) {
      return;
    }

    this.lastText = subtitle;
    this.lastPushMs = now;

    const data: Record<string, string> = {
      'title': title,
      'subtitle': subtitle,
      'secondary': secondary,
      'state': state
    };

    for (const id of ids) {
      try {
        await formProvider.updateForm(id, formBindingData.createFormBindingData(data));
      } catch (error) {
        hilog.warn(0x0000, SubtitleCardPusher.TAG, 'update form %{public}s failed: %{public}s',
          id, JSON.stringify(error));
      }
    }
  }

  /** 停止播放时把卡片变成空闲态 */
  async idle(context: common.UIAbilityContext, title: string): Promise<void> {
    await this.push(context, title, '已暂停', '', '已暂停', true);
  }

  private readFormIds(context: common.UIAbilityContext): string[] {
    try {
      const store = preferences.getPreferencesSync(context, { name: SubtitleCardPusher.PREFS });
      const raw = store.getSync('form_ids', '') as string;
      if (raw.length === 0) {
        return [];
      }
      return JSON.parse(raw) as string[];
    } catch (error) {
      return [];
    }
  }
}
