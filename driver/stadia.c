// Stadia protocol: translates the controller's input reports to Xbox controls
// and rumble to its output report. The transports (usb.c, bluetooth.c) carry
// the reports.

#include "winstadia.h"

// Stadia input report layout:
//   [0] report ID (0x03)
//   [1] d-pad hat: 0 = up, clockwise to 7 = up-left, 8 = released
//   [2] RS click, Options, Menu, Stadia, R2, L2, Assistant, Capture (bit 7..0)
//   [3] -, A, B, X, Y, L1, R1, LS click (bit 7..0)
//   [4..8] left X, left Y, right X, right Y: centered at 128, Y grows downwards
//   [8..10] L2, R2 analog
#define STADIA_INPUT_REPORT_ID 0x03
#define STADIA_INPUT_REPORT_LEN 10
#define STADIA_HAT_RELEASED 8

#define STADIA_B2_RS 0x80
#define STADIA_B2_OPTIONS 0x40
#define STADIA_B2_MENU 0x20
#define STADIA_B2_STADIA 0x10
#define STADIA_B2_ASSISTANT 0x02
#define STADIA_B2_CAPTURE 0x01

#define STADIA_B3_A 0x40
#define STADIA_B3_B 0x20
#define STADIA_B3_X 0x10
#define STADIA_B3_Y 0x08
#define STADIA_B3_L1 0x04
#define STADIA_B3_R1 0x02
#define STADIA_B3_LS 0x01

// Xbox controls, as laid out in PAD_CONTROLS_LEN:
//   [0..8] left X, left Y, right X, right Y: 16 bit little endian, centered
//          at 0x8000, Y grows downwards
//   [8..12] left, right trigger: 10 bit little endian
//   [12] d-pad hat: 1 = up, clockwise to 8 = up-left, 0 = released
//   [13] Menu, View, RB, LB, Y, X, B, A (bit 7..0)
//   [14] RS click, LS click (bit 1..0)
//   [15] Xbox button
#define XBOX_B13_MENU 0x80
#define XBOX_B13_VIEW 0x40
#define XBOX_B13_RB 0x20
#define XBOX_B13_LB 0x10
#define XBOX_B13_Y 0x08
#define XBOX_B13_X 0x04
#define XBOX_B13_B 0x02
#define XBOX_B13_A 0x01

#define XBOX_B14_RS 0x02
#define XBOX_B14_LS 0x01

#define BIT(source, mask, target) (((source) & (mask)) ? (target) : 0)

#define RUMBLE_STOP_RETRY_INITIAL_MS 80
#define RUMBLE_STOP_RETRY_MAX_MS 500
#define RUMBLE_STOP_RETRY_MAX_ATTEMPTS 30
#define RUMBLE_STOP_PARK_WAKE_COOLDOWN_MS 10000
#define RUMBLE_STOP_PARK_WAKE_MAX 2
#define RUMBLE_NONZERO_RETRY_INITIAL_MS 150
#define RUMBLE_NONZERO_RETRY_MAX_MS 1200
#define RUMBLE_NONZERO_RETRY_MAX_ATTEMPTS 5
#define RUMBLE_SHUTDOWN_STOP_WAIT_MS 350
#define RUMBLE_THREAD_JOIN_TIMEOUT_MS 2000

static VOID
PutUshort(UCHAR *Target, ULONG Value)
{
    Target[0] = (UCHAR)Value;
    Target[1] = (UCHAR)(Value >> 8);
}

VOID
StadiaInitDefaultSettings(PDEVICE_CONTEXT Context)
{
    Context->Settings.StrongGainPct = 100;
    Context->Settings.WeakGainPct = 100;
    Context->Settings.StickDeadzonePct = 0;
    Context->Settings.TriggerDeadzonePct = 0;
    Context->Settings.TriggerMaxPct = 100;
    Context->Settings.BleIntervalMs = 40;
    Context->Settings.BlePostAckGapMs = 12;
    Context->Settings.BleMinDelta = 8;
    Context->Settings.Flags = 0;
    Context->Settings.TurboFlags = STADIA_TURBO_FLAG_ENABLED;
    Context->Settings.TurboHz = 12;
    Context->Settings.TurboMask = 0;

    Context->MacroSettings.Flags = STADIA_MACRO_FLAG_ENABLED | STADIA_MACRO_FLAG_DPAD_MEDIA;
    Context->MacroSettings.AssistantAction = STADIA_MACRO_PLAY_PAUSE;
    Context->MacroSettings.CaptureAction = STADIA_MACRO_WIN_SCREENSHOT;
    Context->MacroSettings.CustomModMask = 0;
    Context->MacroSettings.CustomHidUsage = 0x44; // F11
    Context->PrevDpadComboHat = STADIA_HAT_RELEASED;
}

static int
MapStickAxis(UCHAR Raw, UCHAR DeadzonePct, BOOLEAN Invert)
{
    int d = (int)max(Raw, 1) - 128;

    if (Invert) {
        d = -d;
        if (d < -127) {
            d = -127;
        } else if (d > 127) {
            d = 127;
        }
    }
    if (DeadzonePct > 0) {
        int dz = (127 * (int)DeadzonePct + 50) / 100;
        int mag = d < 0 ? -d : d;
        if (mag <= dz) {
            return 0;
        }
        int span = 127 - dz;
        int scaled = span > 0 ? ((mag - dz) * 127) / span : 127;
        if (scaled > 127) {
            scaled = 127;
        }
        d = (d < 0) ? -scaled : scaled;
    }
    return d;
}

