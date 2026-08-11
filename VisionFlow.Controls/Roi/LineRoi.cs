using HalconDotNet;

namespace VisionFlow.Controls.Roi
{
    public sealed class LineRoi : RoiShape
    {
        public double Row1 { get; set; }
        public double Column1 { get; set; }
        public double Row2 { get; set; }
        public double Column2 { get; set; }

        public override RoiKind Kind => RoiKind.Line;
        public override int HandleCount => 3;

        public LineRoi(string name, double row1, double column1, double row2, double column2) : base(name)
        {
            Row1 = row1;
            Column1 = column1;
            Row2 = row2;
            Column2 = column2;
        }

        public override void Draw(HWindow window, bool active)
        {
            window.SetColor(active ? ActiveColor : Color);
            window.SetDraw("margin");
            window.DispLine(Row1, Column1, Row2, Column2);
            if (active)
            {
                DrawHandle(window, Row1, Column1);
                DrawHandle(window, Row2, Column2);
                DrawHandle(window, (Row1 + Row2) / 2.0, (Column1 + Column2) / 2.0);
            }
        }

        public override HRegion ToRegion()
        {
            HOperatorSet.GenRegionLine(out HObject regionObject, Row1, Column1, Row2, Column2);
            return new HRegion(regionObject);
        }

        public override double[] GetModelData()
        {
            return new[] { Row1, Column1, Row2, Column2 };
        }

        public override void MoveByHandle(int handleIndex, double row, double col)
        {
            if (handleIndex == 0)
            {
                Row1 = row;
                Column1 = col;
            }
            else if (handleIndex == 1)
            {
                Row2 = row;
                Column2 = col;
            }
            else if (handleIndex == 2)
            {
                double midRow = (Row1 + Row2) / 2.0;
                double midCol = (Column1 + Column2) / 2.0;
                double rowOffset = row - midRow;
                double colOffset = col - midCol;
                Row1 += rowOffset;
                Row2 += rowOffset;
                Column1 += colOffset;
                Column2 += colOffset;
            }
        }

        protected override double DistanceToHandle(int handleIndex, double row, double col)
        {
            switch (handleIndex)
            {
                case 0:
                    return Distance(row, col, Row1, Column1);
                case 1:
                    return Distance(row, col, Row2, Column2);
                case 2:
                    return Distance(row, col, (Row1 + Row2) / 2.0, (Column1 + Column2) / 2.0);
                default:
                    return double.MaxValue;
            }
        }
    }
}
