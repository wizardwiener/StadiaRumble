// Modified by Stadia Rumble Project; derived from aisk/WinStadia under GPL-3.0.
// UMDF2 HID transport minidriver that presents a Stadia controller as an Xbox
// Wireless Controller, in the HID format those use over Bluetooth.
//
// The inbox xinputhid filter sits above this stack and turns the HID reports
// into XInput, as it does for the real thing. Input arrives from the
// controller through stadia.c and the transport below it, USB or Bluetooth;
// rumble written by games goes back the same way.

#include "winstadia.h"
#include <hidport.h>

#define XBOX_VID 0x045E
#define XBOX_PID 0x02FD
#define XBOX_VERSION 0x0903

#define REPORT_ID_GAMEPAD 0x01
#define REPORT_ID_GUIDE 0x02
#define REPORT_ID_RUMBLE 0x03
#define REPORT_ID_BATTERY 0x04
#define REPORT_ID_KEYBOARD 0x05
#define REPORT_ID_CONSUMER 0x06
// Not part of a real Xbox controller: driver state for diagnostics and settings.
#define REPORT_ID_STATUS 0xE1
#define REPORT_ID_SETTINGS 0xE2
#define REPORT_ID_MACROS 0xE3

#define GAMEPAD_REPORT_LEN (1 + PAD_GAMEPAD_LEN)
#define GUIDE_REPORT_LEN 2
#define BATTERY_REPORT_LEN 2
#define KEYBOARD_REPORT_LEN 8
#define CONSUMER_REPORT_LEN 2
#define MAX_INPUT_REPORT_LEN GAMEPAD_REPORT_LEN
#define STATUS_REPORT_LEN 64
#define SETTINGS_REPORT_LEN 16
#define MACRO_SETTINGS_REPORT_LEN 16
#define STATUS_RUMBLE_WRITES_INDEX (7 + RAW_REPORT_MAX)

// Rumble output report: [1] motor enable mask, [2] left trigger, [3] right
// trigger, [4] strong, [5] weak, each 0..100, [6..9] timing, which the Stadia
// has no use for.
#define RUMBLE_REPORT_MIN_LEN 6
#define RUMBLE_ENABLE_WEAK 0x01
#define RUMBLE_ENABLE_STRONG 0x02
#define RUMBLE_MAGNITUDE_MAX 100

