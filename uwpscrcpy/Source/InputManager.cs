using System;
using System.Collections.Generic;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.Foundation;
using ScrcpyVideoEngine;

namespace uwpscrcpy
{
    public class InputManager
    {
        private readonly ScrcpyController _controller;
        private readonly SwapChainPanel _videoPanel;
        private readonly Grid _mousePadArea;
        private readonly Border _leftClickArea;
        private readonly Border _rightClickArea;
        private readonly ToggleSwitch _invertScrollToggle;
        private bool _isUhidMouseMode = false;
        private bool _isLeftMouseButtonDown = false;
        private const int MOUSE_BUTTON_LEFT = 1;
        private const int MOUSE_BUTTON_RIGHT = 2;
        private const int MOUSE_BUTTON_MIDDLE = 4;
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

        public InputManager(ScrcpyController controller, SwapChainPanel videoPanel, Grid mousePad, Border leftClick, Border rightClick, ToggleSwitch invertScroll)
        {
            _controller = controller;
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
            if (_isUhidMouseMode || _controller == null) return;
            var prop = e.GetCurrentPoint(_videoPanel).Properties;
            if (prop.IsRightButtonPressed)
            {
                _controller.InjectBackOrScreenOn(0);
                _controller.InjectBackOrScreenOn(1);
                e.Handled = true;
                return;
            }
            SendTouch(0, e);
        }

        private void VideoSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _controller == null) return;
            if (e.Pointer.IsInContact)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    SendTouch(2, e);
                    _lastTouchSendTime = now;
                }
            }
        }

        private void VideoSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _controller == null) return;
            var updateKind = e.GetCurrentPoint(_videoPanel).Properties.PointerUpdateKind;
            if (updateKind == Windows.UI.Input.PointerUpdateKind.RightButtonReleased) return;
            SendTouch(1, e);
        }

        private void SendTouch(int action, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_videoPanel);
            var pos = point.Position;
            int ptrId = (int)e.Pointer.PointerId;
            float pressure = point.Properties.Pressure;
            int buttons = 0;
            if (point.Properties.IsLeftButtonPressed) buttons |= MOUSE_BUTTON_LEFT;
            if (point.Properties.IsRightButtonPressed) buttons |= MOUSE_BUTTON_RIGHT;
            if (point.Properties.IsMiddleButtonPressed) buttons |= MOUSE_BUTTON_MIDDLE;
            int w = (int)_videoPanel.ActualWidth;
            int h = (int)_videoPanel.ActualHeight;
            _controller.InjectTouch(action, ptrId, (int)pos.X, (int)pos.Y, w, h, pressure, buttons);
        }

        private void VideoSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_isUhidMouseMode || _controller == null) return;
            var point = e.GetCurrentPoint(_videoPanel);
            var pos = point.Position;
            var props = point.Properties;
            int w = (int)_videoPanel.ActualWidth;
            int h = (int)_videoPanel.ActualHeight;
            float vScrollFloat = (float)props.MouseWheelDelta / 120.0f;
            if (_invertScrollToggle.IsOn) vScrollFloat = -vScrollFloat;
            int vScroll = (int)Math.Round(vScrollFloat);
            if (vScroll == 0 && Math.Abs(props.MouseWheelDelta) > 10) vScroll = props.MouseWheelDelta > 0 ? 1 : -1;
            if (vScroll != 0)
            {
                int buttons = 0;
                if (props.IsLeftButtonPressed) buttons |= MOUSE_BUTTON_LEFT;
                _controller.InjectScroll((int)pos.X, (int)pos.Y, w, h, 0, vScroll, buttons);
            }
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
                    _controller.InjectUhidInput(MOUSE_BUTTON_LEFT, 0, 0, 0, 0);
                    _lastTapTimestamp = 0;
                }
                else _lastTapTimestamp = now;
            }
        }

        private void MousePadArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!e.Pointer.IsInContact || _controller == null) return;
            uint ptrId = e.Pointer.PointerId;
            var currentPosition = e.GetCurrentPoint(_mousePadArea).Position;
            if (!_pointerPositions.TryGetValue(ptrId, out Point previousPosition))
            {
                _pointerPositions[ptrId] = currentPosition;
                return;
            }
            double MOUSE_SENSITIVITY = 1.0;
            double rawDX = (currentPosition.X - previousPosition.X) * MOUSE_SENSITIVITY;
            double rawDY = (currentPosition.Y - previousPosition.Y) * MOUSE_SENSITIVITY;
            _pointerPositions[ptrId] = currentPosition;
            if (_pointerPositions.Count == 1)
            {
                _pendingMouseX += rawDX;
                _pendingMouseY += rawDY;
                int buttons = 0;
                if (_isDragging || _isLeftMouseButtonDown) buttons |= MOUSE_BUTTON_LEFT;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastTouchSendTime >= TOUCH_RATE_LIMIT_MS)
                {
                    if (Math.Abs(_pendingMouseX) >= 1.0 || Math.Abs(_pendingMouseY) >= 1.0)
                    {
                        int dx = (int)_pendingMouseX;
                        int dy = (int)_pendingMouseY;
                        _pendingMouseX -= dx;
                        _pendingMouseY -= dy;
                        _controller.InjectUhidInput(buttons, dx, dy, 0, 0);
                        _lastTouchSendTime = now;
                    }
                }
            }
            else if (_pointerPositions.Count >= 2)
            {
                double rawDeltaY = rawDY * SCROLL_SENSITIVITY;
                _scrollAccumulatorY += rawDeltaY;
                if (Math.Abs(_scrollAccumulatorY) >= 1.0)
                {
                    int vScrollToSend = (int)_scrollAccumulatorY;
                    _scrollAccumulatorY -= vScrollToSend;
                    if (_invertScrollToggle.IsOn) vScrollToSend = -vScrollToSend;
                    _controller.InjectUhidInput(0, 0, 0, vScrollToSend, 0);
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
                _controller.InjectUhidInput(0, 0, 0, 0, 0);
            }
        }

        private void LeftClickArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isLeftMouseButtonDown = true;
            _controller.InjectUhidInput(MOUSE_BUTTON_LEFT, 0, 0, 0, 0);
        }

        private void LeftClickArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isLeftMouseButtonDown = false;
            if (!_isDragging) _controller.InjectUhidInput(0, 0, 0, 0, 0);
        }

        private void RightClickArea_PointerPressed(object sender, PointerRoutedEventArgs e) => _controller.InjectUhidInput(MOUSE_BUTTON_RIGHT, 0, 0, 0, 0);
        private void RightClickArea_PointerReleased(object sender, PointerRoutedEventArgs e) => _controller.InjectUhidInput(0, 0, 0, 0, 0);
    }
}