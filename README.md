# AV-ALOHA Unity

This repository contains the Unity app code for the AV-ALOHA project for VR passthrough + teleoperation. You can find more details about the project in the following resources:
- **AV-ALOHA Code**: [AV-ALOHA](https://github.com/Soltanilara/av-aloha)
- **Project Page**: [AV-ALOHA Project](https://soltanilara.github.io/av-aloha/)
- **Paper**: ["Active Vision Might Be All You Need: Exploring Active Vision in Bimanual Robotic Manipulation"](https://arxiv.org/abs/2409.17435)

This repository includes the full Unity project and the APK for Meta Quest 2 and Meta Quest 3. While the code may not be perfectly organized, the key scripts are limited and straightforward to understand. For WebRTC communication and video streaming, refer to the following script:

- **Main WebRTC Streaming Script**:  
  `Assets/Scripts/PassthroughScene/WebRTCStreamer.cs`

This script renders two separate video streams for each eye (left and right), which minimizes compression issues compared to combining the streams into one. The project also includes options to use a TURN server, although this feature has not been tested yet.

### APK Location
- **APK**: `/TwoStreamGuidedVision.apk`

## Modifying the Unity Project

If you wish to modify the Unity project, follow these steps:

1. Clone the repository and open the project located at `/Guided-Vision` in Unity.
2. Ensure that you are using **Unity Editor version 2022.3.20f1**.
3. All settings and dependencies should load automatically, but verify that the settings below are configured correctly.
4. Build the project for **Android**.

### Important Project Settings

These settings should already be configured, but it’s important to check them:

- **Graphics API**:  
  Go to `Player` > `Android` > `Graphics API`, and make sure that `OpenGLES3` is selected (not Vulkan). Using Vulkan may cause crashes in the WebRTC package.

- **Stereo Rendering Mode**:  
  Go to `Project Settings` > `XR Plug-in Management` > `Oculus` > `Android` and ensure that **Stereo Rendering Mode** is set to `Multi Pass`. This allows rendering two separate images to the left and right eye.