using System.Globalization;

namespace DXFLib
{
    [Entity("TEXT")]
    public class DXFText : DXFGenericEntity
    {
        public static readonly DXFPoint DefaultAlignmentPoint = new DXFPoint();

        public string Text { get; set; }

        /// <summary>Name of the text style (group code 7).</summary>
        public string TextStyleName { get; set; }

        public double Thickness { get; set; }

        public double TextHeight { get; set; }

        public double TextRotation { get; set; }

        private DXFPoint start = new DXFPoint();

        /// <summary>The insertion point, or the alignment point when one is set.</summary>
        public DXFPoint Start { get { return start; } }

        public override void ParseGroupCode(int groupcode, string value)
        {
            base.ParseGroupCode(groupcode, value);
            switch (groupcode)
            {
                case 1:
                    Text = value;
                    break;
                case 7:
                    TextStyleName = value;
                    break;
                case 10:
                    start.X = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 20:
                    start.Y = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 39:
                    Thickness = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 40:
                    TextHeight = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 50:
                    TextRotation = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    base.ParseGroupCode(groupcode, value);
                    break;
            }
        }
    }
}
