/**
 * 桌面服务卡片：显示书名 + 当前这句字幕（相当于桌面常驻的"悬浮字幕"）。
 *
 * 数据由 App 通过 formProvider.updateForm 推进来（LocalStorage），
 * 卡片自己不读文件，刷新逻辑在 App/SubtitleCardPusher.ets。
 */
@Entry
@Component
struct SubtitleCard {
  @LocalStorageProp('title') title: string = 'KATARU';
  @LocalStorageProp('subtitle') subtitle: string = '在 App 里播放有声书，这里会显示当前这句';
  @LocalStorageProp('secondary') secondary: string = '';
  @LocalStorageProp('state') state: string = '空闲';

  build() {
    Column() {
      Row() {
        Text('KATARU')
          .fontSize(10)
          .fontColor('#C9A227')
          .fontWeight(FontWeight.Medium)

        Blank()

        Text(this.state)
          .fontSize(9)
          .fontColor('#998F86')
      }
      .width('100%')

      Text(this.subtitle.length > 0 ? this.subtitle : '—')
        .fontSize(13)
        .fontColor('#F1EBE1')
        .maxLines(3)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .width('100%')
        .margin({ top: 4 })

      if (this.secondary.length > 0) {
        Text(this.secondary)
          .fontSize(10)
          .fontColor('#B6ADA4')
          .maxLines(2)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
          .width('100%')
          .margin({ top: 2 })
      }

      Blank()

      Text(this.title.length > 0 ? this.title : 'KATARU')
        .fontSize(9)
        .fontColor('#8C837C')
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .width('100%')
    }
    .width('100%')
    .height('100%')
    .padding(12)
    .alignItems(HorizontalAlign.Start)
    .backgroundColor('#1A1718')
    // 点卡片直接回到 App（继续听）
    .onClick(() => {
      postCardAction(this, {
        action: 'router',
        abilityName: 'EntryAbility',
        params: {
          'fromCard': '1'
        }
      });
    })
  }
}
