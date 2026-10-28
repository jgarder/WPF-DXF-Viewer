using DXFLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DXFViewer
{
    /// <summary>
    /// A loaded DXF document together with the statistics of what was drawn, so callers
    /// can tell the user when entity types were skipped.
    /// </summary>
    public sealed class DxfLoadResult
    {
        public DXFDocument Document { get; internal set; }

        public GeometryWrapper Drawing { get; internal set; }

        /// <summary>Number of DXF entities converted into drawable geometry.</summary>
        public int RenderedEntityCount { get; internal set; }

        /// <summary>Number of WPF geometries produced.</summary>
        public int GeometryCount { get; internal set; }

        /// <summary>Entity types present in the file that the viewer cannot draw.</summary>
        public IReadOnlyDictionary<string, int> UnsupportedEntityTypes { get; internal set; }

        /// <summary>Warnings raised while converting.</summary>
        public IReadOnlyList<DxfDiagnostic> Diagnostics { get; internal set; }

        public bool HasUnsupportedEntities => UnsupportedEntityTypes != null && UnsupportedEntityTypes.Count > 0;

        /// <summary>
        /// A single line describing anything that was not drawn, or null when the file
        /// drew completely.
        /// </summary>
        public string GetUnsupportedSummary()
        {
            if (!HasUnsupportedEntities) return null;

            IEnumerable<string> parts = UnsupportedEntityTypes
                .OrderByDescending(x => x.Value)
                .Select(x => $"{x.Key} ({x.Value})");

            return "Not rendered: " + string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The drawable output for one DXF document.
    /// </summary>
    public sealed class GeometryWrapper
    {
        public GeometryWrapper()
        {
        }

        public GeometryWrapper(IList<PositionedGeometry> items)
        {
            if (items == null) return;

            foreach (PositionedGeometry item in items) Items.Add(item);
        }

        /// <summary>The geometry to render, with each item already positioned.</summary>
        public IList<PositionedGeometry> Items { get; } = new List<PositionedGeometry>();
    }

    /// <summary>
    /// One drawable primitive: a geometry with the brush and pen it should be drawn with.
    /// </summary>
    public sealed class PositionedGeometry
    {
        public PositionedGeometry(System.Windows.Media.Geometry geometry, System.Windows.Media.Brush stroke, bool isFilled, System.Windows.Media.Brush fill)
        {
            Geometry = geometry;
            Stroke = stroke;
            IsFilled = isFilled;
            Fill = fill;
        }

        public System.Windows.Media.Geometry Geometry { get; }

        public System.Windows.Media.Brush Stroke { get; }

        public bool IsFilled { get; }

        public System.Windows.Media.Brush Fill { get; }

        /// <summary>Dash pattern in device units, or null for a continuous line.</summary>
        public double[] Dashes { get; set; }

        /// <summary>Draw order within the document.</summary>
        public int Order { get; set; }
    }

    /// <summary>
    /// A path drawn with a dash pattern, remembering the pattern in drawing units so it
    /// can be rescaled whenever the stroke thickness changes.
    /// </summary>
    internal sealed class DashPatternPath
    {
        public DashPatternPath(System.Windows.Shapes.Path path, double[] drawingUnits)
        {
            Path = path;
            DrawingUnits = drawingUnits;
        }

        public System.Windows.Shapes.Path Path { get; }

        /// <summary>Dash lengths in drawing units, as recorded in the DXF line type.</summary>
        public double[] DrawingUnits { get; }
    }
}
