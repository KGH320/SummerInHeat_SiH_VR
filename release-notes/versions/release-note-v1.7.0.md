# v1.7.0 Release Notes

## English

### OpenXR Hand Models

- Added packaged OpenXR hand model assets under `OpenXRHandModels`.
- Reworked OpenXR controller visualization to load hand models from an AssetBundle instead of relying only on generated primitives.
- Added configurable left and right hand model asset names, hand model bundle path, hand scale, and finger curl offset.
- Added lightweight JSON parsing and hand pose model helpers for hand asset metadata.
- Refined OpenXR hand rendering and collider alignment so visual hands and runtime interaction proxies share the same controller pose basis.

### OpenXR Passthrough

- Added `XR_FB_passthrough` support for Meta/OpenXR runtimes that expose the extension.
- Added runtime passthrough toggling through `OpenXR Toggle Passthrough Keybind`.
- Added passthrough background color switching: when passthrough is supported, the same toggle enables passthrough and applies the configured game background color; when unsupported, the toggle only switches the configured background color.
- Added a Unity Blit alpha-fix shader bundle so the OpenXR projection layer can use source alpha while preserving opaque character pixels.
- Added packaged OpenXR shader assets under `OpenXRShaders`.

### Build And Packaging

- Updated `build.ps1` to package OpenXR hand model assets and OpenXR shader assets into release and deployment output.
- Added `copy.ps1` for copying packaged plugin output.
- Added BepInEx 6 Harmony dependency packaging through `lib/bie6/0Harmony.dll`.
- Updated solution and project build metadata for the new packaging layout.

### Physics Diagnostics

- Removed the hardcoded `PHYSICS_LOG` symbol from the BepInEx 5 OpenXR release configuration.
- `PHYSICS_LOG` diagnostics are now enabled through the explicit build option instead of always being compiled into that release target.

### Documentation

- Updated README documentation for the new OpenXR hand model packaging and runtime setup.

### Configuration

| Config | Type | Default | Description |
| --- | --- | --- | --- |
| `OpenXR Hand Model AssetBundle Path` | String | `OpenXRHandModels\openxr_hands` | AssetBundle path for OpenXR hand model prefabs. Relative paths are resolved from the plugin DLL folder. |
| `OpenXR Left Hand Model Name` | String | `LeftHand` | AssetBundle prefab name for the left hand model. |
| `OpenXR Right Hand Model Name` | String | `RightHand` | AssetBundle prefab name for the right hand model. |
| `OpenXR Hand Model Scale` | Float | `1.0` | Scale multiplier applied to loaded OpenXR hand prefabs. |
| `OpenXR Hand Finger Curl Degrees` | Float | `90.0` | Additional local finger curl rotation applied to loaded hand model bones. |
| `OpenXR Toggle Passthrough Keybind` | KeyCode | `F9` | Toggles `XR_FB_passthrough` and passthrough background color. If passthrough is unavailable, only toggles the background color. |
| `OpenXR Passthrough Background Color` | String | `0 0 0 255` | Solid game background color used by passthrough alpha fixing. Format: `R G B A` or `R,G,B,A`. |
| `OpenXR Passthrough Alpha Fix Shader Bundle Path` | String | `OpenXRShaders\passthrough_alpha_fix` | AssetBundle path for shader `Hidden/UnityVRMod/PassthroughAlphaFix`. |

### Notes

- `XR_FB_passthrough` depends on OpenXR runtime support. If the extension is unavailable, passthrough cannot be enabled, but the configured background color can still be toggled.
- Existing generated config files keep their current values. New config entries may need to be added by regenerating or manually updating the config file.
- `PHYSICS_LOG` diagnostics should be enabled with `/p:PhysicsLog=true` or `build.ps1 -PhysicsLog` when diagnostic builds are needed.

## 中文

### OpenXR 手部模型

