using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace uwpscrcpy
{
    public class InputManager
    {
        private readonly ScrcpySession _session;
        private readonly SwapChainPanel _videoPanel;
        private readonly Grid _mousePadArea;
        private readonly Border _leftClickArea;
        private readonly Border _rightClickArea;
        private readonly ToggleSwitch _invertScrollToggle;

        private bool _isUhidMouseMode = false;
        private bool _isLeftMouseButtonDown = false;
        private const byte MOUSE_BUTTON_LEFT = 1 << 0;
        private const byte MOUSE_BUTTON_RIGHT = 1 << 1;
        private readonly Dictionary<uint, Point> _pointerPositions = new Dictionary<uint, Point>();
        private bool _isDragging = false;
        private long _lastTapTimestamp = 0;
        private const int DOUBLE_TAP_THRESHOLD_MS = 200;
        private double _scrollAccumulatorY = 0;
        private const double SCROLL_SENSITIVITY = 0.025;
        private long _lastTouchSendTime = 0;
        private const int TOUCH_RATE_LIMIT_MS = 16;
        private double _pendingMouseX = 0;
        private double _pendingMouseY = 0;

        public InputManager(ScrcpySession session, SwapChainPanel videoPanel, Grid mousePad, Border leftClick, Border rightClick, ToggleSwitch invertScroll)
        {
            _session = session;
            _videoPanel = videoPanel;
            _mousePadArea = mousePad;
            _leftClickArea = leftClick;
            _rightClickArea = rightClick;
            _invertScrollToggle = invertScroll;
        }

        public void SetUhidMode(bool isEnabled)
        {
            _isUhidMouseMode = isEnabled;
        }

        public void RegisterInputHandlers()
        {
            _videoPanel.PointerPressed += VideoSurface_PointerPressed;
            _videoPanel.PointerMoved += VideoSurface_PointerMoved;
            _videoPanel.PointerReleased += VideoSurface_PointerReleased;
            _videoPanel.PointerCanceled += VideoSurface_PointerReleased;
            _videoPanel.PointerExited += VideoSurface_PointerReleased;
            _videoPanel.PointerWheelChanged += VideoSurface_PointerWheelChanged;

            _mousePadArea.PointerPressed += MousePadArea_PointerPressed;
            _mousePadArea.PointerMoved += MousePadArea_PointerMoved;
            _mousePadArea.PointerReleased += MousePadArea_PointerReleased;
            _mousePadArea.PointerCanceled += MousePadArea_PointerCanceled;
            _mousePadArea.PointerExited += MousePadArea_PointerExited;

            _leftClickArea.PointerPressed += LeftClickArea_PointerPressed;
            _leftClickArea.PointerReleased += LeftClickArea_PointerReleased;
            _rightClickArea.PointerPressed += RightClickArea_PointerPressed;
            _rightClickArea.PointerReleased += RightClickArea_PointerReleased;
        }

        public void UnregisterInputHandlers()
        {
            _videoPanel.PointerPressed -= VideoSurface_PointerPressed;
            _videoPanel.PointerMoved -= VideoSurface_PointerMoved;
            _videoPanel.PointerReleased -= VideoSurface_PointerReleased;
            _videoPanel.PointerCanceled -= VideoSurface_PointerReleased;
            _videoPanel.PointerExited -= VideoSurface_PointerReleased;
            _videoPanel.PointerWheelChanged -= VideoSurface_PointerWheelChanged;

            _mousePadArea.PointerPressed -= MousePadArea_PointerPressed;
            _mousePadArea.PointerMoved -= MousePadArea_PointerMoved;
            _mousePadArea.PointerReleased -= MousePadArea_PointerReleased;
            _mousePadArea.PointerCanceled -= MousePadArea_PointerCanceled;
            _mousePadArea.PointerExited -= MousePadArea_PointerExited;

            _leftClickArea.PointerPressed -= LeftClickArea_PointerPressed;
            _leftClickArea.PointerReleased -= LeftClickArea_PointerReleased;
            _rightClickArea.PointerPressed -= RightClickArea_PointerPressed;
            _rightClickArea.PointerReleased -= RightClickArea_PointerReleased;
        }

        private void VideoSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;
            var properties = e.GetCurrentPoint(_videoPanel).Properties;
            if (properties.IsRightButtonPressed) { e.Handled = true; return; }
            _session.SendTouch(0, e, _videoPanel);
        }

        private void VideoSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;
            if (e.Pointer.IsInContact)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    _session.SendTouch(2, e, _videoPanel);
                    _lastTouchSendTime = now;
                }
            }
        }

        private void VideoSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;
            var updateKind = e.GetCurrentPoint(_videoPanel).Properties.PointerUpdateKind;
            if (updateKind == Windows.UI.Input.PointerUpdateKind.RightButtonReleased)
            {
                _session.SendBackEvent(0);
                _session.SendBackEvent(1);
                e.Handled = true;
                return;
            }
            _session.SendTouch(1, e, _videoPanel);
        }

        private void VideoSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _session == null) return;

            var point = e.GetCurrentPoint(_videoPanel);
            var pos = point.Position;
            var props = point.Properties;

            double scaleX = (double)_session.Width / _videoPanel.ActualWidth;
            double scaleY = (double)_session.Height / _videoPanel.ActualHeight;
            int x = Math.Max(0, Math.Min((int)_session.Width, (int)(pos.X * scaleX)));
            int y = Math.Max(0, Math.Min((int)_session.Height, (int)(pos.Y * scaleY)));

            const float ScrollSensitivity = 0.05f;
            const float MaxScroll = 1.0f;

            float vScrollFloat = (float)props.MouseWheelDelta / 120.0f;
            vScrollFloat *= ScrollSensitivity;
            if (_invertScrollToggle.IsOn) vScrollFloat = -vScrollFloat;

            vScrollFloat = Math.Max(-MaxScroll, Math.Min(MaxScroll, vScrollFloat));
            short vScrollFixed = (short)Math.Round(vScrollFloat * 32767.0f);

            int buttons = 0;
            if (props.IsLeftButtonPressed) buttons |= 1;
            if (props.IsRightButtonPressed) buttons |= 2;
            if (props.IsMiddleButtonPressed) buttons |= 4;

            _session.SendScrollEvent(x, y, 0, vScrollFixed, buttons);
        }

        private void MousePadArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            uint ptrId = e.Pointer.PointerId;
            var pos = e.GetCurrentPoint(_mousePadArea).Position;
            if (!_pointerPositions.ContainsKey(ptrId)) _pointerPositions.Add(ptrId, pos);
            else _pointerPositions[ptrId] = pos;

            if (_pointerPositions.Count == 1)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTapTimestamp < DOUBLE_TAP_THRESHOLD_MS)
                {
                    _isDragging = true;
                    _session?.SendHidInputEvent(MOUSE_BUTTON_LEFT, new Point(0, 0));
                    _lastTapTimestamp = 0;
                }
                else _lastTapTimestamp = now;
            }
        }

        private void MousePadArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!e.Pointer.IsInContact || _session == null) return;
            uint ptrId = e.Pointer.PointerId;
            var currentPosition = e.GetCurrentPoint(_mousePadArea).Position;

            if (!_pointerPositions.TryGetValue(ptrId, out Point previousPosition))
            {
                _pointerPositions[ptrId] = currentPosition;
                return;
            }

            double MOUSE_SENSITIVITY = 1.25;
            double rawDX = (currentPosition.X - previousPosition.X) * MOUSE_SENSITIVITY;
            double rawDY = (currentPosition.Y - previousPosition.Y) * MOUSE_SENSITIVITY;
            _pointerPositions[ptrId] = currentPosition;

            if (_pointerPositions.Count == 1)
            {
                _pendingMouseX += rawDX;
                _pendingMouseY += rawDY;
                byte buttons = 0;
                if (_isDragging || _isLeftMouseButtonDown) buttons |= MOUSE_BUTTON_LEFT;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    if (Math.Abs(_pendingMouseX) >= 1.0 || Math.Abs(_pendingMouseY) >= 1.0)
                    {
                        var deltaToSend = new Point(_pendingMouseX, _pendingMouseY);
                        _session.SendHidInputEvent(buttons, deltaToSend);
                        _pendingMouseX = 0; _pendingMouseY = 0;
                        _lastTouchSendTime = now;
                    }
                }
            }
            else if (_pointerPositions.Count >= 2)
            {
                double rawDeltaY = rawDY * SCROLL_SENSITIVITY;
                if (Math.Abs(rawDY) < 0.5) rawDeltaY = 0;
                _scrollAccumulatorY += rawDeltaY;
                if (Math.Abs(_scrollAccumulatorY) >= 1.0)
                {
                    int vScrollToSend = (int)_scrollAccumulatorY;
                    _scrollAccumulatorY -= vScrollToSend;
                    if (_invertScrollToggle.IsOn) vScrollToSend = -vScrollToSend;
                    _session.SendHidInputEvent(0, new Point(0, 0), vScrollToSend, 0);
                }
            }
        }

        private void MousePadArea_PointerReleased(object sender, PointerRoutedEventArgs e) => CleanupPointer(e.Pointer.PointerId);
        private void MousePadArea_PointerCanceled(object sender, PointerRoutedEventArgs e) => CleanupPointer(e.Pointer.PointerId);
        private void MousePadArea_PointerExited(object sender, PointerRoutedEventArgs e) => CleanupPointer(e.Pointer.PointerId);

        private void CleanupPointer(uint ptrId)
        {
            if (_pointerPositions.ContainsKey(ptrId)) _pointerPositions.Remove(ptrId);
            _pendingMouseX = 0; _pendingMouseY = 0;
            if (_isDragging && _pointerPositions.Count == 0)
            {
                _isDragging = false;
                _session?.SendHidInputEvent(0, new Point(0, 0));
            }
        }

        private void LeftClickArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isLeftMouseButtonDown = true;
            _session?.SendHidInputEvent(MOUSE_BUTTON_LEFT, new Point(0, 0));
        }

        private void LeftClickArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isLeftMouseButtonDown = false;
            if (!_isDragging) _session?.SendHidInputEvent(0, new Point(0, 0));
        }

        private void RightClickArea_PointerPressed(object sender, PointerRoutedEventArgs e) => _session?.SendHidInputEvent(MOUSE_BUTTON_RIGHT, new Point(0, 0));
        private void RightClickArea_PointerReleased(object sender, PointerRoutedEventArgs e) => _session?.SendHidInputEvent(0, new Point(0, 0));
    }
}