// Taken from an Xbox One S controller connected over Bluetooth.
static const UCHAR ReportDescriptor[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop)
    0x09, 0x05,        // Usage (Game Pad)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x01,        //   Report ID (1)
    0x09, 0x01,        //   Usage (Pointer)
    0xA1, 0x00,        //   Collection (Physical)
    0x09, 0x30,        //     Usage (X)
    0x09, 0x31,        //     Usage (Y)
    0x15, 0x00,        //     Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,  //     Logical Maximum (65534)
    0x95, 0x02,        //     Report Count (2)
    0x75, 0x10,        //     Report Size (16)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0xC0,              //   End Collection
    0x09, 0x01,        //   Usage (Pointer)
    0xA1, 0x00,        //   Collection (Physical)
    0x09, 0x33,        //     Usage (Rx)
    0x09, 0x34,        //     Usage (Ry)
    0x15, 0x00,        //     Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,  //     Logical Maximum (65534)
    0x95, 0x02,        //     Report Count (2)
    0x75, 0x10,        //     Report Size (16)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0xC0,              //   End Collection
    0x05, 0x01,        //   Usage Page (Generic Desktop)
    0x09, 0x32,        //   Usage (Z)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x03,  //   Logical Maximum (1023)
    0x95, 0x01,        //   Report Count (1)
    0x75, 0x0A,        //   Report Size (10)
    0x81, 0x02,        //   Input (Data,Var,Abs)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x00,        //   Logical Maximum (0)
    0x75, 0x06,        //   Report Size (6)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x03,        //   Input (Const,Var,Abs)
    0x05, 0x01,        //   Usage Page (Generic Desktop)
    0x09, 0x35,        //   Usage (Rz)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x03,  //   Logical Maximum (1023)
    0x95, 0x01,        //   Report Count (1)
    0x75, 0x0A,        //   Report Size (10)
    0x81, 0x02,        //   Input (Data,Var,Abs)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x00,        //   Logical Maximum (0)
    0x75, 0x06,        //   Report Size (6)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x03,        //   Input (Const,Var,Abs)
    0x05, 0x01,        //   Usage Page (Generic Desktop)
    0x09, 0x39,        //   Usage (Hat switch)
    0x15, 0x01,        //   Logical Minimum (1)
    0x25, 0x08,        //   Logical Maximum (8)
    0x35, 0x00,        //   Physical Minimum (0)
    0x46, 0x3B, 0x01,  //   Physical Maximum (315)
    0x66, 0x14, 0x00,  //   Unit (System: English Rotation, Length: Centimeter)
    0x75, 0x04,        //   Report Size (4)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x42,        //   Input (Data,Var,Abs,Null)
    0x75, 0x04,        //   Report Size (4)
    0x95, 0x01,        //   Report Count (1)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x00,        //   Logical Maximum (0)
    0x35, 0x00,        //   Physical Minimum (0)
    0x45, 0x00,        //   Physical Maximum (0)
    0x65, 0x00,        //   Unit (None)
    0x81, 0x03,        //   Input (Const,Var,Abs)
    0x05, 0x09,        //   Usage Page (Button)
    0x19, 0x01,        //   Usage Minimum (0x01)
    0x29, 0x0A,        //   Usage Maximum (0x0A)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x75, 0x01,        //   Report Size (1)
    0x95, 0x0A,        //   Report Count (10)
    0x81, 0x02,        //   Input (Data,Var,Abs)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x00,        //   Logical Maximum (0)
    0x75, 0x06,        //   Report Size (6)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x03,        //   Input (Const,Var,Abs)
    0x05, 0x01,        //   Usage Page (Generic Desktop)
    0x09, 0x80,        //   Usage (Sys Control)
    0x85, 0x02,        //   Report ID (2)
    0xA1, 0x00,        //   Collection (Physical)
    0x09, 0x85,        //     Usage (Sys Main Menu)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x01,        //     Logical Maximum (1)
    0x95, 0x01,        //     Report Count (1)
    0x75, 0x01,        //     Report Size (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x00,        //     Logical Maximum (0)
    0x75, 0x07,        //     Report Size (7)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x03,        //     Input (Const,Var,Abs)
    0xC0,              //   End Collection
    0x05, 0x0F,        //   Usage Page (PID Page)
    0x09, 0x21,        //   Usage (0x21)
    0x85, 0x03,        //   Report ID (3)
    0xA1, 0x02,        //   Collection (Logical)
    0x09, 0x97,        //     Usage (0x97)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x01,        //     Logical Maximum (1)
    0x75, 0x04,        //     Report Size (4)
    0x95, 0x01,        //     Report Count (1)
    0x91, 0x02,        //     Output (Data,Var,Abs)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x00,        //     Logical Maximum (0)
    0x75, 0x04,        //     Report Size (4)
    0x95, 0x01,        //     Report Count (1)
    0x91, 0x03,        //     Output (Const,Var,Abs)
    0x09, 0x70,        //     Usage (0x70)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x64,        //     Logical Maximum (100)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x04,        //     Report Count (4)
    0x91, 0x02,        //     Output (Data,Var,Abs)
    0x09, 0x50,        //     Usage (0x50)
    0x66, 0x01, 0x10,  //     Unit (System: SI Linear, Time: Seconds)
    0x55, 0x0E,        //     Unit Exponent (-2)
    0x15, 0x00,        //     Logical Minimum (0)
    0x26, 0xFF, 0x00,  //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x91, 0x02,        //     Output (Data,Var,Abs)
    0x09, 0xA7,        //     Usage (0xA7)
    0x15, 0x00,        //     Logical Minimum (0)
    0x26, 0xFF, 0x00,  //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x91, 0x02,        //     Output (Data,Var,Abs)
    0x65, 0x00,        //     Unit (None)
    0x55, 0x00,        //     Unit Exponent (0)
    0x09, 0x7C,        //     Usage (0x7C)
    0x15, 0x00,        //     Logical Minimum (0)
    0x26, 0xFF, 0x00,  //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x91, 0x02,        //     Output (Data,Var,Abs)
    0xC0,              //   End Collection
    0x85, 0x04,        //   Report ID (4)
    0x05, 0x06,        //   Usage Page (Generic Dev)
    0x09, 0x20,        //   Usage (Battery Strength)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x02,        //   Input (Data,Var,Abs)
    // Not part of the original: the status report, see GetFeature.
    0x85, 0xE1,        //   Report ID (225)
    0x06, 0x00, 0xFF,  //   Usage Page (Vendor 0xFF00)
    0x09, 0x01,        //   Usage (0x01)
    0x95, 0x3F,        //   Report Count (63)
    0xB1, 0x02,        //   Feature (Data,Var,Abs)
    // Runtime user settings report (0xE2), see GetFeature/SetFeature.
    0x85, 0xE2,        //   Report ID (226)
    0x06, 0x00, 0xFF,  //   Usage Page (Vendor 0xFF00)
    0x09, 0x02,        //   Usage (0x02)
    0x95, 0x0F,        //   Report Count (15)
    0xB1, 0x02,        //   Feature (Data,Var,Abs)
    // Runtime macro configuration report (0xE3), see GetFeature/SetFeature.
    0x85, 0xE3,        //   Report ID (227)
    0x06, 0x00, 0xFF,  //   Usage Page (Vendor 0xFF00)
    0x09, 0x03,        //   Usage (0x03)
    0x95, 0x0F,        //   Report Count (15)
    0xB1, 0x02,        //   Feature (Data,Var,Abs)
    0xC0,              // End Collection
    // Standard HID Keyboard collection for standalone driver-level macros.
    0x05, 0x01,        // Usage Page (Generic Desktop)
    0x09, 0x06,        // Usage (Keyboard)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x05,        //   Report ID (5)
    0x05, 0x07,        //   Usage Page (Key Codes)
    0x19, 0xE0,        //   Usage Minimum (224 - Left Control)
    0x29, 0xE7,        //   Usage Maximum (231 - Right GUI)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x75, 0x01,        //   Report Size (1)
    0x95, 0x08,        //   Report Count (8)
    0x81, 0x02,        //   Input (Data,Var,Abs)
    0x19, 0x00,        //   Usage Minimum (0)
    0x29, 0xDD,        //   Usage Maximum (221)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xDD, 0x00,  //   Logical Maximum (221)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x06,        //   Report Count (6)
    0x81, 0x00,        //   Input (Data,Array)
    0xC0,              // End Collection
    // Standard HID Consumer Control collection for standalone media & volume macros.
    0x05, 0x0C,        // Usage Page (Consumer)
    0x09, 0x01,        // Usage (Consumer Control)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x06,        //   Report ID (6)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x75, 0x01,        //   Report Size (1)
    0x95, 0x07,        //   Report Count (7)
    0x09, 0xCD,        //   Usage (Play/Pause)        bit 0 (0x01)
    0x09, 0xB5,        //   Usage (Scan Next Track)   bit 1 (0x02)
    0x09, 0xB6,        //   Usage (Scan Prev Track)   bit 2 (0x04)
    0x09, 0xE9,        //   Usage (Volume Increment)  bit 3 (0x08)
    0x09, 0xEA,        //   Usage (Volume Decrement)  bit 4 (0x10)
    0x09, 0xE2,        //   Usage (Mute)              bit 5 (0x20)
    0x09, 0xB7,        //   Usage (Stop)              bit 6 (0x40)
    0x81, 0x02,        //   Input (Data,Var,Abs)
    0x75, 0x01,        //   Report Size (1)
    0x95, 0x01,        //   Report Count (1)
    0x81, 0x03,        //   Input (Const,Var,Abs)
    0xC0,              // End Collection
};

