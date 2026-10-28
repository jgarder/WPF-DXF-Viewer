using DXFLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DXFViewer
{
    /// <summary>
    /// Converts parsed DXF entities into positioned WPF geometries.
    ///
    /// The viewer's canvas has its Y axis pointing down while DXF's points up, so each
    /// conversion negates Y exactly once, when DXF coordinates become WPF points (see
    /// <see cref="DxfMath.ToDevice"/>). Everything below after that point is device space.
    /// </summary>
    internal sealed class DxfGeometryConverter
    {
        private const int MaxBlockDepth = 12;

        private const double MaxRayReach = 1e7;

        private readonly DXFDocument document;

        private readonly DxfRenderContext context;

        private readonly List<PositionedGeometry> output = new List<PositionedGeometry>();

        private readonly Dictionary<string, int> unsupported =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Arc flattening tolerance, derived from the drawing size.</summary>
        private readonly double tolerance;

        /// <summary>POINT marker radius, scaled to the drawing so it stays visible.</summary>
        private readonly double pointRadius;

        private int order;

        private DxfGeometryConverter(DXFDocument document, double extent, List<DxfDiagnostic> diagnostics)
        {
            this.document = document;
            context = new DxfRenderContext(document, diagnostics);

            tolerance = DxfMath.GetTolerance(extent);
            pointRadius = extent > 0 ? extent / 400.0 : 1.0;
        }

        /// <summary>Number of entities that produced visible geometry.</summary>
        public int RenderedEntityCount { get; private set; }

        /// <summary>The converted geometry, in draw order.</summary>
        public IList<PositionedGeometry> Items => output;

        /// <summary>
        /// Reads a DXF file and converts it. Returns null when the file cannot be read.
        /// </summary>
        public static DxfLoadResult Load(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName)) return null;

            DXFDocument document = new DXFDocument();
            document.Load(fileName);

            List<DXFEntity> entities = GetTopLevelEntities(document);

            List<DxfDiagnostic> diagnostics = new List<DxfDiagnostic>();

            double extent = EstimateExtent(entities);

            DxfGeometryConverter converter = new DxfGeometryConverter(document, extent, diagnostics);
            converter.Convert(entities);

            DxfLoadResult result = new DxfLoadResult
            {
                Document = document,
                RenderedEntityCount = converter.RenderedEntityCount,
                GeometryCount = converter.output.Count,
                UnsupportedEntityTypes = converter.unsupported,
                Diagnostics = diagnostics
            };

            result.Drawing = new GeometryWrapper(converter.output);

            return result;
        }

        /// <summary>
        /// Model space entities. Blocks are expanded through INSERT, and the polyline's
        /// VERTEX / SEQEND children belong to their parent rather than standing alone.
        /// </summary>
        private static List<DXFEntity> GetTopLevelEntities(DXFDocument document)
        {
            List<DXFEntity> entities = new List<DXFEntity>();

            foreach (DXFEntity entity in document.Entities)
            {
                if (entity is DXFVertex) continue;

            entities.Add(entity);
            }

            return entities;
        }

        /// <summary>
        /// Estimates the drawing size from the entity coordinates. Used only to scale
        /// tolerances and the POINT marker; fitting is the viewer's job.
        /// </summary>
        private static double EstimateExtent(List<DXFEntity> entities)
        {
            BoundsAccumulator bounds = new BoundsAccumulator();

            foreach (DXFEntity entity in entities)
                AccumulateBounds(entity, bounds);

            if (!bounds.HasValue) return 0;

            double width = bounds.MaxX - bounds.MinX;
            double height = bounds.MaxY - bounds.MinY;

            if (!width.IsUsable() || !height.IsUsable()) return 0;
            if (width > 1e12 || height > 1e12) return 0;

            return DxfMath.MaxSpan(width, height);
        }

        /// <summary>Mutable accumulator for a bounding box.</summary>
        private struct BoundsAccumulator
        {
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;

            public bool HasValue => MinX <= MaxX && MinY <= MaxY;

            public static BoundsAccumulator Empty => new BoundsAccumulator
            {
                MinX = double.MaxValue,
                MaxX = double.MinValue,
                MinY = double.MaxValue,
                MaxY = double.MinValue
            };

            public void Add(double x, double y)
            {
                if (!x.IsUsable() || !y.IsUsable()) return;

                if (x < MinX) MinX = x;
                if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y;
                if (y > MaxY) MaxY = y;
            }
        }

        private static void AccumulateBounds(DXFEntity entity, BoundsAccumulator bounds)
        {
            switch (entity)
            {
                case DXFLine line:
                    bounds.Add(line.Start.X.OrIfUnusable(0), line.Start.Y.OrIfUnusable(0));
                    bounds.Add(line.End.X.OrIfUnusable(0), line.End.Y.OrIfUnusable(0));
                    break;

                case DXFCircle circle:
                    bounds.Add(circle.Center.X.OrIfUnusable(0) - circle.Radius, circle.Center.Y.OrIfUnusable(0) - circle.Radius);
                    bounds.Add(circle.Center.X.OrIfUnusable(0) + circle.Radius, circle.Center.Y.OrIfUnusable(0) + circle.Radius);
                    break;

                case DXFArc arc:
                    bounds.Add(arc.Center.X.OrIfUnusable(0) - arc.Radius, arc.Center.Y.OrIfUnusable(0) - arc.Radius);
                    bounds.Add(arc.Center.X.OrIfUnusable(0) + arc.Radius, arc.Center.Y.OrIfUnusable(0) + arc.Radius);
                    break;

                case DXFPointEntity point:
                    bounds.Add(point.Location.X.OrIfUnusable(0), point.Location.Y.OrIfUnusable(0));
                    break;

                case DXFSolid solid:
                    foreach (DXFPoint corner in solid.Corners)
                        bounds.Add(corner.X.OrIfUnusable(0), corner.Y.OrIfUnusable(0));
                    break;

                case DXFTrace trace:
                    foreach (DXFPoint corner in trace.Corners)
                        bounds.Add(corner.X.OrIfUnusable(0), corner.Y.OrIfUnusable(0));
                    break;

                case DXF3DFace face:
                    foreach (DXFPoint corner in face.Corners)
                        bounds.Add(corner.X.OrIfUnusable(0), corner.Y.OrIfUnusable(0));
                    break;

                case DXFPolyLine polyLine:
                    foreach (DXFEntity child in polyLine.Children)
                        if (child is DXFVertex vertex)
                            bounds.Add(vertex.Location.X.OrIfUnusable(0), vertex.Location.Y.OrIfUnusable(0));
                    break;

                case DXFLWPolyLine lwPolyLine:
                    if (lwPolyLine.Elements == null) break;

                    foreach (DXFLWPolyLine.Element element in lwPolyLine.Elements)
                        bounds.Add(element.Vertex.X.OrIfUnusable(0), element.Vertex.Y.OrIfUnusable(0));
                    break;

                case DXFInsert insert:
                    bounds.Add(insert.InsertionPoint.X.OrIfUnusable(0), insert.InsertionPoint.Y.OrIfUnusable(0));
                    break;

                case DXFEllipse ellipse:
                {
                    double rx = Math.Sqrt(
                        Math.Pow(ellipse.MainAxis.X.OrIfUnusable(0), 2) +
                        Math.Pow(ellipse.MainAxis.Y.OrIfUnusable(0), 2));

                    bounds.Add(ellipse.Center.X.OrIfUnusable(0) - rx, ellipse.Center.Y.OrIfUnusable(0) - rx);
                    bounds.Add(ellipse.Center.X.OrIfUnusable(0) + rx, ellipse.Center.Y.OrIfUnusable(0) + rx);
                    break;
                }

                case DXFSpline spline:
                    foreach (DXFPoint point in spline.ControlPoints)
                        bounds.Add(point.X.OrIfUnusable(0), point.Y.OrIfUnusable(0));

                    foreach (DXFPoint point in spline.FitPoints)
                        bounds.Add(point.X.OrIfUnusable(0), point.Y.OrIfUnusable(0));
                    break;

                case DXFHatchEntity hatch:
                    if (hatch.BoundaryPaths == null) break;

                    foreach (DXFHatchEntity.HatchBoundaryPath path in hatch.BoundaryPaths)
                    {
                        if (path.Edges == null) continue;

                        foreach (DXFHatchEntity.HatchEdge edge in path.Edges)
                        {
                            bounds.Add(edge.StartValue, edge.StartY);
                            bounds.Add(edge.EndValue, edge.EndY);
                        }
                    }

                    break;

                case DXFText text:
                    bounds.Add(text.Start.X.OrIfUnusable(0), text.Start.Y.OrIfUnusable(0));
                    break;
            }
        }

        /// <summary>Converts a list of entities, expanding blocks as it goes.</summary>
        private void Convert(IEnumerable<DXFEntity> entities)
        {
            foreach (DXFEntity entity in entities)
                ConvertEntity(entity, null);
        }

        private void ConvertEntity(DXFEntity entity, Transform transform)
        {
            if (entity == null) return;

            context.EnterEntity(entity);

            if (context.ShouldSkip(entity))
            {
                if (!IsSupported(entity)) RegisterUnsupported(entity);
                return;
            }

            switch (entity)
            {
                case DXFLine line: AddLine(line, transform); break;
                case DXFArc arc: AddArc(arc, transform); break;
                case DXFCircle circle: AddCircle(circle, transform); break;
                case DXFEllipse ellipse: AddEllipse(ellipse, transform); break;
                case DXFLWPolyLine lwPolyLine: AddLWPolyLine(lwPolyLine, transform); break;
                case DXFPolyLine polyLine: AddPolyLine(polyLine, transform); break;
                case DXFSolid solid: AddQuad(solid.Corners, solid, transform, filled: true); break;
                case DXFTrace trace: AddQuad(trace.Corners, trace, transform, filled: true); break;
                case DXF3DFace face: Add3DFace(face, transform); break;
                case DXFPointEntity point: AddPoint(point, transform); break;
                case DXFRay ray: AddRay(ray, transform); break;
                case DXFSpline spline: AddSpline(spline, transform); break;
                case DXFInsert insert: AddInsert(insert, transform); break;
                case DXFText text: AddText(text, transform); break;
                case DXFMText mtext: AddGenericText(mtext, transform, 1, ReadPoint(mtext, 10, 20)); break;
                case DXFAttributeDefinition attDef: AddGenericText(attDef, transform, 3, ReadPoint(attDef, 10, 20)); break;
                case DXFAttribute attribute: AddGenericText(attribute, transform, 1, ReadPoint(attribute, 10, 20)); break;
                case DXFDimension dimension: AddDimension(dimension, transform); break;
                case DXFLeader leader: AddLeader(leader, transform); break;
                case DXFHatchEntity hatch: AddHatch(hatch, transform); break;
                case DXFTolerance tolerance: AddTolerance(tolerance, transform); break;
                case DXFMLine mline: AddMLine(mline, transform); break;
                case DXFMLeader mleader: AddMLeader(mleader, transform); break;
                case DXFShape shape: AddShape(shape, transform); break;
                case DXFImage image: AddImage(image, transform); break;

                case DXFViewPort _:
                    // VIEWPORT only describes paper space layout.
                    break;

                default:
                    // IMAGE, OLE, SHAPE, MLINE and true 3D solids need font or ACIS data
                    // that this viewer does not read; report them instead of silently
                    // dropping them.
                    RegisterUnsupported(entity);
                    break;
            }
        }

        private static bool IsSupported(DXFEntity entity)
        {
            switch (entity)
            {
                case DXFLine _:
                case DXFArc _:
                case DXFCircle _:
                case DXFEllipse _:
                case DXFLWPolyLine _:
                case DXFPolyLine _:
                case DXFSolid _:
                case DXFTrace _:
                case DXF3DFace _:
                case DXFPointEntity _:
                case DXFRay _:
                case DXFSpline _:
                case DXFInsert _:
                case DXFText _:
                case DXFMText _:
                case DXFAttributeDefinition _:
                case DXFAttribute _:
                case DXFDimension _:
                case DXFLeader _:
                case DXFHatchEntity _:
                case DXFTolerance _:
                case DXFMLine _:
                case DXFShape _:
                case DXFImage _:
                case DXFViewPort _:
                    return true;
                default:
                    return false;
            }
        }

        private void RegisterUnsupported(DXFEntity entity)
        {
            string name = entity.EntityType;
            if (string.IsNullOrWhiteSpace(name)) name = entity.GetType().Name;

            unsupported[name] = unsupported.TryGetValue(name, out int count) ? count + 1 : 1;
        }

        #region Emitting geometry

        private void Emit(Geometry geometry, DXFEntity entity, Transform transform, bool filled = false, bool worldBulge = false)
        {
            if (geometry == null) return;

            if (transform != null && !transform.Value.IsIdentity)
            {
                Geometry clone = geometry.Clone();
                clone.Transform = new MatrixTransform(transform.Value);
                geometry = clone;
            }

            Brush brush = context.ResolveBrush(entity);
            Brush fill = filled ? context.ResolveFillBrush(entity) : null;

            output.Add(new PositionedGeometry(geometry, brush, filled, fill)
            {
                Dashes = GetDashes(entity),
                Order = order++
            });

            RenderedEntityCount++;
        }

        /// <summary>
        /// Converts an entity's dash pattern into WPF dash lengths. The DXF pattern is
        /// scaled by $LTSCALE, the entity's own scale and the drawing extent so the
        /// pattern stays readable at any zoom, matching how AutoCAD applies ceil.
        /// </summary>
        private double[] GetDashes(DXFEntity entity)
        {
            DXFLineTypeRecord lineType = context.ResolveLineType(entity);
            if (lineType?.Elements == null || lineType.Elements.Count == 0) return null;

            double scale = context.ResolveLineTypeScale(entity);

            List<double> pattern = new List<double>();

            foreach (DXFLineTypeRecord.LineTypeElement element in lineType.Elements)
            {
                double length = Math.Abs(element.Length) * scale;

                // A zero-length element is a dot; give it a hairline so it renders.
                if (length < 1e-9) length = 1e-9;
                if (!length.IsUsable()) return null;

                pattern.Add(length);
            }

            if (pattern.Count < 2) return null;
            if (pattern.Sum() <= 0) return null;
            if (pattern.Count % 2 == 1) pattern.Add(pattern[pattern.Count - 1]);

            return pattern.ToArray();
        }

        #endregion

        #region Basic entities

        private void AddLine(DXFLine line, Transform transform)
        {
            if (!line.Start.X.IsUsable() || !line.Start.Y.IsUsable()) return;
            if (!line.End.X.IsUsable() || !line.End.Y.IsUsable()) return;

            Emit(
                new LineGeometry(
                    DxfMath.ToDevice(line.Start.X.Value, line.Start.Y.Value),
                    DxfMath.ToDevice(line.End.X.Value, line.End.Y.Value)),
                line,
                transform);
        }

        private void AddArc(DXFArc arc, Transform transform)
        {
            if (!(arc.Radius > 0) || !arc.Radius.IsUsable()) return;
            if (!arc.Center.X.IsUsable() || !arc.Center.Y.IsUsable()) return;

            double sweep = DxfGeometryBuilder.GetSweepRadians(arc.StartAngle, arc.EndAngle);
            int segments = DxfGeometryBuilder.GetArcSegmentCount(sweep, arc.Radius, tolerance);

            double startRadians = DxfMath.NormalizeDegrees(arc.StartAngle) * Math.PI / 180.0;

            List<Point> points = new List<Point>(segments + 1);

            for (int i = 0; i <= segments; i++)
            {
                double angle = startRadians + sweep * i / segments;

                points.Add(DxfMath.ToDevice(
                    arc.Center.X.Value + Math.Cos(angle) * arc.Radius,
                    arc.Center.Y.Value + Math.Sin(angle) * arc.Radius));
            }

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, false), arc, transform);
        }

        private void AddCircle(DXFCircle circle, Transform transform)
        {
            if (!(circle.Radius > 0) || !circle.Radius.IsUsable()) return;
            if (!circle.Center.X.IsUsable() || !circle.Center.Y.IsUsable()) return;

            Emit(
                new EllipseGeometry(
                    DxfMath.ToDevice(circle.Center.X.Value, circle.Center.Y.Value),
                    circle.Radius,
                    circle.Radius),
                circle,
                transform);
        }

        private void AddEllipse(DXFEllipse ellipse, Transform transform)
        {
            if (!ellipse.Center.X.IsUsable() || !ellipse.Center.Y.IsUsable()) return;

            double axisX = ellipse.MainAxis.X.OrIfUnusable(0);
            double axisY = ellipse.MainAxis.Y.OrIfUnusable(0);

            double majorRadius = Math.Sqrt(axisX * axisX + axisY * axisY);
            if (!(majorRadius > 0) || !majorRadius.IsUsable()) return;

            double ratio = ellipse.AxisRatio;
            if (!(ratio > 0) || !ratio.IsUsable() || ratio > 1) ratio = 1.0;

            double minorRadius = majorRadius * ratio;

            double startParam = ellipse.StartParam.IsUsable() ? ellipse.StartParam : 0;
            double endParam = ellipse.EndParam.IsUsable() ? ellipse.EndParam : 0;

            double sweep = endParam - startParam;
            while (sweep <= 0) sweep += 2 * Math.PI;
            while (sweep > 2 * Math.PI + 1e-9) sweep -= 2 * Math.PI;

            // The major axis direction is (cos, sin) in DXF space. Y is negated for the
            // canvas, which mirrors the ellipse, so its rotation is negated too.
            double rotationRadians = Math.Atan2(axisY, axisX);
            double rotationDegrees = -rotationRadians * 180.0 / Math.PI;

            Point center = DxfMath.ToDevice(ellipse.Center.X.Value, ellipse.Center.Y.Value);

            if (Math.Abs(sweep - 2 * Math.PI) < 1e-9)
            {
                EllipseGeometry full = new EllipseGeometry(center, majorRadius, minorRadius);

                if (Math.Abs(rotationDegrees) > 1e-9)
                    full.Transform = new RotateTransform(rotationDegrees, center.X, center.Y);

                Emit(full, ellipse, transform);
                return;
            }

            int segments = DxfGeometryBuilder.GetArcSegmentCount(sweep, majorRadius, tolerance);

            double cos = Math.Cos(rotationRadians);
            double sin = Math.Sin(rotationRadians);

            List<Point> points = new List<Point>(segments + 1);

            for (int i = 0; i <= segments; i++)
            {
                double t = startParam + sweep * i / segments;

                double localX = Math.Cos(t) * majorRadius;
                double localY = Math.Sin(t) * minorRadius;

                double dx = localX * cos - localY * sin;
                double dy = localX * sin + localY * cos;

                points.Add(DxfMath.ToDevice(
                    ellipse.Center.X.Value + dx,
                    ellipse.Center.Y.Value + dy));
            }

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, false), ellipse, transform);
        }

        private void AddLWPolyLine(DXFLWPolyLine polyLine, Transform transform)
        {
            List<DXFLWPolyLine.Element> elements = polyLine.Elements;
            if (elements == null || elements.Count < 2) return;

            bool closed = (polyLine.Flags & DXFLWPolyLine.FlagsEnum.closed) != 0;

            List<Point> points;

            int count = elements.Count;

            if (elements.Any(e => !e.Bulge.IsNearlyZero()))
            {
                points = new List<Point>();

                int last = closed ? count : count - 1;

                for (int i = 0; i < last; i++)
                {
                    DXFLWPolyLine.Element start = elements[i];
                    DXFLWPolyLine.Element end = elements[(i + 1) % count];

                    Point startPoint = DxfMath.ToDevice(
                        start.Vertex.X.OrIfUnusable(0),
                        start.Vertex.Y.OrIfUnusable(0));

                    Point endPoint = DxfMath.ToDevice(
                        end.Vertex.X.OrIfUnusable(0),
                        end.Vertex.Y.OrIfUnusable(0));

                    if (points.Count == 0) points.Add(startPoint);

                    // Y is negated for the canvas, which flips the bulge direction.
                    DxfGeometryBuilder.AppendBulge(
                        points,
                        startPoint,
                        endPoint,
                        -start.Bulge,
                        tolerance,
                        includeEnd: true);
                }

                if (!closed)
                {
                    DXFLWPolyLine.Element final = elements[count - 1];

                    points.Add(DxfMath.ToDevice(
                        final.Vertex.X.OrIfUnusable(0),
                        final.Vertex.Y.OrIfUnusable(0)));
                }
            }
            else
            {
                points = new List<Point>(count);

                foreach (DXFLWPolyLine.Element element in elements)
                {
                    points.Add(DxfMath.ToDevice(
                        element.Vertex.X.OrIfUnusable(0),
                        element.Vertex.Y.OrIfUnusable(0)));
                }
            }

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, closed), polyLine, transform);
        }

        private void AddPolyLine(DXFPolyLine polyLine, Transform transform)
        {
            List<DXFVertex> vertices = polyLine.Children.OfType<DXFVertex>().ToList();
            if (vertices.Count < 2) return;

            DXFPolyLine.FlagsEnum flags = polyLine.Flags;

            bool isPolyFaceMesh = (flags & DXFPolyLine.FlagsEnum.IsPolyFace) != 0;
            bool isPolygonMesh = (flags & DXFPolyLine.FlagsEnum.Is3DPolyMesh) != 0;
            bool closed = (flags & DXFPolyLine.FlagsEnum.closed) != 0;

            if (isPolyFaceMesh)
            {
                AddPolyFaceMesh(polyLine, vertices, transform);
                return;
            }

            if (isPolygonMesh)
            {
                AddPolygonMesh(polyLine, vertices, transform);
                return;
            }

            double elevation = polyLine.Elevation.OrIfUnusable(0);

            // Curve-fit control points are guidance for the fit, not part of the path.
            List<DXFVertex> pathVertices = vertices
                .Where(v => (v.Flags & DXFVertex.FlagsEnum.SplineFrameControlPoint) == 0)
                .ToList();

            if (pathVertices.Count < 2) pathVertices = vertices;

            int count = pathVertices.Count;

            List<Point> points = new List<Point>();

            if (pathVertices.Any(v => !v.Buldge.IsNearlyZero()))
            {
                int last = closed ? count : count - 1;

                for (int i = 0; i < last; i++)
                {
                    DXFVertex start = pathVertices[i];
                    DXFVertex end = pathVertices[(i + 1) % count];

                    Point startPoint = DxfMath.ToDevice(
                        start.Location.X.OrIfUnusable(0),
                        start.Location.Y.OrIfUnusable(elevation));

                    Point endPoint = DxfMath.ToDevice(
                        end.Location.X.OrIfUnusable(0),
                        end.Location.Y.OrIfUnusable(elevation));

                    if (points.Count == 0) points.Add(startPoint);

                    DxfGeometryBuilder.AppendBulge(points, startPoint, endPoint, -start.Buldge, tolerance, true);
                }

                if (!closed)
                {
                    DXFVertex final = pathVertices[count - 1];

                    points.Add(DxfMath.ToDevice(
                        final.Location.X.OrIfUnusable(0),
                        final.Location.Y.OrIfUnusable(elevation)));
                }
            }
            else
            {
                foreach (DXFVertex vertex in pathVertices)
                {
                    points.Add(DxfMath.ToDevice(
                        vertex.Location.X.OrIfUnusable(0),
                        vertex.Location.Y.OrIfUnusable(elevation)));
                }
            }

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, closed), polyLine, transform);
        }

        /// <summary>
        /// A POLYFACE mesh stores each face as a VERTEX with group codes 71..74 holding
        /// 1-based indices into the preceding vertices.
        /// </summary>
        private void AddPolyFaceMesh(DXFPolyLine polyLine, List<DXFVertex> vertices, Transform transform)
        {
            List<PositionedGeometry> faces = new List<PositionedGeometry>();

            foreach (DXFVertex face in vertices)
            {
                int[] rawIndices = face.PolyfaceIndices;
                if (rawIndices == null) continue;

                List<int> indices = new List<int>();

                foreach (int index in rawIndices)
                {
                    if (index == 0) break;
                    indices.Add(index);
                }

                if (indices.Count < 3) continue;

                List<Point> points = new List<Point>(indices.Count);

                foreach (int index in indices)
                {
                    DXFVertex target = FindFaceVertex(vertices, index);
                    if (target == null) continue;

                    points.Add(DxfMath.ToDevice(
                        target.Location.X.OrIfUnusable(0),
                        target.Location.Y.OrIfUnusable(0)));
                }

                if (points.Count < 3) continue;

                faces.Add(new PositionedGeometry(
                    DxfGeometryBuilder.CreatePolylineGeometry(points, true),
                    context.ResolveBrush(polyLine),
                    false,
                    null));
            }

            foreach (PositionedGeometry face in faces)
            {
                output.Add(face);
                RenderedEntityCount++;
            }

            if (faces.Count == 0) RegisterUnsupported(polyLine);
        }

        private static DXFVertex FindFaceVertex(List<DXFVertex> vertices, int oneBasedIndex)
        {
            int index = oneBasedIndex - 1;
            if (index < 0 || index >= vertices.Count) return null;

            return vertices[index];
        }

        private void AddPolygonMesh(DXFPolyLine polyLine, List<DXFVertex> vertices, Transform transform)
        {
            int rows = polyLine.MVertexCount;
            int columns = polyLine.NVertexCount;

            if (rows <= 0 || columns <= 0) return;
            if (vertices.Count < rows * columns) return;

            double elevation = polyLine.Elevation.OrIfUnusable(0);

            List<Point> points = new List<Point>(rows * columns);

            for (int i = 0; i < rows * columns; i++)
            {
                points.Add(DxfMath.ToDevice(
                    vertices[i].Location.X.OrIfUnusable(0),
                    vertices[i].Location.Y.OrIfUnusable(elevation)));
            }

            PathGeometry geometry = new PathGeometry();

            // One figure per grid row and per grid column, so the mesh reads as a grid.
            for (int row = 0; row < rows; row++)
            {
                PathFigure figure = new PathFigure
                {
                    StartPoint = points[row * columns],
                    IsClosed = true,
                    IsFilled = false
                };

                PolyLineSegment segment = new PolyLineSegment();
                for (int column = 1; column < columns; column++)
                    segment.Points.Add(points[row * columns + column]);

                figure.Segments.Add(segment);
                geometry.Figures.Add(figure);
            }

            for (int column = 0; column < columns; column++)
            {
                PathFigure figure = new PathFigure
                {
                    StartPoint = points[column],
                    IsClosed = true,
                    IsFilled = false
                };

                PolyLineSegment segment = new PolyLineSegment();
                for (int row = 1; row < rows; row++)
                    segment.Points.Add(points[row * columns + column]);

                figure.Segments.Add(segment);
                geometry.Figures.Add(figure);
            }

            Emit(geometry, polyLine, transform);
        }

        private void Add3DFace(DXF3DFace face, Transform transform)
        {
            List<DXFPoint> corners = new List<DXFPoint>(face.Corners);
            while (corners.Count < 4) corners.Add(corners[corners.Count - 1]);

            AddQuad(corners.ToArray(), face, transform, filled: false);
        }

        /// <summary>
        /// SOLID, TRACE and 3DFACE all store four corners in the order 0, 1, 3, 2; the
        /// fourth repeats the third for a triangle.
        /// </summary>
        private void AddQuad(DXFPoint[] corners, DXFEntity entity, Transform transform, bool filled)
        {
            if (corners == null || corners.Length < 3) return;

            foreach (DXFPoint corner in corners.Take(4))
            {
                if (!corner.X.IsUsable() || !corner.Y.IsUsable()) return;
            }

            DXFPoint p0 = corners[0];
            DXFPoint p1 = corners[1];
            DXFPoint p2 = corners.Length > 3 ? corners[3] : corners[2];
            DXFPoint p3 = corners[2];

            List<Point> points = new List<Point>
            {
                DxfMath.ToDevice(p0.X.Value, p0.Y.Value),
                DxfMath.ToDevice(p1.X.Value, p1.Y.Value),
                DxfMath.ToDevice(p2.X.Value, p2.Y.Value),
                DxfMath.ToDevice(p3.X.Value, p3.Y.Value)
            };

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, true), entity, transform, filled);
        }

        private void AddPoint(DXFPointEntity point, Transform transform)
        {
            if (!point.Location.X.IsUsable() || !point.Location.Y.IsUsable()) return;

            Point center = DxfMath.ToDevice(point.Location.X.Value, point.Location.Y.Value);

            double radius = Math.Max(pointRadius, tolerance * 10);

            GeometryGroup group = new GeometryGroup();
            group.Children.Add(new EllipseGeometry(center, radius, radius));
            group.Children.Add(new LineGeometry(
                new Point(center.X - radius * 2, center.Y),
                new Point(center.X + radius * 2, center.Y)));
            group.Children.Add(new LineGeometry(
                new Point(center.X, center.Y - radius * 2),
                new Point(center.X, center.Y + radius * 2)));

            Emit(group, point, transform);
        }

        private void AddRay(DXFRay ray, Transform transform)
        {
            if (!ray.Start.X.IsUsable() || !ray.Start.Y.IsUsable()) return;
            if (!ray.Direction.X.IsUsable() || !ray.Direction.Y.IsUsable()) return;

            double dx = ray.Direction.X.Value;
            double dy = ray.Direction.Y.Value;

            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-12 || !length.IsUsable()) return;

            // RAY and XLINE are infinite; give them a long finite reach so they read as
            // construction lines without breaking the drawing's bounding box.
            double reach = Math.Min(Math.Max(pointRadius * 400, 1), MaxRayReach);

            dx = dx / length * reach;
            dy = dy / length * reach;

            double x = ray.Start.X.Value;
            double y = ray.Start.Y.Value;

            // An XLINE extends both ways from the point; a RAY only forwards.
            bool isInfinite = ray is DXFXLine;

            Point start = isInfinite
                ? DxfMath.ToDevice(x - dx, y - dy)
                : DxfMath.ToDevice(x, y);

            Point end = DxfMath.ToDevice(x + dx, y + dy);

            Emit(new LineGeometry(start, end), ray, transform);
        }

        private void AddSpline(DXFSpline spline, Transform transform)
        {
            List<Point> controlPoints = new List<Point>();

            foreach (DXFPoint point in spline.ControlPoints)
            {
                if (!point.X.IsUsable() || !point.Y.IsUsable()) continue;
                controlPoints.Add(DxfMath.ToDevice(point.X.Value, point.Y.Value));
            }

            List<Point> fitPoints = new List<Point>();

            foreach (DXFPoint point in spline.FitPoints)
            {
                if (!point.X.IsUsable() || !point.Y.IsUsable()) continue;
                fitPoints.Add(DxfMath.ToDevice(point.X.Value, point.Y.Value));
            }

            bool closed = (spline.Flags & DXFSpline.FlagsEnum.Closed) != 0;

            List<Point> points;

            if (controlPoints.Count >= 2)
            {
                List<double> knots = spline.KnotValues?.ToList() ?? new List<double>();

                // A rational spline (flag 4) carries one weight per control point. Drop the
                // list when it does not line up, so a malformed export degrades to the non
                // rational curve instead of picking up weights from the wrong control points.
                List<double> weights = spline.WeightValues;
                if (weights != null && weights.Count != controlPoints.Count) weights = null;

                points = DxfSplineEvaluator.Sample(
                    controlPoints,
                    knots,
                    weights,
                    spline.Degree,
                    closed,
                    Math.Min(controlPoints.Count * 12, 2000));
            }
            else if (fitPoints.Count >= 2)
            {
                // No control polygon: the fit points are all we have, so draw through them.
                points = fitPoints;
            }
            else
            {
                return;
            }

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, closed), spline, transform);
        }

        #endregion

        #region Blocks

        private void AddInsert(DXFInsert insert, Transform transform)
        {
            if (context.BlockDepth >= MaxBlockDepth)
            {
                context.Warn($"Insert of block '{insert.BlockName}' is nested too deeply; skipped.");
                return;
            }

            DXFBlock block = FindBlock(insert.BlockName);

            if (block == null || !block.HasChildren)
            {
                RegisterUnsupported(insert);
                return;
            }

            if (block.IsInvisible) return;

            double rotation = insert.RotationAngle.OrIfUnusable(0);

            double scaleX = insert.Scaling.X.OrIfUnusable(1);
            double scaleY = insert.Scaling.Y.OrIfUnusable(1);

            if (scaleX.IsNearlyZero()) scaleX = 1;
            if (scaleY.IsNearlyZero()) scaleY = 1;

            double insertX = insert.InsertionPoint.X.OrIfUnusable(0);
            double insertY = insert.InsertionPoint.Y.OrIfUnusable(0);

            // Block children are authored relative to the block's base point.
            double baseX = block.BasePoint.X.OrIfUnusable(0);
            double baseY = block.BasePoint.Y.OrIfUnusable(0);

            // MINSERT arrays step along the block's own rotated axes.
            int columns = Math.Max(1, insert.ColumnCount);
            int rows = Math.Max(1, insert.RowCount);

            double columnSpacing = insert.ColumnSpacing.OrIfUnusable(0);
            double rowSpacing = insert.RowSpacing.OrIfUnusable(0);

            double radians = rotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            bool mirrored = DxfMath.IsNegativeZExtrusion(insert.ExtrusionDirection?.Z);

            int resolvedColor = context.ResolveColorIndex(insert);

            for (int column = 0; column < columns; column++)
            {
                for (int row = 0; row < rows; row++)
                {
                    // MINSERT arrays step along the block's own rotated axes.
                    double originX = insertX + column * columnSpacing * cos - row * rowSpacing * sin;
                    double originY = insertY + column * columnSpacing * sin + row * rowSpacing * cos;

                    // Build the block-to-device matrix in DXF space first, then negate Y.
                    Matrix dxf = Matrix.Identity;
                    dxf.Translate(-baseX, -baseY);

                    if (mirrored)
                    {
                        Matrix mirror = Matrix.Identity;
                        mirror.Scale(-1, 1);
                        dxf.Append(mirror);
                    }

                    dxf.Scale(scaleX, scaleY);
                    dxf.Rotate(rotation);
                    dxf.Translate(originX, originY);

                    // Canvas space: x' = x, y' = -y.
                    Matrix device = new Matrix(dxf.M11, -dxf.M12, -dxf.M21, dxf.M22, dxf.OffsetX, -dxf.OffsetY);

                    if (transform != null && !transform.Value.IsIdentity)
                        device.Prepend(transform.Value);

                    using (context.PushBlock(resolvedColor))
                    {
                        foreach (DXFEntity child in block.Children)
                        {
                            if (child is DXFAttribute) continue;
                            if (child is DXFVertex) continue;

                            ConvertEntity(child, new MatrixTransform(device));
                        }
                    }
                }
            }
        }

        private DXFBlock FindBlock(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            string trimmed = name.Trim();

            foreach (DXFBlock block in document.Blocks)
            {
                if (string.Equals(block.BlockName, trimmed, StringComparison.OrdinalIgnoreCase))
                    return block;
            }

            return null;
        }

        #endregion

        #region Text

        private void AddText(DXFText text, Transform transform)
        {
            string value = text.Text;
            if (string.IsNullOrEmpty(value)) return;
            if (!text.Start.X.IsUsable() || !text.Start.Y.IsUsable()) return;

            double height = text.TextHeight.OrIfUnusable(1);
            if (!(height > 0) || !height.IsUsable()) height = 1;

            DXFStyleRecord style = context.ResolveStyle(text.TextStyleName);

            double widthFactor = style?.WidthFactor ?? 1;
            if (!(widthFactor > 0) || !widthFactor.IsUsable()) widthFactor = 1;

            double oblique = style?.ObliqueAngle ?? 0;
            if (!oblique.IsUsable()) oblique = 0;

            double rotation = text.TextRotation.OrIfUnusable(0);

            AddTextGeometry(
                text.Start,
                StripFormatting(value),
                height,
                widthFactor,
                oblique,
                rotation,
                text,
                transform,
                style?.FontFileName,
                style?.BigFontFileName);
        }

        private void AddGenericText(DXFGenericEntity entity, Transform transform, int valueCode, DXFPoint start)
        {
            string value = null;

            foreach (DXFGenericEntity.Entry entry in entity.Entries)
            {
                if (entry.GroupCode != valueCode) continue;
                value = entry.Value;
                break;
            }

            if (string.IsNullOrWhiteSpace(value)) return;
            if (start == null || !start.X.IsUsable() || !start.Y.IsUsable()) return;

            double height = ReadDouble(entity, 40, 1);
            if (!(height > 0) || !height.IsUsable()) height = 1;

            double widthFactor = ReadDouble(entity, 41, 1);
            if (!(widthFactor > 0) || !widthFactor.IsUsable()) widthFactor = 1;

            double oblique = ReadDouble(entity, 51, 0);
            if (!oblique.IsUsable()) oblique = 0;

            double rotation = ReadDouble(entity, 50, 0);

            string styleName = ReadString(entity, 7);

            DXFStyleRecord style = context.ResolveStyle(styleName);

            AddTextGeometry(
                start,
                StripFormatting(value),
                height,
                widthFactor,
                oblique,
                rotation,
                entity,
                transform,
                style?.FontFileName,
                style?.BigFontFileName);
        }

        /// <summary>
        /// Builds the glyph outline for one text string and emits it at the correct place.
        ///
        /// DXF text is drawn with SHX stroke fonts, which are not parsed here. Instead the
        /// closest system font is used to produce a glyph outline (<see cref="FormattedText"/>,
        /// or a stroke font's own line-work) and that outline is scaled so its cap height
        /// matches the DXF text height. The result keeps the drawing's colour, position,
        /// rotation, width factor and oblique angle, and is legible at any zoom because it
        /// is vector data rather than a box.
        /// </summary>
        private void AddTextGeometry(
            DXFPoint start,
            string value,
            double height,
            double widthFactor,
            double oblique,
            double rotation,
            DXFEntity entity,
            Transform transform,
            string primaryFont,
            string bigFont)
        {
            if (!(height > 0) || !height.IsUsable()) return;
            if (string.IsNullOrEmpty(value)) return;

            Point origin = DxfMath.ToDevice(start.X.Value, start.Y.Value);

            List<Geometry> glyphs = BuildTextGeometry(origin, value, height, widthFactor, oblique, primaryFont, bigFont);

            if (glyphs.Count == 0) return;

            Transform place = null;

            if (Math.Abs(rotation) > 1e-9)
            {
                // The canvas Y axis points down, so a counter-clockwise DXF rotation is a
                // clockwise one here.
                place = new RotateTransform(-rotation, origin.X, origin.Y);
            }

            foreach (Geometry glyph in glyphs)
            {
                if (place == null)
                {
                    Emit(glyph, entity, transform);
                    continue;
                }

                Emit(new GeometryGroup { Transform = place, Children = { glyph } }, entity, transform);
            }
        }

        /// <summary>
        /// Produces device-space glyph geometry for the supplied text, positioned with its
        /// baseline origin at <paramref name="origin"/> and extending upwards in -Y.
        /// </summary>
        private static List<Geometry> BuildTextGeometry(
            Point origin,
            string value,
            double height,
            double widthFactor,
            double oblique,
            string primaryFont,
            string bigFont)
        {
            List<Geometry> result = new List<Geometry>();

            string family = MapShxFontToFamily(primaryFont);
            bool strokeFont = IsStrokeFont(primaryFont) || IsStrokeFont(bigFont);

            double scale = 1;

            Geometry glyphGeometry = null;

            try
            {
                Typeface typeface = CreateTypeface(family, strokeFont);

                FormattedText formatted = new FormattedText(
                    value,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    height,
                    Brushes.Black,
                    1.0);

                // BuildGeometry places the baseline origin at the supplied point and the
                // glyphs above it, i.e. at negative Y on the device canvas.
                glyphGeometry = formatted.BuildGeometry(origin);

                scale = ComputeCapHeightScale(typeface, height);
            }
            catch (Exception)
            {
                // A font problem must never break the whole drawing; fall through to the
                // outline box so the layout is still visible.
                glyphGeometry = null;
            }

            if (glyphGeometry == null || glyphGeometry.IsEmpty())
            {
                double fallbackWidth = EstimateTextWidth(value, height) * widthFactor;

                Rect rect = new Rect(origin.X, origin.Y - height, fallbackWidth, height);

                result.Add(new RectangleGeometry(rect));

                return result;
            }

            Matrix matrix = Matrix.Identity;

            matrix.ScaleAt(scale * widthFactor, scale, origin.X, origin.Y);

            if (Math.Abs(oblique) > 1e-9)
            {
                // Cosmetic slant about the baseline origin, mirrored for the downward Y axis.
                // Shear x by y around the origin: x' = x - tan(oblique) * (y - origin.Y).
                double shear = -Math.Tan(oblique * Math.PI / 180.0);

                Matrix slant = new Matrix(1, 0, shear, 1, -shear * origin.Y, 0);

                matrix.Append(slant);
            }

            glyphGeometry.Transform = new MatrixTransform(matrix);

            result.Add(glyphGeometry);

            return result;
        }

        private static Typeface CreateTypeface(string family, bool strokeFont)
        {
            // SHX fonts are single-stroke (engraving) fonts, so their glyphs are drawn as
            // thin line-work rather than solid outlines.
            FontWeight weight = strokeFont ? FontWeights.Light : FontWeights.Normal;

            Typeface typeface = new Typeface(
                new FontFamily(family),
                FontStyles.Normal,
                weight,
                FontStretches.Normal);

            // FontFamily falls back silently for unknown names, so no TryGetGlyphTypeface
            // check is needed here.
            return typeface;
        }

        /// <summary>
        /// How much the glyph outline must be scaled so its capital letter height equals the
        /// DXF text height. DXF's text height is the cap height; a font's em size is larger,
        /// so the outline is scaled up by roughly 1/0.72.
        /// </summary>
        private static double ComputeCapHeightScale(Typeface typeface, double height)
        {
            double capHeight = 0;

            try
            {
                Geometry capGeometry = new FormattedText(
                    "H",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    height,
                    Brushes.Black,
                    1.0).BuildGeometry(new Point(0, 0));

                Rect bounds = capGeometry.Bounds;

                if (bounds.Height > 1e-9) capHeight = bounds.Height;
            }
            catch (Exception)
            {
                capHeight = 0;
            }

            if (!(capHeight > 1e-9)) return 1 / 0.72;

            return height / capHeight;
        }

        /// <summary>True for SHX stroke fonts, which are rendered here as thin line-work.</summary>
        private static bool IsStrokeFont(string fontFile)
        {
            if (string.IsNullOrEmpty(fontFile)) return true;

            return fontFile.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Picks a system font family for a DXF text style. SHX fonts have no system
        /// equivalent, so the closest generic family is used.
        /// </summary>
        private static string MapShxFontToFamily(string fontFile)
        {
            if (string.IsNullOrEmpty(fontFile)) return "Consolas";

            string name = fontFile.Trim();

            int slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (slash >= 0) name = name.Substring(slash + 1);

            if (name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.LastIndexOf('.'));
            }

            if (name.Length == 0) return "Consolas";

            switch (name.ToUpperInvariant())
            {
                case "SIMPLEX":
                case "ROMANS":
                case "ROMAND":
                case "ITALIC":
                case "ITALICC":
                case "SCRIPTS":
                    return "Segoe UI";
                case "TXT":
                case "MONOTXT":
                    return "Consolas";
                case "COMPLEX":
                case "GOTHICG":
                case "GOTHICE":
                case "GOTHICB":
                case "ISOCP":
                case "ISOCPEUR":
                case "I750MZ":
                    return "Segoe UI";
                case "STYLEMZ":
                case "SYMBOL":
                case "SIMPLEX#":
                    return "Consolas";
            }

            // A real TrueType font named by the style: use it directly.
            return name;
        }

        /// <summary>
        /// Rough advance width for a text box: most SHX fonts average a little over half
        /// the cap height per glyph.
        /// </summary>
        private static double EstimateTextWidth(string value, double height)
        {
            if (string.IsNullOrEmpty(value)) return 0;

            double units = 0;

            foreach (char c in value)
            {
                switch (c)
                {
                    case ' ':
                    case '.':
                    case ',':
                    case ':':
                    case ';':
                    case '|':
                    case '(':
                    case ')':
                    case '[':
                    case ']':
                    case '!':
                    case '\'':
                        units += 0.35;
                        break;
                    case 'I':
                    case 'i':
                    case 'l':
                    case 'j':
                        units += 0.4;
                        break;
                    case 'M':
                    case 'W':
                    case 'm':
                    case 'w':
                        units += 0.9;
                        break;
                    default:
                        units += 0.62;
                        break;
                }
            }

            return units * height;
        }

        /// <summary>
        /// Removes MTEXT inline formatting codes such as \P, \fArial|b0; and {}, then expands
        /// the %% text control codes that both TEXT and MTEXT use.
        /// </summary>
        private static string StripFormatting(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            // Without a format escape there is nothing to strip, but %% codes may still need
            // expanding, so that check cannot return early.
            if (value.IndexOf('\\') < 0 && value.IndexOf('{') < 0) return ExpandControlCodes(value);

            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (c == '\\' && i + 1 < value.Length)
                {
                    char symbol = value[i + 1];

                    switch (symbol)
                    {
                        case 'P':
                        case 'p':
                            builder.Append(' ');
                            i++;
                            continue;
                        case '\\':
                        case '{':
                        case '}':
                            builder.Append(symbol);
                            i++;
                            continue;
                        default:
                            // Code with parameters ending in ';' (e.g. \fArial|b0;).
                            int terminator = value.IndexOf(';', i + 1);
                            if (terminator > 0)
                            {
                                i = terminator;
                                continue;
                            }

                            i++;
                            continue;
                    }
                }

                if (c == '{' || c == '}') continue;

                builder.Append(c);
            }

            return ExpandControlCodes(builder.ToString());
        }

        /// <summary>
        /// Expands the DXF text control codes that AutoCAD recognises in both TEXT and MTEXT
        /// values: %%d is a degree sign, %%c a diameter sign, %%p a plus/minus sign, %%%
        /// a literal percent, and %%u / %%o toggle underline and overline (which are dropped
        /// here because the glyphs are drawn as outlines). An unknown code such as %%k keeps
        /// its following character, which is how AutoCAD treats it.
        /// </summary>
        private static string ExpandControlCodes(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.IndexOf("%%", StringComparison.Ordinal) < 0) return value;

            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '%' || i + 2 >= value.Length || value[i + 1] != '%')
                {
                    builder.Append(value[i]);
                    continue;
                }

                char code = char.ToLowerInvariant(value[i + 2]);

                switch (code)
                {
                    case 'd':
                        builder.Append('\u00B0');
                        break;
                    case 'c':
                        builder.Append('\u00D8');
                        break;
                    case 'p':
                        builder.Append('\u00B1');
                        break;
                    case '%':
                        builder.Append('%');
                        break;
                    case 'u':
                    case 'o':
                        // Underline / overline toggles carry no glyph.
                        break;
                    default:
                        builder.Append(value[i + 2]);
                        break;
                }

                i += 2;
            }

            return builder.ToString();
        }

        #endregion

        #region Dimensions, leaders and hatch

        private void AddDimension(DXFDimension dimension, Transform transform)
        {
            // A DIMENSION's visible geometry lives in the anonymous block named by group
            // code 2; the entity itself only carries the definition points.
            string blockName = null;

            foreach (DXFGenericEntity.Entry entry in dimension.Entries)
            {
                if (entry.GroupCode != 2) continue;
                blockName = entry.Value;
                break;
            }

            DXFBlock block = FindBlock(blockName);

            if (block != null && block.HasChildren)
            {
                if (block.IsInvisible) return;

                int resolvedColor = context.ResolveColorIndex(dimension);

                using (context.PushBlock(resolvedColor))
                {
                    foreach (DXFEntity child in block.Children)
                    {
                        if (child is DXFAttribute) continue;
                        if (child is DXFVertex) continue;

                        ConvertEntity(child, transform);
                    }
                }

                return;
            }

            // No anonymous block: fall back to the measured span so the dimension is at
            // least present on screen.
            DXFPoint first = ReadPoint(dimension, 13, 23);
            DXFPoint second = ReadPoint(dimension, 14, 24);

            if (first == null || second == null) return;
            if (!first.X.IsUsable() || !first.Y.IsUsable()) return;
            if (!second.X.IsUsable() || !second.Y.IsUsable()) return;

            Emit(
                new LineGeometry(
                    DxfMath.ToDevice(first.X.Value, first.Y.Value),
                    DxfMath.ToDevice(second.X.Value, second.Y.Value)),
                dimension,
                transform);
        }

        private void AddLeader(DXFLeader leader, Transform transform)
        {
            // LEADER vertices are the ordered group code 10/20 pairs on the entity.
            List<double> xs = new List<double>();
            List<double> ys = new List<double>();

            double? pendingX = null;

            foreach (DXFGenericEntity.Entry entry in leader.Entries)
            {
                switch (entry.GroupCode)
                {
                    case 10:
                        pendingX = ParseDouble(entry.Value);
                        break;

                    case 20:
                        if (pendingX.HasValue)
                        {
                            xs.Add(pendingX.Value);
                            ys.Add(ParseDouble(entry.Value));
                            pendingX = null;
                        }

                        break;
                }
            }

            List<Point> points = new List<Point>();

            for (int i = 0; i < xs.Count && i < ys.Count; i++)
            {
                if (!xs[i].IsUsable() || !ys[i].IsUsable()) continue;

                points.Add(DxfMath.ToDevice(xs[i], ys[i]));
            }

            Emit(DxfGeometryBuilder.CreatePolylineGeometry(points, false), leader, transform);
        }

        /// <summary>
        /// Renders a HATCH.
        ///
        /// The boundary is built from either form of boundary path: a polyline path (bit 2
        /// of group code 92) whose vertices arrive as 10/20 pairs with optional bulges, or
        /// an edge-data path whose segments are individual line/arc/ellipse edges.
        ///
        /// A solid fill becomes a filled path. A pattern fill is drawn as real hatch
        /// line-work: the pattern definition lines (group code 53/43-46) are clipped against
        /// the boundary, so the result looks like the hatch a CAD package would draw rather
        /// than an empty outline.
        /// </summary>
        private void AddHatch(DXFHatchEntity hatch, Transform transform)
        {
            if (hatch.BoundaryPaths == null || hatch.BoundaryPaths.Count == 0)
            {
                RegisterUnsupported(hatch);
                return;
            }

            PathGeometry boundary = BuildHatchBoundary(hatch);

            if (boundary == null || boundary.IsEmpty())
            {
                RegisterUnsupported(hatch);
                return;
            }

            if (hatch.IsSolidFill)
            {
                Emit(boundary, hatch, transform, filled: true);
                return;
            }

            Geometry pattern = BuildHatchPattern(hatch, boundary);

            if (pattern != null && !pattern.IsEmpty())
            {
                Emit(pattern, hatch, transform);
                return;
            }

            // No usable pattern definition: draw the boundary so the region still shows.
            Emit(boundary, hatch, transform);
        }

        /// <summary>
        /// Builds the closed outline of every boundary path in a hatch.
        ///
        /// Each path becomes one closed figure, so the result encloses the region that the
        /// pattern lines are clipped against and holes are honoured through the even-odd rule.
        /// </summary>
        private PathGeometry BuildHatchBoundary(DXFHatchEntity hatch)
        {
            PathGeometry boundary = new PathGeometry();

            foreach (DXFHatchEntity.HatchBoundaryPath path in hatch.BoundaryPaths)
            {
                if (path == null) continue;

                PathGeometry loop = path.IsPolylinePath
                    ? BuildHatchPolylinePath(path)
                    : BuildHatchEdgePath(path);

                if (loop == null || loop.IsEmpty()) continue;

                boundary.AddGeometry(loop);
            }

            if (boundary.IsEmpty()) return null;

            boundary.FillRule = FillRule.EvenOdd;

            return boundary;
        }

        /// <summary>
        /// A polyline boundary path: vertices in file order, with a bulge joining each
        /// vertex to the next and a closed flag for the final segment.
        /// </summary>
        private PathGeometry BuildHatchPolylinePath(DXFHatchEntity.HatchBoundaryPath path)
        {
            if (path.Vertices == null || path.Vertices.Count < 2) return null;

            List<Point> points = new List<Point>(path.Vertices.Count);
            List<double> bulges = new List<double>(path.Vertices.Count);

            foreach (DXFHatchEntity.HatchVertex vertex in path.Vertices)
            {
                if (vertex?.Point == null) continue;
                if (!vertex.Point.X.IsUsable() || !vertex.Point.Y.IsUsable()) continue;

                points.Add(DxfMath.ToDevice(vertex.Point.X.Value, vertex.Point.Y.Value));
                bulges.Add(vertex.Bulge);
            }

            return BuildBulgedLoop(points, bulges, path.IsClosedPolyline, path.Vertices.Count);
        }

        /// <summary>Closes a bulged polyline into a filled boundary outline.</summary>
        private PathGeometry BuildBulgedLoop(List<Point> points, List<double> bulges, bool closed, int declaredVertices)
        {
            if (points.Count < 2) return null;

            bool shouldClose = closed || declaredVertices == points.Count;

            List<Point> expanded = new List<Point>(points.Count * 4);

            for (int i = 0; i < points.Count; i++)
            {
                expanded.Add(points[i]);

                // The segment from this vertex to the next is curved when it has a bulge.
                int next = i + 1;

                if (next >= points.Count)
                {
                    if (!shouldClose) break;
                    next = 0;
                }

                double bulge = i < bulges.Count ? bulges[i] : 0;

                if (Math.Abs(bulge) < 1e-9) continue;

                foreach (Point point in ExpandBulge(points[i], points[next], bulge))
                    expanded.Add(point);
            }

            // DXF boundaries are always closed regions.
            return DxfGeometryBuilder.CreatePolylineGeometry(expanded, true, true);
        }

        /// <summary>
        /// Turns one bulged segment into the flattening points between its two ends,
        /// excluding the end points themselves. The bulge is the tangent of a quarter of
        /// the included angle, positive for a counter-clockwise arc in DXF space; the
        /// canvas Y axis is flipped, so the sign is negated the same way as elsewhere.
        /// </summary>
        private List<Point> ExpandBulge(Point start, Point end, double bulge)
        {
            List<Point> points = new List<Point>();

            // Bulge is measured in drawing units; the device delta is already flipped in Y,
            // so negate to keep the arc bending the same way it does in the DXF file.
            double beta = -2 * Math.Atan(bulge);

            if (Math.Abs(beta) < 1e-9) return points;

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double chord = Math.Sqrt((dx * dx) + (dy * dy));

            if (!(chord > 1e-12)) return points;

            int segments = DxfGeometryBuilder.GetArcSegmentCount(Math.Abs(beta), chord / Math.Max(Math.Abs(beta), 1e-9), tolerance);

            segments = Math.Max(segments, 2);

            double midX = (start.X + end.X) / 2;
            double midY = (start.Y + end.Y) / 2;

            // Unit normal pointing towards the arc's centre of curvature.
            double normalX = -dy / chord;
            double normalY = dx / chord;

            // Distance from the chord midpoint to the arc centre. The half angle is
            // beta/2, so the centre sits at (chord/2)/tan(beta/2) from the midpoint;
            // multiplying half the chord by the sagitta would place the centre on the
            // wrong side and shrink the arc, so it is done properly here.
            double halfAngle = Math.Abs(beta) / 2;

            if (halfAngle < 1e-9 || Math.Abs(Math.Sin(halfAngle)) < 1e-12) return points;

            double centreOffset = (chord / 2) / Math.Tan(halfAngle);
            if (beta < 0) centreOffset = -centreOffset;

            double centreX = midX + (normalX * centreOffset);
            double centreY = midY + (normalY * centreOffset);

            double radius = Math.Sqrt(
                Math.Pow(centreX - start.X, 2) + Math.Pow(centreY - start.Y, 2));

            if (!(radius > 1e-12)) return points;

            double startAngle = Math.Atan2(start.Y - centreY, start.X - centreX);

            bool counterClockwise = beta > 0;

            for (int i = 1; i < segments; i++)
            {
                double angle = startAngle + (beta * i / segments);

                points.Add(new Point(
                    centreX + (Math.Cos(angle) * radius),
                    centreY + (Math.Sin(angle) * radius)));
            }

            return points;
        }

        /// <summary>
        /// An edge-data boundary path: individual line, arc and ellipse edges.
        ///
        /// The edges are chained into a single closed outline. That matters beyond neatness:
        /// pattern clipping in <see cref="BuildHatchPattern"/> intersects each pattern line
        /// with this geometry, and only a closed figure encloses an area, so a boundary left
        /// as loose segments would clip every pattern line away to nothing.
        /// </summary>
        private PathGeometry BuildHatchEdgePath(DXFHatchEntity.HatchBoundaryPath path)
        {
            if (path.Edges == null || path.Edges.Count == 0) return null;

            List<Point> outline = new List<Point>(path.Edges.Count * 4);

            foreach (DXFHatchEntity.HatchEdge edge in path.Edges)
            {
                if (edge == null) continue;

                switch (edge.EdgeType)
                {
                    case 1:
                    case 0:
                    {
                        if (!edge.StartValue.IsUsable() || !edge.StartY.IsUsable()) break;
                        if (!edge.EndValue.IsUsable() || !edge.EndY.IsUsable()) break;

                        Point start = DxfMath.ToDevice(edge.StartValue, edge.StartY);
                        Point end = DxfMath.ToDevice(edge.EndValue, edge.EndY);

                        AppendPoint(outline, start);

                        if (Math.Abs(edge.Bulge) > 1e-9)
                            outline.AddRange(ExpandBulge(start, end, edge.Bulge));

                        AppendPoint(outline, end);

                        break;
                    }

                    case 2:
                    {
                        Point[] arc = BuildHatchArc(edge);
                        if (arc == null) break;

                        foreach (Point point in arc) AppendPoint(outline, point);

                        break;
                    }
                }
            }

            if (outline.Count < 3) return null;

            return DxfGeometryBuilder.CreatePolylineGeometry(outline, true);
        }

        /// <summary>
        /// Adds a point unless it repeats the previous one, so chained edges that share an end
        /// point do not produce zero-length segments.
        /// </summary>
        private static void AppendPoint(List<Point> points, Point point)
        {
            if (points.Count > 0)
            {
                Point last = points[points.Count - 1];

                if (Math.Abs(last.X - point.X) < 1e-9 && Math.Abs(last.Y - point.Y) < 1e-9) return;
            }

            points.Add(point);
        }

        /// <summary>
        /// Builds the actual hatch line-work for a pattern fill by repeating each pattern
        /// definition line across the boundary's bounding box and clipping it against the
        /// boundary outline.
        /// </summary>
        private Geometry BuildHatchPattern(DXFHatchEntity hatch, PathGeometry boundary)
        {
            Rect bounds = boundary.Bounds;

            if (bounds.IsEmpty) return null;

            // Grow the box so lines that only clip the corners are still generated.
            bounds.Inflate(bounds.Width * 0.05 + 1, bounds.Height * 0.05 + 1);

            double diagonal = Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height));

            GeometryGroup pattern = new GeometryGroup { FillRule = FillRule.Nonzero };

            // Flatten the boundary once into polygons; every pattern line is clipped against
            // these. WPF cannot intersect a line with a filled region (Combine reports the
            // result as empty), so the clipping is done here against the polygon outlines.
            List<List<Point>> polygons = FlattenBoundaryPolygons(boundary);

            if (polygons.Count == 0) return null;

            double patternScale = hatch.PatternScale;
            if (!(patternScale > 0) || !patternScale.IsUsable()) patternScale = 1;

            List<DXFHatchEntity.HatchPatternLine> definitions = ResolveHatchPatternLines(hatch, patternScale);

            if (definitions == null || definitions.Count == 0) return null;

            foreach (DXFHatchEntity.HatchPatternLine definition in definitions)
            {
                // Pattern lines are defined in drawing space; the canvas Y axis is flipped,
                // so the angle is mirrored the same way every other direction is.
                double angleRadians = -definition.Angle * Math.PI / 180.0;

                double dirX = Math.Cos(angleRadians);
                double dirY = Math.Sin(angleRadians);

                // Spacing between repeated lines is the component of the offset normal to
                // the line direction.
                double offsetX = definition.OffsetX * patternScale;
                double offsetY = -definition.OffsetY * patternScale;

                double spacing = Math.Abs((offsetX * -dirY) + (offsetY * dirX));
                if (!(spacing > 1e-9)) spacing = Math.Abs(offsetY) > 1e-9 ? Math.Abs(offsetY) : Math.Abs(offsetX);

                // Without a spacing the pattern would be an infinite set of lines.
                if (!(spacing > 1e-9)) continue;

                // Keep the line count sane for very small spacings.
                if (diagonal / spacing > 20000) spacing = diagonal / 20000;

                double baseX = definition.BasePointX * patternScale;
                double baseY = -definition.BasePointY * patternScale;

                // Distance from the pattern origin to the box, along the line normal.
                double normalX = -dirY;
                double normalY = dirX;

                double minProjection = double.MaxValue;
                double maxProjection = double.MinValue;

                foreach (Point corner in new[]
                         {
                             new Point(bounds.Left, bounds.Top),
                             new Point(bounds.Right, bounds.Top),
                             new Point(bounds.Left, bounds.Bottom),
                             new Point(bounds.Right, bounds.Bottom),
                         })
                {
                    double projection = ((corner.X - baseX) * normalX) + ((corner.Y - baseY) * normalY);

                    minProjection = Math.Min(minProjection, projection);
                    maxProjection = Math.Max(maxProjection, projection);
                }

                int firstLine = (int)Math.Floor(minProjection / spacing);
                int lastLine = (int)Math.Ceiling(maxProjection / spacing);

                for (int i = firstLine; i <= lastLine; i++)
                {
                    double shift = i * spacing;

                    // The element of the pattern family this line belongs to. Its offset from
                    // the base point sets the phase, so the repeat lines up with the definition.
                    double pointX = baseX + (normalX * shift);
                    double pointY = baseY + (normalY * shift);

                    // Walk from the box centre out to this line of the family, keeping only the
                    // component perpendicular to the line direction. Moving a point along its
                    // own line leaves the line unchanged, so this centres every the segment on
                    // the geometry while preserving which line of the family it belongs to.
                    // Without it, a base point far from the boundary would leave the segment
                    // short of the geometry.
                    double anchorX = bounds.Left + (bounds.Width / 2);
                    double anchorY = bounds.Top + (bounds.Height / 2);

                    double offsetFromAnchorX = pointX - anchorX;
                    double offsetFromAnchorY = pointY - anchorY;

                    // Remove the along-line component, keeping the perpendicular one.
                    double along = (offsetFromAnchorX * dirX) + (offsetFromAnchorY * dirY);

                    offsetFromAnchorX -= along * dirX;
                    offsetFromAnchorY -= along * dirY;

                    double centreX = anchorX + offsetFromAnchorX;
                    double centreY = anchorY + offsetFromAnchorY;

                    Point lineStart = new Point(
                        centreX - (dirX * diagonal),
                        centreY - (dirY * diagonal));

                    Point lineEnd = new Point(
                        centreX + (dirX * diagonal),
                        centreY + (dirY * diagonal));

                    // Clip the line against the hatch boundary so the pattern stays inside;
                    // the parts inside holes are dropped and the rest is kept as segments.
                    foreach (Point[] run in ClipSegmentToPolygons(lineStart, lineEnd, polygons))
                    {
                        PathGeometry segment = DxfGeometryBuilder.CreatePolylineGeometry(run, false);

                        if (segment != null && !segment.IsEmpty()) pattern.Children.Add(segment);
                    }
                }
            }

            if (pattern.Children.Count == 0) return null;

            return pattern;
        }

        /// <summary>
        /// Flattens a boundary geometry into closed polygons, one list of points per figure.
        ///
        /// The figures are read straight off the path instead of via
        /// <see cref="Geometry.GetFlattenedPathGeometry(double, ToleranceType)"/>: that call
        /// discards a figure made of a single <see cref="PolyLineSegment"/>, which is exactly
        /// how every hatch boundary is built, and returns an empty path for it.
        /// </summary>
        private static List<List<Point>> FlattenBoundaryPolygons(Geometry boundary)
        {
            List<List<Point>> polygons = new List<List<Point>>();

            if (boundary == null || boundary.IsEmpty()) return polygons;

            PathGeometry path = boundary as PathGeometry;

            if (path == null) return polygons;

            foreach (PathFigure figure in path.Figures)
            {
                List<Point> points = new List<Point> { figure.StartPoint };

                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case PolyLineSegment polyline:
                            foreach (Point point in polyline.Points) AppendPoint(points, point);
                            break;

                        case LineSegment line:
                            AppendPoint(points, line.Point);
                            break;

                        case PolyBezierSegment beziers:
                            FlattenBeziers(points, figure.StartPoint, new[] { beziers.Points[0], beziers.Points[1], beziers.Points[2] });
                            break;

                        case BezierSegment bezier:
                            FlattenBeziers(points, figure.StartPoint, new[] { bezier.Point1, bezier.Point2, bezier.Point3 });
                            break;
                    }
                }

                // Drop a repeated closing point; the polygon is treated as implicitly closed.
                while (points.Count > 1)
                {
                    Point first = points[0];
                    Point last = points[points.Count - 1];

                    if (Math.Abs(first.X - last.X) > 1e-9 || Math.Abs(first.Y - last.Y) > 1e-9) break;

                    points.RemoveAt(points.Count - 1);
                }

                if (points.Count >= 3) polygons.Add(points);
            }

            return polygons;
        }

        private static void FlattenBeziers(List<Point> points, Point start, Point[] controls)
        {
            if (controls.Length < 3) return;

            int segments = 16;

            for (int i = 1; i <= segments; i++)
            {
                double t = (double)i / segments;
                double u = 1 - t;

                double x = (u * u * u * start.X)
                           + (3 * u * u * t * controls[0].X)
                           + (3 * u * t * t * controls[1].X)
                           + (t * t * t * controls[2].X);

                double y = (u * u * u * start.Y)
                           + (3 * u * u * t * controls[0].Y)
                           + (3 * u * t * t * controls[1].Y)
                           + (t * t * t * controls[2].Y);

                AppendPoint(points, new Point(x, y));
            }
        }

        /// <summary>
        /// Clips a line segment against a set of polygons and returns the pieces that fall
        /// inside. Crossing points are ordered along the segment and consecutive pairs are
        /// kept according to the even-odd rule, so holes come out empty.
        /// </summary>
        private static List<Point[]> ClipSegmentToPolygons(Point start, Point end, List<List<Point>> polygons)
        {
            List<Point[]> runs = new List<Point[]>();

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;

            double lengthSquared = (dx * dx) + (dy * dy);

            if (!(lengthSquared > 1e-18)) return runs;

            // Parameter along the segment for each crossing with a polygon edge.
            List<double> crossings = new List<double>();

            foreach (List<Point> polygon in polygons)
            {
                for (int i = 0; i < polygon.Count; i++)
                {
                    Point a = polygon[i];
                    Point b = polygon[(i + 1) % polygon.Count];

                    double ex = b.X - a.X;
                    double ey = b.Y - a.Y;

                    double denominator = (dx * ey) - (dy * ex);

                    if (Math.Abs(denominator) < 1e-15) continue;

                    double ax = a.X - start.X;
                    double ay = a.Y - start.Y;

                    double t = ((ax * ey) - (ay * ex)) / denominator;
                    double u = ((ax * dy) - (ay * dx)) / denominator;

                    if (u < 0 || u >= 1) continue;
                    if (t <= 0 || t >= 1) continue;

                    crossings.Add(t);
                }
            }

            if (crossings.Count == 0)
            {
                // No crossings: the whole segment is either inside or outside.
                if (IsPointInsidePolygons(start, polygons)) runs.Add(new[] { start, end });

                return runs;
            }

            crossings.Sort();

            List<Point> bounds = new List<Point> { start };

            foreach (double t in crossings)
            {
                bounds.Add(new Point(start.X + (dx * t), start.Y + (dy * t)));
            }

            bounds.Add(end);

            for (int i = 0; i + 1 < bounds.Count; i++)
            {
                Point from = bounds[i];
                Point to = bounds[i + 1];

                double midX = (from.X + to.X) / 2;
                double midY = (from.Y + to.Y) / 2;

                if (!IsPointInsidePolygons(new Point(midX, midY), polygons)) continue;

                if ((to - from).Length < 1e-9) continue;

                runs.Add(new[] { from, to });
            }

            return runs;
        }

        /// <summary>Even-odd point-in-polygon test across every boundary loop.</summary>
        private static bool IsPointInsidePolygons(Point point, List<List<Point>> polygons)
        {
            bool inside = false;

            foreach (List<Point> polygon in polygons)
            {
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    Point a = polygon[i];
                    Point b = polygon[j];

                    bool straddles = (a.Y > point.Y) != (b.Y > point.Y);

                    if (!straddles) continue;

                    double x = ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X;

                    if (point.X < x) inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// Returns the pattern definition lines to draw. A file may omit them (some writers
        /// only store the pattern name), in which case the common named patterns are
        /// approximated.
        /// </summary>
        private static List<DXFHatchEntity.HatchPatternLine> ResolveHatchPatternLines(DXFHatchEntity hatch, double patternScale)
        {
            if (hatch.PatternLines != null && hatch.PatternLines.Count > 0) return hatch.PatternLines;

            string name = (hatch.PatternName ?? string.Empty).Trim().ToUpperInvariant();

            // User-defined patterns carry only an angle and rely on 52/41 plus the default
            // spacing; ANSI31 is a single 45 degree family, which is by far the most common.
            List<DXFHatchEntity.HatchPatternLine> lines = new List<DXFHatchEntity.HatchPatternLine>();

            switch (name)
            {
                case "ANSI31":
                case "ANSI32":
                case "ANSI33":
                case "ANSI37":
                {
                    double angle = name == "ANSI37" ? 45 : 45;
                    double spacing = 3.175 * (name == "ANSI32" ? 1.5 : 1);

                    lines.Add(MakePatternLine(angle, spacing));
                    break;
                }

                case "ANSI34":
                case "ANSI35":
                case "ANSI36":
                    lines.Add(MakePatternLine(45, 3.175));
                    lines.Add(MakePatternLine(135, 3.175));
                    break;

                case "LINE":
                    lines.Add(MakePatternLine(hatch.PatternAngle, 3.175));
                    break;

                case "CROSS":
                    lines.Add(MakePatternLine(hatch.PatternAngle, 3.175));
                    lines.Add(MakePatternLine(hatch.PatternAngle + 90, 3.175));
                    break;

                case "DOT":
                    lines.Add(MakePatternLine(0, 3.175));
                    break;

                default:
                    if (hatch.PatternType == 0)
                    {
                        // User defined: a single family at the requested angle.
                        lines.Add(MakePatternLine(hatch.PatternAngle, 3.175));
                    }

                    break;
            }

            if (hatch.IsDouble && lines.Count == 1)
            {
                lines.Add(MakePatternLine(lines[0].Angle + 90, 3.175));
            }

            return lines.Count == 0 ? null : lines;
        }

        private static DXFHatchEntity.HatchPatternLine MakePatternLine(double angleDegrees, double spacing)
        {
            double radians = angleDegrees * Math.PI / 180.0;

            return new DXFHatchEntity.HatchPatternLine
            {
                Angle = angleDegrees,
                BasePointX = 0,
                BasePointY = 0,
                OffsetX = -Math.Sin(radians) * spacing,
                OffsetY = Math.Cos(radians) * spacing,
                DashCount = 0,
            };
        }

        private Point[] BuildHatchArc(DXFHatchEntity.HatchEdge edge)
        {
            double radius = edge.Radius;

            if (!(radius > 0) || !radius.IsUsable())
            {
                Point startFallback = edge.StartValue.IsUsable() && edge.StartY.IsUsable()
                    ? DxfMath.ToDevice(edge.StartValue, edge.StartY)
                    : new Point();

                Point endFallback = edge.EndValue.IsUsable() && edge.EndY.IsUsable()
                    ? DxfMath.ToDevice(edge.EndValue, edge.EndY)
                    : new Point();

                radius = (endFallback - startFallback).Length / 2;
            }

            if (!(radius > 0) || !radius.IsUsable()) return null;

            // An arc edge carries a start point, an end point, a radius, and a start and end
            // angle. It does NOT carry a centre: group codes 12/22 belong to spline control
            // points, so using them here would draw every arc around (0,0). The centre comes
            // from the start point and start angle instead, because the start point is
            // exactly one radius from the centre along the start angle.
            if (!edge.StartValue.IsUsable() || !edge.StartY.IsUsable() ||
                !edge.StartAngle.IsUsable() || !edge.EndAngle.IsUsable())
            {
                // Fall back to the chord when the angles are missing.
                if (!edge.StartValue.IsUsable() || !edge.EndValue.IsUsable() ||
                    !edge.StartY.IsUsable() || !edge.EndY.IsUsable())
                {
                    return null;
                }

                return new[]
                {
                    DxfMath.ToDevice(edge.StartValue, edge.StartY),
                    DxfMath.ToDevice(edge.EndValue, edge.EndY)
                };
            }

            double sweep = DxfGeometryBuilder.GetSweepRadians(edge.StartAngle, edge.EndAngle);
            int segments = DxfGeometryBuilder.GetArcSegmentCount(sweep, radius, tolerance);

            if (segments < 1) segments = 1;

            double startRadians = DxfMath.NormalizeDegrees(edge.StartAngle) * Math.PI / 180.0;

            // Radius vector from the centre out to the start point, negated to reach the centre.
            double centreX = edge.StartValue - (Math.Cos(startRadians) * radius);
            double centreY = edge.StartY - (Math.Sin(startRadians) * radius);

            Point[] points = new Point[segments + 1];

            for (int i = 0; i <= segments; i++)
            {
                double angle = startRadians + sweep * i / segments;

                points[i] = DxfMath.ToDevice(
                    centreX + Math.Cos(angle) * radius,
                    centreY + Math.Sin(angle) * radius);
            }

            return points;
        }

        #endregion

        #region Tolerances and multilines

        /// <summary>
        /// Renders a TOLERANCE (feature control frame, or GD&amp;T frame).
        ///
        /// The frame text uses MTEXT inline codes: \Fgdt;j selects an AutoCAD "gdt" symbol
        /// font glyph and \P separates stacked frames. Symbols are mapped to their Unicode
        /// equivalents where the character exists, so the frame draws as a boxed row of
        /// glyphs rather than being dropped.
        /// </summary>
        private void AddTolerance(DXFTolerance tolerance, Transform transform)
        {
            DXFPoint insertion = tolerance.InsertionPoint;

            if (insertion == null) return;
            if (!insertion.X.IsUsable() || !insertion.Y.IsUsable()) return;

            string text = tolerance.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                RegisterUnsupported(tolerance);
                return;
            }

            // The frame's text height comes from its dimension style; 2.5 is AutoCAD's
            // default and keeps the frame readable when no style is present.
            double height = 2.5;

            double rotation = 0;

            if (tolerance.Direction != null)
            {
                double dx = tolerance.Direction.X.OrIfUnusable(0);
                double dy = tolerance.Direction.Y.OrIfUnusable(0);

                // A horizontal reference direction means no rotation.
                if (Math.Abs(dy) > 1e-9 || Math.Abs(dx) > 1e-9)
                {
                    rotation = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    if (Math.Abs(rotation) < 1e-9) rotation = 0;
                }
            }

            string plain = ReplaceGdtSymbols(StripFormatting(text.Replace("\\P", " ")));

            if (string.IsNullOrWhiteSpace(plain))
            {
                RegisterUnsupported(tolerance);
                return;
            }

            Point origin = DxfMath.ToDevice(insertion.X.Value, insertion.Y.Value);

            double width = EstimateTextWidth(plain, height);

            // A feature control frame is a rectangle divided into cells, one per entry.
            // The border is drawn so the frame reads correctly even at small sizes.
            Geometry frame = new RectangleGeometry(new Rect(origin.X, origin.Y - height, width + (height * 0.4), height));

            Emit(frame, tolerance, transform);

            AddTextGeometry(
                new DXFPoint { X = insertion.X.Value + (height * 0.2), Y = insertion.Y.Value },
                plain,
                height,
                1,
                0,
                rotation,
                tolerance,
                transform,
                null,
                null);
        }

        /// <summary>
        /// Maps the single-character codes of AutoCAD's gdt.shx symbol font to Unicode
        /// symbols. Unknown codes are replaced by a middle dot so the frame keeps its width.
        /// </summary>
        private static string ReplaceGdtSymbols(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                // \Fgdt;j is decoded before this runs, so the raw glyph letters remain only
                // where a file used them directly. Map the common GD&T glyph codes.
                if (c == '\\' && i + 1 < value.Length)
                {
                    i++;
                    continue;
                }

                switch (c)
                {
                    case 'j': builder.Append('\u2313'); break; // perpendicularity
                    case 'b': builder.Append('\u2312'); break; // profile of a surface
                    case 'c': builder.Append('\u2312'); break;
                    case 'd': builder.Append('\u2295'); break; // position
                    case 'e': builder.Append('\u24BB'); break; // flatness
                    case 'f': builder.Append('\u232D'); break; // cylindricity
                    case 'g': builder.Append('\u25CE'); break; // circularity
                    case 'h': builder.Append('\u232F'); break; // symmetry
                    case 'i': builder.Append('\u2225'); break; // parallelism
                    case 'k': builder.Append('\u2318'); break;
                    case 'n': builder.Append('\u2220'); break; // angularity
                    case 'u': builder.Append('\u232E'); break; // profile of a line
                    case 'r': builder.Append('\u2312'); break;
                    case 't': builder.Append('\u24C2'); break;
                    case 'a': builder.Append('\u2313'); break;
                    default: builder.Append(c); break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Renders an MLINE by joining its vertex list. A multiline is stored as a set of
        /// parallel offset elements; the vertex positions used by most writers arrive in
        /// group codes 11/21/31, and some writers emit the simpler 10/20/30 form. Drawing
        /// the vertex chain reproduces the multiline's path, which is what the element
        /// offsets are measured from.
        /// </summary>
        private void AddMLine(DXFMLine mline, Transform transform)
        {
            List<Point> vertices = ReadGroupCodePoints(mline.Entries, 11, 21);

            if (vertices.Count < 2)
            {
                // Some writers store the multiline vertices as plain 10/20 pairs.
                vertices = ReadGroupCodePoints(mline.Entries, 10, 20);
            }

            if (vertices.Count < 2)
            {
                RegisterUnsupported(mline);
                return;
            }

            PathGeometry geometry = DxfGeometryBuilder.CreatePolylineGeometry(vertices, false);

            if (geometry == null)
            {
                RegisterUnsupported(mline);
                return;
            }

            Emit(geometry, mline, transform);
        }

        /// <summary>
        /// Renders an MLEADER.
        ///
        /// A multileader stores its leader in a different place depending on the writer: the
        /// simple form keeps plain 10/20 vertex pairs on the entity, while the full form
        /// wraps them in a CONTEXT_DATA{ ... } block (gc 300/301) and a LEADER{ ... } block
        /// (gc 302/303). Both forms are handled by scanning for vertex pairs, in file order,
        /// and drawing the resulting polyline. When no vertices can be found the entity is
        /// reported as unsupported rather than silently dropped.
        /// </summary>
        private void AddMLeader(DXFMLeader mleader, Transform transform)
        {
            List<Point> vertices = ReadGroupCodePoints(mleader.Entries, 10, 20);

            if (vertices.Count < 2)
            {
                RegisterUnsupported(mleader);
                return;
            }

            PathGeometry geometry = DxfGeometryBuilder.CreatePolylineGeometry(vertices, false);

            if (geometry == null)
            {
                RegisterUnsupported(mleader);
                return;
            }

            Emit(geometry, mleader, transform);
        }

        /// <summary>
        /// Collects every (xCode, yCode) coordinate pair from an entity's raw entry list, in
        /// file order. A y code belongs to the most recent x code.
        /// </summary>
        private static List<Point> ReadGroupCodePoints(List<DXFGenericEntity.Entry> entries, int xCode, int yCode)        {
            List<Point> points = new List<Point>();

            double x = double.NaN;

            foreach (DXFGenericEntity.Entry entry in entries)
            {
                if (entry.GroupCode == xCode)
                {
                    x = ParseDouble(entry.Value);
                    continue;
                }

                if (entry.GroupCode != yCode) continue;
                if (!x.IsUsable()) continue;

                double y = ParseDouble(entry.Value);

                if (!y.IsUsable()) continue;

                points.Add(DxfMath.ToDevice(x, y));

                // Consume the x so a stray second y cannot duplicate the point.
                x = double.NaN;
            }

            return points;
        }

        /// <summary>
        /// Places a SHAPE. The glyph outline lives in an external shape (SHX) file that this
        /// viewer does not read, so a small marker box is drawn at the insertion point at the
        /// requested size. That keeps the entity visible and correctly placed instead of
        /// silently dropping it.
        /// </summary>
        private void AddShape(DXFShape shape, Transform transform)
        {
            DXFPoint insertion = shape.InsertionPoint;

            if (insertion == null) return;
            if (!insertion.X.IsUsable() || !insertion.Y.IsUsable()) return;

            double size = shape.Size;
            if (!(size > 0) || !size.IsUsable()) size = pointRadius * 8;

            Point centre = DxfMath.ToDevice(insertion.X.Value, insertion.Y.Value);

            // A shape is placed by its insertion point, which sits on the glyph's baseline.
            Rect box = new Rect(centre.X, centre.Y - size, size, size);

            Geometry marker = new RectangleGeometry(box);

            double rotation = shape.RotationAngle;

            if (rotation.IsUsable() && Math.Abs(rotation) > 1e-9)
            {
                marker = new RectangleGeometry(box, 0, 0, new RotateTransform(-rotation, centre.X, centre.Y));
            }

            context.Warn($"SHAPE '{shape.ShapeName}' is drawn as a placeholder box; its glyph needs the external shape file.");

            Emit(marker, shape, transform);
        }

        /// <summary>
        /// Draws an IMAGE. The raster itself lives in an external file referenced by an
        /// IMAGEDEF object, which is not resolved here, so the image's placement rectangle
        /// and clip boundary are drawn instead. That shows the image's size, position,
        /// rotation and scale even when the referenced file is unavailable.
        /// </summary>
        private void AddImage(DXFImage image, Transform transform)
        {
            DXFPoint insertion = ReadPoint(image, 10, 20);

            if (insertion == null || !insertion.X.IsUsable() || !insertion.Y.IsUsable())
            {
                RegisterUnsupported(image);
                return;
            }

            // Group code 11/21 is the U vector (image width direction), 12/22 the V vector
            // (image height direction); both are in drawing units, so they give the size.
            DXFPoint uVector = ReadPoint(image, 11, 21);
            DXFPoint vVector = ReadPoint(image, 12, 22);

            double ux = uVector?.X.OrIfUnusable(0) ?? 0;
            double uy = uVector?.Y.OrIfUnusable(0) ?? 0;
            double vx = vVector?.X.OrIfUnusable(0) ?? 0;
            double vy = vVector?.Y.OrIfUnusable(0) ?? 0;

            double originX = insertion.X.Value;
            double originY = insertion.Y.Value;

            // Without usable size vectors fall back to a small square so the entity shows.
            if (Math.Abs(ux) < 1e-12 && Math.Abs(uy) < 1e-12)
            {
                ux = pointRadius * 20;
                uy = 0;
            }

            if (Math.Abs(vx) < 1e-12 && Math.Abs(vy) < 1e-12)
            {
                vx = 0;
                vy = pointRadius * 20;
            }

            // The four corners of the placed raster: origin, +U, +U+V, +V.
            Point a = DxfMath.ToDevice(originX, originY);
            Point b = DxfMath.ToDevice(originX + ux, originY + uy);
            Point c = DxfMath.ToDevice(originX + ux + vx, originY + uy + vy);
            Point d = DxfMath.ToDevice(originX + vx, originY + vy);

            PathGeometry frame = DxfGeometryBuilder.CreatePolylineGeometry(new[] { a, b, c, d }, true);

            if (frame != null) Emit(frame, image, transform);
            else RegisterUnsupported(image);
        }

        #endregion

        #region Generic entity reading

        private static DXFPoint ReadPoint(DXFGenericEntity entity, int xCode, int yCode)
        {
            double? x = null;
            double? y = null;

            foreach (DXFGenericEntity.Entry entry in entity.Entries)
            {
                if (entry.GroupCode == xCode && x == null) x = ParseDouble(entry.Value);
                else if (entry.GroupCode == yCode && y == null) y = ParseDouble(entry.Value);
            }

            if (x == null || y == null) return null;

            return new DXFPoint { X = x.Value, Y = y.Value };
        }

        /// <summary>
        /// Reads a group code from an entity's raw entry list. DXFLib keeps every group
        /// code in <see cref="DXFGenericEntity.Entries"/>, and INSERT is not a generic
        /// entity, so its MINSERT columns/rows/spacing are read here instead.
        /// </summary>
        private static double ReadDouble(DXFEntity entity, int groupCode, double fallback)
        {
            if (!(entity is DXFGenericEntity generic)) return fallback;

            foreach (DXFGenericEntity.Entry entry in generic.Entries)
            {
                if (entry.GroupCode == groupCode) return ParseDouble(entry.Value);
            }

            return fallback;
        }

        /// <summary>Reads a string-valued group code from an entity's raw entry list.</summary>
        private static string ReadString(DXFEntity entity, int groupCode)
        {
            if (!(entity is DXFGenericEntity generic)) return null;

            foreach (DXFGenericEntity.Entry entry in generic.Entries)
            {
                if (entry.GroupCode == groupCode) return entry.Value;
            }

            return null;
        }

        private static double ParseDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return double.NaN;

            return double.TryParse(
                value.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed)
                ? parsed
                : double.NaN;
        }

        #endregion
    }
}

