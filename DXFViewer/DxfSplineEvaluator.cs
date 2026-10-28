using System;
using System.Collections.Generic;
using System.Windows;

namespace DXFViewer
{
    /// <summary>
    /// Evaluates DXF SPLINE entities. A DXF spline is a non uniform rational B-spline
    /// (NURBS) described by a degree, a knot vector and weighted control points.
    /// Entities that only carry fit points are interpolated through those points with a
    /// cubic spline instead.
    /// </summary>
    internal static class DxfSplineEvaluator
    {
        /// <summary>
        /// Evaluates a non uniform rational B-spline at parameter <paramref name="t"/>.
        /// </summary>
        public static Point Evaluate(
            IReadOnlyList<Point> controlPoints,
            IReadOnlyList<double> knots,
            IReadOnlyList<double> weights,
            int degree,
            double t)
        {
            int n = controlPoints.Count - 1;

            if (n < 0) return new Point(0, 0);
            if (n == 0) return controlPoints[0];

            // Clamp the parameter into the valid knot span.
            double low = knots[degree];
            double high = knots[n + 1];
            if (t <= low) return controlPoints[0];
            if (t >= high) return controlPoints[n];

            int span = FindSpan(knots, degree, n, t);

            // A DXF spline is a NURBS, so de Boor's algorithm has to run in homogeneous
            // coordinates: each control point is lifted to (x*w, y*w, w), interpolated, and
            // then divided back by w. Skipping the weights -- running the plain, non-rational
            // form -- passes through the control points but bends a weighted curve, which
            // makes any rational spline (arcs, ellipses) come out visibly wrong.
            bool rational = weights != null && weights.Count > n;
            Point[] d = new Point[degree + 1];
            double[] w = new double[degree + 1];

            for (int j = 0; j <= degree; j++)
            {
                int source = span - degree + j;
                Point control = controlPoints[source];
                double weight = rational ? weights[source] : 1.0;

                // A zero or missing weight would divide by zero after the projection; treat
                // it as the non rational case rather than producing NaN coordinates.
                if (weight.IsNearlyZero()) weight = 1.0;

                d[j] = new Point(control.X * weight, control.Y * weight);
                w[j] = weight;
            }

            for (int r = 1; r <= degree; r++)
            {
                for (int j = degree; j >= r; j--)
                {
                    int index = span - degree + j;
                    double denominator = knots[index + degree - r + 1] - knots[index];

                    double alpha = denominator.IsNearlyZero()
                        ? 0.0
                        : (t - knots[index]) / denominator;

                    d[j] = new Point(
                        (1.0 - alpha) * d[j - 1].X + alpha * d[j].X,
                        (1.0 - alpha) * d[j - 1].Y + alpha * d[j].Y);

                    w[j] = (1.0 - alpha) * w[j - 1] + alpha * w[j];
                }
            }

            // Project back from homogeneous coordinates.
            if (Math.Abs(w[degree]) < 1e-12) return new Point(d[degree].X, d[degree].Y);

            return new Point(d[degree].X / w[degree], d[degree].Y / w[degree]);
        }

        /// <summary>
        /// Finds the knot span containing <paramref name="t"/>.
        /// </summary>
        private static int FindSpan(IReadOnlyList<double> knots, int degree, int n, double t)
        {
            if (t >= knots[n + 1]) return n;

            int low = degree;
            int high = n + 1;
            int mid = (low + high) / 2;

            while (t < knots[mid] || t >= knots[mid + 1])
            {
                if (t < knots[mid]) high = mid;
                else low = mid;

                mid = (low + high) / 2;

                if (mid <= low) break;
            }

            return mid;
        }

        /// <summary>
        /// Samples a spline into a point list. Uses the control polygon when the entity
        /// carries no knot vector (a malformed but common export), otherwise evaluates.
        /// </summary>
        public static List<Point> Sample(
            IReadOnlyList<Point> controlPoints,
            IReadOnlyList<double> knots,
            IReadOnlyList<double> weights,
            int degree,
            bool closed,
            int sampleCount)
        {
            List<Point> result = new List<Point>();

            if (controlPoints == null || controlPoints.Count == 0) return result;

            int n = controlPoints.Count - 1;

            if (controlPoints.Count == 1)
            {
                result.Add(controlPoints[0]);
                result.Add(controlPoints[0]);
                return result;
            }

            // A degree of 1 is a polyline through the control points.
            if (degree <= 1 || knots == null || knots.Count < n + degree + 2)
            {
                for (int i = 0; i < controlPoints.Count; i++)
                    result.Add(controlPoints[i]);

                if (closed && result.Count > 2) result.Add(result[0]);

                return result;
            }

            if (sampleCount < 2) sampleCount = 2;

            double low = knots[degree];
            double high = knots[n + 1];

            if (high <= low)
            {
                for (int i = 0; i < controlPoints.Count; i++)
                    result.Add(controlPoints[i]);
                return result;
            }

            int count = Math.Max(sampleCount, controlPoints.Count * 4);
            count = Math.Min(count, 2000);

            for (int i = 0; i <= count; i++)
            {
                double t = low + (high - low) * i / count;
                result.Add(Evaluate(controlPoints, knots, weights, degree, t));
            }

            if (closed && result.Count > 2)
            {
                Vector gap = (Vector)(result[0] - result[result.Count - 1]);
                if (gap.Length > 1e-9) result.Add(result[0]);
            }

            return result;
        }

        /// <summary>
        /// Builds a knot vector for a fit-point-only spline.
        /// </summary>
        public static List<double> CreateClampedKnotVector(int controlPointCount, int degree)
        {
            int n = controlPointCount - 1;
            List<double> knots = new List<double>(n + degree + 2);

            for (int i = 0; i <= degree; i++) knots.Add(0);
            for (int i = 1; i <= n - degree; i++) knots.Add(i);
            for (int i = 0; i <= degree; i++) knots.Add(n - degree + 1);

            return knots;
        }
    }
}
