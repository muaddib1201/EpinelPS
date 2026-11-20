# 当前客户端可开放的招募池

核对日期：2026-11-21（北京时间）

核对环境：
- 客户端：153.6.10，资源版本 `153-b55624c389`
- 服务器静态数据：`qa-261029-10b/578481`

## 判断标准

一个招募池要能在客户端正常显示，以下三样资源客户端必须都有：

1. 活动横幅使用的图片表（`EventManagerTable.EventBannerResourceTable`）
2. 图片表里对应的大小两张横幅（`EventBannerResourceId` 和 `EventThumbnailResourceId`）
3. 招募页界面（`GachaTypeTable.GachaPagePrefab`）

缺任何一样都不能开放。缺图片表时，客户端在 `LobbyBanner.Setup` 抛出 `Localized sprite table not found`，主页会卡在只有背景的状态。

当前客户端只保留最近 12 期活动的图片表（`event_260409` 至 `event_261029`）和 `perm_*` 常驻表，没有 `event_old`，也没有 `event_261119`。

## 可开放的 25 个招募池

| 卡池 | 类型 | 界面分类 | 活动 | 横幅图片表 / 图片 | 招募界面 | 角色 |
|---|---|---|---|---|---|---|
| 10070 | 限定招募 | 普通 | 70130 | `event_260409` / `EVENT_BANNER_GACHA_C620_S` | `base_gacha_type_pickup` | 白鹤 |
| 10071 | 限定招募 | 特别 | 70131 | `event_260423` / `EVENT_BANNER_GACHA_C017_S` | `base_gacha_type_pickup_special_c017` | 阿妮斯：超级巨星 |
| 10072 | 限定招募 | 特别 | 70132 | `event_260423` / `EVENT_BANNER_GACHA_C018_S` | `base_gacha_type_pickup_special_c018` | 尼恩：透视之眼 |
| 10073 | 限定招募 | 普通 | 70133 | `event_260514` / `EVENT_BANNER_GACHA_C600_S` | `base_gacha_type_pickup` | 敏特 |
| 10074 | 限定招募 | 复刻 | 70134 | `event_260514` / `EVENT_BANNER_GACHA_RE_C850_S` | `base_gacha_type_pickup_special_c850` | 伊芙 |
| 10075 | 限定招募 | 复刻 | 70135 | `event_260514` / `EVENT_BANNER_GACHA_RE_C851_S` | `base_gacha_type_pickup_special_c851` | 渡鸦 |
| 10076 | 限定招募 | 普通 | 70136 | `event_260528` / `EVENT_BANNER_GACHA_C601_S` | `base_gacha_type_pickup` | 普莉卡 |
| 10077 | 限定招募 | 普通 | 70137 | `event_260611` / `EVENT_BANNER_GACHA_C570_S` | `base_gacha_type_pickup` | 方舟黑色游侠 |
| 10078 | 自选招募 | 复刻 | 70138 | `event_260611` / `EVENT_BANNER_GACHA_SELECT_03_S` | `base_gacha_type_pickup_return_03` | 樱花：夏日绽放、罗珊娜：高雅海洋、阿妮斯：闪耀夏日、海伦：海蓝宝石、梅里：海湾女神、尼恩：蓝色海洋 |
| 10079 | 限定招募 | 复刻 | 70139 | `event_260702` / `EVENT_BANNER_GACHA_RE_C234_S` | `base_gacha_type_pickup_special_c234` | 桃乐丝：机缘巧遇 |
| 10080 | 限定招募 | 复刻 | 70140 | `event_260702` / `EVENT_BANNER_GACHA_RE_C502_S` | `base_gacha_type_pickup_special_c502` | 伊莱格：BOOM与惊吓 |
| 10081 | 限定招募 | 限定 | 70141 | `event_260702` / `EVENT_BANNER_GACHA_C515_S` | `base_gacha_type_pickup_special_c515` | 灰姑娘：琉璃波光 |
| 10082 | 限定招募 | 限定 | 70142 | `event_260702` / `EVENT_BANNER_GACHA_C322_S` | `base_gacha_type_pickup_special_c322` | 玛律恰那：海洋进修 |
| 10083 | 限定招募 | 普通 | 70143 | `event_260723` / `EVENT_BANNER_GACHA_C103_S` | `base_gacha_type_pickup` | 拉普拉斯：究极英雄 |
| 10084 | 限定招募 | 普通 | 70144 | `event_260723` / `EVENT_BANNER_GACHA_C105_S` | `base_gacha_type_pickup` | 麦斯威尔：平凡技师 |
| 10085 | 限定招募 | 限定 | 70145 | `event_260813` / `EVENT_BANNER_GACHA_C870_S` | `base_gacha_type_pickup_special_c870` | QUEEN（真） |
| 10086 | 限定招募 | 限定 | 70146 | `event_260813` / `EVENT_BANNER_GACHA_C871_S` | `base_gacha_type_pickup_special_c871` | 雪子 |
| 10087 | 限定招募 | 普通 | 70147 | `event_260903` / `EVENT_BANNER_GACHA_C104_S` | `base_gacha_type_pickup` | 德雷克：终极反派 |
| 10088 | 限定招募 | 普通 | 70148 | `event_260917` / `EVENT_BANNER_GACHA_C404_S` | `base_gacha_type_pickup` | 吉尔提：神力兔女郎 |
| 10089 | 限定招募 | 普通 | 70149 | `event_260917` / `EVENT_BANNER_GACHA_C405_S` | `base_gacha_type_pickup` | 森：疾速兔女郎 |
| 10090 | 自选招募 | 复刻 | 70150 | `event_260917` / `EVENT_BANNER_GACHA_SELECT_04_S` | `base_gacha_type_pickup_return_04` | 梅登：冰玫瑰、吉萝婷：寒冬杀手、鲁德米拉：冬日之主、米卡：雪地伙伴、露菲：冬日购物狂、安妮：奇迹仙女 |
| 10091 | 限定招募 | 普通 | 70151 | `event_261008` / `EVENT_BANNER_GACHA_C063_S` | `base_gacha_type_pickup` | 贝洛塔：南瓜女巫 |
| 10092 | 限定招募 | 特别 | 70152 | `event_261029` / `EVENT_BANNER_GACHA_C019_S` | `base_gacha_type_pickup_special_c019` | 玛丽安：女王 |
| 10093 | 限定招募 | 特别 | 70153 | `event_261029` / `EVENT_BANNER_GACHA_C472_S` | `base_gacha_type_pickup_special_c472` | 红莲：弑神 |
| 10094 | 自选招募 | 特别 | 70154 | `event_261029` / `EVENT_BANNER_GACHA_SELECT_PILGRIM_02_S` | `base_gacha_type_pickup_pilgrim_return_02` | 灰姑娘、娜由塔、格拉维 |

