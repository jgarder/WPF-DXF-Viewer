using System;
using System.Collections.Generic;
using System.Globalization;

namespace DXFLib
{
    /// <summary>
    /// HATCH as described in the AutoCAD DXF reference (entity section).
    ///
    /// A HATCH has one or more boundary paths. Each path is either
    ///
    ///   * an "edge data" path (bit 2 of group code 92 clear), where every boundary
    ///     segment is its own edge object (group code 72 = edge type), or
    ///   * a "polyline" path (bit 2 of group code 92 set), which is a single closed
    ///     polyline whose vertices arrive as 10/20 pairs with an optional bulge per
    ///     vertex in group code 42 and a closed flag in group code 73.
    ///
    /// Both forms are normalised here into <see cref="HatchBoundaryPath.Edges"/> and,
    /// for polyline paths, into <see cref="HatchBoundaryPath.Vertices"/>.
    /// </summary>
    [Entity("HATCH")]
    public class DXFHatchEntity : DXFEntity
    {
        private DXFPoint elevationPoint = new DXFPoint() { X = 0, Y = 0, Z = 0 };

        public DXFPoint ElevationPoint { get { return elevationPoint; } }

        public string PatternName { get; set; }

        /// <summary>True for a solid fill (group code 70 = 1).</summary>
        public bool IsSolidFill { get; set; }

        /// <summary>Associativity flag (group code 71).</summary>
        public bool IsAssociative { get; set; }

        public int NumberOfBoundaryPaths { get; set; }

        /// <summary>Hatch style (group code 75): 0 = odd parity, 1 = outermost, 2 = ignore.</summary>
        public int HatchStyle { get; set; }

        /// <summary>Pattern type (group code 76): 0 = user defined, 1 = predefined, 2 = custom.</summary>
        public int PatternType { get; set; }

        /// <summary>Pattern angle in degrees for user-defined patterns (group code 52).</summary>
        public double PatternAngle { get; set; }

        /// <summary>Pattern scale (group code 41).</summary>
        public double PatternScale { get; set; }

        /// <summary>Double flag (group code 77): the pattern is a double hatch.</summary>
        public bool IsDouble { get; set; }

        /// <summary>Number of pattern definition lines (group code 78).</summary>
        public int NumberOfPatternLines { get; set; }

        /// <summary>Number of seed points (group code 98).</summary>
        public int NumberOfSeedPoints { get; set; }

        public double PixelSize { get; set; }

        private DXFPoint extrusion = new DXFPoint() { X = 0, Y = 0, Z = 1 };

        public DXFPoint ExtrusionDirection { get { return extrusion; } }

        private List<HatchBoundaryPath> boundaryPaths = new List<HatchBoundaryPath>();

        public List<HatchBoundaryPath> BoundaryPaths { get { return boundaryPaths; } }

        private List<HatchPatternLine> patternLines = new List<HatchPatternLine>();

        public List<HatchPatternLine> PatternLines { get { return patternLines; } }

        private List<DXFPoint> seedPoints = new List<DXFPoint>();

        public List<DXFPoint> SeedPoints { get { return seedPoints; } }

        /// <summary>True when a gradient fill is present (group code 450 = 1).</summary>
        public bool IsGradient { get; set; }

        public string GradientName { get; set; }

        public double GradientAngle { get; set; }

        public double GradientShift { get; set; }

        private List<double> gradientTint = new List<double>();

        /// <summary>Gradient tint values in the order they appear in the file.</summary>
        public List<double> GradientTintValues { get { return gradientTint; } }

        // Parser state
        private HatchBoundaryPath currentPath;
        private HatchEdge currentEdge;
        private HatchPatternLine currentPatternLine;
        private int pendingPatternLineCount;
        private int patternLinesParsed;
        private bool inPatternDefinition;

        /// <summary>
        /// True once gc 98 has been read, which means every remaining 10/20 pair is a seed
        /// point rather than a boundary vertex or an edge start point.
        /// </summary>
        private bool inSeedPoints;

