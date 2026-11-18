// Shared between the HID side presenting an Xbox controller (winstadia.c), the
// Stadia protocol (stadia.c) and the transports that reach the controller
// (usb.c, bluetooth.c).

#pragma once

#include <windows.h>
#include <wdf.h>
#include <usb.h>
#include <wdfusb.h>
#include "private-gatt.h"

// Controls of the presented pad: the payload of the gamepad input report,
// followed by the guide button, which travels in a report of its own.
#define PAD_GAMEPAD_LEN 15
#define PAD_GUIDE_INDEX PAD_GAMEPAD_LEN
#define PAD_CONTROLS_LEN (PAD_GAMEPAD_LEN + 1)

#define RAW_REPORT_MAX 16

// The Stadia controller's output report: the speeds of its two motors.
#define STADIA_RUMBLE_REPORT_ID 0x05

// Input reports the Bluetooth transport keeps pending below, like the HID
// class driver does.
#define BLUETOOTH_READER_COUNT 3

typedef struct _DEVICE_CONTEXT DEVICE_CONTEXT, *PDEVICE_CONTEXT;

// How Stadia reports travel to and from the controller. Start and Stop follow
// the power state; input reports go to StadiaInputReport.
typedef struct _TRANSPORT {
    NTSTATUS (*PrepareHardware)(WDFDEVICE Device);
    NTSTATUS (*Start)(PDEVICE_CONTEXT Context);
    VOID (*Stop)(PDEVICE_CONTEXT Context);
    // The report starts with its ID. May block; called from the dedicated
    // rumble thread while holding RumbleLock.
    NTSTATUS (*SendOutputReport)(PDEVICE_CONTEXT Context, PUCHAR Report, size_t Length);
    ULONG MinRumbleIntervalMs;
    ULONG PostAckGapMs;
    UCHAR MinRumbleDelta;
} TRANSPORT;

extern const TRANSPORT UsbTransport;
extern const TRANSPORT BluetoothTransport;

typedef struct _BLUETOOTH_READER {
    PDEVICE_CONTEXT Context;
    WDFREQUEST Request;
    WDFMEMORY Buffer;
    // Guarded by the context's lock.
    BOOLEAN Sent;
} BLUETOOTH_READER, *PBLUETOOTH_READER;

struct _DEVICE_CONTEXT {
    WDFDEVICE Device;
    const TRANSPORT *Transport;
    BOOLEAN IsBluetooth;

    // Guards the input, diagnostics, and desired/sent rumble state below.
    SRWLOCK Lock;

    WDFQUEUE ReadQueue;
    UCHAR Controls[PAD_CONTROLS_LEN];
    // Set while the report holds changes no read has picked up yet.
    BOOLEAN GamepadChanged;
    BOOLEAN GuideChanged;

    // Diagnostics, reported through the status feature report.
    UCHAR RawReport[RAW_REPORT_MAX];
    UCHAR RawReportLength;
    BOOLEAN RumbleFailed;
    NTSTATUS LastError;
    NTSTATUS RumbleError;
    ULONG RumbleWrites;
    ULONG RumbleFailures;
    USHORT LastRumbleLatencyMs;

    // Motor speeds: those asked for and those the controller has, both
    // guarded by Lock. A dedicated thread writes the difference so slow BLE
    // operations never block the WDF workitem pool or input handling.
    UCHAR RumbleStrong;
    UCHAR RumbleWeak;
    UCHAR RumbleSentStrong;
    UCHAR RumbleSentWeak;
    BOOLEAN RumbleStateUnknown;
    BOOLEAN RumbleShuttingDown;
    BOOLEAN RumbleStopParked;
    UCHAR RumbleStopParkWakeCount;
    ULONGLONG RumbleStopParkedTick;
    HANDLE RumbleThread;
    HANDLE RumbleWakeEvent;
    HANDLE RumbleStopEvent;
    HANDLE RumbleStopDoneEvent;
    // Serializes SendOutputReport and GattSession teardown.
    SRWLOCK RumbleLock;

    // Runtime user settings (0xE2 feature report), guarded by Lock.
    struct {
        UCHAR StrongGainPct;
        UCHAR WeakGainPct;
        UCHAR StickDeadzonePct;
        UCHAR TriggerDeadzonePct;
        UCHAR TriggerMaxPct;
        UCHAR BleIntervalMs;
        UCHAR BlePostAckGapMs;
        UCHAR BleMinDelta;
        UCHAR Flags;
        UCHAR TurboFlags;
        UCHAR TurboHz;
        USHORT TurboMask;
    } Settings;

    // Hardware Turbo state (Assistant + button toggle), guarded by Lock.
    USHORT TurboPrevPhysicalMask;
    USHORT TurboHeldMask;
    BOOLEAN TurboPrevAssist;
    BOOLEAN TurboPhaseOff;
    UCHAR TurboToggleSeq;
    WDFTIMER TurboTimer;

    // Driver-level HID Keyboard + Consumer macros (0xE3 feature report), guarded by Lock.
    struct {
        UCHAR Flags;
        UCHAR AssistantAction;
        UCHAR CaptureAction;
        UCHAR CustomModMask;
        UCHAR CustomHidUsage;
    } MacroSettings;

