// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using QuickLook.Common.NativeMethods;
using QuickLook.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace QuickLook;

internal class KeystrokeDispatcher : IDisposable
{
    private static KeystrokeDispatcher _instance;

    private static HashSet<Keys> _validKeys;

    private GlobalKeyboardHook _hook;
    private nint _winEventHook;
    private User32.WinEventProc _winEventProc; // keep reference to prevent GC
    private bool _isPreviewRequest;
    private bool _spaceIsDown;
    private long _spaceHoldTick;
    private long _lastInvalidKeyPressTick;

    private const long HOLD_TO_PREVIEW_DURATION = TimeSpan.TicksPerMillisecond * 750;
    private const long VALID_KEY_PRESS_DELAY = TimeSpan.TicksPerSecond * 1;

    protected KeystrokeDispatcher()
    {
        InstallKeyHook(KeyDownEventHandler, KeyUpEventHandler);
        InstallForegroundWindowHook();

        _validKeys =
        [
            Keys.Up, Keys.Down, Keys.Left, Keys.Right,
            Keys.Enter, Keys.Space, Keys.Escape,
            Keys.F5, Keys.F11, Keys.Tab,
        ];
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;

        if (_winEventHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void KeyDownEventHandler(object sender, KeyEventArgs e)
    {
        CallViewWindowManagerInvokeRoutine(e, true);
    }

    private void KeyUpEventHandler(object sender, KeyEventArgs e)
    {
        CallViewWindowManagerInvokeRoutine(e, false);
    }

    private void CallViewWindowManagerInvokeRoutine(KeyEventArgs e, bool isKeyDown)
    {
        // skip invalid keys, but record the timestamp
        if (!_validKeys.Contains(e.KeyCode))
        {
            Debug.WriteLine($"Invalid keypress: key={e.KeyCode},down={isKeyDown}, time={_lastInvalidKeyPressTick}");
            if (WindowHelper.IsForegroundWindowBelongToSelf())
            {
                _lastInvalidKeyPressTick = 0L;
            }
            else
            {
                _lastInvalidKeyPressTick = DateTime.Now.Ticks;
            }
            return;
        }

        // skip valid keys when modifiers are used
        if (isKeyDown && e.Modifiers != Keys.None)
            return;

        // skip if key is valid but too close after pressing an invalid key
        if (!WindowHelper.IsForegroundWindowBelongToSelf() && DateTime.Now.Ticks - _lastInvalidKeyPressTick < VALID_KEY_PRESS_DELAY)
            return;
        _lastInvalidKeyPressTick = 0L;

        if (e.KeyCode == Keys.Tab && !WindowHelper.IsForegroundWindowBelongToSelf())
        {
            var wnd = System.Windows.Application.Current.Windows.OfType<ViewerWindow>().FirstOrDefault();
            Debug.WriteLine($"[TabFocus] Tab pressed (down={isKeyDown}), wnd={wnd != null}, wnd.IsVisible={wnd?.IsVisible}, Plugin={wnd?.Plugin?.GetType().FullName}");
            if (wnd != null)
            {
                // 窗口已显示且确认是视频插件 → 直接聚焦
                bool isVideoReady = wnd.IsVisible &&
                    wnd.Plugin?.GetType().FullName == "QuickLook.Plugin.VideoViewer.Plugin";
                // 窗口还在加载中（Plugin 为 null 或窗口还没显示）→ 延迟聚焦
                bool isLoading = !wnd.IsVisible || wnd.Plugin == null;

                Debug.WriteLine($"[TabFocus] isVideoReady={isVideoReady}, isLoading={isLoading}");
                if (isVideoReady || isLoading)
                {
                    if (isKeyDown)
                    {
                        ScheduleVideoWindowFocus(wnd);
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        if (IsVideoViewerForeground(out var videoWnd))
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
            {
                if (isKeyDown)
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        videoWnd.Dispatcher.BeginInvoke(new Action(() => videoWnd.ToggleFullscreen()));
                    }
                    else if (e.KeyCode == Keys.Space)
                    {
                        var contextObj = videoWnd.GetType().GetProperty("ContextObject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(videoWnd);
                        var viewerContent = contextObj?.GetType().GetProperty("ViewerContent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(contextObj);
                        if (viewerContent != null)
                        {
                            var toggleMethod = viewerContent.GetType().GetMethod("TogglePlayPause", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                            if (toggleMethod != null)
                            {
                                videoWnd.Dispatcher.BeginInvoke(new Action(() => toggleMethod.Invoke(viewerContent, new object[] { null, null })));
                            }
                        }
                    }
                    else if (e.KeyCode == Keys.Tab)
                    {
                        var contextObj = videoWnd.GetType().GetProperty("ContextObject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(videoWnd);
                        var viewerContent = contextObj?.GetType().GetProperty("ViewerContent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(contextObj);
                        if (viewerContent != null)
                        {
                            var toggleInfoMethod = viewerContent.GetType().GetMethod("ToggleVideoInfoOverlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                            if (toggleInfoMethod != null)
                            {
                                videoWnd.Dispatcher.BeginInvoke(new Action(() => toggleInfoMethod.Invoke(viewerContent, null)));
                            }
                        }
                    }
                }
                e.Handled = true;
                return;
            }
        }

        // skip if user is holding Space (don't skip other valid keys)
        if (isKeyDown && e.KeyCode == Keys.Space)
        {
            if (_spaceIsDown)
                return;

            _spaceHoldTick = DateTime.Now.Ticks;
        }

        // check if the valid key is a preview request
        if (isKeyDown)
        {
            _isPreviewRequest = NativeMethods.QuickLook.GetFocusedWindowType() !=
                                NativeMethods.QuickLook.FocusedWindowType.Invalid;
            _isPreviewRequest |= WindowHelper.IsForegroundWindowBelongToSelf();
        } // else (when isKeyDown is false), _isPreviewRequest retain its current state

        // call InvokeRoutine only when user pressed a key in a valid window, or
        // released a key which was pressed in a valid window, with an exception of Space which
        // must be hold for 750ms before releasing.
        if (_isPreviewRequest)
        {
            if (isKeyDown || e.KeyCode != Keys.Space ||
                DateTime.Now.Ticks - _spaceHoldTick >= HOLD_TO_PREVIEW_DURATION)
            {
                InvokeRoutine(e.KeyCode, isKeyDown);

                if (isKeyDown && e.KeyCode == Keys.Space)
                    _spaceIsDown = true;
            }
        }

        // when the key has been released, reset variables
        if (!isKeyDown)
        {
            _isPreviewRequest = false;
            _spaceIsDown = e.KeyCode != Keys.Space && _spaceIsDown;
        }
    }

    /// <summary>
    /// 等待视频窗口完全就绪后聚焦窗口和 ViewerPanel。
    /// 处理窗口还没显示、Plugin 还没加载、ViewerContent 还没设置等各种时序情况。
    /// </summary>
    private void ScheduleVideoWindowFocus(ViewerWindow wnd)
    {
        Debug.WriteLine($"[TabFocus] ScheduleVideoWindowFocus called, wnd.IsVisible={wnd.IsVisible}, Plugin={wnd.Plugin?.GetType().FullName}");

        // 先尝试直接聚焦（窗口和内容都已就绪的情况）
        if (TryFocusVideoWindow(wnd))
        {
            Debug.WriteLine("[TabFocus] Direct focus succeeded");
            return;
        }

        Debug.WriteLine("[TabFocus] Starting retry timer...");
        // 还没准备好，启动定时器等待
        int retryCount = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (s, args) =>
        {
            retryCount++;
            // 最多等待3秒，或者窗口被关闭了
            if (retryCount > 30)
            {
                Debug.WriteLine("[TabFocus] Timer timeout (3s), giving up");
                timer.Stop();
                return;
            }

            // 重新获取窗口（可能被重新创建了）
            var currentWnd = System.Windows.Application.Current.Windows.OfType<ViewerWindow>().FirstOrDefault();
            if (currentWnd == null)
            {
                Debug.WriteLine("[TabFocus] Window gone, stopping timer");
                timer.Stop();
                return;
            }

            // Plugin 已加载但不是视频插件 → 停止等待
            if (currentWnd.Plugin != null &&
                currentWnd.Plugin.GetType().FullName != "QuickLook.Plugin.VideoViewer.Plugin")
            {
                Debug.WriteLine($"[TabFocus] Not video plugin: {currentWnd.Plugin.GetType().FullName}, stopping");
                timer.Stop();
                return;
            }

            Debug.WriteLine($"[TabFocus] Retry #{retryCount}: IsVisible={currentWnd.IsVisible}, Plugin={currentWnd.Plugin?.GetType().FullName}");
            if (TryFocusVideoWindow(currentWnd))
            {
                Debug.WriteLine($"[TabFocus] Focus succeeded on retry #{retryCount}");
                timer.Stop();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 尝试聚焦视频窗口和 ViewerPanel。
    /// 如果窗口已显示、Plugin 是 VideoViewer、ViewerContent 已设置，则执行聚焦并返回 true。
    /// </summary>
    private bool TryFocusVideoWindow(ViewerWindow wnd)
    {
        if (!wnd.IsVisible)
        {
            Debug.WriteLine("[TabFocus] TryFocus: window not visible");
            return false;
        }

        // 确认是视频插件（如果 Plugin 还是 null 说明还在加载中）
        if (wnd.Plugin == null)
        {
            Debug.WriteLine("[TabFocus] TryFocus: Plugin is null (loading)");
            return false;
        }

        if (wnd.Plugin.GetType().FullName != "QuickLook.Plugin.VideoViewer.Plugin")
        {
            Debug.WriteLine($"[TabFocus] TryFocus: not video plugin ({wnd.Plugin.GetType().FullName})");
            return false; // 不是视频插件，不需要聚焦
        }

        // 检查 ViewerContent 是否已设置（Plugin.View 是否已执行）
        var contextObj = wnd.GetType().GetProperty("ContextObject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(wnd);
        var viewerContent = contextObj?.GetType().GetProperty("ViewerContent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(contextObj);

        // ViewerContent 还没设置（Plugin.View 还没执行完），继续等待
        if (viewerContent is not System.Windows.UIElement uiElement)
        {
            Debug.WriteLine($"[TabFocus] TryFocus: ViewerContent is null or not UIElement (type={viewerContent?.GetType().FullName})");
            return false;
        }

        Debug.WriteLine($"[TabFocus] TryFocus: All ready! ViewerContent type={uiElement.GetType().FullName}");

        // 聚焦窗口
        wnd.Activate();
        wnd.Focus();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(wnd).Handle;
        if (hwnd != IntPtr.Zero)
        {
            User32.SetForegroundWindow(hwnd);
        }

        // 聚焦 ViewerPanel
        var focusResult1 = uiElement.Focus();
        var focusedElement = System.Windows.Input.Keyboard.Focus(uiElement);
        Debug.WriteLine($"[TabFocus] TryFocus: Focus()={focusResult1}, Keyboard.Focus()={focusedElement?.GetType().FullName}, IsFocused={uiElement.IsFocused}, IsKeyboardFocused={uiElement.IsKeyboardFocused}");

        return uiElement.IsKeyboardFocused || uiElement.IsKeyboardFocusWithin;
    }

    private bool IsVideoViewerForeground(out ViewerWindow wnd)
    {
        wnd = System.Windows.Application.Current.Windows.OfType<ViewerWindow>().FirstOrDefault();
        return wnd != null && wnd.IsVisible && WindowHelper.IsForegroundWindowBelongToSelf() && wnd.Plugin?.GetType().FullName == "QuickLook.Plugin.VideoViewer.Plugin";
    }

    private void InvokeRoutine(Keys key, bool isKeyDown)
    {
        Debug.WriteLine($"InvokeRoutine: key={key},down={isKeyDown}");

        if (isKeyDown)
        {
            switch (key)
            {
                case Keys.Enter:
                    PipeServerManager.SendMessage(PipeMessages.RunAndClose);
                    break;

                case Keys.Space:
                    PipeServerManager.SendMessage(PipeMessages.Toggle);
                    break;

                case Keys.F5:
                    PipeServerManager.SendMessage(PipeMessages.Reload);
                    break;

                case Keys.F11:
                    PipeServerManager.SendMessage(PipeMessages.Fullscreen);
                    break;
            }
        }
        else
        {
            switch (key)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                    PipeServerManager.SendMessage(PipeMessages.Switch);
                    break;

                case Keys.Escape:
                    PipeServerManager.SendMessage(PipeMessages.Close);
                    break;

                case Keys.Space:
                    if (SettingHelper.Get("AutoCloseHolding", true, "QuickLook"))
                        PipeServerManager.SendMessage(PipeMessages.Toggle);
                    break;
            }
        }
    }

    private void InstallKeyHook(KeyEventHandler downHandler, KeyEventHandler upHandler)
    {
        _hook = GlobalKeyboardHook.GetInstance();

        _hook.KeyDown += downHandler;
        _hook.KeyUp += upHandler;
    }

    private void InstallForegroundWindowHook()
    {
        // When the foreground window changes (e.g. via Alt+Tab), reset the invalid-key
        // delay so the first Space press in the newly focused Explorer window works.
        // https://github.com/QL-Win/QuickLook/issues/1939
        _winEventProc = OnForegroundWindowChanged;
        _winEventHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc,
            0, 0, User32.WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundWindowChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // A different window is now in the foreground. Any invalid key presses that happened
        // before this switch (e.g. Alt+Tab keystrokes) belong to the previous context,
        // so they must not suppress valid keys in the new window.
        _lastInvalidKeyPressTick = 0L;

#if false // The problem of requiring two spaces has been solved -- comment the test code first.
        Debug.WriteLine($"Foreground window changed to {hwnd:X}, invalid-key delay cleared.");
#endif
    }

    internal static KeystrokeDispatcher GetInstance()
    {
        return _instance ??= new KeystrokeDispatcher();
    }
}