        /// <summary>The vertex currently being read for a polyline boundary path.</summary>
        private HatchVertex pendingVertex;

        public override void ParseGroupCode(int groupcode, string value)
        {
            base.ParseGroupCode(groupcode, value);

            switch (groupcode)
            {
                case 47:
                    PixelSize = ParseDouble(value);
                    break;

                case 10:
                    if (inPatternDefinition) break;
                    // Everything from gc 98 to the end of the entity is seed points, which
                    // reuse group codes 10/20. They must not be routed to a boundary at all,
                    // or the first seed overwrites the last edge's start point and the
                    // outline collapses towards the origin.
                    if (inSeedPoints) break;
                    if (currentPath != null && currentPath.IsPolylinePath) SetVertexX(ParseDouble(value));
                    else if (currentPath != null && currentPath.HasEdgeData)
                    {
                        BeginEdgeIfNeeded();
                        currentEdge.StartValue = ParseDouble(value);
                    }
                    else SetElevationX(ParseDouble(value));
                    break;

                case 20:
                    if (inPatternDefinition) break;
                    if (inSeedPoints) break;
                    if (currentPath != null && currentPath.IsPolylinePath) SetVertexY(ParseDouble(value));
                    else if (currentPath != null && currentPath.HasEdgeData) SetEdgeStartY(ParseDouble(value));
                    else SetElevationY(ParseDouble(value));
                    break;

                case 30:
                    if (inPatternDefinition) break;
                    if (inSeedPoints) break;
                    if (currentPath != null && currentPath.IsPolylinePath) { /* 2D only */ }
                    else if (currentPath != null && currentPath.HasEdgeData) SetEdgeStartZ(ParseDouble(value));
                    else elevationPoint.Z = ParseDouble(value);
                    break;

                case 2:
                    PatternName = value;
                    break;

                case 70:
                    IsSolidFill = ParseInt(value) != 0;
                    break;

                case 71:
                    IsAssociative = ParseInt(value) != 0;
                    break;

                case 91:
                    NumberOfBoundaryPaths = ParseInt(value);
                    break;

                case 75:
                    HatchStyle = ParseInt(value);
                    break;

                case 76:
                    PatternType = ParseInt(value);
                    break;

                case 52:
                    PatternAngle = ParseDouble(value);
                    break;

                case 41:
                    PatternScale = ParseDouble(value);
                    break;

                case 77:
                    IsDouble = ParseInt(value) != 0;
                    break;

                case 78:
                    NumberOfPatternLines = ParseInt(value);
                    pendingPatternLineCount = NumberOfPatternLines;
                    inPatternDefinition = true;
                    break;

                case 53:
                    // gc 53 starts a new pattern line (a hatch may define several, e.g. a
                    // crosshatch), so this must open a fresh line every time rather than
                    // only when none is open yet.
                    BeginNewPatternLine();
                    currentPatternLine.Angle = ParseDouble(value);
                    break;

                case 43:
                    if (currentPatternLine != null) currentPatternLine.BasePointX = ParseDouble(value);
                    break;

                case 44:
                    if (currentPatternLine != null) currentPatternLine.BasePointY = ParseDouble(value);
                    break;

                case 45:
                    if (currentPatternLine != null) currentPatternLine.OffsetX = ParseDouble(value);
                    break;

                case 46:
                    if (currentPatternLine != null) currentPatternLine.OffsetY = ParseDouble(value);
                    break;

                case 79:
                    if (currentPatternLine != null) currentPatternLine.DashCount = ParseInt(value);
                    break;

                case 49:
                    BeginPatternLine();
                    currentPatternLine.Dashes.Add(ParseDouble(value));
                    break;

                case 92:
                    // Boundary path type flag. Bit 2 = polyline path.
                    currentPath = new HatchBoundaryPath { PathType = ParseInt(value) };
                    boundaryPaths.Add(currentPath);
                    currentEdge = null;
                    pendingVertex = null;
                    break;

                case 93:
                    // Vertex count for a polyline path, edge count otherwise. Only an
                    // edge-data path carries edges; a polyline path must not be flagged,
                    // or its 10/20 pairs are read as edge start points instead of vertices.
                    if (currentPath != null)
                    {
                        if (currentPath.IsPolylinePath)
                        {
                            currentPath.VertexCount = ParseInt(value);
                        }
                        else
                        {
                            currentPath.HasEdgeData = true;
                            currentPath.EdgeCount = ParseInt(value);
                        }
                    }
                    break;

                case 72:
                    // Vertex flag for a polyline path, edge type otherwise. Each edge of an
                    // edge-data path starts with its own 72, so this always begins a new
                    // edge: reusing the previous one would lose every edge but the last.
                    //
                    // An edge can only exist after 93 has declared the path's edge count, so
                    // a 72 that arrives before it is a stray flag (some writers emit one right
                    // after 92) and must not open an edge, or every later edge is shifted by
                    // one slot and the outline gains a phantom segment from the origin.
                    //
                    // A path also declares its edge count in 93 before any edge, and nothing
                    // ends a path but the next 92, so a trailing 72 can appear after the last
                    // declared edge. Once all declared edges are present that 72 must not
                    // start another one, for the same reason.
                    if (currentPath != null && !currentPath.IsPolylinePath)
                    {
                        if (!currentPath.HasEdgeData)
                        {
                            currentEdge = null;
                            break;
                        }

                        if (currentPath.EdgeCount > 0 && currentPath.Edges.Count >= currentPath.EdgeCount)
                        {
                            currentEdge = null;
                            break;
                        }

                        BeginEdge();
                        currentEdge.EdgeType = ParseInt(value);
                    }
                    break;

                case 73:
                    // Polyline paths: closed flag. Edge data paths: has-bulge flag.
                    if (currentPath != null)
                    {
                        if (currentPath.IsPolylinePath) currentPath.IsClosedPolyline = ParseInt(value) != 0;
                        else currentPath.HasBulge = ParseInt(value) != 0;
                    }
                    break;

                case 74:
                    if (currentPath != null && currentPath.IsPolylinePath)
                        currentPath.HasBulge = ParseInt(value) != 0;
                    break;

                case 97:
                    if (currentPath != null)
                        currentPath.HasSourceObjects = ParseInt(value) != 0;
                    break;

                case 11:
                    if (currentEdge != null) currentEdge.EndValue = ParseDouble(value);
                    break;

                case 21:
                    if (currentEdge != null) currentEdge.EndY = ParseDouble(value);
                    break;

                case 31:
                    if (currentEdge != null) currentEdge.EndZ = ParseDouble(value);
                    break;

                case 40:
                    if (currentEdge != null) currentEdge.Radius = ParseDouble(value);
                    break;

                case 50:
                    if (currentEdge != null) currentEdge.StartAngle = ParseDouble(value);
                    break;

                case 51:
                    if (currentEdge != null) currentEdge.EndAngle = ParseDouble(value);
                    break;

                case 42:
                    // Bulge: per vertex on a polyline path, per edge otherwise.
                    if (currentPath != null && currentPath.IsPolylinePath)
                    {
                        if (pendingVertex != null) pendingVertex.Bulge = ParseDouble(value);
                    }
                    else if (currentEdge != null)
                    {
                        currentEdge.Bulge = ParseDouble(value);
                    }
                    break;
                case 12:
                    if (currentEdge != null) currentEdge.ControlValue = ParseDouble(value);
                    break;

                case 22:
                    if (currentEdge != null) currentEdge.ControlY = ParseDouble(value);
                    break;

                case 98:
                    NumberOfSeedPoints = ParseInt(value);

                    // The seed-point count closes the pattern definition, so the boundary
                    // point codes that follow must be parsed normally again.
                    inPatternDefinition = false;

                    // Everything after this point is seed points, which reuse group codes
                    // 10/20 and must never be mistaken for boundary geometry.
                    inSeedPoints = true;
                    break;

                case 450:
                    IsGradient = ParseInt(value) != 0;
                    break;

                case 460:
                    GradientAngle = ParseDouble(value) * 180 / Math.PI;
                    break;

                case 461:
                    GradientShift = ParseDouble(value);
                    break;

                case 462:
                case 463:
                    gradientTint.Add(ParseDouble(value));
                    break;

                case 470:
                    GradientName = value;
                    break;

                case 210:
                    extrusion.X = ParseDouble(value);
                    break;

                case 220:
                    extrusion.Y = ParseDouble(value);
                    break;

                case 230:
                    extrusion.Z = ParseDouble(value);
                    break;
            }
        }

