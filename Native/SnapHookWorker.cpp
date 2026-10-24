// SnapHookWorker — release-only snap tap engine.
//
// Design notes
// ------------
// The classic "null movement" implementation swallows the physical key and
// re-injects a synthetic one. That only works in games that accept injected
// input: if a game ignores SendInput, the player loses movement entirely,
// because the real keystroke never reached it.
//
// This worker never swallows anything. Every physical keydown and keyup is
// passed through to the system untouched. The engine only adds two synthetic
// events, and only around the moment an opposing key is held:
//
//   press D while A is held    -> inject keyup(A)     (release the old direction)
//   release D while A is held  -> inject keydown(A)   (restore the old direction)
//
// Both synthetic events are purely additive. If a game drops them, the worst
// case is plain keyboard behaviour: the character still moves. Nothing the
// engine does can block movement.
//
// The engine therefore works over raw input, DirectInput, GetAsyncKeyState
// polling and WM_KEYDOWN alike, because it relies on the native delivery path
// for everything that matters.
//
// Usage:
//   SnapHookWorker.exe --profile "TslGame|30:32,17:31" --profile "cs2|30:32"
//                     [--any "30:32"] [--debug]
//
// A profile pairs a process image name with its scan code pairs. The profile
// whose process owns the foreground window is the only one armed at a time.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <fcntl.h>
#include <io.h>
#include <functional>
#include <cwchar>
#include <cwctype>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace
{

// Opens the log so that other processes can read it while it is being written.
//
// _wfopen_s takes the file exclusively, so a running worker locks out every reader:
// the log looked empty for the whole session and only became readable once the
// worker had exited. Sharing the handle means the file can be watched live, which
// is the entire point of having it.
bool OpenLogFile(const std::wstring& path, FILE** out)
{
    HANDLE handle = CreateFileW(
        path.c_str(),
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (handle == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    const int descriptor = _open_osfhandle(reinterpret_cast<intptr_t>(handle), _O_APPEND);
    if (descriptor == -1)
    {
        CloseHandle(handle);
        return false;
    }

    *out = _fdopen(descriptor, "a");
    if (*out == nullptr)
    {
        _close(descriptor);
        return false;
    }

    // A byte order mark, so the file is recognised as UTF-8 by any editor.
    static bool wroteBom = false;
    if (!wroteBom)
    {
        wroteBom = true;
        const unsigned char bom[] = {0xEF, 0xBB, 0xBF};

        // Written through the same handle so it lands at the very start.
        DWORD written = 0;
        WriteFile(handle, bom, sizeof(bom), &written, nullptr);
    }

    return true;
}

// A key is identified by its scan code plus an extension bit.
//
// Some keys share a scan code and differ only by the E0 prefix: right Ctrl and
// left Ctrl are both 0x1D, right Alt and left Alt are both 0x38, and the numeric
// keypad's 0 and 9 are 0x52 and 0x49, which are also Insert and Page Up. Without
// the marker those pairs would be the same key, so binding right Ctrl would
// silently bind left Ctrl.
//
// Real scan codes never exceed 0xFF, so bit 0x100 is free to carry the marker
// and the whole identity still fits in a WORD.
constexpr WORD kExtendedBit = 0x100;

// Signature written into dwExtraInfo on every event this worker sends.
//
// "SNAPHOOK" in ASCII, at an offset that keeps it clear of anything the system uses.
constexpr ULONG_PTR kOwnTag = 0x4B4F4F4850414E53ULL;

// How events reach the game. Chosen per profile, because games differ in what
// they actually read.
//
//   Release  — the physical key is never swallowed; the engine only adds a
//              release when opposing directions overlap. Suits games that read
//              the input *stream*: raw input, DirectInput, window messages.
//              Nothing can break movement, because the native event still
//              arrives.
//
//   Blocking — the physical press is swallowed and replaced by an injected one,
//              as in the original null-movement implementation. It is the
//              fallback for a game that does not react to the release-only model,
//              at the cost of needing the game to accept injected input: if it
//              does not, movement stops working entirely.
enum class EngineMode
{
    Release,
    Hybrid,
    Blocking
};



struct KeyState
{
    bool held = false;       // physical key is currently down
    bool scriptDown = false; // the game currently believes this key is down
    WORD vk = 0;             // virtual key, learned from real events (layout-proof)
    WORD scan = 0;
    bool extended = false;
};

// One game: which process it is, which key pairs snap tap applies to, and how
// events reach that particular game.
struct Profile
{
    std::wstring target;
    // Full executable path when the profile was selected from Browse or the
    // running-process list. This is the authoritative identity: names such as
    // GTA5.exe are shared by different launchers and separate clients.
    std::wstring targetPath;
    EngineMode mode = EngineMode::Release;
    std::unordered_set<WORD> managedScanCodes;
    std::unordered_map<WORD, std::vector<WORD>> opponents;
    std::unordered_map<WORD, KeyState> states;
};

HHOOK g_hook = nullptr;
HWND g_rawInputWindow = nullptr;
HINSTANCE g_instance = nullptr;
DWORD g_mainThreadId = 0;

// All input state belongs to this thread. Raw Input and a low-level keyboard
// hook installed by this thread are dispatched through its message queue too,
// so profile changes can be serialized with physical key events instead of
// contending for a lock.
constexpr UINT kRefreshForegroundMessage = WM_APP + 41;
constexpr UINT kReleaseAndQuitMessage = WM_APP + 42;
constexpr UINT kRestoreBlockingMessage = WM_APP + 43;

// Tick of the last event the hook saw, and how many it has seen in total.
//
// The count is what makes the heartbeat meaningful: a hook that is alive but idle
// and a hook that Windows has removed produce identical logs otherwise.
volatile DWORD g_lastHookCall = 0;
// Counting costs an atomic increment, which is nothing next to the work the hook
// already does. They are still only touched while a log is open: the numbers exist to
// be written out, and a session that will never write them should not pay for them.
volatile long g_hookEventCount = 0;
volatile long g_ownEventCount = 0;
volatile long g_foreignEventCount = 0;
volatile long g_unarmedEventCount = 0;
volatile long g_managedEventCount = 0;
volatile long g_unmanagedEventCount = 0;
HWINEVENTHOOK g_foregroundHook = nullptr;

std::vector<Profile> g_profiles;
// The profile list is immutable after startup. Only the worker's message thread
// selects an index and touches the corresponding KeyState objects.
std::atomic<int> g_activeProfile{-1};

// Hybrid profiles deliberately have no WH_KEYBOARD_LL hook while active.
// This flag is message-thread-only, just like the hook itself.
bool g_keyboardHookWanted = false;

// Background watcher threads never alter input state. They only request work
// from the message thread and stop posting once shutdown begins.
std::atomic<bool> g_shutdownRequested{false};
std::atomic<bool> g_foregroundRefreshPosted{false};
bool g_stopping = false;

void UpdateKeyboardHook();

bool g_debug = false;

// Set while the round-trip benchmark is running. The hook records the moment an
// injected event arrives back at this thread, which is the earliest point an
// application can possibly see it.
volatile bool g_benchWatching = false;
volatile bool g_benchArrived = false;
WORD g_benchScan = 0;
LARGE_INTEGER g_benchArrival{};
FILE* g_log = nullptr;

EngineMode g_mode = EngineMode::Release;

// Name of the event the interface signals to ask for a clean shutdown. Passed on
// the command line so several workers never share one handle.
std::wstring g_shutdownEventName;

// ---------------------------------------------------------------- utilities

std::wstring ToUpper(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(std::towupper(c)); });
    return value;
}

bool EqualsIgnoreCase(const std::wstring& left, const std::wstring& right)
{
    return ToUpper(left) == ToUpper(right);
}

// Writes one line to the log, in UTF-8.
//
// The file is a shared handle rather than a stream with an encoding, so the wide
// characters are converted here. That is what lets the interface, an editor or a
// plain tail read the log while the worker is still running, and what makes the
// timestamps legible instead of being written as UTF-16.
void Log(const wchar_t* format, ...)
{
    if (g_log == nullptr)
    {
        return;
    }

    SYSTEMTIME time{};
    GetLocalTime(&time);

    wchar_t line[2048]{};

    int offset = swprintf_s(line,
                            L"[%02u:%02u:%02u.%03u] ",
                            time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);

    if (offset < 0)
    {
        return;
    }

    va_list args;
    va_start(args, format);
    vswprintf_s(line + offset, 2048 - offset, format, args);
    va_end(args);

    char utf8[2048]{};
    const int bytes = WideCharToMultiByte(
        CP_UTF8, 0, line, -1, utf8, sizeof(utf8) - 1, nullptr, nullptr);

    if (bytes > 1)
    {
        // bytes counts the terminating null, which is not written.
        std::fwrite(utf8, 1, static_cast<size_t>(bytes - 1), g_log);
    }

    std::fwrite("\n", 1, 1, g_log);
    std::fflush(g_log);
}

// Some keys travel as an E0-prefixed scancode. Injecting them needs
// KEYEVENTF_EXTENDEDKEY, otherwise they are decoded as a different key.
bool IsExtendedScanCode(WORD scanCode)
{
    switch (scanCode)
    {
    case 0x1C: // numpad enter
    case 0x1D: // right ctrl
    case 0x35: // numpad divide
    case 0x37: // print screen
    case 0x38: // right alt
    case 0x47: // home
    case 0x48: // up
    case 0x49: // page up
    case 0x4B: // left
    case 0x4D: // right
    case 0x4F: // end
    case 0x50: // down
    case 0x51: // page down
    case 0x52: // insert
    case 0x53: // delete
    case 0x5B: // left windows
    case 0x5C: // right windows
    case 0x5D: // applications
        return true;
    default:
        return false;
    }
}

WORD VkFromScanCode(WORD scanCode)
{
    UINT vk = MapVirtualKeyW(scanCode, MAPVK_VSC_TO_VK_EX);
    if (vk == 0)
    {
        vk = MapVirtualKeyW(scanCode, MAPVK_VSC_TO_VK);
    }

    return static_cast<WORD>(vk);
}

// Strips the .exe suffix so a stored process name and an image file name can be
// compared directly.
//
// This matters because the two arrive in different shapes: the interface stores
// the process name as Windows reports it ("cs2", "TslGame"), while a process
// image name always carries its extension ("cs2.exe", "TslGame.exe"). Comparing
// them verbatim never matched, so no profile was ever armed and the engine
// appeared to do nothing at all.
std::wstring WithoutExtension(const std::wstring& fileName)
{
    const size_t dot = fileName.find_last_of(L'.');
    if (dot == std::wstring::npos || dot == 0)
    {
        return fileName;
    }

    return fileName.substr(0, dot);
}

// True when a stored profile target refers to this executable.
bool MatchesTarget(const std::wstring& fileName, const std::wstring& target)
{
    if (EqualsIgnoreCase(fileName, target))
    {
        return true;
    }

    return EqualsIgnoreCase(WithoutExtension(fileName), WithoutExtension(target));
}

// Windows paths are case-insensitive. QueryFullProcessImageNameW normally uses
// backslashes, but accepting forward slashes too makes manually written profiles
// behave exactly like paths that came from the interface.
bool MatchesPath(const std::wstring& imagePath, std::wstring targetPath)
{
    std::replace(targetPath.begin(), targetPath.end(), L'/', L'\\');
    std::wstring normalImagePath = imagePath;
    std::replace(normalImagePath.begin(), normalImagePath.end(), L'/', L'\\');
    return EqualsIgnoreCase(normalImagePath, targetPath);
}

// A path-bearing profile must match that exact executable. Legacy profiles did
// not record a path, so they retain the historical process-name fallback.
bool MatchesProfile(const std::wstring& imageName, const std::wstring& imagePath, const Profile& profile)
{
    return profile.targetPath.empty()
        ? MatchesTarget(imageName, profile.target)
        : MatchesPath(imagePath, profile.targetPath);
}

// ------------------------------------------------------------ key injection

using SendInputFunction = UINT(WINAPI*)(UINT, LPINPUT, int);
SendInputFunction g_sendInput = ::SendInput;

// Sends one synthetic event and reports whether Windows accepted it. This only
// confirms delivery to the Windows input queue; a game may still choose not to
// use injected input. The state machine must nevertheless not claim a change
// that Windows rejected outright.
bool InjectKey(const KeyState& state, bool down)
{
    INPUT input{};

    // Filling both wVk and wScan keeps the event consistent for every reader:
    // raw input consumers use wScan, polling consumers use wVk.
    input.type = INPUT_KEYBOARD;
    input.ki.wVk = state.vk;
    input.ki.wScan = state.scan;
    input.ki.dwFlags = KEYEVENTF_SCANCODE;
    if (state.extended)
    {
        input.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
    }
    if (!down)
    {
        input.ki.dwFlags |= KEYEVENTF_KEYUP;
    }
    // Our signature, so the hook can recognise this event on the way back.
    input.ki.dwExtraInfo = kOwnTag;

    return g_sendInput(1, &input, sizeof(INPUT)) == 1;
}

// Updates the state only after the synthetic transition has entered the Windows
// input queue. Native physical transitions are recorded by their callers and do
// not use this helper.
bool SetInjectedKeyState(KeyState& state, bool down)
{
    if (state.scriptDown == down)
    {
        return true;
    }

    if (!InjectKey(state, down))
    {
        Log(L"inject failed  scan=%u  transition=%s", state.scan, down ? L"down" : L"up");
        return false;
    }

    state.scriptDown = down;
    return true;
}

// -------------------------------------------------------------- state sync

bool IsPhysicallyDown(const KeyState& state)
{
    const WORD vk = state.vk != 0 ? state.vk : VkFromScanCode(state.scan);
    if (vk == 0)
    {
        return false;
    }

    return (GetAsyncKeyState(vk) & 0x8000) != 0;
}

// Bring every managed key back to "what the game sees == what is really down".
void ResyncStates(Profile& profile)
{
    bool changed = false;

    for (auto& entry : profile.states)
    {
        KeyState& state = entry.second;
        state.held = IsPhysicallyDown(state);

        if (state.held && !state.scriptDown)
        {
            if (SetInjectedKeyState(state, true))
            {
                changed = true;
                Log(L"resync   scan=%u -> down", state.scan);
            }
        }
        else if (!state.held && state.scriptDown)
        {
            if (SetInjectedKeyState(state, false))
            {
                changed = true;
                Log(L"resync   scan=%u -> up", state.scan);
            }
        }
    }

    if (changed)
    {
        Log(L"resync complete");
    }
}

// Hand back any key the engine was suppressing, so the game agrees with the
// physical keyboard again.
void RestoreSuppressedKeys(Profile& profile)
{
    for (auto& entry : profile.states)
    {
        KeyState& state = entry.second;
        if (state.held && !state.scriptDown)
        {
            if (SetInjectedKeyState(state, true))
            {
                Log(L"restore  scan=%u", state.scan);
            }
        }
    }
}

void ResetAll(Profile& profile)
{
    for (auto& entry : profile.states)
    {
        entry.second.held = false;
        entry.second.scriptDown = false;
    }
}

// ------------------------------------------------------------------ engine

// Decides what the hook should do with a physical event.
enum class HookAction
{
    Pass,    // deliver the physical event to the system untouched
    Swallow  // drop it
};

// Whether another still-held key currently owns this key's pair. The fact that
// scriptDown is false alone is not enough: a restoration may have been rejected
// by Windows after the opposing key was already released.
bool HasHeldOpponent(const Profile& profile, WORD scanCode)
{
    const auto opponentsIt = profile.opponents.find(scanCode);
    if (opponentsIt == profile.opponents.end())
    {
        return false;
    }

    for (const WORD opponentScan : opponentsIt->second)
    {
        const auto opponentIt = profile.states.find(opponentScan);
        if (opponentIt != profile.states.end() && opponentIt->second.held)
        {
            return true;
        }
    }

    return false;
}

// An auto repeat of a key the engine has actually released for an opponent.
//
// While a key is physically held the keyboard keeps sending repeats, roughly
// thirty per second. In release mode the physical keydown let
// through, so those repeats would re-assert the key in the game and undo the
// release that snap tap just injected — the game would see the old direction as
// continuously held and the feature would appear dead.
//
// A repeat carries no new information: the key is already down. Dropping it is
// therefore safe in both directions. If the game honoured the injected release,
// the key stays released. If the game dropped it, the key was already down and
// nothing changes.
bool IsSuppressedRepeat(const Profile& profile, WORD scanCode, const KeyState& state, bool down)
{
    return down && state.held && !state.scriptDown && HasHeldOpponent(profile, scanCode);
}

void HandleKey(Profile& profile, WORD scanCode, bool down)
{
    auto stateIt = profile.states.find(scanCode);
    if (stateIt == profile.states.end())
    {
        return;
    }

    KeyState& state = stateIt->second;
    const auto opponentsIt = profile.opponents.find(scanCode);
    if (opponentsIt == profile.opponents.end())
    {
        return;
    }

    if (down)
    {
        // If a previous restoration was rejected, the native repeat is the
        // next real down transition the game receives. It repairs bookkeeping
        // once no held opponent still needs this key suppressed.
        if (state.held)
        {
            if (!state.scriptDown && !HasHeldOpponent(profile, scanCode))
            {
                state.scriptDown = true;
            }
            return;
        }

        state.held = true;

        for (const WORD opponentScan : opponentsIt->second)
        {
            auto opponentIt = profile.states.find(opponentScan);
            if (opponentIt == profile.states.end())
            {
                continue;
            }

            KeyState& opponent = opponentIt->second;
            if (opponent.held && opponent.scriptDown)
            {
                if (SetInjectedKeyState(opponent, false))
                {
                    Log(L"press    %u -> release opponent %u", state.scan, opponent.scan);
                }
            }
        }

        // The physical keydown is delivered natively, so the game sees this key
        // as down from here on.
        state.scriptDown = true;
    }
    else
    {
        state.held = false;

        // The physical keyup is delivered natively as well.
        state.scriptDown = false;
        Log(L"release  %u", state.scan);

        for (const WORD opponentScan : opponentsIt->second)
        {
            auto opponentIt = profile.states.find(opponentScan);
            if (opponentIt == profile.states.end())
            {
                continue;
            }

            KeyState& opponent = opponentIt->second;
            if (opponent.held && !opponent.scriptDown &&
                !HasHeldOpponent(profile, opponentScan))
            {
                if (SetInjectedKeyState(opponent, true))
                {
                    Log(L"release  %u -> restore opponent %u", state.scan, opponent.scan);
                }
            }
        }
    }
}

// Hybrid filters its own synthetic events out of Raw Input, so this restoration
// cannot be mistaken for a real press. A single synthetic keydown is therefore
// the same clean restoration used by Release.
bool RestoreHybridKey(KeyState& state)
{
    return SetInjectedKeyState(state, true);
}

void HandleHybridKey(Profile& profile, WORD scanCode, bool down)
{
    auto stateIt = profile.states.find(scanCode);
    if (stateIt == profile.states.end())
    {
        return;
    }

    KeyState& state = stateIt->second;
    const auto opponentsIt = profile.opponents.find(scanCode);
    if (opponentsIt == profile.opponents.end())
    {
        return;
    }

    if (down)
    {
        if (state.held)
        {
            return;
        }

        state.held = true;
        for (const WORD opponentScan : opponentsIt->second)
        {
            auto opponentIt = profile.states.find(opponentScan);
            if (opponentIt == profile.states.end())
            {
                continue;
            }

            KeyState& opponent = opponentIt->second;
            if (opponent.held && opponent.scriptDown)
            {
                if (SetInjectedKeyState(opponent, false))
                {
                    Log(L"compat press %u -> release opponent %u", state.scan, opponent.scan);
                }
            }
        }

        state.scriptDown = true;
        return;
    }

    if (!state.held)
    {
        return;
    }

    state.held = false;
    state.scriptDown = false;

    for (const WORD opponentScan : opponentsIt->second)
    {
        auto opponentIt = profile.states.find(opponentScan);
        if (opponentIt == profile.states.end())
        {
            continue;
        }

        KeyState& opponent = opponentIt->second;
        if (opponent.held && !opponent.scriptDown &&
            !HasHeldOpponent(profile, opponentScan))
        {
            if (RestoreHybridKey(opponent))
            {
                Log(L"compat release %u -> restore opponent %u", state.scan, opponent.scan);
            }
        }
    }
}

// Delivery where the engine owns the stream: the physical press is held back and
// replaced by an injected one, so the game only ever sees synthetic events.
//
// Used by Blocking and Both. In Both the value is that the opponent's physical
// key is also gone from the system, so a game polling GetAsyncKeyState cannot
// still see it held.
void QueueBlockingRestore(const Profile& profile, WORD releasedScan);

// Restores keys that lost to releasedScan while it was held. Keeping this as a
// helper lets the rare physical-keyup fallback defer restoration until Windows
// has delivered that native keyup.
bool RestoreBlockingOpponents(Profile& profile, WORD releasedScan)
{
    const auto opponentsIt = profile.opponents.find(releasedScan);
    if (opponentsIt == profile.opponents.end())
    {
        return true;
    }

    bool complete = true;
    for (const WORD opponentScan : opponentsIt->second)
    {
        auto opponentIt = profile.states.find(opponentScan);
        if (opponentIt == profile.states.end())
        {
            continue;
        }

        KeyState& opponent = opponentIt->second;
        if (opponent.held && !opponent.scriptDown &&
            !HasHeldOpponent(profile, opponentScan))
        {
            if (SetInjectedKeyState(opponent, true))
            {
                Log(L"blocking up   %u -> restore opponent %u", releasedScan, opponent.scan);
            }
            else
            {
                complete = false;
            }
        }
    }

    return complete;
}

HookAction HandleBlocking(Profile& profile, WORD scanCode, bool down)
{
    auto stateIt = profile.states.find(scanCode);
    if (stateIt == profile.states.end())
    {
        return HookAction::Pass;
    }

    KeyState& state = stateIt->second;
    const auto opponentsIt = profile.opponents.find(scanCode);
    if (opponentsIt == profile.opponents.end())
    {
        return HookAction::Pass;
    }

    if (down)
    {
        if (state.held)
        {
            if (!state.scriptDown && !HasHeldOpponent(profile, scanCode))
            {
                // A previous restoration was rejected. Retry it while the
                // physical key is still held; if that also fails, let this
                // real repeat through so the game cannot remain stuck up.
                if (SetInjectedKeyState(state, true))
                {
                    return HookAction::Swallow;
                }

                state.scriptDown = true;
                return HookAction::Pass;
            }

            return HookAction::Swallow; // auto repeat of an already-owned key
        }

        state.held = true;

        for (const WORD opponentScan : opponentsIt->second)
        {
            auto opponentIt = profile.states.find(opponentScan);
            if (opponentIt == profile.states.end())
            {
                continue;
            }

            KeyState& opponent = opponentIt->second;
            if (opponent.held && opponent.scriptDown)
            {
                if (SetInjectedKeyState(opponent, false))
                {
                    Log(L"blocking down %u -> release opponent %u", state.scan, opponent.scan);
                }
            }
        }

        if (SetInjectedKeyState(state, true))
        {
            Log(L"blocking down %u", state.scan);
            return HookAction::Swallow;
        }

        // The injected replacement was rejected. Pass the original physical
        // keydown instead and reflect that native delivery in the state.
        state.scriptDown = true;
        return HookAction::Pass;
    }

    if (!state.held)
    {
        return HookAction::Pass;
    }

    state.held = false;

    if (state.scriptDown)
    {
        if (SetInjectedKeyState(state, false))
        {
            Log(L"blocking up   %u", state.scan);
        }
        else
        {
            // The original physical keyup is now the only reliable release.
            // Queue restoration after the hook has returned so it cannot be
            // ordered ahead of that native keyup.
            state.scriptDown = false;
            QueueBlockingRestore(profile, scanCode);
            return HookAction::Pass;
        }
    }

    if (!RestoreBlockingOpponents(profile, scanCode))
    {
        // One deferred retry handles a transient SendInput rejection. If it is
        // still rejected, the held key's next real repeat takes the fallback
        // path above rather than leaving a silent stuck state.
        QueueBlockingRestore(profile, scanCode);
    }

    return HookAction::Swallow;
}

// ------------------------------------------------------------------ targets

// Returns the index of the profile owning the foreground window, or -1.
// Describes the window that currently owns the foreground.
//
// Everything the profile match depends on, gathered in one place. The old code
// returned "no match" and nothing else, so a game that was in front but not
// recognised looked identical to no game being in front at all.
struct ForegroundInfo
{
    HWND window = nullptr;
    HWND root = nullptr;
    DWORD processId = 0;
    std::wstring imageName;   // "TslGame.exe", as Windows reports it
    std::wstring imagePath;   // full path, when it can be read
    std::wstring windowTitle;
    std::wstring className;
    bool readOk = false;
    bool elevated = false;
    int matchIndex = -1;
};

ForegroundInfo DescribeForeground()
{
    ForegroundInfo info;

    info.window = GetForegroundWindow();
    if (info.window == nullptr)
    {
        return info;
    }

    // The root window is what a game usually owns; the foreground handle is often a
    // child of it.
    info.root = GetAncestor(info.window, GA_ROOT);

    GetWindowThreadProcessId(info.window, &info.processId);

    // Window title and class, which is how a launcher or an overlay gives itself
    // away.
    wchar_t title[512]{};
    GetWindowTextW(info.window, title, 512);
    info.windowTitle = title;

    wchar_t className[256]{};
    GetClassNameW(info.window, className, 256);
    info.className = className;

    if (info.processId == 0 || info.processId == GetCurrentProcessId())
    {
        return info;
    }

    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, info.processId);

    if (process == nullptr)
    {
        // A game running elevated cannot be opened by a normal process. That alone
        // is worth recording, because it explains a missing match.
        info.elevated = GetLastError() == ERROR_ACCESS_DENIED;
        return info;
    }

    wchar_t module[MAX_PATH]{};
    DWORD size = MAX_PATH;

    if (QueryFullProcessImageNameW(process, 0, module, &size) != FALSE)
    {
        info.imagePath = module;

        const size_t slash = info.imagePath.find_last_of(L"\\/");
        info.imageName = slash == std::wstring::npos ? info.imagePath : info.imagePath.substr(slash + 1);
        info.readOk = true;
    }

    CloseHandle(process);

    // Which profile, if any, this matches.
    for (size_t index = 0; index < g_profiles.size(); ++index)
    {
        if (MatchesProfile(info.imageName, info.imagePath, g_profiles[index]))
        {
            info.matchIndex = static_cast<int>(index);
            break;
        }
    }

    return info;
}