static ULONG
MapTriggerAxis(UCHAR Raw, UCHAR DeadzonePct, UCHAR MaxPct)
{
    int v = (int)Raw;
    int minT = (255 * (int)DeadzonePct) / 100;
    int maxT = (255 * (int)MaxPct) / 100;

    if (maxT <= minT) {
        maxT = minT + 1;
    }
    if (v <= minT) {
        v = 0;
    } else if (v >= maxT) {
        v = 255;
    } else {
        v = ((v - minT) * 255) / (maxT - minT);
    }
    return (ULONG)(v * 4 + (v >> 6));
}

static USHORT
ExtractPhysicalTurboMask(const UCHAR *Stadia)
{
    USHORT mask = 0;
    UCHAR b2 = Stadia[2];
    UCHAR b3 = Stadia[3];

    if (b3 & STADIA_B3_A) mask |= STADIA_TURBO_BTN_A;
    if (b3 & STADIA_B3_B) mask |= STADIA_TURBO_BTN_B;
    if (b3 & STADIA_B3_X) mask |= STADIA_TURBO_BTN_X;
    if (b3 & STADIA_B3_Y) mask |= STADIA_TURBO_BTN_Y;
    if (b3 & STADIA_B3_L1) mask |= STADIA_TURBO_BTN_L1;
    if (b3 & STADIA_B3_R1) mask |= STADIA_TURBO_BTN_R1;
    if (Stadia[8] >= 80) mask |= STADIA_TURBO_BTN_L2;
    if (Stadia[9] >= 80) mask |= STADIA_TURBO_BTN_R2;
    if (b3 & STADIA_B3_LS) mask |= STADIA_TURBO_BTN_LS;
    if (b2 & STADIA_B2_RS) mask |= STADIA_TURBO_BTN_RS;
    return mask;
}

static ULONG
TurboHalfPeriodMs(UCHAR Hz)
{
    ULONG hz = (Hz < 5) ? 5UL : (Hz > 25 ? 25UL : (ULONG)Hz);
    ULONG halfMs = 500UL / hz;
    return halfMs < 20UL ? 20UL : halfMs;
}

// The buttons carry the same letters in the same places. Options and Menu
// become View and Menu, Stadia the Xbox button. Capture and Assistant have no
// counterpart.
static VOID
MapControls(const UCHAR *Stadia, UCHAR *Controls, UCHAR StickDzPct, UCHAR TrigDzPct, UCHAR TrigMaxPct, UCHAR Flags, USHORT SuppressMask, BOOLEAN SuppressHat)
{
    UCHAR b2 = Stadia[2];
    UCHAR b3 = Stadia[3];
    BOOLEAN swapSticks = (Flags & STADIA_FLAG_SWAP_STICKS) != 0;
    BOOLEAN invertLy = (Flags & STADIA_FLAG_INVERT_LY) != 0;
    BOOLEAN invertRy = (Flags & STADIA_FLAG_INVERT_RY) != 0;
    int lx = MapStickAxis(Stadia[swapSticks ? 6 : 4], StickDzPct, FALSE);
    int ly = MapStickAxis(Stadia[swapSticks ? 7 : 5], StickDzPct, invertLy);
    int rx = MapStickAxis(Stadia[swapSticks ? 4 : 6], StickDzPct, FALSE);
    int ry = MapStickAxis(Stadia[swapSticks ? 5 : 7], StickDzPct, invertRy);

    if (SuppressMask & STADIA_TURBO_BTN_A) b3 = (UCHAR)(b3 & ~STADIA_B3_A);
    if (SuppressMask & STADIA_TURBO_BTN_B) b3 = (UCHAR)(b3 & ~STADIA_B3_B);
    if (SuppressMask & STADIA_TURBO_BTN_X) b3 = (UCHAR)(b3 & ~STADIA_B3_X);
    if (SuppressMask & STADIA_TURBO_BTN_Y) b3 = (UCHAR)(b3 & ~STADIA_B3_Y);
    if (SuppressMask & STADIA_TURBO_BTN_L1) b3 = (UCHAR)(b3 & ~STADIA_B3_L1);
    if (SuppressMask & STADIA_TURBO_BTN_R1) b3 = (UCHAR)(b3 & ~STADIA_B3_R1);
    if (SuppressMask & STADIA_TURBO_BTN_LS) b3 = (UCHAR)(b3 & ~STADIA_B3_LS);
    if (SuppressMask & STADIA_TURBO_BTN_RS) b2 = (UCHAR)(b2 & ~STADIA_B2_RS);

    UCHAR rawL2 = (SuppressMask & STADIA_TURBO_BTN_L2) ? 0 : Stadia[8];
    UCHAR rawR2 = (SuppressMask & STADIA_TURBO_BTN_R2) ? 0 : Stadia[9];

    PutUshort(Controls + 0, (ULONG)(0x8000 + lx * 258));
    PutUshort(Controls + 2, (ULONG)(0x8000 + ly * 258));
    PutUshort(Controls + 4, (ULONG)(0x8000 + rx * 258));
    PutUshort(Controls + 6, (ULONG)(0x8000 + ry * 258));
    PutUshort(Controls + 8, MapTriggerAxis(rawL2, TrigDzPct, TrigMaxPct));
    PutUshort(Controls + 10, MapTriggerAxis(rawR2, TrigDzPct, TrigMaxPct));
    Controls[12] = (!SuppressHat && Stadia[1] < STADIA_HAT_RELEASED) ? Stadia[1] + 1 : 0;
    Controls[13] = (UCHAR)(BIT(b2, STADIA_B2_MENU, XBOX_B13_MENU) |
                           BIT(b2, STADIA_B2_OPTIONS, XBOX_B13_VIEW) |
                           BIT(b3, STADIA_B3_R1, XBOX_B13_RB) |
                           BIT(b3, STADIA_B3_L1, XBOX_B13_LB) |
                           BIT(b3, STADIA_B3_Y, XBOX_B13_Y) |
                           BIT(b3, STADIA_B3_X, XBOX_B13_X) |
                           BIT(b3, STADIA_B3_B, XBOX_B13_B) |
                           BIT(b3, STADIA_B3_A, XBOX_B13_A));
    Controls[14] = (UCHAR)(BIT(b2, STADIA_B2_RS, XBOX_B14_RS) | BIT(b3, STADIA_B3_LS, XBOX_B14_LS));
    Controls[PAD_GUIDE_INDEX] = (UCHAR)BIT(b2, STADIA_B2_STADIA, 1);
}

