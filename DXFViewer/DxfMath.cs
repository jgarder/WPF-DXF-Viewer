using System;
using System.Collections.Generic;
using System.Windows;

namespace DXFViewer
{
    /// <summary>
    /// Math helpers used by the DXF converters.
    /// </summary>
    internal static class DxfMath
    {
        public static bool IsNearlyZero(this double value)
        {
            return Math.Abs(value) < 1e-12;
        }

        public static bool IsUsable(this double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static bool IsUsable(this double? value)
        {
            return value.HasValue && value.Value.IsUsable();
        }

        /// <summary>
        /// Returns the value when it is finite, otherwise the fallback.
        /// </summary>
        public static double OrIfUnusable(this double? value, double fallback)
        {
            if (!value.HasValue || !value.Value.IsUsable()) return fallback;

            return value.Value;
        }

        /// <summary>
        /// Returns the value when it is finite, otherwise the fallback. DXF writers emit
        /// sentinels such as 1e20 for "undefined"; those are treated as unusable.
        /// </summary>
        public static double OrIfUnusable(this double value, double fallback)
        {
            return value.IsUsable() ? value : fallback;
        }

        /// <summary>
        /// Returns true when a DXF extrusion vector points back along Z, which mirrors the
        /// entity's X axis and therefore its angles.
        /// </summary>
        public static bool IsNegativeZExtrusion(double? z)
        {
            return z.HasValue && z.Value < 0;
        }

        /// <summary>
        /// Normalises an angle in degrees into [0, 360).
        /// </summary>
        public static double NormalizeDegrees(double degrees)
        {
            if (!degrees.IsUsable()) return 0;

            double value = degrees % 360.0;
            if (value < 0) value += 360.0;

            return value;
        }

        /// <summary>
        /// Returns a tolerance scaled to the size of the drawing, so arc flattening is
        /// sub-pixel for both a 10 unit plate and a 50,000 unit floor plan.
        /// </summary>
        public static double GetTolerance(double extent)
        {
            if (!extent.IsUsable() || extent <= 0) return 1e-3;

            return Math.Max(extent * 1e-4, 1e-9);
        }

        /// <summary>
        /// Returns the largest absolute component of an extent width/height pair.
        /// </summary>
        public static double MaxSpan(double width, double height)
        {
            double span = Math.Max(Math.Abs(width), Math.Abs(height));

            return span.IsUsable() ? span : 0;
        }

        /// <summary>
        /// Converts a list of DXF points to device space by negating Y.
        /// </summary>
        public static Point ToDevice(double x, double y)
        {
            return new Point(x, -y);
        }
    }
}
