using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayerInputDriver;

internal static class Program
{
    // ReSharper disable InconsistentNaming
    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int VK_SHIFT = 0x10;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_ACTIVATE = 0x0006;
    private const int WA_ACTIVE = 1;
    private const int WA_CLICKACTIVE = 2;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x0002;
    private static readonly IntPtr HWND_TOP = new(0);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    // ReSharper restore InconsistentNaming

    private static readonly Dictionary<string, ushort> s_namedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RETURN"] = 0x0D,
        ["ENTER"] = 0x0D,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20,
        ["TAB"] = 0x09,
        ["BACK"] = 0x08,
        ["UP"] = 0x26,
        ["DOWN"] = 0x28,
        ["LEFT"] = 0x25,
        ["RIGHT"] = 0x27,
        ["W"] = 0x57,
        ["A"] = 0x41,
        ["S"] = 0x53,
        ["D"] = 0x44,
        ["C"] = 0x43,
        ["X"] = 0x58,
        ["E"] = 0x45,
        ["F"] = 0x46,
        ["T"] = 0x54,
        ["M"] = 0x4D,
        ["1"] = 0x31,
        ["2"] = 0x32,
        ["3"] = 0x33,
        ["4"] = 0x34,
        ["5"] = 0x35,
        ["6"] = 0x36,
        ["7"] = 0x37,
        ["8"] = 0x38,
        ["9"] = 0x39,
        ["0"] = 0x30,
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
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
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static IntPtr s_hwnd = IntPtr.Zero;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return Execute(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PlayerInputDriver - send low-level keyboard/mouse input to Stardew Valley.");
        Console.WriteLine();
        Console.WriteLine("Usage: PlayerInputDriver <command> [args...]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  wait [timeoutMs]          Wait for the game window and print its handle.");
        Console.WriteLine("  focus                     Focus the game window.");
        Console.WriteLine("  chat <text>               Open chat (T), type text, press Enter.");
        Console.WriteLine("  key <name>                Press and release a named key.");
        Console.WriteLine("  hold <name> <ms>          Hold a named key for the given milliseconds.");
        Console.WriteLine("  move <dir> <ms>           Hold W/A/S/D for the given milliseconds.");
        Console.WriteLine("  click [left|right] [x y]  Click at client position or center.");
        Console.WriteLine("  postkey <name>            Send a key via PostMessage (fallback for games that ignore SendInput).");
        Console.WriteLine("  sleep <ms>                Sleep.");
        Console.WriteLine("  script <file>             Execute commands from a file (one per line).");
        Console.WriteLine();
        Console.WriteLine("Named keys: RETURN, ESC, SPACE, W, A, S, D, C, X, E, F, T, M, 1..0, F1..F12, arrows.");
    }