VOID
StadiaRecordError(PDEVICE_CONTEXT Context, NTSTATUS Status)
{
    AcquireSRWLockExclusive(&Context->Lock);
    Context->LastError = Status;
    ReleaseSRWLockExclusive(&Context->Lock);
}

// Returns TRUE if the controller needs a wire update for (Strong, Weak).
// Caller holds Context->Lock.
static BOOLEAN
RumbleStateNeedsWrite(PDEVICE_CONTEXT Context, UCHAR Strong, UCHAR Weak)
{
    UCHAR sentStrong = Context->RumbleSentStrong;
    UCHAR sentWeak = Context->RumbleSentWeak;
    UCHAR deltaLimit = Context->IsBluetooth ? Context->Settings.BleMinDelta : Context->Transport->MinRumbleDelta;
    int diffStrong;
    int diffWeak;

    if (Context->RumbleStateUnknown) {
        return TRUE;
    }
    if (Strong == sentStrong && Weak == sentWeak) {
        return FALSE;
    }
    // Never suppress turning either motor on from 0 or off to 0.
    if ((Strong == 0) != (sentStrong == 0) || (Weak == 0) != (sentWeak == 0)) {
        return TRUE;
    }
    if (deltaLimit == 0) {
        return TRUE;
    }
    diffStrong = (int)Strong - (int)sentStrong;
    if (diffStrong < 0) {
        diffStrong = -diffStrong;
    }
    diffWeak = (int)Weak - (int)sentWeak;
    if (diffWeak < 0) {
        diffWeak = -diffWeak;
    }
    return diffStrong >= (int)deltaLimit || diffWeak >= (int)deltaLimit;
}

static BOOLEAN
TriggerMacroActionLocked(PDEVICE_CONTEXT Context, UCHAR ActionId, ULONG *PulseMs)
{
    UCHAR kMod = 0;
    UCHAR kKey = 0;
    UCHAR cBits = 0;

    switch (ActionId) {
    case STADIA_MACRO_PLAY_PAUSE:
        cBits = STADIA_CONSUMER_PLAY_PAUSE;
        break;
    case STADIA_MACRO_WIN_SCREENSHOT:
        kMod = 0x08; // Left Win
        kKey = 0x46; // PrintScreen
        break;
    case STADIA_MACRO_STEAM_F12:
        kKey = 0x45; // F12
        break;
    case STADIA_MACRO_GAMEBAR_OVERLAY:
        kMod = 0x08; // Left Win
        kKey = 0x0A; // G
        break;
    case STADIA_MACRO_GAMEBAR_CLIP:
        kMod = 0x0C; // Left Win + Left Alt
        kKey = 0x0A; // G
        break;
    case STADIA_MACRO_NEXT_TRACK:
    case STADIA_MACRO_PREV_TRACK_DPAD:
        cBits = (ActionId == STADIA_MACRO_PREV_TRACK_DPAD) ? STADIA_CONSUMER_PREV_TRACK : STADIA_CONSUMER_NEXT_TRACK;
        break;
    case STADIA_MACRO_VOLUME_MUTE:
        cBits = STADIA_CONSUMER_MUTE;
        break;
    case STADIA_MACRO_CUSTOM_HOTKEY:
        kMod = Context->MacroSettings.CustomModMask;
        kKey = Context->MacroSettings.CustomHidUsage;
        if (kMod == 0 && kKey == 0) {
            return FALSE;
        }
        break;
    case STADIA_MACRO_VOL_UP_DPAD:
        cBits = STADIA_CONSUMER_VOL_UP;
        break;
    case STADIA_MACRO_VOL_DOWN_DPAD:
        cBits = STADIA_CONSUMER_VOL_DOWN;
        break;
    default:
        return FALSE;
    }

    if (kMod != 0 || kKey != 0) {
        if (Context->KeyboardMod != 0 || Context->KeyboardKey != 0) {
            Context->KeyboardReleaseFirst = TRUE;
        }
        Context->KeyboardMod = kMod;
        Context->KeyboardKey = kKey;
        Context->KeyboardChanged = TRUE;
    }
    if (cBits != 0) {
        if (Context->ConsumerBits != 0) {
            Context->ConsumerReleaseFirst = TRUE;
        }
        Context->ConsumerBits = cBits;
        Context->ConsumerChanged = TRUE;
    }
    Context->MacroTriggerSeq++;
    Context->LastMacroId = ActionId;
    *PulseMs = 30;
    return TRUE;
}

