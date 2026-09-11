# HP60C 手部关节 Sphere 跟踪

打开 `Assets/Scenes/ASJ.unity`，点击 Play。左侧 RGB 上叠加关节 Sphere，右侧保留深度预览。
如需重新配置场景，使用 Unity 菜单 `ASJ > Setup Hand Joint Spheres`。

每只手有 21 个真实 Sphere 和 21 段骨架连线，最多两只手。运行时在 Hierarchy 的
`ASJ_HandJointSpheres/LeftHand_21Joints`、`RightHand_21Joints` 下查看。丢失识别会隐藏对应物体。
`ASJHandJointTracker` 上可调整 `Sphere Diameter`、`Smoothing`、`Inference Fps`、`Lost Timeout`。
颜色按模型的 Left/Right 标签分配；镜像输入会影响左右手语义。

## 关节索引

0 Wrist；1–4 拇指；5–8 食指；9–12 中指；13–16 无名指；17–20 小指。
各指最后一个索引为指尖，其他索引从掌根向指尖排列。

```csharp
// 获取对应的 Sphere Transform；手未跟踪时 activeInHierarchy 为 false。
Transform indexTip = tracker.GetJoint(leftHand: true, index: 8);
if (indexTip != null && indexTip.gameObject.activeInHierarchy)
    target.localPosition = indexTip.localPosition;
```

坐标：Sphere.localPosition 为 RGB 对齐空间，图像宽度对应 2 个 Unity 单位，Y 向上。
Z 是模型估计的腕部相对深度，不是 HP60C 深度测量值。为避免影响原场景，预览根节点放在
(10000,10000,10000)，由独立正交相机渲染到透明 RenderTexture，再叠加到 RGB RawImage。
业务代码应使用 localPosition，不要把预览 Sphere 的 world position 当作真实相机坐标。
如需真实米制 XYZ，需要额外完成 RGB/Depth 配准与相机内外参标定。

`LatestResult` / `OnHandsUpdated` 提供完整识别结果；`world` 是模型的手部局部米制估计，
同样不是传感器坐标系中的绝对位置。

## 本地运行环境

使用 Tools/ASJHandTracking/.venv 中的 Python 3.12、MediaPipe 0.10.21。
依赖锁定在 requirements-lock.txt；模型和 worker 位于 Assets/StreamingAssets/ASJ/HandTracking。
Unity 自动启动隐藏的 Python 进程，通过标准输入输出传输 RGB 帧与 JSON，不打开第二个摄像头，
也不向网络发送图像。处理队列只保留最新帧，默认每秒最多 15 次推理。

当前虚拟环境基于本机 Codex Python 3.12。迁移电脑时用自己的 Python 3.12 重建：

```powershell
py -3.12 -m venv Tools/ASJHandTracking/.venv
Tools/ASJHandTracking/.venv/Scripts/python.exe -m pip install -r Tools/ASJHandTracking/requirements-lock.txt
```

Windows Player 发布时还需部署 Python 环境，并在组件 Python Executable 配置其绝对路径；
这不是完全打包在 Unity DLL 内的推理方案。本次仅验证 Unity Editor。

官方模型来源：https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task
文档：https://developers.google.com/edge/mediapipe/solutions/vision/hand_landmarker/python
