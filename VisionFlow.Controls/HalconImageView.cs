using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls.Roi;

namespace VisionFlow.Controls
{
    public sealed class HalconImageView : UserControl
    {
        private readonly HWindowControl _window;
        private HObject _image;
        private HObject _overlay;
        private int _imageWidth;
        private int _imageHeight;
        private double _row1;
        private double _col1;
        private double _row2;
        private double _col2;
        private bool _hasImage;
        private bool _isPanning;
        private bool _isEditingRoi;
        private double _lastRow;
        private double _lastCol;
        private int _activeHandle = -1;
        private RoiKind? _pendingRoiKind;
        private int _roiCounter;

        public RoiCollection Rois { get; } = new RoiCollection();

        public event EventHandler<RoiChangedEventArgs> RoiChanged;

        public HalconImageView()
        {
            _window = new HWindowControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            Controls.Add(_window);

            _window.HMouseWheel += OnHMouseWheel;
            _window.HMouseDown += OnHMouseDown;
            _window.HMouseMove += OnHMouseMove;
            _window.HMouseUp += OnHMouseUp;
            _window.MouseLeave += (s, e) => StopInteraction();
            Resize += (s, e) => Redraw();
            Rois.Changed += (s, e) =>
            {
                Redraw();
                RoiChanged?.Invoke(this, e);
            };
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HWindowControl WindowControl => _window;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HObject ImageObject => _image;

        public void ShowImage(HObject image)
        {
            ClearImage();
            if (image == null || !image.IsInitialized())
            {
                return;
            }

            _image = image.Clone();
            HOperatorSet.GetImageSize(_image, out HTuple width, out HTuple height);
            _imageWidth = width.I;
            _imageHeight = height.I;
            _hasImage = _imageWidth > 0 && _imageHeight > 0;
            FitToWindow();
        }

        public void ClearImage()
        {
            _image?.Dispose();
            _image = null;
            _hasImage = false;
            _window.HalconWindow.ClearWindow();
        }

        public void SetOverlay(HObject overlay)
        {
            _overlay?.Dispose();
            _overlay = overlay == null || !overlay.IsInitialized() ? null : overlay.Clone();
            Redraw();
        }

        public void ClearOverlay()
        {
            _overlay?.Dispose();
            _overlay = null;
            Redraw();
        }

        public void FitToWindow()
        {
            if (!_hasImage || _window.Width <= 0 || _window.Height <= 0)
            {
                return;
            }

            double windowRatio = (double)_window.Width / _window.Height;
            double imageRatio = (double)_imageWidth / _imageHeight;
            if (windowRatio >= imageRatio)
            {
                _row1 = 0;
                _row2 = _imageHeight - 1;
                double pad = (_imageHeight * windowRatio - _imageWidth) / 2.0;
                _col1 = -pad;
                _col2 = _imageWidth + pad;
            }
            else
            {
                _col1 = 0;
                _col2 = _imageWidth - 1;
                double pad = (_imageWidth / windowRatio - _imageHeight) / 2.0;
                _row1 = -pad;
                _row2 = _imageHeight + pad;
            }

            Redraw();
        }

        public void BeginAddRoi(RoiKind kind)
        {
            _pendingRoiKind = kind;
            Cursor = Cursors.Cross;
        }

        public void CancelAddRoi()
        {
            _pendingRoiKind = null;
            Cursor = Cursors.Default;
        }

        public HRegion BuildRegion()
        {
            return Rois.BuildRegion();
        }

        private void OnHMouseWheel(object sender, HMouseEventArgs e)
        {
            if (!_hasImage)
            {
                return;
            }

            double scale = e.Delta > 0 ? 0.8 : 1.25;
            ZoomAt(e.Y, e.X, scale);
        }

        private void OnHMouseDown(object sender, HMouseEventArgs e)
        {
            if (!_hasImage)
            {
                return;
            }

            if (_pendingRoiKind.HasValue)
            {
                AddDefaultRoi(_pendingRoiKind.Value, e.Y, e.X);
                CancelAddRoi();
                return;
            }

            Rois.HitTest(e.Y, e.X, out _activeHandle);
            if (_activeHandle >= 0)
            {
                _isEditingRoi = true;
            }
            else
            {
                _isPanning = true;
            }

            _lastRow = e.Y;
            _lastCol = e.X;
            Redraw();
        }

        private void OnHMouseMove(object sender, HMouseEventArgs e)
        {
            if (!_hasImage)
            {
                return;
            }

            if (_isEditingRoi && Rois.ActiveRoi != null && _activeHandle >= 0)
            {
                Rois.ActiveRoi.MoveByHandle(_activeHandle, e.Y, e.X);
                Rois.NotifyActiveChanged();
            }
            else if (_isPanning)
            {
                double dRow = e.Y - _lastRow;
                double dCol = e.X - _lastCol;
                _row1 -= dRow;
                _row2 -= dRow;
                _col1 -= dCol;
                _col2 -= dCol;
                Redraw();
            }
        }

        private void OnHMouseUp(object sender, HMouseEventArgs e)
        {
            StopInteraction();
        }

        private void StopInteraction()
        {
            _isPanning = false;
            _isEditingRoi = false;
            _activeHandle = -1;
        }

        private void ZoomAt(double row, double col, double scale)
        {
            double width = (_col2 - _col1) * scale;
            double height = (_row2 - _row1) * scale;
            if (width < 4 || height < 4 || width > _imageWidth * 100 || height > _imageHeight * 100)
            {
                return;
            }

            double colPercent = (col - _col1) / (_col2 - _col1);
            double rowPercent = (row - _row1) / (_row2 - _row1);
            _col1 = col - width * colPercent;
            _col2 = _col1 + width;
            _row1 = row - height * rowPercent;
            _row2 = _row1 + height;
            Redraw();
        }

        private void AddDefaultRoi(RoiKind kind, double row, double col)
        {
            _roiCounter++;
            string name = $"{kind}_{_roiCounter}";
            switch (kind)
            {
                case RoiKind.Rectangle1:
                    Rois.Add(new Rectangle1Roi(name, row - 50, col - 50, row + 50, col + 50));
                    break;
                case RoiKind.Rectangle2:
                    Rois.Add(new Rectangle2Roi(name, row, col, 0, 80, 40));
                    break;
                case RoiKind.Circle:
                    Rois.Add(new CircleRoi(name, row, col, 50));
                    break;
                case RoiKind.Line:
                    Rois.Add(new LineRoi(name, row, col - 50, row, col + 50));
                    break;
            }
        }

        public void Redraw()
        {
            if (!_hasImage)
            {
                return;
            }

            try
            {
                HOperatorSet.SetPart(_window.HalconWindow, _row1, _col1, _row2, _col2);
                _window.HalconWindow.ClearWindow();
                _window.HalconWindow.DispObj(_image);
                if (_overlay != null && _overlay.IsInitialized())
                {
                    _window.HalconWindow.SetColor("cyan");
                    _window.HalconWindow.SetDraw("margin");
                    _window.HalconWindow.DispObj(_overlay);
                }
                Rois.Draw(_window.HalconWindow);
            }
            catch (HalconException)
            {
                Debug.WriteLine("Halcon window redraw failed.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image?.Dispose();
                _overlay?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