static const HID_DESCRIPTOR HidDescriptor = {
    0x09,   // bLength
    0x21,   // bDescriptorType (HID)
    0x0100, // bcdHID
    0x00,   // bCountry
    0x01,   // bNumDescriptors
    {{0x22, sizeof(ReportDescriptor)}},
};

static const HID_DEVICE_ATTRIBUTES DeviceAttributes = {
    sizeof(HID_DEVICE_ATTRIBUTES),
    XBOX_VID,
    XBOX_PID,
    XBOX_VERSION,
};

// Sticks centered, triggers and buttons released.
static const UCHAR IdleControls[PAD_CONTROLS_LEN] = {0x00, 0x80, 0x00, 0x80, 0x00, 0x80, 0x00, 0x80};

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;

static NTSTATUS
CopyToRequest(WDFREQUEST Request, const VOID *Source, size_t Length)
{
    WDFMEMORY memory;
    size_t capacity;
    NTSTATUS status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    WdfMemoryGetBuffer(memory, &capacity);
    if (capacity < Length) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    status = WdfMemoryCopyFromBuffer(memory, 0, (PVOID)Source, Length);
    if (NT_SUCCESS(status)) {
        WdfRequestSetInformation(Request, Length);
    }
    return status;
}

static NTSTATUS
GetInputBuffer(WDFREQUEST Request, size_t MinLength, PUCHAR *Buffer, size_t *Length)
{
    WDFMEMORY memory;
    NTSTATUS status = WdfRequestRetrieveInputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    *Buffer = WdfMemoryGetBuffer(memory, Length);
    return *Length < MinLength ? STATUS_INVALID_BUFFER_SIZE : STATUS_SUCCESS;
}

// Builds the input report with the given ID from the current controls and
// returns its length, 0 for unknown IDs. Caller holds the lock.
static size_t
BuildInputReport(PDEVICE_CONTEXT Context, UCHAR ReportId, PUCHAR Report)
{
    Report[0] = ReportId;
    switch (ReportId) {
    case REPORT_ID_GAMEPAD:
        RtlCopyMemory(Report + 1, Context->Controls, PAD_GAMEPAD_LEN);
        return GAMEPAD_REPORT_LEN;
    case REPORT_ID_GUIDE:
        Report[1] = Context->Controls[PAD_GUIDE_INDEX];
        return GUIDE_REPORT_LEN;
    case REPORT_ID_BATTERY:
        Report[1] = 0xFF;
        return BATTERY_REPORT_LEN;
    case REPORT_ID_KEYBOARD:
        RtlZeroMemory(Report + 1, KEYBOARD_REPORT_LEN - 1);
        Report[1] = Context->KeyboardMod;
        Report[2] = Context->KeyboardKey;
        return KEYBOARD_REPORT_LEN;
    case REPORT_ID_CONSUMER:
        Report[1] = Context->ConsumerBits;
        return CONSUMER_REPORT_LEN;
    default:
        return 0;
    }
}