        /// <summary>
        /// True for an edge-data boundary path whose edges are read individually. Used to
        /// decide whether a group code 10 starts a new edge or a new polyline vertex.
        /// </summary>
        private static double ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : double.NaN;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;
        }

        private void BeginVertex()
        {
            if (currentPath == null) return;

            // A polyline path declares its vertex count in group code 93. The seed points
            // at the end of the entity also use 10/20, so a pair that would exceed the
            // declared count must not open a vertex. A vertex is only complete once its
            // 20 has arrived, so the check is "all declared vertices already have a Y".
            if (PolylineOutlineComplete())
            {
                pendingVertex = null;
                return;
            }

            pendingVertex = currentPath.AddVertex();
        }

        private void SetVertexX(double value)
        {
            BeginVertex();
            if (pendingVertex != null) pendingVertex.Point.X = value;
        }

        private void SetVertexY(double value)
        {
            // A 20 without a preceding 10 belongs to the vertex just added.
            if (pendingVertex == null) BeginVertex();
            if (pendingVertex != null) pendingVertex.Point.Y = value;
        }

        /// <summary>
        /// True once a polyline path has collected its declared number of complete
        /// vertices, which means any later 10/20 pairs are seed points, not outline points.
        /// A vertex counts as complete only when both coordinates have been read.
        /// </summary>
        private bool PolylineOutlineComplete()
        {
            if (currentPath == null || !currentPath.IsPolylinePath) return false;
            if (currentPath.VertexCount <= 0) return false;
            if (currentPath.Vertices.Count < currentPath.VertexCount) return false;

            // The still-open vertex must have its Y too, otherwise the trailing 20 of the
            // last declared vertex would be discarded as a seed point.
            return pendingVertex == null
                || (pendingVertex.Point.Y.HasValue && !double.IsNaN(pendingVertex.Point.Y.Value));
        }

