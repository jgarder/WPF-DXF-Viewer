namespace DXFLib
{
    /// <summary>
    /// TOLERANCE (called a "feature control frame" or GD&amp;T frame) as stored in DXF.
    /// The boxed text lives in group code 1 using MTEXT-style inline codes such as
    /// <c>{\Fgdt;j}</c> for a symbol and <c>^J</c> to start a new frame.
    /// </summary>
    [Entity("TOLERANCE")]
    public class DXFTolerance : DXFEntity
    {
        /// <summary>
        /// The feature control frame text, using MTEXT inline formatting. Lines are
        /// separated by the MTEXT paragraph marker \P.
        /// </summary>
        public string Text { get; set; }

        public string DimensionStyle { get; set; }

        private DXFPoint insertion = new DXFPoint();

        public DXFPoint InsertionPoint { get { return insertion; } }

        private DXFPoint extrusion = new DXFPoint() { X = 0, Y = 0, Z = 1 };

        public DXFPoint ExtrusionDirection { get { return extrusion; } }

        private DXFPoint direction = new DXFPoint();

        public DXFPoint Direction { get { return direction; } }

        public override void ParseGroupCode(int groupcode, string value)
        {
            base.ParseGroupCode(groupcode, value);
            switch (groupcode)
            {
                case 1:
                    Text = value;
                    break;
                case 3:
                    DimensionStyle = value;
                    break;
                case 10:
                    InsertionPoint.X = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 20:
                    InsertionPoint.Y = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 30:
                    InsertionPoint.Z = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 11:
                    Direction.X = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 21:
                    Direction.Y = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 31:
                    Direction.Z = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 210:
                    ExtrusionDirection.X = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 220:
                    ExtrusionDirection.Y = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 230:
                    ExtrusionDirection.Z = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }
    }
}
