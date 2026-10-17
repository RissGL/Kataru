# KATARU · HarmonyOS 版

桌面版 KATARU（WPF/C#）的鸿蒙重写版。**不是移植**——C#/WPF 在鸿蒙上跑不了，
这里是用 **ArkTS / ArkUI** 重写，但**算法和数据结构沿用桌面上那套**（桌面版已有 269 项自检覆盖）。

> ✅ **当前状态：命令行构建通过**（`hvigor assembleHap` → BUILD SUCCESSFUL，产出 502 KB 的 HAP）

## 当前进度

已完成（可直接跑）：

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| SRT 解析 | `entry/src/main/ets/model/SrtParser.ets` | BOM / CRLF / 序号缺失 / 多行字幕 / 逗号小数点 |
| 字幕同步 | `model/SubtitleSynchronizer.ets` | 播放时游标前进 O(1)，跳转时二分重定位 |
| 时间码 | `model/Timecode.ets` | 解析 + 格式化 |
| 语言识别 | `model/SubtitleLanguage.ets` | `xx.zh.srt` / `xx.chs.srt` / `xx.ja.srt` 等 |
| 主界面 | `pages/Index.ets` | 选音频 + 选字幕、播放/暂停、±10s、进度条、大字幕、阅读列表自动高亮滚动 |

还没做（下一步）：

- 多语言双语轨道（模型已就绪，界面还没接）
- 媒体库扫描（桌面版是扫文件夹；鸿蒙这里还要处理文件访问权限）
- 锁屏/通知栏播控（AVSession）+ 后台播放
- 播放进度记忆（preferences）
- 桌面版那个"悬浮字幕"在手机上做不了（全局悬浮窗要系统级权限），
  替代方案是**应用小窗**（用户自己把 app 拖成小窗）

## 怎么跑起来

### 0. 先确认手机是鸿蒙 NEXT

设置 → 关于手机 → 看「HarmonyOS 版本」：

- **HarmonyOS NEXT 5.x / 6.x** → 可以装这个工程
- **HarmonyOS 4.x 及更早**（本质是安卓底座）→ 装不了，这个工程是 NEXT 的 HAP

### 1. 命令行构建（不用开 IDE）

```powershell
cd D:\GitHubFiles\AudioBookPlayer\AudioBookPlayer\harmony\KATARU
.\build.ps1
```

产物：`entry\build\default\outputs\default\entry-default-unsigned.hap`

脚本会自动使用 DevEco 自带的 java / node / hvigor。
DevEco 装在别处就改脚本顶部的 `$DevEco` 变量。

### 2. 用 DevEco 打开工程

`File → Open` → 选 `harmony\KATARU` → 等右下角 Sync 完成。

若提示「SDK 版本不匹配 / 需要下载 API 12」，可以：

- 让它下载（约 1GB），或
- 让我把 `build-profile.json5` 的 `compatibleSdkVersion` 改成你已装的 API 26

### 3. 配签名（必须，否则装不进真机）

1. 右上角 **Sign In** 登录华为账号
2. `File → Project Structure → Signing Configs`
3. 勾 **Automatically generate signature** → Apply

> HarmonyOS NEXT 侧载调试一般要求账号**实名认证**，没认证这步会失败。

### 4. 手机准备

1. 设置 → 关于手机 → 连点「版本号」7 次 → 开启开发者模式
2. 设置 → 系统 → 开发者选项 → 打开 **USB 调试**
3. 数据线连电脑 → 手机弹「允许 USB 调试」→ 允许

### 5. 运行

DevEco 右上角设备下拉选到你的手机 → 点绿色三角 **Run 'entry'**。

首屏操作顺序：**选音频** → **选字幕**（.srt）→ **播放**。
中间大字幕会跟着走，下面的阅读列表自动高亮并滚动。

### 6. 出问题怎么办

- **构建报错** → 把 DevEco 的 Build 窗口文字贴给我
- **App 崩溃 / 没声音 / 字幕不显示** → DevEco 底部 **HiLog** 窗口，过滤 `KATARU`，
  里面的 `state: xxx` 会显示 AVPlayer 状态机走到哪一步

## 构建环境（本机实测）

| 组件 | 位置 / 版本 |
| --- | --- |
| DevEco Studio | `D:\HormanyOS\DevEco Studio`（26.0.0.851） |
| SDK | `sdk\default\openharmony`（API 26，platformVersion 26.0.0） |
| hvigor | 6.26.8 |
| ohpm | 26.0.0.630 |
| Node | 24.14.1（DevEco 自带） |
| JBR | 25.0.2（DevEco 自带，打包阶段需要） |

## 和桌面版的关系

- 算法、数据结构、语言识别规则与桌面版一致
- 配色（黑 / 米白 / 暗红 / 金）在 `resources/base/element/color.json` 里，和桌面版同一套值
- 图标复用桌面版的 app.ico（从 ICO 里取的 256×256 PNG 帧）
- m4b 的章节 / 封面解析（桌面版 `MediaTagReader`）还没移植——AVPlayer 能直接播 m4b 音频，
  但读章节要靠自己解析 MP4 box，这块可以之后搬过来
