namespace DualSenseTweaker.Core.Device;

/// <summary>
/// Report IDs, sizes, offsets and flags of the DualSense HID protocol.
/// Verified against the Linux driver (drivers/hid/hid-playstation.c) and nowrep/dualsensectl.
/// </summary>
internal static class DualSenseProtocol
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseProductId = 0x0CE6;
    public const ushort DualSenseEdgeProductId = 0x0DF2;

    public const byte InputReportUsb = 0x01;
    public const int InputReportUsbSize = 64;
    public const byte InputReportBt = 0x31;
    public const int InputReportBtSize = 78;

    public const byte OutputReportUsb = 0x02;
    // Linux sends 63 bytes, but the Windows HID descriptor declares 48 (report ID + 47-byte common block)
    // and a longer write would be split into several reports, so we send exactly 48.
    public const int OutputReportUsbSize = 48;
    public const byte OutputReportBt = 0x31;
    public const int OutputReportBtSize = 78;

    public const byte FeatureCalibration = 0x05;
    public const int FeatureCalibrationSize = 41;
    public const byte FeaturePairingInfo = 0x09;
    public const int FeaturePairingInfoSize = 20;
    public const byte FeatureFirmwareInfo = 0x20;
    public const int FeatureFirmwareInfoSize = 64;

    // Bluetooth CRC32 seeds (the HIDP transaction header byte that precedes each report on the wire).
    public const byte CrcSeedInput = 0xA1;
    public const byte CrcSeedOutput = 0xA2;
    public const byte CrcSeedFeature = 0xA3;

    /// <summary>Byte 2 of a Bluetooth output report; must be set, exact meaning unknown.</summary>
    public const byte OutputTag = 0x10;

    /// <summary>Offset of the input status bytes inside the common input report (after the BT/USB header).</summary>
    public const int InputStatusOffset = 52;

    /// <summary>x, y, rx, ry (sticks), z, rz (L2, R2) at the start of the common input report.</summary>
    public const int InputSticksOffset = 0;
    public const int InputTriggersOffset = 4;

    /// <summary>buttons[0]: hat switch (low nibble, 8 = released) and □ ✕ ○ △ (bits 4–7).</summary>
    public const int InputButtons0Offset = 7;
    public const byte HatReleased = 8;
    /// <summary>buttons[1]: L1 R1 L2 R2 Create Options L3 R3 (bits 0–7).</summary>
    public const int InputButtons1Offset = 8;

    /// <summary>buttons[2] of the common input report (after x, y, rx, ry, z, rz, seq, buttons[0..1]).</summary>
    public const int InputButtons2Offset = 9;
    public const byte Buttons2PsHome = 1 << 0;
    public const byte Buttons2TouchpadClick = 1 << 1;
    public const byte Buttons2Mute = 1 << 2;
    /// <summary>DualSense Edge: Fn left/right (bits 4–5) and the back paddles (bits 6–7).</summary>
    public const int Buttons2EdgeShift = 4;

    /// <summary>gyro[3] (pitch, yaw, roll), accel[3] (x, y, z) as int16 LE, then a uint32 sensor clock in 1/3 µs.</summary>
    public const int InputGyroOffset = 15;
    public const int InputAccelOffset = 21;
    public const int InputSensorTimestampOffset = 27;

    /// <summary>Two 4-byte touch points: contact (bit 7 = inactive, bits 0–6 = id), x_lo, x_hi:4|y_lo:4, y_hi.</summary>
    public const int InputTouchOffset = 32;
    public const byte TouchInactive = 1 << 7;

    /// <summary>Firmware "update version" 2.21 introduced the v2 compatible-vibration mode.</summary>
    public const ushort VibrationV2MinUpdateVersion = 0x0215;

    /// <summary>Offsets inside the 47-byte block shared by the USB (0x02) and Bluetooth (0x31) output reports.</summary>
    public static class Common
    {
        public const int Size = 47;
        public const int ValidFlag0 = 0;
        public const int ValidFlag1 = 1;
        public const int MotorRight = 2;
        public const int MotorLeft = 3;
        public const int HeadphoneVolume = 4;
        public const int SpeakerVolume = 5;
        public const int MicVolume = 6;
        public const int AudioControl = 7;
        public const int MuteButtonLed = 8;
        public const int PowerSaveControl = 9;
        /// <summary>Mode byte followed by 10 parameters (see TriggerEffectFactory).</summary>
        public const int RightTrigger = 10;
        public const int LeftTrigger = 21;
        /// <summary>Bits 0–2: rumble power reduction, bits 4–6: trigger power reduction (0–7 each).</summary>
        public const int MotorPower = 36;
        public const int AudioControl2 = 37;
        public const int ValidFlag2 = 38;
        public const int LightbarSetup = 41;
        public const int LedBrightness = 42;
        public const int PlayerLeds = 43;
        public const int LightbarRed = 44;
        public const int LightbarGreen = 45;
        public const int LightbarBlue = 46;
    }

    public static class Flag0
    {
        /// <summary>Rumble emulation (v1). Like <see cref="HapticsSelect"/>, it must be absent for audio haptics to play.</summary>
        public const byte CompatibleVibration = 1 << 0;
        /// <summary>"Use rumble, not audio haptics". SDL: leaving the emulated-rumble bits off restores audio haptics.</summary>
        public const byte HapticsSelect = 1 << 1;
        public const byte RightTrigger = 1 << 2;
        public const byte LeftTrigger = 1 << 3;
        public const byte HeadphoneVolume = 1 << 4;
        public const byte SpeakerVolume = 1 << 5;
        public const byte MicVolume = 1 << 6;
        public const byte AudioControl = 1 << 7;
    }

    public static class Flag1
    {
        public const byte MicMuteLed = 1 << 0;
        public const byte PowerSaveControl = 1 << 1;
        public const byte LightbarControl = 1 << 2;
        public const byte ReleaseLeds = 1 << 3;
        public const byte PlayerIndicator = 1 << 4;
        public const byte MotorPower = 1 << 6;
        public const byte AudioControl2 = 1 << 7;
    }

    public static class Flag2
    {
        public const byte LedBrightness = 1 << 0;
        public const byte LightbarSetup = 1 << 1;
        public const byte CompatibleVibration2 = 1 << 2;
    }

    public const byte LightbarSetupLightOut = 1 << 1;
    public const int AudioOutputPathShift = 4;
    /// <summary>Speaker pre-amp gain +6 dB (audio_control2 bits 0–2), as set by the Linux driver.</summary>
    public const byte SpeakerPreampGain6Db = 0x02;

    public const byte Status1HeadphonesDetect = 1 << 0;
    public const byte Status1MicrophoneDetect = 1 << 1;
}