        private void SetElevationX(double value) => elevationPoint.X = value;

        private void SetElevationY(double value) => elevationPoint.Y = value;

        private void SetEdgeStartY(double value)
        {
            BeginEdgeIfNeeded();
            currentEdge.StartY = value;
        }

        private void SetEdgeStartZ(double value)
        {
            BeginEdgeIfNeeded();
            currentEdge.StartZ = value;
        }

        private void BeginEdgeIfNeeded()
        {
            if (currentPath == null) return;

            if (currentEdge == null)
            {
                BeginEdge();
            }
        }

        /// <summary>
        /// Starts a new boundary edge unconditionally. Group code 72 introduces every edge
        /// of an edge-data path, so it must never reuse the edge already in progress.
        /// </summary>
        private void BeginEdge()
        {
            if (currentPath == null) return;

            currentEdge = new HatchEdge();
            currentPath.Edges.Add(currentEdge);
        }

        private void BeginPatternLine()
        {
            if (currentPatternLine != null) return;
            if (pendingPatternLineCount > 0 && patternLinesParsed >= pendingPatternLineCount) return;

            currentPatternLine = new HatchPatternLine();
            patternLines.Add(currentPatternLine);
            patternLinesParsed++;
        }

        /// <summary>
        /// Opens a new pattern line, whatever is currently open. Group code 53 begins a
        /// pattern line definition, so a hatch with several lines (a crosshatch, for
        /// example) keeps appending one line per 53.
        /// </summary>
        private void BeginNewPatternLine()
        {
            if (pendingPatternLineCount > 0 && patternLinesParsed >= pendingPatternLineCount) return;

            currentPatternLine = new HatchPatternLine();
            patternLines.Add(currentPatternLine);
            patternLinesParsed++;
        }