Profile* ActiveProfile()
{
    const int index = g_activeProfile.load(std::memory_order_acquire);
    if (index < 0)
    {
        return nullptr;
    }

    return &g_profiles[index];
}

// Signalled by the interface to ask for a clean stop. The completion event lets
// the watchdog wait until the message thread has released all injected keys.
HANDLE g_shutdownEvent = nullptr;
HANDLE g_shutdownCompleteEvent = nullptr;
HANDLE g_foregroundRefreshEvent = nullptr;
std::thread g_shutdownWatchdogThread;
std::thread g_foregroundPollThread;
bool g_releasedEverything = false;

// Runs from the state-owning message thread after a blocking keyup has either
// completed or been allowed through natively. The profile is revalidated in
// case focus changed between the hook callback and this queued work.
void RetryBlockingRestoreOnMessageThread(int profileIndex, WORD releasedScan)
{
    if (g_stopping || profileIndex != g_activeProfile.load(std::memory_order_acquire))
    {
        return;
    }

    Profile* profile = ActiveProfile();
    if (profile == nullptr || profile->mode != EngineMode::Blocking)
    {
        return;
    }

    const auto releasedIt = profile->states.find(releasedScan);
    if (releasedIt != profile->states.end() && releasedIt->second.held)
    {
        // The user pressed the source again before this retry ran, so its old
        // release must not restore an opponent into the new press.
        return;
    }

    RestoreBlockingOpponents(*profile, releasedScan);
}