    private static int Execute(string[] args)
    {
        var command = args[0].ToLowerInvariant();

        if (command == "script")
        {
            if (args.Length < 2)
                throw new InvalidOperationException("Usage: script <file>");
            return RunScript(args[1]);
        }

        EnsureWindow();

        switch (command)
        {
            case "wait":
                Console.WriteLine($"Window found: {s_hwnd}");
                return 0;
            case "focus":
                FocusWindow();
                return 0;
            case "chat":
                {
                    var text = string.Join(" ", args.Skip(1));
                    SendChat(text);
                    return 0;
                }
            case "key":
                {
                    if (args.Length < 2) throw new InvalidOperationException("Usage: key <name>");
                    var vk = ResolveKey(args[1]);
                    PressKey(vk);
                    return 0;
                }
            case "postkey":
                {
                    if (args.Length < 2) throw new InvalidOperationException("Usage: postkey <name>");
                    var vk = ResolveKey(args[1]);
                    PostKey(vk);
                    return 0;
                }
            case "hold":
                {
                    if (args.Length < 3 || !int.TryParse(args[2], out var ms))
                        throw new InvalidOperationException("Usage: hold <name> <ms>");
                    var vk = ResolveKey(args[1]);
                    HoldKey(vk, ms);
                    return 0;
                }
            case "move":
                {
                    if (args.Length < 3 || !int.TryParse(args[2], out var ms))
                        throw new InvalidOperationException("Usage: move <dir> <ms>");
                    var vk = ResolveDirection(args[1]);
                    HoldKey(vk, ms);
                    return 0;
                }
            case "click":
                {
                    var button = MouseButton.Left;
                    int? x = null;
                    int? y = null;
                    var index = 1;
                    if (index < args.Length && (args[index].Equals("left", StringComparison.OrdinalIgnoreCase) || args[index].Equals("right", StringComparison.OrdinalIgnoreCase)))
                    {
                        button = args[index].Equals("right", StringComparison.OrdinalIgnoreCase) ? MouseButton.Right : MouseButton.Left;
                        index++;
                    }
                    if (index + 1 < args.Length && int.TryParse(args[index], out var px) && int.TryParse(args[index + 1], out var py))
                    {
                        x = px;
                        y = py;
                        index += 2;
                    }
                    Click(button, x, y);
                    return 0;
                }
            case "sleep":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var ms))
                        throw new InvalidOperationException("Usage: sleep <ms>");
                    Thread.Sleep(ms);
                    return 0;
                }
            default:
                throw new InvalidOperationException($"Unknown command: {command}");
        }
    }

    private static int RunScript(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Script not found: {path}");

        var lines = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        Console.WriteLine($"Executing {lines.Count} script lines from {path}");

        foreach (var line in lines)
        {
            Console.WriteLine($"> {line}");
            var parts = SplitLine(line);
            if (parts.Length == 0) continue;

            // Some commands do not need a window (sleep, wait), but most do.
            var cmd = parts[0].ToLowerInvariant();
            if (cmd != "sleep" && cmd != "script")
            {
                EnsureWindow();
            }

            _ = Execute(parts);
        }

        return 0;
    }

    private static string[] SplitLine(string line)
    {
        // Very simple splitter; enough for our script syntax.
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void EnsureWindow()
    {
        if (s_hwnd != IntPtr.Zero && IsWindowValid(s_hwnd))
            return;

        s_hwnd = FindGameWindow();
        if (s_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not find the Stardew Valley / SMAPI game window.");

        _ = GetClientRect(s_hwnd, out var rect);
        Console.WriteLine($"Game window: {s_hwnd}, client size {rect.Right}x{rect.Bottom}");
    }

    private static bool IsWindowValid(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IntPtr FindGameWindow()
    {
        // First try the main window of the SMAPI / game process.
        // When multiple instances are running (host + farmhand), prefer the farmhand window
        // by looking for "[farmhand]" in the title, otherwise fall back to the first match.
        IntPtr fallback = IntPtr.Zero;
        foreach (var name in new[] { "StardewModdingAPI", "Stardew Valley" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                var hWnd = process.MainWindowHandle;
                if (hWnd == IntPtr.Zero)
                    continue;

                if (fallback == IntPtr.Zero)
                    fallback = hWnd;

                var title = GetWindowTitle(hWnd);
                if (title.Contains("[farmhand]", StringComparison.OrdinalIgnoreCase))
                    return hWnd;
            }
        }

        if (fallback != IntPtr.Zero)
            return fallback;

        // Fallback: enumerate windows owned by the SMAPI process.
        var result = IntPtr.Zero;
        _ = EnumWindows((hWnd, _lParam) =>
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var processName = process.ProcessName;
                if (processName.Contains("StardewModdingAPI", StringComparison.OrdinalIgnoreCase) ||
                    processName.Contains("Stardew Valley", StringComparison.OrdinalIgnoreCase))
                {
                    if (result == IntPtr.Zero)
                        result = hWnd;

                    var title = GetWindowTitle(hWnd);
                    if (title.Contains("[farmhand]", StringComparison.OrdinalIgnoreCase))
                    {
                        result = hWnd;
                        return false; // stop enumeration
                    }
                }
            }
            catch (ArgumentException)
            {
                // process exited
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void MinimizeSmapiConsole()
    {
        var consoleHwnd = FindSmapiConsoleWindow();
        if (consoleHwnd != IntPtr.Zero)
        {
            _ = ShowWindow(consoleHwnd, SW_MINIMIZE);
            Thread.Sleep(100);
        }
    }

    private static IntPtr FindSmapiConsoleWindow()
    {
        var result = IntPtr.Zero;
        _ = EnumWindows((hWnd, _lParam) =>
        {
            if (!IsWindow(hWnd) || hWnd == s_hwnd)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hWnd, out var pid);
            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                return true;
            }

            // The game window belongs to the StardewModdingAPI/Stardew Valley process.
            // The SMAPI console is typically hosted by WindowsTerminal or cmd.
            if (processName.Contains("StardewModdingAPI", StringComparison.OrdinalIgnoreCase) ||
                processName.Contains("Stardew Valley", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = new System.Text.StringBuilder(256);
            _ = GetWindowText(hWnd, title, title.Capacity);
            var titleStr = title.ToString();
            if (titleStr.Contains("SMAPI", StringComparison.OrdinalIgnoreCase) &&
                titleStr.Contains("Stardew", StringComparison.OrdinalIgnoreCase))
            {
                result = hWnd;
                return false; // stop enumeration
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static void FocusWindow()
    {
        // Remove the foreground-lock timeout so a background process can steal focus.
        _ = SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);

        _ = ShowWindow(s_hwnd, SW_RESTORE);

        // Minimize the SMAPI console window so it cannot steal focus or obscure the game.
        MinimizeSmapiConsole();

        // SwitchToThisWindow works even when SetForegroundWindow is blocked.
        SwitchToThisWindow(s_hwnd, false);
        Thread.Sleep(100);

        // Attach to the foreground thread so SetForegroundWindow succeeds from a background process.
        var foregroundHwnd = GetForegroundWindow();
        _ = GetWindowThreadProcessId(s_hwnd, out var gameThreadId);
        _ = GetWindowThreadProcessId(foregroundHwnd, out var foregroundThreadId);
        if (foregroundThreadId != gameThreadId && foregroundThreadId != 0 && gameThreadId != 0)
        {
            _ = AttachThreadInput(foregroundThreadId, gameThreadId, true);
        }

        _ = SetForegroundWindow(s_hwnd);

        if (foregroundThreadId != gameThreadId && foregroundThreadId != 0 && gameThreadId != 0)
        {
            _ = AttachThreadInput(foregroundThreadId, gameThreadId, false);
        }

        // Bring window to the top of the Z order so it is not obscured by the SMAPI console.
        _ = SetWindowPos(s_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Thread.Sleep(100);
        _ = SetWindowPos(s_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

        Thread.Sleep(200);

        // Force the game window to process WM_ACTIVATE so FNA/SDL2 sets IsActive=true.
        // SetForegroundWindow may succeed at the Win32 level, but SDL2's internal
        // activation tracking sometimes doesn't update until the message is processed.
        _ = PostMessage(s_hwnd, WM_ACTIVATE, new IntPtr(WA_ACTIVE), IntPtr.Zero);
        Thread.Sleep(200);

        // Best-effort verification: log focus state but do not fail — SDL2 activation
        // via PostMessage is what actually matters for receiving input.
        var actualForeground = GetForegroundWindow();
        if (actualForeground != s_hwnd)
        {
            Console.WriteLine($"[INFO] Foreground window {actualForeground} differs from game window {s_hwnd}; attempting mouse-click activation.");
            ActivateByClick();
            actualForeground = GetForegroundWindow();
            if (actualForeground == s_hwnd)
            {
                Console.WriteLine($"[OK] Game window activated by mouse click: {s_hwnd}");
            }
            else
            {
                Console.WriteLine($"[INFO] Foreground window {actualForeground} still differs from game window {s_hwnd}; SDL2 activation sent anyway.");
            }
        }
        else
        {
            Console.WriteLine($"[OK] Game window is foreground: {s_hwnd}");
        }
    }

    private static void ActivateByClick()
    {
        _ = GetClientRect(s_hwnd, out var clientRect);
        var pt = new POINT { X = clientRect.Right / 2, Y = clientRect.Bottom / 2 };
        _ = ClientToScreen(s_hwnd, ref pt);

        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        var absX = (pt.X * 65535) / screenWidth;
        var absY = (pt.Y * 65535) / screenHeight;

        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new INPUTUNION { mi = new MOUSEINPUT { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new INPUTUNION { mi = new MOUSEINPUT { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE } }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                U = new INPUTUNION { mi = new MOUSEINPUT { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE } }
            }
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Console.WriteLine("[WARN] Mouse activation click SendInput failed.");
        }
        else
        {
            Thread.Sleep(300);
        }
    }

    private static void SendChat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        FocusWindow();

        // Open chat with T.
        PressKey(0x54);
        Thread.Sleep(150);

        // Type the command text.
        foreach (var ch in text)
        {
            if (ch == ' ')
            {
                PressKey(0x20);
            }
            else if (ch >= 'a' && ch <= 'z')
            {
                PressKey((ushort)(0x41 + (ch - 'a')));
            }
            else if (ch >= 'A' && ch <= 'Z')
            {
                PressKey((ushort)(0x41 + (ch - 'A')));
            }
            else if (ch >= '0' && ch <= '9')
            {
                PressKey((ushort)(0x30 + (ch - '0')));
            }
            else if (ch == '/')
            {
                PressKey(0xBF); // VK_OEM_2
            }
            else if (ch == '_')
            {
                PressShiftedKey(0xBD); // Shift + VK_OEM_MINUS
            }
            else if (ch == '-')
            {
                PressKey(0xBD);
            }
            else
            {
                // Unknown character; skip.
            }
        }

        Thread.Sleep(100);
        PressKey(0x0D); // Enter
        Thread.Sleep(100);
    }

    private static ushort ResolveKey(string name)
    {
        if (s_namedKeys.TryGetValue(name, out var vk))
            return vk;
        throw new InvalidOperationException($"Unknown key name: {name}");
    }

    private static ushort ResolveDirection(string dir)
    {
        return dir.ToUpperInvariant() switch
        {
            "W" or "UP" => 0x57,
            "A" or "LEFT" => 0x41,
            "S" or "DOWN" => 0x53,
            "D" or "RIGHT" => 0x44,
            _ => throw new InvalidOperationException($"Unknown direction: {dir}"),
        };
    }

    private static void PressKey(ushort vk)
    {
        PostKey(vk);
    }

    private static void PostKey(ushort vk)
    {
        PostKeyDown(vk);
        Thread.Sleep(50);
        PostKeyUp(vk);
        Thread.Sleep(50);
    }

    private static void PostKeyDown(ushort vk)
    {
        var (down, _) = BuildKeyParams(vk, false);
        _ = PostMessage(s_hwnd, WM_KEYDOWN, new IntPtr(vk), down);
    }

    private static void PostKeyUp(ushort vk)
    {
        var (_, up) = BuildKeyParams(vk, true);
        _ = PostMessage(s_hwnd, WM_KEYUP, new IntPtr(vk), up);
    }

    private static (IntPtr down, IntPtr up) BuildKeyParams(ushort vk, bool up)
    {
        // Build a realistic lParam so SDL2/FNA can extract the scan code and extended flag.
        var scan = (uint)MapVirtualKey(vk, 0);
        var extended = (scan & 0xE000) != 0 ? 0x01000000u : 0u;
        scan &= 0xFF;

        var repeatCount = 1u;
        var lParamDown = (IntPtr)((scan << 16) | extended | repeatCount);
        var lParamUp = (IntPtr)((scan << 16) | extended | repeatCount | 0xC0000000u);
        return up ? (lParamDown, lParamUp) : (lParamDown, lParamUp);
    }

    private static void PressShiftedKey(ushort vk)
    {
        PostKeyDown(VK_SHIFT);
        Thread.Sleep(30);
        PostKeyDown(vk);
        Thread.Sleep(30);
        PostKeyUp(vk);
        Thread.Sleep(30);
        PostKeyUp(VK_SHIFT);
        Thread.Sleep(30);
    }

    private static void HoldKey(ushort vk, int milliseconds)
    {
        PostKeyDown(vk);
        Thread.Sleep(Math.Max(0, milliseconds));
        PostKeyUp(vk);
        Thread.Sleep(50);
    }

    private static void SendKey(ushort vk, bool up)
    {
        // Legacy virtual-key SendInput path. Kept for callers that explicitly need it;
        // most keyboard input now uses PostMessage because FNA/SDL2 on this setup does
        // not receive the synthetic WM_KEYDOWN messages generated by SendInput.
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            throw new InvalidOperationException($"SendInput failed for key 0x{vk:X} {(up ? "up" : "down")}.");
    }

    private enum MouseButton { Left, Right }

    private static void Click(MouseButton button, int? x, int? y)
    {
        FocusWindow();

        _ = GetClientRect(s_hwnd, out var clientRect);
        var px = x ?? clientRect.Right / 2;
        var py = y ?? clientRect.Bottom / 2;

        // Convert client coordinates to absolute normalized (0..65535).
        var pt = new POINT { X = px, Y = py };
        _ = ClientToScreen(s_hwnd, ref pt);

        var screenWidth = GetSystemMetrics(0); // SM_CXSCREEN
        var screenHeight = GetSystemMetrics(1); // SM_CYSCREEN
        var absX = (pt.X * 65535) / screenWidth;
        var absY = (pt.Y * 65535) / screenHeight;

        var downFlag = button == MouseButton.Left ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_RIGHTDOWN;
        var upFlag = button == MouseButton.Left ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_RIGHTUP;

        var moveInput = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                },
            },
        };

        var downInput = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = downFlag | MOUSEEVENTF_ABSOLUTE,
                },
            },
        };

        var upInput = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = upFlag | MOUSEEVENTF_ABSOLUTE,
                },
            },
        };

        var inputs = new[] { moveInput, downInput, upInput };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            throw new InvalidOperationException("SendInput failed for mouse click.");

        Thread.Sleep(100);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
