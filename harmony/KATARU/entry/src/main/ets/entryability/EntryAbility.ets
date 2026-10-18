import { UIAbility } from '@kit.AbilityKit';
import { window } from '@kit.ArkUI';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { Want } from '@kit.AbilityKit';

/**
 * KATARU 入口 Ability。
 *
 * 只做一件事：把窗口背景设成深色（和桌面版同一套配色），
 * 免得启动瞬间闪一下白底。
 */
export default class EntryAbility extends UIAbility {
  private static readonly TAG: string = 'KATARU';

  onCreate(want: Want): void {
    hilog.info(0x0000, EntryAbility.TAG, 'ability onCreate');
    EntryAbility.collectSharedUris(want);
  }

  onNewWant(want: Want): void {
    hilog.info(0x0000, EntryAbility.TAG, 'onNewWant');
    EntryAbility.collectSharedUris(want);
  }

  /**
   * 把"分享进来"的文件 URI 收进 AppStorage，页面那边取出来直接导入。
   *
   * 鸿蒙的分享参数有几种形态（单文件 uri、多文件数组、ability.params.stream），
   * 这里都兼容一下；拿到的字符串里可能带 file:// 前缀，统一处理。
   */
  private static collectSharedUris(want: Want): void {
    if (want === null || want === undefined) {
      return;
    }

    const uris: string[] = [];
    const action = want.action ?? '';
    hilog.info(0x0000, EntryAbility.TAG, 'want action=%{public}s', action);

    if (action !== 'ohos.want.action.sendData') {
      return;
    }

    const single = want.uri;
    if (single !== undefined && single.length > 0) {
      uris.push(single);
    }

    const parameters = want.parameters;
    if (parameters !== undefined && parameters !== null) {
      const stream = parameters['ability.params.stream'];
      if (typeof stream === 'string' && stream.length > 0) {
        for (const part of stream.split(';')) {
          if (part.length > 0) {
            uris.push(part);
          }
        }
      }

      const extra = parameters['ohos.extra.param.key.contentUriList'];
      if (typeof extra === 'string' && extra.length > 0) {
        for (const part of extra.split(';')) {
          if (part.length > 0) {
            uris.push(part);
          }
        }
      }
    }

    hilog.info(0x0000, EntryAbility.TAG, 'shared uris: %{public}d', uris.length);
    if (uris.length > 0) {
      AppStorage.setOrCreate<string[]>('sharedUris', uris);
    }
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
