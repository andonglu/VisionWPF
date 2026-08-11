using System;
using HalconDotNet;

namespace VisionFlow.Controls.Roi
{
    [Serializable]
    public abstract class RoiShape
    {
        private const double HandleHitRadius = 15.0;

        public string Name { get; set; }
        public RoiPolarity Polarity { get; set; }
        public string Color { get; set; } = "green";
        public string ActiveColor { get; set; } = "red";

        public abstract RoiKind Kind { get; }
        public abstract int HandleCount { get; }

        protected RoiShape(string name)
        {
            Name = name;
        }

        public abstract void Draw(HWindow window, bool active);
        public abstract HRegion ToRegion();
        public abstract double[] GetModelData();
        public abstract void MoveByHandle(int handleIndex, double row, double col);
        protected abstract double DistanceToHandle(int handleIndex, double row, double col);

        public int HitTestHandle(double row, double col)
        {
            int hit = -1;
            double best = HandleHitRadius;
            for (int i = 0; i < HandleCount; i++)
            {
                double distance = DistanceToHandle(i, row, col);
                if (distance <= best)
                {
                    best = distance;
                    hit = i;
                }
            }
            return hit;
        }

        protected static double Distance(double row1, double col1, double row2, double col2)
        {
            double dr = row1 - row2;
            double dc = col1 - col2;
            return Math.Sqrt(dr * dr + dc * dc);
        }

        protected static void DrawHandle(HWindow window, double row, double col)
        {
            window.DispRectangle2(row, col, 0, 6, 6);
        }
    }
}
