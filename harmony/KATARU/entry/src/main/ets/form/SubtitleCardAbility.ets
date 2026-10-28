import { formBindingData, FormExtensionAbility, formInfo, formProvider } from '@kit.FormKit';
import { Want } from '@kit.AbilityKit';
import { preferences } from '@kit.ArkData';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 桌面卡片（悬浮字幕）的 FormExtensionAbility。
 *
 * 普通应用拿不到系统悬浮窗权限（SYSTEM_FLOAT_WINDOW 是系统权限），
 * 桌面服务卡片是官方给第三方的正规口子：把卡片放到桌面，App 播放时
 * 往卡片推当前这句字幕，桌面上就常驻一行字幕。
 */
export default class SubtitleCardAbility extends FormExtensionAbility {
  private static readonly TAG: string = 'KATARU';
  private static readonly PREFS: string = 'kataru_forms';

  /** 卡片被添加到桌面 */
  onAddForm(want: Want): formBindingData.FormBindingData {
    let formId = '';
    if (want.parameters !== undefined && want.parameters !== null) {
      const value = want.parameters[formInfo.FormParam.IDENTITY_KEY];
      if (typeof value === 'string') {
        formId = value;
      }
    }
    hilog.info(0x0000, SubtitleCardAbility.TAG, 'form added: %{public}s', formId);
    if (formId.length > 0) {
      this.rememberFormId(formId);
    }

    const data: Record<string, string> = {
      'title': 'KATARU',
      'subtitle': '在 App 里播放有声书，这里会显示当前这句',
      'secondary': '',
      'state': '空闲'
    };
    return formBindingData.createFormBindingData(data);
  }

  onRemoveForm(formId: string): void {
    hilog.info(0x0000, SubtitleCardAbility.TAG, 'form removed: %{public}s', formId);
    this.forgetFormId(formId);
  }

  onUpdateForm(formId: string): void {
    hilog.info(0x0000, SubtitleCardAbility.TAG, 'form update: %{public}s', formId);
    this.rememberFormId(formId);
  }

  onFormEvent(formId: string, message: string): void {
    hilog.info(0x0000, SubtitleCardAbility.TAG, 'form event: %{public}s %{public}s', formId, message);
  }

  onCastToNormalForm(formId: string): void {
    hilog.info(0x0000, SubtitleCardAbility.TAG, 'form cast to normal: %{public}s', formId);
  }

  /** 卡片 id 存进偏好文件，App 侧推送时要用（同一个应用沙箱，跨进程可见） */
  private rememberFormId(formId: string): void {
    try {
      const store = preferences.getPreferencesSync(this.context, { name: SubtitleCardAbility.PREFS });
      const raw = store.getSync('form_ids', '') as string;
      const ids: string[] = raw.length > 0 ? JSON.parse(raw) as string[] : [];
      if (ids.indexOf(formId) < 0) {
        ids.push(formId);
        store.putSync('form_ids', JSON.stringify(ids));
        store.flush();
      }
    } catch (error) {
      hilog.warn(0x0000, SubtitleCardAbility.TAG, 'remember form id failed: %{public}s', JSON.stringify(error));
    }
  }

  private forgetFormId(formId: string): void {
    try {
      const store = preferences.getPreferencesSync(this.context, { name: SubtitleCardAbility.PREFS });
      const raw = store.getSync('form_ids', '') as string;
      if (raw.length === 0) {
        return;
      }
      const ids: string[] = JSON.parse(raw) as string[];
      const index = ids.indexOf(formId);
      if (index >= 0) {
        ids.splice(index, 1);
        store.putSync('form_ids', JSON.stringify(ids));
        store.flush();
      }
    } catch (error) {
      hilog.warn(0x0000, SubtitleCardAbility.TAG, 'forget form id failed: %{public}s', JSON.stringify(error));
    }
  }
}
