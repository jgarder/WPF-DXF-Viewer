using System.Collections.Generic;

namespace DXFLib
{
    public class DXFEntity
    {
        public string EntityType { get; set; }

        public string Handle { get; set; }

        private List<string> classhierarchy = new List<string>();

        public List<string> ClassHierarchy { get { return classhierarchy; } }

        public bool IsInPaperSpace { get; set; }

        public string LayerName { get; set; }

        public string LineType { get; set; }

        /// <summary>
        /// AutoCAD Color Index: 0 = ByBlock, 256 (or absent) = ByLayer, otherwise a
        /// concrete palette index. Use <see cref="EffectiveColorNumber"/> to tell an
        /// absent group code 62 (ByLayer) apart from an explicit 0 (ByBlock).
        /// </summary>
        public int ColorNumber { get; set; } = 256;

        /// <summary>
        /// True when the entity carried an explicit group code 62. When false the entity
        /// colour is ByLayer, which is what DXF specifies for an absent 62.
        /// </summary>
        public bool ColorNumberWasSpecified { get; set; }

        /// <summary>
        /// The colour AutoCAD would use: the explicit index when one was written, and
        /// ByLayer (256) when group code 62 was absent.
        /// </summary>
        public int EffectiveColorNumber => ColorNumberWasSpecified ? ColorNumber : 256;

        /// <summary>
        /// True-colour value from group codes 420 (24-bit) or 430 (name), when present.
        /// A value here overrides <see cref="ColorNumber"/>.
        /// </summary>
        public int? TrueColor { get; set; }

        /// <summary>True when the entity carried an explicit group code 48.</summary>
        public bool LineTypeScaleWasSpecified { get; set; }

        public double LineTypeScale { get; set; } = 1.0;

        public bool IsInvisible { get; set; }

        /// <summary>Lineweight in hundredths of a millimetre (group code 370).</summary>
        public int? LineWeight { get; set; }

        public virtual bool HasChildren { get { return (children != null && children.Count > 0); } }

        private List<DXFEntity> children = new List<DXFEntity>();

        public List<DXFEntity> Children { get { return children; } }

        public virtual void ParseGroupCode(int groupcode, string value)
        {
            switch (groupcode)
            {
                case 0:
                    EntityType = value;
                    break;
                case 5:
                    Handle = value;
                    break;
                case 100:
                    ClassHierarchy.Add(value);
                    break;
                case 67:
                    IsInPaperSpace = int.Parse(value) == 1;
                    break;
                case 8:
                    LayerName = value;
                    break;
                case 6:
                    LineType = value;
                    break;
                case 62:
                    ColorNumber = int.Parse(value);
                    ColorNumberWasSpecified = true;
                    break;
                case 420:
                    TrueColor = int.Parse(value);
                    break;
                case 48:
                    LineTypeScale = double.Parse(value);
                    LineTypeScaleWasSpecified = true;
                    break;
                case 60:
                    IsInvisible = int.Parse(value) == 1;
                    break;
                case 370:
                    LineWeight = int.Parse(value);
                    break;
            }
        }
    }
}