// Builds the next input report that has unsent changes and returns its
// length, 0 when there is none. Caller holds the lock.
// Macro Keyboard/Consumer reports run first so Windows receives hotkeys with zero extra IRP delay.
static size_t
NextChangedReport(PDEVICE_CONTEXT Context, PUCHAR Report)
{
    if (Context->KeyboardChanged) {
        if (Context->KeyboardReleaseFirst) {
            Context->KeyboardReleaseFirst = FALSE;
            Report[0] = REPORT_ID_KEYBOARD;
            RtlZeroMemory(Report + 1, KEYBOARD_REPORT_LEN - 1);
            return KEYBOARD_REPORT_LEN;
        }
        Context->KeyboardChanged = FALSE;
        return BuildInputReport(Context, REPORT_ID_KEYBOARD, Report);
    }
    if (Context->ConsumerChanged) {
        if (Context->ConsumerReleaseFirst) {
            Context->ConsumerReleaseFirst = FALSE;
            Report[0] = REPORT_ID_CONSUMER;
            Report[1] = 0;
            return CONSUMER_REPORT_LEN;
        }
        Context->ConsumerChanged = FALSE;
        return BuildInputReport(Context, REPORT_ID_CONSUMER, Report);
    }
    if (Context->GamepadChanged) {
        Context->GamepadChanged = FALSE;
        return BuildInputReport(Context, REPORT_ID_GAMEPAD, Report);
    }
    if (Context->GuideChanged) {
        Context->GuideChanged = FALSE;
        return BuildInputReport(Context, REPORT_ID_GUIDE, Report);
    }
    return 0;
}

// Caller holds Context->Lock exclusively; populates completed[] and returns count.
static size_t
DrainChangedReportsLocked(PDEVICE_CONTEXT Context, WDFREQUEST *Requests, NTSTATUS *Statuses, size_t MaxCount)
{
    size_t completedCount = 0;

    while ((Context->KeyboardChanged || Context->ConsumerChanged ||
            Context->GamepadChanged || Context->GuideChanged) &&
           completedCount < MaxCount) {
        UCHAR report[MAX_INPUT_REPORT_LEN];
        WDFREQUEST read;
        size_t length;

        if (!NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Context->ReadQueue, &read))) {
            break;
        }
        length = NextChangedReport(Context, report);
        Requests[completedCount] = read;
        Statuses[completedCount] = CopyToRequest(read, report, length);
        completedCount++;
    }
    return completedCount;
}

VOID
PadFlushPendingReads(PDEVICE_CONTEXT Context)
{
    WDFREQUEST requests[6];
    NTSTATUS statuses[6];
    size_t completedCount;

    AcquireSRWLockExclusive(&Context->Lock);
    completedCount = DrainChangedReportsLocked(Context, requests, statuses, 6);
    ReleaseSRWLockExclusive(&Context->Lock);

    for (size_t i = 0; i < completedCount; i++) {
        WdfRequestComplete(requests[i], statuses[i]);
    }
}

VOID
PadPublishControls(PDEVICE_CONTEXT Context, const UCHAR *Controls)
{
    WDFREQUEST requests[6];
    NTSTATUS statuses[6];
    size_t completedCount;

    AcquireSRWLockExclusive(&Context->Lock);
    if (!RtlEqualMemory(Context->Controls, Controls, PAD_GAMEPAD_LEN)) {
        Context->GamepadChanged = TRUE;
    }
    if (Context->Controls[PAD_GUIDE_INDEX] != Controls[PAD_GUIDE_INDEX]) {
        Context->GuideChanged = TRUE;
    }
    RtlCopyMemory(Context->Controls, Controls, PAD_CONTROLS_LEN);

    // Taking pending reads under the lock keeps ReadReport from parking a
    // request right after this update, which would deliver it late, while
    // completing them after releasing the lock prevents lock contention.
    completedCount = DrainChangedReportsLocked(Context, requests, statuses, 6);
    ReleaseSRWLockExclusive(&Context->Lock);

    for (size_t i = 0; i < completedCount; i++) {
        WdfRequestComplete(requests[i], statuses[i]);
    }
}

static NTSTATUS
ReadReport(PDEVICE_CONTEXT Context, WDFREQUEST Request, BOOLEAN *Complete)
{
    UCHAR report[MAX_INPUT_REPORT_LEN];
    size_t length;
    NTSTATUS status;

    AcquireSRWLockExclusive(&Context->Lock);
    length = NextChangedReport(Context, report);
    if (length != 0) {
        ReleaseSRWLockExclusive(&Context->Lock);
        return CopyToRequest(Request, report, length);
    }
    status = WdfRequestForwardToIoQueue(Request, Context->ReadQueue);
    ReleaseSRWLockExclusive(&Context->Lock);

    *Complete = !NT_SUCCESS(status);
    return status;
}

// The report buffer of write-type requests starts with the report ID.
static NTSTATUS
WriteReport(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    PUCHAR report;
    size_t length;
    NTSTATUS status = GetInputBuffer(Request, RUMBLE_REPORT_MIN_LEN, &report, &length);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    if (report[0] != REPORT_ID_RUMBLE) {
        return STATUS_INVALID_PARAMETER;
    }

    UCHAR strong = (report[1] & RUMBLE_ENABLE_STRONG) ? (UCHAR)min(report[4], RUMBLE_MAGNITUDE_MAX) : 0;
    UCHAR weak = (report[1] & RUMBLE_ENABLE_WEAK) ? (UCHAR)min(report[5], RUMBLE_MAGNITUDE_MAX) : 0;
    UCHAR strongGain;
    UCHAR weakGain;

    AcquireSRWLockShared(&Context->Lock);
    strongGain = Context->Settings.StrongGainPct;
    weakGain = Context->Settings.WeakGainPct;
    ReleaseSRWLockShared(&Context->Lock);

    ULONG strongScaled = ((ULONG)strong * 255 * (ULONG)strongGain + 5000) / 10000;
    ULONG weakScaled = ((ULONG)weak * 255 * (ULONG)weakGain + 5000) / 10000;
    StadiaSetRumble(Context, (UCHAR)min(strongScaled, 255), (UCHAR)min(weakScaled, 255));
    WdfRequestSetInformation(Request, length);
    return STATUS_SUCCESS;
}