void QueueBlockingRestore(const Profile& profile, WORD releasedScan)
{
    if (g_stopping || g_mainThreadId == 0)
    {
        return;
    }

    const int profileIndex = g_activeProfile.load(std::memory_order_acquire);
    if (profileIndex < 0 || profileIndex >= static_cast<int>(g_profiles.size()) ||
        &g_profiles[profileIndex] != &profile)
    {
        return;
    }

    PostThreadMessageW(g_mainThreadId,
                       kRestoreBlockingMessage,
                       static_cast<WPARAM>(profileIndex),
                       static_cast<LPARAM>(releasedScan));
}

EngineMode ParseModeName(const std::wstring& name)
{
    if (EqualsIgnoreCase(name, L"blocking"))
    {
        return EngineMode::Blocking;
    }

    if (EqualsIgnoreCase(name, L"compatibility") || EqualsIgnoreCase(name, L"hybrid"))
    {
        return EngineMode::Hybrid;
    }

    return EngineMode::Release;
}

const wchar_t* ModeName(EngineMode mode)
{
    switch (mode)
    {
    case EngineMode::Blocking: return L"blocking";
    case EngineMode::Hybrid: return L"hybrid";
    default: return L"release";
    }
}

// Writes everything known about the window in front.
//
// The old line said only that no profile matched, which is the same message whether
// a game is in front under a different executable name, a launcher has taken focus,
// or an elevated process cannot be inspected at all. Those need different fixes, so
// the details are recorded.
void LogForeground(const wchar_t* reason, const ForegroundInfo& info)
{
    if (info.window == nullptr)
    {
        Log(L"%-22s no foreground window at all", reason);
        return;
    }

    Log(L"%-22s window=%p class=\"%s\" title=\"%s\"",
        reason,
        static_cast<void*>(info.window),
        info.className.c_str(),
        info.windowTitle.c_str());

    if (!info.readOk)
    {
        Log(L"%-22s pid=%lu  image unreadable  elevated=%s",
            reason,
            info.processId,
            info.elevated ? L"YES (this is why it cannot be matched)" : L"no");
        return;
    }

    Log(L"%-22s pid=%lu  image=\"%s\"  path=%s",
        reason,
        info.processId,
        info.imageName.c_str(),
        info.imagePath.c_str());
}

void SwitchProfile(int next)
{
    if (next < -1 || next >= static_cast<int>(g_profiles.size()))
    {
        next = -1;
    }

    if (next == g_activeProfile.load(std::memory_order_relaxed))
    {
        return;
    }

    if (Profile* current = ActiveProfile())
    {
        // Leaving a game: hand back anything suppressed, then forget the state.
        RestoreSuppressedKeys(*current);
        ResetAll(*current);
    }

    g_activeProfile.store(next, std::memory_order_release);

    if (Profile* entered = ActiveProfile())
    {
        // Entering a game: keys may already be held from before.
        ResyncStates(*entered);

        Log(L"ARMED      \"%s\"  method=%s  keys=%zu  target=\"%s\"  path=%s",
            entered->target.c_str(),
            ModeName(entered->mode),
            entered->managedScanCodes.size(),
            entered->target.c_str(),
            entered->targetPath.empty() ? L"(legacy name match)" : entered->targetPath.c_str());
    }
    else
    {
        // Detailed foreground inspection happens in the background watcher;
        // do not repeat it in the input message queue just to produce logging.
        Log(L"NOT-ARMED  no matching foreground profile");
    }

    // A Hybrid profile uses WM_INPUT and must not coexist with the
    // global low-level hook. A normal release or blocking profile still needs
    // that hook for repeat suppression or event replacement respectively.
    const Profile* active = ActiveProfile();
    g_keyboardHookWanted = active != nullptr && active->mode != EngineMode::Hybrid;
    UpdateKeyboardHook();
}

