using System;
using System.Globalization;

namespace DXFLib
{
    public class DXFStyleRecord : DXFRecord
    {
        public string StyleName { get; set; }

        public double FixedHeight { get; set; }

        /// <summary>Horizontal expansion factor; 1 means the font's natural width.</summary>
        public double WidthFactor { get; set; } = 1;

        /// <summary>Italic slant in degrees.</summary>
        public double ObliqueAngle { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1}) ({2})", StyleName, FixedHeight, WidthFactor);
        }

        [Flags]
        public enum TextGenerationFlags
        {
            None = 0,
            MirroredX = 2,
            MirroredY = 4
        }

        public TextGenerationFlags GenerationFlags { get; set; }

        public double LastUsedHeight { get; set; }

        /// <summary>
        /// Primary font: either a TrueType family name (e.g. "Arial") or an SHX file
        /// (e.g. "txt.shx"). SHX fonts are stroke fonts and have no system equivalent.
        /// </summary>
        public string FontFileName { get; set; }

        public string BigFontFileName { get; set; }
    }

    class DXFStyleRecordParser : DXFRecordParser
    {
        DXFStyleRecord _record;

        protected override DXFRecord currentRecord
        {
            get { return _record; }
        }

        protected override void createRecord(DXFDocument doc)
        {
            _record = new DXFStyleRecord();
            doc.Tables.Styles.Add(_record);
        }

        public override void ParseGroupCode(DXFDocument doc, int groupcode, string value)
        {
            base.ParseGroupCode(doc, groupcode, value);
            switch (groupcode)
            {
                case 2:
                    _record.StyleName = value;
                    break;
                case 40:
                    _record.FixedHeight = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 41:
                    _record.WidthFactor = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 50:
                    _record.ObliqueAngle = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 71:
                    // Group code 71 is a bit mask, not an enum name.
                    _record.GenerationFlags = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags)
                        ? (DXFStyleRecord.TextGenerationFlags)flags
                        : DXFStyleRecord.TextGenerationFlags.None;
                    break;
                case 42:
                    _record.LastUsedHeight = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case 3:
                    _record.FontFileName = value;
                    break;
                case 4:
                    _record.BigFontFileName = value;
                    break;
            }
        }
    }

}