VOID
StadiaMacroTimerFunc(WDFTIMER Timer)
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfTimerGetParentObject(Timer));
    ULONG rearmMs = 0;

    AcquireSRWLockExclusive(&context->Lock);
    if (context->KeyboardMod != 0 || context->KeyboardKey != 0 || context->KeyboardReleaseFirst) {
        context->KeyboardMod = 0;
        context->KeyboardKey = 0;
        context->KeyboardReleaseFirst = FALSE;
        context->KeyboardChanged = TRUE;
    }
    if (context->ConsumerBits != 0 || context->ConsumerReleaseFirst) {
        context->ConsumerBits = 0;
        context->ConsumerReleaseFirst = FALSE;
        context->ConsumerChanged = TRUE;
        if (context->VolHoldDir == 1 || context->VolHoldDir == 2) {
            context->VolGapWait = TRUE;
            rearmMs = 135;
        }
    } else if (context->VolGapWait && (context->VolHoldDir == 1 || context->VolHoldDir == 2)) {
        UCHAR act = (context->VolHoldDir == 1) ? STADIA_MACRO_VOL_UP_DPAD : STADIA_MACRO_VOL_DOWN_DPAD;
        context->VolGapWait = FALSE;
        TriggerMacroActionLocked(context, act, &rearmMs);
    } else {
        context->VolGapWait = FALSE;
    }
    ReleaseSRWLockExclusive(&context->Lock);

    PadFlushPendingReads(context);
    if (rearmMs > 0) {
        WdfTimerStart(Timer, WDF_REL_TIMEOUT_IN_MS(rearmMs));
    }
}

VOID
StadiaTurboTimerFunc(WDFTIMER Timer)
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfTimerGetParentObject(Timer));
    UCHAR controls[PAD_CONTROLS_LEN];
    BOOLEAN publish = FALSE;
    BOOLEAN rearm = FALSE;
    ULONG timerMs = 42;

    AcquireSRWLockExclusive(&context->Lock);
    BOOLEAN valid = context->RawReportLength >= STADIA_INPUT_REPORT_LEN &&
                    context->RawReport[0] == STADIA_INPUT_REPORT_ID;
    BOOLEAN turboEnabled = (context->Settings.TurboFlags & STADIA_TURBO_FLAG_ENABLED) != 0;
    BOOLEAN holdMode = (context->Settings.TurboFlags & STADIA_TURBO_FLAG_HOLD_MODE) != 0;
    BOOLEAN macrosEnabled = (context->MacroSettings.Flags & STADIA_MACRO_FLAG_ENABLED) != 0;
    BOOLEAN dpadMedia = macrosEnabled && (context->MacroSettings.Flags & STADIA_MACRO_FLAG_DPAD_MEDIA) != 0;
    if (valid && turboEnabled) {
        BOOLEAN assist = (context->RawReport[2] & STADIA_B2_ASSISTANT) != 0;
        UCHAR hat = (UCHAR)(context->RawReport[1] & 0x0F);
        BOOLEAN suppressHat = assist && dpadMedia && (hat < STADIA_HAT_RELEASED);
        USHORT physMask = ExtractPhysicalTurboMask(context->RawReport);
        USHORT activeMask = holdMode ? (assist ? physMask : 0) : context->Settings.TurboMask;
        USHORT heldTurbo = (USHORT)(physMask & activeMask);

        context->TurboHeldMask = heldTurbo;
        if (heldTurbo != 0) {
            context->TurboPhaseOff = !context->TurboPhaseOff;
            USHORT suppressMask = (USHORT)((context->TurboPhaseOff ? heldTurbo : 0) |
                                           ((!holdMode && assist) ? physMask : 0));
            MapControls(context->RawReport, controls,
                        context->Settings.StickDeadzonePct,
                        context->Settings.TriggerDeadzonePct,
                        context->Settings.TriggerMaxPct,
                        context->Settings.Flags,
                        suppressMask,
                        suppressHat);
            publish = TRUE;
            rearm = TRUE;
            timerMs = TurboHalfPeriodMs(context->Settings.TurboHz);
        } else {
            context->TurboPhaseOff = FALSE;
        }
    } else {
        context->TurboHeldMask = 0;
        context->TurboPhaseOff = FALSE;
    }
    ReleaseSRWLockExclusive(&context->Lock);

    if (publish) {
        PadPublishControls(context, controls);
    }
    if (rearm) {
        WdfTimerStart(Timer, WDF_REL_TIMEOUT_IN_MS(timerMs));
    }
}