        /// <summary>
        /// A hatch boundary edge. Only the fields relevant to the edge type are set.
        /// </summary>
        public class HatchEdge
        {
            /// <summary>
            /// Edge type: 1 = line, 2 = circular arc, 3 = elliptical arc, 4 = spline.
            /// </summary>
            public int EdgeType { get; set; }

            public double StartValue { get; set; }

            public double StartY { get; set; }

            public double StartZ { get; set; }

            public double EndValue { get; set; }

            public double EndY { get; set; }

            public double EndZ { get; set; }

            public double Radius { get; set; }

            public double StartAngle { get; set; }

            public double EndAngle { get; set; }

            /// <summary>0 = counter-clockwise, 1 = clockwise.</summary>
            public int IsCounterClockwise { get; set; }

            public double Bulge { get; set; }

            /// <summary>Elliptical arc / spline control point x.</summary>
            public double ControlValue { get; set; }

            public double ControlY { get; set; }

            public double Length { get; set; }

            private DXFPoint center = new DXFPoint();

            public DXFPoint Center { get { return center; } }

            private DXFPoint majorAxisEndPoint = new DXFPoint();

            public DXFPoint MajorAxisEndPoint { get { return majorAxisEndPoint; } }

            public double MinorToMajorRatio { get; set; }

            public double StartParam { get; set; }

            public double EndParam { get; set; }
        }

        /// <summary>
        /// One boundary path. When bit 2 of <see cref="PathType"/> is set the path is a
        /// single closed polyline and <see cref="Vertices"/> holds its outline.
        /// </summary>
        public class HatchBoundaryPath
        {
            public int PathType { get; set; }

            /// <summary>True once group code 93 has been seen.</summary>
            public bool HasEdgeData { get; set; }

            /// <summary>Number of edges, or number of vertices for a polyline path.</summary>
            public int EdgeCount { get; set; }

            /// <summary>
            /// Declared vertex count of a polyline path (group code 93). Once this many
            /// vertices have been read, any further 10/20 pairs belong to the seed points
            /// that close the entity and must not be collected as outline vertices.
            /// </summary>
            public int VertexCount { get; set; }

            public bool HasBulge { get; set; }

            public bool IsClosedPolyline { get; set; }

            public bool HasSourceObjects { get; set; }

            private List<HatchEdge> edges = new List<HatchEdge>();

            /// <summary>Edge-data paths only; polyline paths use <see cref="Vertices"/>.</summary>
            public List<HatchEdge> Edges { get { return edges; } }

            private List<HatchVertex> vertices = new List<HatchVertex>();

            /// <summary>Outline of a polyline boundary path, in file order.</summary>
            public List<HatchVertex> Vertices { get { return vertices; } }

            public bool IsPolylinePath { get { return (PathType & 2) != 0; } }

            public bool IsDerivedPath { get { return (PathType & 1) != 0; } }

            public bool IsExternal { get { return (PathType & 4) != 0; } }

            internal HatchVertex AddVertex()
            {
                HatchVertex vertex = new HatchVertex { Point = new DXFPoint() };
                vertices.Add(vertex);

                return vertex;
            }
        }

        /// <summary>One vertex of a polyline boundary path, with its bulge value.</summary>
        public class HatchVertex
        {
            public DXFPoint Point { get; set; }

            /// <summary>Bulge to the next vertex; 0 for a straight segment.</summary>
            public double Bulge { get; set; }
        }

        /// <summary>
        /// One pattern definition line: angle, base point, offset and the dash pattern.
        /// </summary>
        public class HatchPatternLine
        {
            public double Angle { get; set; }

            public double BasePointX { get; set; }

            public double BasePointY { get; set; }

            public double OffsetX { get; set; }

            public double OffsetY { get; set; }

            public int DashCount { get; set; }

            private List<double> dashes = new List<double>();

            public List<double> Dashes { get { return dashes; } }
        }
    }
}