// A foreground probe is read-only and happens off the input message thread.
// Applying its result is still serialized with Raw Input and the low-level
// hook. The root-window check rejects a stale probe after a rapid Alt+Tab.
void ApplyForegroundRefreshOnMessageThread(int next, HWND observedRoot)
{
    const HWND foreground = GetForegroundWindow();
    const HWND currentRoot = foreground == nullptr ? nullptr : GetAncestor(foreground, GA_ROOT);
    if (currentRoot == observedRoot)
    {
        SwitchProfile(next);
    }
    else if (!g_shutdownRequested.load(std::memory_order_acquire) &&
             g_foregroundRefreshEvent != nullptr)
    {
        // The foreground changed while its process was being inspected. Ask
        // the poller for a fresh read immediately instead of waiting for its
        // 250 ms safety interval.
        SetEvent(g_foregroundRefreshEvent);
    }
}

// Watcher threads never touch a Profile or KeyState. Repeated requests are
// coalesced: each one merely posts a read-only foreground match for the input
// thread to validate and apply.
void RequestForegroundRefresh()
{
    if (g_shutdownRequested.load(std::memory_order_acquire) || g_mainThreadId == 0)
    {
        return;
    }

    bool expected = false;
    if (!g_foregroundRefreshPosted.compare_exchange_strong(
            expected, true, std::memory_order_acq_rel, std::memory_order_acquire))
    {
        return;
    }

    const ForegroundInfo info = DescribeForeground();
    const WPARAM matchIndex = static_cast<WPARAM>(static_cast<INT_PTR>(info.matchIndex));
    const LPARAM root = reinterpret_cast<LPARAM>(info.root);
    if (!PostThreadMessageW(g_mainThreadId, kRefreshForegroundMessage, matchIndex, root))
    {
        g_foregroundRefreshPosted.store(false, std::memory_order_release);
    }
}

// Fires whenever the foreground window changes.
//
// This is the fast path. The polling loop below does the same work on a timer, so a
// missed event is still caught within half a second.
void CALLBACK ForegroundEventProc(HWINEVENTHOOK, DWORD, HWND, LONG, LONG, DWORD, DWORD)
{
    // Do not inspect another process from this callback. Depending on how
    // Windows dispatches the event, it may share the input message thread.
    // Waking the dedicated poller keeps all potentially slow inspection away
    // from Raw Input and the low-level hook.
    if (!g_shutdownRequested.load(std::memory_order_acquire) &&
        g_foregroundRefreshEvent != nullptr)
    {
        SetEvent(g_foregroundRefreshEvent);
    }
}

// Re-checks which game owns the foreground a few times a second.
//
// The foreground event is the fast path, but it can be missed: the hook is
// installed after the game may already be in front, a window can change focus
// without the event being delivered, and a game that creates its window late
// would leave the engine unarmed until something else happened to trigger a
// switch. That is what made disarming and re-arming appear to fix things. This
// poll makes arming independent of event delivery.
//
// The check is a foreground window lookup plus one process open, so it is cheap
// enough to run continuously.
void ForegroundPollLoop()
{
    if (g_foregroundRefreshEvent == nullptr)
    {
        while (WaitForSingleObject(g_shutdownEvent, 250) == WAIT_TIMEOUT)
        {
            RequestForegroundRefresh();
        }
        return;
    }

    const HANDLE events[] = {g_shutdownEvent, g_foregroundRefreshEvent};
    while (true)
    {
        const DWORD wait = WaitForMultipleObjects(2, events, FALSE, 250);
        if (wait == WAIT_OBJECT_0)
        {
            return;
        }

        if (wait == WAIT_OBJECT_0 + 1 || wait == WAIT_TIMEOUT)
        {
            RequestForegroundRefresh();
            continue;
        }

        return;
    }
}

LRESULT CALLBACK KeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode < 0)
    {
        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    const auto* data = reinterpret_cast<KBDLLHOOKSTRUCT*>(lParam);

    g_lastHookCall = GetTickCount();
    if (g_log != nullptr)
    {
        InterlockedIncrement(&g_hookEventCount);
    }

    // Only real hardware drives the engine; synthetic events are left alone.
    if ((data->flags & LLKHF_INJECTED) != 0)
    {
        const bool ours = data->dwExtraInfo == kOwnTag;

        if (g_log != nullptr)
        {
            if (ours)
            {
                InterlockedIncrement(&g_ownEventCount);
            }
            else
            {
                InterlockedIncrement(&g_foreignEventCount);
            }
        }

        // The benchmark watches for its own event coming back, which is the point at
        // which the system has finished delivering it.
        if (g_benchWatching && data->scanCode == g_benchScan)
        {
            QueryPerformanceCounter(&g_benchArrival);
            g_benchArrived = true;
        }

        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    const bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
    const bool isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;
    if (!isDown && !isUp)
    {
        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    const WORD baseScan = static_cast<WORD>(data->scanCode);
    const bool extended = (data->flags & LLKHF_EXTENDED) != 0;
    const WORD keyId = static_cast<WORD>(baseScan | (extended ? kExtendedBit : 0));

    // Never query foreground windows or open processes from this callback.
    // This hook, WM_INPUT and profile transitions are all dispatched by this
    // same message thread, so there is no shared lock to contend for and no
    // physical event is ever skipped while a profile is changing.

    Profile* profile = ActiveProfile();
    if (profile == nullptr)
    {
        // Counted rather than ignored. A keystroke that arrives with nothing armed
        // is the sign that the hook is alive and the game is not the foreground
        // window, which is a different fault from a hook that has stopped being
        // called at all.
        if (g_log != nullptr)
        {
            if (g_log != nullptr)
            {
                InterlockedIncrement(&g_unarmedEventCount);
            }
        }
        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    // Prefer the identity including the flag; fall back to the bare scan code so
    // a profile written before this distinction existed still matches.
    auto stateIt = profile->states.find(keyId);
    if (stateIt == profile->states.end())
    {
        stateIt = profile->states.find(baseScan);
    }

    if (stateIt == profile->states.end())
    {
        // Counted, not logged per press. Writing a line per keystroke inside a hook
        // that Windows removes after 200 ms is a risk not worth taking, and the
        // heartbeat reports the total anyway.
        if (g_log != nullptr)
        {
            if (g_log != nullptr)
            {
                InterlockedIncrement(&g_unmanagedEventCount);
            }
        }
        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    if (g_log != nullptr)
        {
            if (g_log != nullptr)
            {
                InterlockedIncrement(&g_managedEventCount);
            }
        }

    // The dictionary key is the identity, so the engine must use it too.
    const WORD key = stateIt->first;

    KeyState& state = stateIt->second;
    state.scan = baseScan;
    state.vk = static_cast<WORD>(data->vkCode);
    state.extended = extended;

    if (profile->mode == EngineMode::Hybrid)
    {
        // The hook can still be in the process of being removed when focus
        // changes. Hybrid state belongs exclusively to Raw Input, so this
        // transitional callback must never act on it.
        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    if (profile->mode == EngineMode::Blocking)
    {
        // The physical event never reaches the system; the engine supplies the
        // replacement instead. If Windows rejects that replacement, pass this
        // one physical event through rather than silently dropping a key.
        if (HandleBlocking(*profile, key, isDown) == HookAction::Swallow)
        {
            return 1;
        }

        return CallNextHookEx(g_hook, nCode, wParam, lParam);
    }

    const auto action = IsSuppressedRepeat(*profile, key, state, isDown)
        ? HookAction::Swallow
        : HookAction::Pass;

    HandleKey(*profile, key, isDown);

    if (action == HookAction::Swallow)
    {
        return 1;
    }


    // Every other event, including every keyup and every keydown that starts
    // a new press, reaches the system untouched.
    return CallNextHookEx(g_hook, nCode, wParam, lParam);
}

// ---------------------------------------------------------------- raw input

// Handles the Hybrid method. Raw Input observes device events without joining
// the system-wide low-level hook chain, so macro applications retain their
// normal hook order and timing.
void HandleRawKeyboardInput(const RAWKEYBOARD& keyboard)
{
    // Raw Input also receives the events we create through SendInput. They carry
    // the low 32 bits of kOwnTag in ExtraInformation. Treating our synthetic
    // keyup as a physical release cleared (for example) D immediately after A
    // pressed it, leaving nothing to restore when A was released.
    if (keyboard.ExtraInformation == static_cast<ULONG>(kOwnTag))
    {
        return;
    }

    // VK_PACKET and a zero scan code do not represent a normal physical key.
    if (keyboard.VKey == 0xFF || keyboard.MakeCode == 0)
    {
        return;
    }

    const bool down = (keyboard.Flags & RI_KEY_BREAK) == 0;
    const WORD baseScan = keyboard.MakeCode;
    const bool extended = (keyboard.Flags & (RI_KEY_E0 | RI_KEY_E1)) != 0;
    const WORD keyId = static_cast<WORD>(baseScan | (extended ? kExtendedBit : 0));

    Profile* profile = ActiveProfile();
    if (profile == nullptr || profile->mode != EngineMode::Hybrid)
    {
        return;
    }

    auto stateIt = profile->states.find(keyId);
    if (stateIt == profile->states.end())
    {
        stateIt = profile->states.find(baseScan);
    }

    if (stateIt == profile->states.end())
    {
        return;
    }

    const WORD key = stateIt->first;
    KeyState& state = stateIt->second;
    state.scan = baseScan;
    state.vk = static_cast<WORD>(keyboard.VKey);
    state.extended = extended;

    // Raw Input owns state changes. A repeat of a direction currently released
    // for SOCD gets another synthetic release; it never turns into SnapHook
    // state and no low-level hook is needed to filter it.
    if (down && state.held)
    {
        if (!state.scriptDown)
        {
            if (!HasHeldOpponent(*profile, key))
            {
                // A failed restoration left bookkeeping at "up", but the
                // original opposing key is no longer held. This native repeat
                // is therefore allowed through and makes the key down again.
                state.scriptDown = true;
            }
            else if (!InjectKey(state, false))
            {
                // The physical repeat has already reasserted the native down.
                // If Windows rejects our corrective keyup, reflect that real
                // repeat instead of pretending the old key is still released.
                state.scriptDown = true;
            }
        }
        return;
    }

    HandleHybridKey(*profile, key, down);
}

LRESULT CALLBACK RawInputWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_INPUT)
    {
        UINT bytes = sizeof(RAWINPUT);
        RAWINPUT input{};
        if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, &input, &bytes,
                            sizeof(RAWINPUTHEADER)) != static_cast<UINT>(-1) &&
            input.header.dwType == RIM_TYPEKEYBOARD)
        {
            HandleRawKeyboardInput(input.data.keyboard);
        }
        return 0;
    }

    return DefWindowProcW(window, message, wParam, lParam);
}

bool CreateRawInputWindow()
{
    constexpr wchar_t kRawInputClass[] = L"SnapHookRawInputWindow";
    WNDCLASSW windowClass{};
    windowClass.hInstance = g_instance;
    windowClass.lpszClassName = kRawInputClass;
    windowClass.lpfnWndProc = RawInputWindowProc;

    const ATOM atom = RegisterClassW(&windowClass);
    if (atom == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
    {
        Log(L"raw input window class failed  error=%lu", GetLastError());
        return false;
    }

    g_rawInputWindow = CreateWindowExW(0, kRawInputClass, L"", 0,
                                        0, 0, 0, 0, HWND_MESSAGE, nullptr, g_instance, nullptr);
    if (g_rawInputWindow == nullptr)
    {
        Log(L"raw input window create failed  error=%lu", GetLastError());
        return false;
    }

    RAWINPUTDEVICE device{};
    device.usUsagePage = 0x01; // Generic Desktop Controls
    device.usUsage = 0x06;     // Keyboard
    device.dwFlags = RIDEV_INPUTSINK;
    device.hwndTarget = g_rawInputWindow;
    if (!RegisterRawInputDevices(&device, 1, sizeof(device)))
    {
        Log(L"raw input registration failed  error=%lu", GetLastError());
        DestroyWindow(g_rawInputWindow);
        g_rawInputWindow = nullptr;
        return false;
    }

    return true;
}

void UpdateKeyboardHook()
{
    const bool wanted = g_keyboardHookWanted;
    if (wanted && g_hook == nullptr)
    {
        g_hook = SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardProc, g_instance, 0);
        if (g_hook == nullptr)
        {
            Log(L"hook install failed  error=%lu", GetLastError());
        }
    }
    else if (!wanted && g_hook != nullptr)
    {
        UnhookWindowsHookEx(g_hook);
        g_hook = nullptr;
    }
}

// ------------------------------------------------------------------ parsing

void AddPair(Profile& profile, WORD firstScan, WORD secondScan)
{
    if (firstScan == 0 || secondScan == 0 || firstScan == secondScan)
    {
        return;
    }

    // Guard against a value that carries neither a real scan code nor a sane
    // extension bit, which would create a key the hook can never match.
    if ((firstScan & ~kExtendedBit) > 0xFF || (secondScan & ~kExtendedBit) > 0xFF)
    {
        Log(L"ignoring pair with out of range scan codes %u:%u", firstScan, secondScan);
        return;
    }

    profile.managedScanCodes.insert(firstScan);
    profile.managedScanCodes.insert(secondScan);

    // A key may take part in several pairs (W with S, but also W with A), so the
    // opponents are a list rather than a single partner. The same pair listed
    // twice must not add the opponent twice, or the engine would inject a
    // duplicate release.
    auto addOpponent = [&profile](WORD key, WORD opponent)
    {
        auto& list = profile.opponents[key];
        if (std::find(list.begin(), list.end(), opponent) == list.end())
        {
            list.push_back(opponent);
        }
    };

    addOpponent(firstScan, secondScan);
    addOpponent(secondScan, firstScan);

    profile.states[firstScan].scan = firstScan;
    profile.states[secondScan].scan = secondScan;
}

void ParsePairs(Profile& profile, const std::wstring& pairs)
{
    size_t start = 0;
    while (start < pairs.length())
    {
        const size_t comma = pairs.find(L',', start);
        const std::wstring item =
            pairs.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start);
        const size_t colon = item.find(L':');
        if (colon != std::wstring::npos)
        {
            const WORD first = static_cast<WORD>(std::wcstoul(item.substr(0, colon).c_str(), nullptr, 10));
            const WORD second = static_cast<WORD>(std::wcstoul(item.substr(colon + 1).c_str(), nullptr, 10));
            AddPair(profile, first, second);
        }

        if (comma == std::wstring::npos)
        {
            break;
        }
        start = comma + 1;
    }
}