VOID
StadiaInputReport(PDEVICE_CONTEXT Context, const UCHAR *Report, size_t Length)
{
    UCHAR controls[PAD_CONTROLS_LEN];
    BOOLEAN valid = Length >= STADIA_INPUT_REPORT_LEN && Report[0] == STADIA_INPUT_REPORT_ID;
    BOOLEAN wakeStop = FALSE;
    BOOLEAN startTimer = FALSE;
    BOOLEAN stopTimer = FALSE;
    BOOLEAN startMacroTimer = FALSE;
    ULONG timerMs = 42;
    ULONG macroMs = 45;

    AcquireSRWLockExclusive(&Context->Lock);
    Context->RawReportLength = (UCHAR)min(Length, RAW_REPORT_MAX);
    RtlCopyMemory(Context->RawReport, Report, Context->RawReportLength);
    if (valid) {
        BOOLEAN assist = (Report[2] & STADIA_B2_ASSISTANT) != 0;
        BOOLEAN captureDown = (Report[2] & STADIA_B2_CAPTURE) != 0;
        UCHAR hat = (UCHAR)(Report[1] & 0x0F);
        USHORT physMask = ExtractPhysicalTurboMask(Report);
        BOOLEAN turboEnabled = (Context->Settings.TurboFlags & STADIA_TURBO_FLAG_ENABLED) != 0;
        BOOLEAN holdMode = (Context->Settings.TurboFlags & STADIA_TURBO_FLAG_HOLD_MODE) != 0;
        USHORT rising = (USHORT)(physMask & ~Context->TurboPrevPhysicalMask);
        BOOLEAN macrosEnabled = (Context->MacroSettings.Flags & STADIA_MACRO_FLAG_ENABLED) != 0;
        BOOLEAN dpadMedia = macrosEnabled && (Context->MacroSettings.Flags & STADIA_MACRO_FLAG_DPAD_MEDIA) != 0;
        BOOLEAN prevAssist = Context->TurboPrevAssist;
        BOOLEAN suppressHat = FALSE;

        Context->LastError = STATUS_SUCCESS;

        if (turboEnabled && assist && !holdMode && rising != 0) {
            Context->Settings.TurboMask = (USHORT)((Context->Settings.TurboMask ^ rising) & STADIA_TURBO_BTN_ALL);
            Context->TurboToggleSeq++;
            Context->AssistComboUsed = TRUE;
        }
        if (turboEnabled && holdMode && assist && physMask != 0) {
            Context->AssistComboUsed = TRUE;
        }

        if (macrosEnabled) {
            if (captureDown && !Context->PrevCaptureDown &&
                Context->MacroSettings.CaptureAction != STADIA_MACRO_NONE) {
                Context->VolHoldDir = 0;
                Context->VolGapWait = FALSE;
                if (TriggerMacroActionLocked(Context, Context->MacroSettings.CaptureAction, &macroMs)) {
                    startMacroTimer = TRUE;
                }
            }

            if (assist) {
                if (!prevAssist) {
                    if (!(turboEnabled && !holdMode && rising != 0) &&
                        !(turboEnabled && holdMode && physMask != 0)) {
                        Context->AssistComboUsed = FALSE;
                    }
                    Context->PrevDpadComboHat = STADIA_HAT_RELEASED;
                }
                if (dpadMedia && hat < STADIA_HAT_RELEASED) {
                    suppressHat = TRUE;
                    Context->AssistComboUsed = TRUE;
                    if (hat == 0 || hat == 4 || hat == 6 || hat == 2) {
                        if (hat != Context->PrevDpadComboHat) {
                            UCHAR dpadAct = STADIA_MACRO_NONE;
                            if (hat == 0) {
                                dpadAct = STADIA_MACRO_VOL_UP_DPAD;
                                Context->VolHoldDir = 1;
                            } else if (hat == 4) {
                                dpadAct = STADIA_MACRO_VOL_DOWN_DPAD;
                                Context->VolHoldDir = 2;
                            } else if (hat == 6) {
                                dpadAct = STADIA_MACRO_PREV_TRACK_DPAD;
                                Context->VolHoldDir = 0;
                            } else if (hat == 2) {
                                dpadAct = STADIA_MACRO_NEXT_TRACK;
                                Context->VolHoldDir = 0;
                            }
                            Context->VolGapWait = FALSE;
                            if (TriggerMacroActionLocked(Context, dpadAct, &macroMs)) {
                                startMacroTimer = TRUE;
                            }
                        }
                    } else {
                        Context->VolHoldDir = 0;
                        Context->VolGapWait = FALSE;
                    }
                } else {
                    Context->VolHoldDir = 0;
                    Context->VolGapWait = FALSE;
                }
                Context->PrevDpadComboHat = hat;
            } else {
                Context->VolHoldDir = 0;
                Context->VolGapWait = FALSE;
                if (prevAssist && !Context->AssistComboUsed &&
                    Context->MacroSettings.AssistantAction != STADIA_MACRO_NONE) {
                    if (TriggerMacroActionLocked(Context, Context->MacroSettings.AssistantAction, &macroMs)) {
                        startMacroTimer = TRUE;
                    }
                }
                Context->AssistComboUsed = FALSE;
                Context->PrevDpadComboHat = STADIA_HAT_RELEASED;
            }
        } else {
            Context->VolHoldDir = 0;
            Context->VolGapWait = FALSE;
            Context->AssistComboUsed = FALSE;
            Context->PrevDpadComboHat = STADIA_HAT_RELEASED;
        }

        Context->PrevCaptureDown = captureDown;
        Context->TurboPrevPhysicalMask = physMask;
        Context->TurboPrevAssist = assist;

        USHORT activeMask = turboEnabled
                                ? (holdMode ? (assist ? physMask : 0) : Context->Settings.TurboMask)
                                : 0;
        USHORT heldTurbo = (USHORT)(physMask & activeMask);
        BOOLEAN wasHolding = (Context->TurboHeldMask != 0);
        Context->TurboHeldMask = heldTurbo;

        if (heldTurbo == 0) {
            Context->TurboPhaseOff = FALSE;
            stopTimer = wasHolding;
        } else if (!wasHolding) {
            Context->TurboPhaseOff = FALSE;
            startTimer = TRUE;
            timerMs = TurboHalfPeriodMs(Context->Settings.TurboHz);
        }

        USHORT suppressMask = (USHORT)((Context->TurboPhaseOff ? heldTurbo : 0) |
                                       ((turboEnabled && !holdMode && assist) ? physMask : 0));
        MapControls(Report, controls,
                    Context->Settings.StickDeadzonePct,
                    Context->Settings.TriggerDeadzonePct,
                    Context->Settings.TriggerMaxPct,
                    Context->Settings.Flags,
                    suppressMask,
                    suppressHat);

        if (Context->RumbleStopParked && Context->RumbleStateUnknown &&
            Context->RumbleStrong == 0 && Context->RumbleWeak == 0 &&
            Context->RumbleStopParkWakeCount < RUMBLE_STOP_PARK_WAKE_MAX &&
            GetTickCount64() - Context->RumbleStopParkedTick >= RUMBLE_STOP_PARK_WAKE_COOLDOWN_MS) {
            Context->RumbleStopParked = FALSE;
            Context->RumbleStopParkWakeCount++;
            wakeStop = TRUE;
        }
    }
    ReleaseSRWLockExclusive(&Context->Lock);

    if (wakeStop && Context->RumbleWakeEvent != NULL) {
        SetEvent(Context->RumbleWakeEvent);
    }

    if (Context->TurboTimer != NULL) {
        if (stopTimer) {
            WdfTimerStop(Context->TurboTimer, FALSE);
        } else if (startTimer) {
            WdfTimerStart(Context->TurboTimer, WDF_REL_TIMEOUT_IN_MS(timerMs));
        }
    }

    if (startMacroTimer && Context->MacroTimer != NULL) {
        WdfTimerStart(Context->MacroTimer, WDF_REL_TIMEOUT_IN_MS(macroMs));
    }

    if (valid) {
        PadPublishControls(Context, controls);
    }
}

