import { UIAbility } from '@kit.AbilityKit';
import { window } from '@kit.ArkUI';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * KATARU 入口 Ability。
 *
 * 只做一件事：把窗口背景设成深色（和桌面版同一套配色），
 * 免得启动瞬间闪一下白底。
 */
export default class EntryAbility extends UIAbility {
  private static readonly TAG: string = 'KATARU';

  onCreate(): void {
    hilog.info(0x0000, EntryAbility.TAG, 'ability onCreate');
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    windowStage.loadContent('pages/Index', (err: BusinessError) => {
      if (err.code !== 0) {
        hilog.error(0x0000, EntryAbility.TAG, 'loadContent failed: %{public}s', JSON.stringify(err));
        return;
      }

      // 深色底：和桌面版 #121011 一致
      // 注意：setWindowSystemBarProperties 在 phone/tablet/2in1 上不可用，所以只设窗口底色，
      // 状态栏颜色交给系统深色模式处理（避免适配告警）。
      const mainWindow = windowStage.getMainWindowSync();
      mainWindow.setWindowBackgroundColor('#121011');
      hilog.info(0x0000, EntryAbility.TAG, 'window ready');
    });
  }
}