// Name of a delivery method, as it appears on the command line.
// Parses one --profile argument.
//
// Three shapes are accepted, so existing settings and hand-written command lines
// remain usable:
//
//   "TslGame|C:\\Games\\GTA5.exe|release|30:32,17:31"  exact executable path
//   "TslGame|release|30:32,17:31"                       legacy name + method
//   "TslGame|30:32,17:31"                               legacy name + default method
//
// The target is the process name as the interface stores it, with or without
// ".exe"; matching strips the extension either way.
void ParseProfile(const std::wstring& argument)
{
    const size_t bar = argument.find(L'|');
    if (bar == std::wstring::npos)
    {
        return;
    }

    std::wstring target = argument.substr(0, bar);
    const size_t firstMeaningful = target.find_first_not_of(L" \t");
    if (firstMeaningful == std::wstring::npos)
    {
        return;
    }

    target = target.substr(firstMeaningful);
    while (!target.empty() && std::iswspace(target.back()))
    {
        target.pop_back();
    }

    std::wstring rest = argument.substr(bar + 1);
    Profile profile;
    profile.target = target;
    profile.mode = g_mode;

    // A modern profile has path|method|pairs. A Windows path cannot contain '|',
    // so this delimiter is safe. An empty path is intentionally allowed to retain
    // the legacy name-only behaviour.
    const size_t nextBar = rest.find(L'|');
    if (nextBar != std::wstring::npos)
    {
        const std::wstring firstToken = rest.substr(0, nextBar);
        const std::wstring afterFirst = rest.substr(nextBar + 1);
        const size_t secondBar = afterFirst.find(L'|');

        if (secondBar != std::wstring::npos)
        {
            profile.targetPath = firstToken;
            profile.mode = ParseModeName(afterFirst.substr(0, secondBar));
            rest = afterFirst.substr(secondBar + 1);
        }
        else
        {
            // The old target|method|pairs form.
            profile.mode = ParseModeName(firstToken);
            rest = afterFirst;
        }
    }

    ParsePairs(profile, rest);

    if (!profile.managedScanCodes.empty())
    {
        g_profiles.push_back(std::move(profile));
    }
}

// ------------------------------------------------------- unattended shutdown

// Releases every key the engine could be holding.
//
// In blocking mode the engine owns the stream, so a key it injected stays down
// in the system if the process simply disappears — the game would believe the
// player is still holding it.
void ReleaseEverythingHeld()
{
    if (g_releasedEverything)
    {
        return;
    }

    g_releasedEverything = true;

    if (Profile* profile = ActiveProfile())
    {
        ResetAll(*profile);
    }

    // Belt and braces: push a release for every managed key regardless of the
    // bookkeeping, in case a state flag ever disagrees with reality. Releasing a
    // key that is not down is harmless.
    for (size_t index = 0; index < g_profiles.size(); ++index)
    {
        for (const WORD scanCode : g_profiles[index].managedScanCodes)
        {
            KeyState probe;
            probe.scan = scanCode;
            const WORD vk = VkFromScanCode(scanCode);
            probe.vk = vk;
            probe.extended = IsExtendedScanCode(scanCode);
            InjectKey(probe, false);
        }
    }
}

// Waits for the interface to ask the worker to stop, then unwinds cleanly.
//
// The interface terminates the process rather than closing it, and
// TerminateProcess runs no cleanup at all, so relying on the code after the
// message loop would leave keys held. The watchdog must not release them
// itself: only the message thread owns keyboard state.
DWORD WINAPI ShutdownWatchdog(LPVOID)
{
    const DWORD wait = WaitForSingleObject(g_shutdownEvent, INFINITE);

    if (wait == WAIT_OBJECT_0)
    {
        g_shutdownRequested.store(true, std::memory_order_release);
        if (g_mainThreadId != 0 &&
            PostThreadMessageW(g_mainThreadId, kReleaseAndQuitMessage, 0, 0))
        {
            if (g_shutdownCompleteEvent != nullptr)
            {
                WaitForSingleObject(g_shutdownCompleteEvent, 1500);
            }
        }
    }

    return 0;
}

// Runs in the state-owning message thread. Stop accepting focus changes before
// releasing keys so a late notification cannot re-arm a profile halfway
// through shutdown.
void ShutdownOnMessageThread()
{
    if (g_stopping)
    {
        return;
    }

    g_stopping = true;
    g_shutdownRequested.store(true, std::memory_order_release);

    if (g_foregroundHook != nullptr)
    {
        UnhookWinEvent(g_foregroundHook);
        g_foregroundHook = nullptr;
    }

    g_keyboardHookWanted = false;
    UpdateKeyboardHook();
    ReleaseEverythingHeld();

    if (g_rawInputWindow != nullptr)
    {
        DestroyWindow(g_rawInputWindow);
        g_rawInputWindow = nullptr;
    }

    if (g_shutdownCompleteEvent != nullptr)
    {
        SetEvent(g_shutdownCompleteEvent);
    }
}

// ------------------------------------------------------------------ selftest

// A controllable SendInput replacement makes state-machine tests deterministic
// and keeps them from generating real keyboard input on the developer's PC.
struct CapturedInjection
{
    WORD scan = 0;
    bool down = false;
};

std::vector<CapturedInjection> g_capturedInjections;
int g_testSendInputCalls = 0;
int g_testFailOnSendInputCall = 0;

UINT WINAPI CaptureSendInput(UINT count, LPINPUT inputs, int)
{
    ++g_testSendInputCalls;
    if (g_testFailOnSendInputCall == g_testSendInputCalls)
    {
        return 0;
    }

    for (UINT index = 0; index < count; ++index)
    {
        g_capturedInjections.push_back(
            {inputs[index].ki.wScan, (inputs[index].ki.dwFlags & KEYEVENTF_KEYUP) == 0});
    }

    return count;
}

class CapturedSendInputScope
{
public:
    CapturedSendInputScope()
        : previous_(g_sendInput)
    {
        g_capturedInjections.clear();
        g_testSendInputCalls = 0;
        g_testFailOnSendInputCall = 0;
        g_sendInput = CaptureSendInput;
    }

    ~CapturedSendInputScope()
    {
        g_sendInput = previous_;
        g_capturedInjections.clear();
        g_testSendInputCalls = 0;
        g_testFailOnSendInputCall = 0;
    }

private:
    SendInputFunction previous_;
};

Profile MakeHybridTestProfile()
{
    Profile profile;
    profile.target = L"selftest";
    profile.mode = EngineMode::Hybrid;
    AddPair(profile, 0x1E, 0x20); // A and D
    profile.states[0x1E].vk = 'A';
    profile.states[0x20].vk = 'D';
    return profile;
}