VOID
StadiaSetRumble(PDEVICE_CONTEXT Context, UCHAR Strong, UCHAR Weak)
{
    BOOLEAN wake;

    // Games repeat the same output report a lot; only meaningful changes reach the wire.
    AcquireSRWLockExclusive(&Context->Lock);
    Context->RumbleStrong = Strong;
    Context->RumbleWeak = Weak;
    if (Strong != 0 || Weak != 0) {
        Context->RumbleStopParkWakeCount = 0;
    }
    wake = RumbleStateNeedsWrite(Context, Strong, Weak);
    ReleaseSRWLockExclusive(&Context->Lock);

    if (wake && Context->RumbleWakeEvent != NULL) {
        SetEvent(Context->RumbleWakeEvent);
    }
}

// Dedicated rumble worker thread: coalesces frame-by-frame updates to match the
// transport's natural cadence without blocking the shared WDF workitem pool,
// fires stop (0,0) and initial spin-up immediately, and retries failed stops
// with bounded exponential backoff so vibration never sticks.
DWORD WINAPI
StadiaRumbleThread(LPVOID Parameter)
{
    PDEVICE_CONTEXT context = (PDEVICE_CONTEXT)Parameter;
    HANDLE handles[2] = {context->RumbleStopEvent, context->RumbleWakeEvent};
    HRESULT threadInitHr = PrivateGattThreadInit();
    ULONGLONG lastWriteStartTick = 0;
    ULONGLONG lastWriteEndTick = 0;
    ULONGLONG nextRetryTick = 0;
    ULONG retryDelayMs = 0;
    ULONG retryCount = 0;
    BOOLEAN lastFailedWasStop = FALSE;
    BOOLEAN giveUpNonZero = FALSE;
    UCHAR lastAttemptedStrong = 0;
    UCHAR lastAttemptedWeak = 0;

    for (;;) {
        UCHAR strong;
        UCHAR weak;
        BOOLEAN shuttingDown;
        BOOLEAN changed;
        BOOLEAN isStop;
        DWORD waitMs;

        ULONG minIntervalMs;
        ULONG postAckGapMs;

        if (WaitForSingleObject(context->RumbleStopEvent, 0) == WAIT_OBJECT_0) {
            break;
        }

        AcquireSRWLockShared(&context->Lock);
        strong = context->RumbleStrong;
        weak = context->RumbleWeak;
        shuttingDown = context->RumbleShuttingDown;
        changed = RumbleStateNeedsWrite(context, strong, weak);
        minIntervalMs = context->IsBluetooth ? context->Settings.BleIntervalMs : context->Transport->MinRumbleIntervalMs;
        postAckGapMs = context->IsBluetooth ? context->Settings.BlePostAckGapMs : context->Transport->PostAckGapMs;
        ReleaseSRWLockShared(&context->Lock);

        if (!changed) {
            retryDelayMs = 0;
            retryCount = 0;
            lastFailedWasStop = FALSE;
            giveUpNonZero = FALSE;
            if (shuttingDown && strong == 0 && weak == 0) {
                SetEvent(context->RumbleStopDoneEvent);
                break;
            }
            if (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0) {
                break;
            }
            continue;
        }

        isStop = (strong == 0 && weak == 0);

        // If a new non-zero value was requested after giving up on an earlier
        // non-zero failure, allow fresh retries for the new value.
        if (!isStop && giveUpNonZero &&
            (strong != lastAttemptedStrong || weak != lastAttemptedWeak)) {
            giveUpNonZero = FALSE;
            retryDelayMs = 0;
            retryCount = 0;
        }

        if (!isStop && giveUpNonZero) {
            if (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0) {
                break;
            }
            continue;
        }

        // A pending stop immediately cancels any non-zero retry backoff.
        if (isStop && !lastFailedWasStop) {
            retryDelayMs = 0;
            retryCount = 0;
        }

        waitMs = 0;
        if (retryDelayMs > 0) {
            ULONGLONG now = GetTickCount64();
            if (now < nextRetryTick) {
                waitMs = (DWORD)(nextRetryTick - now);
            }
        } else if (!isStop && lastWriteStartTick != 0) {
            ULONGLONG now = GetTickCount64();
            ULONGLONG minByStart = lastWriteStartTick + minIntervalMs;
            ULONGLONG minByEnd = lastWriteEndTick + postAckGapMs;
            ULONGLONG minNextTick = max(minByStart, minByEnd);
            if (now < minNextTick) {
                waitMs = (DWORD)(minNextTick - now);
            }
        }

        if (waitMs > 0) {
            if (WaitForMultipleObjects(2, handles, FALSE, waitMs) == WAIT_OBJECT_0) {
                break;
            }
            continue;
        }

        // Motor speeds are 16 bit little endian; x * 257 scales 8 to 16 bits.
        UCHAR report[] = {STADIA_RUMBLE_REPORT_ID, strong, strong, weak, weak};
        LARGE_INTEGER qpcFreq, qpcStart, qpcEnd;
        QueryPerformanceFrequency(&qpcFreq);
        QueryPerformanceCounter(&qpcStart);
        ULONGLONG writeStart = GetTickCount64();
        NTSTATUS status;

        lastAttemptedStrong = strong;
        lastAttemptedWeak = weak;

        AcquireSRWLockExclusive(&context->RumbleLock);
        status = context->Transport->SendOutputReport(context, report, sizeof(report));
        ReleaseSRWLockExclusive(&context->RumbleLock);

        QueryPerformanceCounter(&qpcEnd);
        ULONGLONG writeEnd = GetTickCount64();
        lastWriteStartTick = writeStart;
        lastWriteEndTick = writeEnd;
        ULONGLONG elapsedMs = (qpcFreq.QuadPart > 0)
                                  ? (ULONGLONG)(((qpcEnd.QuadPart - qpcStart.QuadPart) * 1000LL) / qpcFreq.QuadPart)
                                  : (writeEnd - writeStart);
        USHORT writeRttMs = (USHORT)min(elapsedMs, (ULONGLONG)65535);

        AcquireSRWLockExclusive(&context->Lock);
        context->LastRumbleLatencyMs = writeRttMs;
        context->RumbleFailed = !NT_SUCCESS(status);
        if (NT_SUCCESS(status)) {
            context->RumbleSentStrong = strong;
            context->RumbleSentWeak = weak;
            context->RumbleStateUnknown = FALSE;
            context->RumbleStopParked = FALSE;
            context->RumbleStopParkWakeCount = 0;
            context->RumbleError = STATUS_SUCCESS;
            context->RumbleWrites++;
        } else {
            context->RumbleStateUnknown = TRUE;
            context->RumbleError = status;
            context->RumbleFailures++;
        }
        shuttingDown = context->RumbleShuttingDown;
        ReleaseSRWLockExclusive(&context->Lock);

        if (NT_SUCCESS(status)) {
            retryDelayMs = 0;
            retryCount = 0;
            lastFailedWasStop = FALSE;
            giveUpNonZero = FALSE;
            if (isStop && shuttingDown) {
                SetEvent(context->RumbleStopDoneEvent);
            }
        } else {
            if (status == STATUS_CANCELLED || status == (NTSTATUS)HRESULT_FROM_WIN32(ERROR_CANCELLED)) {
                break;
            }
            if (shuttingDown) {
                SetEvent(context->RumbleStopDoneEvent);
                break;
            }
            lastFailedWasStop = isStop;
            retryCount++;
            if (isStop) {
                if (retryCount >= RUMBLE_STOP_RETRY_MAX_ATTEMPTS) {
                    // Controller appears out of range or GATT unavailable; park with
                    // cooldown/cap on input-report wakes so we never loop endlessly.
                    AcquireSRWLockExclusive(&context->Lock);
                    context->RumbleStopParked = TRUE;
                    context->RumbleStopParkedTick = GetTickCount64();
                    ReleaseSRWLockExclusive(&context->Lock);
                    if (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0) {
                        break;
                    }
                    AcquireSRWLockExclusive(&context->Lock);
                    context->RumbleStopParked = FALSE;
                    ReleaseSRWLockExclusive(&context->Lock);
                    retryDelayMs = 0;
                    retryCount = 0;
                } else {
                    retryDelayMs = (retryDelayMs == 0)
                                       ? RUMBLE_STOP_RETRY_INITIAL_MS
                                       : min(retryDelayMs * 2, RUMBLE_STOP_RETRY_MAX_MS);
                    nextRetryTick = writeEnd + retryDelayMs;
                }
            } else {
                if (retryCount >= RUMBLE_NONZERO_RETRY_MAX_ATTEMPTS) {
                    giveUpNonZero = TRUE;
                    retryDelayMs = 0;
                } else {
                    retryDelayMs = (retryDelayMs == 0)
                                       ? RUMBLE_NONZERO_RETRY_INITIAL_MS
                                       : min(retryDelayMs * 2, RUMBLE_NONZERO_RETRY_MAX_MS);
                    nextRetryTick = writeEnd + retryDelayMs;
                }
            }
        }
    }

    if (SUCCEEDED(threadInitHr)) {
        PrivateGattThreadUninit();
    }
    return 0;
}