// The input buffer of GET_INPUT_REPORT holds the report ID, the output buffer
// receives the whole report including the ID.
static NTSTATUS
GetInputReport(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    UCHAR report[MAX_INPUT_REPORT_LEN];
    PUCHAR id;
    size_t length;
    NTSTATUS status = GetInputBuffer(Request, 1, &id, &length);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    AcquireSRWLockShared(&Context->Lock);
    length = BuildInputReport(Context, *id, report);
    ReleaseSRWLockShared(&Context->Lock);
    return length == 0 ? STATUS_INVALID_PARAMETER : CopyToRequest(Request, report, length);
}

static VOID
SaveSettingsToRegistry(PDEVICE_CONTEXT Context, const UCHAR *Blob, size_t BlobLength)
{
    static const WCHAR settingsName[] = L"StadiaSettings";
    UNICODE_STRING valueName = {
        sizeof(settingsName) - sizeof(WCHAR),
        sizeof(settingsName),
        (PWSTR)settingsName
    };
    WDFKEY key = NULL;
    NTSTATUS status;

    if (Context->Device == NULL) {
        return;
    }
    status = WdfDeviceOpenRegistryKey(Context->Device, PLUGPLAY_REGKEY_DEVICE, KEY_SET_VALUE,
                                      WDF_NO_OBJECT_ATTRIBUTES, &key);
    if (!NT_SUCCESS(status)) {
        return;
    }
    WdfRegistryAssignValue(key, &valueName, REG_BINARY, (ULONG)BlobLength, (PVOID)Blob);
    WdfRegistryClose(key);
}

static VOID
ApplySettingsBlob(PDEVICE_CONTEXT Context, const UCHAR *Report, size_t Length)
{
    Context->Settings.StrongGainPct = (UCHAR)min(Report[2], 100);
    Context->Settings.WeakGainPct = (UCHAR)min(Report[3], 100);
    Context->Settings.StickDeadzonePct = (UCHAR)min(Report[4], 25);
    Context->Settings.TriggerDeadzonePct = (UCHAR)min(Report[5], 20);
    Context->Settings.TriggerMaxPct = (Report[6] < 50) ? (UCHAR)50 : (UCHAR)min(Report[6], 100);
    Context->Settings.BleIntervalMs = (Report[7] < 15) ? (UCHAR)15 : (UCHAR)min(Report[7], 100);
    Context->Settings.BlePostAckGapMs = (UCHAR)min(Report[8], 30);
    Context->Settings.BleMinDelta = (UCHAR)min(Report[9], 20);
    Context->Settings.Flags =
        (UCHAR)(Report[10] & (STADIA_FLAG_SWAP_STICKS | STADIA_FLAG_INVERT_RY | STADIA_FLAG_INVERT_LY));
    if (Length >= 15) {
        Context->Settings.TurboFlags =
            (UCHAR)(Report[11] & (STADIA_TURBO_FLAG_ENABLED | STADIA_TURBO_FLAG_HOLD_MODE));
        Context->Settings.TurboHz = (Report[12] < 5) ? (UCHAR)12 : (UCHAR)min(Report[12], 25);
        Context->Settings.TurboMask = (USHORT)((((USHORT)Report[14] << 8) | Report[13]) & STADIA_TURBO_BTN_ALL);
    }
}

static VOID
LoadSettingsFromRegistry(PDEVICE_CONTEXT Context)
{
    static const WCHAR settingsName[] = L"StadiaSettings";
    UNICODE_STRING valueName = {
        sizeof(settingsName) - sizeof(WCHAR),
        sizeof(settingsName),
        (PWSTR)settingsName
    };
    WDFKEY key = NULL;
    UCHAR blob[SETTINGS_REPORT_LEN] = {0};
    ULONG length = 0;
    ULONG type = 0;

    if (Context->Device == NULL) {
        return;
    }
    if (!NT_SUCCESS(WdfDeviceOpenRegistryKey(Context->Device, PLUGPLAY_REGKEY_DEVICE, KEY_QUERY_VALUE,
                                             WDF_NO_OBJECT_ATTRIBUTES, &key))) {
        return;
    }
    if (NT_SUCCESS(WdfRegistryQueryValue(key, &valueName, sizeof(blob), blob, &length, &type)) &&
        type == REG_BINARY && length >= 11 && blob[0] == REPORT_ID_SETTINGS && blob[1] == 1) {
        AcquireSRWLockExclusive(&Context->Lock);
        ApplySettingsBlob(Context, blob, (size_t)length);
        ReleaseSRWLockExclusive(&Context->Lock);
    }
    WdfRegistryClose(key);
}

