using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DXFViewer
{
    /// <summary>
    /// Geometry helpers shared by the DXF entity converters. All of these work in WPF
    /// device space, i.e. the Y axis has already been flipped.
    /// </summary>
    internal static class DxfGeometryBuilder
    {
        /// <summary>
        /// Returns the sweep angle in radians covered by an arc, always positive and in
        /// the range (0, 2*pi]. DXF stores start and end angles in degrees, counter
        /// clockwise from the positive X axis, and the arc always runs counter clockwise
        /// from start to end.
        /// </summary>
        public static double GetSweepRadians(double startDegrees, double endDegrees)
        {
            double start = NormalizeDegrees(startDegrees);
            double end = NormalizeDegrees(endDegrees);

            double sweep = end - start;
            while (sweep <= 0) sweep += 360.0;
            while (sweep > 360.0) sweep -= 360.0;

            return sweep * Math.PI / 180.0;
        }

        /// <summary>
        /// Normalises an angle in degrees into [0, 360). Delegates to
        /// <see cref="DxfMath.NormalizeDegrees"/> so that a NaN or infinite angle resolves to
        /// 0 here as well; a local copy would have propagated the NaN into the geometry.
        /// </summary>
        public static double NormalizeDegrees(double degrees)
        {
            return DxfMath.NormalizeDegrees(degrees);
        }

        /// <summary>
        /// Builds the points along a circular arc. DXF angles are measured counter
        /// clockwise in a Y-up system; the caller supplies a transform that converts to
        /// the Y-down device space.
        /// </summary>
        public static void SampleArc(
            IList<Point> points,
            bool includeStart,
            double centerX,
            double centerY,
            double radius,
            double startDegrees,
            double sweepRadians,
            int segments)
        {
            if (segments < 1) segments = 1;
            if (radius <= 0) return;

            double startRadians = NormalizeDegrees(startDegrees) * Math.PI / 180.0;

            for (int i = includeStart ? 0 : 1; i <= segments; i++)
            {
                double angle = startRadians + sweepRadians * i / segments;
                points.Add(new Point(
                    centerX + Math.Cos(angle) * radius,
                    centerY + Math.Sin(angle) * radius));
            }
        }

        /// <summary>
        /// Number of straight segments needed to approximate an arc within tolerance.
        /// </summary>
        public static int GetArcSegmentCount(double sweepRadians, double radius, double tolerance)
        {
            if (radius <= 0 || sweepRadians <= 0) return 1;

            // Sagitta of a circular segment: s = r * (1 - cos(theta/2)).
            // Solve for theta given the tolerated sagitta.
            double ratio = 1.0 - (tolerance / radius);
            if (ratio <= -1.0) return 1;
            if (ratio >= 1.0) ratio = 1.0;

            double maxStepAngle = 2.0 * Math.Acos(ratio);
            if (maxStepAngle <= 0) return 1;

            int count = (int)Math.Ceiling(sweepRadians / maxStepAngle);
            return Math.Max(1, Math.Min(count, 720));
        }

        /// <summary>
        /// Converts a DXF polyline bulge to the arc it represents and appends its points.
        /// The bulge is tan(theta/4) where theta is the included angle; positive bulges
        /// run counter clockwise in the DXF (Y-up) coordinate system.
        /// </summary>
        /// <param name="points">Destination list. The start point must already be present.</param>
        /// <param name="start">Arc start point in DXF coordinates.</param>
        /// <param name="end">Arc end point in DXF coordinates.</param>
        /// <param name="bulge">Bulge value.</param>
        /// <param name="tolerance">Chord tolerance in DXF units.</param>
        /// <param name="includeEnd">Whether to append the end point.</param>
        public static void AppendBulge(
            IList<Point> points,
            Point start,
            Point end,
            double bulge,
            double tolerance,
            bool includeEnd)
        {
            if (Math.Abs(bulge) < 1e-12)
            {
                if (includeEnd) points.Add(end);
                return;
            }

            double chord = (end - start).Length;
            if (chord < 1e-12)
            {
                if (includeEnd) points.Add(end);
                return;
            }

            double theta = 4.0 * Math.Atan(bulge);
            double radius = chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0));
            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0)
            {
                if (includeEnd) points.Add(end);
                return;
            }

            // Centre lies on the perpendicular bisector.
            Point mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            Vector toStart = start - mid;
            double halfChord = chord / 2.0;
            double offset = Math.Sqrt(Math.Max(0, radius * radius - halfChord * halfChord));

            // Perpendicular direction, consistently oriented so the arc bulges the way
            // the sign of the bulge says.
            Vector perpendicular = new Vector(-toStart.Y, toStart.X);
            if (perpendicular.Length > 1e-12) perpendicular.Normalize();

            bool isClockwise = theta < 0;
            double largeArcSign = Math.Abs(theta) > Math.PI ? -1 : 1;

            Point center = mid + perpendicular * (offset * largeArcSign * (isClockwise ? 1 : 1));

            // Recompute so the centre is on the correct side: for a bulge of tan(t/4) the
            // centre sits on the opposite side of the chord from the arc apex.
            double signedOffset = Math.Cos(Math.Abs(theta) / 2.0) * radius;
            center = mid - perpendicular * (Math.Sign(bulge) * signedOffset);

            double startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);

            double sweep = endAngle - startAngle;
            if (bulge > 0)
            {
                while (sweep <= 0) sweep += 2 * Math.PI;
            }
            else
            {
                while (sweep >= 0) sweep -= 2 * Math.PI;
            }

            int segments = GetArcSegmentCount(Math.Abs(sweep), radius, tolerance);
            for (int i = 1; i <= segments; i++)
            {
                if (!includeEnd && i >= segments) break;

                double angle = startAngle + sweep * i / segments;
                points.Add(new Point(
                    center.X + Math.Cos(angle) * radius,
                    center.Y + Math.Sin(angle) * radius));
            }
        }

        /// <summary>
        /// Builds a path geometry from a point list, optionally closing it.
        /// </summary>
        public static PathGeometry CreatePolylineGeometry(
            IList<Point> points,
            bool closed,
            bool filled = false)
        {
            if (points == null || points.Count == 0) return null;

            if (points.Count == 1)
                points = new List<Point> { points[0], points[0] };

            if (closed && points.Count > 1)
            {
                // Skip an explicit duplicate closing point; the figure is closed instead.
                Vector closing = (Vector)(points[points.Count - 1] - points[0]);
                if (closing.Length < 1e-12) closed = false;
            }

            PathFigure figure = new PathFigure
            {
                StartPoint = points[0],
                IsClosed = closed,
                IsFilled = filled
            };

            PolyLineSegment segment = new PolyLineSegment();
            for (int i = 1; i < points.Count; i++)
                segment.Points.Add(points[i]);

            figure.Segments.Add(segment);

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return geometry;
        }

        /// <summary>
        /// Returns an elliptical arc approximation of a full circle or arc in DXF space,
        /// flipped into device space by the caller's Y negation.
        /// </summary>
        public static void AppendEllipticalArc(
            IList<Point> points,
            bool includeStart,
            Point center,
            Vector majorAxis,
            double minorToMajorRatio,
            double startParam,
            double endParam)
        {
            double majorRadius = majorAxis.Length;
            if (majorRadius < 1e-12) return;

            double minorRadius = majorRadius * minorToMajorRatio;
            Vector minorAxis = new Vector(-majorAxis.Y, majorAxis.X) * minorToMajorRatio;

            double startAngle = startParam;
            double endAngle = endParam;

            double sweep = endAngle - startAngle;
            // DXF ellipse parameters increase counter clockwise; a non positive sweep
            // means "go the long way round" only when the values really are unequal.
            if (sweep <= 0) sweep += 2 * Math.PI;

            double scale = Math.Max(majorRadius, Math.Abs(minorRadius));
            int segments = GetArcSegmentCount(sweep, scale, scale * 1e-3);
            if (segments < 2) segments = 2;

            for (int i = includeStart ? 0 : 1; i <= segments; i++)
            {
                double t = startAngle + sweep * i / segments;
                points.Add(new Point(
                    center.X + Math.Cos(t) * majorRadius,
                    center.Y + Math.Sin(t) * minorRadius));
            }
        }
    }
}
