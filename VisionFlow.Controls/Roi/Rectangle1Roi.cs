using HalconDotNet;

namespace VisionFlow.Controls.Roi
{
    public sealed class Rectangle1Roi : RoiShape
    {
        public double Row1 { get; set; }
        public double Column1 { get; set; }
        public double Row2 { get; set; }
        public double Column2 { get; set; }

        public override RoiKind Kind => RoiKind.Rectangle1;
        public override int HandleCount => 5;

        public Rectangle1Roi(string name, double row1, double column1, double row2, double column2) : base(name)
        {
            Row1 = row1;
            Column1 = column1;
            Row2 = row2;
            Column2 = column2;
        }

        public override void Draw(HWindow window, bool active)
        {
            Normalize();
            window.SetColor(active ? ActiveColor : Color);
            window.SetDraw("margin");
            window.DispRectangle1(Row1, Column1, Row2, Column2);
            if (!active)
            {
                return;
            }

            DrawHandle(window, Row1, Column1);
            DrawHandle(window, Row1, Column2);
            DrawHandle(window, Row2, Column2);
            DrawHandle(window, Row2, Column1);
            DrawHandle(window, (Row1 + Row2) / 2.0, (Column1 + Column2) / 2.0);
        }

        public override HRegion ToRegion()
        {
            Normalize();
            var region = new HRegion();
            region.GenRectangle1(Row1, Column1, Row2, Column2);
            return region;
        }

        public override double[] GetModelData()
        {
            Normalize();
            return new[] { Row1, Column1, Row2, Column2 };
        }

        public override void MoveByHandle(int handleIndex, double row, double col)
        {
            double oldMidRow = (Row1 + Row2) / 2.0;
            double oldMidCol = (Column1 + Column2) / 2.0;
            switch (handleIndex)
            {
                case 0:
                    Row1 = row;
                    Column1 = col;
                    break;
                case 1:
                    Row1 = row;
                    Column2 = col;
                    break;
                case 2:
                    Row2 = row;
                    Column2 = col;
                    break;
                case 3:
                    Row2 = row;
                    Column1 = col;
                    break;
                case 4:
                    double rowOffset = row - oldMidRow;
                    double colOffset = col - oldMidCol;
                    Row1 += rowOffset;
                    Row2 += rowOffset;
                    Column1 += colOffset;
                    Column2 += colOffset;
                    break;
            }
        }

        protected override double DistanceToHandle(int handleIndex, double row, double col)
        {
            Normalize();
            switch (handleIndex)
            {
                case 0:
                    return Distance(row, col, Row1, Column1);
                case 1:
                    return Distance(row, col, Row1, Column2);
                case 2:
                    return Distance(row, col, Row2, Column2);
                case 3:
                    return Distance(row, col, Row2, Column1);
                case 4:
                    return Distance(row, col, (Row1 + Row2) / 2.0, (Column1 + Column2) / 2.0);
                default:
                    return double.MaxValue;
            }
        }

        private void Normalize()
        {
            if (Row1 > Row2)
            {
                double tmp = Row1;
                Row1 = Row2;
                Row2 = tmp;
            }
            if (Column1 > Column2)
            {
                double tmp = Column1;
                Column1 = Column2;
                Column2 = tmp;
            }
        }
    }
}