NTSTATUS
StadiaPrepareHardware(WDFDEVICE Device, WDFCMRESLIST ResourcesRaw, WDFCMRESLIST ResourcesTranslated)
{
    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);

    return GetDeviceContext(Device)->Transport->PrepareHardware(Device);
}

NTSTATUS
StadiaD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    PDEVICE_CONTEXT context = GetDeviceContext(Device);
    NTSTATUS status;

    UNREFERENCED_PARAMETER(PreviousState);

    // The motors are off after power up, whatever was requested before.
    AcquireSRWLockExclusive(&context->Lock);
    context->RumbleStrong = 0;
    context->RumbleWeak = 0;
    context->RumbleSentStrong = 0;
    context->RumbleSentWeak = 0;
    context->RumbleStateUnknown = TRUE;
    context->RumbleShuttingDown = FALSE;
    context->RumbleStopParked = FALSE;
    context->RumbleStopParkWakeCount = 0;
    context->RumbleStopParkedTick = 0;
    ReleaseSRWLockExclusive(&context->Lock);

    status = context->Transport->Start(context);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (context->RumbleThread == NULL) {
        ResetEvent(context->RumbleStopEvent);
        ResetEvent(context->RumbleStopDoneEvent);
        context->RumbleThread = CreateThread(NULL, 0, StadiaRumbleThread, context, 0, NULL);
        if (context->RumbleThread == NULL) {
            StadiaRecordError(context, (NTSTATUS)HRESULT_FROM_WIN32(GetLastError()));
        }
    }
    // Warm up GATT / ensure motors are off asynchronously in the background.
    StadiaSetRumble(context, 0, 0);
    return STATUS_SUCCESS;
}

