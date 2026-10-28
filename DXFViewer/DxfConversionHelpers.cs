using DXFLib;
using System;
using System.Diagnostics;
using System.IO;

namespace DXFViewer
{
    /// <summary>
    /// Entry points for reading DXF files into the viewer.
    ///
    /// The conversion itself lives in <see cref="DxfGeometryConverter"/>, which resolves
    /// each entity's colour, line type and layer state as AutoCAD does; this type only
    /// handles file access and timing.
    /// </summary>
    public static class DxfConversionHelpers
    {
        /// <summary>
        /// Loads a DXF file and converts it into positioned geometry, with each entity's
        /// colour resolved from its layer, its enclosing block and its own colour number.
        /// Returns null when the file cannot be read or holds no entities.
        /// </summary>
        public static DxfLoadResult LoadDxf(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                if (Debugger.IsAttached) Debugger.Break();
                return null;
            }

            DateTime start = DateTime.UtcNow;

            DxfLoadResult result = DxfGeometryConverter.Load(fileName);

            if (result == null || result.Document?.Entities == null || result.Document.Entities.Count == 0)
            {
                if (Debugger.IsAttached) Debugger.Break();
                return null;
            }

            Debug.WriteLine("Converted {0} in {1}ms",
                Path.GetFileName(fileName),
                DateTime.UtcNow.Subtract(start).TotalMilliseconds);

            return result;
        }

        /// <summary>
        /// Parses a DXF file without converting it, for callers that only need the header
        /// and tables.
        /// </summary>
        public static DXFDocument ReadDXF(string fileName)
        {
            DateTime start = DateTime.UtcNow;

            DXFDocument document = new DXFDocument();
            document.Load(fileName);

            Debug.WriteLine("Loaded {0} in {1}ms",
                Path.GetFileName(fileName),
                DateTime.UtcNow.Subtract(start).TotalMilliseconds);

            return document;
        }
    }
}