// Exercises the exact Hybrid sequence that used to leave an old direction
// stuck: hold D, hold A, release A, then release D. It also verifies that a
// rejected SendInput never changes scriptDown and that a repeated raw keydown
// recovers to the physical state if its corrective release is rejected.
int RunHybridSelfTest(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);
    CapturedSendInputScope capture;

    int failures = 0;
    auto check = [&](const wchar_t* what, bool ok)
    {
        if (!ok)
        {
            ++failures;
        }
        Log(L"%s  %s", ok ? L"pass" : L"FAIL", what);
    };

    constexpr WORD keyA = 0x1E;
    constexpr WORD keyD = 0x20;

    // The normal order from the reported gameplay case: D -> A -> release A
    // -> release D. D must be restored exactly once while it remains held.
    {
        Profile profile = MakeHybridTestProfile();
        HandleHybridKey(profile, keyD, true);
        HandleHybridKey(profile, keyA, true);
        HandleHybridKey(profile, keyA, false);
        HandleHybridKey(profile, keyD, false);

        check(L"hybrid restores D after A is released",
              !profile.states[keyA].held && !profile.states[keyA].scriptDown &&
              !profile.states[keyD].held && !profile.states[keyD].scriptDown);
        check(L"hybrid transcript is D up, D down",
              g_capturedInjections.size() == 2 &&
              g_capturedInjections[0].scan == keyD && !g_capturedInjections[0].down &&
              g_capturedInjections[1].scan == keyD && g_capturedInjections[1].down);
    }

    // Once the originally suppressed key has physically gone up, releasing the
    // other key must not re-inject a stale keydown.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    {
        Profile profile = MakeHybridTestProfile();
        HandleHybridKey(profile, keyA, true);
        HandleHybridKey(profile, keyD, true);
        HandleHybridKey(profile, keyA, false);
        HandleHybridKey(profile, keyD, false);

        check(L"hybrid does not restore a physically released opponent",
              g_capturedInjections.size() == 1 &&
              g_capturedInjections[0].scan == keyA && !g_capturedInjections[0].down);
    }

    // A failed synthetic release leaves the state as down; the engine must not
    // claim that Windows released a key it did not accept.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    g_testFailOnSendInputCall = 1;
    {
        Profile profile = MakeHybridTestProfile();
        HandleHybridKey(profile, keyD, true);
        HandleHybridKey(profile, keyA, true);
        check(L"failed hybrid release keeps opponent marked down",
              profile.states[keyD].held && profile.states[keyD].scriptDown &&
              g_capturedInjections.empty());
    }

    // A failed restore likewise leaves the key marked released. A later real
    // event can then reconcile it instead of the bookkeeping drifting away.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    g_testFailOnSendInputCall = 0;
    {
        Profile profile = MakeHybridTestProfile();
        HandleHybridKey(profile, keyD, true);
        HandleHybridKey(profile, keyA, true);
        g_testFailOnSendInputCall = g_testSendInputCalls + 1;
        HandleHybridKey(profile, keyA, false);

        check(L"failed hybrid restore stays marked released",
              profile.states[keyD].held && !profile.states[keyD].scriptDown &&
              g_capturedInjections.size() == 1 &&
              g_capturedInjections[0].scan == keyD && !g_capturedInjections[0].down);
    }

    // Release mode has the same native-repeat recovery rule. If D's synthetic
    // restoration fails after A is up, D's next physical repeat must be passed
    // through rather than swallowed as if A were still held.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    g_testFailOnSendInputCall = 0;
    {
        Profile profile = MakeHybridTestProfile();
        profile.mode = EngineMode::Release;
        HandleKey(profile, keyD, true);
        HandleKey(profile, keyA, true);
        g_testFailOnSendInputCall = g_testSendInputCalls + 1;
        HandleKey(profile, keyA, false);

        const bool repeatSuppressed =
            IsSuppressedRepeat(profile, keyD, profile.states[keyD], true);
        HandleKey(profile, keyD, true);
        check(L"release repeat repairs a failed restore",
              !repeatSuppressed && profile.states[keyD].scriptDown);
    }

    // Repeat the raw-input path itself. If the corrective keyup is rejected,
    // the native repeat has already reached the game, so scriptDown must return
    // to true rather than leave an impossible suppressed state behind.
    {
        std::vector<Profile> savedProfiles = std::move(g_profiles);
        const int savedActive = g_activeProfile.load(std::memory_order_relaxed);
        g_profiles.clear();
        g_profiles.push_back(MakeHybridTestProfile());
        g_activeProfile.store(0, std::memory_order_release);
        g_capturedInjections.clear();
        g_testSendInputCalls = 0;
        g_testFailOnSendInputCall = 0;

        RAWKEYBOARD keyDDown{};
        keyDDown.MakeCode = keyD;
        keyDDown.VKey = 'D';
        RAWKEYBOARD keyADown{};
        keyADown.MakeCode = keyA;
        keyADown.VKey = 'A';

        HandleRawKeyboardInput(keyDDown);
        HandleRawKeyboardInput(keyADown);
        g_testFailOnSendInputCall = g_testSendInputCalls + 1;
        HandleRawKeyboardInput(keyDDown); // physical repeat while D is suppressed

        check(L"failed raw repeat release follows native repeat",
              g_profiles[0].states[keyD].held && g_profiles[0].states[keyD].scriptDown);

        g_profiles = std::move(savedProfiles);
        g_activeProfile.store(savedActive, std::memory_order_release);
    }

    // The same case through the actual Raw Input handler: A goes up, restoring
    // D is rejected, then D repeats. There must be no second synthetic D up.
    {
        std::vector<Profile> savedProfiles = std::move(g_profiles);
        const int savedActive = g_activeProfile.load(std::memory_order_relaxed);
        g_profiles.clear();
        g_profiles.push_back(MakeHybridTestProfile());
        g_activeProfile.store(0, std::memory_order_release);
        g_capturedInjections.clear();
        g_testSendInputCalls = 0;
        g_testFailOnSendInputCall = 0;

        RAWKEYBOARD keyDDown{};
        keyDDown.MakeCode = keyD;
        keyDDown.VKey = 'D';
        RAWKEYBOARD keyADown{};
        keyADown.MakeCode = keyA;
        keyADown.VKey = 'A';
        RAWKEYBOARD keyAUp = keyADown;
        keyAUp.Flags = RI_KEY_BREAK;

        HandleRawKeyboardInput(keyDDown);
        HandleRawKeyboardInput(keyADown);
        g_testFailOnSendInputCall = g_testSendInputCalls + 1;
        HandleRawKeyboardInput(keyAUp);
        const size_t countBeforeRepeat = g_capturedInjections.size();
        g_testFailOnSendInputCall = 0;
        HandleRawKeyboardInput(keyDDown);

        check(L"raw repeat repairs failed restore without another release",
              g_profiles[0].states[keyD].scriptDown &&
              g_capturedInjections.size() == countBeforeRepeat);

        g_profiles = std::move(savedProfiles);
        g_activeProfile.store(savedActive, std::memory_order_release);
    }

    Log(L"hybrid selftest: %d failed", failures);
    if (g_log != nullptr)
    {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return failures == 0 ? 0 : 1;
}

// Drives the engine directly, with no hook and no real keyboard, and reports
// the resulting key state. Run with:  SnapHookWorker.exe --selftest <log path>
//
// A pair A(30):D(32) is used. The expected transcript is:
//   press A     -> A held, A seen by the game
//   press D     -> A released by the engine, D held
//   release D   -> A restored by the engine
//   release A   -> A released
int RunSelfTest(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);
    CapturedSendInputScope capture;

    Profile profile;
    profile.target = L"selftest";
    AddPair(profile, 0x1E, 0x20); // A and D

    const WORD keyA = 0x1E;
    const WORD keyD = 0x20;

    auto show = [&](const wchar_t* stage)
    {
        const KeyState& a = profile.states[keyA];
        const KeyState& d = profile.states[keyD];
        Log(L"%-22s  A[held=%d seen=%d]  D[held=%d seen=%d]",
            stage,
            a.held ? 1 : 0, a.scriptDown ? 1 : 0,
            d.held ? 1 : 0, d.scriptDown ? 1 : 0);
    };

    Log(L"selftest start  pair A(30):D(32)");

    // The virtual keys are normally learned from real events; supply them here.
    profile.states[keyA].vk = 'A';
    profile.states[keyD].vk = 'D';

    HandleKey(profile, keyA, true);
    show(L"press A");

    HandleKey(profile, keyD, true);
    show(L"press D (opposes A)");

    HandleKey(profile, keyD, false);
    show(L"release D");

    HandleKey(profile, keyA, false);
    show(L"release A");

    // Press A again, let repeats arrive, then press D. The repeats must be
    // classified for dropping and must not re-run the suppression logic.
    HandleKey(profile, keyA, true);
    Log(L"repeat of A suppressed = %d",
        IsSuppressedRepeat(profile, keyA, profile.states[keyA], true) ? 1 : 0);
    HandleKey(profile, keyA, true);
    HandleKey(profile, keyD, true);
    show(L"A held, repeat, press D");

    // The repeat of the released key must be dropped for as long as D is held.
    Log(L"repeat of A while D held suppressed = %d",
        IsSuppressedRepeat(profile, keyA, profile.states[keyA], true) ? 1 : 0);

    // Once A is let go the flag clears, so a fresh press is never dropped.
    HandleKey(profile, keyD, false);
    HandleKey(profile, keyA, false);
    Log(L"repeat of A after release suppressed = %d",
        IsSuppressedRepeat(profile, keyA, profile.states[keyA], true) ? 1 : 0);

    Log(L"selftest done");

    if (g_log != nullptr)
    {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return 0;
}



