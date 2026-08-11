using System;
using HalconDotNet;

namespace VisionFlow.Controls.Roi
{
    public sealed class CircleRoi : RoiShape
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double Radius { get; set; }

        public override RoiKind Kind => RoiKind.Circle;
        public override int HandleCount => 2;

        public CircleRoi(string name, double row, double column, double radius) : base(name)
        {
            Row = row;
            Column = column;
            Radius = Math.Max(1, radius);
        }

        public override void Draw(HWindow window, bool active)
        {
            window.SetColor(active ? ActiveColor : Color);
            window.SetDraw("margin");
            window.DispCircle(Row, Column, Radius);
            if (active)
            {
                DrawHandle(window, Row, Column);
                DrawHandle(window, Row, Column + Radius);
            }
        }

        public override HRegion ToRegion()
        {
            var region = new HRegion();
            region.GenCircle(Row, Column, Radius);
            return region;
        }

        public override double[] GetModelData()
        {
            return new[] { Row, Column, Radius };
        }

        public override void MoveByHandle(int handleIndex, double row, double col)
        {
            if (handleIndex == 0)
            {
                Row = row;
                Column = col;
            }
            else if (handleIndex == 1)
            {
                Radius = Math.Max(1, Distance(Row, Column, row, col));
            }
        }

        protected override double DistanceToHandle(int handleIndex, double row, double col)
        {
            if (handleIndex == 0)
            {
                return Distance(row, col, Row, Column);
            }
            if (handleIndex == 1)
            {
                return Distance(row, col, Row, Column + Radius);
            }
            return double.MaxValue;
        }
    }
}
