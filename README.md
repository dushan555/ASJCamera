# AstraTest
裸眼3d手势控制方案-深度相机astra&amp;asj

普通 RGB 摄像头输入：打开 ASJ 场景，使用 `ASJ > Hand Input > Use Ordinary Webcam` 切换，在 `WebcamRgbSource` 中指定设备名称或索引。

手背骨架视角：`ASJHandJointTracker.Back Of Hand View` 将整只手绕预览坐标原点的竖直轴旋转 180°，姿态和整体平移一起反转：X（左右）、Z（前后）取反，Y（上下）不变。当前 ASJ 场景已开启。`GetJoint()` 和抓取使用变换后的关节，`LatestResult` 保留原始模型数据。已有 `ASJPinchGrab.Mirror Horizontal` 是整幅显示的左右镜像，与此开关叠加会再次反转显示的左右方向。

`Estimate Forward Motion` 通过手掌大小相对首次识别时的变化，估计整只手的前后移动：手变大向观察者移动，变小远离。当前场景已开启，可用 `Forward Sensitivity / Forward Limit` 调整灵敏度和位移范围；关闭后恢复原有模型相对 Z。此功能不使用深度相机测量，侧转、遮挡会造成估计误差。跟踪丢失后重新建立参考位置，并释放旧的抓取。建议退出 Play 后修改视角和灵敏度。

前后估计先加到关节位置，再随手背视角整体反转；开启手背视角时，上述前后估计的显示方向也相反。

已通过编译和几何测试（整体 X/Z 反转、Y 不变、关节距离不变、前后估计一起反转），尚未实机验证显示效果。测试代码为 `Assets/ASJ/Editor/HandSkeletonViewTests.cs`，未创建 Tools 目录。