// Measures how long an injected event takes to come back round to this thread.
//
// This is the number that matters: from the engine deciding to release a key, to
// the system having delivered that event. It runs with a hook installed and a
// message loop pumping, which is the worker's real condition — the earlier
// measurement had neither, and so timed the input queue backing up rather than the
// delivery of one event.
int RunHookRoundTripBenchmark(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);

    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);

    g_hook = SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardProc, GetModuleHandleW(nullptr), 0);
    if (!g_hook)
    {
        Log(L"RESULT hook_install_failed=%lu", GetLastError());
        if (g_log) { std::fclose(g_log); g_log = nullptr; }
        return 1;
    }

    KeyState probe;
    probe.scan = 0x20;
    probe.vk = 'D';
    probe.extended = false;

    g_benchScan = 0x20;
    g_benchWatching = true;

    std::vector<double> samples;
    samples.reserve(300);
    int missed = 0;

    for (int i = 0; i < 300; ++i)
    {
        g_benchArrived = false;

        LARGE_INTEGER start{};
        QueryPerformanceCounter(&start);

        InjectKey(probe, false);

        // Pump until the event comes back, or give up after 50 ms. A real worker
        // pumps continuously, so this matches how the hook is actually driven.
        LARGE_INTEGER now{};
        do
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }

            QueryPerformanceCounter(&now);
        }
        while (!g_benchArrived &&
               (double)(now.QuadPart - start.QuadPart) * 1000.0 / (double)frequency.QuadPart < 50.0);

        if (g_benchArrived)
        {
            samples.push_back(
                (double)(g_benchArrival.QuadPart - start.QuadPart) * 1'000'000.0 / (double)frequency.QuadPart);
        }
        else
        {
            missed++;
        }

        Sleep(2);
    }

    // Second pass with logging silenced, to see whether disk writes inside the
    // hook are costing anything.
    std::vector<double> quiet;
    quiet.reserve(samples.size());

    FILE* realLog = g_log;
    g_log = nullptr;

    for (int i = 0; i < 200; ++i)
    {
        g_benchArrived = false;

        LARGE_INTEGER start{};
        QueryPerformanceCounter(&start);

        InjectKey(probe, false);

        LARGE_INTEGER now{};
        do
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }

            QueryPerformanceCounter(&now);
        }
        while (!g_benchArrived &&
               (double)(now.QuadPart - start.QuadPart) * 1000.0 / (double)frequency.QuadPart < 50.0);

        if (g_benchArrived)
        {
            quiet.push_back(
                (double)(g_benchArrival.QuadPart - start.QuadPart) * 1'000'000.0 / (double)frequency.QuadPart);
        }

        Sleep(2);
    }

    g_log = realLog;

    if (!quiet.empty())
    {
        std::sort(quiet.begin(), quiet.end());
        Log(L"RESULT roundtrip_no_logging_median_us=%.1f", quiet[quiet.size() / 2]);
        Log(L"RESULT roundtrip_no_logging_p90_us=%.1f", quiet[(size_t)(quiet.size() * 0.9)]);
    }

    g_benchWatching = false;
    UnhookWindowsHookEx(g_hook);
    g_hook = nullptr;

    if (samples.empty())
    {
        Log(L"RESULT roundtrip=no samples, missed=%d", missed);
        if (g_log) { std::fclose(g_log); g_log = nullptr; }
        return 1;
    }

    std::sort(samples.begin(), samples.end());
    const double median = samples[samples.size() / 2];
    const double p90 = samples[(size_t)(samples.size() * 0.9)];
    const double best = samples.front();
    const double worst = samples.back();

    Log(L"RESULT roundtrip_median_us=%.1f", median);
    Log(L"RESULT roundtrip_best_us=%.1f", best);
    Log(L"RESULT roundtrip_p90_us=%.1f", p90);
    Log(L"RESULT roundtrip_worst_us=%.1f", worst);
    Log(L"RESULT roundtrip_missed=%d", missed);

    if (g_log) { std::fclose(g_log); g_log = nullptr; }
    return 0;
}

// Measures the cost of the paths that run inside the keyboard hook.
//
// Everything here happens between a physical keystroke arriving and the hook
// returning, so any time spent is time the game is waiting. The numbers are taken
// with QueryPerformanceCounter over many iterations, and reported in microseconds.
int RunLatencyBenchmark(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);

    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);

    auto measure = [&frequency](int iterations, const std::function<void()>& action)
    {
        // A warm-up pass, so first-call costs such as page faults do not land in
        // the measurement.
        for (int i = 0; i < 20; ++i)
        {
            action();
        }

        LARGE_INTEGER start{};
        LARGE_INTEGER end{};
        QueryPerformanceCounter(&start);

        for (int i = 0; i < iterations; ++i)
        {
            action();
        }

        QueryPerformanceCounter(&end);

        const double totalMicroseconds =
            (double)(end.QuadPart - start.QuadPart) * 1'000'000.0 / (double)frequency.QuadPart;

        return totalMicroseconds / iterations;
    };

    Profile profile;
    profile.target = L"benchmark";
    AddPair(profile, 0x1E, 0x20);

    KeyState probe;
    probe.scan = 0x20;
    probe.vk = 'D';
    probe.extended = false;

    Log(L"--- latency benchmark ---");

    // 1. The hook's own bookkeeping, with no logging and no injection.
    auto bookkeeping = [&profile]()
    {
        HandleKey(profile, 0x20, true);
        HandleKey(profile, 0x20, false);
    };

    // 2. One injected event through SendInput, as a single call with the queue
    //    given time to drain. Back-to-back calls would measure throughput rather
    //    than the cost of one event, which is what the hook actually pays.
    auto injectOnly = [&probe]()
    {
        InjectKey(probe, false);
    };

    // 3. One log line, which is what the hook currently does on every keystroke.
    auto logLine = []() { Log(L"benchmark line"); };

    const int iterations = 2000;

    // Reported as each is taken, so a failure later cannot hide what was already
    // measured.
    const double bookkeepingUs = measure(iterations, bookkeeping);
    Log(L"RESULT bookkeeping_us=%.2f", bookkeepingUs);

    // Measured one at a time with a pause between, so each call meets an empty
    // queue. The median of the samples is reported, which ignores the occasional
    // scheduling spike.
    std::vector<double> samples;
    samples.reserve(200);

    for (int i = 0; i < 200; ++i)
    {
        LARGE_INTEGER start{};
        LARGE_INTEGER end{};
        QueryPerformanceCounter(&start);
        InjectKey(probe, false);
        QueryPerformanceCounter(&end);
        samples.push_back((double)(end.QuadPart - start.QuadPart) * 1'000'000.0 / (double)frequency.QuadPart);
        Sleep(2);
    }

    UINT accepted = 0;
    {
        INPUT check{};
        check.type = INPUT_KEYBOARD;
        check.ki.wVk = probe.vk;
        check.ki.wScan = probe.scan;
        check.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
        accepted = SendInput(1, &check, sizeof(INPUT));
    }

    // Read the error only on failure: after a successful call whatever is in
    // GetLastError is left over from something else entirely.
    Log(L"RESULT sendinput_accepted=%u%s", accepted,
        accepted == 0 ? L" (rejected)" : L"");

    std::sort(samples.begin(), samples.end());
    const double injectUs = samples[samples.size() / 2];
    const double injectWorstUs = samples.back();

    Log(L"RESULT inject_median_us=%.2f", injectUs);
    Log(L"RESULT inject_worst_us=%.2f", injectWorstUs);

    const double logUs = measure(iterations, logLine);
    Log(L"RESULT logline_us=%.2f", logUs);


    Log(L"RESULT press_with_log_us=%.2f", bookkeepingUs + injectUs + logUs);
    Log(L"RESULT press_no_log_us=%.2f", bookkeepingUs + injectUs);

    if (g_log != nullptr)
    {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return 0;
}

// Verifies pairs that share a key, which is the case ONEKEY allowed and this
// engine has to keep supporting.
//
// With W:S and W:A, holding W and pressing A must release W, and W must remain a
// single managed key with two opponents. Listing the same pair twice must not
// duplicate the opponent, or a duplicate release would be injected.
int RunSharedKeySelfTest(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);
    CapturedSendInputScope capture;

    Profile profile;
    AddPair(profile, 0x11, 0x1F); // W : S
    AddPair(profile, 0x11, 0x1E); // W : A   (W is in two pairs)
    AddPair(profile, 0x11, 0x1E); // the same pair again, on purpose

    constexpr WORD keyW = 0x11;
    constexpr WORD keyS = 0x1F;
    constexpr WORD keyA = 0x1E;

    int failures = 0;
    auto check = [&](const wchar_t* what, bool ok)
    {
        if (!ok)
        {
            failures++;
        }

        Log(L"%s  %s", ok ? L"pass" : L"FAIL", what);
    };

    check(L"W is managed", profile.managedScanCodes.contains(keyW));
    check(L"W has exactly two opponents", profile.opponents[keyW].size() == 2);
    check(L"S and A are both opponents of W",
          std::find(profile.opponents[keyW].begin(), profile.opponents[keyW].end(), keyS) != profile.opponents[keyW].end() &&
          std::find(profile.opponents[keyW].begin(), profile.opponents[keyW].end(), keyA) != profile.opponents[keyW].end());
    check(L"A has W as its only opponent", profile.opponents[keyA].size() == 1);

    profile.states[keyW].vk = 'W';
    profile.states[keyS].vk = 'S';
    profile.states[keyA].vk = 'A';

    // Hold W, then press A: the release has to reach W, not S.
    HandleKey(profile, keyW, true);
    HandleKey(profile, keyA, true);

    const KeyState& w = profile.states[keyW];
    const KeyState& a = profile.states[keyA];
    check(L"holding W then pressing A releases W", w.held && !w.scriptDown);
    check(L"A is held", a.held && a.scriptDown);

    // Releasing A must restore W.
    HandleKey(profile, keyA, false);
    check(L"releasing A restores W", profile.states[keyW].scriptDown);

    // Hold W, then press S: same behaviour through the other pair.
    HandleKey(profile, keyW, false);
    HandleKey(profile, keyW, true);
    HandleKey(profile, keyS, true);
    check(L"holding W then pressing S releases W", profile.states[keyW].held && !profile.states[keyW].scriptDown);

    // W may be part of more than one pair. If S and A are both held, releasing
    // just one of them must not restore W until the final opposing key is up.
    HandleKey(profile, keyA, true);
    HandleKey(profile, keyS, false);
    check(L"releasing one of two opponents keeps W suppressed",
          profile.states[keyW].held && !profile.states[keyW].scriptDown);
    HandleKey(profile, keyA, false);
    check(L"releasing final opponent restores W", profile.states[keyW].scriptDown);

    Log(L"shared key selftest: %d failed", failures);

    if (g_log != nullptr)
    {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return failures == 0 ? 0 : 1;
}

