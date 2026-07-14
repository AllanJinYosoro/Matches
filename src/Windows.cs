using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class KeyboardHook : IDisposable
{
    private const int KeyboardLowLevel = 13;
    private const int KeyDown = 0x0100;
    private const int SystemKeyDown = 0x0104;
    private const int KeyUp = 0x0101;
    private const int SystemKeyUp = 0x0105;
    private readonly Action activated;
    private readonly Native.LowLevelKeyboardProc callback;
    private IntPtr hook;
    private bool controlDown;
    private DateTime lastControl = DateTime.MinValue;

    internal KeyboardHook(Action activation)
    {
        activated = activation;
        callback = Handle;
        hook = Native.SetWindowsHookEx(KeyboardLowLevel, callback, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = (Keys)Marshal.ReadInt32(data);
            var down = message.ToInt32() == KeyDown || message.ToInt32() == SystemKeyDown;
            var up = message.ToInt32() == KeyUp || message.ToInt32() == SystemKeyUp;
            var control = key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey;
            if (control && down && !controlDown)
            {
                controlDown = true;
                var now = DateTime.UtcNow;
                if ((now - lastControl).TotalMilliseconds <= 400)
                {
                    lastControl = DateTime.MinValue;
                    activated();
                }
                else lastControl = now;
            }
            else if (control && up) controlDown = false;
            else if (!control && down) lastControl = DateTime.MinValue;
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }
}

internal static class Native
{
    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        internal int Width;
        internal int Height;

        internal NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint from, uint to, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    internal static void FocusWindow(IntPtr window, IntPtr control)
    {
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(window);
            BringWindowToTop(window);
            SetFocus(control);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr value);
}
