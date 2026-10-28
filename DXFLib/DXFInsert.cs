using System.Globalization;

namespace DXFLib
{
    [Entity("INSERT")]
    public class DXFInsert : DXFEntity
    {
        public string BlockName { get; set; }

        private DXFPoint insertionPoint = new DXFPoint();

        public DXFPoint InsertionPoint { get { return insertionPoint; } }

        private DXFPoint scaling = new DXFPoint();

        public DXFPoint Scaling { get { return scaling; } }

        public double? RotationAngle { get; set; }

        /// <summary>Column count of a MINSERT array (group code 70).</summary>
        public int ColumnCount { get; set; } = 1;

        /// <summary>Row count of a MINSERT array (group code 71).</summary>
        public int RowCount { get; set; } = 1;

        /// <summary>Column spacing of a MINSERT array (group code 44).</summary>
        public double ColumnSpacing { get; set; }

        /// <summary>Row spacing of a MINSERT array (group code 45).</summary>
        public double RowSpacing { get; set; }

        private DXFPoint extrusionDirection = new DXFPoint() { X = 0, Y = 0, Z = 1 };

        public DXFPoint ExtrusionDirection { get { return extrusionDirection; } }

        public override void ParseGroupCode(int groupcode, string value)
        {
            base.ParseGroupCode(groupcode, value);
            switch (groupcode)
            {
                case 2:
                    BlockName = value;
                    break;
                case 10:
                    InsertionPoint.X = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 20:
                    InsertionPoint.Y = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 30:
                    InsertionPoint.Z = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 41:
                    Scaling.X = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 42:
                    Scaling.Y = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 43:
                    Scaling.Z = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 44:
                    ColumnSpacing = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 45:
                    RowSpacing = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 50:
                    RotationAngle = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 70:
                    ColumnCount = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 71:
                    RowCount = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 210:
                    extrusionDirection.X = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 220:
                    extrusionDirection.Y = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 230:
                    extrusionDirection.Z = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
            }
        }
    }
}
