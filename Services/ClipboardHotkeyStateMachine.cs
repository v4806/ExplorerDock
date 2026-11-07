using ExplorerDock.Interop;

namespace ExplorerDock.Services;

internal enum ClipboardHotkeyDecision
{
    Pass,
    Swallow,
    ToggleClipboard,
    SwallowAndRestoreWin,
}

/// <summary>
/// Win+V 的纯状态机。真实热键必须从本钩子观察到的 Win down 开始，
/// 物理键状态只能否决陈旧缓存，不能单独授权触发。
/// </summary>
internal sealed class ClipboardHotkeyStateMachine
{
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkV = 0x56;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private bool _leftWinObserved;
    private bool _rightWinObserved;
    private int _lastObservedWin;
    private bool _vPassedDown;
    private bool _vSwallowed;
    private bool _swallowWinUp;
    private int _sequenceWin;

    public ClipboardHotkeyDecision Process(KeyStroke stroke, bool enabled, Func<int, bool> isKeyDown)
    {
        switch (stroke.Vk)
        {
            case VkLWin:
            case VkRWin:
                return ProcessWin(stroke);

            case VkV:
                return ProcessV(stroke, enabled, isKeyDown);

            default:
                return ClipboardHotkeyDecision.Pass;
        }
    }

    private ClipboardHotkeyDecision ProcessWin(KeyStroke stroke)
    {
        bool left = stroke.Vk == VkLWin;

        if (stroke.Down)
        {
            bool wasObserved = left ? _leftWinObserved : _rightWinObserved;

            // 只有同一颗 Win 在没有收到 up 的情况下又被按下，才把它当成新的一轮。
            // 另一侧 Win down 不能清掉正在等待收尾的 initiating Win。
            if (!wasObserved && _swallowWinUp && stroke.Vk == _sequenceWin)
            {
                _swallowWinUp = false;
                _sequenceWin = 0;
            }

            if (left) _leftWinObserved = true;
            else _rightWinObserved = true;

            _lastObservedWin = stroke.Vk;
            return ClipboardHotkeyDecision.Pass;
        }

        if (left) _leftWinObserved = false;
        else _rightWinObserved = false;

        if (_swallowWinUp && stroke.Vk == _sequenceWin)
        {
            _swallowWinUp = false;
            _sequenceWin = 0;
            return ClipboardHotkeyDecision.SwallowAndRestoreWin;
        }

        return ClipboardHotkeyDecision.Pass;
    }

    private ClipboardHotkeyDecision ProcessV(KeyStroke stroke, bool enabled, Func<int, bool> isKeyDown)
    {
        if (!stroke.Down)
        {
            if (!_vSwallowed)
            {
                _vPassedDown = false;
                return ClipboardHotkeyDecision.Pass;
            }

            _vSwallowed = false;
            return ClipboardHotkeyDecision.Swallow;
        }

        if (_vSwallowed) return ClipboardHotkeyDecision.Swallow;
        if (_vPassedDown) return ClipboardHotkeyDecision.Pass;

        if (!enabled)
        {
            _vPassedDown = true;
            return ClipboardHotkeyDecision.Pass;
        }

        int observedWin = ChooseObservedPhysicalWin(isKeyDown);
        if (observedWin == 0)
        {
            _vPassedDown = true;
            return ClipboardHotkeyDecision.Pass;
        }

        if (stroke.AltDown
            || isKeyDown(VkControl)
            || isKeyDown(VkMenu)
            || isKeyDown(VkShift))
        {
            _vPassedDown = true;
            return ClipboardHotkeyDecision.Pass;
        }

        _vPassedDown = false;
        _vSwallowed = true;
        _swallowWinUp = true;
        _sequenceWin = observedWin;
        return ClipboardHotkeyDecision.ToggleClipboard;
    }

    private int ChooseObservedPhysicalWin(Func<int, bool> isKeyDown)
    {
        if (IsObservedAndDown(_lastObservedWin, isKeyDown)) return _lastObservedWin;
        if (IsObservedAndDown(VkLWin, isKeyDown)) return VkLWin;
        if (IsObservedAndDown(VkRWin, isKeyDown)) return VkRWin;

        if (_leftWinObserved && !isKeyDown(VkLWin)) _leftWinObserved = false;
        if (_rightWinObserved && !isKeyDown(VkRWin)) _rightWinObserved = false;

        return 0;
    }

    private bool IsObservedAndDown(int vk, Func<int, bool> isKeyDown)
    {
        bool observed = vk switch
        {
            VkLWin => _leftWinObserved,
            VkRWin => _rightWinObserved,
            _ => false,
        };

        return observed && isKeyDown(vk);
    }
}
