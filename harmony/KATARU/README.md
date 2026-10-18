# KATARU · HarmonyOS 版

桌面版 KATARU（WPF/C#）的鸿蒙重写版。**不是移植**——C#/WPF 在鸿蒙上跑不了，
这里是用 **ArkTS / ArkUI** 重写，但**算法和数据结构沿用桌面上那套**（桌面版已有 269 项自检覆盖）。

> ✅ **当前状态：命令行构建通过**（`hvigor assembleHap` → BUILD SUCCESSFUL，产出 503 KB 的 HAP）
>
> **目标 API：HarmonyOS 6.0.0 / API 20**（`minAPIVersion = 60000020`），
> 可在 HarmonyOS 6.1.0 / API 24 的真机（MIA-AL00）上安装运行。

## 真机验证过的状态（MIA-AL00 / HarmonyOS 6.1 / API 24）

- ✅ 安装成功、启动成功（`ability onCreate` / `window ready`，无崩溃）
- ✅ 四个模式页：书库 / 播放 / 阅读 / 设置（底部 Tabs）
- ✅ 书库扫描：一本书一个文件夹，自动配同名字幕，找封面（cover.png 等）
- ✅ 字幕字号/字体可调
- ⚠️ **公共 Download 目录不能直接读**：`/storage/media/100/local/files/Docs/Download`
  连 shell 都 `mkdir: Permission denied`，App 也读不到。
  所以改成：**用户用系统文件选择器选一次文件夹**，再用 `fileShare.persistPermission`
  持久化授权，之后每次启动直接用（存在 preferences 里）。

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
| 目标版本 | `compatibleSdkVersion` / `targetSdkVersion` = **`6.0.0(20)`** |
| 实测设备 | MIA-AL00 · HarmonyOS 6.1.0 · API 24 |
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

## 关于 SDK 版本配对（踩过的坑）

`build-profile.json5` 里的 `compatibleSdkVersion` / `targetSdkVersion` **不是随便写数字就行**，
hvigor 会拿它跟已安装的 SDK 做**精确配对校验**。本机实测：

| 写法 | 结果 |
| --- | --- |
| `24` / `26`（纯数字） | ❌ Schema validate failed |
| `"6.0.0(24)"` | ❌ platform version 与 API version 不匹配 |
| `"6.1.0(24)"` | ❌ 同上 |
| `"6.1.0(26)"` | ❌ 同上 |
| `"26.0.0(26)"` | ❌ 同上 |
| **`"6.0.0(20)"`** | ✅ BUILD SUCCESSFUL |

也就是说：**只有 SDK 认得的「平台版本(API 等级)」组合才能用**。
想换成别的版本，得先用 DevEco 的 SDK Manager 把对应的 SDK 装上。

## 安装到真机必须签名

`bm install` 报 `error: no signature file (code:9568320)` = HAP 未签名。
在 DevEco 中文版里：`文件(File) → 项目结构(Project Structure) → 签名配置(Signing Configs)`
→ 勾选「**自动生成签名**」（也可直接点报错里的「**打开签名配置**」链接）。

前置条件：华为账号已登录 + **已实名认证**，且手机已连接（自动签名要把设备 UDID 写进 profile）。
## 手机上怎么放书（重要）

1. 用手机自带「文件管理」在 **Download** 下新建文件夹 **KATARU Book**（USB 连电脑传也可以）
2. 里面一个书一个文件夹，例如：
   ```
   Download/KATARU Book/化物語 上/01 ひたぎクラブ.mp3
   Download/KATARU Book/化物語 上/01 ひたぎクラブ.srt
   Download/KATARU Book/化物語 上/cover.png
   ```
3. 打开 App → **设置 → 选择书库文件夹** → 选到 `KATARU Book` → 授权
4. 回到**书库**，会自动扫描出书；点书进入播放

> 为什么必须手动选一次：鸿蒙把公共下载目录保护起来了，第三方 App 拿不到
> 「直接按路径读」的权限，只能用系统选择器授权。选一次就记住。

## 还没做

- **灵动岛 / 实况窗**：需要 LiveViewKit + **申请正式权限**（Huawei 侧审批），
  而且必须真机调试。单独一轮做。
- 双语对照（多语言轨道）：`SubtitleLanguage.ets` 已就绪，阅读页还没接。
- 后台播放 / 锁屏播控（AVSession）、进度记忆（preferences 已用上，播放进度还没存）。
## 为什么是"导入式书库"（重要）

鸿蒙 6.1 上第三方 App **读不到公共 Download 目录**，实测三条路都堵死：

| 尝试 | 结果 |
| --- | --- |
| 按路径直读 `/storage/media/100/local/files/Docs/Download/...` | `code 13900001` Operation not permitted |
| 申请 `ohos.permission.READ_WRITE_DOWNLOAD_DIRECTORY` | `authResults = 2`（系统拒绝，受限权限） |
| 文件选择器「选文件夹」`DocumentSelectMode.FOLDER` | 「该功能暂不支持」（该机型未开放） |
| shell 里 `mkdir` 该目录 | Permission denied |

**所以只有一条路**：用系统选择器选**具体文件**（这个所有机型都支持），
App 用 `fileShare.persistPermission` 持久化授权，把 URI 存进 preferences。

### 用户操作

1. 书库页点 **「导入书本」**
2. 一次多选这本书的文件：**音频 (.m4b/.mp3) + 字幕 (.srt) + 封面 (png)**
3. 自动配对（同名 / 同名+语言后缀）并存下来
4. 之后直接点书就播放，重启也不丢（URI 授权已持久化）

> 这是鸿蒙平台限制，不是偷懒。桌面版能扫目录是因为 Windows 桌面没有这层沙箱。