static VOID
SaveMacroSettingsToRegistry(PDEVICE_CONTEXT Context, const UCHAR *Blob, size_t BlobLength)
{
    static const WCHAR macroName[] = L"StadiaMacroSettings";
    UNICODE_STRING valueName = {
        sizeof(macroName) - sizeof(WCHAR),
        sizeof(macroName),
        (PWSTR)macroName
    };
    WDFKEY key = NULL;

    if (Context->Device == NULL) {
        return;
    }
    if (!NT_SUCCESS(WdfDeviceOpenRegistryKey(Context->Device, PLUGPLAY_REGKEY_DEVICE, KEY_SET_VALUE,
                                             WDF_NO_OBJECT_ATTRIBUTES, &key))) {
        return;
    }
    WdfRegistryAssignValue(key, &valueName, REG_BINARY, (ULONG)BlobLength, (PVOID)Blob);
    WdfRegistryClose(key);
}

static VOID
ApplyMacroSettingsBlob(PDEVICE_CONTEXT Context, const UCHAR *Report, size_t Length)
{
    if (Length < 7) {
        return;
    }
    Context->MacroSettings.Flags = (UCHAR)(Report[2] & (STADIA_MACRO_FLAG_ENABLED | STADIA_MACRO_FLAG_DPAD_MEDIA));
    Context->MacroSettings.AssistantAction = (Report[3] <= STADIA_MACRO_CUSTOM_HOTKEY) ? Report[3] : STADIA_MACRO_NONE;
    Context->MacroSettings.CaptureAction = (Report[4] <= STADIA_MACRO_CUSTOM_HOTKEY) ? Report[4] : STADIA_MACRO_NONE;
    Context->MacroSettings.CustomModMask = (UCHAR)(Report[5] & 0xFF);
    Context->MacroSettings.CustomHidUsage = (Report[6] <= 0xDD) ? Report[6] : 0;
}

static VOID
LoadMacroSettingsFromRegistry(PDEVICE_CONTEXT Context)
{
    static const WCHAR macroName[] = L"StadiaMacroSettings";
    UNICODE_STRING valueName = {
        sizeof(macroName) - sizeof(WCHAR),
        sizeof(macroName),
        (PWSTR)macroName
    };
    WDFKEY key = NULL;
    UCHAR blob[MACRO_SETTINGS_REPORT_LEN] = {0};
    ULONG length = 0;
    ULONG type = 0;

    if (Context->Device == NULL) {
        return;
    }
    if (!NT_SUCCESS(WdfDeviceOpenRegistryKey(Context->Device, PLUGPLAY_REGKEY_DEVICE, KEY_QUERY_VALUE,
                                             WDF_NO_OBJECT_ATTRIBUTES, &key))) {
        return;
    }
    if (NT_SUCCESS(WdfRegistryQueryValue(key, &valueName, sizeof(blob), blob, &length, &type)) &&
        type == REG_BINARY && length >= 7 && blob[0] == REPORT_ID_MACROS && blob[1] == 1) {
        AcquireSRWLockExclusive(&Context->Lock);
        ApplyMacroSettingsBlob(Context, blob, (size_t)length);
        ReleaseSRWLockExclusive(&Context->Lock);
    }
    WdfRegistryClose(key);
}

// Same buffer conventions as GET_INPUT_REPORT.
static NTSTATUS
GetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    PUCHAR id;
    size_t length;
    NTSTATUS status = GetInputBuffer(Request, 1, &id, &length);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (*id == REPORT_ID_SETTINGS) {
        UCHAR settingsReport[SETTINGS_REPORT_LEN] = {REPORT_ID_SETTINGS, 1};

        AcquireSRWLockShared(&Context->Lock);
        settingsReport[2] = Context->Settings.StrongGainPct;
        settingsReport[3] = Context->Settings.WeakGainPct;
        settingsReport[4] = Context->Settings.StickDeadzonePct;
        settingsReport[5] = Context->Settings.TriggerDeadzonePct;
        settingsReport[6] = Context->Settings.TriggerMaxPct;
        settingsReport[7] = Context->Settings.BleIntervalMs;
        settingsReport[8] = Context->Settings.BlePostAckGapMs;
        settingsReport[9] = Context->Settings.BleMinDelta;
        settingsReport[10] = Context->Settings.Flags;
        settingsReport[11] = Context->Settings.TurboFlags;
        settingsReport[12] = Context->Settings.TurboHz;
        settingsReport[13] = (UCHAR)(Context->Settings.TurboMask & 0xFF);
        settingsReport[14] = (UCHAR)((Context->Settings.TurboMask >> 8) & 0xFF);
        ReleaseSRWLockShared(&Context->Lock);
        return CopyToRequest(Request, settingsReport, sizeof(settingsReport));
    }

    if (*id == REPORT_ID_MACROS) {
        UCHAR macroReport[MACRO_SETTINGS_REPORT_LEN] = {REPORT_ID_MACROS, 1};

        AcquireSRWLockShared(&Context->Lock);
        macroReport[2] = Context->MacroSettings.Flags;
        macroReport[3] = Context->MacroSettings.AssistantAction;
        macroReport[4] = Context->MacroSettings.CaptureAction;
        macroReport[5] = Context->MacroSettings.CustomModMask;
        macroReport[6] = Context->MacroSettings.CustomHidUsage;
        ReleaseSRWLockShared(&Context->Lock);
        return CopyToRequest(Request, macroReport, sizeof(macroReport));
    }

    if (*id != REPORT_ID_STATUS) {
        return STATUS_INVALID_PARAMETER;
    }

    UCHAR report[STATUS_REPORT_LEN] = {REPORT_ID_STATUS};
    // [1] rumble failed, [2..5] last driver error NTSTATUS (LE), [6] length
    // of the last raw Stadia input report, [7..22] that report, [23..26]
    // successful rumble writes (LE), [27..30] result of the latest rumble
    // write (LE), [31] sent strong, [32] sent weak, [33] isBluetooth,
    // [34..37] failed rumble writes (LE), [38..39] last latency ms,
    // [40] battery, [41..42] active TurboMask (LE), [43] TurboToggleSeq,
    // [44] MacroTriggerSeq, [45] LastMacroId.
    AcquireSRWLockShared(&Context->Lock);
    report[1] = Context->RumbleFailed;
    RtlCopyMemory(report + 2, &Context->LastError, sizeof(NTSTATUS));
    report[6] = Context->RawReportLength;
    RtlCopyMemory(report + 7, Context->RawReport, Context->RawReportLength);
    RtlCopyMemory(report + STATUS_RUMBLE_WRITES_INDEX, &Context->RumbleWrites, sizeof(ULONG));
    RtlCopyMemory(report + 27, &Context->RumbleError, sizeof(NTSTATUS));
    report[31] = Context->RumbleSentStrong;
    report[32] = Context->RumbleSentWeak;
    report[33] = Context->IsBluetooth;
    RtlCopyMemory(report + 34, &Context->RumbleFailures, sizeof(ULONG));
    RtlCopyMemory(report + 38, &Context->LastRumbleLatencyMs, sizeof(USHORT));
    report[40] = Context->IsBluetooth ? 0xFF : 100;
    report[41] = (UCHAR)(Context->Settings.TurboMask & 0xFF);
    report[42] = (UCHAR)((Context->Settings.TurboMask >> 8) & 0xFF);
    report[43] = Context->TurboToggleSeq;
    report[44] = Context->MacroTriggerSeq;
    report[45] = Context->LastMacroId;
    ReleaseSRWLockShared(&Context->Lock);
    return CopyToRequest(Request, report, sizeof(report));
}