- 新增打包的 OpenXR 手部模型资源，位于 `OpenXRHandModels`。
- 重做 OpenXR 控制器可视化，支持从 AssetBundle 加载手部模型，不再只依赖运行时生成的基础几何体。
- 新增左右手模型资源名、手部模型 AssetBundle 路径、手部缩放、手指弯曲偏移等配置。
- 新增轻量 JSON 解析和手部姿态模型辅助逻辑，用于读取手部资源元数据。
- 调整 OpenXR 手部渲染和碰撞代理对齐，使可视手部与运行时交互代理使用一致的控制器 pose 基准。

### OpenXR 透视

- 新增对 `XR_FB_passthrough` 的支持，用于暴露该扩展的 Meta/OpenXR 运行时。
- 新增通过 `OpenXR Toggle Passthrough Keybind` 在运行时切换透视。
- 新增透视背景色切换：支持透视时，同一个按键会开启透视并应用配置的游戏背景色；不支持透视时，该按键只切换配置的背景色。
- 新增 Unity Blit alpha 修复 shader bundle，使 OpenXR projection layer 可以使用 source alpha，同时保留人物等非背景像素的不透明效果。
- 新增打包的 OpenXR shader 资源，位于 `OpenXRShaders`。

### 构建与打包

- 更新 `build.ps1`，将 OpenXR 手部模型资源和 OpenXR shader 资源复制到 release 与部署输出。
- 新增 `copy.ps1`，用于复制打包后的插件输出。
- 新增 BepInEx 6 Harmony 依赖打包，文件为 `lib/bie6/0Harmony.dll`。
- 更新 solution 和 project 构建元数据，以适配新的打包结构。

### 物理诊断

- 移除 BepInEx 5 OpenXR release 配置中硬编码的 `PHYSICS_LOG` 符号。
- `PHYSICS_LOG` 诊断现在只通过显式构建选项启用，不再默认编译进该 release 目标。

### 文档

- 更新 README 中关于 OpenXR 手部模型打包和运行时设置的说明。

### 配置项

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `OpenXR Hand Model AssetBundle Path` | 字符串 | `OpenXRHandModels\openxr_hands` | OpenXR 手部模型 prefab 的 AssetBundle 路径。相对路径会基于插件 DLL 所在目录解析。 |
| `OpenXR Left Hand Model Name` | 字符串 | `LeftHand` | 左手模型在 AssetBundle 中的 prefab 名称。 |
| `OpenXR Right Hand Model Name` | 字符串 | `RightHand` | 右手模型在 AssetBundle 中的 prefab 名称。 |
| `OpenXR Hand Model Scale` | 浮点数 | `1.0` | 加载后的 OpenXR 手部 prefab 缩放倍率。 |
| `OpenXR Hand Finger Curl Degrees` | 浮点数 | `90.0` | 额外应用到手部模型骨骼的本地手指弯曲角度。 |
| `OpenXR Toggle Passthrough Keybind` | KeyCode | `F9` | 切换 `XR_FB_passthrough` 和透视背景色。如果透视不可用，则只切换背景色。 |
| `OpenXR Passthrough Background Color` | 字符串 | `0 0 0 255` | 透视 alpha 修复使用的游戏纯色背景。格式为 `R G B A` 或 `R,G,B,A`。 |
| `OpenXR Passthrough Alpha Fix Shader Bundle Path` | 字符串 | `OpenXRShaders\passthrough_alpha_fix` | shader `Hidden/UnityVRMod/PassthroughAlphaFix` 的 AssetBundle 路径。 |

### 说明

- `XR_FB_passthrough` 取决于 OpenXR 运行时是否支持该扩展。如果扩展不可用，无法启用原生透视，但仍可以切换配置的背景色。
- 已生成的配置文件会保留现有值。新增配置项可能需要重新生成配置文件或手动补充。
- 需要物理诊断构建时，请通过 `/p:PhysicsLog=true` 或 `build.ps1 -PhysicsLog` 显式开启 `PHYSICS_LOG`。