// Verifies both the legacy name match and the exact-path match used by modern
// profiles. Exact paths prevent separate clients with the same executable name
// from arming each other's key pairs.
//
// The interface stores what Windows calls the process ("cs2"), while
// QueryFullProcessImageNameW returns the file ("cs2.exe"). Comparing them
// verbatim never matched, so no profile was ever armed and the engine looked
// broken while the keyboard behaved normally.
int RunTargetSelfTest(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);

    struct Case
    {
        const wchar_t* file;
        const wchar_t* path;
        const wchar_t* target;
        const wchar_t* targetPath;
        bool expected;
    };

    const Case cases[] =
    {
        {L"TslGame.exe", L"C:\\Games\\PUBG\\TslGame.exe", L"TslGame", L"", true},
        {L"TslGame.exe", L"C:\\Games\\PUBG\\TslGame.exe", L"TslGame.exe", L"", true},
        {L"cs2.exe", L"D:\\Steam\\cs2.exe", L"CS2", L"", true},
        {L"TslGame.exe", L"C:\\Games\\PUBG\\TslGame.exe", L"csgo", L"", false},
        {L"cs2.exe", L"D:\\Steam\\cs2.exe", L"cs", L"", false},
        {L"GTA5.exe", L"C:\\ClientA\\GTA5.exe", L"GTA5", L"C:\\ClientA\\GTA5.exe", true},
        {L"GTA5.exe", L"C:\\ClientB\\GTA5.exe", L"GTA5", L"C:\\ClientA\\GTA5.exe", false},
        {L"GTA5.exe", L"C:\\ClientA\\GTA5.exe", L"GTA5", L"c:/clienta/gta5.exe", true},
    };

    int failures = 0;

    for (const Case& test : cases)
    {
        Profile profile;
        profile.target = test.target;
        profile.targetPath = test.targetPath;
        const bool actual = MatchesProfile(test.file, test.path, profile);
        const bool ok = actual == test.expected;
        if (!ok)
        {
            failures++;
        }

        Log(L"%s  file=\"%s\" path=\"%s\" vs target=\"%s\" targetPath=\"%s\" -> %d (expected %d)",
            ok ? L"pass" : L"FAIL", test.file, test.path, test.target, test.targetPath,
            actual ? 1 : 0, test.expected ? 1 : 0);
    }

    Log(L"target selftest: %zu cases, %d failed", std::size(cases), failures);

    if (g_log != nullptr)
    {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return failures == 0 ? 0 : 1;
}

// Verifies the blocking state machine, which lives in the hook rather than in
// HandleKey, so it is exercised through the same helper the hook uses.
int RunBlockingSelfTest(const std::wstring& logPath)
{
    OpenLogFile(logPath, &g_log);
    CapturedSendInputScope capture;

    Profile profile;
    profile.target = L"selftest";
    profile.mode = EngineMode::Blocking;
    AddPair(profile, 0x1E, 0x20); // A and D

    const WORD keyA = 0x1E;
    const WORD keyD = 0x20;

    profile.states[keyA].vk = 'A';
    profile.states[keyD].vk = 'D';

    auto show = [&](const wchar_t* stage)
    {
        const KeyState& a = profile.states[keyA];
        const KeyState& d = profile.states[keyD];
        Log(L"%-24s  A[held=%d seen=%d]  D[held=%d seen=%d]",
            stage,
            a.held ? 1 : 0, a.scriptDown ? 1 : 0,
            d.held ? 1 : 0, d.scriptDown ? 1 : 0);
    };

    int failures = 0;
    auto check = [&](const wchar_t* what, bool ok)
    {
        if (!ok)
        {
            ++failures;
        }
        Log(L"%s  %s", ok ? L"pass" : L"FAIL", what);
    };

    Log(L"blocking selftest start  pair A(30):D(32)");

    // The hook path: both events are swallowed and replaced by injections, so
    // "held" and "seen" move together.
    HandleBlocking(profile, keyA, true);
    show(L"press A");

    HandleBlocking(profile, keyD, true);
    show(L"press D (opposes A)");

    HandleBlocking(profile, keyD, false);
    show(L"release D");

    HandleBlocking(profile, keyA, false);
    show(L"release A");

    // If Windows rejects the synthetic replacement for a first physical down,
    // Blocking must let that genuine keydown through rather than lose it.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    g_testFailOnSendInputCall = 1;
    {
        Profile failedDown = MakeHybridTestProfile();
        failedDown.mode = EngineMode::Blocking;
        const HookAction action = HandleBlocking(failedDown, keyA, true);
        check(L"blocking passes a rejected replacement keydown",
              action == HookAction::Pass &&
              failedDown.states[keyA].held && failedDown.states[keyA].scriptDown &&
              g_capturedInjections.empty());
    }

    // If an injected keyup is rejected, the corresponding native keyup is the
    // fallback. The state must follow that native event and not remain stuck.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    g_testFailOnSendInputCall = 0;
    {
        Profile failedUp = MakeHybridTestProfile();
        failedUp.mode = EngineMode::Blocking;
        HandleBlocking(failedUp, keyA, true);
        g_testFailOnSendInputCall = g_testSendInputCalls + 1;
        const HookAction action = HandleBlocking(failedUp, keyA, false);
        check(L"blocking passes a rejected replacement keyup",
              action == HookAction::Pass &&
              !failedUp.states[keyA].held && !failedUp.states[keyA].scriptDown);
    }

    // A failed restoration is retried on the next physical repeat. The repeat
    // remains swallowed only after its synthetic restoration succeeds.
    g_capturedInjections.clear();
    g_testSendInputCalls = 0;
    g_testFailOnSendInputCall = 0;
    {
        Profile retry = MakeHybridTestProfile();
        retry.mode = EngineMode::Blocking;
        HandleBlocking(retry, keyD, true);
        HandleBlocking(retry, keyA, true);
        g_testFailOnSendInputCall = g_testSendInputCalls + 1;
        const HookAction failedUpAction = HandleBlocking(retry, keyA, false);
        g_testFailOnSendInputCall = 0;
        const HookAction repeatAction = HandleBlocking(retry, keyD, true);

        check(L"blocking repeat repairs a failed restore",
              failedUpAction == HookAction::Pass &&
              repeatAction == HookAction::Swallow && retry.states[keyD].scriptDown);
    }

    Log(L"blocking selftest: %d failed", failures);

    if (g_log != nullptr)
    {
        std::fclose(g_log);
        g_log = nullptr;
    }

    return failures == 0 ? 0 : 1;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (EqualsIgnoreCase(arg, L"--profile") && i + 1 < argc)
        {
            ParseProfile(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--debug"))
        {
            g_debug = true;
        }
        else if (EqualsIgnoreCase(arg, L"--stop-event") && i + 1 < argc)
        {
            g_shutdownEventName = argv[++i];
        }
        else if (EqualsIgnoreCase(arg, L"--mode") && i + 1 < argc)
        {
            // Method used by profiles that do not name one. Parsed as a template
            // only; every real profile carries its own, so changing it here can
            // never silently retune a game that was configured explicitly.
            g_mode = ParseModeName(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--selftest") && i + 1 < argc)
        {
            return RunSelfTest(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--selftest-blocking") && i + 1 < argc)
        {
            return RunBlockingSelfTest(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--selftest-targets") && i + 1 < argc)
        {
            return RunTargetSelfTest(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--selftest-shared") && i + 1 < argc)
        {
            return RunSharedKeySelfTest(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--selftest-hybrid") && i + 1 < argc)
        {
            return RunHybridSelfTest(argv[++i]);
        }
        else if (EqualsIgnoreCase(arg, L"--benchmark") && i + 1 < argc)
        {
            const std::wstring target = argv[++i];
            RunLatencyBenchmark(target);
            return RunHookRoundTripBenchmark(target);
        }
    }
    LocalFree(argv);

    if (g_profiles.empty())
    {
        Log(L"no profiles given, exiting");
        if (g_log)
        {
            std::fclose(g_log);
        }
        return 0;
    }

    Log(L"worker start  profiles=%zu  default=%s", g_profiles.size(), ModeName(g_mode));
    for (const auto& profile : g_profiles)
    {
        Log(L"  profile \"%s\"  method=%s  keys=%zu",
            profile.target.c_str(), ModeName(profile.mode), profile.managedScanCodes.size());
    }

    g_instance = instance;
    g_mainThreadId = GetCurrentThreadId();

    // Foreground and shutdown watchers use PostThreadMessage. Make this thread's
    // queue exist before any watcher can post to it.
    MSG queued{};
    PeekMessageW(&queued, nullptr, WM_USER, WM_USER, PM_NOREMOVE);

    if (!CreateRawInputWindow())
    {
        return 1;
    }

    // A named event lets the interface ask for a clean stop, so keys are released
    // before the process ends.
    if (g_shutdownEventName.empty())
    {
        g_shutdownEventName = L"Local\\SnapHookWorkerStop";
    }

    g_shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, g_shutdownEventName.c_str());
    g_shutdownCompleteEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_foregroundRefreshEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    g_activeProfile.store(-1, std::memory_order_release);

    // Reported before the first switch, because SwitchProfile stays quiet when the
    // state does not change and both start at "nothing".
    {
        const ForegroundInfo startup = DescribeForeground();
        LogForeground(L"STARTUP", startup);
        Log(L"STARTUP    %zu profile(s) configured", g_profiles.size());
        SwitchProfile(startup.matchIndex);
    }

    g_foregroundHook = SetWinEventHook(
        EVENT_SYSTEM_FOREGROUND,
        EVENT_SYSTEM_FOREGROUND,
        nullptr,
        ForegroundEventProc,
        0,
        0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

    if (g_shutdownEvent != nullptr)
    {
        g_shutdownWatchdogThread = std::thread(ShutdownWatchdog, nullptr);
        g_foregroundPollThread = std::thread(ForegroundPollLoop);
    }

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        if (message.message == kRefreshForegroundMessage)
        {
            g_foregroundRefreshPosted.store(false, std::memory_order_release);
            if (!g_stopping)
            {
                const int next = static_cast<int>(static_cast<INT_PTR>(message.wParam));
                const HWND observedRoot = reinterpret_cast<HWND>(message.lParam);
                ApplyForegroundRefreshOnMessageThread(next, observedRoot);
            }
            continue;
        }

        if (message.message == kReleaseAndQuitMessage)
        {
            ShutdownOnMessageThread();
            PostQuitMessage(0);
            continue;
        }

        if (message.message == kRestoreBlockingMessage)
        {
            RetryBlockingRestoreOnMessageThread(
                static_cast<int>(message.wParam),
                static_cast<WORD>(message.lParam));
            continue;
        }

        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    ShutdownOnMessageThread();

    if (g_shutdownEvent != nullptr)
    {
        SetEvent(g_shutdownEvent);
    }
    if (g_foregroundPollThread.joinable())
    {
        g_foregroundPollThread.join();
    }
    if (g_shutdownWatchdogThread.joinable())
    {
        g_shutdownWatchdogThread.join();
    }
    if (g_shutdownCompleteEvent != nullptr)
    {
        CloseHandle(g_shutdownCompleteEvent);
        g_shutdownCompleteEvent = nullptr;
    }
    if (g_foregroundRefreshEvent != nullptr)
    {
        CloseHandle(g_foregroundRefreshEvent);
        g_foregroundRefreshEvent = nullptr;
    }
    if (g_shutdownEvent != nullptr)
    {
        CloseHandle(g_shutdownEvent);
        g_shutdownEvent = nullptr;
    }

    Log(L"worker stop");
    if (g_log)
    {
        std::fclose(g_log);
    }
    return 0;
}
