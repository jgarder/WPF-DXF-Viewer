using DXFLib;
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DXFViewer
{
    /// <summary>
    /// Carries the state that DXF entities inherit from their layer, their enclosing
    /// block and the drawing defaults while an entity is converted for display.
    ///
    /// In DXF an entity's colour is resolved as follows:
    /// <list type="bullet">
    /// <item>256 (ByLayer) takes the colour of its layer.</item>
    /// <item>0 (ByBlock) takes the colour of the enclosing INSERT, which may itself be
    /// ByLayer or ByBlock and so resolves the same way.</item>
    /// <item>Any other index is the AutoCAD Color Index of the entity.</item>
    /// </list>
    /// A layer whose colour index is negative is off, and entities on it are hidden in
    /// AutoCAD (still visible in a plot). Entities that are frozen or whose layer is
    /// frozen are hidden as well. This context tracks that so the converter can skip them.
    /// </summary>
    internal sealed class DxfRenderContext
    {
        private readonly DXFDocument document;

        private readonly Dictionary<string, DXFLayerRecord> layersByKey;

        private readonly Dictionary<string, DXFLineTypeRecord> lineTypesByKey;

        private readonly Dictionary<string, DXFStyleRecord> stylesByKey;

        private readonly Dictionary<string, double> lineTypeScaleCache = new Dictionary<string, double>();

        /// <summary>Colour handed down by the enclosing block, or ByLayer at the top level.</summary>
        public int InheritedColorIndex { get; private set; } = DxfColorPalette.ByLayer;

        /// <summary>Colour index of the layer the current entity sits on.</summary>
        public int LayerColorIndex { get; private set; } = DxfColorPalette.DefaultColorIndex;

        /// <summary>True when the current entity's layer is off (negative colour index).</summary>
        public bool LayerIsOff { get; private set; }

        /// <summary>True when the current entity's layer is frozen.</summary>
        public bool LayerIsFrozen { get; private set; }

        /// <summary>Current unambiguous layer name, used for -LAYER style lookups.</summary>
        public string LayerName { get; private set; } = "0";

        /// <summary>
        /// Global line type scale ($LTSCALE). DXF dash patterns are expressed in drawing
        /// units and must be multiplied by this to get the on-screen length.
        /// </summary>
        public double GlobalLineTypeScale { get; private set; } = 1.0;

        /// <summary>Nesting depth of INSERT resolution, so recursive blocks cannot hang.</summary>
        public int BlockDepth { get; private set; }

        public IReadOnlyList<DxfDiagnostic> Diagnostics => diagnostics;

        private readonly List<DxfDiagnostic> diagnostics;

        public DxfRenderContext(DXFDocument document, List<DxfDiagnostic> diagnostics)
        {
            this.document = document;
            this.diagnostics = diagnostics;

            double? ltScale = document?.Header?.LineTypeScale;
            if (ltScale.HasValue && ltScale.Value > 0 && !double.IsInfinity(ltScale.Value))
                GlobalLineTypeScale = ltScale.Value;

            layersByKey = IndexByName(
                document?.Tables?.Layers,
                x => x.LayerName);

            lineTypesByKey = IndexByName(
                document?.Tables?.LineTypes,
                x => x.LineTypeName);

            stylesByKey = IndexByName(
                document?.Tables?.Styles,
                x => x.StyleName);
        }

        private static Dictionary<string, T> IndexByName<T>(IEnumerable<T> records, Func<T, string> nameSelector)
        {
            Dictionary<string, T> index = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            if (records == null) return index;

            foreach (T record in records)
            {
                string name = nameSelector(record);
                if (!string.IsNullOrWhiteSpace(name)) index[name.Trim()] = record;
            }

            return index;
        }

        /// <summary>
        /// Enters an entity: resolves its layer and the colour it inherits from its parent.
        /// </summary>
        public void EnterEntity(DXFEntity entity)
        {
            LayerName = string.IsNullOrWhiteSpace(entity?.LayerName) ? "0" : entity.LayerName.Trim();

            LayerIsOff = false;
            LayerIsFrozen = false;
            LayerColorIndex = DxfColorPalette.DefaultColorIndex;

            if (layersByKey.TryGetValue(LayerName, out DXFLayerRecord layer) && layer != null)
            {
                // A negative colour index means "off"; the magnitude is the real colour.
                if (layer.Color < 0)
                {
                    LayerIsOff = true;
                    LayerColorIndex = -layer.Color;
                }
                else
                {
                    LayerColorIndex = layer.Color;
                }

                // Bit 1 of the layer flags is "frozen".
                LayerIsFrozen = (layer.Flags & 1) != 0;
            }
        }

        /// <summary>
        /// Returns whether an entity should be skipped entirely: entities marked
        /// invisible, and entities on a layer that is off or frozen, are not displayed.
        /// </summary>
        public bool ShouldSkip(DXFEntity entity)
        {
            if (entity == null) return true;
            if (entity.IsInvisible) return true;

            return LayerIsOff || LayerIsFrozen;
        }

        /// <summary>
        /// Resolves the fill brush for a filled entity (SOLID, TRACE, solid HATCH). A
        /// filled entity is filled with the same colour it is stroked with.
        /// </summary>
        public Brush ResolveFillBrush(DXFEntity entity) => ResolveBrush(entity);

        /// <summary>
        /// Resolves the final ACI colour index of the current entity.
        /// </summary>
        public int ResolveColorIndex(DXFEntity entity)
        {
            // A true colour (group code 420) wins outright and is already concrete.
            if (entity?.TrueColor.HasValue == true) return DxfColorPalette.ByLayer;

            int entityColor = entity?.EffectiveColorNumber ?? DxfColorPalette.ByLayer;

            return DxfColorPalette.Resolve(entityColor, LayerColorIndex, InheritedColorIndex);
        }

        /// <summary>
        /// Resolves the brush for an entity, honouring a true colour (group code 420)
        /// before falling back to the ACI palette.
        /// </summary>
        public Brush ResolveBrush(DXFEntity entity)
        {
            if (entity?.TrueColor.HasValue == true)
            {
                int value = entity.TrueColor.Value;
                Color trueColor = Color.FromRgb(
                    (byte)((value >> 16) & 0xFF),
                    (byte)((value >> 8) & 0xFF),
                    (byte)(value & 0xFF));

                return CreateBrush(trueColor);
            }

            return ResolveBrush(ResolveColorIndex(entity));
        }

        /// <summary>
        /// Resolves the brush for an ACI index, inverting AutoCAD's black/white index so
        /// it stays legible on the viewer's white background.
        /// </summary>
        public Brush ResolveBrush(int colorIndex)
        {
            if (brushes.TryGetValue(colorIndex, out Brush cached)) return cached;

            int[] rgb = DxfColorPalette.GetRgb(colorIndex);
            Color color;

            if (rgb == null || DxfColorPalette.IsAdaptive(colorIndex))
            {
                // Index 7 is white in AutoCAD, which is drawn black on a white canvas.
                color = DefaultForeground;
            }
            else
            {
                color = Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
            }

            SolidColorBrush brush = CreateBrush(color);
            brushes[colorIndex] = brush;

            return brush;
        }

        private static SolidColorBrush CreateBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();

            return brush;
        }

        /// <summary>The colour used for entities that render as black/white.</summary>
        public Color DefaultForeground { get; set; } = Color.FromRgb(0x1E, 0x1E, 0x1E);

        private readonly Dictionary<int, Brush> brushes = new Dictionary<int, Brush>();

        private readonly Dictionary<int, Pen> penCache = new Dictionary<int, Pen>();

        /// <summary>
        /// Returns the line type record that applies to the current entity, or null for a
        /// continuous line.
        /// </summary>
        public DXFLineTypeRecord ResolveLineType(DXFEntity entity)
        {
            string lineTypeName = entity?.LineType;

            // An entity with no line type is ByLayer.
            if (string.IsNullOrWhiteSpace(lineTypeName))
            {
                if (layersByKey.TryGetValue(LayerName, out DXFLayerRecord layer) && layer != null)
                    lineTypeName = layer.LineType;
            }

            if (string.IsNullOrWhiteSpace(lineTypeName)) return null;

            lineTypeName = lineTypeName.Trim();

            if (string.Equals(lineTypeName, "BYLAYER", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lineTypeName, "BYBLOCK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lineTypeName, "CONTINUOUS", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!lineTypesByKey.TryGetValue(lineTypeName, out DXFLineTypeRecord lineType) || lineType == null)
                return null;

            if (lineType.Elements == null || lineType.Elements.Count == 0) return null;

            return lineType;
        }

        /// <summary>
        /// Returns the effective dash scale for the current entity: the global $LTSCALE
        /// times the entity's own line type scale.
        /// </summary>
        public double ResolveLineTypeScale(DXFEntity entity)
        {
            double entityScale = entity?.LineTypeScale ?? 1.0;
            if (!(entityScale > 0) || double.IsInfinity(entityScale)) entityScale = 1.0;

            return GlobalLineTypeScale * entityScale;
        }

        /// <summary>Looks up a text style record by name.</summary>
        public DXFStyleRecord ResolveStyle(string styleName)
        {
            if (string.IsNullOrWhiteSpace(styleName)) return null;

            return stylesByKey.TryGetValue(styleName.Trim(), out DXFStyleRecord style) ? style : null;
        }

        /// <summary>
        /// Pushes a block insertion scope, handing the INSERT's resolved colour down to
        /// its children so ByBlock children can resolve it. The layer of the enclosing
        /// entity stays in effect, because block children on layer 0 take on the INSERT's
        /// layer properties, which the caller already resolved before entering.
        /// </summary>
        public IDisposable PushBlock(int insertResolvedColorIndex) =>
            PushBlock(
                InheritedColorIndex,
                insertResolvedColorIndex,
                LayerName,
                LayerIsOff,
                LayerIsFrozen);

        /// <summary>
        /// Pushes a block insertion scope, handing the INSERT's resolved colour down to
        /// its children so ByBlock children can resolve it.
        /// </summary>
        public IDisposable PushBlock(
            int parentInheritedColorIndex,
            int insertResolvedColorIndex,
            string insertLayerName,
            bool insertLayerIsOff,
            bool insertLayerIsFrozen)
        {
            BlockState previous = new BlockState
            {
                InheritedColorIndex = InheritedColorIndex,
                LayerColorIndex = LayerColorIndex,
                LayerIsOff = LayerIsOff,
                LayerIsFrozen = LayerIsFrozen,
                LayerName = LayerName,
                BlockDepth = BlockDepth
            };

            // Children of a block are drawn with the INSERT's resolved colour until one of
            // them specifies its own, and on layer 0 children they inherit the INSERT's layer
            // properties. Children on a named layer keep that layer's properties.
            InheritedColorIndex = insertResolvedColorIndex;
            LayerColorIndex = insertResolvedColorIndex;
            LayerIsOff = insertLayerIsOff;
            LayerIsFrozen = insertLayerIsFrozen;
            if (!string.IsNullOrEmpty(insertLayerName)) LayerName = insertLayerName;
            BlockDepth++;

            return new BlockScope(this, previous);
        }

        private struct BlockState
        {
            public int InheritedColorIndex;
            public int LayerColorIndex;
            public bool LayerIsOff;
            public bool LayerIsFrozen;
            public string LayerName;
            public int BlockDepth;
        }

        private sealed class BlockScope : IDisposable
        {
            private DxfRenderContext owner;
            private readonly BlockState previous;

            public BlockScope(DxfRenderContext owner, BlockState previous)
            {
                this.owner = owner;
                this.previous = previous;
            }

            public void Dispose()
            {
                if (owner == null) return;

                owner.InheritedColorIndex = previous.InheritedColorIndex;
                owner.LayerColorIndex = previous.LayerColorIndex;
                owner.LayerIsOff = previous.LayerIsOff;
                owner.LayerIsFrozen = previous.LayerIsFrozen;
                owner.LayerName = previous.LayerName;
                owner.BlockDepth = previous.BlockDepth;
                owner = null;
            }
        }

        public DXFDocument Document => document;

        public void Warn(string message)
        {
            diagnostics?.Add(new DxfDiagnostic(DxfDiagnosticSeverity.Warning, message));
        }
    }

    public enum DxfDiagnosticSeverity
    {
        Information,
        Warning
    }

    /// <summary>One entry in the load report of a DXF document.</summary>
    public sealed class DxfDiagnostic
    {
        public DxfDiagnostic(DxfDiagnosticSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }

        public DxfDiagnosticSeverity Severity { get; }

        public string Message { get; }

        public override string ToString() => $"{Severity}: {Message}";
    }
}