    BOOLEAN AssistComboUsed;
    BOOLEAN PrevCaptureDown;
    UCHAR PrevDpadComboHat;
    UCHAR VolHoldDir;
    BOOLEAN VolGapWait;
    UCHAR KeyboardMod;
    UCHAR KeyboardKey;
    BOOLEAN KeyboardChanged;
    BOOLEAN KeyboardReleaseFirst;
    UCHAR ConsumerBits;
    BOOLEAN ConsumerChanged;
    BOOLEAN ConsumerReleaseFirst;
    UCHAR MacroTriggerSeq;
    UCHAR LastMacroId;
    WDFTIMER MacroTimer;

    // USB transport.
    WDFUSBDEVICE UsbDevice;
    WDFUSBPIPE InputPipe;
    // NULL when the interface has no interrupt OUT endpoint; output reports
    // then go through the control endpoint.
    WDFUSBPIPE OutputPipe;
    UCHAR InterfaceNumber;

    // Bluetooth transport.
    WDFIOTARGET LowerDriver;
    BLUETOOTH_READER Readers[BLUETOOTH_READER_COUNT];
    WDFTIMER RetryTimer;
    // Guarded by Lock.
    BOOLEAN Reading;
    // Rumble goes around the driver below, through a session with bthserv.
    // Guarded by RumbleLock.
    UINT64 BluetoothAddress;
    PRIVATE_GATT_SESSION *GattSession;
    HRESULT GattOpenStatus;
    ULONGLONG GattOpenTick;
    ULONG GattConsecutiveFailures;
};

#define STADIA_FLAG_SWAP_STICKS 0x01
#define STADIA_FLAG_INVERT_RY   0x02
#define STADIA_FLAG_INVERT_LY   0x04

#define STADIA_TURBO_FLAG_ENABLED   0x01
#define STADIA_TURBO_FLAG_HOLD_MODE 0x02

#define STADIA_TURBO_BTN_A      0x0001
#define STADIA_TURBO_BTN_B      0x0002
#define STADIA_TURBO_BTN_X      0x0004
#define STADIA_TURBO_BTN_Y      0x0008
#define STADIA_TURBO_BTN_L1     0x0010
#define STADIA_TURBO_BTN_R1     0x0020
#define STADIA_TURBO_BTN_L2     0x0040
#define STADIA_TURBO_BTN_R2     0x0080
#define STADIA_TURBO_BTN_LS     0x0100
#define STADIA_TURBO_BTN_RS     0x0200
#define STADIA_TURBO_BTN_ALL    0x03FF

#define STADIA_MACRO_FLAG_ENABLED    0x01
#define STADIA_MACRO_FLAG_DPAD_MEDIA 0x02

#define STADIA_MACRO_NONE            0
#define STADIA_MACRO_PLAY_PAUSE      1
#define STADIA_MACRO_WIN_SCREENSHOT  2
#define STADIA_MACRO_STEAM_F12       3
#define STADIA_MACRO_GAMEBAR_OVERLAY 4
#define STADIA_MACRO_GAMEBAR_CLIP    5
#define STADIA_MACRO_NEXT_TRACK      6
#define STADIA_MACRO_VOLUME_MUTE     7
#define STADIA_MACRO_CUSTOM_HOTKEY   8
#define STADIA_MACRO_VOL_UP_DPAD     9
#define STADIA_MACRO_VOL_DOWN_DPAD   10
#define STADIA_MACRO_PREV_TRACK_DPAD 11

#define STADIA_CONSUMER_PLAY_PAUSE 0x01
#define STADIA_CONSUMER_NEXT_TRACK 0x02
#define STADIA_CONSUMER_PREV_TRACK 0x04
#define STADIA_CONSUMER_VOL_UP     0x08
#define STADIA_CONSUMER_VOL_DOWN   0x10
#define STADIA_CONSUMER_MUTE       0x20

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

// Publishes new control values as input reports, if they changed.
VOID PadPublishControls(PDEVICE_CONTEXT Context, const UCHAR *Controls);
VOID PadFlushPendingReads(PDEVICE_CONTEXT Context);

EVT_WDF_DEVICE_PREPARE_HARDWARE StadiaPrepareHardware;
EVT_WDF_DEVICE_D0_ENTRY StadiaD0Entry;
EVT_WDF_DEVICE_D0_EXIT StadiaD0Exit;
EVT_WDF_OBJECT_CONTEXT_CLEANUP StadiaDeviceCleanup;
EVT_WDF_TIMER StadiaTurboTimerFunc;
EVT_WDF_TIMER StadiaMacroTimerFunc;

// Takes an input report from the transport.
VOID StadiaInputReport(PDEVICE_CONTEXT Context, const UCHAR *Report, size_t Length);
VOID StadiaSetRumble(PDEVICE_CONTEXT Context, UCHAR Strong, UCHAR Weak);
DWORD WINAPI StadiaRumbleThread(LPVOID Parameter);
VOID StadiaRecordError(PDEVICE_CONTEXT Context, NTSTATUS Status);
VOID StadiaInitDefaultSettings(PDEVICE_CONTEXT Context);