"界面分类"取自 `GachaTypeTable.GachaPrefabType`：普通 = None，特别 = Special，限定 = Limited，复刻 = ReLimited。

## 已开放

- **10090 冬季复刻自选招募**：2026-11-21 02:38:42 至 2026-12-12 02:38:42（北京时间），共 21 天，提交 `7be3c8c`。
  - 单独排期开放，只下发活动 `70150`。
  - 可选角色包括露菲「冬日购物狂」（选项 `10405`）和安妮「奇迹仙女」（选项 `10406`）。

## 开放注意事项

- **同期卡池会挂在大型活动下面**。例如 10088、10089、10090 都挂在 26-09-17 的 "Coin Rush Showdown" 下（根活动 `83400`，后台大厅横幅 `10121`）。在后台选中这期大厅活动，会一次下发 12 个活动，包括剧情、小游戏、商店、通行证、服装扭蛋和这三个卡池。只想开某个卡池时，应该像 10090 那样单独排期（见 `EpinelPS/Services/MiracleSnowRecruitment.cs`）。
- **单独排期的卡池**，抽卡时会检查开放期，付款只接受该卡池价格表里的方式。新增卡池前，先核对它的 `GachaPriceGroup`。
- **客户端会校验静态数据包签名**（2026-11-21 实测，日志报 `InvalidOperationException: Invalid signature`）。所以不能通过修改数据包，把旧卡池改指向现有的横幅或界面。客户端资源文件也都附带 `.nds` 签名文件。

## 不能开放的招募池

共 136 个：122 个限定招募、12 个阶梯招募、2 个自选招募。原因都是横幅图片表已从客户端删除（大多是 `event_old`），其中 29 个连招募界面也不在。

其中包括露菲「冬日购物狂」的单独卡池 101 和安妮「奇迹仙女」的单独卡池 102。这两个卡池在服务器里保持暂停（`MiracleSnowRecruitment.Suspended`），存档里的排期没有改动。

## 客户端更新后重新核对

客户端更新后，可开放的名单会变化。重新核对的步骤：

1. 解开 `Clients/NIKKE/Unity/com_proximabeta_NIKKE/com.shiftup.patch/*/catalog.ndb`，得到各资源目录。这些文件是 NKDB v1 格式：每块用文件头里的 16 字节密钥，按块号和偏移做 AES-CTR，再用 zlib 解压，结果是 SQLite。
   - 图片表是 `saus` 目录里的 `*.lsc`，解开后是 SQLite，`locale` 表的 `key` 字段就是图片名。
   - 招募界面在各目录的 `files_chunktype` 里，键名形如 `gachaui_assets_<界面名>_<哈希>.bundle`。
2. 从服务器静态数据读出每个卡池的 `EventBannerResourceTable`、`EventBannerResourceId`、`EventThumbnailResourceId` 和 `GachaPagePrefab`，逐个对照。