NTSTATUS
StadiaD0Exit(WDFDEVICE Device, WDF_POWER_DEVICE_STATE TargetState)
{
    PDEVICE_CONTEXT context = GetDeviceContext(Device);
    BOOLEAN needStopWrite;

    UNREFERENCED_PARAMETER(TargetState);

    BOOLEAN flushReleasedKeys = FALSE;

    if (context->TurboTimer != NULL) {
        WdfTimerStop(context->TurboTimer, TRUE);
    }
    if (context->MacroTimer != NULL) {
        WdfTimerStop(context->MacroTimer, TRUE);
    }

    AcquireSRWLockExclusive(&context->Lock);
    context->TurboHeldMask = 0;
    context->TurboPhaseOff = FALSE;
    context->TurboPrevPhysicalMask = 0;
    context->TurboPrevAssist = FALSE;
    context->PrevCaptureDown = FALSE;
    context->AssistComboUsed = FALSE;
    context->PrevDpadComboHat = STADIA_HAT_RELEASED;
    context->VolHoldDir = 0;
    context->VolGapWait = FALSE;
    context->KeyboardReleaseFirst = FALSE;
    context->ConsumerReleaseFirst = FALSE;
    if (context->KeyboardMod != 0 || context->KeyboardKey != 0) {
        context->KeyboardMod = 0;
        context->KeyboardKey = 0;
        context->KeyboardChanged = TRUE;
        flushReleasedKeys = TRUE;
    } else {
        context->KeyboardChanged = FALSE;
    }
    if (context->ConsumerBits != 0) {
        context->ConsumerBits = 0;
        context->ConsumerChanged = TRUE;
        flushReleasedKeys = TRUE;
    } else {
        context->ConsumerChanged = FALSE;
    }
    needStopWrite = context->RumbleStateUnknown || context->RumbleSentStrong != 0 ||
                    context->RumbleSentWeak != 0;
    context->RumbleStrong = 0;
    context->RumbleWeak = 0;
    context->RumbleShuttingDown = TRUE;
    if (context->RumbleStopDoneEvent != NULL) {
        ResetEvent(context->RumbleStopDoneEvent);
    }
    ReleaseSRWLockExclusive(&context->Lock);

    if (flushReleasedKeys) {
        PadFlushPendingReads(context);
    }

    if (context->RumbleThread != NULL) {
        if (needStopWrite && context->RumbleWakeEvent != NULL && context->RumbleStopDoneEvent != NULL) {
            SetEvent(context->RumbleWakeEvent);
            WaitForSingleObject(context->RumbleStopDoneEvent, RUMBLE_SHUTDOWN_STOP_WAIT_MS);
        }
        if (context->RumbleStopEvent != NULL) {
            SetEvent(context->RumbleStopEvent);
        }
        WaitForSingleObject(context->RumbleThread, RUMBLE_THREAD_JOIN_TIMEOUT_MS);
        CloseHandle(context->RumbleThread);
        context->RumbleThread = NULL;
    }

    context->Transport->Stop(context);
    return STATUS_SUCCESS;
}

VOID
StadiaDeviceCleanup(WDFOBJECT DeviceObject)
{
    PDEVICE_CONTEXT context = GetDeviceContext(DeviceObject);

    if (context->RumbleThread != NULL) {
        if (context->RumbleStopEvent != NULL) {
            SetEvent(context->RumbleStopEvent);
        }
        WaitForSingleObject(context->RumbleThread, RUMBLE_THREAD_JOIN_TIMEOUT_MS);
        CloseHandle(context->RumbleThread);
        context->RumbleThread = NULL;
    }
    if (context->RumbleWakeEvent != NULL) {
        CloseHandle(context->RumbleWakeEvent);
        context->RumbleWakeEvent = NULL;
    }
    if (context->RumbleStopEvent != NULL) {
        CloseHandle(context->RumbleStopEvent);
        context->RumbleStopEvent = NULL;
    }
    if (context->RumbleStopDoneEvent != NULL) {
        CloseHandle(context->RumbleStopDoneEvent);
        context->RumbleStopDoneEvent = NULL;
    }
}
