using System.Collections.Generic;
using System.Windows;

namespace NCHops;

public partial class ReihenlochbohrungDialog : Window
{
    public ReihenlochbohrungParams? Result { get; private set; }
    private readonly ReihenlochbohrungParams? _prefill;

    public ReihenlochbohrungDialog(double defaultZ, ReihenlochbohrungParams? prefill = null,
                                   IReadOnlyList<Werkzeug>? werkzeuge = null)
    {
        InitializeComponent();
        _prefill = prefill;
        if (werkzeuge?.Count > 0)
        {
            CbWerkzeug.ItemsSource = werkzeuge;
            if (prefill == null || werkzeuge.Count == 1) CbWerkzeug.SelectedIndex = 0;
            CbWerkzeug.IsEnabled = werkzeuge.Count > 1;
        }
        if (prefill != null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            TxtStartX.Text    = prefill.StartX.ToString(inv);
            TxtStartY.Text    = prefill.StartY.ToString(inv);
            TxtCountX.Text    = prefill.CountX.ToString(inv);
            TxtCountY.Text    = prefill.CountY.ToString(inv);
            TxtSpacingX.Text  = prefill.SpacingX.ToString(inv);
            TxtSpacingY.Text  = prefill.SpacingY.ToString(inv);
            TxtBohrtiefe.Text = prefill.Bohrtiefe.ToString(inv);
        }
        else
        {
            TxtBohrtiefe.Text = defaultZ.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var w = CbWerkzeug.SelectedItem as Werkzeug;
        Result = new ReihenlochbohrungParams(
            StartX:    double.Parse(TxtStartX.Text,   inv),
            StartY:    double.Parse(TxtStartY.Text,   inv),
            CountX:    int.Parse(TxtCountX.Text,      inv),
            CountY:    int.Parse(TxtCountY.Text,      inv),
            SpacingX:  double.Parse(TxtSpacingX.Text, inv),
            SpacingY:  double.Parse(TxtSpacingY.Text, inv),
            Diameter:   w?.Durchmesser ?? 5,
            Bohrtiefe:  double.Parse(TxtBohrtiefe.Text, inv),
            Zustellung: w?.ZZustellung ?? 10,
            VorschubFz: w?.VorschubFz ?? 500,
            Drehzahl:   w?.Drehzahl ?? 20000,
            // Bohrart aus dem bestehenden Eintrag übernehmen (wird im Eigenschaften-Panel gesetzt)
            IstKreistasche: _prefill?.IstKreistasche ?? false,
            TascheD:        _prefill?.TascheD ?? 0,
            Vorschub:       w?.VorschubFxy ?? 3000,
            Eintauchwinkel: w?.Eintauchwinkel ?? 3,
            Faktor:         w != null ? w.RaeumzustellungXY / 100.0 : 0.5
        );
        // Einzelabstände nur behalten, solange der Standardabstand nicht geändert wurde
        if (_prefill != null)
            Result = Result with
            {
                AbstaendeX = Result.SpacingX == _prefill.SpacingX ? _prefill.AbstaendeX : null,
                AbstaendeY = Result.SpacingY == _prefill.SpacingY ? _prefill.AbstaendeY : null,
            };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public record ReihenlochbohrungParams(
    double StartX, double StartY,
    int CountX, int CountY,
    double SpacingX, double SpacingY,
    double Diameter, double Bohrtiefe, double Zustellung, double VorschubFz, double Drehzahl,
    // Bohrart Kreistasche: jedes Loch wird als Kreistasche mit TascheD gefräst (statt gebohrt)
    bool IstKreistasche = false, double TascheD = 0,
    double Vorschub = 3000, double Eintauchwinkel = 3, double Faktor = 0.5,
    // Einzeln vermasste Lochabstände (Index = Lücke zwischen Loch i und i+1), null = alle gleich
    Lochabstaende? AbstaendeX = null, Lochabstaende? AbstaendeY = null)
{
    /// <summary>Tatsächlicher Lochdurchmesser (Taschendurchmesser bzw. Bohrerdurchmesser).</summary>
    public double LochD => IstKreistasche && TascheD > Diameter ? TascheD : Diameter;

    public int Count(bool inX) => inX ? CountX : CountY;
    public double Spacing(bool inX) => inX ? SpacingX : SpacingY;
    private Lochabstaende? Abstaende(bool inX) => inX ? AbstaendeX : AbstaendeY;

    /// <summary>Ist die Lücke i (Loch i → i+1) einzeln vermasst?</summary>
    public bool IstEinzelabstand(bool inX, int i) => Abstaende(inX)?.Get(i) != null;

    /// <summary>Abstand Loch i → Loch i+1.</summary>
    public double Gap(bool inX, int i) => Abstaende(inX)?.Get(i) ?? Spacing(inX);

    /// <summary>Versatz von Loch i gegenüber dem 1. Loch.</summary>
    public double Off(bool inX, int i)
    {
        double s = 0;
        for (int k = 0; k < i; k++) s += Gap(inX, k);
        return s;
    }

    /// <summary>Länge 1. → letztes Loch.</summary>
    public double Laenge(bool inX) => Off(inX, Count(inX) - 1);

    /// <summary>Kleinster Lochabstand der Achse (für Plausibilitätsprüfung).</summary>
    public double MinGap(bool inX)
    {
        double m = double.MaxValue;
        for (int k = 0; k < Count(inX) - 1; k++) m = Math.Min(m, Gap(inX, k));
        return m;
    }

    private ReihenlochbohrungParams MitAbstaenden(bool inX, Lochabstaende? a, double spacing)
        => inX ? this with { AbstaendeX = a, SpacingX = spacing } : this with { AbstaendeY = a, SpacingY = spacing };

    /// <summary>Lücken von..bis-1 einzeln auf wert setzen (übrige Abstände bleiben).</summary>
    public ReihenlochbohrungParams MitEinzelabstand(bool inX, int von, int bis, double wert)
    {
        var w = new double[Math.Max(Count(inX) - 1, 0)];
        for (int k = 0; k < w.Length; k++)
            w[k] = k >= von && k < bis ? wert : (Abstaende(inX)?.Get(k) ?? double.NaN);
        return MitAbstaenden(inX, new Lochabstaende(w), Spacing(inX));
    }

    /// <summary>Länge 1. → letztes Loch setzen: die nicht einzeln vermassten Abstände werden
    /// gleichmässig verteilt; sind alle einzeln vermasst, werden alle gleich gross.</summary>
    public ReihenlochbohrungParams MitLaenge(bool inX, double laenge)
    {
        int n = Count(inX) - 1;
        if (n < 1) return this;
        int frei = 0; double fix = 0;
        for (int k = 0; k < n; k++)
            if (IstEinzelabstand(inX, k)) fix += Gap(inX, k); else frei++;
        return frei > 0
            ? MitAbstaenden(inX, Abstaende(inX), Math.Round((laenge - fix) / frei, 3))
            : MitAbstaenden(inX, null, Math.Round(laenge / n, 3));
    }

    /// <summary>Abstand der Lücke i setzen (einzeln, falls sie es schon ist, sonst Standardabstand).</summary>
    public ReihenlochbohrungParams MitGap(bool inX, int i, double wert)
        => IstEinzelabstand(inX, i) ? MitEinzelabstand(inX, i, i + 1, wert)
                                     : MitAbstaenden(inX, Abstaende(inX), wert);
}

/// <summary>Einzelne Lochabstände mit Wertgleichheit (NaN = Standardabstand).</summary>
public sealed record Lochabstaende(double[] Werte)
{
    public double? Get(int i) => i >= 0 && i < Werte.Length && !double.IsNaN(Werte[i]) ? Werte[i] : null;
    public bool Equals(Lochabstaende? o) => o != null && Werte.AsSpan().SequenceEqual(o.Werte);
    public override int GetHashCode() => Werte.Length;
}
