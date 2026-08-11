using System;

namespace VisionFlow.Controls.Roi
{
    public sealed class RoiChangedEventArgs : EventArgs
    {
        public RoiShape Roi { get; private set; }

        public RoiChangedEventArgs(RoiShape roi)
        {
            Roi = roi;
        }
    }
}
