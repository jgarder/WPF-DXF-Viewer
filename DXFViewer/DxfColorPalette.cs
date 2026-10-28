using System;
using System.Collections.Generic;

namespace DXFViewer
{
    /// <summary>
    /// Resolves the AutoCAD Color Index (ACI) palette used by DXF files to RGB values.
    /// Index 0 means ByBlock, 256 means ByLayer and 255 is an explicit white/black
    /// placeholder; the remaining indices are the standard AutoCAD palette.
    /// </summary>
    internal static class DxfColorPalette
    {
        /// <summary>Value returned for an entity whose colour cannot be resolved.</summary>
        public const int ByBlock = 0;

        public const int ByLayer = 256;

        private static readonly int[] DefaultColors =
        {
            // 0..9
            0x000000, 0xFF0000, 0xFFFF00, 0x00FF00, 0x00FFFF,
            0x0000FF, 0xFF00FF, 0xFFFFFF, 0x808080, 0xC0C0C0,
            // 10..19
            0xFF0000, 0x7F0000, 0xFF7F7F, 0xFF3F00, 0xBF2F00, 0xDF7F5F, 0xFF1F00, 0x9F4F00, 0x9F2F00, 0xBF5F2F,
            // 20..29
            0xFF7F00, 0xBF7F00, 0xFFBF00, 0x9F7F00, 0x7F5F00, 0xBF9F00, 0xFFFF00, 0xBFBF00, 0x9F9F00, 0x7F7F00,
            // 30..39
            0xFFFF00, 0xBFBF00, 0x7F7F00, 0x5F5F00, 0x3F3F00, 0x7F7F00, 0x7FFF00, 0x5FBF00, 0x3F9F00, 0x2F7F00,
            // 40..49
            0x00FF00, 0x00BF00, 0x007F00, 0x004F00, 0x002F00, 0x004F00, 0x00FF7F, 0x00BF5F, 0x009F3F, 0x007F2F,
            // 50..59
            0x00FFFF, 0x00BFBF, 0x007F7F, 0x004F4F, 0x002F2F, 0x004F4F, 0x00BFFF, 0x009FBF, 0x005F9F, 0x004F7F,
            // 60..69
            0x0000FF, 0x0000BF, 0x00007F, 0x00004F, 0x00002F, 0x004F2F, 0x007FFF, 0x005FBF, 0x003F9F, 0x002F7F,
            // 70..79
            0x7F7FFF, 0x5F5FBF, 0x3F3F9F, 0x2F2F7F, 0x1F1F5F, 0x2F2F7F, 0xFF7FFF, 0xBF5FBF, 0x9F3F9F, 0x7F2F7F,
            // 80..89
            0xFF00FF, 0xBF00BF, 0x7F007F, 0x4F004F, 0x2F002F, 0x4F004F, 0xFF7F7F, 0xBF5F5F, 0x9F3F3F, 0x7F2F2F,
            // 90..99
            0xFF9F7F, 0xBF7F5F, 0x9F5F3F, 0x7F4F2F, 0x5F3F1F, 0x7F4F2F, 0xFFBF7F, 0xBF9F5F, 0x9F7F3F, 0x7F5F2F,
            // 100..109
            0xFFFF00, 0xBFBF00, 0x9F9F00, 0x7F7F00, 0x5F5F00, 0x7F7F00, 0xFFFF7F, 0xBFBF5F, 0x9F9F3F, 0x7F7F2F,
            // 110..119
            0xBFFFBF, 0x7FBF7F, 0x5F9F5F, 0x3F7F3F, 0x2F5F2F, 0x3F7F3F, 0xBFFFFF, 0x7FBFBF, 0x5F9F9F, 0x3F7F7F,
            // 120..129
            0xBFBFFF, 0x7F7FBF, 0x5F5F9F, 0x3F3F7F, 0x2F2F5F, 0x3F3F7F, 0xFFBFFF, 0xBF7FBF, 0x9F5F9F, 0x7F3F7F,
            // 130..139
            0xFF7F7F, 0xBF5F5F, 0x9F3F3F, 0x7F2F2F, 0x5F1F1F, 0x7F2F2F, 0xFFBFBF, 0xBF9F9F, 0x9F7F7F, 0x7F5F5F,
            // 140..149
            0xFFBF7F, 0xBF9F5F, 0x9F7F3F, 0x7F5F2F, 0x5F3F1F, 0x7F5F2F, 0xFFBFBF, 0xBF9F9F, 0x9F7F7F, 0x7F5F5F,
            // 150..159
            0xFFBFBF, 0xBF9F9F, 0x9F7F7F, 0x7F5F5F, 0x5F3F3F, 0x7F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 160..169
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 170..179
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 180..189
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 190..199
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 200..209
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 210..219
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 220..229
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 230..239
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 240..249
            0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F, 0x3F3F3F, 0x5F5F5F, 0xBFBFBF, 0x9F9F9F, 0x7F7F7F, 0x5F5F5F,
            // 250..255
            0x333333, 0x505050, 0x696969, 0x828282, 0xBEBEBE, 0xFFFFFF
        };

        /// <summary>
        /// The palette as RGB triplets, keyed by ACI index. Declared after
        /// <see cref="DefaultColors"/> so the static initialiser runs in the right order.
        /// </summary>
        private static readonly Dictionary<int, int[]> IndexColors = CreateIndexColors();

        private static Dictionary<int, int[]> CreateIndexColors()
        {
            Dictionary<int, int[]> colors = new Dictionary<int, int[]>(DefaultColors.Length);

            for (int i = 0; i < DefaultColors.Length; i++)
            {
                int rgb = DefaultColors[i];
                colors[i] = new[] { (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF };
            }

            return colors;
        }

        /// <summary>
        /// Returns the RGB triplet for an ACI index, or null when the index is
        /// ByBlock, ByLayer or out of range.
        /// </summary>
        public static int[] GetRgb(int colorIndex)
        {
            if (colorIndex <= ByBlock || colorIndex >= ByLayer) return null;

            if (IndexColors.TryGetValue(colorIndex, out int[] rgb)) return rgb;

            return null;
        }

        /// <summary>
        /// Resolves an ACI index to a concrete colour index, following the DXF
        /// ByLayer / ByBlock rules. The caller supplies the layer colour and the
        /// enclosing block colour so the result is always a real palette index.
        /// </summary>
        public static int Resolve(int colorIndex, int layerColorIndex, int blockColorIndex)
        {
            if (colorIndex == ByBlock)
            {
                // An unresolvable ByBlock (no enclosing block) falls back to ByLayer.
                if (blockColorIndex > ByBlock && blockColorIndex < ByLayer) return blockColorIndex;

                return ResolveLayer(layerColorIndex);
            }

            if (colorIndex == ByLayer || colorIndex < 0) return ResolveLayer(layerColorIndex);

            return colorIndex;
        }

        private static int ResolveLayer(int layerColorIndex)
        {
            if (layerColorIndex > ByBlock && layerColorIndex < ByLayer) return layerColorIndex;

            // A layer with no usable colour renders as black/white, exactly like
            // AutoCAD's default layer colour.
            return 7;
        }

        /// <summary>
        /// The ACI index AutoCAD draws as black on a white background. Index 7 is
        /// nominally white; viewers built for a white canvas invert it.
        /// </summary>
        public const int DefaultColorIndex = 7;

        /// <summary>
        /// Returns true when an ACI index is one of the two "either black or white"
        /// indices that must be inverted for a light background.
        /// </summary>
        public static bool IsAdaptive(int colorIndex)
        {
            return colorIndex == 7 || colorIndex == 255;
        }
    }
}
