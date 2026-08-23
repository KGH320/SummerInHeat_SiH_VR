# v1.10.0 Release Notes

## English

### OpenXR MagicaCloth2 Grip Grab

- Added an experimental OpenXR-only grip interaction for nearby movable MagicaCloth2 particles, allowing each hand to temporarily control physics-enabled clothing such as skirts.
- Added independent left- and right-hand grip poses based on the rendered hand model, so a captured particle follows the hand that acquired it.
- Grips consumed by an active or pending clothing capture no longer also trigger the normal grip interaction.
- Added safe release handling for grip release, missing hand poses, scene changes, and excessive target distance. Released particles inherit a capped portion of hand motion.
- Added runtime compatibility checks for the MagicaCloth2 data layout. The grab feature disables itself when the required runtime bindings are unavailable instead of writing through an unknown layout.

### Grab Stability and Configuration

- Replaced the initial fixed-step grab transition with damped targets to reduce abrupt movement and improve clothing grab stability.
- Added the following OpenXR configuration options:

| Config | Type | Default | Description |
| --- | --- | --- | --- |
| `OpenXR Enable MagicaCloth Grab` | Boolean | `true` | Enables experimental grip capture for nearby movable MagicaCloth2 particles. |
| `OpenXR MagicaCloth Grab Radius` | Float | `0.075` | Search radius in meters for particles eligible for grip capture. |
| `OpenXR MagicaCloth Grab Max Particles` | Integer | `2` | Maximum movable particles each hand can capture. |

### Logging

- Added a `Debug` log level for Unity VR Mod diagnostics.
- Runtime debug messages are emitted only when `Log Level` is set to `Debug`, keeping normal Warning and Info logging free from high-cost diagnostic output.

### Documentation

- Documented skirt and physics-clothing control in the English, Chinese, and Japanese READMEs.

### Notes

- MagicaCloth grip grab is experimental and available only in the OpenXR build.
- To control nearby physics clothing, press and hold the controller Grip button. Release Grip to release the captured particles.
- This release note summarizes committed changes in `1.9.2..3f256ee`.

## 中文

### OpenXR MagicaCloth2 握持抓取

- 新增仅限 OpenXR 的实验性握持交互，可让左右手暂时控制附近可移动的 MagicaCloth2 粒子，用于操作裙子等带物理效果的衣物。
- 为左右手分别提供基于渲染手部模型的握持姿势，使被抓取的粒子跟随实际抓取它的手。
- 衣物抓取处于等待或生效状态时，会占用对应手的握持输入，不会同时触发原有的握持交互。
- 在松开握持、手部姿势丢失、切换场景或目标距离过远时安全释放粒子；释放时会继承受限的手部运动速度。
- 增加 MagicaCloth2 运行时数据布局兼容性检查。无法获得所需绑定时会停用抓取功能，不会向未知的数据布局写入数据。

### 抓取稳定性与配置

- 将原有的固定步长抓取过渡替换为阻尼目标，减少突兀位移并提升衣物抓取稳定性。
- 新增以下 OpenXR 配置项：

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `OpenXR Enable MagicaCloth Grab` | 布尔值 | `true` | 启用实验性的 MagicaCloth2 附近可移动粒子握持抓取。 |
| `OpenXR MagicaCloth Grab Radius` | 浮点数 | `0.075` | 可被握持抓取的粒子搜索半径，单位为米。 |
| `OpenXR MagicaCloth Grab Max Particles` | 整数 | `2` | 每只手最多可抓取的可移动粒子数。 |

### 日志

- 为 Unity VR Mod 诊断信息新增 `Debug` 日志级别。
- 仅当 `Log Level` 设置为 `Debug` 时才输出运行时调试信息，使普通 Warning 和 Info 日志不受高开销诊断信息影响。

### 文档

- 已在英文、中文和日文 README 中补充裙子及物理衣物控制说明。

### 说明

- MagicaCloth 握持抓取为实验性功能，仅在 OpenXR 构建中可用。
- 靠近可操作的物理衣物后，按住控制器握持键即可控制粒子；松开握持键即可释放。
- 本发布说明汇总的提交范围为 `1.9.2..3f256ee`。