static NTSTATUS
SetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    PUCHAR report;
    size_t length;
    NTSTATUS status = GetInputBuffer(Request, 7, &report, &length);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (report[0] == REPORT_ID_MACROS && report[1] == 1) {
        UCHAR normalizedMacro[MACRO_SETTINGS_REPORT_LEN] = {REPORT_ID_MACROS, 1};
        if (length < MACRO_SETTINGS_REPORT_LEN) {
            return STATUS_INVALID_BUFFER_SIZE;
        }
        AcquireSRWLockExclusive(&Context->Lock);
        ApplyMacroSettingsBlob(Context, report, length);
        normalizedMacro[2] = Context->MacroSettings.Flags;
        normalizedMacro[3] = Context->MacroSettings.AssistantAction;
        normalizedMacro[4] = Context->MacroSettings.CaptureAction;
        normalizedMacro[5] = Context->MacroSettings.CustomModMask;
        normalizedMacro[6] = Context->MacroSettings.CustomHidUsage;
        ReleaseSRWLockExclusive(&Context->Lock);

        SaveMacroSettingsToRegistry(Context, normalizedMacro, sizeof(normalizedMacro));
        WdfRequestSetInformation(Request, length);
        return STATUS_SUCCESS;
    }

    if (report[0] != REPORT_ID_SETTINGS || report[1] != 1) {
        return STATUS_INVALID_PARAMETER;
    }
    if (length < SETTINGS_REPORT_LEN) {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    UCHAR normalized[SETTINGS_REPORT_LEN] = {REPORT_ID_SETTINGS, 1};
    AcquireSRWLockExclusive(&Context->Lock);
    ApplySettingsBlob(Context, report, length);
    normalized[2] = Context->Settings.StrongGainPct;
    normalized[3] = Context->Settings.WeakGainPct;
    normalized[4] = Context->Settings.StickDeadzonePct;
    normalized[5] = Context->Settings.TriggerDeadzonePct;
    normalized[6] = Context->Settings.TriggerMaxPct;
    normalized[7] = Context->Settings.BleIntervalMs;
    normalized[8] = Context->Settings.BlePostAckGapMs;
    normalized[9] = Context->Settings.BleMinDelta;
    normalized[10] = Context->Settings.Flags;
    normalized[11] = Context->Settings.TurboFlags;
    normalized[12] = Context->Settings.TurboHz;
    normalized[13] = (UCHAR)(Context->Settings.TurboMask & 0xFF);
    normalized[14] = (UCHAR)((Context->Settings.TurboMask >> 8) & 0xFF);
    ReleaseSRWLockExclusive(&Context->Lock);

    SaveSettingsToRegistry(Context, normalized, sizeof(normalized));
    if (Context->RumbleWakeEvent != NULL) {
        SetEvent(Context->RumbleWakeEvent);
    }
    WdfRequestSetInformation(Request, length);
    return STATUS_SUCCESS;
}

