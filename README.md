<div>
  <img src="https://github.com/user-attachments/assets/bed8f2ba-35e6-44ed-abf2-658ed5ed1ee9" 
       alt="logo" 
       width="150">
</div
   
## UWP Scrcpy

---

**UWP Scrcpy** is a client for the popular Android screen-mirroring tool **Scrcpy**, built specifically for the **Universal Windows Platform (UWP)**. 

It allows you to mirror and control your Android device from a **Windows 10 PC** or **Windows 10 Mobile** device.

<div center>
      <img width="250" height="500" alt="image" src="https://github.com/user-attachments/assets/00829796-0c59-4de6-a65a-653203f9bc1d" />

</div>

---

## Features

- Android screen mirroring
- Mouse control
- Controls-only mode (no video stream)
- UHID mouse support for precise cursor control
- Works over **Wireless ADB**

---

## Requirements

### Host Device
- Windows 10 PC or Windows 10 Mobile  
- Creators Update (**Build 1703**) or newer

### Target Device
- Android device with **Wireless ADB debugging** enabled

---

## Setup Guide

Follow the steps below carefully to establish a connection.

### Step 1: Get the App

Download the **universal release bundle** from the project’s **Latest Releases** page.

> **Note:**  
> The release package is a **single universal bundle** that supports **x86, x64, and ARM.**

You may need to enable one of the following in Windows Settings to install the package:

- **Sideload apps**  
- **Developer Mode**

---

### Step 2: Enable Wireless ADB on Your Android Device

Wireless ADB must be enabled on your Android device.

#### Method A: Any Android Version (Using a PC)

1. Enable **Developer Options** and **USB debugging** on your Android device.
2. Connect your phone to your PC using a USB cable.
3. On your PC, run:
   ```
   adb tcpip 5555
   ```
4. Disconnect the USB cable.
5. On your phone, go to **Settings → Wi-Fi** and note your device’s local IP address (e.g., `192.168.1.10`).

#### Method B: Android 11 and Newer

1. Enable **Developer Options**.
2. Go to **Settings → System → Developer Options**.
3. Enable **Wireless debugging**.
4. Tap **Wireless debugging** (not the toggle) to open its menu.
5. Note the displayed **IP address and port** (e.g., `192.168.1.10:45889`).

---

### Step 3: Connect and Authorize

**This step requires two connection attempts.**

1. Open **UWP Scrcpy** on your Windows device.
2. Enter the **IP address** and **Port** obtained in Step 2.
3. Click **Start**.

#### Important: The First Connection Will Fail

On the first attempt, your Android device will show a security prompt:

> **“Allow wireless debugging?”**

1. Check **“Always allow from this computer”**  
2. Tap **Allow**  
3. In the UWP app, click **Stop**  
4. Click **Start** again  

The second connection attempt should succeed.

---

## How to Use

### Basic Controls

- **IP Address / Port** – Android connection details  
- **Start / Stop** – Connect or disconnect from the device  
- **Fullscreen** – Enter fullscreen mode (`Esc` to exit)
- **Max Height** – Set max resolution height (`0` for native)
- **Bitrate** – Set video bitrate in Mbps
- **Max FPS** – Set max FPS

---

### Input Modes

#### Standard Mode (Video Mirroring)

The default mode, showing the Android screen in the app.

- **Left-Click** – Tap  
- **Right-Click** – Android **Back** button  
- **Mouse Wheel** – Vertical scrolling

#### Controls-Only Mode

Control the Android device **without displaying the video stream**.

##### Enable Mouse (UHID)

A special sub-mode available within Controls-Only Mode.

**What it does:**  
- Displays a visible mouse cursor directly on the Android device

**How it works:**  
- The app transforms into a large touchpad area:  
  - Move your mouse or finger to move the cursor  
  - Use the bottom buttons for **Left** and **Right** clicks  
  - Use two fingers to scroll vertically

---

## Building from Source

**Requirements**

- Visual Studio 2017  
- Universal Windows Platform development workload installed

**Steps**

1. Clone this repository  
2. Open `uwpscrcpy.sln` in Visual Studio 2017  
3. Select the desired architecture:  
   - x86 for PC  
   - ARM for Mobile / ARM devices  
4. Build the solution: **Build → Build Solution**

---

## Credits and Acknowledgements

This project would not be possible without the original **Scrcpy** project.

It uses `scrcpy-server.jar`, developed by the **Genymobile** team, which handles screen capture and input control on the Android device.

Please support the official Scrcpy project: https://github.com/Genymobile/scrcpy


## License

This project is licensed under the [MIT License](LICENSE).
