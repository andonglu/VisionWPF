using System;
using System.Collections.Generic;
using HalconDotNet;

namespace VisionFlow.Controls.Roi
{
    public sealed class RoiCollection
    {
        private readonly List<RoiShape> _items = new List<RoiShape>();

        public IReadOnlyList<RoiShape> Items => _items;
        public int ActiveIndex { get; private set; } = -1;

        public RoiShape ActiveRoi
        {
            get
            {
                if (ActiveIndex < 0 || ActiveIndex >= _items.Count)
                {
                    return null;
                }
                return _items[ActiveIndex];
            }
        }

        public event EventHandler<RoiChangedEventArgs> Changed;

        public void Add(RoiShape roi)
        {
            _items.Add(roi);
            ActiveIndex = _items.Count - 1;
            OnChanged(roi);
        }

        public void RemoveActive()
        {
            RoiShape active = ActiveRoi;
            if (active == null)
            {
                return;
            }
            _items.RemoveAt(ActiveIndex);
            ActiveIndex = Math.Min(ActiveIndex, _items.Count - 1);
            OnChanged(active);
        }

        public void Clear()
        {
            _items.Clear();
            ActiveIndex = -1;
            OnChanged(null);
        }

        public void SetActive(int index)
        {
            if (index < -1 || index >= _items.Count)
            {
                return;
            }
            ActiveIndex = index;
            OnChanged(ActiveRoi);
        }

        public void Draw(HWindow window)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Draw(window, i == ActiveIndex);
            }
        }

        public int HitTest(double row, double col, out int handleIndex)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                handleIndex = _items[i].HitTestHandle(row, col);
                if (handleIndex >= 0)
                {
                    ActiveIndex = i;
                    return i;
                }
            }
            handleIndex = -1;
            return -1;
        }

        public HRegion BuildRegion()
        {
            HRegion include = new HRegion();
            HRegion exclude = new HRegion();
            include.GenEmptyRegion();
            exclude.GenEmptyRegion();

            foreach (RoiShape roi in _items)
            {
                using HRegion region = roi.ToRegion();
                if (roi.Polarity == RoiPolarity.Include)
                {
                    include = include.Union2(region);
                }
                else
                {
                    exclude = exclude.Union2(region);
                }
            }

            return include.Difference(exclude);
        }

        public void NotifyActiveChanged()
        {
            OnChanged(ActiveRoi);
        }

        private void OnChanged(RoiShape roi)
        {
            Changed?.Invoke(this, new RoiChangedEventArgs(roi));
        }
    }
}