static NTSTATUS
GetString(WDFREQUEST Request)
{
    PUCHAR input;
    size_t length;
    NTSTATUS status = GetInputBuffer(Request, sizeof(ULONG), &input, &length);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // The low word is the string ID, the high word the language ID.
    switch (*(PULONG)input & 0xFFFF) {
    case HID_STRING_ID_IMANUFACTURER: {
        static const WCHAR manufacturer[] = L"Microsoft";
        return CopyToRequest(Request, manufacturer, sizeof(manufacturer));
    }
    case HID_STRING_ID_IPRODUCT: {
        static const WCHAR product[] = L"Xbox Wireless Controller";
        return CopyToRequest(Request, product, sizeof(product));
    }
    case HID_STRING_ID_ISERIALNUMBER: {
        static const WCHAR serial[] = L"StadiaRumble";
        return CopyToRequest(Request, serial, sizeof(serial));
    }
    default:
        return STATUS_INVALID_PARAMETER;
    }
}

VOID
EvtIoDeviceControl(
    WDFQUEUE Queue,
    WDFREQUEST Request,
    size_t OutputBufferLength,
    size_t InputBufferLength,
    ULONG IoControlCode)
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfIoQueueGetDevice(Queue));
    BOOLEAN complete = TRUE;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = CopyToRequest(Request, &HidDescriptor, HidDescriptor.bLength);
        break;
    case IOCTL_HID_GET_DEVICE_ATTRIBUTES:
        status = CopyToRequest(Request, &DeviceAttributes, sizeof(DeviceAttributes));
        break;
    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = CopyToRequest(Request, ReportDescriptor, sizeof(ReportDescriptor));
        break;
    case IOCTL_HID_READ_REPORT:
        status = ReadReport(context, Request, &complete);
        break;
    case IOCTL_HID_WRITE_REPORT:
    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT:
        status = WriteReport(context, Request);
        break;
    case IOCTL_UMDF_HID_GET_FEATURE:
        status = GetFeature(context, Request);
        break;
    case IOCTL_UMDF_HID_SET_FEATURE:
        status = SetFeature(context, Request);
        break;
    case IOCTL_UMDF_HID_GET_INPUT_REPORT:
        status = GetInputReport(context, Request);
        break;
    case IOCTL_HID_GET_STRING:
        status = GetString(Request);
        break;
    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    if (complete) {
        WdfRequestComplete(Request, status);
    }
}

// The INF puts the driver on the controller's USB HID interface and on its
// HID service on Bluetooth LE.
static BOOLEAN
IsBluetooth(PWDFDEVICE_INIT DeviceInit)
{
    WDFMEMORY memory;
    BOOLEAN bluetooth = FALSE;

    if (NT_SUCCESS(WdfFdoInitAllocAndQueryProperty(DeviceInit, DevicePropertyEnumeratorName, NonPagedPoolNx,
                                                   WDF_NO_OBJECT_ATTRIBUTES, &memory))) {
        bluetooth = _wcsicmp(WdfMemoryGetBuffer(memory, NULL), L"BTHLEDevice") == 0;
        WdfObjectDelete(memory);
    }
    return bluetooth;
}

NTSTATUS
EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    WDF_PNPPOWER_EVENT_CALLBACKS pnpCallbacks;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDFDEVICE device;
    WDFQUEUE queue;
    PDEVICE_CONTEXT context;
    BOOLEAN bluetooth;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);

    // mshidumdf.sys is the function driver, this driver sits below it.
    WdfFdoInitSetFilter(DeviceInit);
    bluetooth = IsBluetooth(DeviceInit);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpCallbacks);
    pnpCallbacks.EvtDevicePrepareHardware = StadiaPrepareHardware;
    pnpCallbacks.EvtDeviceD0Entry = StadiaD0Entry;
    pnpCallbacks.EvtDeviceD0Exit = StadiaD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpCallbacks);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = StadiaDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    context = GetDeviceContext(device);
    context->Device = device;
    context->IsBluetooth = bluetooth;
    context->Transport = bluetooth ? &BluetoothTransport : &UsbTransport;
    InitializeSRWLock(&context->Lock);
    InitializeSRWLock(&context->RumbleLock);
    RtlCopyMemory(context->Controls, IdleControls, PAD_CONTROLS_LEN);
    StadiaInitDefaultSettings(context);
    LoadSettingsFromRegistry(context);
    LoadMacroSettingsFromRegistry(context);

    context->RumbleWakeEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
    context->RumbleStopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    context->RumbleStopDoneEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (context->RumbleWakeEvent == NULL || context->RumbleStopEvent == NULL ||
        context->RumbleStopDoneEvent == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    // Parallel, so that no request holds up another.
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_TIMER_CONFIG timerConfig;
    WDF_OBJECT_ATTRIBUTES timerAttrs;
    WDF_TIMER_CONFIG_INIT(&timerConfig, StadiaTurboTimerFunc);
    timerConfig.AutomaticSerialization = FALSE;
    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttrs);
    timerAttrs.ParentObject = device;
    status = WdfTimerCreate(&timerConfig, &timerAttrs, &context->TurboTimer);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_TIMER_CONFIG_INIT(&timerConfig, StadiaMacroTimerFunc);
    timerConfig.AutomaticSerialization = FALSE;
    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttrs);
    timerAttrs.ParentObject = device;
    status = WdfTimerCreate(&timerConfig, &timerAttrs, &context->MacroTimer);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // Holds IOCTL_HID_READ_REPORT requests until the input state changes.
    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &context->ReadQueue);
}

NTSTATUS
DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}
