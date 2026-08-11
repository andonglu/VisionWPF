using System;
using HalconDotNet;

namespace VisionFlow.Controls.Roi
{
    public sealed class Rectangle2Roi : RoiShape
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }

        public override RoiKind Kind => RoiKind.Rectangle2;
        public override int HandleCount => 6;

        public Rectangle2Roi(string name, double row, double column, double phi, double length1, double length2)
            : base(name)
        {
            Row = row;
            Column = column;
            Phi = phi;
            Length1 = Math.Max(1, length1);
            Length2 = Math.Max(1, length2);
        }

        public override void Draw(HWindow window, bool active)
        {
            window.SetColor(active ? ActiveColor : Color);
            window.SetDraw("margin");
            window.DispRectangle2(Row, Column, Phi, Length1, Length2);
            if (!active)
            {
                return;
            }

            for (int i = 0; i < HandleCount; i++)
            {
                GetHandle(i, out double row, out double col);
                DrawHandle(window, row, col);
            }
            GetHandle(5, out double arrowRow, out double arrowCol);
            window.DispArrow(Row, Column, arrowRow, arrowCol, 5.0);
        }

        public override HRegion ToRegion()
        {
            var region = new HRegion();
            region.GenRectangle2(Row, Column, Phi, Length1, Length2);
            return region;
        }

        public override double[] GetModelData()
        {
            return new[] { Row, Column, Phi, Length1, Length2 };
        }

        public override void MoveByHandle(int handleIndex, double row, double col)
        {
            if (handleIndex == 4)
            {
                Row = row;
                Column = col;
                return;
            }

            double cos = Math.Cos(Phi);
            double sin = Math.Sin(Phi);
            double dRow = row - Row;
            double dCol = col - Column;
            double local1 = dCol * cos + dRow * sin;
            double local2 = -dCol * sin + dRow * cos;

            if (handleIndex == 5)
            {
                Phi = Math.Atan2(dRow, dCol);
                return;
            }

            Length1 = Math.Max(1, Math.Abs(local1));
            Length2 = Math.Max(1, Math.Abs(local2));
        }

        protected override double DistanceToHandle(int handleIndex, double row, double col)
        {
            GetHandle(handleIndex, out double handleRow, out double handleCol);
            return Distance(row, col, handleRow, handleCol);
        }

        private void GetHandle(int index, out double row, out double col)
        {
            double local1 = 0;
            double local2 = 0;
            switch (index)
            {
                case 0:
                    local1 = -Length1;
                    local2 = -Length2;
                    break;
                case 1:
                    local1 = Length1;
                    local2 = -Length2;
                    break;
                case 2:
                    local1 = Length1;
                    local2 = Length2;
                    break;
                case 3:
                    local1 = -Length1;
                    local2 = Length2;
                    break;
                case 4:
                    row = Row;
                    col = Column;
                    return;
                case 5:
                    local1 = Length1 * 1.3;
                    local2 = 0;
                    break;
            }

            double cos = Math.Cos(Phi);
            double sin = Math.Sin(Phi);
            col = Column + local1 * cos - local2 * sin;
            row = Row + local1 * sin + local2 * cos;
        }
    }
}
