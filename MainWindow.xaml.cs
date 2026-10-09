using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace NCHops;

public partial class MainWindow : Window
{

    private static readonly Regex GCodeTokenRegex = new(
        "([A-Za-z][+-]?\\d*\\.?\\d*)|(\\s+)|(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DispatcherTimer _refreshTimer;
    private Rect _topRect;
    private Rect _bottomRect;

    // ── Canvas-Zoom / Pan ────────────────────────────────────────
    private double _zoom      = 1.0;
    private double _lastZoom  = 1.0;  // Speichert letzten Zoom um Änderungen zu erkennen
    private double _panX      = 0.0;
    private double _panY      = 0.0;
    private double _dpiScale  = 1.0;  // physische Pixel / logische Pixel (wird in OnDrawSkia aktualisiert)
    private double _lastDpiScale = 1.0;  // Speichert letzten _dpiScale um Änderungen zu erkennen
    private double _canvasScale = 1.0; // Skalierung des Werkstücks auf die Canvas (wird in DrawGCodeTopViewSk aktualisiert)
    private bool   _isPanning = false;
    private Point  _panStart;   // Startpunkt im Parent-Koordinatensystem
    private Point  _panOrigin;  // _panX/_panY beim Drag-Start

    // ── Nullpunkt Position ───────────────────────────────────────
    private int _nullpunktPosition = 6; // 0-8: Oben-Links bis Unten-Rechts (Standard: Unten-Links)
    private bool _nullpunktZAufTisch = false; // false: Z0 = Werkstückoberfläche (Standard), true: Z0 = Maschinentisch

    // ── Aktives Werkzeug ─────────────────────────────────────────
    private enum CanvasTool { Select, Hand, Zoom, VCarveTextSk, Move, Pfeil, Vermassen, PfadStart, PfadLinie, PfadBogen, PfadSpline, Rechteck, Kreis, NEck, Reihenloch }
    private CanvasTool _activeTool    = CanvasTool.Select;
    private bool       _isZoomDragging = false;
    private Point      _zoomDragStart;
    private System.Windows.Shapes.Rectangle? _zoomRubberBand;
    private bool       _isTextDragging = false;
    private Point      _textDragStart;
    private System.Windows.Shapes.Rectangle? _textRubberBand;
    private ImprovedSkiaTextEditor?   _inlineTextBox;
    private int _savedSelectionStart = -1;  // Speichert Selection wenn Textfeld Fokus verliert
    private int _savedSelectionEnd = -1;
    private GraviereParams?           _inlineParams;
    private List<TextToLineSegments.LineSegment>? _inlineTextLineSegments;  // Liniensegmente für Schrift-Zeichnung
    private int                       _inlineExistingIdx = -1; // >=0 = bestehendes Textfeld editieren
    private DispatcherTimer?  _inlineVCarveTimer;      // Debounce: VCarve vorausberechnen während Tippen
    private System.Threading.CancellationTokenSource? _inlineVCarveCts; // laufende Hintergrundberechnung abbrechen
    private int               _moveHistoryIdx   = -1;
    private Point             _moveDragStartMm;
    private double            _moveStartRefX, _moveStartRefY;
    // Resize: -1=verschieben; 0=BL,1=BR,2=TL,3=TR (world Y-up)
    private int               _moveResizeCorner = -1;
    private double            _resizeStartLeft, _resizeStartBottom, _resizeStartWidth, _resizeStartHeight;
    private bool              _ctrlResizeMode   = false; // Ctrl gehalten während Inline-Edit
    private int               _ctrlResizeReopen = -1;   // History-Idx nach Ctrl-Resize wieder öffnen

    // ── Pfad-Werkzeuge (Canvas-Klick) ───────────────────────────
    private bool             _pfadBogenWaiting = false;  // Wartet auf Bogenmittelpunkt
    private (double x, double y) _pfadBogenEndAbs;       // Bogen-Endpunkt (absolut mm)
    private (double x, double y) _pfadMouseMm;            // Aktuell gefangene Mausposition (mm)
    private bool             _pfadMouseValid = false;
    private List<(double x, double y)> _splinePointsBeingCreated = [];
    private string _splineModeBeingCreated = "Catmull-Rom";
    private double _splineTensionBeingCreated = 0.5;
    private bool _splineFinalized = false;  // Markiert ob Spline fertig ist

    // ── Rechteck-Werkzeug ────────────────────────────────────────
    private bool             _rktDragging  = false;
    private Point            _rktDragStart;             // Canvas-Pixel
    private System.Windows.Shapes.Rectangle? _rktRubberBand;

    private bool             _kreisDragging = false;
    private Point            _kreisDragCenter;          // Canvas-Pixel (Mittelpunkt)
    private System.Windows.Shapes.Ellipse? _kreisRubberBand;
    private TextBox?         _kreisInputBox;

    private bool             _neckDragging = false;
    private Point            _neckDragCenter;           // Canvas-Pixel (Mittelpunkt)
    private System.Windows.Shapes.Polygon? _neckRubberBand;

    // ── Pfad-Punkte verschieben (Move-Werkzeug) ──────────────
    private int              _pfadDragHistIdx = -1;       // History-Idx des gezogenen Pfad-Punkts
    private (double x, double y) _pfadDragOrigAbs;        // Ursprüngliche Absolut-Position

    // ── Ganzen Pfad verschieben (Move-Werkzeug) ──
    private int              _pfadChainDragIdx = -1;      // History-Idx des Startpunkts der Kette
    private (double x, double y) _pfadChainDragMouse;
    private List<(double x, double y)> _pfadChainDragOrigAbs = [];

    // ── Pfad-Kette skalieren (Ankerpunkte der Bounding-Box) ──
    private int    _pfadScaleChainIdx = -1;
    private int    _pfadScaleAnchor   = -1;     // 0-7, s. AnchorPosMm()
    private (double x, double y) _pfadScaleOriginMm;
    private (double minX, double minY, double maxX, double maxY) _pfadScaleOrigBBox;
    private List<(double x, double y)> _pfadScaleOrigAbs = [];

    // ── Pfad-Segment-Mittelpunkt verschieben (Pfeil-Werkzeug) ──
    private int              _pfadSegDragIdx = -1;        // History-Idx des Segment-Endpunkts (p2)
    private bool             _pfadSegDragIsArc;           // true = Bogen (Pfeilhöhe ändern)
    private (double x, double y) _pfadSegDragP1;         // Abs-Position p1 bei Drag-Start
    private (double x, double y) _pfadSegDragP2;         // Abs-Position p2 bei Drag-Start
    private (double x, double y) _pfadSegDragMouse;      // Maus-Abs bei Drag-Start

    // ── Reihenlochbohrung-Werkzeug (Anker + Pfeile X/Y) ──────────
    private const double RlbStdAbstand = 32.0;   // Standard-Lochabstand (System 32)
    private const double RlbStdAbstandTasche = 64.0; // Standard-Lochabstand bei Bohrart Kreistasche
    private const double RlbStdTascheD = 35.0;   // Standard-Durchmesser Kreistasche (Topfband)
    private const double RlbStdTiefe   = -13.0;  // Standard-Bohrtiefe
    private int    _rlbIdx  = -1;                // History-Idx der bearbeiteten Lochreihe
    private int    _rlbDrag = 0;                 // 0 = keiner, 1 = Anker, 2 = Pfeil X, 3 = Pfeil Y
    private ReihenlochbohrungParams? _rlbPreview; // Vorschau während des Ziehens
    private (double x, double y) _rlbDragMouse;  // Maus (Standard-mm) bei Drag-Start
    private (double x, double y) _rlbDragRef;    // Anker bzw. Pfeilspitze (Standard-mm) bei Drag-Start
    private double _rlbArrowTip;                 // aktuelle Pfeilspitze (X bzw. Y) während des Ziehens
    private ReihenlochbohrungParams? _rlbDragOrig; // Lochreihe bei Drag-Start
    private (double x, double y)? _rlbHoverMm;   // am Raster gefangener Setzpunkt (Standard-mm), null = keiner

    // ── Vermassen-Werkzeug ───────────────────────────────────────
    private enum VermKind { Length, ParallelDist, Angle, EdgeDist, EdgeAngle, PointDist, LineToPoint, PointEdgeDist, Coincident, Perpendicular, Parallel, ParallelEdge, PerpendicularEdge, CoincidentCorner,
                            KreisDurchmesser, KreisRadius, KreisKantenDist,
                            RktSeite, RktKantenDist }
    // Kreis-Bemassungen: P2Idx = History-Index des Kreises (P1Idx = -1).
    //   KreisDurchmesser/KreisRadius: Offset = Winkel (rad) der Masslinie ab Kreismitte
    //   KreisKantenDist: Abstand Kreismitte → Werkstückkante (Edge), Offset wie PointEdgeDist
    // Rechteck-Bemassungen: P2Idx = History-Index des Rechtecks + Seite * RlbAnkerOffset (P1Idx = -1),
    //   Seite 0=unten 1=rechts 2=oben 3=links (siehe GetRektSeiten).
    //   RktSeite: Länge der Seite (Breite bzw. Höhe), Offset = Versatz der Masslinie wie Length
    //   RktKantenDist: Abstand Seite → parallele Werkstückkante (Edge), Offset wie EdgeDist
    private enum GeomConstraintMode { None, Coincident, Perpendicular, Parallel }
    private record VermEntry(
        VermKind Kind, int P1Idx, int P2Idx, double Offset, double Value,
        int Q1Idx = -1, int Q2Idx = -1, int Edge = 0,
        double DirX = 0, double DirY = 0);  // normierter Richtungsvektor P1→P2 (nur Length/PointDist)
    // Edge: 0=none, 1=left(x=0), 2=right(x=WorkX), 3=bottom(y=0), 4=top(y=WorkY)
    // States: 0=idle, 1=seg1 gewählt (Länge-Vorschau), 2=TextBox,
    //         3=Drag bestehende Linie, 4=Edit bestehendes Label,
    //         5=seg2 gewählt (Parallel/Winkel-Vorschau, warte auf 3. Klick)
    private int    _vermState  = 0;
    private int    _vermP1Idx  = -1;
    private int    _vermP2Idx  = -1;
    private (double x, double y) _vermP1Abs;
    private (double x, double y) _vermP2Abs;
    private int    _vermQ1Idx  = -1;   // 2. Segment für ParallelDist / Angle
    private int    _vermQ2Idx  = -1;
    private (double x, double y) _vermQ1Abs;
    private (double x, double y) _vermQ2Abs;
    private VermKind _vermActiveKind = VermKind.Length;
    private double _vermOffset = 0;
    private (double x, double y) _vermMouseMm;
    private TextBox? _vermTextBox;
    private readonly List<VermEntry> _vermPlaced = new();
    private int _vermHoverP1  = -1;   // Hover-Segment im State 0 (für Hervorhebung)
    private int _vermHoverP2  = -1;
    private int _vermHoverEdge  = 0;   // 0=none, 1-4: hovered workpiece edge
    private int _vermHoverPoint = -1;  // Index eines gehoverten Pfad-Punktes
    private int _vermActiveEdge = 0;   // edge selected for current EdgeDist placement
    private GeomConstraintMode _geomMode       = GeomConstraintMode.None;
    private int                _geomFirstIdx   = -1; // erster geklickter Punkt/Segment-Idx
    private int                _geomFirstIdx2  = -1; // zweiter Punkt des ersten Segments
    private int                _selectedGeomIdx = -1; // gewähltes Geom-Constraint-Symbol (-1 = keines)
    private int _vermPtIdx      = -1;  // erster gewählter Punkt (PointDist / LineToPoint)
    private int _vermKreisIdx   = -1;  // gewählter Kreis (History-Idx) für Kreis-Bemassungen
    private int _vermHoverKreis = -1;  // gehoverter Kreis (History-Idx)
    private int _vermHoverRkt   = -1;  // gehoverte Rechteck-Seite (History-Idx + Seite * RlbAnkerOffset)
    private int _vermRktIdx     = -1;  // gewählte Rechteck-Seite (History-Idx + Seite * RlbAnkerOffset)
    private int _vermEditIdx  = -1;   // Index in _vermPlaced für State 3/4
    private double _vermDragOffset;   // Vorschau-Offset beim Ziehen (State 3)
    private bool _vermIsHolding = false;  // Maustaste nach 1. Klick gehalten (Drag-Positionierung)
    private (double x, double y) _vermDownMm; // Klick-Position beim Drücken (für Bewegungs-Schwellwert)

    // ── Template-Image (Schablone) ────────────────────────────────────────
    private TemplateImage? _templateImage;
    private int _templateImageDragAnchor = -1;  // Welcher Ankerpunkt wird gerade gezogen (-1 = keiner)
    private bool _showTemplateContextMenu = false;  // Kontextmenü für Template anzeigen

    // ── G-Code Zeilenmarkierung ───────────────────────────────────
    private int _highlightGCodeLine = -1;   // Caret-Zeile
    private int _mouseHoverLine     = -1;   // Maus-Hover im Editor (Vorrang)
    private int _selectedGCodeLine  = -1;   // Klick auf Werkstück
    private int _selectionSource    =  0;   // 0=Draufsicht, 1=Seitenansicht
    private GCodeLineBackgroundRenderer? _gcodeBgRenderer;
    private readonly DispatcherTimer _hlTimer;   // Debounce 80 ms
    private readonly DispatcherTimer _eigTimer;  // Debounce 400 ms für Eigenschaften-Texteingabe
    private readonly DispatcherTimer _simTimer;  // ~60 fps Animations-Tick

    // Simulation state
    private bool   _simPlaying;
    private bool   _simSliderBusy; // verhindert Rückkopplungsschleife beim Slider-Update
    private bool   _simPathDirty = true;
    private double _simPosMm;
    private double _simTotalMm;
    private double _simSpeedMult = 1.0;
    private DateTime _simLastTick;

    private record struct SimSeg(
        double X0, double Y0, double X1, double Y1,
        double Len, double CumStart,
        bool IsRapid, double FeedMmMin,
        bool IsArc, double Cx, double Cy, double R, double A0, double DA);

    private List<SimSeg> _simSegs = [];
    private CancellationTokenSource? _regenCts;
    private bool _needsAutoFit = true;
    private bool _suppressNextAutoFit = false;
    private bool _rasterEnabled = true;
    private double _rasterX = 10.0;
    private double _rasterY = 10.0;
    private readonly ObservableCollection<HistoryEntry> _history = [];
    private readonly List<HistoryEntry> _historyClipboard = [];
    private System.ComponentModel.ICollectionView? _historyView;
    // Verlauf-Eintrag → G-Code-Zeilenbereich (1-basiert, inklusiv)
    private Dictionary<HistoryEntry, (int start, int end)> _historyLineMap = [];
    private bool _suppressHistoryRegen;
    private readonly ObservableCollection<Werkzeug> _werkzeuge = [];
    private Werkzeug? _aktivesWerkzeug;
    private bool _suppressSave;
    private bool _suppressGCodeUiUpdate;

    // G-Code Backing-Field: Canvas/Parser nutzen dies direkt, TextBox wird im Hintergrund gesetzt
    private string _gcodeContent = string.Empty;

    // Parse-Cache: G-Code nur neu parsen wenn Text sich ändert
    private string?         _parsedGCodeText   = null;
    private List<Move>      _cachedTopMoves    = [];
    private List<SideMove>  _cachedSideMoves   = [];
    private List<DrillHole> _cachedDrillPoints = [];

    // V-Carve: Cache {GraviereParams → Kreisliste} und Ergebnisliste für G-Code
    private readonly Dictionary<GraviereParams, List<GCodeGenerator.VCarveCircle>>
        _vCarveCache = new();
    private readonly Dictionary<GraviereParams, GCodeGenerator.TextGeoCtx>
        _textGeoCache = new();
    private readonly HashSet<GraviereParams> _vCarvePending = new();
    public List<GCodeGenerator.VCarveCircle> VCarveCenters { get; private set; } = [];
    private GraviereParams?  _previewGravParams;
    private RechteckParams?  _previewRktParams;
    private KreisParams?     _previewKreisParams;
    private static readonly string WerkzeugDatei = ProjektPfade.WerkzeugDatei;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

#if false // ── Pfad Fräsen (deaktiviert) ─────────────────────────
    private readonly ObservableCollection<PfadPunkt> _pfadPunkte = [];
    private int    _pfadHoverIdx      = -1;
    private int    _pfadDragIdx       = -1;
    private bool   _arrowJustClicked  = false;
    private double _pfadScale         = 1.0;
    private Rect   _pfadCanvasRect    = Rect.Empty;

    private double PfadSchritt => double.TryParse(PfadTxtSchritt?.Text,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Max(0.1, v) : 5.0;

    private Point AbsMmToPx(double absX, double absY) => new(
        _pfadCanvasRect.Left   + absX * _pfadScale,
        _pfadCanvasRect.Bottom - absY * _pfadScale);

    private (double absX, double absY) PxToAbsMm(Point px) => (
        (px.X - _pfadCanvasRect.Left)   / _pfadScale,
        (_pfadCanvasRect.Bottom - px.Y) / _pfadScale);

    private static (double absX, double absY) RelToAbs(
        string bezug, double relX, double relY, double w, double h)
        => bezug switch
        {
            "Unten links"  => (relX,         relY),
            "Oben links"   => (relX,         h - relY),
            "Unten rechts" => (w - relX,     relY),
            "Oben rechts"  => (w - relX,     h - relY),
            "Links Mitte"  => (relX,         h / 2 + relY),
            "Rechts Mitte" => (w - relX,     h / 2 + relY),
            "Oben Mitte"   => (w / 2 + relX, h - relY),
            "Unten Mitte"  => (w / 2 + relX, relY),
            "Mitte"        => (w / 2 + relX, h / 2 + relY),
            _              => (relX, relY)
        };

    private static (double relX, double relY) AbsToRel(
        string bezug, double absX, double absY, double w, double h)
        => bezug switch
        {
            "Unten links"  => (absX,         absY),
            "Oben links"   => (absX,         h - absY),
            "Unten rechts" => (w - absX,     absY),
            "Oben rechts"  => (w - absX,     h - absY),
            "Links Mitte"  => (absX,         absY - h / 2),
            "Rechts Mitte" => (w - absX,     absY - h / 2),
            "Oben Mitte"   => (absX - w / 2, h - absY),
            "Unten Mitte"  => (absX - w / 2, absY),
            "Mitte"        => (absX - w / 2, absY - h / 2),
            _              => (absX, absY)
        };

    private Point PunktToPx(PfadPunkt p)
    {
        double relX = double.Parse(p.X, System.Globalization.CultureInfo.InvariantCulture);
        double relY = double.Parse(p.Y, System.Globalization.CultureInfo.InvariantCulture);
        var (absX, absY) = RelToAbs(p.Bezug, relX, relY, WorkX, WorkY);
        return AbsMmToPx(absX, absY);
    }
#endif

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            // Schriftarten-ComboBox in den Eigenschaften befüllen
            var fontNames = System.Windows.Media.Fonts.SystemFontFamilies
                                .Select(f => f.Source)
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                .ToList();
            EigFont.ItemsSource = fontNames;

            DrawSkia.PaintSurface += OnDrawSkia;
            CanvasGrid.MouseDown  += OnCanvasMouseDown;
            CanvasGrid.MouseMove  += OnCanvasMouseMove;
            CanvasGrid.MouseUp    += OnCanvasMouseUp;
            CanvasGrid.MouseLeave += OnCanvasMouseLeave;
            CanvasGrid.MouseWheel += OnCanvasMouseWheel;
            // Zoom-Label anklickbar → Reset
            if (TxtZoomLevel is not null)
            {
                var border = (Border)TxtZoomLevel.Parent;
                border.IsHitTestVisible = true;
                border.Cursor           = Cursors.Hand;
                border.ToolTip          = "Klick: 100 % zentriert";
                border.MouseLeftButtonDown += (_, _) =>
                {
                    if (_topRect.IsEmpty) return;
                    double cw3 = DrawSkia.ActualWidth, ch3 = DrawSkia.ActualHeight;
                    ApplyCenterZoom(cw3, ch3, DefaultZoom(cw3, ch3));
                    ApplyCanvasTransform();
                    UpdateAll();
                };
            }
            UpdatePfadMenuState();
            UpdateAll();

            // Sicherheits-Repaint: falls DrawSkia.ActualWidth beim ersten UpdateAll() noch 0 war,
            // triggert dieser Aufruf OnDrawSkia erneut, wo der Autofit mit garantiert gültiger Größe läuft.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => DrawSkia?.InvalidateVisual());
        };

        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => UpdateAll(), Dispatcher);
        _refreshTimer.Stop();

        _hlTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background, (_, _) =>
            { _hlTimer.Stop(); UpdateAll(); }, Dispatcher);
        _hlTimer.Stop();

        _eigTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Background, (_, _) =>
            {
                _eigTimer.Stop();
                UpdatePreviewFromFields();
                UpdateEditorFontFamily();  // Auch Editor aktualisieren (nur für ausgewählte Zeichen!)
            }, Dispatcher);
        _eigTimer.Stop();

        _simTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render, OnSimTick, Dispatcher);
        _simTimer.Stop();

        _historyView = System.Windows.Data.CollectionViewSource.GetDefaultView(_history);
        _historyView.Filter = IsHistoryEntryVisible;
        HistoryList.ItemsSource = _historyView;
        _history.CollectionChanged += (_, e) =>
        {
            if (!_suppressHistoryRegen) RegenerateGCodeFromHistory();
            // Masslinien bereinigen wenn ein History-Eintrag entfernt wurde
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                && e.OldStartingIndex >= 0)
                CleanupVermAfterRemove(e.OldStartingIndex);
        };
        _werkzeuge.CollectionChanged += (_, _) => SaveWerkzeuge();
        LoadWerkzeuge();
#if false
        PfadLvPunkte.ItemsSource = _pfadPunkte;
        _pfadPunkte.CollectionChanged += (_, _) => UpdateAll();
#endif
    }

    // ── Werkstückmaße ────────────────────────────────────────────

    private double WorkX => double.TryParse(TxtX.Text, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 800;
    private double WorkY => double.TryParse(TxtY.Text, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 400;
    private double WorkZ => double.TryParse(TxtZ.Text, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 19;

    /// <summary>
    /// Berechnet die Nullpunkt-Koordinaten (X, Y) basierend auf der Nullpunkt-Position.
    /// Position 0-8: 0=ObenLinks, 1=ObenMitte, 2=ObenRechts,
    ///              3=MitteLinks, 4=Mitte, 5=MitteRechts,
    ///              6=UntenLinks, 7=UntenMitte, 8=UntenRechts
    /// </summary>
    private (double nullpunkX, double nullpunkY) GetNullpunktCoordinates()
    {
        double x = _nullpunktPosition switch
        {
            0 or 3 or 6 => 0,           // Links
            1 or 4 or 7 => WorkX / 2,   // Mitte
            2 or 5 or 8 => WorkX,       // Rechts
            _ => 0
        };

        double y = _nullpunktPosition switch
        {
            0 or 1 or 2 => WorkY,       // Oben
            3 or 4 or 5 => WorkY / 2,   // Mitte
            6 or 7 or 8 => 0,           // Unten
            _ => 0
        };

        return (x, y);
    }

    /// <summary>
    /// Berechnet die Offset für alle Koordinaten basierend auf Nullpunkt.
    /// Das Standard-System hat Nullpunkt oben-links (0,0).
    /// Diese Methode berechnet, wie viel jede Koordinate verschoben werden muss.
    /// </summary>
    private (double offsetX, double offsetY) GetNullpunktOffset()
    {
        var (nullX, nullY) = GetNullpunktCoordinates();
        // Offset ist die negative des Nullpunkts (wir wollen von dort aus rechnen)
        return (-nullX, -nullY);
    }

    /// <summary>
    /// Konvertiert Koordinaten vom Standard-System (oben-links) zum Nullpunkt-System.
    /// Wird verwendet für die Speicherung von Bearbeitungen.
    /// </summary>
    private (double x, double y) ConvertToNullpunktCoordinates(double standardX, double standardY)
    {
        // Wenn Nullpunkt = Unten-Links (Standard), keine Konvertierung nötig
        if (_nullpunktPosition == 6)
            return (standardX, standardY);

        // Berechne die Verschiebung vom Werkstück-Ursprung zum Nullpunkt
        var (nullX, nullY) = GetNullpunktCoordinates();

        // Konvertiere: Koordinate relativ zu Nullpunkt
        double newX = standardX - nullX;
        double newY = standardY - nullY;

        return (newX, newY);
    }

    /// <summary>
    /// Konvertiert Koordinaten vom Nullpunkt-System zurück zum Standard-System (oben-links).
    /// Wird verwendet für die Visualisierung von Fräsbahnen.
    /// </summary>
    private (double x, double y) ConvertFromNullpunktCoordinates(double nullpunktX, double nullpunktY)
    {
        // Wenn Nullpunkt = Unten-Links (Standard), keine Konvertierung nötig
        if (_nullpunktPosition == 6)
            return (nullpunktX, nullpunktY);

        // Berechne die Verschiebung vom Werkstück-Ursprung zum Nullpunkt
        var (nullX, nullY) = GetNullpunktCoordinates();

        // Konvertiere zurück: von Nullpunkt-Koordinate zu Standard-Koordinate
        double newX = nullpunktX + nullX;
        double newY = nullpunktY + nullY;

        return (newX, newY);
    }

    /// <summary>
    /// Konvertiert Koordinaten mit Bezugspunkt-Logik und Nullpunkt-Anpassung.
    /// Wird für die Zeichnung von Fräsbahnen verwendet.
    /// </summary>
    private (double x, double y) GetAbsoluteCoordinates(string bezugspunkt, double xRel, double yRel, double workW, double workH)
    {
        // Zuerst: Konvertiere von Nullpunkt-System zu Oben-Links-System
        var (standardX, standardY) = ConvertFromNullpunktCoordinates(xRel, yRel);

        // Dann: Wende Bezugspunkt-Logik an
        return GCodeGenerator.ConvertBezugspunkt(bezugspunkt, standardX, standardY, workW, workH);
    }

    /// <summary>
    /// Passt die Bearbeitungs-Parameter an den aktuellen Nullpunkt an.
    /// Konvertiert alle Koordinaten vom Standard-System zum Nullpunkt-System.
    /// </summary>
    private object AdjustParamsToNullpunkt(object param)
    {
        // Wenn Nullpunkt = Unten-Links (Standard), keine Konvertierung nötig
        if (_nullpunktPosition == 6)
            return param;

        try
        {
            return param switch
            {
                BohrungParams p =>
                    AdjustBohrung(p),

                UmfahrenParams p =>
                    AdjustUmfahren(p),

                TascheFräsenParams p =>
                    AdjustTasche(p),

                KreistascheParams p =>
                    AdjustKreistasche(p),

                RechteckParams p =>
                    AdjustRechteck(p),

                KreisParams p =>
                    AdjustKreis(p),

                NEckParams p =>
                    AdjustNEck(p),

                GraviereParams p =>
                    AdjustGravieren(p),

                PfadPunktParams p =>
                    AdjustPfadPunkt(p),

                _ => param
            };
        }
        catch
        {
            return param; // Bei Fehler unverändert zurückgeben
        }
    }

    private BohrungParams AdjustBohrung(BohrungParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        return p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) };
    }

    private UmfahrenParams AdjustUmfahren(UmfahrenParams p)
    {
        // Umfahren hat keine XRel/YRel Eigenschaften, daher keine Anpassung
        return p;
    }

    private TascheFräsenParams AdjustTasche(TascheFräsenParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        return p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) };
    }

    private KreistascheParams AdjustKreistasche(KreistascheParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        return p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) };
    }

    private RechteckParams AdjustRechteck(RechteckParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        return p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) };
    }

    private KreisParams AdjustKreis(KreisParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        return p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) };
    }

    private NEckParams AdjustNEck(NEckParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        return p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) };
    }

    private GraviereParams AdjustGravieren(GraviereParams p)
    {
        // Gravieren hat verschiedene Eigenschaften, keine direkte X/Y Anpassung möglich
        return p;
    }

    private PfadPunktParams AdjustPfadPunkt(PfadPunktParams p)
    {
        var (newX, newY) = ConvertToNullpunktCoordinates(p.XRel, p.YRel);
        var (newXMid, newYMid) = ConvertToNullpunktCoordinates(p.XMid, p.YMid);
        return p with
        {
            XRel = Math.Round(newX, 3),
            YRel = Math.Round(newY, 3),
            XMid = Math.Round(newXMid, 3),
            YMid = Math.Round(newYMid, 3)
        };
    }

    /// <summary>
    /// Formatiert die Display-Details für einen History-Eintrag, berücksichtigt Nullpunkt.
    /// Ersetzt X/Y Koordinaten in den Details mit Nullpunkt-relativen Werten.
    /// </summary>
    private string FormatHistoryDetails(HistoryEntry entry)
    {
        try
        {
            // Nur Koordinaten anpassen, wenn Nullpunkt nicht Unten-Links ist
            if (_nullpunktPosition == 6)
                return entry.Details; // Unverändert

            var details = entry.Details;

            // Ersetze X/Y Koordinaten in den Details
            var (nullX, nullY) = GetNullpunktCoordinates();

            // Finde alle "X=<zahl>" Muster und ersetze sie
            details = System.Text.RegularExpressions.Regex.Replace(details, @"X=([-+]?\d+\.?\d*)", match =>
            {
                if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
                {
                    double newX = x - nullX;
                    return $"X={newX:F0}";
                }
                return match.Value;
            });

            // Finde alle "Y=<zahl>" Muster und ersetze sie
            details = System.Text.RegularExpressions.Regex.Replace(details, @"Y=([-+]?\d+\.?\d*)", match =>
            {
                if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                {
                    double newY = y - nullY;
                    return $"Y={newY:F0}";
                }
                return match.Value;
            });

            return details;
        }
        catch
        {
            return entry.Details; // Bei Fehler Original-Details
        }
    }

    /// <summary>
    /// Passt den generierten GCode an, um Nullpunkt-Verschiebung zu berücksichtigen.
    /// Verschiebt alle X und Y Koordinaten um den Nullpunkt-Offset.
    /// </summary>
    private string ApplyNullpunktOffsetToGCode(string gcode)
    {
        try
        {
            if (string.IsNullOrEmpty(gcode))
                return gcode;

            var (offsetX, offsetY) = GetNullpunktOffset();
            if (Math.Abs(offsetX) < 0.001 && Math.Abs(offsetY) < 0.001)
                return gcode; // Kein Offset nötig

            var lines = gcode.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            var adjustedLines = new List<string>();

            foreach (var line in lines)
            {
                string adjustedLine = line;

                // G0/G1/G2/G3 Befehle mit X/Y Koordinaten anpassen
                if (!string.IsNullOrEmpty(line) &&
                    (line.Contains("G0") || line.Contains("G1") || line.Contains("G2") || line.Contains("G3")))
                {
                    adjustedLine = AdjustCoordinateLine(line, offsetX, offsetY);
                }

                adjustedLines.Add(adjustedLine);
            }

            return string.Join(Environment.NewLine, adjustedLines);
        }
        catch
        {
            // Bei Fehler den GCode unverändert zurückgeben
            return gcode;
        }
    }

    private string AdjustCoordinateLine(string line, double offsetX, double offsetY)
    {
        try
        {
            var result = line;

            // X Koordinate anpassen
            var xMatch = System.Text.RegularExpressions.Regex.Match(line, @"X([-+]?\d+\.?\d*)");
            if (xMatch.Success)
            {
                string xStr = xMatch.Groups[1].Value;
                if (double.TryParse(xStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
                {
                    double newX = x + offsetX;
                    // Ersetze nur das erste Vorkommen, um Probleme zu vermeiden
                    result = result.Replace($"X{xStr}", $"X{newX:F4}", StringComparison.OrdinalIgnoreCase);
                }
            }

            // Y Koordinate anpassen (auf dem bereits angepassten String)
            var yMatch = System.Text.RegularExpressions.Regex.Match(result, @"Y([-+]?\d+\.?\d*)");
            if (yMatch.Success)
            {
                string yStr = yMatch.Groups[1].Value;
                if (double.TryParse(yStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                {
                    double newY = y + offsetY;
                    result = result.Replace($"Y{yStr}", $"Y{newY:F4}", StringComparison.OrdinalIgnoreCase);
                }
            }

            return result;
        }
        catch
        {
            // Bei Fehler die Zeile unverändert zurückgeben
            return line;
        }
    }

    // ── Menü ─────────────────────────────────────────────────────

    private void OnBildOeffnen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Bild als Schablone öffnen",
            Filter = "Bilder (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|Alle Dateien (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            _templateImage = new TemplateImage(dlg.FileName);
            if (_templateImage.Bitmap != null)
            {
                _templateImage.UpdateAnchorPoints();
                DrawSkia.InvalidateVisual();
            }
            else
            {
                MessageBox.Show("Bild konnte nicht geladen werden.", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                _templateImage = null;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler beim Laden der Schablone: {ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            _templateImage = null;
        }
    }

    private void OnCanvasContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Menü nur öffnen, wenn auf das Bild geklickt wurde
        if (!_showTemplateContextMenu)
        {
            e.Handled = true;
            CanvasGrid.ContextMenu.IsOpen = false;
        }
    }

    private void OnTemplateRotateCW(object sender, RoutedEventArgs e)
    {
        if (_templateImage != null)
        {
            _templateImage.RotateCW();
            DrawSkia?.InvalidateVisual();
        }
    }

    private void OnTemplateRotateCCW(object sender, RoutedEventArgs e)
    {
        if (_templateImage != null)
        {
            _templateImage.RotateCCW();
            DrawSkia?.InvalidateVisual();
        }
    }

    private void OnTemplateFlipH(object sender, RoutedEventArgs e)
    {
        if (_templateImage != null)
        {
            _templateImage.FlipH();
            DrawSkia?.InvalidateVisual();
        }
    }

    private void OnTemplateFlipV(object sender, RoutedEventArgs e)
    {
        if (_templateImage != null)
        {
            _templateImage.FlipV();
            DrawSkia?.InvalidateVisual();
        }
    }

    private void OnTemplateRemove(object sender, RoutedEventArgs e)
    {
        _templateImage = null;
        _templateImageDragAnchor = -1;
        DrawSkia?.InvalidateVisual();
    }

    private void OnSpeichern(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "G-Code speichern",
            Filter = "G-Code (*.nc)|*.nc|Text (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
            DefaultExt = ".nc",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var text = GetGCodeFuerAusgabe();
            File.WriteAllText(dlg.FileName, text,new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Speichern fehlgeschlagen:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnFraesenSenden(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gcodeContent))
        {
            MessageBox.Show(this, "Kein G-Code vorhanden.", "Fräsen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var downloads = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        downloads = System.IO.Path.Combine(downloads, "Downloads");
        string fileName = DateTime.Now.ToString("yyMMdd-HHmm") + ".nc";
        string filePath = System.IO.Path.Combine(downloads, fileName);

        try
        {
            var text = GetGCodeFuerAusgabe();
            File.WriteAllText(filePath, text,new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Speichern fehlgeschlagen:\n{ex.Message}", "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        const string estlcam = @"C:\Program Files (x86)\Estlcam11\estlcam64.exe";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = estlcam,
                Arguments = $"\"{filePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Estlcam konnte nicht gestartet werden:\n{ex.Message}", "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnWerkstueckNullpunkt(object sender, RoutedEventArgs e)
    {
        var dlg = new NullpunktDialog { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _nullpunktPosition = dlg.SelectedPosition;

            // History-Details aktualisieren mit neuen Nullpunkt-Koordinaten
            UpdateHistoryDetailsForNullpunkt();

            // Zeichnung aktualisieren
            DrawSkia.InvalidateVisual();
        }
    }

    private void OnNullpunktZWerkstueck(object sender, RoutedEventArgs e) => SetNullpunktZAufTisch(false);
    private void OnNullpunktZTisch(object sender, RoutedEventArgs e)      => SetNullpunktZAufTisch(true);

    private void SetNullpunktZAufTisch(bool aufTisch)
    {
        _nullpunktZAufTisch = aufTisch;
        MnuNullpunktZWerkstueck.IsChecked = !aufTisch;
        MnuNullpunktZTisch.IsChecked      = aufTisch;
    }

    /// <summary>
    /// G-Code für Datei/Estlcam. Intern beziehen sich alle Z-Werte auf die Werkstückoberfläche
    /// (Vorschau und Seitenansicht bleiben so unverändert). Ist der Z-Nullpunkt auf dem
    /// Maschinentisch, wird beim Ausgeben die Werkstückdicke auf jede absolute Z-Koordinate addiert.
    /// </summary>
    private string GetGCodeFuerAusgabe()
    {
        var text = _gcodeContent.Replace("\r\n", "\n");
        if (_nullpunktZAufTisch)
            text = $"(Nullpunkt Z: Maschinentisch, Werkstückdicke {F4(WorkZ)} addiert)\n"
                 + AddZOffsetToGCode(text, WorkZ);
        return text.Replace("\n", Environment.NewLine);
    }

    private static string F4(double v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Regex RxZWord = new(@"(?<![A-Za-z])Z\s*([-+]?(?:\d+\.?\d*|\.\d+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Addiert <paramref name="offset"/> auf alle Z-Wörter im absoluten Modus (G90).
    /// Kommentare in (…) und nach ';' sowie inkrementelle Z-Werte (G91) bleiben unverändert.
    /// </summary>
    internal static string AddZOffsetToGCode(string gcode, double offset)
    {
        var lines = gcode.Split('\n');
        bool abs = true;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var sbLine = new StringBuilder(line.Length + 8);
            int pos = 0;
            while (pos < line.Length)
            {
                // Code-Abschnitt bis zum nächsten Kommentar
                int open = line.IndexOfAny(['(', ';'], pos);
                int end  = open < 0 ? line.Length : open;
                string code = line.Substring(pos, end - pos);

                if (Regex.IsMatch(code, @"\bG0*90\b", RegexOptions.IgnoreCase)) abs = true;
                if (Regex.IsMatch(code, @"\bG0*91\b", RegexOptions.IgnoreCase)) abs = false;

                if (abs)
                    code = RxZWord.Replace(code, m =>
                    {
                        double z = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        return "Z" + (z + offset).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    });
                sbLine.Append(code);

                if (open < 0) break;
                if (line[open] == ';') { sbLine.Append(line, open, line.Length - open); break; }
                int close = line.IndexOf(')', open);
                if (close < 0) { sbLine.Append(line, open, line.Length - open); break; }
                sbLine.Append(line, open, close - open + 1);
                pos = close + 1;
            }
            lines[i] = sbLine.ToString();
        }
        return string.Join("\n", lines);
    }

    private void UpdateHistoryDetailsForNullpunkt()
    {
        for (int i = 0; i < _history.Count; i++)
        {
            var entry = _history[i];
            string newDetails = FormatHistoryDetails(entry);

            // Wenn Details sich geändert haben, aktualisiere den Eintrag
            if (newDetails != entry.Details)
            {
                _history[i] = new HistoryEntry(entry.Label, newDetails, entry.Params, entry.Level);
            }
        }

        // History-Anzeige aktualisieren
        _historyView?.Refresh();
    }

    /// <summary>
    /// Hilfsmethode zum Erstellen und Speichern einer neuen Bearbeitung mit Nullpunkt-Anpassung.
    /// </summary>
    private void AddHistoryEntry(string label, string details, object param)
    {
        // Parameter an Nullpunkt anpassen
        var adjustedParam = AdjustParamsToNullpunkt(param);

        // Details neu formatieren (für aktuelle Anzeige)
        var formattedDetails = FormatHistoryDetails(
            new HistoryEntry(label, details, adjustedParam, 0));

        _history.Add(new HistoryEntry(label, formattedDetails, adjustedParam));
    }

    /// <summary>
    /// SVG Export: Exportiert G-Code + Text-Konturlinien als SVG
    /// </summary>
    private void OnExportSvg(object sender, RoutedEventArgs e)
    {
        // Überprüfe ob G-Code vorhanden ist
        if (string.IsNullOrWhiteSpace(_gcodeContent))
        {
            MessageBox.Show(this,
                "Kein G-Code vorhanden.\n\nBitte generieren Sie zuerst G-Code durch:\n" +
                "1. Ein Werkzeug aus der \"Fräsen\" Liste auswählen\n" +
                "2. Parameter eingeben\n" +
                "3. G-Code generieren",
                "SVG Export - Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Versuche die letzten GraviereParams zu finden
        var lastGraviereParams = _history
            .Select(h => h.Params)
            .OfType<GraviereParams>()
            .LastOrDefault();

        if (lastGraviereParams == null)
        {
            MessageBox.Show(this,
                "Keine Text-Parameter gefunden.\n\n" +
                "Dies ist nur für Gravieren/V-Carve relevant.",
                "SVG Export - Info",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Trotzdem G-Code exportieren (ohne Text)
            ExportGCodeOnlyToSvg();
            return;
        }

        // SaveFileDialog öffnen
        var saveDialog = new SaveFileDialog
        {
            Filter = "SVG Files (*.svg)|*.svg|All Files (*.*)|*.*",
            DefaultExt = ".svg",
            FileName = $"export-{lastGraviereParams.Text}-{DateTime.Now:yyyy-MM-dd-HHmmss}.svg",
            InitialDirectory = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.MyDocuments)
        };

        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            // Exportiere kombinierte SVG (Text + G-Code)
            SvgExporter.ExportCombined(
                saveDialog.FileName,
                _gcodeContent,
                lastGraviereParams,
                workWidth: 200.0,  // Standard Werkstückgröße
                workHeight: 100.0
            );

            MessageBox.Show(this,
                $"SVG erfolgreich exportiert:\n\n{saveDialog.FileName}",
                "SVG Export - Erfolg",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Optional: Im Explorer öffnen
            var openExplorer = MessageBox.Show(this,
                "Möchten Sie die Datei im Explorer öffnen?",
                "SVG Export",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (openExplorer == MessageBoxResult.Yes)
            {
                var folder = System.IO.Path.GetDirectoryName(saveDialog.FileName);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{saveDialog.FileName}\"",
                        UseShellExecute = true
                    });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Fehler beim SVG-Export:\n\n{ex.Message}",
                "SVG Export - Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Exportiert nur G-Code Werkzeugbahn als SVG (ohne Text)
    /// </summary>
    private void ExportGCodeOnlyToSvg()
    {
        var saveDialog = new SaveFileDialog
        {
            Filter = "SVG Files (*.svg)|*.svg|All Files (*.*)|*.*",
            DefaultExt = ".svg",
            FileName = $"gcode-{DateTime.Now:yyyy-MM-dd-HHmmss}.svg",
            InitialDirectory = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.MyDocuments)
        };

        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            // Exportiere nur G-Code Pfade
            SvgExporter.ExportGCodePaths(
                saveDialog.FileName,
                _gcodeContent,
                workWidth: 200.0,
                workHeight: 100.0,
                title: "G-Code Werkzeugbahn"
            );

            MessageBox.Show(this,
                $"G-Code SVG erfolgreich exportiert:\n\n{saveDialog.FileName}",
                "SVG Export - Erfolg",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Fehler beim SVG-Export:\n\n{ex.Message}",
                "SVG Export - Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnBeenden(object sender, RoutedEventArgs e) => Close();
    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWerkzeuge();
    }

    private void OnPlanfraesen(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new PlanfräsenDialog(WorkX, WorkY, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        p = (PlanfräsenParams)AdjustParamsToNullpunkt(p);
        _history.Add(new HistoryEntry("Planfräsen",
            $"{(p.Horizontal ? "Horizontal" : "Vertikal")}, Z={p.Z}, Ø{p.FraeserD}", p));
    }

    private void OnBohrung(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new BohrungDialog(WorkZ + 3, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        p = (BohrungParams)AdjustParamsToNullpunkt(p);
        _history.Add(new HistoryEntry("Bohrung",
            $"X={p.XRel} Y={p.YRel}, Ø{p.Durchmesser}, Z={p.Bohrtiefe}, {p.Bezugspunkt}", p));
    }

    private void OnReihenlochbohrung(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new ReihenlochbohrungDialog(WorkZ + 3, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        _history.Add(new HistoryEntry("Reihenlochbohrung",
            $"{p.CountX}×{p.CountY}, Ø{p.Diameter}, Z={p.Bohrtiefe}", p));
    }

    private void OnUmfahren(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new UmfahrenDialog(WorkZ, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        _history.Add(new HistoryEntry("Umfahren",
            $"A={p.A}, Ø{p.Diameter}, Z={p.Z}", p));
    }

    private void OnTasche(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new TascheFräsenDialog(-(WorkZ + 3), werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        _history.Add(new HistoryEntry("Tasche",
            $"X={p.XRel} Y={p.YRel}, {p.Breite}×{p.Höhe}, Z={p.ZTiefe}, Ø{p.FraeserD}", p));
    }

    private void OnNut(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new NutFräsenDialog(-(WorkZ + 3), WorkX + 20, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        _history.Add(new HistoryEntry("Nut",
            $"X={p.XRel} Y={p.YRel}, L={p.Länge} B={p.Breite}, Z={p.ZTiefe}, Ø{p.FraeserD}", p));
    }

    private void OnKreistasche(object sender, RoutedEventArgs e)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var dlg = new KreistascheDialog(-(WorkZ + 3), werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result!;
        _history.Add(new HistoryEntry("Kreistasche",
            $"X={p.XRel} Y={p.YRel}, Ø{p.Durchmesser}, Z={p.ZTiefe}", p));
    }

    private bool IsPfadAktiv()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is PfadPunktParams)
                return true;
            if (_history[i].Params is not null)
                return false;
        }
        return false;
    }

    private void UpdatePfadMenuState()
    {
        bool aktiv = IsPfadAktiv();
        MnuPfadLinie.IsEnabled     = aktiv;
        MnuPfadBogen.IsEnabled     = aktiv;
        MnuPfadSpline.IsEnabled    = aktiv;
        BtnToolPfadLinie.IsEnabled = aktiv;
        BtnToolPfadKurve.IsEnabled = aktiv;
        BtnToolPfadSpline.IsEnabled = aktiv;
    }

    // ── Verlauf: Einklappen ──────────────────────────────────────
    private bool IsHistoryEntryVisible(object obj)
    {
        if (obj is not HistoryEntry e || e.Level == 0) return true;
        // Level-1-Eintrag: zum übergeordneten Level-0-Eintrag suchen
        int idx = _history.IndexOf(e);
        for (int i = idx - 1; i >= 0; i--)
        {
            if (_history[i].Level == 0)
                return !_history[i].IsCollapsed;
        }
        return true;
    }

    private void OnHistoryToggleCollapse(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is HistoryEntry entry)
        {
            entry.IsCollapsed = !entry.IsCollapsed;
            _historyView?.Refresh();
            // Klick nicht weiter propagieren – kein ungewolltes Deselect
            e.Handled = true;
        }
    }

    // ── Verlauf: G-Code-Zeilen hervorheben ──────────────────────
    private void HighlightHistoryEntry(HistoryEntry? entry)
    {
        if (_gcodeBgRenderer is null) return;

        // Bei Mehrfach-Selektion: Union aller Bereiche
        var selected = HistoryList.SelectedItems.Cast<HistoryEntry>().ToList();
        int rangeStart = -1, rangeEnd = -1;

        foreach (var e in selected)
        {
            // Bei Pfad-Startpunkt auch alle zugehörigen Sub-Einträge einschließen
            var candidates = (e.Params is PfadPunktParams { Typ: PfadPunktTyp.Start })
                ? _history
                    .SkipWhile(h => !ReferenceEquals(h, e))
                    .TakeWhile(h => ReferenceEquals(h, e) || h.Level == 1)
                    .ToList()
                : [e];

            foreach (var ce in candidates)
            {
                if (!_historyLineMap.TryGetValue(ce, out var r)) continue;
                if (rangeStart < 0) { rangeStart = r.start; rangeEnd = r.end; }
                else { rangeStart = Math.Min(rangeStart, r.start); rangeEnd = Math.Max(rangeEnd, r.end); }
            }
        }

        _gcodeBgRenderer.HistRangeStart = rangeStart;
        _gcodeBgRenderer.HistRangeEnd   = rangeEnd;
        GCodeBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        // Editor zum Bereich scrollen (nur wenn sinnvolle Range)
        if (rangeStart > 0 && GCodeBox.Document.LineCount >= rangeStart)
        {
            var docLine = GCodeBox.Document.GetLineByNumber(
                Math.Min(rangeStart, GCodeBox.Document.LineCount));
            GCodeBox.ScrollTo(docLine.LineNumber, 1);
        }
    }

    private void OnPfadStart(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.PfadStart ? CanvasTool.Select : CanvasTool.PfadStart);

    private void OnPfadLinie(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.PfadLinie ? CanvasTool.Select : CanvasTool.PfadLinie);

    private void OnPfadBogen(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.PfadBogen ? CanvasTool.Select : CanvasTool.PfadBogen);

    private void OnPfadSpline(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.PfadSpline ? CanvasTool.Select : CanvasTool.PfadSpline);

    // ── Pfad-Klick-Werkzeuge: Punkt per Canvas-Klick setzen ─────

    private (double x, double y)? GetPointAtIndex(int idx)
    {
        if (idx < 0 || idx >= _history.Count) return null;
        if (_history[idx].Params is not PfadPunktParams p) return null;

        if (p.Bezugspunkt == "Letzter Punkt" && idx > 0)
        {
            var prev = GetPointAtIndex(idx - 1);
            return prev.HasValue ? (prev.Value.x + p.XRel, prev.Value.y + p.YRel) : null;
        }
        return GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, WorkX, WorkY);
    }

    private (double x, double y)? GetLastPfadAbsPoint()
    {
        var chain = new List<PfadPunktParams>();
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not PfadPunktParams p) break;
            chain.Insert(0, p);
            if (p.Typ == PfadPunktTyp.Start) break;
        }
        if (chain.Count == 0) return null;
        double w = WorkX, h = WorkY;
        (double x, double y) last = (0, 0);
        for (int i = 0; i < chain.Count; i++)
        {
            var p = chain[i];
            if (i > 0 && p.Bezugspunkt == "Letzter Punkt")
                last = (last.x + p.XRel, last.y + p.YRel);
            else
                last = GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, w, h);
        }
        return last;
    }

    private void AddPfadStart(double mmX, double mmY)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var wz = _aktivesWerkzeug;
        var p = new PfadPunktParams(
            XRel: Math.Round(mmX, 3), YRel: Math.Round(mmY, 3),
            ZTiefe:        wz != null ? WorkZ + 3 : 5,
            ZZustellung:   wz?.ZZustellung  ?? 5,
            FraeserD:      wz?.Durchmesser  ?? 10,
            Drehzahl:      wz?.Drehzahl     ?? 18000,
            Vorschub:      wz?.VorschubFxy  ?? 3000,
            VorschubFz:    wz?.VorschubFz   ?? 500,
            Radiuskorrektur: "Rechts",
            Bezugspunkt:   "Unten links",
            Typ:           PfadPunktTyp.Start,
            Eintauchwinkel: wz?.Eintauchwinkel ?? 90
        );
        _suppressNextAutoFit = true;
        p = (PfadPunktParams)AdjustParamsToNullpunkt(p);
        _history.Add(new HistoryEntry("Pfad Start",
            $"X={p.XRel} Y={p.YRel}, Z={p.ZTiefe}", p));
        UpdatePfadMenuState();
        HistoryList.SelectedItem    = _history[^1];
        TabEigenschaften.IsSelected = true;
        SetActiveTool(CanvasTool.PfadLinie);
    }

    // ── Rechteck ────────────────────────────────────────────────
    private void AddRechteck(double x0mm, double y0mm, double bMm, double hMm)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var wz = _aktivesWerkzeug;
        var p = new RechteckParams(
            XRel:        Math.Round(x0mm, 3),
            YRel:        Math.Round(y0mm, 3),
            Breite:      bMm,
            Hoehe:       hMm,
            ZTiefe:      wz != null ? -(Math.Abs(WorkZ)) : -5,
            FraeserD:    wz?.Durchmesser ?? 6,
            Drehzahl:    wz?.Drehzahl    ?? 18000,
            Vorschub:    wz?.VorschubFxy ?? 3000,
            VorschubFz:  wz?.VorschubFz  ?? 500,
            Bezugspunkt: "Unten links",
            Fraesung:    "Aussen",
            Laufrichtung:"Gegenlauf",
            Verrundung:  0,
            WerkzeugNr:  wz?.Nr ?? 0,
            Eintauchwinkel:    wz?.Eintauchwinkel ?? 3,
            MehrfachZustellung: wz != null,
            ZZustellung:       wz?.ZZustellung ?? 2
        );
        _suppressNextAutoFit = true;
        _history.Add(new HistoryEntry("Rechteck",
            $"X={p.XRel} Y={p.YRel}, {p.Breite}×{p.Hoehe}, Z={p.ZTiefe}", p));
        HistoryList.SelectedItem    = _history[^1];
        TabEigenschaften.IsSelected = true;
    }

    private void OnRechteckTool(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Rechteck ? CanvasTool.Select : CanvasTool.Rechteck);

    private void ApplyRechteckEig()
    {
        if (_eigSuppressUpdate) return;
        int idx = HistoryList.SelectedIndex;
        if (idx < 0 || _history[idx].Params is not RechteckParams rkt) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        string Norm(string s) => s.Replace(',', '.');

        if (!double.TryParse(Norm(RktEigX.Text),    sty, inv, out var x))    return;
        if (!double.TryParse(Norm(RktEigY.Text),    sty, inv, out var y))    return;
        if (!double.TryParse(Norm(RktEigBreite.Text),sty, inv, out var b))   return;
        if (!double.TryParse(Norm(RktEigHoehe.Text), sty, inv, out var h))   return;
        if (!double.TryParse(Norm(RktEigZ.Text),    sty, inv, out var z))    return;
        if (!double.TryParse(Norm(RktEigVer.Text),  sty, inv, out var ver))  return;
        bool mehrfach = RktEigMehrfach.IsChecked == true;
        double.TryParse(Norm(RktEigZZust.Text), sty, inv, out var zzust);

        string bezug = RktBezugName();
        string fraesung = (RktEigFrAussen.IsChecked == true) ? "Aussen"
                        : (RktEigFrInnen.IsChecked  == true) ? "Innen"
                        : "Mittig";
        string lauf = (RktEigGleich.IsChecked == true) ? "Gleichlauf" : "Gegenlauf";
        bool isTasche = RktModusTasche.IsChecked == true;

        var np = rkt with
        {
            XRel = x, YRel = y, Breite = b, Hoehe = h, ZTiefe = z,
            Bezugspunkt         = bezug,
            Fraesung            = fraesung,
            Laufrichtung        = lauf,
            Verrundung          = ver,
            MehrfachZustellung  = mehrfach,
            ZZustellung         = zzust > 0 ? zzust : rkt.ZZustellung,
            IsTasche            = isTasche,
        };
        UpdateRktModusVisibility(isTasche);

        _eigSuppressUpdate = true;
        _suppressHistoryRegen = true;
        try { _history[idx] = new HistoryEntry("Rechteck",
            $"X={np.XRel} Y={np.YRel}, {np.Breite}×{np.Hoehe}, Z={np.ZTiefe}", np); }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = idx;
    }

    // Name des gewählten Bezugspunkts aus den RadioButtons
    private string RktBezugName()
    {
        if (RktBezugOL.IsChecked == true) return "Oben links";
        if (RktBezugOM.IsChecked == true) return "Oben Mitte";
        if (RktBezugOR.IsChecked == true) return "Oben rechts";
        if (RktBezugML.IsChecked == true) return "Links Mitte";
        if (RktBezugMM.IsChecked == true) return "Mitte";
        if (RktBezugMR.IsChecked == true) return "Rechts Mitte";
        if (RktBezugUM.IsChecked == true) return "Unten Mitte";
        if (RktBezugUR.IsChecked == true) return "Unten rechts";
        return "Unten links";
    }

    private void SetRktBezugRadio(string name)
    {
        RktBezugOL.IsChecked = name == "Oben links";
        RktBezugOM.IsChecked = name == "Oben Mitte";
        RktBezugOR.IsChecked = name == "Oben rechts";
        RktBezugML.IsChecked = name == "Links Mitte";
        RktBezugMM.IsChecked = name == "Mitte";
        RktBezugMR.IsChecked = name == "Rechts Mitte";
        RktBezugUL.IsChecked = name == "Unten links";
        RktBezugUM.IsChecked = name == "Unten Mitte";
        RktBezugUR.IsChecked = name == "Unten rechts";
    }

    private void UpdateRktModusVisibility(bool isTasche)
    {
        var vis = isTasche ? Visibility.Collapsed : Visibility.Visible;
        PnlRktFraesung.Visibility   = vis;
        PnlRktLauf.Visibility       = vis;
    }

    private string KrBezugName()
    {
        if (KrBezugOL.IsChecked == true) return "Oben links";
        if (KrBezugOM.IsChecked == true) return "Oben Mitte";
        if (KrBezugOR.IsChecked == true) return "Oben rechts";
        if (KrBezugML.IsChecked == true) return "Links Mitte";
        if (KrBezugMR.IsChecked == true) return "Rechts Mitte";
        if (KrBezugUM.IsChecked == true) return "Unten Mitte";
        if (KrBezugUR.IsChecked == true) return "Unten rechts";
        if (KrBezugUL.IsChecked == true) return "Unten links";
        return "Mitte"; // KrBezugMM default
    }

    private void SetKrBezugRadio(string name)
    {
        KrBezugOL.IsChecked = name == "Oben links";
        KrBezugOM.IsChecked = name == "Oben Mitte";
        KrBezugOR.IsChecked = name == "Oben rechts";
        KrBezugML.IsChecked = name == "Links Mitte";
        KrBezugMM.IsChecked = name == "Mitte";
        KrBezugMR.IsChecked = name == "Rechts Mitte";
        KrBezugUL.IsChecked = name == "Unten links";
        KrBezugUM.IsChecked = name == "Unten Mitte";
        KrBezugUR.IsChecked = name == "Unten rechts";
    }

    private void UpdateKrModusVisibility(bool isTasche)
    {
        var vis = isTasche ? Visibility.Collapsed : Visibility.Visible;
        PnlKrFraesung.Visibility = vis;
    }

    private static TascheFräsenParams RechteckToTasche(RechteckParams p) =>
        new(XRel: p.XRel, YRel: p.YRel, Breite: p.Breite, Höhe: p.Hoehe,
            ZTiefe: p.ZTiefe, ZZustellung: p.ZZustellung > 0 ? p.ZZustellung : 2,
            FraeserD: p.FraeserD, Faktor: 0.5,
            Vorschub: p.Vorschub, VorschubFz: p.VorschubFz, Drehzahl: p.Drehzahl,
            Bezugspunkt: p.Bezugspunkt, Eintauchwinkel: p.Eintauchwinkel,
            Verrundung: p.Verrundung);

    private void OnRktEigChanged(object sender, RoutedEventArgs e)   => ApplyRechteckEig();
    private void OnRktEigTextChanged(object sender, TextChangedEventArgs e) { /* LostFocus reicht */ }
    private void OnRktEigLostFocus(object sender, RoutedEventArgs e) => ApplyRechteckEig();
    private void OnRktEigKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyRechteckEig(); }

    private void AddKreis(double cxMm, double cyMm, double radiusMm)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var wz = _aktivesWerkzeug;
        string bezug = KrBezugName();
        var (xRel, yRel) = AbsToRel(bezug, cxMm, cyMm, WorkX, WorkY);
        var p = new KreisParams(
            XRel:        Math.Round(xRel, 3),
            YRel:        Math.Round(yRel, 3),
            Radius:      Math.Round(radiusMm, 3),
            ZTiefe:      wz != null ? -(Math.Abs(WorkZ)) : -5,
            FraeserD:    wz?.Durchmesser ?? 6,
            Drehzahl:    wz?.Drehzahl    ?? 18000,
            Vorschub:    wz?.VorschubFxy ?? 3000,
            VorschubFz:  wz?.VorschubFz  ?? 500,
            Fraesung:    "Aussen",
            Laufrichtung:"Gegenlauf",
            WerkzeugNr:  wz?.Nr ?? 0,
            Eintauchwinkel:    wz?.Eintauchwinkel ?? 3,
            MehrfachZustellung: wz != null,
            ZZustellung:       wz?.ZZustellung ?? 2,
            Bezugspunkt:       bezug,
            IsTasche:          false
        );
        _suppressNextAutoFit = true;
        _history.Add(new HistoryEntry("Kreis",
            $"M={p.XRel}/{p.YRel} R={p.Radius} Z={p.ZTiefe}", p));
        HistoryList.SelectedItem    = _history[^1];
        TabEigenschaften.IsSelected = true;
    }

    private void OnKreisTool(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Kreis ? CanvasTool.Select : CanvasTool.Kreis);

    // ── Reihenlochbohrung-Werkzeug ───────────────────────────────
    // Klick auf freie Fläche erzeugt eine Standard-Bohrung. Der Anker (1. Loch unten links)
    // verschiebt die ganze Reihe, die Pfeile rechts/oben fügen Bohrungen hinzu: es werden so
    // viele Löcher erzeugt, wie im Lochabstand bis zur Pfeilspitze Platz haben.
    // Koordinaten der Hilfsfunktionen: Standard-mm (Werkstück unten links = 0/0).

    private void OnReihenlochTool(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Reihenloch ? CanvasTool.Select : CanvasTool.Reihenloch);

    private static string RlbDetails(ReihenlochbohrungParams p)
        => $"{p.CountX}×{p.CountY}, Ø{p.LochD}{(p.LochD > p.Diameter ? " Tasche" : "")}, Z={p.Bohrtiefe}";

    // Aktuell bearbeitete Lochreihe (Vorschau während des Ziehens)
    private ReihenlochbohrungParams? RlbActive()
    {
        if (_rlbPreview != null) return _rlbPreview;
        if (_rlbIdx < 0 || _rlbIdx >= _history.Count) return null;
        return _history[_rlbIdx].Params as ReihenlochbohrungParams;
    }

    private (double x, double y) RlbStart(ReihenlochbohrungParams p)
        => ConvertFromNullpunktCoordinates(p.StartX, p.StartY);

    // Abstand Mittelpunkt letztes Loch → Pfeilspitze im Ruhezustand (immer < Lochabstand,
    // damit beim Anfassen des Pfeils keine Bohrung dazukommt oder wegfällt)
    private double RlbArrowLen(ReihenlochbohrungParams p, double spacing)
        => Math.Min(spacing * 0.9, p.LochD / 2 + 28.0 / _zoom);

    private (double x, double y) RlbArrowTipX(ReihenlochbohrungParams p)
    {
        var (sx, sy) = RlbStart(p);
        return (sx + (p.CountX - 1) * p.SpacingX + RlbArrowLen(p, p.SpacingX), sy);
    }

    private (double x, double y) RlbArrowTipY(ReihenlochbohrungParams p)
    {
        var (sx, sy) = RlbStart(p);
        return (sx, sy + (p.CountY - 1) * p.SpacingY + RlbArrowLen(p, p.SpacingY));
    }

    // Anzahl Löcher, die zwischen Start und Pfeilspitze Platz haben (Loch komplett im Werkstück)
    private static int RlbCountTo(double start, double tip, double spacing, double radius, double work)
    {
        if (spacing <= 0) return 1;
        int n    = (int)Math.Floor((tip - start) / spacing + 1e-9) + 1;
        int nMax = (int)Math.Floor((work - radius - start) / spacing + 1e-9) + 1;
        return Math.Max(1, Math.Min(n, nMax));
    }

    // 0 = nichts, 1 = Anker, 2 = Pfeil X, 3 = Pfeil Y, 4 = Abstand-Y-Anker (2. Loch in Y),
    // 5 = End-Anker X (letztes Loch in X), 6 = End-Anker Y (letztes Loch in Y, ab 3 Löchern)
    private int RlbHitHandle(double mmX, double mmY)
    {
        var p = RlbActive(); if (p == null) return 0;
        double tol = 8.0 / _zoom;
        bool Near((double x, double y) a) => Math.Abs(mmX - a.x) <= tol && Math.Abs(mmY - a.y) <= tol;
        var (sx, sy) = RlbStart(p);
        var tx = RlbArrowTipX(p);
        var ty = RlbArrowTipY(p);
        if (Near(tx)) return 2;
        if (Near(ty)) return 3;
        if (Near((sx, sy))) return 1;
        if (RlbAnkerPos(p, 1) is { } a1 && Near(a1)) return 4;
        if (RlbAnkerPos(p, 2) is { } aX && Near(aX)) return 5;
        if (RlbAnkerPos(p, 3) is { } aY && Near(aY)) return 6;
        // Pfeilschaft (Strecke letztes Loch → Spitze)
        double lastX = sx + (p.CountX - 1) * p.SpacingX, lastY = sy + (p.CountY - 1) * p.SpacingY;
        if (Math.Abs(mmY - sy) <= tol / 2 && mmX >= lastX + p.LochD / 2 && mmX <= tx.x) return 2;
        if (Math.Abs(mmX - sx) <= tol / 2 && mmY >= lastY + p.LochD / 2 && mmY <= ty.y) return 3;
        return 0;
    }

    // Treffer auf ein Loch irgendeiner Lochreihe → History-Idx (-1 = keiner)
    private int RlbHitHole(double mmX, double mmY)
    {
        double tol = 4.0 / _zoom;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not ReihenlochbohrungParams p) continue;
            var (sx, sy) = RlbStart(p);
            double r = p.LochD / 2 + tol;
            for (int iy = 0; iy < p.CountY; iy++)
            for (int ix = 0; ix < p.CountX; ix++)
            {
                double dx = mmX - (sx + ix * p.SpacingX), dy = mmY - (sy + iy * p.SpacingY);
                if (dx * dx + dy * dy <= r * r) return i;
            }
        }
        return -1;
    }

    private void RlbMouseDown(double mmX, double mmY)
    {
        int handle = RlbHitHandle(mmX, mmY);
        if (handle == 0)
        {
            int hole = RlbHitHole(mmX, mmY);
            if (hole >= 0)
            {
                // Lochreihe anwählen und direkt am Anker verschieben
                _rlbIdx = hole;
                HistoryList.SelectedItem = _history[hole];
                handle = 1;
            }
        }
        if (handle == 0)
        {
            AddStandardReihenloch(SnapX(mmX), SnapY(mmY));
            return;
        }

        var p = RlbActive()!;
        _rlbDrag      = handle;
        _rlbPreview   = p;
        _rlbDragOrig  = p;
        _rlbDragMouse = (mmX, mmY);
        _rlbDragRef   = handle switch
        {
            2 => RlbArrowTipX(p),
            3 => RlbArrowTipY(p),
            4 => RlbAnkerPos(p, 1) ?? RlbStart(p),
            5 => RlbAnkerPos(p, 2) ?? RlbStart(p),
            6 => RlbAnkerPos(p, 3) ?? RlbStart(p),
            _ => RlbStart(p)
        };
        _rlbArrowTip = handle == 3 ? _rlbDragRef.y : _rlbDragRef.x;
        CanvasGrid.CaptureMouse();
        CanvasGrid.Cursor = handle switch { 2 or 5 => Cursors.SizeWE, 3 or 4 or 6 => Cursors.SizeNS, _ => Cursors.SizeAll };
        DrawSkia?.InvalidateVisual();
    }

    private void RlbMouseMove(double mmX, double mmY)
    {
        if (_rlbDrag == 0 || _rlbPreview == null)
        {
            CanvasGrid.Cursor = RlbHitHandle(mmX, mmY) switch
            {
                1 => Cursors.SizeAll, 2 or 5 => Cursors.SizeWE, 3 or 4 or 6 => Cursors.SizeNS,
                _ => RlbHitHole(mmX, mmY) >= 0 ? Cursors.SizeAll : Cursors.Cross
            };
            // Fangpunkt nur dort, wo ein Klick eine neue Lochreihe setzt
            (double x, double y)? hover = CanvasGrid.Cursor == Cursors.Cross ? (SnapX(mmX), SnapY(mmY)) : null;
            if (hover != _rlbHoverMm)
            {
                _rlbHoverMm = hover;
                DrawSkia?.InvalidateVisual();
            }
            return;
        }
        _rlbHoverMm = null;

        var p = _rlbPreview;
        double r = p.LochD / 2;
        var (sx, sy) = RlbStart(p);
        double dx = mmX - _rlbDragMouse.x, dy = mmY - _rlbDragMouse.y;
        switch (_rlbDrag)
        {
            case 1:
            {
                var (nx, ny) = ConvertToNullpunktCoordinates(SnapX(_rlbDragRef.x + dx), SnapY(_rlbDragRef.y + dy));
                _rlbPreview = p with { StartX = Math.Round(nx, 3), StartY = Math.Round(ny, 3) };
                break;
            }
            case 2:
                _rlbArrowTip = Math.Max(sx, Math.Min(WorkX, _rlbDragRef.x + dx));
                _rlbPreview  = p with { CountX = RlbCountTo(sx, _rlbArrowTip, p.SpacingX, r, WorkX) };
                break;
            case 3:
                _rlbArrowTip = Math.Max(sy, Math.Min(WorkY, _rlbDragRef.y + dy));
                _rlbPreview  = p with { CountY = RlbCountTo(sy, _rlbArrowTip, p.SpacingY, r, WorkY) };
                break;
            case 4:
            {
                // Lochabstand Y = Abstand 1. → 2. Loch; Anzahl wie bei Drag-Start, soweit sie ins Werkstück passt
                var orig = _rlbDragOrig ?? p;
                double y2 = SnapY(_rlbDragRef.y + dy);
                double spY = Math.Max(p.LochD + 1, Math.Round(y2 - sy, 1));
                _rlbPreview = p with
                {
                    SpacingY = spY,
                    CountY   = RlbCountTo(sy, sy + (orig.CountY - 1) * spY, spY, r, WorkY)
                };
                break;
            }
            case 5:
            {
                // Letztes Loch in X verschieben → Anzahl bleibt, Abstand gleichmässig verteilt
                int n = p.CountX - 1; if (n < 1) break;
                double xl = Math.Min(WorkX - r, SnapX(_rlbDragRef.x + dx));
                double spX = Math.Max(p.LochD + 1, Math.Round((xl - sx) / n, 2));
                _rlbPreview = p with { SpacingX = spX };
                break;
            }
            case 6:
            {
                // Letztes Loch in Y verschieben → Anzahl bleibt, Abstand gleichmässig verteilt
                int n = p.CountY - 1; if (n < 1) break;
                double yl = Math.Min(WorkY - r, SnapY(_rlbDragRef.y + dy));
                double spY = Math.Max(p.LochD + 1, Math.Round((yl - sy) / n, 2));
                _rlbPreview = p with { SpacingY = spY };
                break;
            }
        }
        DrawSkia?.InvalidateVisual();
    }

    private void CommitRlbDrag()
    {
        var final = _rlbPreview;
        _rlbDrag    = 0;
        _rlbPreview = null;
        CanvasGrid.Cursor = Cursors.Cross;
        if (final != null && _rlbIdx >= 0 && _rlbIdx < _history.Count
            && _history[_rlbIdx].Params is ReihenlochbohrungParams old && old != final)
        {
            int idx = _rlbIdx;
            _suppressNextAutoFit = true;
            _history[idx] = new HistoryEntry(_history[idx].Label, RlbDetails(final), final);
            HistoryList.SelectedItem = _history[idx];
        }
        DrawSkia?.InvalidateVisual();
    }

    private void AddStandardReihenloch(double xMm, double yMm)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var wz = _aktivesWerkzeug;
        var (nx, ny) = ConvertToNullpunktCoordinates(xMm, yMm);
        var p = new ReihenlochbohrungParams(
            StartX:     Math.Round(nx, 3),
            StartY:     Math.Round(ny, 3),
            CountX:     1,
            CountY:     1,
            SpacingX:   RlbStdAbstand,
            SpacingY:   RlbStdAbstand,
            Diameter:   wz?.Durchmesser ?? 5,
            Bohrtiefe:  -Math.Min(Math.Abs(RlbStdTiefe), WorkZ),
            Zustellung: wz?.ZZustellung ?? 10,
            VorschubFz: wz?.VorschubFz  ?? 500,
            Drehzahl:   wz?.Drehzahl    ?? 20000,
            Vorschub:       wz?.VorschubFxy    ?? 3000,
            Eintauchwinkel: wz?.Eintauchwinkel ?? 3,
            Faktor:         wz != null ? wz.RaeumzustellungXY / 100.0 : 0.5);
        _suppressNextAutoFit = true;
        _history.Add(new HistoryEntry("Reihenlochbohrung", RlbDetails(p), p));
        _rlbIdx = _history.Count - 1;
        HistoryList.SelectedItem    = _history[^1];
        TabEigenschaften.IsSelected = true;
        DrawSkia?.InvalidateVisual();
    }

    // Eigenschaften-Panel: Lochabstand X/Y und Bohrtiefe übernehmen
    private void ApplyReihenlochEig()
    {
        if (_eigSuppressUpdate) return;
        int idx = _history.IndexOf(HistoryList.SelectedItem as HistoryEntry);
        if (idx < 0 || _history[idx].Params is not ReihenlochbohrungParams p) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        string Norm(string s) => s.Replace(',', '.');
        if (!double.TryParse(Norm(RlbEigAbstandX.Text), sty, inv, out var spX) || spX <= 0) return;
        if (!double.TryParse(Norm(RlbEigAbstandY.Text), sty, inv, out var spY) || spY <= 0) return;
        if (!double.TryParse(Norm(RlbEigTiefe.Text),    sty, inv, out var tiefe))          return;
        bool istTasche = RlbEigKreistasche.IsChecked == true;
        if (!double.TryParse(Norm(RlbEigTascheD.Text), sty, inv, out var tascheD) || tascheD <= 0)
            tascheD = p.TascheD;

        // Bohrart umgeschaltet: Standardwerte setzen (Abstände nur, wenn sie noch dem anderen Standard entsprechen)
        if (istTasche != p.IstKreistasche)
        {
            double von  = istTasche ? RlbStdAbstand : RlbStdAbstandTasche;
            double nach = istTasche ? RlbStdAbstandTasche : RlbStdAbstand;
            if (spX == von) spX = nach;
            if (spY == von) spY = nach;
            if (istTasche && p.TascheD <= 0) tascheD = RlbStdTascheD;
        }

        // Anzahl beibehalten, aber nur so viele Löcher, wie mit dem neuen Abstand ins Werkstück passen
        var (sx, sy) = RlbStart(p);
        var np = p with
        {
            SpacingX       = spX,
            SpacingY       = spY,
            Bohrtiefe      = tiefe,
            IstKreistasche = istTasche,
            TascheD        = Math.Round(tascheD, 3),
        };
        double r = np.LochD / 2;
        np = np with
        {
            CountX = RlbCountTo(sx, sx + (p.CountX - 1) * spX, spX, r, WorkX),
            CountY = RlbCountTo(sy, sy + (p.CountY - 1) * spY, spY, r, WorkY),
        };
        if (np == p) return;

        _eigSuppressUpdate = true;
        try { _history[idx] = new HistoryEntry(_history[idx].Label, RlbDetails(np), np); }
        finally { _eigSuppressUpdate = false; }
        _suppressNextAutoFit = true;
        HistoryList.SelectedItem = _history[idx];
        DrawSkia?.InvalidateVisual();
    }

    // Lochreihe auf dem Werkstück zentrieren (horizontal bzw. vertikal, andere Achse bleibt)
    private void OnRlbEinmittenX(object sender, RoutedEventArgs e) => RlbEinmitten(inX: true);
    private void OnRlbEinmittenY(object sender, RoutedEventArgs e) => RlbEinmitten(inX: false);

    private void RlbEinmitten(bool inX)
    {
        int idx = _history.IndexOf(HistoryList.SelectedItem as HistoryEntry);
        if (idx < 0 || _history[idx].Params is not ReihenlochbohrungParams p) return;
        var (sx, sy) = RlbStart(p);
        if (inX) sx = (WorkX - (p.CountX - 1) * p.SpacingX) / 2;
        else     sy = (WorkY - (p.CountY - 1) * p.SpacingY) / 2;
        var (nx, ny) = ConvertToNullpunktCoordinates(sx, sy);
        var np = p with { StartX = Math.Round(nx, 3), StartY = Math.Round(ny, 3) };
        if (np != p)
        {
            _suppressNextAutoFit = true;
            _history[idx] = new HistoryEntry(_history[idx].Label, RlbDetails(np), np);
            HistoryList.SelectedItem = _history[idx];
        }
        RlbAddEinmittenVerm(idx, np, inX);
        DrawSkia?.InvalidateVisual();
    }

    // Masslinien Kante → 1. Loch und letztes Loch → gegenüberliegende Kante
    // (X: links/rechts, Y: unten/oben); ersetzt bestehende Kantenabstände dieser Lochreihe in der Achse
    private void RlbAddEinmittenVerm(int idx, ReihenlochbohrungParams p, bool inX)
    {
        var (sx, sy) = RlbStart(p);
        int ankerEnde = inX
            ? (p.CountX >= 2 ? 2 : 0)
            : (p.CountY >= 3 ? 3 : p.CountY == 2 ? 1 : 0);
        int edgeAnf = inX ? 1 : 3, edgeEnd = inX ? 2 : 4;
        _vermPlaced.RemoveAll(en => en.Kind == VermKind.KreisKantenDist && (en.Edge == edgeAnf || en.Edge == edgeEnd)
                                    && DecodeKreisVermIdx(en.P2Idx).histIdx == idx);
        double anf  = inX ? sx : sy;
        double rest = inX ? WorkX - (sx + (p.CountX - 1) * p.SpacingX)
                          : WorkY - (sy + (p.CountY - 1) * p.SpacingY);
        _vermPlaced.Add(new VermEntry(VermKind.KreisKantenDist, -1, idx, 0, Math.Round(anf, 3), Edge: edgeAnf));
        _vermPlaced.Add(new VermEntry(VermKind.KreisKantenDist, -1, idx + ankerEnde * RlbAnkerOffset, 0,
                                      Math.Round(rest, 3), Edge: edgeEnd));
    }

    // Kantenabstand mit Gegenstück am selben Objekt (gegenüberliegende Kante) und genau gleichem Wert?
    private bool KreisKantenDistSymmetrisch(VermEntry en)
    {
        if (en.Kind != VermKind.KreisKantenDist || en.Edge <= 0) return false;
        int gegen = en.Edge switch { 1 => 2, 2 => 1, 3 => 4, _ => 3 };
        int h = DecodeKreisVermIdx(en.P2Idx).histIdx;
        var val = KreisVermActual(en); if (val == null) return false;
        foreach (var o in _vermPlaced)
        {
            if (o.Kind != VermKind.KreisKantenDist || o.Edge != gegen
                || DecodeKreisVermIdx(o.P2Idx).histIdx != h) continue;
            if (KreisVermActual(o) is { } ov && Math.Abs(Math.Round(ov, 3) - Math.Round(val.Value, 3)) < 0.0005)
                return true;
        }
        return false;
    }

    private void OnRlbEigLostFocus(object sender, RoutedEventArgs e) => ApplyReihenlochEig();
    private void OnRlbEigChanged(object sender, RoutedEventArgs e)   => ApplyReihenlochEig();
    private void OnRlbEigKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyReihenlochEig(); }

    private void DrawReihenlochOverlay(SKCanvas canvas)
    {
        if (WorkX <= 0 || WorkY <= 0) return;
        (float x, float y) Px(double x, double y) => ((float)x, (float)(WorkY - y));

        float lt = (float)(1.0 / _zoom);
        using var holePaint = new SKPaint { Color = new SKColor(200, 70, 0, 220),
            Style = SKPaintStyle.Stroke, StrokeWidth = lt * 1.5f, IsAntialias = true };
        using var holeFill  = new SKPaint { Color = new SKColor(255, 140, 0, 70),
            Style = SKPaintStyle.Fill, IsAntialias = true };
        using var otherPaint = new SKPaint { Color = new SKColor(200, 70, 0, 110),
            Style = SKPaintStyle.Stroke, StrokeWidth = lt, IsAntialias = true };
        using var handlePaint = new SKPaint { Color = new SKColor(20, 100, 200),
            Style = SKPaintStyle.Fill, IsAntialias = true };
        using var handleLine  = new SKPaint { Color = new SKColor(20, 100, 200),
            Style = SKPaintStyle.Stroke, StrokeWidth = lt * 2, IsAntialias = true };
        using var ankerRand   = new SKPaint { Color = SKColors.White,
            Style = SKPaintStyle.Stroke, StrokeWidth = lt, IsAntialias = true };
        using var textPaint = new SKPaint { Color = new SKColor(20, 100, 200), IsAntialias = true,
            TextSize = (float)(11.0 / _zoom), Typeface = SKTypeface.FromFamilyName("Arial") };
        using var bgPaint   = new SKPaint { Color = new SKColor(255, 255, 255, 200), Style = SKPaintStyle.Fill };

        // Übrige Lochreihen: nur Kontur (anklickbar zum Anwählen)
        for (int i = 0; i < _history.Count; i++)
        {
            if (i == _rlbIdx || _history[i].Params is not ReihenlochbohrungParams op) continue;
            var (ox, oy) = RlbStart(op);
            for (int iy = 0; iy < op.CountY; iy++)
            for (int ix = 0; ix < op.CountX; ix++)
            {
                var (cx, cy) = Px(ox + ix * op.SpacingX, oy + iy * op.SpacingY);
                canvas.DrawCircle(cx, cy, (float)(op.LochD / 2), otherPaint);
            }
        }

        // Am Raster gefangener Setzpunkt für eine neue Lochreihe
        if (_rlbHoverMm is { } hv)
        {
            var (dx, dy) = Px(hv.x, hv.y);
            using var dotPaint = new SKPaint { Color = new SKColor(255, 140, 0),
                Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle(dx, dy, (float)(4.0 / _zoom), dotPaint);
            canvas.DrawCircle(dx, dy, (float)(4.0 / _zoom), ankerRand);
        }

        var p = RlbActive(); if (p == null) return;
        var (sx, sy) = RlbStart(p);
        float rad = (float)(p.LochD / 2);
        for (int iy = 0; iy < p.CountY; iy++)
        for (int ix = 0; ix < p.CountX; ix++)
        {
            var (cx, cy) = Px(sx + ix * p.SpacingX, sy + iy * p.SpacingY);
            canvas.DrawCircle(cx, cy, rad, holeFill);
            canvas.DrawCircle(cx, cy, rad, holePaint);
        }

        void DrawLabel(float x, float y, string text)
        {
            float tw = textPaint.MeasureText(text);
            float fh = textPaint.TextSize;
            canvas.DrawRect(x - 3 * lt, y - fh - 2 * lt, tw + 6 * lt, fh + 6 * lt, bgPaint);
            canvas.DrawText(text, x, y, textPaint);
        }

        // Pfeil von a nach b, Spitze bei b
        void DrawArrow((double x, double y) a, (double x, double y) b)
        {
            var (ax, ay) = Px(a.x, a.y);
            var (bx, by) = Px(b.x, b.y);
            float dx = bx - ax, dy = by - ay;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) return;
            float ux = dx / len, uy = dy / len;
            float hl = (float)(9.0 / _zoom), hw = (float)(5.0 / _zoom);
            canvas.DrawLine(ax, ay, bx - ux * hl, by - uy * hl, handleLine);
            using var head = new SKPath();
            head.MoveTo(bx, by);
            head.LineTo(bx - ux * hl - uy * hw, by - uy * hl + ux * hw);
            head.LineTo(bx - ux * hl + uy * hw, by - uy * hl - ux * hw);
            head.Close();
            canvas.DrawPath(head, handlePaint);
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double lastX = sx + (p.CountX - 1) * p.SpacingX;
        double lastY = sy + (p.CountY - 1) * p.SpacingY;
        double tipX  = _rlbDrag == 2 ? _rlbArrowTip : RlbArrowTipX(p).x;
        double tipY  = _rlbDrag == 3 ? _rlbArrowTip : RlbArrowTipY(p).y;
        DrawArrow((lastX + p.LochD / 2, sy), (Math.Max(tipX, lastX + p.LochD / 2), sy));
        DrawArrow((sx, lastY + p.LochD / 2), (sx, Math.Max(tipY, lastY + p.LochD / 2)));

        var (lx, ly) = Px(tipX, sy);
        DrawLabel(lx + (float)(6.0 / _zoom), ly + (float)(4.0 / _zoom),
            $"{p.CountX} × {p.SpacingX.ToString(inv)}");
        var (vx, vy) = Px(sx, tipY);
        DrawLabel(vx + (float)(6.0 / _zoom), vy,
            $"{p.CountY} × {p.SpacingY.ToString(inv)}");

        // Anker (1. Loch unten links)
        var (hx, hy) = Px(sx, sy);
        float hs = (float)(5.0 / _zoom);
        canvas.DrawRect(hx - hs, hy - hs, 2 * hs, 2 * hs, handlePaint);
        canvas.DrawRect(hx - hs, hy - hs, 2 * hs, 2 * hs, ankerRand);
        if (_rlbDrag == 1)
            DrawLabel(hx + (float)(8.0 / _zoom), hy + (float)(16.0 / _zoom),
                $"X={Math.Round(p.StartX, 2).ToString(inv)}  Y={Math.Round(p.StartY, 2).ToString(inv)}");

        // Abstand-Y-Anker (2. Loch in Y-Richtung): Raute; beim Ziehen immer sichtbar
        if (p.CountY >= 2 || _rlbDrag == 4)
        {
            var (ax, ay) = Px(sx, sy + p.SpacingY);
            float ds = (float)(6.0 / _zoom);
            using var raute = new SKPath();
            raute.MoveTo(ax, ay - ds); raute.LineTo(ax + ds, ay);
            raute.LineTo(ax, ay + ds); raute.LineTo(ax - ds, ay);
            raute.Close();
            using var abstandPaint = new SKPaint { Color = new SKColor(30, 150, 60),
                Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawPath(raute, abstandPaint);
            canvas.DrawPath(raute, ankerRand);
            if (_rlbDrag == 4)
                DrawLabel(ax - (float)(90.0 / _zoom), ay + (float)(4.0 / _zoom),
                    $"Abstand Y={p.SpacingY.ToString(inv)}");
        }

        // End-Anker (letztes Loch in X bzw. Y): violetter Kreis mit Punkt
        using var endPaint = new SKPaint { Color = new SKColor(140, 60, 190),
            Style = SKPaintStyle.Fill, IsAntialias = true };
        float er = (float)(5.5 / _zoom);
        foreach (var (anker, drag) in new[] { (2, 5), (3, 6) })
        {
            if (RlbAnkerPos(p, anker) is not { } ep) continue;
            var (ex, ey) = Px(ep.x, ep.y);
            canvas.DrawCircle(ex, ey, er, endPaint);
            canvas.DrawCircle(ex, ey, er, ankerRand);
            canvas.DrawCircle(ex, ey, er * 0.3f, ankerRand);
            if (_rlbDrag == drag)
                DrawLabel(ex + (float)(8.0 / _zoom), ey - (float)(10.0 / _zoom),
                    drag == 5 ? $"Abstand X={p.SpacingX.ToString(inv)}"
                              : $"Abstand Y={p.SpacingY.ToString(inv)}");
        }
    }

    private void ApplyKreisEig()
    {
        if (_eigSuppressUpdate) return;
        int idx = HistoryList.SelectedIndex;
        if (idx < 0 || _history[idx].Params is not KreisParams kr) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        string Norm(string s) => s.Replace(',', '.');

        if (!double.TryParse(Norm(KrEigX.Text),      sty, inv, out var x))  return;
        if (!double.TryParse(Norm(KrEigY.Text),      sty, inv, out var y))  return;
        if (!double.TryParse(Norm(KrEigRadius.Text), sty, inv, out var rad)) return;
        if (!double.TryParse(Norm(KrEigZ.Text),      sty, inv, out var z))  return;
        bool mehrfach = KrEigMehrfach.IsChecked == true;
        double.TryParse(Norm(KrEigZZust.Text), sty, inv, out var zzust);

        string fraesung = (KrEigFrAussen.IsChecked == true) ? "Aussen"
                        : (KrEigFrInnen.IsChecked  == true) ? "Innen"
                        : "Mittig";
        string lauf = (KrEigGleich.IsChecked == true) ? "Gleichlauf" : "Gegenlauf";
        bool isTasche = KrModusTasche.IsChecked == true;
        string bezug  = KrBezugName();

        // Recalculate XRel/YRel if bezugspunkt changed
        var (oldAbsX, oldAbsY) = GCodeGenerator.ConvertBezugspunkt(kr.Bezugspunkt, kr.XRel, kr.YRel, WorkX, WorkY);
        var (newRelX, newRelY) = kr.Bezugspunkt != bezug
            ? AbsToRel(bezug, oldAbsX, oldAbsY, WorkX, WorkY)
            : (x, y);

        var np = kr with
        {
            XRel = Math.Round(newRelX, 3), YRel = Math.Round(newRelY, 3),
            Radius = rad, ZTiefe = z,
            Fraesung            = fraesung,
            Laufrichtung        = lauf,
            MehrfachZustellung  = mehrfach,
            ZZustellung         = zzust > 0 ? zzust : kr.ZZustellung,
            Bezugspunkt         = bezug,
            IsTasche            = isTasche,
        };

        _eigSuppressUpdate = true;
        _suppressHistoryRegen = true;
        try { _history[idx] = new HistoryEntry("Kreis",
            $"M={np.XRel}/{np.YRel} R={np.Radius} Z={np.ZTiefe}", np); }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = idx;
        UpdateKrModusVisibility(isTasche);
    }

    private void OnKrEigChanged(object sender, RoutedEventArgs e)   => ApplyKreisEig();
    private void OnKrEigLostFocus(object sender, RoutedEventArgs e) => ApplyKreisEig();
    private void OnKrEigKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyKreisEig(); }
    private void OnPfadEigKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPfadStartEig(); }

    private void AddNEck(double cxMm, double cyMm, double radiusMm, int eckenAnzahl = 6)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        var wz = _aktivesWerkzeug;
        string bezug = "Mitte";
        var (xRel, yRel) = AbsToRel(bezug, cxMm, cyMm, WorkX, WorkY);
        var p = new NEckParams(
            XRel:        Math.Round(xRel, 3),
            YRel:        Math.Round(yRel, 3),
            Ecken:       eckenAnzahl,
            Radius:      Math.Round(radiusMm, 3),
            RotationGrad:0,
            ZTiefe:      wz != null ? -(Math.Abs(WorkZ)) : -5,
            FraeserD:    wz?.Durchmesser ?? 6,
            Drehzahl:    wz?.Drehzahl    ?? 18000,
            Vorschub:    wz?.VorschubFxy ?? 3000,
            VorschubFz:  wz?.VorschubFz  ?? 500,
            Fraesung:    "Aussen",
            Laufrichtung:"Gegenlauf",
            Radiuskorrektur: "Keine",
            WerkzeugNr:  wz?.Nr ?? 0,
            Eintauchwinkel:    wz?.Eintauchwinkel ?? 3,
            MehrfachZustellung: wz != null,
            ZZustellung:       wz?.ZZustellung ?? 2,
            Bezugspunkt:       bezug,
            IsTasche:          false
        );
        _suppressNextAutoFit = true;
        _history.Add(new HistoryEntry($"{eckenAnzahl}-Eck",
            $"M={p.XRel}/{p.YRel} R={p.Radius} Z={p.ZTiefe}", p));
        HistoryList.SelectedItem    = _history[^1];
        TabEigenschaften.IsSelected = true;
    }

    private void OnNEckTool(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.NEck ? CanvasTool.Select : CanvasTool.NEck);

    private void ApplyNEckEig()
    {
        if (_eigSuppressUpdate) return;
        int idx = HistoryList.SelectedIndex;
        if (idx < 0 || _history[idx].Params is not NEckParams ne) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        string Norm(string s) => s.Replace(',', '.');

        if (!double.TryParse(Norm(NEckEigX.Text),      sty, inv, out var x))  return;
        if (!double.TryParse(Norm(NEckEigY.Text),      sty, inv, out var y))  return;
        if (!double.TryParse(Norm(NEckEigRadius.Text), sty, inv, out var rad)) return;
        if (!double.TryParse(Norm(NEckEigZ.Text),      sty, inv, out var z))  return;
        if (!int.TryParse(NEckEigEcken.Text,           out var ecken)) ecken = ne.Ecken;
        if (!double.TryParse(Norm(NEckEigRotation.Text), sty, inv, out var rot)) rot = ne.RotationGrad;
        bool mehrfach = NEckEigMehrfach.IsChecked == true;
        double.TryParse(Norm(NEckEigZZust.Text), sty, inv, out var zzust);

        string fraesung = (NEckEigFrAussen.IsChecked == true) ? "Aussen"
                        : (NEckEigFrInnen.IsChecked  == true) ? "Innen"
                        : "Mittig";
        string lauf = (NEckEigGleich.IsChecked == true) ? "Gleichlauf" : "Gegenlauf";
        string radkorr = (NEckEigRadKorInnen.IsChecked == true) ? "Innen"
                       : (NEckEigRadKorAussen.IsChecked == true) ? "Aussen"
                       : "Keine";

        var (oldAbsX, oldAbsY) = GCodeGenerator.ConvertBezugspunkt(ne.Bezugspunkt, ne.XRel, ne.YRel, WorkX, WorkY);
        var (newRelX, newRelY) = (x, y);

        var np = ne with
        {
            XRel = Math.Round(newRelX, 3), YRel = Math.Round(newRelY, 3),
            Ecken = ecken,
            RotationGrad = rot,
            Radius = rad, ZTiefe = z,
            Fraesung            = fraesung,
            Laufrichtung        = lauf,
            Radiuskorrektur     = radkorr,
            MehrfachZustellung  = mehrfach,
            ZZustellung         = zzust > 0 ? zzust : ne.ZZustellung,
        };

        _eigSuppressUpdate = true;
        _suppressHistoryRegen = true;
        try { _history[idx] = new HistoryEntry($"{np.Ecken}-Eck",
            $"M={np.XRel}/{np.YRel} R={np.Radius} Ø={np.RotationGrad}° Z={np.ZTiefe}", np); }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = idx;
    }

    private void OnNEckEigChanged(object sender, RoutedEventArgs e)   => ApplyNEckEig();
    private void OnNEckEigLostFocus(object sender, RoutedEventArgs e) => ApplyNEckEig();
    private void OnNEckEigKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyNEckEig(); }

    private void AddPfadLinie(double mmX, double mmY)
    {
        double xRel = Math.Round(mmX, 3);
        double yRel = Math.Round(mmY, 3);
        _suppressNextAutoFit = true;
        var p = new PfadPunktParams(
            XRel: xRel, YRel: yRel,
            ZTiefe: 0, ZZustellung: 0, FraeserD: 0, Drehzahl: 0,
            Vorschub: 0, VorschubFz: 0,
            Radiuskorrektur: "Mittig",
            Bezugspunkt: "Unten links",
            Typ: PfadPunktTyp.Linie
        );
        p = (PfadPunktParams)AdjustParamsToNullpunkt(p);
        _history.Add(new HistoryEntry($"Pfad Linie #{PfadPunktNummer(_history.Count)}",
            $"X={p.XRel} Y={p.YRel}", p, level: 1));
        AutoDetectGeomConstraints(_history.Count - 1);
        HistoryList.SelectedItem    = _history[^1];
    }

    private void AddPfadSpline(double mmX, double mmY)
    {
        _splinePointsBeingCreated.Add((mmX, mmY));
        DrawSkia?.InvalidateVisual();
    }

    private void OnSplineFinish(object sender, RoutedEventArgs e)
    {
        if (_splinePointsBeingCreated.Count < 2)
        {
            MessageBox.Show("Mindestens 2 Spline-Punkte erforderlich.", "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(PfadEigSplineSegmentLen.Text, NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var segmentLen)
            || segmentLen <= 0)
        {
            MessageBox.Show("Ungültige Segmentlänge.", "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FinalizePfadSpline(segmentLen);
    }

    private void FinalizePfadSpline(double segmentLength = 0.5)
    {
        if (_splinePointsBeingCreated.Count < 2)
        {
            MessageBox.Show("Mindestens 2 Punkte erforderlich.", "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _suppressNextAutoFit = true;

        // Speichere nur die geklickten Punkte als Spline (nicht die interpolierten)
        foreach (var pt in _splinePointsBeingCreated)
        {
            double xRel = Math.Round(pt.x, 3);
            double yRel = Math.Round(pt.y, 3);

            var p = new PfadPunktParams(
                XRel: xRel, YRel: yRel,
                ZTiefe: 0, ZZustellung: 0, FraeserD: 0, Drehzahl: 0,
                Vorschub: 0, VorschubFz: 0,
                Radiuskorrektur: "Mittig",
                Bezugspunkt: "Unten links",
                Typ: PfadPunktTyp.Spline,
                SplineModus: _splineModeBeingCreated,
                SplineTension: _splineTensionBeingCreated,
                SplineSegmentLength: segmentLength
            );
            p = (PfadPunktParams)AdjustParamsToNullpunkt(p);
            _history.Add(new HistoryEntry($"Pfad Spline #{PfadPunktNummer(_history.Count)}",
                $"X={p.XRel} Y={p.YRel}", p, level: 1));
        }

        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = _history.Count - 1;

        // Orange Linie bleibt sichtbar als feste Linie (nicht mehr als Vorschau)
        _splineFinalized = true;

        // Benutzer kann mit Linien/Bögen/Splines weitermachen
        SetActiveTool(CanvasTool.PfadLinie);
        DrawSkia?.InvalidateVisual();
    }

    private List<(double x, double y)> InterpolateFullSpline(List<(double x, double y)> pts,
                                                               string splineMode, double tension,
                                                               double segmentLength = 0.5)
    {
        if (pts.Count < 2) return new();
        if (pts.Count == 2)
        {
            // Mit 2 Punkten: Auch Catmull-Rom mit wiederholten Kontrollpunkten (sanfte Kurve)
            // Behandle wie 3 Punkte mit Cursor als 3ter Punkt
            var temp3 = new List<(double x, double y)> { pts[0], pts[1], pts[1] };
            return InterpolateFullSpline(temp3, splineMode, tension, segmentLength);
        }
        if (pts.Count == 3)
        {
            // Mit 3 Punkten: Catmull-Rom Kurve mit maximaler Krümmung (tension=0)
            var result = new List<(double x, double y)>();
            double curveTension = 0.0;  // Maximale Krümmung für 3 Punkte

            // Segment 1: Start -> Spline1
            var p0 = pts[0];  // Wiederhole Start als Kontrollpunkt
            var p1 = pts[0];
            var p2 = pts[1];
            var p3 = pts[2];

            double dist1 = Math.Sqrt(Math.Pow(p2.x - p1.x, 2) + Math.Pow(p2.y - p1.y, 2));
            int steps1 = Math.Max(15, (int)Math.Ceiling(dist1 / segmentLength));

            result.Add(p1);
            for (int s = 1; s <= steps1; s++)
            {
                double t = (double)s / steps1;
                var pt = CatmullRomPointPreview(p0, p1, p2, p3, t, curveTension);
                result.Add(pt);
            }

            // Segment 2: Spline1 -> Cursor
            p0 = pts[0];
            p1 = pts[1];
            p2 = pts[2];
            p3 = pts[2];  // Wiederhole Cursor als Kontrollpunkt

            double dist2 = Math.Sqrt(Math.Pow(p2.x - p1.x, 2) + Math.Pow(p2.y - p1.y, 2));
            int steps2 = Math.Max(15, (int)Math.Ceiling(dist2 / segmentLength));

            for (int s = 1; s <= steps2; s++)
            {
                double t = (double)s / steps2;
                var pt = CatmullRomPointPreview(p0, p1, p2, p3, t, curveTension);
                result.Add(pt);
            }

            return result;
        }

        // 4+ Punkte: Catmull-Rom Spline mit maximaler Krümmung
        var fullResult = new List<(double x, double y)> { pts[0] };
        int n = pts.Count;
        double maxTension = 0.0;  // Maximale Krümmung

        for (int i = 0; i < n - 1; i++)
        {
            var p1 = pts[i];
            var p2 = pts[i + 1];
            // Für erste Segment: p0 = p1 wiederholt (keine Extrapolation)
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            // Für letzte Segment: p3 = p2 wiederholt (keine Extrapolation)
            var p3 = i < n - 2 ? pts[i + 2] : pts[i + 1];

            double dx = p2.x - p1.x;
            double dy = p2.y - p1.y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(10, (int)Math.Ceiling(dist / segmentLength));

            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                var pt = CatmullRomPointPreview(p0, p1, p2, p3, t, maxTension);
                fullResult.Add(pt);
            }
        }

        return fullResult;
    }

    private static (double x, double y) CatmullRomPointPreview(
        (double x, double y) p0, (double x, double y) p1,
        (double x, double y) p2, (double x, double y) p3,
        double t, double tension = 0.5)
    {
        // Standard Catmull-Rom Spline Formel
        // Geht durch p1 bei t=0 und durch p2 bei t=1
        double t2 = t * t;
        double t3 = t2 * t;
        double m = 1.0 - tension;
        double c = m / 2.0;

        // Kurve = c * [ a*t³ + b*t² + d*t + e ]
        double a = -p0.x + 3*p1.x - 3*p2.x + p3.x;
        double b = 2*p0.x - 5*p1.x + 4*p2.x - p3.x;
        double d = -p0.x + p2.x;
        double e = 2*p1.x;

        double x = c * (a * t3 + b * t2 + d * t + e);

        a = -p0.y + 3*p1.y - 3*p2.y + p3.y;
        b = 2*p0.y - 5*p1.y + 4*p2.y - p3.y;
        d = -p0.y + p2.y;
        e = 2*p1.y;

        double y = c * (a * t3 + b * t2 + d * t + e);

        return (x, y);
    }

    // Erkennt geometrische Eigenschaften automatisch nach dem Setzen eines Linienpunkts:
    //   • Exakt horizontales Segment  → ParallelEdge zur unteren Kante (= wagerecht)
    //   • Exakt vertikales Segment    → ParallelEdge zur linken Kante  (= senkrecht)
    //   • Exakt rechtwinkliger Eckpunkt zwischen zwei geraden Linien → Perpendicular-Constraint
    private void AutoDetectGeomConstraints(int newPtIdx)
    {
        if (_history[newPtIdx].Params is not PfadPunktParams { Typ: PfadPunktTyp.Linie }) return;
        if (newPtIdx < 1) return;
        if (_history[newPtIdx - 1].Params is not PfadPunktParams) return;

        var abs2 = GetPfadAbsAt(newPtIdx);     if (abs2 == null) return;
        var abs1 = GetPfadAbsAt(newPtIdx - 1); if (abs1 == null) return;

        double dx1 = abs2.Value.x - abs1.Value.x;
        double dy1 = abs2.Value.y - abs1.Value.y;
        double L1  = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
        if (L1 < 1e-9) return;

        const double eps = 1e-6;

        // ── Horizontales / vertikales Segment ──────────────────────────────────────
        // Prüfen ob bereits eine Richtungs-Constraint auf diesem Segment existiert
        bool hasDirConstraint = _vermPlaced.Any(en =>
            en.Kind is VermKind.EdgeAngle or VermKind.ParallelEdge or VermKind.PerpendicularEdge
            && ((en.P1Idx == newPtIdx - 1 && en.P2Idx == newPtIdx)
             || (en.P1Idx == newPtIdx     && en.P2Idx == newPtIdx - 1)));

        if (!hasDirConstraint)
        {
            if (Math.Abs(dy1) < eps)
            {
                // Horizontal → ParallelEdge zur unteren (waagerechten) Kante
                // Zeigt als Symbol, nicht als Masslinie
                _vermPlaced.Add(new VermEntry(
                    VermKind.ParallelEdge, newPtIdx - 1, newPtIdx, 0, 0.0, -1, -1, 3));
            }
            else if (Math.Abs(dx1) < eps)
            {
                // Vertikal → ParallelEdge zur linken (senkrechten) Kante
                _vermPlaced.Add(new VermEntry(
                    VermKind.ParallelEdge, newPtIdx - 1, newPtIdx, 0, 0.0, -1, -1, 1));
            }
        }

        if (false) // Perpendicular-Auto-Erkennung deaktiviert
        {
            if (false)
            {
            }
        }
    }

    private void AddPfadBogen((double x, double y) endAbs, (double x, double y) midAbs)
    {
        _suppressNextAutoFit = true;
        var last = GetLastPfadAbsPoint();
        double xRel = Math.Round(endAbs.x, 3);
        double yRel = Math.Round(endAbs.y, 3);

        // Pfeilhöhe aus den zwei Canvas-Klicks berechnen (vorzeichenbehaftet: + = links)
        double pfeilhoehe = 0;
        if (last.HasValue)
        {
            double dx = endAbs.x - last.Value.x, dy = endAbs.y - last.Value.y;
            double L  = Math.Sqrt(dx * dx + dy * dy);
            if (L > 1e-10)
            {
                double perpX = -dy / L, perpY = dx / L;
                double mcx   = (last.Value.x + endAbs.x) / 2;
                double mcy   = (last.Value.y + endAbs.y) / 2;
                pfeilhoehe = Math.Round((midAbs.x - mcx) * perpX + (midAbs.y - mcy) * perpY, 3);
            }
        }

        var p = new PfadPunktParams(
            XRel: xRel, YRel: yRel,
            ZTiefe: 0, ZZustellung: 0, FraeserD: 0, Drehzahl: 0,
            Vorschub: 0, VorschubFz: 0,
            Radiuskorrektur: "Mittig",
            Bezugspunkt: "Unten links",
            Typ: PfadPunktTyp.Bogen,
            XMid: pfeilhoehe, YMid: 0,
            BogenModus: "Pfeilhöhe"
        );
        p = (PfadPunktParams)AdjustParamsToNullpunkt(p);
        _history.Add(new HistoryEntry($"Pfad Bogen #{PfadPunktNummer(_history.Count)}",
            $"X={p.XRel} Y={p.YRel}, Pfeilhöhe={p.XMid}", p, level: 1));
        HistoryList.SelectedItem    = _history[^1];
        TabEigenschaften.IsSelected = true;
    }

    // ── Pfad-Punkt Hilfsmethoden ──────────────────────────────────

    private int PfadPunktNummer(int histIdx)
    {
        int n = 0;
        for (int i = histIdx - 1; i >= 0; i--)
        {
            if (_history[i].Params is not PfadPunktParams pp) break;
            if (pp.Typ == PfadPunktTyp.Start) break;
            n++;
        }
        return n + 1;
    }

    // Liefert die Radiuskorrektur ("Links"/"Rechts"/"Mittig") der Pfad-Kette, zu der ein
    // Segment gehört — bestimmt, auf welcher Seite der Konturlinie die tatsächliche
    // Fräsbahn (Werkzeugradius-Versatz) verläuft.
    private string GetRadiuskorrekturForSeg(int p1Idx, int p2Idx)
    {
        int idx = Math.Max(p1Idx, p2Idx);
        if (idx < 0 || idx >= _history.Count || _history[idx].Params is not PfadPunktParams) return "Mittig";
        int startIdx = idx;
        while (startIdx > 0 && _history[startIdx].Params is PfadPunktParams pp && pp.Typ != PfadPunktTyp.Start)
            startIdx--;
        return _history[startIdx].Params is PfadPunktParams sp ? sp.Radiuskorrektur : "Mittig";
    }

    // Absolute Position eines Pfad-History-Eintrags berechnen
    private (double x, double y)? GetPfadAbsAt(int histIdx)
    {
        if (histIdx < 0 || histIdx >= _history.Count) return null;
        if (_history[histIdx].Params is not PfadPunktParams) return null;

        // Kette ab dem zugehörigen Startpunkt aufbauen
        int startIdx = histIdx;
        while (startIdx > 0 && _history[startIdx].Params is PfadPunktParams pp && pp.Typ != PfadPunktTyp.Start)
            startIdx--;

        double w = WorkX, h = WorkY;
        (double x, double y) abs = (0, 0);
        for (int i = startIdx; i <= histIdx; i++)
        {
            if (_history[i].Params is not PfadPunktParams p) break;
            if (i > startIdx && p.Bezugspunkt == "Letzter Punkt")
                abs = (abs.x + p.XRel, abs.y + p.YRel);
            else
                abs = GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, w, h);
        }
        return abs;
    }

    // Treffertest: Irgendein Punkt einer Pfad-Kette. Gibt Start-History-Idx der Kette zurück.
    // Ankerpositionen einer Chain-BBox in mm (Index 0-7, Reihenfolge: TL TM TR LM RM BL BM BR)
    // In mm-Koordinaten: "oben" = große Y-Werte, "links" = kleine X-Werte
    private (double x, double y)[] AnchorPosMm((double minX, double minY, double maxX, double maxY) bbox)
    {
        double padMm = 4.0 / _zoom;
        double L = bbox.minX - padMm, R = bbox.maxX + padMm;
        double B = bbox.minY - padMm, T = bbox.maxY + padMm;
        double mH = (L + R) / 2, mV = (B + T) / 2;
        return new[] {
            (L, T), (mH, T), (R, T),
            (L, mV),          (R, mV),
            (L, B), (mH, B), (R, B)
        };
    }

    private static Cursor ScaleAnchorCursor(int anchor) => anchor switch
    {
        0 or 7 => Cursors.SizeNWSE,
        2 or 5 => Cursors.SizeNESW,
        1 or 6 => Cursors.SizeNS,
        _      => Cursors.SizeWE
    };

    // Trifft Cursor einen der 8 Ankerpunkte irgendeiner Kette? → (chainStart, anchor) oder (-1,-1)
    private (int chainIdx, int anchor) HitTestPfadChainAnchor(double mmX, double mmY)
    {
        double tol = 6.0 / _zoom;
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp || pp.Typ != PfadPunktTyp.Start) continue;
            var bboxOpt = GetChainBBox(i);
            if (bboxOpt == null) continue;
            var pts = AnchorPosMm(bboxOpt.Value);
            for (int a = 0; a < 8; a++)
            {
                double dx = mmX - pts[a].x, dy = mmY - pts[a].y;
                if (dx*dx + dy*dy <= tol*tol) return (i, a);
            }
        }
        return (-1, -1);
    }

    private void StartScalePfadChain(int chainIdx, int anchor)
    {
        var bboxOpt = GetChainBBox(chainIdx);
        if (bboxOpt == null) return;
        _pfadScaleChainIdx = chainIdx;
        _pfadScaleAnchor   = anchor;
        _pfadScaleOrigBBox = bboxOpt.Value;
        var pts = AnchorPosMm(bboxOpt.Value);
        _pfadScaleOriginMm = pts[7 - anchor]; // gegenüberliegender Ankerpunkt ist fixiert
        _pfadScaleOrigAbs.Clear();
        for (int i = chainIdx; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp) break;
            if (i > chainIdx && pp.Typ == PfadPunktTyp.Start) break;
            _pfadScaleOrigAbs.Add(GetPfadAbsAt(i) ?? (0, 0));
        }
        HistoryList.SelectedItem    = _history[chainIdx];
        TabEigenschaften.IsSelected = true;
    }

    private void UpdateScalePfadChain(double mmX, double mmY)
    {
        if (_pfadScaleChainIdx < 0) return;
        var anchorPts  = AnchorPosMm(_pfadScaleOrigBBox);
        int  anchor    = _pfadScaleAnchor;
        bool doX       = anchor != 1 && anchor != 6; // linke/rechte Anker skalieren X
        bool doY       = anchor != 3 && anchor != 4; // obere/untere Anker skalieren Y
        double ox = _pfadScaleOriginMm.x, oy = _pfadScaleOriginMm.y;
        double origAX  = anchorPts[anchor].x, origAY = anchorPts[anchor].y;

        double scaleX  = (doX && Math.Abs(origAX - ox) > 1e-6) ? (mmX - ox) / (origAX - ox) : 1.0;
        double scaleY  = (doY && Math.Abs(origAY - oy) > 1e-6) ? (mmY - oy) / (origAY - oy) : 1.0;
        scaleX = Math.Max(0.05, scaleX);
        scaleY = Math.Max(0.05, scaleY);

        _suppressHistoryRegen = true;
        try
        {
            int local = 0;
            for (int i = _pfadScaleChainIdx; i < _history.Count && local < _pfadScaleOrigAbs.Count; i++, local++)
            {
                if (_history[i].Params is not PfadPunktParams p) break;
                var (oax, oay) = _pfadScaleOrigAbs[local];
                double newAbsX = ox + (oax - ox) * scaleX;
                double newAbsY = oy + (oay - oy) * scaleY;
                double xRel, yRel;
                if (p.Bezugspunkt == "Letzter Punkt" && local > 0)
                {
                    var (prevOax, prevOay) = _pfadScaleOrigAbs[local - 1];
                    xRel = Math.Round(newAbsX - (ox + (prevOax - ox) * scaleX), 3);
                    yRel = Math.Round(newAbsY - (oy + (prevOay - oy) * scaleY), 3);
                }
                else
                {
                    (xRel, yRel) = InverseBezugspunkt(p.Bezugspunkt, newAbsX, newAbsY, WorkX, WorkY);
                    xRel = Math.Round(xRel, 3); yRel = Math.Round(yRel, 3);
                }

                double xMid = p.XMid, yMid = p.YMid;
                if (p.Typ == PfadPunktTyp.Bogen && p.BogenModus == "Bogenmitte" && local > 0)
                {
                    // Bogenmittelpunkt absolut skalieren
                    var (prevOax, prevOay) = _pfadScaleOrigAbs[local - 1];
                    if (p.Bezugspunkt == "Letzter Punkt")
                    {
                        double absMidX = prevOax + p.XMid, absMidY = prevOay + p.YMid;
                        double prevNewX = ox + (prevOax - ox) * scaleX, prevNewY = oy + (prevOay - oy) * scaleY;
                        xMid = Math.Round(ox + (absMidX - ox) * scaleX - prevNewX, 3);
                        yMid = Math.Round(oy + (absMidY - oy) * scaleY - prevNewY, 3);
                    }
                }
                else if (p.Typ == PfadPunktTyp.Bogen && (p.BogenModus == "Pfeilhöhe" || p.BogenModus == "Radius")
                         && local > 0)
                {
                    // Sehne des ursprünglichen Bogens
                    var (prevOax, prevOay) = _pfadScaleOrigAbs[local - 1];
                    double cdx = oax - prevOax, cdy = oay - prevOay;
                    double L  = Math.Sqrt(cdx * cdx + cdy * cdy);
                    double Lp = Math.Sqrt((cdx * scaleX) * (cdx * scaleX) + (cdy * scaleY) * (cdy * scaleY));
                    if (L > 1e-10 && Lp > 1e-10)
                    {
                        if (p.BogenModus == "Pfeilhöhe")
                        {
                            // h' = h · scaleX · scaleY · L / L'
                            xMid = Math.Round(p.XMid * scaleX * scaleY * L / Lp, 3);
                        }
                        else // "Radius"
                        {
                            // Pfeilhöhe aus Radius + Sehne berechnen, skalieren, zurück in Radius
                            double R = p.XMid;
                            double a = L / 2;
                            double absR = Math.Max(Math.Abs(R), a);
                            double h = (absR - Math.Sqrt(Math.Max(0, absR * absR - a * a))) * Math.Sign(R != 0 ? R : 1);
                            double hp = h * scaleX * scaleY * L / Lp;
                            double ap = Lp / 2;
                            double Rp = (ap * ap + hp * hp) / (2 * Math.Max(Math.Abs(hp), 1e-10)) * Math.Sign(hp);
                            xMid = Math.Round(Rp, 3);
                        }
                    }
                }

                _history[i] = new HistoryEntry(_history[i].Label, _history[i].Details,
                    p with { XRel = xRel, YRel = yRel, XMid = xMid, YMid = yMid }, _history[i].Level);
            }
        }
        finally { _suppressHistoryRegen = false; }
        DrawSkia?.InvalidateVisual();
    }

    private void CommitScalePfadChain()
    {
        if (_pfadScaleChainIdx < 0) return;
        int idx = _pfadScaleChainIdx;
        _pfadScaleChainIdx = -1;
        _pfadScaleOrigAbs.Clear();
        _suppressNextAutoFit = true;
        PropagateVermConstraints();
        CheckAndReportConstraints();
        HistoryList.SelectedItem = _history[idx];
        UpdateAll();
    }

    // ── Vermassen ────────────────────────────────────────────────

    private static double DistPointToSegment(double px, double py,
        double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx*dx + dy*dy;
        if (lenSq < 1e-12) return Math.Sqrt((px-ax)*(px-ax)+(py-ay)*(py-ay));
        double t = Math.Clamp(((px-ax)*dx + (py-ay)*dy) / lenSq, 0, 1);
        double qx = ax + t*dx, qy = ay + t*dy;
        return Math.Sqrt((px-qx)*(px-qx)+(py-qy)*(py-qy));
    }

    private (int p1, int p2) HitTestPfadLineSegment(double mmX, double mmY)
    {
        double tol = 5.0 / _zoom;
        for (int i = 1; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp || pp.Typ != PfadPunktTyp.Linie) continue;
            var abs2 = GetPfadAbsAt(i);     if (!abs2.HasValue) continue;
            var abs1 = GetPfadAbsAt(i - 1); if (!abs1.HasValue) continue;
            if (_history[i - 1].Params is not PfadPunktParams) continue;
            double d = DistPointToSegment(mmX, mmY,
                abs1.Value.x, abs1.Value.y, abs2.Value.x, abs2.Value.y);
            if (d <= tol) return (i - 1, i);
        }
        return (-1, -1);
    }

    // Nächster Pfad-Punkt innerhalb der Toleranz (-1 = keiner)
    private int HitTestPfadPoint(double mmX, double mmY)
    {
        double tol = 6.0 / _zoom;
        int best = -1; double bestD = tol;
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams) continue;
            var abs = GetPfadAbsAt(i); if (!abs.HasValue) continue;
            double dx = mmX - abs.Value.x, dy = mmY - abs.Value.y;
            double d = Math.Sqrt(dx*dx + dy*dy);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // Returns which workpiece edge the mouse is near: 0=none,1=left,2=right,3=bottom,4=top
    private int HitTestWorkpieceEdge(double mmX, double mmY)
    {
        double tol = 5.0 / _zoom;
        if (mmX >= -tol && mmX <= WorkX + tol && mmY >= -tol && mmY <= WorkY + tol)
        {
            if (Math.Abs(mmX)         <= tol) return 1;
            if (Math.Abs(mmX - WorkX) <= tol) return 2;
            if (Math.Abs(mmY)         <= tol) return 3;
            if (Math.Abs(mmY - WorkY) <= tol) return 4;
        }
        return 0;
    }

    // Gibt Eckpunkt-Index zurück: 1=unten-links(0,0) 2=unten-rechts(W,0) 3=oben-rechts(W,H) 4=oben-links(0,H)
    private int HitTestWorkpieceCorner(double mmX, double mmY)
    {
        double tol = 6.0 / _zoom;
        (double x, double y)[] corners = { (0, 0), (WorkX, 0), (WorkX, WorkY), (0, WorkY) };
        for (int i = 0; i < corners.Length; i++)
            if (Math.Abs(mmX - corners[i].x) <= tol && Math.Abs(mmY - corners[i].y) <= tol)
                return i + 1;
        return 0;
    }

    private (double x, double y) WorkpieceCornerPos(int corner) => corner switch
    {
        1 => (0, 0),
        2 => (WorkX, 0),
        3 => (WorkX, WorkY),
        4 => (0, WorkY),
        _ => (0, 0)
    };

    // Rechteck-Punkte für Vermassungswerkzeug: 4 Eckpunkte in mm
    // Rückgabewert: Array von (x, y) Positionen: [BL, BR, TL, TR]
    private (double x, double y)[] GetRektEckpunkte(RechteckParams rp)
    {
        var (left, bottom, w, h) = RechteckBoundsInMm(rp);
        return
        [
            (left,       bottom),       // 0: unten-links
            (left + w,   bottom),       // 1: unten-rechts
            (left,       bottom + h),   // 2: oben-links
            (left + w,   bottom + h)    // 3: oben-rechts
        ];
    }

    // Rechteck-Seiten für Vermassungswerkzeug
    // Rückgabewert: Array von Segmenten: [(x1,y1,x2,y2)]
    // 0=unten, 1=rechts, 2=oben, 3=links
    private (double x1, double y1, double x2, double y2)[] GetRektSeiten(RechteckParams rp)
    {
        var pts = GetRektEckpunkte(rp);
        return
        [
            (pts[0].x, pts[0].y, pts[1].x, pts[1].y),  // 0: unten
            (pts[1].x, pts[1].y, pts[3].x, pts[3].y),  // 1: rechts
            (pts[3].x, pts[3].y, pts[2].x, pts[2].y),  // 2: oben
            (pts[2].x, pts[2].y, pts[0].x, pts[0].y)   // 3: links
        ];
    }

    // Hit-Test für Rechteck-Eckpunkte
    // Gibt: (-1, -1) zurück, wenn kein Treffer
    //       (rectHistIdx, cornerIdx) wenn Treffer, wobei cornerIdx = 0-3
    private (int rectIdx, int cornerIdx) HitTestRktPoint(double mmX, double mmY)
    {
        double tol = 6.0 / _zoom;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not RechteckParams rp) continue;
            var pts = GetRektEckpunkte(rp);
            for (int c = 0; c < pts.Length; c++)
            {
                double dx = mmX - pts[c].x, dy = mmY - pts[c].y;
                if (dx * dx + dy * dy <= tol * tol)
                    return (i, c);
            }
        }
        return (-1, -1);
    }

    // Hit-Test für Rechteck-Seiten (Kanten)
    // Gibt: (-1, -1) zurück, wenn kein Treffer
    //       (rectHistIdx, sideIdx) wenn Treffer, wobei sideIdx = 0-3
    private (int rectIdx, int sideIdx) HitTestRktSide(double mmX, double mmY)
    {
        double tol = 5.0 / _zoom;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not RechteckParams rp) continue;
            var sides = GetRektSeiten(rp);
            for (int s = 0; s < sides.Length; s++)
            {
                var (x1, y1, x2, y2) = sides[s];
                double d = DistPointToSegment(mmX, mmY, x1, y1, x2, y2);
                if (d <= tol)
                    return (i, s);
            }
        }
        return (-1, -1);
    }

    // Encoding-Helper für Rechteck-Punkte und Seiten
    // Punkt: -(1000000 + historyIdx * 10 + cornerIdx)
    // Seite: -(2000000 + historyIdx * 10 + sideIdx)
    private int EncodeRktPoint(int historyIdx, int cornerIdx)
        => -(1000000 + historyIdx * 10 + cornerIdx);

    private int EncodeRktSide(int historyIdx, int sideIdx)
        => -(2000000 + historyIdx * 10 + sideIdx);

    private bool IsRktPointEncoded(int idx) => idx < -1000000 && idx >= -2000000;
    private bool IsRktSideEncoded(int idx) => idx < -2000000;

    private (int historyIdx, int cornerIdx) DecodeRktPoint(int idx)
    {
        int encoded = -idx - 1000000;
        return (encoded / 10, encoded % 10);
    }

    private (int historyIdx, int sideIdx) DecodeRktSide(int idx)
    {
        int encoded = -idx - 2000000;
        return (encoded / 10, encoded % 10);
    }

    // Erweiterte Hit-Tests, die auch Rechteck-Punkte und -Seiten berücksichtigen
    private int HitTestPfadPointOrRktPoint(double mmX, double mmY)
    {
        int ptHit = HitTestPfadPoint(mmX, mmY);
        if (ptHit >= 0) return ptHit;

        var (rktIdx, cornerIdx) = HitTestRktPoint(mmX, mmY);
        if (rktIdx >= 0) return EncodeRktPoint(rktIdx, cornerIdx);

        return -1;
    }

    private (int p1, int p2) HitTestPfadSegmentOrRktSide(double mmX, double mmY)
    {
        var pfadHit = HitTestPfadLineSegment(mmX, mmY);
        if (pfadHit.p1 >= 0) return pfadHit;

        var (rktIdx, sideIdx) = HitTestRktSide(mmX, mmY);
        if (rktIdx >= 0) return (EncodeRktSide(rktIdx, sideIdx), 0);  // p2 wird nicht verwendet für Seiten

        return (-1, -1);
    }

    // Helper: Absolutposition eines Pfad-Punkts oder Rechteck-Eckpunkts ermitteln
    private (double x, double y)? GetAbsPosForVerm(int idx)
    {
        if (IsRktPointEncoded(idx))
        {
            var (historyIdx, cornerIdx) = DecodeRktPoint(idx);
            if (historyIdx < 0 || historyIdx >= _history.Count) return null;
            if (_history[historyIdx].Params is not RechteckParams rp) return null;
            var pts = GetRektEckpunkte(rp);
            if (cornerIdx < 0 || cornerIdx >= pts.Length) return null;
            return pts[cornerIdx];
        }
        else
        {
            return GetPfadAbsAt(idx);
        }
    }

    private double EdgeDistValue(double px, double py, int edge) => edge switch
    {
        1 => px,
        2 => WorkX - px,
        3 => py,
        4 => WorkY - py,
        _ => 0
    };

    // ── Kreis-Bemassung (Durchmesser / Radius / Abstand Mitte → Werkstückkante) ──

    private static bool IsKreisVermKind(VermKind k)
        => k is VermKind.KreisDurchmesser or VermKind.KreisRadius or VermKind.KreisKantenDist;

    // Reihenlochbohrung-Anker werden in der Kreis-Bemassung wie Kreise behandelt.
    // Kodierung: History-Idx + Anker * RlbAnkerOffset
    //   Anker 0 = 1. Loch unten links, 1 = 2. Loch in Y (Lochabstand Y),
    //   Anker 2 = letztes Loch in X, 3 = letztes Loch in Y
    private const int RlbAnkerOffset  = 1_000_000;

    private static (int histIdx, int anker) DecodeKreisVermIdx(int idx)
        => idx < 0 ? (idx, 0) : (idx % RlbAnkerOffset, idx / RlbAnkerOffset);

    private bool IsRlbAnkerVermIdx(int idx)
    {
        var (h, _) = DecodeKreisVermIdx(idx);
        return h >= 0 && h < _history.Count && _history[h].Params is ReihenlochbohrungParams;
    }

    // Mittelpunkt eines Reihenloch-Ankers (Standard-mm), null wenn nicht vorhanden
    private (double x, double y)? RlbAnkerPos(ReihenlochbohrungParams p, int anker)
    {
        var (sx, sy) = RlbStart(p);
        return anker switch
        {
            0                     => (sx, sy),
            1 when p.CountY >= 2  => (sx, sy + p.SpacingY),
            2 when p.CountX >= 2  => (sx + (p.CountX - 1) * p.SpacingX, sy),
            3 when p.CountY >= 3  => (sx, sy + (p.CountY - 1) * p.SpacingY),  // bei 2 Löchern = Anker 1
            _                     => null
        };
    }

    // Mittelpunkt + Radius eines Kreises (History-Idx) in mm, null wenn kein Kreis
    private (double cx, double cy, double r)? GetKreisGeom(int histIdx)
    {
        var (h, anker) = DecodeKreisVermIdx(histIdx);
        if (h < 0 || h >= _history.Count) return null;
        if (_history[h].Params is ReihenlochbohrungParams rlb)
        {
            var pos = RlbAnkerPos(rlb, anker);
            return pos == null ? null : (pos.Value.x, pos.Value.y, rlb.LochD / 2);
        }
        if (anker != 0 || _history[h].Params is not KreisParams kr) return null;
        var (cx, cy) = GCodeGenerator.ConvertBezugspunkt(kr.Bezugspunkt, kr.XRel, kr.YRel, WorkX, WorkY);
        return (cx, cy, kr.Radius);
    }

    // Treffertest Kreis im Vermassen-Werkzeug: Kontur oder Mittelpunkt (-1 = keiner)
    private int HitTestKreisVerm(double mmX, double mmY)
    {
        double tol = 6.0 / _zoom;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            foreach (int k in new[] { i + 3 * RlbAnkerOffset, i + 2 * RlbAnkerOffset, i + RlbAnkerOffset, i })
            {
                var g = GetKreisGeom(k); if (g == null) continue;
                double dx = mmX - g.Value.cx, dy = mmY - g.Value.cy;
                double d = Math.Sqrt(dx*dx + dy*dy);
                if (Math.Abs(d - g.Value.r) <= tol || d <= tol) return k;
            }
        }
        return -1;
    }

    // Kantenabstand eines Reihenloch-Ankers anwenden:
    //   Anker 0 → Lochreihe verschieben
    //   Anker 1 / 3 (Y-Richtung): oben/unten → Lochabstand Y, links/rechts → Reihe verschieben
    //   Anker 2     (X-Richtung): links/rechts → Lochabstand X, oben/unten → Reihe verschieben
    private void ApplyRlbAnkerVerm(VermEntry en, double newVal)
    {
        var (h, anker) = DecodeKreisVermIdx(en.P2Idx);
        if (en.Kind != VermKind.KreisKantenDist || h < 0 || h >= _history.Count
            || _history[h].Params is not ReihenlochbohrungParams p) return;
        var pos = RlbAnkerPos(p, anker); if (pos == null) return;
        var (sx, sy) = RlbStart(p);
        var (ax, ay) = pos.Value;
        switch (en.Edge)
        {
            case 1: ax = newVal;         break;
            case 2: ax = WorkX - newVal; break;
            case 3: ay = newVal;         break;
            case 4: ay = WorkY - newVal; break;
            default: return;
        }
        bool vertikal = en.Edge is 3 or 4;
        ReihenlochbohrungParams np;
        if (anker is 1 or 3 && vertikal)
        {
            int n = anker == 1 ? 1 : p.CountY - 1;
            double spY = Math.Round((ay - sy) / n, 3);
            if (spY <= p.LochD) return;
            np = p with { SpacingY = spY };
        }
        else if (anker == 2 && !vertikal)
        {
            double spX = Math.Round((ax - sx) / (p.CountX - 1), 3);
            if (spX <= p.LochD) return;
            np = p with { SpacingX = spX };
        }
        else
        {
            // Reihe verschieben: Startpunkt um dieselbe Differenz wie der Anker
            var (nx, ny) = ConvertToNullpunktCoordinates(sx + (ax - pos.Value.x), sy + (ay - pos.Value.y));
            np = p with { StartX = Math.Round(nx, 3), StartY = Math.Round(ny, 3) };
        }
        if (np == p) return;
        var old = _history[h];
        bool wasSelected = HistoryList.SelectedItem == old;
        bool wasSuppressed = _suppressHistoryRegen;
        _suppressHistoryRegen = true;
        try { _history[h] = new HistoryEntry(old.Label, RlbDetails(np), np, old.Level); }
        finally { _suppressHistoryRegen = wasSuppressed; }
        if (wasSelected) HistoryList.SelectedItem = _history[h];
    }

    // Endpunkte der Durchmesser-/Radius-Masslinie unter dem Winkel angle (rad)
    private ((double x, double y) a, (double x, double y) b)? KreisDimLine(int histIdx, bool isRadius, double angle)
    {
        var g = GetKreisGeom(histIdx); if (g == null) return null;
        var (cx, cy, r) = g.Value;
        double ux = Math.Cos(angle), uy = Math.Sin(angle);
        var b = (cx + ux*r, cy + uy*r);
        var a = isRadius ? (cx, cy) : (cx - ux*r, cy - uy*r);
        return (a, b);
    }

    // Label-Position Durchmesser/Radius: auf der Masslinie, halbwegs zwischen Mitte und Kontur
    private (double x, double y)? KreisDimLabelPos(int histIdx, double angle)
    {
        var g = GetKreisGeom(histIdx); if (g == null) return null;
        return (g.Value.cx + Math.Cos(angle) * g.Value.r / 2, g.Value.cy + Math.Sin(angle) * g.Value.r / 2);
    }

    // Aktueller Ist-Wert einer Kreis-Bemassung
    private double? KreisVermActual(VermEntry en)
    {
        var g = GetKreisGeom(en.P2Idx); if (g == null) return null;
        return en.Kind switch
        {
            VermKind.KreisDurchmesser => 2 * g.Value.r,
            VermKind.KreisRadius      => g.Value.r,
            VermKind.KreisKantenDist  => EdgeDistValue(g.Value.cx, g.Value.cy, en.Edge),
            _                         => null
        };
    }

    // Kreis-Bemassung auf die KreisParams anwenden (Radius bzw. Position ändern)
    private void ApplyKreisVerm(VermEntry en, double newVal)
    {
        if (IsRlbAnkerVermIdx(en.P2Idx)) { ApplyRlbAnkerVerm(en, newVal); return; }
        if (en.P2Idx < 0 || en.P2Idx >= _history.Count || _history[en.P2Idx].Params is not KreisParams kr) return;
        KreisParams np;
        switch (en.Kind)
        {
            case VermKind.KreisDurchmesser: np = kr with { Radius = Math.Round(newVal / 2, 4) }; break;
            case VermKind.KreisRadius:      np = kr with { Radius = Math.Round(newVal, 4) };     break;
            case VermKind.KreisKantenDist:
            {
                var (cx, cy) = GCodeGenerator.ConvertBezugspunkt(kr.Bezugspunkt, kr.XRel, kr.YRel, WorkX, WorkY);
                switch (en.Edge)
                {
                    case 1: cx = newVal;         break;
                    case 2: cx = WorkX - newVal; break;
                    case 3: cy = newVal;         break;
                    case 4: cy = WorkY - newVal; break;
                    default: return;
                }
                var (xRel, yRel) = AbsToRel(kr.Bezugspunkt, cx, cy, WorkX, WorkY);
                np = kr with { XRel = Math.Round(xRel, 3), YRel = Math.Round(yRel, 3) };
                break;
            }
            default: return;
        }
        if (np == kr) return;
        var old = _history[en.P2Idx];
        bool wasSelected = HistoryList.SelectedItem == old;
        bool wasSuppressed = _suppressHistoryRegen;
        _suppressHistoryRegen = true;
        try { _history[en.P2Idx] = new HistoryEntry(old.Label,
            $"M={np.XRel}/{np.YRel} R={np.Radius} Z={np.ZTiefe}", np, old.Level); }
        finally { _suppressHistoryRegen = wasSuppressed; }
        if (wasSelected) HistoryList.SelectedItem = _history[en.P2Idx];
    }

    // Kreis gewählt (State 1), Klick ins Freie → Durchmesser (Maus ausserhalb) bzw. Radius (innerhalb)
    private void PlaceKreisDimAt(double mmX, double mmY)
    {
        var g = GetKreisGeom(_vermKreisIdx);
        // Reihenloch-Anker: nur Kantenabstand möglich → Klick ins Freie bricht ab
        if (g == null || IsRlbAnkerVermIdx(_vermKreisIdx)) { _vermState = 0; _vermKreisIdx = -1; return; }
        var (cx, cy, r) = g.Value;
        bool isRadius = Math.Sqrt((mmX - cx)*(mmX - cx) + (mmY - cy)*(mmY - cy)) < r;
        _vermActiveKind = isRadius ? VermKind.KreisRadius : VermKind.KreisDurchmesser;
        _vermP1Idx      = -1;
        _vermP2Idx      = _vermKreisIdx;
        _vermActiveEdge = 0;
        _vermOffset     = Math.Atan2(mmY - cy, mmX - cx);
        _vermState      = 2;
        ShowVermTextBox(Math.Round(isRadius ? r : 2 * r, 3), "F3");
    }

    // Kreis + Kante gewählt → Kantenabstand-Vorschau (State 5)
    private void StartKreisKantenDist(int kreisIdx, int edge)
    {
        var g = GetKreisGeom(kreisIdx); if (g == null) return;
        _vermKreisIdx   = kreisIdx;
        _vermActiveEdge = edge;
        _vermP1Idx      = -1;
        _vermP2Idx      = kreisIdx;
        _vermP2Abs      = (g.Value.cx, g.Value.cy);
        _vermActiveKind = VermKind.KreisKantenDist;
        _vermOffset     = 0; _vermPtIdx = -1;
        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0; _vermHoverPoint = -1; _vermHoverKreis = -1; _vermHoverRkt = -1;
        _vermState      = 5;
    }

    // ── Rechteck-Bemassung (Seitenlänge / Abstand Seite → Werkstückkante) ──

    private static bool IsRktVermKind(VermKind k)
        => k is VermKind.RktSeite or VermKind.RktKantenDist;

    // Kodierte Rechteck-Seite (History-Idx + Seite * RlbAnkerOffset) aus Hover-/Hit-Kodierung
    private static int RktVermIdx(int histIdx, int side) => histIdx + side * RlbAnkerOffset;

    // Endpunkte einer Rechteck-Seite (mm), null wenn kein Rechteck
    private ((double x, double y) a, (double x, double y) b)? GetRktSeiteGeom(int rktVermIdx)
    {
        var (h, side) = DecodeKreisVermIdx(rktVermIdx);
        if (h < 0 || h >= _history.Count || side < 0 || side > 3
            || _history[h].Params is not RechteckParams rp) return null;
        var (x1, y1, x2, y2) = GetRektSeiten(rp)[side];
        return ((x1, y1), (x2, y2));
    }

    private (double x, double y)? RktSeiteMitte(int rktVermIdx)
    {
        var g = GetRktSeiteGeom(rktVermIdx); if (g == null) return null;
        return ((g.Value.a.x + g.Value.b.x) / 2, (g.Value.a.y + g.Value.b.y) / 2);
    }

    // Seite 0/2 (unten/oben) ist horizontal → parallel zu Kante 3/4; Seite 1/3 → Kante 1/2
    private static bool RktSeiteParallelZuKante(int side, int edge)
        => side is 0 or 2 ? edge is 3 or 4 : edge is 1 or 2;

    // Aktueller Ist-Wert einer Rechteck-Bemassung
    private double? RktVermActual(VermEntry en)
    {
        var g = GetRktSeiteGeom(en.P2Idx); if (g == null) return null;
        var (a, b) = g.Value;
        return en.Kind switch
        {
            VermKind.RktSeite      => Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y)),
            VermKind.RktKantenDist => EdgeDistValue((a.x + b.x) / 2, (a.y + b.y) / 2, en.Edge),
            _                      => null
        };
    }

    // True, wenn die Seite side des Rechtecks h über eine Kantenabstand-Bemassung fixiert ist
    private bool IsRktSeiteKantenBemasst(int h, int side)
        => _vermPlaced.Any(e => e.Kind == VermKind.RktKantenDist && e.P2Idx == RktVermIdx(h, side));

    // Rechteck-Bemassung auf die RechteckParams anwenden.
    //   RktSeite: Breite/Höhe ändern. Fix bleibt die linke bzw. untere Seite — ausser nur die
    //             gegenüberliegende Seite ist zur Werkstückkante bemasst, dann bleibt diese fix.
    //   RktKantenDist: Rechteck verschieben. Ist die gegenüberliegende Seite ebenfalls zur
    //             Werkstückkante bemasst, wird stattdessen nur diese Seite verschoben (Grösse ändert).
    private void ApplyRktVerm(VermEntry en, double newVal)
    {
        var (h, side) = DecodeKreisVermIdx(en.P2Idx);
        if (h < 0 || h >= _history.Count || _history[h].Params is not RechteckParams rp) return;
        var (left, bottom, w, ht) = RechteckBoundsInMm(rp);
        double right = left + w, top = bottom + ht;
        bool horizSeite = side is 0 or 2;
        switch (en.Kind)
        {
            case VermKind.RktSeite:
                if (horizSeite)
                {
                    if (IsRktSeiteKantenBemasst(h, 1) && !IsRktSeiteKantenBemasst(h, 3)) left = right - newVal;
                    else right = left + newVal;
                }
                else
                {
                    if (IsRktSeiteKantenBemasst(h, 2) && !IsRktSeiteKantenBemasst(h, 0)) bottom = top - newVal;
                    else top = bottom + newVal;
                }
                break;
            case VermKind.RktKantenDist:
            {
                if (!RktSeiteParallelZuKante(side, en.Edge)) return;
                double target = en.Edge switch
                {
                    1 => newVal,
                    2 => WorkX - newVal,
                    3 => newVal,
                    _ => WorkY - newVal,
                };
                int gegenSeite = (side + 2) % 4;
                bool resize = IsRktSeiteKantenBemasst(h, gegenSeite);
                double cur = side switch { 0 => bottom, 1 => right, 2 => top, _ => left };
                double d = target - cur;
                switch (side)
                {
                    case 0: bottom += d; if (!resize) top   += d; break;
                    case 1: right  += d; if (!resize) left  += d; break;
                    case 2: top    += d; if (!resize) bottom += d; break;
                    default: left  += d; if (!resize) right += d; break;
                }
                break;
            }
            default: return;
        }
        double nw = right - left, nh = top - bottom;
        if (nw <= 0.001 || nh <= 0.001) return;
        var (refX, refY) = BezugAbsPos(rp.Bezugspunkt, left, bottom, nw, nh);
        var (xRel, yRel) = AbsToRel(rp.Bezugspunkt, refX, refY, WorkX, WorkY);
        var np = rp with
        {
            XRel   = Math.Round(xRel, 3), YRel  = Math.Round(yRel, 3),
            Breite = Math.Round(nw, 3),   Hoehe = Math.Round(nh, 3),
        };
        if (np == rp) return;
        var old = _history[h];
        bool wasSelected = HistoryList.SelectedItem == old;
        bool wasSuppressed = _suppressHistoryRegen;
        _suppressHistoryRegen = true;
        try { _history[h] = new HistoryEntry(old.Label,
            $"X={np.XRel} Y={np.YRel}, {np.Breite}×{np.Hoehe}, Z={np.ZTiefe}", np, old.Level); }
        finally { _suppressHistoryRegen = wasSuppressed; }
        if (wasSelected) HistoryList.SelectedItem = _history[h];
    }

    // Rechteck-Seite gewählt (State 1), Klick ins Freie → Seitenlänge platzieren
    private void PlaceRktSeiteAt(double mmX, double mmY)
    {
        var g = GetRktSeiteGeom(_vermRktIdx);
        if (g == null) { _vermState = 0; _vermRktIdx = -1; return; }
        _vermP1Abs      = g.Value.a;
        _vermP2Abs      = g.Value.b;
        _vermActiveKind = VermKind.RktSeite;
        _vermP1Idx      = -1;
        _vermP2Idx      = _vermRktIdx;
        _vermActiveEdge = 0;
        _vermOffset     = VermSignedOffset(mmX, mmY, _vermP1Abs, _vermP2Abs);
        _vermState      = 2;
        ShowVermTextBox(Math.Round(VermSegmentLength(), 3), "F3");
    }

    // Rechteck-Seite + parallele Kante gewählt → Kantenabstand-Vorschau (State 5)
    private bool StartRktKantenDist(int rktVermIdx, int edge)
    {
        var (_, side) = DecodeKreisVermIdx(rktVermIdx);
        if (!RktSeiteParallelZuKante(side, edge)) return false;
        var m = RktSeiteMitte(rktVermIdx); if (m == null) return false;
        _vermRktIdx     = rktVermIdx;
        _vermActiveEdge = edge;
        _vermP1Idx      = -1;
        _vermP2Idx      = rktVermIdx;
        _vermP2Abs      = m.Value;
        _vermActiveKind = VermKind.RktKantenDist;
        _vermOffset     = 0; _vermPtIdx = -1;
        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0; _vermHoverPoint = -1; _vermHoverKreis = -1; _vermHoverRkt = -1;
        _vermState      = 5;
        return true;
    }

    // Treffertest Rechteck-Seite im Vermassen-Werkzeug (-1 = keine)
    private int HitTestRktVerm(double mmX, double mmY)
    {
        var (h, side) = HitTestRktSide(mmX, mmY);
        return h >= 0 ? RktVermIdx(h, side) : -1;
    }

    // Hit-Test: Label einer platzierten Masslinie (Screenkoordinaten in logischen Pixeln)
    private int HitTestVermLabel(double screenX, double screenY)
    {
        for (int i = 0; i < _vermPlaced.Count; i++)
        {
            var lmm = VermLabelPosMm(_vermPlaced[i]);
            if (lmm == null) continue;
            double sx = lmm.Value.x * _zoom + _panX;
            double sy = (WorkY - lmm.Value.y) * _zoom + _panY;
            if (Math.Abs(screenX - sx) <= 48 && Math.Abs(screenY - sy) <= 14)
                return i;
        }
        return -1;
    }

    // Label-Position einer VermEntry in mm-Koordinaten
    private (double x, double y)? VermLabelPosMm(VermEntry en)
    {
        if (en.Kind is VermKind.KreisDurchmesser or VermKind.KreisRadius)
            return KreisDimLabelPos(en.P2Idx, en.Offset);
        if (en.Kind == VermKind.KreisKantenDist)
        {
            var g = GetKreisGeom(en.P2Idx);
            if (g == null || en.Edge <= 0) return null;
            if (en.Edge == 1 || en.Edge == 2)
                return ((g.Value.cx + (en.Edge == 1 ? 0 : WorkX)) / 2, g.Value.cy + en.Offset);
            return (g.Value.cx + en.Offset, (g.Value.cy + (en.Edge == 3 ? 0 : WorkY)) / 2);
        }
        if (en.Kind == VermKind.RktSeite)
        {
            var g = GetRktSeiteGeom(en.P2Idx); if (g == null) return null;
            var (a, b) = g.Value;
            double dx = b.x - a.x, dy = b.y - a.y;
            double len = Math.Sqrt(dx*dx + dy*dy); if (len < 1e-9) return null;
            return ((a.x + b.x)/2 - dy/len * en.Offset, (a.y + b.y)/2 + dx/len * en.Offset);
        }
        if (en.Kind == VermKind.RktKantenDist)
        {
            var m = RktSeiteMitte(en.P2Idx);
            if (m == null || en.Edge <= 0) return null;
            if (en.Edge == 1 || en.Edge == 2)
                return ((m.Value.x + (en.Edge == 1 ? 0 : WorkX)) / 2, m.Value.y + en.Offset);
            return (m.Value.x + en.Offset, (m.Value.y + (en.Edge == 3 ? 0 : WorkY)) / 2);
        }
        var p1 = GetAbsPosForVerm(en.P1Idx);
        if (p1 == null && en.Kind != VermKind.EdgeDist && en.Kind != VermKind.PointEdgeDist) return null;
        var p2 = GetAbsPosForVerm(en.P2Idx); if (p2 == null) return null;
        switch (en.Kind)
        {
            case VermKind.Length:
            {
                double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
                double len = Math.Sqrt(dx*dx + dy*dy); if (len < 1e-9) return null;
                double nx = -dy/len, ny = dx/len;
                return ((p1.Value.x + p2.Value.x)/2 + nx * en.Offset,
                        (p1.Value.y + p2.Value.y)/2 + ny * en.Offset);
            }
            case VermKind.ParallelDist:
            {
                var q1 = GetAbsPosForVerm(en.Q1Idx); if (q1 == null) return null;
                double dx1 = p2.Value.x - p1.Value.x, dy1 = p2.Value.y - p1.Value.y;
                double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1); if (l1 < 1e-9) return null;
                double nx = -dy1/l1, ny = dx1/l1;
                double signedDist = (q1.Value.x - p1.Value.x)*nx + (q1.Value.y - p1.Value.y)*ny;
                var anchor1 = (p1.Value.x + en.Offset * dx1, p1.Value.y + en.Offset * dy1);
                var anchor2 = (anchor1.Item1 + nx * signedDist, anchor1.Item2 + ny * signedDist);
                return ((anchor1.Item1 + anchor2.Item1)/2, (anchor1.Item2 + anchor2.Item2)/2);
            }
            case VermKind.Angle:
            {
                var q1 = GetAbsPosForVerm(en.Q1Idx); if (q1 == null) return null;
                var q2 = GetAbsPosForVerm(en.Q2Idx); if (q2 == null) return null;
                var inter = LinesIntersection(p1.Value, p2.Value, q1.Value, q2.Value);
                if (inter == null) return null;
                double a1 = VermSegArcAngle(inter.Value, p1.Value, p2.Value);
                double a2 = VermSegArcAngle(inter.Value, q1.Value, q2.Value);
                double amid = VermArcMidAngle(a1, a2);
                // en.Offset ist t-Parameter entlang P-Segment → Radius daraus berechnen
                double r = AngleArcRadius(en.Offset, p1.Value, p2.Value, inter.Value);
                return (inter.Value.x + r * Math.Cos(amid), inter.Value.y + r * Math.Sin(amid));
            }
            case VermKind.EdgeDist:
            case VermKind.PointEdgeDist:
            {
                if (en.Edge <= 0) return null;
                if (en.Edge == 1 || en.Edge == 2)
                    return ((p2.Value.x + (en.Edge == 1 ? 0 : WorkX)) / 2, p2.Value.y + en.Offset);
                else
                    return (p2.Value.x + en.Offset, (p2.Value.y + (en.Edge == 3 ? 0 : WorkY)) / 2);
            }
            case VermKind.EdgeAngle:
            {
                if (p1 == null || en.Edge <= 0) return null;
                var inter = SegmentEdgeIntersection(p1.Value, p2.Value, en.Edge);
                if (inter == null) return null;
                var (e1, e2) = EdgeVirtualSegment(inter.Value, en.Edge);
                double a1 = VermSegArcAngle(inter.Value, p1.Value, p2.Value);
                double a2 = VermSegArcAngle(inter.Value, e1, e2);
                double amid = VermArcMidAngle(a1, a2);
                double r = AngleArcRadius(en.Offset, p1.Value, p2.Value, inter.Value);
                return (inter.Value.x + r * Math.Cos(amid), inter.Value.y + r * Math.Sin(amid));
            }
            case VermKind.PointDist:
            {
                double dx = p2.Value.x - p1!.Value.x, dy = p2.Value.y - p1.Value.y;
                double len = Math.Sqrt(dx*dx + dy*dy); if (len < 1e-9) return null;
                double nx = -dy/len, ny = dx/len;
                double midX = (p1.Value.x + p2.Value.x)/2 + nx * en.Offset;
                double midY = (p1.Value.y + p2.Value.y)/2 + ny * en.Offset;
                return (midX, midY);
            }
            case VermKind.LineToPoint:
            {
                if (p1 == null) return null;
                var q1 = GetPfadAbsAt(en.Q1Idx); if (q1 == null) return null;
                double dx1 = p2.Value.x - p1.Value.x, dy1 = p2.Value.y - p1.Value.y;
                double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1); if (l1 < 1e-9) return null;
                double nx = -dy1/l1, ny = dx1/l1;
                double sD = (q1.Value.x - p1.Value.x)*nx + (q1.Value.y - p1.Value.y)*ny;
                double ax = p1.Value.x + en.Offset * dx1, ay = p1.Value.y + en.Offset * dy1;
                return (ax + nx * sD / 2, ay + ny * sD / 2);
            }
            default: return null;
        }
    }

    // Hit-Test: Masslinie (Dimensionslinie) einer platzierten Masslinie (mm-Koordinaten)
    private int HitTestVermLine(double mmX, double mmY)
    {
        double tol = 4.0 / _zoom;
        for (int i = 0; i < _vermPlaced.Count; i++)
        {
            var en = _vermPlaced[i];
            if (en.Kind is VermKind.KreisDurchmesser or VermKind.KreisRadius)
            {
                var dl = KreisDimLine(en.P2Idx, en.Kind == VermKind.KreisRadius, en.Offset);
                if (dl != null && DistPointToSegment(mmX, mmY, dl.Value.a.x, dl.Value.a.y,
                                                     dl.Value.b.x, dl.Value.b.y) <= tol) return i;
                continue;
            }
            if (en.Kind == VermKind.RktSeite)
            {
                var rs = GetRktSeiteGeom(en.P2Idx); if (rs == null) continue;
                var (ra, rb) = rs.Value;
                double rdx = rb.x - ra.x, rdy = rb.y - ra.y;
                double rl = Math.Sqrt(rdx*rdx + rdy*rdy); if (rl < 1e-9) continue;
                double onx = -rdy/rl * en.Offset, ony = rdx/rl * en.Offset;
                if (DistPointToSegment(mmX, mmY, ra.x + onx, ra.y + ony, rb.x + onx, rb.y + ony) <= tol) return i;
                continue;
            }
            var p1 = GetPfadAbsAt(en.P1Idx);
            if (p1 == null && en.Kind != VermKind.EdgeDist && en.Kind != VermKind.PointEdgeDist
                && en.Kind != VermKind.KreisKantenDist && en.Kind != VermKind.RktKantenDist) continue;
            (double x, double y)? p2 = GetPfadAbsAt(en.P2Idx);
            if (en.Kind == VermKind.KreisKantenDist && GetKreisGeom(en.P2Idx) is { } kg) p2 = (kg.cx, kg.cy);
            if (en.Kind == VermKind.RktKantenDist) p2 = RktSeiteMitte(en.P2Idx);
            if (p2 == null) continue;
            if (en.Kind == VermKind.EdgeDist || en.Kind == VermKind.PointEdgeDist || en.Kind == VermKind.KreisKantenDist
                || en.Kind == VermKind.RktKantenDist)
            {
                if (en.Edge <= 0) continue;
                bool isHoriz = (en.Edge == 1 || en.Edge == 2);
                double xEdge = en.Edge == 1 ? 0 : WorkX;
                double yEdge = en.Edge == 3 ? 0 : WorkY;
                if (isHoriz)
                {
                    double lineY = p2.Value.y + en.Offset;
                    double lineX1 = Math.Min(p2.Value.x, xEdge);
                    double lineX2 = Math.Max(p2.Value.x, xEdge);
                    if (DistPointToSegment(mmX, mmY, lineX1, lineY, lineX2, lineY) <= tol) return i;
                }
                else
                {
                    double lineX = p2.Value.x + en.Offset;
                    double lineY1 = Math.Min(p2.Value.y, yEdge);
                    double lineY2 = Math.Max(p2.Value.y, yEdge);
                    if (DistPointToSegment(mmX, mmY, lineX, lineY1, lineX, lineY2) <= tol) return i;
                }
                continue;
            }
            if (en.Kind == VermKind.Length)
            {
                double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
                double len = Math.Sqrt(dx*dx + dy*dy); if (len < 1e-9) continue;
                double nx = -dy/len, ny = dx/len;
                double d1x = p1.Value.x + nx * en.Offset, d1y = p1.Value.y + ny * en.Offset;
                double d2x = p2.Value.x + nx * en.Offset, d2y = p2.Value.y + ny * en.Offset;
                if (DistPointToSegment(mmX, mmY, d1x, d1y, d2x, d2y) <= tol) return i;
            }
            else if (en.Kind == VermKind.ParallelDist)
            {
                var q1 = GetPfadAbsAt(en.Q1Idx); if (q1 == null) continue;
                double dx1 = p2.Value.x - p1.Value.x, dy1 = p2.Value.y - p1.Value.y;
                double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1); if (l1 < 1e-9) continue;
                double nx = -dy1/l1, ny = dx1/l1;
                double sD = (q1.Value.x - p1.Value.x)*nx + (q1.Value.y - p1.Value.y)*ny;
                double ax = p1.Value.x + en.Offset * dx1, ay = p1.Value.y + en.Offset * dy1;
                if (DistPointToSegment(mmX, mmY, ax, ay, ax + nx*sD, ay + ny*sD) <= tol) return i;
            }
            else if (en.Kind == VermKind.Angle)
            {
                var q1 = GetPfadAbsAt(en.Q1Idx); if (q1 == null) continue;
                var q2 = GetPfadAbsAt(en.Q2Idx); if (q2 == null) continue;
                var inter = LinesIntersection(p1.Value, p2.Value, q1.Value, q2.Value);
                if (inter == null) continue;
                // Approx: hit if within arc band ±2*tol of arc
                double r = en.Offset;
                double dist = Math.Sqrt(Math.Pow(mmX - inter.Value.x, 2) + Math.Pow(mmY - inter.Value.y, 2));
                if (Math.Abs(dist - r) <= tol * 2)
                {
                    double a1 = VermSegArcAngle(inter.Value, p1.Value, p2.Value);
                    double a2 = VermSegArcAngle(inter.Value, q1.Value, q2.Value);
                    double clickAng = Math.Atan2(mmY - inter.Value.y, mmX - inter.Value.x);
                    if (VermAngleInArc(clickAng, a1, a2)) return i;
                }
            }
        }
        return -1;
    }

    // Aktueller tatsächlicher Bogenwinkel (0–180°) einer Angle/EdgeAngle-Masslinie, live aus der
    // Geometrie berechnet — wird für Anzeige/Bearbeitung verwendet statt des gespeicherten
    // en.Value, damit das Editieren auch dann korrekt bleibt, wenn der Sollwinkel (bewusst,
    // siehe PropagateVermConstraintsLive) nicht mehr automatisch an die Geometrie angepasst wird.
    // Ohne das kann der gespeicherte Wert veralten (z.B. durch eine Parallel-Bemassung, die die
    // Linie mitdreht) und beim Bearbeiten wird der falsche Sektor (spitz/stumpf) gewählt, was zu
    // einer stark falschen Zieldrehung führt.
    private double? GetCurrentActualAngle(VermEntry en)
    {
        var p1 = GetPfadAbsAt(en.P1Idx); var p2 = GetPfadAbsAt(en.P2Idx);
        if (p1 == null || p2 == null) return null;
        if (en.Kind == VermKind.Angle)
        {
            var q1 = GetPfadAbsAt(en.Q1Idx); var q2 = GetPfadAbsAt(en.Q2Idx);
            if (q1 == null || q2 == null) return null;
            return VermArcSpanActual(p1.Value, p2.Value, q1.Value, q2.Value);
        }
        if (en.Kind == VermKind.EdgeAngle)
        {
            var inter = SegmentEdgeIntersection(p1.Value, p2.Value, en.Edge);
            if (inter == null) return null;
            var (e1, e2) = EdgeVirtualSegment(inter.Value, en.Edge);
            return VermArcSpanActual(p1.Value, p2.Value, e1, e2);
        }
        return null;
    }

    // TextBox für das Bearbeiten einer bestehenden Masslinie (State 4)
    private void ShowVermEditTextBox(int idx)
    {
        CloseVermTextBox();
        var en = _vermPlaced[idx];
        var lmm = VermLabelPosMm(en); if (lmm == null) return;
        double sx = lmm.Value.x * _zoom + _panX;
        double sy = (WorkY - lmm.Value.y) * _zoom + _panY;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string txt;
        if (en.Kind == VermKind.Angle || en.Kind == VermKind.EdgeAngle)
        {
            // Anzeigewert = tatsächlicher Winkel (0–180°, wie im Label) — live berechnet, damit
            // die Box immer den echten, sichtbaren Winkel zeigt (keine Faltung auf den spitzen
            // Wert mehr, sonst würde z.B. eine Eingabe von 110° nach dem Bestätigen wieder als
            // 70° angezeigt).
            txt = (GetCurrentActualAngle(en) ?? en.Value).ToString("F2", inv);
        }
        else if (IsKreisVermKind(en.Kind))
            txt = Math.Round(KreisVermActual(en) ?? en.Value, 3).ToString("F3", inv);
        else if (IsRktVermKind(en.Kind))
            txt = Math.Round(RktVermActual(en) ?? en.Value, 3).ToString("F3", inv);
        else
            txt = en.Value.ToString("F3", inv);

        _vermTextBox = new TextBox
        {
            Text        = txt,
            Width       = 80, FontSize = 13,
            Background  = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(220, 100, 20)),
            BorderThickness = new Thickness(2),
            Padding     = new Thickness(3, 2, 3, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _vermTextBox.KeyDown += OnVermTextBoxKeyDown;
        System.Windows.Controls.Canvas.SetLeft(_vermTextBox, sx - 40);
        System.Windows.Controls.Canvas.SetTop (_vermTextBox, sy - 28);
        VermOverlayCanvas.Children.Add(_vermTextBox);
        _vermTextBox.SelectAll();
        _vermTextBox.Focus();
    }

    // Senkrechten Abstand Maus→Segment berechnen (vorzeichenbehaftet, links=positiv)
    private double VermSignedOffset(double mmX, double mmY,
        (double x, double y) p1, (double x, double y) p2)
    {
        double dx = p2.x - p1.x, dy = p2.y - p1.y;
        double len = Math.Sqrt(dx*dx + dy*dy);
        if (len < 1e-6) return 0;
        double nx = -dy/len, ny = dx/len; // linke Normale
        double mx = (p1.x + p2.x)/2, my = (p1.y + p2.y)/2;
        return (mmX - mx)*nx + (mmY - my)*ny;
    }

    // Sind zwei Segmente parallel? (Winkeltoleranz tolDeg°)
    private static bool AreSegmentsParallel(
        (double x, double y) p1, (double x, double y) p2,
        (double x, double y) q1, (double x, double y) q2,
        double tolDeg = 6.0)
    {
        double dx1 = p2.x - p1.x, dy1 = p2.y - p1.y;
        double dx2 = q2.x - q1.x, dy2 = q2.y - q1.y;
        double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1);
        double l2 = Math.Sqrt(dx2*dx2 + dy2*dy2);
        if (l1 < 1e-9 || l2 < 1e-9) return false;
        double cross = Math.Abs(dx1*dy2 - dy1*dx2) / (l1*l2);
        return cross < Math.Sin(tolDeg * Math.PI / 180.0);
    }

    // Ist ein Segment parallel zur angegebenen Werkstückkante?
    private bool IsSegmentParallelToEdge((double x, double y) p1, (double x, double y) p2, int edge)
    {
        // Kantenrichtung: links/rechts → vertikal (0,1), oben/unten → horizontal (1,0)
        var q1 = (0.0, 0.0);
        var q2 = (edge <= 2) ? (0.0, 1.0) : (1.0, 0.0);
        return AreSegmentsParallel(p1, p2, q1, q2, tolDeg: 0.1);
    }

    // Virtuelle Kantenpunkte für DrawAngleLine / VermArcSpanDeg
    private ((double x, double y) e1, (double x, double y) e2) EdgeVirtualSegment(
        (double x, double y) inter, int edge)
    {
        double ex = edge <= 2 ? 0 : 1, ey = edge <= 2 ? 1 : 0;
        return ((inter.x - ex * 100, inter.y - ey * 100),
                (inter.x + ex * 100, inter.y + ey * 100));
    }

    // Schnittpunkt Segment mit Kante (verlängert)
    private (double x, double y)? SegmentEdgeIntersection(
        (double x, double y) p1, (double x, double y) p2, int edge)
    {
        var e1 = edge == 1 ? (0.0, 0.0)    : edge == 2 ? (WorkX, 0.0)
               : edge == 3 ? (0.0, 0.0)    : (0.0, WorkY);
        var e2 = edge == 1 ? (0.0, WorkY)  : edge == 2 ? (WorkX, WorkY)
               : edge == 3 ? (WorkX, 0.0)  : (WorkX, WorkY);
        return LinesIntersection(p1, p2, e1, e2);
    }

    // Winkel zwischen zwei Segmenten in Grad (0–90°, Spitzwinkel)
    private static double AngleBetweenSegments(
        (double x, double y) p1, (double x, double y) p2,
        (double x, double y) q1, (double x, double y) q2)
    {
        double dx1 = p2.x - p1.x, dy1 = p2.y - p1.y;
        double dx2 = q2.x - q1.x, dy2 = q2.y - q1.y;
        double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1);
        double l2 = Math.Sqrt(dx2*dx2 + dy2*dy2);
        if (l1 < 1e-9 || l2 < 1e-9) return 0;
        double dot = (dx1*dx2 + dy1*dy2) / (l1*l2);
        return Math.Acos(Math.Clamp(Math.Abs(dot), 0.0, 1.0)) * 180.0 / Math.PI;
    }

    // Tatsächlicher Winkel des gezeichneten Bogens (0–180°).
    // Verwendet VermSegArcAngle (Richtung vom Schnittpunkt zur Segmentmitte),
    // damit der angezeigte Wert exakt dem sichtbaren Bogen entspricht:
    // kleinerer Wert = spitzerer Bogen, größerer Wert = stumpferer Bogen.
    // Echter Bogenwinkel (0–180°) — wird gespeichert, damit ApplyAngleConstraint den Bogen
    // nicht in einen anderen Sektor verschiebt.
    private static double VermArcSpanActual(
        (double x, double y) p1, (double x, double y) p2,
        (double x, double y) q1, (double x, double y) q2)
    {
        var inter = LinesIntersection(p1, p2, q1, q2);
        if (inter == null) return AngleBetweenSegments(p1, p2, q1, q2);
        double a1 = VermSegArcAngle(inter.Value, p1, p2);
        double a2 = VermSegArcAngle(inter.Value, q1, q2);
        double diff = a2 - a1;
        while (diff >  Math.PI) diff -= 2*Math.PI;
        while (diff < -Math.PI) diff += 2*Math.PI;
        return Math.Abs(diff) * 180.0 / Math.PI; // 0–180°, kein Flip
    }

    // Anzeigewert: spitzer Winkel (0–90°) — 0°=parallel, 90°=rechtwinklig.
    private static double VermArcSpanDeg(
        (double x, double y) p1, (double x, double y) p2,
        (double x, double y) q1, (double x, double y) q2)
    {
        double span = VermArcSpanActual(p1, p2, q1, q2);
        return span > 90.0 ? 180.0 - span : span;
    }

    // Schnittpunkt zweier Geraden (null wenn parallel)
    private static (double x, double y)? LinesIntersection(
        (double x, double y) p1, (double x, double y) p2,
        (double x, double y) q1, (double x, double y) q2)
    {
        double dx1 = p2.x - p1.x, dy1 = p2.y - p1.y;
        double dx2 = q2.x - q1.x, dy2 = q2.y - q1.y;
        double det = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(det) < 1e-12) return null;
        double t = ((q1.x - p1.x)*dy2 - (q1.y - p1.y)*dx2) / det;
        return (p1.x + t*dx1, p1.y + t*dy1);
    }

    // Winkel des Bogenstrichs für Winkelbemaßung: Richtung von inter zum "aktiven" Ende des Segments
    private static double VermSegArcAngle(
        (double x, double y) inter,
        (double x, double y) segP1, (double x, double y) segP2)
    {
        // Wähle die Richtung, die vom Schnittpunkt weg zeigt (zur Mitte des Segments)
        double mx = (segP1.x + segP2.x)/2 - inter.x;
        double my = (segP1.y + segP2.y)/2 - inter.y;
        if (Math.Sqrt(mx*mx + my*my) < 1e-9) mx = segP2.x - segP1.x;
        return Math.Atan2(my, mx);
    }

    // Mitte des kürzeren Bogens zwischen Winkel a1 und a2 (in Radiant)
    private static double VermArcMidAngle(double a1, double a2)
    {
        double diff = a2 - a1;
        while (diff > Math.PI)  diff -= 2*Math.PI;
        while (diff < -Math.PI) diff += 2*Math.PI;
        return a1 + diff / 2.0;
    }

    // Liegt clickAng innerhalb des kürzeren Bogens von a1 nach a2?
    private static bool VermAngleInArc(double clickAng, double a1, double a2)
    {
        double amid = VermArcMidAngle(a1, a2);
        // Check if click is within the arc half-angle
        double halfSpan = Math.Abs(a2 - a1) / 2.0;
        while (halfSpan > Math.PI) halfSpan -= Math.PI;
        double diffC = clickAng - amid;
        while (diffC >  Math.PI) diffC -= 2*Math.PI;
        while (diffC < -Math.PI) diffC += 2*Math.PI;
        return Math.Abs(diffC) <= halfSpan + 0.3; // 0.3 rad tolerance
    }

    // t-Parameter entlang Seg1 (P1→P2) für den Mausklick-Punkt berechnen.
    // Für Winkel-Masslinien wird dieser t-Wert als Offset gespeichert (statt Radius vom
    // Schnittpunkt), damit der Bogen bei fast-parallelen Linien stabil bleibt.
    private static double AngleTParam(double mmX, double mmY,
        (double x, double y) p1, (double x, double y) p2)
    {
        double dx = p2.x - p1.x, dy = p2.y - p1.y;
        double l2 = dx*dx + dy*dy;
        if (l2 < 1e-9) return 0.5;
        return ((mmX - p1.x)*dx + (mmY - p1.y)*dy) / l2;
    }

    // Bogradius = Abstand vom t-Punkt auf Seg1 zum Schnittpunkt.
    // Damit bleibt der Bogen immer an derselben Stelle auf Seg1 verankert.
    private static double AngleArcRadius(double t,
        (double x, double y) p1, (double x, double y) p2,
        (double x, double y) inter)
    {
        double px = p1.x + t * (p2.x - p1.x);
        double py = p1.y + t * (p2.y - p1.y);
        return Math.Max(Math.Sqrt(Math.Pow(px - inter.x, 2) + Math.Pow(py - inter.y, 2)), 1.0);
    }

    // Berechne neuen Drag-Offset für eine bestehende VermEntry
    private double VermComputeNewOffset(double mmX, double mmY, VermEntry en)
    {
        switch (en.Kind)
        {
            case VermKind.Length:
            {
                var a1 = GetPfadAbsAt(en.P1Idx);
                var a2 = GetPfadAbsAt(en.P2Idx);
                if (a1 == null || a2 == null) return en.Offset;
                return VermSignedOffset(mmX, mmY, a1.Value, a2.Value);
            }
            case VermKind.ParallelDist:
            {
                var p1 = GetPfadAbsAt(en.P1Idx);
                var p2 = GetPfadAbsAt(en.P2Idx);
                if (p1 == null || p2 == null) return en.Offset;
                double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
                double l2 = dx*dx + dy*dy;
                if (l2 < 1e-9) return en.Offset;
                return ((mmX - p1.Value.x)*dx + (mmY - p1.Value.y)*dy) / l2;
            }
            case VermKind.Angle:
            {
                var p1 = GetPfadAbsAt(en.P1Idx);
                var p2 = GetPfadAbsAt(en.P2Idx);
                if (p1 == null || p2 == null) return en.Offset;
                return AngleTParam(mmX, mmY, p1.Value, p2.Value);
            }
            case VermKind.EdgeDist:
            {
                var p2 = GetPfadAbsAt(en.P2Idx);
                if (p2 == null) return en.Offset;
                return (en.Edge == 1 || en.Edge == 2)
                    ? mmY - p2.Value.y
                    : mmX - p2.Value.x;
            }
            case VermKind.EdgeAngle:
            {
                var p1e = GetPfadAbsAt(en.P1Idx); var p2e = GetPfadAbsAt(en.P2Idx);
                if (p1e == null || p2e == null) return en.Offset;
                return AngleTParam(mmX, mmY, p1e.Value, p2e.Value);
            }
            case VermKind.KreisDurchmesser:
            case VermKind.KreisRadius:
            {
                var g = GetKreisGeom(en.P2Idx); if (g == null) return en.Offset;
                return Math.Atan2(mmY - g.Value.cy, mmX - g.Value.cx);
            }
            case VermKind.KreisKantenDist:
            {
                var g = GetKreisGeom(en.P2Idx); if (g == null) return en.Offset;
                return (en.Edge == 1 || en.Edge == 2) ? mmY - g.Value.cy : mmX - g.Value.cx;
            }
            case VermKind.RktSeite:
            {
                var g = GetRktSeiteGeom(en.P2Idx); if (g == null) return en.Offset;
                return VermSignedOffset(mmX, mmY, g.Value.a, g.Value.b);
            }
            case VermKind.RktKantenDist:
            {
                var m = RktSeiteMitte(en.P2Idx); if (m == null) return en.Offset;
                return (en.Edge == 1 || en.Edge == 2) ? mmY - m.Value.y : mmX - m.Value.x;
            }
            default: return en.Offset;
        }
    }

    // Konflikterkennung: Wird einer der zu bewegenden Punkte bereits durch eine
    // andere Masslinie als deren *bewegter* Endpunkt gebunden?
    // (Anker-/Referenzpunkte P1/Q1 zählen NICHT als Konflikt.)
    private bool VermHasConflict(int skipEntryIdx, VermKind newKind, params int[] movedIndices)
    {
        foreach (int mi in movedIndices)
        {
            if (mi < 0) continue;
            for (int i = 0; i < _vermPlaced.Count; i++)
            {
                if (i == skipEntryIdx) continue;
                var en = _vermPlaced[i];
                // Länge + Winkel am selben Punkt sind kompatibel:
                //   Länge verschiebt P2 entlang der Richtung → Winkel bleibt erhalten
                //   Winkel dreht Q2 um Q1 → Abstand (Länge) bleibt erhalten
                // Konflikt = zwei Constraints bewegen DENSELBEN Endpunkt.
                // Referenz-/Ankerpunkte (P1/P2 des ersten Segments bei Angle) sind frei beweglich:
                //   die Propagation zieht das zweite Segment automatisch nach.
                //
                // Kompatible Kombinationen (kein Konflikt):
                //   Length   + Angle/EdgeDist/EdgeAngle : verschiedene bewegte Punkte
                //   Angle    + Angle (andere Q):          PropagateVermConstraints löst die Kette
                //   Angle    + EdgeDist/EdgeAngle:        P-Segment darf verschoben werden
                //   EdgeDist + Length/Angle
                //   EdgeAngle + Length/Angle
                // Legende: "erlaubt" = kein Konflikt, auch wenn derselbe Punkt betroffen ist
                //
                //  EdgeAngle / ParallelEdge / PerpendicularEdge  →  Richtung relativ zur Werkstückkante
                //  Perpendicular / Parallel                       →  Winkel zwischen zwei Pfad-Segmenten
                //
                // Diese beiden Gruppen sind ORTHOGONAL: ein Segment kann gleichzeitig
                // „parallel zur unteren Kante" (EdgeAngle) und „rechtwinklig zum Nachbarsegment"
                // (Perpendicular) sein → kein Konflikt zwischen den Gruppen.
                //
                // Innerhalb einer Gruppe gilt: gleiche Eigenschaft zweimal setzen = Konflikt
                // (außer bei Ketten wie Angle+Angle oder Perp+Perp).
                // ── Globale Kurzschluss-Regel ───────────────────────────────────────
                // Richtungs-Constraints (Perpendicular / Parallel / ParallelEdge /
                // PerpendicularEdge) fixieren nur den Winkel eines Segments, nie dessen
                // Position oder Länge.  Reine Positions-/Längen-Constraints (Length,
                // EdgeDist, PointEdgeDist, Coincident, CoincidentCorner, ParallelDist)
                // fixieren Position/Länge, nie den Winkel.
                // → Diese beiden Gruppen sind orthogonal: kein Konflikt zwischen ihnen.
                bool newIsDir = newKind is VermKind.Perpendicular or VermKind.Parallel
                                or VermKind.ParallelEdge or VermKind.PerpendicularEdge;
                bool enIsPos  = en.Kind is VermKind.Length or VermKind.EdgeDist
                                or VermKind.PointEdgeDist or VermKind.Coincident
                                or VermKind.CoincidentCorner or VermKind.ParallelDist;
                if (newIsDir && enIsPos) continue;  // Richtung ↔ Position: immer OK

                bool clash = en.Kind switch
                {
                    VermKind.Length       => en.P2Idx == mi
                                            && newKind != VermKind.Angle
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.EdgeAngle
                                            && newKind != VermKind.PointEdgeDist,
                    VermKind.ParallelDist => en.Q1Idx == mi || en.Q2Idx == mi,
                    VermKind.Angle        => (en.Q1Idx == mi || en.Q2Idx == mi)
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.EdgeAngle
                                            && newKind != VermKind.PointEdgeDist
                                            && newKind != VermKind.Angle,
                    // EdgeDist fixiert NUR eine Achse (X oder Y abhängig von der Kante).
                    // Eine zweite EdgeDist kann die andere Achse fixieren → kein Konflikt.
                    // Ausserdem kompatibel mit Length (fixiert Länge, nicht Position) und
                    // EdgeAngle/PointEdgeDist (andere DOFs).
                    VermKind.EdgeDist     => en.P2Idx == mi
                                            && newKind != VermKind.Angle
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.EdgeAngle
                                            && newKind != VermKind.PointEdgeDist,
                    // EdgeAngle: absolute Richtung → erlaubt alle anderen Richtungs-Constraints
                    // sowie Positions-Constraints (EdgeDist/PointEdgeDist verschieben den
                    // Punkt entlang der bereits fixierten Richtung, kein Konflikt).
                    VermKind.EdgeAngle    => (en.P1Idx == mi || en.P2Idx == mi)
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.Angle
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.PointEdgeDist
                                            && newKind != VermKind.Perpendicular
                                            && newKind != VermKind.Parallel
                                            && newKind != VermKind.ParallelEdge
                                            && newKind != VermKind.PerpendicularEdge,
                    // PointEdgeDist: wie EdgeDist, aber mit Segment-Referenz
                    VermKind.PointEdgeDist => en.P2Idx == mi
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.Angle
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.EdgeAngle
                                            && newKind != VermKind.PointEdgeDist,
                    // Coincident: fixiert Positions-Gleichheit zweier Punkte, kein Winkelkonflikt.
                    // EdgeDist/PointEdgeDist (absolute Kanten-Position) sind erlaubt.
                    VermKind.Coincident       => en.P2Idx == mi
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.PointEdgeDist
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.EdgeAngle,
                    // CoincidentCorner: Punkt an Werkstück-Ecke → bereits voll fixiert.
                    // Weitere Constraints auf demselben Punkt erlauben (geometrisch redundant,
                    // aber kein echter Konflikt).
                    VermKind.CoincidentCorner => en.P2Idx == mi
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.PointEdgeDist
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.EdgeAngle,
                    // ParallelEdge / PerpendicularEdge: nur gleiche Gruppe blockieren
                    VermKind.ParallelEdge      => (en.P1Idx == mi || en.P2Idx == mi)
                                                && newKind != VermKind.Length
                                                && newKind != VermKind.Angle
                                                && newKind != VermKind.EdgeDist
                                                && newKind != VermKind.PointEdgeDist
                                                && newKind != VermKind.Perpendicular
                                                && newKind != VermKind.Parallel
                                                && newKind != VermKind.ParallelEdge
                                                && newKind != VermKind.PerpendicularEdge,
                    VermKind.PerpendicularEdge => (en.P1Idx == mi || en.P2Idx == mi)
                                                && newKind != VermKind.Length
                                                && newKind != VermKind.Angle
                                                && newKind != VermKind.EdgeDist
                                                && newKind != VermKind.PointEdgeDist
                                                && newKind != VermKind.Perpendicular
                                                && newKind != VermKind.Parallel
                                                && newKind != VermKind.ParallelEdge
                                                && newKind != VermKind.PerpendicularEdge,
                    // Perpendicular / Parallel: Q-Segment schützen, Richtungs-Constraints OK
                    VermKind.Perpendicular => (en.Q1Idx == mi || en.Q2Idx == mi)
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.EdgeAngle
                                            && newKind != VermKind.PointEdgeDist
                                            && newKind != VermKind.Angle
                                            && newKind != VermKind.Perpendicular
                                            && newKind != VermKind.Parallel
                                            && newKind != VermKind.ParallelEdge
                                            && newKind != VermKind.PerpendicularEdge,
                    VermKind.Parallel      => (en.Q1Idx == mi || en.Q2Idx == mi)
                                            && newKind != VermKind.Length
                                            && newKind != VermKind.EdgeDist
                                            && newKind != VermKind.EdgeAngle
                                            && newKind != VermKind.PointEdgeDist
                                            && newKind != VermKind.Angle
                                            && newKind != VermKind.Perpendicular
                                            && newKind != VermKind.Parallel
                                            && newKind != VermKind.ParallelEdge
                                            && newKind != VermKind.PerpendicularEdge,
                    _                     => false
                };
                if (clash) return true;
            }
        }
        return false;
    }

    private void PlaceVermassungAt(double mmX, double mmY)
    {
        _vermOffset      = VermSignedOffset(mmX, mmY, _vermP1Abs, _vermP2Abs);
        _vermActiveKind  = VermKind.Length;
        _vermState       = 2;
        double len       = Math.Round(VermSegmentLength(), 3);
        ShowVermTextBox(len, "F3");
    }

    // 3. Klick für Zwei-Segment-Modus: ParallelDist, Angle, EdgeDist, PointDist, LineToPoint positionieren
    private void PlaceTwoSegmentVermAt(double mmX, double mmY)
    {
        if (_vermActiveKind == VermKind.PointDist)
        {
            double dx = _vermP2Abs.x - _vermP1Abs.x, dy = _vermP2Abs.y - _vermP1Abs.y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            if (len < 1e-9) { _vermState = 0; return; }
            double nx = -dy/len, ny = dx/len;
            _vermOffset = (mmX - (_vermP1Abs.x + _vermP2Abs.x)/2) * nx
                        + (mmY - (_vermP1Abs.y + _vermP2Abs.y)/2) * ny;
            _vermState = 2;
            ShowVermTextBox(Math.Round(len, 3), "F3");
            return;
        }
        if (_vermActiveKind == VermKind.LineToPoint)
        {
            double dx1 = _vermP2Abs.x - _vermP1Abs.x, dy1 = _vermP2Abs.y - _vermP1Abs.y;
            double l2  = dx1*dx1 + dy1*dy1;
            double t   = l2 < 1e-9 ? 0.5
                : Math.Clamp(((mmX - _vermP1Abs.x)*dx1 + (mmY - _vermP1Abs.y)*dy1) / l2, -2.0, 3.0);
            _vermOffset = t;
            double l1  = Math.Sqrt(l2);
            double nx  = -dy1/l1, ny = dx1/l1;
            double dist = Math.Abs((_vermQ1Abs.x - _vermP1Abs.x)*nx + (_vermQ1Abs.y - _vermP1Abs.y)*ny);
            _vermState  = 2;
            ShowVermTextBox(Math.Round(dist, 3), "F3");
            return;
        }
        if ((_vermActiveKind == VermKind.EdgeDist || _vermActiveKind == VermKind.PointEdgeDist
             || _vermActiveKind == VermKind.KreisKantenDist || _vermActiveKind == VermKind.RktKantenDist)
            && _vermActiveEdge > 0)
        {
            // Offset = Versatz der Masslinie senkrecht zur Kante (Y für links/rechts, X für oben/unten)
            bool isHoriz = (_vermActiveEdge == 1 || _vermActiveEdge == 2);
            _vermOffset = isHoriz ? mmY - _vermP2Abs.y : mmX - _vermP2Abs.x;
            double dist = EdgeDistValue(_vermP2Abs.x, _vermP2Abs.y, _vermActiveEdge);
            _vermState  = 2;
            ShowVermTextBox(Math.Round(dist, 3), "F3");
            return;
        }
        if (_vermActiveKind == VermKind.EdgeAngle && _vermActiveEdge > 0)
        {
            var inter = SegmentEdgeIntersection(_vermP1Abs, _vermP2Abs, _vermActiveEdge);
            if (inter == null) { _vermState = 0; return; }
            _vermOffset = AngleTParam(mmX, mmY, _vermP1Abs, _vermP2Abs);
            var (e1, e2) = EdgeVirtualSegment(inter.Value, _vermActiveEdge);
            double ang = VermArcSpanActual(_vermP1Abs, _vermP2Abs, e1, e2);
            _vermState  = 2;
            ShowVermTextBox(Math.Round(ang, 2), "F2");
            return;
        }
        if (_vermActiveKind == VermKind.ParallelDist)
        {
            double dx1 = _vermP2Abs.x - _vermP1Abs.x, dy1 = _vermP2Abs.y - _vermP1Abs.y;
            double l2  = dx1*dx1 + dy1*dy1;
            double t   = l2 < 1e-9 ? 0.5
                : Math.Clamp(((mmX - _vermP1Abs.x)*dx1 + (mmY - _vermP1Abs.y)*dy1) / l2, -2.0, 3.0);
            _vermOffset = t;
            // Perpendicular distance
            double l1  = Math.Sqrt(l2);
            double nx  = -dy1/l1, ny = dx1/l1;
            double dist = Math.Abs(((_vermQ1Abs.x - _vermP1Abs.x)*nx + (_vermQ1Abs.y - _vermP1Abs.y)*ny));
            _vermState  = 2;
            ShowVermTextBox(Math.Round(dist, 3), "F3");
        }
        else // Angle
        {
            var inter = LinesIntersection(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs);
            if (inter == null) { _vermState = 0; return; }
            _vermOffset = AngleTParam(mmX, mmY, _vermP1Abs, _vermP2Abs);
            double ang  = VermArcSpanActual(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs);
            _vermState  = 2;
            ShowVermTextBox(Math.Round(ang, 2), "F2");
        }
    }

    private double VermSegmentLength()
    {
        double dx = _vermP2Abs.x - _vermP1Abs.x, dy = _vermP2Abs.y - _vermP1Abs.y;
        return Math.Sqrt(dx*dx + dy*dy);
    }

    private Point VermLabelScreenPos()
    {
        if (_vermActiveKind == VermKind.ParallelDist)
        {
            double dx1 = _vermP2Abs.x - _vermP1Abs.x, dy1 = _vermP2Abs.y - _vermP1Abs.y;
            double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1);
            if (l1 < 1e-9) return new Point(0, 0);
            double nx = -dy1/l1, ny = dx1/l1;
            double sD = (_vermQ1Abs.x - _vermP1Abs.x)*nx + (_vermQ1Abs.y - _vermP1Abs.y)*ny;
            double ax  = _vermP1Abs.x + _vermOffset * dx1, ay = _vermP1Abs.y + _vermOffset * dy1;
            double midX = ax + nx * sD / 2, midY = ay + ny * sD / 2;
            return new Point(midX * _zoom + _panX, (WorkY - midY) * _zoom + _panY);
        }
        else if (_vermActiveKind == VermKind.Angle)
        {
            var inter = LinesIntersection(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs);
            if (inter == null) return new Point(0, 0);
            double a1 = VermSegArcAngle(inter.Value, _vermP1Abs, _vermP2Abs);
            double a2 = VermSegArcAngle(inter.Value, _vermQ1Abs, _vermQ2Abs);
            double amid = VermArcMidAngle(a1, a2);
            double r = AngleArcRadius(_vermOffset, _vermP1Abs, _vermP2Abs, inter.Value);
            double mx = inter.Value.x + r * Math.Cos(amid);
            double my = inter.Value.y + r * Math.Sin(amid);
            return new Point(mx * _zoom + _panX, (WorkY - my) * _zoom + _panY);
        }
        else if (_vermActiveKind is VermKind.KreisDurchmesser or VermKind.KreisRadius)
        {
            var lp = KreisDimLabelPos(_vermP2Idx, _vermOffset);
            if (lp == null) return new Point(0, 0);
            return new Point(lp.Value.x * _zoom + _panX, (WorkY - lp.Value.y) * _zoom + _panY);
        }
        else if ((_vermActiveKind == VermKind.EdgeDist || _vermActiveKind == VermKind.PointEdgeDist
                  || _vermActiveKind == VermKind.KreisKantenDist || _vermActiveKind == VermKind.RktKantenDist)
                 && _vermActiveEdge > 0)
        {
            bool isHoriz = (_vermActiveEdge == 1 || _vermActiveEdge == 2);
            double xEdge = _vermActiveEdge == 1 ? 0 : WorkX;
            double yEdge = _vermActiveEdge == 3 ? 0 : WorkY;
            double midX, midY;
            if (isHoriz) { midX = (_vermP2Abs.x + xEdge) / 2; midY = _vermP2Abs.y + _vermOffset; }
            else         { midX = _vermP2Abs.x + _vermOffset; midY = (_vermP2Abs.y + yEdge) / 2; }
            return new Point(midX * _zoom + _panX, (WorkY - midY) * _zoom + _panY);
        }
        else
        {
            double dx = _vermP2Abs.x - _vermP1Abs.x, dy = _vermP2Abs.y - _vermP1Abs.y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            if (len < 1e-6) return new Point(0, 0);
            double nx = -dy/len, ny = dx/len;
            double midX = (_vermP1Abs.x + _vermP2Abs.x)/2 + nx * _vermOffset;
            double midY = (_vermP1Abs.y + _vermP2Abs.y)/2 + ny * _vermOffset;
            return new Point(midX * _zoom + _panX, (WorkY - midY) * _zoom + _panY);
        }
    }

    private void ShowVermTextBox(double value, string fmt = "F3")
    {
        CloseVermTextBox();
        _vermTextBox = new TextBox
        {
            Text              = value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
            Width             = 80,
            FontSize          = 13,
            Background        = System.Windows.Media.Brushes.White,
            BorderBrush       = new System.Windows.Media.SolidColorBrush(
                                    System.Windows.Media.Color.FromRgb(30, 120, 220)),
            BorderThickness   = new Thickness(2),
            Padding           = new Thickness(3, 2, 3, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _vermTextBox.KeyDown += OnVermTextBoxKeyDown;
        var screenPos = VermLabelScreenPos();
        System.Windows.Controls.Canvas.SetLeft(_vermTextBox, screenPos.X - 40);
        System.Windows.Controls.Canvas.SetTop (_vermTextBox, screenPos.Y - 28);
        VermOverlayCanvas.Children.Add(_vermTextBox);
        _vermTextBox.SelectAll();
        _vermTextBox.Focus();
    }

    private void CloseVermTextBox()
    {
        if (_vermTextBox == null) return;
        VermOverlayCanvas.Children.Remove(_vermTextBox);
        _vermTextBox = null;
    }

    private void OnVermTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string raw = _vermTextBox!.Text.Replace(',', '.');
            bool isAngleKind = _vermActiveKind == VermKind.Angle || _vermActiveKind == VermKind.EdgeAngle
                || (_vermState == 4 && _vermEditIdx >= 0
                    && (_vermPlaced[_vermEditIdx].Kind == VermKind.Angle
                     || _vermPlaced[_vermEditIdx].Kind == VermKind.EdgeAngle));
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, inv, out double newVal)
                && (isAngleKind ? newVal >= 0.0 : newVal > 0.001))
            {
                if (_vermState == 4 && _vermEditIdx >= 0)
                {
                    // Bestehende Masslinie bearbeiten
                    var en = _vermPlaced[_vermEditIdx];
                    bool conflict = en.Kind switch {
                        VermKind.Length       => VermHasConflict(_vermEditIdx, VermKind.Length, en.P2Idx),
                        VermKind.ParallelDist => VermHasConflict(_vermEditIdx, VermKind.ParallelDist, en.Q1Idx, en.Q2Idx),
                        VermKind.Angle        => VermHasConflict(_vermEditIdx, VermKind.Angle, en.Q1Idx, en.Q2Idx),
                        VermKind.EdgeDist      => VermHasConflict(_vermEditIdx, VermKind.EdgeDist, en.P2Idx),
                        VermKind.EdgeAngle     => VermHasConflict(_vermEditIdx, VermKind.EdgeAngle, en.P1Idx, en.P2Idx),
                        VermKind.PointEdgeDist => VermHasConflict(_vermEditIdx, VermKind.PointEdgeDist, en.P2Idx),
                        _                     => false
                    };
                    if (conflict)
                    {
                        MessageBox.Show(this,
                            "Ein oder mehrere referenzierte Punkte werden bereits durch eine andere Masslinie gebunden.\nDas Maß kann nicht geändert werden.",
                            "Konflikt", MessageBoxButton.OK, MessageBoxImage.Warning);
                        e.Handled = true; return;
                    }
                    // Winkel: Die Bearbeiten-Box zeigt jetzt immer den echten Winkel (0–180°,
                    // siehe ShowVermEditTextBox), daher ist die Eingabe bereits der gewünschte
                    // Zielwinkel selbst und wird ohne Umrechnung übernommen. (Vorher wurde hier
                    // stets auf den spitzen Winkel gefaltet zurückgerechnet, was bei einer
                    // Eingabe >90° den falschen Wert speicherte, z.B. aus 110° wurde 70°.)
                    double storeVal = newVal;
                    ApplyVermConstraint(_vermEditIdx, storeVal);
                    _vermPlaced[_vermEditIdx] = _vermPlaced[_vermEditIdx] with { Value = storeVal };
                    PropagateVermConstraints();
                    ShowVermDiagIfViolated();
                    CloseVermTextBox();
                    _vermState = 0; _vermEditIdx = -1;
                }
                else
                {
                    // Explizite Winkelbemaßung (Angle/EdgeAngle) auf einem Segment, das bereits
                    // eine automatisch erkannte Rechtwinklig/Parallel-zur-Kante-Markierung trägt
                    // (grünes Symbol), ersetzt diese Markierung, statt einen Konflikt zu melden —
                    // so kann der Nutzer das Segment bewusst schräg stellen.
                    // Bei EdgeAngle ist P1/P2 selbst das betroffene Segment (direkter Ersatz).
                    // Bei Angle (zwei Segmente) ist P1/P2 nur die feste Referenz, die von
                    // ApplyAngleConstraint NIE bewegt wird — dort muss stattdessen das
                    // Q-Segment geprüft werden, denn nur das wird tatsächlich gedreht und
                    // könnte mit dessen eigener Kanten-Markierung in Konflikt stehen.
                    if (_vermActiveKind == VermKind.EdgeAngle)
                    {
                        _vermPlaced.RemoveAll(en =>
                            (en.Kind == VermKind.PerpendicularEdge || en.Kind == VermKind.ParallelEdge)
                            && en.P1Idx == _vermP1Idx && en.P2Idx == _vermP2Idx);
                    }
                    else if (_vermActiveKind == VermKind.Angle)
                    {
                        _vermPlaced.RemoveAll(en =>
                            (en.Kind == VermKind.PerpendicularEdge || en.Kind == VermKind.ParallelEdge)
                            && en.P1Idx == _vermQ1Idx && en.P2Idx == _vermQ2Idx);
                    }
                    // Neue Masslinie bestätigen
                    bool conflict = _vermActiveKind switch {
                        VermKind.Length       => VermHasConflict(-1, VermKind.Length, _vermP2Idx),
                        VermKind.ParallelDist => VermHasConflict(-1, VermKind.ParallelDist, _vermQ1Idx, _vermQ2Idx),
                        VermKind.Angle        => VermHasConflict(-1, VermKind.Angle, _vermQ1Idx, _vermQ2Idx),
                        VermKind.EdgeDist      => VermHasConflict(-1, VermKind.EdgeDist, _vermP2Idx),
                        VermKind.EdgeAngle     => VermHasConflict(-1, VermKind.EdgeAngle, _vermP1Idx, _vermP2Idx),
                        VermKind.PointEdgeDist => VermHasConflict(-1, VermKind.PointEdgeDist, _vermP2Idx),
                        _                     => false
                    };
                    if (conflict)
                    {
                        MessageBox.Show(this,
                            "Ein oder mehrere referenzierte Punkte werden bereits durch eine andere Masslinie gebunden.\nDie Masslinie kann nicht hinzugefügt werden.",
                            "Konflikt", MessageBoxButton.OK, MessageBoxImage.Warning);
                        e.Handled = true; return;
                    }
                    // Werte vor Apply einfrieren (Apply verändert History)
                    double eDirX = 0, eDirY = 0;
                    if (_vermActiveKind is VermKind.Length or VermKind.PointDist
                        && _vermP1Idx >= 0 && _vermP2Idx >= 0)
                    {
                        var ep1 = GetPfadAbsAt(_vermP1Idx); var ep2 = GetPfadAbsAt(_vermP2Idx);
                        if (ep1 != null && ep2 != null)
                        {
                            double edx = ep2.Value.x - ep1.Value.x, edy = ep2.Value.y - ep1.Value.y;
                            double eLen = Math.Sqrt(edx*edx + edy*edy);
                            if (eLen > 1e-9) { eDirX = edx/eLen; eDirY = edy/eLen; }
                        }
                    }
                    var newEntry = new VermEntry(_vermActiveKind,
                        _vermP1Idx, _vermP2Idx, _vermOffset, newVal,
                        _vermQ1Idx, _vermQ2Idx, _vermActiveEdge, eDirX, eDirY);
                    ApplyVermNewEntry(newEntry, newVal);
                    _vermPlaced.Add(newEntry with { Value = newVal });
                    PropagateVermConstraints();
                    ShowVermDiagIfViolated();
                    CloseVermTextBox();
                    _vermState = 0; _vermP1Idx = -1; _vermQ1Idx = -1; _vermActiveEdge = 0; _vermPtIdx = -1;
                    _vermKreisIdx = -1; _vermRktIdx = -1;
                }
            }
            else
            {
                CloseVermTextBox();
                _vermState = 0; _vermEditIdx = -1; _vermP1Idx = -1; _vermQ1Idx = -1; _vermActiveEdge = 0;
                _vermKreisIdx = -1; _vermRktIdx = -1;
            }
            DrawSkia?.InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseVermTextBox();
            _vermEditIdx = -1;
            _vermState   = _vermState == 4 ? 0 : (_vermActiveKind == VermKind.ParallelDist || _vermActiveKind == VermKind.Angle
                                                  || _vermActiveKind == VermKind.KreisKantenDist
                                                  || _vermActiveKind == VermKind.RktKantenDist ? 5 : 1);
            DrawSkia?.InvalidateVisual();
            e.Handled = true;
        }
    }

    // Wendet eine neue VermEntry auf die Geometrie an (neue Masslinie)
    private void ApplyVermNewEntry(VermEntry en, double newVal)
    {
        switch (en.Kind)
        {
            case VermKind.Length:
                ApplyLengthConstraint(en.P1Idx, en.P2Idx, newVal, en.DirX, en.DirY);
                break;
            case VermKind.ParallelDist:
                ApplyParallelDistConstraint(en.P1Idx, en.P2Idx, en.Q1Idx, en.Q2Idx, newVal);
                break;
            case VermKind.Angle:
                ApplyAngleConstraint(en.P1Idx, en.P2Idx, en.Q1Idx, en.Q2Idx, newVal);
                break;
            case VermKind.EdgeDist:
                ApplyEdgeDistConstraint(en.P1Idx, en.P2Idx, en.Edge, newVal, en);
                break;
            case VermKind.EdgeAngle:
                ApplyEdgeAngleConstraint(en.P1Idx, en.P2Idx, en.Edge, newVal);
                break;
            case VermKind.PointDist:
                ApplyLengthConstraint(en.P1Idx, en.P2Idx, newVal, en.DirX, en.DirY);  // verschiebt P2 entlang gespeicherter Richtung
                break;
            case VermKind.LineToPoint:
                ApplyLineToPointConstraint(en.P1Idx, en.P2Idx, en.Q1Idx, newVal);
                break;
            case VermKind.PointEdgeDist:
                ApplyPointEdgeDistConstraint(en.P2Idx, en.Edge, newVal, en);
                break;
            case VermKind.ParallelEdge:
                ApplyEdgeAngleConstraint(en.P1Idx, en.P2Idx, en.Edge, 0.0);
                break;
            case VermKind.PerpendicularEdge:
                ApplyEdgeAngleConstraint(en.P1Idx, en.P2Idx, en.Edge, 90.0);
                break;
            case VermKind.Coincident:
                ApplyCoincidentConstraint(en.P1Idx, en.P2Idx);
                break;
            case VermKind.CoincidentCorner:
                ApplyCoincidentCornerConstraint(en.P2Idx, en.Edge);
                break;
            case VermKind.Perpendicular:
                ApplyAngleConstraint(en.P1Idx, en.P2Idx, en.Q1Idx, en.Q2Idx, 90.0);
                break;
            case VermKind.Parallel:
                ApplyAngleConstraint(en.P1Idx, en.P2Idx, en.Q1Idx, en.Q2Idx, 0.0);
                break;
            case VermKind.KreisDurchmesser:
            case VermKind.KreisRadius:
            case VermKind.KreisKantenDist:
                ApplyKreisVerm(en, newVal);
                break;
            case VermKind.RktSeite:
            case VermKind.RktKantenDist:
                ApplyRktVerm(en, newVal);
                break;
        }
    }

    // Bereinigt _vermPlaced nach dem Entfernen eines History-Eintrags bei removedIdx.
    // Entfernt alle Masslinien die den gelöschten Index referenzieren,
    // und dekrementiert alle Indizes > removedIdx.
    private void CleanupVermAfterRemove(int removedIdx)
    {
        _vermPlaced.RemoveAll(en =>
            en.P1Idx == removedIdx || en.P2Idx == removedIdx ||
            (en.P2Idx >= RlbAnkerOffset && en.P2Idx % RlbAnkerOffset == removedIdx) ||
            en.Q1Idx == removedIdx || en.Q2Idx == removedIdx);

        for (int i = 0; i < _vermPlaced.Count; i++)
        {
            var en = _vermPlaced[i];
            int Adj(int idx) => idx >= RlbAnkerOffset
                ? (idx % RlbAnkerOffset > removedIdx ? idx - 1 : idx)
                : (idx > removedIdx ? idx - 1 : idx);
            _vermPlaced[i] = en with {
                P1Idx = Adj(en.P1Idx),
                P2Idx = Adj(en.P2Idx),
                Q1Idx = en.Q1Idx >= 0 ? Adj(en.Q1Idx) : -1,
                Q2Idx = en.Q2Idx >= 0 ? Adj(en.Q2Idx) : -1,
            };
        }
    }

    // Wendet eine bestehende VermEntry (bei Bearbeitung) auf die Geometrie an
    private void ApplyVermConstraint(int idx, double newVal)
    {
        var en = _vermPlaced[idx];
        ApplyVermNewEntry(en, newVal);
    }

    // Nach dem Anwenden eines Masses alle anderen Masse iterativ erneut durchsetzen,
    // damit kein bestehendes Mass durch die Änderung verletzt wird.
    // Constraints anwenden ohne G-Code neu zu generieren (für Live-Drag)
    private void PropagateVermConstraintsLive(int maxIter = 25)
    {
        if (_vermPlaced.Count < 1) return;
        _suppressHistoryRegen = true;
        try
        {
            // Punkte, die über EdgeDist/PointEdgeDist an eine Werkstückkante gebunden sind,
            // sollen sich rein rechtwinklig zur Kante bewegen — auch wenn derselbe Punkt
            // zusätzlich über Länge/Winkel an eine andere Linie gebunden ist. Ohne diese
            // Priorisierung "kämpfen" beide Masse bei jeder Iteration gegeneinander (Winkel/
            // Länge dreht bzw. verschiebt den Punkt wieder von der Senkrechten weg, Kantenmass
            // korrigiert ihn zurück), wodurch der Punkt am Ende schräg statt senkrecht zur
            // Kante zu liegen kommt. Das Kantenmass hat daher Vorrang: Länge/Winkel wird für
            // den betroffenen Punkt während der Propagation nicht mehr aktiv durchgesetzt.
            // Ausnahme: Parallel/Rechtwinklig-Eigenschaften (fix auf 0°/90°) werden NIE
            // übersteuert — anders als Länge/Winkel können sie sich nicht auf den aktuellen Wert
            // "nachziehen" (siehe Zeichen-Code, der bei Length/Angle/ParallelDist einfach den
            // gespeicherten Wert an die aktuelle Geometrie anpasst). Ohne feste Priorität würde
            // eine später hinzugefügte Kanten-/Längenbemaßung eine bereits als parallel markierte
            // Linie schräg ziehen können, ohne dass sie je zurückkorrigiert wird.
            // Hinweis: Length/PointDist werden hier NICHT mehr pauschal übersprungen, wenn ihr
            // Zielpunkt kantengebunden ist — ApplyLengthConstraint entscheidet inzwischen selbst
            // (via GetAxisFixedPoints/IsAxisFixed), welcher der beiden Endpunkte bei einem echten
            // Achsen-Konflikt bewegt werden muss, und verschiebt in dem Fall automatisch den
            // ANDEREN (nicht kantenfixierten) Punkt. Ein pauschales Überspringen hier würde die
            // Längen-Vermassung stattdessen komplett wirkungslos machen, obwohl sie durchaus
            // korrekt (am jeweils freien Punkt) durchsetzbar wäre.
            var edgeLocked = new HashSet<int>();
            foreach (var e in _vermPlaced)
            {
                if (e.Kind == VermKind.EdgeDist) { edgeLocked.Add(e.P1Idx); edgeLocked.Add(e.P2Idx); }
                else if (e.Kind == VermKind.PointEdgeDist) edgeLocked.Add(e.P2Idx);
            }

            bool IsEdgeOverridden(VermEntry e)
            {
                switch (e.Kind)
                {
                    case VermKind.Angle:
                    case VermKind.ParallelDist:
                    case VermKind.LineToPoint:
                        return edgeLocked.Contains(e.Q1Idx) || edgeLocked.Contains(e.Q2Idx);
                    case VermKind.EdgeAngle:
                        return edgeLocked.Contains(e.P1Idx) || edgeLocked.Contains(e.P2Idx);
                    default:
                        return false;
                }
            }

            bool IsDirectionConstraint(VermKind k) => k is VermKind.ParallelEdge or VermKind.PerpendicularEdge
                                                          or VermKind.Parallel     or VermKind.Perpendicular;

            var ordered = _vermPlaced
                .OrderBy(e => IsDirectionConstraint(e.Kind) ? 1 : 0)
                .ThenBy(e => Math.Min(e.P1Idx < 0 ? int.MaxValue : e.P1Idx,
                                      e.Q1Idx < 0 ? int.MaxValue : e.Q1Idx))
                .ToList();
            for (int iter = 0; iter < maxIter; iter++)
                foreach (var en in ordered)
                {
                    // Kreis-Bemassungen sind unabhängig von Pfad-Constraints und werden nur beim
                    // Eingeben direkt angewendet — ihr Wert folgt sonst der Kreis-Geometrie
                    // (z.B. nach Verschieben/Ändern des Kreises).
                    if (IsKreisVermKind(en.Kind) || IsRktVermKind(en.Kind)) continue;
                    if (!IsDirectionConstraint(en.Kind) && IsEdgeOverridden(en)) continue;
                    ApplyVermNewEntry(en, en.Value);
                }
        }
        finally { _suppressHistoryRegen = false; }
    }

    private void PropagateVermConstraints(int maxIter = 25)
    {
        if (_vermPlaced.Count < 1) return;
        PropagateVermConstraintsLive(maxIter);
        RegenerateGCodeFromHistory();
    }

    // Prüft ob alle Constraints innerhalb der Toleranz eingehalten werden.
    // Gibt null zurück wenn alles stimmt, sonst eine Fehlerbeschreibung.
    private string? VerifyVermConstraints()
    {
        const double tolMm  = 0.05;   // mm
        const double tolDeg = 0.2;    // Grad

        foreach (var en in _vermPlaced)
        {
            var p1 = GetPfadAbsAt(en.P1Idx);
            var p2 = GetPfadAbsAt(en.P2Idx);

            switch (en.Kind)
            {
                case VermKind.Length:
                case VermKind.PointDist:
                {
                    if (p1 == null || p2 == null) break;
                    double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
                    double dist = Math.Sqrt(dx*dx + dy*dy);
                    if (Math.Abs(dist - en.Value) > tolMm)
                        return $"Länge: Ist {dist:F2} mm, Soll {en.Value:F2} mm (P1={en.P1Idx}, P2={en.P2Idx}, DirX={en.DirX:F3}, DirY={en.DirY:F3})";
                    break;
                }
                case VermKind.Angle:
                case VermKind.Perpendicular:
                case VermKind.Parallel:
                {
                    if (p1 == null || p2 == null) break;
                    var q1 = GetPfadAbsAt(en.Q1Idx); var q2 = GetPfadAbsAt(en.Q2Idx);
                    if (q1 == null || q2 == null) break;
                    double actual = VermArcSpanActual(p1.Value, p2.Value, q1.Value, q2.Value);
                    double display = actual > 90.0 ? 180.0 - actual : actual;
                    double expected = en.Value > 90.0 ? 180.0 - en.Value : en.Value;
                    if (Math.Abs(display - expected) > tolDeg)
                        return $"{(en.Kind == VermKind.Perpendicular ? "Rechtwinklig" : en.Kind == VermKind.Parallel ? "Parallel" : "Winkel")}: "
                             + $"Ist {display:F1}°, Soll {expected:F1}°";
                    break;
                }
                case VermKind.EdgeDist:
                {
                    if (p2 == null) break;
                    double actual = EdgeDistValue(p2.Value.x, p2.Value.y, en.Edge);
                    if (Math.Abs(actual - en.Value) > tolMm)
                        return $"Kantendistanz: Ist {actual:F2} mm, Soll {en.Value:F2} mm";
                    break;
                }
                case VermKind.PointEdgeDist:
                {
                    if (p2 == null) break;
                    double actual = EdgeDistValue(p2.Value.x, p2.Value.y, en.Edge);
                    if (Math.Abs(actual - en.Value) > tolMm)
                        return $"Punkt-Kantendistanz: Ist {actual:F2} mm, Soll {en.Value:F2} mm";
                    break;
                }
                case VermKind.Coincident:
                {
                    if (p1 == null || p2 == null) break;
                    double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
                    if (Math.Sqrt(dx*dx + dy*dy) > tolMm)
                        return "Koinzident: Punkte nicht mehr am gleichen Ort";
                    break;
                }
                case VermKind.CoincidentCorner:
                {
                    if (p2 == null) break;
                    var (cpx, cpy) = WorkpieceCornerPos(en.Edge);
                    double dx = p2.Value.x - cpx, dy = p2.Value.y - cpy;
                    if (Math.Sqrt(dx*dx + dy*dy) > tolMm)
                        return "Koinzident zur Ecke: Punkt nicht mehr an Werkstück-Ecke";
                    break;
                }
                case VermKind.ParallelEdge:
                case VermKind.PerpendicularEdge:
                {
                    if (p1 == null || p2 == null) break;
                    // Direkt über die Richtungsvektoren prüfen statt über den Schnittpunkt mit
                    // der (verlängerten) Kante: ist das Segment bereits (fast) parallel bzw.
                    // rechtwinklig zur Kante — genau der Zustand, den wir hier verifizieren —
                    // liegt dieser Schnittpunkt extrem weit entfernt bzw. gar nicht vor, was die
                    // darauf basierende Winkelberechnung numerisch instabil macht und einen
                    // falschen Abweichungswert liefern kann, obwohl das Segment korrekt
                    // ausgerichtet ist.
                    bool isVerticalEdge = en.Edge == 1 || en.Edge == 2;
                    var edgeQ1 = (0.0, 0.0);
                    var edgeQ2 = isVerticalEdge ? (0.0, 1.0) : (1.0, 0.0);
                    double display = AngleBetweenSegments(p1.Value, p2.Value, edgeQ1, edgeQ2);
                    double expected = en.Kind == VermKind.ParallelEdge ? 0.0 : 90.0;
                    if (Math.Abs(display - expected) > tolDeg)
                        return $"{(en.Kind == VermKind.ParallelEdge ? "Parallel zur Kante" : "Rechtwinklig zur Kante")}: "
                             + $"Ist {display:F1}°, Soll {expected:F1}°";
                    break;
                }
            }
        }
        return null;
    }

    private void CheckAndReportConstraints()
    {
        string? err = VerifyVermConstraints();
        if (err != null)
            MessageBox.Show(this, "Constraint kann nicht eingehalten werden:\n" + err + "\n\n" + VermDiagDump(),
                "Constraint-Konflikt", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // TEMPORÄRE Diagnose-Hilfsfunktion: zeigt bei einer Constraint-Verletzung sofort einen
    // vollständigen Dump aller platzierten Vermassungen + aller aktuellen Pfad-Punktkoordinaten,
    // damit sich Ursachen ohne erneutes Rätselraten anhand von Nutzer-Screenshots lokalisieren
    // lassen. Nach vollständiger Behebung der zugrunde liegenden Constraint-Solver-Bugs entfernen.
    private string VermDiagDump()
    {
        var dump = string.Join("\n", _vermPlaced.Select((e2, i2) =>
            $"[{i2}] {e2.Kind} P1={e2.P1Idx} P2={e2.P2Idx} Q1={e2.Q1Idx} Q2={e2.Q2Idx} Edge={e2.Edge} Val={e2.Value:F2} Dir=({e2.DirX:F3},{e2.DirY:F3})"));
        var pts = new List<string>();
        for (int i = 0; i < _history.Count; i++)
        {
            var a = GetPfadAbsAt(i);
            if (a != null) pts.Add($"pt{i}=({a.Value.x:F2},{a.Value.y:F2})");
        }
        return dump + "\n\n" + string.Join(" ", pts);
    }

    private void ShowVermDiagIfViolated()
    {
        string? diag = VerifyVermConstraints();
        if (diag != null)
            MessageBox.Show(this, "DIAG: " + diag + "\n\n" + VermDiagDump(), "Diagnose",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Gibt die Menge aller History-Indizes zurück, deren Position vollständig
    // durch Constraints festgelegt ist (X und Y je fixiert).
    private HashSet<int> GetFullyConstrainedPoints()
    {
        if (_vermPlaced.Count == 0) return [];

        var xFixed = new HashSet<int>();
        var yFixed = new HashSet<int>();

        // Direkte Positions-Constraints
        foreach (var en in _vermPlaced)
        {
            switch (en.Kind)
            {
                case VermKind.CoincidentCorner:
                    xFixed.Add(en.P2Idx);
                    yFixed.Add(en.P2Idx);
                    break;
                case VermKind.PointEdgeDist:
                case VermKind.EdgeDist:
                    // Kante 1=links, 2=rechts → X; Kante 3=unten, 4=oben → Y
                    if (en.Edge == 1 || en.Edge == 2) xFixed.Add(en.P2Idx);
                    else if (en.Edge == 3 || en.Edge == 4) yFixed.Add(en.P2Idx);
                    break;
            }
        }

        // Segmente mit bekannter Richtung (EdgeAngle / ParallelEdge / PerpendicularEdge)
        // und bekannter Länge (Length) → wenn ein Endpunkt 2D-fixiert ist, ist der andere auch fixiert.
        var segDirFixed = new HashSet<(int, int)>();
        var segLenFixed = new HashSet<(int, int)>();
        foreach (var en in _vermPlaced)
        {
            switch (en.Kind)
            {
                case VermKind.EdgeAngle:
                case VermKind.ParallelEdge:
                case VermKind.PerpendicularEdge:
                    segDirFixed.Add((en.P1Idx, en.P2Idx));
                    segDirFixed.Add((en.P2Idx, en.P1Idx));
                    break;
                case VermKind.Length:
                    segLenFixed.Add((en.P1Idx, en.P2Idx));
                    segLenFixed.Add((en.P2Idx, en.P1Idx));
                    break;
            }
        }
        // Schnittmenge: Segmente die sowohl Richtung als auch Länge kennen
        var segBothFixed = new HashSet<(int, int)>(segDirFixed);
        segBothFixed.IntersectWith(segLenFixed);

        // Iterative Ausbreitung
        bool changed = true;
        while (changed)
        {
            changed = false;

            // Coincident: Ankerpunkt-Fixierung auf verschobenen Punkt übertragen
            foreach (var en in _vermPlaced)
            {
                if (en.Kind != VermKind.Coincident) continue;
                if (xFixed.Contains(en.P1Idx) && xFixed.Add(en.P2Idx)) changed = true;
                if (yFixed.Contains(en.P1Idx) && yFixed.Add(en.P2Idx)) changed = true;
            }

            // Segment mit Richtung+Länge: ein fixierter Endpunkt fixiert den anderen
            foreach (var (a, b) in segBothFixed)
            {
                bool aFix = xFixed.Contains(a) && yFixed.Contains(a);
                bool bFix = xFixed.Contains(b) && yFixed.Contains(b);
                if (aFix && !bFix)
                {
                    if (xFixed.Add(b)) changed = true;
                    if (yFixed.Add(b)) changed = true;
                }
                else if (bFix && !aFix)
                {
                    if (xFixed.Add(a)) changed = true;
                    if (yFixed.Add(a)) changed = true;
                }
            }
        }

        // Nur Punkte, die in X und Y fixiert sind
        var result = new HashSet<int>(xFixed);
        result.IntersectWith(yFixed);
        return result;
    }

    // Achsen-weise (statt nur vollständige 2D-) Positions-Fixierung: X bzw. Y eines Punktes
    // gilt als fixiert, wenn er direkt über EdgeDist/PointEdgeDist/CoincidentCorner an eine
    // Kante gebunden ist, ODER wenn er über eine garantiert achsparallele/-senkrechte
    // Segment-Verbindung (ParallelEdge/PerpendicularEdge — OHNE dass dafür zusätzlich die
    // Länge bekannt sein müsste) starr mit einem bereits auf dieser Achse fixierten Punkt
    // verbunden ist (z.B. eine vertikale Linie: beide Endpunkte teilen dieselbe X-Position).
    // Ohne diese Weiterleitung "sieht" eine Length-Vermassung auf einer benachbarten Linie
    // nicht, dass ihr Zielpunkt indirekt schon über eine andere Kanten-Vermassung ortsfest
    // ist, und verschiebt ihn trotzdem — was die eigentlich fixierte Kantendistanz am
    // anderen Ende der starren Linie verändert.
    private (HashSet<int> xFixed, HashSet<int> yFixed) GetAxisFixedPoints()
    {
        var xFixed = new HashSet<int>();
        var yFixed = new HashSet<int>();
        foreach (var en in _vermPlaced)
        {
            switch (en.Kind)
            {
                case VermKind.CoincidentCorner:
                    xFixed.Add(en.P2Idx); yFixed.Add(en.P2Idx);
                    break;
                case VermKind.PointEdgeDist:
                case VermKind.EdgeDist:
                    if (en.Edge == 1 || en.Edge == 2) xFixed.Add(en.P2Idx);
                    else if (en.Edge == 3 || en.Edge == 4) yFixed.Add(en.P2Idx);
                    break;
            }
        }

        var sameX = new List<(int, int)>();
        var sameY = new List<(int, int)>();
        foreach (var en in _vermPlaced)
        {
            bool vertEdge = en.Edge == 1 || en.Edge == 2;
            bool horizEdge = en.Edge == 3 || en.Edge == 4;
            if (en.Kind == VermKind.ParallelEdge)
            {
                if (vertEdge) sameX.Add((en.P1Idx, en.P2Idx));
                else if (horizEdge) sameY.Add((en.P1Idx, en.P2Idx));
            }
            else if (en.Kind == VermKind.PerpendicularEdge)
            {
                if (vertEdge) sameY.Add((en.P1Idx, en.P2Idx));
                else if (horizEdge) sameX.Add((en.P1Idx, en.P2Idx));
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (a, b) in sameX)
            {
                if (xFixed.Contains(a) && xFixed.Add(b)) changed = true;
                if (xFixed.Contains(b) && xFixed.Add(a)) changed = true;
            }
            foreach (var (a, b) in sameY)
            {
                if (yFixed.Contains(a) && yFixed.Add(b)) changed = true;
                if (yFixed.Contains(b) && yFixed.Add(a)) changed = true;
            }
            foreach (var en in _vermPlaced)
            {
                if (en.Kind != VermKind.Coincident) continue;
                if (xFixed.Contains(en.P1Idx) && xFixed.Add(en.P2Idx)) changed = true;
                if (xFixed.Contains(en.P2Idx) && xFixed.Add(en.P1Idx)) changed = true;
                if (yFixed.Contains(en.P1Idx) && yFixed.Add(en.P2Idx)) changed = true;
                if (yFixed.Contains(en.P2Idx) && yFixed.Add(en.P1Idx)) changed = true;
            }
        }
        return (xFixed, yFixed);
    }

    // Wie set.Contains(pt), aber unter Berücksichtigung des geschlossene-Pfad-Zwillingsindex
    // (siehe SamePathCorner) — pt und sein Zwilling sind physisch derselbe Punkt.
    private bool IsAxisFixed(int pt, HashSet<int> set)
    {
        if (set.Contains(pt)) return true;
        var (st, en) = FindChainBounds(pt);
        if (st != en && IsChainClosed(st, en))
        {
            int partner = pt == st ? en : (pt == en ? st : -1);
            if (partner >= 0 && set.Contains(partner)) return true;
        }
        return false;
    }

    // True wenn die durch (a,b) definierte Linie dieselbe Pfad-Linie ist wie (p1,p2) —
    // unabhängig von der Reihenfolge der Endpunkte und unter Berücksichtigung von
    // SamePathCorner (geschlossene-Pfad-Zwillingsindizes).
    private bool SegmentIsSameLine(int a, int b, int p1, int p2)
    {
        if (a < 0 || b < 0) return false;
        return (SamePathCorner(a, p1) && SamePathCorner(b, p2)) ||
               (SamePathCorner(a, p2) && SamePathCorner(b, p1));
    }

    // True wenn eine ANDERE platzierte Vermassung die Richtung des Segments (p1Idx,p2Idx)
    // aktiv vorgibt (Angle/Perpendicular/Parallel als deren gedrehtes Q-Segment, oder
    // EdgeAngle/ParallelEdge/PerpendicularEdge direkt). In diesem Fall darf eine Length-
    // Vermassung auf demselben Segment NICHT ihre beim Anlegen eingefrorene Richtung
    // (DirX/DirY) erzwingen — sonst kämpfen beide Constraints bei jeder Propagation
    // gegeneinander: die Winkel-Vermassung dreht die Linie auf den Soll-Winkel, die
    // Length-Vermassung dreht sie im selben Durchlauf wieder auf die alte, beim Anlegen
    // gültige Richtung zurück (Symptom: Winkel-Vermassung nach der Länge hinzugefügt
    // wirkt nicht / Werte driften bei jeder Propagation).
    private bool SegmentHasDirectionConstraint(int p1Idx, int p2Idx)
    {
        foreach (var en in _vermPlaced)
        {
            switch (en.Kind)
            {
                case VermKind.Angle:
                case VermKind.Perpendicular:
                case VermKind.Parallel:
                    if (SegmentIsSameLine(en.Q1Idx, en.Q2Idx, p1Idx, p2Idx)) return true;
                    break;
                case VermKind.EdgeAngle:
                case VermKind.ParallelEdge:
                case VermKind.PerpendicularEdge:
                    if (SegmentIsSameLine(en.P1Idx, en.P2Idx, p1Idx, p2Idx)) return true;
                    break;
            }
        }
        return false;
    }

    private void ApplyLengthConstraint(int p1Idx, int p2Idx, double newLen, double dirX = 0, double dirY = 0)
    {
        var p1 = GetPfadAbsAt(p1Idx); var p2v = GetPfadAbsAt(p2Idx);
        if (p1 == null || p2v == null) return;
        double dx, dy;
        if ((Math.Abs(dirX) > 1e-9 || Math.Abs(dirY) > 1e-9) && !SegmentHasDirectionConstraint(p1Idx, p2Idx)
            && !IsPositionDrivenByAngle(p1Idx) && !IsPositionDrivenByAngle(p2Idx))
        {
            // Gespeicherten Normalvektor verwenden → Winkel bleibt immer erhalten,
            // solange keine andere Vermassung die Richtung dieses Segments aktiv vorgibt.
            dx = dirX; dy = dirY;
        }
        else
        {
            dx = p2v.Value.x - p1.Value.x; dy = p2v.Value.y - p1.Value.y;
            double curLen = Math.Sqrt(dx*dx + dy*dy);
            if (curLen < 1e-9) return;
            dx /= curLen; dy /= curLen;
        }
        // Normalerweise bleibt P1 fix und P2 wird verschoben. Zwei Gründe können das umkehren:
        //
        // 1. Echte Achsen-Fixierung (Vorrang): Ist P2 über eine Kanten-Vermassung — direkt
        //    oder indirekt über eine starre achsparallele/-senkrechte Linie (GetAxisFixedPoints)
        //    — auf genau der Achse fixiert, die newLen entlang der aktuellen Richtung ändern
        //    würde, MUSS stattdessen P1 verschoben werden — alles andere verletzt die Kanten-
        //    Vermassung. Bsp.: die untere Linie einer Kontur, deren rechter Eckpunkt über eine
        //    starre vertikale Linie an eine kantenbemasste obere Ecke gekoppelt ist — beim Ändern
        //    der unteren Länge muss der LINKE Punkt wandern, nicht der (indirekt) kantenfixierte.
        // 2. Ist P2 der Start- oder Endpunkt einer geschlossenen Kette (IsClosedChainEndpoint),
        //    würde das Verschieben über den "Partner-Punkt"-Mechanismus in UpdatePfadPunktPos
        //    unbemerkt auch den jeweils anderen (spatial identischen) Index mitverschieben —
        //    den eigentlichen Anker des Pfades. Nur als Fallback, wenn Fall 1 nicht eindeutig
        //    entscheidet (keiner der beiden Punkte ist achsen-fixiert).
        var (axisXFixed, axisYFixed) = GetAxisFixedPoints();
        bool AxisConflict(int pt) => (IsAxisFixed(pt, axisXFixed) && Math.Abs(dx) > 1e-6) ||
                                      (IsAxisFixed(pt, axisYFixed) && Math.Abs(dy) > 1e-6);
        bool p1AxisFixed = AxisConflict(p1Idx);
        bool p2AxisFixed = AxisConflict(p2Idx);
        bool p2IsAnchor, p1IsAnchor;
        if (p2AxisFixed != p1AxisFixed)
        {
            p2IsAnchor = p2AxisFixed;
            p1IsAnchor = p1AxisFixed;
        }
        else
        {
            p2IsAnchor = IsClosedChainEndpoint(p2Idx);
            p1IsAnchor = IsClosedChainEndpoint(p1Idx);
        }

        // Sonderfall: Der Punkt, den die normale Anker-Logik gerade bewegen würde, ist in
        // Wirklichkeit durch eine WINKEL-Bemassung auf einem anderen (angrenzenden) Segment
        // bereits vollständig und eindeutig positioniert (z.B. ein Eckpunkt, dessen Lage sich
        // aus einer Winkel- + Längen-Bemassung der angrenzenden schrägen Linie ergibt). Er darf
        // dann NICHT erneut verschoben werden — sonst reißt die Längen-Bemassung diese Position
        // bei jeder Propagation wieder um, und beide Bemassungen pendeln sich gegenseitig hoch,
        // ohne exakt zu konvergieren (Symptom: der Winkel bleibt dauerhaft leicht daneben, z.B.
        // 88.9° statt 70°). Bleibt dem eigentlichen Anker-Punkt dabei genau EINE freie (nicht
        // kantenfixierte) Achse, wird stattdessen ER verschoben — aber nur entlang dieser freien
        // Achse, über die geometrische Kreis-Schnittpunkt-Lösung: er muss auf dem Kreis mit
        // Radius newLen um den (fixen) winkelbemassten Punkt liegen, bei fester X- bzw.
        // Y-Koordinate. Von den zwei möglichen Schnittpunkten wird der näher an der aktuellen
        // Position gewählt (stabile, kontinuierliche Konvergenz statt Sprung).
        int provMoveIdx   = (p2IsAnchor && !p1IsAnchor) ? p1Idx : p2Idx;
        int provAnchorIdx = (p2IsAnchor && !p1IsAnchor) ? p2Idx : p1Idx;
        if (IsPositionDrivenByAngle(provMoveIdx) && !IsPositionDrivenByAngle(provAnchorIdx))
        {
            bool anchorXFixed = IsAxisFixed(provAnchorIdx, axisXFixed);
            bool anchorYFixed = IsAxisFixed(provAnchorIdx, axisYFixed);

            // Bevorzugte Lösung: Statt die komplette Ausgleichsbewegung dem Anker aufzubürden,
            // wird sie zwischen dem Anker und dem "Pivot" der winkelbemassenden Nachbar-Linie
            // aufgeteilt (z.B. untere UND rechte Linie, statt nur die rechte). Der Pivot ist der
            // andere Endpunkt derselben Winkel-Bemassung, die provMoveIdx positioniert — da diese
            // Bemassung Länge UND Winkel fixiert, ist der Versatz zwischen Pivot und provMoveIdx
            // konstant, d.h. provMoveIdx verschiebt sich exakt um denselben Betrag wie der Pivot
            // (reine Translation). So bleibt der eigene (unbemasste) Winkel dieser Länge-Linie
            // erhalten, weil ihre Richtung (cosT/sinT unten) beim Verlängern/Verkürzen konstant
            // gehalten wird, und die nötige Verschiebung wird über zwei orthogonale freie Achsen
            // (Anker + Pivot) eindeutig aufgelöst.
            int pivotIdx = FindAngleDrivenPivot(provMoveIdx);
            if (pivotIdx >= 0 && pivotIdx != provAnchorIdx && pivotIdx != provMoveIdx)
            {
                bool pivotXFixed = IsAxisFixed(pivotIdx, axisXFixed);
                bool pivotYFixed = IsAxisFixed(pivotIdx, axisYFixed);
                if (anchorXFixed != anchorYFixed && pivotXFixed != pivotYFixed && anchorXFixed != pivotXFixed)
                {
                    var anchorOld = GetPfadAbsAt(provAnchorIdx);
                    var moveOld   = GetPfadAbsAt(provMoveIdx);
                    var pivotOld  = GetPfadAbsAt(pivotIdx);
                    if (anchorOld != null && moveOld != null && pivotOld != null)
                    {
                        double vx = moveOld.Value.x - anchorOld.Value.x;
                        double vy = moveOld.Value.y - anchorOld.Value.y;
                        double l0 = Math.Sqrt(vx*vx + vy*vy);
                        if (l0 > 1e-9)
                        {
                            double cosT = vx / l0, sinT = vy / l0;
                            double dLen = newLen - l0;
                            double Dx = dLen * cosT, Dy = dLen * sinT;
                            double dAnchorX = 0, dAnchorY = 0, dPivotX = 0, dPivotY = 0;
                            if (anchorXFixed) { dPivotX = Dx; dAnchorY = -Dy; }   // Anker frei in Y, Pivot frei in X
                            else               { dAnchorX = -Dx; dPivotY = Dy; }  // Anker frei in X, Pivot frei in Y
                            UpdatePfadPunktPos(pivotIdx, pivotOld.Value.x + dPivotX, pivotOld.Value.y + dPivotY, preserveFollowers: true);
                            UpdatePfadPunktPos(provAnchorIdx, anchorOld.Value.x + dAnchorX, anchorOld.Value.y + dAnchorY, preserveFollowers: true);
                            UpdatePfadPunktPos(provMoveIdx, moveOld.Value.x + dPivotX, moveOld.Value.y + dPivotY, preserveFollowers: true);
                            DrawSkia?.InvalidateVisual();
                            return;
                        }
                    }
                }
            }

            if (anchorXFixed != anchorYFixed)   // genau eine Achse frei
            {
                var fixedPt   = GetPfadAbsAt(provMoveIdx);
                var curAnchor = GetPfadAbsAt(provAnchorIdx);
                if (fixedPt != null && curAnchor != null)
                {
                    double cx = fixedPt.Value.x, cy = fixedPt.Value.y;
                    if (anchorXFixed)
                    {
                        double ax = curAnchor.Value.x;
                        double rem = newLen*newLen - (ax - cx)*(ax - cx);
                        if (rem >= 0)
                        {
                            double d = Math.Sqrt(rem);
                            double solA = cy + d, solB = cy - d;
                            double chosen = Math.Abs(solA - curAnchor.Value.y) <= Math.Abs(solB - curAnchor.Value.y) ? solA : solB;
                            UpdatePfadPunktPos(provAnchorIdx, ax, chosen, preserveFollowers: provAnchorIdx <= provMoveIdx);
                            DrawSkia?.InvalidateVisual();
                            return;
                        }
                    }
                    else
                    {
                        double ay = curAnchor.Value.y;
                        double rem = newLen*newLen - (ay - cy)*(ay - cy);
                        if (rem >= 0)
                        {
                            double d = Math.Sqrt(rem);
                            double solA = cx + d, solB = cx - d;
                            double chosen = Math.Abs(solA - curAnchor.Value.x) <= Math.Abs(solB - curAnchor.Value.x) ? solA : solB;
                            UpdatePfadPunktPos(provAnchorIdx, chosen, ay, preserveFollowers: provAnchorIdx <= provMoveIdx);
                            DrawSkia?.InvalidateVisual();
                            return;
                        }
                    }
                }
            }
        }

        double movedX, movedY; int movedIdx;
        if (p2IsAnchor && !p1IsAnchor)
        {
            movedIdx = p1Idx;
            movedX = p2v.Value.x - dx*newLen; movedY = p2v.Value.y - dy*newLen;
            // preserveFollowers=false verschiebt nachfolgende Pfad-Punkte starr mit P1 mit.
            // Liegt P2 (Anker) vor P1 im Pfad, wäre P2 selbst ein "Folge-Punkt" von P1 und
            // würde fälschlich mitverschoben — dann einfrieren, damit P2/der Anker fix bleibt.
            UpdatePfadPunktPos(movedIdx, movedX, movedY, preserveFollowers: p1Idx <= p2Idx);
        }
        else
        {
            movedIdx = p2Idx;
            movedX = p1.Value.x + dx*newLen; movedY = p1.Value.y + dy*newLen;
            // preserveFollowers=false verschiebt nachfolgende Pfad-Punkte starr mit P2 mit.
            // Bei PointDist kann P2 (2. Klick) aber vor P1 (1. Klick) im Pfad liegen — dann wäre
            // P1 selbst ein "Folge-Punkt" von P2 und würde fälschlich mitverschoben. In diesem
            // Fall einfrieren (preserveFollowers=true), damit der Anker P1 fix bleibt.
            UpdatePfadPunktPos(movedIdx, movedX, movedY, preserveFollowers: p2Idx <= p1Idx);
        }
        // Aktive Abs-Referenz für Label-Repositionierung aktualisieren
        if (movedIdx == _vermP2Idx)
            _vermP2Abs = (movedX, movedY);
        if (_vermTextBox != null)
        {
            var pos = VermLabelScreenPos();
            System.Windows.Controls.Canvas.SetLeft(_vermTextBox, pos.X - 40);
            System.Windows.Controls.Canvas.SetTop (_vermTextBox, pos.Y - 28);
        }
        DrawSkia?.InvalidateVisual();
    }

    private void ApplyParallelDistConstraint(int p1Idx, int p2Idx, int q1Idx, int q2Idx, double newDist)
    {
        var p1 = GetPfadAbsAt(p1Idx); var p2 = GetPfadAbsAt(p2Idx);
        var q1 = GetPfadAbsAt(q1Idx); var q2 = GetPfadAbsAt(q2Idx);
        if (p1 == null || p2 == null || q1 == null || q2 == null) return;
        double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
        double l = Math.Sqrt(dx*dx + dy*dy);
        if (l < 1e-9) return;
        double nx = -dy/l, ny = dx/l;
        double curSigned = (q1.Value.x - p1.Value.x)*nx + (q1.Value.y - p1.Value.y)*ny;
        if (Math.Abs(curSigned) < 1e-9) return;
        double delta = Math.Sign(curSigned)*newDist - curSigned;
        UpdatePfadPunktPos(q1Idx, q1.Value.x + nx*delta, q1.Value.y + ny*delta, preserveFollowers: false);
        UpdatePfadPunktPos(q2Idx, q2.Value.x + nx*delta, q2.Value.y + ny*delta, preserveFollowers: false);
        DrawSkia?.InvalidateVisual();
    }

    // True, wenn idx der aktiv von einer Länge-/Punktabstand-Bemassung bewegte Zielpunkt ist
    // (also P2Idx eines Length- oder PointDist-Eintrags). Wird benutzt, um bei der Winkel-
    // Korrektur den richtigen Endpunkt zu drehen: der Punkt, dessen Position gerade durch eine
    // eigene Längen-Bemassung bestimmt wird, darf nicht von der Winkel-Korrektur wieder
    // verschoben werden — sonst zerstört die Winkel-Bemassung die soeben gesetzte Länge (und
    // umgekehrt), und beide Vermassungen pendeln sich gegenseitig hoch statt zu konvergieren.
    private bool IsPositionDrivenByLength(int idx)
    {
        foreach (var en in _vermPlaced)
            if ((en.Kind == VermKind.Length || en.Kind == VermKind.PointDist) && SamePathCorner(en.P2Idx, idx))
                return true;
        return false;
    }

    // True, wenn idx (oder sein SamePathCorner-Zwilling) als Q1Idx/Q2Idx einer Winkel-Bemassung
    // (Angle/Perpendicular/Parallel) referenziert wird — seine Position wird also (mit)bestimmt
    // durch eine Winkel-Korrektur auf einem angrenzenden Segment. Analog zu IsPositionDrivenByLength,
    // aber für Winkel statt Länge: wird benutzt, damit eine Längen-Bemassung auf einem NACHBAR-
    // Segment einen solchen Punkt nicht einfach entlang ihrer eigenen (eingefrorenen) Richtung
    // verschiebt — das würde die vom Winkel gerade korrekt bestimmte Position wieder zerstören.
    private bool IsPositionDrivenByAngle(int idx)
    {
        foreach (var en in _vermPlaced)
            if ((en.Kind == VermKind.Angle || en.Kind == VermKind.Perpendicular || en.Kind == VermKind.Parallel)
                && (SamePathCorner(en.Q1Idx, idx) || SamePathCorner(en.Q2Idx, idx)))
                return true;
        return false;
    }

    // Findet den "anderen" Q-Punkt der Winkel-Bemassung, die drivenIdx positioniert (siehe
    // IsPositionDrivenByAngle). Da diese Bemassung sowohl Länge als auch Winkel des Segments
    // (Q1Idx,Q2Idx) fixiert, ist der Versatz zwischen diesem Pivot-Punkt und drivenIdx konstant —
    // verschiebt sich der Pivot, verschiebt sich drivenIdx um exakt denselben Betrag (reine
    // Translation, keine Rotation). Wird in ApplyLengthConstraint genutzt, um die Ausgleichs-
    // bewegung einer Nachbar-Längen-Bemassung zwischen Anker und Pivot aufzuteilen, statt sie
    // komplett dem Anker aufzubürden.
    private int FindAngleDrivenPivot(int drivenIdx)
    {
        foreach (var en in _vermPlaced)
        {
            if (en.Kind != VermKind.Angle && en.Kind != VermKind.Perpendicular && en.Kind != VermKind.Parallel) continue;
            if (SamePathCorner(en.Q1Idx, drivenIdx)) return en.Q2Idx;
            if (SamePathCorner(en.Q2Idx, drivenIdx)) return en.Q1Idx;
        }
        return -1;
    }

    private void ApplyAngleConstraint(int p1Idx, int p2Idx, int q1Idx, int q2Idx, double newAngleDeg)
    {
        var p1 = GetPfadAbsAt(p1Idx); var p2 = GetPfadAbsAt(p2Idx);
        var q1 = GetPfadAbsAt(q1Idx); var q2 = GetPfadAbsAt(q2Idx);
        if (p1 == null || p2 == null || q1 == null || q2 == null) return;

        // Kantenfixierte Punkte (direkt über EdgeDist/PointEdgeDist/CoincidentCorner, oder
        // indirekt über eine starre achsparallele/-senkrechte Kette wie eine horizontale/
        // vertikale Nachbarlinie, siehe GetAxisFixedPoints) haben oberste Priorität — sie dürfen
        // von der Winkel-Korrektur NIE bewegt werden, auch nicht wenn der jeweils andere Q-Punkt
        // "nur" durch eine Längen-Bemassung positioniert ist. Der längenbemasste Punkt kann sich
        // dagegen meist noch über sein JEWEILS ANDERES Ende neu einpendeln (z.B. wird eine
        // unvermasste Nachbarlinie länger/kürzer), ein kantenfixierter Punkt hat dagegen keinen
        // Freiheitsgrad mehr. Ohne diese Priorisierung würde z.B. bei einer horizontalen,
        // kantenbemassten Unterkante deren (via Ringschluss indirekt kantenfixierte) Ecke von der
        // Winkel-Korrektur einer angrenzenden Bemassung mitgedreht — die Kanten-Vermassung zieht
        // sie danach zurück, wodurch der Winkel nie exakt konvergiert (pendelt/bleibt leicht daneben).
        var (angAxisXFixed, angAxisYFixed) = GetAxisFixedPoints();
        bool IsPtAxisFixed(int idx) => IsAxisFixed(idx, angAxisXFixed) || IsAxisFixed(idx, angAxisYFixed);

        if (Math.Abs(newAngleDeg) < 0.5 || Math.Abs(newAngleDeg - 90.0) < 0.5)
        {
            // Parallel (0°) / Rechtwinklig (90°) zu einer anderen Linie: direkt über die
            // Richtungsvektoren lösen statt über den Schnittpunkt der beiden Geraden (wie unten).
            // Der Schnittpunkt liegt bei (fast) paralleler Ausrichtung im Unendlichen bzw. wird
            // numerisch instabil — genau der Zustand, den diese Bedingung halten soll. Ohne diesen
            // Sonderfall bricht die Korrektur in exakt diesem Fall ab (LinesIntersection == null),
            // wodurch eine als "parallel" markierte Linie durch andere, gleichzeitig wirkende
            // Constraints schräg gezogen werden kann, ohne dass sie zurückkorrigiert wird.
            bool q1IsSharedPar = SamePathCorner(q1Idx, p1Idx) || SamePathCorner(q1Idx, p2Idx);
            bool q2IsSharedPar = SamePathCorner(q2Idx, p1Idx) || SamePathCorner(q2Idx, p2Idx);
            bool q1AxisFixedPar = IsPtAxisFixed(q1Idx);
            bool q2AxisFixedPar = IsPtAxisFixed(q2Idx);
            bool q1LenDrivenPar = IsPositionDrivenByLength(q1Idx);
            bool q2LenDrivenPar = IsPositionDrivenByLength(q2Idx);
            bool pivotIsQ1Par;
            if (q1AxisFixedPar != q2AxisFixedPar)
                pivotIsQ1Par = q1AxisFixedPar;   // kantenfixierter Punkt hat Vorrang vor allem anderen
            else if (q1LenDrivenPar != q2LenDrivenPar)
                pivotIsQ1Par = q1LenDrivenPar;   // der längenbemasste Punkt bleibt fix, siehe IsPositionDrivenByLength
            else
                pivotIsQ1Par = !(q2IsSharedPar && !q1IsSharedPar); // gemeinsamer Eckpunkt fix, sonst Q1 als Konvention

            var pivotPar     = pivotIsQ1Par ? q1.Value : q2.Value;
            var movePointPar = pivotIsQ1Par ? q2.Value : q1.Value;
            int moveIdxPar   = pivotIsQ1Par ? q2Idx    : q1Idx;
            int pivotIdxPar  = pivotIsQ1Par ? q1Idx    : q2Idx;

            double segLenPar = Math.Sqrt(Math.Pow(movePointPar.x - pivotPar.x, 2) + Math.Pow(movePointPar.y - pivotPar.y, 2));
            double refLenChk = Math.Sqrt(Math.Pow(p2.Value.x - p1.Value.x, 2) + Math.Pow(p2.Value.y - p1.Value.y, 2));
            if (segLenPar < 1e-9 || refLenChk < 1e-9) return;

            double urx = (p2.Value.x - p1.Value.x) / refLenChk, ury = (p2.Value.y - p1.Value.y) / refLenChk;
            double targetRad = newAngleDeg * Math.PI / 180.0;
            (double x, double y) Rot(double vx, double vy, double rad)
            {
                double c = Math.Cos(rad), s = Math.Sin(rad);
                return (vx * c - vy * s, vx * s + vy * c);
            }
            var d1 = Rot(urx, ury, targetRad);
            var d2 = Rot(urx, ury, -targetRad);
            var candidates = new (double x, double y)[] { d1, (-d1.x, -d1.y), d2, (-d2.x, -d2.y) };

            double curDx = movePointPar.x - pivotPar.x, curDy = movePointPar.y - pivotPar.y;
            double curLen = Math.Sqrt(curDx*curDx + curDy*curDy);
            double cux = curDx / curLen, cuy = curDy / curLen;

            var best = candidates[0];
            double bestDot = double.NegativeInfinity;
            foreach (var c in candidates)
            {
                double dot = c.x * cux + c.y * cuy;
                if (dot > bestDot) { bestDot = dot; best = c; }
            }

            double nxPar = pivotPar.x + best.x * segLenPar, nyPar = pivotPar.y + best.y * segLenPar;
            if (Math.Abs(nxPar - movePointPar.x) > 1e-6 || Math.Abs(nyPar - movePointPar.y) > 1e-6)
                UpdatePfadPunktPos(moveIdxPar, nxPar, nyPar, preserveFollowers: moveIdxPar <= pivotIdxPar);
            DrawSkia?.InvalidateVisual();
            return;
        }

        // Bestimme Pivot und bewegten Endpunkt des Q-Segments:
        // Der Pivot ist der Q-Endpunkt der näher am Schnittpunkt liegt (das gemeinsame Eck).
        // Der andere (weiter entfernte) wird gedreht — so bleibt die erste Linie unverändert
        // auch wenn P und Q einen gemeinsamen Punkt teilen.
        var inter = LinesIntersection(p1.Value, p2.Value, q1.Value, q2.Value);
        if (inter == null) return;

        // Pivot = gemeinsamer Eckpunkt der beiden Segmente (P und Q teilen ihn).
        // Wenn kein eindeutiger gemeinsamer Punkt: Punkt näher am Schnittpunkt.
        // Das verhindert, dass beim Propagieren rückwärts der falsche Punkt bewegt wird.
        bool q1IsShared = SamePathCorner(q1Idx, p1Idx) || SamePathCorner(q1Idx, p2Idx);
        bool q2IsShared = SamePathCorner(q2Idx, p1Idx) || SamePathCorner(q2Idx, p2Idx);
        // Vorrangig: ein Punkt, der bereits aktiv durch eine EIGENE Längen-/Punktabstand-
        // Bemassung positioniert wird (z.B. weil der Nutzer gerade die anliegende Linie auf
        // eine bestimmte Länge vermasst hat), bleibt fix — sonst reißt die Winkel-Korrektur die
        // gerade gesetzte Länge wieder um, und beide Bemassungen pendeln sich gegenseitig hoch
        // statt zu konvergieren (z.B.: obere Linie auf 300mm vermasst, während die schräge Linie
        // bereits Länge+Winkel hat — dann muss sich stattdessen die unvermasste untere Linie
        // anpassen, nicht die schräge).
        bool q1AxisFixed = IsPtAxisFixed(q1Idx);
        bool q2AxisFixed = IsPtAxisFixed(q2Idx);
        bool q1LenDriven = IsPositionDrivenByLength(q1Idx);
        bool q2LenDriven = IsPositionDrivenByLength(q2Idx);
        bool pivotIsQ1;
        if (q1AxisFixed != q2AxisFixed)
            pivotIsQ1 = q1AxisFixed;   // kantenfixierter Punkt hat Vorrang vor allem anderen
        else if (q1LenDriven != q2LenDriven)
            pivotIsQ1 = q1LenDriven;
        else if (q1IsShared && !q2IsShared)
            pivotIsQ1 = true;   // Q1 = gemeinsamer Eckpunkt → pivot Q1, move Q2
        else if (q2IsShared && !q1IsShared)
            pivotIsQ1 = false;  // Q2 = gemeinsamer Eckpunkt → pivot Q2, move Q1
        else
        {
            // Kein eindeutiger gemeinsamer Punkt → näher am Schnittpunkt
            double d1 = Math.Pow(q1.Value.x - inter.Value.x, 2) + Math.Pow(q1.Value.y - inter.Value.y, 2);
            double d2 = Math.Pow(q2.Value.x - inter.Value.x, 2) + Math.Pow(q2.Value.y - inter.Value.y, 2);
            pivotIsQ1 = d1 <= d2;
        }
        var pivot     = pivotIsQ1 ? q1.Value : q2.Value;
        var movePoint = pivotIsQ1 ? q2.Value : q1.Value;
        int moveIdx   = pivotIsQ1 ? q2Idx    : q1Idx;
        int pivotIdx  = pivotIsQ1 ? q1Idx    : q2Idx;

        double dxM = movePoint.x - pivot.x, dyM = movePoint.y - pivot.y;
        if (Math.Sqrt(dxM*dxM + dyM*dyM) < 1e-9) return;
        double a1 = VermSegArcAngle(inter.Value, p1.Value, p2.Value);
        double a2 = VermSegArcAngle(inter.Value, q1.Value, q2.Value);
        double diff = a2 - a1;
        while (diff >  Math.PI) diff -= 2*Math.PI;
        while (diff < -Math.PI) diff += 2*Math.PI;

        double newAngleRad = newAngleDeg * Math.PI / 180.0;
        // 0° = parallel: rotate to make diff = 0 (same direction), keeping rotation sign
        double rotDelta = (diff != 0 ? Math.Sign(diff) : 1) * newAngleRad - diff;
        while (rotDelta >  Math.PI) rotDelta -= 2*Math.PI;
        while (rotDelta < -Math.PI) rotDelta += 2*Math.PI;

        double cosR = Math.Cos(rotDelta), sinR = Math.Sin(rotDelta);
        double nx = pivot.x + dxM*cosR - dyM*sinR;
        double ny = pivot.y + dxM*sinR + dyM*cosR;
        // preserveFollowers=false: der bewegte Punkt dreht sich um den Pivot, alle
        // nachfolgenden Pfad-Punkte folgen ihm dabei starr (gleiche Verschiebung), statt
        // eingefroren zu werden — sonst würde der Pfad danach verzerrt/verkürzt.
        // Ausnahme: liegt moveIdx im Pfad VOR dem Pivot, wäre der Pivot selbst ein
        // "Folge-Punkt" von moveIdx — dann muss eingefroren werden (preserveFollowers=true),
        // sonst würde sich der eigentlich fixe Pivot mitverschieben.
        UpdatePfadPunktPos(moveIdx, nx, ny, preserveFollowers: moveIdx <= pivotIdx);
        DrawSkia?.InvalidateVisual();
    }

    private void ApplyEdgeDistConstraint(int p1Idx, int p2Idx, int edge, double value, VermEntry? self = null)
    {
        var p2 = GetPfadAbsAt(p2Idx); if (p2 == null) return;
        // Delta berechnen damit P2 den Zielabstand zur Kante hat
        double dx = 0, dy = 0;
        switch (edge) {
            case 1: dx = value           - p2.Value.x; break;
            case 2: dx = (WorkX - value) - p2.Value.x; break;
            case 3: dy = value           - p2.Value.y; break;
            case 4: dy = (WorkY - value) - p2.Value.y; break;
        }
        // Die komplette Kette (nicht nur den referenzierten Punkt) starr verschieben, damit
        // alle Segmentlängen erhalten bleiben. Frisch gezeichnete Pfadpunkte sind standardmäßig
        // absolut ("Unten links") statt relativ ("Letzter Punkt") referenziert — ShiftPfadChain
        // verschiebt daher jeden Punkt der Kette einzeln um dasselbe Delta.
        // Punkte, die bereits durch eine ANDERE platzierte Masslinie fixiert sind, dürfen dabei
        // nicht mitverschoben werden (siehe LockedPfadIndices) — die Verschiebung bricht dort ab,
        // sodass die dazwischenliegende unvermasste Pfadlinie die Längendifferenz aufnimmt.
        var locked = LockedPfadIndices(self, xAxis: edge == 1 || edge == 2);
        var (chainSt1, chainEn1) = FindChainBounds(p1Idx);
        var (chainSt2, chainEn2) = FindChainBounds(p2Idx);
        if (chainSt1 == chainSt2)
        {
            int coreLo = Math.Min(p1Idx, p2Idx), coreHi = Math.Max(p1Idx, p2Idx);
            ShiftPfadChain(chainSt1, chainEn1, dx, dy, coreLo, coreHi, locked);
        }
        else
        {
            ShiftPfadChain(chainSt1, chainEn1, dx, dy, p1Idx, p1Idx, locked);
            ShiftPfadChain(chainSt2, chainEn2, dx, dy, p2Idx, p2Idx, locked);
        }
        DrawSkia?.InvalidateVisual();
    }

    private void ApplyPointEdgeDistConstraint(int ptIdx, int edge, double value, VermEntry? self = null)
    {
        var pt = GetPfadAbsAt(ptIdx); if (pt == null) return;
        double dx = 0, dy = 0;
        switch (edge) {
            case 1: dx = value           - pt.Value.x; break;
            case 2: dx = (WorkX - value) - pt.Value.x; break;
            case 3: dy = value           - pt.Value.y; break;
            case 4: dy = (WorkY - value) - pt.Value.y; break;
        }
        // Wie bei ApplyEdgeDistConstraint: die komplette Kette verschieben statt nur den
        // vermassten Punkt, aber an bereits anderweitig fixierten Punkten abbrechen (s.u.).
        var (chainSt, chainEn) = FindChainBounds(ptIdx);
        ShiftPfadChain(chainSt, chainEn, dx, dy, ptIdx, ptIdx, LockedPfadIndices(self, xAxis: edge == 1 || edge == 2));
        DrawSkia?.InvalidateVisual();
    }

    // Punkte, die bereits durch eine platzierte Positions-Masslinie (Länge, Kantendistanz,
    // Koinzident, ...) fixiert sind. exclude ist die gerade angewendete Masslinie selbst
    // (bei erneutem Anwenden/Propagieren) und wird von der Sperre ausgenommen.
    //
    // Grund: Wird eine unvermasste Pfadlinie auf BEIDEN Seiten zur Werkstückkante vermasst,
    // legt die zweite Bemassung sonst die komplette Kette (inkl. des bereits über die erste
    // Bemassung fixierten Punkts) starr neu fest — das bereits gesetzte Maß würde dadurch
    // bei jeder Propagation verändert statt die dazwischenliegende, unvermasste Linie
    // anzupassen. LockedPfadIndices lässt ShiftPfadChain an solchen Punkten anhalten.
    //
    // xAxis: für welche Achse die Sperre gilt. EdgeDist/PointEdgeDist fixieren je nach Kante
    // (Edge 1/2 = X, Edge 3/4 = Y) NUR eine Achse — eine bereits zur rechten Kante vermasste Ecke
    // darf trotzdem noch frei nach unten/oben verschoben werden, wenn anschließend eine ANDERE
    // Ecke zur unteren Kante vermasst wird. Ohne diese Unterscheidung wurde die X-Sperre
    // fälschlich auch für den Y-Shift übernommen, wodurch ShiftPfadChain die Verschiebung schon
    // an dieser (nur in X fixierten) Ecke abgebrochen hat — nur ein Teil der Kette wanderte nach
    // unten, der Rest blieb stehen, und das Rechteck wurde dadurch verzerrt/verkleinert statt nur
    // verschoben. Länge/PointDist/Coincident/CoincidentCorner sind dagegen echte 2D-Fixierungen
    // (kein Kanten-Bezug) und sperren daher weiterhin beide Achsen.
    private HashSet<int> LockedPfadIndices(VermEntry? exclude, bool xAxis)
    {
        var locked = new HashSet<int>();
        foreach (var en in _vermPlaced)
        {
            if (exclude != null && en == exclude) continue;
            switch (en.Kind)
            {
                case VermKind.EdgeDist:
                    if ((en.Edge == 1 || en.Edge == 2) == xAxis)
                    { locked.Add(en.P1Idx); locked.Add(en.P2Idx); }
                    break;
                case VermKind.PointEdgeDist:
                    if ((en.Edge == 1 || en.Edge == 2) == xAxis)
                        locked.Add(en.P2Idx);
                    break;
                case VermKind.Length:
                case VermKind.PointDist:
                case VermKind.Coincident:
                case VermKind.CoincidentCorner:
                    locked.Add(en.P2Idx);
                    break;
            }
        }
        return locked;
    }

    // Verschiebt jeden Punkt einer Pfad-Kette einzeln um dasselbe (dx,dy) — unabhängig vom
    // jeweiligen Bezugspunkt (absolut z.B. "Unten links" oder relativ "Letzter Punkt").
    // Dadurch bleiben alle Segmentlängen/-winkel exakt erhalten, auch bei frisch gezeichneten,
    // noch unvermassten Pfaden (deren Punkte standardmäßig absolut referenziert sind und daher
    // NICHT automatisch über "Letzter Punkt" mitwandern würden).
    // coreLo/coreHi: der/die eigentlich bemasste(n) Punkt(e), die auf jeden Fall verschoben
    // werden. Von dort aus wird die Verschiebung in beide Richtungen ausgedehnt, bis entweder
    // das Kettenende oder ein in "locked" enthaltener (anderweitig fixierter) Punkt erreicht
    // wird — an dem wird abgebrochen, sodass das dortige Segment die Längendifferenz aufnimmt.
    private void ShiftPfadChain(int chainSt, int chainEn, double dx, double dy,
                                 int coreLo, int coreHi, HashSet<int>? locked = null)
    {
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
        int effSt = chainSt, effEn = chainEn;
        if (locked != null)
        {
            for (int i = coreLo - 1; i >= chainSt; i--)
                if (locked.Contains(i)) { effSt = i + 1; break; }
            for (int i = coreHi + 1; i <= chainEn; i++)
                if (locked.Contains(i)) { effEn = i - 1; break; }
        }
        var orig = new (double x, double y)?[effEn - effSt + 1];
        for (int i = effSt; i <= effEn; i++)
            orig[i - effSt] = GetPfadAbsAt(i);
        for (int i = effSt; i <= effEn; i++)
        {
            var a = orig[i - effSt];
            if (a == null) continue;
            UpdatePfadPunktPos(i, a.Value.x + dx, a.Value.y + dy, preserveFollowers: false, onlyThisPoint: true);
        }
    }

    private void ApplyCoincidentConstraint(int p1Idx, int p2Idx)
    {
        var p1 = GetPfadAbsAt(p1Idx); var p2 = GetPfadAbsAt(p2Idx);
        if (p1 == null || p2 == null) return;
        // Ist P2 der Start-/Endpunkt einer geschlossenen Kette, würde ihn zu verschieben über
        // den Partner-Punkt-Mechanismus unbemerkt auch den andererorts als Anker genutzten
        // Zwillings-Index mitverschieben (siehe IsClosedChainEndpoint) — dann stattdessen P1
        // verschieben und P2/den Anker fix lassen.
        if (IsClosedChainEndpoint(p2Idx) && !IsClosedChainEndpoint(p1Idx))
        {
            UpdatePfadPunktPos(p1Idx, p2.Value.x, p2.Value.y, preserveFollowers: p1Idx <= p2Idx);
            DrawSkia?.InvalidateVisual();
            return;
        }
        // P1/P2 werden per Klick-Reihenfolge gewählt, nicht nach Pfad-Reihenfolge — P2 kann
        // also vor P1 im Pfad liegen. Dann wäre P1 ein "Folge-Punkt" von P2 und preserveFollowers
        // =false würde ihn fälschlich mitverschieben. Nur verschieben lassen, wenn P2 wirklich
        // nach P1 im Pfad liegt; sonst einfrieren, damit der andere Punkt (P1) fix bleibt.
        UpdatePfadPunktPos(p2Idx, p1.Value.x, p1.Value.y, preserveFollowers: p2Idx <= p1Idx);
        DrawSkia?.InvalidateVisual();
    }

    private void ApplyCoincidentCornerConstraint(int ptIdx, int corner)
    {
        var (cx, cy) = WorkpieceCornerPos(corner);
        UpdatePfadPunktPos(ptIdx, cx, cy, preserveFollowers: false);
        DrawSkia?.InvalidateVisual();
    }

    // ── Geometrie-Constraint-Toolbar: Klick-Ablauf für Koinzident/Rechtwinklig/Parallel ──
    // Diese 3 Modi verzichten (anders als Länge/Winkel) auf die TextBox-Eingabe, weil der
    // Zielwert bereits feststeht (0 mm / 0° / 90°). Ein Klick auf das erste Element merkt
    // sich dieses in _geomFirstIdx/_geomFirstIdx2, der zweite Klick löst die Berechnung aus.
    private void HandleGeomModeClick(double vmx, double vmy)
    {
        if (_geomMode == GeomConstraintMode.Coincident)
        {
            if (_geomFirstIdx < 0)
            {
                int ptHit = HitTestPfadPoint(vmx, vmy);
                if (ptHit >= 0) { _geomFirstIdx = ptHit; DrawSkia?.InvalidateVisual(); }
                return;
            }
            int ptHit2 = HitTestPfadPoint(vmx, vmy);
            if (ptHit2 >= 0 && ptHit2 != _geomFirstIdx)
            {
                FinalizeGeomConstraint(VermKind.Coincident, _geomFirstIdx, ptHit2, -1, -1, 0, 0.0);
                return;
            }
            int cornerHit = HitTestWorkpieceCorner(vmx, vmy);
            if (cornerHit > 0)
            {
                FinalizeGeomConstraint(VermKind.CoincidentCorner, -1, _geomFirstIdx, -1, -1, cornerHit, 0.0);
                return;
            }
            // Klick ins Leere → Auswahl zurücksetzen, von vorne beginnen
            ResetGeomSelection();
        }
        else // Perpendicular / Parallel
        {
            if (_geomFirstIdx < 0)
            {
                var hit = HitTestPfadLineSegment(vmx, vmy);
                if (hit.p1 >= 0) { _geomFirstIdx = hit.p1; _geomFirstIdx2 = hit.p2; DrawSkia?.InvalidateVisual(); }
                return;
            }
            // 2. Klick: entweder ein zweites Segment ODER eine Werkstückkante
            int edgeHit = HitTestWorkpieceEdge(vmx, vmy);
            if (edgeHit > 0)
            {
                var edgeKind  = _geomMode == GeomConstraintMode.Perpendicular ? VermKind.PerpendicularEdge : VermKind.ParallelEdge;
                double edgeVal = _geomMode == GeomConstraintMode.Perpendicular ? 90.0 : 0.0;
                FinalizeGeomConstraint(edgeKind, _geomFirstIdx, _geomFirstIdx2, -1, -1, edgeHit, edgeVal);
                return;
            }
            var hit2 = HitTestPfadLineSegment(vmx, vmy);
            if (hit2.p1 >= 0 && !(hit2.p1 == _geomFirstIdx && hit2.p2 == _geomFirstIdx2))
            {
                var kind  = _geomMode == GeomConstraintMode.Perpendicular ? VermKind.Perpendicular : VermKind.Parallel;
                double val = _geomMode == GeomConstraintMode.Perpendicular ? 90.0 : 0.0;
                FinalizeGeomConstraint(kind, _geomFirstIdx, _geomFirstIdx2, hit2.p1, hit2.p2, 0, val);
                return;
            }
            // Klick ins Leere → Auswahl zurücksetzen, von vorne beginnen
            ResetGeomSelection();
        }
    }

    // Prüft auf Konflikte, legt bei Erfolg die neue Geometrie-Constraint an und wendet sie
    // sofort an (kein TextBox-Zwischenschritt, da der Zielwert schon feststeht).
    private void FinalizeGeomConstraint(VermKind kind, int p1Idx, int p2Idx, int q1Idx, int q2Idx, int edge, double value)
    {
        bool conflict = kind switch
        {
            VermKind.Coincident        => VermHasConflict(-1, VermKind.Coincident, p2Idx),
            VermKind.CoincidentCorner  => VermHasConflict(-1, VermKind.CoincidentCorner, p2Idx),
            VermKind.Perpendicular     => VermHasConflict(-1, VermKind.Perpendicular, q1Idx, q2Idx),
            VermKind.Parallel          => VermHasConflict(-1, VermKind.Parallel, q1Idx, q2Idx),
            VermKind.PerpendicularEdge => VermHasConflict(-1, VermKind.PerpendicularEdge, p1Idx, p2Idx),
            VermKind.ParallelEdge      => VermHasConflict(-1, VermKind.ParallelEdge, p1Idx, p2Idx),
            _                          => false
        };
        if (conflict)
        {
            MessageBox.Show(this,
                "Ein oder mehrere referenzierte Punkte werden bereits durch eine andere Bedingung gebunden.\nDie Bedingung kann nicht hinzugefügt werden.",
                "Konflikt", MessageBoxButton.OK, MessageBoxImage.Warning);
            ResetGeomSelection();
            return;
        }
        var newEntry = new VermEntry(kind, p1Idx, p2Idx, 0, value, q1Idx, q2Idx, edge);
        ApplyVermNewEntry(newEntry, value);
        _vermPlaced.Add(newEntry);
        PropagateVermConstraints();
        CheckAndReportConstraints();
        ResetGeomSelection();
    }

    private void ResetGeomSelection()
    {
        _geomFirstIdx = -1; _geomFirstIdx2 = -1;
        DrawSkia?.InvalidateVisual();
    }

    private void ApplyEdgeAngleConstraint(int p1Idx, int p2Idx, int edge, double newAngleDeg)
    {
        var p1 = GetPfadAbsAt(p1Idx); var p2 = GetPfadAbsAt(p2Idx);
        if (p1 == null || p2 == null) return;

        // Sonderfall 0° / 90°: P2 auf die Zielachse drehen (P1 = fester Ankerpunkt), NICHT nur
        // auf sie projizieren — sonst würde sich die gezeichnete Segmentlänge ändern (die
        // Projektion nimmt ja nur eine der beiden Koordinaten der Zielachse, die andere bleibt
        // unverändert stehen). Stattdessen wird die ursprüngliche Länge |P1P2| beibehalten und
        // P2 entlang der Zielachse (in dieselbe Richtung wie vorher) auf diesen Radius gesetzt.
        // Eine echte Rotation um einen von der Segment-Lage abhängigen Pivot (wie im allgemeinen
        // Fall unten) wäre hier instabil, weil dieser Pivot bei schrägen Zwischenzuständen
        // (während der Propagation) am falschen Ende liegen kann — die direkte Berechnung über
        // die bekannte Länge und das Vorzeichen der bisherigen Richtung braucht dagegen keinen
        // Schnittpunkt und bleibt daher stabil.
        bool isVerticalEdge = (edge == 1 || edge == 2);
        double dx0 = p2.Value.x - p1.Value.x, dy0 = p2.Value.y - p1.Value.y;
        double segLen = Math.Sqrt(dx0*dx0 + dy0*dy0);
        if (segLen < 1e-9) return;
        // Ist P1 (der feste Anker) gerade selbst durch eine eigene Längen-/Punktabstand-
        // Bemassung positioniert worden (z.B. weil eine ANGRENZENDE Linie gerade auf eine neue
        // Länge vermasst wurde), dann ist die aktuelle Distanz |P1P2| vorübergehend verfälscht —
        // sie spiegelt nur die neue P1-Position wider, nicht die tatsächlich gewünschte Länge
        // dieses (unvermassten) Segments. Würde man diese verfälschte segLen trotzdem als "zu
        // erhaltende Länge" übernehmen, würde sich die unvermasste Linie künstlich verlängern/
        // verkürzen, obwohl sie eigentlich einfach nur starr mit P1 hätte mitwandern sollen. In
        // diesem Fall daher NICHT die Länge erhalten, sondern nur die durch die Achsen-Vorgabe
        // zwingend geforderte Koordinate anpassen (reine Projektion) — die andere Koordinate von
        // P2 bleibt unverändert stehen, wodurch sich P2 effektiv exakt wie P1 verschiebt
        // (Translation statt Streckung).
        bool preservePerpCoord = IsPositionDrivenByLength(p1Idx) && !IsPositionDrivenByLength(p2Idx);
        if (Math.Abs(newAngleDeg - 90.0) < 0.5)      // PerpendicularEdge
        {
            // senkrecht zur Kante → horizontal wenn Kante vertikal, sonst vertikal
            double nx2 = isVerticalEdge ? p1.Value.x + (dx0 >= 0 ? segLen : -segLen) : p1.Value.x;
            double ny2 = isVerticalEdge ? p1.Value.y : p1.Value.y + (dy0 >= 0 ? segLen : -segLen);
            if (preservePerpCoord)
            {
                if (isVerticalEdge) nx2 = p2.Value.x; else ny2 = p2.Value.y;
            }
            // P1 < P2 im Pfad (Segment aus zwei aufeinanderfolgenden Punkten) → P1 ist nie
            // ein Folge-Punkt von P2, preserveFollowers=false verschiebt daher nur echte
            // nachfolgende Pfad-Punkte, nicht den fixen Anker P1.
            // Toleranz an die Speicherpräzision (Math.Round(...,3) → 0.001mm) angleichen statt
            // 1e-6: sonst löst bereits reines Rundungsrauschen (z.B. nach einem starren
            // ShiftPfadChain-Verschieben der ganzen Kette) hier eine "Korrektur" aus, die einen
            // vermeintlich schon exakt ausgerichteten Punkt unnötig neu setzt und dadurch das
            // Rechteck bei jeder Propagation minimal verzerrt.
            if (Math.Abs(nx2 - p2.Value.x) > 1e-3 || Math.Abs(ny2 - p2.Value.y) > 1e-3)
                UpdatePfadPunktPos(p2Idx, nx2, ny2, preserveFollowers: false);
            DrawSkia?.InvalidateVisual();
            return;
        }
        if (Math.Abs(newAngleDeg) < 0.5)              // ParallelEdge
        {
            // parallel zur Kante → vertikal wenn Kante vertikal, sonst horizontal
            double nx2 = isVerticalEdge ? p1.Value.x : p1.Value.x + (dx0 >= 0 ? segLen : -segLen);
            double ny2 = isVerticalEdge ? p1.Value.y + (dy0 >= 0 ? segLen : -segLen) : p1.Value.y;
            if (preservePerpCoord)
            {
                if (isVerticalEdge) ny2 = p2.Value.y; else nx2 = p2.Value.x;
            }
            // Gleiche Toleranz-Angleichung wie im PerpendicularEdge-Zweig oben (0.001mm statt 1e-6).
            if (Math.Abs(nx2 - p2.Value.x) > 1e-3 || Math.Abs(ny2 - p2.Value.y) > 1e-3)
                UpdatePfadPunktPos(p2Idx, nx2, ny2, preserveFollowers: false);
            DrawSkia?.InvalidateVisual();
            return;
        }

        var inter = SegmentEdgeIntersection(p1.Value, p2.Value, edge);
        if (inter == null) return;
        var (e1, e2) = EdgeVirtualSegment(inter.Value, edge);

        // Pivot = Endpunkt näher am Schnittpunkt; der andere dreht sich
        double d1 = Math.Pow(p1.Value.x - inter.Value.x, 2) + Math.Pow(p1.Value.y - inter.Value.y, 2);
        double d2 = Math.Pow(p2.Value.x - inter.Value.x, 2) + Math.Pow(p2.Value.y - inter.Value.y, 2);
        var pivot     = d1 <= d2 ? p1.Value : p2.Value;
        var movePoint = d1 <= d2 ? p2.Value : p1.Value;
        int moveIdx   = d1 <= d2 ? p2Idx    : p1Idx;
        int pivotIdx  = d1 <= d2 ? p1Idx    : p2Idx;

        double dxM = movePoint.x - pivot.x, dyM = movePoint.y - pivot.y;
        if (Math.Sqrt(dxM*dxM + dyM*dyM) < 1e-9) return;

        double a1 = VermSegArcAngle(inter.Value, p1.Value, p2.Value);
        double a2 = VermSegArcAngle(inter.Value, e1, e2);
        double diff = a1 - a2;  // Segment relativ zur Kante
        while (diff >  Math.PI) diff -= 2*Math.PI;
        while (diff < -Math.PI) diff += 2*Math.PI;

        double newAngleRad = newAngleDeg * Math.PI / 180.0;
        double rotDelta = (diff != 0 ? Math.Sign(diff) : 1) * newAngleRad - diff;
        while (rotDelta >  Math.PI) rotDelta -= 2*Math.PI;
        while (rotDelta < -Math.PI) rotDelta += 2*Math.PI;
        // Wenn movePoint == p1 dreht es in Gegenrichtung
        if (d1 > d2) rotDelta = -rotDelta;

        double cosR = Math.Cos(rotDelta), sinR = Math.Sin(rotDelta);
        double nx = pivot.x + dxM*cosR - dyM*sinR;
        double ny = pivot.y + dxM*sinR + dyM*cosR;
        // preserveFollowers=false: nachfolgende Pfad-Punkte drehen/verschieben sich starr mit,
        // statt eingefroren zu werden. Ausnahme: liegt moveIdx im Pfad VOR dem Pivot, wäre der
        // Pivot selbst ein "Folge-Punkt" von moveIdx — dann einfrieren, damit der eigentlich
        // fixe Pivot nicht mitverschoben wird.
        UpdatePfadPunktPos(moveIdx, nx, ny, preserveFollowers: moveIdx <= pivotIdx);
        DrawSkia?.InvalidateVisual();
    }

    private void ApplyLineToPointConstraint(int p1Idx, int p2Idx, int q1Idx, double newDist)
    {
        var p1 = GetPfadAbsAt(p1Idx); var p2 = GetPfadAbsAt(p2Idx);
        var q1 = GetPfadAbsAt(q1Idx);
        if (p1 == null || p2 == null || q1 == null) return;
        double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
        double l = Math.Sqrt(dx*dx + dy*dy); if (l < 1e-9) return;
        double nx = -dy/l, ny = dx/l;
        double curSigned = (q1.Value.x - p1.Value.x)*nx + (q1.Value.y - p1.Value.y)*ny;
        if (Math.Abs(curSigned) < 1e-9) return;
        double delta = Math.Sign(curSigned)*newDist - curSigned;
        UpdatePfadPunktPos(q1Idx, q1.Value.x + nx*delta, q1.Value.y + ny*delta, preserveFollowers: false);
        DrawSkia?.InvalidateVisual();
    }

    private void DrawVermassungOverlay(SKCanvas canvas)
    {
        if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return;
        bool hasActive = (_vermState == 1 || _vermState == 2 || _vermState == 5) && (_vermP1Idx >= 0 || _vermPtIdx >= 0 || _vermKreisIdx >= 0 || _vermRktIdx >= 0);
        bool hasHover  = _activeTool == CanvasTool.Vermassen && (_vermHoverP1 >= 0 || _vermHoverEdge > 0 || _vermHoverPoint >= 0 || _vermHoverKreis >= 0 || _vermHoverRkt >= 0);
        if (!hasActive && !hasHover && _vermPlaced.Count == 0) return;

        // Gemeinsame Paint-Objekte
        float sw = (float)(1.2 / _zoom);
        using var linePaint = new SKPaint { Color = new SKColor(20, 100, 200), Style = SKPaintStyle.Stroke,
            StrokeWidth = sw, IsAntialias = true };
        using var textPaint = new SKPaint { Color = new SKColor(20, 100, 200), IsAntialias = true,
            TextSize = (float)(11.0 / _zoom), Typeface = SKTypeface.FromFamilyName("Arial") };
        using var bgPaint   = new SKPaint { Color = new SKColor(255, 255, 255, 200), Style = SKPaintStyle.Fill };

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        (float sx, float sy) Px(double x, double y)
        {
            var (nullpunktX, nullpunktY) = ConvertFromNullpunktCoordinates(x, y);
            return ((float)nullpunktX, (float)(WorkY - nullpunktY));
        }
        float gap = (float)(2.0 / _zoom);
        float as_ = (float)(5.0 / _zoom);

        void DrawLabel(float msx, float msy, string text)
        {
            float tw = textPaint.MeasureText(text);
            float fh = textPaint.TextSize;
            canvas.DrawRect(msx - tw/2 - 3, msy - fh - 2, tw + 6, fh + 6, bgPaint);
            canvas.DrawText(text, msx - tw/2, msy, textPaint);
        }

        void DrawArrowTip(float tx, float ty, float toDx, float toDy)
        {
            float tLen = (float)Math.Sqrt(toDx*toDx + toDy*toDy);
            if (tLen < 1e-6f) return;
            float tdx = toDx/tLen, tdy = toDy/tLen;
            using var path = new SKPath();
            path.MoveTo(tx, ty);
            path.LineTo(tx + tdx*as_ + tdy*as_*0.35f, ty + tdy*as_ - tdx*as_*0.35f);
            path.LineTo(tx + tdx*as_ - tdy*as_*0.35f, ty + tdy*as_ + tdx*as_*0.35f);
            path.Close();
            using var fill = new SKPaint { Color = linePaint.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawPath(path, fill);
        }

        // Längen-Masslinie (ein Segment)
        void DrawOneLine(double p1x, double p1y, double p2x, double p2y,
                         double offset, string? labelText)
        {
            double dx = p2x - p1x, dy = p2y - p1y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            if (len < 1e-6) return;
            double nx = -dy/len, ny = dx/len;
            double d1x = p1x + nx*offset, d1y = p1y + ny*offset;
            double d2x = p2x + nx*offset, d2y = p2y + ny*offset;
            var (p1sx, p1sy) = Px(p1x, p1y);
            var (p2sx, p2sy) = Px(p2x, p2y);
            var (d1sx, d1sy) = Px(d1x, d1y);
            var (d2sx, d2sy) = Px(d2x, d2y);
            float eNx = d1sx - p1sx, eNy = d1sy - p1sy;
            float eLen = (float)Math.Sqrt(eNx*eNx + eNy*eNy);
            if (eLen > gap * 4)
            {
                float gapF = gap * 2 / eLen;
                canvas.DrawLine(p1sx + eNx*gapF, p1sy + eNy*gapF, d1sx + eNx*gap, d1sy + eNy*gap, linePaint);
                canvas.DrawLine(p2sx + eNx*gapF, p2sy + eNy*gapF, d2sx + eNx*gap, d2sy + eNy*gap, linePaint);
            }
            canvas.DrawLine(d1sx, d1sy, d2sx, d2sy, linePaint);
            DrawArrowTip(d1sx, d1sy, d2sx - d1sx, d2sy - d1sy);
            DrawArrowTip(d2sx, d2sy, d1sx - d2sx, d1sy - d2sy);
            if (labelText != null) DrawLabel((d1sx+d2sx)/2f, (d1sy+d2sy)/2f, labelText);
        }

        // Parallel-Abstand Masslinie (zwei parallele Segmente)
        void DrawParallelLine(
            double p1x, double p1y, double p2x, double p2y,
            double q1x, double q1y,
            double offset, string? labelText)
        {
            double dx = p2x - p1x, dy = p2y - p1y;
            double l = Math.Sqrt(dx*dx + dy*dy); if (l < 1e-9) return;
            double nx = -dy/l, ny = dx/l;
            double sD = (q1x - p1x)*nx + (q1y - p1y)*ny; // signed dist seg1→seg2
            double ax = p1x + offset*dx, ay = p1y + offset*dy; // anchor on seg1
            double bx = ax + nx*sD,     by = ay + ny*sD;       // anchor on seg2
            var (asx, asy) = Px(ax, ay);
            var (bsx, bsy) = Px(bx, by);
            // Extension tick from seg1 and seg2
            var (p1sx, p1sy) = Px(p1x, p1y); var (p2sx, p2sy) = Px(p2x, p2y);
            double q2x = q1x + dx, q2y = q1y + dy; // parallel extended
            var (q1sx, q1sy) = Px(q1x, q1y); var (q2sx, q2sy) = Px(q2x, q2y);
            // Small extension ticks
            float exLen = (float)(3.0/_zoom);
            float enxF = (float)nx, enyF = (float)ny; // extension direction
            canvas.DrawLine(asx - enxF*gap, asy - enyF*gap, asx + enxF*exLen, asy + enyF*exLen, linePaint);
            canvas.DrawLine(bsx - enxF*gap, bsy - enyF*gap, bsx + enxF*exLen, bsy + enyF*exLen, linePaint);
            // Main dim line
            canvas.DrawLine(asx, asy, bsx, bsy, linePaint);
            DrawArrowTip(asx, asy, bsx - asx, bsy - asy);
            DrawArrowTip(bsx, bsy, asx - bsx, asy - bsy);
            if (labelText != null) DrawLabel((asx+bsx)/2f, (asy+bsy)/2f, labelText);
        }

        // Winkel-Masslinie (zwei nicht-parallele Segmente)
        void DrawAngleLine(
            (double x, double y) p1, (double x, double y) p2,
            (double x, double y) q1, (double x, double y) q2,
            double radius, string? labelText)
        {
            var inter = LinesIntersection(p1, p2, q1, q2);
            if (inter == null) return;
            double a1 = VermSegArcAngle(inter.Value, p1, p2);
            double a2 = VermSegArcAngle(inter.Value, q1, q2);
            double diff = a2 - a1;
            while (diff >  Math.PI) diff -= 2*Math.PI;
            while (diff < -Math.PI) diff += 2*Math.PI;
            // Always draw arc in the interior sector (between the two lines, not outside the corner).
            // The label uses VermArcSpanDeg which returns the acute value (0°=parallel, 90°=perp).
            // Arc in SKCanvas: angles are clockwise (Y flipped), convert
            var (ix, iy) = Px(inter.Value.x, inter.Value.y);
            float r  = (float)radius;
            // SKCanvas Arc: start/sweepAngle in degrees, clockwise
            float startDeg = (float)(-a1 * 180.0 / Math.PI); // negate for Y-flip
            float sweepDeg = (float)(-diff * 180.0 / Math.PI);
            var arcRect = new SKRect(ix - r, iy - r, ix + r, iy + r);
            canvas.DrawArc(arcRect, startDeg, sweepDeg, false, linePaint);
            // Arrows at arc ends
            double a1End = a1, a2End = a1 + diff;
            float ax1 = ix + r*(float)Math.Cos(a1End), ay1 = iy - r*(float)Math.Sin(a1End);
            float ax2 = ix + r*(float)Math.Cos(a2End), ay2 = iy - r*(float)Math.Sin(a2End);
            // Arrow tangent directions (perpendicular to radius, in sweep direction)
            float t1dx = (float)(Math.Sin(a1End) * Math.Sign(diff)), t1dy = (float)(Math.Cos(a1End) * Math.Sign(diff));
            float t2dx = -(float)(Math.Sin(a2End) * Math.Sign(diff)), t2dy = -(float)(Math.Cos(a2End) * Math.Sign(diff));
            DrawArrowTip(ax1, ay1, t1dx, t1dy);
            DrawArrowTip(ax2, ay2, t2dx, t2dy);
            // Extension lines to intersection
            var (i1sx, i1sy) = Px(inter.Value.x, inter.Value.y);
            float l1x = i1sx + r*(float)Math.Cos(a1End)*1.1f, l1y = i1sy - r*(float)Math.Sin(a1End)*1.1f;
            float l2x = i1sx + r*(float)Math.Cos(a2End)*1.1f, l2y = i1sy - r*(float)Math.Sin(a2End)*1.1f;
            // No radial "pizza slice" lines — arc only
            if (labelText != null)
            {
                // Place label directly on the arc (not scaled from far intersection)
                double amid = VermArcMidAngle(a1, a2);
                float lmx = ix + r*(float)Math.Cos(amid);
                float lmy = iy - r*(float)Math.Sin(amid);
                DrawLabel(lmx, lmy, labelText);
            }
        }

        void DrawEdgeDist(double p2x, double p2y, int edge, double offset, string? labelText)
        {
            if (edge <= 0) return;
            bool isHoriz = (edge == 1 || edge == 2);
            if (isHoriz)
            {
                double lineY = p2y + offset;
                double xEdge = edge == 1 ? 0 : WorkX;
                double lineX1 = Math.Min(p2x, xEdge);
                double lineX2 = Math.Max(p2x, xEdge);
                var (ls1x, ls1y) = Px(lineX1, lineY);
                var (ls2x, ls2y) = Px(lineX2, lineY);
                var (p2sx, p2sy) = Px(p2x, p2y);
                var (essx, essy) = Px(xEdge, lineY);
                // Extension line from P2 to dim line
                if (Math.Abs(p2sy - ls1y) > gap)
                    canvas.DrawLine(p2sx, p2sy + (p2sy > ls1y ? gap : -gap), p2sx, ls1y + (p2sy > ls1y ? -gap : gap), linePaint);
                // Extension tick at edge
                float tk = (float)(3.0 / _zoom);
                canvas.DrawLine(essx, essy - tk, essx, essy + tk, linePaint);
                // Main dim line
                canvas.DrawLine(ls1x, ls1y, ls2x, ls2y, linePaint);
                DrawArrowTip(ls1x, ls1y, ls2x - ls1x, ls2y - ls1y);
                DrawArrowTip(ls2x, ls2y, ls1x - ls2x, ls1y - ls2y);
                if (labelText != null) DrawLabel((ls1x + ls2x) / 2f, ls1y - gap * 2, labelText);
            }
            else
            {
                double lineX = p2x + offset;
                double yEdge = edge == 3 ? 0 : WorkY;
                double lineY1 = Math.Min(p2y, yEdge);
                double lineY2 = Math.Max(p2y, yEdge);
                var (ls1x, ls1y) = Px(lineX, lineY1);
                var (ls2x, ls2y) = Px(lineX, lineY2);
                var (p2sx, p2sy) = Px(p2x, p2y);
                var (essx, essy) = Px(lineX, yEdge);
                // Extension line from P2 to dim line
                if (Math.Abs(p2sx - ls1x) > gap)
                    canvas.DrawLine(p2sx + (p2sx > ls1x ? gap : -gap), p2sy, ls1x + (p2sx > ls1x ? -gap : gap), p2sy, linePaint);
                // Extension tick at edge
                float tk = (float)(3.0 / _zoom);
                canvas.DrawLine(essx - tk, essy, essx + tk, essy, linePaint);
                // Main dim line
                canvas.DrawLine(ls1x, ls1y, ls2x, ls2y, linePaint);
                DrawArrowTip(ls1x, ls1y, ls2x - ls1x, ls2y - ls1y);
                DrawArrowTip(ls2x, ls2y, ls1x - ls2x, ls1y - ls2y);
                if (labelText != null) DrawLabel(ls1x + gap * 2, (ls1y + ls2y) / 2f, labelText);
            }
        }

        // Durchmesser-/Radius-Masslinie eines Kreises
        void DrawKreisDim(int kreisIdx, bool isRadius, double angle, string? labelText)
        {
            var dl = KreisDimLine(kreisIdx, isRadius, angle);
            var g  = GetKreisGeom(kreisIdx);
            if (dl == null || g == null) return;
            var (asx, asy) = Px(dl.Value.a.x, dl.Value.a.y);
            var (bsx, bsy) = Px(dl.Value.b.x, dl.Value.b.y);
            canvas.DrawLine(asx, asy, bsx, bsy, linePaint);
            DrawArrowTip(bsx, bsy, asx - bsx, asy - bsy);
            if (!isRadius) DrawArrowTip(asx, asy, bsx - asx, bsy - asy);
            // Zentrumsmarkierung
            var (csx, csy) = Px(g.Value.cx, g.Value.cy);
            float ck = (float)(3.0 / _zoom);
            canvas.DrawLine(csx - ck, csy, csx + ck, csy, linePaint);
            canvas.DrawLine(csx, csy - ck, csx, csy + ck, linePaint);
            if (labelText != null)
            {
                var lp = KreisDimLabelPos(kreisIdx, angle);
                if (lp != null) { var (lsx, lsy) = Px(lp.Value.x, lp.Value.y); DrawLabel(lsx, lsy, labelText); }
            }
        }

        void DrawKreisHighlight(int kreisIdx, SKColor col)
        {
            var g = GetKreisGeom(kreisIdx); if (g == null) return;
            using var kp = new SKPaint { Color = col, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(3.5 / _zoom), IsAntialias = true };
            var (csx, csy) = Px(g.Value.cx, g.Value.cy);
            canvas.DrawCircle(csx, csy, (float)g.Value.r, kp);
        }

        void DrawRktSeiteHighlight(int rktVermIdx, SKColor col)
        {
            var g = GetRktSeiteGeom(rktVermIdx); if (g == null) return;
            using var rpnt = new SKPaint { Color = col, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(3.5 / _zoom), IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            var (ax, ay) = Px(g.Value.a.x, g.Value.a.y);
            var (bx, by) = Px(g.Value.b.x, g.Value.b.y);
            canvas.DrawLine(ax, ay, bx, by, rpnt);
        }

        void DrawEdgeHighlight(int edgeId, SKColor col)
        {
            if (edgeId <= 0) return;
            float hw2 = (float)(3.5 / _zoom);
            using var ep = new SKPaint { Color = col, Style = SKPaintStyle.Stroke,
                StrokeWidth = hw2, IsAntialias = true };
            var (ax, ay) = edgeId == 1 ? Px(0, 0) : edgeId == 2 ? Px(WorkX, 0) : edgeId == 3 ? Px(0, 0) : Px(0, WorkY);
            var (bx, by) = edgeId == 1 ? Px(0, WorkY) : edgeId == 2 ? Px(WorkX, WorkY) : edgeId == 3 ? Px(WorkX, 0) : Px(WorkX, WorkY);
            canvas.DrawLine(ax, ay, bx, by, ep);
        }

        // Segment-Hervorhebungsfarbe
        void DrawSegHighlight(int hp1, int hp2, SKColor col)
        {
            (double x, double y)? p1 = null;
            (double x, double y)? p2 = null;

            // Wenn hp1 ein kodierter Rechteck-Seiten-Index ist
            if (IsRktSideEncoded(hp1))
            {
                var (rktIdx, sideIdx) = DecodeRktSide(hp1);
                if (rktIdx >= 0 && rktIdx < _history.Count && _history[rktIdx].Params is RechteckParams rp)
                {
                    var sides = GetRektSeiten(rp);
                    if (sideIdx >= 0 && sideIdx < sides.Length)
                    {
                        var side = sides[sideIdx];
                        p1 = (side.x1, side.y1);
                        p2 = (side.x2, side.y2);
                    }
                }
            }
            else
            {
                p1 = GetPfadAbsAt(hp1);
                p2 = GetPfadAbsAt(hp2);
            }

            if (p1 == null || p2 == null) return;
            float hw = (float)(3.5 / _zoom);
            using var hp = new SKPaint { Color = col, Style = SKPaintStyle.Stroke,
                StrokeWidth = hw, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            var (hx1, hy1) = Px(p1.Value.x, p1.Value.y);
            var (hx2, hy2) = Px(p2.Value.x, p2.Value.y);
            canvas.DrawLine(hx1, hy1, hx2, hy2, hp);
        }

        bool isVermActive = _activeTool == CanvasTool.Vermassen;

        // ── Hover-Hervorhebung (Werkzeug aktiv) ─────────────────────────────
        if (isVermActive)
        {
            // Erstes gewähltes Segment (state 1 / 5)
            if (_vermState >= 1 && _vermState <= 5 && _vermP1Idx >= 0)
                DrawSegHighlight(_vermP1Idx, _vermP2Idx, new SKColor(30, 120, 220, 200));
            // Zweites gewähltes Segment (state 5)
            if (_vermState == 5 && _vermQ1Idx >= 0)
                DrawSegHighlight(_vermQ1Idx, _vermQ2Idx, new SKColor(30, 180, 80, 200));
            // Hover-Segment (state 0)
            if ((_vermState == 0 || _vermState == 1) && _vermHoverP1 >= 0)
                DrawSegHighlight(_vermHoverP1, _vermHoverP2, new SKColor(255, 160, 0, 220));
            // Hover-Kante (state 0 oder 1)
            if ((_vermState == 0 || _vermState == 1) && _vermHoverEdge > 0)
                DrawEdgeHighlight(_vermHoverEdge, new SKColor(255, 160, 0, 220));
            // Aktive Kante (state 1)
            if (_vermState == 1 && _vermActiveEdge > 0)
                DrawEdgeHighlight(_vermActiveEdge, new SKColor(30, 120, 220, 200));
            // Hover-Punkt (state 0 oder 1)
            if ((_vermState == 0 || _vermState == 1) && _vermHoverPoint >= 0)
            {
                var hpa = GetAbsPosForVerm(_vermHoverPoint);
                if (hpa != null) {
                    var (hpx, hpy) = Px(hpa.Value.x, hpa.Value.y);
                    float hr = (float)(5.0 / _zoom);
                    using var hpp = new SKPaint { Color = new SKColor(255, 160, 0, 220), Style = SKPaintStyle.Fill, IsAntialias = true };
                    canvas.DrawCircle(hpx, hpy, hr, hpp);
                }
            }
            // Hover-Kreis (state 0 oder 1)
            if ((_vermState == 0 || _vermState == 1) && _vermHoverKreis >= 0)
                DrawKreisHighlight(_vermHoverKreis, new SKColor(255, 160, 0, 220));
            // Hover-Rechteck-Seite (state 0 oder 1)
            if ((_vermState == 0 || _vermState == 1) && _vermHoverRkt >= 0)
                DrawRktSeiteHighlight(_vermHoverRkt, new SKColor(255, 160, 0, 220));
            // Gewählter Kreis (state 1 / 2 / 5)
            if (_vermState >= 1 && _vermState <= 5 && _vermKreisIdx >= 0)
                DrawKreisHighlight(_vermKreisIdx, new SKColor(30, 120, 220, 200));
            // Gewählte Rechteck-Seite (state 1 / 2 / 5)
            if (_vermState >= 1 && _vermState <= 5 && _vermRktIdx >= 0)
                DrawRktSeiteHighlight(_vermRktIdx, new SKColor(30, 120, 220, 200));
            // Aktiver Punkt (state 1, Punkt-Modus)
            if (_vermState == 1 && _vermPtIdx >= 0)
            {
                var apa = GetAbsPosForVerm(_vermPtIdx);
                if (apa != null) {
                    var (apx, apy) = Px(apa.Value.x, apa.Value.y);
                    float ar = (float)(5.0 / _zoom);
                    using var app = new SKPaint { Color = new SKColor(30, 120, 220, 200), Style = SKPaintStyle.Fill, IsAntialias = true };
                    canvas.DrawCircle(apx, apy, ar, app);
                }
            }
        }

        // ── Platzierte Masslinien – immer sichtbar ───────────────────────────
        for (int ei = 0; ei < _vermPlaced.Count; ei++)
        {
            var en = _vermPlaced[ei];
            // Geometrie-Constraints werden separat durch DrawGeomConstraintSymbols gezeichnet
            if (en.Kind == VermKind.Coincident || en.Kind == VermKind.Perpendicular || en.Kind == VermKind.Parallel
             || en.Kind == VermKind.ParallelEdge || en.Kind == VermKind.PerpendicularEdge
             || en.Kind == VermKind.CoincidentCorner) continue;

            bool hideLabel = isVermActive && ei == _vermEditIdx && (_vermState == 3 || _vermState == 4);
            double drawOffset = (isVermActive && _vermState == 3 && ei == _vermEditIdx)
                ? _vermDragOffset : en.Offset;

            // Kreis-Bemassungen: Wert folgt der aktuellen Kreis-Geometrie
            if (IsKreisVermKind(en.Kind))
            {
                var cur = KreisVermActual(en); if (cur == null) continue;
                double curVal = Math.Round(cur.Value, 3);
                if (Math.Abs(curVal - en.Value) > 0.0005)
                    _vermPlaced[ei] = en = en with { Value = curVal };
                if (en.Kind == VermKind.KreisKantenDist)
                {
                    var g = GetKreisGeom(en.P2Idx);
                    string? lblK = hideLabel ? null : curVal.ToString("F2", inv) + " mm";
                    // Grün, wenn das Gegenstück (gegenüberliegende Kante) genau gleich gross ist
                    var normalCol = linePaint.Color;
                    if (KreisKantenDistSymmetrisch(en))
                        linePaint.Color = textPaint.Color = new SKColor(0, 150, 60);
                    if (g != null && en.Edge > 0)
                        DrawEdgeDist(g.Value.cx, g.Value.cy, en.Edge, drawOffset, lblK);
                    linePaint.Color = textPaint.Color = normalCol;
                }
                else
                {
                    bool isRad = en.Kind == VermKind.KreisRadius;
                    string? lblK = hideLabel ? null : (isRad ? "R " : "Ø ") + curVal.ToString("F2", inv);
                    DrawKreisDim(en.P2Idx, isRad, drawOffset, lblK);
                }
                continue;
            }

            // Rechteck-Bemassungen: Wert folgt der aktuellen Rechteck-Geometrie
            if (IsRktVermKind(en.Kind))
            {
                var cur = RktVermActual(en); if (cur == null) continue;
                double curVal = Math.Round(cur.Value, 3);
                if (Math.Abs(curVal - en.Value) > 0.0005)
                    _vermPlaced[ei] = en = en with { Value = curVal };
                string? lblR = hideLabel ? null : curVal.ToString("F2", inv) + " mm";
                if (en.Kind == VermKind.RktKantenDist)
                {
                    var m = RktSeiteMitte(en.P2Idx);
                    if (m != null && en.Edge > 0)
                        DrawEdgeDist(m.Value.x, m.Value.y, en.Edge, drawOffset, lblR);
                }
                else if (GetRktSeiteGeom(en.P2Idx) is { } rs)
                    DrawOneLine(rs.a.x, rs.a.y, rs.b.x, rs.b.y, drawOffset, lblR);
                continue;
            }

            var p1abs = GetPfadAbsAt(en.P1Idx);
            if (p1abs == null && en.Kind != VermKind.EdgeDist && en.Kind != VermKind.PointEdgeDist) continue;
            var p2abs = GetPfadAbsAt(en.P2Idx); if (p2abs == null) continue;

            if (en.Kind == VermKind.Length)
            {
                double dx = p2abs.Value.x - p1abs.Value.x, dy = p2abs.Value.y - p1abs.Value.y;
                double curLen = Math.Round(Math.Sqrt(dx*dx + dy*dy), 3);
                // Der gespeicherte Sollwert (en.Value) wird hier NICHT mehr an die aktuelle
                // Geometrie angepasst — sonst könnte eine andere Vermassung/Constraint die
                // Länge verändern und der Sollwert würde stillschweigend auf den (falschen)
                // Ist-Wert überschrieben. PropagateVermConstraintsLive/ApplyLengthConstraint
                // setzt die Geometrie ohnehin anhand des unveränderten en.Value durch.
                string? lbl = hideLabel ? null : curLen.ToString("F2", inv) + " mm";
                DrawOneLine(p1abs.Value.x, p1abs.Value.y, p2abs.Value.x, p2abs.Value.y, drawOffset, lbl);
            }
            else if (en.Kind == VermKind.ParallelDist)
            {
                var q1abs = GetPfadAbsAt(en.Q1Idx); if (q1abs == null) continue;
                double l = Math.Sqrt(Math.Pow(p2abs.Value.x - p1abs.Value.x, 2) + Math.Pow(p2abs.Value.y - p1abs.Value.y, 2));
                if (l < 1e-9) continue;
                double nx = -(p2abs.Value.y - p1abs.Value.y)/l, ny = (p2abs.Value.x - p1abs.Value.x)/l;
                double curDist = Math.Round(Math.Abs((q1abs.Value.x - p1abs.Value.x)*nx + (q1abs.Value.y - p1abs.Value.y)*ny), 3);
                if (Math.Abs(curDist - en.Value) > 0.0005)
                    _vermPlaced[ei] = en = en with { Value = curDist };
                string? lbl = hideLabel ? null : curDist.ToString("F2", inv) + " mm";
                DrawParallelLine(p1abs.Value.x, p1abs.Value.y, p2abs.Value.x, p2abs.Value.y,
                                 q1abs.Value.x, q1abs.Value.y, drawOffset, lbl);
            }
            else if (en.Kind == VermKind.Angle)
            {
                var q1abs = GetPfadAbsAt(en.Q1Idx); if (q1abs == null) continue;
                var q2abs = GetPfadAbsAt(en.Q2Idx); if (q2abs == null) continue;
                // Der gespeicherte Sollwinkel (en.Value) wird hier NICHT mehr an die aktuelle
                // Geometrie angepasst — anders als z.B. bei Länge, die sich bewusst nachzieht.
                // Eine Winkelbemaßung ist ein fester Sollwert: ändert eine andere Bemaßung die
                // Geometrie, muss PropagateVermConstraintsLive/ApplyAngleConstraint den Winkel
                // anhand des unveränderten en.Value zurückkorrigieren, statt dass der Sollwert
                // hier im Zeichen-Code stillschweigend auf den (falschen) Ist-Winkel überschrieben
                // wird — genau das führte dazu, dass sich Winkelwerte durch andere Vermassungen
                // verändern konnten.
                double curActual = Math.Round(VermArcSpanActual(p1abs.Value, p2abs.Value, q1abs.Value, q2abs.Value), 2);
                // Anzeige als echter Winkel (0–180°) statt auf den spitzen Wert gefaltet, damit
                // z.B. eine eingegebene 110°-Bemassung auch als 110° angezeigt bleibt.
                string? lbl = hideLabel ? null : curActual.ToString("F2", inv) + "°";
                var interA = LinesIntersection(p1abs.Value, p2abs.Value, q1abs.Value, q2abs.Value);
                double arcR = interA.HasValue ? AngleArcRadius(drawOffset, p1abs.Value, p2abs.Value, interA.Value) : 20;
                DrawAngleLine(p1abs.Value, p2abs.Value, q1abs.Value, q2abs.Value, arcR, lbl);
            }
            else if ((en.Kind == VermKind.EdgeDist || en.Kind == VermKind.PointEdgeDist) && en.Edge > 0)
            {
                double curDist = Math.Round(EdgeDistValue(p2abs.Value.x, p2abs.Value.y, en.Edge), 3);
                if (Math.Abs(curDist - en.Value) > 0.0005)
                    _vermPlaced[ei] = en = en with { Value = curDist };
                string? lbl = hideLabel ? null : curDist.ToString("F2", inv) + " mm";
                DrawEdgeDist(p2abs.Value.x, p2abs.Value.y, en.Edge, drawOffset, lbl);
            }
            else if (en.Kind == VermKind.EdgeAngle && en.Edge > 0 && p1abs != null)
            {
                var inter = SegmentEdgeIntersection(p1abs.Value, p2abs.Value, en.Edge);
                if (inter == null) continue;
                var (e1, e2) = EdgeVirtualSegment(inter.Value, en.Edge);
                double curActual = Math.Round(VermArcSpanActual(p1abs.Value, p2abs.Value, e1, e2), 2);
                if (Math.Abs(curActual - en.Value) > 0.005)
                    _vermPlaced[ei] = en = en with { Value = curActual };
                // Anzeige als echter Winkel (0–180°), siehe Angle-Zweig oben.
                string? lbl = hideLabel ? null : curActual.ToString("F2", inv) + "°";
                double arcREA = AngleArcRadius(drawOffset, p1abs.Value, p2abs.Value, inter.Value);
                DrawAngleLine(p1abs.Value, p2abs.Value, e1, e2, arcREA, lbl);
            }
            else if (en.Kind == VermKind.PointDist && p1abs != null)
            {
                double dx = p2abs.Value.x - p1abs.Value.x, dy = p2abs.Value.y - p1abs.Value.y;
                double curLen = Math.Round(Math.Sqrt(dx*dx + dy*dy), 3);
                if (Math.Abs(curLen - en.Value) > 0.0005)
                    _vermPlaced[ei] = en = en with { Value = curLen };
                string? lbl = hideLabel ? null : curLen.ToString("F2", inv) + " mm";
                DrawOneLine(p1abs.Value.x, p1abs.Value.y, p2abs.Value.x, p2abs.Value.y, drawOffset, lbl);
            }
            else if (en.Kind == VermKind.LineToPoint && p1abs != null)
            {
                var q1abs2 = GetPfadAbsAt(en.Q1Idx); if (q1abs2 == null) continue;
                double dx1 = p2abs.Value.x - p1abs.Value.x, dy1 = p2abs.Value.y - p1abs.Value.y;
                double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1); if (l1 < 1e-9) continue;
                double nx = -dy1/l1, ny = dx1/l1;
                double curDist2 = Math.Round(Math.Abs((q1abs2.Value.x - p1abs.Value.x)*nx + (q1abs2.Value.y - p1abs.Value.y)*ny), 3);
                if (Math.Abs(curDist2 - en.Value) > 0.0005)
                    _vermPlaced[ei] = en = en with { Value = curDist2 };
                string? lbl = hideLabel ? null : curDist2.ToString("F2", inv) + " mm";
                DrawParallelLine(p1abs.Value.x, p1abs.Value.y, p2abs.Value.x, p2abs.Value.y,
                                 q1abs2.Value.x, q1abs2.Value.y, drawOffset, lbl);
            }
        }

        // ── Geometrie-Constraint-Symbole ─────────────────────────────────────
        DrawGeomConstraintSymbols(canvas);

        // ── Aktive Vorschau (nur wenn Werkzeug aktiv) ────────────────────────
        if (!isVermActive) goto repositionTextBox;

        if (_vermState == 1 && _vermIsHolding && _vermP1Idx >= 0)
        {
            double previewOff = VermSignedOffset(_vermMouseMm.x, _vermMouseMm.y, _vermP1Abs, _vermP2Abs);
            double dl = VermSegmentLength();
            DrawOneLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y,
                previewOff, dl.ToString("F2", inv) + " mm");
        }
        else if (_vermState == 1 && _vermKreisIdx >= 0 && _vermActiveEdge == 0 && _vermHoverEdge == 0
                 && !IsRlbAnkerVermIdx(_vermKreisIdx))
        {
            // Kreis gewählt: Durchmesser (Maus ausserhalb) bzw. Radius (Maus innerhalb) folgt der Maus
            var g = GetKreisGeom(_vermKreisIdx);
            if (g != null)
            {
                double mdx = _vermMouseMm.x - g.Value.cx, mdy = _vermMouseMm.y - g.Value.cy;
                bool isRad = Math.Sqrt(mdx*mdx + mdy*mdy) < g.Value.r;
                double val = isRad ? g.Value.r : 2 * g.Value.r;
                DrawKreisDim(_vermKreisIdx, isRad, Math.Atan2(mdy, mdx),
                    (isRad ? "R " : "Ø ") + val.ToString("F2", inv));
            }
        }
        else if (_vermState == 2 && _vermKreisIdx >= 0
            && _vermActiveKind is VermKind.KreisDurchmesser or VermKind.KreisRadius)
        {
            DrawKreisDim(_vermKreisIdx, _vermActiveKind == VermKind.KreisRadius, _vermOffset, null);
        }
        else if (_vermState == 1 && _vermRktIdx >= 0 && _vermActiveEdge == 0 && _vermHoverEdge == 0)
        {
            // Rechteck-Seite gewählt: Seitenlänge folgt der Maus
            if (GetRktSeiteGeom(_vermRktIdx) is { } rs)
            {
                double rdx = rs.b.x - rs.a.x, rdy = rs.b.y - rs.a.y;
                DrawOneLine(rs.a.x, rs.a.y, rs.b.x, rs.b.y,
                    VermSignedOffset(_vermMouseMm.x, _vermMouseMm.y, rs.a, rs.b),
                    Math.Sqrt(rdx*rdx + rdy*rdy).ToString("F2", inv) + " mm");
            }
        }
        else if (_vermState == 2 && _vermActiveKind == VermKind.RktSeite && _vermRktIdx >= 0)
        {
            DrawOneLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y, _vermOffset, null);
        }
        else if (_vermState == 2 && _vermActiveKind is VermKind.KreisKantenDist or VermKind.RktKantenDist
                 && _vermActiveEdge > 0 && _vermP2Idx >= 0)
        {
            DrawEdgeDist(_vermP2Abs.x, _vermP2Abs.y, _vermActiveEdge, _vermOffset, null);
        }
        else if (_vermState == 5
            && (_vermActiveKind == VermKind.EdgeDist || _vermActiveKind == VermKind.PointEdgeDist
                || _vermActiveKind == VermKind.KreisKantenDist || _vermActiveKind == VermKind.RktKantenDist)
            && _vermActiveEdge > 0 && _vermP2Idx >= 0)
        {
            // EdgeDist/PointEdgeDist-Vorschau: Masslinie folgt der Maus
            bool isHoriz = (_vermActiveEdge == 1 || _vermActiveEdge == 2);
            double previewOff = isHoriz ? _vermMouseMm.y - _vermP2Abs.y : _vermMouseMm.x - _vermP2Abs.x;
            double dist = EdgeDistValue(_vermP2Abs.x, _vermP2Abs.y, _vermActiveEdge);
            DrawEdgeDist(_vermP2Abs.x, _vermP2Abs.y, _vermActiveEdge, previewOff, dist.ToString("F2", inv) + " mm");
        }
        else if (_vermState == 5 && _vermActiveKind == VermKind.EdgeAngle && _vermActiveEdge > 0 && _vermP1Idx >= 0)
        {
            // EdgeAngle-Vorschau: Bogenlinie folgt der Maus
            var inter = SegmentEdgeIntersection(_vermP1Abs, _vermP2Abs, _vermActiveEdge);
            if (inter != null)
            {
                double tEA = AngleTParam(_vermMouseMm.x, _vermMouseMm.y, _vermP1Abs, _vermP2Abs);
                double rEA = AngleArcRadius(tEA, _vermP1Abs, _vermP2Abs, inter.Value);
                var (e1, e2) = EdgeVirtualSegment(inter.Value, _vermActiveEdge);
                double angA = VermArcSpanActual(_vermP1Abs, _vermP2Abs, e1, e2);
                DrawAngleLine(_vermP1Abs, _vermP2Abs, e1, e2, rEA, angA.ToString("F2", inv) + "°");
            }
        }
        else if (_vermState == 5 && _vermActiveKind == VermKind.PointDist && _vermP1Idx >= 0)
        {
            double dx = _vermP2Abs.x - _vermP1Abs.x, dy = _vermP2Abs.y - _vermP1Abs.y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            if (len > 1e-9) {
                double nx = -dy/len, ny = dx/len;
                double off = (_vermMouseMm.x - (_vermP1Abs.x+_vermP2Abs.x)/2)*nx
                           + (_vermMouseMm.y - (_vermP1Abs.y+_vermP2Abs.y)/2)*ny;
                DrawOneLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y,
                    off, len.ToString("F2", inv) + " mm");
            }
        }
        else if (_vermState == 5 && _vermActiveKind == VermKind.LineToPoint && _vermP1Idx >= 0 && _vermQ1Idx >= 0)
        {
            double dx1 = _vermP2Abs.x - _vermP1Abs.x, dy1 = _vermP2Abs.y - _vermP1Abs.y;
            double l2  = dx1*dx1 + dy1*dy1;
            double t   = l2 < 1e-9 ? 0.5
                : Math.Clamp((_vermMouseMm.x - _vermP1Abs.x)*dx1/l2 + (_vermMouseMm.y - _vermP1Abs.y)*dy1/l2, -2.0, 3.0);
            double l1  = Math.Sqrt(l2);
            double nx  = -dy1/l1, ny = dx1/l1;
            double dist = Math.Abs((_vermQ1Abs.x - _vermP1Abs.x)*nx + (_vermQ1Abs.y - _vermP1Abs.y)*ny);
            DrawParallelLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y,
                _vermQ1Abs.x, _vermQ1Abs.y, t, dist.ToString("F2", inv) + " mm");
        }
        else if (_vermState == 5 && _vermP1Idx >= 0 && _vermQ1Idx >= 0)
        {
            if (_vermActiveKind == VermKind.ParallelDist)
            {
                double dx1 = _vermP2Abs.x - _vermP1Abs.x, dy1 = _vermP2Abs.y - _vermP1Abs.y;
                double l1 = Math.Sqrt(dx1*dx1 + dy1*dy1); if (l1 > 1e-9)
                {
                    double nx = -dy1/l1, ny = dx1/l1;
                    double l2 = dx1*dx1 + dy1*dy1;
                    double t = l2 < 1e-9 ? 0.5
                        : Math.Clamp((_vermMouseMm.x - _vermP1Abs.x)*dx1/l2 + (_vermMouseMm.y - _vermP1Abs.y)*dy1/l2, -2.0, 3.0);
                    double dist = Math.Abs((_vermQ1Abs.x - _vermP1Abs.x)*nx + (_vermQ1Abs.y - _vermP1Abs.y)*ny);
                    DrawParallelLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y,
                        _vermQ1Abs.x, _vermQ1Abs.y, t, dist.ToString("F2", inv) + " mm");
                }
            }
            else // Angle
            {
                var inter = LinesIntersection(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs);
                if (inter != null)
                {
                    double tPrev = AngleTParam(_vermMouseMm.x, _vermMouseMm.y, _vermP1Abs, _vermP2Abs);
                    double rPrev = AngleArcRadius(tPrev, _vermP1Abs, _vermP2Abs, inter.Value);
                    double angA2 = VermArcSpanActual(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs);
                    DrawAngleLine(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs,
                        rPrev, angA2.ToString("F2", inv) + "°");
                }
            }
        }
        else if (_vermState == 2 && _vermP1Idx >= 0)
        {
            // State 2: TextBox offen, zeige Masslinie ohne Label (TextBox übernimmt)
            if (_vermActiveKind == VermKind.Length)
                DrawOneLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y, _vermOffset, null);
            else if (_vermActiveKind == VermKind.ParallelDist)
                DrawParallelLine(_vermP1Abs.x, _vermP1Abs.y, _vermP2Abs.x, _vermP2Abs.y,
                    _vermQ1Abs.x, _vermQ1Abs.y, _vermOffset, null);
            else if (_vermActiveKind == VermKind.Angle && _vermQ1Idx >= 0)
            {
                var interS2 = LinesIntersection(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs);
                if (interS2.HasValue) {
                    double rS2 = AngleArcRadius(_vermOffset, _vermP1Abs, _vermP2Abs, interS2.Value);
                    DrawAngleLine(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs, rS2, null);
                }
            }
            else if ((_vermActiveKind == VermKind.EdgeDist || _vermActiveKind == VermKind.PointEdgeDist)
                     && _vermActiveEdge > 0 && _vermP2Idx >= 0)
                DrawEdgeDist(_vermP2Abs.x, _vermP2Abs.y, _vermActiveEdge, _vermOffset, null);
            else if (_vermActiveKind == VermKind.EdgeAngle && _vermActiveEdge > 0 && _vermP1Idx >= 0)
            {
                var inter = SegmentEdgeIntersection(_vermP1Abs, _vermP2Abs, _vermActiveEdge);
                if (inter != null) {
                    var (e1,e2) = EdgeVirtualSegment(inter.Value, _vermActiveEdge);
                    double rEA2 = AngleArcRadius(_vermOffset, _vermP1Abs, _vermP2Abs, inter.Value);
                    DrawAngleLine(_vermP1Abs, _vermP2Abs, e1, e2, rEA2, null);
                }
            }
        }

        repositionTextBox:
        if (_vermState == 2 && _vermTextBox != null)
        {
            var pos = VermLabelScreenPos();
            System.Windows.Controls.Canvas.SetLeft(_vermTextBox, pos.X - 40);
            System.Windows.Controls.Canvas.SetTop (_vermTextBox, pos.Y - 28);
        }
    }

    // Bounding Box einer Pfad-Kette in mm
    // Erweitert bbox mit einem Punkt
    private static void ExpandBBox(ref double minX, ref double minY, ref double maxX, ref double maxY,
        double x, double y)
    {
        if (x < minX) minX = x;
        if (y < minY) minY = y;
        if (x > maxX) maxX = x;
        if (y > maxY) maxY = y;
    }

    // Erweitert bbox mit dem exakten Bogen zwischen p1 und p2 mit Mittelpunkt midAbs
    private static void ExpandBBoxForArc(ref double minX, ref double minY, ref double maxX, ref double maxY,
        (double x, double y) p1, (double x, double y) p2, (double x, double y) midAbs)
    {
        // Sehne
        double dx = p2.x - p1.x, dy = p2.y - p1.y;
        double L = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-10) return;

        // Pfeilhöhe h (vorzeichenbehaftet, links-positiv)
        double perpX = -dy / L, perpY = dx / L;
        double mcx = (p1.x + p2.x) / 2, mcy = (p1.y + p2.y) / 2;
        double h = (midAbs.x - mcx) * perpX + (midAbs.y - mcy) * perpY;
        if (Math.Abs(h) < 1e-10) return; // Gerade

        // Kreisradius und Zentrum
        double half = L / 2;
        double R = (half * half + h * h) / (2 * Math.Abs(h));
        // Zentrum liegt auf der Gegenseite der Sehne (von midAbs aus gesehen)
        double cx = mcx - (R - Math.Abs(h)) * Math.Sign(h) * perpX;
        double cy = mcy - (R - Math.Abs(h)) * Math.Sign(h) * perpY;

        // Start- und Endwinkel
        double a1 = Math.Atan2(p1.y - cy, p1.x - cx);
        double a2 = Math.Atan2(p2.y - cy, p2.x - cx);
        double am = Math.Atan2(midAbs.y - cy, midAbs.x - cx); // Bogenmitte-Winkel

        // Sweep-Richtung: CCW wenn h > 0 (Bogenmitte links), CW sonst
        bool ccw = h > 0;
        // Normalisierung: a2 muss nach a1 in der Sweep-Richtung liegen
        if (ccw && a2 < a1) a2 += 2 * Math.PI;
        if (!ccw && a2 > a1) a2 -= 2 * Math.PI;

        // Prüfe ob Bogenmitte zwischen a1 und a2 liegt (Sanity-Check für Richtung)
        double amAdj = am;
        if (ccw && amAdj < a1) amAdj += 2 * Math.PI;
        if (!ccw && amAdj > a1) amAdj -= 2 * Math.PI;
        if ((ccw && amAdj > a2) || (!ccw && amAdj < a2))
        {
            // Richtung umkehren
            ccw = !ccw;
            a2 = Math.Atan2(p2.y - cy, p2.x - cx);
            if (ccw && a2 < a1) a2 += 2 * Math.PI;
            if (!ccw && a2 > a1) a2 -= 2 * Math.PI;
        }

        // Kardinalwinkel prüfen (0°, 90°, 180°, 270°)
        double[] cardinals = { 0, Math.PI / 2, Math.PI, 3 * Math.PI / 2 };
        foreach (double card in cardinals)
        {
            // Normalisiere card relativ zu a1
            double ca = card;
            if (ccw) { while (ca < a1) ca += 2 * Math.PI; }
            else      { while (ca > a1) ca -= 2 * Math.PI; }
            bool inArc = ccw ? (ca >= a1 && ca <= a2) : (ca <= a1 && ca >= a2);
            if (inArc)
                ExpandBBox(ref minX, ref minY, ref maxX, ref maxY,
                    cx + R * Math.Cos(card), cy + R * Math.Sin(card));
        }
    }

    private (double minX, double minY, double maxX, double maxY)? GetChainBBox(int startIdx)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        for (int i = startIdx; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp) break;
            if (i > startIdx && pp.Typ == PfadPunktTyp.Start) break;
            var abs = GetPfadAbsAt(i);
            if (abs == null) continue;
            ExpandBBox(ref minX, ref minY, ref maxX, ref maxY, abs.Value.x, abs.Value.y);
            any = true;

            // Für Bogen-Segmente: exakte Arc-BBox einbeziehen
            if (pp.Typ == PfadPunktTyp.Bogen && i > startIdx)
            {
                var p1abs = GetPfadAbsAt(i - 1);
                if (p1abs.HasValue)
                {
                    if (pp.BogenModus == "Bogenmitte")
                    {
                        // Kreiszentrum aus gespeichertem Offset berechnen
                        (double cx, double cy) arcCtr;
                        if (pp.Bezugspunkt == "Letzter Punkt")
                            arcCtr = (p1abs.Value.x + pp.XMid, p1abs.Value.y + pp.YMid);
                        else
                            arcCtr = GCodeGenerator.ConvertBezugspunkt(pp.Bezugspunkt, pp.XMid, pp.YMid, WorkX, WorkY);
                        double R = Math.Sqrt(Math.Pow(p1abs.Value.x - arcCtr.cx, 2) + Math.Pow(p1abs.Value.y - arcCtr.cy, 2));
                        if (R > 1e-10)
                        {
                            // Bogenmitte-Punkt: auf Arc zwischen p1 und p2, auf der gegenüberliegenden Seite vom Zentrum zur Sehne
                            double mcx = (p1abs.Value.x + abs.Value.x) / 2, mcy = (p1abs.Value.y + abs.Value.y) / 2;
                            double dcx = mcx - arcCtr.cx, dcy = mcy - arcCtr.cy;
                            double dcLen = Math.Sqrt(dcx * dcx + dcy * dcy);
                            (double x, double y) arcApex = dcLen > 1e-10
                                ? (arcCtr.cx + R * dcx / dcLen, arcCtr.cy + R * dcy / dcLen)
                                : (arcCtr.cx + R, arcCtr.cy);
                            ExpandBBoxForArc(ref minX, ref minY, ref maxX, ref maxY,
                                p1abs.Value, abs.Value, arcApex);
                        }
                    }
                    else
                    {
                        var midAbs = GetPfadSegMidAbs(i);
                        if (midAbs.HasValue)
                            ExpandBBoxForArc(ref minX, ref minY, ref maxX, ref maxY,
                                p1abs.Value, abs.Value, midAbs.Value);
                    }
                }
            }
        }
        if (!any) return null;
        return (minX, minY, maxX, maxY);
    }

    // Trifft Cursor eine Pfad-Kette (Cursor innerhalb Bounding Box)?
    private int HitTestPfadChainBBox(double mmX, double mmY)
    {
        double padMm = 4.0 / _zoom;
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp || pp.Typ != PfadPunktTyp.Start) continue;
            var bbox = GetChainBBox(i);
            if (bbox == null) continue;
            if (mmX >= bbox.Value.minX - padMm && mmX <= bbox.Value.maxX + padMm &&
                mmY >= bbox.Value.minY - padMm && mmY <= bbox.Value.maxY + padMm)
                return i;
        }
        return -1;
    }

    private void StartMovePfadChain(int startIdx, double mmX, double mmY)
    {
        _pfadChainDragIdx   = startIdx;
        _pfadChainDragMouse = (mmX, mmY);
        _pfadChainDragOrigAbs.Clear();
        for (int i = startIdx; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp) break;
            if (i > startIdx && pp.Typ == PfadPunktTyp.Start) break;
            _pfadChainDragOrigAbs.Add(GetPfadAbsAt(i) ?? (0, 0));
        }
        HistoryList.SelectedItem    = _history[startIdx];
        TabEigenschaften.IsSelected = true;
    }

    private void UpdateMovePfadChain(double mmX, double mmY)
    {
        if (_pfadChainDragIdx < 0) return;
        double dx = SnapX(mmX) - SnapX(_pfadChainDragMouse.x);
        double dy = SnapY(mmY) - SnapY(_pfadChainDragMouse.y);
        _suppressHistoryRegen = true;
        try
        {
            int local = 0;
            for (int i = _pfadChainDragIdx; i < _history.Count && local < _pfadChainDragOrigAbs.Count; i++, local++)
            {
                if (_history[i].Params is not PfadPunktParams p) break;
                if (p.Bezugspunkt == "Letzter Punkt" && local > 0) continue; // relative bleibt gleich
                var orig = _pfadChainDragOrigAbs[local];
                var (newX, newY) = AbsToRel(p.Bezugspunkt, orig.x + dx, orig.y + dy, WorkX, WorkY);
                _history[i] = new HistoryEntry(_history[i].Label, _history[i].Details,
                    p with { XRel = Math.Round(newX, 3), YRel = Math.Round(newY, 3) }, _history[i].Level);
            }
        }
        finally { _suppressHistoryRegen = false; }
        DrawSkia?.InvalidateVisual();
    }

    private void CommitMovePfadChain()
    {
        if (_pfadChainDragIdx < 0) return;
        int idx = _pfadChainDragIdx;
        _pfadChainDragIdx = -1;
        _pfadChainDragOrigAbs.Clear();
        _suppressNextAutoFit = true;
        PropagateVermConstraints();
        CheckAndReportConstraints();
        HistoryList.SelectedItem = _history[idx];
        UpdateAll();
    }

    // Treffertest: Pfad-Punkt in der Nähe von (mmX, mmY)? Gibt History-Index zurück.
    private int HitTestPfadPunkt(double mmX, double mmY)
    {
        double tol = 5.0 / _zoom; // 5 Pixel Toleranz
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams) continue;
            var abs = GetPfadAbsAt(i);
            if (abs == null) continue;
            double dx = abs.Value.x - mmX, dy = abs.Value.y - mmY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < tol && dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    // Midpoint des Segments, das bei p2Idx endet
    private (double x, double y)? GetPfadSegMidAbs(int p2Idx)
    {
        if (p2Idx <= 0 || p2Idx >= _history.Count) return null;
        if (_history[p2Idx].Params is not PfadPunktParams p2) return null;
        var p2abs = GetPfadAbsAt(p2Idx);
        var p1abs = GetPfadAbsAt(p2Idx - 1);
        if (!p2abs.HasValue || !p1abs.HasValue) return null;

        if (p2.Typ == PfadPunktTyp.Bogen && p2.BogenModus == "Pfeilhöhe")
        {
            double ddx = p2abs.Value.x - p1abs.Value.x, ddy = p2abs.Value.y - p1abs.Value.y;
            double len = Math.Sqrt(ddx*ddx + ddy*ddy);
            if (len > 1e-10)
            {
                double px = -ddy / len, py = ddx / len;
                return ((p1abs.Value.x + p2abs.Value.x) / 2 + p2.XMid * px,
                        (p1abs.Value.y + p2abs.Value.y) / 2 + p2.XMid * py);
            }
        }
        return ((p1abs.Value.x + p2abs.Value.x) / 2, (p1abs.Value.y + p2abs.Value.y) / 2);
    }

    // Treffertest: Segment-Mittelpunkt
    private int HitTestPfadSegMid(double mmX, double mmY)
    {
        double tol = 6.0 / _zoom;
        int best = -1; double bestDist = double.MaxValue;
        for (int i = 1; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams p2) continue;
            if (p2.Typ == PfadPunktTyp.Start) continue;
            var mid = GetPfadSegMidAbs(i);
            if (mid == null) continue;
            double dx = mid.Value.x - mmX, dy = mid.Value.y - mmY;
            double dist = Math.Sqrt(dx*dx + dy*dy);
            if (dist < tol && dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    // Umkehrung von ConvertBezugspunkt: Absolut → XRel/YRel für gegebenen Bezug
    private static (double xRel, double yRel) InverseBezugspunkt(
        string ref_, double absX, double absY, double w, double h)
        => ref_ switch
        {
            "Unten links"  => (absX,         absY),
            "Oben links"   => (absX,         h - absY),
            "Unten rechts" => (w - absX,     absY),
            "Oben rechts"  => (w - absX,     h - absY),
            "Links Mitte"  => (absX,         absY - h / 2),
            "Rechts Mitte" => (w - absX,     absY - h / 2),
            "Oben Mitte"   => (absX - w / 2, h - absY),
            "Unten Mitte"  => (absX - w / 2, absY),
            "Mitte"        => (absX - w / 2, absY - h / 2),
            _              => (absX,         absY)
        };

    // Grenzen der Pfad-Kette, die idx enthält (Start-Idx, letzter Idx)
    private (int startIdx, int endIdx) FindChainBounds(int idx)
    {
        int start = idx;
        for (int i = idx; i >= 0; i--)
        {
            if (_history[i].Params is PfadPunktParams p) { if (p.Typ == PfadPunktTyp.Start) { start = i; break; } }
            else break;
        }
        int end = start;
        for (int i = start + 1; i < _history.Count; i++)
        {
            if (_history[i].Params is PfadPunktParams np && np.Typ != PfadPunktTyp.Start) end = i;
            else break;
        }
        return (start, end);
    }

    // True wenn Start- und Endpunkt der Kette am selben Ort liegen
    private bool IsChainClosed(int startIdx, int endIdx)
    {
        if (startIdx >= endIdx) return false;
        var a = GetPfadAbsAt(startIdx);
        var b = GetPfadAbsAt(endIdx);
        if (!a.HasValue || !b.HasValue) return false;
        double dx = a.Value.x - b.Value.x, dy = a.Value.y - b.Value.y;
        return (dx * dx + dy * dy) < 0.01 * 0.01;
    }

    // True wenn a und b dieselbe Pfad-Ecke sind — entweder identischer Index, oder
    // Start-/Endpunkt einer geschlossenen Kette (die zeichnerisch am selben Ort liegen,
    // aber unterschiedliche History-Indizes haben). Ohne diese Prüfung erkennt z.B.
    // ApplyAngleConstraint den gemeinsamen Eckpunkt zwischen der letzten und der ersten
    // Linie eines geschlossenen Pfades nicht als "geteilt" und wählt über die
    // Schnittpunkt-Heuristik den falschen Pivot — wodurch am Ende der eigentlich fixe
    // Startpunkt des Pfades mitverschoben wird.
    private bool SamePathCorner(int a, int b)
    {
        if (a == b) return true;
        if (a < 0 || b < 0) return false;
        var (stA, enA) = FindChainBounds(a);
        var (stB, enB) = FindChainBounds(b);
        if (stA != stB || enA != enB) return false;
        if (!IsChainClosed(stA, enA)) return false;
        return (a == stA && b == enA) || (a == enA && b == stA);
    }

    // True wenn idx der Start- oder Endpunkt einer GESCHLOSSENEN Kette ist. Diese beiden Indizes
    // liegen zeichnerisch am selben Ort, sind aber zwei unterschiedliche History-Einträge — und
    // UpdatePfadPunktPos führt beim Verschieben des einen automatisch auch den anderen synchron
    // mit ("Partner-Punkt", siehe dort). Ein Constraint-Apply, der einen dieser beiden Indizes
    // hart verschiebt, verschiebt also unbemerkt auch den jeweils anderen — obwohl der oft als
    // fixer Anker für ganz andere Vermassungen (z.B. eine andere Length- oder EdgeDist-Vermassung)
    // dient. Wird das nicht erkannt, "wandert" der Anker bei völlig unabhängigen Constraint-Edits.
    private bool IsClosedChainEndpoint(int idx)
    {
        if (idx < 0) return false;
        var (st, en) = FindChainBounds(idx);
        if (st == en) return false;
        if (!IsChainClosed(st, en)) return false;
        return idx == st || idx == en;
    }

    // Pfad-Punkt auf neue absolute Position verschieben
    // preserveFollowers = true  → nachfolgende "Letzter Punkt"-Punkte werden auf ihrer
    //                             absoluten Position eingefroren (Drag-Verhalten).
    // preserveFollowers = false → nachfolgende Punkte folgen dem verschobenen Punkt
    //                             automatisch (relative Koordinaten bleiben unverändert).
    //                             Verwenden für alle Vermassen-Constraints, damit der
    //                             Pfad nach dem vermassten Punkt seine Form behält
    //                             und nicht eingefroren/verzerrt wird.
    private void UpdatePfadPunktPos(int idx, double newAbsX, double newAbsY,
                                    bool preserveFollowers = true, bool onlyThisPoint = false)
    {
        if (idx < 0 || idx >= _history.Count) return;
        if (_history[idx].Params is not PfadPunktParams pfad) return;

        string MkDet(PfadPunktParams p) => p.Typ switch
        {
            PfadPunktTyp.Bogen => (p.BogenModus == "Bogenmitte"
                ? $"X={p.XRel} Y={p.YRel}, M={p.XMid}/{p.YMid}"
                : $"X={p.XRel} Y={p.YRel}, {p.BogenModus}={p.XMid}"),
            PfadPunktTyp.Linie => $"X={p.XRel} Y={p.YRel}",
            _                  => $"X={p.XRel} Y={p.YRel}, Z={p.ZTiefe}"
        };
        string MkLbl(int i, PfadPunktParams p) => p.Typ switch
        {
            PfadPunktTyp.Bogen => $"Pfad Bogen #{PfadPunktNummer(i)}",
            PfadPunktTyp.Linie => $"Pfad Linie #{PfadPunktNummer(i)}",
            _                  => "Pfad Start"
        };

        // Absolutpositionen aller nachfolgenden "Letzter Punkt"-Punkte VOR der Änderung sichern.
        // Nur solange die Kette ununterbrochen "Letzter Punkt" verwendet; erstes anderes Bezug
        // bricht die direkte Abhängigkeit.
        // Bei preserveFollowers=false werden die Folge-Punkte NICHT eingefroren —
        // sie folgen dem verschobenen Punkt automatisch über ihre relativen Koordinaten.
        // Bei onlyThisPoint=true wird nur dieser eine Punkt geändert, nichts weiter.
        var follow = new List<(int j, double ax, double ay, double mx, double my)>();
        if (preserveFollowers && !onlyThisPoint)
        {
            for (int j = idx + 1; j < _history.Count; j++)
            {
                if (_history[j].Params is not PfadPunktParams nxt) break;
                if (nxt.Bezugspunkt != "Letzter Punkt") break;
                var jAbs = GetPfadAbsAt(j);
                if (!jAbs.HasValue) break;
                double mAbsX = 0, mAbsY = 0;
                if (nxt.Typ == PfadPunktTyp.Bogen && nxt.BogenModus == "Bogenmitte")
                {
                    var prevA = GetPfadAbsAt(j - 1);
                    if (prevA.HasValue) { mAbsX = prevA.Value.x + nxt.XMid; mAbsY = prevA.Value.y + nxt.YMid; }
                }
                follow.Add((j, jAbs.Value.x, jAbs.Value.y, mAbsX, mAbsY));
            }
        }

        // Neues XRel/YRel für den verschobenen Punkt berechnen
        double w = WorkX, h = WorkY;
        double xRel, yRel;
        if (pfad.Bezugspunkt == "Letzter Punkt" && idx > 0)
        {
            var prevAbs = GetPfadAbsAt(idx - 1);
            xRel = prevAbs.HasValue ? Math.Round(newAbsX - prevAbs.Value.x, 3) : Math.Round(newAbsX, 3);
            yRel = prevAbs.HasValue ? Math.Round(newAbsY - prevAbs.Value.y, 3) : Math.Round(newAbsY, 3);
        }
        else
        {
            (xRel, yRel) = InverseBezugspunkt(pfad.Bezugspunkt, newAbsX, newAbsY, w, h);
            xRel = Math.Round(xRel, 3);
            yRel = Math.Round(yRel, 3);
        }
        var np = pfad with { XRel = xRel, YRel = yRel };

        // Geschlossener Pfad: vor jeder Änderung prüfen (IsChainClosed verlässt sich auf unveränderte History)
        var (chainSt, chainEn) = FindChainBounds(idx);
        bool closedPath = chainSt != chainEn && IsChainClosed(chainSt, chainEn);
        var origChainStAbs = (closedPath && idx == chainEn) ? GetPfadAbsAt(chainSt) : null;

        _eigSuppressUpdate    = true;
        _suppressHistoryRegen = true;
        try
        {
            _history[idx] = new HistoryEntry(MkLbl(idx, np), MkDet(np), np, _history[idx].Level);

            // Jeden abhängigen Folge-Punkt auf seine gespeicherte Absolutposition zurücksetzen.
            // prevX/prevY = Absolutposition des gerade vorangegangenen Punkts (nach Anpassung).
            double prevX = newAbsX, prevY = newAbsY;
            foreach (var (j, ax, ay, mx, my) in follow)
            {
                if (_history[j].Params is not PfadPunktParams nxt) break;
                double nxRel = Math.Round(ax - prevX, 3);
                double nyRel = Math.Round(ay - prevY, 3);
                double nxMid = nxt.XMid, nyMid = nxt.YMid;
                if (nxt.Typ == PfadPunktTyp.Bogen && nxt.BogenModus == "Bogenmitte")
                {
                    nxMid = Math.Round(mx - prevX, 3);
                    nyMid = Math.Round(my - prevY, 3);
                }
                var nxtNp = nxt with { XRel = nxRel, YRel = nyRel, XMid = nxMid, YMid = nyMid };
                _history[j] = new HistoryEntry(MkLbl(j, nxtNp), MkDet(nxtNp), nxtNp, _history[j].Level);
                prevX = ax; prevY = ay; // Absolutposition dieses Punkts als Referenz für den Nächsten
            }

            // Geschlossener Pfad: Partner-Punkt synchron mitführen
            if (closedPath)
            {
                if (idx == chainSt)
                {
                    if (_history[chainEn].Params is PfadPunktParams ep)
                    {
                        double exRel, eyRel;
                        if (ep.Bezugspunkt == "Letzter Punkt")
                        {
                            var prevA = GetPfadAbsAt(chainEn - 1);
                            exRel = Math.Round(newAbsX - (prevA?.x ?? 0), 3);
                            eyRel = Math.Round(newAbsY - (prevA?.y ?? 0), 3);
                        }
                        else { (exRel, eyRel) = InverseBezugspunkt(ep.Bezugspunkt, newAbsX, newAbsY, w, h); exRel = Math.Round(exRel, 3); eyRel = Math.Round(eyRel, 3); }
                        var enp = ep with { XRel = exRel, YRel = eyRel };
                        _history[chainEn] = new HistoryEntry(MkLbl(chainEn, enp), MkDet(enp), enp, _history[chainEn].Level);
                    }
                }
                else if (idx == chainEn && _history[chainSt].Params is PfadPunktParams sp)
                {
                    // sp.XRel/YRel sind noch die Original-Werte (sp wurde vor der Überschreibung captured)
                    var (origSX, origSY) = GCodeGenerator.ConvertBezugspunkt(sp.Bezugspunkt, sp.XRel, sp.YRel, w, h);
                    var (sxRel, syRel) = InverseBezugspunkt(sp.Bezugspunkt, newAbsX, newAbsY, w, h);
                    var snp = sp with { XRel = Math.Round(sxRel, 3), YRel = Math.Round(syRel, 3) };
                    _history[chainSt] = new HistoryEntry(MkLbl(chainSt, snp), MkDet(snp), snp, _history[chainSt].Level);
                    // chainSt+1 mit "Letzter Punkt" auf absolute Position einfrieren
                    int nextSt = chainSt + 1;
                    if (nextSt < chainEn && _history[nextSt].Params is PfadPunktParams np2 && np2.Bezugspunkt == "Letzter Punkt")
                    {
                        double absNextX = origSX + np2.XRel;
                        double absNextY = origSY + np2.YRel;
                        var nn = np2 with { XRel = Math.Round(absNextX - newAbsX, 3), YRel = Math.Round(absNextY - newAbsY, 3) };
                        _history[nextSt] = new HistoryEntry(MkLbl(nextSt, nn), MkDet(nn), nn, _history[nextSt].Level);
                    }
                }
            }
        }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }

        _suppressNextAutoFit = true; // Zoom während Drag beibehalten
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = idx;
        UpdateEigenschaften();
    }

    // Liniensegment (beide Endpunkte) um delta verschieben
    private void MovePfadLinienSegment(int p2Idx, double mmX, double mmY)
    {
        int p1Idx = p2Idx - 1;
        if (p1Idx < 0) return;
        if (_history[p1Idx].Params is not PfadPunktParams p1) return;
        if (_history[p2Idx].Params is not PfadPunktParams p2) return;

        double dx = mmX - _pfadSegDragMouse.x, dy = mmY - _pfadSegDragMouse.y;
        double newP1X = _pfadSegDragP1.x + dx, newP1Y = _pfadSegDragP1.y + dy;
        double newP2X = _pfadSegDragP2.x + dx, newP2Y = _pfadSegDragP2.y + dy;
        double w = WorkX, h = WorkY;

        double p1XR, p1YR;
        if (p1.Bezugspunkt == "Letzter Punkt" && p1Idx > 0)
        {
            var prev = GetPfadAbsAt(p1Idx - 1);
            p1XR = Math.Round(newP1X - (prev?.x ?? 0), 3);
            p1YR = Math.Round(newP1Y - (prev?.y ?? 0), 3);
        }
        else { (p1XR, p1YR) = InverseBezugspunkt(p1.Bezugspunkt, newP1X, newP1Y, w, h); p1XR = Math.Round(p1XR, 3); p1YR = Math.Round(p1YR, 3); }

        // p2 "Letzter Punkt": beide Punkte bewegen sich gleich → XRel bleibt unverändert
        double p2XR = p2.Bezugspunkt == "Letzter Punkt"
            ? p2.XRel : Math.Round(InverseBezugspunkt(p2.Bezugspunkt, newP2X, newP2Y, w, h).xRel, 3);
        double p2YR = p2.Bezugspunkt == "Letzter Punkt"
            ? p2.YRel : Math.Round(InverseBezugspunkt(p2.Bezugspunkt, newP2X, newP2Y, w, h).yRel, 3);

        // Follow-Punkte nach p2 (absolute Positionen einfrieren)
        var follow = new List<(int j, double ax, double ay, double mx, double my, PfadPunktParams np)>();
        for (int j = p2Idx + 1; j < _history.Count; j++)
        {
            if (_history[j].Params is not PfadPunktParams nxt) break;
            if (nxt.Bezugspunkt != "Letzter Punkt") break;
            var jAbs = GetPfadAbsAt(j); if (!jAbs.HasValue) break;
            double mAbsX = 0, mAbsY = 0;
            if (nxt.Typ == PfadPunktTyp.Bogen && nxt.BogenModus == "Bogenmitte")
            {
                var prevA = GetPfadAbsAt(j - 1);
                if (prevA.HasValue) { mAbsX = prevA.Value.x + nxt.XMid; mAbsY = prevA.Value.y + nxt.YMid; }
            }
            follow.Add((j, jAbs.Value.x, jAbs.Value.y, mAbsX, mAbsY, nxt));
        }

        string MkLbl(int i, PfadPunktParams p) => p.Typ switch { PfadPunktTyp.Bogen => $"Pfad Bogen #{PfadPunktNummer(i)}", PfadPunktTyp.Linie => $"Pfad Linie #{PfadPunktNummer(i)}", _ => "Pfad Start" };
        string MkDet(PfadPunktParams p) => p.Typ switch
        {
            PfadPunktTyp.Bogen => p.BogenModus == "Bogenmitte" ? $"X={p.XRel} Y={p.YRel}, M={p.XMid}/{p.YMid}" : $"X={p.XRel} Y={p.YRel}, {p.BogenModus}={p.XMid}",
            PfadPunktTyp.Linie => $"X={p.XRel} Y={p.YRel}",
            _ => $"X={p.XRel} Y={p.YRel}, Z={p.ZTiefe}"
        };

        var (chainSt, chainEn) = FindChainBounds(p2Idx);
        bool closedPath = chainSt != chainEn && IsChainClosed(chainSt, chainEn);
        // Original-Absolutposition des Startpunkts vor jeder Änderung sichern
        var origChainStAbs = closedPath ? GetPfadAbsAt(chainSt) : null;

        _eigSuppressUpdate = true; _suppressHistoryRegen = true;
        try
        {
            var np1 = p1 with { XRel = p1XR, YRel = p1YR };
            _history[p1Idx] = new HistoryEntry(MkLbl(p1Idx, np1), MkDet(np1), np1, _history[p1Idx].Level);
            var np2 = p2 with { XRel = p2XR, YRel = p2YR };
            _history[p2Idx] = new HistoryEntry(MkLbl(p2Idx, np2), MkDet(np2), np2, _history[p2Idx].Level);
            double prevX = newP2X, prevY = newP2Y;
            foreach (var (j, ax, ay, mx, my, nxt) in follow)
            {
                double nxMid = nxt.XMid, nyMid = nxt.YMid;
                if (nxt.Typ == PfadPunktTyp.Bogen && nxt.BogenModus == "Bogenmitte")
                { nxMid = Math.Round(mx - prevX, 3); nyMid = Math.Round(my - prevY, 3); }
                var np = nxt with { XRel = Math.Round(ax - prevX, 3), YRel = Math.Round(ay - prevY, 3), XMid = nxMid, YMid = nyMid };
                _history[j] = new HistoryEntry(MkLbl(j, np), MkDet(np), np, _history[j].Level);
                prevX = ax; prevY = ay;
            }

            if (closedPath)
            {
                if (p1Idx == chainSt) // erste Linie: Endpunkt auf neues p1 legen
                {
                    if (_history[chainEn].Params is PfadPunktParams ep)
                    {
                        double exRel, eyRel;
                        if (ep.Bezugspunkt == "Letzter Punkt")
                        {
                            var prevA = GetPfadAbsAt(chainEn - 1);
                            exRel = Math.Round(newP1X - (prevA?.x ?? 0), 3);
                            eyRel = Math.Round(newP1Y - (prevA?.y ?? 0), 3);
                        }
                        else { (exRel, eyRel) = InverseBezugspunkt(ep.Bezugspunkt, newP1X, newP1Y, w, h); exRel = Math.Round(exRel, 3); eyRel = Math.Round(eyRel, 3); }
                        var enp = ep with { XRel = exRel, YRel = eyRel };
                        _history[chainEn] = new HistoryEntry(MkLbl(chainEn, enp), MkDet(enp), enp, _history[chainEn].Level);
                    }
                }
                else if (p2Idx == chainEn && _history[chainSt].Params is PfadPunktParams sp) // letzte Linie: Startpunkt auf neues p2 legen
                {
                    var (sxRel, syRel) = InverseBezugspunkt(sp.Bezugspunkt, newP2X, newP2Y, w, h);
                    var snp = sp with { XRel = Math.Round(sxRel, 3), YRel = Math.Round(syRel, 3) };
                    _history[chainSt] = new HistoryEntry(MkLbl(chainSt, snp), MkDet(snp), snp, _history[chainSt].Level);
                    int nextSt = chainSt + 1;
                    if (nextSt < p1Idx && origChainStAbs.HasValue &&
                        _history[nextSt].Params is PfadPunktParams np3 && np3.Bezugspunkt == "Letzter Punkt")
                    {
                        double absNextX = origChainStAbs.Value.x + np3.XRel;
                        double absNextY = origChainStAbs.Value.y + np3.YRel;
                        var nn = np3 with { XRel = Math.Round(absNextX - newP2X, 3), YRel = Math.Round(absNextY - newP2Y, 3) };
                        _history[nextSt] = new HistoryEntry(MkLbl(nextSt, nn), MkDet(nn), nn, _history[nextSt].Level);
                    }
                }
            }
        }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
    }

    // Bogensegment: Pfeilhöhe über Drag-Position ändern
    private void UpdateBogenPfeilhoehe(int p2Idx, double mmX, double mmY)
    {
        if (_history[p2Idx].Params is not PfadPunktParams p2) return;
        if (p2.Typ != PfadPunktTyp.Bogen) return;

        double chDx = _pfadSegDragP2.x - _pfadSegDragP1.x, chDy = _pfadSegDragP2.y - _pfadSegDragP1.y;
        double chLen = Math.Sqrt(chDx*chDx + chDy*chDy);
        if (chLen < 1e-10) return;

        double pX = -chDy / chLen, pY = chDx / chLen; // links der Fahrrichtung
        double mcX = (_pfadSegDragP1.x + _pfadSegDragP2.x) / 2;
        double mcY = (_pfadSegDragP1.y + _pfadSegDragP2.y) / 2;
        double newH = Math.Round((mmX - mcX) * pX + (mmY - mcY) * pY, 3);

        string MkDet(PfadPunktParams p) => $"X={p.XRel} Y={p.YRel}, {p.BogenModus}={p.XMid}";

        _eigSuppressUpdate = true; _suppressHistoryRegen = true;
        try
        {
            var np = p2 with { BogenModus = "Pfeilhöhe", XMid = newH, YMid = 0 };
            _history[p2Idx] = new HistoryEntry($"Pfad Bogen #{PfadPunktNummer(p2Idx)}", MkDet(np), np, _history[p2Idx].Level);
        }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        UpdateEigenschaften();
    }

    // Pfad-Punkte als Dots zeichnen (für Move-Werkzeug)
    private void DrawPfadChainBBoxes(SKCanvas canvas)
    {
        if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return;
        double sc  = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
        float  pad = (float)(4.0 / _zoom);

        bool dragging = _pfadChainDragIdx >= 0 || _pfadScaleChainIdx >= 0;
        var boxColor    = dragging ? new SKColor(255, 160, 0, 200) : new SKColor(30, 120, 220, 180);
        var anchorColor = dragging ? new SKColor(255, 160, 0, 230) : new SKColor(30, 120, 220, 220);

        using var boxPaint = new SKPaint
        {
            Color = boxColor, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(1.2 / _zoom), IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { (float)(6 / _zoom), (float)(3 / _zoom) }, 0)
        };
        using var aFill   = new SKPaint { Color = anchorColor, Style = SKPaintStyle.Fill,   IsAntialias = true };
        using var aStroke = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(0.9 / _zoom), IsAntialias = true };

        using var aHotFill = new SKPaint { Color = new SKColor(255, 220, 0, 255), Style = SKPaintStyle.Fill, IsAntialias = true };

        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp || pp.Typ != PfadPunktTyp.Start) continue;
            var bboxOpt = GetChainBBox(i);
            if (bboxOpt == null) continue;
            var (minX, minY, maxX, maxY) = bboxOpt.Value;

            float L = (float)(_topRect.Left   + minX * sc) - pad;
            float R = (float)(_topRect.Left   + maxX * sc) + pad;
            float T = (float)(_topRect.Bottom - maxY * sc) - pad;
            float B = (float)(_topRect.Bottom - minY * sc) + pad;

            canvas.DrawRect(L, T, R - L, B - T, boxPaint);

            float as_ = (float)(4.5 / _zoom);
            float mH  = (L + R) / 2f, mV = (T + B) / 2f;
            float[] axs = { L, mH, R, L, R, L, mH, R };
            float[] ays = { T, T,  T, mV, mV, B, B, B };
            for (int a = 0; a < 8; a++)
            {
                bool hot = _pfadScaleChainIdx == i && _pfadScaleAnchor == a;
                canvas.DrawRect(axs[a] - as_/2, ays[a] - as_/2, as_, as_, hot ? aHotFill : aFill);
                canvas.DrawRect(axs[a] - as_/2, ays[a] - as_/2, as_, as_, aStroke);
            }
        }
    }

    // ── Geometrie-Constraint-Symbole zeichnen ──────────────────────────────
    private void DrawGeomConstraintSymbols(SKCanvas canvas)
    {
        if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return;
        (float x, float y) Px(double mx, double my) => (
            (float)(_topRect.Left + mx * Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY)),
            (float)(_topRect.Bottom - my * Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY)));

        float symR  = (float)(9.0 / _zoom);  // Symbolradius in px (größer)
        float thick = (float)(1.8 / _zoom);

        using var symPaint = new SKPaint
        {
            Color = new SKColor(50, 180, 80, 230), Style = SKPaintStyle.Stroke,
            StrokeWidth = thick, IsAntialias = true
        };
        using var symFill = new SKPaint
        {
            Color = new SKColor(50, 180, 80, 60), Style = SKPaintStyle.Fill, IsAntialias = true
        };
        using var selPaint = new SKPaint
        {
            Color = new SKColor(255, 140, 0, 240), Style = SKPaintStyle.Stroke,
            StrokeWidth = thick, IsAntialias = true
        };
        using var selFill = new SKPaint
        {
            Color = new SKColor(255, 140, 0, 80), Style = SKPaintStyle.Fill, IsAntialias = true
        };
        using var refHighlight = new SKPaint
        {
            Color = new SKColor(255, 160, 0, 220), Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(3.5 / _zoom), IsAntialias = true, StrokeCap = SKStrokeCap.Round
        };

        // Hebt die Linie(n)/Punkte hervor, auf die ein ausgewähltes Constraint-Symbol verweist —
        // damit beim Klick auf z.B. das Parallel-Symbol sofort sichtbar ist, welche Linie(n)
        // gemeint sind.
        void HighlightSeg(int hp1, int hp2)
        {
            var ha1 = GetPfadAbsAt(hp1); var ha2 = GetPfadAbsAt(hp2);
            if (ha1 == null || ha2 == null) return;
            var (hx1, hy1) = Px(ha1.Value.x, ha1.Value.y);
            var (hx2, hy2) = Px(ha2.Value.x, ha2.Value.y);
            canvas.DrawLine(hx1, hy1, hx2, hy2, refHighlight);
        }
        void HighlightPoint(int idx)
        {
            var pa = GetPfadAbsAt(idx);
            if (pa == null) return;
            var (px, py) = Px(pa.Value.x, pa.Value.y);
            canvas.DrawCircle(px, py, symR * 0.9f, refHighlight);
        }
        void HighlightEdge(int edgeId)
        {
            if (edgeId <= 0) return;
            var (ax, ay) = edgeId == 1 ? Px(0, 0) : edgeId == 2 ? Px(WorkX, 0) : edgeId == 3 ? Px(0, 0) : Px(0, WorkY);
            var (bx, by) = edgeId == 1 ? Px(0, WorkY) : edgeId == 2 ? Px(WorkX, WorkY) : edgeId == 3 ? Px(WorkX, 0) : Px(WorkX, WorkY);
            canvas.DrawLine(ax, ay, bx, by, refHighlight);
        }
        void HighlightCorner(int cornerId)
        {
            if (cornerId <= 0) return;
            var (cmx, cmy) = WorkpieceCornerPos(cornerId);
            var (px, py) = Px(cmx, cmy);
            canvas.DrawCircle(px, py, symR * 0.9f, refHighlight);
        }

        for (int ei = 0; ei < _vermPlaced.Count; ei++)
        {
            var en  = _vermPlaced[ei];
            bool isSel = (ei == _selectedGeomIdx);
            var sp = isSel ? selPaint : symPaint;
            var sf = isSel ? selFill  : symFill;

            if (isSel)
            {
                switch (en.Kind)
                {
                    case VermKind.ParallelEdge:
                    case VermKind.PerpendicularEdge:
                        HighlightSeg(en.P1Idx, en.P2Idx);
                        HighlightEdge(en.Edge);
                        break;
                    case VermKind.Perpendicular:
                    case VermKind.Parallel:
                        HighlightSeg(en.P1Idx, en.P2Idx);
                        HighlightSeg(en.Q1Idx, en.Q2Idx);
                        break;
                    case VermKind.Coincident:
                        HighlightPoint(en.P1Idx);
                        HighlightPoint(en.P2Idx);
                        break;
                    case VermKind.CoincidentCorner:
                        HighlightPoint(en.P2Idx);
                        HighlightCorner(en.Edge);
                        break;
                }
            }

            if (en.Kind == VermKind.ParallelEdge || en.Kind == VermKind.PerpendicularEdge)
            {
                var p1a = GetPfadAbsAt(en.P1Idx); var p2a = GetPfadAbsAt(en.P2Idx);
                if (p1a == null || p2a == null) continue;

                if (en.Kind == VermKind.ParallelEdge)
                {
                    // Segment-Mittelpunkt
                    double mx = (p1a.Value.x + p2a.Value.x) / 2, my = (p1a.Value.y + p2a.Value.y) / 2;
                    var (cx2, cy2) = Px(mx, my);

                    // "="-Symbol (zwei kurze parallele Striche, entlang Segment ausgerichtet), neben
                    // der Konturlinie platziert — auf der Seite, die der versetzten Fräsbahn (Werkzeug-
                    // radiuskorrektur) gegenüberliegt, damit sich Symbol und Bahn nicht überlappen.
                    double ddx = p2a.Value.x - p1a.Value.x, ddy = p2a.Value.y - p1a.Value.y;
                    double l = Math.Sqrt(ddx*ddx+ddy*ddy); if (l < 1e-9) continue;
                    float sz = symR * 0.75f;
                    float ux = (float)(ddx/l), uy = (float)(-ddy/l);
                    float nx4 = -uy, ny4 = ux;
                    var rk4 = GetRadiuskorrekturForSeg(en.P1Idx, en.P2Idx);
                    float sgToolpath4 = rk4 == "Links" ? 1f : rk4 == "Rechts" ? -1f : 0f;
                    float side4 = sgToolpath4 != 0 ? sgToolpath4 : 1f;
                    float baseOff = symR * 1.5f, gap = symR * 0.5f;
                    foreach (float k in new float[]{0f, 1f})
                    {
                        float dist = side4 * (baseOff + k * gap);
                        float ox = cx2 + nx4 * dist, oy = cy2 + ny4 * dist;
                        canvas.DrawLine(ox - ux * sz, oy - uy * sz, ox + ux * sz, oy + uy * sz, sp);
                    }
                }
                else // PerpendicularEdge — Winkel-Häkchen an der Ecke, Spitze zeigt in die Konturecke
                {
                    double ddx = p2a.Value.x - p1a.Value.x, ddy = p2a.Value.y - p1a.Value.y;
                    double l = Math.Sqrt(ddx*ddx+ddy*ddy); if (l < 1e-9) continue;

                    double DistToEdge((double x, double y) pt) => en.Edge switch
                    {
                        1 => Math.Abs(pt.x),
                        2 => Math.Abs(pt.x - WorkX),
                        3 => Math.Abs(pt.y),
                        4 => Math.Abs(pt.y - WorkY),
                        _ => 0
                    };
                    bool p1IsCorner = DistToEdge(p1a.Value) <= DistToEdge(p2a.Value);
                    var corner = p1IsCorner ? p1a.Value : p2a.Value;
                    var far    = p1IsCorner ? p2a.Value : p1a.Value;
                    var (cx2, cy2) = Px(corner.x, corner.y);
                    var (fx2, fy2) = Px(far.x, far.y);

                    // Schenkel 1: entlang Segment P, weg von der Ecke (reale Richtung, kein Raten).
                    float t1x = fx2 - cx2, t1y = fy2 - cy2;
                    float t1l = (float)Math.Sqrt(t1x*t1x + t1y*t1y); if (t1l < 1e-6) continue;
                    t1x /= t1l; t1y /= t1l;

                    // Schenkel 2: entlang der Werkstückkante (deterministisch aus Kantengeometrie).
                    float t2x = en.Edge is 1 or 2 ? 0f : 1f;
                    float t2y = en.Edge is 1 or 2 ? 1f : 0f;

                    // Winkelspitze (echte 90°, da t1⊥t2) nahe der Ecke, verschoben auf die dem
                    // Werkzeug-Versatz gegenüberliegende Seite (gleiche Logik wie "="-Symbol oben) —
                    // die Schenkel laufen von dort entlang der realen Linien nach aussen, sodass
                    // die Spitze in die Konturecke zeigt und das Symbol wie ein Rechtwinkel aussieht.
                    float bx1 = t1x + t2x, by1 = t1y + t2y;
                    float bl1 = (float)Math.Sqrt(bx1*bx1 + by1*by1); if (bl1 < 1e-6) continue;
                    bx1 /= bl1; by1 /= bl1;
                    var rk4b = GetRadiuskorrekturForSeg(en.P1Idx, en.P2Idx);
                    float sgToolpath4b = rk4b == "Links" ? 1f : rk4b == "Rechts" ? -1f : 0f;
                    float side4b = -(sgToolpath4b != 0 ? sgToolpath4b : 1f);

                    float legLen = symR * 0.9f, tipShift = symR * 1.5f;
                    float tipX  = cx2 + bx1 * side4b * tipShift, tipY = cy2 + by1 * side4b * tipShift;
                    float armX1 = tipX + t1x * legLen, armY1 = tipY + t1y * legLen;
                    float armX2 = tipX + t2x * legLen, armY2 = tipY + t2y * legLen;

                    using var sqPath2 = new SKPath();
                    sqPath2.MoveTo(armX1, armY1);
                    sqPath2.LineTo(tipX, tipY);
                    sqPath2.LineTo(armX2, armY2);
                    canvas.DrawPath(sqPath2, sp);
                }
            }
            else if (en.Kind == VermKind.Coincident)
            {
                var a = GetPfadAbsAt(en.P1Idx); var b = GetPfadAbsAt(en.P2Idx);
                if (a == null || b == null) continue;
                var (ax, ay) = Px(a.Value.x, a.Value.y);
                var (bx, by) = Px(b.Value.x, b.Value.y);
                canvas.DrawCircle(ax, ay, symR * 0.55f, sp);
                canvas.DrawCircle(bx, by, symR * 0.55f, sp);
                if (Math.Abs(ax - bx) > 0.5f || Math.Abs(ay - by) > 0.5f)
                    canvas.DrawLine(ax, ay, bx, by, sp);
            }
            else if (en.Kind == VermKind.CoincidentCorner)
            {
                var ptAbs = GetPfadAbsAt(en.P2Idx);
                if (ptAbs == null) continue;
                var (px2, py2) = Px(ptAbs.Value.x, ptAbs.Value.y);
                canvas.DrawCircle(px2, py2, symR * 0.6f, sp);
                canvas.DrawCircle(px2, py2, symR * 0.3f, sp);
            }
            else if (en.Kind == VermKind.Perpendicular || en.Kind == VermKind.Parallel)
            {
                var p1a = GetPfadAbsAt(en.P1Idx); var p2a = GetPfadAbsAt(en.P2Idx);
                if (p1a == null || p2a == null) continue;
                double ddx = p2a.Value.x - p1a.Value.x, ddy = p2a.Value.y - p1a.Value.y;
                double l = Math.Sqrt(ddx*ddx+ddy*ddy); if (l < 1e-9) continue;

                if (en.Kind == VermKind.Parallel)
                {
                    double mx = (p1a.Value.x + p2a.Value.x) / 2, my = (p1a.Value.y + p2a.Value.y) / 2;
                    var (cx3, cy3) = Px(mx, my);

                    // "="-Symbol wie ParallelEdge — neben der Konturlinie, gegenüber der Fräsbahn
                    float sz = symR * 0.75f;
                    float ux = (float)(ddx/l), uy = (float)(-ddy/l);
                    float nx5 = -uy, ny5 = ux;
                    var rk5 = GetRadiuskorrekturForSeg(en.P1Idx, en.P2Idx);
                    float sgToolpath5 = rk5 == "Links" ? 1f : rk5 == "Rechts" ? -1f : 0f;
                    float side5 = sgToolpath5 != 0 ? sgToolpath5 : 1f;
                    float baseOff5 = symR * 1.5f, gap = symR * 0.5f;
                    foreach (float k in new float[]{0f, 1f})
                    {
                        float dist = side5 * (baseOff5 + k * gap);
                        float ox = cx3 + nx5 * dist, oy = cy3 + ny5 * dist;
                        canvas.DrawLine(ox - ux * sz, oy - uy * sz, ox + ux * sz, oy + uy * sz, sp);
                    }
                }
                else // Perpendicular — Winkel-Häkchen an der gemeinsamen Ecke, Spitze zeigt in die Konturecke
                {
                    // Gemeinsamer Punkt der beiden Segmente = die tatsächliche rechtwinklige Ecke
                    int sharedIdx = en.P1Idx == en.Q1Idx || en.P1Idx == en.Q2Idx ? en.P1Idx
                                  : en.P2Idx == en.Q1Idx || en.P2Idx == en.Q2Idx ? en.P2Idx
                                  : -1;
                    var q1a = GetPfadAbsAt(en.Q1Idx); var q2a = GetPfadAbsAt(en.Q2Idx);
                    var cornerAbs = sharedIdx >= 0 ? GetPfadAbsAt(sharedIdx) : null;
                    (double x, double y) corner = cornerAbs
                        ?? ((p1a.Value.x + p2a.Value.x) / 2, (p1a.Value.y + p2a.Value.y) / 2);
                    (double x, double y) pFar = sharedIdx == en.P1Idx ? p2a.Value
                                               : sharedIdx == en.P2Idx ? p1a.Value : p2a.Value;
                    (double x, double y) qFar = q1a == null || q2a == null ? pFar
                                               : sharedIdx == en.Q1Idx ? q2a.Value
                                               : sharedIdx == en.Q2Idx ? q1a.Value : q2a.Value;
                    var (cx3, cy3) = Px(corner.x, corner.y);
                    var (pfx, pfy) = Px(pFar.x, pFar.y);
                    var (qfx, qfy) = Px(qFar.x, qFar.y);

                    // Schenkel entlang der realen Segmentrichtungen, weg von der Ecke (kein Raten
                    // über eine 90°-Rotation — Q kann unabhängig von P orientiert sein).
                    float t1x = pfx - cx3, t1y = pfy - cy3;
                    float t1l = (float)Math.Sqrt(t1x*t1x + t1y*t1y); if (t1l < 1e-6) continue;
                    t1x /= t1l; t1y /= t1l;
                    float t2x = qfx - cx3, t2y = qfy - cy3;
                    float t2l = (float)Math.Sqrt(t2x*t2x + t2y*t2y);
                    if (t2l < 1e-6) { t2x = -t1y; t2y = t1x; } else { t2x /= t2l; t2y /= t2l; }

                    // Winkelspitze (echte 90°, da t1⊥t2) nahe der Ecke, verschoben auf die dem
                    // Werkzeug-Versatz gegenüberliegende Seite (gleiche Logik wie "="-Symbol oben) —
                    // die Schenkel laufen von dort entlang der realen Linien nach aussen, sodass
                    // die Spitze in die Konturecke zeigt und das Symbol wie ein Rechtwinkel aussieht.
                    float bx2 = t1x + t2x, by2 = t1y + t2y;
                    float bl2 = (float)Math.Sqrt(bx2*bx2 + by2*by2); if (bl2 < 1e-6) continue;
                    bx2 /= bl2; by2 /= bl2;
                    var rk5b = GetRadiuskorrekturForSeg(en.P1Idx, en.P2Idx);
                    float sgToolpath5b = rk5b == "Links" ? 1f : rk5b == "Rechts" ? -1f : 0f;
                    float side5b = -(sgToolpath5b != 0 ? sgToolpath5b : 1f);

                    float legLen = symR * 0.9f, tipShift = symR * 1.5f;
                    float tipX  = cx3 + bx2 * side5b * tipShift, tipY = cy3 + by2 * side5b * tipShift;
                    float armX1 = tipX + t1x * legLen, armY1 = tipY + t1y * legLen;
                    float armX2 = tipX + t2x * legLen, armY2 = tipY + t2y * legLen;

                    using var sqPath3 = new SKPath();
                    sqPath3.MoveTo(armX1, armY1);
                    sqPath3.LineTo(tipX, tipY);
                    sqPath3.LineTo(armX2, armY2);
                    canvas.DrawPath(sqPath3, sp);
                }
            }
        }
    }

    // Gibt den Index eines Geom-Constraint-Eintrags zurück wenn der Mausklick in der Nähe des Symbols liegt
    private int HitTestGeomConstraintSymbol(double mmX, double mmY)
    {
        if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return -1;
        double sc  = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
        double tol = 10.0 / _zoom;

        for (int i = 0; i < _vermPlaced.Count; i++)
        {
            var en = _vermPlaced[i];
            if (en.Kind != VermKind.Coincident && en.Kind != VermKind.Perpendicular && en.Kind != VermKind.Parallel
             && en.Kind != VermKind.ParallelEdge && en.Kind != VermKind.PerpendicularEdge
             && en.Kind != VermKind.CoincidentCorner) continue;

            if (en.Kind == VermKind.Coincident)
            {
                var a = GetPfadAbsAt(en.P1Idx); var b = GetPfadAbsAt(en.P2Idx);
                if (a == null || b == null) continue;
                foreach (var pt in new[] { a.Value, b.Value })
                    if (Math.Abs(pt.x - mmX) < tol && Math.Abs(pt.y - mmY) < tol) return i;
            }
            else if (en.Kind == VermKind.CoincidentCorner)
            {
                var ptAbs = GetPfadAbsAt(en.P2Idx);
                if (ptAbs == null) continue;
                if (Math.Abs(ptAbs.Value.x - mmX) < tol && Math.Abs(ptAbs.Value.y - mmY) < tol) return i;
                var (cpx, cpy) = WorkpieceCornerPos(en.Edge);
                if (Math.Abs(cpx - mmX) < tol && Math.Abs(cpy - mmY) < tol) return i;
            }
            else if (en.Kind == VermKind.Perpendicular || en.Kind == VermKind.PerpendicularEdge)
            {
                // Ecke, an der das L-Symbol tatsächlich gezeichnet wird
                var p1a = GetPfadAbsAt(en.P1Idx); var p2a = GetPfadAbsAt(en.P2Idx);
                if (p1a == null || p2a == null) continue;
                (double x, double y) corner;
                if (en.Kind == VermKind.PerpendicularEdge)
                {
                    double DistToEdge((double x, double y) pt) => en.Edge switch
                    {
                        1 => Math.Abs(pt.x), 2 => Math.Abs(pt.x - WorkX),
                        3 => Math.Abs(pt.y), 4 => Math.Abs(pt.y - WorkY), _ => 0
                    };
                    corner = DistToEdge(p1a.Value) <= DistToEdge(p2a.Value) ? p1a.Value : p2a.Value;
                }
                else
                {
                    int sharedIdx = en.P1Idx == en.Q1Idx || en.P1Idx == en.Q2Idx ? en.P1Idx
                                  : en.P2Idx == en.Q1Idx || en.P2Idx == en.Q2Idx ? en.P2Idx : -1;
                    var cornerAbs = sharedIdx >= 0 ? GetPfadAbsAt(sharedIdx) : null;
                    corner = cornerAbs ?? ((p1a.Value.x + p2a.Value.x) / 2, (p1a.Value.y + p2a.Value.y) / 2);
                }
                if (Math.Abs(corner.x - mmX) < tol && Math.Abs(corner.y - mmY) < tol) return i;
            }
            else // Parallel / ParallelEdge — Symbol liegt neben der Linie (gegenüber der Fräsbahn)
            {
                var p1a = GetPfadAbsAt(en.P1Idx); var p2a = GetPfadAbsAt(en.P2Idx);
                if (p1a == null || p2a == null) continue;
                double mx = (p1a.Value.x + p2a.Value.x) / 2, my = (p1a.Value.y + p2a.Value.y) / 2;
                double ddx = p2a.Value.x - p1a.Value.x, ddy = p2a.Value.y - p1a.Value.y;
                double l = Math.Sqrt(ddx*ddx+ddy*ddy);
                if (l < 1e-9) { if (Math.Abs(mx - mmX) < tol && Math.Abs(my - mmY) < tol) return i; continue; }
                double dxu = ddx/l, dyu = ddy/l;
                var rk = GetRadiuskorrekturForSeg(en.P1Idx, en.P2Idx);
                double sgTool = rk == "Links" ? 1.0 : rk == "Rechts" ? -1.0 : 0.0;
                double side = sgTool != 0 ? sgTool : 1.0;
                double symRmm = 9.0 / _zoom / sc;
                double offMag = symRmm * 1.75;
                double sx = mx + side * dyu * offMag, sy = my - side * dxu * offMag;
                if (Math.Abs(sx - mmX) < tol * 1.3 && Math.Abs(sy - mmY) < tol * 1.3) return i;
            }
        }
        return -1;
    }

    private void DrawPfadPunkteDots(SKCanvas canvas)
    {
        if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return;
        double sc = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
        float r = (float)(4.5 / _zoom);
        float lt = (float)(1.2 / _zoom);
        var selEntry = HistoryList.SelectedItem as HistoryEntry;

        using var fill   = new SKPaint { Color = new SKColor(30, 120, 220, 200),
            Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(255, 255, 255, 220),
            Style = SKPaintStyle.Stroke, StrokeWidth = lt, IsAntialias = true };
        using var selFill = new SKPaint { Color = new SKColor(220, 80, 0, 230),
            Style = SKPaintStyle.Fill, IsAntialias = true };

        using var midFill = new SKPaint { Color = new SKColor(60, 180, 60, 200),
            Style = SKPaintStyle.Fill, IsAntialias = true };

        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Params is not PfadPunktParams pp) continue;
            var abs = GetPfadAbsAt(i);
            if (abs == null) continue;
            float cx = (float)(_topRect.Left   + abs.Value.x * sc);
            float cy = (float)(_topRect.Bottom - abs.Value.y * sc);
            bool isSel = _history[i] == selEntry;
            canvas.DrawCircle(cx, cy, r, isSel ? selFill : fill);
            canvas.DrawCircle(cx, cy, r, stroke);

            // Segment-Mittelpunkt-Anker (Raute) für Linien und Bögen
            if (pp.Typ != PfadPunktTyp.Start && _activeTool == CanvasTool.Pfeil)
            {
                var midOpt = GetPfadSegMidAbs(i);
                if (midOpt == null) continue;
                float mx = (float)(_topRect.Left   + midOpt.Value.x * sc);
                float my = (float)(_topRect.Bottom - midOpt.Value.y * sc);
                float rd = (float)(4.0 / _zoom);
                var path = new SKPath();
                path.MoveTo(mx,      my - rd);
                path.LineTo(mx + rd, my);
                path.LineTo(mx,      my + rd);
                path.LineTo(mx - rd, my);
                path.Close();
                canvas.DrawPath(path, midFill);
                canvas.DrawPath(path, stroke);
            }
        }
    }

    // ── Gravieren ─────────────────────────────────────────────────
    private void OnGravieren      (object sender, RoutedEventArgs e) => OpenGravierenDialog();
    private void OnVCarve         (object sender, RoutedEventArgs e) => OpenGravierenDialog(isVCarve: true);
private void OnTextfeldTasche (object sender, RoutedEventArgs e) => OpenGravierenDialog(isTasche: true);

    private void OpenGravierenDialog(bool isVCarve = false, bool isTasche = false)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        if (_aktivesWerkzeug!.Schneidenwinkel >= 180)
        {
            MessageBox.Show(this,
                "Das aktive Werkzeug ist kein Gravierwerkzeug (Schneidenwinkel muss < 180° sein). " +
                "Bitte zuerst über 'Werkzeug wechsel' ein passendes Werkzeug wählen.",
                "Ungeeignetes Werkzeug", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string title = isTasche ? "Gravieren – Textfeld A Tasche"
                     : isVCarve ? "Gravieren – Textfeld A carve"
                     : "Gravieren – Textfeld A umriss";
        var dlg = new GravierenDialog(werkzeuge: [_aktivesWerkzeug], workX: WorkX, workY: WorkY)
                      { Owner = this, Title = title };
        if (dlg.ShowDialog() != true) return;
        var p = dlg.Result! with
        {
            IsVCarve = isVCarve,
            IsTasche = isTasche
        };
        string label = isTasche  ? "Textfeld-Tasche"
                     : isVCarve  ? "V-Carve"
                     : "Gravieren";
        // G-Code noch nicht berechnen – erst nach "G-Code berechnen"
        _suppressHistoryRegen = true;
        try { _history.Add(new HistoryEntry(label,
            $"\"{p.Text.Replace('\n', ' ')}\" {p.FontFamily} {p.FontSizeMm} mm", p)); }
        finally { _suppressHistoryRegen = false; }

        // Eintrag selektieren → UpdateEigenschaften() füllt Felder + ResetGCodeButton()
        HistoryList.SelectedItem  = _history[^1];
        TabEigenschaften.IsSelected = true;

        // Preview einschalten, Button als ausstehend markieren
        _previewGravParams = p;
        BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
        BtnGCodeBerechnen.Content    = "● G-Code berechnen";
        UpdateAll();
    }

    // ── Eigenschaften-Tab ─────────────────────────────────────────
    private bool         _eigSuppressUpdate;
    private HistoryEntry? _lastShownEntry;   // letzter in Eigenschaften angezeigter Eintrag
    private int          _eigEndIdx = -1;    // History-Index des Endpunkts bei geschlossenem Pfad

    private void UpdateEigenschaften()
    {
        ResetGCodeButton(); // Ausstehende Änderungen des vorherigen Eintrags verwerfen
        var entry = HistoryList.SelectedItem as HistoryEntry;
        if (!_eigSuppressUpdate && entry?.Params is not ReihenlochbohrungParams)
            PnlReihenloch.Visibility = Visibility.Collapsed;
        if (entry?.Params is GraviereParams p)
        {
            // Visibility nur ändern wenn kein Apply läuft (sonst verliert EigText den Fokus)
            if (!_eigSuppressUpdate)
            {
                TbEigKein.Visibility       = Visibility.Collapsed;
                PnlGravieren.Visibility    = Visibility.Visible;
                PnlPfadStart.Visibility    = Visibility.Collapsed;
                PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
                PnlRechteck.Visibility     = Visibility.Collapsed;
                PnlKreis.Visibility        = Visibility.Collapsed;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            static void Set(TextBox tb, string val)
            { if (!tb.IsKeyboardFocused) tb.Text = val; }

            _eigSuppressUpdate = true;
            Set(EigText,            p.Text);
            if (!EigFont.IsKeyboardFocused)
            {
                var match = (EigFont.ItemsSource as IEnumerable<string>)?
                    .FirstOrDefault(f => f.Equals(p.FontFamily, StringComparison.OrdinalIgnoreCase));
                if (match != null) EigFont.SelectedItem = match;
                else               EigFont.Text         = p.FontFamily;
            }
            // Bezugspunkt ComboBox
            foreach (ComboBoxItem item in EigBezugspunkt.Items)
                if (item.Content as string == p.Bezugspunkt)
                    { EigBezugspunkt.SelectedItem = item; break; }
            Set(EigXRel,            p.XRel.ToString(inv));
            Set(EigYRel,            p.YRel.ToString(inv));
            Set(EigTextBreite,      p.TextBreite.ToString(inv));
            Set(EigTextHoehe,       (p.TextHoehe > 0 ? p.TextHoehe : p.FontSizeMm).ToString(inv));
            Set(EigFontSize,        p.FontSizeMm.ToString(inv));
            Set(EigTracking,        "0");  // Standard-Wert
            Set(EigLineHeight,      "0");  // Standard-Wert (0 = automatisch)

            // Horizontal alignment
            EigAusrLinks.IsChecked  = p.Ausrichtung == "Links"  || string.IsNullOrEmpty(p.Ausrichtung);
            EigAusrMitte.IsChecked  = p.Ausrichtung == "Mitte";
            EigAusrRechts.IsChecked = p.Ausrichtung == "Rechts";

            // Vertical alignment
            EigAusrOben.IsChecked   = p.AusrichtungV == "Oben"  || string.IsNullOrEmpty(p.AusrichtungV);
            EigAusrVMitte.IsChecked = p.AusrichtungV == "Mitte";
            EigAusrUnten.IsChecked  = p.AusrichtungV == "Unten";
            // Fräser-Info (rein informativ, kein Auswahl mehr im Eigenschaften-Panel)
            var eigWzInfo = _werkzeuge.FirstOrDefault(w => w.Nr == p.WerkzeugNr);
            // V-Carve-Felder
            PnlVCarveEig.Visibility = p.IsVCarve ? Visibility.Visible : Visibility.Collapsed;
            if (p.IsVCarve)
            {
                // Max. Tiefe: Werkzeug-ZZustellung wenn noch kein expliziter Wert gesetzt (WerkzeugNr=0)
                double dispZt = (p.WerkzeugNr == 0 && eigWzInfo?.ZZustellung > 0)
                    ? eigWzInfo.ZZustellung : p.ZTiefe;
                // Auflösung: berechneten Auto-Wert anzeigen wenn SampleStepMm = 0
                double dispStep = p.SampleStepMm > 0
                    ? p.SampleStepMm
                    : Math.Clamp(p.FontSizeMm / 300.0, 0.02, 0.1);
                Set(EigMaxTiefe,   dispZt.ToString("F2", inv));
                Set(EigAufloesung, dispStep.ToString("F3", inv));
                Set(EigSpitzenTol, p.VereinfachungMm.ToString(inv));
            }
            _eigSuppressUpdate = false;

            // Schnittbreite-Info
            double halfRadInfo = p.SchneidenWinkel / 2.0 * Math.PI / 180.0;
            double effWInfo    = 2.0 * p.ZTiefe * Math.Tan(halfRadInfo);
            TbEigInfo.Text = $"Fräser: {(p.WerkzeugNr > 0 ? $"#{p.WerkzeugNr}" : "–")}  " +
                             $"Tiefe: {p.ZTiefe:F2} mm  Winkel: {p.SchneidenWinkel}°  " +
                             $"Schnittbreite: {effWInfo:F3} mm";
        }
        else if (entry?.Params is PfadPunktParams pfad)
        {
            // Neuer Punkt ausgewählt → Felder immer überschreiben, auch wenn ein Feld fokussiert ist.
            // Gleicher Punkt (z. B. Position-Update während Drag) → fokussierte Felder schonen.
            bool entryChanged = !ReferenceEquals(entry, _lastShownEntry);
            _lastShownEntry = entry;

            bool isStart = pfad.Typ == PfadPunktTyp.Start;
            bool isBogen = pfad.Typ == PfadPunktTyp.Bogen;
            if (!_eigSuppressUpdate)
            {
                TbEigKein.Visibility            = Visibility.Collapsed;
                PnlGravieren.Visibility         = Visibility.Collapsed;
                PnlRechteck.Visibility          = Visibility.Collapsed;
                PnlKreis.Visibility             = Visibility.Collapsed;
                PnlPfadStart.Visibility         = Visibility.Visible;
                PnlPfadEigStartOnly.Visibility    = isStart ? Visibility.Visible : Visibility.Collapsed;
                PnlPfadEigBezug.Visibility        = Visibility.Collapsed;
                PnlPfadEigBogenMid.Visibility    = isBogen ? Visibility.Visible : Visibility.Collapsed;
                PnlPfadEigVerrundung.Visibility  = isStart ? Visibility.Collapsed : Visibility.Visible;
                PfadEigTitel.Text = pfad.Typ switch
                {
                    PfadPunktTyp.Bogen => "Pfad – Bogen",
                    PfadPunktTyp.Linie => "Pfad – Linie",
                    _                  => "Pfad – Startpunkt"
                };
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _eigSuppressUpdate = true;
            if (entryChanged || !PfadEigX.IsKeyboardFocused) PfadEigX.Text = pfad.XRel.ToString(inv);
            if (entryChanged || !PfadEigY.IsKeyboardFocused) PfadEigY.Text = pfad.YRel.ToString(inv);
            if (isStart)
            {
                if (entryChanged || !PfadEigZ.IsKeyboardFocused) PfadEigZ.Text = pfad.ZTiefe.ToString(inv);
                PfadEigRadius.SelectedIndex = pfad.Radiuskorrektur switch
                    { "Links" => 0, "Rechts" => 2, _ => 1 };
            }
            if (isBogen)
            {
                // Modus-ComboBox (triggert OnPfadEigBogenModusChanged → passt Labels/Sichtbarkeit an)
                foreach (System.Windows.Controls.ComboBoxItem ci in PfadEigBogenModus.Items)
                    if (ci.Content as string == pfad.BogenModus) { PfadEigBogenModus.SelectedItem = ci; break; }
                if (entryChanged || !PfadEigXMid.IsKeyboardFocused) PfadEigXMid.Text = pfad.XMid.ToString(inv);
                if (entryChanged || !PfadEigYMid.IsKeyboardFocused) PfadEigYMid.Text = pfad.YMid.ToString(inv);
            }
            if (!isStart)
                if (entryChanged || !PfadEigVer.IsKeyboardFocused) PfadEigVer.Text = pfad.Verrundung.ToString(inv);
            foreach (ComboBoxItem ci in PfadEigBezug.Items)
                if (ci.Content as string == pfad.Bezugspunkt) { PfadEigBezug.SelectedItem = ci; break; }

            // Geschlossener Pfad: Endpunkt-Panel befüllen wenn Startpunkt ausgewählt
            _eigEndIdx = -1;
            int pfadIdx = _history.IndexOf(entry);
            if (isStart && !_eigSuppressUpdate)
            {
                var (chainSt, chainEn) = FindChainBounds(pfadIdx);
                if (chainSt != chainEn && IsChainClosed(chainSt, chainEn)
                    && _history[chainEn].Params is PfadPunktParams endPfad)
                {
                    _eigEndIdx = chainEn;
                    bool endIsBogen = endPfad.Typ == PfadPunktTyp.Bogen;
                    PnlPfadEndPunkt.Visibility        = Visibility.Visible;
                    PnlEndPfadEigBogenMid.Visibility  = endIsBogen ? Visibility.Visible : Visibility.Collapsed;
                    PnlEndPfadEigVerrundung.Visibility = Visibility.Visible;
                    EndPfadEigTitel.Text = endIsBogen ? "Endpunkt – Bogen (Pfad geschlossen)"
                                                      : "Endpunkt – Linie (Pfad geschlossen)";
                    if (entryChanged || !EndPfadEigX.IsKeyboardFocused) EndPfadEigX.Text = endPfad.XRel.ToString(inv);
                    if (entryChanged || !EndPfadEigY.IsKeyboardFocused) EndPfadEigY.Text = endPfad.YRel.ToString(inv);
                    if (entryChanged || !EndPfadEigVer.IsKeyboardFocused) EndPfadEigVer.Text = endPfad.Verrundung.ToString(inv);
                    if (endIsBogen)
                    {
                        foreach (System.Windows.Controls.ComboBoxItem ci in EndPfadEigBogenModus.Items)
                            if (ci.Content as string == endPfad.BogenModus) { EndPfadEigBogenModus.SelectedItem = ci; break; }
                        if (entryChanged || !EndPfadEigXMid.IsKeyboardFocused) EndPfadEigXMid.Text = endPfad.XMid.ToString(inv);
                        if (entryChanged || !EndPfadEigYMid.IsKeyboardFocused) EndPfadEigYMid.Text = endPfad.YMid.ToString(inv);
                    }
                }
                else
                {
                    PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
                }
            }
            else if (!_eigSuppressUpdate)
            {
                PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
            }

            _eigSuppressUpdate = false;
        }
        else if (entry?.Params is RechteckParams rkt)
        {
            if (!_eigSuppressUpdate)
            {
                TbEigKein.Visibility       = Visibility.Collapsed;
                PnlGravieren.Visibility    = Visibility.Collapsed;
                PnlPfadStart.Visibility    = Visibility.Collapsed;
                PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
                PnlRechteck.Visibility     = Visibility.Visible;
                PnlKreis.Visibility        = Visibility.Collapsed;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _eigSuppressUpdate = true;
            if (!RktEigX.IsKeyboardFocused)      RktEigX.Text      = rkt.XRel.ToString(inv);
            if (!RktEigY.IsKeyboardFocused)      RktEigY.Text      = rkt.YRel.ToString(inv);
            if (!RktEigBreite.IsKeyboardFocused) RktEigBreite.Text = rkt.Breite.ToString(inv);
            if (!RktEigHoehe.IsKeyboardFocused)  RktEigHoehe.Text  = rkt.Hoehe.ToString(inv);
            if (!RktEigZ.IsKeyboardFocused)      RktEigZ.Text      = rkt.ZTiefe.ToString(inv);
            if (!RktEigVer.IsKeyboardFocused)    RktEigVer.Text    = rkt.Verrundung.ToString(inv);
            RktModusNut.IsChecked     = !rkt.IsTasche;
            RktModusTasche.IsChecked  = rkt.IsTasche;
            RktEigFrAussen.IsChecked  = rkt.Fraesung     == "Aussen";
            RktEigFrInnen.IsChecked   = rkt.Fraesung     == "Innen";
            RktEigFrMittig.IsChecked  = rkt.Fraesung     == "Mittig";
            RktEigGegen.IsChecked     = rkt.Laufrichtung == "Gegenlauf";
            RktEigGleich.IsChecked    = rkt.Laufrichtung == "Gleichlauf";
            RktEigMehrfach.IsChecked  = rkt.MehrfachZustellung;
            if (!RktEigZZust.IsKeyboardFocused) RktEigZZust.Text = rkt.ZZustellung.ToString(inv);
            SetRktBezugRadio(rkt.Bezugspunkt);
            UpdateRktModusVisibility(rkt.IsTasche);
            _eigSuppressUpdate = false;
        }
        else if (entry?.Params is KreisParams kr)
        {
            if (!_eigSuppressUpdate)
            {
                TbEigKein.Visibility       = Visibility.Collapsed;
                PnlGravieren.Visibility    = Visibility.Collapsed;
                PnlPfadStart.Visibility    = Visibility.Collapsed;
                PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
                PnlRechteck.Visibility     = Visibility.Collapsed;
                PnlKreis.Visibility        = Visibility.Visible;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _eigSuppressUpdate = true;
            if (!KrEigX.IsKeyboardFocused)      KrEigX.Text      = kr.XRel.ToString(inv);
            if (!KrEigY.IsKeyboardFocused)      KrEigY.Text      = kr.YRel.ToString(inv);
            if (!KrEigRadius.IsKeyboardFocused) KrEigRadius.Text = kr.Radius.ToString(inv);
            if (!KrEigZ.IsKeyboardFocused)      KrEigZ.Text      = kr.ZTiefe.ToString(inv);
            KrEigFrAussen.IsChecked  = kr.Fraesung     == "Aussen";
            KrEigFrInnen.IsChecked   = kr.Fraesung     == "Innen";
            KrEigFrMittig.IsChecked  = kr.Fraesung     == "Mittig";
            KrEigGegen.IsChecked     = kr.Laufrichtung == "Gegenlauf";
            KrEigGleich.IsChecked    = kr.Laufrichtung == "Gleichlauf";
            KrEigMehrfach.IsChecked  = kr.MehrfachZustellung;
            if (!KrEigZZust.IsKeyboardFocused) KrEigZZust.Text = kr.ZZustellung.ToString(inv);
            KrModusNut.IsChecked     = !kr.IsTasche;
            KrModusTasche.IsChecked  = kr.IsTasche;
            SetKrBezugRadio(kr.Bezugspunkt);
            UpdateKrModusVisibility(kr.IsTasche);
            _eigSuppressUpdate = false;
        }
        else if (entry?.Params is NEckParams ne)
        {
            if (!_eigSuppressUpdate)
            {
                TbEigKein.Visibility       = Visibility.Collapsed;
                PnlGravieren.Visibility    = Visibility.Collapsed;
                PnlPfadStart.Visibility    = Visibility.Collapsed;
                PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
                PnlRechteck.Visibility     = Visibility.Collapsed;
                PnlKreis.Visibility        = Visibility.Collapsed;
                PnlNEck.Visibility         = Visibility.Visible;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _eigSuppressUpdate = true;
            if (!NEckEigX.IsKeyboardFocused)      NEckEigX.Text       = ne.XRel.ToString(inv);
            if (!NEckEigY.IsKeyboardFocused)      NEckEigY.Text       = ne.YRel.ToString(inv);
            if (!NEckEigEcken.IsKeyboardFocused)  NEckEigEcken.Text   = ne.Ecken.ToString(inv);
            if (!NEckEigRadius.IsKeyboardFocused) NEckEigRadius.Text  = ne.Radius.ToString(inv);
            if (!NEckEigRotation.IsKeyboardFocused) NEckEigRotation.Text = ne.RotationGrad.ToString(inv);
            if (!NEckEigZ.IsKeyboardFocused)      NEckEigZ.Text       = ne.ZTiefe.ToString(inv);
            NEckEigFrAussen.IsChecked  = ne.Fraesung == "Aussen";
            NEckEigFrInnen.IsChecked   = ne.Fraesung == "Innen";
            NEckEigFrMittig.IsChecked  = ne.Fraesung == "Mittig";
            NEckEigGegen.IsChecked     = ne.Laufrichtung == "Gegenlauf";
            NEckEigGleich.IsChecked    = ne.Laufrichtung == "Gleichlauf";
            NEckEigRadKorKeine.IsChecked  = ne.Radiuskorrektur == "Keine";
            NEckEigRadKorInnen.IsChecked  = ne.Radiuskorrektur == "Innen";
            NEckEigRadKorAussen.IsChecked = ne.Radiuskorrektur == "Aussen";
            NEckEigMehrfach.IsChecked  = ne.MehrfachZustellung;
            if (!NEckEigZZust.IsKeyboardFocused) NEckEigZZust.Text = ne.ZZustellung.ToString(inv);
            NEckModusNut.IsChecked     = !ne.IsTasche;
            NEckModusTasche.IsChecked  = ne.IsTasche;
            _eigSuppressUpdate = false;
        }
        else if (entry?.Params is ReihenlochbohrungParams rlb)
        {
            if (!_eigSuppressUpdate)
            {
                TbEigKein.Visibility       = Visibility.Collapsed;
                PnlGravieren.Visibility    = Visibility.Collapsed;
                PnlPfadStart.Visibility    = Visibility.Collapsed;
                PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
                PnlRechteck.Visibility     = Visibility.Collapsed;
                PnlKreis.Visibility        = Visibility.Collapsed;
                PnlNEck.Visibility         = Visibility.Collapsed;
                PnlReihenloch.Visibility   = Visibility.Visible;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _eigSuppressUpdate = true;
            if (!RlbEigAbstandX.IsKeyboardFocused) RlbEigAbstandX.Text = rlb.SpacingX.ToString(inv);
            if (!RlbEigAbstandY.IsKeyboardFocused) RlbEigAbstandY.Text = rlb.SpacingY.ToString(inv);
            if (!RlbEigTiefe.IsKeyboardFocused)    RlbEigTiefe.Text    = rlb.Bohrtiefe.ToString(inv);
            RlbEigKreistasche.IsChecked = rlb.IstKreistasche;
            if (!RlbEigTascheD.IsKeyboardFocused)
                RlbEigTascheD.Text = (rlb.TascheD > 0 ? rlb.TascheD : RlbStdTascheD).ToString(inv);
            _eigSuppressUpdate = false;
        }
        else if (!_eigSuppressUpdate)
        {
            TbEigKein.Visibility       = Visibility.Visible;
            PnlGravieren.Visibility    = Visibility.Collapsed;
            PnlPfadStart.Visibility    = Visibility.Collapsed;
            PnlPfadEndPunkt.Visibility = Visibility.Collapsed;
            PnlRechteck.Visibility     = Visibility.Collapsed;
            PnlKreis.Visibility        = Visibility.Collapsed;
            PnlNEck.Visibility         = Visibility.Collapsed;
        }
    }

    private void ApplyPfadStartEig()
    {
        if (_eigSuppressUpdate) return;
        var entry = HistoryList.SelectedItem as HistoryEntry;
        if (entry?.Params is not PfadPunktParams pfad) return;
        int idx = _history.IndexOf(entry);
        if (idx < 0) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        static string Norm(string s) => s.Replace(',', '.');
        if (!double.TryParse(Norm(PfadEigX.Text), sty, inv, out var xRel)) return;
        if (!double.TryParse(Norm(PfadEigY.Text), sty, inv, out var yRel)) return;

        string bezug = "Unten links";

        PfadPunktParams np;
        string lbl, det;

        if (pfad.Typ == PfadPunktTyp.Start)
        {
            if (!double.TryParse(Norm(PfadEigZ.Text), sty, inv, out var z)) return;
            string radius = PfadEigRadius.SelectedIndex switch { 0 => "Links", 2 => "Rechts", _ => "Mittig" };
            np  = pfad with
            {
                XRel = xRel, YRel = yRel, ZTiefe = z,
                Radiuskorrektur = radius, Bezugspunkt = bezug,
            };
            lbl = "Pfad Start";
            det = $"X={np.XRel} Y={np.YRel}, Z={np.ZTiefe}";
        }
        else if (pfad.Typ == PfadPunktTyp.Bogen)
        {
            string bModus = (PfadEigBogenModus.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Bogenmitte";
            bool   bMitte = bModus == "Bogenmitte";
            if (!double.TryParse(Norm(PfadEigXMid.Text), sty, inv, out var xm)) return;
            double ym = 0;
            if (bMitte && !double.TryParse(Norm(PfadEigYMid.Text), sty, inv, out ym)) return;
            double.TryParse(Norm(PfadEigVer.Text), sty, inv, out var verB);
            np  = pfad with { XRel = xRel, YRel = yRel, XMid = xm, YMid = ym,
                              Bezugspunkt = bezug, BogenModus = bModus, Verrundung = Math.Max(0, verB) };
            lbl = $"Pfad Bogen #{PfadPunktNummer(idx)}";
            det = bMitte
                ? $"X={np.XRel} Y={np.YRel}, M={np.XMid}/{np.YMid}"
                : $"X={np.XRel} Y={np.YRel}, {bModus}={np.XMid}";
        }
        else
        {
            double.TryParse(Norm(PfadEigVer.Text), sty, inv, out var verL);
            np  = pfad with { XRel = xRel, YRel = yRel, Bezugspunkt = bezug, Verrundung = Math.Max(0, verL) };
            lbl = $"Pfad Linie #{PfadPunktNummer(idx)}";
            det = $"X={np.XRel} Y={np.YRel}";
        }

        _eigSuppressUpdate    = true;
        _suppressHistoryRegen = true;
        try { _history[idx] = new HistoryEntry(lbl, det, np, entry.Level); }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }

        // Bestehende Vermassungen (z.B. grünes Rechtwinklig-Symbol) erneut durchsetzen,
        // damit eine manuelle Bearbeitung über das Eigenschaften-Panel sie nicht verletzt.
        PropagateVermConstraintsLive();
        ShowVermDiagIfViolated();

        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = idx;
    }

    private void OnPfadEigLostFocus(object sender, RoutedEventArgs e)                => ApplyPfadStartEig();
    private void OnPfadEigSelChanged(object sender, SelectionChangedEventArgs e)     => ApplyPfadStartEig();
    private void OnPfadEigTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyPfadStartEig();

    // ── Endpunkt-Panel (geschlossener Pfad) ──────────────────────────────────
    private void OnEndPfadEigLostFocus(object sender, RoutedEventArgs e)            => ApplyPfadEndPunktEig();
    private void OnEndPfadEigSelChanged(object sender, SelectionChangedEventArgs e) => ApplyPfadEndPunktEig();
    private void OnEndPfadEigTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyPfadEndPunktEig();
    private void OnEndPfadEigKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPfadEndPunktEig(); }

    private void OnEndPfadEigBogenModusChanged(object sender, SelectionChangedEventArgs e)
    {
        string modus = (EndPfadEigBogenModus.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Pfeilhöhe";
        bool isMitte = modus == "Bogenmitte";
        EndPfadEigBogenWertLabel.Text  = modus switch
        {
            "Radius"    => "Radius (mm):",
            "Pfeilhöhe" => "Pfeilhöhe (mm):",
            _           => "Bogen-Mitte X (mm):"
        };
        PnlEndPfadEigYMid.Visibility = isMitte ? Visibility.Visible : Visibility.Collapsed;
        ApplyPfadEndPunktEig();
    }

    private void ApplyPfadEndPunktEig()
    {
        if (_eigSuppressUpdate || _eigEndIdx < 0) return;
        if (_history[_eigEndIdx].Params is not PfadPunktParams pfad) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        static string Norm(string s) => s.Replace(',', '.');
        if (!double.TryParse(Norm(EndPfadEigX.Text), sty, inv, out var xRel)) return;
        if (!double.TryParse(Norm(EndPfadEigY.Text), sty, inv, out var yRel)) return;

        PfadPunktParams np;
        string lbl, det;

        if (pfad.Typ == PfadPunktTyp.Bogen)
        {
            string bModus = (EndPfadEigBogenModus.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Bogenmitte";
            bool   bMitte = bModus == "Bogenmitte";
            if (!double.TryParse(Norm(EndPfadEigXMid.Text), sty, inv, out var xm)) return;
            double ym = 0;
            if (bMitte && !double.TryParse(Norm(EndPfadEigYMid.Text), sty, inv, out ym)) return;
            double.TryParse(Norm(EndPfadEigVer.Text), sty, inv, out var verB);
            np  = pfad with { XRel = xRel, YRel = yRel, XMid = xm, YMid = ym,
                              Bezugspunkt = "Unten links", BogenModus = bModus, Verrundung = Math.Max(0, verB) };
            lbl = $"Pfad Bogen #{PfadPunktNummer(_eigEndIdx)}";
            det = bMitte
                ? $"X={np.XRel} Y={np.YRel}, M={np.XMid}/{np.YMid}"
                : $"X={np.XRel} Y={np.YRel}, {bModus}={np.XMid}";
        }
        else
        {
            double.TryParse(Norm(EndPfadEigVer.Text), sty, inv, out var verL);
            np  = pfad with { XRel = xRel, YRel = yRel, Bezugspunkt = "Unten links", Verrundung = Math.Max(0, verL) };
            lbl = $"Pfad Linie #{PfadPunktNummer(_eigEndIdx)}";
            det = $"X={np.XRel} Y={np.YRel}";
        }

        int startIdx = HistoryList.SelectedIndex;
        _eigSuppressUpdate    = true;
        _suppressHistoryRegen = true;
        try { _history[_eigEndIdx] = new HistoryEntry(lbl, det, np, _history[_eigEndIdx].Level); }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }

        // Bestehende Vermassungen (z.B. grünes Rechtwinklig-Symbol) erneut durchsetzen,
        // damit eine manuelle Bearbeitung über das Endpunkt-Panel sie nicht verletzt.
        PropagateVermConstraintsLive();
        ShowVermDiagIfViolated();

        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = startIdx;
    }

    private void OnPfadEigBogenModusChanged(object sender, SelectionChangedEventArgs e)
    {
        // Label + Sichtbarkeit anpassen
        string modus = (PfadEigBogenModus.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Pfeilhöhe";
        bool isMitte = modus == "Bogenmitte";
        PfadEigBogenWertLabel.Text    = modus switch
        {
            "Radius"    => "Radius (mm):",
            "Pfeilhöhe" => "Pfeilhöhe (mm):",
            _           => "Bogen-Mitte X (mm):"
        };
        PnlPfadEigYMid.Visibility     = isMitte ? Visibility.Visible : Visibility.Collapsed;
        PfadEigBogenHinweis.Visibility = isMitte ? Visibility.Collapsed : Visibility.Visible;

        // Radius-Modus: Halbkreis-Radius aus Sehnenlänge vorschlagen
        if (modus == "Radius" && !_eigSuppressUpdate)
        {
            var entry = HistoryList.SelectedItem as HistoryEntry;
            if (entry?.Params is PfadPunktParams bogenPfad && bogenPfad.Typ == PfadPunktTyp.Bogen)
            {
                int bidx = _history.IndexOf(entry);
                if (bidx > 0)
                {
                    var p1 = GetPfadAbsAt(bidx - 1);
                    var p2 = GetPfadAbsAt(bidx);
                    if (p1.HasValue && p2.HasValue)
                    {
                        double dx = p2.Value.x - p1.Value.x, dy = p2.Value.y - p1.Value.y;
                        double L  = Math.Sqrt(dx * dx + dy * dy);
                        if (L > 1e-6)
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            PfadEigXMid.Text = Math.Round(L / 2, 3).ToString(inv);
                        }
                    }
                }
            }
        }

        ApplyPfadStartEig();
    }

    private void ApplyEigenschaften()
    {
        if (_eigSuppressUpdate) return;
        var entry = HistoryList.SelectedItem as HistoryEntry;
        if (entry?.Params is not GraviereParams p) return;
        int idx = _history.IndexOf(entry);
        if (idx < 0) return;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        static string Norm(string s) => s.Replace(',', '.');
        if (!double.TryParse(Norm(EigTextBreite.Text), sty, inv, out var tw)) return;
        if (!double.TryParse(Norm(EigTextHoehe.Text),  sty, inv, out var th) || th <= 0) return;
        if (!double.TryParse(Norm(EigFontSize.Text),   sty, inv, out var fs) || fs <= 0) return;

        string ausrichtung = EigAusrRechts.IsChecked == true ? "Rechts"
                           : EigAusrMitte.IsChecked  == true ? "Mitte" : "Links";
        string fontFamily  = (EigFont.SelectedItem as string) ?? EigFont.Text.Trim();
        if (string.IsNullOrWhiteSpace(fontFamily)) return;
        string bezugApply  = (EigBezugspunkt.SelectedItem as ComboBoxItem)?.Content as string ?? p.Bezugspunkt;
        double xrApply     = double.TryParse(Norm(EigXRel.Text), sty, inv, out var vxa) ? vxa : p.XRel;
        double yrApply     = double.TryParse(Norm(EigYRel.Text), sty, inv, out var vya) ? vya : p.YRel;

        double zt  = p.ZTiefe;
        double sw  = p.SchneidenWinkel;
        double halfRad = sw / 2.0 * Math.PI / 180.0;
        double effW    = 2.0 * zt * Math.Tan(halfRad);

        // V-Carve-spezifische Felder
        if (p.IsVCarve)
        {
            if (double.TryParse(Norm(EigMaxTiefe.Text),   sty, inv, out var vzt) && vzt > 0) zt  = vzt;
        }
        double sampleStep = p.SampleStepMm;
        double spitzenTol = p.VereinfachungMm;
        if (p.IsVCarve)
        {
            if (double.TryParse(Norm(EigAufloesung.Text),  sty, inv, out var vss) && vss >= 0) sampleStep = vss;
            if (double.TryParse(Norm(EigSpitzenTol.Text),  sty, inv, out var vst) && vst >= 0) spitzenTol = vst;
        }

        var np = p with
        {
            Text            = EigText.Text,
            FontFamily      = fontFamily,
            Bezugspunkt     = bezugApply,
            XRel            = xrApply,
            YRel            = yrApply,
            FontSizeMm      = fs,
            TextBreite      = tw,
            TextHoehe       = th,
            Ausrichtung     = ausrichtung,
            ZTiefe          = zt,
            SampleStepMm    = sampleStep,
            VereinfachungMm = spitzenTol,
        };

        TbEigInfo.Text = $"Fräser: {(np.WerkzeugNr > 0 ? $"#{np.WerkzeugNr}" : "–")}  " +
                         $"Tiefe: {zt:F2} mm  Winkel: {sw}°  Schnittbreite: {effW:F3} mm";
        string lbl2 = np.IsTasche  ? "Textfeld-Tasche"
                    : np.IsVCarve ? "V-Carve"
                    : "Gravieren";
        // _eigSuppressUpdate + _suppressHistoryRegen: verhindert Panel-Flicker und doppeltes Regenerieren
        // während der ObservableCollection Replace-Event HistoryList.SelectionChanged auslöst
        _eigSuppressUpdate    = true;
        _suppressHistoryRegen = true;
        try { _history[idx] = new HistoryEntry(lbl2,
            $"\"{np.Text.Replace('\n', ' ')}\" {np.FontFamily} {np.FontSizeMm} mm", np); }
        finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }

        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        HistoryList.SelectedIndex = idx;
    }

    private void OnEigTextDirty(object sender, TextChangedEventArgs e) => RestartEigTimer();

    private void UpdatePreviewFromFields()
    {
        if (_eigSuppressUpdate) return;
        BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
        BtnGCodeBerechnen.Content    = "● G-Code berechnen";

        // Speichere die Selection, damit wir NACH RepositionInlineTextBox()
        // wissen, ob wir UpdateEditorFontFamily() aufrufen müssen
        int savedSelStart = _savedSelectionStart;
        int savedSelEnd = _savedSelectionEnd;

        var entry = HistoryList.SelectedItem as HistoryEntry;
        if (entry?.Params is GraviereParams gp)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sty = System.Globalization.NumberStyles.Float;
            static string Norm(string s) => s.Replace(',', '.');

            string fontFamily = (EigFont.SelectedItem as string) ?? EigFont.Text.Trim();
            if (string.IsNullOrWhiteSpace(fontFamily)) fontFamily = gp.FontFamily;
            string bezug = (EigBezugspunkt.SelectedItem as ComboBoxItem)?.Content as string ?? gp.Bezugspunkt;
            double xr = double.TryParse(Norm(EigXRel.Text),           sty, inv, out var vx)           ? vx : gp.XRel;
            double yr = double.TryParse(Norm(EigYRel.Text),           sty, inv, out var vy)           ? vy : gp.YRel;
            double fs = double.TryParse(Norm(EigFontSize.Text),       sty, inv, out var v1) && v1 > 0 ? v1 : gp.FontSizeMm;
            double tw = double.TryParse(Norm(EigTextBreite.Text),     sty, inv, out var v2)           ? v2 : gp.TextBreite;
            double th = double.TryParse(Norm(EigTextHoehe.Text),      sty, inv, out var v3) && v3 > 0 ? v3 : gp.TextHoehe;

            // Horizontal alignment (Left/Center/Right)
            string ausr = EigAusrRechts.IsChecked == true ? "Rechts"
                        : EigAusrMitte.IsChecked  == true ? "Mitte" : "Links";

            // Vertical alignment (Top/Center/Bottom)
            string ausrV = EigAusrUnten.IsChecked == true ? "Unten"
                         : EigAusrVMitte.IsChecked == true ? "Mitte" : "Oben";

            double previewZt = gp.ZTiefe;
            if (gp.IsVCarve && double.TryParse(Norm(EigMaxTiefe.Text),  sty, inv, out var pvzt) && pvzt > 0) previewZt = pvzt;
            double previewStep = gp.SampleStepMm;
            double previewSimp = gp.VereinfachungMm;
            if (gp.IsVCarve)
            {
                if (double.TryParse(Norm(EigAufloesung.Text), sty, inv, out var pvss) && pvss >= 0) previewStep = pvss;
                if (double.TryParse(Norm(EigSpitzenTol.Text), sty, inv, out var pvst) && pvst >= 0) previewSimp = pvst;
            }

            _previewGravParams = gp with
            {
                Text            = EigText.Text,
                FontFamily      = fontFamily,
                Bezugspunkt     = bezug,
                XRel            = xr,
                YRel            = yr,
                FontSizeMm      = fs,
                TextBreite      = tw,
                TextHoehe       = th,
                Ausrichtung     = ausr,
                AusrichtungV    = ausrV,
                ZTiefe          = previewZt,
                SampleStepMm    = previewStep,
                VereinfachungMm = previewSimp,
            };

            // Update inline text editor if it's active (alignment, font size, position/size)
            if (_inlineTextBox != null && _inlineParams != null)
            {
                var (hAlign, vAlign) = ParseAlignment(ausr, ausrV);
                _inlineTextBox.HorizontalAlign = hAlign;
                _inlineTextBox.VerticalAlign = vAlign;

                // Update font size wird jetzt durch UpdateEditorFontFamily() erledigt
                // (nur für ausgewählte Zeichen, nicht für alle)

                // Prüfe, ob die Schriftgröße geändert wurde (BEVOR _inlineParams aktualisiert wird!)
                bool fontSizeChanged = _inlineParams.FontSizeMm != fs || _inlineParams.FontFamily != fontFamily;

                // Update _inlineParams with only the fields that changed from properties
                // This preserves the edited Text and other important values
                _inlineParams = _inlineParams with
                {
                    FontSizeMm = fs,
                    Ausrichtung = ausr,
                    AusrichtungV = ausrV,
                    TextBreite = tw,
                    TextHoehe = th,
                    XRel = xr,
                    YRel = yr,
                };

                // Reposition and resize the text box based on updated parameters
                RepositionInlineTextBox(fontSizeChanged);

                // WICHTIG: Nach RepositionInlineTextBox() muss UpdateEditorFontFamily() aufgerufen werden,
                // WENN eine Selection existiert! Sonst würde RepositionInlineTextBox() ALLE Zeichen
                // mit UpdateFontSize() geändert haben (wenn _savedSelectionStart >= 0 wäre).
                // Aber jetzt wird UpdateFontSize() NICHT aufgerufen, weil _savedSelectionStart >= 0.
                // Deshalb müssen wir UpdateEditorFontFamily() aufrufen, um die markierten Zeichen zu ändern.
                if (savedSelStart >= 0 && savedSelEnd >= 0)
                {
                    UpdateEditorFontFamily();
                    // Lösche die gespeicherten Werte NACH UpdateEditorFontFamily()
                    // (nicht in UpdateEditorFontFamily() selbst, damit OnEigFontSizeKeyDown() nicht leidet)
                    _savedSelectionStart = -1;
                    _savedSelectionEnd = -1;
                }
            }
        }
        UpdateAll();
    }

    private void OnGCodeBerechnen(object sender, RoutedEventArgs e)
    {
        ResetGCodeButton();
        _suppressNextAutoFit = true;
        ApplyEigenschaften();
    }

    private void ResetGCodeButton()
    {
        BtnGCodeBerechnen.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        BtnGCodeBerechnen.Content = "G-Code berechnen";
        _previewGravParams = null;
    }

    // Numerische Felder → Debounce → Preview (kein G-Code)
    private void OnEigSizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_eigSuppressUpdate) return;

        // WICHTIG: Speichere die Selection BEVOR die Schriftgröße geändert wird
        if (_inlineTextBox != null)
        {
            var (start, end) = _inlineTextBox.GetSelection();
            if (start >= 0 && end >= 0)  // Nur wenn eine Selection existiert
            {
                _savedSelectionStart = start;
                _savedSelectionEnd = end;
            }
        }

        RestartEigTimer();
    }

    // Zeichenabstand und Zeilenabstand → SOFORT auf markierten Text anwenden (kein Debounce)
    // Diese wirken NUR auf den markierten Text, nicht auf den gesamten Text!
    private void OnEigTrackingLineHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (_eigSuppressUpdate) return;

        // Speichere die aktuelle Selection
        if (_inlineTextBox != null)
        {
            var (start, end) = _inlineTextBox.GetSelection();
            if (start >= 0 && end >= 0)
            {
                _savedSelectionStart = start;
                _savedSelectionEnd = end;
            }
        }

        // Wende sofort auf markierten Text an (kein Debounce!)
        UpdateEditorFontFamily();
    }
    // Auswahl-Events → sofort Preview (kein G-Code)
    private void OnEigFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_eigSuppressUpdate) return;

        // WICHTIG: Speichere die Selection BEVOR die Schriftart geändert wird
        if (_inlineTextBox != null)
        {
            var (start, end) = _inlineTextBox.GetSelection();
            _savedSelectionStart = start;
            _savedSelectionEnd = end;
        }

        UpdatePreviewFromFields();
        UpdateEditorFontFamily();
        // NICHT löschen! UpdatePreviewFromFields() wird die Werte löschen, falls nötig!

        // Fokus verzögert zurück auf Editor, damit das Textwerkzeug aktiv bleibt
        // (Verzögerung verhindert, dass Events die Schriftart zurücksetzen)
        if (_inlineTextBox != null && _inlineTextBox.IsVisible)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                _inlineTextBox?.Focus();
            });
        }
    }
    private void OnEigFontKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Up && e.Key != Key.Down)
        {
            RestartEigTimer();
            return;
        }

        // WICHTIG: Speichere die Selection BEVOR die Pfeiltaste verarbeitet wird
        if (_inlineTextBox != null)
        {
            var (start, end) = _inlineTextBox.GetSelection();
            if (start >= 0 && end >= 0)  // Nur wenn eine Selection existiert
            {
                _savedSelectionStart = start;
                _savedSelectionEnd = end;
            }
        }

        // Pfeiltasten zum Durchlaufen von Schriftarten
        string[] fonts = { "Segoe UI", "Arial", "Courier New", "Times New Roman", "Consolas", "Calibri" };
        string currentFont = EigFont.Text ?? "Segoe UI";
        int currentIndex = System.Array.IndexOf(fonts, currentFont);
        if (currentIndex < 0) currentIndex = 0;

        int newIndex = e.Key == Key.Up
            ? (currentIndex + 1) % fonts.Length
            : (currentIndex - 1 + fonts.Length) % fonts.Length;

        EigFont.Text = fonts[newIndex];
        EigFont.Focus();  // Fokus halten!
        RestartEigTimer();
        e.Handled = true;
    }

    /// <summary>
    /// Aktualisiere die Schriftart des ImprovedSkiaTextEditors (wenn vorhanden)
    /// Behält den Cursor-Blinkzustand bei!
    /// </summary>
    private void UpdateSelectedCharacterPropertiesFromEditor()
    {
        if (_inlineTextBox == null) return;

        _eigSuppressUpdate = true;
        try
        {
            static void Apply(TextBox tb, string value)
            {
                if (!tb.IsKeyboardFocused)
                    tb.Text = value;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var fmt = _inlineTextBox.GetSelectedFormat();

            if (fmt == null)
            {
                if (_inlineParams != null)
                {
                    Apply(EigFontSize, _inlineParams.FontSizeMm.ToString(inv));
                    Apply(EigTracking, "0");
                    Apply(EigLineHeight, "0");
                    string fontFamily = _inlineParams.FontFamily;
                    var match = (EigFont.ItemsSource as IEnumerable<string>)?
                        .FirstOrDefault(f => f.Equals(fontFamily, StringComparison.OrdinalIgnoreCase));
                    if (match != null) EigFont.SelectedItem = match;
                    else if (!EigFont.IsKeyboardFocused) EigFont.Text = fontFamily;
                }
                return;
            }

            if (!EigFont.IsKeyboardFocused)
            {
                var match = (EigFont.ItemsSource as IEnumerable<string>)?
                    .FirstOrDefault(f => f.Equals(fmt.FontFamily, StringComparison.OrdinalIgnoreCase));
                if (match != null) EigFont.SelectedItem = match;
                else EigFont.Text = fmt.FontFamily;
            }

            if (!EigFontSize.IsKeyboardFocused)
            {
                double fontSizeMm = fmt.FontSizePt / (Math.Max(_zoom, 0.0001) * Math.Max(_dpiScale, 0.0001));
                Apply(EigFontSize, fontSizeMm.ToString("F1", inv));
            }

            if (!EigTracking.IsKeyboardFocused)
                Apply(EigTracking, fmt.Tracking.ToString("F2", inv));

            if (!EigLineHeight.IsKeyboardFocused)
                Apply(EigLineHeight, fmt.LineHeight.ToString("F2", inv));
        }
        finally
        {
            _eigSuppressUpdate = false;
        }
    }

    private void UpdateEditorFontFamily()
    {
        if (_inlineTextBox == null) return;

        // WICHTIG: GetModel() gibt einen CLONE zurück! Wir müssen SetSelectedFormat verwenden
        // um das ORIGINAL-Modell zu ändern!

        // Wenn keine Selection gespeichert, nichts tun
        int start = _savedSelectionStart;
        int end = _savedSelectionEnd;

        if (start < 0 || end < 0)
            return;

        string fontFamily = (EigFont.SelectedItem as string) ?? EigFont.Text.Trim();
        if (string.IsNullOrWhiteSpace(fontFamily)) return;

        // Hole aktuelle Werte aus den Eigenschaften-Feldern
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;
        float fontSize = 12f;
        float tracking = 0f;
        float lineHeight = 0f;

        if (double.TryParse(EigFontSize.Text?.Replace(",", "."), sty, inv, out var fs))
            fontSize = (float)(fs * Math.Max(_zoom, 0.0001) * Math.Max(_dpiScale, 0.0001));

        if (double.TryParse(EigTracking.Text?.Replace(",", "."), sty, inv, out var t))
            tracking = (float)t;

        if (double.TryParse(EigLineHeight.Text?.Replace(",", "."), sty, inv, out var lh))
            lineHeight = (float)lh;

        // Normalisiere Start/End
        int minPos = Math.Min(start, end);
        int maxPos = Math.Max(start, end);

        // Erstelle Format-Objekt
        var format = new TextCharacterFormat
        {
            FontFamily = fontFamily,
            FontSizePt = fontSize,
            Tracking = tracking,
            LineHeight = lineHeight,
            Color = SKColors.White
        };

        // Stelle die Selection im Editor her
        _inlineTextBox.SetSelection(minPos, maxPos);

        // Nutze SetSelectedFormat um das ORIGINAL-Modell zu ändern
        _inlineTextBox.SetSelectedFormat(format);

        // Stelle die Selection nochmal her (falls durch SetSelectedFormat gelöscht)
        _inlineTextBox.SetSelection(minPos, maxPos);

        // WICHTIG: Aktualisiere _defaultFormat NICHT!
        // Das würde dazu führen, dass ALLE neuen Zeichen die neue Formatierung bekommen!
        // Wir wollen nur die markierten Zeichen ändern!

        // NICHT hier löschen! Der Aufrufer löscht die Werte, wenn nötig!
        // Das verhindert, dass wenn UpdateEditorFontFamily() direkt aufgerufen wird,
        // der Timer später mit -1 Werten arbeitet.
    }

    // Schriftgröße mit Pfeiltasten ändern
    private void OnEigFontSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Up && e.Key != Key.Down)
            return;

        // WICHTIG: Speichere die Selection BEVOR die Pfeiltaste verarbeitet wird
        if (_inlineTextBox != null)
        {
            var (start, end) = _inlineTextBox.GetSelection();
            _savedSelectionStart = start;
            _savedSelectionEnd = end;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sty = System.Globalization.NumberStyles.Float;

        if (!double.TryParse(EigFontSize.Text?.Replace(",", "."), sty, inv, out var currentSize))
            return;

        double step = 2.0;  // 2 mm pro Taste (größere Schritte)
        double newSize = e.Key == Key.Up ? currentSize + step : currentSize - step;
        newSize = Math.Max(0.1, newSize);  // Minimum 0.1 mm

        _eigSuppressUpdate = true;
        EigFontSize.Text = newSize.ToString("F1", inv);
        _eigSuppressUpdate = false;

        RestartEigTimer();
        UpdateEditorFontFamily();  // Auch Editor aktualisieren
        // NICHT löschen! Der Timer in UpdatePreviewFromFields() wird die Werte löschen!
        e.Handled = true;
    }

    private void RestartEigTimer()
    {
        if (_eigSuppressUpdate) return;    // Programmatische Textzuweisung → kein Timer
        _eigTimer.Stop(); _eigTimer.Start();
    }
    private void OnEigAusrichtungChanged(object sender, RoutedEventArgs e)        => UpdatePreviewFromFields();

    private void OnEigTextStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_inlineTextBox == null) return;

        var rdoFill = (RadioButton)FindName("EigTextFill");
        if (rdoFill?.IsChecked == true)
        {
            _inlineTextBox.TextStyle = SKPaintStyle.Fill;
            _inlineTextBox.TextColor = null;  // Verwende Format-Farbe
        }
        else
        {
            _inlineTextBox.TextStyle = SKPaintStyle.Stroke;
            _inlineTextBox.TextColor = SKColors.Black;  // Stroke-Standard: Schwarz
        }
    }

    private void OnEigStrokeWidthChanged(object sender, RoutedEventArgs e)
    {
        if (_inlineTextBox == null) return;

        var slider = (Slider)FindName("EigStrokeWidth");
        var label = (TextBlock)FindName("EigStrokeWidthLabel");

        float width = (float)slider.Value;
        _inlineTextBox.StrokeWidth = width;
        label.Text = width.ToString("F2");
    }


    private void OnHorizEinmitten(object sender, RoutedEventArgs e)
    {
        var entry = HistoryList.SelectedItem as HistoryEntry;
        if (entry?.Params is not GraviereParams gp) return;
        var p = _previewGravParams ?? gp;
        var (left, bottom, width, height) = TextFieldBoundsInMm(p);
        double newLeft = (WorkX - width) / 2.0;
        var (newRefX, newRefY) = BezugAbsPos(p.Bezugspunkt, newLeft, bottom, width, height);
        var (newXRel, _)       = AbsToRel   (p.Bezugspunkt, newRefX, newRefY, WorkX, WorkY);
        CommitEinmitten(p with { XRel = Math.Round(newXRel, 3) });
    }

    private void OnVertEinmitten(object sender, RoutedEventArgs e)
    {
        var entry = HistoryList.SelectedItem as HistoryEntry;
        if (entry?.Params is not GraviereParams gp) return;
        var p = _previewGravParams ?? gp;
        var (left, bottom, width, height) = TextFieldBoundsInMm(p);
        double newBottom = (WorkY - height) / 2.0;
        var (newRefX, newRefY) = BezugAbsPos(p.Bezugspunkt, left, newBottom, width, height);
        var (_, newYRel)       = AbsToRel   (p.Bezugspunkt, newRefX, newRefY, WorkX, WorkY);
        CommitEinmitten(p with { YRel = Math.Round(newYRel, 3) });
    }

    /// <summary>
    /// Setzt <see cref="_previewGravParams"/> direkt (kein Umweg über UpdatePreviewFromFields),
    /// aktualisiert die X/Y-Felder mit Unterdrückung und verhindert, dass der Debounce-Timer
    /// das Ergebnis später überschreibt.
    /// </summary>
    private void CommitEinmitten(GraviereParams centered)
    {
        _eigTimer.Stop();                               // laufenden Debounce-Timer abbrechen
        _previewGravParams = centered;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _eigSuppressUpdate = true;                      // RestartEigTimer ignoriert Änderungen
        try
        {
            EigXRel.Text = centered.XRel.ToString(inv);
            EigYRel.Text = centered.YRel.ToString(inv);
        }
        finally { _eigSuppressUpdate = false; }

        // Neue Position in History persistieren – sonst liest StartEditExistingTextField
        // beim erneuten Klick mit dem Textfeld-Werkzeug die alten Koordinaten aus _history.
        var selEntry = HistoryList.SelectedItem as HistoryEntry;
        int selIdx   = selEntry != null ? _history.IndexOf(selEntry) : -1;
        if (selIdx >= 0)
        {
            string lbl = centered.IsTasche ? "Textfeld-Tasche"
                       : centered.IsVCarve ? "V-Carve" : "Gravieren";
            _eigSuppressUpdate    = true;
            _suppressHistoryRegen = true;
            try { _history[selIdx] = new HistoryEntry(lbl,
                $"\"{centered.Text.Replace('\n', ' ')}\" {centered.FontFamily} {centered.FontSizeMm} mm",
                centered); }
            finally { _suppressHistoryRegen = false; _eigSuppressUpdate = false; }
            HistoryList.SelectedIndex = selIdx;
        }

        BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
        BtnGCodeBerechnen.Content    = "● G-Code berechnen";
        UpdateAll();
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEigenschaften();
        HighlightHistoryEntry(HistoryList.SelectedItem as HistoryEntry);
        if (_activeTool == CanvasTool.Reihenloch && _rlbDrag == 0)
        {
            _rlbIdx = HistoryList.SelectedItem is HistoryEntry { Params: ReihenlochbohrungParams } se
                ? _history.IndexOf(se) : -1;
            DrawSkia?.InvalidateVisual();
        }
    }

    // ── Verlauf: Doppelklick → Bearbeiten ───────────────────────
    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = (e.OriginalSource as DependencyObject)?.FindVisualParent<ListBoxItem>();
        if (item?.DataContext is not HistoryEntry entry) return;
        EditHistoryEntry(entry);
    }

    // ── Verlauf: Tastatur (Ctrl+C, Ctrl+V, Del, Alt+↑↓) ────────
    private void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        // Bei gedrücktem Alt liefert WPF e.Key == Key.System; der echte Key steckt in e.SystemKey
        var key  = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt  = (Keyboard.Modifiers & ModifierKeys.Alt)     != 0;
        if      (ctrl && key == Key.C)    { CopySelectedHistory();    e.Handled = true; }
        else if (ctrl && key == Key.V)    { PasteHistory();           e.Handled = true; }
        else if (key == Key.Delete)       { DeleteSelectedHistory();  e.Handled = true; }
        else if (alt  && key == Key.Up)   { MoveSelectedHistoryUp();  e.Handled = true; }
        else if (alt  && key == Key.Down) { MoveSelectedHistoryDown(); e.Handled = true; }
    }

    // ── Verlauf: Clipboard ───────────────────────────────────────
    private void CopySelectedHistory()
    {
        _historyClipboard.Clear();
        _historyClipboard.AddRange(HistoryList.SelectedItems.Cast<HistoryEntry>()
            .OrderBy(_history.IndexOf));
    }

    private void PasteHistory()
    {
        if (_historyClipboard.Count == 0) return;
        int insertIdx = HistoryList.SelectedItems.Count > 0
            ? HistoryList.SelectedItems.Cast<HistoryEntry>().Max(_history.IndexOf)
            : _history.Count - 1;
        _suppressHistoryRegen = true;
        try
        {
            for (int i = 0; i < _historyClipboard.Count; i++)
            {
                var src = _historyClipboard[i];
                _history.Insert(insertIdx + 1 + i,
                    new HistoryEntry(src.Label, src.Details, src.Params, src.Level));
            }
        }
        finally { _suppressHistoryRegen = false; RegenerateGCodeFromHistory(); }
    }

    private void DeleteSelectedHistory()
    {
        var toDelete = HistoryList.SelectedItems.Cast<HistoryEntry>().ToList();
        _suppressHistoryRegen = true;
        try { foreach (var e in toDelete) _history.Remove(e); }
        finally { _suppressHistoryRegen = false; RegenerateGCodeFromHistory(); }
    }

    // ── Verlauf: Reihenfolge ─────────────────────────────────────
    private void OnHistoryMoveUp(object sender, RoutedEventArgs e)   => MoveSelectedHistoryUp();
    private void OnHistoryMoveDown(object sender, RoutedEventArgs e) => MoveSelectedHistoryDown();

    private void MoveSelectedHistoryUp()
    {
        var entries = HistoryList.SelectedItems.Cast<HistoryEntry>()
            .OrderBy(_history.IndexOf).ToList();
        if (entries.Count == 0) return;
        _suppressHistoryRegen = true;
        try
        {
            foreach (var entry in entries)
            {
                int idx = _history.IndexOf(entry);
                if (idx > 0) _history.Move(idx, idx - 1);
            }
        }
        finally { _suppressHistoryRegen = false; RegenerateGCodeFromHistory(); }
        RestoreSelection(entries);
    }

    private void MoveSelectedHistoryDown()
    {
        var entries = HistoryList.SelectedItems.Cast<HistoryEntry>()
            .OrderByDescending(_history.IndexOf).ToList();
        if (entries.Count == 0) return;
        _suppressHistoryRegen = true;
        try
        {
            foreach (var entry in entries)
            {
                int idx = _history.IndexOf(entry);
                if (idx < _history.Count - 1) _history.Move(idx, idx + 1);
            }
        }
        finally { _suppressHistoryRegen = false; RegenerateGCodeFromHistory(); }
        RestoreSelection(entries);
    }

    private void RestoreSelection(IEnumerable<HistoryEntry> entries)
    {
        HistoryList.SelectedItems.Clear();
        foreach (var e in entries) HistoryList.SelectedItems.Add(e);
    }

    private void EditHistoryEntry(HistoryEntry entry)
    {
        if (!SicherstellenAktivesWerkzeug()) return;
        int idx = _history.IndexOf(entry);
        switch (entry.Params)
        {
            case PlanfräsenParams p:
            {
                var dlg = new PlanfräsenDialog(WorkX, WorkY, p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Planfräsen",
                    $"{(np.Horizontal ? "Horizontal" : "Vertikal")}, Z={np.Z}, Ø{np.FraeserD}", np);
                break;
            }
            case BohrungParams p:
            {
                var dlg = new BohrungDialog(WorkZ + 3, p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Bohrung",
                    $"X={np.XRel} Y={np.YRel}, Ø{np.Durchmesser}, Z={np.Bohrtiefe}, {np.Bezugspunkt}", np);
                break;
            }
            case ReihenlochbohrungParams p:
            {
                var dlg = new ReihenlochbohrungDialog(WorkZ + 3, p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Reihenlochbohrung",
                    $"{np.CountX}×{np.CountY}, Ø{np.Diameter}, Z={np.Bohrtiefe}", np);
                break;
            }
            case UmfahrenParams p:
            {
                var dlg = new UmfahrenDialog(WorkZ, p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Umfahren",
                    $"A={np.A}, Ø{np.Diameter}, Z={np.Z}", np);
                break;
            }
            case TascheFräsenParams p:
            {
                var dlg = new TascheFräsenDialog(-(WorkZ + 3), p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Tasche",
                    $"X={np.XRel} Y={np.YRel}, {np.Breite}×{np.Höhe}, Z={np.ZTiefe}, Ø{np.FraeserD}", np);
                break;
            }
            case NutParams p:
            {
                var dlg = new NutFräsenDialog(-(WorkZ + 3), p.Länge, p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Nut",
                    $"X={np.XRel} Y={np.YRel}, L={np.Länge} B={np.Breite}, Z={np.ZTiefe}, Ø{np.FraeserD}", np);
                break;
            }
            case KreistascheParams p:
            {
                var dlg = new KreistascheDialog(-(WorkZ + 3), p, werkzeuge: [_aktivesWerkzeug!]) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result!;
                _history[idx] = new HistoryEntry("Kreistasche",
                    $"X={np.XRel} Y={np.YRel}, Ø{np.Durchmesser}, Z={np.ZTiefe}", np);
                break;
            }
            case PfadPunktParams p:
            {
                string title = p.Typ switch
                {
                    PfadPunktTyp.Start => "Pfad – Startpunkt",
                    PfadPunktTyp.Bogen => "Pfad – Bogen",
                    PfadPunktTyp.Spline => "Pfad – Spline",
                    _                  => "Pfad – Linie"
                };
                var dlg = new PfadPunktDialog(title, -(WorkZ + 3),
                    isStart: p.Typ == PfadPunktTyp.Start, p,
                    werkzeuge: [_aktivesWerkzeug!],
                    isBogen: p.Typ == PfadPunktTyp.Bogen,
                    isSpline: p.Typ == PfadPunktTyp.Spline) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result! with { Typ = p.Typ };
                int lvl = np.Typ == PfadPunktTyp.Start ? 0 : 1;
                string det = np.Typ == PfadPunktTyp.Start
                    ? $"X={np.XRel} Y={np.YRel}, Z={np.ZTiefe}"
                    : np.Typ == PfadPunktTyp.Bogen
                    ? (np.BogenModus == "Bogenmitte"
                        ? $"X={np.XRel} Y={np.YRel}, M={np.XMid}/{np.YMid}"
                        : $"X={np.XRel} Y={np.YRel}, {np.BogenModus}={np.XMid}")
                    : np.Typ == PfadPunktTyp.Spline
                    ? $"X={np.XRel} Y={np.YRel}, {np.SplineModus}, T={np.SplineTension}"
                    : $"X={np.XRel} Y={np.YRel}";
                string lbl = np.Typ switch
                {
                    PfadPunktTyp.Start => "Pfad Start",
                    PfadPunktTyp.Bogen => $"Pfad Bogen #{PfadPunktNummer(idx)}",
                    PfadPunktTyp.Spline => $"Pfad Spline #{PfadPunktNummer(idx)}",
                    _                  => $"Pfad Linie #{PfadPunktNummer(idx)}"
                };
                _history[idx] = new HistoryEntry(lbl, det, np, lvl);
                break;
            }
            case GraviereParams p:
            {
                if (_aktivesWerkzeug!.Schneidenwinkel >= 180)
                {
                    MessageBox.Show(this,
                        "Das aktive Werkzeug ist kein Gravierwerkzeug (Schneidenwinkel muss < 180° sein). " +
                        "Bitte zuerst über 'Werkzeug wechsel' ein passendes Werkzeug wählen.",
                        "Ungeeignetes Werkzeug", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string dlgTitle = p.IsTasche ? "Gravieren – Textfeld A Tasche"
                                : p.IsVCarve ? "Gravieren – Textfeld A carve"
                                : "Gravieren – Textfeld A umriss";
                var dlg = new GravierenDialog(p, werkzeuge: [_aktivesWerkzeug])
                              { Owner = this, Title = dlgTitle };
                if (dlg.ShowDialog() != true) return;
                var np = dlg.Result! with
                {
                    Text            = p.Text,
                    FontFamily      = p.FontFamily,
                    FontSizeMm      = p.FontSizeMm,
                    TextBreite      = p.TextBreite,
                    TextHoehe       = p.TextHoehe,
                    ZTiefe          = p.ZTiefe,
                    SchneidenWinkel = p.SchneidenWinkel,
                    Vorschub        = p.Vorschub,
                    Drehzahl        = p.Drehzahl,
                    Ausrichtung     = p.Ausrichtung,
                    IsVCarve = p.IsVCarve,
                    IsTasche = p.IsTasche
                };
                string lbl = np.IsTasche  ? "Textfeld-Tasche"
                           : np.IsVCarve  ? "V-Carve"
                           : "Gravieren";
                _history[idx] = new HistoryEntry(lbl,
                    $"\"{np.Text.Replace('\n', ' ')}\" {np.FontFamily} {np.FontSizeMm} mm", np);
                break;
            }
        }
        // Bestehende Vermassungen (z.B. grünes Rechtwinklig-Symbol) erneut durchsetzen,
        // damit eine Bearbeitung über den Eigenschaften-Dialog sie nicht verletzt.
        PropagateVermConstraintsLive();
        ShowVermDiagIfViolated();
        RegenerateGCodeFromHistory();
    }

    private void OnHistoryRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = (e.OriginalSource as DependencyObject)?.FindVisualParent<ListBoxItem>();
        if (item == null) return;
        e.Handled = true;

        // Angeklicktes Element selektieren falls noch nicht in der Auswahl
        if (item.DataContext is HistoryEntry clicked && !HistoryList.SelectedItems.Contains(clicked))
        {
            HistoryList.SelectedItems.Clear();
            HistoryList.SelectedItems.Add(clicked);
        }

        int n = HistoryList.SelectedItems.Count;
        if (n == 0) return;
        string nStr = n == 1 ? "Eintrag" : $"{n} Einträge";

        var cm = new ContextMenu();
        var miCopy   = new MenuItem { Header = $"{nStr} kopieren (Ctrl+C)" };
        miCopy.Click += (_, _) => CopySelectedHistory();
        var miPaste  = new MenuItem { Header = "Einfügen (Ctrl+V)", IsEnabled = _historyClipboard.Count > 0 };
        miPaste.Click += (_, _) => PasteHistory();
        var miUp     = new MenuItem { Header = $"{nStr} nach oben (Alt+↑)" };
        miUp.Click   += (_, _) => MoveSelectedHistoryUp();
        var miDown   = new MenuItem { Header = $"{nStr} nach unten (Alt+↓)" };
        miDown.Click += (_, _) => MoveSelectedHistoryDown();
        var miDel    = new MenuItem { Header = $"{nStr} löschen (Del)" };
        miDel.Click  += (_, _) => DeleteSelectedHistory();

        cm.Items.Add(miCopy);
        cm.Items.Add(miPaste);
        cm.Items.Add(new Separator());
        cm.Items.Add(miUp);
        cm.Items.Add(miDown);
        cm.Items.Add(new Separator());
        cm.Items.Add(miDel);
        item.ContextMenu = cm;
        item.ContextMenu.IsOpen = true;
    }

    private void RegenerateGCodeFromHistory()
    {
        // Laufende Berechnung abbrechen (z. B. vorheriger Tastendruck noch in Arbeit)
        _regenCts?.Cancel();
        var cts = _regenCts = new CancellationTokenSource();

        // Snapshot auf UI-Thread – keine Zugriffe auf UI-Objekte im Hintergrund-Thread nötig
        var historySnap = _history.ToList();
        double workX = WorkX, workY = WorkY;

        // STA-Thread: WPF-Geometrie (FormattedText, PathGeometry) erfordert STA
        var t = new Thread(() =>
        {
            var sb          = new System.Text.StringBuilder();
            var pfadBuffer  = new List<PfadPunktParams>();
            var pfadEntries = new List<HistoryEntry>();   // Einträge im aktuellen Pfad-Block
            double lastStartZ          = 0;
            string lastRadiuskorrektur = "Mittig";
            double lastFraeserD        = 0;
            int lineCounter = 1;  // nächste Zeile im entstehenden Dokument (1-basiert)
            var lineMap = new Dictionary<HistoryEntry, (int start, int end)>();
            bool spindleOn = false;   // Spindel läuft bereits (M03 wurde ausgegeben, M05 steht noch aus)

            // Zählt Zeilen, die sb.AppendLine(code) hinzufügt
            int LinesIn(string s) => s.Count(c => c == '\n') + 1;

            void FlushPfad()
            {
                if (pfadBuffer.Count == 0) return;
                var c = GCodeGenerator.PfadFräsen(pfadBuffer, workX, workY);
                if (!string.IsNullOrEmpty(c))
                {
                    int ls = lineCounter;
                    sb.AppendLine(c);
                    lineCounter += LinesIn(c);
                    int le = lineCounter - 1;
                    foreach (var e in pfadEntries) lineMap[e] = (ls, le);
                }
                pfadBuffer.Clear();
                pfadEntries.Clear();
            }

            foreach (var entry in historySnap)
            {
                if (cts.IsCancellationRequested) return;

                if (entry.Params is PfadPunktParams pfad)
                {
                    if (pfad.Typ == PfadPunktTyp.Start)
                    {
                        FlushPfad();
                        lastStartZ          = pfad.ZTiefe;
                        lastRadiuskorrektur = pfad.Radiuskorrektur;
                        lastFraeserD        = pfad.FraeserD;
                        pfadBuffer.Add(pfad);
                    }
                    else
                    {
                        pfadBuffer.Add(pfad with {
                            ZTiefe          = lastStartZ,
                            Radiuskorrektur = lastRadiuskorrektur,
                            FraeserD        = lastFraeserD
                        });
                    }
                    pfadEntries.Add(entry);
                    continue;
                }

                FlushPfad();
                string code = entry.Params switch
                {
                    PlanfräsenParams p        => GCodeGenerator.Planfräsen(p),
                    BohrungParams p           => GCodeGenerator.Bohrung(p, workX, workY),
                    ReihenlochbohrungParams p => GCodeGenerator.Reihenlochbohrung(p),
                    UmfahrenParams p          => GCodeGenerator.Umfahren(p, workX, workY),
                    TascheFräsenParams p      => GCodeGenerator.Tasche(p, workX, workY),
                    NutParams p               => GCodeGenerator.Nut(p, workX, workY),
                    KreistascheParams p       => GCodeGenerator.Kreistasche(p, workX, workY),
                    RechteckParams p when p.IsTasche => GCodeGenerator.Tasche(RechteckToTasche(p), workX, workY),
                    RechteckParams p          => GCodeGenerator.Rechteck(p, workX, workY),
                    KreisParams p             => GCodeGenerator.Kreis(p, workX, workY),
                    NEckParams p              => GCodeGenerator.NEck(p, workX, workY),
                    GraviereParams p when p.IsTasche => GCodeGenerator.TextfeldTasche(p, workX, workY),
                    GraviereParams p when p.IsVCarve => GCodeGenerator.VCarve(p, workX, workY),
                    GraviereParams p                 => GCodeGenerator.Gravieren(p, workX, workY),
                    WerkzeugWechselParams p          => GCodeGenerator.WerkzeugWechsel(p),
                    _                                => string.Empty
                };

                // Hinweis: Nullpunkt-Verschiebung erfolgt bereits bei der Parameter-Anpassung (AdjustParamsToNullpunkt),
                // daher keine zusätzliche Verschiebung des GCode nötig

                // Werkzeugwechsel gibt immer M05 / M06 / M03 aus und startet die Spindel neu
                if (entry.Params is WerkzeugWechselParams)
                    spindleOn = true;

                if (!string.IsNullOrEmpty(code))
                {
                    int ls = lineCounter;
                    sb.AppendLine(code);
                    lineCounter += LinesIn(code);
                    lineMap[entry] = (ls, lineCounter - 1);
                }
            }
            FlushPfad();

            // Spindel am Programmende stoppen, falls sie noch läuft
            if (spindleOn)
                sb.AppendLine("M05");

            if (cts.IsCancellationRequested) return;
            var result = sb.ToString();

            Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                _vCarveCache.Clear();
                _textGeoCache.Clear();
                _vCarvePending.Clear();
                _historyLineMap = lineMap;
                GCodeText = result;
                UpdatePfadMenuState();
                UpdateAll();
                // Aktuell gewählten Eintrag sofort hervorheben
                HighlightHistoryEntry(HistoryList.SelectedItem as HistoryEntry);
            });
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

#if false // ── Pfad Fräsen Panel ────────────────────────────────────────

    private void OnPfadAnzeigenChanged(object sender, RoutedEventArgs e)
    {
        PfadPanel.Visibility = CbPfadAnzeigen.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        if (CbPfadAnzeigen.IsChecked == true)
            PfadTxtZ.Text = (-Math.Abs(WorkZ)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateAll();
    }

    private void OnPfadParamChanged(object sender, SelectionChangedEventArgs e) => UpdateAll();

    private void OnPfadPunktKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) PfadPunktHinzufügen();
    }

    private void OnPfadHinzufügen(object sender, RoutedEventArgs e) => PfadPunktHinzufügen();

    private void PfadPunktHinzufügen()
    {
        if (!double.TryParse(PfadTxtX.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(PfadTxtY.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var y))
            return;

        string bezug = (PfadCbBezug?.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Unten links";
        _pfadPunkte.Add(new PfadPunkt
        {
            Nr    = _pfadPunkte.Count + 1,
            X     = x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Y     = y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Bezug = bezug
        });
        PfadLvPunkte.SelectedIndex = _pfadPunkte.Count - 1;
        PfadLvPunkte.ScrollIntoView(PfadLvPunkte.SelectedItem);
        PfadTxtX.Focus(); PfadTxtX.SelectAll();
    }

    private void OnPfadHoch(object sender, RoutedEventArgs e)
    {
        int i = PfadLvPunkte.SelectedIndex;
        if (i <= 0) return;
        (_pfadPunkte[i], _pfadPunkte[i - 1]) = (_pfadPunkte[i - 1], _pfadPunkte[i]);
        PfadLvPunkte.SelectedIndex = i - 1;
        PfadAktualisiereNummern();
    }

    private void OnPfadRunter(object sender, RoutedEventArgs e)
    {
        int i = PfadLvPunkte.SelectedIndex;
        if (i < 0 || i >= _pfadPunkte.Count - 1) return;
        (_pfadPunkte[i], _pfadPunkte[i + 1]) = (_pfadPunkte[i + 1], _pfadPunkte[i]);
        PfadLvPunkte.SelectedIndex = i + 1;
        PfadAktualisiereNummern();
    }

    private void OnPfadLöschen(object sender, RoutedEventArgs e)
    {
        int i = PfadLvPunkte.SelectedIndex;
        if (i < 0) return;
        _pfadPunkte.RemoveAt(i);
        PfadAktualisiereNummern();
        PfadLvPunkte.SelectedIndex = Math.Min(i, _pfadPunkte.Count - 1);
    }

#endif // OnWindowKeyDown aus #if false herausgenommen
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Ignore tool shortcuts if typing in any text input
        if (e.OriginalSource is System.Windows.Controls.TextBox
            || e.OriginalSource is ICSharpCode.AvalonEdit.Editing.TextArea) return;

        // Ignore tool shortcuts if inline text editor has focus
        if (Keyboard.FocusedElement == _inlineTextBox) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.H when !ctrl: SetActiveTool(_activeTool == CanvasTool.Hand ? CanvasTool.Select : CanvasTool.Hand); e.Handled = true; break;
            case Key.Z when !ctrl: SetActiveTool(_activeTool == CanvasTool.Zoom ? CanvasTool.Select : CanvasTool.Zoom); e.Handled = true; break;
            case Key.Back:
            {
                // Backspace: Während Spline-Erstellung letzten Punkt löschen
                if (_activeTool == CanvasTool.PfadSpline && _splinePointsBeingCreated.Count > 0)
                {
                    _splinePointsBeingCreated.RemoveAt(_splinePointsBeingCreated.Count - 1);
                    DrawSkia?.InvalidateVisual();
                }
                // Ansonsten: Letzten Pfad-Punkt entfernen (Zoom-Ansicht beibehalten)
                else
                {
                    int last = _history.Count - 1;
                    if (last >= 0 && _history[last].Params is PfadPunktParams)
                    {
                        _suppressNextAutoFit = true;
                        _history.RemoveAt(last);
                        UpdatePfadMenuState();
                        DrawSkia?.InvalidateVisual();
                    }
                }
                e.Handled = true; break;
            }
            case Key.Delete when _selectedGeomIdx >= 0:
                _vermPlaced.RemoveAt(_selectedGeomIdx);
                _selectedGeomIdx = -1;
                PropagateVermConstraints();
                DrawSkia?.InvalidateVisual();
                e.Handled = true; break;

            case Key.Delete when _activeTool == CanvasTool.Vermassen && _vermState == 3 && _vermEditIdx >= 0:
                _vermPlaced.RemoveAt(_vermEditIdx);
                _vermEditIdx = -1;
                _vermState   = 0;
                DrawSkia?.InvalidateVisual();
                e.Handled = true; break;

            case Key.Delete when _activeTool == CanvasTool.Move && HistoryList.SelectedItems.Count > 0:
                DeleteSelectedHistory();
                e.Handled = true; break;

            // Reihenlochbohrung: aktive Lochreihe (Anker sichtbar) löschen
            case Key.Delete when _activeTool == CanvasTool.Reihenloch && RlbActive() != null:
            {
                int idx = _rlbIdx;
                _rlbDrag = 0; _rlbPreview = null; _rlbIdx = -1;
                if (CanvasGrid.IsMouseCaptured) CanvasGrid.ReleaseMouseCapture();
                _suppressNextAutoFit = true;
                _history.RemoveAt(idx);
                DrawSkia?.InvalidateVisual();
                e.Handled = true; break;
            }

            case Key.Escape:
                if (_activeTool == CanvasTool.Vermassen)
                {
                    CloseVermTextBox();
                    _vermEditIdx = -1;
                    if (_vermState == 5 && _vermActiveKind is VermKind.KreisKantenDist or VermKind.RktKantenDist)
                    { _vermActiveEdge = 0; _vermP2Idx = -1; _vermState = 1; }   // zurück zu „Kreis/Rechteck-Seite gewählt"
                    else if (_vermState == 5) { _vermQ1Idx = -1; _vermQ2Idx = -1; _vermState = 1; }
                    else if (_vermState >= 1) { _vermIsHolding = false; _vermP1Idx = -1; _vermQ1Idx = -1; _vermActiveEdge = 0; _vermKreisIdx = -1; _vermRktIdx = -1; _vermState = 0; if (CanvasGrid.IsMouseCaptured) CanvasGrid.ReleaseMouseCapture(); }
                    else _vermState = 0;
                    DrawSkia?.InvalidateVisual();
                }
                else if (_activeTool == CanvasTool.PfadBogen && _pfadBogenWaiting)
                { _pfadBogenWaiting = false; DrawSkia?.InvalidateVisual(); }
                else if ((_activeTool == CanvasTool.PfadSpline && _splinePointsBeingCreated.Count > 0) || _splineFinalized)
                { _splinePointsBeingCreated.Clear(); _splineFinalized = false; DrawSkia?.InvalidateVisual(); }
                else if ((_activeTool == CanvasTool.VCarveTextSk) && _isTextDragging)
                { _isTextDragging = false; ClearTextRubberBand(); }
                else if (_activeTool == CanvasTool.Rechteck && _rktDragging)
                { _rktDragging = false; ClearRktRubberBand(); DrawSkia?.InvalidateVisual(); }
                else if (_activeTool == CanvasTool.Kreis && _kreisDragging)
                { _kreisDragging = false; CloseKreisDurchmesserBox(); ClearKreisRubberBand(); DrawSkia?.InvalidateVisual(); }
                else if (_activeTool == CanvasTool.NEck && _neckDragging)
                { _neckDragging = false; ClearNEckRubberBand(); DrawSkia?.InvalidateVisual(); }
                else if (_activeTool == CanvasTool.Reihenloch && _rlbDrag != 0)
                {
                    _rlbDrag = 0; _rlbPreview = null;
                    if (CanvasGrid.IsMouseCaptured) CanvasGrid.ReleaseMouseCapture();
                    DrawSkia?.InvalidateVisual();
                }
                else
                    SetActiveTool(CanvasTool.Select);
                e.Handled = true; break;
            case Key.D0 or Key.NumPad0 when ctrl: ZoomTo100();    e.Handled = true; break;
            case Key.D1 or Key.NumPad1 when ctrl: ZoomTo1to1();   e.Handled = true; break;

            case Key.Return:
                if (_activeTool == CanvasTool.PfadSpline && _splinePointsBeingCreated.Count >= 2)
                {
                    double segmentLen = 0.5;
                    if (double.TryParse(PfadEigSplineSegmentLen.Text, NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var len))
                        segmentLen = Math.Max(0.1, len);
                    FinalizePfadSpline(segmentLen);
                    e.Handled = true;
                }
                break;
        }
    }
#if false

    private void OnPfadAlleLöschen(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Alle Punkte löschen?", "Bestätigung",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _pfadPunkte.Clear();
    }

    private void OnPfadSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAll();

    private void OnPfadCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Nach Commit der Zelle Zeichnung aktualisieren
        Dispatcher.BeginInvoke(UpdateAll, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Pfeilklick: Punkt verschieben + Maus nachführen ──────────

    private void OnPfadArrowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement el) return;
        var parts = (el.Tag as string ?? "").Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int idx)) return;
        var dir = parts[1];
        if (idx < 0 || idx >= _pfadPunkte.Count) return;

        double relX = double.Parse(_pfadPunkte[idx].X, System.Globalization.CultureInfo.InvariantCulture);
        double relY = double.Parse(_pfadPunkte[idx].Y, System.Globalization.CultureInfo.InvariantCulture);
        string bezug = _pfadPunkte[idx].Bezug;
        double s = PfadSchritt;

        // Verschiebung in absoluten Koords (oben = +absY, unten = -absY)
        var (absX, absY) = RelToAbs(bezug, relX, relY, WorkX, WorkY);
        (absX, absY) = dir switch
        {
            "U" => (absX,     absY + s),
            "D" => (absX,     absY - s),
            "L" => (absX - s, absY),
            "R" => (absX + s, absY),
            _   => (absX, absY)
        };

        // Zurück in relative Koords für diesen Bezug
        var (newRelX, newRelY) = AbsToRel(bezug, absX, absY, WorkX, WorkY);
        _pfadPunkte[idx].X = newRelX.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        _pfadPunkte[idx].Y = newRelY.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        _pfadHoverIdx = idx;
        _arrowJustClicked = true;

        // Cursor um genau den Pixel-Betrag des Schritts verschieben
        // → bleibt relativ zur Klickposition, kein Voreilung
        var clickPos = e.GetPosition(HitCanvas);
        double stepPx = s * _pfadScale;
        (double cdx, double cdy) = dir switch
        {
            "U" => (0.0,    -stepPx),
            "D" => (0.0,     stepPx),
            "L" => (-stepPx, 0.0),
            _   => (stepPx,  0.0)
        };
        var screen = HitCanvas.PointToScreen(new Point(clickPos.X + cdx, clickPos.Y + cdy));
        SetCursorPos((int)screen.X, (int)screen.Y);

        PfadLvPunkte.Items.Refresh();
        UpdateAll();
    }

    private void PfadAktualisiereNummern()
    {
        for (int i = 0; i < _pfadPunkte.Count; i++) _pfadPunkte[i].Nr = i + 1;
        PfadLvPunkte.Items.Refresh();
        UpdateAll();
    }

    private void OnPfadGCodeErzeugen(object sender, RoutedEventArgs e)
    {
        if (_pfadPunkte.Count < 2)
        {
            MessageBox.Show("Mindestens 2 Punkte erforderlich.", "Hinweis",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var p = BuildPfadParams();
        if (p == null) return;
        var gcode = GCodeGenerator.PfadFräsen(p, WorkX, WorkY);
        PrependGeneratedGCode(gcode);
        UpdateAll();
    }

    private PfadFräsenParams? BuildPfadParams()
    {
        if (_pfadPunkte.Count == 0) return null;
        if (!double.TryParse(PfadTxtZ.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var z)) return null;
        if (!double.TryParse(PfadTxtZustellung.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var zu)) return null;
        if (!double.TryParse(PfadTxtVorschub.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        if (!double.TryParse(PfadTxtDrehzahl.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return null;
        if (!double.TryParse(PfadTxtFraeserD.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fd)) return null;

        double wx = WorkX, wy = WorkY;
        // Jeden Punkt in absolute Werkstück-Koords umrechnen (per-Punkt-Bezug)
        var punkte = _pfadPunkte.Select(p =>
        {
            double relX = double.Parse(p.X, System.Globalization.CultureInfo.InvariantCulture);
            double relY = double.Parse(p.Y, System.Globalization.CultureInfo.InvariantCulture);
            var (absX, absY) = RelToAbs(p.Bezug, relX, relY, wx, wy);
            return (X: absX, Y: absY);
        }).ToList();

        string seite = (PfadCbSeite.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Mitte";
        return new PfadFräsenParams(punkte, z, zu, v, d, fd, "absolut", seite);
    }
#endif // Pfad Fräsen Panel Ende

    // ── Canvas: Zoom / Pan ───────────────────────────────────────

    private void ApplyCanvasTransform()
    {
        // HitCanvas bekommt dieselbe Transform wie früher DrawCanvas,
        // damit transparente Klickflächen korrekt positioniert bleiben.
        var grp = new TransformGroup();
        grp.Children.Add(new ScaleTransform(_zoom, _zoom));
        grp.Children.Add(new TranslateTransform(_panX, _panY));
        HitCanvas.RenderTransform = grp;
        DrawSkia.InvalidateVisual();
        if (TxtZoomLevel is not null)
            TxtZoomLevel.Text = $"{_zoom * 100:F0} %";
        RepositionInlineTextBox();
        RepositionVermTextBox();
    }

    private void ResetZoom()
    {
        _zoom = 1.0; _panX = 0.0; _panY = 0.0;
        ApplyCanvasTransform();
    }

    // Ctrl+0: Werkstück zentrieren, Zoom auf 100 % (oder kleiner falls nötig)
    private void ZoomTo100()
    {
        double cw = DrawSkia.ActualWidth, ch = DrawSkia.ActualHeight;
        if (!_topRect.IsEmpty && cw > 0 && ch > 0)
        {
            ApplyCenterZoom(cw, ch, DefaultZoom(cw, ch));
            ApplyCanvasTransform();
        }
        else
        {
            ResetZoom();
        }
        UpdateAll();
    }

    // Ctrl+Fit: Werkstück einpassen und zentrieren
    private void ZoomToFit()
    {
        double cw = DrawSkia.ActualWidth, ch = DrawSkia.ActualHeight;
        if (cw <= 0 || ch <= 0 || _topRect.IsEmpty) return;
        ApplyZoomToFit(cw, ch);
        ApplyCanvasTransform();
        UpdateAll();
    }

    // Originalmaßstab – 1 mm auf dem Bildschirm = 1 mm in Wirklichkeit
    private void ZoomTo1to1()
    {
        if (_topRect.IsEmpty || WorkX <= 0) return;
        // baseScale = WPF-DIPs pro mm bei zoom=1.0
        double baseScale = _topRect.Width / WorkX;

        // Physische Pixeldichte des Monitors (MDT_RAW_DPI = 2)
        // Formel: zoom = physDpi / (25.4 mm/in × baseScale × m11)
        //   m11  = WPF-DIPs → physische Pixel (entspricht dem Windows-Skalierungsfaktor)
        //   physDpi = tatsächliche Pixel pro Zoll des Monitors
        double physDpi = 96.0;
        try
        {
            var helper  = new System.Windows.Interop.WindowInteropHelper(this);
            var monitor = MonitorFromWindow(helper.Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (GetDpiForMonitor(monitor, 2 /* MDT_RAW_DPI */, out uint dx, out _) == 0)
                physDpi = dx;
        }
        catch { }

        var src  = PresentationSource.FromVisual(this);
        double m11 = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        double newZoom = Math.Clamp(physDpi / (25.4 * baseScale * m11), 0.05, 200.0);

        // Aktuellen Mittelpunkt des sichtbaren Bereichs als Ankerpunkt beibehalten
        double cx  = DrawSkia.ActualWidth  / 2;
        double cy  = DrawSkia.ActualHeight / 2;
        double wCx = (cx - _panX) / _zoom;
        double wCy = (cy - _panY) / _zoom;
        _zoom = newZoom;
        _panX = cx - wCx * _zoom;
        _panY = cy - wCy * _zoom;
        ApplyCanvasTransform();
        UpdateAll();
    }

    // ── Werkzeugpalette ──────────────────────────────────────────

    // Commit/cancel inline text edit without recursive tool-switching side effects.
    private void FlushInlineEdit()
    {
        if (_inlineTextBox == null) return;
        var text       = _inlineTextBox.GetText();
        int existingIdx = _inlineExistingIdx;
        _inlineTextBox.TextChanged    -= InlineTextBox_TextChanged;
        _inlineTextBox.LostFocus      -= InlineTextBox_LostFocus;
        SimToolCanvas.Children.Remove(_inlineTextBox);
        _inlineTextBox     = null;
        _inlineExistingIdx = -1;
        _ctrlResizeMode    = false;
        _inlineVCarveTimer?.Stop();   // Debounce-Timer abbrechen

        _suppressHistoryRegen = true;
        try
        {
            if (existingIdx >= 0)
            {
                // Editing existing entry: replace or leave unchanged (if empty)
                if (!string.IsNullOrWhiteSpace(text) && _inlineParams != null && existingIdx < _history.Count)
                {
                    var final = _inlineParams with { Text = text };
                    EnsureInlineVCarveCache(final);   // Fallback cache-warm
                    _history[existingIdx] = new HistoryEntry("V-Carve",
                        $"\"{text.Replace('\n', ' ')}\" {final.FontFamily} {final.FontSizeMm} mm", final);
                    _previewGravParams = final;
                    BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
                    BtnGCodeBerechnen.Content    = "● G-Code berechnen";
                }
                else { _previewGravParams = null; }
            }
            else
            {
                // New entry created by drag
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
                    _previewGravParams = null;
                }
                else
                {
                    var final = _inlineParams! with { Text = text };
                    EnsureInlineVCarveCache(final);   // Fallback cache-warm
                    if (_history.Count > 0)
                        _history[_history.Count - 1] = new HistoryEntry("V-Carve",
                            $"\"{text.Replace('\n', ' ')}\" {final.FontFamily} {final.FontSizeMm} mm", final);
                    _previewGravParams = final;
                    BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
                    BtnGCodeBerechnen.Content    = "● G-Code berechnen";
                }
            }
        }
        finally { _suppressHistoryRegen = false; }
        _inlineParams = null;
    }

    private void SetActiveTool(CanvasTool tool)
    {
        // Inline-Texteditor schließen wenn Werkzeug wechselt (aber nur wenn es nicht VCarveTextSk bleibt)
        if (tool is not CanvasTool.VCarveTextSk && _inlineTextBox != null)
            FlushInlineEdit();

        // Pfad-Vorschau und Bogen-Warte-Zustand abbrechen wenn Werkzeug wechselt
        bool leavingPfad = _activeTool is CanvasTool.PfadStart or CanvasTool.PfadLinie or CanvasTool.PfadBogen or CanvasTool.PfadSpline
                           && tool is not (CanvasTool.PfadStart or CanvasTool.PfadLinie or CanvasTool.PfadBogen or CanvasTool.PfadSpline);
        if (leavingPfad)
        {
            _pfadMouseValid = false;
            _pfadBogenWaiting = false;
            _splinePointsBeingCreated.Clear();  // Clearen wenn Spline-Werkzeug verlassen wird
            _splineFinalized = false;
        }

        // Auch Spline-Punkte leeren wenn von Spline zu anderen Pfad-Werkzeugen wechselt (aber NICHT wenn bereits fertig)
        if (_activeTool == CanvasTool.PfadSpline && tool is not CanvasTool.PfadSpline && tool is (CanvasTool.PfadStart or CanvasTool.PfadLinie or CanvasTool.PfadBogen) && !_splineFinalized)
        {
            _splinePointsBeingCreated.Clear();
            _splineFinalized = false;
        }

        _activeTool = tool;
        var active   = new System.Windows.Media.SolidColorBrush(
                           System.Windows.Media.Color.FromArgb(0xCC, 0xDD, 0xD0, 0xB0));
        var inactive = System.Windows.Media.Brushes.Transparent;
        BtnToolHand.Background        = tool == CanvasTool.Hand       ? active : inactive;
        BtnToolZoom.Background        = tool == CanvasTool.Zoom       ? active : inactive;
        BtnToolVCarveTextSk.Background  = tool == CanvasTool.VCarveTextSk ? active : inactive;
        BtnToolMove.Background        = tool == CanvasTool.Move       ? active : inactive;
        BtnToolPfeil.Background       = tool == CanvasTool.Pfeil      ? active : inactive;
        BtnToolVermassen.Background   = tool == CanvasTool.Vermassen  ? active : inactive;
        if (tool != CanvasTool.Vermassen)
        {
            CloseVermTextBox();
            _vermState = 0; _vermIsHolding = false; _vermP1Idx = -1; _vermP2Idx = -1;
            _vermQ1Idx = -1; _vermQ2Idx = -1; _vermEditIdx = -1; _vermActiveEdge = 0;
            _vermKreisIdx = -1; _vermHoverKreis = -1; _vermHoverRkt = -1; _vermRktIdx = -1;
            _geomMode = GeomConstraintMode.None; _geomFirstIdx = -1; _geomFirstIdx2 = -1;
            _selectedGeomIdx = -1;
            UpdateGeomModeButtons();
            if (CanvasGrid.IsMouseCaptured) CanvasGrid.ReleaseMouseCapture();
        }
        VermToolbar.Visibility = tool == CanvasTool.Vermassen ? Visibility.Visible : Visibility.Collapsed;
        BtnToolPfadStart.Background   = tool == CanvasTool.PfadStart  ? active : inactive;
        BtnToolPfadLinie.Background   = tool == CanvasTool.PfadLinie  ? active : inactive;
        BtnToolPfadKurve.Background   = tool == CanvasTool.PfadBogen  ? active : inactive;
        BtnToolPfadSpline.Background  = tool == CanvasTool.PfadSpline ? active : inactive;

        // Spline-Panel nur sichtbar wenn Spline-Werkzeug aktiv ist
        if (tool == CanvasTool.PfadSpline)
        {
            TabEigenschaften.IsSelected = true;
            PnlPfadStart.Visibility = Visibility.Visible;
            PnlPfadEigStartOnly.Visibility = Visibility.Collapsed;
            PnlPfadEigBezug.Visibility = Visibility.Collapsed;
            PnlPfadEigBogenMid.Visibility = Visibility.Collapsed;
            PnlPfadEigVerrundung.Visibility = Visibility.Collapsed;
            PnlPfadEigSpline.Visibility = Visibility.Visible;
            PfadEigTitel.Text = "Pfad – Spline";
        }
        else if (leavingPfad)
        {
            PnlPfadEigSpline.Visibility = Visibility.Collapsed;
        }
        BtnToolRechteck.Background    = tool == CanvasTool.Rechteck  ? active : inactive;
        BtnToolKreis.Background       = tool == CanvasTool.Kreis     ? active : inactive;
        BtnToolNEck.Background        = tool == CanvasTool.NEck      ? active : inactive;
        BtnToolReihenloch.Background  = tool == CanvasTool.Reihenloch ? active : inactive;
        _rlbHoverMm = null;
        if (tool == CanvasTool.Reihenloch)
            _rlbIdx = HistoryList.SelectedItem is HistoryEntry { Params: ReihenlochbohrungParams } se
                ? _history.IndexOf(se) : -1;
        else
        { _rlbDrag = 0; _rlbPreview = null; }
        CanvasGrid.Cursor = tool switch
        {
            CanvasTool.Hand         => Cursors.Hand,
            CanvasTool.Zoom         => Cursors.Cross,
            CanvasTool.VCarveTextSk => Cursors.Cross,
            CanvasTool.PfadStart    => Cursors.Cross,
            CanvasTool.PfadLinie    => Cursors.Cross,
            CanvasTool.PfadBogen    => Cursors.Cross,
            CanvasTool.PfadSpline   => Cursors.Cross,
            CanvasTool.Rechteck     => Cursors.Cross,
            CanvasTool.Kreis        => Cursors.Cross,
            CanvasTool.NEck         => Cursors.Cross,
            CanvasTool.Reihenloch   => Cursors.Cross,
            _                       => Cursors.Arrow,   // Move: context-sensitive (see MouseMove)
        };
        DrawSkia?.InvalidateVisual();
    }

    private void OnToolHand(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Hand ? CanvasTool.Select : CanvasTool.Hand);

    private void OnToolZoom(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Zoom ? CanvasTool.Select : CanvasTool.Zoom);

    private void OnToolWerkzeugWechsel(object sender, RoutedEventArgs e) => WerkzeugWechselDialog();

    /// <summary>
    /// Öffnet die Werkzeugliste im Auswahlmodus. Bei Auswahl wird das Werkzeug als aktives
    /// Werkzeug übernommen und ein "Werkzeugwechsel"-Eintrag im Verlauf angelegt.
    /// Gibt true zurück, wenn ein Werkzeug gewählt wurde, sonst false (Abbruch).
    /// </summary>
    private bool WerkzeugWechselDialog()
    {
        var dlg = new WerkzeugeDialog(_werkzeuge, SaveWerkzeuge, waehlModus: true) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Ausgewaehlt == null) return false;

        _aktivesWerkzeug = dlg.Ausgewaehlt;
        BtnToolWerkzeugWechsel.ToolTip = $"Werkzeug wechsel – aktiv: {_aktivesWerkzeug.DisplayName}";

        _history.Add(new HistoryEntry("Werkzeugwechsel", _aktivesWerkzeug.DisplayName,
            new WerkzeugWechselParams(_aktivesWerkzeug.Nr, _aktivesWerkzeug.Name, _aktivesWerkzeug.Drehzahl,
                _aktivesWerkzeug.Durchmesser, _aktivesWerkzeug.Schneidenwinkel)));
        HistoryList.SelectedItem = _history[^1];
        return true;
    }

    /// <summary>
    /// Stellt sicher, dass ein aktives Werkzeug gesetzt ist. Ist noch keines gewählt,
    /// öffnet sich automatisch die Werkzeugauswahl. Gibt false zurück, wenn der Benutzer
    /// die Auswahl abbricht (in diesem Fall soll die aufrufende Aktion nicht ausgeführt werden).
    /// </summary>
    private bool SicherstellenAktivesWerkzeug()
    {
        if (_aktivesWerkzeug != null) return true;
        return WerkzeugWechselDialog();
    }

    private void OnToolVCarveTextSk(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.VCarveTextSk ? CanvasTool.Select : CanvasTool.VCarveTextSk);

    private void OnToolMove(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Move ? CanvasTool.Select : CanvasTool.Move);

    private void OnToolPfeil(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Pfeil ? CanvasTool.Select : CanvasTool.Pfeil);

    private void OnToolVermassen(object sender, RoutedEventArgs e)
        => SetActiveTool(_activeTool == CanvasTool.Vermassen ? CanvasTool.Select : CanvasTool.Vermassen);

    // ── Geometrie-Constraint-Toolbar ─────────────────────────────
    private void OnVermKoinzident  (object sender, RoutedEventArgs e) => ToggleGeomMode(GeomConstraintMode.Coincident);
    private void OnVermRechtwinklig(object sender, RoutedEventArgs e) => ToggleGeomMode(GeomConstraintMode.Perpendicular);
    private void OnVermParallel    (object sender, RoutedEventArgs e) => ToggleGeomMode(GeomConstraintMode.Parallel);

    private void ToggleGeomMode(GeomConstraintMode mode)
    {
        if (_activeTool != CanvasTool.Vermassen) SetActiveTool(CanvasTool.Vermassen);
        // Laufende Länge/Winkel/Kanten-Eingabe abbrechen, damit die Klick-States nicht
        // mit dem Geometrie-Constraint-Modus kollidieren.
        CloseVermTextBox();
        _vermState = 0; _vermIsHolding = false;
        _vermP1Idx = -1; _vermP2Idx = -1; _vermQ1Idx = -1; _vermQ2Idx = -1;
        _vermEditIdx = -1; _vermActiveEdge = 0; _vermPtIdx = -1;
        if (CanvasGrid.IsMouseCaptured) CanvasGrid.ReleaseMouseCapture();
        _geomMode = _geomMode == mode ? GeomConstraintMode.None : mode;   // erneuter Klick = abwählen
        _geomFirstIdx = -1; _geomFirstIdx2 = -1;
        UpdateGeomModeButtons();
        DrawSkia?.InvalidateVisual();
    }

    private void UpdateGeomModeButtons()
    {
        var active   = new System.Windows.Media.SolidColorBrush(
                           System.Windows.Media.Color.FromArgb(0xCC, 0xDD, 0xD0, 0xB0));
        var inactive = System.Windows.Media.Brushes.Transparent;
        BtnVermKoinzident.Background   = _geomMode == GeomConstraintMode.Coincident    ? active : inactive;
        BtnVermRechtwinklig.Background = _geomMode == GeomConstraintMode.Perpendicular ? active : inactive;
        BtnVermParallel.Background     = _geomMode == GeomConstraintMode.Parallel      ? active : inactive;
    }

    private void OnZoom100(object sender, RoutedEventArgs e)   => ZoomTo100();
    private void OnZoom1to1(object sender, RoutedEventArgs e)  => ZoomTo1to1();

    private void UpdateZoomRubberBand(Point p1, Point p2)
    {
        if (_zoomRubberBand == null)
        {
            _zoomRubberBand = new System.Windows.Shapes.Rectangle
            {
                Stroke            = System.Windows.Media.Brushes.White,
                StrokeThickness   = 1.5,
                StrokeDashArray   = new System.Windows.Media.DoubleCollection { 5, 3 },
                Fill              = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromArgb(30, 80, 140, 255)),
                IsHitTestVisible  = false,
            };
        }
        double x = Math.Min(p1.X, p2.X);
        double y = Math.Min(p1.Y, p2.Y);
        _zoomRubberBand.Width  = Math.Abs(p2.X - p1.X);
        _zoomRubberBand.Height = Math.Abs(p2.Y - p1.Y);
        System.Windows.Controls.Canvas.SetLeft(_zoomRubberBand, x);
        System.Windows.Controls.Canvas.SetTop (_zoomRubberBand, y);
        if (!SimToolCanvas.Children.Contains(_zoomRubberBand))
            SimToolCanvas.Children.Add(_zoomRubberBand);
    }

    private void ClearZoomRubberBand()
    {
        if (_zoomRubberBand != null)
            SimToolCanvas.Children.Remove(_zoomRubberBand);
    }

    private void UpdateTextRubberBand(Point p1, Point p2)
    {
        if (_textRubberBand == null)
        {
            _textRubberBand = new System.Windows.Shapes.Rectangle
            {
                Stroke          = System.Windows.Media.Brushes.Orange,
                StrokeThickness = 1.5,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 5, 3 },
                Fill            = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromArgb(25, 255, 160, 0)),
                IsHitTestVisible = false,
            };
        }
        double x = Math.Min(p1.X, p2.X);
        double y = Math.Min(p1.Y, p2.Y);
        _textRubberBand.Width  = Math.Abs(p2.X - p1.X);
        _textRubberBand.Height = Math.Abs(p2.Y - p1.Y);
        System.Windows.Controls.Canvas.SetLeft(_textRubberBand, x);
        System.Windows.Controls.Canvas.SetTop (_textRubberBand, y);
        if (!SimToolCanvas.Children.Contains(_textRubberBand))
            SimToolCanvas.Children.Add(_textRubberBand);
    }

    private void ClearTextRubberBand()
    {
        if (_textRubberBand != null)
            SimToolCanvas.Children.Remove(_textRubberBand);
    }

    // ── Rechteck-Werkzeug: Rubber-Band ───────────────────────────
    private void UpdateRktRubberBand(Point p1, Point p2)
    {
        if (_rktRubberBand == null)
        {
            _rktRubberBand = new System.Windows.Shapes.Rectangle
            {
                Stroke           = System.Windows.Media.Brushes.Orange,
                StrokeThickness  = 1.5,
                StrokeDashArray  = new System.Windows.Media.DoubleCollection { 5, 3 },
                Fill             = new System.Windows.Media.SolidColorBrush(
                                       System.Windows.Media.Color.FromArgb(25, 255, 160, 0)),
                IsHitTestVisible = false,
            };
        }
        _rktRubberBand.Width  = Math.Abs(p2.X - p1.X);
        _rktRubberBand.Height = Math.Abs(p2.Y - p1.Y);
        System.Windows.Controls.Canvas.SetLeft(_rktRubberBand, Math.Min(p1.X, p2.X));
        System.Windows.Controls.Canvas.SetTop (_rktRubberBand, Math.Min(p1.Y, p2.Y));
        if (!SimToolCanvas.Children.Contains(_rktRubberBand))
            SimToolCanvas.Children.Add(_rktRubberBand);
    }

    private void ClearRktRubberBand()
    {
        if (_rktRubberBand != null) SimToolCanvas.Children.Remove(_rktRubberBand);
    }

    // ── Kreis-Werkzeug: Rubber-Band ──────────────────────────────
    private void UpdateKreisRubberBand(Point center, double radiusPx)
    {
        if (_kreisRubberBand == null)
        {
            _kreisRubberBand = new System.Windows.Shapes.Ellipse
            {
                Stroke           = System.Windows.Media.Brushes.Orange,
                StrokeThickness  = 1.5,
                StrokeDashArray  = new System.Windows.Media.DoubleCollection { 5, 3 },
                Fill             = new System.Windows.Media.SolidColorBrush(
                                       System.Windows.Media.Color.FromArgb(25, 255, 160, 0)),
                IsHitTestVisible = false,
            };
        }
        double d = radiusPx * 2;
        _kreisRubberBand.Width  = d;
        _kreisRubberBand.Height = d;
        System.Windows.Controls.Canvas.SetLeft(_kreisRubberBand, center.X - radiusPx);
        System.Windows.Controls.Canvas.SetTop (_kreisRubberBand, center.Y - radiusPx);
        if (!SimToolCanvas.Children.Contains(_kreisRubberBand))
            SimToolCanvas.Children.Add(_kreisRubberBand);
    }

    private void ClearKreisRubberBand()
    {
        if (_kreisRubberBand != null) SimToolCanvas.Children.Remove(_kreisRubberBand);
    }

    private void ShowKreisDurchmesserBox(Point cursorPx)
    {
        CloseKreisDurchmesserBox();
        _kreisInputBox = new TextBox
        {
            Width  = 90, Height = 26,
            Text   = "10",
            ToolTip = "Durchmesser (mm) — Enter bestätigt",
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x22, 0x99, 0xFF)),
            BorderThickness = new Thickness(2),
            Background      = new SolidColorBrush(Color.FromArgb(230, 30, 30, 40)),
            Foreground      = System.Windows.Media.Brushes.White,
            CaretBrush      = System.Windows.Media.Brushes.White,
            FontSize        = 13,
            Padding         = new Thickness(4, 2, 4, 2),
        };
        System.Windows.Controls.Canvas.SetLeft(_kreisInputBox, cursorPx.X + 14);
        System.Windows.Controls.Canvas.SetTop (_kreisInputBox, cursorPx.Y - 30);
        SimToolCanvas.Children.Add(_kreisInputBox);
        _kreisInputBox.KeyDown += KreisInputBox_KeyDown;
        _kreisInputBox.SelectAll();
        _kreisInputBox.Focus();
        Keyboard.Focus(_kreisInputBox);
    }

    private void CloseKreisDurchmesserBox()
    {
        if (_kreisInputBox == null) return;
        SimToolCanvas.Children.Remove(_kreisInputBox);
        _kreisInputBox = null;
    }

    private void UpdateNEckRubberBand(Point center, double radiusPx, int ecken)
    {
        if (_neckRubberBand == null)
        {
            _neckRubberBand = new System.Windows.Shapes.Polygon
            {
                Stroke           = System.Windows.Media.Brushes.Orange,
                StrokeThickness  = 1.5,
                StrokeDashArray  = new System.Windows.Media.DoubleCollection { 5, 3 },
                Fill             = new System.Windows.Media.SolidColorBrush(
                                       System.Windows.Media.Color.FromArgb(25, 255, 160, 0)),
                IsHitTestVisible = false,
            };
        }
        var points = new System.Windows.Media.PointCollection();
        for (int i = 0; i < ecken; i++)
        {
            double angle = 2 * Math.PI * i / ecken - Math.PI / 2;
            double px = center.X + radiusPx * Math.Cos(angle);
            double py = center.Y + radiusPx * Math.Sin(angle);
            points.Add(new Point(px, py));
        }
        _neckRubberBand.Points = points;
        if (!SimToolCanvas.Children.Contains(_neckRubberBand))
            SimToolCanvas.Children.Add(_neckRubberBand);
    }

    private void ClearNEckRubberBand()
    {
        if (_neckRubberBand != null) SimToolCanvas.Children.Remove(_neckRubberBand);
    }

    private void KreisInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string txt = _kreisInputBox!.Text.Replace(',', '.');
            if (double.TryParse(txt, System.Globalization.NumberStyles.Float, inv, out double d) && d > 0.1)
            {
                double cx2mm = SnapX((_kreisDragCenter.X - _panX) / _zoom);
                double cy2mm = SnapY(WorkY - (_kreisDragCenter.Y - _panY) / _zoom);
                CloseKreisDurchmesserBox();
                ClearKreisRubberBand();
                _kreisDragging = false;
                AddKreis(cx2mm, cy2mm, d / 2.0);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseKreisDurchmesserBox();
            ClearKreisRubberBand();
            _kreisDragging = false;
            DrawSkia?.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void DrawBogenPreview(SKCanvas canvas,
        (double x, double y) p1, (double x, double y) p2, (double x, double y) mid, float lt)
    {
        double dx = p2.x - p1.x, dy = p2.y - p1.y;
        double L = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-6) return;

        double perpX = -dy / L, perpY = dx / L;
        double mcx = (p1.x + p2.x) / 2, mcy = (p1.y + p2.y) / 2;
        double h = (mid.x - mcx) * perpX + (mid.y - mcy) * perpY;

        using var paint = new SKPaint
        {
            Color = new SKColor(220, 80, 0, 180),
            Style = SKPaintStyle.Stroke, StrokeWidth = lt * 1.5f, IsAntialias = true
        };

        double wy = WorkY;
        if (Math.Abs(h) < 1e-6)
        {
            canvas.DrawLine((float)p1.x, (float)(wy - p1.y),
                            (float)p2.x, (float)(wy - p2.y), paint);
            return;
        }

        double r     = (L * L / 4 + h * h) / (2 * Math.Abs(h));
        double sign  = h > 0 ? 1 : -1;
        double t     = h - sign * r;
        double ocx   = mcx + t * perpX;
        double ocy   = mcy + t * perpY;

        float dOcx = (float)ocx, dOcy = (float)(wy - ocy), dR = (float)r;
        float dp1x = (float)p1.x, dp1y = (float)(wy - p1.y);
        float dp2x = (float)p2.x, dp2y = (float)(wy - p2.y);

        double a1    = Math.Atan2(dp1y - dOcy, dp1x - dOcx) * 180 / Math.PI;
        double a2    = Math.Atan2(dp2y - dOcy, dp2x - dOcx) * 180 / Math.PI;
        double sweep = a2 - a1;
        // h > 0 → CCW in mm = CW in screen → positive sweep in SkiaSharp
        if (h > 0) { if (sweep < 0) sweep += 360; }
        else       { if (sweep > 0) sweep -= 360; }

        canvas.DrawArc(new SKRect(dOcx - dR, dOcy - dR, dOcx + dR, dOcy + dR),
                       (float)a1, (float)sweep, false, paint);
    }

    private void DrawSplinePreviewSk(SKCanvas canvas, List<(double x, double y)> pts,
                                     string splineMode, double tension, bool finalized = false)
    {
        if (pts.Count < 2) return;

        using var paint = new SKPaint
        {
            Color = new SKColor(255, 165, 0, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        double sc = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
        double px(double x) => _topRect.Left + x * sc;
        double py(double y) => _topRect.Bottom - y * sc;

        // Wenn finalized: zeichne nur die geklickten Spline-Punkte ohne Cursor-Verfolgung
        // KEINE gerade Linie zwischen den Kontrollpunkten!
        var ptsToInterpolate = new List<(double x, double y)>(pts);
        if (!finalized && _pfadMouseValid)
            ptsToInterpolate.Add(_pfadMouseMm);

        var interpolated = InterpolateFullSpline(ptsToInterpolate, splineMode, tension);

        // Nur die interpolierte Kurve zeichnen (Linien zwischen interpolierten Punkten)
        // NICHT die direkte Verbindung zwischen Kontrollpunkten!
        for (int i = 1; i < interpolated.Count; i++)
        {
            float x1 = (float)px(interpolated[i - 1].x);
            float y1 = (float)py(interpolated[i - 1].y);
            float x2 = (float)px(interpolated[i].x);
            float y2 = (float)py(interpolated[i].y);
            canvas.DrawLine(x1, y1, x2, y2, paint);
        }
    }

    private void StartInlineTextEdit(Point screenA, Point screenB)
    {
        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return;
        _inlineExistingIdx = -1;   // new entry

        double ax = SnapX((screenA.X - _panX) / _zoom);
        double ay = SnapY(wy - (screenA.Y - _panY) / _zoom);
        double bx = SnapX((screenB.X - _panX) / _zoom);
        double by = SnapY(wy - (screenB.Y - _panY) / _zoom);

        // Position auf Raster ausrichten, ABER Größe NICHT runden!
        // (Damit Feldgröße exakt der aufgezogenen Größe entspricht)
        double left   = Math.Max(0, Math.Min(ax, bx));      // Gesnappte Position
        double bottom = Math.Max(0, Math.Min(ay, by));      // Gesnappte Position

        // Größe aus UNGESNAPPTEN Werten (damit sie nicht durch Raster verändert wird)
        double width  = Math.Round(Math.Abs(bx - ax), 2);   // Rohe Größe (nicht gerundet)
        double height = Math.Round(Math.Abs(by - ay), 2);   // Rohe Größe (nicht gerundet)

        if (width < 0.5 || height < 0.5) return;

        var lastGrav  = _history.Select(h => h.Params).OfType<GraviereParams>().LastOrDefault();
        double fontSizeMm = 30.0;  // Standard-Schriftgröße: 30mm

        // Temporärer History-Eintrag mit leerem Text — wird live beim Tippen aktualisiert
        _inlineParams = new GraviereParams(
            Text:            "",
            FontFamily:      lastGrav?.FontFamily ?? "Arial",
            FontSizeMm:      fontSizeMm,
            XRel:            left,
            YRel:            bottom,
            TextBreite:      width,
            TextHoehe:       height,
            ZTiefe:          lastGrav?.ZTiefe ?? 3.0,
            SchneidenWinkel: lastGrav?.SchneidenWinkel ?? 90.0,
            FraeserD:        lastGrav?.FraeserD ?? 0.1,
            Vorschub:        lastGrav?.Vorschub ?? 1000,
            Drehzahl:        lastGrav?.Drehzahl ?? 24000,
            Bezugspunkt:     "Unten links",
            IsVCarve:        true,
            UseSkia:         _activeTool == CanvasTool.VCarveTextSk);

        _suppressHistoryRegen = true;
        try { _history.Add(new HistoryEntry("V-Carve", "…", _inlineParams)); }
        finally { _suppressHistoryRegen = false; }

        _previewGravParams           = _inlineParams;
        HistoryList.SelectedItem     = _history[^1];
        TabEigenschaften.IsSelected  = true;

        // Transparente TextBox — Position und Größe mit _zoom
        double screenLeft = left * _zoom + _panX;
        double screenTop  = (wy - (bottom + height)) * _zoom + _panY;
        double screenW    = width * _zoom;
        double screenH    = height * _zoom;

        var (hAlign, vAlign) = ParseAlignment(_inlineParams.Ausrichtung, _inlineParams.AusrichtungV);
        _inlineTextBox = new ImprovedSkiaTextEditor
        {
            Width  = screenW,
            Height = screenH,
            HorizontalAlign = hAlign,
            VerticalAlign = vAlign,
        };
        // Schriftgröße OHNE GetDpiScale() setzen — wird später von UpdateFontSize() korrigiert
        _inlineTextBox.SetText("", _inlineParams.FontFamily, (float)(_inlineParams.FontSizeMm * _zoom), _zoom);
        System.Windows.Controls.Canvas.SetLeft(_inlineTextBox, screenLeft);
        System.Windows.Controls.Canvas.SetTop(_inlineTextBox, screenTop);
        SimToolCanvas.Children.Add(_inlineTextBox);

        _inlineTextBox.TextChanged      += InlineTextBox_TextChanged;
        _inlineTextBox.SelectionChanged += InlineTextBox_SelectionChanged;
        _inlineTextBox.LostFocus        += InlineTextBox_LostFocus;

        // Registriere Mouse-Events direkt auf CanvasGrid, da SKElement sie blockiert
        // WICHTIG: Null-Check, da diese Handler auch nach dem Löschen des Textfeldes aufgerufen werden können
        CanvasGrid.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (_inlineTextBox == null) return;  // Textfeld wurde gelöscht → ignoriere Event
            var pos = e.GetPosition(_inlineTextBox);
            if (pos.X >= 0 && pos.X <= _inlineTextBox.ActualWidth &&
                pos.Y >= 0 && pos.Y <= _inlineTextBox.ActualHeight)
            {
                _inlineTextBox.HandleMouseDown(pos.X, pos.Y);
                e.Handled = true;
            }
        };

        CanvasGrid.PreviewMouseMove += (s, e) =>
        {
            if (_inlineTextBox == null) return;  // Textfeld wurde gelöscht → ignoriere Event

            // Prüfe ob Maus über dem Text-Editor ist
            var pos = e.GetPosition(_inlineTextBox);
            bool mouseOverTextBox = pos.X >= 0 && pos.X <= _inlineTextBox.ActualWidth &&
                                    pos.Y >= 0 && pos.Y <= _inlineTextBox.ActualHeight;

            // Setze Cursor auf IBeam wenn über TextBox, sonst auf Standard-Cursor
            CanvasGrid.Cursor = mouseOverTextBox ? Cursors.IBeam : _activeTool switch
            {
                CanvasTool.Hand         => Cursors.Hand,
                CanvasTool.Zoom         => Cursors.Cross,
                CanvasTool.VCarveTextSk => Cursors.Cross,
                CanvasTool.PfadStart    => Cursors.Cross,
                CanvasTool.PfadLinie    => Cursors.Cross,
                CanvasTool.PfadBogen    => Cursors.Cross,
                CanvasTool.PfadSpline   => Cursors.Cross,
                CanvasTool.Rechteck     => Cursors.Cross,
                CanvasTool.Kreis        => Cursors.Cross,
                CanvasTool.NEck         => Cursors.Cross,
                CanvasTool.Reihenloch   => Cursors.Cross,
                _                       => Cursors.Arrow,
            };

            // Nur wenn Maus gedrückt und über TextBox: weitergeben an Editor
            if (!_inlineTextBox.IsFocused || Mouse.LeftButton != MouseButtonState.Pressed)
                return;

            _inlineTextBox.HandleMouseMove(pos.X, pos.Y);
            e.Handled = true;
        };

        _inlineTextBox.Focus();
        Keyboard.Focus(_inlineTextBox);
        CanvasGrid.Cursor = Cursors.IBeam;

        // Flag setzen: Schriftgröße muss nach DPI-Berechnung korrigiert werden
        _needsInitialFontSizeUpdate = true;
        _lastDpiScale = 0;  // Erzwinge Update bei nächstem OnDrawSkia

        // Sofort OnDrawSkia erzwingen
        DrawSkia?.InvalidateVisual();
        Dispatcher.BeginInvoke(() =>
        {
            if (_inlineTextBox != null && _inlineParams != null)
            {
                DrawSkia?.InvalidateVisual();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Bestehendes Textfeld editieren ───────────────────────────────────
    private void StartEditExistingTextField(int historyIdx)
    {
        if (historyIdx < 0 || historyIdx >= _history.Count) return;
        if (_history[historyIdx].Params is not GraviereParams gp) return;
        double fh = gp.TextHoehe > 0 ? gp.TextHoehe : gp.FontSizeMm;
        if (fh <= 0 || gp.TextBreite <= 0) return;

        // mm-Grenzen → Screen-Koordinaten mit _zoom
        var (leftMm, bottomMm, wMm, hMm) = TextFieldBoundsInMm(gp);
        double screenLeft = leftMm * _zoom + _panX;
        double screenTop  = (WorkY - (bottomMm + hMm)) * _zoom + _panY;
        double screenW    = wMm * _zoom;
        double screenH    = hMm * _zoom;
        if (screenW < 4 || screenH < 4) return;

        _inlineExistingIdx           = historyIdx;
        _inlineParams                = gp;
        _previewGravParams           = gp;
        HistoryList.SelectedItem     = _history[historyIdx];
        TabEigenschaften.IsSelected  = true;

        var (hAlign2, vAlign2) = ParseAlignment(gp.Ausrichtung, gp.AusrichtungV);
        _inlineTextBox = new ImprovedSkiaTextEditor
        {
            Width  = screenW,
            Height = screenH,
            HorizontalAlign = hAlign2,
            VerticalAlign = vAlign2,
        };
        // Schriftgröße OHNE GetDpiScale() setzen — wird später von UpdateFontSize() korrigiert
        _inlineTextBox.SetText(gp.Text, gp.FontFamily, (float)(gp.FontSizeMm * _zoom), _zoom);
        System.Windows.Controls.Canvas.SetLeft(_inlineTextBox, screenLeft);
        System.Windows.Controls.Canvas.SetTop(_inlineTextBox, screenTop);
        SimToolCanvas.Children.Add(_inlineTextBox);

        _inlineTextBox.TextChanged      += InlineTextBox_TextChanged;
        _inlineTextBox.SelectionChanged += InlineTextBox_SelectionChanged;
        _inlineTextBox.LostFocus        += InlineTextBox_LostFocus;

        // Registriere Mouse-Events direkt auf CanvasGrid, da SKElement sie blockiert
        // WICHTIG: Null-Check, da diese Handler auch nach dem Löschen des Textfeldes aufgerufen werden können
        CanvasGrid.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (_inlineTextBox == null) return;  // Textfeld wurde gelöscht → ignoriere Event
            var pos = e.GetPosition(_inlineTextBox);
            if (pos.X >= 0 && pos.X <= _inlineTextBox.ActualWidth &&
                pos.Y >= 0 && pos.Y <= _inlineTextBox.ActualHeight)
            {
                _inlineTextBox.HandleMouseDown(pos.X, pos.Y);
                e.Handled = true;
            }
        };

        CanvasGrid.PreviewMouseMove += (s, e) =>
        {
            if (_inlineTextBox == null) return;  // Textfeld wurde gelöscht → ignoriere Event

            // Prüfe ob Maus über dem Text-Editor ist
            var pos = e.GetPosition(_inlineTextBox);
            bool mouseOverTextBox = pos.X >= 0 && pos.X <= _inlineTextBox.ActualWidth &&
                                    pos.Y >= 0 && pos.Y <= _inlineTextBox.ActualHeight;

            // Setze Cursor auf IBeam wenn über TextBox, sonst auf Standard-Cursor
            CanvasGrid.Cursor = mouseOverTextBox ? Cursors.IBeam : _activeTool switch
            {
                CanvasTool.Hand         => Cursors.Hand,
                CanvasTool.Zoom         => Cursors.Cross,
                CanvasTool.VCarveTextSk => Cursors.Cross,
                CanvasTool.PfadStart    => Cursors.Cross,
                CanvasTool.PfadLinie    => Cursors.Cross,
                CanvasTool.PfadBogen    => Cursors.Cross,
                CanvasTool.PfadSpline   => Cursors.Cross,
                CanvasTool.Rechteck     => Cursors.Cross,
                CanvasTool.Kreis        => Cursors.Cross,
                CanvasTool.NEck         => Cursors.Cross,
                CanvasTool.Reihenloch   => Cursors.Cross,
                _                       => Cursors.Arrow,
            };

            // Nur wenn Maus gedrückt und über TextBox: weitergeben an Editor
            if (!_inlineTextBox.IsFocused || Mouse.LeftButton != MouseButtonState.Pressed)
                return;

            _inlineTextBox.HandleMouseMove(pos.X, pos.Y);
            e.Handled = true;
        };

        _inlineTextBox.Focus();
        Keyboard.Focus(_inlineTextBox);
        CanvasGrid.Cursor = Cursors.IBeam;

        // Flag setzen: Schriftgröße muss nach DPI-Berechnung korrigiert werden
        _needsInitialFontSizeUpdate = true;
        _lastDpiScale = 0;  // Erzwinge Update bei nächstem OnDrawSkia

        // Sofort OnDrawSkia erzwingen
        DrawSkia?.InvalidateVisual();
        Dispatcher.BeginInvoke(() =>
        {
            if (_inlineTextBox != null && _inlineParams != null)
            {
                DrawSkia?.InvalidateVisual();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Konvertiert Ausrichtungs-Strings in TextHorizontalAlign/TextVerticalAlign Enums
    /// </summary>
    private (TextHorizontalAlign hAlign, TextVerticalAlign vAlign) ParseAlignment(string ausrichtung, string ausrichtungV)
    {
        var hAlign = ausrichtung switch
        {
            "Mitte" => TextHorizontalAlign.Center,
            "Rechts" => TextHorizontalAlign.Right,
            _ => TextHorizontalAlign.Left
        };

        var vAlign = ausrichtungV switch
        {
            "Mitte" => TextVerticalAlign.Center,
            "Oben" => TextVerticalAlign.Top,
            _ => TextVerticalAlign.Bottom
        };

        return (hAlign, vAlign);
    }

    private void RepositionInlineTextBox(bool updateFontSize = true)
    {
        if (_inlineTextBox == null || _inlineParams == null) return;
        double wy = WorkY;
        if (wy <= 0) return;
        var (leftMm, bottomMm, wMm, hMm) = TextFieldBoundsInMm(_inlineParams);

        // Position und Größe: mm * _zoom (WPF-Koordinaten)
        // Schriftgröße wird extra mit _dpiScale skaliert (Skia-Rendering)
        double sl = leftMm * _zoom + _panX;
        double st = (wy - (bottomMm + hMm)) * _zoom + _panY;
        double sw = wMm * _zoom;
        double sh = hMm * _zoom;
        _inlineTextBox.Width    = sw;
        _inlineTextBox.Height   = sh;

        float newFontSize = (float)(_inlineParams.FontSizeMm * _zoom * _dpiScale);

        // DEAKTIVIERT: UpdateFontSize() ändert die Schriftgröße für ALLE Zeichen
        // Das ist bei der Bearbeitung unerwünscht!
        // Die Selection-basierte Formatierung wird durch UpdateEditorFontFamily() erledigt.

        //// Die Sicht-/Zoom-Anpassung des Textfelds muss das komplette Modell neu skalieren.
        //// Aber NUR wenn die Schriftgröße geändert wurde!
        //// Wenn nur Ausrichtung geändert wird, sollen die formatierten Zeichen NICHT überschrieben werden!
        //if (updateFontSize)
        //{
        //    _inlineTextBox.UpdateFontSize(newFontSize);
        //}

        System.Windows.Controls.Canvas.SetLeft(_inlineTextBox, sl);
        System.Windows.Controls.Canvas.SetTop (_inlineTextBox, st);

        // Erzwinge Neuzeichnung mit neuen Dimensionen
        // Damit wird OnPaintSurface() aufgerufen und der Text wird mit der neuen Breite neu umgebrochen
        _inlineTextBox.InvalidateVisual();
    }

    private void RepositionVermTextBox()
    {
        if (_vermTextBox == null) return;
        Point pos;
        if (_vermState == 2) pos = VermLabelScreenPos();
        else if (_vermState == 4 && _vermEditIdx >= 0 && _vermEditIdx < _vermPlaced.Count)
        {
            var lmm = VermLabelPosMm(_vermPlaced[_vermEditIdx]);
            if (lmm == null) return;
            pos = new Point(lmm.Value.x * _zoom + _panX, (WorkY - lmm.Value.y) * _zoom + _panY);
        }
        else return;
        System.Windows.Controls.Canvas.SetLeft(_vermTextBox, pos.X - 40);
        System.Windows.Controls.Canvas.SetTop (_vermTextBox, pos.Y - 28);
    }

    private void InlineTextBox_SelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectedCharacterPropertiesFromEditor();
    }

    private void InlineTextBox_TextChanged(object? sender, EventArgs e)
    {
        if (_inlineTextBox == null || _inlineParams == null) return;
        _inlineParams      = _inlineParams with { Text = _inlineTextBox.GetText() };
        _previewGravParams = _inlineParams;

        // Aktualisiere die Eigenschaften (Schriftgröße, etc.) des aktuellen Zeichens
        UpdateSelectedCharacterPropertiesFromEditor();

        // Debounced VCarve-Vorausberechnung + Canvas-Update: 300 ms nach letztem Tastendruck.
        // Kein InvalidateVisual() pro Tastendruck – das würde BuildTextGeo bei jedem Zeichen aufrufen.
        if (_inlineVCarveTimer == null)
        {
            _inlineVCarveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(300) };
            _inlineVCarveTimer.Tick += (_, _) =>
            {
                _inlineVCarveTimer.Stop();
                // Canvas mit aktuellem Text neu zeichnen (BuildTextGeo einmal, nicht pro Taste)
                DrawSkia?.InvalidateVisual();
                if (_inlineParams != null) EnsureInlineVCarveCache(_inlineParams);
            };
        }
        _inlineVCarveTimer.Stop();
        _inlineVCarveTimer.Start();
    }

    private void EnsureInlineVCarveCache(GraviereParams gp)
    {
        if (!gp.IsVCarve) return;
        _inlineVCarveCts?.Cancel();
        var cts = _inlineVCarveCts = new System.Threading.CancellationTokenSource();
        LaunchVCacheAsync(gp, cts.Token);
    }

    /// <summary>
    /// Startet BuildTextGeo + VCarve-Berechnung auf einem Hintergrund-STA-Thread.
    /// Kein UI-Thread-Blocking. Ergebnis landet via Dispatcher in den Caches.
    /// </summary>
    private void LaunchVCacheAsync(GraviereParams gp,
        System.Threading.CancellationToken token = default)
    {
        if (_vCarvePending.Contains(gp)) return;
        bool needsGeo = !_textGeoCache.ContainsKey(gp);
        bool needsVc  = gp.IsVCarve && !_vCarveCache.ContainsKey(gp);
        if (!needsGeo && !needsVc) return;
        _vCarvePending.Add(gp);
        double wx   = WorkX, wy = WorkY;
        double step = gp.SampleStepMm > 0
            ? gp.SampleStepMm
            : Math.Clamp(gp.FontSizeMm / 300.0, 0.02, 0.1);
        double simp = gp.VereinfachungMm;
        var t = new System.Threading.Thread(() =>
        {
            if (token.IsCancellationRequested) { Dispatcher.BeginInvoke(() => _vCarvePending.Remove(gp)); return; }
            GCodeGenerator.TextGeoCtx ctx;
            try { ctx = gp.UseSkia
                      ? GCodeGenerator.BuildTextGeoSk(gp, wx, wy)
                      : GCodeGenerator.BuildTextGeo(gp, wx, wy); }
            catch { Dispatcher.BeginInvoke(() => _vCarvePending.Remove(gp)); return; }
            List<GCodeGenerator.VCarveCircle>? circles = null;
            if (gp.IsVCarve && !token.IsCancellationRequested)
                circles = GCodeGenerator.ResampleVCarveCircles(
                    GCodeGenerator.ComputeVCarveCircles(gp, ctx, step),
                    spacingMm: step, simplifyMm: simp);
            Dispatcher.BeginInvoke(() =>
            {
                _vCarvePending.Remove(gp);
                if (!token.IsCancellationRequested)
                {
                    _textGeoCache[gp] = ctx;
                    if (circles != null) _vCarveCache[gp] = circles;
                    DrawSkia?.InvalidateVisual();
                }
            });
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    private void CommitInlineText()
    {
        if (_inlineTextBox == null) return;

        try
        {
            var text        = _inlineTextBox.GetText();
            int existingIdx = _inlineExistingIdx;

            _inlineTextBox.TextChanged -= InlineTextBox_TextChanged;
            _inlineTextBox.LostFocus   -= InlineTextBox_LostFocus;
            SimToolCanvas.Children.Remove(_inlineTextBox);
            _inlineTextBox     = null;
            _inlineExistingIdx = -1;

            // Timer stoppen — Fallback: cache synchron befüllen falls Debounce noch nicht gelaufen ist
            _inlineVCarveTimer?.Stop();

            if (existingIdx >= 0)
            {
                // Editing existing entry
                if (!string.IsNullOrWhiteSpace(text) && _inlineParams != null && existingIdx < _history.Count)
                {
                    var final = _inlineParams with { Text = text };
                    EnsureInlineVCarveCache(final);   // Fallback: garantiert Cache-Hit beim UpdateAll
                    _suppressHistoryRegen = true;
                    try { _history[existingIdx] = new HistoryEntry("V-Carve",
                        $"\"{text.Replace('\n', ' ')}\" {final.FontFamily} {final.FontSizeMm} mm", final); }
                    finally { _suppressHistoryRegen = false; }
                    _previewGravParams           = final;
                    HistoryList.SelectedItem     = _history[existingIdx];
                    BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
                    BtnGCodeBerechnen.Content    = "● G-Code berechnen";
                }
                else { _previewGravParams = null; }
                _inlineParams = null;
                // Werkzeug bleibt aktiv — Nutzer muss bewusst wechseln
                UpdateAll();
                return;
            }

            // New entry created by drag
            if (string.IsNullOrWhiteSpace(text))
            {
                _suppressHistoryRegen = true;
                try { if (_history.Count > 0) _history.RemoveAt(_history.Count - 1); }
                finally { _suppressHistoryRegen = false; }
                _previewGravParams = null;
                _inlineParams      = null;
                // Werkzeug bleibt aktiv — Nutzer muss bewusst wechseln
                UpdateAll();
                return;
            }

            var finalNew = _inlineParams! with { Text = text };
            EnsureInlineVCarveCache(finalNew);   // Fallback: garantiert Cache-Hit beim UpdateAll
            _suppressHistoryRegen = true;
            try
            {
                if (_history.Count > 0)
                    _history[_history.Count - 1] = new HistoryEntry("V-Carve",
                        $"\"{text.Replace('\n', ' ')}\" {finalNew.FontFamily} {finalNew.FontSizeMm} mm", finalNew);
            }
            finally { _suppressHistoryRegen = false; }

            _previewGravParams           = finalNew;
            _inlineParams                = null;
            HistoryList.SelectedItem     = _history[^1];
            TabEigenschaften.IsSelected  = true;
            BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
            BtnGCodeBerechnen.Content    = "● G-Code berechnen";
            // Werkzeug bleibt aktiv — Nutzer muss bewusst wechseln
            UpdateAll();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ERROR in CommitInlineText (full): {ex}");
            // Cleanup bei Fehler sicherstellen
            if (_inlineTextBox != null)
            {
                try
                {
                    _inlineTextBox.TextChanged -= InlineTextBox_TextChanged;
                    _inlineTextBox.LostFocus   -= InlineTextBox_LostFocus;
                    SimToolCanvas.Children.Remove(_inlineTextBox);
                    _inlineTextBox = null;
                }
                catch { }
            }
            _inlineExistingIdx = -1;
            _inlineParams      = null;
            _previewGravParams = null;
            // NICHT löschen: _inlineTextLineSegments bleibt für Visualisierung bestehen!
            // Zeige dem Benutzer, was schiefging
            MessageBox.Show($"Fehler beim Speichern des Textes:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelInlineText()
    {
        if (_inlineTextBox == null) return;
        bool isExisting = _inlineExistingIdx >= 0;
        _inlineTextBox.TextChanged    -= InlineTextBox_TextChanged;
        _inlineTextBox.LostFocus      -= InlineTextBox_LostFocus;
        SimToolCanvas.Children.Remove(_inlineTextBox);
        _inlineTextBox     = null;
        _inlineExistingIdx = -1;
        _inlineParams      = null;
        _previewGravParams = null;
        _ctrlResizeMode    = false;
        _inlineVCarveTimer?.Stop();   // Debounce-Timer abbrechen

        if (!isExisting)
        {
            // Remove the temp history entry that was added for a new drag
            _suppressHistoryRegen = true;
            try { if (_history.Count > 0) _history.RemoveAt(_history.Count - 1); }
            finally { _suppressHistoryRegen = false; }
        }
        // For existing entries: history unchanged, preview cleared → original shows again
        UpdateAll();
    }

    private void InlineTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter wird im ImprovedSkiaTextEditor als Zeilenumbruch behandelt
        // Escape speichert und schließt das Textfeld (LostFocus wird auch CommitInlineText aufrufen)
        if (e.Key == Key.Escape) { CommitInlineText(); e.Handled = true; }
    }

    private void InlineTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_ctrlResizeReopen >= 0) return;   // LostFocus während Ctrl-Resize ignorieren

        // Speichere Selection bevor Fokus verloren geht!
        // So können Eigenschaften auf markierte Buchstaben angewendet werden
        if (_inlineTextBox != null)
        {
            var (start, end) = _inlineTextBox.GetSelection();
            _savedSelectionStart = start;
            _savedSelectionEnd = end;
        }

        // Don't close editor if focus goes to Properties panel (allows live editing)
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (IsDescendantOf(focused, EigPanel))
        {
            return;  // Keep editor open while adjusting properties
        }

        // WICHTIG: BeginInvoke(), um das Canvas.Children.Remove() zu deferrieren
        // So wird der Rendering-Cycle nicht unterbrochen
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                CommitInlineText();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in CommitInlineText: {ex}");
                MessageBox.Show($"Fehler beim Speichern des Textes:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                CancelInlineText();  // Cleanup bei Fehler
            }
        });
    }

    /// <summary>
    /// Check if a visual element is a descendant of a parent element
    /// </summary>
    private bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (child == parent)
                return true;
            child = LogicalTreeHelper.GetParent(child);
        }
        return false;
    }

    // Ctrl gedrückt/losgelassen während Inline-Edit → Resize-Handles ein-/ausblenden
    private void InlineCtrlDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && !_ctrlResizeMode)
        {
            _ctrlResizeMode = true;
            DrawSkia?.InvalidateVisual();
        }
    }
    private void InlineCtrlUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && _ctrlResizeMode)
        {
            _ctrlResizeMode = false;
            CanvasGrid.Cursor = Cursors.IBeam;
            DrawSkia?.InvalidateVisual();
        }
    }

    // ── Verschieben-Werkzeug ─────────────────────────────────────────────

    private (double left, double bottom, double width, double height) TextFieldBoundsInMm(GraviereParams gp)
    {
        double fh = gp.TextHoehe > 0 ? gp.TextHoehe : gp.FontSizeMm;
        var (ox, oy) = GCodeGenerator.ConvertBezugspunkt(gp.Bezugspunkt, gp.XRel, gp.YRel, WorkX, WorkY);
        if (gp.Bezugspunkt.Contains("Oben"))                                       oy -= fh;
        if (gp.Bezugspunkt.Contains("rechts", StringComparison.OrdinalIgnoreCase)) ox -= gp.TextBreite;
        if (gp.Bezugspunkt is "Mitte" or "Oben Mitte" or "Unten Mitte")            ox -= gp.TextBreite / 2;
        return (ox, oy, gp.TextBreite, fh);
    }

    private int HitTestTextField(double mmX, double mmY)
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not GraviereParams gp || gp.TextBreite <= 0) continue;
            double fh = gp.TextHoehe > 0 ? gp.TextHoehe : gp.FontSizeMm;
            if (fh <= 0) continue;
            var (left, bottom, w, h) = TextFieldBoundsInMm(gp);
            if (mmX >= left && mmX <= left + w && mmY >= bottom && mmY <= bottom + h)
                return i;
        }
        return -1;
    }

    private void StartMoveTextField(int idx, double mmX, double mmY)
    {
        _moveHistoryIdx  = idx;
        _moveDragStartMm = new Point(mmX, mmY);
        var gp = (GraviereParams)_history[idx].Params;
        (_moveStartRefX, _moveStartRefY) = GCodeGenerator.ConvertBezugspunkt(
            gp.Bezugspunkt, gp.XRel, gp.YRel, WorkX, WorkY);
        HistoryList.SelectedItem    = _history[idx];
        TabEigenschaften.IsSelected = true;
        _previewGravParams = gp;
    }

    private (double left, double bottom, double width, double height) RechteckBoundsInMm(RechteckParams rp)
    {
        var (refX, refY) = GCodeGenerator.ConvertBezugspunkt(rp.Bezugspunkt, rp.XRel, rp.YRel, WorkX, WorkY);
        var (bx, by) = rp.Bezugspunkt switch
        {
            "Unten links"  => (0.0,           0.0),
            "Unten Mitte"  => (-rp.Breite/2,  0.0),
            "Unten rechts" => (-rp.Breite,    0.0),
            "Links Mitte"  => (0.0,           -rp.Hoehe/2),
            "Mitte"        => (-rp.Breite/2,  -rp.Hoehe/2),
            "Rechts Mitte" => (-rp.Breite,    -rp.Hoehe/2),
            "Oben links"   => (0.0,           -rp.Hoehe),
            "Oben Mitte"   => (-rp.Breite/2,  -rp.Hoehe),
            _              => (-rp.Breite,    -rp.Hoehe)
        };
        return (refX + bx, refY + by, rp.Breite, rp.Hoehe);
    }

    private int HitTestRechteck(double mmX, double mmY)
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not RechteckParams rp) continue;
            var (left, bottom, w, h) = RechteckBoundsInMm(rp);
            if (mmX >= left && mmX <= left + w && mmY >= bottom && mmY <= bottom + h)
                return i;
        }
        return -1;
    }

    // 0=BL 1=BR 2=TL 3=TR  4=BM 5=RM 6=TM 7=LM  –1=kein Treffer
    private int HitTestRktCorner(double mmX, double mmY, RechteckParams rp)
    {
        var (left, bottom, w, h) = RechteckBoundsInMm(rp);
        double r     = 10.0 / _zoom;
        double right = left + w;
        double top   = bottom + h;
        double mx    = left + w / 2;
        double my    = bottom + h / 2;
        (double x, double y)[] anchors =
        [
            (left,  bottom), (right, bottom), (left,  top),   (right, top),
            (mx,    bottom), (right, my),     (mx,    top),   (left,  my),
        ];
        for (int i = 0; i < anchors.Length; i++)
            if (Math.Abs(mmX - anchors[i].x) <= r && Math.Abs(mmY - anchors[i].y) <= r)
                return i;
        return -1;
    }

    private void StartMoveRechteck(int idx, double mmX, double mmY)
    {
        _moveHistoryIdx   = idx;
        _moveResizeCorner = -1;
        _moveDragStartMm  = new Point(mmX, mmY);
        var rp = (RechteckParams)_history[idx].Params;
        (_moveStartRefX, _moveStartRefY) = GCodeGenerator.ConvertBezugspunkt(
            rp.Bezugspunkt, rp.XRel, rp.YRel, WorkX, WorkY);
        HistoryList.SelectedItem    = _history[idx];
        TabEigenschaften.IsSelected = true;
        _previewRktParams = rp;
    }

    private void StartResizeRechteck(int idx, int corner, double mmX, double mmY)
    {
        _moveHistoryIdx   = idx;
        _moveResizeCorner = corner;
        _moveDragStartMm  = new Point(mmX, mmY);
        var rp = (RechteckParams)_history[idx].Params;
        (_resizeStartLeft, _resizeStartBottom, _resizeStartWidth, _resizeStartHeight)
            = RechteckBoundsInMm(rp);
        HistoryList.SelectedItem    = _history[idx];
        TabEigenschaften.IsSelected = true;
        _previewRktParams = rp;
    }

    private void UpdateMoveRechteck(double mmX, double mmY)
    {
        if (_moveHistoryIdx < 0 || _previewRktParams == null) return;
        if (_moveResizeCorner >= 0) { UpdateResizeRechteck(mmX, mmY); return; }
        var rp = (RechteckParams)_history[_moveHistoryIdx].Params;
        double newRefX = SnapX(_moveStartRefX + (mmX - _moveDragStartMm.X));
        double newRefY = SnapY(_moveStartRefY + (mmY - _moveDragStartMm.Y));
        var (newXRel, newYRel) = AbsToRel(rp.Bezugspunkt, newRefX, newRefY, WorkX, WorkY);
        _previewRktParams = rp with { XRel = Math.Round(newXRel, 3), YRel = Math.Round(newYRel, 3) };
        DrawSkia?.InvalidateVisual();
    }

    private void UpdateResizeRechteck(double mmX, double mmY)
    {
        if (_moveHistoryIdx < 0 || _previewRktParams == null) return;
        var rp = (RechteckParams)_history[_moveHistoryIdx].Params;
        double sx = SnapX(mmX), sy = SnapY(mmY);
        const double minSize = 1.0;
        double newLeft   = _resizeStartLeft;
        double newBottom = _resizeStartBottom;
        double newRight  = _resizeStartLeft   + _resizeStartWidth;
        double newTop    = _resizeStartBottom + _resizeStartHeight;
        switch (_moveResizeCorner)
        {
            case 0: newLeft   = Math.Min(sx, newRight  - minSize); newBottom = Math.Min(sy, newTop    - minSize); break;
            case 1: newRight  = Math.Max(sx, newLeft   + minSize); newBottom = Math.Min(sy, newTop    - minSize); break;
            case 2: newLeft   = Math.Min(sx, newRight  - minSize); newTop    = Math.Max(sy, newBottom + minSize); break;
            case 3: newRight  = Math.Max(sx, newLeft   + minSize); newTop    = Math.Max(sy, newBottom + minSize); break;
            case 4: newBottom = Math.Min(sy, newTop    - minSize); break;
            case 5: newRight  = Math.Max(sx, newLeft   + minSize); break;
            case 6: newTop    = Math.Max(sy, newBottom + minSize); break;
            case 7: newLeft   = Math.Min(sx, newRight  - minSize); break;
        }
        double newW = Math.Round(newRight - newLeft, 3);
        double newH = Math.Round(newTop   - newBottom, 3);
        var (newRefX, newRefY) = BezugAbsPos(rp.Bezugspunkt, newLeft, newBottom, newW, newH);
        var (newXRel, newYRel) = AbsToRel(rp.Bezugspunkt, newRefX, newRefY, WorkX, WorkY);
        _previewRktParams = rp with
        {
            XRel   = Math.Round(newXRel, 3),
            YRel   = Math.Round(newYRel, 3),
            Breite = newW,
            Hoehe  = newH,
        };
        DrawSkia?.InvalidateVisual();
    }

    private void CommitMoveRechteck()
    {
        if (_moveHistoryIdx < 0 || _previewRktParams == null) return;
        var final        = _previewRktParams;
        var entry        = _history[_moveHistoryIdx];
        bool isResize    = _moveResizeCorner >= 0;
        _moveResizeCorner = -1;
        int committedIdx = _moveHistoryIdx;
        _suppressHistoryRegen = true;
        try { _history[committedIdx] = new HistoryEntry(entry.Label,
            isResize ? $"B={final.Breite:F1} H={final.Hoehe:F1}"
                     : $"X={final.XRel:F2} Y={final.YRel:F2}", final); }
        finally { _suppressHistoryRegen = false; }
        _moveHistoryIdx   = -1;
        _previewRktParams = null;
        HistoryList.SelectedItem    = _history[committedIdx];
        TabEigenschaften.IsSelected = true;
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        UpdateAll();
    }

    private int HitTestKreis(double mmX, double mmY)
    {
        double tol = 8.0 / _zoom;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Params is not KreisParams kr) continue;
            var (cx, cy) = GCodeGenerator.ConvertBezugspunkt(kr.Bezugspunkt, kr.XRel, kr.YRel, WorkX, WorkY);
            double dist = Math.Sqrt((mmX - cx) * (mmX - cx) + (mmY - cy) * (mmY - cy));
            if (dist <= kr.Radius + tol)
                return i;
        }
        return -1;
    }

    private void StartMoveKreis(int idx, double mmX, double mmY)
    {
        _moveHistoryIdx  = idx;
        _moveDragStartMm = new Point(mmX, mmY);
        var kr = (KreisParams)_history[idx].Params;
        (_moveStartRefX, _moveStartRefY) = GCodeGenerator.ConvertBezugspunkt(kr.Bezugspunkt, kr.XRel, kr.YRel, WorkX, WorkY);
        HistoryList.SelectedItem    = _history[idx];
        TabEigenschaften.IsSelected = true;
        _previewKreisParams = kr;
    }

    private void UpdateMoveKreis(double mmX, double mmY)
    {
        if (_moveHistoryIdx < 0 || _previewKreisParams == null) return;
        double newCx = SnapX(_moveStartRefX + (mmX - _moveDragStartMm.X));
        double newCy = SnapY(_moveStartRefY + (mmY - _moveDragStartMm.Y));
        var (newXRel, newYRel) = AbsToRel(_previewKreisParams.Bezugspunkt, newCx, newCy, WorkX, WorkY);
        _previewKreisParams = _previewKreisParams with
        {
            XRel = Math.Round(newXRel, 3),
            YRel = Math.Round(newYRel, 3)
        };
        DrawSkia?.InvalidateVisual();
    }

    private void CommitMoveKreis()
    {
        if (_moveHistoryIdx < 0 || _previewKreisParams == null) return;
        var final        = _previewKreisParams;
        var entry        = _history[_moveHistoryIdx];
        int committedIdx = _moveHistoryIdx;
        _suppressHistoryRegen = true;
        try { _history[committedIdx] = new HistoryEntry(entry.Label,
            $"M={final.XRel:F2}/{final.YRel:F2} R={final.Radius}", final); }
        finally { _suppressHistoryRegen = false; }
        _moveHistoryIdx     = -1;
        _previewKreisParams = null;
        HistoryList.SelectedItem    = _history[committedIdx];
        TabEigenschaften.IsSelected = true;
        _suppressNextAutoFit = true;
        RegenerateGCodeFromHistory();
        UpdateAll();
    }

    // Inverse of GCodeGenerator.ConvertBezugspunkt
    private static (double xRel, double yRel) AbsToRel(string bezug, double absX, double absY, double w, double h)
        => bezug switch
        {
            "Unten links"  => (absX,         absY),
            "Oben links"   => (absX,         h - absY),
            "Unten rechts" => (w - absX,     absY),
            "Oben rechts"  => (w - absX,     h - absY),
            "Links Mitte"  => (absX,         absY - h / 2),
            "Rechts Mitte" => (w - absX,     absY - h / 2),
            "Oben Mitte"   => (absX - w / 2, h - absY),
            "Unten Mitte"  => (absX - w / 2, absY),
            _              => (absX - w / 2, absY - h / 2),  // "Mitte"
        };

    /// <summary>Absolute mm-Position des Bezugspunkts innerhalb eines Bounding-Boxes.</summary>
    private static (double refX, double refY) BezugAbsPos(
        string bezug, double left, double bottom, double width, double height)
        => bezug switch
        {
            "Unten links"  => (left,              bottom),
            "Oben links"   => (left,              bottom + height),
            "Unten rechts" => (left + width,      bottom),
            "Oben rechts"  => (left + width,      bottom + height),
            "Links Mitte"  => (left,              bottom + height / 2),
            "Rechts Mitte" => (left + width,      bottom + height / 2),
            "Oben Mitte"   => (left + width / 2,  bottom + height),
            "Unten Mitte"  => (left + width / 2,  bottom),
            _              => (left + width / 2,  bottom + height / 2),  // "Mitte"
        };

    /// <summary>
    /// Gibt den nächsten Anker-Index zurück wenn der Cursor in der Trefferzone liegt:
    /// 0=BL 1=BR 2=TL 3=TR  4=BM 5=RM 6=TM 7=LM  –1=kein Treffer
    /// </summary>
    private int HitTestMoveCorner(double mmX, double mmY, GraviereParams gp)
    {
        var (left, bottom, w, h) = TextFieldBoundsInMm(gp);
        double r  = 10.0 / _zoom;
        double mx = left + w / 2;
        double my = bottom + h / 2;
        (double cx, double cy, int idx)[] anchors =
        [
            (left,     bottom,     0),   // BL
            (left + w, bottom,     1),   // BR
            (left,     bottom + h, 2),   // TL
            (left + w, bottom + h, 3),   // TR
            (mx,       bottom,     4),   // BM
            (left + w, my,         5),   // RM
            (mx,       bottom + h, 6),   // TM
            (left,     my,         7),   // LM
        ];
        foreach (var (cx, cy, idx) in anchors)
            if (Math.Abs(mmX - cx) <= r && Math.Abs(mmY - cy) <= r)
                return idx;
        return -1;
    }

    private static Cursor CornerCursor(int corner) => corner switch
    {
        0 or 3 => Cursors.SizeNESW,
        1 or 2 => Cursors.SizeNWSE,
        4 or 6 => Cursors.SizeNS,
        5 or 7 => Cursors.SizeWE,
        _      => Cursors.Arrow,
    };

    private void StartResizeTextField(int idx, int corner, double mmX, double mmY)
    {
        _moveHistoryIdx   = idx;
        _moveResizeCorner = corner;
        _moveDragStartMm  = new Point(mmX, mmY);
        var gp = (GraviereParams)_history[idx].Params;
        (_resizeStartLeft, _resizeStartBottom, _resizeStartWidth, _resizeStartHeight)
            = TextFieldBoundsInMm(gp);
        HistoryList.SelectedItem    = _history[idx];
        TabEigenschaften.IsSelected = true;
        _previewGravParams = gp;
    }

    /// <summary>X-Koordinate an Raster und Werkstückkanten fangen.</summary>
    private double SnapX(double x)
    {
        if (_rasterEnabled && _rasterX > 0)
            x = Math.Round(x / _rasterX) * _rasterX;
        double t = 6.0 / _zoom;   // Fangradius = 6 Bildschirmpixel in mm
        if (Math.Abs(x)        < t) return 0;
        if (Math.Abs(x - WorkX) < t) return WorkX;
        return x;
    }

    /// <summary>Y-Koordinate an Raster und Werkstückkanten fangen.</summary>
    private double SnapY(double y)
    {
        if (_rasterEnabled && _rasterY > 0)
            y = Math.Round(y / _rasterY) * _rasterY;
        double t = 6.0 / _zoom;
        if (Math.Abs(y)        < t) return 0;
        if (Math.Abs(y - WorkY) < t) return WorkY;
        return y;
    }

    private string EigAusrichtung =>
        EigAusrRechts.IsChecked == true ? "Rechts"
      : EigAusrMitte.IsChecked  == true ? "Mitte" : "Links";

    private void UpdateMoveTextField(double mmX, double mmY)
    {
        if (_moveHistoryIdx < 0) return;
        if (_previewRktParams != null) { UpdateMoveRechteck(mmX, mmY); return; }
        if (_moveResizeCorner >= 0) { UpdateResizeTextField(mmX, mmY); return; }
        var gp = (GraviereParams)_history[_moveHistoryIdx].Params;
        double newRefX = SnapX(_moveStartRefX + (mmX - _moveDragStartMm.X));
        double newRefY = SnapY(_moveStartRefY + (mmY - _moveDragStartMm.Y));
        var (newXRel, newYRel) = AbsToRel(gp.Bezugspunkt, newRefX, newRefY, WorkX, WorkY);
        _previewGravParams = gp with
        {
            XRel        = Math.Round(newXRel, 3),
            YRel        = Math.Round(newYRel, 3),
            Ausrichtung = EigAusrichtung,          // panel radio buttons win
        };
        UpdateAll();
    }

    private void UpdateResizeTextField(double mmX, double mmY)
    {
        if (_moveHistoryIdx < 0) return;
        var gp = (GraviereParams)_history[_moveHistoryIdx].Params;

        // Snap mouse to grid and workpiece edges before applying to any edge
        double sx = SnapX(mmX);
        double sy = SnapY(mmY);

        const double minSize = 0.5;
        double newLeft   = _resizeStartLeft;
        double newBottom = _resizeStartBottom;
        double newRight  = _resizeStartLeft  + _resizeStartWidth;
        double newTop    = _resizeStartBottom + _resizeStartHeight;

        switch (_moveResizeCorner)
        {
            case 0: // BL: TR fixiert
                newLeft   = Math.Min(sx, newRight  - minSize);
                newBottom = Math.Min(sy, newTop    - minSize);
                break;
            case 1: // BR: TL fixiert
                newRight  = Math.Max(sx, newLeft   + minSize);
                newBottom = Math.Min(sy, newTop    - minSize);
                break;
            case 2: // TL: BR fixiert
                newLeft   = Math.Min(sx, newRight  - minSize);
                newTop    = Math.Max(sy, newBottom + minSize);
                break;
            case 3: // TR: BL fixiert
                newRight  = Math.Max(sx, newLeft   + minSize);
                newTop    = Math.Max(sy, newBottom + minSize);
                break;
            case 4: // BM: Oberkante fix, Unterkante ziehen
                newBottom = Math.Min(sy, newTop    - minSize);
                break;
            case 5: // RM: Linke Kante fix, rechte ziehen
                newRight  = Math.Max(sx, newLeft   + minSize);
                break;
            case 6: // TM: Unterkante fix, Oberkante ziehen
                newTop    = Math.Max(sy, newBottom + minSize);
                break;
            case 7: // LM: Rechte Kante fix, linke ziehen
                newLeft   = Math.Min(sx, newRight  - minSize);
                break;
        }

        double newW = newRight - newLeft;
        double newH = newTop   - newBottom;
        var (newRefX, newRefY) = BezugAbsPos(gp.Bezugspunkt, newLeft, newBottom, newW, newH);
        var (newXRel, newYRel) = AbsToRel(gp.Bezugspunkt, newRefX, newRefY, WorkX, WorkY);

        _previewGravParams = gp with
        {
            XRel        = Math.Round(newXRel, 3),
            YRel        = Math.Round(newYRel, 3),
            TextBreite  = Math.Round(newW,    3),
            TextHoehe   = Math.Round(newH,    3),
            Ausrichtung = EigAusrichtung,          // panel radio buttons win
        };
        UpdateAll();
    }

    private void CommitMoveTextField()
    {
        if (_moveHistoryIdx < 0) return;
        if (_previewRktParams != null) { CommitMoveRechteck(); return; }
        if (_previewGravParams == null) return;
        var final    = _previewGravParams;
        var entry    = _history[_moveHistoryIdx];

        bool isResize = _moveResizeCorner >= 0;
        _moveResizeCorner = -1;

        if (entry.Params is GraviereParams origGp)
        {
            _vCarveCache.TryGetValue(origGp, out var origCircles);
            _textGeoCache.TryGetValue(origGp, out var origCtx);
            _vCarveCache.Remove(origGp);
            _textGeoCache.Remove(origGp);
            if (!isResize)
            {
                // Verschieben: Kreise und TextGeo nur verschieben – kein Neuberechnen nötig
                var (origRefX, origRefY) = GCodeGenerator.ConvertBezugspunkt(
                    origGp.Bezugspunkt, origGp.XRel, origGp.YRel, WorkX, WorkY);
                var (newRefX2, newRefY2) = GCodeGenerator.ConvertBezugspunkt(
                    final.Bezugspunkt, final.XRel, final.YRel, WorkX, WorkY);
                double dx = newRefX2 - origRefX, dy = newRefY2 - origRefY;
                if (origCircles != null)
                    _vCarveCache[final] = origCircles
                        .Select(c => c with { X = c.X + dx, Y = c.Y + dy })
                        .ToList();
                if (origCtx != null)
                    _textGeoCache[final] = origCtx with { Ox = origCtx.Ox + dx, Oy = origCtx.Oy + dy };
            }
            // Resize: Einträge entfernt, werden neu berechnet
        }

        int committedIdx = _moveHistoryIdx;
        _suppressHistoryRegen = true;
        try { _history[committedIdx] = new HistoryEntry(entry.Label,
            isResize ? $"B={final.TextBreite:F1} H={final.TextHoehe:F1}"
                     : $"X={final.XRel:F2} Y={final.YRel:F2}", final); }
        finally { _suppressHistoryRegen = false; }
        _moveHistoryIdx = -1;

        // Selektion wiederherstellen — ObservableCollection.Replace verliert SelectedItem
        HistoryList.SelectedItem    = _history[committedIdx];
        TabEigenschaften.IsSelected = true;

        BtnGCodeBerechnen.Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));
        BtnGCodeBerechnen.Content    = "● G-Code berechnen";
        UpdateAll();
    }

    private void ZoomToRect(Point parentP1, Point parentP2)
    {
        // Parent-Koordinaten → DrawCanvas-lokale Koordinaten (vor Transform)
        double lx1 = (parentP1.X - _panX) / _zoom;
        double ly1 = (parentP1.Y - _panY) / _zoom;
        double lx2 = (parentP2.X - _panX) / _zoom;
        double ly2 = (parentP2.Y - _panY) / _zoom;
        double localW = Math.Abs(lx2 - lx1);
        double localH = Math.Abs(ly2 - ly1);
        if (localW < 1 || localH < 1) return;

        double cw = DrawSkia.ActualWidth;
        double ch = DrawSkia.ActualHeight;
        double newZoom = Math.Clamp(Math.Min(cw / localW, ch / localH), 0.05, 200.0);

        double centerX = (Math.Min(lx1, lx2) + localW / 2);
        double centerY = (Math.Min(ly1, ly2) + localH / 2);
        _zoom = newZoom;
        _panX = cw / 2 - centerX * _zoom;
        _panY = ch / 2 - centerY * _zoom;
        ApplyCanvasTransform();
        UpdateAll();
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor  = e.Delta > 0 ? 1.18 : 1.0 / 1.18;
        double newZoom = Math.Clamp(_zoom * factor, 0.05, 200.0);

        // Mauszeiger-Position in DrawSkia-Layoutkoordinaten (kein RenderTransform → echte Screen-Pixel)
        var pt     = e.GetPosition(DrawSkia);
        double wx  = (pt.X - _panX) / _zoom;
        double wy  = (pt.Y - _panY) / _zoom;
        _zoom  = newZoom;
        _panX  = pt.X - wx * _zoom;
        _panY  = pt.Y - wy * _zoom;

        ApplyCanvasTransform();
        e.Handled = true;
    }

    // ── Canvas: Klick / Drag ─────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Klick auf inline text editor: überspringen und das Element den Event selbst behandeln lassen
        if (_inlineTextBox != null && e.OriginalSource is System.Windows.DependencyObject dep)
        {
            // Überprüfe, ob OriginalSource das Textfeld oder ein Kind davon ist
            var parent = dep;
            while (parent != null)
            {
                if (parent == _inlineTextBox)
                {
                    // Klick ist auf dem Textfeld → nicht behandeln
                    return;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent) as System.Windows.DependencyObject;
            }
        }

        // Template-Image: Rechtsklick für Kontextmenü (nur auf Bildfläche)
        if (_templateImage?.Bitmap != null && e.ChangedButton == MouseButton.Right)
        {
            var screenPt = e.GetPosition(CanvasGrid);
            double mmX = (screenPt.X - _panX) / _zoom;
            double mmY = (screenPt.Y - _panY) / _zoom;

            double sc = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
            double canvasMmX = (mmX - _topRect.Left) / sc;
            double canvasMmY = (_topRect.Bottom - mmY) / sc;

            // Überprüfe, ob Klick innerhalb der Bildbegrenzung liegt
            double left = _templateImage.X - _templateImage.Width / 2;
            double right = _templateImage.X + _templateImage.Width / 2;
            double bottom = _templateImage.Y - _templateImage.Height / 2;
            double top = _templateImage.Y + _templateImage.Height / 2;

            if (canvasMmX >= left && canvasMmX <= right && canvasMmY >= bottom && canvasMmY <= top)
            {
                _showTemplateContextMenu = true;
                e.Handled = true;
                return;
            }
        }
        _showTemplateContextMenu = false;

        // Rechtsklick außerhalb des Bildes - kein Menü, aber Pan erlauben
        if (e.ChangedButton == MouseButton.Right && !_showTemplateContextMenu)
        {
            if (CanvasGrid.ContextMenu != null)
                CanvasGrid.ContextMenu.IsOpen = false;
            // Nicht e.Handled setzen, damit Pan-Logik weitermacht!
        }

        // Template-Image: Ankerpunkt mit Move-Werkzeug vergrößern/verschieben
        if (_activeTool == CanvasTool.Move && _templateImage?.Bitmap != null && e.ChangedButton == MouseButton.Left)
        {
            var screenPt = e.GetPosition(CanvasGrid);
            double mmX = (screenPt.X - _panX) / _zoom;
            double mmY = (screenPt.Y - _panY) / _zoom;

            double sc = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
            double canvasMmX = (mmX - _topRect.Left) / sc;
            double canvasMmY = (_topRect.Bottom - mmY) / sc;

            int anchorIdx = _templateImage.GetClosestAnchorPoint(canvasMmX, canvasMmY, 10.0);

            if (anchorIdx >= 0)
            {
                _templateImageDragAnchor = anchorIdx;
                CanvasGrid.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // Zoom-Werkzeug
        if (_activeTool == CanvasTool.Zoom)
        {
            // Rechtsklick / Alt+Links → sofort herauszoomen
            if (e.ChangedButton == MouseButton.Right ||
                (e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Alt) != 0))
            {
                double newZoom = Math.Clamp(_zoom * 0.5, 0.05, 200.0);
                var screenPt = e.GetPosition(CanvasGrid);
                double wx = (screenPt.X - _panX) / _zoom, wy = (screenPt.Y - _panY) / _zoom;
                _panX += wx * (_zoom - newZoom);
                _panY += wy * (_zoom - newZoom);
                _zoom  = newZoom;
                UpdateAll();
                e.Handled = true;
                return;
            }
            // Linksklick → Drag-Tracking starten (Gummiband oder Klick-Zoom bei MouseUp)
            if (e.ChangedButton == MouseButton.Left)
            {
                _zoomDragStart  = e.GetPosition(CanvasGrid);
                _isZoomDragging = false;
                CanvasGrid.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // Klick auf Textfeld (VCarveText/Sk) → inline editieren oder Focus zurückgeben
        if ((_activeTool == CanvasTool.VCarveTextSk)
            && e.ChangedButton == MouseButton.Left
            && e.ClickCount == 1)
        {
            var pos2 = e.GetPosition(CanvasGrid);
            double ex = (pos2.X - _panX) / _zoom;
            double ey = WorkY - (pos2.Y - _panY) / _zoom;
            int tidx = HitTestTextField(ex, ey);
            if (tidx >= 0)
            {
                if (_inlineTextBox == null)
                {
                    // Start editing a new textfield
                    StartEditExistingTextField(tidx);
                    e.Handled = true;
                    return;
                }
                else if (_inlineExistingIdx == tidx && _inlineTextBox != null)
                {
                    // Click inside the currently active textfield
                    // Position cursor at click location and restore focus
                    var posInElement = e.GetPosition(_inlineTextBox);
                    _inlineTextBox.SetCursorAtPosition(posInElement.X, posInElement.Y);
                    _inlineTextBox.Focus();
                    Keyboard.Focus(_inlineTextBox);
                    CanvasGrid.Cursor = Cursors.IBeam;
                    e.Handled = true;
                    return;
                }
                else
                {
                    // Switching to a different textfield
                    CommitInlineText();
                    StartEditExistingTextField(tidx);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Doppelklick auf leere Fläche: Zoom 100 % mit zentriertem Werkstück
        if (_activeTool == CanvasTool.Select &&
            e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            var pos2 = e.GetPosition(CanvasGrid);
            double ex = (pos2.X - _panX) / _zoom;
            double ey = WorkY - (pos2.Y - _panY) / _zoom;
            if (HitTestTextField(ex, ey) >= 0) return;   // Doppelklick auf Textfeld → kein Zoom
            if (!_topRect.IsEmpty)
            {
                double cw2 = DrawSkia.ActualWidth, ch2 = DrawSkia.ActualHeight;
                ApplyCenterZoom(cw2, ch2, DefaultZoom(cw2, ch2));
                ApplyCanvasTransform();
                UpdateAll();
            }
            return;
        }

        // Reihenlochbohrung-Werkzeug: Klick = neue Bohrung, Anker/Pfeile ziehen
        if (_activeTool == CanvasTool.Reihenloch && e.ChangedButton == MouseButton.Left
            && Keyboard.Modifiers != ModifierKeys.Control)
        {
            var pos = e.GetPosition(CanvasGrid);
            RlbMouseDown((pos.X - _panX) / _zoom, WorkY - (pos.Y - _panY) / _zoom);
            e.Handled = true;
            return;
        }

        // Pfeil-Werkzeug: einzelne Pfad-Punkte und Segment-Mittelpunkte verschieben
        if (_activeTool == CanvasTool.Pfeil && e.ChangedButton == MouseButton.Left)
        {
            var pos  = e.GetPosition(CanvasGrid);
            double mmX = (pos.X - _panX) / _zoom;
            double mmY = WorkY - (pos.Y - _panY) / _zoom;

            int segIdx = HitTestPfadSegMid(mmX, mmY);
            if (segIdx >= 0 && _history[segIdx].Params is PfadPunktParams segP2)
            {
                _pfadSegDragIdx    = segIdx;
                _pfadSegDragIsArc  = segP2.Typ == PfadPunktTyp.Bogen;
                _pfadSegDragP1     = GetPfadAbsAt(segIdx - 1) ?? (mmX, mmY);
                _pfadSegDragP2     = GetPfadAbsAt(segIdx)     ?? (mmX, mmY);
                _pfadSegDragMouse  = (mmX, mmY);
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor  = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
            int pfadIdx = HitTestPfadPunkt(mmX, mmY);
            if (pfadIdx >= 0)
            {
                var absOpt = GetPfadAbsAt(pfadIdx);
                _pfadDragHistIdx = pfadIdx;
                _pfadDragOrigAbs = absOpt ?? (mmX, mmY);
                HistoryList.SelectedItem    = _history[pfadIdx];
                TabEigenschaften.IsSelected = true;
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
            e.Handled = true;
            return;
        }

        // Move-Werkzeug: ganzen Pfad verschieben + Objekte (Kreis, Rechteck, Textfeld)
        if (_activeTool == CanvasTool.Move && e.ChangedButton == MouseButton.Left)
        {
            var pos  = e.GetPosition(CanvasGrid);
            double mmX = (pos.X - _panX) / _zoom;
            double mmY = WorkY - (pos.Y - _panY) / _zoom;

            // Ankerpunkt → Skalieren (Priorität vor BBox-Interior)
            var (scaleChain, scaleAnchor) = HitTestPfadChainAnchor(mmX, mmY);
            if (scaleChain >= 0)
            {
                StartScalePfadChain(scaleChain, scaleAnchor);
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor = ScaleAnchorCursor(scaleAnchor);
                e.Handled = true;
                return;
            }

            // BBox-Interior → ganzen Pfad verschieben
            int chainIdx = HitTestPfadChainBBox(mmX, mmY);
            if (chainIdx >= 0)
            {
                StartMovePfadChain(chainIdx, mmX, mmY);
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            // Zuerst Ecken des selektierten Eintrags testen
            if (HistoryList.SelectedItem is HistoryEntry selEntry
                && selEntry.Params is GraviereParams selGp)
            {
                int corner = HitTestMoveCorner(mmX, mmY, selGp);
                if (corner >= 0)
                {
                    int selIdx = _history.IndexOf(selEntry);
                    StartResizeTextField(selIdx, corner, mmX, mmY);
                    CanvasGrid.CaptureMouse();
                    CanvasGrid.Cursor = CornerCursor(corner);
                    e.Handled = true;
                    return;
                }
            }

            // Dann Textfeld verschieben
            int idx = HitTestTextField(mmX, mmY);
            if (idx >= 0)
            {
                StartMoveTextField(idx, mmX, mmY);
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            // Rechteck: Ecke des selektierten Eintrags prüfen (Resize)
            if (HistoryList.SelectedItem is HistoryEntry selRktEntry
                && selRktEntry.Params is RechteckParams selRp)
            {
                int corner = HitTestRktCorner(mmX, mmY, selRp);
                if (corner >= 0)
                {
                    int selIdx = _history.IndexOf(selRktEntry);
                    StartResizeRechteck(selIdx, corner, mmX, mmY);
                    CanvasGrid.CaptureMouse();
                    CanvasGrid.Cursor = CornerCursor(corner);
                    e.Handled = true;
                    return;
                }
            }

            // Rechteck verschieben (Body-Hit)
            int rktIdx = HitTestRechteck(mmX, mmY);
            if (rktIdx >= 0)
            {
                StartMoveRechteck(rktIdx, mmX, mmY);
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
            int krIdx = HitTestKreis(mmX, mmY);
            if (krIdx >= 0)
            {
                StartMoveKreis(krIdx, mmX, mmY);
                CanvasGrid.CaptureMouse();
                CanvasGrid.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
            return;
        }

        // VCarveText/Sk + Ctrl: Resize-Handle anklicken
        if ((_activeTool == CanvasTool.VCarveTextSk) && _ctrlResizeMode
            && _inlineTextBox != null && _inlineExistingIdx >= 0
            && e.ChangedButton == MouseButton.Left)
        {
            var pos  = e.GetPosition(CanvasGrid);
            double mmX = (pos.X - _panX) / _zoom;
            double mmY = WorkY - (pos.Y - _panY) / _zoom;
            if (_inlineExistingIdx < _history.Count
                && _history[_inlineExistingIdx].Params is GraviereParams ctrlGp)
            {
                int corner = HitTestMoveCorner(mmX, mmY, ctrlGp);
                if (corner >= 0)
                {
                    int editIdx = _inlineExistingIdx;
                    _ctrlResizeReopen = editIdx;
                    FlushInlineEdit();                   // Text committen, Textbox schließen
                    HistoryList.SelectedItem = _history[editIdx];
                    StartResizeTextField(editIdx, corner, mmX, mmY);
                    CanvasGrid.CaptureMouse();
                    CanvasGrid.Cursor = CornerCursor(corner);
                    e.Handled = true;
                    return;
                }
            }
        }

        // VCarveText/Sk-Werkzeug: Abbrechen mit Rechtsklick
        if ((_activeTool == CanvasTool.VCarveTextSk)
            && e.ChangedButton == MouseButton.Right && _isTextDragging)
        {
            _isTextDragging = false;
            ClearTextRubberBand();
            e.Handled = true;
            return;
        }

        // VCarveText/Sk-Werkzeug: 2-Klick-Modus
        if ((_activeTool == CanvasTool.VCarveTextSk)
            && e.ChangedButton == MouseButton.Left && _inlineTextBox == null)
        {
            var pos = e.GetPosition(CanvasGrid);
            if (!_isTextDragging)
            {
                // Erster Klick: gefangene (Fadenkreuz-)Position als Startpunkt merken
                double mmX = SnapX((pos.X - _panX) / _zoom);
                double mmY = SnapY(WorkY - (pos.Y - _panY) / _zoom);
                _textDragStart  = new Point(mmX * _zoom + _panX, (WorkY - mmY) * _zoom + _panY);
                _pfadMouseMm    = (mmX, mmY);
                _pfadMouseValid = true;
                _isTextDragging = true;
            }
            else
            {
                // Zweiter Klick: Textfeld erstellen — Ende auch runden!
                _isTextDragging = false;
                ClearTextRubberBand();
                double mmX2 = SnapX((pos.X - _panX) / _zoom);
                double mmY2 = SnapY(WorkY - (pos.Y - _panY) / _zoom);
                var snappedEnd = new Point(mmX2 * _zoom + _panX, (WorkY - mmY2) * _zoom + _panY);
                StartInlineTextEdit(_textDragStart, snappedEnd);
            }
            e.Handled = true;
            return;
        }

        // Pfad-Werkzeuge: Punkt per Klick setzen
        if (e.ChangedButton == MouseButton.Left)
        {
            var posPf = e.GetPosition(CanvasGrid);
            double rawPfX = (posPf.X - _panX) / _zoom;
            double rawPfY = WorkY - (posPf.Y - _panY) / _zoom;
            double pfX = SnapX(rawPfX);
            double pfY = SnapY(rawPfY);

            if (_activeTool == CanvasTool.PfadStart)
            {
                AddPfadStart(pfX, pfY);
                e.Handled = true;
                return;
            }
            if (_activeTool == CanvasTool.PfadLinie)
            {
                AddPfadLinie(pfX, pfY);
                e.Handled = true;
                return;
            }
            if (_activeTool == CanvasTool.PfadSpline)
            {
                AddPfadSpline(pfX, pfY);
                e.Handled = true;
                return;
            }
            if (_activeTool == CanvasTool.PfadBogen)
            {
                if (!_pfadBogenWaiting)
                {
                    _pfadBogenEndAbs  = (pfX, pfY);
                    _pfadBogenWaiting = true;
                    DrawSkia?.InvalidateVisual();
                }
                else
                {
                    AddPfadBogen(_pfadBogenEndAbs, (rawPfX, rawPfY));
                    _pfadBogenWaiting = false;
                    DrawSkia?.InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
            if (_activeTool == CanvasTool.Rechteck)
            {
                var rp = e.GetPosition(CanvasGrid);
                double rsmmX = SnapX((rp.X - _panX) / _zoom);
                double rsmmY = SnapY(WorkY - (rp.Y - _panY) / _zoom);
                var snappedPx = new Point(rsmmX * _zoom + _panX, (WorkY - rsmmY) * _zoom + _panY);
                if (!_rktDragging)
                {
                    _rktDragStart   = snappedPx;
                    _pfadMouseMm    = (rsmmX, rsmmY);
                    _pfadMouseValid = true;
                    _rktDragging    = true;
                }
                else
                {
                    _rktDragging = false;
                    ClearRktRubberBand();
                    double x1mm = SnapX((Math.Min(_rktDragStart.X, snappedPx.X) - _panX) / _zoom);
                    double y1mm = SnapY(WorkY - (Math.Max(_rktDragStart.Y, snappedPx.Y) - _panY) / _zoom);
                    double x2mm = SnapX((Math.Max(_rktDragStart.X, snappedPx.X) - _panX) / _zoom);
                    double y2mm = SnapY(WorkY - (Math.Min(_rktDragStart.Y, snappedPx.Y) - _panY) / _zoom);
                    double bMm  = Math.Round(x2mm - x1mm, 3);
                    double hMm  = Math.Round(y2mm - y1mm, 3);
                    if (bMm > 0.1 && hMm > 0.1)
                        AddRechteck(x1mm, y1mm, bMm, hMm);
                }
                e.Handled = true;
                return;
            }
            if (_activeTool == CanvasTool.Kreis)
            {
                var rp2 = e.GetPosition(CanvasGrid);
                double cxMm = SnapX((rp2.X - _panX) / _zoom);
                double cyMm = SnapY(WorkY - (rp2.Y - _panY) / _zoom);
                var centerPx = new Point(cxMm * _zoom + _panX, (WorkY - cyMm) * _zoom + _panY);
                if (!_kreisDragging)
                {
                    _kreisDragCenter = centerPx;
                    _pfadMouseMm     = (cxMm, cyMm);
                    _pfadMouseValid  = true;
                    _kreisDragging   = true;
                    ShowKreisDurchmesserBox(centerPx);
                }
                else
                {
                    _kreisDragging = false;
                    CloseKreisDurchmesserBox();
                    ClearKreisRubberBand();
                    double cx2mm = SnapX((_kreisDragCenter.X - _panX) / _zoom);
                    double cy2mm = SnapY(WorkY - (_kreisDragCenter.Y - _panY) / _zoom);
                    double dx = cxMm - cx2mm, dy = cyMm - cy2mm;
                    double radMm = Math.Round(Math.Sqrt(dx*dx + dy*dy), 3);
                    if (radMm > 0.1)
                        AddKreis(cx2mm, cy2mm, radMm);
                }
                e.Handled = true;
                return;
            }
            if (_activeTool == CanvasTool.NEck)
            {
                var np = e.GetPosition(CanvasGrid);
                double ncxMm = SnapX((np.X - _panX) / _zoom);
                double ncyMm = SnapY(WorkY - (np.Y - _panY) / _zoom);
                var ncenterPx = new Point(ncxMm * _zoom + _panX, (WorkY - ncyMm) * _zoom + _panY);
                if (!_neckDragging)
                {
                    _neckDragCenter = ncenterPx;
                    _pfadMouseMm     = (ncxMm, ncyMm);
                    _pfadMouseValid  = true;
                    _neckDragging   = true;
                }
                else
                {
                    _neckDragging = false;
                    ClearNEckRubberBand();
                    double ncx2mm = SnapX((_neckDragCenter.X - _panX) / _zoom);
                    double ncy2mm = SnapY(WorkY - (_neckDragCenter.Y - _panY) / _zoom);
                    double ndx = ncxMm - ncx2mm, ndy = ncyMm - ncy2mm;
                    double nradMm = Math.Round(Math.Sqrt(ndx*ndx + ndy*ndy), 3);
                    if (nradMm > 0.1)
                        AddNEck(ncx2mm, ncy2mm, nradMm, 6);
                }
                e.Handled = true;
                return;
            }
        }

        // Vermassen-Werkzeug
        if (_activeTool == CanvasTool.Vermassen && e.ChangedButton == MouseButton.Left)
        {
            var vpos = e.GetPosition(CanvasGrid);
            double vmx = (vpos.X - _panX) / _zoom;
            double vmy = WorkY - (vpos.Y - _panY) / _zoom;

            // Symbol-Klick: Constraint-Icon auswählen (in jedem Geom-Modus)
            {
                int symHit = HitTestGeomConstraintSymbol(vmx, vmy);
                if (symHit >= 0)
                {
                    _selectedGeomIdx = (_selectedGeomIdx == symHit) ? -1 : symHit;
                    DrawSkia?.InvalidateVisual();
                    e.Handled = true; return;
                }
                // Klick ins Leere → Selektion aufheben
                _selectedGeomIdx = -1;
            }

            // Geometrie-Constraint-Modus (Koinzident/Rechtwinklig/Parallel) aktiv:
            // eigener, einfacher Klick-Ablauf statt der normalen Vermassen-State-Machine.
            if (_geomMode != GeomConstraintMode.None)
            {
                HandleGeomModeClick(vmx, vmy);
                e.Handled = true;
                return;
            }

            // State 2/4: TextBox aktiv → fokussieren
            if (_vermState == 2 || _vermState == 4)
            {
                _vermTextBox?.Focus();
                e.Handled = true;
                return;
            }
            // State 3: Offset-Drag bestätigen
            if (_vermState == 3 && _vermEditIdx >= 0)
            {
                _vermPlaced[_vermEditIdx] = _vermPlaced[_vermEditIdx] with { Offset = _vermDragOffset };
                _vermState   = 0;
                _vermEditIdx = -1;
                DrawSkia?.InvalidateVisual();
                e.Handled = true;
                return;
            }
            // State 0/1: Klick
            if (_vermState == 0)
            {
                _vermRktIdx = -1;
                // 1. Klick auf Label einer platzierten Masslinie → Bearbeiten
                int labelHit = HitTestVermLabel(vpos.X, vpos.Y);
                if (labelHit >= 0)
                {
                    _vermEditIdx = labelHit;
                    _vermState   = 4;
                    ShowVermEditTextBox(labelHit);
                    e.Handled = true;
                    return;
                }
                // 2. Klick auf Masslinie → Offset ziehen
                int lineHit = HitTestVermLine(vmx, vmy);
                if (lineHit >= 0)
                {
                    _vermEditIdx    = lineHit;
                    _vermDragOffset = _vermPlaced[lineHit].Offset;
                    _vermMouseMm    = (vmx, vmy);
                    _vermState      = 3;
                    _vermHoverP1 = -1; _vermHoverP2 = -1;
                    DrawSkia?.InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                // 3a. Klick auf Pfad-Punkt → Punkt-Modus
                int ptHit0 = HitTestPfadPoint(vmx, vmy);
                if (ptHit0 >= 0)
                {
                    _vermPtIdx = ptHit0;
                    _vermP1Idx = -1; _vermP2Idx = -1;
                    _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverPoint = -1;
                    _vermState = 1;
                    DrawSkia?.InvalidateVisual();
                    e.Handled = true; return;
                }
                // 3b. Klick auf Pfad-Segment → neue Masslinie
                var hit = HitTestPfadLineSegment(vmx, vmy);
                if (hit.p1 >= 0)
                {
                    _vermP1Idx    = hit.p1;
                    _vermP2Idx    = hit.p2;
                    _vermP1Abs    = GetPfadAbsAt(_vermP1Idx) ?? (vmx, vmy);
                    _vermP2Abs    = GetPfadAbsAt(_vermP2Idx) ?? (vmx, vmy);
                    _vermMouseMm  = (vmx, vmy);
                    _vermDownMm   = (vmx, vmy);
                    _vermIsHolding = true;
                    _vermState    = 1;
                    _vermHoverP1  = -1; _vermHoverP2 = -1; _vermHoverPoint = -1;
                    CanvasGrid.CaptureMouse();
                    DrawSkia?.InvalidateVisual();
                }
                else if (HitTestRktVerm(vmx, vmy) is int rktHit && rktHit >= 0)
                {
                    // 3c. Klick auf Rechteck-Seite → Seitenlänge (Klick ins Freie) oder Kantenabstand (Klick auf parallele Kante)
                    _vermRktIdx = rktHit;
                    _vermKreisIdx = -1;
                    _vermP1Idx = -1; _vermP2Idx = -1; _vermPtIdx = -1; _vermActiveEdge = 0;
                    _vermIsHolding = false;
                    _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverPoint = -1; _vermHoverEdge = 0; _vermHoverKreis = -1; _vermHoverRkt = -1;
                    _vermState = 1;
                    DrawSkia?.InvalidateVisual();
                }
                else if (HitTestKreisVerm(vmx, vmy) is int krHit && krHit >= 0)
                {
                    // 3d. Klick auf Kreis → Durchmesser/Radius (Klick ins Freie) oder Kantenabstand (Klick auf Kante)
                    _vermKreisIdx = krHit;
                    _vermP1Idx = -1; _vermP2Idx = -1; _vermPtIdx = -1; _vermActiveEdge = 0;
                    _vermIsHolding = false;
                    _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverPoint = -1; _vermHoverEdge = 0; _vermHoverKreis = -1; _vermHoverRkt = -1;
                    _vermState = 1;
                    DrawSkia?.InvalidateVisual();
                }
                else
                {
                    int edgeHit = HitTestWorkpieceEdge(vmx, vmy);
                    if (edgeHit > 0)
                    {
                        _vermActiveEdge = edgeHit;
                        _vermP1Idx = -1; _vermP2Idx = -1;
                        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0;
                        _vermState = 1;
                        DrawSkia?.InvalidateVisual();
                    }
                }
            }
            else if (_vermState == 1)
            {
                // Kreis gewählt: Klick auf Kante → Kantenabstand, sonst Durchmesser/Radius platzieren
                if (_vermKreisIdx >= 0 && _vermActiveEdge == 0)
                {
                    int edgeHitK = HitTestWorkpieceEdge(vmx, vmy);
                    if (edgeHitK > 0) StartKreisKantenDist(_vermKreisIdx, edgeHitK);
                    else              PlaceKreisDimAt(vmx, vmy);
                    DrawSkia?.InvalidateVisual();
                    e.Handled = true; return;
                }
                // Rechteck-Seite gewählt: Klick auf parallele Kante → Kantenabstand, sonst Seitenlänge platzieren
                if (_vermRktIdx >= 0 && _vermActiveEdge == 0)
                {
                    int edgeHitR = HitTestWorkpieceEdge(vmx, vmy);
                    // Nicht parallele Kante → wie Klick ins Freie behandeln
                    if (edgeHitR == 0 || !StartRktKantenDist(_vermRktIdx, edgeHitR))
                        PlaceRktSeiteAt(vmx, vmy);
                    DrawSkia?.InvalidateVisual();
                    e.Handled = true; return;
                }
                // EdgeDist/EdgeAngle/PointEdgeDist: Kante bereits gewählt, jetzt Segment oder Punkt wählen
                if (_vermActiveEdge > 0 && _vermP1Idx < 0)
                {
                    // Punkt geklickt → PointEdgeDist
                    int ptHitE = HitTestPfadPoint(vmx, vmy);
                    if (ptHitE >= 0)
                    {
                        _vermP1Idx = -1; _vermP2Idx = ptHitE;
                        _vermP2Abs = GetPfadAbsAt(_vermP2Idx) ?? (vmx, vmy);
                        _vermActiveKind = VermKind.PointEdgeDist;
                        _vermOffset = 0; _vermPtIdx = -1;
                        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0; _vermHoverPoint = -1;
                        _vermState = 5;
                        DrawSkia?.InvalidateVisual();
                        e.Handled = true; return;
                    }
                    // Segment geklickt → EdgeDist oder EdgeAngle
                    var segHit = HitTestPfadLineSegment(vmx, vmy);
                    if (segHit.p1 >= 0)
                    {
                        _vermP1Idx = segHit.p1; _vermP2Idx = segHit.p2;
                        _vermP1Abs = GetPfadAbsAt(_vermP1Idx) ?? (vmx, vmy);
                        _vermP2Abs = GetPfadAbsAt(_vermP2Idx) ?? (vmx, vmy);
                        _vermActiveKind = IsSegmentParallelToEdge(_vermP1Abs, _vermP2Abs, _vermActiveEdge)
                            ? VermKind.EdgeDist : VermKind.EdgeAngle;
                        _vermOffset = 0;
                        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0;
                        _vermState = 5;
                        DrawSkia?.InvalidateVisual();
                        e.Handled = true; return;
                    }
                    // Kreis geklickt → Abstand Kreismitte → Kante
                    int krHitE = HitTestKreisVerm(vmx, vmy);
                    if (krHitE >= 0)
                    {
                        StartKreisKantenDist(krHitE, _vermActiveEdge);
                        DrawSkia?.InvalidateVisual();
                        e.Handled = true; return;
                    }
                    // Rechteck-Seite geklickt → Abstand Seite → Kante (nur parallele Seiten)
                    int rktHitE = HitTestRktVerm(vmx, vmy);
                    if (rktHitE >= 0 && StartRktKantenDist(rktHitE, _vermActiveEdge))
                    {
                        DrawSkia?.InvalidateVisual();
                        e.Handled = true; return;
                    }
                }
                // Punkt bereits gewählt, jetzt Kante wählen → PointEdgeDist
                if (_vermPtIdx >= 0)
                {
                    int edgeHitPt = HitTestWorkpieceEdge(vmx, vmy);
                    if (edgeHitPt > 0)
                    {
                        _vermActiveEdge = edgeHitPt;
                        _vermP1Idx = -1; _vermP2Idx = _vermPtIdx;
                        _vermP2Abs = GetPfadAbsAt(_vermP2Idx) ?? (vmx, vmy);
                        _vermActiveKind = VermKind.PointEdgeDist;
                        _vermOffset = 0; _vermPtIdx = -1;
                        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0; _vermHoverPoint = -1;
                        _vermState = 5;
                        DrawSkia?.InvalidateVisual();
                        e.Handled = true; return;
                    }
                }
                // Segment bereits gewählt, jetzt Kante wählen → Vorschau (State 5)
                if (_vermP1Idx >= 0)
                {
                    int edgeHit1 = HitTestWorkpieceEdge(vmx, vmy);
                    if (edgeHit1 > 0)
                    {
                        _vermActiveEdge = edgeHit1;
                        _vermActiveKind = IsSegmentParallelToEdge(_vermP1Abs, _vermP2Abs, edgeHit1)
                            ? VermKind.EdgeDist : VermKind.EdgeAngle;
                        _vermOffset     = 0;
                        _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverEdge = 0;
                        _vermState = 5;
                        DrawSkia?.InvalidateVisual();
                        e.Handled = true; return;
                    }
                }
                // Zweiter Klick auf Punkt → PointDist (Pt→Pt) oder LineToPoint (Seg→Pt)
                int ptHit1 = HitTestPfadPoint(vmx, vmy);
                if (ptHit1 >= 0 && ptHit1 != _vermPtIdx)
                {
                    if (_vermPtIdx >= 0)
                    {
                        // Punkt→Punkt
                        _vermP1Idx = _vermPtIdx; _vermP2Idx = ptHit1;
                        _vermP1Abs = GetPfadAbsAt(_vermP1Idx) ?? (vmx, vmy);
                        _vermP2Abs = GetPfadAbsAt(_vermP2Idx) ?? (vmx, vmy);
                        _vermActiveKind = VermKind.PointDist;
                    }
                    else if (_vermP1Idx >= 0)
                    {
                        // Linie→Punkt
                        _vermQ1Idx = ptHit1; _vermQ2Idx = ptHit1;
                        _vermQ1Abs = GetPfadAbsAt(ptHit1) ?? (vmx, vmy);
                        _vermQ2Abs = _vermQ1Abs;
                        _vermActiveKind = VermKind.LineToPoint;
                    }
                    else { e.Handled = true; return; }
                    _vermOffset = 0; _vermPtIdx = -1;
                    _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverPoint = -1;
                    _vermState = 5;
                    DrawSkia?.InvalidateVisual();
                    e.Handled = true; return;
                }
                // Prüfen ob ein zweites (anderes) Segment angeklickt wurde
                var hit2 = HitTestPfadLineSegment(vmx, vmy);
                if (hit2.p1 >= 0 && (hit2.p1 != _vermP1Idx || hit2.p2 != _vermP2Idx))
                {
                    // Zweites Segment gewählt → ParallelDist oder Angle
                    _vermQ1Idx = hit2.p1;
                    _vermQ2Idx = hit2.p2;
                    _vermQ1Abs = GetPfadAbsAt(_vermQ1Idx) ?? (vmx, vmy);
                    _vermQ2Abs = GetPfadAbsAt(_vermQ2Idx) ?? (vmx, vmy);
                    bool isParallel = AreSegmentsParallel(_vermP1Abs, _vermP2Abs, _vermQ1Abs, _vermQ2Abs, tolDeg: 0.1);
                    _vermActiveKind = isParallel ? VermKind.ParallelDist : VermKind.Angle;
                    _vermState = 5; _vermPtIdx = -1;
                    _vermHoverP1 = -1; _vermHoverP2 = -1;
                }
                else if (_vermP1Idx >= 0)
                {
                    PlaceVermassungAt(vmx, vmy);
                }
            }
            else if (_vermState == 5)
            {
                PlaceTwoSegmentVermAt(vmx, vmy);
            }
            e.Handled = true;
            return;
        }

        // Pan starten: Rechtsklick immer, Mitteltaste immer, Linksklick beim Hand-Werkzeug oder mit Ctrl
        bool startPan = e.ChangedButton == MouseButton.Right
                        || e.ChangedButton == MouseButton.Middle
                        || (e.ChangedButton == MouseButton.Left && _activeTool == CanvasTool.Hand)
                        || (e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        if (!startPan) return;
        _isPanning = true;
        _panStart  = e.GetPosition(CanvasGrid);
        _panOrigin = new Point(_panX, _panY);
        CanvasGrid.CaptureMouse();
        CanvasGrid.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Pan beendet (Rechtsklick, Mitteltaste, oder Ctrl+Linksklick) - ZUERST überprüfen!
        if (_isPanning && (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left))
        {
            _isPanning = false;
            CanvasGrid.ReleaseMouseCapture();
            CanvasGrid.Cursor = _activeTool switch
            {
                CanvasTool.Hand         => Cursors.Hand,
                CanvasTool.Zoom         => Cursors.Cross,
                CanvasTool.VCarveTextSk => Cursors.Cross,
                _                       => Cursors.Arrow,
            };
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            // Template-Image: Ankerpunkt-Drag beendet
            if (_templateImageDragAnchor >= 0 && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                _templateImageDragAnchor = -1;
                DrawSkia?.InvalidateVisual();
                e.Handled = true;
                return;
            }

            // VCarveText/Sk + Ctrl-Resize: Drag beendet → Resize committen, Editor wieder öffnen
            if ((_activeTool == CanvasTool.VCarveTextSk)
                && _ctrlResizeReopen >= 0 && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                int reopenIdx = _ctrlResizeReopen;
                _ctrlResizeReopen = -1;
                CommitMoveTextField();                  // Neue Grösse in History schreiben
                var savedTool = _activeTool;
                SetActiveTool(savedTool);               // Tool-Highlight sichern
                StartEditExistingTextField(reopenIdx);  // Editor mit neuer Grösse wieder öffnen
                e.Handled = true;
                return;
            }

            // Reihenlochbohrung: Anker-/Pfeil-Drag beendet
            if (_activeTool == CanvasTool.Reihenloch && _rlbDrag != 0)
            {
                if (CanvasGrid.IsMouseCaptured) CanvasGrid.ReleaseMouseCapture();
                CommitRlbDrag();
                e.Handled = true;
                return;
            }

            // Pfeil-Werkzeug: Segment-Mittelpunkt-Drag beendet
            if (_activeTool == CanvasTool.Pfeil && _pfadSegDragIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.Arrow;
                _pfadSegDragIdx = -1;
                PropagateVermConstraints();
                CheckAndReportConstraints();
                DrawSkia?.InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Pfeil-Werkzeug: Pfad-Punkt-Drag beendet
            if (_activeTool == CanvasTool.Pfeil && _pfadDragHistIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.Arrow;
                _pfadDragHistIdx = -1;
                PropagateVermConstraints();
                CheckAndReportConstraints();
                DrawSkia?.InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Move-Werkzeug: Pfad skalieren beendet
            if (_activeTool == CanvasTool.Move && _pfadScaleChainIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.Arrow;
                CommitScalePfadChain();
                e.Handled = true;
                return;
            }

            // Move-Werkzeug: ganzen Pfad-Drag beendet
            if (_activeTool == CanvasTool.Move && _pfadChainDragIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.Arrow;
                CommitMovePfadChain();
                e.Handled = true;
                return;
            }

            // Move-Werkzeug: Kreis-Drag beendet
            if (_activeTool == CanvasTool.Move && CanvasGrid.IsMouseCaptured
                && _moveHistoryIdx >= 0 && _moveHistoryIdx < _history.Count
                && _history[_moveHistoryIdx].Params is KreisParams)
            {
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.Arrow;
                CommitMoveKreis();
                e.Handled = true;
                return;
            }

            // Move-Werkzeug: Textfeld-Drag beendet
            if (_activeTool == CanvasTool.Move && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.SizeAll;
                CommitMoveTextField();
                e.Handled = true;
                return;
            }

            // Vermassen State 1: Drag-Positionierung abschließen oder in Warte-Modus wechseln
            if (_activeTool == CanvasTool.Vermassen && _vermState == 1 && _vermIsHolding)
            {
                CanvasGrid.ReleaseMouseCapture();
                _vermIsHolding = false;
                var vup = e.GetPosition(CanvasGrid);
                double vux = (vup.X - _panX) / _zoom;
                double vuy = WorkY - (vup.Y - _panY) / _zoom;
                double dx = vux - _vermDownMm.x, dy = vuy - _vermDownMm.y;
                bool hasMoved = (dx*dx + dy*dy) > 4.0; // > 2 mm Schwellwert
                if (hasMoved)
                {
                    // Maustaste nach Positionierung losgelassen → Masslinie platzieren
                    _vermMouseMm = (vux, vuy);
                    PlaceVermassungAt(vux, vuy);
                }
                // else: schneller Klick → Warte-Modus (state 1 bleibt, _vermIsHolding = false)
                DrawSkia?.InvalidateVisual();
                e.Handled = true;
                return;
            }

            // VCarveText/Sk-Werkzeug: Mouse-Capture freigeben (z.B. nach Ctrl-Resize)
            if ((_activeTool == CanvasTool.VCarveTextSk) && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }


            // Zoom-Werkzeug: Drag beendet oder Klick
            if (_activeTool == CanvasTool.Zoom && CanvasGrid.IsMouseCaptured)
            {
                CanvasGrid.ReleaseMouseCapture();
                if (_isZoomDragging)
                {
                    ClearZoomRubberBand();
                    ZoomToRect(_zoomDragStart, e.GetPosition(CanvasGrid));
                }
                else
                {
                    // Kurzer Klick → 2× hineinzoomen auf Klickposition
                    double newZoom = Math.Clamp(_zoom * 2.0, 0.05, 200.0);
                    var screenPt = e.GetPosition(CanvasGrid);
                    double wx = (screenPt.X - _panX) / _zoom, wy = (screenPt.Y - _panY) / _zoom;
                    _panX += wx * (_zoom - newZoom);
                    _panY += wy * (_zoom - newZoom);
                    _zoom  = newZoom;
                    UpdateAll();
                }
                _isZoomDragging = false;
                e.Handled = true;
                return;
            }

            if (_isPanning && _activeTool == CanvasTool.Hand)
            {
                _isPanning = false;
                CanvasGrid.ReleaseMouseCapture();
                CanvasGrid.Cursor = Cursors.Hand;
                return;
            }
            // Linksklick auf leere Fläche → Auswahl aufheben
            if (e.ClickCount == 1 && _selectedGCodeLine >= 0 && !_isPanning && e.OriginalSource == CanvasGrid)
            {
                SetSelectedGCodeLine(-1);
                UpdateAll();
            }
            return;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        // Template-Image: Ankerpunkt verschieben
        if (_templateImageDragAnchor >= 0 && CanvasGrid.IsMouseCaptured && _templateImage != null)
        {
            var screenPt = e.GetPosition(CanvasGrid);
            double mmX = (screenPt.X - _panX) / _zoom;
            double mmY = (screenPt.Y - _panY) / _zoom;

            double sc = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
            double canvasMmX = (mmX - _topRect.Left) / sc;
            double canvasMmY = (_topRect.Bottom - mmY) / sc;

            _templateImage.ResizeFromAnchor(_templateImageDragAnchor, canvasMmX, canvasMmY);
            DrawSkia?.InvalidateVisual();
            return;
        }

        // Pfad-Werkzeuge: Mausposition für Vorschau-Fadenkreuz tracken
        if (_activeTool is CanvasTool.PfadStart or CanvasTool.PfadLinie or CanvasTool.PfadBogen or CanvasTool.PfadSpline
            && !_isPanning && !CanvasGrid.IsMouseCaptured)
        {
            var pos = e.GetPosition(CanvasGrid);
            double rawX = (pos.X - _panX) / _zoom;
            double rawY = WorkY - (pos.Y - _panY) / _zoom;
            // Bogen-Mittelpunkt nicht am Raster fangen
            bool noSnap = _activeTool == CanvasTool.PfadBogen && _pfadBogenWaiting;
            _pfadMouseMm    = noSnap ? (rawX, rawY) : (SnapX(rawX), SnapY(rawY));
            _pfadMouseValid = true;
            DrawSkia?.InvalidateVisual();
            return;
        }

        // Reihenlochbohrung-Werkzeug: Anker/Pfeile ziehen, Hover-Cursor
        if (_activeTool == CanvasTool.Reihenloch && !_isPanning)
        {
            var pos = e.GetPosition(CanvasGrid);
            RlbMouseMove((pos.X - _panX) / _zoom, WorkY - (pos.Y - _panY) / _zoom);
            return;
        }

        // Pfeil-Werkzeug: Pfad-Punkte und Segment-Mittelpunkte ziehen
        if (_activeTool == CanvasTool.Pfeil && !_isPanning)
        {
            var pos = e.GetPosition(CanvasGrid);
            double mmX = (pos.X - _panX) / _zoom;
            double mmY = WorkY - (pos.Y - _panY) / _zoom;

            if (_pfadSegDragIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                if (_pfadSegDragIsArc) UpdateBogenPfeilhoehe(_pfadSegDragIdx, mmX, mmY);
                else                  MovePfadLinienSegment(_pfadSegDragIdx, mmX, mmY);
                PropagateVermConstraintsLive();
                DrawSkia?.InvalidateVisual();
                return;
            }
            if (_pfadDragHistIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                UpdatePfadPunktPos(_pfadDragHistIdx, SnapX(mmX), SnapY(mmY));
                PropagateVermConstraintsLive();
                DrawSkia?.InvalidateVisual();
                return;
            }
            // Hover
            CanvasGrid.Cursor = (HitTestPfadPunkt(mmX, mmY) >= 0 || HitTestPfadSegMid(mmX, mmY) >= 0)
                ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }

        // Move-Werkzeug: ganzen Pfad / Objekte ziehen oder Hover-Cursor
        if (_activeTool == CanvasTool.Move && !_isPanning)
        {
            var pos = e.GetPosition(CanvasGrid);
            double mmX = (pos.X - _panX) / _zoom;
            double mmY = WorkY - (pos.Y - _panY) / _zoom;

            // Kette wird skaliert
            if (_pfadScaleChainIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                UpdateScalePfadChain(mmX, mmY);
                return;
            }

            // Ganze Kette wird gezogen
            if (_pfadChainDragIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                UpdateMovePfadChain(mmX, mmY);
                return;
            }

            if (_moveHistoryIdx >= 0 && CanvasGrid.IsMouseCaptured)
            {
                if (_moveHistoryIdx < _history.Count && _history[_moveHistoryIdx].Params is KreisParams)
                    UpdateMoveKreis(mmX, mmY);
                else
                    UpdateMoveTextField(mmX, mmY);
            }
            else
            {
                // Hover
                Cursor cur = Cursors.Arrow;
                var (hoverChain, hoverAnchor) = HitTestPfadChainAnchor(mmX, mmY);
                if (hoverChain >= 0)
                    cur = ScaleAnchorCursor(hoverAnchor);
                else if (HitTestPfadChainBBox(mmX, mmY) >= 0)
                    cur = Cursors.SizeAll;
                else if (HistoryList.SelectedItem is HistoryEntry hov
                    && hov.Params is GraviereParams hovGp)
                {
                    int hc = HitTestMoveCorner(mmX, mmY, hovGp);
                    if (hc >= 0)
                        cur = CornerCursor(hc);
                    else if (HitTestTextField(mmX, mmY) >= 0)
                        cur = Cursors.SizeAll;
                }
                else if (HistoryList.SelectedItem is HistoryEntry hovRktEntry
                    && hovRktEntry.Params is RechteckParams hovRp)
                {
                    int hc = HitTestRktCorner(mmX, mmY, hovRp);
                    if (hc >= 0)
                        cur = CornerCursor(hc);
                    else if (HitTestTextField(mmX, mmY) >= 0 || HitTestRechteck(mmX, mmY) >= 0)
                        cur = Cursors.SizeAll;
                }
                else if (HitTestTextField(mmX, mmY) >= 0 || HitTestRechteck(mmX, mmY) >= 0
                         || HitTestKreis(mmX, mmY) >= 0)
                    cur = Cursors.SizeAll;
                CanvasGrid.Cursor = cur;
            }
            return;
        }

        // VCarveText/Sk + Ctrl-Resize: Drag läuft
        if ((_activeTool == CanvasTool.VCarveTextSk)
            && _ctrlResizeReopen >= 0
            && _moveHistoryIdx >= 0 && CanvasGrid.IsMouseCaptured && !_isPanning)
        {
            var pos  = e.GetPosition(CanvasGrid);
            double mmX = (pos.X - _panX) / _zoom;
            double mmY = WorkY - (pos.Y - _panY) / _zoom;
            UpdateResizeTextField(mmX, mmY);
            return;
        }

        // VCarveText/Sk-Werkzeug: Gummiband nach erstem Klick — Fadenkreuz + eingerastetes Gummiband
        if ((_activeTool == CanvasTool.VCarveTextSk) && _isTextDragging && !_isPanning)
        {
            var pos2   = e.GetPosition(CanvasGrid);
            double mx2 = SnapX((pos2.X - _panX) / _zoom);
            double my2 = SnapY(WorkY - (pos2.Y - _panY) / _zoom);
            _pfadMouseMm    = (mx2, my2);
            _pfadMouseValid = true;
            var snapped2 = new Point(mx2 * _zoom + _panX, (WorkY - my2) * _zoom + _panY);
            UpdateTextRubberBand(_textDragStart, snapped2);
            DrawSkia?.InvalidateVisual();
            return;
        }

        // Zoom-Werkzeug: Gummiband-Rechteck aufziehen
        if (_activeTool == CanvasTool.Zoom && CanvasGrid.IsMouseCaptured && !_isPanning)
        {
            var pos   = e.GetPosition(CanvasGrid);
            var delta = pos - _zoomDragStart;
            if (!_isZoomDragging && (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4))
                _isZoomDragging = true;
            if (_isZoomDragging)
                UpdateZoomRubberBand(_zoomDragStart, pos);
            return;
        }

        // Rechteck-Werkzeug: Fadenkreuz + gerastertes Gummiband
        if (_activeTool == CanvasTool.Rechteck && !_isPanning)
        {
            var rpos   = e.GetPosition(CanvasGrid);
            double rmx = SnapX((rpos.X - _panX) / _zoom);
            double rmy = SnapY(WorkY - (rpos.Y - _panY) / _zoom);
            _pfadMouseMm    = (rmx, rmy);
            _pfadMouseValid = true;
            if (_rktDragging)
            {
                var snappedR = new Point(rmx * _zoom + _panX, (WorkY - rmy) * _zoom + _panY);
                UpdateRktRubberBand(_rktDragStart, snappedR);
            }
            DrawSkia?.InvalidateVisual();
            return;
        }
        if (_activeTool == CanvasTool.Kreis && !_isPanning)
        {
            var kpos   = e.GetPosition(CanvasGrid);
            double kmx = SnapX((kpos.X - _panX) / _zoom);
            double kmy = SnapY(WorkY - (kpos.Y - _panY) / _zoom);
            _pfadMouseMm    = (kmx, kmy);
            _pfadMouseValid = true;
            if (_kreisDragging)
            {
                double cx2 = (_kreisDragCenter.X - _panX) / _zoom;
                double cy2 = WorkY - (_kreisDragCenter.Y - _panY) / _zoom;
                double dx = kmx - cx2, dy = kmy - cy2;
                double rPx = Math.Sqrt(dx*dx + dy*dy) * _zoom;
                UpdateKreisRubberBand(_kreisDragCenter, rPx);
            }
            DrawSkia?.InvalidateVisual();
            return;
        }
        if (_activeTool == CanvasTool.NEck && !_isPanning)
        {
            var npos   = e.GetPosition(CanvasGrid);
            double nmx = SnapX((npos.X - _panX) / _zoom);
            double nmy = SnapY(WorkY - (npos.Y - _panY) / _zoom);
            _pfadMouseMm    = (nmx, nmy);
            _pfadMouseValid = true;
            if (_neckDragging)
            {
                double ncx2 = (_neckDragCenter.X - _panX) / _zoom;
                double ncy2 = WorkY - (_neckDragCenter.Y - _panY) / _zoom;
                double ndx = nmx - ncx2, ndy = nmy - ncy2;
                double nrPx = Math.Sqrt(ndx*ndx + ndy*ndy) * _zoom;
                UpdateNEckRubberBand(_neckDragCenter, nrPx, 6);
            }
            DrawSkia?.InvalidateVisual();
            return;
        }

        // Vermassen-Werkzeug: Hover (0) + Offset-Vorschau (1) + Drag (3)
        if (_activeTool == CanvasTool.Vermassen && !_isPanning)
        {
            var vmp = e.GetPosition(CanvasGrid);
            double vmx = (vmp.X - _panX) / _zoom;
            double vmy = WorkY - (vmp.Y - _panY) / _zoom;
            _vermMouseMm = (vmx, vmy);
            if (_vermState == 0)
            {
                // Reihenfolge wie im Klick-Handler: Punkt → Segment → Rechteck-Seite → Kreis → Kante
                int ptHit  = HitTestPfadPoint(vmx, vmy);
                var hit    = ptHit < 0 ? HitTestPfadLineSegment(vmx, vmy) : (-1, -1);
                int rktHit = hit.Item1 < 0 && ptHit < 0 ? HitTestRktVerm(vmx, vmy) : -1;
                int krHit  = hit.Item1 < 0 && ptHit < 0 && rktHit < 0 ? HitTestKreisVerm(vmx, vmy) : -1;
                int edgeHit = hit.Item1 < 0 && ptHit < 0 && rktHit < 0 && krHit < 0 ? HitTestWorkpieceEdge(vmx, vmy) : 0;
                if (ptHit != _vermHoverPoint || hit.Item1 != _vermHoverP1 ||
                    hit.Item2 != _vermHoverP2 || edgeHit != _vermHoverEdge || krHit != _vermHoverKreis
                    || rktHit != _vermHoverRkt)
                {
                    _vermHoverPoint = ptHit;
                    _vermHoverP1 = hit.Item1; _vermHoverP2 = hit.Item2; _vermHoverEdge = edgeHit;
                    _vermHoverKreis = krHit; _vermHoverRkt = rktHit;
                    DrawSkia?.InvalidateVisual();
                }
            }
            else if (_vermState == 1 || _vermState == 5)
            {
                // Im Warte-Modus (state 1, nicht haltend): Hover für 2. Auswahl aktualisieren
                if (_vermState == 1 && (_vermKreisIdx >= 0 || _vermRktIdx >= 0))
                {
                    // Kreis/Rechteck-Seite gewählt: nur Werkstückkanten als 2. Auswahl hervorheben
                    _vermHoverPoint = -1; _vermHoverP1 = -1; _vermHoverP2 = -1; _vermHoverKreis = -1; _vermHoverRkt = -1;
                    _vermHoverEdge = HitTestWorkpieceEdge(vmx, vmy);
                    // Rechteck-Seite: nur parallele Kanten sind bemassbar
                    if (_vermRktIdx >= 0 && _vermHoverEdge > 0
                        && !RktSeiteParallelZuKante(DecodeKreisVermIdx(_vermRktIdx).anker, _vermHoverEdge))
                        _vermHoverEdge = 0;
                }
                else if (_vermState == 1 && !_vermIsHolding)
                {
                    _vermHoverPoint = HitTestPfadPoint(vmx, vmy);
                    var hit = _vermHoverPoint < 0 ? HitTestPfadLineSegment(vmx, vmy) : (-1, -1);
                    _vermHoverP1 = hit.Item1; _vermHoverP2 = hit.Item2;
                    // Kante bereits gewählt → Kreise und (parallele) Rechteck-Seiten als 2. Auswahl hervorheben
                    _vermHoverKreis = _vermActiveEdge > 0 && hit.Item1 < 0 && _vermHoverPoint < 0
                        ? HitTestKreisVerm(vmx, vmy) : -1;
                    _vermHoverRkt = -1;
                    if (_vermActiveEdge > 0 && hit.Item1 < 0 && _vermHoverPoint < 0 && _vermHoverKreis < 0)
                    {
                        int rh = HitTestRktVerm(vmx, vmy);
                        if (rh >= 0 && RktSeiteParallelZuKante(DecodeKreisVermIdx(rh).anker, _vermActiveEdge))
                            _vermHoverRkt = rh;
                    }
                    if (_vermActiveEdge == 0)
                        _vermHoverEdge = hit.Item1 < 0 && _vermHoverPoint < 0
                            ? HitTestWorkpieceEdge(vmx, vmy) : 0;
                }
                DrawSkia?.InvalidateVisual();
            }
            else if (_vermState == 3 && _vermEditIdx >= 0)
            {
                // Offset-Drag: Vorschau aktualisieren (alle Arten)
                _vermDragOffset = VermComputeNewOffset(vmx, vmy, _vermPlaced[_vermEditIdx]);
                DrawSkia?.InvalidateVisual();
            }
            return;
        }

        // Hover-Cursor im VCarveText/Sk-Modus: IBeam über Textfeldern, Corner-Cursor bei Ctrl
        if (!_isPanning && !CanvasGrid.IsMouseCaptured
            && (_activeTool == CanvasTool.VCarveTextSk))
        {
            var hPos  = e.GetPosition(CanvasGrid);
            double hx = (hPos.X - _panX) / _zoom;
            double hy = WorkY - (hPos.Y - _panY) / _zoom;
            _pfadMouseMm    = (SnapX(hx), SnapY(hy));
            _pfadMouseValid = _inlineTextBox == null;
            DrawSkia?.InvalidateVisual();
            if (_ctrlResizeMode && _inlineTextBox != null && _inlineExistingIdx >= 0
                && _inlineExistingIdx < _history.Count
                && _history[_inlineExistingIdx].Params is GraviereParams ctrlHovGp)
            {
                int hc = HitTestMoveCorner(hx, hy, ctrlHovGp);
                CanvasGrid.Cursor = hc >= 0 ? CornerCursor(hc) : Cursors.IBeam;
            }
            else if (_inlineTextBox == null)
                CanvasGrid.Cursor = HitTestTextField(hx, hy) >= 0 ? Cursors.IBeam : Cursors.Cross;
        }

        if (!_isPanning) return;
        var panPos = e.GetPosition(CanvasGrid);
        _panX = _panOrigin.X + (panPos.X - _panStart.X);
        _panY = _panOrigin.Y + (panPos.Y - _panStart.Y);
        ApplyCanvasTransform();
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (_rlbHoverMm != null)
        {
            _rlbHoverMm = null;
            DrawSkia?.InvalidateVisual();
        }
        if (_pfadMouseValid)
        {
            _pfadMouseValid = false;
            DrawSkia?.InvalidateVisual();
        }
        if (_activeTool == CanvasTool.Move && _moveHistoryIdx >= 0 && CanvasGrid.IsMouseCaptured)
        {
            CanvasGrid.ReleaseMouseCapture();
            CommitMoveTextField();
            return;
        }
        if (_isTextDragging)
        {
            _isTextDragging = false;
            ClearTextRubberBand();
            CanvasGrid.ReleaseMouseCapture();
            return;
        }
        if (_isZoomDragging)
        {
            _isZoomDragging = false;
            ClearZoomRubberBand();
            CanvasGrid.ReleaseMouseCapture();
            return;
        }
        if (!_isPanning) return;
        _isPanning = false;
        CanvasGrid.ReleaseMouseCapture();
        CanvasGrid.Cursor = _activeTool switch
        {
            CanvasTool.Hand         => Cursors.Hand,
            CanvasTool.Zoom         => Cursors.Cross,
            CanvasTool.VCarveTextSk => Cursors.Cross,
            _                       => Cursors.Arrow,
        };
    }

    private void OnInfo(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title           = "Info",
            SizeToContent   = SizeToContent.WidthAndHeight,
            ResizeMode      = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner           = this,
            Background      = new System.Windows.Media.SolidColorBrush(
                                  (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                                  .ConvertFromString("#d8e0ea")),
        };

        // Grössten Frame aus der ICO-Datei laden
        var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
            new Uri("pack://application:,,,/icon.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.Default);
        var bestFrame = decoder.Frames
            .OrderByDescending(f => f.PixelWidth)
            .First();

        var img = new System.Windows.Controls.Image
        {
            Width   = 128,
            Height  = 128,
            Margin  = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Source  = bestFrame,
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            img, System.Windows.Media.BitmapScalingMode.HighQuality);

        var txt = new System.Windows.Controls.TextBlock
        {
            Text            = "NC Studio – G-Code Generator & Visualisierer\n\n" +
                              "NC Studio ist ein CNC-Hilfsprogramm für die Holzbearbeitung und das Fräsen. " +
                              "Es unterstützt bei der Erstellung und Visualisierung von G-Code für CNC-Maschinen.\n\n" +
                              "G-Code ist eine weit verbreitete Programmiersprache zur Steuerung von CNC-Maschinen. " +
                              "Er beschreibt Bewegungen, Geschwindigkeiten und Werkzeugbefehle in einer Abfolge von Befehlen – " +
                              "zum Beispiel bestimmt G0 eine schnelle Leerfahrt und G1 eine gefräste Linie mit definiertem Vorschub.\n\n" +
                              "\"NC\" steht für Numerical Control (Numerische Steuerung), " +
                              "die technologische Grundlage moderner CNC-Maschinen-Steuerung.\n\n" +
                              "Entwickler: Joel Suter",
            TextAlignment   = TextAlignment.Center,
            Foreground      = new System.Windows.Media.SolidColorBrush(
                                  (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                                  .ConvertFromString("#1a1a1a")),
            FontSize        = 13,
            Margin          = new Thickness(0, 0, 0, 16),
            MaxWidth        = 480,
            TextWrapping    = System.Windows.TextWrapping.Wrap,
        };

        var btn = new System.Windows.Controls.Button
        {
            Content             = "OK",
            Width               = 80,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background          = new System.Windows.Media.SolidColorBrush(
                                      (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                                      .ConvertFromString("#DDD0B0")),
            Foreground          = System.Windows.Media.Brushes.White,
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(8, 4, 8, 4),
        };
        btn.Click += (_, __) => dlg.Close();

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(32, 24, 32, 24) };
        panel.Children.Add(img);
        panel.Children.Add(txt);
        panel.Children.Add(btn);
        dlg.Content = panel;
        dlg.ShowDialog();
    }

    // ── Aktualisieren ─────────────────────────────────────────────

    private void OnAktualisieren(object sender, RoutedEventArgs e) => UpdateAll();

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAll();

    /// <summary>
    /// Gibt _dpiScale zurück und aktualisiert Textfeld-Schriftgröße wenn sich DPI ändert
    /// </summary>
    private double GetDpiScale()
    {
        // Wenn _dpiScale bereits berechnet, verwende es
        if (_dpiScale > 0)
            return _dpiScale;

        // Versuche die Screen-DPI zu lesen als Fallback
        try
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            double dpiX = graphics.DpiX / 96.0;  // 96 DPI = Standard
            graphics.Dispose();
            return dpiX > 1.0 ? dpiX : 1.0;
        }
        catch
        {
            return 1.0;  // Fallback bei Fehler
        }
    }

    private bool _needsInitialFontSizeUpdate = false;  // Flag: Textfeld braucht Schriftgröße-Korrektur nach DPI-Berechnung

    private bool _gcodeBoxDirty = false;

    private string GCodeText
    {
        get => _gcodeContent;
        set
        {
            if (_gcodeContent == value) return;
            _gcodeContent    = value;
            _parsedGCodeText = null;
            _gcodeBoxDirty   = true;
            _simPathDirty    = true;
            if (!_suppressNextAutoFit)
                _needsAutoFit = true;
            _suppressNextAutoFit = false;

            // TextBox nur aktualisieren wenn G-Code Tab aktiv ist
            if (IsGCodeTabActive())
                FlushGCodeBox();
        }
    }

    private bool IsGCodeTabActive() =>
        TabGCode.Visibility == Visibility.Visible &&
        TabGCode.IsSelected;


    private void FlushGCodeBox()
    {
        if (!_gcodeBoxDirty) return;
        _gcodeBoxDirty = false;

        _suppressGCodeUiUpdate = true;
        try { GCodeBox.Text = _gcodeContent; }
        finally { _suppressGCodeUiUpdate = false; }
    }

    private void OnGCodeTextChanged(object? sender, EventArgs e)
    {
        if (_suppressGCodeUiUpdate) return;
        _gcodeContent    = GCodeBox.Text;
        _parsedGCodeText = null;
        _gcodeBoxDirty   = false;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void EnsureParsed()
    {
        if (_gcodeContent == _parsedGCodeText) return;
        _parsedGCodeText   = _gcodeContent;
        _cachedTopMoves    = GCodeParser.ParseTopView(_gcodeContent);
        _cachedSideMoves   = GCodeParser.ParseSideView(_gcodeContent);
        _cachedDrillPoints = GCodeParser.ParseDrillPoints(_gcodeContent);
    }

    private void OnGCodeBoxLoaded(object sender, RoutedEventArgs e)
    {
        // Guard: Loaded feuert beim Tab-Wechsel erneut — keine Doppel-Registrierung
        if (GCodeBox.TextArea.TextView.LineTransformers.OfType<GCodeColorizer>().Any()) return;

        GCodeBox.TextChanged += OnGCodeTextChanged;
        GCodeBox.TextArea.TextView.LineTransformers.Add(new GCodeColorizer());
        _gcodeBgRenderer = new GCodeLineBackgroundRenderer();
        GCodeBox.TextArea.TextView.BackgroundRenderers.Add(_gcodeBgRenderer);
        GCodeBox.TextArea.TextView.MouseHover        += OnGCodeMouseHover;
        GCodeBox.TextArea.TextView.MouseHoverStopped += OnGCodeMouseHoverStopped;
        GCodeBox.TextArea.LeftMargins.Add(new GCodeArrowMargin());
        GCodeBox.TextArea.Caret.PositionChanged      += OnGCodeCaretMoved;
        GCodeBox.TextArea.TextView.MouseMove              += OnGCodeEditorMouseMove;
        GCodeBox.TextArea.TextView.MouseLeave             += OnGCodeEditorMouseLeave;
        GCodeBox.TextArea.TextView.MouseLeftButtonDown    += OnGCodeEditorClick;
        GCodeBox.TextArea.PreviewKeyDown                  += OnGCodeEditorKeyDown;
    }

    private ToolTip? _gCodeToolTip;

    private void OnGCodeMouseHover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var tv  = GCodeBox.TextArea.TextView;
        var pos = tv.GetPositionFloor(e.GetPosition(tv) + tv.ScrollOffset);
        if (pos is null) return;

        int offset  = GCodeBox.Document.GetOffset(pos.Value.Location);
        var docLine = GCodeBox.Document.GetLineByOffset(offset);
        var lineText = GCodeBox.Document.GetText(docLine);
        int col = offset - docLine.Offset;

        var (tipTitle, tipDesc) = GCodeTooltip.GetTooltip(lineText, col);
        if (tipDesc is null) return;

        if (_gCodeToolTip is null)
        {
            _gCodeToolTip = new ToolTip
            {
                Placement       = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow   = false,
                Padding         = new Thickness(0)
            };
        }
        _gCodeToolTip.Content = BuildTooltipBubble(tipTitle, tipDesc);
        _gCodeToolTip.IsOpen  = true;
        e.Handled = true;
    }

    private void OnGCodeMouseHoverStopped(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_gCodeToolTip is not null) _gCodeToolTip.IsOpen = false;
    }

    // ── G-Code → Canvas Zeilenmarkierung ────────────────────────

    private void OnGCodeCaretMoved(object? sender, EventArgs e)
    {
        int ln = GCodeBox.TextArea.Caret.Line;
        if (ln == _highlightGCodeLine) return;
        _highlightGCodeLine = ln;
        if (_mouseHoverLine < 1) { _hlTimer.Stop(); _hlTimer.Start(); }
    }

    private void OnGCodeEditorMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var tv  = GCodeBox.TextArea.TextView;
        var vp  = tv.GetPositionFloor(e.GetPosition(tv) + tv.ScrollOffset);
        int ln  = vp?.Line ?? -1;
        if (ln == _mouseHoverLine) return;
        _mouseHoverLine = ln;
        // Editor sofort aktualisieren (kein Debounce nötig)
        if (_gcodeBgRenderer != null)
        {
            _gcodeBgRenderer.HoverLine = ln;
            GCodeBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        // Canvas via Debounce (80 ms)
        _hlTimer.Stop(); _hlTimer.Start();
    }

    private void OnGCodeEditorMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_mouseHoverLine < 0) return;
        _mouseHoverLine = -1;
        if (_gcodeBgRenderer != null)
        {
            _gcodeBgRenderer.HoverLine = -1;
            GCodeBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        _hlTimer.Stop(); _hlTimer.Start();
    }

    private void OnGCodeEditorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_selectedGCodeLine < 1) return;
        int delta = e.Key switch
        {
            Key.Up   => -1,
            Key.Down => +1,
            _        => 0
        };
        if (delta == 0) return;

        int max  = GCodeBox.Document.LineCount;
        int next = Math.Clamp(_selectedGCodeLine + delta, 1, max);
        if (next == _selectedGCodeLine) return;

        SetSelectedGCodeLine(next);
        // Caret mitbewegen damit die Zeile sichtbar bleibt
        GCodeBox.TextArea.Caret.Line   = next;
        GCodeBox.TextArea.Caret.Column = 1;
        GCodeBox.TextArea.Caret.BringCaretToView();
        UpdateAll();
        e.Handled = true;   // Standard-Caret-Bewegung unterdrücken
    }

    private void OnGCodeEditorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var tv = GCodeBox.TextArea.TextView;
        var vp = tv.GetPositionFloor(e.GetPosition(tv) + tv.ScrollOffset);
        int ln = vp?.Line ?? -1;
        if (ln < 1) return;
        // Toggle: nochmal klicken hebt Selektion auf
        SetSelectedGCodeLine(_selectedGCodeLine == ln ? -1 : ln);
        UpdateAll();
    }

    // Konvertierung G-Code-mm → Canvas-Pixel
    private Point TopMmToPx(double x, double y)
    {
        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return default;
        double scale = Math.Min(_topRect.Width / wx, _topRect.Height / wy);
        return new(_topRect.Left + x * scale, _topRect.Bottom - y * scale);
    }

    // ── Klick auf Werkstück-Form ──────────────────────────────────

    private void SetSelectedGCodeLine(int ln)
    {
        _selectedGCodeLine = ln;
        // Beim Deselektieren auch den Caret-Highlight auf dem Canvas löschen —
        // sonst bleibt die Form durch _highlightGCodeLine (= Caret) halb sichtbar.
        if (ln < 0) _highlightGCodeLine = -1;
        if (_gcodeBgRenderer != null)
        {
            _gcodeBgRenderer.SelectedLine = ln;
            GCodeBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    private void OnTopViewFormClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { _selectionSource = 0; OnWorkpieceFormClick(sender, e); }

    private void OnSideViewFormClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { _selectionSource = 1; OnWorkpieceFormClick(sender, e); }

    private void OnWorkpieceFormClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_activeTool != CanvasTool.Select) return;   // Werkzeug aktiv → kein Segment-Klick
        e.Handled = true;   // kein Pan starten
        if (sender is not FrameworkElement el || el.Tag is not int ln) return;
        SetSelectedGCodeLine(_selectedGCodeLine == ln ? -1 : ln);   // toggle
        UpdateAll();   // Form neu zeichnen (mit neuer Farbe)

        // G-Code Editor scrollen und Zeile markieren
        if (_selectedGCodeLine >= 1 && GCodeBox.Document.LineCount >= _selectedGCodeLine)
        {
            GCodeBox.TextArea.Caret.Line   = _selectedGCodeLine;
            GCodeBox.TextArea.Caret.Column = 1;
            GCodeBox.TextArea.Caret.BringCaretToView();
        }
    }

    // Mini-Interpreter: gibt (vorherige X/Y, aktuelle X/Y, Bewegungstyp) für die Ziellinie zurück
    private static readonly Regex RxHlX = new(@"(?<![A-Za-z])X([+-]?[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxHlY = new(@"(?<![A-Za-z])Y([+-]?[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxHlG = new(@"\bG(0{1,2}|1|00|01|02|03|2|3)\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (double px, double py, double cx, double cy, string moveType)?
        GetGCodePosAtLine(string gcode, int targetLine)
    {
        var inv   = System.Globalization.CultureInfo.InvariantCulture;
        var style = System.Globalization.NumberStyles.Float;
        double x = 0, y = 0;
        bool abs = true;
        string modal = "G1";   // modaler Bewegungsbefehl (Standard G1)
        int ln = 0;

        foreach (var raw in gcode.Split('\n'))
        {
            ln++;
            var line = raw.Trim();
            int ci = line.IndexOf('('); if (ci >= 0) line = line[..ci].Trim();
            ci = line.IndexOf(';');     if (ci >= 0) line = line[..ci].Trim();

            if (string.IsNullOrEmpty(line))
            { if (ln == targetLine) return null; continue; }

            if (Regex.IsMatch(line, @"\bG90\b", RegexOptions.IgnoreCase)) abs = true;
            if (Regex.IsMatch(line, @"\bG91\b", RegexOptions.IgnoreCase)) abs = false;

            // Bewegungsbefehl dieser Zeile ermitteln
            string? lineMove = null;
            foreach (Match mg in RxHlG.Matches(line))
            {
                var n = mg.Groups[1].Value.TrimStart('0');
                if (n == "" ) n = "0";
                lineMove = n switch { "0" => "G0", "1" => "G1", "2" => "G2", "3" => "G3", _ => null };
                if (lineMove != null) { modal = lineMove; break; }
            }

            var mx = RxHlX.Match(line);
            var my = RxHlY.Match(line);
            double vx = 0, vy = 0;
            bool hasX = mx.Success && double.TryParse(mx.Groups[1].Value, style, inv, out vx);
            bool hasY = my.Success && double.TryParse(my.Groups[1].Value, style, inv, out vy);

            double nx = hasX ? (abs ? vx : x + vx) : x;
            double ny = hasY ? (abs ? vy : y + vy) : y;

            if (ln == targetLine)
            {
                string mt = (hasX || hasY) ? modal : (lineMove ?? "");
                return (x, y, nx, ny, mt);
            }

            if (hasX || hasY) { x = nx; y = ny; }
        }
        return null;
    }

    private static UIElement BuildTooltipBubble(string? title, string desc)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 7, 10, 8) };
        if (!string.IsNullOrEmpty(title))
        {
            panel.Children.Add(new TextBlock
            {
                Text       = title,
                FontWeight = FontWeights.Bold,
                FontSize   = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 70)),
                Margin     = new Thickness(0, 0, 0, 4)
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text         = desc,
            FontSize     = 12,
            Foreground   = new SolidColorBrush(Color.FromRgb(230, 230, 225)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 340
        });

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(32, 32, 38)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(100, 100, 115)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(7),
            Child           = panel,
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color       = Colors.Black,
                Opacity     = 0.55,
                BlurRadius  = 10,
                ShadowDepth = 4
            }
        };
    }


    private void TextBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
        }
    }

    private void TextBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
            textBox.SelectAll();
    }

    // ── Zeichnen ─────────────────────────────────────────────────

    private void UpdateAll()
    {
        if (DrawSkia == null) return;
        EnsureParsed();

        ComputeWorkpieceRects();

        // Autofit wird ausschließlich in OnDrawSkia ausgeführt (dort ist DrawSkia.ActualWidth garantiert > 0).

        HitCanvas.Children.Clear();
        if (!_topRect.IsEmpty)
        {
            BuildTopViewHits();
            BuildSideViewHits();
        }

        WatermarkCanvas.Children.Clear();

        ApplyCanvasTransform();
    }

    // Bounding-Rect beider Ansichten zusammen (für Zentrierung und Fit)
    private Rect CombinedRect =>
        _topRect.IsEmpty    ? Rect.Empty :
        _bottomRect.IsEmpty ? _topRect   : Rect.Union(_topRect, _bottomRect);

    private void ApplyCenterZoom(double cw, double ch, double zoom)
    {
        var r = CombinedRect;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
        _zoom = zoom;
        _panX = cw / 2 - (r.Left + r.Width  / 2) * _zoom;
        _panY = ch / 2 - (r.Top  + r.Height / 2) * _zoom;
    }

    // 100 % wenn das Werkstück passt, sonst kleinstmöglicher Zoom damit alles sichtbar bleibt
    private double DefaultZoom(double cw, double ch)
    {
        var r = CombinedRect;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return 1.0;
        double margin  = Math.Max(30, Math.Min(cw, ch) * 0.15);
        double fitZoom = Math.Min((cw - 2 * margin) / r.Width,
                                  (ch - 2 * margin) / r.Height);
        return Math.Min(1.0, fitZoom);
    }

    private void ApplyZoomToFit(double cw, double ch)
    {
        var r = CombinedRect;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
        double margin  = Math.Max(30, Math.Min(cw, ch) * 0.15);
        double newZoom = Math.Clamp(
            Math.Min((cw - 2 * margin) / r.Width,
                     (ch - 2 * margin) / r.Height),
            0.05, 200.0);
        ApplyCenterZoom(cw, ch, newZoom);
    }

    // ── SkiaSharp Haupt-Render-Methode ────────────────────────────
    private void OnDrawSkia(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // e.Info gibt physische Pixel; ActualWidth/Height geben logische WPF-DIPs.
        _dpiScale = e.Info.Width > 0 && DrawSkia.ActualWidth > 0
            ? e.Info.Width / DrawSkia.ActualWidth : 1.0;
        double dpiScale = _dpiScale;

        if (_inlineTextBox != null && _inlineParams != null)
        {
            var (selStart, selEnd) = _inlineTextBox.GetSelection();
            bool hasSelection = selStart >= 0 && selEnd >= 0;
            // Zoom aktualisieren: Wenn Zoom oder DPI-Skala sich ändert, IMMER die Schriftgröße anpassen
            // (beim Mausrad-Zoom ändert sich _zoom, nicht _dpiScale!)
            bool needsInitialFontSync = _needsInitialFontSizeUpdate ||
                Math.Abs(_zoom - _lastZoom) > 0.001 ||
                Math.Abs(_dpiScale - _lastDpiScale) > 0.001;

            if (needsInitialFontSync)
            {
                _needsInitialFontSizeUpdate = false;

                // Berechne Zoom-Faktoren BEVOR _lastZoom und _lastDpiScale aktualisiert werden
                double lastZoom = _lastZoom > 0 ? _lastZoom : 1.0;
                double lastDpiScale = _lastDpiScale > 0 ? _lastDpiScale : 1.0;

                float zoomFactor = (float)(_zoom / lastZoom);
                float dpiScaleFactor = (float)(_dpiScale / lastDpiScale);
                float totalScaleFactor = zoomFactor * dpiScaleFactor;

                // Skaliere nur wenn der Faktor sinnvoll ist (zwischen 0.1 und 10)
                if (totalScaleFactor > 0.1f && totalScaleFactor < 10.0f && Math.Abs(totalScaleFactor - 1.0f) > 0.001f)
                {
                    _inlineTextBox.ScaleFontSize(totalScaleFactor);
                }

                // Speichere aktuelle Werte
                _lastDpiScale = _dpiScale;
                _lastZoom = _zoom;
            }
        }

        // Alle Zoom/Pan-Berechnungen in logischen Pixeln (cw/ch = WPF-DIPs).
        double cw = DrawSkia.ActualWidth;
        double ch = DrawSkia.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        EnsureParsed();
        ComputeWorkpieceRects();

        if (_needsAutoFit && _topRect.Width > 0 && _topRect.Height > 0)
        {
            _needsAutoFit = false;
            ApplyCenterZoom(cw, ch, DefaultZoom(cw, ch));
            ApplyCanvasTransform();
        }

        // Matrix in physischen Pixeln: logische Werte × DPI-Faktor
        canvas.SetMatrix(SKMatrix.CreateScaleTranslation(
            (float)(_zoom * dpiScale), (float)(_zoom * dpiScale),
            (float)(_panX * dpiScale), (float)(_panY * dpiScale)));

        // Werkstücke + G-Code + Nullpunkt + Raster zeichnen
        DrawWorkpiecesSk(canvas);
        DrawGCodeTopViewSk(canvas);
        DrawShapeOutlinesSk(canvas);
        DrawGCodeSideViewSk(canvas);
        DrawNullpunktCrosshairSk(canvas);
        if (_rasterEnabled) DrawRasterSk(canvas, cw, ch);

        // Selektions-Locator: zoom-invariant, in Screen-Koordinaten
        if (_selectedGCodeLine >= 1 && !_topRect.IsEmpty)
            DrawSelectionLocatorSk(canvas);

        // Move-Werkzeug: Bounding-Box mit Ankerpunkten; Pfeil-Werkzeug: Punkte-Dots
        if (_activeTool == CanvasTool.Move)
            DrawPfadChainBBoxes(canvas);
        else if (_activeTool == CanvasTool.Pfeil)
            DrawPfadPunkteDots(canvas);
        else if (_activeTool == CanvasTool.Reihenloch)
            DrawReihenlochOverlay(canvas);

        DrawVermassungOverlay(canvas);
        DrawTextLineSegmentsSk(canvas);
        DrawTemplateImageSk(canvas);

        // Pfad- und Textfeld-Werkzeuge: Fadenkreuz über gesamte Zeichenfläche
        if (_pfadMouseValid && WorkX > 0 && WorkY > 0 && !_topRect.IsEmpty
            && (_activeTool is CanvasTool.PfadStart or CanvasTool.PfadLinie or CanvasTool.PfadBogen or CanvasTool.PfadSpline
                or CanvasTool.VCarveTextSk or CanvasTool.VCarveTextSk or CanvasTool.Rechteck
                or CanvasTool.Kreis or CanvasTool.NEck))
        {
            double sc2 = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
            float  cx2 = (float)(_topRect.Left   + _pfadMouseMm.x * sc2);
            float  cy2 = (float)(_topRect.Bottom - _pfadMouseMm.y * sc2);
            float  lt2 = (float)(1.0 / _zoom);
            float  r2  = (float)(3.5 / _zoom);
            float  dk  = (float)(8.0 / _zoom);   // Dash-Länge
            float  gk  = (float)(5.0 / _zoom);   // Gap-Länge

            // Sichtbare Ausdehnung in Canvas-Koordinaten
            float canvasL = (float)(-_panX / _zoom);
            float canvasR = (float)((cw - _panX) / _zoom);
            float canvasT = (float)(-_panY / _zoom);
            float canvasB = (float)((ch - _panY) / _zoom);

            using var cp = new SKPaint
            {
                Color = new SKColor(200, 70, 0, 180),
                Style = SKPaintStyle.Stroke, StrokeWidth = lt2, IsAntialias = false,
                PathEffect = SKPathEffect.CreateDash(new float[] { dk, gk }, 0)
            };
            canvas.DrawLine(canvasL, cy2, canvasR, cy2, cp);
            canvas.DrawLine(cx2, canvasT, cx2, canvasB, cp);

            // Kleiner Kreis als Positionsmarker
            using var cr = new SKPaint { Color = new SKColor(220, 80, 0, 230),
                Style = SKPaintStyle.Stroke, StrokeWidth = lt2 * 1.5f, IsAntialias = true };
            canvas.DrawCircle(cx2, cy2, r2, cr);

            if (_activeTool == CanvasTool.PfadBogen && _pfadBogenWaiting)
            {
                float ex = (float)(_topRect.Left   + _pfadBogenEndAbs.x * sc2);
                float ey = (float)(_topRect.Bottom - _pfadBogenEndAbs.y * sc2);
                using var ep2 = new SKPaint { Color = new SKColor(220, 80, 0, 200),
                    Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawCircle(ex, ey, (float)(4.5 / _zoom), ep2);
                // Live-Bogenvorschau
                var p1 = GetLastPfadAbsPoint();
                if (p1.HasValue)
                    DrawBogenPreview(canvas, p1.Value, _pfadBogenEndAbs, _pfadMouseMm, lt2);
            }

            // Live-Spline-Vorschau und Punkt-Markierungen (auch nach Finalisierung sichtbar)
            // KEINE geraden Linien zwischen Spline-Punkten! Nur die interpolierte Kurve!
            if (_splinePointsBeingCreated.Count >= 1 && !_splineFinalized)
            {
                // Kurvenvorschau bereits nach 1. Spline-Punkt
                var previewPts = new List<(double x, double y)>();

                // Finde den direkten Vorgänger-Punkt (Startpunkt oder letzter Linie/Bogenpunkt)
                if (_history.Count > 0)
                {
                    // Finde den letzten Nicht-Spline-Punkt
                    for (int i = _history.Count - 1; i >= 0; i--)
                    {
                        if (_history[i].Params is PfadPunktParams p && p.Typ != PfadPunktTyp.Spline)
                        {
                            // Berechne die Position dieses Punktes
                            (double x, double y) pt;
                            if (p.Bezugspunkt == "Letzter Punkt" && i > 0)
                            {
                                // Relativer Punkt - berechne Position vom vorherigen Punkt
                                var prevPt = GetPointAtIndex(i - 1);
                                pt = prevPt.HasValue ? (prevPt.Value.x + p.XRel, prevPt.Value.y + p.YRel) : (p.XRel, p.YRel);
                            }
                            else
                            {
                                // Absolute Koordinaten
                                pt = GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, WorkX, WorkY);
                            }
                            previewPts.Add(pt);
                            break;
                        }
                    }
                }

                // Füge Spline-Punkte hinzu
                previewPts.AddRange(_splinePointsBeingCreated);

                // Zeichne Vorschau wenn mindestens 1 Punkt vorhanden
                if (previewPts.Count >= 1)
                {
                    DrawSplinePreviewSk(canvas, previewPts,
                                        _splineModeBeingCreated, _splineTensionBeingCreated, finalized: false);
                }

                // Markiere Spline-Punkte
                using var pointPaint = new SKPaint
                {
                    Color = new SKColor(255, 165, 0, 220),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                foreach (var pt in _splinePointsBeingCreated)
                {
                    float px = (float)(_topRect.Left + pt.x * sc2);
                    float py = (float)(_topRect.Bottom - pt.y * sc2);
                    canvas.DrawCircle(px, py, (float)(3.5 / _zoom), pointPaint);
                }
            }
        }

        // Fertige Spline-Linie IMMER zeichnen (auch wenn Cursor außerhalb)
        if (_splineFinalized && _splinePointsBeingCreated.Count >= 1 && WorkX > 0 && WorkY > 0 && !_topRect.IsEmpty)
        {
            var previewPts = new List<(double x, double y)>();

            // Finde den direkten Vorgänger-Punkt
            if (_history.Count > 0)
            {
                for (int i = _history.Count - 1; i >= 0; i--)
                {
                    if (_history[i].Params is PfadPunktParams p && p.Typ != PfadPunktTyp.Spline)
                    {
                        (double x, double y) pt;
                        if (p.Bezugspunkt == "Letzter Punkt" && i > 0)
                        {
                            var prevPt = GetPointAtIndex(i - 1);
                            pt = prevPt.HasValue ? (prevPt.Value.x + p.XRel, prevPt.Value.y + p.YRel) : (p.XRel, p.YRel);
                        }
                        else
                        {
                            pt = GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, WorkX, WorkY);
                        }
                        previewPts.Add(pt);
                        break;
                    }
                }
            }

            previewPts.AddRange(_splinePointsBeingCreated);

            // KEINE gerade Linie zwischen Spline-Punkten zeichnen! Nur die interpolierte Kurve!
            if (previewPts.Count >= 1)
            {
                // Nur die interpolierte Kurve zeichnen, nicht die direkten Verbindungen
                DrawSplinePreviewSk(canvas, previewPts,
                                    _splineModeBeingCreated, _splineTensionBeingCreated, finalized: true);
            }
            // Ende: Es sollte KEINE andere Zeichnung nach DrawSplinePreviewSk sein!
        }

    }

    // ── Template-Image zeichnen ──────────────────────────────────────
    private void DrawTemplateImageSk(SKCanvas canvas)
    {
        if (_templateImage?.Bitmap == null) return;

        double sc = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
        if (sc <= 0) return;

        var bitmap = _templateImage.Bitmap;

        // Bildmitte in Canvas-Koordinaten
        float centerX = (float)(_topRect.Left + _templateImage.X * sc);
        float centerY = (float)(_topRect.Bottom - _templateImage.Y * sc);

        // Skaliere Bild auf mm-Größe
        float dstWidth = (float)(_templateImage.Width * sc);
        float dstHeight = (float)(_templateImage.Height * sc);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            Color = new SKColor(255, 255, 255, (byte)(int)(_templateImage.Opacity * 255))
        };

        canvas.Save();
        canvas.Translate(centerX, centerY);
        canvas.RotateDegrees((float)_templateImage.Rotation);

        // Spiegelung anwenden (vor Translation für korrekte Skalierung)
        if (_templateImage.FlipHorizontal)
            canvas.Scale(-1, 1);
        if (_templateImage.FlipVertical)
            canvas.Scale(1, -1);

        // Jetzt zum Ursprung verschieben und zeichnen
        canvas.Translate(-dstWidth / 2, -dstHeight / 2);

        // Direktes Skalieren des Bitmap auf die richtige Größe
        var dst = new SKRect(0, 0, dstWidth, dstHeight);
        canvas.DrawBitmap(bitmap, dst, paint);
        canvas.Restore();

        // 9 Ankerpunkte zeichnen
        using var anchorPaint = new SKPaint
        {
            Color = new SKColor(255, 150, 0, 220),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 200, 0, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(2.0 / _zoom),
            IsAntialias = true
        };

        for (int i = 0; i < 9; i++)
        {
            var pt = _templateImage.AnchorPoints[i];
            float px = (float)(_topRect.Left + pt.x * sc);
            float py = (float)(_topRect.Bottom - pt.y * sc);

            float radius = (float)(4.0 / _zoom);

            // Höheres Highlight für gerade gezogenen Punkt
            if (i == _templateImageDragAnchor)
            {
                using var highlightPaint = new SKPaint
                {
                    Color = new SKColor(255, 100, 0, 255),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                canvas.DrawCircle(px, py, radius * 1.5f, highlightPaint);
            }

            canvas.DrawCircle(px, py, radius, anchorPaint);
            canvas.DrawCircle(px, py, radius, borderPaint);
        }
    }

    // ── Werkstück-Layout berechnen (stabile mm-Weltkoordinaten) ──────
    private void ComputeWorkpieceRects()
    {
        double wx = WorkX, wy = WorkY, wz = WorkZ;
        if (wx <= 0 || wy <= 0 || wz <= 0) { _topRect = _bottomRect = Rect.Empty; return; }
        // Fester 20-mm-Abstand zwischen Draufsicht und Seitenansicht.
        // _topRect / _bottomRect sind in mm und ändern sich nicht mehr mit der Canvas-Größe.
        _topRect    = new Rect(0, 0,       wx, wy);
        _bottomRect = new Rect(0, wy + 50, wx, wz);
    }

    // ── Werkstücke zeichnen (SkiaSharp) ───────────────────────────
    private void DrawWorkpiecesSk(SKCanvas canvas)
    {
        if (_topRect.IsEmpty) return;
        DrawWoodRectSk(canvas, _topRect);
        DrawWoodRectSk(canvas, _bottomRect);
    }

    private static void DrawWoodRectSk(SKCanvas canvas, Rect r)
    {
        var bmp = GetWoodBitmap();
        var dst = new SKRect((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);
        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Medium };
        canvas.DrawBitmap(bmp, dst, paint);
        using var border = new SKPaint
        {
            Color = new SKColor(0xE0, 0xD4, 0xB8),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.1f,
        };
        canvas.DrawRect(dst, border);
    }

    private static SKBitmap? _woodBitmap;
    private static SKBitmap GetWoodBitmap()
    {
        if (_woodBitmap != null) return _woodBitmap;
        var imgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maple.png");
        if (System.IO.File.Exists(imgPath))
            _woodBitmap = SKBitmap.Decode(imgPath);
        _woodBitmap ??= CreateMapleTextureSk(512, 512);
        return _woodBitmap;
    }

    private static SKBitmap CreateMapleTextureSk(int w, int h)
    {
        var rng    = new Random(13);
        var pixels = new byte[w * h * 4];
        const int gw = 64, gh = 64;
        var grid1 = new double[gw * gh];
        var grid2 = new double[gw * gh];
        var grid3 = new double[gw * gh];
        for (int i = 0; i < gw * gh; i++)
        { grid1[i] = rng.NextDouble(); grid2[i] = rng.NextDouble(); grid3[i] = rng.NextDouble(); }

        double Bilinear(double[] g, double gx, double gy)
        {
            gx = ((gx % gw) + gw) % gw; gy = ((gy % gh) + gh) % gh;
            int x0 = (int)gx, y0 = (int)gy, x1 = (x0 + 1) % gw, y1 = (y0 + 1) % gh;
            double fx = gx - x0, fy = gy - y0;
            return g[y0 * gw + x0] * (1 - fx) * (1 - fy) + g[y0 * gw + x1] * fx * (1 - fy)
                 + g[y1 * gw + x0] * (1 - fx) * fy       + g[y1 * gw + x1] * fx * fy;
        }
        double Fractal(double[] g, double gx, double gy)
        {
            double v = 0, amp = 0.5, freq = 1, sum = 0;
            for (int o = 0; o < 4; o++)
            { v += Bilinear(g, gx * freq, gy * freq) * amp; sum += amp; amp *= 0.55; freq *= 2.1; }
            return v / sum;
        }
        const double twoPi = 2 * Math.PI;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double gx = x * gw / (double)w, gy = y * gh / (double)h;
            double warpY = (Fractal(grid1, gx, gy) - 0.5) * 55.0;
            double warpX = (Fractal(grid2, gx + 10, gy + 10) - 0.5) * 8.0;
            double ring = Math.Sin((y + warpY) * twoPi / 70.0 + (x + warpX) * 0.003)
                        + 0.25 * Math.Sin((y + warpY) * twoPi / 22.0 + (x + warpX) * 0.006);
            double t = Math.Clamp(Math.Pow((Math.Sin(ring * 1.8) + 1.0) / 2.0, 2.2), 0.0, 1.0);
            double tt = Math.Clamp(t + (Fractal(grid3, gx * 0.7, gy * 0.7) - 0.5) * 0.12, 0.0, 1.0);
            int pi = (y * w + x) * 4;
            pixels[pi]     = (byte)(223 - tt * 143);
            pixels[pi + 1] = (byte)(240 - tt * 64);
            pixels[pi + 2] = (byte)(246 - tt * 30);
            pixels[pi + 3] = 255;
        }
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var ptr = bmp.GetPixels();
        if (ptr != IntPtr.Zero)
            Marshal.Copy(pixels, 0, ptr, pixels.Length);
        bmp.NotifyPixelsChanged();
        return bmp;
    }

    private void DrawIconWatermark(Rect target)
    {
        if (target.IsEmpty || target.Width < 20 || target.Height < 20) return;

        const double SVG = 256.0;
        double scX = target.Width  / SVG;
        double scY = target.Height / SVG;
        double S(double v) => v * Math.Min(scX, scY);
        double X(double x) => target.Left + x * scX;
        double Y(double y) => target.Top  + y * scY;
        Point  P(double x, double y) => new(X(x), Y(y));

        var c = Brushes.Black;
        var wm = new Canvas { IsHitTestVisible = false, Opacity = 0.06 };

        void Rect(double x, double y, double w, double h, double rx, bool filled = true)
        {
            var r = new System.Windows.Shapes.Rectangle {
                Width=w*scX, Height=h*scY, RadiusX=S(rx), RadiusY=S(rx),
                Fill=filled ? c : Brushes.Transparent, Stroke=c, StrokeThickness=S(filled ? 0 : 1.5) };
            Canvas.SetLeft(r, X(x)); Canvas.SetTop(r, Y(y));
            wm.Children.Add(r);
        }
        void Poly(params (double x, double y)[] pts) =>
            wm.Children.Add(new System.Windows.Shapes.Polygon {
                Fill=c, Points=new PointCollection(pts.Select(p => P(p.x, p.y))) });
        void Line(double x1, double y1, double x2, double y2, double w, bool dash=false)
        {
            var ln = new System.Windows.Shapes.Line {
                X1=X(x1), Y1=Y(y1), X2=X(x2), Y2=Y(y2),
                Stroke=c, StrokeThickness=S(w),
                StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round };
            if (dash) ln.StrokeDashArray = new System.Windows.Media.DoubleCollection {4,3};
            wm.Children.Add(ln);
        }
        void Path(string d, double w)
        {
            var el = new System.Windows.Shapes.Path {
                Data=System.Windows.Media.Geometry.Parse(d),
                Stroke=c, StrokeThickness=S(w), Fill=Brushes.Transparent,
                RenderTransform=new System.Windows.Media.ScaleTransform(scX, scY) };
            Canvas.SetLeft(el, target.Left); Canvas.SetTop(el, target.Top);
            wm.Children.Add(el);
        }
        void Dot(double cx, double cy, double r)
        {
            var e = new System.Windows.Shapes.Ellipse { Width=r*2*scX, Height=r*2*scY, Fill=c };
            Canvas.SetLeft(e, X(cx)-r*scX); Canvas.SetTop(e, Y(cy)-r*scY);
            wm.Children.Add(e);
        }
        void Text(double x, double y, string t, double sz)
        {
            var tb = new TextBlock { Text=t, FontSize=S(sz), Foreground=c,
                FontFamily=new System.Windows.Media.FontFamily("Consolas"), FontWeight=FontWeights.Bold };
            Canvas.SetLeft(tb, X(x)); Canvas.SetTop(tb, Y(y)-S(sz));
            wm.Children.Add(tb);
        }

        // ── Fräsbahnpfeil (Draufsicht, oben) ────────────────────────────────
        Line(24,18, 232,18, 1.5, dash:true);
        Poly((225,18),(215,13),(215,23));            // Pfeilspitze rechts
        Text(16, 22, "G01", 13);

        // ── Werkzeughalter / Flansch ─────────────────────────────────────────
        Rect(90, 28, 76, 34, 8);                    // Spindelgehäuse
        // Spannzange: trapezförmig
        Poly((90,62),(100,62),(116,82),(140,82),(156,62),(166,62),(162,62),(148,86),(108,86),(94,62));

        // ── Schaft (Zylinder mit Spiralschneiden) ────────────────────────────
        Rect(110, 86, 36, 90, 2);
        // linke Spiralschneide
        Path("M111,88 C105,100 118,108 111,120 C104,132 118,140 111,152 C104,162 112,170 111,176", 2);
        // rechte Spiralschneide
        Path("M145,88 C151,100 138,108 145,120 C152,132 138,140 145,152 C152,162 144,170 145,176", 2);

        // ── Schneidspitze (flacher Fräser) ───────────────────────────────────
        Rect(110, 176, 36, 4, 0);

        // ── Holzoberfläche ───────────────────────────────────────────────────
        Line(24, 184, 232, 184, 3);

        // ── Holzkörper ───────────────────────────────────────────────────────
        Rect(24, 184, 208, 60, 3, false);

        // ── Holzmaserung (wellige Linien) ────────────────────────────────────
        Path("M24,196 Q56,191 88,196 Q120,201 152,196 Q184,191 232,196", 1);
        Path("M24,210 Q56,205 88,210 Q120,215 152,210 Q184,205 232,210", 1);
        Path("M24,224 Q56,219 88,224 Q120,229 152,224 Q184,219 232,224", 1);
        Path("M24,238 Q56,233 88,238 Q120,243 152,238 Q184,233 232,238", 1);

        // ── Gefräste Nut (Querschnitt) ───────────────────────────────────────
        Line(110, 184, 110, 200, 2);     // linke Nutwand
        Line(146, 184, 146, 200, 2);     // rechte Nutwand
        Line(110, 200, 146, 200, 2);     // Nutboden

        // ── Späne / Holzfasern am Kontaktpunkt ───────────────────────────────
        Line(104, 182, 96,  170, 1.8);
        Line(100, 183, 91,  173, 1.4);
        Line( 95, 182, 87,  175, 1.2);
        Line(152, 182, 160, 170, 1.8);
        Line(156, 183, 165, 173, 1.4);
        Line(161, 182, 169, 175, 1.2);
        Dot(88, 168, 3);
        Dot(82, 174, 2.2);
        Dot(168, 168, 3);
        Dot(174, 174, 2.2);

        // ── X/Y/Z-Achsenbeschriftungen ───────────────────────────────────────
        Line(24, 250, 200, 250, 2);
        Poly((200,250),(190,245),(190,255));         // X-Pfeil
        Text(204, 255, "X", 15);
        Line(18, 184, 18, 50, 2);
        Poly((18,48),(13,58),(23,58));               // Z-Pfeil
        Text(21, 58, "Z", 15);

        WatermarkCanvas.Children.Add(wm);
    }

    // ── G-Code Draufsicht: visuell (SkiaSharp) + Hit-Shapes (HitCanvas) ─────────────

    private void DrawGCodeTopViewSk(SKCanvas canvas)
    {
        var moves = _cachedTopMoves;
        if (_topRect.IsEmpty) return;
        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return;

        double scale = Math.Min(_topRect.Width / wx, _topRect.Height / wy);

        // Echte Skalierung: WPF-Pixel pro Millimeter (für Textfeld-Größenberechnung)
        double canvasW = DrawSkia.ActualWidth;
        double canvasH = DrawSkia.ActualHeight;
        _canvasScale = canvasW > 0 && canvasH > 0 ? Math.Min(canvasW / wx, canvasH / wy) : 1.0;
        (float px, float py) MmToPx(double x, double y)
        {
            // Konvertiere Nullpunkt-Koordinaten zurück zu Oben-Links-Koordinaten
            var (standardX, standardY) = ConvertFromNullpunktCoordinates(x, y);
            return (
                (float)(_topRect.Left + standardX * scale),
                (float)(_topRect.Bottom - standardY * scale)
            );
        }

        void AddArc(SKPath p, float endX, float endY, float r, bool lg, bool cw) =>
            p.ArcTo(r, r, 0, lg ? SKPathArcSize.Large : SKPathArcSize.Small,
                cw ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, endX, endY);

        if (moves.Count > 0)
        {
            int activeLine = _mouseHoverLine >= 1 ? _mouseHoverLine : _highlightGCodeLine;
            float lt = (float)(1.5 / _zoom);  // zoom-invariante Linienstärke

            // ── Rapid-Moves: gestrichelte graue Linie ──
            using (var rPath = new SKPath())
            {
                float lx = 0, ly = 0; bool had = false;
                foreach (var m in moves)
                {
                    bool skip = m.LineNumber == _selectedGCodeLine || m.LineNumber == activeLine;
                    (float cx, float cy) = m.Type is MoveType.ArcCW or MoveType.ArcCCW
                        ? MmToPx(m.Xe, m.Ye) : MmToPx(m.X, m.Y);
                    if (m.Type == MoveType.Rapid && had && !skip)
                    { rPath.MoveTo(lx, ly); rPath.LineTo(cx, cy); }
                    lx = cx; ly = cy; had = true;
                }
                using var rp = new SKPaint { Color = new SKColor(160, 160, 160), Style = SKPaintStyle.Stroke,
                    StrokeWidth = lt, IsAntialias = true,
                    PathEffect = SKPathEffect.CreateDash(new[] { 5 * lt, 3 * lt }, 0) };
                canvas.DrawPath(rPath, rp);
            }

            // ── Schnittlinien ──
            if (_showFraesbreite)
            {
                float borderThick = (float)(2.0 / _zoom);
                using var borderBrush = new SKPaint { Color = new SKColor(50, 50, 50, 130), Style = SKPaintStyle.Stroke,
                    StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
                using var fillBrush = new SKPaint { Color = new SKColor(150, 150, 150, 35), Style = SKPaintStyle.Stroke,
                    StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
                float lx2 = 0, ly2 = 0; bool had2 = false;

                foreach (var m in moves)
                {
                    bool skip = m.LineNumber == _selectedGCodeLine || m.LineNumber == activeLine ||
                                (_selectionSource==1 && _selectedGCodeLine>=1 && m.LineNumber>0 &&
                                 m.Type!=MoveType.Rapid && Math.Abs(m.LineNumber-_selectedGCodeLine)<=3);
                    (float ex, float ey) = m.Type is MoveType.ArcCW or MoveType.ArcCCW
                        ? MmToPx(m.Xe, m.Ye) : MmToPx(m.X, m.Y);
                    if (m.Type == MoveType.Rapid) { had2=true; lx2=ex; ly2=ey; continue; }
                    if (skip || m.ToolWidthMm <= 0) { had2=true; lx2=ex; ly2=ey; continue; }

                    float sX = had2 ? lx2 : (float)(_topRect.Left + m.X*scale);
                    float sY = had2 ? ly2 : (float)(_topRect.Bottom - m.Y*scale);
                    float toolPx = Math.Max(lt * 3, (float)(m.ToolWidthMm * scale));
                    borderBrush.StrokeWidth = toolPx + borderThick;
                    fillBrush.StrokeWidth   = toolPx;

                    using var seg = new SKPath();
                    seg.MoveTo(sX, sY);
                    if (m.Type == MoveType.Line)
                    { seg.LineTo(ex, ey); }
                    else
                    {
                        double ccx=m.X+m.I, ccy=m.Y+m.J, ar=Math.Sqrt(m.I*m.I+m.J*m.J);
                        if (ar > 0)
                        {
                            bool cw = m.Type == MoveType.ArcCW;
                            float arPx = (float)(ar * scale);
                            bool full = Math.Abs(m.Xe-m.X)<1e-6 && Math.Abs(m.Ye-m.Y)<1e-6;
                            if (full)
                            {
                                double sA=Math.Atan2(m.Y-ccy,m.X-ccx);
                                var (mpx,mpy)=MmToPx(ccx+ar*Math.Cos(sA+Math.PI),ccy+ar*Math.Sin(sA+Math.PI));
                                AddArc(seg, mpx, mpy, arPx, false, cw);
                                AddArc(seg, sX, sY, arPx, false, cw);
                            }
                            else
                            {
                                double sA=Math.Atan2(m.Y-ccy,m.X-ccx),eA=Math.Atan2(m.Ye-ccy,m.Xe-ccx);
                                if(cw&&eA>sA)eA-=2*Math.PI; if(!cw&&eA<sA)eA+=2*Math.PI;
                                AddArc(seg, ex, ey, arPx, Math.Abs(eA-sA)>Math.PI, cw);
                            }
                        }
                    }
                    canvas.DrawPath(seg, borderBrush);
                    canvas.DrawPath(seg, fillBrush);
                    had2=true; lx2=ex; ly2=ey;
                }
                // Rote Mittellinie on top
                using var midPath = new SKPath();
                float lxm=0, lym=0; bool hadm=false;
                foreach (var m in moves)
                {
                    bool skip = m.LineNumber==_selectedGCodeLine || m.LineNumber==activeLine ||
                                (_selectionSource==1&&_selectedGCodeLine>=1&&m.LineNumber>0&&
                                 m.Type!=MoveType.Rapid&&Math.Abs(m.LineNumber-_selectedGCodeLine)<=3);
                    if (m.Type is MoveType.Rapid or MoveType.Line)
                    {
                        var (cx,cy)=MmToPx(m.X,m.Y);
                        if (m.Type==MoveType.Line && hadm && !skip) { midPath.MoveTo(lxm,lym); midPath.LineTo(cx,cy); }
                        lxm=cx; lym=cy; hadm=true;
                    }
                    else
                    {
                        double ccx=m.X+m.I,ccy=m.Y+m.J,ar=Math.Sqrt(m.I*m.I+m.J*m.J);
                        if (ar>0 && !skip)
                        {
                            bool cw=m.Type==MoveType.ArcCW; float arPx=(float)(ar*scale);
                            var (spx,spy)=MmToPx(m.X,m.Y); var (epx,epy)=MmToPx(m.Xe,m.Ye);
                            bool full=Math.Abs(m.Xe-m.X)<1e-6&&Math.Abs(m.Ye-m.Y)<1e-6;
                            midPath.MoveTo(spx,spy);
                            if(full){double sA=Math.Atan2(m.Y-ccy,m.X-ccx);var(mx,my)=MmToPx(ccx+ar*Math.Cos(sA+Math.PI),ccy+ar*Math.Sin(sA+Math.PI));AddArc(midPath,mx,my,arPx,false,cw);AddArc(midPath,spx,spy,arPx,false,cw);}
                            else{double sA=Math.Atan2(m.Y-ccy,m.X-ccx),eA=Math.Atan2(m.Ye-ccy,m.Xe-ccx);if(cw&&eA>sA)eA-=2*Math.PI;if(!cw&&eA<sA)eA+=2*Math.PI;AddArc(midPath,epx,epy,arPx,Math.Abs(eA-sA)>Math.PI,cw);}
                        }
                        lxm=(float)(_topRect.Left+m.Xe*scale); lym=(float)(_topRect.Bottom-m.Ye*scale); hadm=true;
                    }
                }
                using var midPaint = new SKPaint { Color = new SKColor(200,30,30), Style=SKPaintStyle.Stroke, StrokeWidth=lt, IsAntialias=true };
                canvas.DrawPath(midPath, midPaint);
            }
            else
            {
                // Einfache rote Schnittlinie
                using var cutPath = new SKPath();
                float lx3=0, ly3=0; bool had3=false;
                foreach (var m in moves)
                {
                    bool skip = m.LineNumber==_selectedGCodeLine || m.LineNumber==activeLine ||
                                (_selectionSource==1&&_selectedGCodeLine>=1&&m.LineNumber>0&&
                                 m.Type!=MoveType.Rapid&&Math.Abs(m.LineNumber-_selectedGCodeLine)<=3);
                    if (m.Type is MoveType.Rapid or MoveType.Line)
                    {
                        var (cx,cy)=MmToPx(m.X,m.Y);
                        if (m.Type==MoveType.Line && had3 && !skip) { cutPath.MoveTo(lx3,ly3); cutPath.LineTo(cx,cy); }
                        lx3=cx; ly3=cy; had3=true;
                    }
                    else
                    {
                        double ccx=m.X+m.I,ccy=m.Y+m.J,ar=Math.Sqrt(m.I*m.I+m.J*m.J);
                        if (ar>0 && !skip)
                        {
                            bool cw=m.Type==MoveType.ArcCW; float arPx=(float)(ar*scale);
                            var (spx,spy)=MmToPx(m.X,m.Y); var (epx,epy)=MmToPx(m.Xe,m.Ye);
                            bool full=Math.Abs(m.Xe-m.X)<1e-6&&Math.Abs(m.Ye-m.Y)<1e-6;
                            cutPath.MoveTo(spx,spy);
                            if(full){double sA=Math.Atan2(m.Y-ccy,m.X-ccx);var(mx,my)=MmToPx(ccx+ar*Math.Cos(sA+Math.PI),ccy+ar*Math.Sin(sA+Math.PI));AddArc(cutPath,mx,my,arPx,false,cw);AddArc(cutPath,spx,spy,arPx,false,cw);}
                            else{double sA=Math.Atan2(m.Y-ccy,m.X-ccx),eA=Math.Atan2(m.Ye-ccy,m.Xe-ccx);if(cw&&eA>sA)eA-=2*Math.PI;if(!cw&&eA<sA)eA+=2*Math.PI;AddArc(cutPath,epx,epy,arPx,Math.Abs(eA-sA)>Math.PI,cw);}
                        }
                        lx3=(float)(_topRect.Left+m.Xe*scale); ly3=(float)(_topRect.Bottom-m.Ye*scale); had3=true;
                    }
                }
                using var cutPaint = new SKPaint { Color=new SKColor(200,30,30), Style=SKPaintStyle.Stroke, StrokeWidth=lt, IsAntialias=true };
                canvas.DrawPath(cutPath, cutPaint);
            }

            // ── Selektierter / aktiver Move hervorheben ──
            float lxh=0, lyh=0;
            foreach (var m in moves)
            {
                float fromX=lxh, fromY=lyh;
                bool sel    = m.LineNumber == _selectedGCodeLine;
                bool active = !sel && m.LineNumber == activeLine;
                bool nearby = !sel && !active && _selectionSource==1 && _selectedGCodeLine>=1 && m.LineNumber>0 &&
                              Math.Abs(m.LineNumber-_selectedGCodeLine)<=3 && m.Type!=MoveType.Rapid;
                if (sel || active || nearby)
                {
                    var hlColor = sel    ? new SKColor(255,215,0)
                                : active ? new SKColor(255,235,80)
                                         : new SKColor(255,215,0,140);
                    float hlW = sel ? (float)(2.5/_zoom) : (float)(2.0/_zoom);
                    using var hlPaint = new SKPaint { Color=hlColor, Style=SKPaintStyle.Stroke, StrokeWidth=hlW, IsAntialias=true };
                    using var hlPath = new SKPath();
                    if (m.Type is MoveType.Rapid or MoveType.Line)
                    { var (tx,ty)=MmToPx(m.X,m.Y); hlPath.MoveTo(fromX,fromY); hlPath.LineTo(tx,ty); }
                    else
                    {
                        double ccx=m.X+m.I,ccy=m.Y+m.J,ar=Math.Sqrt(m.I*m.I+m.J*m.J);
                        if (ar>0) {
                            bool cw=m.Type==MoveType.ArcCW; float arPx=(float)(ar*scale);
                            var (spx,spy)=MmToPx(m.X,m.Y); var (epx,epy)=MmToPx(m.Xe,m.Ye);
                            bool full=Math.Abs(m.Xe-m.X)<1e-6&&Math.Abs(m.Ye-m.Y)<1e-6;
                            hlPath.MoveTo(spx,spy);
                            if(full){double sA=Math.Atan2(m.Y-ccy,m.X-ccx);var(mx,my)=MmToPx(ccx+ar*Math.Cos(sA+Math.PI),ccy+ar*Math.Sin(sA+Math.PI));AddArc(hlPath,mx,my,arPx,false,cw);AddArc(hlPath,spx,spy,arPx,false,cw);}
                            else{double sA=Math.Atan2(m.Y-ccy,m.X-ccx),eA=Math.Atan2(m.Ye-ccy,m.Xe-ccx);if(cw&&eA>sA)eA-=2*Math.PI;if(!cw&&eA<sA)eA+=2*Math.PI;AddArc(hlPath,epx,epy,arPx,Math.Abs(eA-sA)>Math.PI,cw);}
                        }
                    }
                    canvas.DrawPath(hlPath, hlPaint);
                }
                (lxh, lyh) = m.Type is MoveType.ArcCW or MoveType.ArcCCW
                    ? ((float)(_topRect.Left+m.Xe*scale), (float)(_topRect.Bottom-m.Ye*scale))
                    : ((float)(_topRect.Left+m.X*scale),  (float)(_topRect.Bottom-m.Y*scale));
            }

            // ── Bohrpunkte (visuell) ──
            foreach (var hole in _cachedDrillPoints)
            {
                bool selHole = hole.LineNumber == _selectedGCodeLine;
                var (hx, hy) = MmToPx(hole.X, hole.Y);
                float dotR = (float)(3.0 / _zoom);
                using var dotFill = new SKPaint { Color = selHole ? new SKColor(255,215,0) : new SKColor(0,140,255), IsAntialias=true };
                using var dotBord = new SKPaint { Color=SKColors.White, Style=SKPaintStyle.Stroke, StrokeWidth=(float)(1.0/_zoom), IsAntialias=true };
                canvas.DrawCircle(hx, hy, dotR, dotFill);
                canvas.DrawCircle(hx, hy, dotR, dotBord);
            }
        } // end if (moves.Count > 0)

        // ── Gravieren: Buchstaben-Konturen grau ──
        foreach (var entry in _history)
        {
            if (entry.Params is not GraviereParams gp || (!gp.IsTasche && !gp.IsVCarve)) continue;
            bool isPreview = entry == HistoryList.SelectedItem && _previewGravParams != null;
            var displayGp = isPreview ? _previewGravParams! : gp;
            GCodeGenerator.TextGeoCtx tctx;
            if (_textGeoCache.TryGetValue(displayGp, out var cachedCtx))
                tctx = cachedCtx;
            else if (isPreview)
                tctx = GCodeGenerator.BuildTextGeo(displayGp, wx, wy);  // preview: nur 1 Eintrag, kein VCarve
            else
            { LaunchVCacheAsync(displayGp); continue; }
            if (tctx.FlatDisplay.Bounds.IsEmpty) continue;

            double ts = tctx.Scale, tmH = tctx.MultiH;
            (float, float) ToPxT(double fx, double fy)
            {
                var (px, py) = MmToPx(tctx.Ox + fx * ts, tctx.Oy + tctx.YOffset + (tmH - fy) * ts);
                return (px, py);
            }

            using var contPath = new SKPath();
            foreach (var fig in tctx.FlatDisplay.Figures)
            {
                if (!fig.IsClosed) continue;
                var (sx, sy) = ToPxT(fig.StartPoint.X, fig.StartPoint.Y);
                contPath.MoveTo(sx, sy);
                foreach (var seg in fig.Segments)
                {
                    if (seg is System.Windows.Media.PolyLineSegment pls)
                        foreach (var pt in pls.Points) { var (ptx,pty)=ToPxT(pt.X,pt.Y); contPath.LineTo(ptx,pty); }
                    else if (seg is System.Windows.Media.LineSegment ls)
                    { var (ptx,pty)=ToPxT(ls.Point.X,ls.Point.Y); contPath.LineTo(ptx,pty); }
                }
                contPath.LineTo(sx, sy);
            }
            using var contPaint = new SKPaint { Color=new SKColor(80,80,80,200), Style=SKPaintStyle.Stroke, StrokeWidth=(float)(1.0/_zoom), IsAntialias=true };
            canvas.DrawPath(contPath, contPaint);
        }

        // ── V-Carve Kreise (blau, optional) ──
        bool showVC = MnuVCarveVisualisieren.IsChecked == true;
        var allVCC = new List<GCodeGenerator.VCarveCircle>();
        foreach (var entry in _history)
        {
            if (entry.Params is not GraviereParams gp || !gp.IsVCarve) continue;
            if (!_vCarveCache.TryGetValue(gp, out var circles))
            { LaunchVCacheAsync(gp); continue; }
            allVCC.AddRange(circles);
            if (!showVC || circles.Count == 0) continue;

            using var vcPath = new SKPath();
            foreach (var c in circles)
            {
                float rPx = (float)(c.R * scale);
                if (rPx < 0.01f) continue;
                var (cx, cy) = MmToPx(c.X, c.Y);
                vcPath.MoveTo(cx + rPx, cy);
                AddArc(vcPath, cx - rPx, cy, rPx, false, true);
                AddArc(vcPath, cx + rPx, cy, rPx, false, true);
            }
            using var vcPaint = new SKPaint { Color=new SKColor(0,80,210), Style=SKPaintStyle.Stroke, StrokeWidth=(float)(0.8/_zoom), IsAntialias=true };
            canvas.DrawPath(vcPath, vcPaint);
        }
        VCarveCenters = allVCC;

        // ── Gravieren-Textfelder (grau gepunktet) ──
        bool isMoveTool   = _activeTool == CanvasTool.Move;
        bool isCtrlResize = _ctrlResizeMode && _inlineTextBox != null && _inlineExistingIdx >= 0;
        HistoryEntry? ctrlEntry = isCtrlResize && _inlineExistingIdx < _history.Count
                                  ? _history[_inlineExistingIdx] : null;
        int  moveIdx    = _moveHistoryIdx;
        foreach (var entry in _history)
        {
            if (entry.Params is not GraviereParams gpBase) continue;
            bool isSelected = HistoryList.SelectedItem == entry;
            // During move drag, show preview position for the dragged entry
            var gp = (isSelected && _previewGravParams != null) ? _previewGravParams : gpBase;
            double fh = gp.TextHoehe > 0 ? gp.TextHoehe : gp.FontSizeMm;
            if (gp.TextBreite <= 0 || fh <= 0) continue;
            var (ox2, oy2) = GCodeGenerator.ConvertBezugspunkt(gp.Bezugspunkt, gp.XRel, gp.YRel, WorkX, WorkY);
            if (gp.Bezugspunkt.Contains("Oben"))                                       oy2 -= fh;
            if (gp.Bezugspunkt.Contains("rechts", StringComparison.OrdinalIgnoreCase)) ox2 -= gp.TextBreite;
            if (gp.Bezugspunkt is "Mitte" or "Oben Mitte" or "Unten Mitte")            ox2 -= gp.TextBreite / 2;
            var (tlx, tly) = MmToPx(ox2, oy2 + fh);
            var (brx, bry) = MmToPx(ox2 + gp.TextBreite, oy2);
            float pw = brx - tlx, ph = bry - tly;
            if (pw < 1 || ph < 1) continue;
            float dw = 4f / (float)_zoom;
            bool showHandles = isSelected && (isMoveTool || (isCtrlResize && entry == ctrlEntry));
            var frameColor = showHandles ? new SKColor(0xFF, 0xA0, 0x00) : SKColors.Gray;
            using var rectPaint = new SKPaint { Color = frameColor, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1.0 / _zoom),
                PathEffect = SKPathEffect.CreateDash(new[] { dw, dw * 0.75f }, 0) };
            canvas.DrawRect(tlx, tly, pw, ph, rectPaint);

            // Anchor squares at 4 corners for selected entry in Move mode
            if (showHandles)
            {
                float as_ = 8f / (float)_zoom;   // zoom-invariant: always 8 screen px
                using var anchorFill = new SKPaint { Color = new SKColor(0xFF, 0xA0, 0x00), Style = SKPaintStyle.Fill };
                using var anchorBdr  = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
                void DrawAnchor(float ax, float ay)
                {
                    canvas.DrawRect(ax - as_ / 2, ay - as_ / 2, as_, as_, anchorFill);
                    canvas.DrawRect(ax - as_ / 2, ay - as_ / 2, as_, as_, anchorBdr);
                }
                float mx_ = (tlx + brx) / 2f;
                float my_ = (tly + bry) / 2f;
                // 4 Ecken
                DrawAnchor(tlx, tly);
                DrawAnchor(brx, tly);
                DrawAnchor(tlx, bry);
                DrawAnchor(brx, bry);
                // 4 Kantenmittelpunkte
                DrawAnchor(mx_, tly);
                DrawAnchor(brx, my_);
                DrawAnchor(mx_, bry);
                DrawAnchor(tlx, my_);
            }
        }

        // ── Rechteck-Konturen ──
        foreach (var entry in _history)
        {
            if (entry.Params is not RechteckParams rp) continue;
            bool isSelected = HistoryList.SelectedItem == entry;
            bool isDragged  = isSelected && _previewRktParams != null;
            if (isDragged) rp = _previewRktParams!;
            var (refX, refY) = GCodeGenerator.ConvertBezugspunkt(rp.Bezugspunkt, rp.XRel, rp.YRel, wx, wy);
            var (bx, by) = rp.Bezugspunkt switch
            {
                "Unten links"  => (0.0,           0.0),
                "Unten Mitte"  => (-rp.Breite/2,  0.0),
                "Unten rechts" => (-rp.Breite,     0.0),
                "Links Mitte"  => (0.0,            -rp.Hoehe/2),
                "Mitte"        => (-rp.Breite/2,   -rp.Hoehe/2),
                "Rechts Mitte" => (-rp.Breite,     -rp.Hoehe/2),
                "Oben links"   => (0.0,            -rp.Hoehe),
                "Oben Mitte"   => (-rp.Breite/2,   -rp.Hoehe),
                _              => (-rp.Breite,     -rp.Hoehe)
            };
            double rx0 = refX + bx, ry0 = refY + by;
            double rx1 = rx0 + rp.Breite, ry1 = ry0 + rp.Hoehe;

            // Verrundungsradius auf Pixelskala begrenzen
            double maxR = Math.Min(rp.Breite, rp.Hoehe) / 2.0;
            double r    = Math.Min(Math.Max(0.0, rp.Verrundung), maxR);

            // Hilfsfunktionen: mm → Pixel, Pixel-Radius
            (float px, float py) P(double x, double y) => MmToPx(x, y);
            float Rp() => (float)(r * scale);

            using var rktPath = new SKPath();
            if (r < 1e-6)
            {
                // Einfaches Rechteck
                var (x0p, y1p) = P(rx0, ry1);
                rktPath.MoveTo(x0p, y1p);
                rktPath.LineTo(P(rx1, ry1).px, P(rx1, ry1).py);
                rktPath.LineTo(P(rx1, ry0).px, P(rx1, ry0).py);
                rktPath.LineTo(P(rx0, ry0).px, P(rx0, ry0).py);
                rktPath.Close();
            }
            else
            {
                float rp2 = Rp();
                // Gerundetes Rechteck — Bögen im Bildschirm-KS (Y-down → CCW = CW in Maschine)
                var (bl_x, bl_y) = P(rx0,   ry0);   // unten-links  (Screen: oben-links)
                var (br_x, br_y) = P(rx1,   ry0);   // unten-rechts (Screen: oben-rechts)
                var (tr_x, tr_y) = P(rx1,   ry1);   // oben-rechts  (Screen: unten-rechts)
                var (tl_x, tl_y) = P(rx0,   ry1);   // oben-links   (Screen: unten-links)

                rktPath.MoveTo(bl_x + rp2, bl_y);
                rktPath.LineTo(br_x - rp2, br_y);
                rktPath.ArcTo(rp2, rp2, 0, SKPathArcSize.Small, SKPathDirection.CounterClockwise,     br_x, br_y - rp2);
                rktPath.LineTo(tr_x, tr_y + rp2);
                rktPath.ArcTo(rp2, rp2, 0, SKPathArcSize.Small, SKPathDirection.CounterClockwise,     tr_x - rp2, tr_y);
                rktPath.LineTo(tl_x + rp2, tl_y);
                rktPath.ArcTo(rp2, rp2, 0, SKPathArcSize.Small, SKPathDirection.CounterClockwise,     tl_x, tl_y + rp2);
                rktPath.LineTo(bl_x, bl_y - rp2);
                rktPath.ArcTo(rp2, rp2, 0, SKPathArcSize.Small, SKPathDirection.CounterClockwise,     bl_x + rp2, bl_y);
                rktPath.Close();
            }

            var lineColor = isSelected ? new SKColor(0xFF, 0xA0, 0x00) : new SKColor(80, 80, 80, 200);
            using var rktPaint = new SKPaint
            {
                Color = lineColor, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(1.0 / _zoom), IsAntialias = true
            };
            canvas.DrawPath(rktPath, rktPaint);

            // Anker-Quadrate wenn im Verschieben-Modus und selektiert
            if (isSelected && _activeTool == CanvasTool.Move)
            {
                float as_ = 8f / (float)_zoom;
                using var anchorFill = new SKPaint { Color = new SKColor(0xFF, 0xA0, 0x00), Style = SKPaintStyle.Fill };
                using var anchorBdr  = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
                void DrawAnchor(float ax, float ay)
                {
                    canvas.DrawRect(ax - as_ / 2, ay - as_ / 2, as_, as_, anchorFill);
                    canvas.DrawRect(ax - as_ / 2, ay - as_ / 2, as_, as_, anchorBdr);
                }
                var (blx, bly) = P(rx0, ry0);
                var (brx, bry) = P(rx1, ry0);
                var (trx, trY) = P(rx1, ry1);
                var (tlx, tly) = P(rx0, ry1);
                float mhx = (blx + brx) / 2f, mhy = (bly + bry) / 2f; // bottom mid
                float mvx = (blx + tlx) / 2f, mvy = (bly + tly) / 2f; // left mid
                // 0=BL 1=BR 2=TL 3=TR 4=BM 5=RM 6=TM 7=LM
                DrawAnchor(blx, bly); DrawAnchor(brx, bry);
                DrawAnchor(tlx, tly); DrawAnchor(trx, trY);
                DrawAnchor(mhx, mhy);
                DrawAnchor((brx + trx) / 2f, (bry + trY) / 2f);
                DrawAnchor((tlx + trx) / 2f, (tly + trY) / 2f);
                DrawAnchor(mvx, mvy);
            }
        }

        // ── Kreis-Konturen ──
        foreach (var entry in _history)
        {
            if (entry.Params is not KreisParams kr) continue;
            bool isSelected = HistoryList.SelectedItem == entry;
            bool isDragged  = isSelected && _previewKreisParams != null;
            if (isDragged) kr = _previewKreisParams!;
            var (cx, cy) = GCodeGenerator.ConvertBezugspunkt(kr.Bezugspunkt, kr.XRel, kr.YRel, wx, wy);
            float rPx = (float)(kr.Radius * scale);
            var (cpx, cpy) = MmToPx(cx, cy);
            var lineColor = isSelected ? new SKColor(0xFF, 0xA0, 0x00) : new SKColor(80, 80, 80, 200);
            using var krPaint = new SKPaint
            {
                Color = lineColor, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(1.0 / _zoom), IsAntialias = true
            };
            canvas.DrawCircle(cpx, cpy, rPx, krPaint);

            if (isSelected && _activeTool == CanvasTool.Move)
            {
                float as_ = 8f / (float)_zoom;
                using var anchorFill = new SKPaint { Color = new SKColor(0xFF, 0xA0, 0x00), Style = SKPaintStyle.Fill };
                using var anchorBdr  = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
                void DrawAnchorK(float ax, float ay)
                {
                    canvas.DrawRect(ax - as_ / 2, ay - as_ / 2, as_, as_, anchorFill);
                    canvas.DrawRect(ax - as_ / 2, ay - as_ / 2, as_, as_, anchorBdr);
                }
                DrawAnchorK(cpx + rPx, cpy);      // rechts (Radius-Griff)
                DrawAnchorK(cpx,       cpy);       // Mitte
            }
        }

        // ── N-Eck-Konturen ──
        foreach (var entry in _history)
        {
            if (entry.Params is not NEckParams np || np.Ecken < 3) continue;
            bool isSelected = HistoryList.SelectedItem == entry;
            var (ncx, ncy) = GCodeGenerator.ConvertBezugspunkt(np.Bezugspunkt, np.XRel, np.YRel, wx, wy);
            using var nePath = new SKPath();
            for (int i = 0; i < np.Ecken; i++)
            {
                double angle = 2 * Math.PI * i / np.Ecken + np.RotationGrad * Math.PI / 180.0 - Math.PI / 2;
                var (px, py) = MmToPx(ncx + np.Radius * Math.Cos(angle), ncy + np.Radius * Math.Sin(angle));
                if (i == 0) nePath.MoveTo(px, py);
                else nePath.LineTo(px, py);
            }
            nePath.Close();
            var lineColor = isSelected ? new SKColor(0xFF, 0xA0, 0x00) : new SKColor(80, 80, 80, 200);
            using var nePaint = new SKPaint
            {
                Color = lineColor, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(1.0 / _zoom), IsAntialias = true
            };
            canvas.DrawPath(nePath, nePaint);
        }

        // ── Pfad-Konturen ──
        {
            var selEntry = HistoryList.SelectedItem as HistoryEntry;

            // Hilfsfunktion: Bogenmittelpunkt aus PfadPunktParams (spiegelt GCodeGenerator.ResolveBogenMid)
            (double mx, double my) ArcMid((double x, double y) p1, (double x, double y) p2, PfadPunktParams p)
            {
                if (p.BogenModus == "Bogenmitte")
                    return p.Bezugspunkt == "Letzter Punkt"
                        ? (p1.x + p.XMid, p1.y + p.YMid)
                        : GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XMid, p.YMid, wx, wy);
                double dx = p2.x - p1.x, dy = p2.y - p1.y;
                double L = Math.Sqrt(dx * dx + dy * dy);
                if (L < 1e-10) return ((p1.x + p2.x) / 2, (p1.y + p2.y) / 2);
                double perpX = -dy / L, perpY = dx / L;
                double mcx = (p1.x + p2.x) / 2, mcy = (p1.y + p2.y) / 2;
                double h = p.BogenModus == "Radius"
                    ? (Math.Max(Math.Abs(p.XMid), L / 2) - Math.Sqrt(Math.Max(0,
                        Math.Max(p.XMid * p.XMid, L * L / 4) - L * L / 4))) * (p.XMid >= 0 ? 1 : -1)
                    : p.XMid;
                return (mcx + h * perpX, mcy + h * perpY);
            }

            // Bogenmittelpunkt + Radius aus 3 Punkten (inline von ArcFrom3Points)
            static (double cx, double cy, double R, bool cw) Arc3Pts(
                double x1, double y1, double xm, double ym, double x2, double y2)
            {
                double ax = (x1 + xm) / 2, ay = (y1 + ym) / 2;
                double dax = ym - y1, day = x1 - xm;
                double bx = (xm + x2) / 2, by = (ym + y2) / 2;
                double dbx = y2 - ym, dby = xm - x2;
                double det = dax * (-dby) + dbx * day;
                if (Math.Abs(det) < 1e-12) return (0, 0, double.PositiveInfinity, false);
                double t = ((bx - ax) * (-dby) + dbx * (by - ay)) / det;
                double cx = ax + t * dax, cy = ay + t * day;
                double R = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
                double cross = (xm - x1) * (y2 - y1) - (ym - y1) * (x2 - x1);
                return (cx, cy, R, cross < 0);
            }

            var fullyConstrainedPts = GetFullyConstrainedPoints();
            int hi = 0;
            while (hi < _history.Count)
            {
                if (_history[hi].Params is not PfadPunktParams startP || startP.Typ != PfadPunktTyp.Start)
                { hi++; continue; }

                // Kette ab Start sammeln
                int chainHi = hi;
                var chain = new List<(HistoryEntry e, PfadPunktParams p)> { (_history[hi], startP) };
                int hj = hi + 1;
                while (hj < _history.Count
                    && _history[hj].Params is PfadPunktParams np
                    && np.Typ != PfadPunktTyp.Start)
                { chain.Add((_history[hj], np)); hj++; }
                hi = hj;

                // Absolute Positionen
                var pts = new List<(double x, double y)>();
                foreach (var (_, p) in chain)
                {
                    (double x, double y) pt = p.Bezugspunkt == "Letzter Punkt" && pts.Count > 0
                        ? (pts[^1].x + p.XRel, pts[^1].y + p.YRel)
                        : GCodeGenerator.ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, wx, wy);
                    pts.Add(pt);
                }
                if (pts.Count < 1) continue;

                bool isSel = chain.Any(c => c.e == selEntry);
                var lineCol = isSel ? new SKColor(0xFF, 0xA0, 0x00) : new SKColor(80, 80, 80, 200);

                // arcMids aufbauen (null = Linie, HasValue = Bogen mit Mittelspunkt)
                var arcMidsC = new List<(double mx, double my)?>(pts.Count);
                arcMidsC.Add(null);
                for (int k = 1; k < pts.Count; k++)
                {
                    var (_, pp) = chain[k];
                    if (pp.Typ == PfadPunktTyp.Bogen)
                        arcMidsC.Add(ArcMid(pts[k - 1], pts[k], pp));
                    else
                        arcMidsC.Add(null);
                }

                // Spline-Punkte aus der Kette entfernen - sie sollten NICHT mit geraden Linien verbunden werden!
                var chainForCornerArcs = chain.Where(c => c.p.Typ != PfadPunktTyp.Spline).ToList();

                // Verrundungen pro Punkt (nur für Nicht-Spline-Punkte)
                var verrC = chainForCornerArcs.Select(c => c.p.Verrundung).ToList();
                bool hasVerrC = verrC.Any(v => v > 1e-10);

                // Verrundungs-Vorverarbeitung: Ecken einfügen (NICHT für Spline-Punkte!)
                var drawPts  = pts;
                var drawMids = arcMidsC;
                if (hasVerrC && chainForCornerArcs.Count > 0)
                    (drawPts, drawMids) = GCodeGenerator.InsertLineCornerArcs(
                        new List<(double x, double y)>(pts), arcMidsC, verrC, closed: false);

                // Konkave Ecken mit Fräserradius visualisieren:
                // Kreis-Bogen mit Radius r um den Fräspfad-Eckpunkt Q (korrekte Darstellung)
                var chainStart = chain[0].p;
                if (chainStart.Radiuskorrektur != "Mittig" && chainStart.FraeserD > 1e-10)
                {
                    double sign = chainStart.Radiuskorrektur == "Links" ? 1.0 : -1.0;
                    var concaveRad = GCodeGenerator.ConcaveCornerRadii(
                        drawPts, drawMids, chainStart.FraeserD / 2.0, sign);
                    if (concaveRad.Any(v => v > 1e-10))
                        (drawPts, drawMids) = GCodeGenerator.InsertConcaveCircleArcs(
                            new List<(double x, double y)>(drawPts),
                            new List<(double, double)?>(drawMids),
                            concaveRad, sign);
                }

                using var pfadPath = new SKPath();
                var sp0 = MmToPx(drawPts[0].x, drawPts[0].y);
                pfadPath.MoveTo(sp0.px, sp0.py);

                for (int k = 1; k < drawPts.Count; k++)
                {
                    var ep = MmToPx(drawPts[k].x, drawPts[k].y);
                    if (drawMids[k].HasValue)
                    {
                        var (mx, my) = drawMids[k]!.Value;
                        var (cx, cy, R, cw) = Arc3Pts(drawPts[k-1].x, drawPts[k-1].y, mx, my, drawPts[k].x, drawPts[k].y);
                        if (!double.IsInfinity(R) && R > 1e-6)
                        {
                            var cp = MmToPx(cx, cy);
                            float rPx2 = (float)(R * scale);
                            var oval = new SKRect(cp.px - rPx2, cp.py - rPx2, cp.px + rPx2, cp.py + rPx2);
                            float a1 = (float)(Math.Atan2(-(drawPts[k-1].y - cy), drawPts[k-1].x - cx) * 180 / Math.PI);
                            float a2 = (float)(Math.Atan2(-(drawPts[k].y   - cy), drawPts[k].x   - cx) * 180 / Math.PI);
                            float sw = a2 - a1;
                            if ( cw && sw < 0) sw += 360;
                            if (!cw && sw > 0) sw -= 360;
                            pfadPath.ArcTo(oval, a1, sw, false);
                        }
                        else pfadPath.LineTo(ep.px, ep.py);
                    }
                    else pfadPath.LineTo(ep.px, ep.py);
                }

                using var pfadPaint = new SKPaint { Color = lineCol, Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)(1.5 / _zoom), IsAntialias = true };
                canvas.DrawPath(pfadPath, pfadPaint);

                // Vollständig eingeschränkte Segmente in grüner Farbe hervorheben
                if (!isSel && fullyConstrainedPts.Count > 0)
                {
                    using var cPaint = new SKPaint { Color = new SKColor(0, 190, 110),
                        Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1.5 / _zoom), IsAntialias = true };
                    for (int k = 1; k < chain.Count; k++)
                    {
                        if (!fullyConstrainedPts.Contains(chainHi + k - 1) ||
                            !fullyConstrainedPts.Contains(chainHi + k)) continue;
                        using var cSegPath = new SKPath();
                        var csp = MmToPx(pts[k - 1].x, pts[k - 1].y);
                        cSegPath.MoveTo(csp.px, csp.py);
                        if (arcMidsC[k].HasValue)
                        {
                            var (amx, amy) = arcMidsC[k]!.Value;
                            var (cx2, cy2, cR2, ccw2) = Arc3Pts(
                                pts[k-1].x, pts[k-1].y, amx, amy, pts[k].x, pts[k].y);
                            if (!double.IsInfinity(cR2) && cR2 > 1e-6)
                            {
                                var ccp = MmToPx(cx2, cy2);
                                float rPxC = (float)(cR2 * scale);
                                var ovalC = new SKRect(ccp.px - rPxC, ccp.py - rPxC,
                                                       ccp.px + rPxC, ccp.py + rPxC);
                                float ca1 = (float)(Math.Atan2(-(pts[k-1].y - cy2), pts[k-1].x - cx2) * 180 / Math.PI);
                                float ca2 = (float)(Math.Atan2(-(pts[k].y   - cy2), pts[k].x   - cx2) * 180 / Math.PI);
                                float csw = ca2 - ca1;
                                if ( ccw2 && csw < 0) csw += 360;
                                if (!ccw2 && csw > 0) csw -= 360;
                                cSegPath.ArcTo(ovalC, ca1, csw, false);
                            }
                            else
                            {
                                var cep = MmToPx(pts[k].x, pts[k].y);
                                cSegPath.LineTo(cep.px, cep.py);
                            }
                        }
                        else
                        {
                            var cep = MmToPx(pts[k].x, pts[k].y);
                            cSegPath.LineTo(cep.px, cep.py);
                        }
                        canvas.DrawPath(cSegPath, cPaint);
                    }
                }

                // Punkte als kleine Kreise (fixierte in grün)
                float dotR2 = 3f / (float)_zoom;
                using var dotPaintDef  = new SKPaint { Color = lineCol, Style = SKPaintStyle.Fill };
                using var dotPaintFix  = new SKPaint { Color = new SKColor(0, 190, 110), Style = SKPaintStyle.Fill };
                for (int kd = 0; kd < pts.Count; kd++)
                {
                    bool ptFix = !isSel && fullyConstrainedPts.Contains(chainHi + kd);
                    var dp = MmToPx(pts[kd].x, pts[kd].y);
                    canvas.DrawCircle(dp.px, dp.py, dotR2, ptFix ? dotPaintFix : dotPaintDef);
                }
            }
        }
    }

    // ── Hit-Shapes für Draufsicht (werden in HitCanvas eingefügt) ──
    private void BuildTopViewHits()
    {
        var moves = _cachedTopMoves;
        if (_topRect.IsEmpty) return;
        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return;

        double scale = Math.Min(_topRect.Width / wx, _topRect.Height / wy);
        Point MmToPxW(double x, double y) =>
            new(_topRect.Left + x * scale, _topRect.Bottom - y * scale);

        double hitThick = Math.Max(2.0, 14.0 / _zoom);
        double lx = 0, ly = 0;

        foreach (var m in moves)
        {
            double fromX = lx, fromY = ly;
            System.Windows.Shapes.Path? hitEl = null;

            if (m.Type is MoveType.Rapid or MoveType.Line)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                { ctx.BeginFigure(MmToPxW(fromX, fromY), false, false); ctx.LineTo(MmToPxW(m.X, m.Y), true, false); }
                geo.Freeze();
                hitEl = new System.Windows.Shapes.Path { Data=geo, Stroke=Brushes.Transparent, StrokeThickness=hitThick, Cursor=Cursors.Hand, Tag=m.LineNumber };
                lx=m.X; ly=m.Y;
            }
            else
            {
                double ccx=m.X+m.I, ccy=m.Y+m.J, ar=Math.Sqrt(m.I*m.I+m.J*m.J);
                if (ar > 0)
                {
                    bool cw = m.Type == MoveType.ArcCW;
                    var sweep = cw ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
                    double arPx = ar * scale;
                    var spx = MmToPxW(m.X, m.Y); var epx = MmToPxW(m.Xe, m.Ye);
                    bool full = Math.Abs(m.Xe-m.X)<1e-6 && Math.Abs(m.Ye-m.Y)<1e-6;
                    var pg = new PathGeometry();
                    var fig = new PathFigure { StartPoint=spx, IsFilled=false };
                    if (full)
                    {
                        double sA=Math.Atan2(m.Y-ccy,m.X-ccx);
                        var mp=MmToPxW(ccx+ar*Math.Cos(sA+Math.PI),ccy+ar*Math.Sin(sA+Math.PI));
                        fig.Segments.Add(new ArcSegment(mp,  new System.Windows.Size(arPx,arPx), 0, false, sweep, true));
                        fig.Segments.Add(new ArcSegment(spx, new System.Windows.Size(arPx,arPx), 0, false, sweep, true));
                    }
                    else
                    {
                        double sA=Math.Atan2(m.Y-ccy,m.X-ccx), eA=Math.Atan2(m.Ye-ccy,m.Xe-ccx);
                        if(cw&&eA>sA)eA-=2*Math.PI; if(!cw&&eA<sA)eA+=2*Math.PI;
                        fig.Segments.Add(new ArcSegment(epx, new System.Windows.Size(arPx,arPx), 0, Math.Abs(eA-sA)>Math.PI, sweep, true));
                    }
                    pg.Figures.Add(fig);
                    hitEl = new System.Windows.Shapes.Path { Data=pg, Stroke=Brushes.Transparent, StrokeThickness=hitThick, Cursor=Cursors.Hand, Tag=m.LineNumber };
                }
                lx=m.Xe; ly=m.Ye;
            }
            if (hitEl != null) { hitEl.MouseLeftButtonDown += OnTopViewFormClick; HitCanvas.Children.Add(hitEl); }
        }

        // Bohrpunkte
        foreach (var hole in _cachedDrillPoints)
        {
            var ctr = MmToPxW(hole.X, hole.Y);
            double dotR = 5.0;
            var circle = new Ellipse { Width=dotR*2, Height=dotR*2, Fill=Brushes.Transparent, Cursor=Cursors.Hand, Tag=hole.LineNumber };
            circle.MouseLeftButtonDown += OnTopViewFormClick;
            Canvas.SetLeft(circle, ctr.X - dotR); Canvas.SetTop(circle, ctr.Y - dotR);
            HitCanvas.Children.Add(circle);
        }
    }

    // ── G-Code Seitenansicht: visuell (SkiaSharp) ────────────────────────────────
    private void DrawGCodeSideViewSk(SKCanvas canvas)
    {
        var moves = _cachedSideMoves;
        if (moves.Count == 0 || _bottomRect.IsEmpty) return;
        double wx = WorkX, wz = WorkZ;
        if (wx <= 0 || wz <= 0) return;

        double scale = Math.Min(_bottomRect.Width / wx, _bottomRect.Height / wz);
        (float px, float py) MmToPx(double x, double z)
        {
            // Konvertiere X-Koordinate vom Nullpunkt-System zurück zu Oben-Links-System
            var (standardX, _) = ConvertFromNullpunktCoordinates(x, 0); // Y wird nicht benötigt in Seitenansicht
            return (
                (float)(_bottomRect.Left + standardX * scale),
                (float)(_bottomRect.Top  + (-z) * scale)
            );
        }

        float thick = (float)(1.5 / _zoom);
        int activeLine = _mouseHoverLine >= 1 ? _mouseHoverLine : _highlightGCodeLine;

        // Rapid + Cut getrennt aufbauen
        using var cutPath  = new SKPath();
        using var rapPath  = new SKPath();
        System.Windows.Point? prevPtW = null;
        foreach (var m in moves)
        {
            var (cx, cy) = MmToPx(m.X, m.Z);
            if (prevPtW.HasValue)
            {
                var path = m.Cmd == "G0" ? rapPath : cutPath;
                path.MoveTo((float)prevPtW.Value.X, (float)prevPtW.Value.Y);
                path.LineTo(cx, cy);
            }
            prevPtW = new System.Windows.Point(cx, cy);
        }

        using var rapPaint = new SKPaint { Color=new SKColor(160,160,160), Style=SKPaintStyle.Stroke, StrokeWidth=thick, IsAntialias=true,
            PathEffect=SKPathEffect.CreateDash(new[]{ 5*thick, 3*thick }, 0) };
        using var cutPaint = new SKPaint { Color=new SKColor(200,30,30), Style=SKPaintStyle.Stroke, StrokeWidth=thick, IsAntialias=true };
        canvas.DrawPath(rapPath, rapPaint);
        canvas.DrawPath(cutPath, cutPaint);

        // Selektierter / aktiver Move hervorheben
        prevPtW = null;
        foreach (var m in moves)
        {
            var (cx, cy) = MmToPx(m.X, m.Z);
            if (prevPtW.HasValue && m.LineNumber > 0)
            {
                bool sel    = m.LineNumber == _selectedGCodeLine;
                bool active = !sel && m.LineNumber == activeLine;
                bool nearby = !sel && !active && _selectionSource==0 && _selectedGCodeLine>=1 &&
                              Math.Abs(m.LineNumber-_selectedGCodeLine)<=3 && m.Cmd!="G0";
                if (sel || active || nearby)
                {
                    var hlColor = sel    ? new SKColor(255,215,0)
                                : active ? new SKColor(255,235,80)
                                         : new SKColor(255,215,0,200);
                    float hlW = sel ? (float)(2.5/_zoom) : (float)(2.0/_zoom);
                    using var hlPaint = new SKPaint { Color=hlColor, Style=SKPaintStyle.Stroke, StrokeWidth=hlW, IsAntialias=true };
                    using var hlPath  = new SKPath();
                    hlPath.MoveTo((float)prevPtW.Value.X, (float)prevPtW.Value.Y);
                    hlPath.LineTo(cx, cy);
                    canvas.DrawPath(hlPath, hlPaint);
                }
            }
            prevPtW = new System.Windows.Point(cx, cy);
        }
    }

    // ── Hit-Shapes für Seitenansicht ──────────────────────────────
    private void BuildSideViewHits()
    {
        var moves = _cachedSideMoves;
        if (moves.Count == 0 || _bottomRect.IsEmpty) return;
        double wx = WorkX, wz = WorkZ;
        if (wx <= 0 || wz <= 0) return;

        double scale = Math.Min(_bottomRect.Width / wx, _bottomRect.Height / wz);
        Point MmToPxW(double x, double z) =>
            new(_bottomRect.Left + x * scale, _bottomRect.Top + (-z) * scale);

        double hitThick = Math.Max(2.0, 14.0 / _zoom);
        System.Windows.Point? prevPt = null;
        foreach (var m in moves)
        {
            var cur = MmToPxW(m.X, m.Z);
            if (prevPt.HasValue && m.LineNumber > 0 && m.Cmd != "G0")
            {
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                { c.BeginFigure(prevPt.Value, false, false); c.LineTo(cur, true, false); }
                geo.Freeze();
                var hitEl = new System.Windows.Shapes.Path
                {
                    Data=geo, Stroke=Brushes.Transparent, StrokeThickness=hitThick,
                    Cursor=Cursors.Hand, Tag=m.LineNumber
                };
                hitEl.MouseLeftButtonDown += OnSideViewFormClick;
                HitCanvas.Children.Add(hitEl);
            }
            prevPt = cur;
        }
    }

    // ── Rechteck-, Kreis- und NEck-Konturen zeichnen ──────────────
    private void DrawShapeOutlinesSk(SKCanvas canvas)
    {
        if (_topRect.IsEmpty) return;
        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return;

        double scale = Math.Min(_topRect.Width / wx, _topRect.Height / wy);
        (float px, float py) MmToPx(double x, double y)
        {
            var (standardX, standardY) = ConvertFromNullpunktCoordinates(x, y);
            return (
                (float)(_topRect.Left + standardX * scale),
                (float)(_topRect.Bottom - standardY * scale)
            );
        }

        float lt = (float)(1.5 / _zoom);
        using var outlinePaint = new SKPaint
        {
            Color = new SKColor(255, 150, 0),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = lt,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4 * lt, 3 * lt }, 0)
        };

        foreach (var entry in _history)
        {
            if (entry.Params is RechteckParams rp)
            {
                var (left, bottom, w, h) = RechteckBoundsInMm(rp);
                var (x1, y1) = MmToPx(left, bottom);
                var (x2, y2) = MmToPx(left + w, bottom + h);
                canvas.DrawRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1), outlinePaint);
            }
            else if (entry.Params is KreisParams kp)
            {
                // Beim Verschieben die Vorschau-Parameter verwenden (wie die durchgezogene Kontur)
                if (_previewKreisParams != null && HistoryList.SelectedItem == entry) kp = _previewKreisParams;
                var (kmx, kmy) = GCodeGenerator.ConvertBezugspunkt(kp.Bezugspunkt, kp.XRel, kp.YRel, wx, wy);
                var (cx, cy) = MmToPx(kmx, kmy);
                float r = (float)(kp.Radius * scale);
                canvas.DrawCircle(cx, cy, r, outlinePaint);
            }
            else if (entry.Params is NEckParams np)
            {
                if (np.Ecken < 3) continue;
                // Eckpunkte in mm wie in GCodeGenerator.NEck (Bezugspunkt + Rotation), dann in Pixel
                var (cxMm, cyMm) = GCodeGenerator.ConvertBezugspunkt(np.Bezugspunkt, np.XRel, np.YRel, wx, wy);
                using var path = new SKPath();
                for (int i = 0; i < np.Ecken; i++)
                {
                    double angle = 2 * Math.PI * i / np.Ecken + np.RotationGrad * Math.PI / 180.0 - Math.PI / 2;
                    var (px, py) = MmToPx(cxMm + np.Radius * Math.Cos(angle), cyMm + np.Radius * Math.Sin(angle));
                    if (i == 0) path.MoveTo(px, py);
                    else path.LineTo(px, py);
                }
                path.Close();
                canvas.DrawPath(path, outlinePaint);
            }
        }
    }

    // ── Pfad Fräsen zeichnen ─────────────────────────────────────

#if false // DrawPfadFräsen + DrawHoverArrows
    private void DrawPfadFräsen()
    {
        if (_topRect.IsEmpty) return;

        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return;

        // State immer setzen — auch ohne Punkte, damit Klicken sofort funktioniert
        _pfadScale      = Math.Min(_topRect.Width / wx, _topRect.Height / wy);
        _pfadCanvasRect = _topRect;

        if (_pfadPunkte.Count == 0) return;

        int selIdx = PfadLvPunkte.SelectedIndex;

        // Linien mit Richtungspfeil-Mitte
        for (int i = 0; i < _pfadPunkte.Count - 1; i++)
        {
            var a = PunktToPx(_pfadPunkte[i]);
            var b = PunktToPx(_pfadPunkte[i + 1]);
            bool hi = (i == selIdx || i + 1 == selIdx);
            DrawCanvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = hi ? Brushes.OrangeRed : Brushes.DarkViolet,
                StrokeThickness = hi ? 3 : 2
            });

            // Kleiner Richtungspfeil in der Linienmitte
            double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 20)
            {
                dx /= len; dy /= len;
                double sz = 6;
                DrawCanvas.Children.Add(new Polygon
                {
                    Points = new PointCollection
                    {
                        new(mx, my),
                        new(mx - dx * sz + dy * sz / 2, my - dy * sz - dx * sz / 2),
                        new(mx - dx * sz - dy * sz / 2, my - dy * sz + dx * sz / 2)
                    },
                    Fill = hi ? Brushes.OrangeRed : Brushes.DarkViolet,
                    IsHitTestVisible = false
                });
            }
        }

        // Punkte + Nummern
        for (int i = 0; i < _pfadPunkte.Count; i++)
        {
            var px = PunktToPx(_pfadPunkte[i]);
            bool sel  = i == selIdx;
            bool hov  = i == _pfadHoverIdx;
            double r  = (sel || hov) ? 7 : 5;

            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = i == 0 ? Brushes.LimeGreen : (hov ? Brushes.Orange : (sel ? Brushes.Gold : Brushes.DarkViolet)),
                Stroke = Brushes.White, StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, px.X - r);
            Canvas.SetTop(dot, px.Y - r);
            DrawCanvas.Children.Add(dot);

            var lbl = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = Brushes.DarkViolet,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, px.X + r + 2);
            Canvas.SetTop(lbl, px.Y - 8);
            DrawCanvas.Children.Add(lbl);

            // Hover-Pfeile
            if (hov)
                DrawHoverArrows(px, i);
        }
    }

    private void DrawHoverArrows(Point center, int punktIdx)
    {
        double dist = 24;   // px vom Mittelpunkt
        double sz   = 9;    // Pfeilgröße

        (double dx, double dy, string dir)[] dirs =
        [
            ( 0,   -dist, "U"),   // ↑
            ( 0,    dist, "D"),   // ↓
            (-dist, 0,    "L"),   // ←
            ( dist, 0,    "R"),   // →
        ];

        foreach (var (dx, dy, dir) in dirs)
        {
            var tip = new Point(center.X + dx, center.Y + dy);

            // Pfeil-Dreieck
            PointCollection pts = dir switch
            {
                "U" => [new(tip.X, tip.Y), new(tip.X - sz/2, tip.Y + sz), new(tip.X + sz/2, tip.Y + sz)],
                "D" => [new(tip.X, tip.Y), new(tip.X - sz/2, tip.Y - sz), new(tip.X + sz/2, tip.Y - sz)],
                "L" => [new(tip.X, tip.Y), new(tip.X + sz, tip.Y - sz/2), new(tip.X + sz, tip.Y + sz/2)],
                _   => [new(tip.X, tip.Y), new(tip.X - sz, tip.Y - sz/2), new(tip.X - sz, tip.Y + sz/2)],
            };

            // Transparenter Klickbereich (größer als der Pfeil)
            var hitArea = new Ellipse
            {
                Width = 22, Height = 22,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = $"{punktIdx}:{dir}"
            };
            hitArea.MouseLeftButtonDown += OnPfadArrowClick;
            Canvas.SetLeft(hitArea, tip.X - 11);
            Canvas.SetTop(hitArea,  tip.Y - 11);
            DrawCanvas.Children.Add(hitArea);

            var arrow = new Polygon
            {
                Points = pts,
                Fill = Brushes.DarkOrange,
                Stroke = Brushes.White, StrokeThickness = 1,
                Cursor = Cursors.Hand,
                Tag = $"{punktIdx}:{dir}"
            };
            arrow.MouseLeftButtonDown += OnPfadArrowClick;
            DrawCanvas.Children.Add(arrow);
        }
    }
#endif // DrawPfadFräsen + DrawHoverArrows Ende

    // ── Hilfsmethode ─────────────────────────────────────────────

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsGCodeTabActive())
            FlushGCodeBox();
    }

    private void OnGCodeAnzeigen(object sender, RoutedEventArgs e)
    {
        TabGCode.Visibility = MnuGCodeAnzeigen.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWerkzeugliste(object sender, RoutedEventArgs e)
    {
        var dlg = new WerkzeugeDialog(_werkzeuge, SaveWerkzeuge) { Owner = this };
        dlg.ShowDialog();
    }

    private bool _showFraesbreite = false;

    private void OnFraesbreiteAnzeigen(object sender, RoutedEventArgs e)
    {
        _showFraesbreite = MnuFraesbreite.IsChecked;
        UpdateAll();
    }

    private void OnVCarveVisualisierenChanged(object sender, RoutedEventArgs e)
    {
        _vCarveCache.Clear();
        _textGeoCache.Clear();
        _vCarvePending.Clear();
        UpdateAll();
    }

    private void LoadWerkzeuge()
    {
        _suppressSave = true;
        try
        {
            _werkzeuge.Clear();
            if (File.Exists(WerkzeugDatei))
            {
                var list = JsonSerializer.Deserialize<List<Werkzeug>>(File.ReadAllText(WerkzeugDatei));
                if (list != null)
                    foreach (var w in list)
                        _werkzeuge.Add(w);
            }
            if (_werkzeuge.Count == 0)
                _werkzeuge.Add(new Werkzeug
                {
                    Nr = 1, Name = "Werkzeug 1",
                    Durchmesser = 10, Schneidenwinkel = 180, ZZustellung = 4,
                    Eintauchwinkel = 20, VorschubFxy = 3000, VorschubFz = 2000,
                    Drehzahl = 18000, RaeumzustellungXY = 75,
                });
        }
        catch { /* korrupte Datei ignorieren */ }
        finally { _suppressSave = false; }
    }

    private void SaveWerkzeuge()
    {
        if (_suppressSave) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WerkzeugDatei)!);
            var json = JsonSerializer.Serialize(_werkzeuge.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(WerkzeugDatei, json);
        }
        catch { }
    }

    private void OnRasterEinblenden(object sender, RoutedEventArgs e)
    {
        if (MnuRaster.IsChecked)
        {
            var dlg = new RasterDialog(_rasterX, _rasterY) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _rasterX = dlg.RasterX;
                _rasterY = dlg.RasterY;
                _rasterEnabled = true;
            }
            else
            {
                MnuRaster.IsChecked = false;
                _rasterEnabled = false;
            }
        }
        else
        {
            _rasterEnabled = false;
        }
        UpdateAll();
    }

    // ── Nullpunkt-Fadenkreuz zeichnen ────────────────────────────
    private void DrawNullpunktCrosshairSk(SKCanvas canvas)
    {
        try
        {
            if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return;

            var (nullX, nullY) = GetNullpunktCoordinates();

            // Top-Ansicht (XY-Ebene)
            DrawCrosshairInRect(canvas, _topRect, nullX, nullY, WorkX, WorkY);

            // Seiten-Ansicht (XZ-Ebene) - Nullpunkt auch hier anzeigen (nur X-Achse)
            if (!_bottomRect.IsEmpty && WorkZ > 0 && _bottomRect.Width > 0 && _bottomRect.Height > 0)
            {
                // In der Seitenansicht ist Y die vertikale Achse, X die horizontale
                double scaleX = _bottomRect.Width / WorkX;
                double scaleZ = _bottomRect.Height / WorkZ;
                double scale = Math.Min(scaleX, scaleZ);

                float px = (float)(_bottomRect.Left + nullX * scale);
                float centerY = (float)(_bottomRect.Top + _bottomRect.Height / 2); // Vertikal zentriert

                using var paint = new SKPaint
                {
                    Color = new SKColor(255, 100, 0), // Orange
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2,
                    IsAntialias = true
                };

                // Vertikale Linie (für X-Position)
                canvas.DrawLine(px, (float)_bottomRect.Top, px, (float)(_bottomRect.Top + _bottomRect.Height), paint);
            }
        }
        catch
        {
            // Fehler bei Zeichnung ignorieren
        }
    }

    private void DrawCrosshairInRect(SKCanvas canvas, Rect rect, double x, double y, double maxX, double maxY)
    {
        try
        {
            if (rect.IsEmpty || maxX <= 0 || maxY <= 0 || rect.Width <= 0 || rect.Height <= 0) return;

            // Berechne Pixel-Koordinaten
            double scale = Math.Min(rect.Width / maxX, rect.Height / maxY);
            float px = (float)(rect.Left + x * scale);
            float py = (float)(rect.Top + (maxY - y) * scale); // Y invertieren (Bildschirm vs. Werkstück)

            using var paint = new SKPaint
            {
                Color = new SKColor(255, 100, 0), // Orange
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };

            float crossSize = 15; // Größe des Fadenkreuzes in Pixeln

            // Horizontale Linie
            canvas.DrawLine(px - crossSize, py, px + crossSize, py, paint);
            // Vertikale Linie
            canvas.DrawLine(px, py - crossSize, px, py + crossSize, paint);

            // Mittelpunkt (kleiner Kreis)
            using var circlePaint = new SKPaint
            {
                Color = new SKColor(255, 100, 0),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawCircle(px, py, 3, circlePaint);
        }
        catch
        {
            // Fehler bei Zeichnung ignorieren
        }
    }

    private void DrawRasterSk(SKCanvas canvas, double cw, double ch)
    {
        double scale = (!_topRect.IsEmpty && WorkX > 0 && WorkY > 0)
            ? Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY)
            : 1.0;

        double stepX = _rasterX * scale;
        double stepY = _rasterY * scale;
        if (stepX * _zoom < 2 || stepY * _zoom < 2) return;

        double worldMinX = -_panX / _zoom;
        double worldMaxX = (cw - _panX) / _zoom;
        double worldMinY = -_panY / _zoom;
        double worldMaxY = (ch - _panY) / _zoom;

        using var paint = new SKPaint
        {
            Color       = new SKColor(30, 90, 200, 55),
            StrokeWidth = (float)(1.0 / _zoom),
            IsAntialias = false,
            Style       = SKPaintStyle.Stroke,
        };

        // Rasterursprung am Werkstück (oder Weltorigon), erste sichtbare Linie per Floor
        double ox = _topRect.IsEmpty ? 0 : _topRect.Left;
        double oy = _topRect.IsEmpty ? 0 : _topRect.Bottom;

        double firstX = ox + Math.Floor((worldMinX - ox) / stepX) * stepX;
        for (double px = firstX; px <= worldMaxX; px += stepX)
            canvas.DrawLine((float)px, (float)worldMinY, (float)px, (float)worldMaxY, paint);

        double firstY = oy + Math.Floor((worldMinY - oy) / stepY) * stepY;
        for (double py = firstY; py <= worldMaxY; py += stepY)
            canvas.DrawLine((float)worldMinX, (float)py, (float)worldMaxX, (float)py, paint);
    }

    private void DrawSelectionLocatorSk(SKCanvas canvas)
    {
        const float VisibleThreshold = 6f;
        double wx = WorkX, wy = WorkY;
        if (wx <= 0 || wy <= 0) return;
        double scale = Math.Min(_topRect.Width / wx, _topRect.Height / wy);
        (float, float) MmToPxW(double x, double y) => (
            (float)(_topRect.Left + x * scale),
            (float)(_topRect.Bottom - y * scale));

        float? ptx = null, pty = null;
        bool showRing = false;

        var hole = _cachedDrillPoints.FirstOrDefault(h => h.LineNumber == _selectedGCodeLine);
        if (hole != null)
        {
            var (hx, hy) = MmToPxW(hole.X, hole.Y);
            ptx = hx; pty = hy;
        }
        else
        {
            int idx = _cachedTopMoves.FindIndex(m => m.LineNumber == _selectedGCodeLine);
            var move = idx >= 0 ? _cachedTopMoves[idx] : null;
            if (move != null)
            {
                if (move.Type is MoveType.ArcCW or MoveType.ArcCCW)
                {
                    double arPx = Math.Sqrt(move.I * move.I + move.J * move.J) * scale * _zoom;
                    showRing = arPx < VisibleThreshold;
                    var (mx, my) = MmToPxW((move.X + move.Xe) / 2.0, (move.Y + move.Ye) / 2.0);
                    ptx = mx; pty = my;
                }
                else
                {
                    var (sx0, sy0) = idx > 0
                        ? MmToPxW(_cachedTopMoves[idx - 1].X, _cachedTopMoves[idx - 1].Y)
                        : MmToPxW(0, 0);
                    var (ex, ey) = MmToPxW(move.X, move.Y);
                    double lenPx = Math.Sqrt(Math.Pow(ex - sx0, 2) + Math.Pow(ey - sy0, 2)) * _zoom;
                    showRing = lenPx < VisibleThreshold;
                    ptx = ex; pty = ey;
                }
            }
        }

        if (!showRing || ptx == null) return;

        // Welt → logische Screen-Pixel → physische Screen-Pixel (für ResetMatrix-Kontext)
        float sx = (float)((ptx.Value  * _zoom + _panX) * _dpiScale);
        float sy = (float)((pty!.Value * _zoom + _panY) * _dpiScale);

        int saveCount = canvas.Save();
        canvas.ResetMatrix();

        const float R    = 20f;
        const float tick = 7f;
        const float gap  = 4f;
        using var paint = new SKPaint
        {
            Color       = new SKColor(255, 215, 0),
            StrokeWidth = 1.8f,
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke,
            StrokeCap   = SKStrokeCap.Round,
        };
        canvas.DrawCircle(sx, sy, R, paint);
        paint.StrokeWidth = 1.5f;
        canvas.DrawLine(sx - R - gap - tick, sy,  sx - R - gap,        sy,  paint);
        canvas.DrawLine(sx + R + gap,        sy,  sx + R + gap + tick, sy,  paint);
        canvas.DrawLine(sx, sy - R - gap - tick,  sx, sy - R - gap,        paint);
        canvas.DrawLine(sx, sy + R + gap,         sx, sy + R + gap + tick, paint);

        canvas.RestoreToCount(saveCount);
    }

    // ── G-Code Simulation ───────────────────────────────────────────────────

    private void BuildSimPath()
    {
        _simSegs.Clear();
        _simTotalMm = 0;
        var moves = GCodeParser.ParseTopView(_gcodeContent);
        double prevX = 0, prevY = 0;

        foreach (var m in moves)
        {
            bool   rapid = m.Type == MoveType.Rapid;
            double feed  = rapid ? 3000 : (m.FeedRate > 0 ? m.FeedRate : 800);

            if (m.Type is MoveType.Rapid or MoveType.Line)
            {
                double dx = m.X - prevX, dy = m.Y - prevY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-9)
                {
                    _simSegs.Add(new SimSeg(prevX, prevY, m.X, m.Y, len, _simTotalMm,
                        rapid, feed, false, 0, 0, 0, 0, 0));
                    _simTotalMm += len;
                }
                prevX = m.X; prevY = m.Y;
            }
            else // Arc
            {
                double cx = m.X + m.I, cy = m.Y + m.J;
                double r  = Math.Sqrt(m.I * m.I + m.J * m.J);
                if (r > 1e-9)
                {
                    double a0 = Math.Atan2(m.Y - cy, m.X - cx);
                    double a1 = Math.Atan2(m.Ye - cy, m.Xe - cx);
                    bool   cw = m.Type == MoveType.ArcCW;
                    double da = a1 - a0;
                    bool full = Math.Abs(m.Xe - m.X) < 1e-6 && Math.Abs(m.Ye - m.Y) < 1e-6;
                    if (full) da = cw ? -2 * Math.PI : 2 * Math.PI;
                    else
                    {
                        if ( cw && da > 0) da -= 2 * Math.PI;
                        if (!cw && da < 0) da += 2 * Math.PI;
                    }
                    double len = Math.Abs(da) * r;
                    _simSegs.Add(new SimSeg(m.X, m.Y, m.Xe, m.Ye, len, _simTotalMm,
                        false, feed, true, cx, cy, r, a0, da));
                    _simTotalMm += len;
                }
                prevX = m.Xe; prevY = m.Ye;
            }
        }

        _simPathDirty = false;
        _simSliderBusy = true;
        SimSlider.Maximum = _simTotalMm > 0 ? _simTotalMm : 1;
        SimSlider.Value   = 0;
        _simSliderBusy = false;
        _simPosMm = 0;
        TxtSimPos.Text = "0 mm";
    }

    private (double x, double y, bool rapid, double feed) SimInterp(double posMm)
    {
        if (_simSegs.Count == 0) return (0, 0, false, 800);
        posMm = Math.Clamp(posMm, 0, _simTotalMm);

        int lo = 0, hi = _simSegs.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_simSegs[mid].CumStart <= posMm) lo = mid; else hi = mid - 1;
        }
        var s = _simSegs[lo];
        double t = s.Len > 1e-12 ? Math.Clamp((posMm - s.CumStart) / s.Len, 0, 1) : 1.0;

        double x, y;
        if (!s.IsArc)
        {
            x = s.X0 + t * (s.X1 - s.X0);
            y = s.Y0 + t * (s.Y1 - s.Y0);
        }
        else
        {
            double a = s.A0 + t * s.DA;
            x = s.Cx + s.R * Math.Cos(a);
            y = s.Cy + s.R * Math.Sin(a);
        }
        return (x, y, s.IsRapid, s.FeedMmMin);
    }

    private void DrawSimTool(double xMm, double yMm, bool rapid)
    {
        SimToolCanvas.Children.Clear();
        if (_topRect.IsEmpty || WorkX <= 0 || WorkY <= 0) return;

        double scale  = Math.Min(_topRect.Width / WorkX, _topRect.Height / WorkY);
        // DrawCanvas-lokale Koordinaten → Bildschirmkoordinaten (Zoom + Pan)
        double px     = (_topRect.Left   + xMm * scale) * _zoom + _panX;
        double py     = (_topRect.Bottom - yMm * scale) * _zoom + _panY;
        double rPx    = Math.Max(6, 2.0 * scale * _zoom);

        var color  = rapid ? Color.FromArgb(200, 80, 80, 255) : Color.FromArgb(200, 255, 100, 0);
        var stroke = new SolidColorBrush(color);
        var fill   = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));

        var circle = new System.Windows.Shapes.Ellipse
        {
            Width = rPx * 2, Height = rPx * 2,
            Stroke = stroke, StrokeThickness = 2,
            Fill   = fill
        };
        Canvas.SetLeft(circle, px - rPx);
        Canvas.SetTop(circle,  py - rPx);
        SimToolCanvas.Children.Add(circle);
    }

    private void OnSimPlay(object sender, RoutedEventArgs e)
    {
        if (_simPathDirty) BuildSimPath();
        if (_simTotalMm <= 0) return;

        if (_simPosMm >= _simTotalMm - 1e-6) _simPosMm = 0;

        _simPlaying = !_simPlaying;
        BtnSimPlay.Content = _simPlaying ? "⏸" : "▶";

        if (_simPlaying)
        {
            _simLastTick = DateTime.UtcNow;
            _simTimer.Start();
        }
        else _simTimer.Stop();
    }

    private void OnSimStop(object sender, RoutedEventArgs e)
    {
        _simPlaying = false;
        _simTimer.Stop();
        _simPosMm          = 0;
        BtnSimPlay.Content = "▶";
        _simSliderBusy     = true;
        SimSlider.Value    = 0;
        _simSliderBusy     = false;
        TxtSimPos.Text     = "0 mm";
        SimToolCanvas.Children.Clear();
    }

    private void OnSimSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_simSliderBusy) return;
        _simPosMm = SimSlider.Value;
        TxtSimPos.Text = $"{_simPosMm:F0} mm";
        var (x, y, rapid, _) = SimInterp(_simPosMm);
        DrawSimTool(x, y, rapid);
    }

    private void OnSimSliderMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_simPlaying)
        {
            _simPlaying = false;
            _simTimer.Stop();
            BtnSimPlay.Content = "▶";
        }
    }

    private void OnSimSliderMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) { }

    private void OnSimSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        _simSpeedMult = CbSimSpeed.SelectedIndex switch
        {
            1 => 2.0, 2 => 5.0, 3 => 10.0, _ => 1.0
        };
    }

    private void OnSimTick(object? sender, EventArgs e)
    {
        var now       = DateTime.UtcNow;
        double elMs   = Math.Min((now - _simLastTick).TotalMilliseconds, 100);
        _simLastTick  = now;

        var (_, _, rapid, feedMmMin) = SimInterp(_simPosMm);
        double effFeed = rapid ? Math.Max(feedMmMin, 3000) : Math.Max(feedMmMin, 100);
        double deltaMm = effFeed / 60000.0 * elMs * _simSpeedMult;

        _simPosMm = Math.Min(_simPosMm + deltaMm, _simTotalMm);

        _simSliderBusy  = true;
        SimSlider.Value = _simPosMm;
        _simSliderBusy  = false;
        TxtSimPos.Text  = $"{_simPosMm:F0} mm";

        var (x, y, isRapid, _) = SimInterp(_simPosMm);
        DrawSimTool(x, y, isRapid);

        if (_simPosMm >= _simTotalMm)
        {
            _simPlaying        = false;
            _simTimer.Stop();
            BtnSimPlay.Content = "▶";
        }
    }

    private void OnOpenTextEditorRendering(object sender, RoutedEventArgs e)
    {
        // Text-zu-Liniensegmente wird jetzt direkt im Textfeld-Werkzeug (VCarveTextSk) verwendet
        MessageBox.Show("Die Text-zu-Liniensegmente-Konvertierung ist jetzt im Textfeld-Werkzeug integriert.\n\n" +
                        "Verwenden Sie das VCarveTextSk-Werkzeug zum Konvertieren von Text.", "Info");
    }

    /// <summary>
    /// Konvertiert den aktuellen Text zu Liniensegmenten und zeichnet sie auf der Canvas
    /// </summary>
    private void OnConvertTextToLineSegments(object sender, RoutedEventArgs e)
    {
        if (_inlineParams == null || string.IsNullOrWhiteSpace(_inlineParams.Text))
        {
            MessageBox.Show("Das Textfeld ist leer. Bitte geben Sie Text ein.", "Info");
            _inlineTextLineSegments = null;
            return;
        }

        try
        {
            TbTextConversionInfo.Height = double.NaN;
            TbTextConversionInfo.Text = "Konvertiere...";
            TbTextConversionInfo.Foreground = new SolidColorBrush(Color.FromRgb(100, 150, 255));

            var gp = _inlineParams;

            // Konvertiere Text zu Liniensegmenten
            // DEBUG: Überprüfe alle Positions-Variablen
            float fontSizeMm = (float)gp.FontSizeMm;

            System.Diagnostics.Debug.WriteLine($"=== Text Konvertierung ===");
            System.Diagnostics.Debug.WriteLine($"Text: '{gp.Text}'");
            System.Diagnostics.Debug.WriteLine($"gp.XRel: {gp.XRel}, gp.YRel: {gp.YRel}");
            System.Diagnostics.Debug.WriteLine($"gp.TextBreite: {gp.TextBreite}");
            System.Diagnostics.Debug.WriteLine($"gp.Ausrichtung: {gp.Ausrichtung}");
            System.Diagnostics.Debug.WriteLine($"gp.Bezugspunkt: {gp.Bezugspunkt}");
            System.Diagnostics.Debug.WriteLine($"FontSize: {fontSizeMm}mm");

            float posX = (float)gp.XRel;
            float posY = (float)gp.YRel;

            // Verwende die exakte Position des Textfeldes für die Konvertierung
            var geometries = TextToLineSegments.ConvertTextToLineSegments(
                text: gp.Text,
                fontFamily: gp.FontFamily ?? "Segoe UI",
                fontSize: fontSizeMm,
                startX: posX,  // Exakte X-Position des Textfeldes
                startY: posY,  // Exakte Y-Position des Textfeldes
                tolerance: 0.3f
            );

            var allSegments = TextToLineSegments.ExtractAllLineSegments(geometries);

            if (allSegments.Count == 0)
            {
                TbTextConversionInfo.Text = "✗ Keine Liniensegmente generiert";
                TbTextConversionInfo.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                _inlineTextLineSegments = null;
                return;
            }

            // Speichere Liniensegmente für Canvas-Zeichnung
            _inlineTextLineSegments = allSegments;

            // Info anzeigen
            TbTextConversionInfo.Text = $"✓ Konvertiert! {geometries.Count} Zeichen, {allSegments.Count} Liniensegmente";
            TbTextConversionInfo.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100));

            System.Diagnostics.Debug.WriteLine($"Text zu Liniensegmenten: {geometries.Count} Zeichen → {allSegments.Count} Segmente");

            // Berechne Bounding Box der Segmente
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var seg in allSegments)
            {
                minX = Math.Min(minX, Math.Min(seg.X1, seg.X2));
                maxX = Math.Max(maxX, Math.Max(seg.X1, seg.X2));
                minY = Math.Min(minY, Math.Min(seg.Y1, seg.Y2));
                maxY = Math.Max(maxY, Math.Max(seg.Y1, seg.Y2));
            }
            float width = maxX - minX;
            float height = maxY - minY;
            System.Diagnostics.Debug.WriteLine($"Bounding Box: X=[{minX:F2}..{maxX:F2}] Y=[{minY:F2}..{maxY:F2}] Größe={width:F2}x{height:F2}");
            System.Diagnostics.Debug.WriteLine($"Erste 3 Segmente:");
            for (int i = 0; i < Math.Min(3, allSegments.Count); i++)
            {
                var seg = allSegments[i];
                System.Diagnostics.Debug.WriteLine($"  [{i}] ({seg.X1:F2},{seg.Y1:F2}) → ({seg.X2:F2},{seg.Y2:F2})");
            }

            // Aktualisiere Canvas zum Zeichnen der Liniensegmente
            DrawSkia?.InvalidateVisual();
        }
        catch (Exception ex)
        {
            TbTextConversionInfo.Height = double.NaN;
            TbTextConversionInfo.Text = $"✗ Fehler: {ex.Message}";
            TbTextConversionInfo.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
            _inlineTextLineSegments = null;
            System.Diagnostics.Debug.WriteLine($"ERROR in OnConvertTextToLineSegments: {ex}");
        }
    }

    /// <summary>
    /// Zeichnet die konvertierten Text-Liniensegmente auf der Canvas
    /// </summary>
    private void DrawTextLineSegmentsSk(SKCanvas canvas)
    {
        if (_inlineTextLineSegments == null || _inlineTextLineSegments.Count == 0)
            return;

        using var paint = new SKPaint
        {
            Color = SKColors.LimeGreen,      // Grüne Linien für Schrift
            StrokeWidth = 2.0f,              // Dickere Linien für Sichtbarkeit
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // Debug: Erste 5 Segmente ausgeben
        System.Diagnostics.Debug.WriteLine($"DrawTextLineSegmentsSk: {_inlineTextLineSegments.Count} Segmente vorhanden");
        for (int i = 0; i < Math.Min(5, _inlineTextLineSegments.Count); i++)
        {
            var seg = _inlineTextLineSegments[i];
            System.Diagnostics.Debug.WriteLine($"  Segment {i}: ({seg.X1:F2},{seg.Y1:F2}) → ({seg.X2:F2},{seg.Y2:F2})");
        }

        // Zeichne alle Liniensegmente
        int drawnCount = 0;
        foreach (var segment in _inlineTextLineSegments)
        {
            canvas.DrawLine(segment.X1, segment.Y1, segment.X2, segment.Y2, paint);
            drawnCount++;
        }

        System.Diagnostics.Debug.WriteLine($"DrawTextLineSegmentsSk: {drawnCount} Segmente gezeichnet");
    }

    /// <summary>
    /// Löscht die angezeigten Liniensegmente
    /// </summary>
    private void OnClearLineSegments(object sender, RoutedEventArgs e)
    {
        _inlineTextLineSegments = null;
        TbTextConversionInfo.Text = "Liniensegmente gelöscht";
        TbTextConversionInfo.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        TbTextConversionInfo.Height = double.NaN;
        DrawSkia?.InvalidateVisual();
        System.Diagnostics.Debug.WriteLine("Liniensegmente gelöscht");
    }
}

public sealed class GCodeColorizer : DocumentColorizingTransformer
{
    private static Brush Fb(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static readonly Brush BrRapid   = Fb(150, 150, 150); // G00
    private static readonly Brush BrCut     = Fb( 26,  26,  26); // G01
    private static readonly Brush BrArc     = Fb( 10,  80, 180); // G02/G03
    private static readonly Brush BrMCode   = Fb(160,  80,   0); // M
    private static readonly Brush BrComment = Fb( 60, 130,  60); // Kommentar

    private static Brush LineColor(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0) return BrCut;
        char c = char.ToUpperInvariant(t[0]);
        if (c == ';' || c == '(') return BrComment;
        if (c == 'M')             return BrMCode;
        if (c != 'G' || t.Length < 2) return BrCut;
        char d = t[1];
        if (d == '0' && (t.Length == 2 || !char.IsDigit(t[2]))) return BrRapid;
        if (d == '0' && t.Length > 2 && t[2] == '0')            return BrRapid;
        if ((d == '2' || d == '3') && (t.Length == 2 || !char.IsDigit(t[2]))) return BrArc;
        if (t.Length > 2 && (t[2] == '2' || t[2] == '3') && (t.Length == 3 || !char.IsDigit(t[3])) && d == '0') return BrArc;
        return BrCut;
    }

    protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        var text  = CurrentContext.Document.GetText(line);
        var brush = LineColor(text);
        ChangeLinePart(line.Offset, line.EndOffset, el =>
            el.TextRunProperties.SetForegroundBrush(brush));
    }
}

// Zeilenhintergrund: G00-Zeilen leicht grau, M30-Zeile zartes Rot
public sealed class GCodeLineBackgroundRenderer : IBackgroundRenderer
{
    private static Brush MkBg(byte r, byte g, byte b, byte a)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    private static readonly Brush BgG00  = MkBg(  0,   0,   0,   0); // kein Hintergrund für G00
    private static readonly Brush BgM30  = MkBg(220, 100,   0,  25); // zartes Orange für M-Ende
    private static readonly Brush BgSel  = MkBg(255, 210,   0, 140); // kräftiges Gold für Werkstück-Selektion
    private static readonly Brush BgHov  = MkBg(255, 230,  60,  70); // helleres Gold für Hover
    private static readonly Brush BgHist = MkBg( 80, 160, 255,  55); // zartes Blau für Verlauf-Selektion
    private static readonly Pen   PenSel;

    static GCodeLineBackgroundRenderer()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 180, 0)), 1.5);
        pen.Freeze();
        PenSel = pen;
    }

    private static readonly Regex RxG00 = new(@"\bG0*0\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxM30 = new(@"\bM30\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Von Werkstück-Klick gesetzte selektierte Zeile (1-basiert, -1 = keine)</summary>
    public int SelectedLine   { get; set; } = -1;
    /// <summary>Maus-Hover-Zeile im Editor (1-basiert, -1 = keine)</summary>
    public int HoverLine      { get; set; } = -1;
    /// <summary>Verlauf-Selektion: erster hervorgehobener Zeilenbereich (1-basiert, -1 = keine)</summary>
    public int HistRangeStart { get; set; } = -1;
    /// <summary>Verlauf-Selektion: letzter hervorgehobener Zeilenbereich (1-basiert, inklusiv)</summary>
    public int HistRangeEnd   { get; set; } = -1;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView?.Document is null) return;
        textView.EnsureVisualLines();
        foreach (var vl in textView.VisualLines)
        {
            var docLine = vl.FirstDocumentLine;
            var text    = textView.Document.GetText(docLine);

            // Werkstück-Selektion hat Vorrang (überschreibt G00/M30-Farbe)
            if (docLine.LineNumber == SelectedLine)
            {
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
                    drawingContext.DrawRectangle(BgSel, PenSel,
                        new Rect(0, rect.Y, textView.ActualWidth, rect.Height));
                continue;
            }
            // Hover-Zeile (Maus im Editor)
            if (docLine.LineNumber == HoverLine)
            {
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
                    drawingContext.DrawRectangle(BgHov, null,
                        new Rect(0, rect.Y, textView.ActualWidth, rect.Height));
                continue;
            }

            bool inHistRange = HistRangeStart > 0
                && docLine.LineNumber >= HistRangeStart
                && docLine.LineNumber <= HistRangeEnd;

            Brush? bg = null;
            if (inHistRange)      bg = BgHist;
            if (RxG00.IsMatch(text) && bg is null) bg = BgG00;
            if (RxM30.IsMatch(text)) bg = BgM30;  // M30 überschreibt G00
            if (bg is null) continue;

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
                drawingContext.DrawRectangle(bg, null,
                    new Rect(0, rect.Y, textView.ActualWidth, rect.Height));
        }
    }
}

// Richtungspfeil-Spalte: zeigt pro Zeile die Bewegungsrichtung des Fräskopfes
// Richtung = Vektor vom Standort der Vorzeile zum Ziel der aktuellen Zeile
public sealed class GCodeArrowMargin : AbstractMargin
{
    private const double ColW = 34;

    private List<LineData>? _cachedLines;  // wird in OnRender befüllt
    private ToolTip?        _tip;
    private int             _lastTipLine = -1;

    public GCodeArrowMargin()
    {
        Width      = ColW;
        MouseMove  += OnMarginMouseMove;
        MouseLeave += OnMarginMouseLeave;
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null)
        {
            oldTextView.VisualLinesChanged    -= OnViewChanged;
            oldTextView.ScrollOffsetChanged   -= OnViewChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
        {
            newTextView.VisualLinesChanged    += OnViewChanged;
            newTextView.ScrollOffsetChanged   += OnViewChanged;
        }
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(ColW, 0);

    // ── Zeichenressourcen ─────────────────────────────────────────────────────
    private static Pen MkPen(byte r, byte g, byte b, double thick, bool dash = false)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), thick);
        if (dash) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }
    private static Brush MkBr(byte r, byte g, byte b)
    { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    // Eilgang: dünner gestrichelter grauer Pfeil
    private static readonly Pen   PenRapid  = MkPen(170, 170, 170, 1.2, dash: true);
    private static readonly Brush BrRapid   = MkBr(170, 170, 170);
    // Linearbewegung: kräftiger schwarzer Pfeil
    private static readonly Pen   PenLinear = MkPen( 30,  30,  30, 1.8);
    private static readonly Brush BrLinear  = MkBr( 30,  30,  30);
    // Bogen: blau
    private static readonly Brush BrArc     = MkBr( 30,  90, 210);
    // M-Codes: rot
    private static readonly Brush BrM       = MkBr(200,  20,  20);
    // Hintergrund der Spalte (passend zu AvalonEdit-Zeilennummer-Farbe)
    private static readonly Brush BgCol     = MkBr(240, 240, 236);
    private static readonly Pen   PenSep    = new(MkBr(200, 200, 195), 1) { };

    private static readonly Typeface Tf = new("Segoe UI Symbol");

    // ── G-Code Mini-Interpreter ───────────────────────────────────────────────
    private static readonly Regex RxG = new(@"(?<![.\d])G(\d+(?:\.\d+)?)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxM = new(@"(?<![.\d])M(\d+)(?!\d)",            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxX = new(@"(?<![A-Za-z])X([+-]?[\d.]+)",       RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxY = new(@"(?<![A-Za-z])Y([+-]?[\d.]+)",       RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxZ = new(@"(?<![A-Za-z])Z([+-]?[\d.]+)",       RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum GCmd { None, Rapid, Linear, ArcCW, ArcCCW, MCode, End }

    // DX/DY/DZ = Differenz vom vorherigen Standort zum Ziel dieser Zeile
    private record LineData(GCmd Cmd, double DX, double DY, double DZ);

    private static List<LineData> ParseDocument(ICSharpCode.AvalonEdit.Document.TextDocument doc)
    {
        var inv    = System.Globalization.CultureInfo.InvariantCulture;
        var result = new List<LineData>(doc.LineCount);
        double x = 0, y = 0, z = 0;
        bool abs   = true;
        int  modal = 1; // modaler G-Code (Standard: G01)

        for (int ln = 1; ln <= doc.LineCount; ln++)
        {
            var seg  = doc.GetLineByNumber(ln);
            var text = doc.GetText(seg).Trim();

            if (string.IsNullOrEmpty(text) || text.StartsWith(";") || text.StartsWith("("))
            { result.Add(new LineData(GCmd.None, 0, 0, 0)); continue; }

            int  gMode   = modal;
            bool hasMove = false;
            bool hasMEnd = false;
            bool hasM    = false;

            foreach (Match mg in RxG.Matches(text))
            {
                if (!double.TryParse(mg.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out double gn)) continue;
                switch ((int)gn)
                {
                    case 0:  gMode = 0; hasMove = true; break;
                    case 1:  gMode = 1; hasMove = true; break;
                    case 2:  gMode = 2; hasMove = true; break;
                    case 3:  gMode = 3; hasMove = true; break;
                    case 90: abs = true;  break;
                    case 91: abs = false; break;
                }
            }
            modal = gMode;

            foreach (Match mm in RxM.Matches(text))
            {
                hasM = true;
                if (int.TryParse(mm.Groups[1].Value, out int mn) && mn == 30) hasMEnd = true;
            }

            // Zielkoordinaten dieser Zeile ermitteln
            double nx = x, ny = y, nz = z;
            var mx = RxX.Match(text); var my = RxY.Match(text); var mz = RxZ.Match(text);
            if (mx.Success && double.TryParse(mx.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out double vx))
            { nx = abs ? vx : x + vx; hasMove = true; }
            if (my.Success && double.TryParse(my.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out double vy))
            { ny = abs ? vy : y + vy; hasMove = true; }
            if (mz.Success && double.TryParse(mz.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out double vz))
            { nz = abs ? vz : z + vz; hasMove = true; }

            // Richtungsvektor: Standort (x,y,z) → Ziel (nx,ny,nz)
            double dx = nx - x, dy = ny - y, dz = nz - z;
            x = nx; y = ny; z = nz;

            GCmd cmd = GCmd.None;
            if      (hasMEnd)  cmd = GCmd.End;
            else if (hasMove)  cmd = gMode switch { 0 => GCmd.Rapid, 2 => GCmd.ArcCW, 3 => GCmd.ArcCCW, _ => GCmd.Linear };
            else if (hasM)     cmd = GCmd.MCode;

            result.Add(new LineData(cmd, dx, dy, dz));
        }
        return result;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────
    protected override void OnRender(DrawingContext dc)
    {
        var tv = TextView;
        if (tv is null) return;

        dc.DrawRectangle(BgCol, null, new Rect(0, 0, ColW, ActualHeight));
        dc.DrawLine(PenSep, new Point(ColW - 0.5, 0), new Point(ColW - 0.5, ActualHeight));

        var doc = tv.Document;
        if (doc is null) return;

        double ppd   = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var    lines = ParseDocument(doc);
        _cachedLines = lines;            // für MouseMove-Tooltip merken
        double cx    = ColW / 2.0;

        foreach (var vl in tv.VisualLines)
        {
            int idx = vl.FirstDocumentLine.LineNumber - 1;
            if (idx < 0 || idx >= lines.Count) continue;

            double top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - tv.VerticalOffset;
            double h   = vl.Height;
            double cy  = top + h / 2;

            // Pfeilgröße: gut sichtbar, aber nicht zu wuchtig
            double sz = Math.Clamp(h * 0.60, 7, 13);

            DrawSymbol(dc, lines[idx], cx, cy, sz, ppd);
        }
    }

    private static void DrawSymbol(DrawingContext dc, LineData ld, double cx, double cy, double sz, double ppd)
    {
        switch (ld.Cmd)
        {
            case GCmd.Rapid:
                // Eilgang: gestrichelter grauer Pfeil in Richtung des Deltas
                DrawArrow(dc, PenRapid, BrRapid, cx, cy, sz, ld.DX, ld.DY, ld.DZ);
                break;
            case GCmd.Linear:
                // Linearbewegung: kräftiger schwarzer Richtungspfeil
                DrawArrow(dc, PenLinear, BrLinear, cx, cy, sz, ld.DX, ld.DY, ld.DZ);
                break;
            case GCmd.ArcCW:
                DrawGlyph(dc, "↻", cx, cy, sz * 1.5, BrArc, ppd);
                break;
            case GCmd.ArcCCW:
                DrawGlyph(dc, "↺", cx, cy, sz * 1.5, BrArc, ppd);
                break;
            case GCmd.MCode:
                DrawGlyph(dc, "●", cx, cy, sz * 1.1, BrM, ppd);
                break;
            case GCmd.End:
                DrawGlyph(dc, "■", cx, cy, sz * 1.1, BrM, ppd);
                break;
        }
    }

    // Richtungspfeil: zeigt vom alten Standort (Vorzeile) zum neuen Ziel (diese Zeile).
    // dx/dy/dz = Differenz im Maschinen-Koordinatensystem.
    // WPF-Y ist invertiert zu CNC-Y (positive CNC-Y = Bildschirm-oben).
    private static void DrawArrow(DrawingContext dc, Pen pen, Brush headBrush,
        double cx, double cy, double sz, double dx, double dy, double dz)
    {
        // Winkel im WPF-Koordinatensystem: dy negieren, weil Y-Achse invertiert
        double angle;
        if (Math.Abs(dx) > 0.001 || Math.Abs(dy) > 0.001)
            angle = Math.Atan2(-dy, dx);
        else if (Math.Abs(dz) > 0.001)
            angle = dz < 0 ? Math.PI / 2 : -Math.PI / 2; // Z-: eintauchen (↓); Z+: abheben (↑)
        else
            return; // kein Delta → kein Pfeil

        // Pfeilschaft
        double half = sz * 0.68;
        double x0 = cx - Math.Cos(angle) * half;
        double y0 = cy - Math.Sin(angle) * half;
        double x1 = cx + Math.Cos(angle) * half;
        double y1 = cy + Math.Sin(angle) * half;
        dc.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));

        // Pfeilspitze (gefülltes Dreieck)
        double hl = sz * 0.58;   // Länge der Spitze
        double hw = 0.58;        // halber Öffnungswinkel (rad)
        double a1 = angle + Math.PI - hw;
        double a2 = angle + Math.PI + hw;
        var tip = new Point(x1, y1);
        var p1  = new Point(x1 + Math.Cos(a1) * hl, y1 + Math.Sin(a1) * hl);
        var p2  = new Point(x1 + Math.Cos(a2) * hl, y1 + Math.Sin(a2) * hl);

        var fig = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(p1, true));
        fig.Segments.Add(new LineSegment(p2, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        dc.DrawGeometry(headBrush, null, geo);
    }

    private static void DrawGlyph(DrawingContext dc, string glyph, double cx, double cy,
        double sz, Brush brush, double ppd)
    {
        var ft = new FormattedText(glyph,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Tf, sz, brush, ppd);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────
    private void OnMarginMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var tv = TextView;
        if (tv is null || _cachedLines is null) return;

        double mouseY = e.GetPosition(this).Y;
        LineData? ld  = null;
        int       ln  = 0;
        foreach (var vl in tv.VisualLines)
        {
            double top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop)
                         - tv.VerticalOffset;
            if (mouseY >= top && mouseY < top + vl.Height)
            {
                int idx = vl.FirstDocumentLine.LineNumber - 1;
                if (idx >= 0 && idx < _cachedLines.Count)
                { ld = _cachedLines[idx]; ln = idx + 1; }
                break;
            }
        }

        if (ld is null || ld.Cmd == GCmd.None)
        { if (_tip is not null) _tip.IsOpen = false; return; }

        // Inhalt nur neu aufbauen wenn sich die Zeile geändert hat
        if (_tip is null)
        {
            _tip = new ToolTip
            {
                Placement       = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow   = false,
                Padding         = new Thickness(0)
            };
        }
        if (ln != _lastTipLine)
        {
            _tip.Content  = BuildArrowTooltip(ld, ln);
            _lastTipLine  = ln;
        }
        _tip.IsOpen = true;
    }

    private void OnMarginMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tip is not null) _tip.IsOpen = false;
        _lastTipLine = -1;
    }

    private static UIElement BuildArrowTooltip(LineData ld, int lineNum)
    {
        // ── Pfeil-Canvas (linke Hälfte) ──────────────────────────────────────
        const double C = 80;
        var canvas = new Canvas
        {
            Width      = C,
            Height     = C,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 34))
        };

        bool xyMove = Math.Abs(ld.DX) > 0.001 || Math.Abs(ld.DY) > 0.001;
        bool zMove  = Math.Abs(ld.DZ) > 0.001;

        if (ld.Cmd is GCmd.Rapid or GCmd.Linear)
        {
            double angle;
            if (xyMove)     angle = Math.Atan2(-ld.DY, ld.DX);
            else if (zMove) angle = ld.DZ < 0 ? Math.PI / 2 : -Math.PI / 2;
            else            angle = 0;

            double cx = C / 2, cy = C / 2, half = C * 0.34;
            double x0 = cx - Math.Cos(angle) * half, y0 = cy - Math.Sin(angle) * half;
            double x1 = cx + Math.Cos(angle) * half, y1 = cy + Math.Sin(angle) * half;

            bool   isRapid = ld.Cmd == GCmd.Rapid;
            var    col     = isRapid ? Color.FromRgb(150, 150, 150) : Color.FromRgb(230, 230, 225);
            var    stroke  = new SolidColorBrush(col);

            // Pfeilschaft
            var shaft = new System.Windows.Shapes.Line
            {
                X1 = x0, Y1 = y0, X2 = x1, Y2 = y1,
                Stroke          = stroke,
                StrokeThickness = isRapid ? 1.8 : 2.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round
            };
            if (isRapid) shaft.StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 };
            canvas.Children.Add(shaft);

            // Startpunkt
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6, Fill = stroke
            };
            Canvas.SetLeft(dot, x0 - 3); Canvas.SetTop(dot, y0 - 3);
            canvas.Children.Add(dot);

            // Pfeilspitze
            double hl = C * 0.17, hw = 0.44;
            var head = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection
                {
                    new(x1, y1),
                    new(x1 + Math.Cos(angle + Math.PI - hw) * hl,
                        y1 + Math.Sin(angle + Math.PI - hw) * hl),
                    new(x1 + Math.Cos(angle + Math.PI + hw) * hl,
                        y1 + Math.Sin(angle + Math.PI + hw) * hl)
                },
                Fill = stroke
            };
            canvas.Children.Add(head);

            // Koordinatenkreuz — volle Breite, deutlich sichtbar
            var dimBrush = new SolidColorBrush(Color.FromArgb(110, 140, 140, 170));
            canvas.Children.Add(new System.Windows.Shapes.Line
            { X1=2, Y1=C/2, X2=C-2, Y2=C/2, Stroke=dimBrush, StrokeThickness=1.0 });
            canvas.Children.Add(new System.Windows.Shapes.Line
            { X1=C/2, Y1=2, X2=C/2, Y2=C-2, Stroke=dimBrush, StrokeThickness=1.0 });
            // Achsenbeschriftung
            var axFont = new System.Windows.Media.FontFamily("Segoe UI");
            var lblX = new TextBlock { Text="X", FontSize=9, Foreground=dimBrush, FontFamily=axFont };
            Canvas.SetLeft(lblX, C-10); Canvas.SetTop(lblX, C/2+2); canvas.Children.Add(lblX);
            var lblY = new TextBlock { Text="Y", FontSize=9, Foreground=dimBrush, FontFamily=axFont };
            Canvas.SetLeft(lblY, C/2+3); Canvas.SetTop(lblY, 2); canvas.Children.Add(lblY);
        }
        else
        {
            // Bogensymbol oder M-Code
            var sym = ld.Cmd switch
            {
                GCmd.ArcCW  => "↻",
                GCmd.ArcCCW => "↺",
                GCmd.End    => "■",
                _           => "●"
            };
            var symColor = ld.Cmd is GCmd.ArcCW or GCmd.ArcCCW
                ? Color.FromRgb(80, 140, 230)
                : Color.FromRgb(210, 50, 50);
            var tb = new TextBlock
            {
                Text       = sym,
                FontSize   = 36,
                Foreground = new SolidColorBrush(symColor)
            };
            Canvas.SetLeft(tb, C / 2 - 20); Canvas.SetTop(tb, C / 2 - 26);
            canvas.Children.Add(tb);
        }

        // ── Infotext (rechte Hälfte) ──────────────────────────────────────────
        // Z-only G01: Eintauchen oder Herausziehen, kein XY-Vorschub
        bool zOnly = ld.Cmd == GCmd.Linear && !xyMove && zMove;

        var (cmdTitle, cmdDesc) = ld.Cmd switch
        {
            GCmd.Rapid  => ("G00 — Eilgang",
                            "Schnelle Positionierfahrt,\nkein Materialabtrag."),
            GCmd.Linear when zOnly && ld.DZ < 0
                        => ("G01 — Eintauchen ↓",
                            "Werkzeug senkrecht in\ndas Material eintauchen."),
            GCmd.Linear when zOnly && ld.DZ > 0
                        => ("G01 — Herausziehen ↑",
                            "Werkzeug senkrecht aus\ndem Material herausziehen."),
            GCmd.Linear => ("G01 — Linearbewegung",
                            "Gerades Fräsen entlang\ndes Richtungsvektors."),
            GCmd.ArcCW  => ("G02 — Bogen ↻",
                            "Kreisbogen im\nUhrzeigersinn."),
            GCmd.ArcCCW => ("G03 — Bogen ↺",
                            "Kreisbogen gegen\nden Uhrzeigersinn."),
            GCmd.MCode  => ("M-Code",
                            "Maschinenfunktion\n(Spindel / Kühlmittel …)"),
            GCmd.End    => ("M30 — Programmende",
                            "Programm beendet,\nRücksprung zum Anfang."),
            _           => ("", "")
        };

        var info = new StackPanel { Margin = new Thickness(10, 8, 12, 9), MinWidth = 160 };

        info.Children.Add(new TextBlock
        {
            Text       = $"Zeile {lineNum}",
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 125)),
            Margin     = new Thickness(0, 0, 0, 2)
        });
        info.Children.Add(new TextBlock
        {
            Text       = cmdTitle,
            FontWeight = FontWeights.Bold,
            FontSize   = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 70)),
            Margin     = new Thickness(0, 0, 0, 4)
        });
        info.Children.Add(new TextBlock
        {
            Text         = cmdDesc,
            FontSize     = 11,
            Foreground   = new SolidColorBrush(Color.FromRgb(195, 195, 190)),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6)
        });

        // Delta-Werte
        if (ld.Cmd is GCmd.Rapid or GCmd.Linear)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            void AddDelta(string lbl, double v)
            {
                if (Math.Abs(v) < 0.001) return;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
                row.Children.Add(new TextBlock
                {
                    Text = lbl, Width = 28, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 125))
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"{v:+0.###;-0.###} mm",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 200))
                });
                info.Children.Add(row);
            }
            AddDelta("ΔX:", ld.DX);
            AddDelta("ΔY:", ld.DY);
            AddDelta("ΔZ:", ld.DZ);

            double dist = Math.Sqrt(ld.DX * ld.DX + ld.DY * ld.DY);
            if (dist > 0.001)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                row.Children.Add(new TextBlock
                {
                    Text = "↔", Width = 28, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 125))
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"{dist:0.###} mm",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 180, 210))
                });
                info.Children.Add(row);
            }

            // Kompassrichtung
            if (xyMove)
            {
                double deg = Math.Atan2(-ld.DY, ld.DX) * 180 / Math.PI;
                if (deg < 0) deg += 360;
                string compass = deg switch
                {
                    >= 337.5 or < 22.5   => "→  Ost",
                    >= 22.5  and < 67.5  => "↗  Nordost",
                    >= 67.5  and < 112.5 => "↑  Nord",
                    >= 112.5 and < 157.5 => "↖  Nordwest",
                    >= 157.5 and < 202.5 => "←  West",
                    >= 202.5 and < 247.5 => "↙  Südwest",
                    >= 247.5 and < 292.5 => "↓  Süd",
                    _                    => "↘  Südost"
                };
                info.Children.Add(new TextBlock
                {
                    Text     = $"{compass}  ({deg:0}°)",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 170, 200)),
                    Margin   = new Thickness(0, 4, 0, 0)
                });
            }
        }

        // Canvas + Text nebeneinander
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(canvas);
        content.Children.Add(info);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(32, 32, 38)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(100, 100, 115)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(7),
            Child           = content,
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, Opacity = 0.6, BlurRadius = 10, ShadowDepth = 4
            }
        };
    }
}

public static class GCodeTooltip
{
    private static readonly Regex TokenRx = new(
        @"(?<c>;[^\r\n]*|\([^)]*\))|(?<g>G\d+(?:\.\d+)?)|(?<m>M\d+)|(?<f>F[+-]?[\d.]+)|(?<s>S[+-]?[\d.]+)|(?<xyz>[XYZ][+-]?[\d.]+)|(?<ijk>[IJK][+-]?[\d.]+)|(?<ln>N\d+)|(?<other>[A-EHLO-RT-W][+-]?[\d.]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Gibt (Titel, Beschreibung) für das Token an Position col zurück.</summary>
    public static (string? Title, string? Desc) GetTooltip(string lineText, int col)
    {
        foreach (Match m in TokenRx.Matches(lineText))
        {
            if (col >= m.Index && col < m.Index + m.Length)
                return Explain(m);
        }
        return (null, null);
    }

    private static (string? Title, string? Desc) Explain(Match m)
    {
        if (m.Groups["c"].Success)
            return ("Kommentar", "Wird von der Maschine vollständig ignoriert.");
        if (m.Groups["g"].Success) return ExplainG(m.Value);
        if (m.Groups["m"].Success) return ExplainM(m.Value);
        if (m.Groups["f"].Success)
            return ("F — Vorschubgeschwindigkeit", $"Werkzeugbewegung mit {m.Value[1..]} mm/min.");
        if (m.Groups["s"].Success)
            return ("S — Spindeldrehzahl", $"Spindel dreht mit {m.Value[1..]} U/min.");
        if (m.Groups["xyz"].Success) return ExplainXYZ(m.Value);
        if (m.Groups["ijk"].Success) return ExplainIJK(m.Value);
        if (m.Groups["ln"].Success)
            return ("N — Zeilennummer", $"Zeilennummer {m.Value[1..]}. Dient der Orientierung im Programm.");
        return (null, null);
    }

    private static (string, string) ExplainG(string tok)
    {
        return tok.ToUpperInvariant() switch
        {
            "G0"  or "G00" => ("G00 — Eilgang",
                "Schnelle Positionierfahrt ohne Fräsen. Das Werkzeug bewegt sich mit maximaler Geschwindigkeit zum Zielpunkt."),
            "G1"  or "G01" => ("G01 — Linearbewegung",
                "Gerades Fräsen mit dem eingestellten Vorschub F. Erzeugt eine gerade Linie im Material."),
            "G2"  or "G02" => ("G02 — Kreisbogen (Uhrzeigersinn)",
                "Fräst einen Kreisbogen im Uhrzeigersinn. Mittelpunkt wird mit I/J angegeben."),
            "G3"  or "G03" => ("G03 — Kreisbogen (Gegenuhrzeigersinn)",
                "Fräst einen Kreisbogen gegen den Uhrzeigersinn. Mittelpunkt wird mit I/J angegeben."),
            "G17"          => ("G17 — XY-Ebene",
                "Wählt die XY-Ebene als aktive Arbeitsebene. Standardeinstellung beim Fräsen."),
            "G20"          => ("G20 — Maßeinheit: Zoll",
                "Alle Koordinaten werden in Zoll (inch) interpretiert."),
            "G21"          => ("G21 — Maßeinheit: Millimeter",
                "Alle Koordinaten werden in Millimetern interpretiert."),
            "G28"          => ("G28 — Referenzpunkt anfahren",
                "Maschine fährt zum Maschinen-Nullpunkt (Referenzpunkt)."),
            "G40"          => ("G40 — Radiuskorrektur AUS",
                "Hebt eine aktive Werkzeug-Radiuskorrektur (G41/G42) auf."),
            "G41"          => ("G41 — Radiuskorrektur links",
                "Werkzeugmittelpunkt fährt links vom programmierten Pfad. Ausgleich für Frästerdurchmesser."),
            "G42"          => ("G42 — Radiuskorrektur rechts",
                "Werkzeugmittelpunkt fährt rechts vom programmierten Pfad. Ausgleich für Frästerdurchmesser."),
            "G54"          => ("G54 — Werkstück-Nullpunkt 1",
                "Aktiviert den ersten Werkstück-Koordinatenursprung (Nullpunktversatz 1)."),
            "G90"          => ("G90 — Absolute Koordinaten",
                "Alle Koordinaten beziehen sich auf den Werkstück-Nullpunkt."),
            "G91"          => ("G91 — Inkrementale Koordinaten",
                "Alle Koordinaten sind relativ zur aktuellen Werkzeugposition angegeben."),
            "G94"          => ("G94 — Vorschub mm/min",
                "Vorschubgeschwindigkeit F wird in Millimetern pro Minute interpretiert."),
            var u          => ($"{u} — G-Code", "Geometrischer Befehl für Bewegung oder Maschinenmodus.")
        };
    }

    private static (string, string) ExplainM(string tok)
    {
        return tok.ToUpperInvariant() switch
        {
            "M3"  or "M03" => ("M03 — Spindel EIN ↻",
                "Schaltet die Frässpindel im Uhrzeigersinn ein. Drehzahl wird mit S angegeben."),
            "M4"  or "M04" => ("M04 — Spindel EIN ↺",
                "Schaltet die Frässpindel gegen den Uhrzeigersinn ein."),
            "M5"  or "M05" => ("M05 — Spindel AUS",
                "Hält die Frässpindel an. Sollte vor Werkzeugwechsel und Programmende aufgerufen werden."),
            "M6"  or "M06" => ("M06 — Werkzeugwechsel",
                "Löst einen Werkzeugwechsel aus. Werkzeugnummer wird mit T angegeben."),
            "M8"  or "M08" => ("M08 — Kühlmittel EIN",
                "Schaltet die Kühlmittelzufuhr ein."),
            "M9"  or "M09" => ("M09 — Kühlmittel AUS",
                "Schaltet die Kühlmittelzufuhr aus."),
            "M30"          => ("M30 — Programmende",
                "Beendet das G-Code-Programm und setzt die Maschine auf den Programmanfang zurück."),
            var u          => ($"{u} — M-Code", "Maschinenfunktion (Hilfsfunktion, kein Bewegungsbefehl).")
        };
    }

    private static (string, string) ExplainXYZ(string tok)
    {
        var axis = char.ToUpperInvariant(tok[0]);
        var val  = tok[1..];
        return axis switch
        {
            'X' => ("X — Längsachse",   $"Zielposition X = {val} mm (links/rechts)."),
            'Y' => ("Y — Querachse",    $"Zielposition Y = {val} mm (vorne/hinten)."),
            'Z' => ("Z — Vertikalachse", $"Zielposition Z = {val} mm. Negative Werte bedeuten Eintauchen ins Material."),
            _   => (tok, "")
        };
    }

    private static (string, string) ExplainIJK(string tok)
    {
        var axis = char.ToUpperInvariant(tok[0]);
        var val  = tok[1..];
        return axis switch
        {
            'I' => ("I — Bogenmittelpunkt X", $"Abstand {val} mm in X-Richtung vom Startpunkt zum Bogenmittelpunkt."),
            'J' => ("J — Bogenmittelpunkt Y", $"Abstand {val} mm in Y-Richtung vom Startpunkt zum Bogenmittelpunkt."),
            'K' => ("K — Bogenmittelpunkt Z", $"Abstand {val} mm in Z-Richtung vom Startpunkt zum Bogenmittelpunkt."),
            _   => (tok, "")
        };
    }
}

public enum WerkzeugTyp { Fraeser, Bohrer }

public class Werkzeug : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private int _nr;
    public int Nr
    {
        get => _nr;
        set { if (SetField(ref _nr, value)) { OnChanged(nameof(DisplayName)); OnChanged(nameof(ListLabel)); } }
    }

    private WerkzeugTyp _typ = WerkzeugTyp.Fraeser;
    public WerkzeugTyp Typ
    {
        get => _typ;
        set { if (SetField(ref _typ, value)) OnChanged(nameof(ListLabel)); }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) { OnChanged(nameof(DisplayName)); OnChanged(nameof(ListLabel)); } }
    }

    public string DisplayName => $"{Nr}. {Name}";
    public string ListLabel   => $"{Nr}. {Name} ({(Typ == WerkzeugTyp.Fraeser ? "Fräser" : "Bohrer")})";

    private string? _bildPfad;
    public string? BildPfad { get => _bildPfad; set => SetField(ref _bildPfad, value); }

    private double _durchmesser;
    public double Durchmesser { get => _durchmesser; set => SetField(ref _durchmesser, value); }

    private double _schneidenwinkel;
    public double Schneidenwinkel { get => _schneidenwinkel; set => SetField(ref _schneidenwinkel, value); }

    private double _zZustellung;
    public double ZZustellung { get => _zZustellung; set => SetField(ref _zZustellung, value); }

    private double _eintauchwinkel;
    public double Eintauchwinkel { get => _eintauchwinkel; set => SetField(ref _eintauchwinkel, value); }

    private double _vorschubFxy;
    public double VorschubFxy { get => _vorschubFxy; set => SetField(ref _vorschubFxy, value); }

    private double _vorschubFz;
    public double VorschubFz { get => _vorschubFz; set => SetField(ref _vorschubFz, value); }

    private double _drehzahl;
    public double Drehzahl { get => _drehzahl; set => SetField(ref _drehzahl, value); }

    private double _raeumzustellungXY = 75.0;
    public double RaeumzustellungXY { get => _raeumzustellungXY; set => SetField(ref _raeumzustellungXY, value); }

    private DateTime _geaendert = DateTime.Now;
    public DateTime Geaendert { get => _geaendert; set => SetField(ref _geaendert, value); }
}

public class HistoryEntry(string label, string details, object p, int level = 0)
    : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Label   { get; } = label;
    public string Details { get; } = details;
    public object Params  { get; } = p;
    public int    Level   { get; } = level;

    // Nur Pfad-Startpunkte sind aufklappbar
    public bool IsExpandable => Params is PfadPunktParams { Typ: PfadPunktTyp.Start };

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value) return;
            _isCollapsed = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCollapsed)));
        }
    }
}

public record KreisParams(
    double XRel, double YRel,
    double Radius,
    double ZTiefe,
    double FraeserD, double Drehzahl, double Vorschub, double VorschubFz,
    string Fraesung,        // "Aussen" | "Innen" | "Mittig"
    string Laufrichtung,    // "Gegenlauf" | "Gleichlauf"
    int WerkzeugNr = 0,
    bool MehrfachZustellung = false,
    double ZZustellung = 0,
    double Eintauchwinkel = 3,
    string Bezugspunkt = "Mitte",
    bool IsTasche = false
);

public record RechteckParams(
    double XRel, double YRel,
    double Breite, double Hoehe,
    double ZTiefe,
    double FraeserD, double Drehzahl, double Vorschub, double VorschubFz,
    string Bezugspunkt,
    string Fraesung,        // "Aussen" | "Innen" | "Mittig"
    string Laufrichtung,    // "Gegenlauf" | "Gleichlauf"
    double Verrundung,
    int WerkzeugNr = 0,
    bool MehrfachZustellung = false,
    double ZZustellung = 0,
    double Eintauchwinkel = 3,
    bool IsTasche = false
);

public record NEckParams(
    double XRel, double YRel,
    int Ecken,
    double Radius,
    double RotationGrad,
    double ZTiefe,
    double FraeserD, double Drehzahl, double Vorschub, double VorschubFz,
    string Fraesung,        // "Aussen" | "Innen" | "Mittig"
    string Laufrichtung,    // "Gegenlauf" | "Gleichlauf"
    string Radiuskorrektur, // "Keine" | "Innen" | "Aussen"
    int WerkzeugNr = 0,
    bool MehrfachZustellung = false,
    double ZZustellung = 0,
    double Eintauchwinkel = 3,
    string Bezugspunkt = "Mitte",
    bool IsTasche = false
);

public record WerkzeugWechselParams(
    int WerkzeugNr,
    string WerkzeugName,
    double Drehzahl,
    double Durchmesser = 10,
    double SchneidenWinkel = 180
);

public static class VisualTreeHelperExtensions
{
    public static T? FindVisualParent<T>(this DependencyObject obj) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(obj);
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
