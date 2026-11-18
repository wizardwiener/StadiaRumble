using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StadiaRumble
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private const string MutexName = "Local\\StadiaRumbleStudioSingleton";
        private const string WakeEventName = "Local\\StadiaRumbleStudioWake";

        [STAThread]
        private static void Main(string[] args)
        {
            // Clean up any legacy --tray startup entry now that macros run inside winstadia.dll
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (runKey != null && runKey.GetValue("StadiaRumble") != null)
                    {
                        runKey.DeleteValue("StadiaRumble", false);
                    }
                }
            }
            catch { }

            bool forceClassic = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = (args[i] ?? "").Trim().ToLowerInvariant();
                    if (a == "--classic" || a == "/classic" || a == "-classic" || a == "--winforms")
                    {
                        forceClassic = true;
                    }
                }
            }

            bool createdNew = false;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (EventWaitHandle wake = EventWaitHandle.OpenExisting(WakeEventName))
                        {
                            wake.Set();
                        }
                    }
                    catch { }
                    return;
                }

                using (EventWaitHandle wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName))
                {
                    try { SetProcessDPIAware(); } catch { }
                    I18n.LoadFromRegistry();

                    if (!forceClassic && LocalWebStudioHost.TryRun(wakeEvent))
                    {
                        return;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(wakeEvent));
                }
            }
        }
    }

    internal static class Native
    {
        public const uint ErrorSuccess = 0;
        public const ushort XboxVid = 0x045E;
        public const ushort XboxPid = 0x02FD;
        public const string StadiaSerial = "StadiaRumble";

        public const byte StatusReportId = 0xE1;
        public const int StatusReportLen = 64;
        public const byte SettingsReportId = 0xE2;
        public const int SettingsReportLen = 16;
        public const byte MacroSettingsReportId = 0xE3;
        public const int MacroSettingsReportLen = 16;

        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;

        public const ushort BtnDpadUp = 0x0001;
        public const ushort BtnDpadDown = 0x0002;
        public const ushort BtnDpadLeft = 0x0004;
        public const ushort BtnDpadRight = 0x0008;
        public const ushort BtnStart = 0x0010;
        public const ushort BtnBack = 0x0020;
        public const ushort BtnLeftThumb = 0x0040;
        public const ushort BtnRightThumb = 0x0080;
        public const ushort BtnLeftShoulder = 0x0100;
        public const ushort BtnRightShoulder = 0x0200;
        public const ushort BtnGuide = 0x0400;
        public const ushort BtnA = 0x1000;
        public const ushort BtnB = 0x2000;
        public const ushort BtnX = 0x4000;
        public const ushort BtnY = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short LeftThumbX;
            public short LeftThumbY;
            public short RightThumbX;
            public short RightThumbY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputVibration
        {
            public ushort LeftMotorSpeed;
            public ushort RightMotorSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HidAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort Version;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateExNative(uint userIndex, out XInputState state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetStateStdNative(uint userIndex, out XInputState state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        public static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetAttributes(IntPtr handle, ref HidAttributes attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetSerialNumberString(IntPtr handle, StringBuilder buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr handle, byte[] report, int length);

        [StructLayout(LayoutKind.Sequential)]
        public struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr handle, byte[] report, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string id, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string id, char[] buffer, uint length, uint flags);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(
            IntPtr hProcess, int processInformationClass,
            ref ProcessPowerThrottlingState processInformation, uint processInformationSize);

        public static void EnableHighResolutionTimer()
        {
            try
            {
                ProcessPowerThrottlingState state = new ProcessPowerThrottlingState();
                state.Version = 1;
                state.ControlMask = 0x4; // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
                state.StateMask = 0;     // Do NOT ignore timer resolution when minimized/background
                SetProcessInformation(GetCurrentProcess(), 4, ref state,
                    (uint)Marshal.SizeOf(typeof(ProcessPowerThrottlingState)));
            }
            catch { }
            try { timeBeginPeriod(1); } catch { }
        }

        private static bool _useStdGetState = false;

        public static uint GetState(uint userIndex, out XInputState state)
        {
            if (!_useStdGetState)
            {
                try
                {
                    return XInputGetStateExNative(userIndex, out state);
                }
                catch (EntryPointNotFoundException)
                {
                    _useStdGetState = true;
                }
            }
            return XInputGetStateStdNative(userIndex, out state);
        }

        public static uint SetVibration(int slot, int strongPct, int weakPct)
        {
            XInputVibration vib = new XInputVibration();
            vib.LeftMotorSpeed = (ushort)(ClampInt(strongPct, 0, 100) * 65535 / 100);
            vib.RightMotorSpeed = (ushort)(ClampInt(weakPct, 0, 100) * 65535 / 100);
            return XInputSetState((uint)slot, ref vib);
        }

        public static IntPtr OpenStadiaHidHandle(uint desiredAccess)
        {
            Guid guid = Guid.Empty;
            HidD_GetHidGuid(out guid);
            uint len = 0;
            CM_Get_Device_Interface_List_SizeW(out len, ref guid, null, 0);
            if (len == 0)
            {
                return IntPtr.Zero;
            }
            char[] buf = new char[len];
            CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0);
            string all = new string(buf);
            string[] paths = all.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            IntPtr invalid = new IntPtr(-1);

            for (int i = 0; i < paths.Length; i++)
            {
                IntPtr h = CreateFileW(paths[i], desiredAccess, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if ((h == invalid || h == IntPtr.Zero) && desiredAccess != 0)
                {
                    h = CreateFileW(paths[i], 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                }
                if (h == invalid || h == IntPtr.Zero)
                {
                    continue;
                }
                HidAttributes attr = new HidAttributes();
                attr.Size = Marshal.SizeOf(typeof(HidAttributes));
                StringBuilder serial = new StringBuilder(64);
                if (HidD_GetAttributes(h, ref attr) &&
                    attr.VendorId == XboxVid &&
                    attr.ProductId == XboxPid &&
                    HidD_GetSerialNumberString(h, serial, serial.Capacity * 2) &&
                    string.Equals(serial.ToString(), StadiaSerial, StringComparison.Ordinal))
                {
                    IntPtr ppd = IntPtr.Zero;
                    bool isGamepadCol = false;
                    try
                    {
                        if (HidD_GetPreparsedData(h, out ppd) && ppd != IntPtr.Zero)
                        {
                            HidpCaps caps;
                            if (HidP_GetCaps(ppd, out caps) >= 0 && caps.UsagePage == 0x01 && caps.Usage == 0x05)
                            {
                                isGamepadCol = true;
                            }
                        }
                    }
                    finally
                    {
                        if (ppd != IntPtr.Zero) HidD_FreePreparsedData(ppd);
                    }
                    if (isGamepadCol)
                    {
                        return h;
                    }
                }
                CloseHandle(h);
            }
            return IntPtr.Zero;
        }

        public static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public const ushort VkShift = 0x10;
        public const ushort VkControl = 0x11;
        public const ushort VkMenu = 0x12;
        public const ushort VkSnapshot = 0x2C;
        public const ushort VkLWin = 0x5B;
        public const ushort VkG = 0x47;
        public const ushort VkF11 = 0x7A;
        public const ushort VkF12 = 0x7B;
        public const ushort VkVolumeMute = 0xAD;
        public const ushort VkVolumeDown = 0xAE;
        public const ushort VkVolumeUp = 0xAF;
        public const ushort VkMediaNextTrack = 0xB0;
        public const ushort VkMediaPrevTrack = 0xB1;
        public const ushort VkMediaPlayPause = 0xB3;

        private const uint InputKeyboard = 1;
        private const uint KeyEventfExtendedKey = 0x0001;
        private const uint KeyEventfKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private static bool IsExtendedKey(ushort vk)
        {
            return vk == VkLWin || vk == VkSnapshot ||
                   (vk >= VkVolumeMute && vk <= VkMediaPlayPause) ||
                   (vk >= 0x21 && vk <= 0x2E);
        }

        public static void SendKeyCombo(params ushort[] vks)
        {
            if (vks == null || vks.Length == 0) return;
            INPUT[] inputs = new INPUT[vks.Length * 2];
            int idx = 0;
            for (int i = 0; i < vks.Length; i++)
            {
                inputs[idx].type = InputKeyboard;
                inputs[idx].u.ki.wVk = vks[i];
                inputs[idx].u.ki.dwFlags = IsExtendedKey(vks[i]) ? KeyEventfExtendedKey : 0u;
                idx++;
            }
            for (int i = vks.Length - 1; i >= 0; i--)
            {
                inputs[idx].type = InputKeyboard;
                inputs[idx].u.ki.wVk = vks[i];
                inputs[idx].u.ki.dwFlags = (IsExtendedKey(vks[i]) ? KeyEventfExtendedKey : 0u) | KeyEventfKeyUp;
                idx++;
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public static byte BuildHidModMask(bool ctrl, bool alt, bool shift)
        {
            byte m = 0;
            if (ctrl) m |= 0x01;
            if (shift) m |= 0x02;
            if (alt) m |= 0x04;
            return m;
        }

        public static byte VkToHidUsage(ushort vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return (byte)(0x04 + (vk - 0x41)); // A-Z
            if (vk >= 0x31 && vk <= 0x39) return (byte)(0x1E + (vk - 0x31)); // 1-9
            if (vk == 0x30) return 0x27; // 0
            if (vk >= 0x70 && vk <= 0x7B) return (byte)(0x3A + (vk - 0x70)); // F1-F12
            if (vk >= 0x7C && vk <= 0x87) return (byte)(0x68 + (vk - 0x7C)); // F13-F24
            if (vk >= 0x61 && vk <= 0x69) return (byte)(0x59 + (vk - 0x61)); // Num1-9
            switch (vk)
            {
                case 0x0D: return 0x28; // Enter
                case 0x1B: return 0x29; // Esc
                case 0x08: return 0x2A; // Backspace
                case 0x09: return 0x2B; // Tab
                case 0x20: return 0x2C; // Space
                case 0xBD: return 0x2D; // -
                case 0xBB: return 0x2E; // =
                case 0xDB: return 0x2F; // [
                case 0xDD: return 0x30; // ]
                case 0xDC: return 0x31; // backslash
                case 0xBA: return 0x33; // ;
                case 0xDE: return 0x34; // '
                case 0xC0: return 0x35; // `
                case 0xBC: return 0x36; // ,
                case 0xBE: return 0x37; // .
                case 0xBF: return 0x38; // /
                case 0x14: return 0x39; // CapsLock
                case 0x2C: return 0x46; // PrintScreen
                case 0x91: return 0x47; // ScrollLock
                case 0x13: return 0x48; // Pause
                case 0x2D: return 0x49; // Insert
                case 0x24: return 0x4A; // Home
                case 0x21: return 0x4B; // PageUp
                case 0x2E: return 0x4C; // Delete
                case 0x23: return 0x4D; // End
                case 0x22: return 0x4E; // PageDown
                case 0x27: return 0x4F; // Right
                case 0x25: return 0x50; // Left
                case 0x28: return 0x51; // Down
                case 0x26: return 0x52; // Up
                case 0x90: return 0x53; // NumLock
                case 0x60: return 0x62; // Num0
                case 0x6F: return 0x54; // Num/
                case 0x6A: return 0x55; // Num*
                case 0x6D: return 0x56; // Num-
                case 0x6B: return 0x57; // Num+
                case 0x6E: return 0x63; // Num.
                case 0x5D: return 0x65; // Apps / ContextMenu
                default: return 0x00;   // Unsupported special key
            }
        }

        public static ushort HidUsageToVk(byte usage)
        {
            if (usage >= 0x04 && usage <= 0x1D) return (ushort)(0x41 + (usage - 0x04)); // A-Z
            if (usage >= 0x1E && usage <= 0x26) return (ushort)(0x31 + (usage - 0x1E)); // 1-9
            if (usage == 0x27) return 0x30; // 0
            if (usage >= 0x3A && usage <= 0x45) return (ushort)(0x70 + (usage - 0x3A)); // F1-F12
            if (usage >= 0x68 && usage <= 0x73) return (ushort)(0x7C + (usage - 0x68)); // F13-F24
            if (usage >= 0x59 && usage <= 0x61) return (ushort)(0x61 + (usage - 0x59)); // Num1-9
            switch (usage)
            {
                case 0x28: return 0x0D; // Enter
                case 0x29: return 0x1B; // Esc
                case 0x2A: return 0x08; // Backspace
                case 0x2B: return 0x09; // Tab
                case 0x2C: return 0x20; // Space
                case 0x2D: return 0xBD; // -
                case 0x2E: return 0xBB; // =
                case 0x2F: return 0xDB; // [
                case 0x30: return 0xDD; // ]
                case 0x31: return 0xDC; // backslash
                case 0x33: return 0xBA; // ;
                case 0x34: return 0xDE; // '
                case 0x35: return 0xC0; // `
                case 0x36: return 0xBC; // ,
                case 0x37: return 0xBE; // .
                case 0x38: return 0xBF; // /
                case 0x39: return 0x14; // CapsLock
                case 0x46: return 0x2C; // PrintScreen
                case 0x47: return 0x91; // ScrollLock
                case 0x48: return 0x13; // Pause
                case 0x49: return 0x2D; // Insert
                case 0x4A: return 0x24; // Home
                case 0x4B: return 0x21; // PageUp
                case 0x4C: return 0x2E; // Delete
                case 0x4D: return 0x23; // End
                case 0x4E: return 0x22; // PageDown
                case 0x4F: return 0x27; // Right
                case 0x50: return 0x25; // Left
                case 0x51: return 0x28; // Down
                case 0x52: return 0x26; // Up
                case 0x53: return 0x90; // NumLock
                case 0x62: return 0x60; // Num0
                case 0x54: return 0x6F; // Num/
                case 0x55: return 0x6A; // Num*
                case 0x56: return 0x6D; // Num-
                case 0x57: return 0x6B; // Num+
                case 0x63: return 0x6E; // Num.
                case 0x65: return 0x5D; // Apps
                default: return VkF11;
            }
        }

        public static string FormatVkKeyName(ushort vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            switch (vk)
            {
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x20: return "Space";
                case 0xBD: return "-";
                case 0xBB: return "=";
                case 0xDB: return "[";
                case 0xDD: return "]";
                case 0xDC: return "\\";
                case 0xBA: return ";";
                case 0xDE: return "'";
                case 0xC0: return "~";
                case 0xBC: return ",";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0x14: return "CapsLock";
                case 0x2C: return "PrtSc";
                case 0x91: return "ScrollLock";
                case 0x13: return "Pause";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x21: return "PgUp";
                case 0x2E: return "Delete";
                case 0x23: return "End";
                case 0x22: return "PgDn";
                case 0x27: return "Right";
                case 0x25: return "Left";
                case 0x28: return "Down";
                case 0x26: return "Up";
                case 0x90: return "NumLock";
                case 0x6F: return "Num /";
                case 0x6A: return "Num *";
                case 0x6D: return "Num -";
                case 0x6B: return "Num +";
                case 0x6E: return "Num .";
                case 0x5D: return "Menu";
                default:
                    try
                    {
                        Keys k = (Keys)vk;
                        return k.ToString();
                    }
                    catch
                    {
                        return string.Format("0x{0:X2}", vk);
                    }
            }
        }

        public static string FormatHotkeyLabel(ushort vk, bool ctrl, bool alt, bool shift)
        {
            StringBuilder sb = new StringBuilder();
            if (ctrl) sb.Append("Ctrl+");
            if (alt) sb.Append("Alt+");
            if (shift) sb.Append("Shift+");
            sb.Append(FormatVkKeyName(vk));
            return sb.ToString();
        }
    }

    internal enum MacroAction
    {
        None = 0,
        PlayPause = 1,
        WinScreenshot = 2,
        SteamF12 = 3,
        GameBarOverlay = 4,
        GameBarClip = 5,
        NextTrack = 6,
        VolumeMute = 7,
        CustomHotkey = 8
    }

    internal sealed class AppMacroSettings
    {
        private const string RegSubKey = @"Software\StadiaRumble";

        public bool MacrosEnabled = true;
        public MacroAction AssistantAction = MacroAction.PlayPause;
        public MacroAction CaptureAction = MacroAction.WinScreenshot;
        public bool DpadMediaCombos = true;
        public ushort CustomVk = Native.VkF11;
        public bool CustomCtrl = false;
        public bool CustomAlt = false;
        public bool CustomShift = false;
        public byte CustomModMask = 0;
        public byte CustomHidUsage = 0x44; // F11

        public void SyncHidFieldsFromVk()
        {
            CustomModMask = Native.BuildHidModMask(CustomCtrl, CustomAlt, CustomShift);
            byte u = Native.VkToHidUsage(CustomVk);
            if (u == 0)
            {
                CustomVk = Native.VkF11;
                u = 0x44;
            }
            CustomHidUsage = u;
        }

        public byte[] ToBlob()
        {
            SyncHidFieldsFromVk();
            byte[] r = new byte[Native.MacroSettingsReportLen];
            r[0] = Native.MacroSettingsReportId;
            r[1] = 1;
            byte flags = 0;
            if (MacrosEnabled) flags |= 0x01;
            if (DpadMediaCombos) flags |= 0x02;
            r[2] = flags;
            r[3] = (byte)Native.ClampInt((int)AssistantAction, 0, 8);
            r[4] = (byte)Native.ClampInt((int)CaptureAction, 0, 8);
            r[5] = CustomModMask;
            r[6] = CustomHidUsage;
            return r;
        }

        public void ApplyFromBlob(byte[] r)
        {
            if (r == null || r.Length < 7) return;
            MacrosEnabled = (r[2] & 0x01) != 0;
            DpadMediaCombos = (r[2] & 0x02) != 0;
            AssistantAction = (MacroAction)Native.ClampInt(r[3], 0, 8);
            CaptureAction = (MacroAction)Native.ClampInt(r[4], 0, 8);
            CustomModMask = r[5];
            CustomHidUsage = r[6];
            CustomCtrl = (r[5] & 0x01) != 0;
            CustomShift = (r[5] & 0x02) != 0;
            CustomAlt = (r[5] & 0x04) != 0;
            CustomVk = Native.HidUsageToVk(CustomHidUsage);
        }

        public void LoadFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegSubKey))
                {
                    if (key != null)
                    {
                        MacrosEnabled = ReadInt(key, "MacrosEnabled", 1) != 0;
                        AssistantAction = (MacroAction)Native.ClampInt(ReadInt(key, "AssistantAction", (int)MacroAction.PlayPause), 0, 8);
                        CaptureAction = (MacroAction)Native.ClampInt(ReadInt(key, "CaptureAction", (int)MacroAction.WinScreenshot), 0, 8);
                        DpadMediaCombos = ReadInt(key, "DpadMediaCombos", 1) != 0;
                        CustomVk = (ushort)Native.ClampInt(ReadInt(key, "CustomVk", Native.VkF11), 1, 254);
                        CustomCtrl = ReadInt(key, "CustomCtrl", 0) != 0;
                        CustomAlt = ReadInt(key, "CustomAlt", 0) != 0;
                        CustomShift = ReadInt(key, "CustomShift", 0) != 0;
                    }
                }
            }
            catch { }
            SyncHidFieldsFromVk();
        }

        public void SaveToRegistry()
        {
            SyncHidFieldsFromVk();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSubKey))
                {
                    if (key != null)
                    {
                        key.SetValue("MacrosEnabled", MacrosEnabled ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("AssistantAction", (int)AssistantAction, RegistryValueKind.DWord);
                        key.SetValue("CaptureAction", (int)CaptureAction, RegistryValueKind.DWord);
                        key.SetValue("DpadMediaCombos", DpadMediaCombos ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("CustomVk", (int)CustomVk, RegistryValueKind.DWord);
                        key.SetValue("CustomCtrl", CustomCtrl ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("CustomAlt", CustomAlt ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("CustomShift", CustomShift ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            object v = key.GetValue(name);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }
    }

    internal enum AppLanguage
    {
        Uk = 0,
        En = 1,
        De = 2,
        Ru = 3
    }

    internal static class I18n
    {
        private const string RegSubKey = @"Software\StadiaRumble";
        public static AppLanguage Current = AppLanguage.Uk;

        public static string T(string uk, string en, string de, string ru)
        {
            switch (Current)
            {
                case AppLanguage.En: return en;
                case AppLanguage.De: return de;
                case AppLanguage.Ru: return ru;
                default: return uk;
            }
        }

        public static string GetCode(AppLanguage lang)
        {
            switch (lang)
            {
                case AppLanguage.En: return "en";
                case AppLanguage.De: return "de";
                case AppLanguage.Ru: return "ru";
                default: return "uk";
            }
        }

        public static void LoadFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegSubKey))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("Language");
                        string s = v != null ? v.ToString().Trim().ToLowerInvariant() : "";
                        if (s == "en") Current = AppLanguage.En;
                        else if (s == "de") Current = AppLanguage.De;
                        else if (s == "ru") Current = AppLanguage.Ru;
                        else if (s == "uk") Current = AppLanguage.Uk;
                    }
                }
            }
            catch { }
        }

        public static void SaveToRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSubKey))
                {
                    if (key != null)
                    {
                        key.SetValue("Language", GetCode(Current), RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }
    }

    internal sealed class DriverSettings
    {
        public int StrongGainPct = 100;
        public int WeakGainPct = 100;
        public int StickDeadzonePct = 0;
        public int TriggerDeadzonePct = 0;
        public int TriggerMaxPct = 100;
        public int BleIntervalMs = 40;
        public int BlePostAckGapMs = 12;
        public int BleMinDelta = 8;
        public bool SwapSticks = false;
        public bool InvertLy = false;
        public bool InvertRy = false;
        public bool TurboEnabled = true;
        public bool TurboHoldMode = false;
        public int TurboHz = 12;
        public int TurboMask = 0;

        public static readonly string[] TurboBtnNames = new string[]
        {
            "A", "B", "X", "Y", "L1", "R1", "L2", "R2", "LS", "RS"
        };

        public static string FormatTurboMask(int mask)
        {
            int clean = mask & 0x03FF;
            if (clean == 0) return I18n.T("немає", "none", "keine", "нет");
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < TurboBtnNames.Length; i++)
            {
                if ((clean & (1 << i)) != 0)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(TurboBtnNames[i]);
                }
            }
            return sb.ToString();
        }

        public byte FlagsByte()
        {
            byte f = 0;
            if (SwapSticks) f |= 0x01;
            if (InvertRy) f |= 0x02;
            if (InvertLy) f |= 0x04;
            return f;
        }

        public byte TurboFlagsByte()
        {
            byte f = 0;
            if (TurboEnabled) f |= 0x01;
            if (TurboHoldMode) f |= 0x02;
            return f;
        }

        public byte[] ToBlob()
        {
            byte[] r = new byte[Native.SettingsReportLen];
            r[0] = Native.SettingsReportId;
            r[1] = 1;
            r[2] = (byte)Native.ClampInt(StrongGainPct, 0, 100);
            r[3] = (byte)Native.ClampInt(WeakGainPct, 0, 100);
            r[4] = (byte)Native.ClampInt(StickDeadzonePct, 0, 25);
            r[5] = (byte)Native.ClampInt(TriggerDeadzonePct, 0, 20);
            r[6] = (byte)Native.ClampInt(TriggerMaxPct, 50, 100);
            r[7] = (byte)Native.ClampInt(BleIntervalMs, 15, 100);
            r[8] = (byte)Native.ClampInt(BlePostAckGapMs, 0, 30);
            r[9] = (byte)Native.ClampInt(BleMinDelta, 0, 20);
            r[10] = FlagsByte();
            r[11] = TurboFlagsByte();
            r[12] = (byte)Native.ClampInt(TurboHz, 5, 25);
            int mask = TurboMask & 0x03FF;
            r[13] = (byte)(mask & 0xFF);
            r[14] = (byte)((mask >> 8) & 0xFF);
            return r;
        }

        public static DriverSettings FromBlob(byte[] r)
        {
            DriverSettings s = new DriverSettings();
            if (r == null || r.Length < 11) return s;
            s.StrongGainPct = Native.ClampInt(r[2], 0, 100);
            s.WeakGainPct = Native.ClampInt(r[3], 0, 100);
            s.StickDeadzonePct = Native.ClampInt(r[4], 0, 25);
            s.TriggerDeadzonePct = Native.ClampInt(r[5], 0, 20);
            s.TriggerMaxPct = Native.ClampInt(r[6], 50, 100);
            s.BleIntervalMs = Native.ClampInt(r[7], 15, 100);
            s.BlePostAckGapMs = Native.ClampInt(r[8], 0, 30);
            s.BleMinDelta = Native.ClampInt(r[9], 0, 20);
            byte f = r[10];
            s.SwapSticks = (f & 0x01) != 0;
            s.InvertRy = (f & 0x02) != 0;
            s.InvertLy = (f & 0x04) != 0;
            if (r.Length >= 15)
            {
                byte tf = r[11];
                s.TurboEnabled = (tf & 0x01) != 0;
                s.TurboHoldMode = (tf & 0x02) != 0;
                s.TurboHz = (r[12] < 5) ? 12 : Native.ClampInt(r[12], 5, 25);
                s.TurboMask = (r[13] | (r[14] << 8)) & 0x03FF;
            }
            return s;
        }

        private const string RegSubKey = @"Software\StadiaRumble";

        public void LoadFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegSubKey))
                {
                    if (key != null)
                    {
                        StrongGainPct = Native.ClampInt(ReadRegInt(key, "StrongGainPct", StrongGainPct), 0, 100);
                        WeakGainPct = Native.ClampInt(ReadRegInt(key, "WeakGainPct", WeakGainPct), 0, 100);
                        StickDeadzonePct = Native.ClampInt(ReadRegInt(key, "StickDeadzonePct", StickDeadzonePct), 0, 25);
                        TriggerDeadzonePct = Native.ClampInt(ReadRegInt(key, "TriggerDeadzonePct", TriggerDeadzonePct), 0, 20);
                        TriggerMaxPct = Native.ClampInt(ReadRegInt(key, "TriggerMaxPct", TriggerMaxPct), 50, 100);
                        BleIntervalMs = Native.ClampInt(ReadRegInt(key, "BleIntervalMs", BleIntervalMs), 15, 100);
                        BlePostAckGapMs = Native.ClampInt(ReadRegInt(key, "BlePostAckGapMs", BlePostAckGapMs), 0, 30);
                        BleMinDelta = Native.ClampInt(ReadRegInt(key, "BleMinDelta", BleMinDelta), 0, 20);
                        SwapSticks = ReadRegInt(key, "SwapSticks", SwapSticks ? 1 : 0) != 0;
                        InvertLy = ReadRegInt(key, "InvertLy", InvertLy ? 1 : 0) != 0;
                        InvertRy = ReadRegInt(key, "InvertRy", InvertRy ? 1 : 0) != 0;
                        TurboEnabled = ReadRegInt(key, "TurboEnabled", TurboEnabled ? 1 : 0) != 0;
                        TurboHoldMode = ReadRegInt(key, "TurboHoldMode", TurboHoldMode ? 1 : 0) != 0;
                        TurboHz = Native.ClampInt(ReadRegInt(key, "TurboHz", TurboHz), 5, 25);
                        TurboMask = ReadRegInt(key, "TurboMask", TurboMask) & 0x03FF;
                    }
                }
            }
            catch { }
        }

        public void SaveToRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSubKey))
                {
                    if (key != null)
                    {
                        key.SetValue("StrongGainPct", StrongGainPct, RegistryValueKind.DWord);
                        key.SetValue("WeakGainPct", WeakGainPct, RegistryValueKind.DWord);
                        key.SetValue("StickDeadzonePct", StickDeadzonePct, RegistryValueKind.DWord);
                        key.SetValue("TriggerDeadzonePct", TriggerDeadzonePct, RegistryValueKind.DWord);
                        key.SetValue("TriggerMaxPct", TriggerMaxPct, RegistryValueKind.DWord);
                        key.SetValue("BleIntervalMs", BleIntervalMs, RegistryValueKind.DWord);
                        key.SetValue("BlePostAckGapMs", BlePostAckGapMs, RegistryValueKind.DWord);
                        key.SetValue("BleMinDelta", BleMinDelta, RegistryValueKind.DWord);
                        key.SetValue("SwapSticks", SwapSticks ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("InvertLy", InvertLy ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("InvertRy", InvertRy ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("TurboEnabled", TurboEnabled ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("TurboHoldMode", TurboHoldMode ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("TurboHz", TurboHz, RegistryValueKind.DWord);
                        key.SetValue("TurboMask", TurboMask & 0x03FF, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private static int ReadRegInt(RegistryKey key, string name, int fallback)
        {
            object v = key.GetValue(name);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }
    }

    internal sealed class DriverTelemetry
    {
        public bool Connected;
        public bool RumbleFailed;
        public uint DriverError;
        public uint RumbleError;
        public uint RumbleWrites;
        public uint RumbleFailures;
        public byte StrongByte;
        public byte WeakByte;
        public bool IsBluetooth;
        public byte RawB1 = 8;
        public byte RawB2;
        public int RawLen;
        public ushort LastLatencyMs;
        public byte BatteryPct = 255;
        public int TurboMask;
        public byte TurboToggleSeq;
        public byte MacroTriggerSeq;
        public byte LastMacroId;
        public string RawHex = "";
        public int ReadErrorCode;
    }

    internal sealed class BleSamplePoint
    {
        public int RssiDbm;
        public int LatencyMs;
        public int WritesDelta;
        public int FailuresDelta;
        public string TimeLabel;
    }

    internal sealed class BatteryPoint
    {
        public int Percent;
        public double VoltageV;
        public string TimeLabel;
    }

    internal sealed class DarkSlider : Control
    {
        private int _min = 0;
        private int _max = 100;
        private int _value = 50;
        private bool _dragging = false;
        private Color _fillColor = Color.FromArgb(255, 82, 56);

        public event EventHandler ValueChanged;

        public DarkSlider()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(20, 24, 33);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum
        {
            get { return _min; }
            set { _min = value; if (_value < _min) Value = _min; Invalidate(); }
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = value; if (_value > _max) Value = _max; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Native.ClampInt(value, _min, _max);
                if (_value != clamped)
                {
                    _value = clamped;
                    Invalidate();
                    if (ValueChanged != null)
                    {
                        ValueChanged(this, EventArgs.Empty);
                    }
                }
            }
        }

        public Color FillColor
        {
            get { return _fillColor; }
            set { _fillColor = value; Invalidate(); }
        }

        private void UpdateFromMouse(int x)
        {
            int pad = Math.Max(8, Height / 3);
            int trackW = Math.Max(1, Width - pad * 2);
            double ratio = (double)(x - pad) / trackW;
            if (ratio < 0.0) ratio = 0.0;
            if (ratio > 1.0) ratio = 1.0;
            int next = _min + (int)Math.Round(ratio * (_max - _min));
            Value = next;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                _dragging = true;
                UpdateFromMouse(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) UpdateFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = _value + (e.Delta > 0 ? 1 : -1);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value = _value - 1;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value = _value + 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int pad = Math.Max(9, Height / 3);
            int trackW = Math.Max(10, Width - pad * 2);
            int trackH = Math.Max(5, Height / 5);
            int trackY = (Height - trackH) / 2;
            int thumbR = Math.Max(7, (int)(Height * 0.31f));

            double span = Math.Max(1, _max - _min);
            double ratio = (_value - _min) / span;
            int thumbX = pad + (int)Math.Round(ratio * trackW);
            int cy = Height / 2;

            using (GraphicsPath bg = RoundedPath(pad, trackY, trackW, trackH, trackH / 2.0f))
            using (SolidBrush bBg = new SolidBrush(Color.FromArgb(40, 47, 62)))
            {
                g.FillPath(bBg, bg);
            }

            int fillW = thumbX - pad;
            if (fillW > 2)
            {
                using (GraphicsPath fg = RoundedPath(pad, trackY, fillW, trackH, trackH / 2.0f))
                using (SolidBrush bFg = new SolidBrush(_fillColor))
                {
                    g.FillPath(bFg, fg);
                }
            }

            Rectangle thumbRect = new Rectangle(thumbX - thumbR, cy - thumbR, thumbR * 2, thumbR * 2);
            using (SolidBrush bThumb = new SolidBrush(Color.FromArgb(241, 245, 249)))
            using (Pen pThumb = new Pen(_fillColor, Math.Max(1.8f, Height * 0.08f)))
            {
                g.FillEllipse(bThumb, thumbRect);
                g.DrawEllipse(pThumb, thumbRect);
            }
        }

        private static GraphicsPath RoundedPath(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(r * 2.0f, Math.Min(w, h));
            if (d < 1.0f)
            {
                p.AddRectangle(new RectangleF(x, y, w, h));
                return p;
            }
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class MotorTestRequestEventArgs : EventArgs
    {
        public bool IsLeft;
        public MotorTestRequestEventArgs(bool isLeft) { IsLeft = isLeft; }
    }

    internal sealed class StadiaControllerCanvas : Panel
    {
        private Native.XInputState _state;
        private bool _connected;
        private DriverTelemetry _telemetry;
        private int _stickDeadzonePct = 0;

        // Interactive virtual press/drag state for clicking directly on controller parts
        private ushort _virtualButtons = 0;
        private bool _virtualAssist = false;
        private bool _virtualCapture = false;
        private byte _virtualL2 = 0;
        private byte _virtualR2 = 0;
        private short _virtualLx = 0;
        private short _virtualLy = 0;
        private short _virtualRx = 0;
        private short _virtualRy = 0;
        private int _dragStick = 0; // 0=none, 1=LS, 2=RS
        private int _virtualStrongPct = 0;
        private int _virtualWeakPct = 0;
        private long _virtualMotorUntilTicks = 0;
        private string _lastVirtualHint = "";

        public event EventHandler<MotorTestRequestEventArgs> MotorZoneClicked;

        public StadiaControllerCanvas()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 24, 33);
            Cursor = Cursors.Hand;
        }

        public void UpdateData(bool connected, Native.XInputState state, DriverTelemetry telemetry, int stickDeadzonePct)
        {
            _connected = connected;
            _state = state;
            _telemetry = telemetry;
            _stickDeadzonePct = stickDeadzonePct;
            if (_virtualMotorUntilTicks != 0 && Stopwatch.GetTimestamp() > _virtualMotorUntilTicks)
            {
                _virtualStrongPct = 0;
                _virtualWeakPct = 0;
                _virtualMotorUntilTicks = 0;
            }
            Invalidate();
        }

        private void GetGeometry(out float cx, out float cy, out float s)
        {
            float w = Width;
            float h = Height;
            cx = w * 0.5f;
            cy = h * 0.5f + 4.0f;
            s = Math.Min((w - 36.0f) / 500.0f, (h - 72.0f) / 345.0f);
            if (s < 0.5f) s = 0.5f;
        }

        private static bool HitCircle(float px, float py, float cx, float cy, float r)
        {
            float dx = px - cx;
            float dy = py - cy;
            return (dx * dx + dy * dy) <= (r * r);
        }

        private static bool HitRect(float px, float py, float x, float y, float w, float h)
        {
            return px >= x && px <= (x + w) && py >= y && py <= (y + h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            float cx, cy, s;
            GetGeometry(out cx, out cy, out s);
            float px = e.X;
            float py = e.Y;

            // Stick wells (drag test)
            float lsX = cx - 64 * s;
            float rsX = cx + 64 * s;
            float stickY = cy + 46 * s;
            float stickR = 38 * s;

            if (HitCircle(px, py, lsX, stickY, stickR))
            {
                _dragStick = 1;
                UpdateStickFromMouse(1, px, py, lsX, stickY, stickR);
                _lastVirtualHint = I18n.T("Віртуальний рух LS", "Virtual LS movement", "Virtuelle LS-Bewegung", "Виртуальное движение LS");
                Invalidate();
                return;
            }
            if (HitCircle(px, py, rsX, stickY, stickR))
            {
                _dragStick = 2;
                UpdateStickFromMouse(2, px, py, rsX, stickY, stickR);
                _lastVirtualHint = I18n.T("Віртуальний рух RS", "Virtual RS movement", "Virtuelle RS-Bewegung", "Виртуальное движение RS");
                Invalidate();
                return;
            }

            // Grip motor zones -> trigger real motor test + visual glow!
            if (HitCircle(px, py, cx - 160 * s, cy + 66 * s, 52 * s))
            {
                _virtualStrongPct = 85;
                _virtualMotorUntilTicks = Stopwatch.GetTimestamp() + (long)(0.7 * Stopwatch.Frequency);
                _lastVirtualHint = I18n.T("Тест лівого мотора (Strong)", "Left motor test (Strong)", "Linker Motortest (Strong)", "Тест левого мотора (Strong)");
                if (MotorZoneClicked != null) MotorZoneClicked(this, new MotorTestRequestEventArgs(true));
                Invalidate();
                return;
            }
            if (HitCircle(px, py, cx + 160 * s, cy + 66 * s, 52 * s))
            {
                _virtualWeakPct = 85;
                _virtualMotorUntilTicks = Stopwatch.GetTimestamp() + (long)(0.7 * Stopwatch.Frequency);
                _lastVirtualHint = I18n.T("Тест правого мотора (Weak)", "Right motor test (Weak)", "Rechter Motortest (Weak)", "Тест правого мотора (Weak)");
                if (MotorZoneClicked != null) MotorZoneClicked(this, new MotorTestRequestEventArgs(false));
                Invalidate();
                return;
            }

            // Triggers & Bumpers
            if (HitRect(px, py, cx - 182 * s, cy - 156 * s, 98 * s, 56 * s)) { _virtualL2 = 255; _lastVirtualHint = I18n.T("Натиснуто L2", "L2 pressed", "L2 gedrückt", "Нажата L2"); Invalidate(); return; }
            if (HitRect(px, py, cx + 84 * s, cy - 156 * s, 98 * s, 56 * s)) { _virtualR2 = 255; _lastVirtualHint = I18n.T("Натиснуто R2", "R2 pressed", "R2 gedrückt", "Нажата R2"); Invalidate(); return; }
            if (HitRect(px, py, cx - 178 * s, cy - 104 * s, 94 * s, 24 * s)) { _virtualButtons |= Native.BtnLeftShoulder; _lastVirtualHint = I18n.T("Натиснуто L1", "L1 pressed", "L1 gedrückt", "Нажата L1"); Invalidate(); return; }
            if (HitRect(px, py, cx + 84 * s, cy - 104 * s, 94 * s, 24 * s)) { _virtualButtons |= Native.BtnRightShoulder; _lastVirtualHint = I18n.T("Натиснуто R1", "R1 pressed", "R1 gedrückt", "Нажата R1"); Invalidate(); return; }

            // Center 5 buttons
            if (HitCircle(px, py, cx - 44 * s, cy - 42 * s, 14 * s)) { _virtualButtons |= Native.BtnBack; _lastVirtualHint = I18n.T("Натиснуто Options", "Options pressed", "Options gedrückt", "Нажата Options"); Invalidate(); return; }
            if (HitCircle(px, py, cx + 44 * s, cy - 42 * s, 14 * s)) { _virtualButtons |= Native.BtnStart; _lastVirtualHint = I18n.T("Натиснуто Menu", "Menu pressed", "Menü gedrückt", "Нажата Menu"); Invalidate(); return; }
            if (HitCircle(px, py, cx, cy - 34 * s, 21 * s)) { _virtualButtons |= Native.BtnGuide; _lastVirtualHint = I18n.T("Натиснуто Stadia", "Stadia pressed", "Stadia gedrückt", "Нажата Stadia"); Invalidate(); return; }
            if (HitCircle(px, py, cx - 24 * s, cy + 4 * s, 13 * s)) { _virtualAssist = true; _lastVirtualHint = I18n.T("Натиснуто Assistant", "Assistant pressed", "Assistant gedrückt", "Нажата Assistant"); Invalidate(); return; }
            if (HitCircle(px, py, cx + 24 * s, cy + 4 * s, 13 * s)) { _virtualCapture = true; _lastVirtualHint = I18n.T("Натиснуто Capture", "Capture pressed", "Capture gedrückt", "Нажата Capture"); Invalidate(); return; }

            // ABXY
            float abxyX = cx + 124 * s;
            float abxyY = cy - 28 * s;
            float bOff = 24 * s;
            float bRad = 16 * s;
            if (HitCircle(px, py, abxyX, abxyY - bOff, bRad)) { _virtualButtons |= Native.BtnY; _lastVirtualHint = I18n.T("Натиснуто Y", "Y pressed", "Y gedrückt", "Нажата Y"); Invalidate(); return; }
            if (HitCircle(px, py, abxyX - bOff, abxyY, bRad)) { _virtualButtons |= Native.BtnX; _lastVirtualHint = I18n.T("Натиснуто X", "X pressed", "X gedrückt", "Нажата X"); Invalidate(); return; }
            if (HitCircle(px, py, abxyX + bOff, abxyY, bRad)) { _virtualButtons |= Native.BtnB; _lastVirtualHint = I18n.T("Натиснуто B", "B pressed", "B gedrückt", "Нажата B"); Invalidate(); return; }
            if (HitCircle(px, py, abxyX, abxyY + bOff, bRad)) { _virtualButtons |= Native.BtnA; _lastVirtualHint = I18n.T("Натиснуто A", "A pressed", "A gedrückt", "Нажата A"); Invalidate(); return; }

            // D-Pad
            float dpadX = cx - 124 * s;
            float dpadY = cy - 28 * s;
            if (HitCircle(px, py, dpadX, dpadY, 38 * s))
            {
                float dx = px - dpadX;
                float dy = py - dpadY;
                if (Math.Abs(dx) > Math.Abs(dy))
                {
                    _virtualButtons |= (dx < 0 ? Native.BtnDpadLeft : Native.BtnDpadRight);
                    _lastVirtualHint = dx < 0
                        ? I18n.T("Хрестовина Вліво", "D-Pad Left", "Steuerkreuz Links", "Крестовина Влево")
                        : I18n.T("Хрестовина Вправо", "D-Pad Right", "Steuerkreuz Rechts", "Крестовина Вправо");
                }
                else
                {
                    _virtualButtons |= (dy < 0 ? Native.BtnDpadUp : Native.BtnDpadDown);
                    _lastVirtualHint = dy < 0
                        ? I18n.T("Хрестовина Вгору", "D-Pad Up", "Steuerkreuz Oben", "Крестовина Вверх")
                        : I18n.T("Хрестовина Вниз", "D-Pad Down", "Steuerkreuz Unten", "Крестовина Вниз");
                }
                Invalidate();
            }
        }

        private void UpdateStickFromMouse(int which, float px, float py, float cx, float cy, float r)
        {
            float nx = (px - cx) / (r * 0.72f);
            float ny = -(py - cy) / (r * 0.72f);
            double mag = Math.Sqrt(nx * nx + ny * ny);
            if (mag > 1.0)
            {
                nx = (float)(nx / mag);
                ny = (float)(ny / mag);
            }
            short sx = (short)Native.ClampInt((int)Math.Round(nx * 32767), -32767, 32767);
            short sy = (short)Native.ClampInt((int)Math.Round(ny * 32767), -32767, 32767);
            if (which == 1) { _virtualLx = sx; _virtualLy = sy; }
            else { _virtualRx = sx; _virtualRy = sy; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragStick == 0) return;
            float cx, cy, s;
            GetGeometry(out cx, out cy, out s);
            float stickY = cy + 46 * s;
            float stickR = 38 * s;
            if (_dragStick == 1) UpdateStickFromMouse(1, e.X, e.Y, cx - 64 * s, stickY, stickR);
            else UpdateStickFromMouse(2, e.X, e.Y, cx + 64 * s, stickY, stickR);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragStick = 0;
            _virtualButtons = 0;
            _virtualAssist = false;
            _virtualCapture = false;
            _virtualL2 = 0;
            _virtualR2 = 0;
            _virtualLx = 0;
            _virtualLy = 0;
            _virtualRx = 0;
            _virtualRy = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int w = Width;
            int h = Height;
            if (w < 220 || h < 200) return;

            Color bgCard = Color.FromArgb(20, 24, 33);
            Color stadiaCoral = Color.FromArgb(255, 82, 56);
            Color activeGreen = Color.FromArgb(16, 185, 129);
            Color activeBlue = Color.FromArgb(59, 130, 246);
            Color mutedText = Color.FromArgb(148, 163, 184);
            Color btnFill = Color.FromArgb(42, 48, 62);
            Color btnStroke = Color.FromArgb(74, 84, 106);

            g.Clear(bgCard);

            float cx, cy, s;
            GetGeometry(out cx, out cy, out s);

            ushort btns = (ushort)((_connected ? _state.Gamepad.Buttons : (ushort)0) | _virtualButtons);
            byte lt = Math.Max(_connected ? _state.Gamepad.LeftTrigger : (byte)0, _virtualL2);
            byte rt = Math.Max(_connected ? _state.Gamepad.RightTrigger : (byte)0, _virtualR2);
            bool lb = (btns & Native.BtnLeftShoulder) != 0;
            bool rb = (btns & Native.BtnRightShoulder) != 0;
            bool assistPressed = _virtualAssist || (_telemetry != null && _telemetry.Connected && _telemetry.RawLen >= 3 && (_telemetry.RawB2 & 0x02) != 0);
            bool capturePressed = _virtualCapture || (_telemetry != null && _telemetry.Connected && _telemetry.RawLen >= 3 && (_telemetry.RawB2 & 0x01) != 0);

            float strongFactor = Math.Max(
                (_telemetry != null && _telemetry.Connected) ? _telemetry.StrongByte / 255.0f : 0.0f,
                _virtualStrongPct / 100.0f);
            float weakFactor = Math.Max(
                (_telemetry != null && _telemetry.Connected) ? _telemetry.WeakByte / 255.0f : 0.0f,
                _virtualWeakPct / 100.0f);

            // Top micro-title hint for interactivity
            using (Font fTop = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            using (Brush bTop = new SolidBrush(Color.FromArgb(120, 136, 162)))
            {
                string topHint = string.IsNullOrEmpty(_lastVirtualHint)
                    ? I18n.T(
                        "Інтерактивний геймпад: натисніть кнопки, потягніть стік або клікніть по рукоятці для вібро",
                        "Interactive gamepad: click buttons, drag sticks, or click a grip for rumble",
                        "Interaktives Gamepad: Tasten klicken, Sticks ziehen oder Griff für Vibration anklicken",
                        "Интерактивный геймпад: нажмите кнопки, потяните стик или кликните по рукоятке для вибро")
                    : _lastVirtualHint;
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                g.DrawString(topHint, fTop, bTop, cx, 8, sf);
                sf.Dispose();
            }

            // Analog Trigger L2 / R2 bars above shoulders
            DrawTriggerMeter(g, cx - 182 * s, cy - 156 * s, 98 * s, 12 * s, lt, "L2", stadiaCoral, s);
            DrawTriggerMeter(g, cx + 84 * s, cy - 156 * s, 98 * s, 12 * s, rt, "R2", stadiaCoral, s);

            DrawCurvedShoulder(g, cx - 172 * s, cy - 122 * s, 80 * s, 26 * s, lt > 8 ? BlendColor(Color.FromArgb(34, 40, 52), stadiaCoral, lt / 255.0f) : Color.FromArgb(28, 33, 44));
            DrawCurvedShoulder(g, cx + 92 * s, cy - 122 * s, 80 * s, 26 * s, rt > 8 ? BlendColor(Color.FromArgb(34, 40, 52), stadiaCoral, rt / 255.0f) : Color.FromArgb(28, 33, 44));

            DrawBumperCap(g, cx - 178 * s, cy - 104 * s, 94 * s, 20 * s, lb, stadiaCoral, "L1", s);
            DrawBumperCap(g, cx + 84 * s, cy - 104 * s, 94 * s, 20 * s, rb, stadiaCoral, "R1", s);

            using (GraphicsPath shell = BuildStadiaShellPath(cx, cy, s))
            {
                using (LinearGradientBrush bodyGrad = new LinearGradientBrush(
                    new PointF(cx, cy - 110 * s),
                    new PointF(cx, cy + 145 * s),
                    Color.FromArgb(46, 52, 68),
                    Color.FromArgb(26, 30, 40)))
                {
                    g.FillPath(bodyGrad, shell);
                }

                Color shellOutline = _connected ? Color.FromArgb(88, 100, 126) : Color.FromArgb(52, 59, 76);
                using (Pen pShell = new Pen(shellOutline, Math.Max(1.6f, 2.0f * s)))
                {
                    g.DrawPath(pShell, shell);
                }

                GraphicsState gs = g.Save();
                g.SetClip(shell);

                using (Pen seam = new Pen(Color.FromArgb(36, 15, 18, 26), Math.Max(1.2f, 1.4f * s)))
                {
                    g.DrawBezier(seam,
                        new PointF(cx - 196 * s, cy - 22 * s),
                        new PointF(cx - 175 * s, cy + 12 * s),
                        new PointF(cx - 172 * s, cy + 68 * s),
                        new PointF(cx - 192 * s, cy + 132 * s));
                    g.DrawBezier(seam,
                        new PointF(cx + 196 * s, cy - 22 * s),
                        new PointF(cx + 175 * s, cy + 12 * s),
                        new PointF(cx + 172 * s, cy + 68 * s),
                        new PointF(cx + 192 * s, cy + 132 * s));
                }

                if (strongFactor > 0.01f)
                {
                    DrawGripMotorGlow(g, cx - 156 * s, cy + 62 * s, 54 * s, strongFactor, stadiaCoral);
                }
                if (weakFactor > 0.01f)
                {
                    DrawGripMotorGlow(g, cx + 156 * s, cy + 62 * s, 54 * s, weakFactor, activeGreen);
                }

                g.Restore(gs);
            }

            using (SolidBrush usbB = new SolidBrush(Color.FromArgb(22, 26, 34)))
            {
                g.FillRectangle(usbB, cx - 11 * s, cy - 94 * s, 22 * s, 4 * s);
            }
            Color ledCol = _connected || _virtualButtons != 0
                ? ((strongFactor > 0 || weakFactor > 0) ? stadiaCoral : activeGreen)
                : Color.FromArgb(90, 96, 112);
            using (SolidBrush ledB = new SolidBrush(ledCol))
            {
                g.FillEllipse(ledB, cx - 2.6f * s, cy - 84 * s, 5.2f * s, 5.2f * s);
            }

            bool optPressed = (btns & Native.BtnBack) != 0;
            bool menuPressed = (btns & Native.BtnStart) != 0;
            bool guidePressed = (btns & Native.BtnGuide) != 0;

            DrawOptionsDotsButton(g, cx - 44 * s, cy - 42 * s, 11 * s, optPressed, activeGreen, btnFill, btnStroke);
            DrawMenuHamburgerButton(g, cx + 44 * s, cy - 42 * s, 11 * s, menuPressed, activeGreen, btnFill, btnStroke);
            DrawStadiaMainButton(g, cx, cy - 34 * s, 19 * s, guidePressed, _connected, stadiaCoral, s);
            DrawAssistantButton(g, cx - 24 * s, cy + 4 * s, 10.5f * s, assistPressed, btnFill, btnStroke, s);
            DrawCaptureButton(g, cx + 24 * s, cy + 4 * s, 10.5f * s, capturePressed, activeBlue, btnFill, btnStroke, s);

            DrawStadiaDpad(g, cx - 124 * s, cy - 28 * s, 34 * s, btns, activeGreen, btnFill, btnStroke);

            float abxyX = cx + 124 * s;
            float abxyY = cy - 28 * s;
            float bOff = 24 * s;
            float bRad = 13.5f * s;
            DrawFaceButton(g, abxyX, abxyY - bOff, bRad, "Y", Color.FromArgb(245, 158, 11), (btns & Native.BtnY) != 0, btnFill, btnStroke, s);
            DrawFaceButton(g, abxyX - bOff, abxyY, bRad, "X", Color.FromArgb(59, 130, 246), (btns & Native.BtnX) != 0, btnFill, btnStroke, s);
            DrawFaceButton(g, abxyX + bOff, abxyY, bRad, "B", Color.FromArgb(239, 68, 68), (btns & Native.BtnB) != 0, btnFill, btnStroke, s);
            DrawFaceButton(g, abxyX, abxyY + bOff, bRad, "A", Color.FromArgb(34, 197, 94), (btns & Native.BtnA) != 0, btnFill, btnStroke, s);

            float lsX = cx - 64 * s;
            float rsX = cx + 64 * s;
            float stickY = cy + 46 * s;
            float stickR = 36 * s;

            short lx = Math.Abs(_virtualLx) > 400 ? _virtualLx : (_connected ? _state.Gamepad.LeftThumbX : (short)0);
            short ly = Math.Abs(_virtualLy) > 400 ? _virtualLy : (_connected ? _state.Gamepad.LeftThumbY : (short)0);
            short rx = Math.Abs(_virtualRx) > 400 ? _virtualRx : (_connected ? _state.Gamepad.RightThumbX : (short)0);
            short ry = Math.Abs(_virtualRy) > 400 ? _virtualRy : (_connected ? _state.Gamepad.RightThumbY : (short)0);

            DrawStadiaStick(g, lsX, stickY, stickR, lx, ly, (btns & Native.BtnLeftThumb) != 0, "LS", stadiaCoral, _stickDeadzonePct, s);
            DrawStadiaStick(g, rsX, stickY, stickR, rx, ry, (btns & Native.BtnRightThumb) != 0, "RS", activeGreen, _stickDeadzonePct, s);

            using (Font fCoord = new Font("Consolas", Math.Max(7.5f, 8.2f * s)))
            using (Brush bMuted = new SolidBrush(mutedText))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                g.DrawString(string.Format("LS: {0,6}, {1,6}", lx, ly), fCoord, bMuted, cx - 105 * s, h - 42, sf);
                g.DrawString(string.Format("RS: {0,6}, {1,6}", rx, ry), fCoord, bMuted, cx + 105 * s, h - 42, sf);
                sf.Dispose();
            }

            using (Font fInfo = new Font("Segoe UI", 8.6f, _connected ? FontStyle.Regular : FontStyle.Bold))
            using (Brush bInfo = new SolidBrush(_connected ? Color.FromArgb(148, 163, 184) : Color.FromArgb(248, 113, 113)))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                if (!_connected)
                {
                    g.DrawString(I18n.T(
                        "Геймпад Stadia не знайдено — працюють віртуальні кліки та підсвітка",
                        "Stadia gamepad not found — virtual clicks & preview active",
                        "Stadia-Gamepad nicht gefunden — virtuelle Klicks & Vorschau aktiv",
                        "Геймпад Stadia не найден — работают виртуальные клики и подсветка"), fInfo, bInfo, cx, h - 22, sf);
                }
                else if (_telemetry != null && _telemetry.Connected)
                {
                    string tr = _telemetry.IsBluetooth ? "Bluetooth LE" : "USB";
                    g.DrawString(string.Format(I18n.T(
                        "0xE1: {0} · Мотори L={1} R={2} · BLE RTT={3} мс · Мертва зона: {4}%",
                        "0xE1: {0} · Motors L={1} R={2} · BLE RTT={3} ms · Deadzone: {4}%",
                        "0xE1: {0} · Motoren L={1} R={2} · BLE RTT={3} ms · Totzone: {4}%",
                        "0xE1: {0} · Моторы L={1} R={2} · BLE RTT={3} мс · Мёртвая зона: {4}%"),
                        tr, _telemetry.StrongByte, _telemetry.WeakByte, _telemetry.LastLatencyMs, _stickDeadzonePct),
                        fInfo, bInfo, cx, h - 22, sf);
                }
                else
                {
                    g.DrawString(I18n.T(
                        "Підключено через XInput · очікування HID-звіту драйвера",
                        "Connected via XInput · waiting for driver HID report",
                        "Über XInput verbunden · warte auf Treiber-HID-Bericht",
                        "Подключено через XInput · ожидание HID-отчёта драйвера"), fInfo, bInfo, cx, h - 22, sf);
                }
                sf.Dispose();
            }
        }

        private static GraphicsPath BuildStadiaShellPath(float cx, float cy, float s)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddBezier(cx - 62 * s, cy - 94 * s, cx - 22 * s, cy - 90 * s, cx + 22 * s, cy - 90 * s, cx + 62 * s, cy - 94 * s);
            p.AddBezier(cx + 62 * s, cy - 94 * s, cx + 128 * s, cy - 96 * s, cx + 168 * s, cy - 82 * s, cx + 188 * s, cy - 50 * s);
            p.AddBezier(cx + 188 * s, cy - 50 * s, cx + 216 * s, cy - 6 * s, cx + 224 * s, cy + 68 * s, cx + 206 * s, cy + 122 * s);
            p.AddBezier(cx + 206 * s, cy + 122 * s, cx + 194 * s, cy + 150 * s, cx + 148 * s, cy + 152 * s, cx + 128 * s, cy + 118 * s);
            p.AddBezier(cx + 128 * s, cy + 118 * s, cx + 108 * s, cy + 84 * s, cx + 80 * s, cy + 80 * s, cx, cy + 82 * s);
            p.AddBezier(cx, cy + 82 * s, cx - 80 * s, cy + 80 * s, cx - 108 * s, cy + 84 * s, cx - 128 * s, cy + 118 * s);
            p.AddBezier(cx - 128 * s, cy + 118 * s, cx - 148 * s, cy + 152 * s, cx - 194 * s, cy + 150 * s, cx - 206 * s, cy + 122 * s);
            p.AddBezier(cx - 206 * s, cy + 122 * s, cx - 224 * s, cy + 68 * s, cx - 216 * s, cy - 6 * s, cx - 188 * s, cy - 50 * s);
            p.AddBezier(cx - 188 * s, cy - 50 * s, cx - 168 * s, cy - 82 * s, cx - 128 * s, cy - 96 * s, cx - 62 * s, cy - 94 * s);
            p.CloseFigure();
            return p;
        }

        private static void DrawGripMotorGlow(Graphics g, float x, float y, float maxR, float intensity, Color baseCol)
        {
            for (int ring = 3; ring >= 1; ring--)
            {
                float r = maxR * (0.45f + ring * 0.18f) * (0.65f + intensity * 0.35f);
                int alpha = Math.Min(160, (int)(intensity * (ring == 1 ? 110 : ring == 2 ? 60 : 30)));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, baseCol)))
                {
                    g.FillEllipse(b, x - r, y - r * 1.15f, r * 2, r * 2.3f);
                }
            }
            using (Font f = new Font("Segoe UI", 7.2f, FontStyle.Bold))
            using (Brush bt = new SolidBrush(Color.FromArgb(220, Color.White)))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(string.Format("{0}%", (int)(intensity * 100)), f, bt, x, y, sf);
                sf.Dispose();
            }
        }

        private static void DrawTriggerMeter(Graphics g, float x, float y, float w, float h, byte val, string label, Color fill, float s)
        {
            using (Font f = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            using (Brush bt = new SolidBrush(val > 5 ? Color.White : Color.FromArgb(180, 192, 212)))
            {
                g.DrawString(string.Format("{0}: {1}%", label, (val * 100) / 255), f, bt, x, y - 16);
            }
            using (GraphicsPath bg = RoundedRect(x, y, w, h, 4))
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(30, 36, 48)))
            using (Pen bp = new Pen(Color.FromArgb(58, 68, 88), 1))
            {
                g.FillPath(bb, bg);
                if (val > 2)
                {
                    float fw = Math.Max(6, w * (val / 255.0f));
                    using (GraphicsPath fg = RoundedRect(x, y, fw, h, 4))
                    using (SolidBrush bf = new SolidBrush(fill))
                    {
                        g.FillPath(bf, fg);
                    }
                }
                g.DrawPath(bp, bg);
            }
        }

        private static void DrawCurvedShoulder(Graphics g, float x, float y, float w, float h, Color col)
        {
            using (GraphicsPath p = RoundedRect(x, y, w, h, 10))
            using (SolidBrush b = new SolidBrush(col))
            using (Pen pen = new Pen(Color.FromArgb(62, 72, 92), 1))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }

        private static void DrawBumperCap(Graphics g, float x, float y, float w, float h, bool pressed, Color active, string text, float s)
        {
            Color bg = pressed ? active : Color.FromArgb(38, 45, 59);
            using (GraphicsPath p = RoundedRect(x, y, w, h, 9))
            using (SolidBrush b = new SolidBrush(bg))
            using (Pen pen = new Pen(pressed ? Color.White : Color.FromArgb(72, 84, 108), pressed ? 1.8f : 1.2f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (Font f = new Font("Segoe UI", Math.Max(7.5f, 8.0f * s), FontStyle.Bold))
            using (Brush bt = new SolidBrush(pressed ? Color.White : Color.FromArgb(196, 206, 224)))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, f, bt, new RectangleF(x, y, w, h), sf);
                sf.Dispose();
            }
        }

        private static void DrawOptionsDotsButton(Graphics g, float cx, float cy, float r, bool pressed, Color active, Color idle, Color stroke)
        {
            using (SolidBrush b = new SolidBrush(pressed ? active : idle))
            using (Pen p = new Pen(pressed ? Color.White : stroke, pressed ? 1.8f : 1.1f))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            }
            float dotR = Math.Max(1.3f, r * 0.14f);
            float step = r * 0.44f;
            using (SolidBrush db = new SolidBrush(pressed ? Color.White : Color.FromArgb(205, 215, 230)))
            {
                for (int i = -1; i <= 1; i++)
                {
                    g.FillEllipse(db, cx + i * step - dotR, cy - dotR, dotR * 2, dotR * 2);
                }
            }
        }

        private static void DrawMenuHamburgerButton(Graphics g, float cx, float cy, float r, bool pressed, Color active, Color idle, Color stroke)
        {
            using (SolidBrush b = new SolidBrush(pressed ? active : idle))
            using (Pen p = new Pen(pressed ? Color.White : stroke, pressed ? 1.8f : 1.1f))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            }
            float hw = r * 0.48f;
            float gap = r * 0.30f;
            using (Pen lp = new Pen(pressed ? Color.White : Color.FromArgb(205, 215, 230), Math.Max(1.4f, r * 0.14f)))
            {
                lp.StartCap = LineCap.Round;
                lp.EndCap = LineCap.Round;
                for (int i = -1; i <= 1; i++)
                {
                    g.DrawLine(lp, cx - hw, cy + i * gap, cx + hw, cy + i * gap);
                }
            }
        }

        private static void DrawStadiaMainButton(Graphics g, float cx, float cy, float r, bool pressed, bool connected, Color coral, float s)
        {
            Color bg = pressed ? coral : (connected ? Color.FromArgb(52, 58, 76) : Color.FromArgb(40, 46, 60));
            Color rimCol = pressed ? Color.White : (connected ? coral : Color.FromArgb(80, 90, 112));
            using (SolidBrush b = new SolidBrush(bg))
            using (Pen p = new Pen(rimCol, pressed ? 2.4f : 1.8f))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            }

            Color waveCol = pressed ? Color.White : coral;
            using (Pen wPen = new Pen(waveCol, Math.Max(2.2f, 2.8f * s)))
            {
                wPen.StartCap = LineCap.Round;
                wPen.EndCap = LineCap.Round;
                g.DrawBezier(wPen, new PointF(cx - 8 * s, cy - 2 * s), new PointF(cx - 2 * s, cy - 9 * s), new PointF(cx + 6 * s, cy - 7 * s), new PointF(cx + 8 * s, cy - 3 * s));
                g.DrawBezier(wPen, new PointF(cx - 7 * s, cy + 2 * s), new PointF(cx - 1 * s, cy - 3 * s), new PointF(cx + 5 * s, cy + 1 * s), new PointF(cx + 4 * s, cy + 5 * s));
                g.DrawBezier(wPen, new PointF(cx - 4 * s, cy + 6 * s), new PointF(cx - 1 * s, cy + 4 * s), new PointF(cx + 2 * s, cy + 6 * s), new PointF(cx + 3 * s, cy + 8 * s));
            }
        }

        private static void DrawAssistantButton(Graphics g, float cx, float cy, float r, bool pressed, Color idle, Color stroke, float s)
        {
            using (SolidBrush b = new SolidBrush(pressed ? Color.FromArgb(59, 130, 246) : idle))
            using (Pen p = new Pen(pressed ? Color.White : stroke, pressed ? 1.8f : 1.1f))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            }
            Color[] cols = new Color[] {
                Color.FromArgb(66, 133, 244),
                Color.FromArgb(234, 67, 53),
                Color.FromArgb(251, 188, 5),
                Color.FromArgb(52, 168, 83)
            };
            float d = r * 0.22f;
            float[] dx = new float[] { -r * 0.34f, r * 0.14f, r * 0.12f, r * 0.42f };
            float[] dy = new float[] { -r * 0.10f, -r * 0.30f, r * 0.25f, r * 0.02f };
            for (int i = 0; i < 4; i++)
            {
                using (SolidBrush sb = new SolidBrush(pressed ? Color.White : cols[i]))
                {
                    g.FillEllipse(sb, cx + dx[i] - d, cy + dy[i] - d, d * 2, d * 2);
                }
            }
        }

        private static void DrawCaptureButton(Graphics g, float cx, float cy, float r, bool pressed, Color activeBlue, Color idle, Color stroke, float s)
        {
            using (SolidBrush b = new SolidBrush(pressed ? activeBlue : idle))
            using (Pen p = new Pen(pressed ? Color.White : stroke, pressed ? 1.8f : 1.1f))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            }
            float rw = r * 0.86f;
            float rh = r * 0.62f;
            using (Pen cp = new Pen(pressed ? Color.White : Color.FromArgb(195, 206, 224), Math.Max(1.1f, s * 1.1f)))
            {
                g.DrawRectangle(cp, cx - rw / 2, cy - rh / 2, rw, rh);
                float cr = r * 0.19f;
                g.DrawEllipse(cp, cx - cr, cy - cr, cr * 2, cr * 2);
            }
        }

        private static void DrawStadiaDpad(Graphics g, float cx, float cy, float dishR, ushort btns, Color active, Color idle, Color stroke)
        {
            using (SolidBrush dishB = new SolidBrush(Color.FromArgb(24, 28, 38)))
            using (Pen dishP = new Pen(Color.FromArgb(56, 65, 84), 1.2f))
            {
                g.FillEllipse(dishB, cx - dishR, cy - dishR, dishR * 2, dishR * 2);
                g.DrawEllipse(dishP, cx - dishR, cy - dishR, dishR * 2, dishR * 2);
            }

            float arm = dishR * 0.62f;
            float thick = arm * 0.68f;
            DrawDpadArm(g, cx - thick / 2, cy - arm * 1.48f, thick, arm, (btns & Native.BtnDpadUp) != 0 ? active : idle, stroke);
            DrawDpadArm(g, cx - thick / 2, cy + arm * 0.48f, thick, arm, (btns & Native.BtnDpadDown) != 0 ? active : idle, stroke);
            DrawDpadArm(g, cx - arm * 1.48f, cy - thick / 2, arm, thick, (btns & Native.BtnDpadLeft) != 0 ? active : idle, stroke);
            DrawDpadArm(g, cx + arm * 0.48f, cy - thick / 2, arm, thick, (btns & Native.BtnDpadRight) != 0 ? active : idle, stroke);

            using (SolidBrush c = new SolidBrush(idle))
            {
                g.FillRectangle(c, cx - thick / 2, cy - thick / 2, thick, thick);
            }
        }

        private static void DrawDpadArm(Graphics g, float x, float y, float w, float h, Color fill, Color stroke)
        {
            using (GraphicsPath p = RoundedRect(x, y, w, h, 4))
            using (SolidBrush b = new SolidBrush(fill))
            using (Pen pen = new Pen(stroke, 1.0f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }

        private static void DrawFaceButton(Graphics g, float cx, float cy, float r, string letter, Color tint, bool pressed, Color idle, Color stroke, float s)
        {
            Color bg = pressed ? tint : idle;
            Color rim = pressed ? Color.White : Color.FromArgb(140, tint);
            RectangleF rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (SolidBrush b = new SolidBrush(bg))
            using (Pen p = new Pen(rim, pressed ? 2.2f : 1.4f))
            {
                g.FillEllipse(b, rect);
                g.DrawEllipse(p, rect);
            }
            using (Font f = new Font("Segoe UI", Math.Max(7.0f, 9.2f * s), FontStyle.Bold))
            using (Brush bt = new SolidBrush(pressed ? Color.White : tint))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, f, bt, rect, sf);
                sf.Dispose();
            }
        }

        private static void DrawStadiaStick(Graphics g, float cx, float cy, float r, short sx, short sy, bool click, string label, Color accent, int dzPct, float s)
        {
            RectangleF outer = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(18, 21, 29)))
            using (Pen rim = new Pen(click ? Color.White : Color.FromArgb(72, 84, 108), click ? 2.4f : 1.5f))
            {
                g.FillEllipse(bg, outer);
                g.DrawEllipse(rim, outer);
            }

            using (Pen cross = new Pen(Color.FromArgb(44, 52, 68), 1.0f))
            {
                g.DrawLine(cross, cx - r + 4, cy, cx + r - 4, cy);
                g.DrawLine(cross, cx, cy - r + 4, cx, cy + r - 4);
            }
            float maxTravel = r - 11 * s;
            if (dzPct > 0)
            {
                float dzR = Math.Max(3.0f, maxTravel * (dzPct / 100.0f));
                using (Pen dzP = new Pen(Color.FromArgb(160, accent), 1.2f))
                {
                    dzP.DashStyle = DashStyle.Dash;
                    g.DrawEllipse(dzP, cx - dzR, cy - dzR, dzR * 2, dzR * 2);
                }
            }

            float nx = sx / 32768.0f;
            float ny = -sy / 32768.0f;
            float dx = cx + nx * maxTravel;
            float dy = cy + ny * maxTravel;

            using (Pen trail = new Pen(Color.FromArgb(145, accent), 2.0f))
            {
                g.DrawLine(trail, cx, cy, dx, dy);
            }

            float capR = 13.5f * s;
            RectangleF capRect = new RectangleF(dx - capR, dy - capR, capR * 2, capR * 2);
            using (SolidBrush capB = new SolidBrush(click ? accent : Color.FromArgb(54, 62, 80)))
            using (Pen capP = new Pen(click ? Color.White : accent, 1.6f))
            {
                g.FillEllipse(capB, capRect);
                g.DrawEllipse(capP, capRect);
            }
            float innerR = capR * 0.62f;
            using (Pen innerP = new Pen(Color.FromArgb(95, Color.Black), 1.2f))
            {
                g.DrawEllipse(innerP, dx - innerR, dy - innerR, innerR * 2, innerR * 2);
            }

            using (Font f = new Font("Segoe UI", Math.Max(6.8f, 7.2f * s), FontStyle.Bold))
            using (Brush bl = new SolidBrush(click ? Color.White : Color.FromArgb(160, 174, 196)))
            {
                g.DrawString(label, f, bl, cx - r - 2 * s, cy - r - 2 * s);
            }
        }

        private static Color BlendColor(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            int r = (int)(a.R + (b.R - a.R) * t);
            int gg = (int)(a.G + (b.G - a.G) * t);
            int bl = (int)(a.B + (b.B - a.B) * t);
            return Color.FromArgb(r, gg, bl);
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            float d = r * 2.0f;
            if (d > w) d = w;
            if (d > h) d = h;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class BleMonitorPanel : Panel
    {
        private List<BleSamplePoint> _points = new List<BleSamplePoint>();

        public BleMonitorPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 17, 24);
        }

        public void SetPoints(List<BleSamplePoint> pts)
        {
            _points = pts;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            int w = Width;
            int h = Height;
            if (w < 160 || h < 180) return;

            int gap = 8;
            int chartH = (h - gap * 4) / 3;
            if (chartH < 46) return;

            DrawRssiChart(g, new RectangleF(8, gap, w - 16, chartH));
            DrawLatencyChart(g, new RectangleF(8, gap * 2 + chartH, w - 16, chartH));
            DrawOpsChart(g, new RectangleF(8, gap * 3 + chartH * 2, w - 16, chartH));
        }

        private void DrawChartBox(Graphics g, RectangleF box, string title, string rightVal, Color valCol)
        {
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(20, 24, 34)))
            using (Pen border = new Pen(Color.FromArgb(40, 48, 66), 1))
            {
                g.FillRectangle(bg, box);
                g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
            }
            using (Font ft = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            using (Font fv = new Font("Consolas", 8.2f, FontStyle.Bold))
            using (Brush bt = new SolidBrush(Color.FromArgb(165, 178, 198)))
            using (Brush bv = new SolidBrush(valCol))
            {
                g.DrawString(title, ft, bt, box.X + 8, box.Y + 5);
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Far;
                g.DrawString(rightVal, fv, bv, box.Right - 8, box.Y + 5, sf);
                sf.Dispose();
            }
        }

        private void DrawRssiChart(Graphics g, RectangleF box)
        {
            int latest = _points.Count > 0 ? _points[_points.Count - 1].RssiDbm : -55;
            Color col = latest >= -65 ? Color.FromArgb(16, 185, 129) : (latest >= -75 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(239, 68, 68));
            DrawChartBox(g, box, I18n.T(
                "1. Стабільність сигналу BLE (RSSI, dBm)",
                "1. BLE Signal Stability (RSSI, dBm)",
                "1. BLE-Signalstabilität (RSSI, dBm)",
                "1. Стабильность сигнала BLE (RSSI, dBm)"), string.Format("{0} dBm", latest), col);

            float px = box.X + 36;
            float py = box.Y + 24;
            float pw = box.Width - 46;
            float ph = box.Height - 32;
            if (pw < 20 || ph < 16) return;

            using (Pen grid = new Pen(Color.FromArgb(34, 42, 58), 1))
            using (Font fl = new Font("Consolas", 7.0f))
            using (Brush bl = new SolidBrush(Color.FromArgb(115, 130, 155)))
            {
                grid.DashStyle = DashStyle.Dot;
                g.DrawLine(grid, px, py, px + pw, py);
                g.DrawLine(grid, px, py + ph * 0.5f, px + pw, py + ph * 0.5f);
                g.DrawLine(grid, px, py + ph, px + pw, py + ph);
                g.DrawString("-40", fl, bl, box.X + 4, py - 5);
                g.DrawString("-70", fl, bl, box.X + 4, py + ph * 0.5f - 5);
                g.DrawString("-95", fl, bl, box.X + 4, py + ph - 6);
            }

            if (_points.Count < 2) return;
            PointF[] pts = new PointF[_points.Count];
            for (int i = 0; i < _points.Count; i++)
            {
                float rx = px + (pw * i) / Math.Max(1, _points.Count - 1);
                double norm = (_points[i].RssiDbm - (-95.0)) / 55.0;
                if (norm < 0) norm = 0;
                if (norm > 1) norm = 1;
                float ry = py + ph - (float)(norm * ph);
                pts[i] = new PointF(rx, ry);
            }

            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLine(pts[0].X, py + ph, pts[0].X, pts[0].Y);
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, py + ph);
                area.CloseFigure();
                using (SolidBrush ab = new SolidBrush(Color.FromArgb(40, col)))
                {
                    g.FillPath(ab, area);
                }
            }
            using (Pen line = new Pen(col, 2.0f))
            {
                g.DrawLines(line, pts);
            }
        }

        private void DrawLatencyChart(Graphics g, RectangleF box)
        {
            int latest = _points.Count > 0 ? _points[_points.Count - 1].LatencyMs : 28;
            Color col = latest <= 45 ? Color.FromArgb(255, 82, 56) : Color.FromArgb(239, 68, 68);
            DrawChartBox(g, box, I18n.T(
                "2. Затримка відповіді BLE / ACK (мс)",
                "2. BLE / ACK Response Latency (ms)",
                "2. BLE / ACK-Antwortlatenz (ms)",
                "2. Задержка ответа BLE / ACK (мс)"), string.Format("{0} {1}", latest, I18n.T("мс", "ms", "ms", "мс")), col);

            float px = box.X + 36;
            float py = box.Y + 24;
            float pw = box.Width - 46;
            float ph = box.Height - 32;
            if (pw < 20 || ph < 16) return;

            int maxLat = 90;
            for (int i = 0; i < _points.Count; i++)
            {
                if (_points[i].LatencyMs + 15 > maxLat) maxLat = _points[i].LatencyMs + 15;
            }

            // 45ms spike threshold
            float thrY = py + ph - (float)((45.0 / maxLat) * ph);
            using (Pen thr = new Pen(Color.FromArgb(140, 245, 158, 11), 1.1f))
            using (Font fl = new Font("Consolas", 7.0f))
            using (Brush bl = new SolidBrush(Color.FromArgb(217, 155, 50)))
            {
                thr.DashStyle = DashStyle.Dash;
                g.DrawLine(thr, px, thrY, px + pw, thrY);
                g.DrawString("45", fl, bl, box.X + 8, thrY - 5);
            }
            using (Font fl = new Font("Consolas", 7.0f))
            using (Brush bl = new SolidBrush(Color.FromArgb(115, 130, 155)))
            {
                g.DrawString(maxLat.ToString(), fl, bl, box.X + 8, py - 5);
                g.DrawString("0", fl, bl, box.X + 14, py + ph - 6);
            }

            if (_points.Count < 2) return;
            PointF[] pts = new PointF[_points.Count];
            for (int i = 0; i < _points.Count; i++)
            {
                float rx = px + (pw * i) / Math.Max(1, _points.Count - 1);
                double norm = (double)_points[i].LatencyMs / maxLat;
                if (norm < 0) norm = 0;
                if (norm > 1) norm = 1;
                pts[i] = new PointF(rx, py + ph - (float)(norm * ph));
            }
            using (Pen line = new Pen(col, 2.0f))
            {
                g.DrawLines(line, pts);
            }
        }

        private void DrawOpsChart(Graphics g, RectangleF box)
        {
            int totOk = 0, totFail = 0;
            for (int i = 0; i < _points.Count; i++)
            {
                totOk += _points[i].WritesDelta;
                totFail += _points[i].FailuresDelta;
            }
            DrawChartBox(g, box, I18n.T(
                "3. Успішні та невдалі записи за інтервал",
                "3. Successful & Failed Writes per Interval",
                "3. Erfolgreiche & Fehlgeschlagene Schreibvorgänge",
                "3. Успешные и неудачные записи за интервал"),
                string.Format("OK +{0} | {1} +{2}", totOk, I18n.T("Збої", "Fails", "Fehler", "Сбои"), totFail),
                totFail == 0 ? Color.FromArgb(16, 185, 129) : Color.FromArgb(248, 113, 113));

            float px = box.X + 36;
            float py = box.Y + 24;
            float pw = box.Width - 46;
            float ph = box.Height - 32;
            if (pw < 20 || ph < 16 || _points.Count == 0) return;

            int maxOp = 10;
            for (int i = 0; i < _points.Count; i++)
            {
                int v = _points[i].WritesDelta + _points[i].FailuresDelta;
                if (v > maxOp) maxOp = v;
            }

            float slotW = pw / _points.Count;
            float barW = Math.Max(3.0f, slotW * 0.62f);

            using (SolidBrush bOk = new SolidBrush(Color.FromArgb(16, 185, 129)))
            using (SolidBrush bFail = new SolidBrush(Color.FromArgb(239, 68, 68)))
            using (Pen baseLine = new Pen(Color.FromArgb(46, 54, 72), 1))
            {
                g.DrawLine(baseLine, px, py + ph, px + pw, py + ph);
                for (int i = 0; i < _points.Count; i++)
                {
                    float bx = px + i * slotW + (slotW - barW) * 0.5f;
                    float hOk = (float)((_points[i].WritesDelta / (double)maxOp) * ph);
                    float hFail = (float)((_points[i].FailuresDelta / (double)maxOp) * ph);
                    if (hOk > 0.5f)
                    {
                        g.FillRectangle(bOk, bx, py + ph - hOk, barW, hOk);
                    }
                    if (hFail > 0.5f)
                    {
                        g.FillRectangle(bFail, bx, py + ph - hOk - hFail, barW, hFail);
                    }
                }
            }
        }
    }

    internal sealed class BatteryHistoryPanel : Panel
    {
        private List<BatteryPoint> _history = new List<BatteryPoint>();

        public BatteryHistoryPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 17, 24);
        }

        public void SetHistory(List<BatteryPoint> history)
        {
            _history = history;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            int w = Width;
            int h = Height;
            if (w < 160 || h < 120) return;

            RectangleF box = new RectangleF(4, 4, w - 8, h - 8);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(20, 24, 34)))
            using (Pen border = new Pen(Color.FromArgb(40, 48, 66), 1))
            {
                g.FillRectangle(bg, box);
                g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
            }

            using (Font ft = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (Brush bt = new SolidBrush(Color.FromArgb(203, 213, 225)))
            {
                g.DrawString(I18n.T(
                    "Лінійний графік історії розряду акумулятора (% · Feature Report 0xE1)",
                    "Battery Discharge History Line Chart (% · Feature Report 0xE1)",
                    "Akku-Entladeverlauf (Liniendiagramm % · Feature Report 0xE1)",
                    "Линейный график истории разряда аккумулятора (% · Feature Report 0xE1)"), ft, bt, box.X + 10, box.Y + 8);
            }

            float px = box.X + 42;
            float py = box.Y + 34;
            float pw = box.Width - 58;
            float ph = box.Height - 62;
            if (pw < 30 || ph < 30) return;

            using (Pen grid = new Pen(Color.FromArgb(36, 44, 60), 1))
            using (Font fl = new Font("Consolas", 7.5f))
            using (Brush bl = new SolidBrush(Color.FromArgb(128, 144, 170)))
            {
                grid.DashStyle = DashStyle.Dot;
                int[] ticks = new int[] { 100, 75, 50, 25, 0 };
                for (int i = 0; i < ticks.Length; i++)
                {
                    float gy = py + ph - (ticks[i] / 100.0f) * ph;
                    g.DrawLine(grid, px, gy, px + pw, gy);
                    g.DrawString(ticks[i] + "%", fl, bl, box.X + 6, gy - 6);
                }
            }

            if (_history.Count < 2) return;
            PointF[] pts = new PointF[_history.Count];
            for (int i = 0; i < _history.Count; i++)
            {
                float rx = px + (pw * i) / (_history.Count - 1);
                float ry = py + ph - (Native.ClampInt(_history[i].Percent, 0, 100) / 100.0f) * ph;
                pts[i] = new PointF(rx, ry);
            }

            Color lineCol = _history[_history.Count - 1].Percent >= 30 ? Color.FromArgb(16, 185, 129) : Color.FromArgb(245, 158, 11);
            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLine(pts[0].X, py + ph, pts[0].X, pts[0].Y);
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, py + ph);
                area.CloseFigure();
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                    new PointF(0, py), new PointF(0, py + ph),
                    Color.FromArgb(75, lineCol), Color.FromArgb(6, lineCol)))
                {
                    g.FillPath(lgb, area);
                }
            }
            using (Pen lp = new Pen(lineCol, 2.4f))
            {
                g.DrawLines(lp, pts);
            }
            using (SolidBrush dotB = new SolidBrush(Color.White))
            using (Pen dotP = new Pen(lineCol, 1.8f))
            using (Font fl = new Font("Consolas", 7.2f))
            using (Brush bl = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                for (int i = 0; i < pts.Length; i++)
                {
                    g.FillEllipse(dotB, pts[i].X - 3.2f, pts[i].Y - 3.2f, 6.4f, 6.4f);
                    g.DrawEllipse(dotP, pts[i].X - 3.2f, pts[i].Y - 3.2f, 6.4f, 6.4f);
                    if (i == 0 || i == pts.Length - 1 || i % 3 == 0)
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        g.DrawString(_history[i].TimeLabel, fl, bl, pts[i].X, py + ph + 6, sf);
                        sf.Dispose();
                    }
                }
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private float _dpi = 1.0f;

        private Panel _headerPanel;
        private Label _titleLabel;
        private Label _subLabel;
        private ComboBox _langBox;
        private ComboBox _slotBox;
        private Label _statusPill;
        private Label _batteryPill;
        private Button _criticalBtn;
        private Button _autoDiagBtn;
        private Button _saveLogBtn;

        private Panel _leftCol;
        private StadiaControllerCanvas _canvas;
        private Panel _pollCard;
        private Label _pollingLabel;
        private Label _rawHexLabel;
        private Button _resetStatsBtn;

        private Panel _rightCol;
        private Panel _tabBar;
        private Button _tabMotorsBtn;
        private Button _tabBleBtn;
        private Button _tabPowerBtn;
        private Button _tabTuneBtn;
        private Button _tabTurboBtn;
        private Button _tabDumpBtn;
        private int _activeTab = 0;

        private Panel _tabMotorsPage;
        private Panel _tabBlePage;
        private Panel _tabPowerPage;
        private Panel _tabTunePage;
        private Panel _tabTurboPage;
        private Panel _tabDumpPage;

        // Tab 0: Motors & Rumble
        private Label _motorsSecHdr;
        private Label _motorsHintLbl;
        private Label _strongCapLbl;
        private Label _weakCapLbl;
        private Label _motorsTestSecHdr;
        private Label _settingsSupportBanner;
        private DarkSlider _strongGainSlider;
        private DarkSlider _weakGainSlider;
        private Label _strongGainLabel;
        private Label _weakGainLabel;
        private CheckBox _syncMotorsCheck;
        private Button _pulseBtn;
        private Button _sweepBtn;
        private Button _holdBtn;
        private Button _stopBtn;
        private Button _applyGainBtn;

        // Tab 1: BLE Monitor
        private Label _bleSecHdr;
        private Label _bleKpiLabel;
        private BleMonitorPanel _bleChart;
        private Button _blePauseBtn;
        private Button _bleStressBtn;
        private Button _bleResetBtn;
        private bool _blePaused = false;
        private List<BleSamplePoint> _bleSamples = new List<BleSamplePoint>();
        private uint _prevWritesForChart = 0;
        private uint _prevFailsForChart = 0;
        private long _lastBleChartSampleTicks = 0;

        // Tab 2: Power (0xE1 Battery + Linear Chart)
        private Label _powerSecHdr;
        private Label _powerSummaryLabel;
        private BatteryHistoryPanel _batteryChart;
        private Button _powerDrainTestBtn;
        private Button _powerResetBtn;
        private List<BatteryPoint> _batteryHistory = new List<BatteryPoint>();
        private int _winPnpBatteryPct = -1;
        private long _lastWinBatteryPollTicks = 0;
        private long _lastBatterySampleTicks = 0;

        // Tab 3: Sticks, Triggers, BLE Patch & WebUSB
        private Label _tuneSecHdr1;
        private Label _stickDzCapLbl;
        private Label _trigDzCapLbl;
        private Label _trigMaxCapLbl;
        private Label _tuneSecHdr2;
        private Label _bleIntCapLbl;
        private Label _bleGapCapLbl;
        private Label _bleDeltaCapLbl;
        private DarkSlider _stickDzSlider;
        private Label _stickDzLabel;
        private DarkSlider _trigDzSlider;
        private Label _trigDzLabel;
        private DarkSlider _trigMaxSlider;
        private Label _trigMaxLabel;
        private CheckBox _swapSticksCheck;
        private CheckBox _invertLyCheck;
        private CheckBox _invertRyCheck;
        private Button _presetFastBtn;
        private Button _presetBalBtn;
        private Button _presetSmoothBtn;
        private DarkSlider _bleIntervalSlider;
        private Label _bleIntervalLabel;
        private DarkSlider _bleGapSlider;
        private Label _bleGapLabel;
        private DarkSlider _bleDeltaSlider;
        private Label _bleDeltaLabel;
        private Button _applyAllBtn;
        private Button _resetDefaultBtn;
        private Button _exportPatchBtn;
        private Button _openWebUsbBtn;
        private Label _tuneStatusLabel;

        // Tab 4: Driver Macros (0xE3) & Hardware Turbo (0xE2) inside winstadia.dll
        private Panel _macroDriverBox;
        private Label _macroDriverTitle;
        private Label _macroDriverBody;
        private Label _macroDriverStatusLbl;

        private Panel _macrosCard;
        private Label _macrosSecHdr;
        private CheckBox _macrosEnabledCheck;
        private Label _assistActionCapLbl;
        private ComboBox _assistActionCombo;
        private Label _captureActionCapLbl;
        private ComboBox _captureActionCombo;
        private Label _customKeyCapLbl;
        private Button _customKeyBtn;
        private ComboBox _customKeyPresetCombo;
        private Label _customKeyHintLbl;
        private CheckBox _dpadComboCheck;
        private Button _macroApplyBtn;
        private Label _macroLastFiredLabel;

        private Label _turboSecHdr;
        private Label _turboHintLbl;
        private Label _turboHzCapLbl;
        private CheckBox _turboEnabledCheck;
        private CheckBox _turboHoldModeCheck;
        private DarkSlider _turboHzSlider;
        private Label _turboHzLabel;
        private Button[] _turboBtnButtons;
        private int _turboMaskLocal = 0;
        private Button _turboApplyBtn;
        private Button _turboClearBtn;
        private Label _turboLiveLabel;
        private Label _turboAssistStateBadge;
        private Label _turboHwStatusLabel;
        private ToolTip _uiToolTip;
        private string _customKeyErrorText = "";

        private struct HotkeyPreset
        {
            public ushort Vk;
            public bool Ctrl;
            public bool Alt;
            public bool Shift;
            public string Label;
            public HotkeyPreset(ushort vk, bool ctrl, bool alt, bool shift, string label)
            {
                Vk = vk;
                Ctrl = ctrl;
                Alt = alt;
                Shift = shift;
                Label = label;
            }
        }

        private static readonly HotkeyPreset[] _hotkeyPresets = new HotkeyPreset[]
        {
            new HotkeyPreset(0x78, false, false, false, "F9"),
            new HotkeyPreset(0x79, false, false, false, "F10"),
            new HotkeyPreset(0x7A, false, false, false, "F11"),
            new HotkeyPreset(0x7B, false, false, false, "F12"),
            new HotkeyPreset(0x2C, false, false, false, "PrtSc"),
            new HotkeyPreset(0x13, false, false, false, "Pause"),
            new HotkeyPreset(0x91, false, false, false, "ScrollLock"),
            new HotkeyPreset(0x2D, false, false, false, "Insert"),
            new HotkeyPreset(0x20, false, false, false, "Space"),
            new HotkeyPreset(0x09, false, false, false, "Tab"),
            new HotkeyPreset(0x0D, false, false, false, "Enter"),
            new HotkeyPreset(0x1B, false, false, false, "Esc"),
            new HotkeyPreset(0x78, false, true,  false, "Alt+F9"),
            new HotkeyPreset(0x5A, false, true,  false, "Alt+Z"),
            new HotkeyPreset(0x4D, true,  false, true,  "Ctrl+Shift+M"),
            new HotkeyPreset(0x53, true,  false, true,  "Ctrl+Shift+S")
        };

        // Tab 5: Crash Dump Analyzer & Log
        private Label _dumpSecHdr;
        private Panel _criticalGuideBox;
        private Label _criticalGuideTitle;
        private Label _criticalGuideBody;
        private Button _scanDumpsBtn;
        private Button _importDumpBtn;
        private Button _copyLogBtn;
        private Label _telemetryLabel;
        private TextBox _logBox;

        private readonly System.Windows.Forms.Timer _uiTimer;
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly ManualResetEvent _stopPollEvent = new ManualResetEvent(false);
        private readonly EventWaitHandle _wakeEvent;
        private Thread _pollThread;
        private Thread _telemetryThread;
        private Thread _wakeThread;

        private Icon _appIcon;
        private bool _workersStarted = false;

        private readonly object _stateLock = new object();
        private bool _padConnected;
        private Native.XInputState _latestState;
        private uint _lastPacketNum;
        private long _lastPacketTicks;
        private int _packetsInWindow;
        private long _windowStartTicks;
        private double _measuredHz;
        private double _lastActiveHz;
        private double _lastIntervalMs;
        private double _avgIntervalMs;
        private double _maxIntervalMs;
        private int _spikeCount;
        private int _activeSlot = 0;

        private IntPtr _hidHandle = IntPtr.Zero;
        private DriverTelemetry _latestTelemetry = new DriverTelemetry();
        private DriverSettings _driverSettings = new DriverSettings();
        private bool _driverSettingsSupported = false;
        private bool _settingsLoadedFromDriver = false;
        private bool _driverMacroSupported = false;
        private bool _macroSettingsLoadedFromDriver = false;

        private readonly object _macroLock = new object();
        private readonly AppMacroSettings _appMacros = new AppMacroSettings();
        private bool _suppressMacroEvents = false;
        private bool _capturingCustomKey = false;
        private byte _prevMacroSeq = 0;
        private bool _macroSeqInitialized = false;
        private string _lastMacroEventText = "";

        private readonly object _logLock = new object();
        private readonly List<string> _logLines = new List<string>();
        private int _desiredStrong;
        private int _desiredWeak;
        private bool _holdingRumble;
        private string _animMode = "";
        private long _animStartTicks;
        private int _animStep;
        private uint _diagStartWrites;
        private uint _diagStartFailures;
        private int _diagStartSpikes;
        private bool _suppressSliderEvents;
        private bool _suppressLangEvents;
        private string _customDumpAnalysis = "";

        private readonly object _turboEventLock = new object();
        private byte _prevTurboToggleSeq = 0;
        private bool _turboSeqInitialized = false;
        private int _lastObservedDriverTurboMask = -1;
        private long _turboHapticUntilTicks = 0;
        private volatile bool _uiRumbleActive = false;
        private string _lastTurboEventText = "";

        private int S(int px)
        {
            return (int)Math.Round(px * _dpi);
        }

        public MainForm(EventWaitHandle wakeEvent)
        {
            _wakeEvent = wakeEvent;
            _appMacros.LoadFromRegistry();
            KeyPreview = true;

            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics())
            {
                _dpi = g.DpiX / 96.0f;
                if (_dpi < 1.0f) _dpi = 1.0f;
            }

            SeedInitialCharts();
            _appIcon = CreateStadiaIcon();
            if (_appIcon != null) Icon = _appIcon;

            Text = I18n.T(
                "Stadia Rumble Studio v1.0.15 (BLE · Макроси 0xE3 · Турбо 0xE2)",
                "Stadia Rumble Studio v1.0.15 (BLE · Macros 0xE3 · Turbo 0xE2)",
                "Stadia Rumble Studio v1.0.15 (BLE · Makros 0xE3 · Turbo 0xE2)",
                "Stadia Rumble Studio v1.0.15 (BLE · Макросы 0xE3 · Турбо 0xE2)");
            ClientSize = new Size(S(1180), S(760));
            MinimumSize = new Size(S(980), S(660));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(14, 17, 22);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = new Font("Segoe UI", 9.5f);

            _uiToolTip = new ToolTip();
            _uiToolTip.AutoPopDelay = 9000;
            _uiToolTip.InitialDelay = 350;
            _uiToolTip.ReshowDelay = 200;

            BuildHeader();
            BuildLeftColumn();
            BuildRightColumn();

            Controls.Add(_rightCol);
            Controls.Add(_leftCol);
            Controls.Add(_headerPanel);

            Resize += (s, e) => { LayoutMainSplit(); };
            Deactivate += (s, e) => { CancelCustomKeyCapture(); };
            LayoutMainSplit();
            ApplyLanguageToUi();
            SelectTab(0);

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 32;
            _uiTimer.Tick += (s, e) => { OnUiTick(); };
            _uiTimer.Start();

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 40;
            _animTimer.Tick += (s, e) => { OnAnimTick(); };

            Shown += (s, e) =>
            {
                if (!_workersStarted)
                {
                    _workersStarted = true;
                    StartBackgroundWorkers();
                }
                LayoutMainSplit();
            };
        }

        private void StartBackgroundWorkers()
        {
            Native.EnableHighResolutionTimer();
            AddLog(I18n.T(
                "Stadia Rumble Studio v1.0.15 запущено (автономні макроси 0xE3 і Turbo 0xE2 у драйвері winstadia.dll).",
                "Stadia Rumble Studio v1.0.15 started (standalone 0xE3 macros & 0xE2 Turbo in winstadia.dll).",
                "Stadia Rumble Studio v1.0.15 gestartet (autonome 0xE3-Makros & 0xE2-Turbo in winstadia.dll).",
                "Stadia Rumble Studio v1.0.15 запущен (автономные макросы 0xE3 и Turbo 0xE2 в драйвере winstadia.dll)."));
            AddLog(string.Format("Windows: {0} (DPI {1:F2}x)", GetWindowsSummary(), _dpi));
            _pollThread = new Thread(PollWorker);
            _pollThread.IsBackground = true;
            _pollThread.Start();
            _telemetryThread = new Thread(TelemetryWorker);
            _telemetryThread.IsBackground = true;
            _telemetryThread.Start();
            if (_wakeEvent != null)
            {
                _wakeThread = new Thread(WakeListenerWorker);
                _wakeThread.IsBackground = true;
                _wakeThread.Start();
            }
            ScanWindowsBatteryAsync();
        }

        private void WakeListenerWorker()
        {
            WaitHandle[] handles = new WaitHandle[] { _stopPollEvent, _wakeEvent };
            while (true)
            {
                int idx = WaitHandle.WaitAny(handles);
                if (idx == 0) break;
                try { BeginInvoke(new Action(RestoreWindowFocus)); } catch { }
            }
        }

        private void RestoreWindowFocus()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Activate();
            BringToFront();
        }

        private static Icon CreateStadiaIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(18, 22, 30)))
                    using (Pen rim = new Pen(Color.FromArgb(255, 82, 56), 2.4f))
                    {
                        g.FillEllipse(bg, 2, 2, 28, 28);
                        g.DrawEllipse(rim, 2, 2, 28, 28);
                    }
                    using (Pen wave = new Pen(Color.FromArgb(255, 82, 56), 2.8f))
                    {
                        wave.StartCap = LineCap.Round;
                        wave.EndCap = LineCap.Round;
                        g.DrawBezier(wave, new PointF(8, 14), new PointF(14, 7), new PointF(21, 9), new PointF(24, 13));
                        g.DrawBezier(wave, new PointF(9, 19), new PointF(15, 14), new PointF(21, 17), new PointF(20, 21));
                        g.DrawBezier(wave, new PointF(13, 23), new PointF(16, 21), new PointF(19, 22), new PointF(20, 24));
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(hIcon))
                        {
                            return (Icon)tmp.Clone();
                        }
                    }
                    finally
                    {
                        Native.DestroyIcon(hIcon);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private void SeedInitialCharts()
        {
            _bleSamples.Clear();
            for (int i = 0; i < 24; i++)
            {
                BleSamplePoint p = new BleSamplePoint();
                p.RssiDbm = -56 + (int)Math.Round(Math.Sin(i * 0.45) * 3);
                p.LatencyMs = 26 + ((i * 5) % 9);
                p.WritesDelta = 0;
                p.FailuresDelta = 0;
                p.TimeLabel = DateTime.Now.AddSeconds(-(24 - i)).ToString("HH:mm:ss");
                _bleSamples.Add(p);
            }

            _batteryHistory.Clear();
            string minSuffix = I18n.T("хв", "m", "Min", "мин");
            for (int i = 0; i < 10; i++)
            {
                BatteryPoint bp = new BatteryPoint();
                bp.Percent = 85;
                bp.VoltageV = 4.06;
                bp.TimeLabel = i == 9 ? I18n.T("Зараз", "Now", "Jetzt", "Сейчас") : ("-" + ((9 - i) * 5) + minSuffix);
                _batteryHistory.Add(bp);
            }
        }

        private void BuildHeader()
        {
            _headerPanel = new Panel();
            _headerPanel.BackColor = Color.FromArgb(20, 24, 33);
            _headerPanel.Height = S(74);
            _headerPanel.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(40, 48, 64), 1))
                {
                    e.Graphics.DrawLine(p, 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
                }
            };

            _titleLabel = new Label();
            _titleLabel.Text = "STADIA RUMBLE STUDIO";
            _titleLabel.Font = new Font("Segoe UI", 12.6f, FontStyle.Bold);
            _titleLabel.ForeColor = Color.White;
            _titleLabel.AutoSize = true;
            _headerPanel.Controls.Add(_titleLabel);

            _subLabel = new Label();
            _subLabel.Text = "";
            _subLabel.Font = new Font("Segoe UI", 8.2f);
            _subLabel.ForeColor = Color.FromArgb(148, 163, 184);
            _subLabel.AutoSize = true;
            _headerPanel.Controls.Add(_subLabel);

            _langBox = new ComboBox();
            _langBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _langBox.BackColor = Color.FromArgb(31, 37, 50);
            _langBox.ForeColor = Color.FromArgb(110, 231, 183);
            _langBox.FlatStyle = FlatStyle.Flat;
            _langBox.Font = new Font("Segoe UI", 8.6f, FontStyle.Bold);
            _langBox.Items.AddRange(new object[] { "УКР", "ENG", "DEU", "РУС" });
            _suppressLangEvents = true;
            _langBox.SelectedIndex = Native.ClampInt((int)I18n.Current, 0, 3);
            _suppressLangEvents = false;
            _langBox.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressLangEvents) return;
                I18n.Current = (AppLanguage)Native.ClampInt(_langBox.SelectedIndex, 0, 3);
                I18n.SaveToRegistry();
                ApplyLanguageToUi();
                AddLog(I18n.T(
                    "Мову інтерфейсу змінено на українську.",
                    "Interface language switched to English.",
                    "Programmsprache auf Deutsch geändert.",
                    "Язык интерфейса изменён на русский."));
            };
            _headerPanel.Controls.Add(_langBox);

            _slotBox = new ComboBox();
            _slotBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _slotBox.BackColor = Color.FromArgb(31, 37, 50);
            _slotBox.ForeColor = Color.White;
            _slotBox.FlatStyle = FlatStyle.Flat;
            _slotBox.Font = new Font("Segoe UI", 8.6f, FontStyle.Bold);
            _slotBox.Items.AddRange(new object[] { "XInput #1", "XInput #2", "XInput #3", "XInput #4" });
            _slotBox.SelectedIndex = 0;
            _slotBox.SelectedIndexChanged += (s, e) =>
            {
                lock (_stateLock)
                {
                    _activeSlot = _slotBox.SelectedIndex;
                    _lastPacketTicks = 0;
                }
                AddLog(string.Format(I18n.T("Обрано слот XInput #{0}", "Selected XInput slot #{0}", "XInput-Slot #{0} gewählt", "Выбран слот XInput #{0}"), _slotBox.SelectedIndex + 1));
            };
            _headerPanel.Controls.Add(_slotBox);

            _statusPill = new Label();
            _statusPill.Text = "...";
            _statusPill.TextAlign = ContentAlignment.MiddleCenter;
            _statusPill.BackColor = Color.FromArgb(42, 49, 64);
            _statusPill.ForeColor = Color.FromArgb(203, 213, 225);
            _statusPill.Font = new Font("Segoe UI", 8.4f, FontStyle.Bold);
            _headerPanel.Controls.Add(_statusPill);

            _batteryPill = new Label();
            _batteryPill.Text = "--";
            _batteryPill.TextAlign = ContentAlignment.MiddleCenter;
            _batteryPill.BackColor = Color.FromArgb(31, 37, 50);
            _batteryPill.ForeColor = Color.FromArgb(110, 231, 183);
            _batteryPill.Font = new Font("Segoe UI", 8.4f, FontStyle.Bold);
            _batteryPill.Cursor = Cursors.Hand;
            _batteryPill.Click += (s, e) => { SelectTab(2); };
            _headerPanel.Controls.Add(_batteryPill);

            _criticalBtn = CreateActionButton("OK", Color.FromArgb(31, 41, 55), Color.FromArgb(110, 231, 183));
            _criticalBtn.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold);
            _criticalBtn.Click += (s, e) => { SelectTab(5); };
            _headerPanel.Controls.Add(_criticalBtn);

            _autoDiagBtn = CreateActionButton("Auto", Color.FromArgb(255, 82, 56), Color.White);
            _autoDiagBtn.Click += (s, e) => { StartAutoDiagnostic(); };
            _headerPanel.Controls.Add(_autoDiagBtn);

            _saveLogBtn = CreateActionButton("Log", Color.FromArgb(16, 185, 129), Color.FromArgb(10, 20, 16));
            _saveLogBtn.Click += (s, e) => { ExportLogAsync(); };
            _headerPanel.Controls.Add(_saveLogBtn);
        }

        private void BuildLeftColumn()
        {
            _leftCol = new Panel();
            _leftCol.BackColor = BackColor;

            _canvas = new StadiaControllerCanvas();
            _canvas.MotorZoneClicked += (s, e) =>
            {
                if (_padConnected)
                {
                    if (e.IsLeft) ApplyTestVibration(85, 0);
                    else ApplyTestVibration(0, 85);
                    Thread t = new Thread(() =>
                    {
                        Thread.Sleep(550);
                        try { BeginInvoke(new Action(() => { if (!_holdingRumble && string.IsNullOrEmpty(_animMode)) ApplyTestVibration(0, 0); })); } catch { }
                    });
                    t.IsBackground = true;
                    t.Start();
                }
                AddLog(e.IsLeft
                    ? I18n.T("Тест кліком: лівий мотор Strong 85%", "Click test: Left Strong motor 85%", "Klicktest: Linker Strong-Motor 85%", "Тест кликом: левый мотор Strong 85%")
                    : I18n.T("Тест кліком: правий мотор Weak 85%", "Click test: Right Weak motor 85%", "Klicktest: Rechter Weak-Motor 85%", "Тест кликом: правый мотор Weak 85%"));
            };
            _leftCol.Controls.Add(_canvas);

            _pollCard = CreateCardPanel();
            _pollingLabel = new Label();
            _pollingLabel.Text = "...";
            _pollingLabel.Font = new Font("Consolas", 8.5f, FontStyle.Bold);
            _pollingLabel.ForeColor = Color.FromArgb(226, 232, 240);
            _pollingLabel.AutoSize = true;
            _pollCard.Controls.Add(_pollingLabel);

            _resetStatsBtn = CreateActionButton("Reset", Color.FromArgb(44, 52, 68), Color.White);
            _resetStatsBtn.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold);
            _resetStatsBtn.Click += (s, e) => { ResetPollingStats(); };
            _pollCard.Controls.Add(_resetStatsBtn);

            _rawHexLabel = new Label();
            _rawHexLabel.Text = "...";
            _rawHexLabel.Font = new Font("Consolas", 8.3f);
            _rawHexLabel.ForeColor = Color.FromArgb(148, 163, 184);
            _rawHexLabel.AutoSize = true;
            _pollCard.Controls.Add(_rawHexLabel);

            _leftCol.Controls.Add(_pollCard);
        }

        private void BuildRightColumn()
        {
            _rightCol = new Panel();
            _rightCol.BackColor = BackColor;

            _tabBar = new Panel();
            _tabBar.BackColor = Color.FromArgb(20, 24, 33);

            _tabMotorsBtn = CreateActionButton("Motors", Color.FromArgb(255, 82, 56), Color.White);
            _tabMotorsBtn.Click += (s, e) => { SelectTab(0); };

            _tabBleBtn = CreateActionButton("BLE", Color.FromArgb(31, 37, 50), Color.FromArgb(180, 192, 212));
            _tabBleBtn.Click += (s, e) => { SelectTab(1); };

            _tabPowerBtn = CreateActionButton("Power", Color.FromArgb(31, 37, 50), Color.FromArgb(180, 192, 212));
            _tabPowerBtn.Click += (s, e) => { SelectTab(2); };

            _tabTuneBtn = CreateActionButton("Tuning", Color.FromArgb(31, 37, 50), Color.FromArgb(180, 192, 212));
            _tabTuneBtn.Click += (s, e) => { SelectTab(3); };

            _tabTurboBtn = CreateActionButton("Turbo", Color.FromArgb(31, 37, 50), Color.FromArgb(180, 192, 212));
            _tabTurboBtn.Click += (s, e) => { SelectTab(4); };

            _tabDumpBtn = CreateActionButton("Dumps", Color.FromArgb(31, 37, 50), Color.FromArgb(180, 192, 212));
            _tabDumpBtn.Click += (s, e) => { SelectTab(5); };

            _tabBar.Controls.Add(_tabMotorsBtn);
            _tabBar.Controls.Add(_tabBleBtn);
            _tabBar.Controls.Add(_tabPowerBtn);
            _tabBar.Controls.Add(_tabTuneBtn);
            _tabBar.Controls.Add(_tabTurboBtn);
            _tabBar.Controls.Add(_tabDumpBtn);
            _rightCol.Controls.Add(_tabBar);

            BuildTabMotorsPage();
            BuildTabBlePage();
            BuildTabPowerPage();
            BuildTabTunePage();
            BuildTabTurboPage();
            BuildTabDumpPage();

            _rightCol.Controls.Add(_tabMotorsPage);
            _rightCol.Controls.Add(_tabBlePage);
            _rightCol.Controls.Add(_tabPowerPage);
            _rightCol.Controls.Add(_tabTunePage);
            _rightCol.Controls.Add(_tabTurboPage);
            _rightCol.Controls.Add(_tabDumpPage);
        }

        private void BuildTabMotorsPage()
        {
            _tabMotorsPage = CreateCardPanel();
            _tabMotorsPage.Size = new Size(S(500), S(560));
            int x = S(16);
            int y = S(14);

            _motorsSecHdr = MakeSectionHeader("", x, y);
            _tabMotorsPage.Controls.Add(_motorsSecHdr);
            y += S(24);

            _motorsHintLbl = new Label();
            _motorsHintLbl.Text = "";
            _motorsHintLbl.Font = new Font("Segoe UI", 8.4f);
            _motorsHintLbl.ForeColor = Color.FromArgb(156, 168, 188);
            _motorsHintLbl.Location = new Point(x, y);
            _motorsHintLbl.Size = new Size(S(460), S(38));
            _motorsHintLbl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabMotorsPage.Controls.Add(_motorsHintLbl);
            y += S(44);

            AddSliderRow(_tabMotorsPage, x, ref y, "", "100%",
                Color.FromArgb(255, 82, 56), 0, 100, 100, out _strongCapLbl, out _strongGainLabel, out _strongGainSlider);
            _strongGainSlider.ValueChanged += (s, e) => { OnGainSliderChanged(true); };

            AddSliderRow(_tabMotorsPage, x, ref y, "", "100%",
                Color.FromArgb(16, 185, 129), 0, 100, 100, out _weakCapLbl, out _weakGainLabel, out _weakGainSlider);
            _weakGainSlider.ValueChanged += (s, e) => { OnGainSliderChanged(false); };

            _syncMotorsCheck = new CheckBox();
            _syncMotorsCheck.Text = "";
            _syncMotorsCheck.Checked = false;
            _syncMotorsCheck.AutoSize = true;
            _syncMotorsCheck.ForeColor = Color.FromArgb(203, 213, 225);
            _syncMotorsCheck.Location = new Point(x, y);
            _tabMotorsPage.Controls.Add(_syncMotorsCheck);
            y += S(32);

            _applyGainBtn = CreateActionButton("", Color.FromArgb(16, 185, 129), Color.FromArgb(10, 20, 16));
            _applyGainBtn.Location = new Point(x, y);
            _applyGainBtn.Size = new Size(S(458), S(36));
            _applyGainBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _applyGainBtn.Click += (s, e) => { PushSettingsToDriver(I18n.T("Силу моторів збережено у драйвер!", "Motor gain saved to driver!", "Motorstärke im Treiber gespeichert!", "Сила моторов сохранена в драйвер!")); };
            _tabMotorsPage.Controls.Add(_applyGainBtn);
            y += S(48);

            _motorsTestSecHdr = MakeSectionHeader("", x, y);
            _tabMotorsPage.Controls.Add(_motorsTestSecHdr);
            y += S(26);

            _pulseBtn = CreateActionButton("Імпульс 1.5с", Color.FromArgb(51, 65, 85), Color.White);
            _pulseBtn.Location = new Point(x, y);
            _pulseBtn.Size = new Size(S(146), S(36));
            _pulseBtn.Click += (s, e) => { StartPulseTest(); };

            _sweepBtn = CreateActionButton("Хвиля BLE", Color.FromArgb(37, 99, 235), Color.White);
            _sweepBtn.Location = new Point(x + S(154), y);
            _sweepBtn.Size = new Size(S(146), S(36));
            _sweepBtn.Click += (s, e) => { StartSweepTest(); };

            _holdBtn = CreateActionButton("Утримувати", Color.FromArgb(51, 65, 85), Color.White);
            _holdBtn.Location = new Point(x + S(308), y);
            _holdBtn.Size = new Size(S(146), S(36));
            _holdBtn.Click += (s, e) => { ToggleHoldRumble(); };

            y += S(44);
            _stopBtn = CreateActionButton("СТОП ВІБРАЦІЯ", Color.FromArgb(185, 28, 28), Color.White);
            _stopBtn.Location = new Point(x, y);
            _stopBtn.Size = new Size(S(458), S(36));
            _stopBtn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _stopBtn.Click += (s, e) => { StopAllRumble(I18n.T("Вібрацію зупинено вручну.", "Rumble stopped manually.", "Vibration manuell gestoppt.", "Вибрация остановлена вручную.")); };

            _tabMotorsPage.Controls.Add(_pulseBtn);
            _tabMotorsPage.Controls.Add(_sweepBtn);
            _tabMotorsPage.Controls.Add(_holdBtn);
            _tabMotorsPage.Controls.Add(_stopBtn);
            y += S(48);

            _settingsSupportBanner = new Label();
            _settingsSupportBanner.Text = I18n.T("Очікування підключення драйвера 0xE2…", "Waiting for 0xE2 driver connection…", "Warte auf 0xE2-Treiberverbindung…", "Ожидание подключения драйвера 0xE2…");
            _settingsSupportBanner.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _settingsSupportBanner.ForeColor = Color.FromArgb(251, 191, 36);
            _settingsSupportBanner.Location = new Point(x, y);
            _settingsSupportBanner.Size = new Size(S(458), S(64));
            _settingsSupportBanner.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabMotorsPage.Controls.Add(_settingsSupportBanner);
        }

        private void BuildTabBlePage()
        {
            _tabBlePage = CreateCardPanel();
            _tabBlePage.Size = new Size(S(500), S(560));
            int x = S(14);
            int y = S(12);

            _bleSecHdr = MakeSectionHeader("", x, y);
            _tabBlePage.Controls.Add(_bleSecHdr);
            y += S(22);

            _bleKpiLabel = new Label();
            _bleKpiLabel.Text = "...";
            _bleKpiLabel.Font = new Font("Consolas", 8.8f, FontStyle.Bold);
            _bleKpiLabel.ForeColor = Color.FromArgb(110, 231, 183);
            _bleKpiLabel.Location = new Point(x, y);
            _bleKpiLabel.Size = new Size(S(468), S(22));
            _bleKpiLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabBlePage.Controls.Add(_bleKpiLabel);
            y += S(26);

            _blePauseBtn = CreateActionButton("", Color.FromArgb(44, 52, 68), Color.White);
            _blePauseBtn.Location = new Point(x, y);
            _blePauseBtn.Size = new Size(S(110), S(30));
            _blePauseBtn.Click += (s, e) =>
            {
                _blePaused = !_blePaused;
                _blePauseBtn.Text = _blePaused
                    ? I18n.T("Продовжити", "Resume", "Fortsetzen", "Продолжить")
                    : I18n.T("Пауза", "Pause", "Pause", "Пауза");
                _blePauseBtn.BackColor = _blePaused ? Color.FromArgb(245, 158, 11) : Color.FromArgb(44, 52, 68);
                _blePauseBtn.ForeColor = _blePaused ? Color.Black : Color.White;
            };

            _bleStressBtn = CreateActionButton("Стрес-тест BLE (хвиля)", Color.FromArgb(37, 99, 235), Color.White);
            _bleStressBtn.Location = new Point(x + S(118), y);
            _bleStressBtn.Size = new Size(S(210), S(30));
            _bleStressBtn.Click += (s, e) => { StartSweepTest(); };

            _bleResetBtn = CreateActionButton("Очистити", Color.FromArgb(44, 52, 68), Color.White);
            _bleResetBtn.Location = new Point(x + S(336), y);
            _bleResetBtn.Size = new Size(S(110), S(30));
            _bleResetBtn.Click += (s, e) =>
            {
                SeedInitialCharts();
                _bleChart.SetPoints(_bleSamples);
            };

            _tabBlePage.Controls.Add(_blePauseBtn);
            _tabBlePage.Controls.Add(_bleStressBtn);
            _tabBlePage.Controls.Add(_bleResetBtn);
            y += S(38);

            _bleChart = new BleMonitorPanel();
            _bleChart.Location = new Point(x, y);
            _bleChart.Size = new Size(_tabBlePage.Width - S(28), _tabBlePage.Height - y - S(12));
            _bleChart.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _bleChart.SetPoints(_bleSamples);
            _tabBlePage.Controls.Add(_bleChart);
        }

        private void BuildTabPowerPage()
        {
            _tabPowerPage = CreateCardPanel();
            _tabPowerPage.Size = new Size(S(500), S(560));
            int x = S(14);
            int y = S(12);

            _powerSecHdr = MakeSectionHeader("", x, y);
            _tabPowerPage.Controls.Add(_powerSecHdr);
            y += S(24);

            _powerSummaryLabel = new Label();
            _powerSummaryLabel.Text = "Заряд: очікування 0xE1 / Windows BLE Battery…\r\nДжерело: -- · Напруга: -- V · Струм моторів: 0 мА";
            _powerSummaryLabel.Font = new Font("Consolas", 9.0f, FontStyle.Bold);
            _powerSummaryLabel.ForeColor = Color.FromArgb(110, 231, 183);
            _powerSummaryLabel.Location = new Point(x, y);
            _powerSummaryLabel.Size = new Size(S(468), S(46));
            _powerSummaryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabPowerPage.Controls.Add(_powerSummaryLabel);
            y += S(52);

            _powerDrainTestBtn = CreateActionButton("Оновити з Windows BLE", Color.FromArgb(37, 99, 235), Color.White);
            _powerDrainTestBtn.Location = new Point(x, y);
            _powerDrainTestBtn.Size = new Size(S(210), S(32));
            _powerDrainTestBtn.Click += (s, e) => { ScanWindowsBatteryAsync(); };

            _powerResetBtn = CreateActionButton("Скинути історію графіка", Color.FromArgb(44, 52, 68), Color.White);
            _powerResetBtn.Location = new Point(x + S(218), y);
            _powerResetBtn.Size = new Size(S(220), S(32));
            _powerResetBtn.Click += (s, e) =>
            {
                SeedInitialCharts();
                _batteryChart.SetHistory(_batteryHistory);
            };

            _tabPowerPage.Controls.Add(_powerDrainTestBtn);
            _tabPowerPage.Controls.Add(_powerResetBtn);
            y += S(40);

            _batteryChart = new BatteryHistoryPanel();
            _batteryChart.Location = new Point(x, y);
            _batteryChart.Size = new Size(_tabPowerPage.Width - S(28), _tabPowerPage.Height - y - S(12));
            _batteryChart.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _batteryChart.SetHistory(_batteryHistory);
            _tabPowerPage.Controls.Add(_batteryChart);
        }

        private void BuildTabTunePage()
        {
            _tabTunePage = CreateCardPanel();
            _tabTunePage.Size = new Size(S(500), S(560));
            _tabTunePage.AutoScroll = true;
            int x = S(16);
            int y = S(14);

            _tuneSecHdr1 = MakeSectionHeader("", x, y);
            _tabTunePage.Controls.Add(_tuneSecHdr1);
            y += S(24);

            AddSliderRow(_tabTunePage, x, ref y, "", "0%",
                Color.FromArgb(255, 82, 56), 0, 25, 0, out _stickDzCapLbl, out _stickDzLabel, out _stickDzSlider);
            _stickDzSlider.ValueChanged += (s, e) => { _stickDzLabel.Text = _stickDzSlider.Value + "%"; MarkTuneDirty(); };

            AddSliderRow(_tabTunePage, x, ref y, "", "0%",
                Color.FromArgb(59, 130, 246), 0, 20, 0, out _trigDzCapLbl, out _trigDzLabel, out _trigDzSlider);
            _trigDzSlider.ValueChanged += (s, e) => { _trigDzLabel.Text = _trigDzSlider.Value + "%"; MarkTuneDirty(); };

            AddSliderRow(_tabTunePage, x, ref y, "", "100%",
                Color.FromArgb(16, 185, 129), 50, 100, 100, out _trigMaxCapLbl, out _trigMaxLabel, out _trigMaxSlider);
            _trigMaxSlider.ValueChanged += (s, e) => { _trigMaxLabel.Text = _trigMaxSlider.Value + "%"; MarkTuneDirty(); };

            _swapSticksCheck = new CheckBox();
            _swapSticksCheck.Text = "";
            _swapSticksCheck.AutoSize = true;
            _swapSticksCheck.Location = new Point(x, y);
            _swapSticksCheck.CheckedChanged += (s, e) => { MarkTuneDirty(); };
            _tabTunePage.Controls.Add(_swapSticksCheck);
            y += S(24);

            _invertLyCheck = new CheckBox();
            _invertLyCheck.Text = "";
            _invertLyCheck.AutoSize = true;
            _invertLyCheck.Location = new Point(x, y);
            _invertLyCheck.CheckedChanged += (s, e) => { MarkTuneDirty(); };
            _tabTunePage.Controls.Add(_invertLyCheck);

            _invertRyCheck = new CheckBox();
            _invertRyCheck.Text = "";
            _invertRyCheck.AutoSize = true;
            _invertRyCheck.Location = new Point(x + S(210), y);
            _invertRyCheck.CheckedChanged += (s, e) => { MarkTuneDirty(); };
            _tabTunePage.Controls.Add(_invertRyCheck);
            y += S(28);

            _tuneSecHdr2 = MakeSectionHeader("", x, y);
            _tabTunePage.Controls.Add(_tuneSecHdr2);
            y += S(24);

            _presetFastBtn = CreateActionButton("", Color.FromArgb(44, 52, 68), Color.White);
            _presetFastBtn.Location = new Point(x, y);
            _presetFastBtn.Size = new Size(S(144), S(30));
            _presetFastBtn.Click += (s, e) => { ApplyBlePreset(25, 6, 4); };

            _presetBalBtn = CreateActionButton("", Color.FromArgb(37, 99, 235), Color.White);
            _presetBalBtn.Location = new Point(x + S(150), y);
            _presetBalBtn.Size = new Size(S(144), S(30));
            _presetBalBtn.Click += (s, e) => { ApplyBlePreset(40, 12, 8); };

            _presetSmoothBtn = CreateActionButton("", Color.FromArgb(44, 52, 68), Color.White);
            _presetSmoothBtn.Location = new Point(x + S(300), y);
            _presetSmoothBtn.Size = new Size(S(146), S(30));
            _presetSmoothBtn.Click += (s, e) => { ApplyBlePreset(55, 16, 12); };

            _tabTunePage.Controls.Add(_presetFastBtn);
            _tabTunePage.Controls.Add(_presetBalBtn);
            _tabTunePage.Controls.Add(_presetSmoothBtn);
            y += S(38);

            AddSliderRow(_tabTunePage, x, ref y, "", "40 ms",
                Color.FromArgb(245, 158, 11), 15, 100, 40, out _bleIntCapLbl, out _bleIntervalLabel, out _bleIntervalSlider);
            _bleIntervalSlider.ValueChanged += (s, e) => { _bleIntervalLabel.Text = _bleIntervalSlider.Value + " " + I18n.T("мс", "ms", "ms", "мс"); MarkTuneDirty(); };

            AddSliderRow(_tabTunePage, x, ref y, "", "12 ms",
                Color.FromArgb(245, 158, 11), 0, 30, 12, out _bleGapCapLbl, out _bleGapLabel, out _bleGapSlider);
            _bleGapSlider.ValueChanged += (s, e) => { _bleGapLabel.Text = _bleGapSlider.Value + " " + I18n.T("мс", "ms", "ms", "мс"); MarkTuneDirty(); };

            AddSliderRow(_tabTunePage, x, ref y, "", "8",
                Color.FromArgb(245, 158, 11), 0, 20, 8, out _bleDeltaCapLbl, out _bleDeltaLabel, out _bleDeltaSlider);
            _bleDeltaSlider.ValueChanged += (s, e) => { _bleDeltaLabel.Text = _bleDeltaSlider.Value.ToString(); MarkTuneDirty(); };

            _applyAllBtn = CreateActionButton("", Color.FromArgb(16, 185, 129), Color.FromArgb(10, 20, 16));
            _applyAllBtn.Location = new Point(x, y);
            _applyAllBtn.Size = new Size(S(280), S(34));
            _applyAllBtn.Click += (s, e) => { PushSettingsToDriver(I18n.T("Усі налаштування застосовано і збережено в реєстр!", "All settings applied and saved to driver registry!", "Alle Einstellungen übernommen & in Registry gespeichert!", "Все настройки применены и сохранены в реестр!")); };

            _resetDefaultBtn = CreateActionButton("", Color.FromArgb(51, 65, 85), Color.White);
            _resetDefaultBtn.Location = new Point(x + S(290), y);
            _resetDefaultBtn.Size = new Size(S(156), S(34));
            _resetDefaultBtn.Click += (s, e) => { ResetSettingsDefaults(); };

            _tabTunePage.Controls.Add(_applyAllBtn);
            _tabTunePage.Controls.Add(_resetDefaultBtn);
            y += S(40);

            _exportPatchBtn = CreateActionButton("", Color.FromArgb(51, 65, 85), Color.White);
            _exportPatchBtn.Location = new Point(x, y);
            _exportPatchBtn.Size = new Size(S(220), S(32));
            _exportPatchBtn.Click += (s, e) => { ExportConfigPatchFiles(); };

            _openWebUsbBtn = CreateActionButton("", Color.FromArgb(37, 99, 235), Color.White);
            _openWebUsbBtn.Location = new Point(x + S(226), y);
            _openWebUsbBtn.Size = new Size(S(220), S(32));
            _openWebUsbBtn.Click += (s, e) =>
            {
                try
                {
                    Process.Start("https://ais-dev-n43yxlxrqzdy664v7gpick-1008996888225.europe-west2.run.app");
                    AddLog(I18n.T("Відкрито WebUSB-консоль Stadia Rumble Studio у браузері.", "Opened Stadia Rumble Studio WebUSB console in browser.", "Stadia Rumble Studio WebUSB-Konsole im Browser geöffnet.", "Открыта WebUSB-консоль Stadia Rumble Studio в браузере."));
                }
                catch (Exception ex)
                {
                    AddLog("Browser error: " + ex.Message);
                }
            };

            _tabTunePage.Controls.Add(_exportPatchBtn);
            _tabTunePage.Controls.Add(_openWebUsbBtn);
            y += S(38);

            _tuneStatusLabel = new Label();
            _tuneStatusLabel.Text = "";
            _tuneStatusLabel.Font = new Font("Segoe UI", 8.0f);
            _tuneStatusLabel.ForeColor = Color.FromArgb(148, 163, 184);
            _tuneStatusLabel.Location = new Point(x, y);
            _tuneStatusLabel.Size = new Size(S(450), S(38));
            _tabTunePage.Controls.Add(_tuneStatusLabel);
        }

        private void BuildTabDumpPage()
        {
            _tabDumpPage = CreateCardPanel();
            _tabDumpPage.Size = new Size(S(500), S(560));
            int x = S(14);
            int y = S(12);

            _dumpSecHdr = MakeSectionHeader("", x, y);
            _tabDumpPage.Controls.Add(_dumpSecHdr);
            y += S(22);

            _criticalGuideBox = new Panel();
            _criticalGuideBox.BackColor = Color.FromArgb(16, 42, 34);
            _criticalGuideBox.Location = new Point(x, y);
            _criticalGuideBox.Size = new Size(_tabDumpPage.Width - S(28), S(112));
            _criticalGuideBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _criticalGuideBox.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(52, 211, 153), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, _criticalGuideBox.Width - 1, _criticalGuideBox.Height - 1);
                }
            };

            _criticalGuideTitle = new Label();
            _criticalGuideTitle.Text = "СТАН ДРАЙВЕРА: КРИТИЧНИХ ПОМИЛОК НЕ ВИЯВЛЕНО";
            _criticalGuideTitle.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold);
            _criticalGuideTitle.ForeColor = Color.FromArgb(110, 231, 183);
            _criticalGuideTitle.Location = new Point(S(10), S(8));
            _criticalGuideTitle.Size = new Size(S(440), S(20));
            _criticalGuideTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _criticalGuideBox.Controls.Add(_criticalGuideTitle);

            _criticalGuideBody = new Label();
            _criticalGuideBody.Text = "Якщо виникне 'Critical Error' (0x80070005, розрив GATT, збій WUDFHost або затримка >45мс), тут одразу з'явиться чітка покрокова інструкція з виправлення.";
            _criticalGuideBody.Font = new Font("Segoe UI", 8.3f);
            _criticalGuideBody.ForeColor = Color.FromArgb(203, 213, 225);
            _criticalGuideBody.Location = new Point(S(10), S(30));
            _criticalGuideBody.Size = new Size(S(440), S(74));
            _criticalGuideBody.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _criticalGuideBox.Controls.Add(_criticalGuideBody);
            _tabDumpPage.Controls.Add(_criticalGuideBox);
            y += S(120);

            _scanDumpsBtn = CreateActionButton("Сканувати дампи Windows", Color.FromArgb(255, 82, 56), Color.White);
            _scanDumpsBtn.Size = new Size(S(172), S(28));
            _scanDumpsBtn.Location = new Point(x, y);
            _scanDumpsBtn.Click += (s, e) => { ScanWindowsDumpsAsync(); };

            _importDumpBtn = CreateActionButton("Аналіз .txt логу", Color.FromArgb(37, 99, 235), Color.White);
            _importDumpBtn.Size = new Size(S(136), S(28));
            _importDumpBtn.Location = new Point(x + S(178), y);
            _importDumpBtn.Click += (s, e) => { ImportAndAnalyzeDumpFile(); };

            _copyLogBtn = CreateActionButton("Копіювати", Color.FromArgb(44, 52, 68), Color.White);
            _copyLogBtn.Size = new Size(S(108), S(28));
            _copyLogBtn.Location = new Point(x + S(320), y);
            _copyLogBtn.Click += (s, e) => { CopyLogToClipboard(); };

            _tabDumpPage.Controls.Add(_scanDumpsBtn);
            _tabDumpPage.Controls.Add(_importDumpBtn);
            _tabDumpPage.Controls.Add(_copyLogBtn);
            y += S(34);

            _telemetryLabel = new Label();
            _telemetryLabel.Text = "Очікування зв'язку з драйвером winstadia.dll (0xE1)…";
            _telemetryLabel.Font = new Font("Consolas", 8.5f);
            _telemetryLabel.ForeColor = Color.FromArgb(203, 213, 225);
            _telemetryLabel.Location = new Point(x, y);
            _telemetryLabel.Size = new Size(S(468), S(54));
            _telemetryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabDumpPage.Controls.Add(_telemetryLabel);
            y += S(58);

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Color.FromArgb(13, 16, 22);
            _logBox.ForeColor = Color.FromArgb(186, 200, 220);
            _logBox.BorderStyle = BorderStyle.FixedSingle;
            _logBox.Font = new Font("Consolas", 8.2f);
            _logBox.Location = new Point(x, y);
            _logBox.Size = new Size(_tabDumpPage.Width - S(28), _tabDumpPage.Height - y - S(12));
            _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _tabDumpPage.Controls.Add(_logBox);
        }

        private void BuildTabTurboPage()
        {
            _tabTurboPage = CreateCardPanel();
            _tabTurboPage.Size = new Size(S(500), S(560));
            _tabTurboPage.AutoScroll = true;
            _tabTurboPage.AutoScrollMargin = new Size(0, S(16));
            int x = S(16);
            int y = S(14);
            int cardW = _tabTurboPage.Width - x * 2 - S(16);

            // 1. Standalone Driver Persistence Banner (0xE3 + 0xE2 in winstadia.dll)
            _macroDriverBox = new Panel();
            _macroDriverBox.BackColor = Color.FromArgb(16, 42, 34);
            _macroDriverBox.Location = new Point(x, y);
            _macroDriverBox.Size = new Size(cardW, S(94));
            _macroDriverBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _macroDriverBox.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(52, 211, 153), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, _macroDriverBox.Width - 1, _macroDriverBox.Height - 1);
                }
            };

            _macroDriverTitle = new Label();
            _macroDriverTitle.Text = "";
            _macroDriverTitle.Font = new Font("Segoe UI", 8.6f, FontStyle.Bold);
            _macroDriverTitle.ForeColor = Color.FromArgb(110, 231, 183);
            _macroDriverTitle.Location = new Point(S(10), S(8));
            _macroDriverTitle.Size = new Size(S(315), S(20));
            _macroDriverBox.Controls.Add(_macroDriverTitle);

            _turboAssistStateBadge = new Label();
            _turboAssistStateBadge.Text = "Assistant";
            _turboAssistStateBadge.Font = new Font("Consolas", 8.0f, FontStyle.Bold);
            _turboAssistStateBadge.TextAlign = ContentAlignment.MiddleCenter;
            _turboAssistStateBadge.BackColor = Color.FromArgb(38, 45, 60);
            _turboAssistStateBadge.ForeColor = Color.FromArgb(148, 163, 184);
            _turboAssistStateBadge.Size = new Size(S(116), S(20));
            _turboAssistStateBadge.Location = new Point(_macroDriverBox.Width - S(126), S(7));
            _turboAssistStateBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _macroDriverBox.Controls.Add(_turboAssistStateBadge);

            _macroDriverBody = new Label();
            _macroDriverBody.Text = "";
            _macroDriverBody.Font = new Font("Segoe UI", 8.0f);
            _macroDriverBody.ForeColor = Color.FromArgb(203, 213, 225);
            _macroDriverBody.Location = new Point(S(10), S(30));
            _macroDriverBody.Size = new Size(_macroDriverBox.Width - S(20), S(36));
            _macroDriverBody.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _macroDriverBox.Controls.Add(_macroDriverBody);

            _macroDriverStatusLbl = new Label();
            _macroDriverStatusLbl.Text = "";
            _macroDriverStatusLbl.Font = new Font("Segoe UI", 8.0f, FontStyle.Bold);
            _macroDriverStatusLbl.ForeColor = Color.FromArgb(148, 163, 184);
            _macroDriverStatusLbl.Location = new Point(S(10), S(68));
            _macroDriverStatusLbl.Size = new Size(_macroDriverBox.Width - S(20), S(18));
            _macroDriverStatusLbl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _macroDriverBox.Controls.Add(_macroDriverStatusLbl);

            _tabTurboPage.Controls.Add(_macroDriverBox);
            y += S(104);

            // 2. Driver-Level Windows Hotkey & Media Macros (0xE3 -> HID Keyboard/Consumer)
            _macrosSecHdr = MakeSectionHeader("", x, y);
            _tabTurboPage.Controls.Add(_macrosSecHdr);
            y += S(22);

            _macrosCard = new Panel();
            _macrosCard.BackColor = Color.FromArgb(26, 31, 43);
            _macrosCard.Location = new Point(x, y);
            _macrosCard.Size = new Size(cardW, S(268));
            _macrosCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _macrosCard.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(48, 58, 78), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, _macrosCard.Width - 1, _macrosCard.Height - 1);
                }
            };

            _macrosEnabledCheck = new CheckBox();
            _macrosEnabledCheck.Text = "";
            _macrosEnabledCheck.Checked = _appMacros.MacrosEnabled;
            _macrosEnabledCheck.AutoSize = true;
            _macrosEnabledCheck.ForeColor = Color.FromArgb(226, 232, 240);
            _macrosEnabledCheck.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _macrosEnabledCheck.Location = new Point(S(12), S(10));
            _macrosEnabledCheck.CheckedChanged += (s, e) =>
            {
                if (_suppressMacroEvents) return;
                lock (_macroLock)
                {
                    _appMacros.MacrosEnabled = _macrosEnabledCheck.Checked;
                    _appMacros.SaveToRegistry();
                }
                PushMacrosToDriver(true);
            };
            _macrosCard.Controls.Add(_macrosEnabledCheck);

            _assistActionCapLbl = new Label();
            _assistActionCapLbl.Text = "";
            _assistActionCapLbl.Font = new Font("Segoe UI", 8.2f);
            _assistActionCapLbl.ForeColor = Color.FromArgb(186, 200, 220);
            _assistActionCapLbl.Location = new Point(S(12), S(40));
            _assistActionCapLbl.Size = new Size(S(165), S(22));
            _macrosCard.Controls.Add(_assistActionCapLbl);

            _assistActionCombo = new ComboBox();
            _assistActionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _assistActionCombo.BackColor = Color.FromArgb(18, 22, 30);
            _assistActionCombo.ForeColor = Color.White;
            _assistActionCombo.FlatStyle = FlatStyle.Flat;
            _assistActionCombo.Font = new Font("Segoe UI", 8.3f);
            _assistActionCombo.Location = new Point(S(180), S(37));
            _assistActionCombo.Size = new Size(S(266), S(24));
            _assistActionCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressMacroEvents) return;
                lock (_macroLock)
                {
                    _appMacros.AssistantAction = (MacroAction)Native.ClampInt(_assistActionCombo.SelectedIndex, 0, 8);
                    _appMacros.SaveToRegistry();
                }
                _customKeyErrorText = "";
                RefreshCustomKeyUiState();
                PushMacrosToDriver(true);
            };
            _macrosCard.Controls.Add(_assistActionCombo);

            _captureActionCapLbl = new Label();
            _captureActionCapLbl.Text = "";
            _captureActionCapLbl.Font = new Font("Segoe UI", 8.2f);
            _captureActionCapLbl.ForeColor = Color.FromArgb(186, 200, 220);
            _captureActionCapLbl.Location = new Point(S(12), S(72));
            _captureActionCapLbl.Size = new Size(S(165), S(22));
            _macrosCard.Controls.Add(_captureActionCapLbl);

            _captureActionCombo = new ComboBox();
            _captureActionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _captureActionCombo.BackColor = Color.FromArgb(18, 22, 30);
            _captureActionCombo.ForeColor = Color.White;
            _captureActionCombo.FlatStyle = FlatStyle.Flat;
            _captureActionCombo.Font = new Font("Segoe UI", 8.3f);
            _captureActionCombo.Location = new Point(S(180), S(69));
            _captureActionCombo.Size = new Size(S(266), S(24));
            _captureActionCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressMacroEvents) return;
                lock (_macroLock)
                {
                    _appMacros.CaptureAction = (MacroAction)Native.ClampInt(_captureActionCombo.SelectedIndex, 0, 8);
                    _appMacros.SaveToRegistry();
                }
                _customKeyErrorText = "";
                RefreshCustomKeyUiState();
                PushMacrosToDriver(true);
            };
            _macrosCard.Controls.Add(_captureActionCombo);

            // Row 3: Dedicated crystal-clear Custom Hotkey row (Button capture + Quick preset picker)
            _customKeyCapLbl = new Label();
            _customKeyCapLbl.Text = "";
            _customKeyCapLbl.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold);
            _customKeyCapLbl.ForeColor = Color.FromArgb(186, 200, 220);
            _customKeyCapLbl.Location = new Point(S(12), S(104));
            _customKeyCapLbl.Size = new Size(S(165), S(22));
            _macrosCard.Controls.Add(_customKeyCapLbl);

            _customKeyBtn = CreateActionButton("", Color.FromArgb(37, 46, 63), Color.FromArgb(147, 197, 253));
            _customKeyBtn.Font = new Font("Consolas", 8.1f, FontStyle.Bold);
            _customKeyBtn.Location = new Point(S(180), S(101));
            _customKeyBtn.Size = new Size(S(162), S(26));
            _customKeyBtn.Click += (s, e) =>
            {
                _customKeyErrorText = "";
                if (_capturingCustomKey)
                {
                    CancelCustomKeyCapture();
                }
                else
                {
                    _capturingCustomKey = true;
                    RefreshCustomKeyUiState();
                }
            };
            _macrosCard.Controls.Add(_customKeyBtn);

            _customKeyPresetCombo = new ComboBox();
            _customKeyPresetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _customKeyPresetCombo.BackColor = Color.FromArgb(18, 22, 30);
            _customKeyPresetCombo.ForeColor = Color.FromArgb(203, 213, 225);
            _customKeyPresetCombo.FlatStyle = FlatStyle.Flat;
            _customKeyPresetCombo.Font = new Font("Segoe UI", 8.1f);
            _customKeyPresetCombo.Location = new Point(S(348), S(102));
            _customKeyPresetCombo.Size = new Size(S(98), S(25));
            _customKeyPresetCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressMacroEvents) return;
                int pIdx = _customKeyPresetCombo.SelectedIndex - 1;
                if (pIdx >= 0 && pIdx < _hotkeyPresets.Length)
                {
                    _capturingCustomKey = false;
                    _customKeyErrorText = "";
                    HotkeyPreset pr = _hotkeyPresets[pIdx];
                    lock (_macroLock)
                    {
                        _appMacros.CustomVk = pr.Vk;
                        _appMacros.CustomCtrl = pr.Ctrl;
                        _appMacros.CustomAlt = pr.Alt;
                        _appMacros.CustomShift = pr.Shift;
                        _appMacros.SaveToRegistry();
                    }
                    string lbl = Native.FormatHotkeyLabel(pr.Vk, pr.Ctrl, pr.Alt, pr.Shift);
                    RefreshCustomKeyUiState();
                    AddLog(string.Format(I18n.T("Власну клавішу макросу змінено: {0}", "Custom hotkey updated: {0}", "Eigener Hotkey gesetzt: {0}", "Собственная клавиша макроса изменена: {0}"), lbl));
                    PushMacrosToDriver(true);
                }
            };
            _macrosCard.Controls.Add(_customKeyPresetCombo);

            _customKeyHintLbl = new Label();
            _customKeyHintLbl.Text = "";
            _customKeyHintLbl.Font = new Font("Segoe UI", 7.9f);
            _customKeyHintLbl.ForeColor = Color.FromArgb(148, 163, 184);
            _customKeyHintLbl.Location = new Point(S(12), S(131));
            _customKeyHintLbl.Size = new Size(_macrosCard.Width - S(24), S(20));
            _customKeyHintLbl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _macrosCard.Controls.Add(_customKeyHintLbl);

            _dpadComboCheck = new CheckBox();
            _dpadComboCheck.Text = "";
            _dpadComboCheck.Checked = _appMacros.DpadMediaCombos;
            _dpadComboCheck.AutoSize = true;
            _dpadComboCheck.ForeColor = Color.FromArgb(186, 200, 220);
            _dpadComboCheck.Font = new Font("Segoe UI", 8.1f);
            _dpadComboCheck.Location = new Point(S(12), S(156));
            _dpadComboCheck.CheckedChanged += (s, e) =>
            {
                if (_suppressMacroEvents) return;
                lock (_macroLock)
                {
                    _appMacros.DpadMediaCombos = _dpadComboCheck.Checked;
                    _appMacros.SaveToRegistry();
                }
                PushMacrosToDriver(true);
            };
            _macrosCard.Controls.Add(_dpadComboCheck);

            _macroApplyBtn = CreateActionButton("", Color.FromArgb(16, 185, 129), Color.FromArgb(10, 20, 16));
            _macroApplyBtn.Font = new Font("Segoe UI", 8.3f, FontStyle.Bold);
            _macroApplyBtn.Location = new Point(S(12), S(184));
            _macroApplyBtn.Size = new Size(S(260), S(30));
            _macroApplyBtn.Click += (s, e) => { PushMacrosToDriver(false); };
            _macrosCard.Controls.Add(_macroApplyBtn);

            _macroLastFiredLabel = new Label();
            _macroLastFiredLabel.Text = "";
            _macroLastFiredLabel.Font = new Font("Consolas", 8.0f, FontStyle.Bold);
            _macroLastFiredLabel.ForeColor = Color.FromArgb(147, 197, 253);
            _macroLastFiredLabel.Location = new Point(S(12), S(220));
            _macroLastFiredLabel.Size = new Size(_macrosCard.Width - S(24), S(38));
            _macroLastFiredLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _macrosCard.Controls.Add(_macroLastFiredLabel);

            _tabTurboPage.Controls.Add(_macrosCard);
            y += S(278);

            // 3. Hardware Turbo inside winstadia.dll (Assistant + Button)
            _turboSecHdr = MakeSectionHeader("", x, y);
            _tabTurboPage.Controls.Add(_turboSecHdr);
            y += S(22);

            Panel turboCard = new Panel();
            turboCard.BackColor = Color.FromArgb(26, 31, 43);
            turboCard.Location = new Point(x, y);
            turboCard.Size = new Size(cardW, S(284));
            turboCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            turboCard.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(48, 58, 78), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, turboCard.Width - 1, turboCard.Height - 1);
                }
            };

            _turboHintLbl = new Label();
            _turboHintLbl.Text = "";
            _turboHintLbl.Font = new Font("Segoe UI", 8.2f);
            _turboHintLbl.ForeColor = Color.FromArgb(172, 184, 204);
            _turboHintLbl.Location = new Point(S(12), S(10));
            _turboHintLbl.Size = new Size(turboCard.Width - S(24), S(38));
            _turboHintLbl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            turboCard.Controls.Add(_turboHintLbl);

            _turboEnabledCheck = new CheckBox();
            _turboEnabledCheck.Text = "";
            _turboEnabledCheck.Checked = true;
            _turboEnabledCheck.AutoSize = true;
            _turboEnabledCheck.ForeColor = Color.FromArgb(226, 232, 240);
            _turboEnabledCheck.Location = new Point(S(12), S(54));
            _turboEnabledCheck.CheckedChanged += (s, e) =>
            {
                if (!_suppressSliderEvents)
                {
                    RefreshTurboPillVisuals();
                    if (_driverSettingsSupported) PushSettingsToDriver(I18n.T("Оновлено Turbo", "Turbo updated", "Turbo aktualisiert", "Обновлён Turbo"), true);
                }
            };
            turboCard.Controls.Add(_turboEnabledCheck);

            _turboHoldModeCheck = new CheckBox();
            _turboHoldModeCheck.Text = "";
            _turboHoldModeCheck.Checked = false;
            _turboHoldModeCheck.AutoSize = true;
            _turboHoldModeCheck.ForeColor = Color.FromArgb(186, 200, 220);
            _turboHoldModeCheck.Location = new Point(S(12), S(78));
            _turboHoldModeCheck.CheckedChanged += (s, e) =>
            {
                if (!_suppressSliderEvents)
                {
                    RefreshTurboPillVisuals();
                    if (_driverSettingsSupported) PushSettingsToDriver(I18n.T("Змінено режим Turbo", "Turbo mode changed", "Turbo-Modus geändert", "Изменён режим Turbo"), true);
                }
            };
            turboCard.Controls.Add(_turboHoldModeCheck);

            int ty = S(108);
            AddSliderRow(turboCard, S(12), ref ty, "", "12 Hz",
                Color.FromArgb(255, 82, 56), 5, 25, 12, out _turboHzCapLbl, out _turboHzLabel, out _turboHzSlider);
            _turboHzSlider.ValueChanged += (s, e) =>
            {
                _turboHzLabel.Text = _turboHzSlider.Value + " " + I18n.T("Гц", "Hz", "Hz", "Гц");
                if (!_suppressSliderEvents)
                {
                    RefreshTurboPillVisuals();
                    if (_turboHwStatusLabel != null)
                    {
                        _turboHwStatusLabel.Text = I18n.T(
                            "Частоту Turbo змінено — натисніть «Зберегти Turbo у драйвер», щоб застосувати.",
                            "Turbo speed changed — click \"Save Turbo to driver\" to apply.",
                            "Turbo-Rate geändert — klicken Sie auf „Turbo im Treiber speichern“.",
                            "Частота Turbo изменена — нажмите «Сохранить Turbo в драйвер», чтобы применить.");
                        _turboHwStatusLabel.ForeColor = Color.FromArgb(251, 191, 36);
                    }
                }
            };

            _turboBtnButtons = new Button[DriverSettings.TurboBtnNames.Length];
            int pillStartX = S(12);
            int pillY = S(162);
            int pillW = S(39);
            int pillH = S(28);
            int pillGap = S(4);
            for (int i = 0; i < DriverSettings.TurboBtnNames.Length; i++)
            {
                int bitIdx = i;
                Button b = CreateActionButton(DriverSettings.TurboBtnNames[i], Color.FromArgb(35, 42, 56), Color.FromArgb(203, 213, 225));
                b.Font = new Font("Segoe UI", 8.0f, FontStyle.Bold);
                b.Location = new Point(pillStartX + i * (pillW + pillGap), pillY);
                b.Size = new Size(pillW, pillH);
                b.Click += (s, e) =>
                {
                    _turboMaskLocal ^= (1 << bitIdx);
                    RefreshTurboPillVisuals();
                    PushSettingsToDriver(I18n.T("Оновлено Turbo-кнопки", "Turbo buttons updated", "Turbo-Tasten aktualisiert", "Обновлены Turbo-кнопки"), true);
                };
                _turboBtnButtons[i] = b;
                turboCard.Controls.Add(b);
            }

            _turboApplyBtn = CreateActionButton("", Color.FromArgb(255, 82, 56), Color.White);
            _turboApplyBtn.Location = new Point(S(12), S(202));
            _turboApplyBtn.Size = new Size(S(216), S(32));
            _turboApplyBtn.Click += (s, e) =>
            {
                PushSettingsToDriver(I18n.T("Збережено налаштування Turbo у драйвер", "Turbo settings saved to driver", "Turbo-Einstellungen im Treiber gespeichert", "Настройки Turbo сохранены в драйвер"), false);
                if (_turboHwStatusLabel != null)
                {
                    _turboHwStatusLabel.Text = I18n.T(
                        "Налаштування Turbo збережено у драйвері (0xE2) і працюють після повного закриття програми.",
                        "Turbo settings persisted inside driver (0xE2) — safe to close this window.",
                        "Turbo-Einstellungen im Treiber (0xE2) gespeichert — Fenster kann geschlossen werden.",
                        "Настройки Turbo сохранены в драйвере (0xE2) и работают после полного закрытия программы.");
                    _turboHwStatusLabel.ForeColor = Color.FromArgb(110, 231, 183);
                }
            };
            turboCard.Controls.Add(_turboApplyBtn);

            _turboClearBtn = CreateActionButton("", Color.FromArgb(51, 65, 85), Color.White);
            _turboClearBtn.Location = new Point(S(236), S(202));
            _turboClearBtn.Size = new Size(S(168), S(32));
            _turboClearBtn.Click += (s, e) =>
            {
                _turboMaskLocal = 0;
                RefreshTurboPillVisuals();
                PushSettingsToDriver(I18n.T("Вимкнено всі Turbo-кнопки", "All Turbo buttons cleared", "Alle Turbo-Tasten deaktiviert", "Отключены все Turbo-кнопки"), true);
            };
            turboCard.Controls.Add(_turboClearBtn);

            _turboLiveLabel = new Label();
            _turboLiveLabel.Text = "";
            _turboLiveLabel.Font = new Font("Consolas", 8.3f, FontStyle.Bold);
            _turboLiveLabel.ForeColor = Color.FromArgb(148, 163, 184);
            _turboLiveLabel.Location = new Point(S(12), S(246));
            _turboLiveLabel.Size = new Size(turboCard.Width - S(24), S(24));
            _turboLiveLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            turboCard.Controls.Add(_turboLiveLabel);

            _tabTurboPage.Controls.Add(turboCard);
            y += S(294);

            _turboHwStatusLabel = new Label();
            _turboHwStatusLabel.Text = "";
            _turboHwStatusLabel.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold);
            _turboHwStatusLabel.ForeColor = Color.FromArgb(110, 231, 183);
            _turboHwStatusLabel.Location = new Point(x, y);
            _turboHwStatusLabel.Size = new Size(cardW, S(46));
            _turboHwStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabTurboPage.Controls.Add(_turboHwStatusLabel);

            RefreshTurboPillVisuals();
        }

        private void CancelCustomKeyCapture()
        {
            if (!_capturingCustomKey) return;
            _capturingCustomKey = false;
            RefreshCustomKeyUiState();
        }

        private void MarkTuneDirty()
        {
            if (_suppressSliderEvents || _tuneStatusLabel == null) return;
            _tuneStatusLabel.Text = I18n.T(
                "Є незбережені зміни — натисніть «Застосувати і зберегти (0xE2)».",
                "Unsaved changes — click \"Apply & save to driver (0xE2)\" to persist.",
                "Ungespeicherte Änderungen — auf „Übernehmen & speichern (0xE2)“ klicken.",
                "Есть несохранённые изменения — нажмите «Применить и сохранить (0xE2)».");
            _tuneStatusLabel.ForeColor = Color.FromArgb(251, 191, 36);
        }

        private void RefreshCustomKeyUiState()
        {
            if (_customKeyBtn == null || _customKeyHintLbl == null) return;
            ushort vk;
            bool ctrl, alt, shift;
            MacroAction assistAct, capAct;
            lock (_macroLock)
            {
                vk = _appMacros.CustomVk;
                ctrl = _appMacros.CustomCtrl;
                alt = _appMacros.CustomAlt;
                shift = _appMacros.CustomShift;
                assistAct = _appMacros.AssistantAction;
                capAct = _appMacros.CaptureAction;
            }

            bool customUsed = (assistAct == MacroAction.CustomHotkey || capAct == MacroAction.CustomHotkey);
            string hkLabel = Native.FormatHotkeyLabel(vk, ctrl, alt, shift);

            if (_capturingCustomKey)
            {
                _customKeyBtn.Text = I18n.T("⌨ Натисніть клавішу…", "⌨ Press any key…", "⌨ Taste drücken…", "⌨ Нажмите клавишу…");
                _customKeyBtn.BackColor = Color.FromArgb(37, 99, 235);
                _customKeyBtn.ForeColor = Color.White;
                _customKeyHintLbl.Text = I18n.T(
                    "Натисніть клавішу або Ctrl/Alt/Shift + клавішу зараз (Esc — скасувати, або виберіть зі списку справа).",
                    "Press a key or Ctrl/Alt/Shift + key now (Esc = cancel, or choose a preset on the right).",
                    "Jetzt Taste oder Ctrl/Alt/Shift + Taste drücken (Esc = Abbruch, oder Preset rechts wählen).",
                    "Нажмите клавишу или Ctrl/Alt/Shift + клавишу сейчас (Esc — отмена, или выберите пресет справа).");
                _customKeyHintLbl.ForeColor = Color.FromArgb(147, 197, 253);
                return;
            }

            _customKeyBtn.Text = string.Format("⌨ {0} ({1})", hkLabel, I18n.T("Змінити", "Set", "Ändern", "Сменить"));
            if (customUsed)
            {
                _customKeyBtn.BackColor = Color.FromArgb(20, 62, 50);
                _customKeyBtn.ForeColor = Color.FromArgb(110, 231, 183);
                if (_customKeyCapLbl != null) _customKeyCapLbl.ForeColor = Color.FromArgb(110, 231, 183);
            }
            else
            {
                _customKeyBtn.BackColor = Color.FromArgb(37, 46, 63);
                _customKeyBtn.ForeColor = Color.FromArgb(147, 197, 253);
                if (_customKeyCapLbl != null) _customKeyCapLbl.ForeColor = Color.FromArgb(186, 200, 220);
            }

            if (!string.IsNullOrEmpty(_customKeyErrorText))
            {
                _customKeyHintLbl.Text = _customKeyErrorText;
                _customKeyHintLbl.ForeColor = Color.FromArgb(251, 191, 36);
            }
            else if (customUsed)
            {
                string who = (assistAct == MacroAction.CustomHotkey && capAct == MacroAction.CustomHotkey)
                    ? "Assistant + Capture"
                    : (assistAct == MacroAction.CustomHotkey ? "Assistant" : "Capture");
                _customKeyHintLbl.Text = string.Format(I18n.T(
                    "✓ Активно для {0}: {1} · Клікніть зліва для запису або оберіть пресет справа.",
                    "✓ Active for {0}: {1} · Click button to record key or pick preset on the right.",
                    "✓ Aktiv für {0}: {1} · Links klicken zum Aufnehmen oder Preset rechts wählen.",
                    "✓ Активно для {0}: {1} · Нажмите кнопку слева для записи или выберите пресет справа."),
                    who, hkLabel);
                _customKeyHintLbl.ForeColor = Color.FromArgb(110, 231, 183);
            }
            else
            {
                _customKeyHintLbl.Text = I18n.T(
                    "Підказка: оберіть «Власна клавіша (Custom)» у списку Assistant або Capture вище, щоб задіяти цей хоткей.",
                    "Tip: select \"Custom Hotkey\" in Assistant or Capture above to trigger this key.",
                    "Tipp: Wählen Sie oben bei Assistant oder Capture „Eigener Hotkey“, um diese Taste zu nutzen.",
                    "Подсказка: выберите «Своя клавиша (Custom)» в списке Assistant или Capture выше, чтобы задействовать хоткей.");
                _customKeyHintLbl.ForeColor = Color.FromArgb(148, 163, 184);
            }

            if (_customKeyPresetCombo != null && _customKeyPresetCombo.Items.Count > 0)
            {
                int matchIdx = 0;
                for (int i = 0; i < _hotkeyPresets.Length; i++)
                {
                    if (_hotkeyPresets[i].Vk == vk && _hotkeyPresets[i].Ctrl == ctrl &&
                        _hotkeyPresets[i].Alt == alt && _hotkeyPresets[i].Shift == shift)
                    {
                        matchIdx = i + 1;
                        break;
                    }
                }
                bool prevSup = _suppressMacroEvents;
                _suppressMacroEvents = true;
                _customKeyPresetCombo.SelectedIndex = matchIdx;
                _suppressMacroEvents = prevSup;
            }
        }

        private bool HandleCapturedCustomKey(Keys keyCode, bool ctrl, bool alt, bool shift)
        {
            if (!_capturingCustomKey) return false;
            if (keyCode == Keys.ControlKey || keyCode == Keys.ShiftKey || keyCode == Keys.Menu ||
                keyCode == Keys.LControlKey || keyCode == Keys.RControlKey ||
                keyCode == Keys.LShiftKey || keyCode == Keys.RShiftKey ||
                keyCode == Keys.LMenu || keyCode == Keys.RMenu ||
                keyCode == Keys.LWin || keyCode == Keys.RWin || keyCode == Keys.None)
            {
                return false;
            }
            if (keyCode == Keys.Escape && !ctrl && !alt && !shift)
            {
                _customKeyErrorText = "";
                CancelCustomKeyCapture();
                return true;
            }

            ushort vk = (ushort)((int)keyCode & 0xFF);
            byte hidUsage = Native.VkToHidUsage(vk);
            if (hidUsage == 0)
            {
                _capturingCustomKey = false;
                string keyName = Native.FormatVkKeyName(vk);
                _customKeyErrorText = string.Format(I18n.T(
                    "Клавіша '{0}' не має HID-коду клавіатури — натисніть іншу або оберіть зі списку справа.",
                    "Key '{0}' has no standard HID keyboard scancode — press another or pick a preset.",
                    "Taste '{0}' hat keinen Standard-HID-Scancode — andere Taste oder Preset wählen.",
                    "Клавиша '{0}' не имеет HID-кода клавиатуры — нажмите другую или выберите пресет справа."), keyName);
                RefreshCustomKeyUiState();
                AddLog("[MACRO] " + _customKeyErrorText);
                return true;
            }

            _capturingCustomKey = false;
            _customKeyErrorText = "";
            lock (_macroLock)
            {
                _appMacros.CustomVk = vk;
                _appMacros.CustomCtrl = ctrl;
                _appMacros.CustomAlt = alt;
                _appMacros.CustomShift = shift;
                _appMacros.SaveToRegistry();
            }
            string lbl = Native.FormatHotkeyLabel(vk, ctrl, alt, shift);
            RefreshCustomKeyUiState();
            AddLog(string.Format(I18n.T("Власну клавішу макросу змінено: {0}", "Custom hotkey updated: {0}", "Eigener Hotkey gesetzt: {0}", "Собственная клавиша макроса изменена: {0}"), lbl));
            PushMacrosToDriver(true);
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_capturingCustomKey)
            {
                Keys keyCode = keyData & Keys.KeyCode;
                bool ctrl = (keyData & Keys.Control) != 0;
                bool alt = (keyData & Keys.Alt) != 0;
                bool shift = (keyData & Keys.Shift) != 0;
                if (HandleCapturedCustomKey(keyCode, ctrl, alt, shift))
                {
                    return true;
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (HandleCapturedCustomKey(e.KeyCode, e.Control, e.Alt, e.Shift))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void RefreshTurboPillVisuals()
        {
            if (_turboBtnButtons == null) return;
            for (int i = 0; i < _turboBtnButtons.Length; i++)
            {
                bool on = (_turboMaskLocal & (1 << i)) != 0;
                _turboBtnButtons[i].BackColor = on ? Color.FromArgb(16, 185, 129) : Color.FromArgb(35, 42, 56);
                _turboBtnButtons[i].ForeColor = on ? Color.FromArgb(10, 20, 16) : Color.FromArgb(203, 213, 225);
            }

            if (_turboLiveLabel != null)
            {
                string modeStr = (_turboHoldModeCheck != null && _turboHoldModeCheck.Checked) ? "Hold" : "Toggle";
                int hz = _turboHzSlider != null ? _turboHzSlider.Value : 12;
                if (_turboEnabledCheck != null && !_turboEnabledCheck.Checked)
                {
                    _turboLiveLabel.Text = I18n.T(
                        "Апаратний Turbo вимкнено в драйвері",
                        "Hardware Turbo disabled in driver",
                        "Hardware-Turbo im Treiber deaktiviert",
                        "Аппаратный Turbo отключён в драйвере");
                    _turboLiveLabel.ForeColor = Color.FromArgb(148, 163, 184);
                }
                else
                {
                    string list = DriverSettings.FormatTurboMask(_turboMaskLocal);
                    string prefix = I18n.T("Активні Turbo", "Active Turbo", "Aktives Turbo", "Активные Turbo");
                    string hzUnit = I18n.T("Гц", "Hz", "Hz", "Гц");
                    _turboLiveLabel.Text = string.Format("{0}: {1} ({2} · {3} {4})", prefix, list, modeStr, hz, hzUnit);
                    _turboLiveLabel.ForeColor = (_turboMaskLocal != 0)
                        ? Color.FromArgb(110, 231, 183)
                        : Color.FromArgb(148, 163, 184);
                }
            }
        }

        private void AddSliderRow(Panel parent, int x, ref int y, string caption, string initVal, Color col,
            int min, int max, int val, out Label capLbl, out Label valLbl, out DarkSlider slider)
        {
            capLbl = new Label();
            capLbl.Text = caption;
            capLbl.AutoSize = true;
            capLbl.Font = new Font("Segoe UI", 8.8f);
            capLbl.ForeColor = Color.FromArgb(215, 223, 235);
            capLbl.Location = new Point(x, y);
            parent.Controls.Add(capLbl);

            valLbl = new Label();
            valLbl.Text = initVal;
            valLbl.Font = new Font("Consolas", 9.2f, FontStyle.Bold);
            valLbl.ForeColor = col;
            valLbl.TextAlign = ContentAlignment.MiddleRight;
            valLbl.Size = new Size(S(70), S(20));
            valLbl.Location = new Point(parent.Width - S(86), y);
            valLbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            parent.Controls.Add(valLbl);

            slider = new DarkSlider();
            slider.Minimum = min;
            slider.Maximum = max;
            slider.Value = val;
            slider.FillColor = col;
            slider.Location = new Point(x, y + S(20));
            slider.Size = new Size(parent.Width - x * 2, S(24));
            slider.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(slider);

            y += S(50);
        }

        private Label MakeSectionHeader(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 8.1f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(148, 163, 184);
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        private void LayoutMainSplit()
        {
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            if (cw < 400 || ch < 300) return;

            int hh = Math.Max(S(74), _subLabel.Bottom + S(12));
            _headerPanel.Bounds = new Rectangle(0, 0, cw, hh);

            int padX = S(16);
            int topTitle = S(12);
            _titleLabel.Location = new Point(padX, topTitle);
            _subLabel.Location = new Point(padX, _titleLabel.Bottom + S(2));
            hh = Math.Max(hh, _subLabel.Bottom + S(12));
            _headerPanel.Height = hh;

            int btnH = S(36);
            int btnY = (hh - btnH) / 2;
            int rightX = cw - padX;

            int wSave = S(138);
            rightX -= wSave;
            _saveLogBtn.Bounds = new Rectangle(rightX, btnY, wSave, btnH);
            rightX -= S(5);

            int wDiag = S(114);
            rightX -= wDiag;
            _autoDiagBtn.Bounds = new Rectangle(rightX, btnY, wDiag, btnH);
            rightX -= S(5);

            int wCrit = S(96);
            rightX -= wCrit;
            _criticalBtn.Bounds = new Rectangle(rightX, btnY, wCrit, btnH);
            rightX -= S(5);

            int wBat = S(92);
            rightX -= wBat;
            _batteryPill.Bounds = new Rectangle(rightX, btnY, wBat, btnH);
            rightX -= S(5);

            int wPill = S(144);
            rightX -= wPill;
            _statusPill.Bounds = new Rectangle(rightX, btnY, wPill, btnH);
            rightX -= S(5);

            int wSlot = S(92);
            rightX -= wSlot;
            int slotY = (hh - _slotBox.Height) / 2;
            _slotBox.Bounds = new Rectangle(rightX, slotY, wSlot, _slotBox.Height);
            rightX -= S(5);

            int wLang = S(62);
            rightX -= wLang;
            int langY = (hh - _langBox.Height) / 2;
            _langBox.Bounds = new Rectangle(rightX, langY, wLang, _langBox.Height);

            _subLabel.MaximumSize = new Size(Math.Max(S(150), rightX - padX - S(10)), 0);
            _headerPanel.Height = Math.Max(hh, _subLabel.Bottom + S(12));

            int pad = S(12);
            int bodyTop = _headerPanel.Height + pad;
            int bodyH = Math.Max(200, ch - bodyTop - pad);
            int leftW = (int)((cw - pad * 3) * 0.49f);
            int rightW = cw - pad * 3 - leftW;

            _leftCol.Bounds = new Rectangle(pad, bodyTop, leftW, bodyH);
            _rightCol.Bounds = new Rectangle(pad * 2 + leftW, bodyTop, rightW, bodyH);

            int pollH = S(78);
            _canvas.Bounds = new Rectangle(0, 0, leftW, Math.Max(120, bodyH - pollH - S(10)));
            _pollCard.Bounds = new Rectangle(0, _canvas.Bottom + S(10), leftW, pollH);

            _pollingLabel.Location = new Point(S(12), S(12));
            _rawHexLabel.Location = new Point(S(12), S(42));
            _resetStatsBtn.Bounds = new Rectangle(leftW - S(94), S(9), S(82), S(26));

            int tabH = S(38);
            _tabBar.Bounds = new Rectangle(0, 0, rightW, tabH);
            int tGap = S(4);
            int tW = (rightW - tGap * 5) / 6;
            _tabMotorsBtn.Bounds = new Rectangle(0, 0, tW, tabH);
            _tabBleBtn.Bounds = new Rectangle(tW + tGap, 0, tW, tabH);
            _tabPowerBtn.Bounds = new Rectangle((tW + tGap) * 2, 0, tW, tabH);
            _tabTuneBtn.Bounds = new Rectangle((tW + tGap) * 3, 0, tW, tabH);
            _tabTurboBtn.Bounds = new Rectangle((tW + tGap) * 4, 0, tW, tabH);
            _tabDumpBtn.Bounds = new Rectangle((tW + tGap) * 5, 0, rightW - (tW + tGap) * 5, tabH);

            Rectangle pageRect = new Rectangle(0, tabH + S(10), rightW, Math.Max(120, bodyH - tabH - S(10)));
            _tabMotorsPage.Bounds = pageRect;
            _tabBlePage.Bounds = pageRect;
            _tabPowerPage.Bounds = pageRect;
            _tabTunePage.Bounds = pageRect;
            _tabTurboPage.Bounds = pageRect;
            _tabDumpPage.Bounds = pageRect;
        }

        private void SelectTab(int idx)
        {
            CancelCustomKeyCapture();
            _activeTab = idx;
            _tabMotorsPage.Visible = (idx == 0);
            _tabBlePage.Visible = (idx == 1);
            _tabPowerPage.Visible = (idx == 2);
            _tabTunePage.Visible = (idx == 3);
            _tabTurboPage.Visible = (idx == 4);
            _tabDumpPage.Visible = (idx == 5);

            StyleTabButton(_tabMotorsBtn, idx == 0);
            StyleTabButton(_tabBleBtn, idx == 1);
            StyleTabButton(_tabPowerBtn, idx == 2);
            StyleTabButton(_tabTuneBtn, idx == 3);
            StyleTabButton(_tabTurboBtn, idx == 4);
            StyleTabButton(_tabDumpBtn, idx == 5);
        }

        private static void StyleTabButton(Button b, bool active)
        {
            b.BackColor = active ? Color.FromArgb(255, 82, 56) : Color.FromArgb(26, 31, 43);
            b.ForeColor = active ? Color.White : Color.FromArgb(165, 178, 200);
        }

        private static Panel CreateCardPanel()
        {
            Panel p = new Panel();
            p.BackColor = Color.FromArgb(20, 24, 33);
            p.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(40, 48, 64), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
                }
            };
            return p;
        }

        private Button CreateActionButton(string text, Color bg, Color fg)
        {
            Button b = new Button();
            b.Text = text;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Segoe UI", 8.4f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            b.UseCompatibleTextRendering = false;
            return b;
        }

        private void AddLog(string line)
        {
            string entry = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss.fff"), line);
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLogUi), entry); } catch { }
            }
            else
            {
                AppendLogUi(entry);
            }
        }

        private void AppendLogUi(string entry)
        {
            lock (_logLock)
            {
                _logLines.Add(entry);
                if (_logLines.Count > 600)
                {
                    _logLines.RemoveRange(0, _logLines.Count - 600);
                }
                _logBox.Text = string.Join(Environment.NewLine, _logLines.ToArray());
            }
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private void ResetPollingStats()
        {
            lock (_stateLock)
            {
                _lastPacketTicks = 0;
                _packetsInWindow = 0;
                _windowStartTicks = Stopwatch.GetTimestamp();
                _measuredHz = 0;
                _lastActiveHz = 0;
                _lastIntervalMs = 0;
                _avgIntervalMs = 0;
                _maxIntervalMs = 0;
                _spikeCount = 0;
            }
            AddLog(I18n.T("Лічильники опитування та мікрофризів скинуто.", "Polling & latency spike counters reset.", "Abfrage- und Latenz-Zähler zurückgesetzt.", "Счётчики опроса и микрофризов сброшены."));
        }

        private void PollWorker()
        {
            long freq = Stopwatch.Frequency;
            _windowStartTicks = Stopwatch.GetTimestamp();
            bool prevConnected = false;
            double pendingSpikeMs = 0;

            while (!_stopPollEvent.WaitOne(2))
            {
                int slot;
                lock (_stateLock) { slot = _activeSlot; }

                Native.XInputState st;
                uint res = Native.GetState((uint)slot, out st);
                bool conn = (res == Native.ErrorSuccess);
                long now = Stopwatch.GetTimestamp();

                if (conn != prevConnected)
                {
                    prevConnected = conn;
                    pendingSpikeMs = 0;
                    if (!conn) _settingsLoadedFromDriver = false;
                    AddLog(conn
                        ? string.Format(I18n.T("Контролер підключено в слот XInput #{0}.", "Controller connected to XInput slot #{0}.", "Controller an XInput-Slot #{0} verbunden.", "Контроллер подключён в слот XInput #{0}."), slot + 1)
                        : string.Format(I18n.T("Контролер відключено зі слоту XInput #{0}.", "Controller disconnected from XInput slot #{0}.", "Controller von XInput-Slot #{0} getrennt.", "Контроллер отключён из слота XInput #{0}."), slot + 1));
                }

                bool spikeDetected = false;
                double spikeMs = 0;
                uint spikeWrites = 0;
                uint spikeFails = 0;

                lock (_stateLock)
                {
                    _padConnected = conn;
                    if (conn)
                    {
                        _latestState = st;
                        if (st.PacketNumber != _lastPacketNum)
                        {
                            if (_lastPacketTicks != 0)
                            {
                                double dtMs = ((now - _lastPacketTicks) * 1000.0) / freq;
                                double prevDt = _lastIntervalMs;
                                // Gaps >= 180 ms over BLE indicate controller idle/resting state
                                if (dtMs < 180.0)
                                {
                                    // Commit candidate lag only if the stream immediately resumes fast (< 35 ms),
                                    // proving a genuine mid-motion stall rather than a finger pause.
                                    if (pendingSpikeMs > 0.0 && dtMs < 35.0)
                                    {
                                        _spikeCount++;
                                        spikeDetected = true;
                                        spikeMs = pendingSpikeMs;
                                        spikeWrites = _latestTelemetry.RumbleWrites;
                                        spikeFails = _latestTelemetry.RumbleFailures;
                                        pendingSpikeMs = 0;
                                    }
                                    else if (dtMs > 45.0 && prevDt > 0.0 && prevDt < 35.0)
                                    {
                                        pendingSpikeMs = dtMs;
                                    }
                                    else
                                    {
                                        pendingSpikeMs = 0;
                                    }

                                    _lastIntervalMs = dtMs;
                                    if (_avgIntervalMs <= 0.0) _avgIntervalMs = dtMs;
                                    else _avgIntervalMs = _avgIntervalMs * 0.90 + dtMs * 0.10;
                                    if (dtMs > _maxIntervalMs) _maxIntervalMs = dtMs;
                                }
                                else
                                {
                                    pendingSpikeMs = 0;
                                }
                            }
                            _lastPacketNum = st.PacketNumber;
                            _lastPacketTicks = now;
                            _packetsInWindow++;
                        }

                        double winSec = (double)(now - _windowStartTicks) / freq;
                        if (winSec >= 0.5)
                        {
                            _measuredHz = _packetsInWindow / winSec;
                            if (_measuredHz > 1.0) _lastActiveHz = _measuredHz;
                            _packetsInWindow = 0;
                            _windowStartTicks = now;
                        }
                    }
                    else
                    {
                        _measuredHz = 0;
                        _lastPacketTicks = 0;
                        pendingSpikeMs = 0;
                    }
                }

                if (spikeDetected)
                {
                    AddLog(string.Format(I18n.T(
                        "[SPIKE] Затримка активного вводу {0:F1} мс (Writes={1}, Fails={2})",
                        "[SPIKE] Active input spike {0:F1} ms (Writes={1}, Fails={2})",
                        "[SPIKE] Aktive Eingabeverzögerung {0:F1} ms (Writes={1}, Fails={2})",
                        "[SPIKE] Задержка активного ввода {0:F1} мс (Writes={1}, Fails={2})"),
                        spikeMs, spikeWrites, spikeFails));
                }
            }
        }

        private void TelemetryWorker()
        {
            while (true)
            {
                int waitMs = _padConnected ? 35 : 200;
                if (_stopPollEvent.WaitOne(waitMs)) break;
                ReadTelemetryAndSettings();
            }
        }

        private void ReadTelemetryAndSettings()
        {
            if (_hidHandle == IntPtr.Zero)
            {
                _hidHandle = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
            }
            if (_hidHandle == IntPtr.Zero)
            {
                lock (_stateLock)
                {
                    _latestTelemetry = new DriverTelemetry();
                    _driverSettingsSupported = false;
                    _driverMacroSupported = false;
                    _settingsLoadedFromDriver = false;
                    _macroSettingsLoadedFromDriver = false;
                }
                ObserveDriverEvents(false, 0, 0, 0, 0, 0, 0);
                return;
            }

            byte[] report = new byte[Native.StatusReportLen];
            report[0] = Native.StatusReportId;
            if (!Native.HidD_GetFeature(_hidHandle, report, report.Length))
            {
                int err = Marshal.GetLastWin32Error();
                Native.CloseHandle(_hidHandle);
                _hidHandle = IntPtr.Zero;
                lock (_stateLock)
                {
                    DriverTelemetry t = new DriverTelemetry();
                    t.ReadErrorCode = err;
                    _latestTelemetry = t;
                    _driverSettingsSupported = false;
                    _driverMacroSupported = false;
                    _settingsLoadedFromDriver = false;
                    _macroSettingsLoadedFromDriver = false;
                }
                ObserveDriverEvents(false, 0, 0, 0, 0, 0, 0);
                return;
            }

            DriverTelemetry tel = new DriverTelemetry();
            tel.Connected = true;
            tel.RumbleFailed = report[1] != 0;
            tel.DriverError = BitConverter.ToUInt32(report, 2);
            int rawLen = report[6];
            if (rawLen > 16) rawLen = 16;
            tel.RawLen = rawLen;
            if (rawLen >= 2) tel.RawB1 = report[7 + 1];
            if (rawLen >= 3) tel.RawB2 = report[7 + 2];
            if (rawLen > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < rawLen; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(report[7 + i].ToString("X2"));
                }
                tel.RawHex = sb.ToString();
            }
            tel.RumbleWrites = BitConverter.ToUInt32(report, 23);
            tel.RumbleError = BitConverter.ToUInt32(report, 27);
            tel.StrongByte = report[31];
            tel.WeakByte = report[32];
            tel.IsBluetooth = report[33] != 0;
            tel.RumbleFailures = BitConverter.ToUInt32(report, 34);
            tel.LastLatencyMs = BitConverter.ToUInt16(report, 38);
            tel.BatteryPct = report[40];
            tel.TurboMask = (report[41] | (report[42] << 8)) & 0x03FF;
            tel.TurboToggleSeq = report[43];
            tel.MacroTriggerSeq = report[44];
            tel.LastMacroId = report[45];

            bool cfgOk;
            bool macroOk;
            bool needLoadSettings;
            bool needLoadMacros;
            lock (_stateLock)
            {
                cfgOk = _driverSettingsSupported;
                macroOk = _driverMacroSupported;
                needLoadSettings = !_settingsLoadedFromDriver;
                needLoadMacros = !_macroSettingsLoadedFromDriver;
            }

            DriverSettings parsedCfg = null;
            if (needLoadSettings)
            {
                byte[] cfg = new byte[Native.SettingsReportLen];
                cfg[0] = Native.SettingsReportId;
                cfgOk = Native.HidD_GetFeature(_hidHandle, cfg, cfg.Length);
                parsedCfg = cfgOk ? DriverSettings.FromBlob(cfg) : null;
            }

            byte[] parsedMacroBlob = null;
            if (needLoadMacros)
            {
                byte[] mbuf = new byte[Native.MacroSettingsReportLen];
                mbuf[0] = Native.MacroSettingsReportId;
                macroOk = Native.HidD_GetFeature(_hidHandle, mbuf, mbuf.Length);
                if (macroOk) parsedMacroBlob = mbuf;
            }

            bool errorJustAppeared = false;
            bool needUiSync = false;
            bool needMacroSync = false;
            lock (_stateLock)
            {
                if (tel.RumbleFailed && !_latestTelemetry.RumbleFailed)
                {
                    errorJustAppeared = true;
                }
                _latestTelemetry = tel;
                _driverSettingsSupported = cfgOk;
                _driverMacroSupported = macroOk;
                if (cfgOk && !_settingsLoadedFromDriver && parsedCfg != null)
                {
                    _driverSettings = parsedCfg;
                    _settingsLoadedFromDriver = true;
                    needUiSync = true;
                }
                if (macroOk && !_macroSettingsLoadedFromDriver && parsedMacroBlob != null)
                {
                    _macroSettingsLoadedFromDriver = true;
                    needMacroSync = true;
                }
            }

            if (needMacroSync && parsedMacroBlob != null)
            {
                lock (_macroLock)
                {
                    _appMacros.ApplyFromBlob(parsedMacroBlob);
                }
                try { BeginInvoke(new Action(SyncMacroControlsFromDriver)); } catch { }
            }

            if (errorJustAppeared)
            {
                AddLog(string.Format(I18n.T(
                    "[ERR] Збій вібрації GATT: 0x{0:X8} (Fails={1})",
                    "[ERR] GATT rumble failure: 0x{0:X8} (Fails={1})",
                    "[ERR] GATT-Vibrationsfehler: 0x{0:X8} (Fails={1})",
                    "[ERR] Сбой вибрации GATT: 0x{0:X8} (Fails={1})"),
                    tel.RumbleError, tel.RumbleFailures));
            }
            if (needUiSync)
            {
                try { BeginInvoke(new Action(SyncControlsFromDriverSettings)); } catch { }
            }

            ObserveDriverEvents(tel.Connected, tel.TurboMask, tel.TurboToggleSeq,
                tel.MacroTriggerSeq, tel.LastMacroId, tel.StrongByte, tel.WeakByte);
        }

        private string DescribeDriverMacroId(byte macroId)
        {
            switch (macroId)
            {
                case 1: return "Play / Pause";
                case 2: return "Win + PrtSc";
                case 3: return "F12";
                case 4: return "Win + G";
                case 5: return "Win + Alt + G";
                case 6: return "Next Track";
                case 7: return "Mute Audio";
                case 8:
                    lock (_macroLock)
                    {
                        return Native.FormatHotkeyLabel(_appMacros.CustomVk, _appMacros.CustomCtrl, _appMacros.CustomAlt, _appMacros.CustomShift);
                    }
                case 9: return "Assistant + D-Pad Up -> Vol +";
                case 10: return "Assistant + D-Pad Down -> Vol -";
                case 11: return "Assistant + D-Pad Left -> Prev Track";
                default: return "Macro #" + macroId;
            }
        }

        private void ObserveDriverEvents(bool connected, int turboMask, byte turboToggleSeq,
            byte macroTriggerSeq, byte lastMacroId, byte strongByte, byte weakByte)
        {
            long hapticUntil = Interlocked.Read(ref _turboHapticUntilTicks);
            if (hapticUntil != 0 && Stopwatch.GetTimestamp() >= hapticUntil)
            {
                if (Interlocked.CompareExchange(ref _turboHapticUntilTicks, 0, hapticUntil) == hapticUntil)
                {
                    if (!_uiRumbleActive)
                    {
                        int stopSlot;
                        lock (_stateLock) { stopSlot = _activeSlot; }
                        Native.SetVibration(stopSlot, 0, 0);
                    }
                }
            }

            if (!connected)
            {
                _turboSeqInitialized = false;
                _macroSeqInitialized = false;
                return;
            }

            if (!_turboSeqInitialized)
            {
                _prevTurboToggleSeq = turboToggleSeq;
                _turboSeqInitialized = true;
            }
            else if (turboToggleSeq != _prevTurboToggleSeq)
            {
                _prevTurboToggleSeq = turboToggleSeq;
                string list = DriverSettings.FormatTurboMask(turboMask);
                AddLog(string.Format(I18n.T(
                    "[TURBO] Геймпад перемкнув апаратний Turbo (winstadia.dll): {0}",
                    "[TURBO] Controller toggled hardware Turbo (winstadia.dll): {0}",
                    "[TURBO] Controller hat Hardware-Turbo umgeschaltet (winstadia.dll): {0}",
                    "[TURBO] Геймпад переключил аппаратный Turbo (winstadia.dll): {0}"), list));
                lock (_turboEventLock)
                {
                    _lastTurboEventText = string.Format("Turbo ({0}): {1}",
                        DateTime.Now.ToString("HH:mm:ss"), list);
                }
                TriggerMiniHaptic(strongByte, weakByte, 35, 65);
            }

            if (!_macroSeqInitialized)
            {
                _prevMacroSeq = macroTriggerSeq;
                _macroSeqInitialized = true;
            }
            else if (macroTriggerSeq != _prevMacroSeq)
            {
                _prevMacroSeq = macroTriggerSeq;
                string summary = DescribeDriverMacroId(lastMacroId);
                string msg = string.Format("[MACRO {0}] winstadia.dll -> {1}", DateTime.Now.ToString("HH:mm:ss"), summary);
                lock (_turboEventLock)
                {
                    _lastMacroEventText = msg;
                }
                AddLog(msg);
                if (lastMacroId != 9 && lastMacroId != 10)
                {
                    TriggerMiniHaptic(strongByte, weakByte, 18, 45);
                }
            }
        }

        private void TriggerMiniHaptic(byte strongByte, byte weakByte, int sPct, int wPct)
        {
            if (strongByte == 0 && weakByte == 0 && !_uiRumbleActive)
            {
                int slot;
                lock (_stateLock) { slot = _activeSlot; }
                Native.SetVibration(slot, sPct, wPct);
                Interlocked.Exchange(ref _turboHapticUntilTicks, Stopwatch.GetTimestamp() + (long)(0.075 * Stopwatch.Frequency));
            }
        }

        private static string[] GetMacroActionItems()
        {
            return new string[]
            {
                I18n.T("Вимкнено (нічого)", "Disabled (none)", "Deaktiviert (keine)", "Выключено (ничего)"),
                I18n.T("Play / Pause (медіа)", "Play / Pause (Media)", "Play / Pause (Medien)", "Play / Pause (медиа)"),
                I18n.T("Скріншот Windows (Win+PrtSc)", "Windows Screenshot (Win+PrtSc)", "Windows-Screenshot (Win+Druck)", "Скриншот Windows (Win+PrtSc)"),
                I18n.T("Скріншот Steam / гри (F12)", "Steam / Game Screenshot (F12)", "Steam-/Spiel-Screenshot (F12)", "Скриншот Steam / игры (F12)"),
                I18n.T("Xbox Game Bar (Win+G)", "Xbox Game Bar (Win+G)", "Xbox Game Bar (Win+G)", "Xbox Game Bar (Win+G)"),
                I18n.T("Кліп останніх 30с (Win+Alt+G)", "Record 30s Clip (Win+Alt+G)", "30s-Clip speichern (Win+Alt+G)", "Клип последних 30с (Win+Alt+G)"),
                I18n.T("Наступний трек (Media Next)", "Next Track (Media Next)", "Nächster Titel (Media Next)", "Следующий трек (Media Next)"),
                I18n.T("Вимкнути звук (Volume Mute)", "Mute Audio (Volume Mute)", "Ton stumm (Volume Mute)", "Отключить звук (Volume Mute)"),
                I18n.T("Власна клавіша (Custom)", "Custom Hotkey", "Eigener Hotkey", "Своя клавиша (Custom)")
            };
        }

        private void ApplyLanguageToUi()
        {
            Text = I18n.T(
                "Stadia Rumble Studio v1.0.15 (BLE · Макроси 0xE3 · Турбо 0xE2)",
                "Stadia Rumble Studio v1.0.15 (BLE · Macros 0xE3 · Turbo 0xE2)",
                "Stadia Rumble Studio v1.0.15 (BLE · Makros 0xE3 · Turbo 0xE2)",
                "Stadia Rumble Studio v1.0.15 (BLE · Макросы 0xE3 · Турбо 0xE2)");
            _subLabel.Text = I18n.T(
                "Автономні макроси 0xE3 і Turbo 0xE2 прямо в драйвері winstadia.dll + BLE-тюнінг і дампи",
                "Standalone 0xE3 macros & 0xE2 Turbo inside winstadia.dll + BLE tuning & dump analyzer",
                "Autonome 0xE3-Makros & 0xE2-Turbo direkt im winstadia.dll-Treiber + BLE-Tuning & Dumps",
                "Автономные макросы 0xE3 и Turbo 0xE2 прямо в драйвере winstadia.dll + BLE-тюнинг и дампы");

            _autoDiagBtn.Text = I18n.T("Автотест (5с)", "Auto Test (5s)", "Auto-Test (5s)", "Автотест (5с)");
            _saveLogBtn.Text = I18n.T("Зберегти лог (.txt)", "Save Log (.txt)", "Log (.txt)", "Сохранить лог (.txt)");
            _resetStatsBtn.Text = I18n.T("Скинути", "Reset", "Reset", "Сбросить");

            _tabMotorsBtn.Text = I18n.T("Мотори", "Motors", "Motoren", "Моторы");
            _tabBleBtn.Text = "BLE";
            _tabPowerBtn.Text = I18n.T("Живлення", "Power", "Akku", "Питание");
            _tabTuneBtn.Text = I18n.T("Тюнінг", "Tuning", "Tuning", "Тюнинг");
            _tabTurboBtn.Text = I18n.T("Макро/Турбо", "Macro/Turbo", "Makro/Turbo", "Макро/Турбо");
            _tabDumpBtn.Text = I18n.T("Дампи", "Dumps", "Dumps", "Дампы");

            // Tab 0: Motors
            _motorsSecHdr.Text = I18n.T("БАЛАНС І СИЛА МОТОРІВ (0xE2 В ДРАЙВЕРІ)", "MOTOR GAIN & BALANCE (0xE2 IN DRIVER)", "MOTORSTÄRKE & BALANCE (0xE2 IM TREIBER)", "БАЛАНС И СИЛА МОТОРОВ (0xE2 В ДРАЙВЕРЕ)");
            _motorsHintLbl.Text = I18n.T(
                "Налаштування застосовується безпосередньо в winstadia.dll для ВСІХ ігор та зберігається у реєстрі Windows.",
                "Applied directly inside winstadia.dll for ALL games and stored in the Windows registry.",
                "Wird direkt in winstadia.dll für ALLE Spiele angewendet und in der Windows-Registry gespeichert.",
                "Настройка применяется напрямую в winstadia.dll для ВСЕХ игр и сохраняется в реестре Windows.");
            _strongCapLbl.Text = I18n.T("Лівий мотор (Strong / низькі частоти)", "Left motor (Strong / low frequency)", "Linker Motor (Strong / Tieffrequenz)", "Левый мотор (Strong / низкие частоты)");
            _weakCapLbl.Text = I18n.T("Правий мотор (Weak / високі частоти)", "Right motor (Weak / high frequency)", "Rechter Motor (Weak / Hochfrequenz)", "Правый мотор (Weak / высокие частоты)");
            _syncMotorsCheck.Text = I18n.T("Синхронізувати повзунки (лівий = правий)", "Link sliders (left = right)", "Regler koppeln (links = rechts)", "Синхронизировать ползунки (левый = правый)");
            _applyGainBtn.Text = I18n.T("Зберегти силу моторів у драйвер (0xE2)", "Save motor gain to driver (0xE2)", "Motorstärke im Treiber speichern (0xE2)", "Сохранить силу моторов в драйвер (0xE2)");
            _motorsTestSecHdr.Text = I18n.T("ПЕРЕВІРКА ТА СТРЕС-ТЕСТ МОТОРІВ", "RUMBLE TEST & BLE STRESS CHECK", "VIBRATIONSTEST & BLE-BELASTUNGSTEST", "ПРОВЕРКА И СТРЕСС-ТЕСТ МОТОРОВ");
            _pulseBtn.Text = I18n.T("Імпульс 1.5с", "Pulse 1.5s", "Impuls 1.5s", "Импульс 1.5с");
            _sweepBtn.Text = I18n.T("Хвиля BLE", "BLE Sweep", "BLE-Welle", "Волна BLE");
            _holdBtn.Text = I18n.T("Утримувати", "Hold", "Halten", "Удерживать");
            _stopBtn.Text = I18n.T("СТОП ВІБРАЦІЯ", "STOP RUMBLE", "VIBRATION STOP", "СТОП ВИБРАЦИЯ");

            // Tab 1: BLE
            _bleSecHdr.Text = I18n.T("ЖИВИЙ МОНІТОР BLE RSSI І ЗАТРИМОК", "LIVE BLE RSSI & LATENCY MONITOR", "LIVE BLE-RSSI- & LATENZMONITOR", "ЖИВОЙ МОНИТОР BLE RSSI И ЗАДЕРЖЕК");
            _blePauseBtn.Text = _blePaused
                ? I18n.T("Продовжити", "Resume", "Fortsetzen", "Продолжить")
                : I18n.T("Пауза", "Pause", "Pause", "Пауза");
            _bleStressBtn.Text = I18n.T("Стрес-тест BLE (хвиля)", "BLE stress test (sweep)", "BLE-Stresstest (Welle)", "Стресс-тест BLE (волна)");
            _bleResetBtn.Text = I18n.T("Очистити", "Clear", "Leeren", "Очистить");
            _bleChart.Invalidate();

            // Tab 2: Power
            _powerSecHdr.Text = I18n.T("ДАТЧИК ЗАРЯДУ АКУМУЛЯТОРА І ЖИВЛЕННЯ", "BATTERY LEVEL & POWER TELEMETRY", "AKKUSTAND & STROMTELEMETRIE", "ДАТЧИК ЗАРЯДА АККУМУЛЯТОРА И ПИТАНИЯ");
            _powerDrainTestBtn.Text = I18n.T("Оновити з Windows BLE", "Refresh Windows BLE", "Windows BLE prüfen", "Обновить из Windows BLE");
            _powerResetBtn.Text = I18n.T("Скинути історію графіка", "Reset chart history", "Verlauf zurücksetzen", "Сбросить историю графика");
            _batteryChart.Invalidate();

            // Tab 3: Tuning
            _tuneSecHdr1.Text = I18n.T("ДРАЙВЕРНИЙ РЕМЕПІНГ СТІКІВ І ТРИГЕРІВ (0xE2)", "DRIVER STICK & TRIGGER TUNING (0xE2)", "TREIBER-STICK- & TRIGGER-TUNING (0xE2)", "ДРАЙВЕРНЫЙ РЕМЭППИНГ СТИКОВ И ТРИГГЕРОВ (0xE2)");
            _stickDzCapLbl.Text = I18n.T("Мертва зона стіків (проти дрифту)", "Stick deadzone (anti-drift)", "Stick-Totzone (Anti-Drift)", "Мёртвая зона стиков (от дрифта)");
            _trigDzCapLbl.Text = I18n.T("Поріг спрацювання тригерів L2/R2", "Trigger deadzone L2/R2", "Trigger-Totzone L2/R2", "Порог срабатывания триггеров L2/R2");
            _trigMaxCapLbl.Text = I18n.T("Повне натискання тригерів (Hair-Trigger)", "Trigger max cap (Hair-Trigger)", "Trigger-Maximum (Hair-Trigger)", "Полное нажатие триггеров (Hair-Trigger)");
            _swapSticksCheck.Text = I18n.T("Поміняти місцями лівий і правий стіки (Swap LS ↔ RS)", "Swap left and right sticks (Swap LS ↔ RS)", "Linken und rechten Stick tauschen (LS ↔ RS)", "Поменять местами левый и правый стики (Swap LS ↔ RS)");
            _invertLyCheck.Text = I18n.T("Інверсія осі LY", "Invert LY axis", "LY-Achse umkehren", "Инверсия оси LY");
            _invertRyCheck.Text = I18n.T("Інверсія осі RY", "Invert RY axis", "RY-Achse umkehren", "Инверсия оси RY");
            _tuneSecHdr2.Text = I18n.T("ПАТЧ ТАЙМІНГІВ BLE-ВІБРАЦІЇ (БЕЗ ЛАГІВ СЕМПЛІНГУ)", "BLE RUMBLE PACING PATCH (ANTI-STUTTER)", "BLE-VIBRATIONSTIMING-PATCH (OHNE RUCKLER)", "ПАТЧ ТАЙМИНГОВ BLE-ВИБРАЦИИ (БЕЗ ЛАГОВ СЭМПЛИНГА)");
            _presetFastBtn.Text = I18n.T("Швидкий (25мс)", "Fast (25ms)", "Schnell (25ms)", "Быстрый (25мс)");
            _presetBalBtn.Text = I18n.T("Баланс (40мс)", "Balanced (40ms)", "Balance (40ms)", "Баланс (40мс)");
            _presetSmoothBtn.Text = I18n.T("Плавний (55мс)", "Smooth (55ms)", "Sanft (55ms)", "Плавный (55мс)");
            _bleIntCapLbl.Text = I18n.T("Мін. інтервал BLE GATT (мс)", "Min BLE GATT interval (ms)", "Min. BLE-GATT-Intervall (ms)", "Мин. интервал BLE GATT (мс)");
            _bleGapCapLbl.Text = I18n.T("Пауза після Write ACK (мс)", "Post-ACK write gap (ms)", "Pause nach Write-ACK (ms)", "Пауза после Write ACK (мс)");
            _bleDeltaCapLbl.Text = I18n.T("Поріг чутливості зміни мотора (MinDelta)", "Motor delta threshold (MinDelta)", "Motor-Änderungsschwelle (MinDelta)", "Порог чувствительности изменения мотора (MinDelta)");
            _bleIntervalLabel.Text = _bleIntervalSlider.Value + " " + I18n.T("мс", "ms", "ms", "мс");
            _bleGapLabel.Text = _bleGapSlider.Value + " " + I18n.T("мс", "ms", "ms", "мс");
            _applyAllBtn.Text = I18n.T("Застосувати і зберегти (0xE2)", "Apply & save to driver (0xE2)", "Übernehmen & speichern (0xE2)", "Применить и сохранить (0xE2)");
            _resetDefaultBtn.Text = I18n.T("Скинути стандарт", "Reset defaults", "Standardwerte", "Сбросить стандарт");
            _exportPatchBtn.Text = I18n.T("Експорт патча (.bin/.reg)", "Export patch (.bin/.reg)", "Patch exportieren (.bin/.reg)", "Экспорт патча (.bin/.reg)");
            _openWebUsbBtn.Text = I18n.T("Відкрити WebUSB-патчер", "Open WebUSB Patcher", "WebUSB-Patcher öffnen", "Открыть WebUSB-патчер");
            _tuneStatusLabel.Text = I18n.T(
                "Налаштування застосовуються на рівні драйвера winstadia.dll і зберігаються в реєстрі Windows.",
                "Applied directly in winstadia.dll and saved in Windows Registry for automatic reload.",
                "Wird direkt in winstadia.dll angewendet und dauerhaft in der Windows-Registry gespeichert.",
                "Настройки применяются на уровне драйвера winstadia.dll и сохраняются в реестре Windows.");

            // Tab 4: Autonomous Driver Macros (0xE3) + Hardware Turbo (0xE2)
            _macroDriverTitle.Text = I18n.T("АВТОНОМНІ ДРАЙВЕРНІ МАКРОСИ ТА ТУРБО (0xE3 · 0xE2)", "AUTONOMOUS DRIVER MACROS & TURBO (0xE3 · 0xE2)", "AUTONOME TREIBER-MAKROS & TURBO (0xE3 · 0xE2)", "АВТОНОМНЫЕ ДРАЙВЕРНЫЕ МАКРОСЫ И ТУРБО (0xE3 · 0xE2)");
            _macroDriverBody.Text = I18n.T(
                "Налаштуй 1 раз і повністю закривай програму [X]: winstadia.dll сама надсилає HID-клавіші\r\nта медіа-команди в Windows і зберігає профіль назавжди без жодних фонових .exe!",
                "Configure once & close [X] completely: winstadia.dll emits HID keyboard & media reports\r\ndirectly in Windows and persists across reboots with ZERO background apps!",
                "Einmal einstellen & [X] komplett schließen: winstadia.dll sendet HID-Tasten & Medienbefehle\r\ndirekt unter Windows und speichert dauerhaft — OHNE Hintergrundprogramm!",
                "Настрой 1 раз и полностью закрывай программу [X]: winstadia.dll сама отправляет HID-клавиши\r\nи медиа-команды в Windows и хранит профиль навсегда без фоновых .exe!");

            _macrosSecHdr.Text = I18n.T("МАКРОСИ ДРАЙВЕРА (ASSISTANT · CAPTURE · ХРЕСТОВИНА)", "DRIVER HOTKEY MACROS (ASSISTANT · CAPTURE · D-PAD)", "TREIBER-MAKROS (ASSISTANT · CAPTURE · STEUERKREUZ)", "МАКРОСЫ ДРАЙВЕРА (ASSISTANT · CAPTURE · КРЕСТОВИНА)");
            _macrosEnabledCheck.Text = I18n.T("Увімкнути автономні макроси Assistant, Capture і комбінацій", "Enable standalone Assistant, Capture & D-Pad macros", "Autonome Assistant-, Capture- & Steuerkreuz-Makros aktivieren", "Включить автономные макросы Assistant, Capture и комбинаций");
            _assistActionCapLbl.Text = I18n.T("Коротко Assistant (•••):", "Assistant (•••) tap:", "Assistant (•••) Klick:", "Коротко Assistant (•••):");
            _captureActionCapLbl.Text = I18n.T("Натискання Capture ([●]):", "Capture ([●]) press:", "Capture ([●]) Druck:", "Нажатие Capture ([●]):");
            _customKeyCapLbl.Text = I18n.T("Власна клавіша (Custom):", "Custom Hotkey:", "Eigener Hotkey:", "Своя клавиша (Custom):");
            _dpadComboCheck.Text = I18n.T(
                "Assistant + Хрестовина: Вгору/Вниз = Гучність +/- · Вліво/Вправо = Трек ⏮/⏭",
                "Assistant + D-Pad: Up/Down = Volume +/- · Left/Right = Prev/Next Track",
                "Assistant + D-Pad: Hoch/Runter = Lautstärke +/- · Links/Rechts = Titel ⏮/⏭",
                "Assistant + Крестовина: Вверх/Вниз = Громкость +/- · Влево/Вправо = Трек ⏮/⏭");
            _macroApplyBtn.Text = I18n.T("Зберегти макроси в драйвер (0xE3)", "Save macros to driver (0xE3)", "Makros im Treiber speichern (0xE3)", "Сохранить макросы в драйвер (0xE3)");

            _suppressMacroEvents = true;
            string[] mItems = GetMacroActionItems();
            _assistActionCombo.Items.Clear();
            _assistActionCombo.Items.AddRange(mItems);
            _assistActionCombo.SelectedIndex = Native.ClampInt((int)_appMacros.AssistantAction, 0, mItems.Length - 1);
            _captureActionCombo.Items.Clear();
            _captureActionCombo.Items.AddRange(mItems);
            _captureActionCombo.SelectedIndex = Native.ClampInt((int)_appMacros.CaptureAction, 0, mItems.Length - 1);

            if (_customKeyPresetCombo != null)
            {
                _customKeyPresetCombo.Items.Clear();
                _customKeyPresetCombo.Items.Add(I18n.T("Пресет…", "Presets…", "Presets…", "Пресет…"));
                for (int pi = 0; pi < _hotkeyPresets.Length; pi++)
                {
                    _customKeyPresetCombo.Items.Add(_hotkeyPresets[pi].Label);
                }
            }
            _suppressMacroEvents = false;
            RefreshCustomKeyUiState();

            if (_uiToolTip != null)
            {
                _uiToolTip.SetToolTip(_customKeyBtn, I18n.T(
                    "Натисніть кнопку, а потім потрібну клавішу або Ctrl/Alt/Shift + клавішу на клавіатурі. Esc скасовує запис.",
                    "Click this button, then press any key or Ctrl/Alt/Shift + key on your keyboard. Press Esc to cancel.",
                    "Klicken und dann gewünschte Taste oder Ctrl/Alt/Shift + Taste drücken. Esc bricht ab.",
                    "Нажмите кнопку, затем нужную клавишу или Ctrl/Alt/Shift + клавишу на клавиатуре. Esc отменяет запись."));
                _uiToolTip.SetToolTip(_customKeyPresetCombo, I18n.T(
                    "Швидкий вибір популярної клавіші або комбінації без запису з клавіатури.",
                    "Quickly pick a popular hotkey or combo without typing.",
                    "Beliebten Hotkey schnell aus der Liste wählen.",
                    "Быстрый выбор популярной клавиши или комбинации без ввода с клавиатуры."));
                _uiToolTip.SetToolTip(_assistActionCombo, I18n.T(
                    "Спрацьовує при відпусканні Assistant (•••), якщо не натискались D-Pad або Turbo-кнопка.",
                    "Triggers on release of Assistant (•••) unless used in a D-Pad or Turbo combo.",
                    "Löst beim Loslassen von Assistant (•••) aus, sofern keine D-Pad-/Turbo-Kombo genutzt wurde.",
                    "Срабатывает при отпускании Assistant (•••), если не нажимались D-Pad или Turbo-кнопка."));
                _uiToolTip.SetToolTip(_trigMaxSlider, I18n.T(
                    "Hair-Trigger: значення 70% означає, що тригер L2/R2 досягає 100% у грі вже на 70% ходу.",
                    "Hair-Trigger: 70% means L2/R2 reaches full 100% in-game press at 70% physical travel.",
                    "Hair-Trigger: 70% bedeutet, L2/R2 erreicht 100% bereits bei 70% Weg.",
                    "Hair-Trigger: 70% означает, что триггер L2/R2 даёт 100% в игре уже на 70% хода."));
                _uiToolTip.SetToolTip(_bleDeltaSlider, I18n.T(
                    "MinDelta: ігнорує дрібні коливання вібрації <N/255, щоб не забивати Bluetooth LE. Рекомендовано: 8.",
                    "MinDelta: ignores tiny rumble steps <N/255 to avoid saturating Bluetooth LE. Recommended: 8.",
                    "MinDelta: ignoriert kleine Vibrationsänderungen <N/255 für stabiles BLE. Empfohlen: 8.",
                    "MinDelta: игнорирует мелкие колебания вибрации <N/255, чтобы не перегружать Bluetooth LE. Рекомендуется: 8."));
            }

            lock (_turboEventLock)
            {
                if (string.IsNullOrEmpty(_lastMacroEventText))
                {
                    _macroLastFiredLabel.Text = I18n.T(
                        "Після збереження 0xE3 макроси та медіа-комбінації працюють у всіх іграх навіть коли Studio повністю закрита.",
                        "Once saved to 0xE3, hotkeys & media combos work across all games even when Studio is completely closed.",
                        "Nach dem Speichern in 0xE3 funktionieren Hotkeys & Medienkombos auch bei komplett geschlossener App.",
                        "После сохранения 0xE3 макросы и медиа-комбинации работают во всех играх, даже когда Studio полностью закрыта.");
                }
            }

            _turboSecHdr.Text = I18n.T("АПАРАТНИЙ TURBO НА РІВНІ ДРАЙВЕРА (winstadia.dll · 0xE2)", "DRIVER-LEVEL HARDWARE TURBO (winstadia.dll · 0xE2)", "HARDWARE-TURBO IM TREIBER (winstadia.dll · 0xE2)", "АППАРАТНЫЙ TURBO НА УРОВНЕ ДРАЙВЕРА (winstadia.dll · 0xE2)");
            _turboHintLbl.Text = I18n.T(
                "Клікайте кнопки [A..RS] нижче АБО утримуйте Assistant (•••) + кнопку на геймпаді, щоб увімкнути/вимкнути автоклік у драйвері.",
                "Click [A..RS] pills below OR hold Assistant (•••) + button on controller to toggle rapid-fire inside the driver.",
                "Klicken Sie [A..RS] unten ODER halten Sie Assistant (•••) + Taste am Pad für Hardware-Schnellfeuer.",
                "Кликайте кнопки [A..RS] ниже ИЛИ удерживайте Assistant (•••) + кнопку на геймпаде, чтобы вкл/выкл автокликер в драйвере.");
            _turboEnabledCheck.Text = I18n.T(
                "Дозволити апаратний Turbo (комбо Assistant + кнопка в winstadia.dll)",
                "Enable hardware Turbo (Assistant + button combo inside winstadia.dll)",
                "Hardware-Turbo aktivieren (Assistant + Taste in winstadia.dll)",
                "Разрешить аппаратный Turbo (комбо Assistant + кнопка в winstadia.dll)");
            _turboHoldModeCheck.Text = I18n.T(
                "Hold: автоклік лише поки тримаєш Assistant + кнопку (інакше Toggle: тап увімк./вимк.)",
                "Hold mode: fire Turbo only while holding Assistant + button (off = Toggle on/off)",
                "Hold-Modus: Turbo nur beim Halten von Assistant + Taste (aus = Toggle Ein/Aus)",
                "Hold: автоклик только пока держишь Assistant + кнопку (иначе Toggle: тап вкл./выкл.)");
            _turboHzCapLbl.Text = I18n.T("Частота автонатискань (Гц)", "Rapid-fire cadence (Hz)", "Feuerrate (Hz)", "Частота автонажатий (Гц)");
            _turboHzLabel.Text = _turboHzSlider.Value + " " + I18n.T("Гц", "Hz", "Hz", "Гц");
            _turboApplyBtn.Text = I18n.T("Зберегти Turbo у драйвер", "Save Turbo to driver", "Turbo im Treiber speichern", "Сохранить Turbo в драйвер");
            _turboClearBtn.Text = I18n.T("Вимкнути все", "Clear all", "Alle aus", "Выключить всё");
            RefreshTurboPillVisuals();
            lock (_turboEventLock)
            {
                if (string.IsNullOrEmpty(_lastTurboEventText))
                {
                    _turboHwStatusLabel.Text = I18n.T(
                        "Налаштування Turbo зберігаються у драйвері (0xE2) і працюють після повного закриття програми.",
                        "Turbo settings persist inside the driver (0xE2) and stay active after completely closing this app.",
                        "Turbo-Einstellungen bleiben im Treiber (0xE2) gespeichert und funktionieren auch nach komplettem Schließen.",
                        "Настройки Turbo сохраняются в драйвере (0xE2) и работают после полного закрытия программы.");
                }
            }

            // Tab 5: Dumps
            _dumpSecHdr.Text = I18n.T("АНАЛІЗАТОР ДАМПІВ ПОМИЛОК І ДІАГНОСТИЧНИЙ ЛОГ", "CRASH DUMP ANALYZER & DIAGNOSTIC LOG", "CRASH-DUMP-ANALYSE & DIAGNOSELOG", "АНАЛИЗАТОР ДАМПОВ ОШИБОК И ДИАГНОСТИЧЕСКИЙ ЛОГ");
            _scanDumpsBtn.Text = I18n.T("Сканувати дампи Windows", "Scan Windows dumps", "Windows-Dumps scannen", "Сканировать дампы Windows");
            _importDumpBtn.Text = I18n.T("Аналіз .txt логу", "Analyze .txt log", ".txt-Log prüfen", "Анализ .txt лога");
            _copyLogBtn.Text = I18n.T("Копіювати", "Copy log", "Kopieren", "Копировать");

            // Force instant UI refresh
            _lastBatterySampleTicks = 0;
            _lastBleChartSampleTicks = 0;
            _canvas.Invalidate();
            LayoutMainSplit();
        }

        private void ScanWindowsBatteryAsync()
        {
            Thread t = new Thread(() =>
            {
                string ps = RunCommandCapture("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"$p = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'Stadia|Xbox Wireless' -and $_.Status -eq 'OK' } | Select-Object -First 1; if ($p) { $b = Get-PnpDeviceProperty -InstanceId $p.InstanceId -KeyName '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2' -ErrorAction SilentlyContinue; if ($b -and $b.Data -ne $null) { Write-Output $b.Data } }\"",
                    3500);
                int pct;
                if (int.TryParse(ps.Trim(), out pct) && pct >= 0 && pct <= 100)
                {
                    _winPnpBatteryPct = pct;
                    AddLog(string.Format("Windows BLE Battery Service: {0}%", pct));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ScanWindowsDumpsAsync()
        {
            AddLog(I18n.T(
                "[DUMP] Сканування системних журналів Windows, WUDFHost та install.log…",
                "[DUMP] Scanning Windows Event Log, WUDFHost & install.log…",
                "[DUMP] Durchsuche Windows-Ereignisprotokoll, WUDFHost & install.log…",
                "[DUMP] Сканирование системных журналов Windows, WUDFHost и install.log…"));
            Thread t = new Thread(() =>
            {
                StringBuilder findings = new StringBuilder();
                string bth = RunCommandCapture("sc.exe", "query bthserv", 2000);
                bool bthRunning = bth.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;

                string evt = RunCommandCapture("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2} -MaxEvents 12 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'WUDF|winstadia|StadiaRumble|bth' } | ForEach-Object { $_.TimeCreated.ToString('HH:mm:ss') + ' ' + $_.ProviderName + ': ' + ($_.Message -replace '\\r|\\n',' ') }\"",
                    4000);

                if (!bthRunning)
                {
                    findings.AppendLine(I18n.T(
                        "1) Служба Bluetooth (bthserv) не запущена — виконайте 'sc start bthserv' у PowerShell від адміністратора.",
                        "1) Bluetooth Support Service (bthserv) is stopped — run 'sc start bthserv' in elevated PowerShell.",
                        "1) Bluetooth-Dienst (bthserv) gestoppt — in Admin-PowerShell 'sc start bthserv' ausführen.",
                        "1) Служба Bluetooth (bthserv) не запущена — выполните 'sc start bthserv' в PowerShell от администратора."));
                }
                if (!string.IsNullOrEmpty(evt) && evt != "(empty)" && !evt.StartsWith("(timeout"))
                {
                    findings.AppendLine(I18n.T(
                        "2) Знайдено збій UMDF/WUDFHost у журналі Windows — перевстановіть драйвер через install.bat (від адміністратора).",
                        "2) Found UMDF/WUDFHost fault in Windows Event Log — reinstall driver via elevated install.bat.",
                        "2) UMDF/WUDFHost-Fehler im Ereignisprotokoll gefunden — Treiber über Admin-install.bat neu installieren.",
                        "2) Обнаружен сбой UMDF/WUDFHost в журнале Windows — переустановите драйвер через install.bat (от администратора)."));
                }
                else
                {
                    findings.AppendLine(I18n.T(
                        "Системних збоїв WUDFHost не знайдено. Служба bthserv активна.",
                        "No WUDFHost driver crashes found. bthserv service is active.",
                        "Keine WUDFHost-Treiberabstürze gefunden. bthserv-Dienst aktiv.",
                        "Системных сбоев WUDFHost не найдено. Служба bthserv активна."));
                }

                string analysis = findings.ToString().Trim();
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _customDumpAnalysis = analysis;
                        AddLog(I18n.T("[DUMP] Сканування завершено: ", "[DUMP] Scan complete: ", "[DUMP] Scan abgeschlossen: ", "[DUMP] Сканирование завершено: ") + analysis.Replace("\r\n", " | "));
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ImportAndAnalyzeDumpFile()
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "Diagnostic Dump (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*";
            ofd.Title = I18n.T(
                "Оберіть .txt дамп або лог помилок для автоматичного аналізу",
                "Select .txt dump or error log for automatic analysis",
                "Wählen Sie einen .txt-Dump oder Fehlerlog zur Analyse",
                "Выберите .txt дамп или лог ошибок для автоматического анализа");
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string text = File.ReadAllText(ofd.FileName);
            List<string> tips = new List<string>();
            if (text.IndexOf("0x80070005", StringComparison.OrdinalIgnoreCase) >= 0)
                tips.Add(I18n.T(
                    "Виявлено 0x80070005 (Access Denied до GATT): перепідключіть Stadia Controller через Bluetooth і запустіть install.bat від адміністратора.",
                    "Detected 0x80070005 (GATT Access Denied): re-pair Stadia Controller via Bluetooth and run elevated install.bat.",
                    "0x80070005 (GATT-Zugriff verweigert) erkannt: Controller per Bluetooth neu verbinden und install.bat als Admin starten.",
                    "Обнаружен 0x80070005 (Access Denied к GATT): переподключите Stadia Controller через Bluetooth и запустите install.bat от администратора."));
            if (text.IndexOf("0x80000013", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("0x80070490", StringComparison.OrdinalIgnoreCase) >= 0)
                tips.Add(I18n.T(
                    "Виявлено розрив GATT (0x80000013): перевірте заряд батареї контролера, зменште дистанцію до Bluetooth-адаптера.",
                    "Detected GATT disconnect (0x80000013): check controller battery charge and move closer to the Bluetooth adapter.",
                    "GATT-Verbindungsabbruch (0x80000013) erkannt: Akkustand prüfen und näher an den Bluetooth-Adapter gehen.",
                    "Обнаружен разрыв GATT (0x80000013): проверьте заряд батареи контроллера, уменьшите дистанцию до Bluetooth-адаптера."));
            if (text.IndexOf("0xC00000BB", StringComparison.OrdinalIgnoreCase) >= 0)
                tips.Add(I18n.T(
                    "Виявлено 0xC00000BB: версія winstadia.dll застаріла. Запустіть install.bat із нового архіву 1.0.15.",
                    "Detected 0xC00000BB: outdated winstadia.dll detected. Run install.bat from release 1.0.15.",
                    "0xC00000BB erkannt: veraltete winstadia.dll. Führen Sie install.bat aus Version 1.0.15 aus.",
                    "Обнаружен 0xC00000BB: версия winstadia.dll устарела. Запустите install.bat из нового архива 1.0.15."));
            if (Regex.IsMatch(text, @"WUDF|UMDF", RegexOptions.IgnoreCase))
                tips.Add(I18n.T(
                    "Виявлено збій WUDFHost (UMDF): перепідключіть контролер і виконайте install.bat для оновлення драйвера.",
                    "Detected WUDFHost (UMDF) fault: reconnect controller and run install.bat to repair the driver.",
                    "WUDFHost (UMDF)-Fehler erkannt: Controller neu verbinden und install.bat ausführen.",
                    "Обнаружен сбой WUDFHost (UMDF): переподключите контроллер и выполните install.bat для обновления драйвера."));
            if (text.IndexOf("[SPIKE]", StringComparison.OrdinalIgnoreCase) >= 0)
                tips.Add(I18n.T(
                    "Виявлено стрибки затримки BLE (>45мс): у вкладці 'Тюнінг' виберіть профіль 'Плавний (55мс)'.",
                    "Detected BLE latency spikes (>45ms): select 'Smooth (55ms)' preset in Tuning tab.",
                    "BLE-Latenzspitzen (>45ms) erkannt: Wählen Sie im Tab 'Tuning' das Profil 'Sanft (55ms)'.",
                    "Обнаружены скачки задержки BLE (>45мс): во вкладке 'Тюнинг' выберите профиль 'Плавный (55мс)'."));

            if (tips.Count == 0)
            {
                tips.Add(string.Format(I18n.T(
                    "У файлі '{0}' критичних помилок драйвера не виявлено.",
                    "No critical driver faults detected in '{0}'.",
                    "Keine kritischen Treiberfehler in '{0}' gefunden.",
                    "В файле '{0}' критических ошибок драйвера не обнаружено."), Path.GetFileName(ofd.FileName)));
            }
            _customDumpAnalysis = string.Join(" ", tips.ToArray());
            AddLog("[DUMP-IMPORT] " + Path.GetFileName(ofd.FileName) + ": " + _customDumpAnalysis);
        }

        private void ExportConfigPatchFiles()
        {
            DriverSettings s = GatherUiSettings();
            byte[] blob = s.ToBlob();

            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "Stadia Patch Binary (*.bin)|*.bin";
            sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            sfd.FileName = string.Format("stadia_0xe2_patch_{0}ms.bin", s.BleIntervalMs);
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            File.WriteAllBytes(sfd.FileName, blob);
            string regPath = Path.ChangeExtension(sfd.FileName, ".reg");
            StringBuilder reg = new StringBuilder();
            reg.AppendLine("Windows Registry Editor Version 5.00");
            reg.AppendLine();
            reg.AppendLine("[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WinStadia\\Parameters]");
            reg.AppendLine(string.Format("\"StrongGainPct\"=dword:{0:x8}", s.StrongGainPct));
            reg.AppendLine(string.Format("\"WeakGainPct\"=dword:{0:x8}", s.WeakGainPct));
            reg.AppendLine(string.Format("\"BleIntervalMs\"=dword:{0:x8}", s.BleIntervalMs));
            reg.AppendLine(string.Format("\"BlePostAckGapMs\"=dword:{0:x8}", s.BlePostAckGapMs));
            reg.AppendLine(string.Format("\"BleMinDelta\"=dword:{0:x8}", s.BleMinDelta));
            File.WriteAllText(regPath, reg.ToString(), new UTF8Encoding(false));

            AddLog(string.Format(I18n.T("Збережено конфігураційний патч (.bin і .reg): {0}", "Saved configuration patch (.bin and .reg): {0}", "Konfigurationspatch (.bin & .reg) gespeichert: {0}", "Сохранён конфигурационный патч (.bin и .reg): {0}"), sfd.FileName));
            MessageBox.Show(
                I18n.T("Створено файли патча:\n", "Created patch files:\n", "Patch-Dateien erstellt:\n", "Созданы файлы патча:\n") + sfd.FileName + "\n" + regPath,
                I18n.T("Патч 0xE2 готовий", "Patch 0xE2 Ready", "Patch 0xE2 bereit", "Патч 0xE2 готов"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SyncControlsFromDriverSettings()
        {
            DriverSettings s;
            lock (_stateLock) { s = _driverSettings; }

            string msUnit = I18n.T("мс", "ms", "ms", "мс");
            string hzUnit = I18n.T("Гц", "Hz", "Hz", "Гц");

            _suppressSliderEvents = true;
            _strongGainSlider.Value = s.StrongGainPct;
            _strongGainLabel.Text = s.StrongGainPct + "%";
            _weakGainSlider.Value = s.WeakGainPct;
            _weakGainLabel.Text = s.WeakGainPct + "%";
            _stickDzSlider.Value = s.StickDeadzonePct;
            _stickDzLabel.Text = s.StickDeadzonePct + "%";
            _trigDzSlider.Value = s.TriggerDeadzonePct;
            _trigDzLabel.Text = s.TriggerDeadzonePct + "%";
            _trigMaxSlider.Value = s.TriggerMaxPct;
            _trigMaxLabel.Text = s.TriggerMaxPct + "%";
            _swapSticksCheck.Checked = s.SwapSticks;
            _invertLyCheck.Checked = s.InvertLy;
            _invertRyCheck.Checked = s.InvertRy;
            _bleIntervalSlider.Value = s.BleIntervalMs;
            _bleIntervalLabel.Text = s.BleIntervalMs + " " + msUnit;
            _bleGapSlider.Value = s.BlePostAckGapMs;
            _bleGapLabel.Text = s.BlePostAckGapMs + " " + msUnit;
            _bleDeltaSlider.Value = s.BleMinDelta;
            _bleDeltaLabel.Text = s.BleMinDelta.ToString();
            if (_turboEnabledCheck != null) _turboEnabledCheck.Checked = s.TurboEnabled;
            if (_turboHoldModeCheck != null) _turboHoldModeCheck.Checked = s.TurboHoldMode;
            if (_turboHzSlider != null)
            {
                _turboHzSlider.Value = Native.ClampInt(s.TurboHz, 5, 25);
                _turboHzLabel.Text = _turboHzSlider.Value + " " + hzUnit;
            }
            _turboMaskLocal = s.TurboMask & 0x03FF;
            _lastObservedDriverTurboMask = _turboMaskLocal;
            _suppressSliderEvents = false;
            RefreshTurboPillVisuals();

            AddLog(string.Format(I18n.T(
                "Зчитано налаштування 0xE2: Strong={0}%, Weak={1}%, StickDZ={2}%, BLE={3}мс, Turbo={4} Гц",
                "Loaded 0xE2 driver config: Strong={0}%, Weak={1}%, StickDZ={2}%, BLE={3}ms, Turbo={4} Hz",
                "0xE2-Treiberkonfig geladen: Strong={0}%, Weak={1}%, StickDZ={2}%, BLE={3}ms, Turbo={4} Hz",
                "Считаны настройки 0xE2: Strong={0}%, Weak={1}%, StickDZ={2}%, BLE={3}мс, Turbo={4} Гц"),
                s.StrongGainPct, s.WeakGainPct, s.StickDeadzonePct, s.BleIntervalMs, s.TurboHz));
        }

        private void SyncMacroControlsFromDriver()
        {
            _suppressMacroEvents = true;
            lock (_macroLock)
            {
                if (_macrosEnabledCheck != null) _macrosEnabledCheck.Checked = _appMacros.MacrosEnabled;
                if (_dpadComboCheck != null) _dpadComboCheck.Checked = _appMacros.DpadMediaCombos;
                if (_assistActionCombo != null && _assistActionCombo.Items.Count > 0)
                    _assistActionCombo.SelectedIndex = Native.ClampInt((int)_appMacros.AssistantAction, 0, _assistActionCombo.Items.Count - 1);
                if (_captureActionCombo != null && _captureActionCombo.Items.Count > 0)
                    _captureActionCombo.SelectedIndex = Native.ClampInt((int)_appMacros.CaptureAction, 0, _captureActionCombo.Items.Count - 1);
            }
            _suppressMacroEvents = false;
            _customKeyErrorText = "";
            RefreshCustomKeyUiState();
            AddLog(I18n.T(
                "Зчитано профіль автономних макросів з драйвера (0xE3).",
                "Loaded standalone macro profile from driver (0xE3).",
                "Autonomes Makro-Profil aus Treiber (0xE3) geladen.",
                "Считан профиль автономных макросов из драйвера (0xE3)."));
        }

        private void PushMacrosToDriver(bool quiet)
        {
            byte[] blob;
            lock (_macroLock)
            {
                blob = _appMacros.ToBlob();
            }

            IntPtr h = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
            if (h == IntPtr.Zero)
            {
                if (!quiet)
                {
                    MessageBox.Show(I18n.T(
                        "Не знайдено HID-пристрій драйвера Stadia Rumble.\nПідключіть геймпад, щоб зберегти макроси в 0xE3.",
                        "Stadia Rumble driver HID device not found.\nConnect the controller to save macros to 0xE3.",
                        "Stadia Rumble HID-Gerät nicht gefunden.\nController verbinden, um Makros in 0xE3 zu speichern.",
                        "Не найдено HID-устройство драйвера Stadia Rumble.\nПодключите геймпад, чтобы сохранить макросы в 0xE3."),
                        I18n.T("Макроси драйвера (0xE3)", "Driver Macros (0xE3)", "Treiber-Makros (0xE3)", "Макросы драйвера (0xE3)"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            bool ok = Native.HidD_SetFeature(h, blob, blob.Length);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            Native.CloseHandle(h);

            if (!ok)
            {
                lock (_stateLock) { _driverMacroSupported = false; }
                AddLog(string.Format(I18n.T(
                    "[ERR] HidD_SetFeature(0xE3) Win32={0} — запустіть install.bat від адміністратора (оновити winstadia.dll).",
                    "[ERR] HidD_SetFeature(0xE3) Win32={0} — run install.bat as Administrator to update winstadia.dll.",
                    "[ERR] HidD_SetFeature(0xE3) Win32={0} — install.bat als Admin ausführen (winstadia.dll aktualisieren).",
                    "[ERR] HidD_SetFeature(0xE3) Win32={0} — запустите install.bat от администратора (обновить winstadia.dll)."), err));
                if (!quiet)
                {
                    MessageBox.Show(I18n.T(
                        "Поточна версія winstadia.dll ще не підтримує 0xE3.\nЗапустіть install.bat від імені адміністратора, щоб зібрати й оновити драйвер.",
                        "Installed winstadia.dll does not support 0xE3 yet.\nRun install.bat as Administrator to rebuild and update the driver.",
                        "Installierte winstadia.dll unterstützt 0xE3 noch nicht.\ninstall.bat als Administrator ausführen, um den Treiber zu aktualisieren.",
                        "Текущая версия winstadia.dll ещё не поддерживает 0xE3.\nЗапустите install.bat от имени администратора, чтобы пересобрать и обновить драйвер."),
                        I18n.T("Оновіть драйвер (0xE3)", "Update Driver (0xE3)", "Treiber aktualisieren (0xE3)", "Обновите драйвер (0xE3)"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            lock (_stateLock) { _driverMacroSupported = true; }
            AddLog(I18n.T(
                "[MACRO] Макроси збережено у winstadia.dll (0xE3) — можна закривати програму повністю!",
                "[MACRO] Macros saved inside winstadia.dll (0xE3) — safe to close Studio completely!",
                "[MACRO] Makros in winstadia.dll (0xE3) gespeichert — App kann komplett geschlossen werden!",
                "[MACRO] Макросы сохранены в winstadia.dll (0xE3) — программу можно закрывать полностью!"));
        }

        private DriverSettings GatherUiSettings()
        {
            DriverSettings s = new DriverSettings();
            s.StrongGainPct = _strongGainSlider.Value;
            s.WeakGainPct = _weakGainSlider.Value;
            s.StickDeadzonePct = _stickDzSlider.Value;
            s.TriggerDeadzonePct = _trigDzSlider.Value;
            s.TriggerMaxPct = _trigMaxSlider.Value;
            s.SwapSticks = _swapSticksCheck.Checked;
            s.InvertLy = _invertLyCheck.Checked;
            s.InvertRy = _invertRyCheck.Checked;
            s.BleIntervalMs = _bleIntervalSlider.Value;
            s.BlePostAckGapMs = _bleGapSlider.Value;
            s.BleMinDelta = _bleDeltaSlider.Value;
            s.TurboEnabled = _turboEnabledCheck == null || _turboEnabledCheck.Checked;
            s.TurboHoldMode = _turboHoldModeCheck != null && _turboHoldModeCheck.Checked;
            s.TurboHz = _turboHzSlider != null ? _turboHzSlider.Value : 12;
            s.TurboMask = _turboMaskLocal & 0x03FF;
            return s;
        }

        private void PushSettingsToDriver(string successLog)
        {
            PushSettingsToDriver(successLog, false);
        }

        private void PushSettingsToDriver(string successLog, bool quiet)
        {
            DriverSettings s = GatherUiSettings();
            byte[] blob = s.ToBlob();

            IntPtr h = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
            if (h == IntPtr.Zero)
            {
                if (!quiet)
                {
                    MessageBox.Show(I18n.T(
                        "Не знайдено HID-пристрій драйвера Stadia Rumble.\nПереконайтесь, що драйвер встановлено і геймпад підключено.",
                        "Stadia Rumble driver HID device not found.\nMake sure the driver is installed and the controller is connected.",
                        "Stadia Rumble HID-Treiber nicht gefunden.\nStellen Sie sicher, dass der Treiber installiert und der Controller verbunden ist.",
                        "Не найдено HID-устройство драйвера Stadia Rumble.\nУбедитесь, что драйвер установлен и геймпад подключён."),
                        I18n.T("Налаштування драйвера", "Driver Settings", "Treibereinstellungen", "Настройки драйвера"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            bool ok = Native.HidD_SetFeature(h, blob, blob.Length);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            Native.CloseHandle(h);

            if (!ok)
            {
                AddLog(string.Format(I18n.T(
                    "[ERR] HidD_SetFeature(0xE2) помилка Win32={0}. Перевстановіть драйвер 1.0.15 через install.bat.",
                    "[ERR] HidD_SetFeature(0xE2) Win32 error={0}. Reinstall driver 1.0.15 via install.bat.",
                    "[ERR] HidD_SetFeature(0xE2) Win32-Fehler={0}. Treiber 1.0.15 per install.bat neu installieren.",
                    "[ERR] HidD_SetFeature(0xE2) ошибка Win32={0}. Переустановите драйвер 1.0.15 через install.bat."), err));
                _tuneStatusLabel.Text = string.Format(I18n.T(
                    "Помилка запису 0xE2 (код {0}) — запустіть install.bat!",
                    "Write error 0xE2 (code {0}) — run install.bat!",
                    "Schreibfehler 0xE2 (Code {0}) — install.bat starten!",
                    "Ошибка записи 0xE2 (код {0}) — запустите install.bat!"), err);
                _tuneStatusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                if (!quiet)
                {
                    MessageBox.Show(I18n.T(
                        "Драйвер не прийняв налаштування (0xE2).\nЗапустіть install.bat від імені адміністратора, щоб встановити драйвер 1.0.15.",
                        "Driver rejected 0xE2 config.\nRun install.bat as Administrator to install driver 1.0.15.",
                        "Treiber hat 0xE2 abgelehnt.\nFühren Sie install.bat als Administrator aus (Version 1.0.15).",
                        "Драйвер не принял настройки (0xE2).\nЗапустите install.bat от имени администратора, чтобы установить драйвер 1.0.15."),
                        I18n.T("Оновіть драйвер (install.bat)", "Update Driver (install.bat)", "Treiber aktualisieren (install.bat)", "Обновите драйвер (install.bat)"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            lock (_stateLock)
            {
                _driverSettings = s;
                _driverSettingsSupported = true;
            }
            _lastObservedDriverTurboMask = s.TurboMask;

            if (_holdingRumble)
            {
                ApplyTestVibration(100, 100);
            }

            _tuneStatusLabel.Text = I18n.T(
                "Збережено в драйвері (0xE2) та реєстрі Windows!",
                "Saved inside driver (0xE2) & Windows Registry!",
                "Im Treiber (0xE2) & der Windows-Registry gespeichert!",
                "Сохранено в драйвере (0xE2) и реестре Windows!");
            _tuneStatusLabel.ForeColor = Color.FromArgb(52, 211, 153);
            AddLog(string.Format("{0} (Strong={1}%, Weak={2}%, StickDZ={3}%, BLE={4}ms, Turbo={5})",
                successLog, s.StrongGainPct, s.WeakGainPct, s.StickDeadzonePct, s.BleIntervalMs,
                DriverSettings.FormatTurboMask(s.TurboMask)));
        }

        private void ApplyBlePreset(int intervalMs, int gapMs, int delta)
        {
            string msUnit = I18n.T("мс", "ms", "ms", "мс");
            _bleIntervalSlider.Value = intervalMs;
            _bleGapSlider.Value = gapMs;
            _bleDeltaSlider.Value = delta;
            _bleIntervalLabel.Text = intervalMs + " " + msUnit;
            _bleGapLabel.Text = gapMs + " " + msUnit;
            _bleDeltaLabel.Text = delta.ToString();
            PushSettingsToDriver(I18n.T("Застосовано BLE-профіль", "Applied BLE preset", "BLE-Profil übernommen", "Применён BLE-профиль"));
        }

        private void ResetSettingsDefaults()
        {
            lock (_stateLock) { _driverSettings = new DriverSettings(); }
            SyncControlsFromDriverSettings();
            if (_driverSettingsSupported)
            {
                PushSettingsToDriver(I18n.T("Налаштування скинуто на стандартні", "Reset settings to defaults", "Auf Standardwerte zurückgesetzt", "Настройки сброшены на стандартные"));
            }
        }

        private void OnUiTick()
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return;

            bool conn;
            Native.XInputState st;
            DriverTelemetry tel;
            double hz, activeHz, lastMs, avgMs, maxMs;
            int spikes;
            bool cfgSupported;

            lock (_stateLock)
            {
                conn = _padConnected;
                st = _latestState;
                tel = _latestTelemetry;
                hz = _measuredHz;
                activeHz = _lastActiveHz;
                lastMs = _lastIntervalMs;
                avgMs = _avgIntervalMs;
                maxMs = _maxIntervalMs;
                spikes = _spikeCount;
                cfgSupported = _driverSettingsSupported;
            }

            _canvas.UpdateData(conn, st, tel, _stickDzSlider.Value);

            if (conn)
            {
                string tr = tel.Connected ? (tel.IsBluetooth ? "Bluetooth LE" : "USB") : "XInput";
                _statusPill.Text = string.Format(I18n.T("Підключено · {0}", "Connected · {0}", "Verbunden · {0}", "Подключено · {0}"), tr);
                _statusPill.BackColor = Color.FromArgb(6, 78, 59);
                _statusPill.ForeColor = Color.FromArgb(110, 231, 183);
            }
            else
            {
                _statusPill.Text = I18n.T("Геймпад не знайдено", "No controller", "Nicht verbunden", "Геймпад не найден");
                _statusPill.BackColor = Color.FromArgb(127, 29, 29);
                _statusPill.ForeColor = Color.FromArgb(254, 202, 202);
            }

            // Battery calculation (0xE1 byte[40] OR Windows PnP BLE Battery fallback)
            int effectiveBat = (tel.Connected && tel.BatteryPct <= 100) ? tel.BatteryPct : _winPnpBatteryPct;
            if (tel.Connected && !tel.IsBluetooth && effectiveBat < 0) effectiveBat = 100;

            if (effectiveBat >= 0)
            {
                _batteryPill.Text = string.Format(I18n.T("Заряд: {0}%", "Bat: {0}%", "Akku: {0}%", "Заряд: {0}%"), effectiveBat);
                _batteryPill.ForeColor = effectiveBat >= 30 ? Color.FromArgb(110, 231, 183) : Color.FromArgb(251, 191, 36);
            }
            else if (tel.Connected)
            {
                _batteryPill.Text = tel.IsBluetooth ? "BLE GATT" : "USB 100%";
                _batteryPill.ForeColor = Color.FromArgb(148, 163, 184);
            }
            else
            {
                _batteryPill.Text = I18n.T("Заряд: --", "Bat: --", "Akku: --", "Заряд: --");
                _batteryPill.ForeColor = Color.FromArgb(148, 163, 184);
            }

            // Critical error status
            bool hasCrit = tel.RumbleFailed || tel.DriverError != 0 || tel.RumbleError != 0;
            if (hasCrit)
            {
                _criticalBtn.Text = "CRITICAL ERR";
                _criticalBtn.BackColor = Color.FromArgb(185, 28, 28);
                _criticalBtn.ForeColor = Color.White;
            }
            else
            {
                _criticalBtn.Text = I18n.T("СТАН: ОК", "STATUS: OK", "STATUS: OK", "СОСТОЯНИЕ: ОК");
                _criticalBtn.BackColor = Color.FromArgb(31, 41, 55);
                _criticalBtn.ForeColor = Color.FromArgb(110, 231, 183);
            }

            double shownHz = hz > 0.5 ? hz : activeHz;
            _pollingLabel.Text = string.Format(I18n.T(
                "Опитування: {0:F0} Гц · Інтервал: {1:F1} / макс {2:F1} мс · Стрибки >45мс: {3}",
                "Polling: {0:F0} Hz · Gap: {1:F1} / max {2:F1} ms · Spikes >45ms: {3}",
                "Abfrage: {0:F0} Hz · Intervall: {1:F1} / Max {2:F1} ms · Spitzen >45ms: {3}",
                "Опрос: {0:F0} Гц · Интервал: {1:F1} / макс {2:F1} мс · Скачки >45мс: {3}"),
                shownHz, avgMs, maxMs, spikes);

            // 1 Hz BLE chart + Battery history sampling
            long now = Stopwatch.GetTimestamp();
            if (!_blePaused && ((now - _lastBleChartSampleTicks) / (double)Stopwatch.Frequency >= 1.0))
            {
                _lastBleChartSampleTicks = now;
                int latMs = tel.Connected && tel.LastLatencyMs > 0 ? tel.LastLatencyMs : (avgMs > 1.0 ? (int)Math.Round(avgMs) : 26);
                int wDelta = tel.RumbleWrites >= _prevWritesForChart ? (int)(tel.RumbleWrites - _prevWritesForChart) : 0;
                int fDelta = tel.RumbleFailures >= _prevFailsForChart ? (int)(tel.RumbleFailures - _prevFailsForChart) : 0;
                _prevWritesForChart = tel.RumbleWrites;
                _prevFailsForChart = tel.RumbleFailures;

                int rssiEst = -54;
                if (fDelta > 0) rssiEst = -82;
                else if (latMs > 45) rssiEst = -72;
                else rssiEst = -52 - Math.Min(16, latMs / 4);

                BleSamplePoint pt = new BleSamplePoint();
                pt.RssiDbm = rssiEst;
                pt.LatencyMs = latMs;
                pt.WritesDelta = wDelta;
                pt.FailuresDelta = fDelta;
                pt.TimeLabel = DateTime.Now.ToString("HH:mm:ss");
                _bleSamples.Add(pt);
                if (_bleSamples.Count > 32) _bleSamples.RemoveAt(0);
                _bleChart.SetPoints(_bleSamples);

                _bleKpiLabel.Text = string.Format(I18n.T(
                    "RSSI: {0} dBm | Затримка BLE: {1} мс | Усього записів: {2} | Збоїв: {3}",
                    "RSSI: {0} dBm | BLE latency: {1} ms | Total writes: {2} | Failures: {3}",
                    "RSSI: {0} dBm | BLE-Latenz: {1} ms | Schreibvorgänge: {2} | Fehler: {3}",
                    "RSSI: {0} dBm | Задержка BLE: {1} мс | Всего записей: {2} | Сбоев: {3}"),
                    rssiEst, latMs, tel.RumbleWrites, tel.RumbleFailures);
                _bleKpiLabel.ForeColor = fDelta > 0 || latMs > 45 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(110, 231, 183);
            }

            if ((now - _lastWinBatteryPollTicks) / (double)Stopwatch.Frequency >= 12.0)
            {
                _lastWinBatteryPollTicks = now;
                ScanWindowsBatteryAsync();
            }

            if ((now - _lastBatterySampleTicks) / (double)Stopwatch.Frequency >= 5.0)
            {
                _lastBatterySampleTicks = now;
                int shownPct = effectiveBat >= 0 ? effectiveBat : 85;
                BatteryPoint bp = new BatteryPoint();
                bp.Percent = shownPct;
                bp.VoltageV = 3.40 + (shownPct / 100.0) * 0.78;
                bp.TimeLabel = DateTime.Now.ToString("HH:mm");
                _batteryHistory.Add(bp);
                if (_batteryHistory.Count > 24) _batteryHistory.RemoveAt(0);
                _batteryChart.SetHistory(_batteryHistory);

                int motorMa = (int)Math.Round(((tel.StrongByte + tel.WeakByte) / 510.0) * 185.0);
                string hrUnit = I18n.T("год", "h", "Std", "ч");
                _powerSummaryLabel.Text = string.Format(I18n.T(
                    "Рівень заряду: {0} | Джерело: {1} | Напруга: {2:F2} V\r\nНавантаження вібромоторів: {3} мА | Орієнтовний залишок: {4}",
                    "Battery level: {0} | Source: {1} | Voltage: {2:F2} V\r\nRumble motor draw: {3} mA | Est. remaining: {4}",
                    "Akkustand: {0} | Quelle: {1} | Spannung: {2:F2} V\r\nMotorlast: {3} mA | Geschätzte Laufzeit: {4}",
                    "Уровень заряда: {0} | Источник: {1} | Напряжение: {2:F2} V\r\nНагрузка вибромоторов: {3} мА | Ориентировочный остаток: {4}"),
                    effectiveBat >= 0 ? (effectiveBat + "%") : I18n.T("GATT (очікування Windows BLE)", "GATT (waiting Windows BLE)", "GATT (warte auf Windows BLE)", "GATT (ожидание Windows BLE)"),
                    tel.Connected
                        ? (tel.IsBluetooth ? I18n.T("Акумулятор (BLE)", "Battery (BLE)", "Akku (BLE)", "Аккумулятор (BLE)") : I18n.T("Живлення / зарядка по USB", "USB Power / Charging", "USB-Strom / Laden", "Питание / зарядка по USB"))
                        : I18n.T("Автономний режим", "Offline", "Getrennt", "Автономный режим"),
                    bp.VoltageV,
                    motorMa,
                    effectiveBat >= 0 ? string.Format("~{0:F1} {1}", (effectiveBat / 100.0) * 8.0, hrUnit) : "--");
            }

            if (tel.Connected)
            {
                _rawHexLabel.Text = string.Format("Raw HID: {0}", string.IsNullOrEmpty(tel.RawHex) ? I18n.T("(немає звітів)", "(no reports)", "(keine Berichte)", "(нет отчётов)") : tel.RawHex);
                _telemetryLabel.Text = string.Format(I18n.T(
                    "0xE1: {0} | RTT: {1} мс | Мотори L/R: {2}/{3} | Записів: {4} (збоїв: {5})\r\nDriverErr: 0x{6:X8} | RumbleErr: 0x{7:X8}",
                    "0xE1: {0} | RTT: {1} ms | Motors L/R: {2}/{3} | Writes: {4} (fails: {5})\r\nDriverErr: 0x{6:X8} | RumbleErr: 0x{7:X8}",
                    "0xE1: {0} | RTT: {1} ms | Motoren L/R: {2}/{3} | Writes: {4} (Fehler: {5})\r\nDriverErr: 0x{6:X8} | RumbleErr: 0x{7:X8}",
                    "0xE1: {0} | RTT: {1} мс | Моторы L/R: {2}/{3} | Записей: {4} (сбоев: {5})\r\nDriverErr: 0x{6:X8} | RumbleErr: 0x{7:X8}"),
                    tel.IsBluetooth ? "Bluetooth LE" : "USB",
                    tel.LastLatencyMs,
                    tel.StrongByte, tel.WeakByte,
                    tel.RumbleWrites, tel.RumbleFailures,
                    tel.DriverError, tel.RumbleError);

                if (hasCrit)
                {
                    _criticalGuideBox.BackColor = Color.FromArgb(58, 20, 26);
                    _criticalGuideTitle.Text = I18n.T(
                        "CRITICAL ERROR ВИЯВЛЕНО — АВТОМАТИЧНА ІНСТРУКЦІЯ З ВИПРАВЛЕННЯ",
                        "CRITICAL ERROR DETECTED — AUTOMATIC REPAIR GUIDE",
                        "KRITISCHER FEHLER ERKANNT — AUTOMATISCHE REPARATURHILFE",
                        "CRITICAL ERROR ОБНАРУЖЕН — АВТОМАТИЧЕСКАЯ ИНСТРУКЦИЯ ПО ИСПРАВЛЕНИЮ");
                    _criticalGuideTitle.ForeColor = Color.FromArgb(254, 202, 202);
                    _criticalGuideBody.Text = DescribeCriticalFix(tel.DriverError, tel.RumbleError, tel.RumbleFailed).Trim();
                }
                else if (!string.IsNullOrEmpty(_customDumpAnalysis))
                {
                    _criticalGuideBox.BackColor = Color.FromArgb(24, 34, 48);
                    _criticalGuideTitle.Text = I18n.T(
                        "РЕЗУЛЬТАТ АНАЛІЗУ ДАМПА / ЖУРНАЛУ WINDOWS",
                        "DUMP / WINDOWS EVENT LOG ANALYSIS RESULT",
                        "DUMP- / WINDOWS-PROTOKOLL-ANALYSEERGEBNIS",
                        "РЕЗУЛЬТАТ АНАЛИЗА ДАМПА / ЖУРНАЛА WINDOWS");
                    _criticalGuideTitle.ForeColor = Color.FromArgb(147, 197, 253);
                    _criticalGuideBody.Text = _customDumpAnalysis;
                }
                else
                {
                    _criticalGuideBox.BackColor = Color.FromArgb(16, 42, 34);
                    _criticalGuideTitle.Text = I18n.T(
                        "СТАН ДРАЙВЕРА: КРИТИЧНИХ ПОМИЛОК НЕ ВИЯВЛЕНО",
                        "DRIVER HEALTH: NO CRITICAL FAULTS DETECTED",
                        "TREIBERZUSTAND: KEINE KRITISCHEN FEHLER ERKANNT",
                        "СОСТОЯНИЕ ДРАЙВЕРА: КРИТИЧЕСКИХ ОШИБОК НЕ ОБНАРУЖЕНО");
                    _criticalGuideTitle.ForeColor = Color.FromArgb(110, 231, 183);
                    _criticalGuideBody.Text = string.Format(I18n.T(
                        "Збоїв GATT = 0 · Успішних записів = {0} · Поточна затримка = {1} мс.",
                        "GATT failures = 0 · Successful writes = {0} · Current latency = {1} ms.",
                        "GATT-Fehler = 0 · Erfolgreiche Schreibvorgänge = {0} · Aktuelle Latenz = {1} ms.",
                        "Сбоев GATT = 0 · Успешных записей = {0} · Текущая задержка = {1} мс."),
                        tel.RumbleWrites, tel.LastLatencyMs);
                }

                if (cfgSupported)
                {
                    _settingsSupportBanner.Text = string.Format(I18n.T(
                        "Профіль драйвера 0xE2 активний (L={0}% · R={1}% · Мертва зона стіка {2}% · BLE {3} мс).",
                        "Driver profile 0xE2 active (L={0}% · R={1}% · Stick deadzone {2}% · BLE {3} ms).",
                        "Treiberprofil 0xE2 aktiv (L={0}% · R={1}% · Stick-Totzone {2}% · BLE {3} ms).",
                        "Профиль драйвера 0xE2 активен (L={0}% · R={1}% · Мёртвая зона стика {2}% · BLE {3} мс)."),
                        _strongGainSlider.Value, _weakGainSlider.Value, _stickDzSlider.Value, _bleIntervalSlider.Value);
                    _settingsSupportBanner.ForeColor = Color.FromArgb(52, 211, 153);
                }
                else
                {
                    _settingsSupportBanner.Text = I18n.T(
                        "Драйвер 0xE2 недоступний (встановлено старішу версію winstadia.dll).\r\nЗапустіть install.bat від імені адміністратора, щоб оновити драйвер 1.0.15.",
                        "Driver 0xE2 unavailable (older winstadia.dll installed).\r\nRun install.bat as Administrator to upgrade to v1.0.15.",
                        "Treiber 0xE2 nicht verfügbar (ältere winstadia.dll).\r\ninstall.bat als Administrator für v1.0.15 ausführen.",
                        "Драйвер 0xE2 недоступен (установлена устаревшая версия winstadia.dll).\r\nЗапустите install.bat от имени администратора, чтобы обновить драйвер до 1.0.15.");
                    _settingsSupportBanner.ForeColor = Color.FromArgb(251, 191, 36);
                }
            }
            else
            {
                _rawHexLabel.Text = I18n.T(
                    "Raw HID: драйвер Stadia Rumble не знайдено або контролер не підключено.",
                    "Raw HID: Stadia Rumble driver not found or controller disconnected.",
                    "Raw HID: Stadia Rumble-Treiber nicht gefunden oder Controller getrennt.",
                    "Raw HID: драйвер Stadia Rumble не найден или контроллер не подключён.");
                _telemetryLabel.Text = I18n.T(
                    "HID-звіт 0xE1 недоступний.\r\nПідключіть контролер або запустіть install.bat для встановлення драйвера.",
                    "HID report 0xE1 unavailable.\r\nConnect your controller or run install.bat to install the driver.",
                    "HID-Bericht 0xE1 nicht verfügbar.\r\nController verbinden oder install.bat ausführen.",
                    "HID-отчёт 0xE1 недоступен.\r\nПодключите контроллер или запустите install.bat для установки драйвера.");
                _settingsSupportBanner.Text = I18n.T(
                    "Підключіть Stadia Controller, щоб зчитати й змінити налаштування драйвера (0xE2).",
                    "Connect Stadia Controller to read and configure driver settings (0xE2).",
                    "Stadia Controller verbinden, um Treibereinstellungen (0xE2) zu lesen.",
                    "Подключите Stadia Controller, чтобы считать и изменить настройки драйвера (0xE2).");
                _settingsSupportBanner.ForeColor = Color.FromArgb(148, 163, 184);
                if (!string.IsNullOrEmpty(_customDumpAnalysis))
                {
                    _criticalGuideBox.BackColor = Color.FromArgb(24, 34, 48);
                    _criticalGuideTitle.Text = I18n.T(
                        "РЕЗУЛЬТАТ АНАЛІЗУ ДАМПА / ЖУРНАЛУ WINDOWS",
                        "DUMP / WINDOWS EVENT LOG ANALYSIS RESULT",
                        "DUMP- / WINDOWS-PROTOKOLL-ANALYSEERGEBNIS",
                        "РЕЗУЛЬТАТ АНАЛИЗА ДАМПА / ЖУРНАЛА WINDOWS");
                    _criticalGuideTitle.ForeColor = Color.FromArgb(147, 197, 253);
                    _criticalGuideBody.Text = _customDumpAnalysis;
                }
            }

            // Live Assistant button badge + Hardware Turbo mask sync
            long hapticUntil = Interlocked.Read(ref _turboHapticUntilTicks);
            if (hapticUntil != 0 && now >= hapticUntil)
            {
                if (Interlocked.CompareExchange(ref _turboHapticUntilTicks, 0, hapticUntil) == hapticUntil)
                {
                    if (!_uiRumbleActive)
                    {
                        int stopSlot;
                        lock (_stateLock) { stopSlot = _activeSlot; }
                        Native.SetVibration(stopSlot, 0, 0);
                    }
                }
            }

            if (tel.Connected && tel.TurboMask != _lastObservedDriverTurboMask)
            {
                _lastObservedDriverTurboMask = tel.TurboMask;
                _turboMaskLocal = tel.TurboMask & 0x03FF;
                RefreshTurboPillVisuals();
            }

            if (tel.Connected)
            {
                bool macroSup;
                lock (_stateLock) { macroSup = _driverMacroSupported; }
                if (macroSup)
                {
                    _macroDriverStatusLbl.Text = I18n.T(
                        "Статус 0xE3: АКТИВНО У ДРАЙВЕРІ — вікно можна повністю закривати!",
                        "0xE3 Status: ACTIVE IN DRIVER — you can completely close this window!",
                        "0xE3-Status: IM TREIBER AKTIV — Fenster kann komplett geschlossen werden!",
                        "Статус 0xE3: АКТИВНО В ДРАЙВЕРЕ — окно можно полностью закрывать!");
                    _macroDriverStatusLbl.ForeColor = Color.FromArgb(110, 231, 183);
                }
                else
                {
                    _macroDriverStatusLbl.Text = I18n.T(
                        "0xE3 недоступний — запустіть install.bat від адміністратора, щоб оновити winstadia.dll.",
                        "0xE3 unavailable — run install.bat as Administrator to update winstadia.dll.",
                        "0xE3 nicht verfügbar — install.bat als Admin zum Update von winstadia.dll starten.",
                        "0xE3 недоступен — запустите install.bat от администратора, чтобы обновить winstadia.dll.");
                    _macroDriverStatusLbl.ForeColor = Color.FromArgb(251, 191, 36);
                }
            }
            else
            {
                _macroDriverStatusLbl.Text = I18n.T(
                    "Підключіть контролер, щоб синхронізувати макроси з драйвером 0xE3.",
                    "Connect controller to sync macros with the 0xE3 driver.",
                    "Controller verbinden, um Makros mit dem 0xE3-Treiber zu synchronisieren.",
                    "Подключите контроллер, чтобы синхронизировать макросы с драйвером 0xE3.");
                _macroDriverStatusLbl.ForeColor = Color.FromArgb(148, 163, 184);
            }

            bool physAssist = tel.Connected && tel.RawLen >= 3 && (tel.RawB2 & 0x02) != 0;
            _turboAssistStateBadge.Text = physAssist
                ? I18n.T("Assistant: НАТ.", "Assistant: DOWN", "Assistant: GEDR.", "Assistant: НАЖ.")
                : I18n.T("Assistant: Очік.", "Assistant: Idle", "Assistant: Bereit", "Assistant: Ожид.");
            _turboAssistStateBadge.BackColor = physAssist ? Color.FromArgb(6, 95, 70) : Color.FromArgb(38, 45, 60);
            _turboAssistStateBadge.ForeColor = physAssist ? Color.FromArgb(110, 231, 183) : Color.FromArgb(148, 163, 184);

            string lastTurbo;
            string lastMacro;
            lock (_turboEventLock)
            {
                lastTurbo = _lastTurboEventText;
                lastMacro = _lastMacroEventText;
            }
            if (!string.IsNullOrEmpty(lastTurbo) && !string.Equals(_turboHwStatusLabel.Text, lastTurbo, StringComparison.Ordinal))
            {
                _turboHwStatusLabel.Text = lastTurbo;
            }
            if (_macroLastFiredLabel != null && !string.IsNullOrEmpty(lastMacro) &&
                !string.Equals(_macroLastFiredLabel.Text, lastMacro, StringComparison.Ordinal))
            {
                _macroLastFiredLabel.Text = lastMacro;
            }
        }

        private void OnGainSliderChanged(bool fromStrong)
        {
            if (_suppressSliderEvents) return;
            _strongGainLabel.Text = _strongGainSlider.Value + "%";
            _weakGainLabel.Text = _weakGainSlider.Value + "%";
            if (_syncMotorsCheck.Checked)
            {
                _suppressSliderEvents = true;
                int v = fromStrong ? _strongGainSlider.Value : _weakGainSlider.Value;
                _strongGainSlider.Value = v;
                _weakGainSlider.Value = v;
                _strongGainLabel.Text = v + "%";
                _weakGainLabel.Text = v + "%";
                _suppressSliderEvents = false;
            }
            if (_driverSettingsSupported && _settingsSupportBanner != null)
            {
                _settingsSupportBanner.Text = string.Format(I18n.T(
                    "Є незбережені зміни (L={0}% · R={1}%) — натисніть «Зберегти силу моторів у драйвер (0xE2)».",
                    "Unsaved motor changes (L={0}% · R={1}%) — click \"Save motor gain to driver (0xE2)\".",
                    "Ungespeicherte Änderungen (L={0}% · R={1}%) — auf „Motorstärke im Treiber speichern (0xE2)“ klicken.",
                    "Есть несохранённые изменения (L={0}% · R={1}%) — нажмите «Сохранить силу моторов в драйвер (0xE2)»."),
                    _strongGainSlider.Value, _weakGainSlider.Value);
                _settingsSupportBanner.ForeColor = Color.FromArgb(251, 191, 36);
            }
            if (_holdingRumble)
            {
                if (_driverSettingsSupported) ApplyTestVibration(100, 100);
                else ApplyTestVibration(_strongGainSlider.Value, _weakGainSlider.Value);
            }
        }

        private void ApplyTestVibration(int strongPct, int weakPct)
        {
            _desiredStrong = strongPct;
            _desiredWeak = weakPct;
            int slot;
            lock (_stateLock) { slot = _activeSlot; }
            uint err = Native.SetVibration(slot, strongPct, weakPct);
            if (err != Native.ErrorSuccess && (strongPct > 0 || weakPct > 0))
            {
                AddLog(string.Format("[ERR] XInputSetState -> {0}", err));
            }
        }

        private void StartPulseTest()
        {
            StopAnimOnly();
            _holdingRumble = false;
            _holdBtn.BackColor = Color.FromArgb(51, 65, 85);
            _animMode = "pulse";
            _uiRumbleActive = true;
            _animStartTicks = Stopwatch.GetTimestamp();
            _animStep = 0;
            if (_driverSettingsSupported)
            {
                byte[] blob = GatherUiSettings().ToBlob();
                IntPtr h = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
                if (h != IntPtr.Zero) { Native.HidD_SetFeature(h, blob, blob.Length); Native.CloseHandle(h); }
                ApplyTestVibration(100, 100);
            }
            else
            {
                ApplyTestVibration(_strongGainSlider.Value, _weakGainSlider.Value);
            }
            AddLog(string.Format(I18n.T("Запущено імпульс 1.5с (L={0}% R={1}%).", "Started 1.5s pulse test (L={0}% R={1}%).", "1.5s-Impulstest gestartet (L={0}% R={1}%).", "Запущен импульс 1.5с (L={0}% R={1}%)."),
                _strongGainSlider.Value, _weakGainSlider.Value));
            _animTimer.Start();
        }

        private void StartSweepTest()
        {
            StopAnimOnly();
            _holdingRumble = false;
            _holdBtn.BackColor = Color.FromArgb(51, 65, 85);
            _animMode = "sweep";
            _uiRumbleActive = true;
            _animStartTicks = Stopwatch.GetTimestamp();
            _animStep = 0;
            AddLog(I18n.T("Запущено плавну хвилю вібрації (10% -> 100% -> 0%) для тесту BLE.", "Started BLE rumble sweep test (10% -> 100% -> 0%).", "Sanfte BLE-Vibrationswelle gestartet (10% -> 100% -> 0%).", "Запущена плавная волна вибрации (10% -> 100% -> 0%) для теста BLE."));
            _animTimer.Start();
        }

        private void ToggleHoldRumble()
        {
            StopAnimOnly();
            _holdingRumble = !_holdingRumble;
            _uiRumbleActive = _holdingRumble;
            if (_holdingRumble)
            {
                _holdBtn.BackColor = Color.FromArgb(16, 185, 129);
                if (_driverSettingsSupported) ApplyTestVibration(100, 100);
                else ApplyTestVibration(_strongGainSlider.Value, _weakGainSlider.Value);
                AddLog(string.Format(I18n.T("Постійна вібрація увімкнена L={0}% R={1}%.", "Continuous rumble enabled L={0}% R={1}%.", "Dauervibration aktiviert L={0}% R={1}%.", "Постоянная вибрация включена L={0}% R={1}%."),
                    _strongGainSlider.Value, _weakGainSlider.Value));
            }
            else
            {
                _holdBtn.BackColor = Color.FromArgb(51, 65, 85);
                ApplyTestVibration(0, 0);
                AddLog(I18n.T("Постійну вібрацію вимкнено.", "Continuous rumble stopped.", "Dauervibration gestoppt.", "Постоянная вибрация отключена."));
            }
        }

        private void StartAutoDiagnostic()
        {
            StopAnimOnly();
            _holdingRumble = false;
            _uiRumbleActive = false;
            _holdBtn.BackColor = Color.FromArgb(51, 65, 85);
            DriverTelemetry tel;
            int spikes;
            bool conn;
            lock (_stateLock)
            {
                conn = _padConnected;
                tel = _latestTelemetry;
                spikes = _spikeCount;
            }
            if (!conn)
            {
                AddLog(I18n.T("[DIAG] Контролер не підключено — спочатку підключіть Stadia Controller.", "[DIAG] No controller connected — pair Stadia Controller first.", "[DIAG] Kein Controller verbunden — bitte Stadia Controller koppeln.", "[DIAG] Контроллер не подключён — сначала подключите Stadia Controller."));
                MessageBox.Show(I18n.T(
                    "Контролер не підключено до XInput. Перевірте Bluetooth або USB.",
                    "Controller not connected to XInput. Check Bluetooth or USB.",
                    "Controller nicht an XInput verbunden. Bluetooth oder USB prüfen.",
                    "Контроллер не подключён к XInput. Проверьте Bluetooth или USB."),
                    I18n.T("Автотест (5с)", "Auto Test (5s)", "Auto-Test (5s)", "Автотест (5с)"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _diagStartWrites = tel.RumbleWrites;
            _diagStartFailures = tel.RumbleFailures;
            _diagStartSpikes = spikes;
            _animMode = "diag";
            _uiRumbleActive = true;
            _animStartTicks = Stopwatch.GetTimestamp();
            _animStep = 0;
            _autoDiagBtn.Enabled = false;
            AddLog(I18n.T("[DIAG] Старт 5-секундного авто-тесту (покрутіть стіки для перевірки стабільності)...", "[DIAG] Starting 5s auto-test (move sticks to verify BLE jitter)...", "[DIAG] Starte 5s-Autotest (Sticks bewegen zum Prüfen)...", "[DIAG] Старт 5-секундного автотеста (покрутите стики для проверки стабильности)..."));
            _animTimer.Start();
        }

        private void OnAnimTick()
        {
            double elapsedSec = (double)(Stopwatch.GetTimestamp() - _animStartTicks) / Stopwatch.Frequency;
            if (_animMode == "pulse")
            {
                if (elapsedSec >= 1.5) StopAllRumble(I18n.T("Імпульс 1.5с завершено.", "1.5s pulse complete.", "1.5s-Impuls beendet.", "Импульс 1.5с завершён."));
                return;
            }
            if (_animMode == "sweep")
            {
                if (elapsedSec >= 3.6)
                {
                    StopAllRumble(I18n.T("Плавну хвилю завершено.", "Rumble sweep complete.", "BLE-Welle beendet.", "Плавная волна завершена."));
                    return;
                }
                double phase = elapsedSec / 1.8;
                int pct = phase <= 1.0 ? (int)(10 + phase * 90) : (int)(100 - (phase - 1.0) * 90);
                ApplyTestVibration(pct, pct);
                return;
            }
            if (_animMode == "diag")
            {
                if (elapsedSec < 1.2)
                {
                    if (_animStep != 1) { _animStep = 1; ApplyTestVibration(65, 0); AddLog(I18n.T("[DIAG] Фаза 1/3: лівий мотор 65%...", "[DIAG] Phase 1/3: left motor 65%...", "[DIAG] Phase 1/3: linker Motor 65%...", "[DIAG] Фаза 1/3: левый мотор 65%...")); }
                }
                else if (elapsedSec < 2.4)
                {
                    if (_animStep != 2) { _animStep = 2; ApplyTestVibration(0, 65); AddLog(I18n.T("[DIAG] Фаза 2/3: правий мотор 65%...", "[DIAG] Phase 2/3: right motor 65%...", "[DIAG] Phase 2/3: rechter Motor 65%...", "[DIAG] Фаза 2/3: правый мотор 65%...")); }
                }
                else if (elapsedSec < 4.4)
                {
                    if (_animStep != 3) { _animStep = 3; AddLog(I18n.T("[DIAG] Фаза 3/3: плавна зміна навантаження BLE...", "[DIAG] Phase 3/3: smooth BLE rumble ramp...", "[DIAG] Phase 3/3: sanfter BLE-Lasttest...", "[DIAG] Фаза 3/3: плавное изменение нагрузки BLE...")); }
                    int pct = (int)(25 + ((elapsedSec - 2.4) / 2.0) * 75);
                    ApplyTestVibration(pct, 100 - pct / 2);
                }
                else if (elapsedSec < 5.0)
                {
                    if (_animStep != 4) { _animStep = 4; ApplyTestVibration(0, 0); }
                }
                else
                {
                    StopAnimOnly();
                    _autoDiagBtn.Enabled = true;
                    DriverTelemetry tel;
                    int spikes;
                    double maxMs;
                    lock (_stateLock)
                    {
                        tel = _latestTelemetry;
                        spikes = _spikeCount;
                        maxMs = _maxIntervalMs;
                    }
                    uint dWrites = tel.RumbleWrites >= _diagStartWrites ? tel.RumbleWrites - _diagStartWrites : 0;
                    uint dFails = tel.RumbleFailures >= _diagStartFailures ? tel.RumbleFailures - _diagStartFailures : 0;
                    int dSpikes = spikes - _diagStartSpikes;
                    AddLog(string.Format(I18n.T(
                        "[DIAG] РЕЗУЛЬТАТ: Записів=+{0}, Збоїв=+{1}, Стрибків вводу=+{2}, Макс={3:F1} мс, Err=0x{4:X8}",
                        "[DIAG] RESULT: Writes=+{0}, Fails=+{1}, Spikes=+{2}, Max={3:F1} ms, Err=0x{4:X8}",
                        "[DIAG] ERGEBNIS: Writes=+{0}, Fehler=+{1}, Spitzen=+{2}, Max={3:F1} ms, Err=0x{4:X8}",
                        "[DIAG] РЕЗУЛЬТАТ: Записей=+{0}, Сбоев=+{1}, Скачков ввода=+{2}, Макс={3:F1} мс, Err=0x{4:X8}"),
                        dWrites, dFails, dSpikes, maxMs, tel.RumbleError));
                }
            }
        }

        private void StopAnimOnly()
        {
            _animTimer.Stop();
            _animMode = "";
            _uiRumbleActive = _holdingRumble;
            _autoDiagBtn.Enabled = true;
        }

        private void StopAllRumble(string reason)
        {
            StopAnimOnly();
            _holdingRumble = false;
            _uiRumbleActive = false;
            _holdBtn.BackColor = Color.FromArgb(51, 65, 85);
            ApplyTestVibration(0, 0);
            if (!string.IsNullOrEmpty(reason)) AddLog(reason);
        }

        private string[] SnapshotLogLines()
        {
            lock (_logLock) { return _logLines.ToArray(); }
        }

        private void CopyLogToClipboard()
        {
            _copyLogBtn.Enabled = false;
            string[] snapshot = SnapshotLogLines();
            Thread t = new Thread(() =>
            {
                string report = BuildFullDiagnosticText(snapshot);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _copyLogBtn.Enabled = true;
                        Clipboard.SetText(report);
                        AddLog(I18n.T("Повний діагностичний звіт скопійовано в буфер обміну.", "Full diagnostic report copied to clipboard.", "Diagnosebericht in Zwischenablage kopiert.", "Полный диагностический отчёт скопирован в буфер обмена."));
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ExportLogAsync()
        {
            _saveLogBtn.Enabled = false;
            _saveLogBtn.Text = I18n.T("Збір звіту…", "Collecting…", "Sammeln…", "Сбор отчёта…");
            string[] snapshot = SnapshotLogLines();
            Thread t = new Thread(() =>
            {
                string content = BuildFullDiagnosticText(snapshot);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _saveLogBtn.Enabled = true;
                        _saveLogBtn.Text = I18n.T("Зберегти лог (.txt)", "Save Log (.txt)", "Log (.txt)", "Сохранить лог (.txt)");

                        SaveFileDialog sfd = new SaveFileDialog();
                        sfd.Filter = "Text Log (*.txt)|*.txt|All files (*.*)|*.*";
                        sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        sfd.FileName = string.Format("StadiaRumble_Log_{0}.txt", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        if (sfd.ShowDialog(this) == DialogResult.OK)
                        {
                            File.WriteAllText(sfd.FileName, content, new UTF8Encoding(false));
                            AddLog(string.Format(I18n.T("Лог збережено: {0}", "Log saved: {0}", "Log gespeichert: {0}", "Лог сохранён: {0}"), sfd.FileName));
                            DialogResult openAsk = MessageBox.Show(
                                string.Format(I18n.T("Лог збережено:\n{0}\n\nВідкрити файл зараз?", "Log saved:\n{0}\n\nOpen file now?", "Log gespeichert:\n{0}\n\nDatei jetzt öffnen?", "Лог сохранён:\n{0}\n\nОткрыть файл сейчас?"), sfd.FileName),
                                "Stadia Rumble Log", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                            if (openAsk == DialogResult.Yes)
                            {
                                try { Process.Start("notepad.exe", "\"" + sfd.FileName + "\""); } catch { }
                            }
                        }
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string DescribeCriticalFix(uint drvErr, uint rmbErr, bool failed)
        {
            uint code = failed && rmbErr != 0 ? rmbErr : (drvErr != 0 ? drvErr : rmbErr);
            if (code == 0 && !failed) return "";
            if (code == 0x80070005)
                return I18n.T(
                    "1) Помилка 0x80070005 (Доступ до GATT заборонено): закрийте ігри/Steam, перезапустіть Bluetooth і перепідключіть контролер.\r\n2) Якщо не допомогло — запустіть install.bat від імені адміністратора.",
                    "1) Error 0x80070005 (GATT Access Denied): close games/Steam, toggle Bluetooth and re-pair controller.\r\n2) Otherwise run install.bat as Administrator.",
                    "1) Fehler 0x80070005 (GATT-Zugriff verweigert): Spiele/Steam schließen, Bluetooth neu starten & Controller neu koppeln.\r\n2) Sonst install.bat als Admin ausführen.",
                    "1) Ошибка 0x80070005 (Доступ к GATT запрещён): закройте игры/Steam, перезапустите Bluetooth и переподключите контроллер.\r\n2) Если не помогло — запустите install.bat от имени администратора.");
            if (code == 0x80000013 || code == 0x80070490 || code == 0x8007045D)
                return string.Format(I18n.T(
                    "1) Розрив GATT-сесії (0x{0:X8}): контролер заснув або розрядився.\r\n2) Утримуйте кнопку Stadia 2с, перевірте заряд або підключіть USB-кабель.",
                    "1) GATT session disconnected (0x{0:X8}): controller went idle or low battery.\r\n2) Hold Stadia button 2s, check battery or plug USB cable.",
                    "1) GATT-Sitzung getrennt (0x{0:X8}): Controller im Standby oder Akku leer.\r\n2) Stadia-Taste 2s halten, Akku prüfen oder USB anschließen.",
                    "1) Разрыв GATT-сессии (0x{0:X8}): контроллер уснул или разрядился.\r\n2) Удерживайте кнопку Stadia 2с, проверьте заряд или подключите USB-кабель."), code);
            if (code == 0x80070422)
                return I18n.T(
                    "1) Службу Bluetooth bthserv зупинено (0x80070422).\r\n2) Відкрийте PowerShell від адміністратора та виконайте: sc start bthserv",
                    "1) Bluetooth Support Service bthserv is stopped (0x80070422).\r\n2) Open elevated PowerShell and run: sc start bthserv",
                    "1) Bluetooth-Dienst bthserv gestoppt (0x80070422).\r\n2) Admin-PowerShell öffnen und ausführen: sc start bthserv",
                    "1) Служба Bluetooth bthserv остановлена (0x80070422).\r\n2) Откройте PowerShell от администратора и выполните: sc start bthserv");
            if (code == 0xC00000BB)
                return I18n.T(
                    "1) Непідтримуваний запит (0xC00000BB): старіша версія winstadia.dll.\r\n2) Запустіть install.bat від адміністратора, щоб оновити драйвер до 1.0.15.",
                    "1) Unsupported request (0xC00000BB): older winstadia.dll active.\r\n2) Run install.bat as Administrator to upgrade driver to v1.0.15.",
                    "1) Nicht unterstützte Anfrage (0xC00000BB): ältere winstadia.dll aktiv.\r\n2) install.bat als Admin ausführen, um Treiber v1.0.15 zu installieren.",
                    "1) Неподдерживаемый запрос (0xC00000BB): устаревшая версия winstadia.dll.\r\n2) Запустите install.bat от администратора, чтобы обновить драйвер до 1.0.15.");
            return string.Format(I18n.T(
                "1) Критичний код 0x{0:X8}: вимкніть/увімкніть Bluetooth у Windows та перепідключіть геймпад.\r\n2) Натисніть 'Сканувати дампи Windows' або збережіть лог (.txt).",
                "1) Critical code 0x{0:X8}: toggle Windows Bluetooth and re-pair your controller.\r\n2) Click 'Scan Windows dumps' or save the log (.txt).",
                "1) Kritischer Code 0x{0:X8}: Windows-Bluetooth neu starten & Controller neu verbinden.\r\n2) 'Windows-Dumps scannen' klicken oder Log (.txt) speichern.",
                "1) Критический код 0x{0:X8}: выключите/включите Bluetooth в Windows и переподключите геймпад.\r\n2) Нажмите 'Сканировать дампы Windows' или сохраните лог (.txt)."), code);
        }

        private string BuildFullDiagnosticText(string[] logSnapshot)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==========================================================");
            sb.AppendLine("  STADIA RUMBLE DIAGNOSTIC LOG (v1.0.15)");
            sb.AppendLine("==========================================================");
            sb.AppendLine(string.Format("Timestamp: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")));
            sb.AppendLine(string.Format("Windows:   {0}", GetWindowsSummary()));
            sb.AppendLine(string.Format("DPI Scale: {0:F2}x", _dpi));
            sb.AppendLine();

            bool conn;
            DriverTelemetry tel;
            DriverSettings cfg;
            bool cfgSupported;
            double hz, activeHz, avgMs, maxMs;
            int spikes, slot;
            lock (_stateLock)
            {
                conn = _padConnected;
                tel = _latestTelemetry;
                cfg = _driverSettings;
                cfgSupported = _driverSettingsSupported;
                hz = _measuredHz;
                activeHz = _lastActiveHz;
                avgMs = _avgIntervalMs;
                maxMs = _maxIntervalMs;
                spikes = _spikeCount;
                slot = _activeSlot;
            }

            sb.AppendLine("--- XInput & Polling ---");
            sb.AppendLine(string.Format("Active Slot:   #{0} (Connected={1})", slot + 1, conn));
            sb.AppendLine(string.Format("Polling Rate:  {0:F1} Hz (last active={1:F1} Hz)", hz, activeHz));
            sb.AppendLine(string.Format("Input Gap:     avg={0:F2} ms, max={1:F2} ms, spikes(>45ms)={2}", avgMs, maxMs, spikes));
            sb.AppendLine();

            sb.AppendLine("--- Driver Feature Report (0xE1) ---");
            if (tel.Connected)
            {
                sb.AppendLine(string.Format("Transport:      {0}", tel.IsBluetooth ? "Bluetooth LE" : "USB"));
                sb.AppendLine(string.Format("WriteLatencyMs: {0} ms", tel.LastLatencyMs));
                sb.AppendLine(string.Format("BatteryPctRaw:  {0}", tel.BatteryPct <= 100 ? (tel.BatteryPct + "%") : ("255 (WinBLE=" + _winPnpBatteryPct + "%)")));
                sb.AppendLine(string.Format("RumbleFailed:   {0}", tel.RumbleFailed));
                sb.AppendLine(string.Format("DriverError:    0x{0:X8}", tel.DriverError));
                sb.AppendLine(string.Format("RumbleError:    0x{0:X8}", tel.RumbleError));
                sb.AppendLine(string.Format("RumbleWrites:   {0}", tel.RumbleWrites));
                sb.AppendLine(string.Format("RumbleFailures: {0}", tel.RumbleFailures));
                sb.AppendLine(string.Format("Motors (0-255): Strong={0}, Weak={1}", tel.StrongByte, tel.WeakByte));
                sb.AppendLine(string.Format("Last RawReport: {0}", tel.RawHex));
                string fixHint = DescribeCriticalFix(tel.DriverError, tel.RumbleError, tel.RumbleFailed).Trim();
                if (!string.IsNullOrEmpty(fixHint)) sb.AppendLine("Auto-Analysis:  " + fixHint.Replace("\r\n", " | "));
            }
            else
            {
                sb.AppendLine(string.Format("Not available (HidReadError={0})", tel.ReadErrorCode));
            }
            sb.AppendLine();

            sb.AppendLine("--- Driver Settings (0xE2) ---");
            sb.AppendLine(string.Format("Supported:      {0}", cfgSupported));
            if (cfgSupported && cfg != null)
            {
                sb.AppendLine(string.Format("Motors Gain:    Strong={0}%, Weak={1}%", cfg.StrongGainPct, cfg.WeakGainPct));
                sb.AppendLine(string.Format("Deadzones:      Stick={0}%, Trigger={1}%..{2}%", cfg.StickDeadzonePct, cfg.TriggerDeadzonePct, cfg.TriggerMaxPct));
                sb.AppendLine(string.Format("Flags:          SwapSticks={0}, InvertLY={1}, InvertRY={2}", cfg.SwapSticks, cfg.InvertLy, cfg.InvertRy));
                sb.AppendLine(string.Format("BLE Pacing:     Interval={0}ms, PostAckGap={1}ms, MinDelta={2}", cfg.BleIntervalMs, cfg.BlePostAckGapMs, cfg.BleMinDelta));
            }
            sb.AppendLine();

            sb.AppendLine("--- Installed Stadia/Xbox PnP Devices ---");
            sb.AppendLine(RunCommandCapture("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_18D1&PID_9400|VID_045E&PID_02FD|00001812.*18d1' } | Select-Object Status, Class, FriendlyName, InstanceId, Problem | Format-List\"",
                4000));

            sb.AppendLine("--- Driver Store (winstadia / oem*.inf) ---");
            sb.AppendLine(RunCommandCapture("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem $env:windir\\INF\\oem*.inf -ErrorAction SilentlyContinue | Where-Object { Select-String -Path $_ -Pattern 'winstadia\\.dll' -Quiet } | ForEach-Object { $_.Name; (Select-String -Path $_ -Pattern 'DriverVer').Line }\"",
                3000));

            sb.AppendLine("--- Bluetooth Service Status ---");
            sb.AppendLine(RunCommandCapture("sc.exe", "query bthserv", 2000));

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidateLogs = new string[]
            {
                Path.GetFullPath(Path.Combine(baseDir, "..\\..\\driver\\out\\install.log")),
                Path.GetFullPath(Path.Combine(baseDir, "..\\..\\driver\\install.log"))
            };
            for (int i = 0; i < candidateLogs.Length; i++)
            {
                if (File.Exists(candidateLogs[i]))
                {
                    sb.AppendLine(string.Format("--- Install Log ({0}) ---", candidateLogs[i]));
                    try
                    {
                        string[] lines = File.ReadAllLines(candidateLogs[i]);
                        int start = Math.Max(0, lines.Length - 35);
                        for (int k = start; k < lines.Length; k++) sb.AppendLine(lines[k]);
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("Read error: " + ex.Message);
                    }
                    sb.AppendLine();
                    break;
                }
            }

            sb.AppendLine("--- Session Event Log ---");
            if (logSnapshot != null)
            {
                for (int i = 0; i < logSnapshot.Length; i++) sb.AppendLine(logSnapshot[i]);
            }
            return sb.ToString();
        }

        private static string GetWindowsSummary()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object prod = key.GetValue("ProductName");
                        object disp = key.GetValue("DisplayVersion");
                        object build = key.GetValue("CurrentBuildNumber");
                        object ubr = key.GetValue("UBR");
                        return string.Format("{0} {1} (Build {2}.{3})", prod, disp, build, ubr);
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        private static string RunCommandCapture(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "(timeout)";
                    }
                    string combined = (stdout + "\n" + stderr).Trim();
                    return string.IsNullOrEmpty(combined) ? "(empty)" : combined;
                }
            }
            catch (Exception ex)
            {
                return "(exec error: " + ex.Message + ")";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopAllRumble("");
            _uiTimer.Stop();
            _stopPollEvent.Set();
            if (_pollThread != null) _pollThread.Join(500);
            if (_telemetryThread != null) _telemetryThread.Join(500);
            if (_wakeThread != null) _wakeThread.Join(300);
            try { Native.timeEndPeriod(1); } catch { }
            if (_hidHandle != IntPtr.Zero)
            {
                Native.CloseHandle(_hidHandle);
                _hidHandle = IntPtr.Zero;
            }
            if (_appIcon != null)
            {
                _appIcon.Dispose();
                _appIcon = null;
            }
            base.OnFormClosing(e);
        }
    }

    internal static class MiniJson
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append(string.Format(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        public static Dictionary<string, string> ParseFlatObject(string json)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return map;
            int idx = 0;
            int len = json.Length;

            SkipWs(json, ref idx, len);
            if (idx >= len || json[idx] != '{') return map;
            idx++;

            while (idx < len)
            {
                SkipWs(json, ref idx, len);
                if (idx >= len || json[idx] == '}') break;
                if (json[idx] == ',')
                {
                    idx++;
                    continue;
                }
                if (json[idx] != '"') break;
                string key = ReadJsonString(json, ref idx, len);
                SkipWs(json, ref idx, len);
                if (idx >= len || json[idx] != ':') break;
                idx++;
                SkipWs(json, ref idx, len);
                string val = ReadJsonScalarOrRaw(json, ref idx, len);
                if (key != null && val != null)
                {
                    map[key] = val;
                }
            }
            return map;
        }

        public static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
        {
            string raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            string low = raw.Trim().ToLowerInvariant();
            if (low == "true" || low == "1") return true;
            if (low == "false" || low == "0") return false;
            return fallback;
        }

        public static int GetInt(Dictionary<string, string> map, string key, int fallback)
        {
            string raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            double d;
            if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return (int)Math.Round(d);
            }
            return fallback;
        }

        public static string GetString(Dictionary<string, string> map, string key, string fallback)
        {
            string raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            return raw;
        }

        private static void SkipWs(string s, ref int idx, int len)
        {
            while (idx < len)
            {
                char c = s[idx];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') idx++;
                else break;
            }
        }

        private static string ReadJsonString(string s, ref int idx, int len)
        {
            if (idx >= len || s[idx] != '"') return null;
            idx++;
            StringBuilder sb = new StringBuilder();
            while (idx < len)
            {
                char c = s[idx++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && idx < len)
                {
                    char esc = s[idx++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (idx + 4 <= len)
                            {
                                string hex = s.Substring(idx, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                }
                                idx += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string ReadJsonScalarOrRaw(string s, ref int idx, int len)
        {
            if (idx >= len) return null;
            if (s[idx] == '"') return ReadJsonString(s, ref idx, len);
            if (s[idx] == '{' || s[idx] == '[')
            {
                char open = s[idx];
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                bool inStr = false;
                int start = idx;
                while (idx < len)
                {
                    char c = s[idx++];
                    if (inStr)
                    {
                        if (c == '\\' && idx < len) idx++;
                        else if (c == '"') inStr = false;
                    }
                    else
                    {
                        if (c == '"') inStr = true;
                        else if (c == open) depth++;
                        else if (c == close)
                        {
                            depth--;
                            if (depth <= 0) break;
                        }
                    }
                }
                return s.Substring(start, idx - start);
            }
            int sIdx = idx;
            while (idx < len)
            {
                char c = s[idx];
                if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\r' || c == '\n') break;
                idx++;
            }
            string tok = s.Substring(sIdx, idx - sIdx);
            return tok == "null" ? null : tok;
        }
    }

    internal sealed class DriverBridgeCore : IDisposable
    {
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private readonly object _stateLock = new object();
        private readonly object _macroLock = new object();
        private readonly object _logLock = new object();
        private readonly List<string> _logLines = new List<string>();

        private Thread _pollThread;
        private Thread _telemetryThread;
        private Thread _batteryThread;

        private int _activeSlot = 0;
        private bool _padConnected = false;
        private Native.XInputState _latestState;
        private uint _lastPacketNum = 0;
        private long _lastPacketTicks = 0;
        private int _packetsInWindow = 0;
        private long _windowStartTicks = 0;
        private double _measuredHz = 0;
        private double _lastActiveHz = 0;
        private double _lastIntervalMs = 0;
        private double _avgIntervalMs = 0;
        private double _maxIntervalMs = 0;
        private int _spikeCount = 0;

        private IntPtr _hidHandle = IntPtr.Zero;
        private DriverTelemetry _latestTelemetry = new DriverTelemetry();
        private DriverSettings _driverSettings = new DriverSettings();
        private bool _driverSettingsSupported = false;
        private bool _settingsLoadedFromDriver = false;
        private bool _driverMacroSupported = false;
        private bool _macroSettingsLoadedFromDriver = false;

        private readonly AppMacroSettings _appMacros = new AppMacroSettings();
        private int _winPnpBatteryPct = -1;

        private readonly object _rumbleTimerLock = new object();
        private System.Threading.Timer _rumbleStopTimer;
        private int _rumbleGen = 0;

        public DriverBridgeCore()
        {
            _driverSettings.LoadFromRegistry();
            _appMacros.LoadFromRegistry();
        }

        public void Start()
        {
            Native.EnableHighResolutionTimer();
            AddLog(I18n.T(
                "Stadia Rumble Studio Web-Bridge v1.0.15 запущено (автономні макроси 0xE3 і Turbo 0xE2 у драйвері winstadia.dll).",
                "Stadia Rumble Studio Web-Bridge v1.0.15 started (standalone 0xE3 macros & 0xE2 Turbo in winstadia.dll).",
                "Stadia Rumble Studio Web-Bridge v1.0.15 gestartet (autonome 0xE3-Makros & 0xE2-Turbo in winstadia.dll).",
                "Stadia Rumble Studio Web-Bridge v1.0.15 запущен (автономные макросы 0xE3 и Turbo 0xE2 в драйвере winstadia.dll)."));

            _pollThread = new Thread(PollWorker);
            _pollThread.IsBackground = true;
            _pollThread.Start();

            _telemetryThread = new Thread(TelemetryWorker);
            _telemetryThread.IsBackground = true;
            _telemetryThread.Start();

            _batteryThread = new Thread(BatteryScanLoopWorker);
            _batteryThread.IsBackground = true;
            _batteryThread.Start();
        }

        private void AddLog(string line)
        {
            string entry = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss.fff"), line);
            lock (_logLock)
            {
                _logLines.Add(entry);
                if (_logLines.Count > 500)
                {
                    _logLines.RemoveRange(0, _logLines.Count - 500);
                }
            }
        }

        private void PollWorker()
        {
            long freq = Stopwatch.Frequency;
            _windowStartTicks = Stopwatch.GetTimestamp();
            bool prevConnected = false;
            double pendingSpikeMs = 0;

            while (!_stopEvent.WaitOne(2))
            {
                int slot;
                lock (_stateLock) { slot = _activeSlot; }

                Native.XInputState st;
                uint res = Native.GetState((uint)slot, out st);
                bool conn = (res == Native.ErrorSuccess);
                long now = Stopwatch.GetTimestamp();

                if (conn != prevConnected)
                {
                    prevConnected = conn;
                    pendingSpikeMs = 0;
                    if (!conn)
                    {
                        lock (_stateLock)
                        {
                            _settingsLoadedFromDriver = false;
                            _macroSettingsLoadedFromDriver = false;
                        }
                    }
                    AddLog(conn
                        ? string.Format(I18n.T("Контролер підключено в слот XInput #{0}.", "Controller connected to XInput slot #{0}.", "Controller an XInput-Slot #{0} verbunden.", "Контроллер подключён в слот XInput #{0}."), slot + 1)
                        : string.Format(I18n.T("Контролер відключено зі слоту XInput #{0}.", "Controller disconnected from XInput slot #{0}.", "Controller von XInput-Slot #{0} getrennt.", "Контроллер отключён из слота XInput #{0}."), slot + 1));
                }

                bool spikeDetected = false;
                double spikeMs = 0;
                uint spikeWrites = 0;
                uint spikeFails = 0;

                lock (_stateLock)
                {
                    _padConnected = conn;
                    if (conn)
                    {
                        _latestState = st;
                        if (st.PacketNumber != _lastPacketNum)
                        {
                            if (_lastPacketTicks != 0)
                            {
                                double dtMs = ((now - _lastPacketTicks) * 1000.0) / freq;
                                double prevDt = _lastIntervalMs;
                                if (dtMs < 180.0)
                                {
                                    if (pendingSpikeMs > 0.0 && dtMs < 35.0)
                                    {
                                        _spikeCount++;
                                        spikeDetected = true;
                                        spikeMs = pendingSpikeMs;
                                        spikeWrites = _latestTelemetry.RumbleWrites;
                                        spikeFails = _latestTelemetry.RumbleFailures;
                                        pendingSpikeMs = 0;
                                    }
                                    else if (dtMs > 45.0 && prevDt > 0.0 && prevDt < 35.0)
                                    {
                                        pendingSpikeMs = dtMs;
                                    }
                                    else
                                    {
                                        pendingSpikeMs = 0;
                                    }

                                    _lastIntervalMs = dtMs;
                                    if (_avgIntervalMs <= 0.0) _avgIntervalMs = dtMs;
                                    else _avgIntervalMs = _avgIntervalMs * 0.90 + dtMs * 0.10;
                                    if (dtMs > _maxIntervalMs) _maxIntervalMs = dtMs;
                                }
                                else
                                {
                                    pendingSpikeMs = 0;
                                }
                            }
                            _lastPacketNum = st.PacketNumber;
                            _lastPacketTicks = now;
                            _packetsInWindow++;
                        }

                        double winSec = (double)(now - _windowStartTicks) / freq;
                        if (winSec >= 0.5)
                        {
                            _measuredHz = _packetsInWindow / winSec;
                            if (_measuredHz > 1.0) _lastActiveHz = _measuredHz;
                            _packetsInWindow = 0;
                            _windowStartTicks = now;
                        }
                    }
                    else
                    {
                        _measuredHz = 0;
                        _lastPacketTicks = 0;
                        pendingSpikeMs = 0;
                    }
                }

                if (spikeDetected)
                {
                    AddLog(string.Format("[SPIKE] {0:F1} ms (Writes={1}, Fails={2})", spikeMs, spikeWrites, spikeFails));
                }
            }
        }

        private void TelemetryWorker()
        {
            while (true)
            {
                bool conn;
                lock (_stateLock) { conn = _padConnected; }
                int waitMs = conn ? 35 : 200;
                if (_stopEvent.WaitOne(waitMs)) break;
                ReadTelemetryAndSettings();
            }
        }

        private void BatteryScanLoopWorker()
        {
            ScanWindowsBatteryNow();
            while (!_stopEvent.WaitOne(15000))
            {
                ScanWindowsBatteryNow();
            }
        }

        private void ReadTelemetryAndSettings()
        {
            if (_hidHandle == IntPtr.Zero)
            {
                _hidHandle = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
            }
            if (_hidHandle == IntPtr.Zero)
            {
                lock (_stateLock)
                {
                    _latestTelemetry = new DriverTelemetry();
                    _driverSettingsSupported = false;
                    _driverMacroSupported = false;
                    _settingsLoadedFromDriver = false;
                    _macroSettingsLoadedFromDriver = false;
                }
                return;
            }

            byte[] report = new byte[Native.StatusReportLen];
            report[0] = Native.StatusReportId;
            if (!Native.HidD_GetFeature(_hidHandle, report, report.Length))
            {
                int err = Marshal.GetLastWin32Error();
                Native.CloseHandle(_hidHandle);
                _hidHandle = IntPtr.Zero;
                lock (_stateLock)
                {
                    DriverTelemetry t = new DriverTelemetry();
                    t.ReadErrorCode = err;
                    _latestTelemetry = t;
                    _driverSettingsSupported = false;
                    _driverMacroSupported = false;
                    _settingsLoadedFromDriver = false;
                    _macroSettingsLoadedFromDriver = false;
                }
                return;
            }

            DriverTelemetry tel = new DriverTelemetry();
            tel.Connected = true;
            tel.RumbleFailed = report[1] != 0;
            tel.DriverError = BitConverter.ToUInt32(report, 2);
            int rawLen = report[6];
            if (rawLen > 16) rawLen = 16;
            tel.RawLen = rawLen;
            if (rawLen >= 2) tel.RawB1 = report[7 + 1];
            if (rawLen >= 3) tel.RawB2 = report[7 + 2];
            if (rawLen > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < rawLen; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(report[7 + i].ToString("X2"));
                }
                tel.RawHex = sb.ToString();
            }
            tel.RumbleWrites = BitConverter.ToUInt32(report, 23);
            tel.RumbleError = BitConverter.ToUInt32(report, 27);
            tel.StrongByte = report[31];
            tel.WeakByte = report[32];
            tel.IsBluetooth = report[33] != 0;
            tel.RumbleFailures = BitConverter.ToUInt32(report, 34);
            tel.LastLatencyMs = BitConverter.ToUInt16(report, 38);
            tel.BatteryPct = report[40];
            tel.TurboMask = (report[41] | (report[42] << 8)) & 0x03FF;
            tel.TurboToggleSeq = report[43];
            tel.MacroTriggerSeq = report[44];
            tel.LastMacroId = report[45];

            bool cfgOk;
            bool macroOk;
            bool needLoadSettings;
            bool needLoadMacros;
            lock (_stateLock)
            {
                cfgOk = _driverSettingsSupported;
                macroOk = _driverMacroSupported;
                needLoadSettings = !_settingsLoadedFromDriver;
                needLoadMacros = !_macroSettingsLoadedFromDriver;
            }

            DriverSettings parsedCfg = null;
            if (needLoadSettings)
            {
                byte[] cfg = new byte[Native.SettingsReportLen];
                cfg[0] = Native.SettingsReportId;
                cfgOk = Native.HidD_GetFeature(_hidHandle, cfg, cfg.Length);
                parsedCfg = cfgOk ? DriverSettings.FromBlob(cfg) : null;
            }

            byte[] parsedMacroBlob = null;
            if (needLoadMacros)
            {
                byte[] mbuf = new byte[Native.MacroSettingsReportLen];
                mbuf[0] = Native.MacroSettingsReportId;
                macroOk = Native.HidD_GetFeature(_hidHandle, mbuf, mbuf.Length);
                if (macroOk) parsedMacroBlob = mbuf;
            }

            lock (_stateLock)
            {
                _latestTelemetry = tel;
                _driverSettingsSupported = cfgOk;
                _driverMacroSupported = macroOk;
                if (cfgOk && !_settingsLoadedFromDriver && parsedCfg != null)
                {
                    _driverSettings = parsedCfg;
                    _driverSettings.SaveToRegistry();
                    _settingsLoadedFromDriver = true;
                }
                if (macroOk && !_macroSettingsLoadedFromDriver && parsedMacroBlob != null)
                {
                    _macroSettingsLoadedFromDriver = true;
                }
            }

            if (parsedMacroBlob != null)
            {
                lock (_macroLock)
                {
                    _appMacros.ApplyFromBlob(parsedMacroBlob);
                    _appMacros.SaveToRegistry();
                }
            }
        }

        public int ScanWindowsBatteryNow()
        {
            string ps = RunCommandCapture("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"$p = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'Stadia|Xbox Wireless' -and $_.Status -eq 'OK' } | Select-Object -First 1; if ($p) { $b = Get-PnpDeviceProperty -InstanceId $p.InstanceId -KeyName '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2' -ErrorAction SilentlyContinue; if ($b -and $b.Data -ne $null) { Write-Output $b.Data } }\"",
                3500);
            int pct;
            if (int.TryParse((ps ?? "").Trim(), out pct) && pct >= 0 && pct <= 100)
            {
                lock (_stateLock) { _winPnpBatteryPct = pct; }
                return pct;
            }
            lock (_stateLock) { return _winPnpBatteryPct; }
        }

        public int SetSlot(int nextSlot)
        {
            int clean = Native.ClampInt(nextSlot, 0, 3);
            int prev;
            lock (_stateLock)
            {
                prev = _activeSlot;
                if (prev != clean)
                {
                    _activeSlot = clean;
                    _lastPacketTicks = 0;
                    _packetsInWindow = 0;
                    _measuredHz = 0;
                }
            }
            if (prev != clean)
            {
                Native.SetVibration(prev, 0, 0);
                AddLog(string.Format("XInput Slot -> #{0}", clean + 1));
            }
            return clean;
        }

        public uint TriggerRumble(int strongPct, int weakPct, int durationMs)
        {
            int sPct = Native.ClampInt(strongPct, 0, 100);
            int wPct = Native.ClampInt(weakPct, 0, 100);
            int slot;
            lock (_stateLock) { slot = _activeSlot; }
            uint err = Native.SetVibration(slot, sPct, wPct);

            lock (_rumbleTimerLock)
            {
                _rumbleGen++;
                int myGen = _rumbleGen;
                if (_rumbleStopTimer != null)
                {
                    _rumbleStopTimer.Dispose();
                    _rumbleStopTimer = null;
                }
                if (durationMs > 0 && (sPct > 0 || wPct > 0))
                {
                    int safeMs = Native.ClampInt(durationMs, 80, 12000);
                    _rumbleStopTimer = new System.Threading.Timer(delegate(object timerState)
                    {
                        lock (_rumbleTimerLock)
                        {
                            if (_rumbleGen != myGen) return;
                        }
                        int stopSlot;
                        lock (_stateLock) { stopSlot = _activeSlot; }
                        Native.SetVibration(stopSlot, 0, 0);
                    }, null, safeMs, Timeout.Infinite);
                }
            }
            return err;
        }

        public void ApplyConfig(Dictionary<string, string> kv, out bool driverSaved, out int err)
        {
            DriverSettings next = new DriverSettings();
            lock (_stateLock)
            {
                next.StrongGainPct = _driverSettings.StrongGainPct;
                next.WeakGainPct = _driverSettings.WeakGainPct;
                next.StickDeadzonePct = _driverSettings.StickDeadzonePct;
                next.TriggerDeadzonePct = _driverSettings.TriggerDeadzonePct;
                next.TriggerMaxPct = _driverSettings.TriggerMaxPct;
                next.BleIntervalMs = _driverSettings.BleIntervalMs;
                next.BlePostAckGapMs = _driverSettings.BlePostAckGapMs;
                next.BleMinDelta = _driverSettings.BleMinDelta;
                next.SwapSticks = _driverSettings.SwapSticks;
                next.InvertLy = _driverSettings.InvertLy;
                next.InvertRy = _driverSettings.InvertRy;
                next.TurboEnabled = _driverSettings.TurboEnabled;
                next.TurboHoldMode = _driverSettings.TurboHoldMode;
                next.TurboHz = _driverSettings.TurboHz;
                next.TurboMask = _driverSettings.TurboMask;
            }

            next.StrongGainPct = Native.ClampInt(MiniJson.GetInt(kv, "strongGainPct", next.StrongGainPct), 0, 100);
            next.WeakGainPct = Native.ClampInt(MiniJson.GetInt(kv, "weakGainPct", next.WeakGainPct), 0, 100);
            next.StickDeadzonePct = Native.ClampInt(MiniJson.GetInt(kv, "stickDeadzonePct", next.StickDeadzonePct), 0, 25);
            next.TriggerDeadzonePct = Native.ClampInt(MiniJson.GetInt(kv, "triggerDeadzonePct", next.TriggerDeadzonePct), 0, 20);
            next.TriggerMaxPct = Native.ClampInt(MiniJson.GetInt(kv, "triggerMaxPct", next.TriggerMaxPct), 50, 100);
            next.BleIntervalMs = Native.ClampInt(MiniJson.GetInt(kv, "bleIntervalMs", next.BleIntervalMs), 15, 100);
            next.BlePostAckGapMs = Native.ClampInt(MiniJson.GetInt(kv, "blePostAckGapMs", next.BlePostAckGapMs), 0, 30);
            next.BleMinDelta = Native.ClampInt(MiniJson.GetInt(kv, "bleMinDelta", next.BleMinDelta), 0, 20);
            next.SwapSticks = MiniJson.GetBool(kv, "swapSticks", next.SwapSticks);
            next.InvertLy = MiniJson.GetBool(kv, "invertLy", next.InvertLy);
            next.InvertRy = MiniJson.GetBool(kv, "invertRy", next.InvertRy);
            next.TurboEnabled = MiniJson.GetBool(kv, "turboEnabled", next.TurboEnabled);
            next.TurboHoldMode = MiniJson.GetBool(kv, "turboHoldMode", next.TurboHoldMode);
            next.TurboHz = Native.ClampInt(MiniJson.GetInt(kv, "turboHz", next.TurboHz), 5, 25);
            next.TurboMask = MiniJson.GetInt(kv, "turboMask", next.TurboMask) & 0x03FF;

            next.SaveToRegistry();
            lock (_stateLock) { _driverSettings = next; }

            byte[] blob = next.ToBlob();
            IntPtr h = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
            if (h == IntPtr.Zero)
            {
                driverSaved = false;
                err = 2; // ERROR_FILE_NOT_FOUND
                return;
            }
            bool ok = Native.HidD_SetFeature(h, blob, blob.Length);
            err = ok ? 0 : Marshal.GetLastWin32Error();
            Native.CloseHandle(h);
            driverSaved = ok;
            if (ok)
            {
                lock (_stateLock) { _driverSettingsSupported = true; }
                AddLog(string.Format("[0xE2] Strong={0}%, Weak={1}%, DZ={2}%, BLE={3}ms, Turbo={4}",
                    next.StrongGainPct, next.WeakGainPct, next.StickDeadzonePct, next.BleIntervalMs,
                    DriverSettings.FormatTurboMask(next.TurboMask)));
            }
        }

        public void ApplyMacros(Dictionary<string, string> kv, out bool driverSaved, out int err)
        {
            byte[] blob;
            lock (_macroLock)
            {
                _appMacros.MacrosEnabled = MiniJson.GetBool(kv, "macrosEnabled", _appMacros.MacrosEnabled);
                _appMacros.DpadMediaCombos = MiniJson.GetBool(kv, "dpadCombos", _appMacros.DpadMediaCombos);
                _appMacros.AssistantAction = ParseMacroId(MiniJson.GetString(kv, "assistAction", MacroActionToId(_appMacros.AssistantAction)));
                _appMacros.CaptureAction = ParseMacroId(MiniJson.GetString(kv, "captureAction", MacroActionToId(_appMacros.CaptureAction)));

                int modMask = MiniJson.GetInt(kv, "customModMask", _appMacros.CustomModMask) & 0x07;
                int hidUsage = Native.ClampInt(MiniJson.GetInt(kv, "customHidUsage", _appMacros.CustomHidUsage), 0x04, 0x73);
                _appMacros.CustomModMask = (byte)modMask;
                _appMacros.CustomHidUsage = (byte)hidUsage;
                _appMacros.CustomCtrl = (modMask & 0x01) != 0;
                _appMacros.CustomShift = (modMask & 0x02) != 0;
                _appMacros.CustomAlt = (modMask & 0x04) != 0;
                _appMacros.CustomVk = Native.HidUsageToVk((byte)hidUsage);
                _appMacros.SaveToRegistry();
                blob = _appMacros.ToBlob();
                // Preserve exact HID usage & modifier byte submitted from Studio
                blob[5] = (byte)modMask;
                blob[6] = (byte)hidUsage;
            }

            IntPtr h = Native.OpenStadiaHidHandle(Native.GenericRead | Native.GenericWrite);
            if (h == IntPtr.Zero)
            {
                driverSaved = false;
                err = 2;
                return;
            }
            bool ok = Native.HidD_SetFeature(h, blob, blob.Length);
            err = ok ? 0 : Marshal.GetLastWin32Error();
            Native.CloseHandle(h);
            driverSaved = ok;
            if (ok)
            {
                lock (_stateLock) { _driverMacroSupported = true; }
                AddLog("[0xE3] Macros persisted inside winstadia.dll.");
            }
        }

        private static string MacroActionToId(MacroAction act)
        {
            switch (act)
            {
                case MacroAction.None: return "none";
                case MacroAction.PlayPause: return "play_pause";
                case MacroAction.WinScreenshot: return "win_prtsc";
                case MacroAction.SteamF12: return "f12";
                case MacroAction.GameBarOverlay: return "win_g";
                case MacroAction.GameBarClip: return "win_alt_g";
                case MacroAction.NextTrack: return "next_track";
                case MacroAction.VolumeMute: return "volume_mute";
                case MacroAction.CustomHotkey: return "custom";
                default: return "play_pause";
            }
        }

        private static MacroAction ParseMacroId(string id)
        {
            string s = (id ?? "").Trim().ToLowerInvariant();
            if (s == "none") return MacroAction.None;
            if (s == "play_pause") return MacroAction.PlayPause;
            if (s == "win_prtsc") return MacroAction.WinScreenshot;
            if (s == "f12") return MacroAction.SteamF12;
            if (s == "win_g") return MacroAction.GameBarOverlay;
            if (s == "win_alt_g") return MacroAction.GameBarClip;
            if (s == "next_track") return MacroAction.NextTrack;
            if (s == "volume_mute") return MacroAction.VolumeMute;
            if (s == "custom") return MacroAction.CustomHotkey;
            return MacroAction.PlayPause;
        }

        public string BuildStatusJson()
        {
            bool conn;
            int slot;
            Native.XInputState st;
            DriverTelemetry tel;
            DriverSettings cfg;
            int winBat;

            lock (_stateLock)
            {
                conn = _padConnected;
                slot = _activeSlot;
                st = _latestState;
                tel = _latestTelemetry;
                cfg = _driverSettings;
                winBat = _winPnpBatteryPct;
            }

            bool mEnabled, dpadCombos;
            MacroAction assistAct, capAct;
            byte customMod, customUsage;
            lock (_macroLock)
            {
                mEnabled = _appMacros.MacrosEnabled;
                dpadCombos = _appMacros.DpadMediaCombos;
                assistAct = _appMacros.AssistantAction;
                capAct = _appMacros.CaptureAction;
                customMod = _appMacros.CustomModMask;
                customUsage = _appMacros.CustomHidUsage;
            }

            double lx = conn ? (st.Gamepad.LeftThumbX / 32768.0) : 0.0;
            double ly = conn ? (-st.Gamepad.LeftThumbY / 32768.0) : 0.0;
            double rx = conn ? (st.Gamepad.RightThumbX / 32768.0) : 0.0;
            double ry = conn ? (-st.Gamepad.RightThumbY / 32768.0) : 0.0;
            double l2 = conn ? (st.Gamepad.LeftTrigger / 255.0) : 0.0;
            double r2 = conn ? (st.Gamepad.RightTrigger / 255.0) : 0.0;
            ushort btns = conn ? st.Gamepad.Buttons : (ushort)0;
            bool physAssist = tel.Connected && tel.RawLen >= 3 && (tel.RawB2 & 0x02) != 0;
            bool physCapture = tel.Connected && tel.RawLen >= 3 && (tel.RawB2 & 0x01) != 0;

            int batOut = (tel.Connected && tel.BatteryPct <= 100) ? tel.BatteryPct : ((winBat >= 0 && winBat <= 100) ? winBat : 255);
            if (tel.Connected && !tel.IsBluetooth && batOut > 100) batOut = 100;

            CultureInfo ic = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"desktop\":true,");
            sb.AppendFormat(ic, "\"lang\":\"{0}\",", I18n.GetCode(I18n.Current));
            sb.AppendFormat(ic, "\"slot\":{0},", slot);

            sb.Append("\"gamepad\":{");
            sb.AppendFormat(ic, "\"connected\":{0},", conn ? "true" : "false");
            sb.AppendFormat(ic, "\"id\":\"Google Stadia Controller (XInput #{0})\",", slot + 1);
            sb.AppendFormat(ic, "\"lx\":{0:0.###},", lx);
            sb.AppendFormat(ic, "\"ly\":{0:0.###},", ly);
            sb.AppendFormat(ic, "\"rx\":{0:0.###},", rx);
            sb.AppendFormat(ic, "\"ry\":{0:0.###},", ry);
            sb.AppendFormat(ic, "\"l2\":{0:0.###},", l2);
            sb.AppendFormat(ic, "\"r2\":{0:0.###},", r2);
            sb.AppendFormat(ic, "\"buttons\":{0},", btns);
            sb.AppendFormat(ic, "\"assistant\":{0},", physAssist ? "true" : "false");
            sb.AppendFormat(ic, "\"capture\":{0}", physCapture ? "true" : "false");
            sb.Append("},");

            sb.Append("\"telemetry\":{");
            sb.AppendFormat(ic, "\"connected\":{0},", tel.Connected ? "true" : "false");
            sb.AppendFormat(ic, "\"isBluetooth\":{0},", tel.IsBluetooth ? "true" : "false");
            sb.AppendFormat(ic, "\"rumbleFailed\":{0},", tel.RumbleFailed ? "true" : "false");
            sb.AppendFormat(ic, "\"driverError\":{0},", tel.DriverError);
            sb.AppendFormat(ic, "\"rumbleError\":{0},", tel.RumbleError);
            sb.AppendFormat(ic, "\"rumbleWrites\":{0},", tel.RumbleWrites);
            sb.AppendFormat(ic, "\"rumbleFailures\":{0},", tel.RumbleFailures);
            sb.AppendFormat(ic, "\"strongByte\":{0},", tel.StrongByte);
            sb.AppendFormat(ic, "\"weakByte\":{0},", tel.WeakByte);
            sb.AppendFormat(ic, "\"lastLatencyMs\":{0},", tel.LastLatencyMs);
            sb.AppendFormat(ic, "\"batteryPct\":{0},", batOut);
            sb.AppendFormat(ic, "\"charging\":{0},", (tel.Connected && !tel.IsBluetooth) ? "true" : "false");
            sb.AppendFormat(ic, "\"turboMask\":{0},", tel.Connected ? tel.TurboMask : cfg.TurboMask);
            sb.AppendFormat(ic, "\"turboToggleSeq\":{0},", tel.TurboToggleSeq);
            sb.AppendFormat(ic, "\"rawHex\":\"{0}\"", MiniJson.Escape(tel.RawHex));
            sb.Append("},");

            sb.Append("\"config\":{");
            sb.AppendFormat(ic, "\"strongGainPct\":{0},", cfg.StrongGainPct);
            sb.AppendFormat(ic, "\"weakGainPct\":{0},", cfg.WeakGainPct);
            sb.AppendFormat(ic, "\"stickDeadzonePct\":{0},", cfg.StickDeadzonePct);
            sb.AppendFormat(ic, "\"triggerDeadzonePct\":{0},", cfg.TriggerDeadzonePct);
            sb.AppendFormat(ic, "\"triggerMaxPct\":{0},", cfg.TriggerMaxPct);
            sb.AppendFormat(ic, "\"bleIntervalMs\":{0},", cfg.BleIntervalMs);
            sb.AppendFormat(ic, "\"blePostAckGapMs\":{0},", cfg.BlePostAckGapMs);
            sb.AppendFormat(ic, "\"bleMinDelta\":{0},", cfg.BleMinDelta);
            sb.AppendFormat(ic, "\"swapSticks\":{0},", cfg.SwapSticks ? "true" : "false");
            sb.AppendFormat(ic, "\"invertLy\":{0},", cfg.InvertLy ? "true" : "false");
            sb.AppendFormat(ic, "\"invertRy\":{0},", cfg.InvertRy ? "true" : "false");
            sb.AppendFormat(ic, "\"turboEnabled\":{0},", cfg.TurboEnabled ? "true" : "false");
            sb.AppendFormat(ic, "\"turboHoldMode\":{0},", cfg.TurboHoldMode ? "true" : "false");
            sb.AppendFormat(ic, "\"turboHz\":{0},", cfg.TurboHz);
            sb.AppendFormat(ic, "\"turboMask\":{0}", cfg.TurboMask);
            sb.Append("},");

            sb.Append("\"macros\":{");
            sb.AppendFormat(ic, "\"macrosEnabled\":{0},", mEnabled ? "true" : "false");
            sb.AppendFormat(ic, "\"dpadCombos\":{0},", dpadCombos ? "true" : "false");
            sb.AppendFormat(ic, "\"assistAction\":\"{0}\",", MacroActionToId(assistAct));
            sb.AppendFormat(ic, "\"captureAction\":\"{0}\",", MacroActionToId(capAct));
            sb.AppendFormat(ic, "\"customModMask\":{0},", customMod);
            sb.AppendFormat(ic, "\"customHidUsage\":{0}", customUsage);
            sb.Append('}');

            sb.Append('}');
            return sb.ToString();
        }

        public string BuildDiagnosticDumpText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==========================================================");
            sb.AppendLine("  STADIA RUMBLE DIAGNOSTIC LOG (v1.0.15)");
            sb.AppendLine("==========================================================");
            sb.AppendLine(string.Format("Timestamp: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")));
            sb.AppendLine(string.Format("Windows:   {0}", GetWindowsSummary()));
            sb.AppendLine();

            bool conn;
            DriverTelemetry tel;
            DriverSettings cfg;
            bool cfgSupported;
            double hz, activeHz, avgMs, maxMs;
            int spikes, slot, winBat;
            lock (_stateLock)
            {
                conn = _padConnected;
                tel = _latestTelemetry;
                cfg = _driverSettings;
                cfgSupported = _driverSettingsSupported;
                hz = _measuredHz;
                activeHz = _lastActiveHz;
                avgMs = _avgIntervalMs;
                maxMs = _maxIntervalMs;
                spikes = _spikeCount;
                slot = _activeSlot;
                winBat = _winPnpBatteryPct;
            }

            sb.AppendLine("--- XInput & Polling ---");
            sb.AppendLine(string.Format("Active Slot:   #{0} (Connected={1})", slot + 1, conn));
            sb.AppendLine(string.Format("Polling Rate:  {0:F1} Hz (last active={1:F1} Hz)", hz, activeHz));
            sb.AppendLine(string.Format("Input Gap:     avg={0:F2} ms, max={1:F2} ms, spikes(>45ms)={2}", avgMs, maxMs, spikes));
            sb.AppendLine();

            sb.AppendLine("--- Driver Feature Report (0xE1) ---");
            if (tel.Connected)
            {
                sb.AppendLine(string.Format("Transport:      {0}", tel.IsBluetooth ? "Bluetooth LE" : "USB"));
                sb.AppendLine(string.Format("WriteLatencyMs: {0} ms", tel.LastLatencyMs));
                sb.AppendLine(string.Format("BatteryPctRaw:  {0}", tel.BatteryPct <= 100 ? (tel.BatteryPct + "%") : ("255 (WinBLE=" + winBat + "%)")));
                sb.AppendLine(string.Format("RumbleFailed:   {0}", tel.RumbleFailed));
                sb.AppendLine(string.Format("DriverError:    0x{0:X8}", tel.DriverError));
                sb.AppendLine(string.Format("RumbleError:    0x{0:X8}", tel.RumbleError));
                sb.AppendLine(string.Format("RumbleWrites:   {0}", tel.RumbleWrites));
                sb.AppendLine(string.Format("RumbleFailures: {0}", tel.RumbleFailures));
                sb.AppendLine(string.Format("Motors (0-255): Strong={0}, Weak={1}", tel.StrongByte, tel.WeakByte));
                sb.AppendLine(string.Format("Last RawReport: {0}", tel.RawHex));
            }
            else
            {
                sb.AppendLine(string.Format("Not available (HidReadError={0})", tel.ReadErrorCode));
            }
            sb.AppendLine();

            sb.AppendLine("--- Driver Settings (0xE2) ---");
            sb.AppendLine(string.Format("Supported:      {0}", cfgSupported));
            if (cfg != null)
            {
                sb.AppendLine(string.Format("Motors Gain:    Strong={0}%, Weak={1}%", cfg.StrongGainPct, cfg.WeakGainPct));
                sb.AppendLine(string.Format("Deadzones:      Stick={0}%, Trigger={1}%..{2}%", cfg.StickDeadzonePct, cfg.TriggerDeadzonePct, cfg.TriggerMaxPct));
                sb.AppendLine(string.Format("Flags:          SwapSticks={0}, InvertLY={1}, InvertRY={2}", cfg.SwapSticks, cfg.InvertLy, cfg.InvertRy));
                sb.AppendLine(string.Format("BLE Pacing:     Interval={0}ms, PostAckGap={1}ms, MinDelta={2}", cfg.BleIntervalMs, cfg.BlePostAckGapMs, cfg.BleMinDelta));
                sb.AppendLine(string.Format("Turbo:          Enabled={0}, Hold={1}, Hz={2}, Mask={3}", cfg.TurboEnabled, cfg.TurboHoldMode, cfg.TurboHz, DriverSettings.FormatTurboMask(cfg.TurboMask)));
            }
            sb.AppendLine();

            sb.AppendLine("--- Bluetooth Service & WUDFHost Event Log ---");
            sb.AppendLine(RunCommandCapture("sc.exe", "query bthserv", 2000));
            string evt = RunCommandCapture("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2} -MaxEvents 12 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'WUDF|winstadia|StadiaRumble|bth' } | ForEach-Object { $_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $_.ProviderName + ': ' + ($_.Message -replace '\\r|\\n',' ') }\"",
                4000);
            sb.AppendLine(evt);
            sb.AppendLine();

            sb.AppendLine("--- Installed Stadia/Xbox PnP Devices ---");
            sb.AppendLine(RunCommandCapture("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_18D1&PID_9400|VID_045E&PID_02FD|00001812.*18d1' } | Select-Object Status, Class, FriendlyName, InstanceId, Problem | Format-List\"",
                4000));

            sb.AppendLine("--- Driver Store (winstadia / oem*.inf) ---");
            sb.AppendLine(RunCommandCapture("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem $env:windir\\INF\\oem*.inf -ErrorAction SilentlyContinue | Where-Object { Select-String -Path $_ -Pattern 'winstadia\\.dll' -Quiet } | ForEach-Object { $_.Name; (Select-String -Path $_ -Pattern 'DriverVer').Line }\"",
                3000));

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidateLogs = new string[]
            {
                Path.GetFullPath(Path.Combine(baseDir, "..\\..\\driver\\out\\install.log")),
                Path.GetFullPath(Path.Combine(baseDir, "..\\..\\driver\\install.log"))
            };
            for (int i = 0; i < candidateLogs.Length; i++)
            {
                if (File.Exists(candidateLogs[i]))
                {
                    sb.AppendLine(string.Format("--- Install Log ({0}) ---", candidateLogs[i]));
                    try
                    {
                        string[] lines = File.ReadAllLines(candidateLogs[i]);
                        int start = Math.Max(0, lines.Length - 35);
                        for (int k = start; k < lines.Length; k++) sb.AppendLine(lines[k]);
                    }
                    catch (Exception ex) { sb.AppendLine("Read error: " + ex.Message); }
                    sb.AppendLine();
                    break;
                }
            }

            sb.AppendLine("--- Session Event Log ---");
            lock (_logLock)
            {
                for (int i = 0; i < _logLines.Count; i++) sb.AppendLine(_logLines[i]);
            }
            return sb.ToString();
        }

        private static string GetWindowsSummary()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object prod = key.GetValue("ProductName");
                        object disp = key.GetValue("DisplayVersion");
                        object build = key.GetValue("CurrentBuildNumber");
                        object ubr = key.GetValue("UBR");
                        return string.Format("{0} {1} (Build {2}.{3})", prod, disp, build, ubr);
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        private static string RunCommandCapture(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "(timeout)";
                    }
                    string combined = (stdout + "\n" + stderr).Trim();
                    return string.IsNullOrEmpty(combined) ? "(empty)" : combined;
                }
            }
            catch (Exception ex)
            {
                return "(exec error: " + ex.Message + ")";
            }
        }

        public void Dispose()
        {
            TriggerRumble(0, 0, 0);
            _stopEvent.Set();
            if (_pollThread != null) _pollThread.Join(500);
            if (_telemetryThread != null) _telemetryThread.Join(500);
            if (_batteryThread != null) _batteryThread.Join(300);
            lock (_rumbleTimerLock)
            {
                if (_rumbleStopTimer != null)
                {
                    _rumbleStopTimer.Dispose();
                    _rumbleStopTimer = null;
                }
            }
            try { Native.timeEndPeriod(1); } catch { }
            if (_hidHandle != IntPtr.Zero)
            {
                Native.CloseHandle(_hidHandle);
                _hidHandle = IntPtr.Zero;
            }
            _stopEvent.Dispose();
        }
    }

    internal static class LocalWebStudioHost
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private sealed class HostSessionState
        {
            public long HasClientPinged;
            public long LastClientTicks = Stopwatch.GetTimestamp();
            public long ClosingTicks;
        }

        public static bool TryRun(EventWaitHandle wakeEvent)
        {
            string webDir = FindWebAssetsDir();
            string browserExe = FindChromiumAppBrowser();
            if (string.IsNullOrEmpty(webDir) || string.IsNullOrEmpty(browserExe))
            {
                return false;
            }

            int port = PickFreeLoopbackPort();
            if (port <= 0)
            {
                return false;
            }

            string prefix = string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", port);
            HttpListener listener = new HttpListener();
            try
            {
                listener.Prefixes.Add(prefix);
                listener.Start();
            }
            catch
            {
                try { listener.Close(); } catch { }
                return false;
            }

            byte[] iconPng = BuildStadiaPngBytes(64);
            byte[] faviconIco = BuildStadiaIcoBytes(32);

            ManualResetEvent stopHostEvent = new ManualResetEvent(false);
            HostSessionState session = new HostSessionState();

            using (DriverBridgeCore core = new DriverBridgeCore())
            {
                core.Start();

                Thread wakeThread = null;
                if (wakeEvent != null)
                {
                    wakeThread = new Thread(() =>
                    {
                        WaitHandle[] handles = new WaitHandle[] { stopHostEvent, wakeEvent };
                        while (true)
                        {
                            int idx = WaitHandle.WaitAny(handles);
                            if (idx == 0) break;
                            RestoreChromiumAppWindow();
                        }
                    });
                    wakeThread.IsBackground = true;
                    wakeThread.Start();
                }

                Thread acceptThread = new Thread(() =>
                {
                    while (!stopHostEvent.WaitOne(0))
                    {
                        HttpListenerContext ctx = null;
                        try
                        {
                            ctx = listener.GetContext();
                        }
                        catch
                        {
                            break;
                        }
                        if (ctx == null) continue;
                        HttpListenerContext capCtx = ctx;
                        ThreadPool.QueueUserWorkItem(delegate(object workState)
                        {
                            HandleHttpRequest(capCtx, core, webDir, iconPng, faviconIco, session);
                        });
                    }
                });
                acceptThread.IsBackground = true;
                acceptThread.Start();

                Process browserProc = null;
                try
                {
                    string profileDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "StadiaRumble\\WebAppProfile");
                    try { Directory.CreateDirectory(profileDir); } catch { }

                    string launchArgs = string.Format(CultureInfo.InvariantCulture,
                        "--app=\"{0}\" --user-data-dir=\"{1}\" --window-size=1360,880 --new-window --no-first-run --no-default-browser-check --disable-features=msEdgeSidebarV2",
                        prefix, profileDir);

                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = browserExe;
                    psi.Arguments = launchArgs;
                    psi.UseShellExecute = false;
                    browserProc = Process.Start(psi);
                }
                catch
                {
                    stopHostEvent.Set();
                    try { listener.Close(); } catch { }
                    if (wakeThread != null) wakeThread.Join(250);
                    return false;
                }

                long startTicks = Stopwatch.GetTimestamp();
                long freq = Stopwatch.Frequency;
                while (!stopHostEvent.WaitOne(150))
                {
                    long now = Stopwatch.GetTimestamp();
                    long closeReq = Interlocked.Read(ref session.ClosingTicks);
                    if (closeReq != 0 && ((now - closeReq) * 1000.0 / freq) >= 1350.0)
                    {
                        break; // Window closed and no F5 reload followed
                    }

                    bool exited = (browserProc != null && browserProc.HasExited);
                    if (exited)
                    {
                        bool pinged = Interlocked.Read(ref session.HasClientPinged) != 0;
                        if (!pinged && ((now - startTicks) * 1000.0 / freq) >= 4500.0)
                        {
                            break;
                        }
                        if (pinged && ((now - Interlocked.Read(ref session.LastClientTicks)) * 1000.0 / freq) >= 1800.0)
                        {
                            break;
                        }
                    }
                }

                stopHostEvent.Set();
                try { listener.Stop(); } catch { }
                try { listener.Close(); } catch { }
                if (wakeThread != null) wakeThread.Join(300);
                if (browserProc != null)
                {
                    try { browserProc.Dispose(); } catch { }
                }
            }

            stopHostEvent.Dispose();
            return true;
        }

        private static void HandleHttpRequest(HttpListenerContext ctx, DriverBridgeCore core, string webDir,
            byte[] iconPng, byte[] faviconIco, HostSessionState session)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                string rawPath = (req.Url != null ? req.Url.AbsolutePath : "/") ?? "/";
                string path = Uri.UnescapeDataString(rawPath);

                if (path == "/api/closing")
                {
                    Interlocked.Exchange(ref session.ClosingTicks, Stopwatch.GetTimestamp());
                    WriteJson(res, 200, "{\"ok\":true}");
                    return;
                }

                if (path == "/" || path == "/index.html" || path == "/api/status")
                {
                    Interlocked.Exchange(ref session.HasClientPinged, 1);
                    Interlocked.Exchange(ref session.LastClientTicks, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref session.ClosingTicks, 0);
                }

                if (path == "/api/status")
                {
                    WriteJson(res, 200, core.BuildStatusJson());
                    return;
                }

                if (path == "/api/config" && string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string body = ReadBodyUtf8(req);
                    Dictionary<string, string> kv = MiniJson.ParseFlatObject(body);
                    bool driverSaved;
                    int err;
                    core.ApplyConfig(kv, out driverSaved, out err);
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"driverSaved\":{0},\"error\":{1}}}",
                        driverSaved ? "true" : "false", err));
                    return;
                }

                if (path == "/api/macros" && string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string body = ReadBodyUtf8(req);
                    Dictionary<string, string> kv = MiniJson.ParseFlatObject(body);
                    bool driverSaved;
                    int err;
                    core.ApplyMacros(kv, out driverSaved, out err);
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"driverSaved\":{0},\"error\":{1}}}",
                        driverSaved ? "true" : "false", err));
                    return;
                }

                if (path == "/api/rumble" && string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string body = ReadBodyUtf8(req);
                    Dictionary<string, string> kv = MiniJson.ParseFlatObject(body);
                    int strong = MiniJson.GetInt(kv, "strong", 0);
                    int weak = MiniJson.GetInt(kv, "weak", 0);
                    int duration = MiniJson.GetInt(kv, "durationMs", 1400);
                    uint err = core.TriggerRumble(strong, weak, duration);
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"error\":{0}}}", err));
                    return;
                }

                if (path == "/api/slot" && string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string body = ReadBodyUtf8(req);
                    Dictionary<string, string> kv = MiniJson.ParseFlatObject(body);
                    int slot = core.SetSlot(MiniJson.GetInt(kv, "slot", 0));
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"slot\":{0}}}", slot));
                    return;
                }

                if (path == "/api/lang" && string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string body = ReadBodyUtf8(req);
                    Dictionary<string, string> kv = MiniJson.ParseFlatObject(body);
                    string code = MiniJson.GetString(kv, "lang", "uk").Trim().ToLowerInvariant();
                    if (code == "en") I18n.Current = AppLanguage.En;
                    else if (code == "de") I18n.Current = AppLanguage.De;
                    else if (code == "ru") I18n.Current = AppLanguage.Ru;
                    else I18n.Current = AppLanguage.Uk;
                    I18n.SaveToRegistry();
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"lang\":\"{0}\"}}", I18n.GetCode(I18n.Current)));
                    return;
                }

                if (path == "/api/dumps")
                {
                    string text = core.BuildDiagnosticDumpText();
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"text\":\"{0}\"}}", MiniJson.Escape(text)));
                    return;
                }

                if (path == "/api/battery-scan")
                {
                    int pct = core.ScanWindowsBatteryNow();
                    WriteJson(res, 200, string.Format(CultureInfo.InvariantCulture,
                        "{{\"ok\":true,\"winBleBatteryPct\":{0}}}", pct));
                    return;
                }

                if (path == "/icon.png" && iconPng != null)
                {
                    WriteBytes(res, 200, "image/png", iconPng, "public, max-age=3600");
                    return;
                }

                if (path == "/favicon.ico" && faviconIco != null)
                {
                    WriteBytes(res, 200, "image/x-icon", faviconIco, "public, max-age=3600");
                    return;
                }

                // Static file serving from app\web\
                string rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(rel)) rel = "index.html";
                string fullWebDir = Path.GetFullPath(webDir);
                if (!fullWebDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    fullWebDir += Path.DirectorySeparatorChar;
                }
                string candidate = Path.GetFullPath(Path.Combine(fullWebDir, rel));
                if (!candidate.StartsWith(fullWebDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                {
                    candidate = Path.Combine(fullWebDir, "index.html");
                }
                if (!File.Exists(candidate))
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                byte[] fileBytes = File.ReadAllBytes(candidate);
                WriteBytes(res, 200, GetMimeType(candidate), fileBytes, "no-cache");
            }
            catch
            {
                try { res.StatusCode = 500; res.Close(); } catch { }
            }
        }

        private static string ReadBodyUtf8(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return "{}";
            using (StreamReader sr = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static void WriteJson(HttpListenerResponse res, int status, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes(json ?? "{}");
            WriteBytes(res, status, "application/json; charset=utf-8", data, "no-store");
        }

        private static void WriteBytes(HttpListenerResponse res, int status, string contentType, byte[] data, string cacheControl)
        {
            res.StatusCode = status;
            res.ContentType = contentType;
            res.Headers["Cache-Control"] = cacheControl;
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
            res.OutputStream.Close();
        }

        private static string GetMimeType(string filePath)
        {
            string ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js":
                case ".mjs": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".json": return "application/json; charset=utf-8";
                case ".woff2": return "font/woff2";
                case ".woff": return "font/woff";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        private static string FindWebAssetsDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "web"),
                Path.GetFullPath(Path.Combine(baseDir, "..\\web")),
                Path.GetFullPath(Path.Combine(baseDir, "..\\app\\web"))
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                string index = Path.Combine(candidates[i], "index.html");
                if (File.Exists(index)) return candidates[i];
            }
            return null;
        }

        private static string FindChromiumAppBrowser()
        {
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";
            string pf64 = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] paths = new string[]
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf64, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(local, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf64, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe")
            };
            for (int i = 0; i < paths.Length; i++)
            {
                if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i])) return paths[i];
            }

            string[] appKeys = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"
            };
            for (int i = 0; i < appKeys.Length; i++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(appKeys[i]))
                    {
                        if (key != null)
                        {
                            object val = key.GetValue(null);
                            string p = val != null ? val.ToString().Trim('"') : "";
                            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static int PickFreeLoopbackPort()
        {
            try
            {
                TcpListener tcp = new TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();
                return port;
            }
            catch
            {
                return 0;
            }
        }

        private static void RestoreChromiumAppWindow()
        {
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    StringBuilder sb = new StringBuilder(256);
                    if (GetWindowTextW(hWnd, sb, sb.Capacity) > 0)
                    {
                        string title = sb.ToString();
                        if (title.IndexOf("Stadia Rumble", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ShowWindow(hWnd, 9); // SW_RESTORE
                            SetForegroundWindow(hWnd);
                            return false;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        private static byte[] BuildStadiaPngBytes(int size)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(size, size))
                using (Graphics g = Graphics.FromImage(bmp))
                using (MemoryStream ms = new MemoryStream())
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float scale = size / 32.0f;
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(18, 22, 30)))
                    using (Pen rim = new Pen(Color.FromArgb(255, 82, 56), 2.4f * scale))
                    {
                        g.FillEllipse(bg, 2 * scale, 2 * scale, 28 * scale, 28 * scale);
                        g.DrawEllipse(rim, 2 * scale, 2 * scale, 28 * scale, 28 * scale);
                    }
                    using (Pen wave = new Pen(Color.FromArgb(255, 82, 56), 2.8f * scale))
                    {
                        wave.StartCap = LineCap.Round;
                        wave.EndCap = LineCap.Round;
                        g.DrawBezier(wave, new PointF(8 * scale, 14 * scale), new PointF(14 * scale, 7 * scale), new PointF(21 * scale, 9 * scale), new PointF(24 * scale, 13 * scale));
                        g.DrawBezier(wave, new PointF(9 * scale, 19 * scale), new PointF(15 * scale, 14 * scale), new PointF(21 * scale, 17 * scale), new PointF(20 * scale, 21 * scale));
                        g.DrawBezier(wave, new PointF(13 * scale, 23 * scale), new PointF(16 * scale, 21 * scale), new PointF(19 * scale, 22 * scale), new PointF(20 * scale, 24 * scale));
                    }
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] BuildStadiaIcoBytes(int size)
        {
            byte[] png = BuildStadiaPngBytes(size);
            if (png == null || png.Length == 0) return null;
            using (MemoryStream ms = new MemoryStream(22 + png.Length))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((ushort)0); // Reserved
                bw.Write((ushort)1); // Type = ICO
                bw.Write((ushort)1); // Image count
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);   // Color palette
                bw.Write((byte)0);   // Reserved
                bw.Write((ushort)1); // Color planes
                bw.Write((ushort)32);// BPP
                bw.Write((uint)png.Length);
                bw.Write((uint)22);  // Offset to PNG data
                bw.Write(png);
                return ms.ToArray();
            }
        }
    }
}
