using System;
using System.Globalization;
using System.Text;

namespace NCHops;

public static class GCodeGenerator
{
    public static double SafeZ { get; set; } = 5.0;

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
    private static string Sz() => $"G00 Z{F(SafeZ)}";

    public static string WerkzeugWechsel(WerkzeugWechselParams p)
    {
        // Klammern im Namen würden den Kommentar vorzeitig beenden
        string name = p.WerkzeugName.Replace('(', '[').Replace(')', ']');
        var sb = new StringBuilder();
        sb.AppendLine("M05");
        sb.AppendLine($"M06 ({name})");
        sb.AppendLine($"M03 S{(int)p.Drehzahl}");
        return sb.ToString();
    }

    public static string Planfräsen(PlanfräsenParams p)
    {
        double schritt = p.FraeserD * p.Faktor;
        var sb = new StringBuilder();
        sb.AppendLine("(Benötigte Werkzeuge:)");
        sb.AppendLine("(planfräser)");
        sb.AppendLine();
        sb.AppendLine(Sz());
        sb.AppendLine();
        sb.AppendLine($"G00 X{F(p.X0)} Y{F(p.Y0)}");
        sb.AppendLine($"G01 Z{F(p.Z)} F{(int)p.VorschubFz}");

        if (p.Horizontal)
        {
            double y = p.Y0;
            bool hin = true;
            while (y < p.Y1)
            {
                sb.AppendLine(hin ? $"G01 X{F(p.X1)} F{(int)p.Vorschub}" : $"G01 X{F(p.X0)} F{(int)p.Vorschub}");
                y += schritt;
                sb.AppendLine($"G01 Y{F(y)} F{(int)p.Vorschub}");
                if (y >= p.Y1)
                {
                    hin = !hin;
                    sb.AppendLine(hin ? $"G01 X{F(p.X1)} F{(int)p.Vorschub}" : $"G01 X{F(p.X0)} F{(int)p.Vorschub}");
                    break;
                }
                hin = !hin;
            }
        }
        else
        {
            double x = p.X0;
            bool hin = true;
            while (x < p.X1)
            {
                sb.AppendLine(hin ? $"G01 Y{F(p.Y1)} F{(int)p.Vorschub}" : $"G01 Y{F(p.Y0)} F{(int)p.Vorschub}");
                x += schritt;
                sb.AppendLine($"G01 X{F(x)} F{(int)p.Vorschub}");
                if (x >= p.X1)
                {
                    hin = !hin;
                    sb.AppendLine(hin ? $"G01 Y{F(p.Y1)} F{(int)p.Vorschub}" : $"G01 Y{F(p.Y0)} F{(int)p.Vorschub}");
                    break;
                }
                hin = !hin;
            }
        }

        sb.AppendLine();
        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string Bohrung(BohrungParams p, double workW, double workH)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(Bohrung)");
        sb.AppendLine($"(D={p.Durchmesser}, Bezug={p.Bezugspunkt})");
        sb.AppendLine(Sz());

        var (x, y) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);
        sb.AppendLine($"G00 X{F(x)} Y{F(y)}");

        var zDepth = -Math.Abs(p.Bohrtiefe);
        var zStep  =  Math.Abs(p.Zustellung);
        double currentZ = 0;
        while (currentZ > zDepth)
        {
            currentZ = Math.Max(zDepth, currentZ - zStep);
            sb.AppendLine($"G01 Z{F(currentZ)} F{(int)p.VorschubFz}");
            sb.AppendLine(Sz());
        }

        return sb.ToString();
    }

    public static string Reihenlochbohrung(ReihenlochbohrungParams p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(Reihenlochbohrung)");
        sb.AppendLine($"(Start: X={p.StartX} Y={p.StartY})");
        sb.AppendLine($"(Anzahl X={p.CountX} Y={p.CountY})");
        sb.AppendLine($"(Abstand X={p.SpacingX} Y={p.SpacingY})");
        sb.AppendLine($"(D={p.Diameter})");
        sb.AppendLine($"(Bohrtiefe={p.Bohrtiefe})");
        sb.AppendLine($"(Zustellung={p.Zustellung})");
        sb.AppendLine(Sz());

        var depth = -Math.Abs(p.Bohrtiefe);
        var step = Math.Abs(p.Zustellung);
        // Bohrart Kreistasche: nur wenn die Tasche grösser als der Fräser ist, sonst normal bohren
        bool tasche = p.IstKreistasche && p.TascheD > p.Diameter;
        if (tasche) sb.AppendLine($"(Bohrart: Kreistasche D={F(p.TascheD)})");
        for (int iy = 0; iy < p.CountY; iy++)
        {
            for (int ix = 0; ix < p.CountX; ix++)
            {
                var x = p.StartX + ix * p.SpacingX;
                var y = p.StartY + iy * p.SpacingY;
                if (tasche)
                {
                    sb.Append(Kreistasche(new KreistascheParams(
                        XRel: x, YRel: y, Durchmesser: p.TascheD,
                        ZTiefe: p.Bohrtiefe, ZZustellung: step > 0 ? step : 2,
                        Eintauchwinkel: p.Eintauchwinkel, FraeserD: p.Diameter, Faktor: p.Faktor,
                        Vorschub: p.Vorschub, VorschubFz: p.VorschubFz, Drehzahl: p.Drehzahl,
                        Bezugspunkt: "Unten links"), 0, 0));
                    continue;
                }
                sb.AppendLine($"(Bohrung: X={x} Y={y})");
                sb.AppendLine($"G00 X{F(x)} Y{F(y)}");

                double currentZ = 0;
                while (currentZ > depth)
                {
                    currentZ = Math.Max(depth, currentZ - step);
                    sb.AppendLine($"G01 Z{F(currentZ)} F{(int)p.VorschubFz}");
                    sb.AppendLine(Sz());
                }
            }
        }

        return sb.ToString();
    }

    public static string Tasche(TascheFräsenParams p, double workW, double workH)
    {
        const double allowance = 1.0; // Schlichtaufmaß an allen Wänden

        double r    = p.FraeserD / 2.0;
        double step = Math.Max(0.1, p.FraeserD * p.Faktor);

        var (refX, refY) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);

        // Bezugspunkt zeigt auf die entsprechende Ecke/Seite der Tasche selbst.
        // Offset auf die untere-linke Taschenecke umrechnen.
        var (bx, by) = p.Bezugspunkt switch
        {
            "Unten links"  => (0,             0),
            "Unten Mitte"  => (-p.Breite / 2, 0),
            "Unten rechts" => (-p.Breite,     0),
            "Links Mitte"  => (0,             -p.Höhe / 2),
            "Mitte"        => (-p.Breite / 2, -p.Höhe / 2),
            "Rechts Mitte" => (-p.Breite,     -p.Höhe / 2),
            "Oben links"   => (0,             -p.Höhe),
            "Oben Mitte"   => (-p.Breite / 2, -p.Höhe),
            "Oben rechts"  => (-p.Breite,     -p.Höhe),
            _              => (0.0,            0.0)
        };

        double ax = refX + bx;
        double ay = refY + by;

        // Fräsermittelpunkt-Bereich bei vollem Wandeingriff (Schlichten)
        double ix0 = ax + r;
        double iy0 = ay + r;
        double ix1 = ax + p.Breite - r;
        double iy1 = ay + p.Höhe   - r;

        var sb = new StringBuilder();
        sb.AppendLine("(Tasche fräsen)");
        sb.AppendLine($"(X={F(ax)} Y={F(ay)} B={F(p.Breite)} H={F(p.Höhe)})");
        sb.AppendLine($"(D={p.FraeserD}, Bezug={p.Bezugspunkt})");

        if (ix1 <= ix0 || iy1 <= iy0)
        {
            sb.AppendLine("(Tasche zu klein für Werkzeug)");
            return sb.ToString();
        }

        sb.AppendLine(Sz());

        double depth = -Math.Abs(p.ZTiefe);
        double zStep = Math.Abs(p.ZZustellung);
        double curZ  = 0;

        // Eckradius der Werkzeugmittelpunktbahn (Fertigkante - Werkzeugradius)
        double cr = Math.Max(0.0, p.Verrundung - r);

        // Schrupp-Bereich: 1mm Aufmaß an allen Wänden lassen
        double rx0 = ix0 + allowance;
        double ry0 = iy0 + allowance;
        double rx1 = ix1 - allowance;
        double ry1 = iy1 - allowance;
        bool hasRoughArea = rx1 > rx0 && ry1 > ry0;

        // Y-Bereich für Räumfräsung
        double zy0 = hasRoughArea ? ry0 : iy0;
        double zy1 = hasRoughArea ? ry1 : iy1;

        // Räumfräsung-X-Bereich bei Werkzeugmittelpunkt-Höhe yy.
        // In geraden Abschnitten: Aufmaß auf Wände (rx0/rx1).
        // In Eckzonen: Aufmaß radial auf den Verrundungsbogen (Radius crClear = cr - allowance).
        (double xL, double xR) RoundedBounds(double yy)
        {
            double dy = cr > 1e-6 ? Math.Max(0, Math.Max(iy0 + cr - yy, yy - (iy1 - cr))) : 0;
            if (dy < 1e-6)
                return (hasRoughArea ? rx0 : ix0, hasRoughArea ? rx1 : ix1);
            // Eckzone: Aufmaß radial vom Eckbogen abziehen
            double crClear = cr - (hasRoughArea ? allowance : 0);
            if (crClear <= 0 || dy >= crClear - 1e-6)
                return (ix0 + cr, ix1 - cr); // gesamte Eckzone ist Aufmaß → keine Räumzeile hier
            double dx = Math.Sqrt(crClear * crClear - dy * dy);
            return (ix0 + cr - dx, ix1 - cr + dx);
        }

        // Startpunkt der Räumfräsung: erste Zeile, linke Kante (1mm von Schlichtkontur)
        var (startX0, startX1) = RoundedBounds(zy0);

        // Eintauchrampe: von startX0 diagonal auf curZ absenken (entlang erster Zeile)
        void AppendEntry(double prevZ, double nextZ)
        {
            double dz    = Math.Abs(nextZ - prevZ);
            double angle = p.Eintauchwinkel;
            if (angle >= 90 || angle <= 0 || dz < 1e-6)
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
                return;
            }
            double rampLen = (dz / 2.0) / Math.Tan(angle * Math.PI / 180.0);
            if (startX0 + rampLen > startX1)
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
                return;
            }
            double midZ = prevZ - dz / 2.0;
            sb.AppendLine($"G01 X{F(startX0 + rampLen)} Z{F(midZ)} F{(int)p.VorschubFz}");
            sb.AppendLine($"G01 X{F(startX0)} Z{F(nextZ)} F{(int)p.VorschubFz}");
        }

        double lastToolX = startX0;
        double lastToolY = zy0;

        while (curZ > depth)
        {
            double prevDepth = curZ;
            curZ = Math.Max(depth, curZ - zStep);

            // 1. G00 direkt zum Räumstartpunkt (erste Zeile, 1mm von Schlichtkontur)
            sb.AppendLine($"G00 X{F(startX0)} Y{F(zy0)}");
            // 2. G00 runter auf vorherige Räumebene
            sb.AppendLine($"G00 Z{F(prevDepth)}");
            // 3. Eintauchrampe
            AppendEntry(prevDepth, curZ);

            double y      = zy0;
            bool rightward = true;
            double curXPos = startX0;

            while (true)
            {
                var (rowX0, rowX1) = RoundedBounds(y);

                if (rowX1 > rowX0 + 1e-6)
                {
                    double startX = rightward ? rowX0 : rowX1;
                    double endX   = rightward ? rowX1 : rowX0;
                    if (Math.Abs(curXPos - startX) > 1e-6)
                        sb.AppendLine($"G01 X{F(startX)} F{(int)p.Vorschub}");
                    sb.AppendLine($"G01 X{F(endX)} F{(int)p.Vorschub}");
                    curXPos = endX;
                    rightward = !rightward;
                }

                if (y >= zy1) break;

                // Nächste Y-Position: kein Einschnappen auf Eckgrenzen → volle Überlappung genutzt
                double nextY = Math.Min(y + step, zy1);

                // Verbindung zur nächsten Zeile
                bool curInCorner  = cr > 1e-6 && rowX1 > rowX0 + 1e-6 &&
                                    (y     < iy0 + cr - 1e-6 || y     > iy1 - cr + 1e-6);
                bool nextInCorner = cr > 1e-6 &&
                                    (nextY < iy0 + cr - 1e-6 || nextY > iy1 - cr + 1e-6);
                if (!curInCorner && !nextInCorner)
                {
                    sb.AppendLine($"G01 Y{F(nextY)} F{(int)p.Vorschub}");
                }
                else
                {
                    bool onRight = Math.Abs(curXPos - rowX1) < 1e-6;
                    double ccx   = onRight ? ix1 - cr : ix0 + cr;
                    string arc   = onRight ? "G03" : "G02";

                    if (!curInCorner)
                    {
                        // Gerade Zone → Eckzone: G01 bis Eckgrenze, dann Bogen
                        double b = nextY > iy1 - cr + 1e-6 ? iy1 - cr : iy0 + cr;
                        var (nrX0, nrX1) = RoundedBounds(nextY);
                        double nextX = onRight ? nrX1 : nrX0;
                        sb.AppendLine($"G01 Y{F(b)} F{(int)p.Vorschub}");
                        sb.AppendLine($"{arc} X{F(nextX)} Y{F(nextY)} I{F(ccx - curXPos)} J0 F{(int)p.Vorschub}");
                        curXPos = nextX;
                    }
                    else if (!nextInCorner)
                    {
                        // Eckzone → gerade Zone: Bogen bis Eckgrenze, dann G01
                        double b   = y < iy0 + cr - 1e-6 ? iy0 + cr : iy1 - cr;
                        double ccy = b;
                        var (brX0, brX1) = RoundedBounds(b);
                        double bX = onRight ? brX1 : brX0;
                        sb.AppendLine($"{arc} X{F(bX)} Y{F(b)} I{F(ccx - curXPos)} J{F(ccy - y)} F{(int)p.Vorschub}");
                        sb.AppendLine($"G01 Y{F(nextY)} F{(int)p.Vorschub}");
                        curXPos = bX;
                    }
                    else
                    {
                        // Eckzone → Eckzone: direkter Bogen
                        double ccy = y < iy0 + cr - 1e-6 ? iy0 + cr : iy1 - cr;
                        var (nrX0, nrX1) = RoundedBounds(nextY);
                        double nextX = onRight ? nrX1 : nrX0;
                        sb.AppendLine($"{arc} X{F(nextX)} Y{F(nextY)} I{F(ccx - curXPos)} J{F(ccy - y)} F{(int)p.Vorschub}");
                        curXPos = nextX;
                    }
                }
                y = nextY;
            }
            lastToolX = curXPos;
            lastToolY = zy1;

            if (curZ > depth)
                sb.AppendLine(Sz());
        }

        // Schlichten: nächstgelegene Ecke anfahren, Kontur CW (Gegenlauf)
        // Reihenfolge: BL, TL, TR, BR → CW
        (double x, double y)[] corners = cr < 1e-6
            ? [(ix0, iy0), (ix0, iy1), (ix1, iy1), (ix1, iy0)]
            : [(ix0, iy1 - cr), (ix0 + cr, iy1), (ix1 - cr, iy1), (ix1, iy1 - cr),
               (ix1, iy0 + cr), (ix1 - cr, iy0), (ix0 + cr, iy0), (ix0, iy0 + cr)];

        int startCorner = 0;
        double minDist  = double.MaxValue;
        int nc = corners.Length;
        for (int i = 0; i < nc; i++)
        {
            double dx = corners[i].x - lastToolX;
            double dy = corners[i].y - lastToolY;
            double d  = dx * dx + dy * dy;
            if (d < minDist) { minDist = d; startCorner = i; }
        }
        sb.AppendLine($"G01 X{F(corners[startCorner].x)} Y{F(corners[startCorner].y)} F{(int)p.Vorschub}");

        if (cr < 1e-6)
        {
            for (int i = 1; i <= 4; i++)
            {
                var c = corners[(startCorner + i) % 4];
                sb.AppendLine($"G01 X{F(c.x)} Y{F(c.y)} F{(int)p.Vorschub}");
            }
        }
        else
        {
            // 8 Punkte: Eintritt/Austritt jeder Ecke; Ecken liegen bei ungeraden Indizes
            // Arc-IJ-Tabelle: (I, J) des Bogenmittelpunkts relativ zum Startpunkt
            // CW (G02) Bögen: TL=(+cr,0), TR=(0,-cr), BR=(-cr,0), BL=(0,+cr)
            (double i, double j)[] arcIJ = [(cr, 0), (0, -cr), (-cr, 0), (0, cr)];
            for (int k = 1; k <= 8; k++)
            {
                int idx    = (startCorner + k) % 8;
                var (ex, ey) = corners[idx];
                if (idx % 2 == 0)
                {
                    // gerader Abschnitt: Linie
                    sb.AppendLine($"G01 X{F(ex)} Y{F(ey)} F{(int)p.Vorschub}");
                }
                else
                {
                    // Eckbogen: G02 (CW = Gegenlauf)
                    var (aI, aJ) = arcIJ[idx / 2 % 4];
                    sb.AppendLine($"G02 X{F(ex)} Y{F(ey)} I{F(aI)} J{F(aJ)} F{(int)p.Vorschub}");
                }
            }
        }

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string Nut(NutParams p, double workW, double workH)
    {
        double r    = p.FraeserD / 2.0;
        double step = Math.Max(0.1, p.FraeserD * p.Faktor);

        var (refX, refY) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);

        // Offset auf untere-linke Nutecke umrechnen
        var (bx, by) = p.Bezugspunkt switch
        {
            "Unten links"  => (0,              0),
            "Unten Mitte"  => (-p.Länge / 2,   0),
            "Unten rechts" => (-p.Länge,        0),
            "Links Mitte"  => (0,              -p.Breite / 2),
            "Mitte"        => (-p.Länge / 2,   -p.Breite / 2),
            "Rechts Mitte" => (-p.Länge,       -p.Breite / 2),
            "Oben links"   => (0,              -p.Breite),
            "Oben Mitte"   => (-p.Länge / 2,   -p.Breite),
            "Oben rechts"  => (-p.Länge,       -p.Breite),
            _              => (0.0,             0.0)
        };

        double ax = refX + bx;
        double ay = refY + by;

        // Fräsermittelpunkt-Bereich – kein Aufmaß, direkt an der Endkontur
        double ix0 = ax + r;
        double iy0 = ay + r;
        double ix1 = ax + p.Länge  - r;
        double iy1 = ay + p.Breite - r;

        var sb = new StringBuilder();
        sb.AppendLine("(Nut fräsen)");
        sb.AppendLine($"(X={F(ax)} Y={F(ay)} L={F(p.Länge)} B={F(p.Breite)})");
        sb.AppendLine($"(D={p.FraeserD}, Bezug={p.Bezugspunkt})");

        if (ix1 < ix0 - 1e-6 || iy1 < iy0 - 1e-6)
        {
            sb.AppendLine("(Nut zu schmal für Werkzeug)");
            return sb.ToString();
        }

        sb.AppendLine(Sz());

        double depth = -Math.Abs(p.ZTiefe);
        double zStep = Math.Abs(p.ZZustellung);
        double curZ  = 0;

        void AppendEntry(double prevZ, double nextZ)
        {
            double dz    = Math.Abs(nextZ - prevZ);
            double angle = p.Eintauchwinkel;
            if (angle >= 90 || angle <= 0 || dz < 1e-6)
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
                return;
            }
            double rampLen = (dz / 2.0) / Math.Tan(angle * Math.PI / 180.0);
            if (ix0 + rampLen > ix1)
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
                return;
            }
            double midZ = prevZ - dz / 2.0;
            sb.AppendLine($"G01 X{F(ix0 + rampLen)} Z{F(midZ)} F{(int)p.VorschubFz}");
            sb.AppendLine($"G01 X{F(ix0)} Z{F(nextZ)} F{(int)p.VorschubFz}");
        }

        while (curZ > depth)
        {
            double prevDepth = curZ;
            curZ = Math.Max(depth, curZ - zStep);

            sb.AppendLine($"G00 X{F(ix0)} Y{F(iy0)}");
            sb.AppendLine($"G00 Z{F(prevDepth)}");
            AppendEntry(prevDepth, curZ);

            double y = iy0;
            bool rightward = true;
            while (true)
            {
                sb.AppendLine(rightward
                    ? $"G01 X{F(ix1)} F{(int)p.Vorschub}"
                    : $"G01 X{F(ix0)} F{(int)p.Vorschub}");
                if (y >= iy1 - 1e-6) break;
                y = Math.Min(y + step, iy1);
                sb.AppendLine($"G01 Y{F(y)} F{(int)p.Vorschub}");
                rightward = !rightward;
            }

            if (curZ > depth)
                sb.AppendLine(Sz());
        }

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string Kreistasche(KreistascheParams p, double workW, double workH)
    {
        const double allowance = 1.0;

        double r       = p.FraeserD / 2.0;
        double step    = Math.Max(0.1, p.FraeserD * p.Faktor);
        double Rp      = p.Durchmesser / 2.0;
        double Rm      = Rp - r;

        var (cx, cy) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);

        var sb = new StringBuilder();
        sb.AppendLine("(Kreistasche fräsen)");
        sb.AppendLine($"(Mitte X={F(cx)} Y={F(cy)}, D={F(p.Durchmesser)})");
        sb.AppendLine($"(D={p.FraeserD}, Eintauchwinkel={p.Eintauchwinkel}°, Bezug={p.Bezugspunkt})");

        if (Rm <= 0)
        {
            sb.AppendLine("(Tasche zu klein für Werkzeug)");
            return sb.ToString();
        }

        sb.AppendLine(Sz());

        double depth     = -Math.Abs(p.ZTiefe);
        double zStep     = Math.Abs(p.ZZustellung);
        double roughRm   = Rm - allowance;
        double maxRoughR = roughRm > 0 ? roughRm : Rm;

        // Eintauchradius: erster Kreisschritt, auf Rm begrenzt und kleiner als der Fräserradius,
        // damit die Helix die Mitte mit räumt und kein Mittelstumpf stehen bleibt
        double rEntry   = Math.Min(Math.Min(step, 0.9 * r), maxRoughR);
        double angleRad = Math.PI / 180.0 * Math.Abs(p.Eintauchwinkel);
        // Z-Absenkung pro Helix-Umdrehung (Eintauchwinkel)
        double zPerRev  = p.Eintauchwinkel > 0.01
            ? 2.0 * Math.PI * rEntry * Math.Tan(angleRad)
            : 0;

        sb.AppendLine($"G00 X{F(cx + rEntry)} Y{F(cy)}");
        double curZ = 0;
        double lastAngle = 0;  // Endwinkel der letzten Schruppspirale (Position am Ende der Schleife)
        double entryAngle = 0; // Winkel des Helix-Eintauchpunkts, wechselt pro Tiefenstufe um 180°

        while (curZ > depth)
        {
            double nextZ = Math.Max(depth, curZ - zStep);
            double hx = rEntry * Math.Cos(entryAngle), hy = rEntry * Math.Sin(entryAngle);

            // Helikal eintauchen von curZ nach nextZ im Gegenlauf (G02 = CW)
            if (zPerRev > 1e-6)
            {
                double z = curZ;
                while (z > nextZ)
                {
                    double zEnd = Math.Max(nextZ, z - zPerRev);
                    double zMid = (z + zEnd) / 2.0;
                    // Voller Kreis als zwei Halbkreise (Start = Ende wird sonst u.a. von Estlcam als Nullbogen gelesen)
                    sb.AppendLine($"G02 X{F(cx - hx)} Y{F(cy - hy)} Z{F(zMid)} I{F(-hx)} J{F(-hy)} F{(int)p.Vorschub}");
                    sb.AppendLine($"G02 X{F(cx + hx)} Y{F(cy + hy)} Z{F(zEnd)} I{F(hx)} J{F(hy)} F{(int)p.Vorschub}");
                    z = zEnd;
                }
            }
            else
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
            }
            curZ = nextZ;

            // Archimedische Spirale von rEntry nach maxRoughR im Gegenlauf (CW = neg. Winkel)
            // Segmente pro Umdrehung aus Sehnentoleranz, damit die Kurve bei großen Radien glatt bleibt
            double endAngle = entryAngle; // Winkel, bei dem die Spirale endet = Start des Abschlusskreises
            if (maxRoughR > rEntry)
            {
                const double chordalTol = 0.05; // mm – max. Abstand Sehne/Kreisbogen
                const int    maxSegsPerRev = 144; // max. 2.5° pro Segment
                double maxAngleRad = Math.Acos(1.0 - chordalTol / maxRoughR);
                int segPerRev = Math.Min(maxSegsPerRev, (int)Math.Ceiling(2.0 * Math.PI / maxAngleRad));
                double totalRevs = (maxRoughR - rEntry) / step;
                int totalSegs = Math.Min(2000, Math.Max(segPerRev, (int)Math.Ceiling(totalRevs * segPerRev)));
                double spiralSweep = 2.0 * Math.PI * totalRevs; // CW
                endAngle = entryAngle - spiralSweep;
                for (int i = 1; i <= totalSegs; i++)
                {
                    double t       = (double)i / totalSegs;
                    double spiralR = rEntry + (maxRoughR - rEntry) * t;
                    double angle   = entryAngle - spiralSweep * t;
                    sb.AppendLine($"G01 X{F(cx + spiralR * Math.Cos(angle))} Y{F(cy + spiralR * Math.Sin(angle))} F{(int)p.Vorschub}");
                }
            }
            // sonst: rEntry == maxRoughR, der Helixkreis ist bereits der Abschlusskreis
            lastAngle = endAngle;
            // Abschlusskreis bei maxRoughR ab Spiralende (stellt vollständige Abdeckung sicher),
            // als zwei Halbkreise: Endpunkt → gegenüberliegender Punkt → Endpunkt
            double ex = maxRoughR * Math.Cos(endAngle), ey = maxRoughR * Math.Sin(endAngle);
            sb.AppendLine($"G02 X{F(cx - ex)} Y{F(cy - ey)} I{F(-ex)} J{F(-ey)} F{(int)p.Vorschub}");
            sb.AppendLine($"G02 X{F(cx + ex)} Y{F(cy + ey)} I{F(ex)} J{F(ey)} F{(int)p.Vorschub}");

            // Für nächste Tiefenstufe zum neuen Eintauchpunkt (im geräumten Bereich), gegenüber
            // dem letzten Eintauchpunkt: Spirale nach innen, CW weiter von endAngle bis entryAngle + 180°
            if (curZ > depth)
            {
                double nextEntry = entryAngle + Math.PI;
                if (maxRoughR > rEntry)
                {
                    double twoPi   = 2.0 * Math.PI;
                    // größter Winkel ≡ nextEntry (mod 2π) unterhalb von endAngle
                    double target  = nextEntry + twoPi * Math.Floor((endAngle - nextEntry) / twoPi - 1e-9);
                    double sweep   = endAngle - target;              // > 0, CW-Winkelweg
                    if (sweep < Math.PI) { target -= twoPi; sweep += twoPi; } // nicht zu steil
                    int segs = Math.Max(8, (int)Math.Ceiling(sweep / (2.5 * Math.PI / 180.0)));
                    for (int i = 1; i <= segs; i++)
                    {
                        double t     = (double)i / segs;
                        // Smoothstep: Radiusänderung an beiden Enden 0 → tangential aus dem
                        // Abschlusskreis heraus und tangential in den Helixkreis hinein
                        double s     = t * t * (3.0 - 2.0 * t);
                        double rr    = maxRoughR + (rEntry - maxRoughR) * s;
                        double angle = endAngle - sweep * t;
                        sb.AppendLine($"G01 X{F(cx + rr * Math.Cos(angle))} Y{F(cy + rr * Math.Sin(angle))} F{(int)p.Vorschub}");
                    }
                }
                else
                {
                    // Halbkreis auf dem Helixkreis zur Gegenseite
                    sb.AppendLine($"G02 X{F(cx - hx)} Y{F(cy - hy)} I{F(-hx)} J{F(-hy)} F{(int)p.Vorschub}");
                }
                entryAngle = nextEntry;
            }
        }

        // Schlichten ohne Austauchen: auf Endtiefe per Spirale (Smoothstep, tangential an beiden Enden)
        // über 180° CW von maxRoughR auf Rm, dann voller Kreis bei Rm im Gegenlauf (G02 = CW)
        double finAngle = lastAngle;
        if (Rm - maxRoughR > 1e-6)
        {
            const int segs = 72; // 2.5° pro Segment
            for (int i = 1; i <= segs; i++)
            {
                double t     = (double)i / segs;
                double s     = t * t * (3.0 - 2.0 * t);
                double rr    = maxRoughR + (Rm - maxRoughR) * s;
                double angle = lastAngle - Math.PI * t;
                sb.AppendLine($"G01 X{F(cx + rr * Math.Cos(angle))} Y{F(cy + rr * Math.Sin(angle))} F{(int)p.Vorschub}");
            }
            finAngle = lastAngle - Math.PI;
        }
        double fx = Rm * Math.Cos(finAngle), fy = Rm * Math.Sin(finAngle);
        sb.AppendLine($"G02 X{F(cx - fx)} Y{F(cy - fy)} I{F(-fx)} J{F(-fy)} F{(int)p.Vorschub}");
        sb.AppendLine($"G02 X{F(cx + fx)} Y{F(cy + fy)} I{F(fx)} J{F(fy)} F{(int)p.Vorschub}");

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string Umfahren(UmfahrenParams p, double workW, double workH)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(Umfahren)");
        sb.AppendLine($"(A={p.A})");
        sb.AppendLine($"(D={p.Diameter})");
        sb.AppendLine($"(Fertigradius={p.Radius})");
        sb.AppendLine($"(Z={p.Z})");
        sb.AppendLine($"(Startseite={p.StartSide})");
        sb.AppendLine($"(Drehzahl={p.Drehzahl})");
        sb.AppendLine($"(Fräsrichtung={p.Direction})");
        sb.AppendLine(Sz());

        var toolOffset = p.Diameter / 2.0;
        var effectiveA = p.A + toolOffset;
        double x0 = -effectiveA;
        double y0 = -effectiveA;
        double x1 = workW + effectiveA;
        double y1 = workH + effectiveA;
        if (x1 <= x0 || y1 <= y0)
        {
            sb.AppendLine("(Ungültige Umfahrungsbreite)");
            return sb.ToString();
        }

        var z = p.Z;
        var requestedFinishedRadius = Math.Max(0, p.Radius);
        var halfW = (x1 - x0) / 2.0;
        var halfH = (y1 - y0) / 2.0;
        var r = Math.Min(requestedFinishedRadius + toolOffset, Math.Min(halfW, halfH));

        var startSide = p.StartSide?.ToLowerInvariant() ?? string.Empty;
        var direction = p.Direction?.ToLowerInvariant() ?? "gegenlauf";
        var gegenlauf = direction == "gegenlauf";
        double startX, startY;
        double approachX, approachY;
        double exitX, exitY;
        double leadIn = 50.0;
        double arcI = 0, arcJ = 0;
        double exitI = 0, exitJ = 0;
        string entryArcCmd, exitArcCmd;

        switch (startSide)
        {
            case "oben":
                startX = (x0 + x1) / 2.0;
                startY = y1;
                if (gegenlauf)
                {
                    entryArcCmd = "G02";
                    exitArcCmd = "G02";
                    approachX = startX + leadIn;
                    approachY = startY + leadIn;
                    exitX = startX - leadIn;
                    exitY = startY + leadIn;
                    arcI = -leadIn;
                    arcJ = 0;
                    exitI = 0;
                    exitJ = leadIn;
                }
                else
                {
                    entryArcCmd = "G03";
                    exitArcCmd = "G03";
                    approachX = startX - leadIn;
                    approachY = startY + leadIn;
                    exitX = startX + leadIn;
                    exitY = startY + leadIn;
                    arcI = leadIn;
                    arcJ = 0;
                    exitI = 0;
                    exitJ = leadIn;
                }
                break;
            case "rechts":
                startX = x1;
                startY = (y0 + y1) / 2.0;
                if (gegenlauf)
                {
                    entryArcCmd = "G02";
                    exitArcCmd = "G02";
                    approachX = startX + leadIn;
                    approachY = startY - leadIn;
                    exitX = startX + leadIn;
                    exitY = startY + leadIn;
                    arcI = 0;
                    arcJ = leadIn;
                    exitI = leadIn;
                    exitJ = 0;
                }
                else
                {
                    entryArcCmd = "G03";
                    exitArcCmd = "G03";
                    approachX = startX + leadIn;
                    approachY = startY + leadIn;
                    exitX = startX + leadIn;
                    exitY = startY - leadIn;
                    arcI = 0;
                    arcJ = -leadIn;
                    exitI = leadIn;
                    exitJ = 0;
                }
                break;
            case "unten":
                startX = (x0 + x1) / 2.0;
                startY = y0;
                if (gegenlauf)
                {
                    entryArcCmd = "G02";
                    exitArcCmd = "G02";
                    approachX = startX - leadIn;
                    approachY = startY - leadIn;
                    exitX = startX + leadIn;
                    exitY = startY - leadIn;
                    arcI = leadIn;
                    arcJ = 0;
                    exitI = 0;
                    exitJ = -leadIn;
                }
                else
                {
                    entryArcCmd = "G03";
                    exitArcCmd = "G03";
                    approachX = startX + leadIn;
                    approachY = startY - leadIn;
                    exitX = startX - leadIn;
                    exitY = startY - leadIn;
                    arcI = -leadIn;
                    arcJ = 0;
                    exitI = 0;
                    exitJ = -leadIn;
                }
                break;
            case "links":
                startX = x0;
                startY = (y0 + y1) / 2.0;
                if (gegenlauf)
                {
                    entryArcCmd = "G02";
                    exitArcCmd = "G02";
                    approachX = startX - leadIn;
                    approachY = startY + leadIn;
                    exitX = startX - leadIn;
                    exitY = startY - leadIn;
                    arcI = 0;
                    arcJ = -leadIn;
                    exitI = -leadIn;
                    exitJ = 0;
                }
                else
                {
                    entryArcCmd = "G03";
                    exitArcCmd = "G03";
                    approachX = startX - leadIn;
                    approachY = startY - leadIn;
                    exitX = startX - leadIn;
                    exitY = startY + leadIn;
                    arcI = 0;
                    arcJ = leadIn;
                    exitI = -leadIn;
                    exitJ = 0;
                }
                break;
            default:
                entryArcCmd = "G03";
                exitArcCmd = "G02";
                startX = (x0 + x1) / 2.0;
                startY = y0;
                approachX = startX - leadIn;
                approachY = startY - leadIn;
                exitX = startX + leadIn;
                exitY = startY - leadIn;
                arcI = leadIn;
                arcJ = 0;
                exitI = 0;
                exitJ = -leadIn;
                break;
        }

        // Kontur-Moves in eine Liste puffern, damit sie in jedem Z-Schritt wiederverwendet werden.
        var contour = new List<string>();
        string L(double x, double y) => $"G01 X{F(x)} Y{F(y)} F{(int)p.VorschubFxy}";
        string A3(double x, double y, double i, double j) => $"G03 X{F(x)} Y{F(y)} I{F(i)} J{F(j)} F{(int)p.VorschubFxy}";

        if (r <= 0)
        {
            if (startSide == "oben")
            {
                if (gegenlauf) { contour.Add(L(x0,y1)); contour.Add(L(x0,y0)); contour.Add(L(x1,y0)); contour.Add(L(x1,y1)); }
                else           { contour.Add(L(x1,y1)); contour.Add(L(x1,y0)); contour.Add(L(x0,y0)); contour.Add(L(x0,y1)); }
                contour.Add(L(startX, startY));
            }
            else if (startSide == "rechts")
            {
                if (gegenlauf) { contour.Add(L(x1,y1)); contour.Add(L(x0,y1)); contour.Add(L(x0,y0)); contour.Add(L(x1,y0)); }
                else           { contour.Add(L(x1,y0)); contour.Add(L(x0,y0)); contour.Add(L(x0,y1)); contour.Add(L(x1,y1)); }
                contour.Add(L(startX, startY));
            }
            else if (startSide == "links")
            {
                if (gegenlauf) { contour.Add(L(x0,y0)); contour.Add(L(x1,y0)); contour.Add(L(x1,y1)); contour.Add(L(x0,y1)); }
                else           { contour.Add(L(x0,y1)); contour.Add(L(x1,y1)); contour.Add(L(x1,y0)); contour.Add(L(x0,y0)); }
                contour.Add(L(startX, startY));
            }
            else
            {
                if (gegenlauf) { contour.Add(L(x1,y0)); contour.Add(L(x1,y1)); contour.Add(L(x0,y1)); contour.Add(L(x0,y0)); }
                else           { contour.Add(L(x0,y0)); contour.Add(L(x0,y1)); contour.Add(L(x1,y1)); contour.Add(L(x1,y0)); }
                contour.Add(L(startX, startY));
            }
        }
        else
        {
            if (startSide == "oben")
            {
                contour.Add(L(x0+r,y1)); contour.Add(A3(x0,y1-r,0,-r)); contour.Add(L(x0,y0+r));
                contour.Add(A3(x0+r,y0,r,0)); contour.Add(L(x1-r,y0)); contour.Add(A3(x1,y0+r,0,r));
                contour.Add(L(x1,y1-r)); contour.Add(A3(x1-r,y1,-r,0)); contour.Add(L(startX,startY));
            }
            else if (startSide == "rechts")
            {
                contour.Add(L(x1,y1-r)); contour.Add(A3(x1-r,y1,-r,0)); contour.Add(L(x0+r,y1));
                contour.Add(A3(x0,y1-r,0,-r)); contour.Add(L(x0,y0+r)); contour.Add(A3(x0+r,y0,r,0));
                contour.Add(L(x1-r,y0)); contour.Add(A3(x1,y0+r,0,r)); contour.Add(L(startX,startY));
            }
            else if (startSide == "links")
            {
                contour.Add(L(x0,y0+r)); contour.Add(A3(x0+r,y0,r,0)); contour.Add(L(x1-r,y0));
                contour.Add(A3(x1,y0+r,0,r)); contour.Add(L(x1,y1-r)); contour.Add(A3(x1-r,y1,-r,0));
                contour.Add(L(x0+r,y1)); contour.Add(A3(x0,y1-r,0,-r)); contour.Add(L(startX,startY));
            }
            else
            {
                contour.Add(L(x1-r,y0)); contour.Add(A3(x1,y0+r,0,r)); contour.Add(L(x1,y1-r));
                contour.Add(A3(x1-r,y1,-r,0)); contour.Add(L(x0+r,y1)); contour.Add(A3(x0,y1-r,0,-r));
                contour.Add(L(x0,y0+r)); contour.Add(A3(x0+r,y0,r,0)); contour.Add(L(startX,startY));
            }
        }

        // Z-Tiefen bestimmen: bei Mehrfachzustellung schrittweise bis zur Endtiefe
        var zLevels = new List<double>();
        if (p.MehrfachZustellung && p.ZZustellung > 0 && z < 0)
        {
            double step = p.ZZustellung;
            for (double cz = -step; cz > z; cz -= step)
                zLevels.Add(Math.Round(cz, 4));
        }
        zLevels.Add(z);

        sb.AppendLine($"G00 X{F(approachX)} Y{F(approachY)}");
        for (int pass = 0; pass < zLevels.Count; pass++)
        {
            sb.AppendLine($"G01 Z{F(zLevels[pass])} F{(int)p.VorschubFz}");
            sb.AppendLine($"{entryArcCmd} X{F(startX)} Y{F(startY)} I{F(arcI)} J{F(arcJ)} F{(int)p.VorschubFxy}");
            foreach (var line in contour) sb.AppendLine(line);
            sb.AppendLine($"{exitArcCmd} X{F(exitX)} Y{F(exitY)} I{F(exitI)} J{F(exitJ)} F{(int)p.VorschubFxy}");
            if (pass < zLevels.Count - 1)
                sb.AppendLine($"G00 X{F(approachX)} Y{F(approachY)}");
        }
        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string Rechteck(RechteckParams p, double workW, double workH)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(Rechteck fräsen)");
        sb.AppendLine($"(B={F(p.Breite)} H={F(p.Hoehe)}, Fraesung={p.Fraesung}, {p.Laufrichtung})");

        // Position der unteren-linken Ecke berechnen
        var (refX, refY) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);
        var (bx, by) = p.Bezugspunkt switch
        {
            "Unten links"  => (0.0,           0.0),
            "Unten Mitte"  => (-p.Breite/2,   0.0),
            "Unten rechts" => (-p.Breite,      0.0),
            "Links Mitte"  => (0.0,           -p.Hoehe/2),
            "Mitte"        => (-p.Breite/2,   -p.Hoehe/2),
            "Rechts Mitte" => (-p.Breite,     -p.Hoehe/2),
            "Oben links"   => (0.0,           -p.Hoehe),
            "Oben Mitte"   => (-p.Breite/2,   -p.Hoehe),
            _              => (-p.Breite,     -p.Hoehe)   // Oben rechts
        };
        double x0 = refX + bx, y0 = refY + by;
        double x1 = x0 + p.Breite, y1 = y0 + p.Hoehe;

        // Werkzeug-Offset je nach Fräsung
        double off = p.Fraesung switch
        {
            "Aussen" =>  p.FraeserD / 2.0,
            "Innen"  => -p.FraeserD / 2.0,
            _        =>  0.0
        };
        double mX0 = x0 - off, mY0 = y0 - off;
        double mX1 = x1 + off, mY1 = y1 + off;

        if (mX1 - mX0 < 1e-6 || mY1 - mY0 < 1e-6)
        {
            sb.AppendLine("(Rechteck zu klein für gewählte Fräsung/Fräser)");
            return sb.ToString();
        }

        // v  = Fertigkante-Eckenradius (auf halbe Werkstückdimension begrenzt)
        // ar = Werkzeugmittelpunkt-Bogenradius = v + off
        //      Aussen (off>0): ar > v  — Bogen um konvexe Außenecke herum
        //      Mittig (off=0): ar = v
        //      Innen  (off<0): ar = v - |off|; <= 0 → keine Bögen
        double v  = Math.Max(0.0, Math.Min(p.Verrundung, Math.Min((x1-x0)/2.0, (y1-y0)/2.0)));
        double ar = v + off;   // Bogenradius der Werkzeugmittelpunktbahn

        bool gegenlauf = p.Laufrichtung != "Gleichlauf";
        double startX  = (mX0 + mX1) / 2.0;
        double startY  = mY0;

        // Eintauchrampe: entlang der Unterkante zur nächsten Ecke hin und zurück
        void AppendEntry(double prevZ, double nextZ)
        {
            double dz    = Math.Abs(nextZ - prevZ);
            double angle = p.Eintauchwinkel;
            if (angle >= 90 || angle <= 0 || dz < 1e-6)
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
                return;
            }
            double rampLen = (dz / 2.0) / Math.Tan(angle * Math.PI / 180.0);
            // Erster Konturpunkt der Unterseite
            double firstX = ar > 1e-6
                ? (gegenlauf ? x1 - v : x0 + v)
                : (gegenlauf ? mX1    : mX0);
            double segLen  = Math.Abs(firstX - startX);
            if (segLen >= rampLen * 2)
            {
                double midZ  = prevZ - dz / 2.0;
                double rampX = gegenlauf ? startX + rampLen : startX - rampLen;
                sb.AppendLine($"G01 X{F(rampX)} Y{F(startY)} Z{F(midZ)} F{(int)p.VorschubFz}");
                sb.AppendLine($"G01 X{F(startX)} Y{F(startY)} Z{F(nextZ)} F{(int)p.VorschubFz}");
            }
            else
            {
                sb.AppendLine($"G01 Z{F(nextZ)} F{(int)p.VorschubFz}");
            }
        }

        var contour = new List<string>();
        string L(double x, double y) =>
            $"G01 X{F(x)} Y{F(y)} F{(int)p.Vorschub}";
        string A3(double x, double y, double i, double j) =>
            $"G03 X{F(x)} Y{F(y)} I{F(i)} J{F(j)} F{(int)p.Vorschub}";
        string A2(double x, double y, double i, double j) =>
            $"G02 X{F(x)} Y{F(y)} I{F(i)} J{F(j)} F{(int)p.Vorschub}";

        // Bögen an allen vier Ecken — unified formula:
        //   Tangentenpunkte auf den Geradestücken hängen von v (Fertigkante) ab.
        //   Bogenmittelpunkte liegen an den Werkstückecken, versetzt um v.
        //   I/J = ar (= v + off).
        //   ar <= 0: keine Bögen (z.B. Innen mit kleinem v), einfache Ecken.
        if (ar > 1e-6)
        {
            if (gegenlauf)
            {
                // Gegenlauf (G03): CW-Pfad, BR→TR→TL→BL
                contour.Add(L(x1-v, mY0));
                contour.Add(A3(mX1,  y0+v,   0,  ar));  // BR: Mitte (x1-v, y0+v)
                contour.Add(L(mX1,  y1-v));
                contour.Add(A3(x1-v, mY1,  -ar,   0));  // TR: Mitte (x1-v, y1-v)
                contour.Add(L(x0+v, mY1));
                contour.Add(A3(mX0,  y1-v,   0, -ar));  // TL: Mitte (x0+v, y1-v)
                contour.Add(L(mX0,  y0+v));
                contour.Add(A3(x0+v, mY0,   ar,   0));  // BL: Mitte (x0+v, y0+v)
            }
            else
            {
                // Gleichlauf (G02): CCW-Pfad, BL→TL→TR→BR
                contour.Add(L(x0+v, mY0));
                contour.Add(A2(mX0,  y0+v,   0,  ar));  // BL: Mitte (x0+v, y0+v)
                contour.Add(L(mX0,  y1-v));
                contour.Add(A2(x0+v, mY1,   ar,   0));  // TL: Mitte (x0+v, y1-v)
                contour.Add(L(x1-v, mY1));
                contour.Add(A2(mX1,  y1-v,   0, -ar));  // TR: Mitte (x1-v, y1-v)
                contour.Add(L(mX1,  y0+v));
                contour.Add(A2(x1-v, mY0,  -ar,   0));  // BR: Mitte (x1-v, y0+v)
            }
        }
        else
        {
            if (gegenlauf)
            {
                contour.Add(L(mX1,mY0)); contour.Add(L(mX1,mY1));
                contour.Add(L(mX0,mY1)); contour.Add(L(mX0,mY0));
            }
            else
            {
                contour.Add(L(mX0,mY0)); contour.Add(L(mX0,mY1));
                contour.Add(L(mX1,mY1)); contour.Add(L(mX1,mY0));
            }
        }
        contour.Add(L(startX, startY));

        // Z-Stufen
        var zLevels = new List<double>();
        if (p.MehrfachZustellung && p.ZZustellung > 0 && p.ZTiefe < 0)
        {
            for (double cz = -p.ZZustellung; cz > p.ZTiefe; cz -= p.ZZustellung)
                zLevels.Add(Math.Round(cz, 4));
        }
        zLevels.Add(p.ZTiefe);

        sb.AppendLine(Sz());
        sb.AppendLine($"G00 X{F(startX)} Y{F(startY)}");
        double prevZ = 0;
        for (int pass = 0; pass < zLevels.Count; pass++)
        {
            sb.AppendLine($"G00 Z{F(prevZ)}");
            AppendEntry(prevZ, zLevels[pass]);
            foreach (var ln in contour) sb.AppendLine(ln);
            prevZ = zLevels[pass];
        }
        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string Kreis(KreisParams p, double workW, double workH)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(Kreis fräsen)");
        sb.AppendLine($"(R={F(p.Radius)}, Fraesung={p.Fraesung}, {p.Laufrichtung})");

        if (p.IsTasche)
        {
            var tp = new KreistascheParams(p.XRel, p.YRel, p.Radius * 2,
                p.ZTiefe, p.ZZustellung > 0 ? p.ZZustellung : 2, p.Eintauchwinkel,
                p.FraeserD, 0.5, p.Vorschub, p.VorschubFz, p.Drehzahl, p.Bezugspunkt);
            return Kreistasche(tp, workW, workH);
        }

        var (cx, cy) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);
        double off = p.Fraesung switch
        {
            "Aussen" =>  p.FraeserD / 2.0,
            "Innen"  => -p.FraeserD / 2.0,
            _        =>  0.0
        };
        double R = p.Radius + off;
        if (R < 1e-6) return sb.AppendLine("(Kreis: Radius zu klein für Werkzeug)").ToString();

        // Gegenlauf = G03 (CCW) für Außen, G02 für Innen; Gleichlauf umgekehrt
        bool ccw = (p.Fraesung == "Innen") ? p.Laufrichtung == "Gleichlauf"
                                            : p.Laufrichtung == "Gegenlauf";
        string arc = ccw ? "G03" : "G02";

        double feed    = p.Vorschub;
        double feedZ   = p.VorschubFz;
        double z       = -Math.Abs(p.ZTiefe);
        double zStep   = Math.Abs(p.ZZustellung);
        double startX  = cx;
        double startY  = cy - R;   // Startpunkt unten

        string Sz() => $"G00 Z{F(5)}";

        sb.AppendLine(Sz());
        sb.AppendLine($"G00 X{F(startX)} Y{F(startY)}");

        double curZ = 0;
        while (curZ > z)
        {
            double prevZ = curZ;
            curZ = Math.Max(z, curZ - (p.MehrfachZustellung && zStep > 1e-6 ? zStep : Math.Abs(z)));

            double dz    = Math.Abs(curZ - prevZ);
            double angle = p.Eintauchwinkel;
            sb.AppendLine($"G00 Z{F(prevZ)}");
            // I/J vom Startpunkt (unten) zum Mittelpunkt
            double iStart = cx - startX;   // = 0
            double jStart = cy - startY;   // = R

            if (angle > 0 && angle < 90 && dz > 1e-6)
            {
                double arcLen = (dz / 2.0) / Math.Tan(angle * Math.PI / 180.0);
                if (arcLen <= R * Math.PI)
                {
                    // Bogenförmige V-Rampe ab Startwinkel -π/2 (unten)
                    double a      = arcLen / R;
                    double midZ2  = prevZ - dz / 2.0;
                    double sign   = ccw ? 1.0 : -1.0;
                    double ex     = cx + R * Math.Cos(-Math.PI / 2 + sign * a);
                    double ey     = cy + R * Math.Sin(-Math.PI / 2 + sign * a);
                    string arcBack = ccw ? "G02" : "G03";
                    sb.AppendLine($"{arc} X{F(ex)} Y{F(ey)} Z{F(midZ2)} I{F(iStart)} J{F(jStart)} F{(int)feedZ}");
                    sb.AppendLine($"{arcBack} X{F(startX)} Y{F(startY)} Z{F(curZ)} I{F(cx - ex)} J{F(cy - ey)} F{(int)feedZ}");
                }
                else
                    sb.AppendLine($"G01 Z{F(curZ)} F{(int)feedZ}");
            }
            else
                sb.AppendLine($"G01 Z{F(curZ)} F{(int)feedZ}");

            // Voller Kreis: zwei Halbkreise (unten → oben → unten)
            double midY = cy + R;
            sb.AppendLine($"{arc} X{F(cx)} Y{F(midY)} I{F(iStart)} J{F(jStart)} F{(int)feed}");
            sb.AppendLine($"{arc} X{F(startX)} Y{F(startY)} I0 J{F(cy - midY)} F{(int)feed}");

            if (curZ > z)
            {
                sb.AppendLine(Sz());
                sb.AppendLine($"G00 X{F(startX)} Y{F(startY)}");
            }
        }

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public static string NEck(NEckParams p, double workW, double workH)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"({p.Ecken}-Eck fräsen)");
        sb.AppendLine($"(R={F(p.Radius)}, Fraesung={p.Fraesung}, {p.Laufrichtung})");

        if (p.Ecken < 3)
        {
            sb.AppendLine($"(Fehler: mindestens 3 Ecken erforderlich, {p.Ecken} angegeben)");
            return sb.ToString();
        }

        var (cx, cy) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);
        double off = p.Fraesung switch
        {
            "Aussen" =>  p.FraeserD / 2.0,
            "Innen"  => -p.FraeserD / 2.0,
            _        =>  0.0
        };
        double R = p.Radius + off;
        if (R < 1e-6) return sb.AppendLine("(NEck: Radius zu klein für Werkzeug)").ToString();

        bool ccw = (p.Fraesung == "Innen") ? p.Laufrichtung == "Gleichlauf"
                                            : p.Laufrichtung == "Gegenlauf";
        string arc = ccw ? "G03" : "G02";

        double feed    = p.Vorschub;
        double feedZ   = p.VorschubFz;
        double z       = -Math.Abs(p.ZTiefe);
        double zStep   = Math.Abs(p.ZZustellung);

        // Eckpunkte des Polygons berechnen
        var points = new List<(double x, double y)>();
        for (int i = 0; i < p.Ecken; i++)
        {
            double angle = 2 * Math.PI * i / p.Ecken + p.RotationGrad * Math.PI / 180.0 - Math.PI / 2;
            double px = cx + R * Math.Cos(angle);
            double py = cy + R * Math.Sin(angle);
            points.Add((px, py));
        }

        string Sz() => $"G00 Z{F(5)}";
        sb.AppendLine(Sz());

        var (startX, startY) = points[0];
        sb.AppendLine($"G00 X{F(startX)} Y{F(startY)}");

        double curZ = 0;
        while (curZ > z)
        {
            double prevZ = curZ;
            curZ = Math.Max(z, curZ - (p.MehrfachZustellung && zStep > 1e-6 ? zStep : Math.Abs(z)));

            sb.AppendLine($"G00 Z{F(prevZ)}");
            sb.AppendLine($"G01 Z{F(curZ)} F{(int)feedZ}");

            // Polygon-Segmente fräsen (Linien zwischen den Eckpunkten)
            for (int i = 0; i < points.Count; i++)
            {
                var (nextX, nextY) = points[(i + 1) % points.Count];
                sb.AppendLine($"G01 X{F(nextX)} Y{F(nextY)} F{(int)feed}");
            }

            if (curZ > z)
            {
                sb.AppendLine(Sz());
                sb.AppendLine($"G00 X{F(startX)} Y{F(startY)}");
            }
        }

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    // Berechnet versetzte Segmentpunkte und liefert (Startpunkt, Liste von G-Code-Moves).
    // Konvexe Ecken → G02/G03-Bogen; konkave Ecken → Miter-Schnittpunkt.
    private static ((double X, double Y) Start, List<string> Moves) BuildOffsetMoves(
        List<(double X, double Y)> pts, double offset, double feed)
    {
        int n = pts.Count;

        // Einheitsnormalen pro Segment (links-Normal: (-dy, dx))
        var norms = new (double x, double y)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = pts[i + 1].X - pts[i].X;
            double dy = pts[i + 1].Y - pts[i].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            norms[i] = len < 1e-9 ? (0, 0) : (-dy / len, dx / len);
        }

        // Segment-Start- und Endpunkte im versetzten Pfad
        var segS = new (double X, double Y)[n - 1];
        var segE = new (double X, double Y)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            segS[i] = (pts[i].X     + norms[i].x * offset, pts[i].Y     + norms[i].y * offset);
            segE[i] = (pts[i + 1].X + norms[i].x * offset, pts[i + 1].Y + norms[i].y * offset);
        }

        // 0=Miter, 1=G02(CW), 2=G03(CCW) pro innerer Ecke
        var cornerKind = new int[n - 2];
        for (int i = 0; i < n - 2; i++)
        {
            double d1x = pts[i + 1].X - pts[i].X,     d1y = pts[i + 1].Y - pts[i].Y;
            double d2x = pts[i + 2].X - pts[i + 1].X, d2y = pts[i + 2].Y - pts[i + 1].Y;
            double cross = d1x * d2y - d1y * d2x;

            if (Math.Abs(offset) > 1e-9 && cross * offset < 0)
            {
                // Konvexe Ecke: Bogen um pts[i+1]
                // segE[i] und segS[i+1] bleiben wie berechnet (je auf ihrer Segmentnormalen)
                cornerKind[i] = cross < 0 ? 1 : 2;
            }
            else
            {
                // Konkave Ecke oder Mitte: Miter-Schnittpunkt
                var (n1x, n1y) = norms[i];
                var (n2x, n2y) = norms[i + 1];
                double bx  = n1x + n2x, by = n1y + n2y;
                double dot = bx * n1x + by * n1y;
                double mx, my;
                if (dot < 1e-9) { mx = pts[i+1].X + n1x * offset; my = pts[i+1].Y + n1y * offset; }
                else             { mx = pts[i+1].X + bx * (offset / dot); my = pts[i+1].Y + by * (offset / dot); }
                segE[i]     = (mx, my);
                segS[i + 1] = (mx, my);
                cornerKind[i] = 0;
            }
        }

        var moves = new List<string>();
        for (int i = 0; i < n - 1; i++)
        {
            moves.Add($"G01 X{F(segE[i].X)} Y{F(segE[i].Y)} F{feed}");

            if (i < n - 2 && cornerKind[i] != 0)
            {
                // Bogen: Zentrum = pts[i+1], aktuelle Pos = segE[i], Ziel = segS[i+1]
                double ix = pts[i + 1].X - segE[i].X;
                double ij = pts[i + 1].Y - segE[i].Y;
                string cmd = cornerKind[i] == 1 ? "G02" : "G03";
                moves.Add($"{cmd} X{F(segS[i+1].X)} Y{F(segS[i+1].Y)} I{F(ix)} J{F(ij)} F{feed}");
            }
        }

        return (segS[0], moves);
    }

#if false // PfadFräsen (deaktiviert)
    public static string PfadFräsen(PfadFräsenParams p, double workW, double workH)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(Pfad Fräsen)");
        sb.AppendLine($"(Punkte: {p.Punkte.Count}, Koordinaten absolut)");
        sb.AppendLine($"(D={p.FraeserD}, Seite={p.Seite})");
        sb.AppendLine();
        sb.AppendLine($"M03 S{p.Drehzahl}");
        sb.AppendLine(Sz());

        double radius = p.FraeserD / 2.0;
        double offset = p.Seite switch
        {
            "Links"  =>  radius,
            "Rechts" => -radius,
            _        =>  0.0,
        };

        var (start, moves) = BuildOffsetMoves(p.Punkte, offset, p.Vorschub);
        sb.AppendLine($"G00 X{F(start.X)} Y{F(start.Y)}");

        double depth    = -Math.Abs(p.Z);
        double step     = Math.Abs(p.Zustellung);
        double currentZ = 0;

        while (currentZ > depth)
        {
            currentZ = Math.Max(depth, currentZ - step);
            sb.AppendLine($"G01 Z{F(currentZ)} F{p.Vorschub}");

            foreach (var move in moves)
                sb.AppendLine(move);

            if (currentZ > depth)
            {
                sb.AppendLine(Sz());
                sb.AppendLine($"G00 X{F(start.X)} Y{F(start.Y)}");
            }
        }

        sb.AppendLine(Sz());
        sb.AppendLine("M05");
        return sb.ToString();
    }
#endif // PfadFräsen Ende

    public static string PfadFräsen(IReadOnlyList<PfadPunktParams> path, double workW, double workH)
    {
        if (path.Count == 0) return string.Empty;
        var sp    = path[0];
        double z  = -Math.Abs(sp.ZTiefe);
        double r  = sp.FraeserD / 2.0;
        bool corr = sp.Radiuskorrektur != "Mittig";
        double sg = sp.Radiuskorrektur == "Links" ? 1.0 : -1.0;

        // Build absolute endpoint coords + arc midpoints (arcMids[i] = midpoint for segment i-1→i)
        var pts     = new List<(double x, double y)>();
        var arcMids = new List<(double mx, double my)?>();
        var splineTypes = new List<(int originalIdx, PfadPunktTyp typ)>(); // Track Spline segments

        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            (double x, double y) pt;
            if (p.Bezugspunkt == "Letzter Punkt" && pts.Count > 0)
                pt = (pts[^1].x + p.XRel, pts[^1].y + p.YRel);
            else
                pt = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);
            pts.Add(pt);
            splineTypes.Add((i, p.Typ));

            if (i > 0 && p.Typ == PfadPunktTyp.Bogen)
            {
                arcMids.Add(ResolveBogenMid(pts[i - 1], pts[i], p, workW, workH));
            }
            else
            {
                arcMids.Add(null);
            }
        }

        // Diskretisiere Spline-Segmente in Linien
        for (int i = path.Count - 1; i >= 1; i--)
        {
            if (path[i].Typ == PfadPunktTyp.Spline)
            {
                // Finde alle aufeinanderfolgenden Spline-Punkte
                int splineStart = i;
                while (splineStart > 1 && path[splineStart - 1].Typ == PfadPunktTyp.Spline)
                    splineStart--;

                // Interpoliere alle Spline-Punkte zusammen
                var splinePtsList = new List<(double x, double y)>();
                for (int k = splineStart - 1; k <= i; k++)
                    splinePtsList.Add(pts[k]);

                var interpolated = InterpolateFullSplineMultiple(splinePtsList, path[i].SplineModus, path[i].SplineTension, path[i].SplineSegmentLength);

                if (interpolated.Count > 1)
                {
                    // Entferne alle Spline-Punkte
                    for (int k = i; k >= splineStart; k--)
                    {
                        pts.RemoveAt(k);
                        splineTypes.RemoveAt(k);
                        if (k < arcMids.Count) arcMids.RemoveAt(k);
                    }

                    // Füge interpolierte Punkte ein (in korrekter Reihenfolge)
                    for (int j = 0; j < interpolated.Count; j++)
                    {
                        pts.Insert(splineStart + j, interpolated[j]);
                        splineTypes.Insert(splineStart + j, (splineStart, PfadPunktTyp.Linie));
                        if (splineStart + j <= arcMids.Count) arcMids.Insert(splineStart + j, null);
                    }
                }
                i = splineStart - 1;
            }
        }

        // Aktualisiere arcMids für neue Länge
        while (arcMids.Count < pts.Count)
            arcMids.Add(null);

        bool hasBogen = arcMids.Skip(1).Any(m => m.HasValue);

        // Geschlossener Pfad: erster und letzter Punkt identisch (< 0.01 mm)
        // Aber nicht wenn der Pfad Spline-Punkte enthält (sollten nicht automatisch geschlossen werden)
        bool hasSpline = path.Any(p => p.Typ == PfadPunktTyp.Spline);
        bool closed = !hasSpline && pts.Count >= 3 &&
            Math.Sqrt(Math.Pow(pts[0].x - pts[^1].x, 2) + Math.Pow(pts[0].y - pts[^1].y, 2)) < 0.01;

        // Corner rounding: Startpunkt-R als globaler Fallback, Einzelpunkte können überschreiben
        double globalR = sp.Verrundung;
        var verrundungen = path.Select(p => p.Verrundung > 1e-10 ? p.Verrundung : globalR).ToList();
        bool hasRounding = verrundungen.Any(v => v > 1e-10);

        List<PathMove> moves;
        if (hasBogen)
        {
            // Bei gemischten Pfaden (Linien + Bögen): Linie→Linie-Ecken mit Verrundung vorverarbeiten
            var (rPts, rMids) = hasRounding
                ? InsertLineCornerArcs(pts, arcMids, verrundungen, closed)
                : (pts, arcMids);
            // BuildBogenMoves erhält bereits die vollen Punkte inkl. des doppelten
            // Schlusspunkts (pts[^1] == pts[0]) und schließt den Pfad damit selbst
            // (letzter Move endet exakt auf moves[0]) – ein zusätzliches
            // moves.Add(moves[0]) hier würde nur einen doppelten/Null-Move
            // (und bei Bogen-Abschluss ein doppeltes G02/G03) erzeugen.
            moves = BuildBogenMoves(rPts, rMids, corr ? r : 0, corr ? sg : 0, closed);
        }
        else
        {
            // Unique points (no duplicate endpoint for closed path)
            var uniquePts = closed ? pts.Take(pts.Count - 1).ToList() : pts;

            if (corr && r > 1e-10 && uniquePts.Count >= 2)
            {
                moves = closed
                    ? ComputeClosedOffsetMoves(uniquePts, r, sg, verrundungen)
                    : ComputeOffsetMoves(uniquePts, r, sg, verrundungen);
            }
            else if (hasRounding && uniquePts.Count >= 3)
            {
                // Mittig mit Verrundung: Offset = 0, Bögen direkt in Werkstückpfad
                moves = closed
                    ? ComputeClosedOffsetMoves(uniquePts, 0, 1, verrundungen)
                    : ComputeOffsetMoves(uniquePts, 0, 1, verrundungen);
                if (closed && moves.Count > 0) moves.Add(moves[0]);
            }
            else
            {
                moves = uniquePts.Select(p => new PathMove(p.x, p.y)).ToList();
                if (closed) moves.Add(moves[0]); // Pfad schliessen
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("(Pfad Fräsen)");
        sb.AppendLine(Sz());
        sb.AppendLine($"G00 X{F(moves[0].X)} Y{F(moves[0].Y)}");

        double zStep = Math.Abs(sp.ZZustellung);
        double curZ  = 0;

        void AppendEntry(double prevZ, double nextZ)
        {
            double dz    = Math.Abs(nextZ - prevZ);
            double angle = sp.Eintauchwinkel;
            if (moves.Count >= 2 && !moves[1].IsArc && angle > 0 && angle < 90 && dz > 1e-6)
            {
                double dx      = moves[1].X - moves[0].X;
                double dy      = moves[1].Y - moves[0].Y;
                double segLen  = Math.Sqrt(dx * dx + dy * dy);
                double rampLen = (dz / 2.0) / Math.Tan(angle * Math.PI / 180.0);
                if (segLen >= rampLen * 2 && segLen > 1e-9)
                {
                    double ux = dx / segLen, uy = dy / segLen;
                    double midZ = prevZ - dz / 2.0;
                    sb.AppendLine($"G01 X{F(moves[0].X + ux * rampLen)} Y{F(moves[0].Y + uy * rampLen)} Z{F(midZ)} F{(int)sp.VorschubFz}");
                    sb.AppendLine($"G01 X{F(moves[0].X)} Y{F(moves[0].Y)} Z{F(nextZ)} F{(int)sp.VorschubFz}");
                    return;
                }
            }
            sb.AppendLine($"G01 Z{F(nextZ)} F{(int)sp.VorschubFz}");
        }

        while (curZ > z)
        {
            double prevDepth = curZ;
            curZ = Math.Max(z, curZ - zStep);
            sb.AppendLine($"G00 Z{F(prevDepth)}");
            AppendEntry(prevDepth, curZ);

            for (int i = 1; i < moves.Count; i++)
            {
                var mv = moves[i];
                if (mv.IsArc)
                    sb.AppendLine($"{(mv.CW ? "G02" : "G03")} X{F(mv.X)} Y{F(mv.Y)} I{F(mv.I)} J{F(mv.J)} F{(int)sp.Vorschub}");
                else
                    sb.AppendLine($"G01 X{F(mv.X)} Y{F(mv.Y)} F{(int)sp.Vorschub}");
            }

            if (curZ > z)
            {
                sb.AppendLine(Sz());
                sb.AppendLine($"G00 X{F(moves[0].X)} Y{F(moves[0].Y)}");
            }
        }

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    private record struct PathMove(double X, double Y, bool IsArc = false,
                                   double I = 0, double J = 0, bool CW = false);

    // Kreismittelpunkt und Richtung aus drei Punkten berechnen
    private static (double cx, double cy, double R, bool cw) ArcFrom3Points(
        double x1, double y1, double xm, double ym, double x2, double y2)
    {
        // Senkrechte Halbierende von P1-Pm und Pm-P2 → Schnittpunkt = Mittelpunkt
        double ax = (x1 + xm) / 2, ay = (y1 + ym) / 2;
        double dax = ym - y1, day = x1 - xm;

        double bx = (xm + x2) / 2, by = (ym + y2) / 2;
        double dbx = y2 - ym, dby = xm - x2;

        double det = dax * (-dby) + dbx * day;
        if (Math.Abs(det) < 1e-12)
            return (0, 0, double.PositiveInfinity, false);   // Punkte kollinear → Gerade

        double t  = ((bx - ax) * (-dby) + dbx * (by - ay)) / det;
        double cx = ax + t * dax;
        double cy = ay + t * day;
        double R  = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));

        // Kreuzprodukt (P1→Pm) × (P1→P2): negativ = im Uhrzeigersinn (G02)
        double cross = (xm - x1) * (y2 - y1) - (ym - y1) * (x2 - x1);
        return (cx, cy, R, cross < 0);
    }

    // Moves für Pfad mit Bogen-Segmenten, optional mit Werkzeug-Radiuskorrektur.
    // r = Werkzeugradius, sg = +1 (Links) oder -1 (Rechts); r=0 → kein Offset.
    // Konvexe Ecken erhalten einen Umfahrbogen (Radius r um den Eckpunkt).
    // Bei geschlossenem Pfad wird auch die Naht als Ecke behandelt.
    private static List<PathMove> BuildBogenMoves(
        List<(double x, double y)> pts,
        List<(double mx, double my)?> arcMids,
        double r = 0, double sg = 0, bool closed = false)
    {
        int n = pts.Count;

        if (!(r > 1e-10 && Math.Abs(sg) > 1e-10))
        {
            // Kein Offset: Originalverhalten
            var moves0 = new List<PathMove> { new PathMove(pts[0].x, pts[0].y) };
            for (int i = 1; i < n; i++)
            {
                var mid0 = arcMids[i];
                if (mid0.HasValue)
                {
                    var (x1, y1) = pts[i - 1];
                    var (mx, my) = mid0.Value;
                    var (x2, y2) = pts[i];
                    var (cx, cy, rr, cw) = ArcFrom3Points(x1, y1, mx, my, x2, y2);
                    if (double.IsInfinity(rr))
                        moves0.Add(new PathMove(x2, y2));
                    else
                        moves0.Add(new PathMove(x2, y2, IsArc: true, I: cx - x1, J: cy - y1, CW: cw));
                }
                else
                {
                    moves0.Add(new PathMove(pts[i].x, pts[i].y));
                }
            }
            return moves0;
        }

        // --- Mit Radiuskorrektur ---
        int m = n - 1; // Anzahl Segmente
        var segArc  = new bool[m];
        var segCxA  = new double[m];
        var segCyA  = new double[m];
        var segRNew = new double[m];
        var segCW   = new bool[m];
        var segDir  = new (double x, double y)[m];
        var segNrm  = new (double x, double y)[m];
        var segS    = new (double x, double y)[m]; // versetzter Startpunkt
        var segE    = new (double x, double y)[m]; // versetzter Endpunkt

        for (int s = 0; s < m; s++)
        {
            var p1 = pts[s]; var p2 = pts[s + 1];
            var mid = arcMids[s + 1];
            bool isArcSeg = false;
            if (mid.HasValue)
            {
                var (cx, cy, R, cw) = ArcFrom3Points(p1.x, p1.y, mid.Value.mx, mid.Value.my, p2.x, p2.y);
                if (!double.IsInfinity(R))
                {
                    // CW-Bogen: links = außen → R_new = R + sg*r; CCW: R_new = R - sg*r
                    double rNew = Math.Max(0, R + (cw ? 1.0 : -1.0) * sg * r);
                    segArc[s] = true; segCxA[s] = cx; segCyA[s] = cy; segRNew[s] = rNew; segCW[s] = cw;
                    isArcSeg = true;
                    double d1 = Math.Sqrt(Math.Pow(p1.x - cx, 2) + Math.Pow(p1.y - cy, 2));
                    double d2 = Math.Sqrt(Math.Pow(p2.x - cx, 2) + Math.Pow(p2.y - cy, 2));
                    segS[s] = d1 > 1e-12 ? (cx + (p1.x - cx) / d1 * rNew, cy + (p1.y - cy) / d1 * rNew) : p1;
                    segE[s] = d2 > 1e-12 ? (cx + (p2.x - cx) / d2 * rNew, cy + (p2.y - cy) / d2 * rNew) : p2;
                }
            }
            if (!isArcSeg)
            {
                ComputeDirNrm(p1, p2, sg, out segDir[s], out segNrm[s]);
                segS[s] = (p1.x + segNrm[s].x * r, p1.y + segNrm[s].y * r);
                segE[s] = (p2.x + segNrm[s].x * r, p2.y + segNrm[s].y * r);
            }
        }

        // Tangentenrichtung am Segmentende/-anfang (für Eckenerkennung)
        (double x, double y) SegEndTan(int s) =>
            segArc[s] ? BogenArcTangentAt(segCxA[s], segCyA[s], segE[s].x, segE[s].y, segCW[s]) : segDir[s];
        (double x, double y) SegStartTan(int s) =>
            segArc[s] ? BogenArcTangentAt(segCxA[s], segCyA[s], segS[s].x, segS[s].y, segCW[s]) : segDir[s];

        // effStart/effEnd: defaults sind segS/segE; bei konkaven Ecken → Schnittpunkt
        var effStart  = new (double x, double y)[m];
        var effEnd    = new (double x, double y)[m];
        var vtxConvex = new bool[n];
        for (int s = 0; s < m; s++) { effStart[s] = segS[s]; effEnd[s] = segE[s]; }

        void ProcessVertex(int vIdx, int prevSeg, int nextSeg, (double x, double y) vtxPt)
        {
            var tanOut = SegEndTan(prevSeg);
            var tanIn  = SegStartTan(nextSeg);
            double cross = tanOut.x * tanIn.y - tanOut.y * tanIn.x;
            bool isConvex = Math.Abs(cross) > 0.01 && (sg > 0 ? cross < 0 : cross > 0);
            vtxConvex[vIdx] = isConvex;
            if (!isConvex)
            {
                // Konkave Ecke oder Gerade: Schnittpunkt der versetzten Segmente
                (double x, double y) conn;
                if (!segArc[prevSeg] && !segArc[nextSeg])
                    conn = LineIntersect(segS[prevSeg], segDir[prevSeg], segS[nextSeg], segDir[nextSeg]) ?? segE[prevSeg];
                else if (!segArc[prevSeg])
                    conn = BogenPickClosest(BogenLineCircleIntersect(segS[prevSeg], segDir[prevSeg], segCxA[nextSeg], segCyA[nextSeg], segRNew[nextSeg]), vtxPt, segE[prevSeg]);
                else if (!segArc[nextSeg])
                    conn = BogenPickClosest(BogenLineCircleIntersect(segS[nextSeg], segDir[nextSeg], segCxA[prevSeg], segCyA[prevSeg], segRNew[prevSeg]), vtxPt, segE[prevSeg]);
                else
                    conn = BogenPickClosest(BogenCircleCircleIntersect(segCxA[prevSeg], segCyA[prevSeg], segRNew[prevSeg], segCxA[nextSeg], segCyA[nextSeg], segRNew[nextSeg]), vtxPt, segE[prevSeg]);
                effEnd[prevSeg]   = conn;
                effStart[nextSeg] = conn;
            }
            // Konvexe Ecke: effEnd[prevSeg]=segE[prevSeg], effStart[nextSeg]=segS[nextSeg] (defaults ok)
        }

        // Interne Ecken verarbeiten
        for (int i = 1; i < n - 1; i++)
            ProcessVertex(i, i - 1, i, pts[i]);

        // Naht bei geschlossenem Pfad als Ecke behandeln
        if (closed && m >= 2)
            ProcessVertex(0, m - 1, 0, pts[0]);

        // Ergebnisliste aufbauen
        var result = new List<PathMove>();
        result.Add(new PathMove(effStart[0].x, effStart[0].y));

        for (int s = 0; s < m; s++)
        {
            var sp = effStart[s];
            var ep = effEnd[s];
            if (segArc[s] && segRNew[s] > 1e-10)
                result.Add(new PathMove(ep.x, ep.y, IsArc: true,
                    I: segCxA[s] - sp.x, J: segCyA[s] - sp.y, CW: segCW[s]));
            else
                result.Add(new PathMove(ep.x, ep.y));

            // Konvexer Umfahrbogen nach Segment s
            int nextS = (s + 1) % m;
            int vIdx  = (s == m - 1 && closed) ? 0 : s + 1;
            if (vIdx < n && vtxConvex[vIdx])
            {
                var cornerPt  = pts[vIdx];
                var nextStart = effStart[nextS];
                // Kreuzprodukt (ep-corner)×(nextStart-corner): negativ → CW, positiv → CCW
                double crArc = (ep.x - cornerPt.x) * (nextStart.y - cornerPt.y)
                             - (ep.y - cornerPt.y) * (nextStart.x - cornerPt.x);
                result.Add(new PathMove(nextStart.x, nextStart.y, IsArc: true,
                    I: cornerPt.x - ep.x, J: cornerPt.y - ep.y, CW: crArc < 0));
            }
        }

        return result;
    }

    private static (double x, double y) BogenPickClosest(
        IEnumerable<(double x, double y)> cands, (double x, double y) hint, (double x, double y) fallback)
    {
        var best = fallback;
        double bestD = double.MaxValue;
        foreach (var c in cands)
        {
            double d = Math.Pow(c.x - hint.x, 2) + Math.Pow(c.y - hint.y, 2);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private static IEnumerable<(double x, double y)> BogenLineCircleIntersect(
        (double x, double y) o, (double x, double y) d, double cx, double cy, double R)
    {
        double fx = o.x - cx, fy = o.y - cy;
        double a = d.x * d.x + d.y * d.y;
        if (a < 1e-20) yield break;
        double b = 2 * (fx * d.x + fy * d.y);
        double c = fx * fx + fy * fy - R * R;
        double disc = b * b - 4 * a * c;
        if (disc < 0) yield break;
        double sq = Math.Sqrt(disc);
        double t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
        yield return (o.x + t1 * d.x, o.y + t1 * d.y);
        if (Math.Abs(t2 - t1) > 1e-10)
            yield return (o.x + t2 * d.x, o.y + t2 * d.y);
    }

    private static IEnumerable<(double x, double y)> BogenCircleCircleIntersect(
        double cx1, double cy1, double r1, double cx2, double cy2, double r2)
    {
        double dx = cx2 - cx1, dy = cy2 - cy1, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-10 || d > r1 + r2 + 1e-6 || d < Math.Abs(r1 - r2) - 1e-6) yield break;
        double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        double h2 = r1 * r1 - a * a;
        if (h2 < 0) yield break;
        double h = Math.Sqrt(h2), mx = cx1 + a * dx / d, my = cy1 + a * dy / d;
        yield return (mx + h * dy / d, my - h * dx / d);
        if (h > 1e-10) yield return (mx - h * dy / d, my + h * dx / d);
    }

    // Tangente eines Kreisbogens am Punkt (px,py) mit Zentrum (cx,cy)
    private static (double x, double y) BogenArcTangentAt(double cx, double cy, double px, double py, bool cw)
    {
        double dx = px - cx, dy = py - cy;
        double R = Math.Sqrt(dx * dx + dy * dy);
        if (R < 1e-12) return (1, 0);
        return cw ? (dy / R, -dx / R) : (-dy / R, dx / R);
    }

    // Ecken in einem gemischten Pfad (Linien + Bögen) verrunden.
    // Funktioniert an allen Übergängen: L→L, L→B, B→L, B→B.
    // Verkürzt eingehende/ausgehende Segmente und fügt einen Tangentialbogen ein.
    internal static (List<(double x, double y)> pts, List<(double mx, double my)?> arcMids)
        InsertLineCornerArcs(
            List<(double x, double y)> pts,
            List<(double mx, double my)?> arcMids,
            List<double> verrundungen, bool closed)
    {
        int n = pts.Count;

        // Bogengeometrie für alle Bogensegmente vorberechnen
        var geom = new (double cx, double cy, double R, bool cw)?[n];
        for (int i = 1; i < n; i++)
        {
            if (arcMids[i].HasValue)
            {
                var (cx, cy, R, cw) = ArcFrom3Points(
                    pts[i-1].x, pts[i-1].y,
                    arcMids[i].Value.mx, arcMids[i].Value.my,
                    pts[i].x, pts[i].y);
                if (!double.IsInfinity(R))
                    geom[i] = (cx, cy, R, cw);
            }
        }

        // Effektive Start-/Endpunkte jedes Segments (nach Verrundungskürzung)
        var segStart = new (double x, double y)[n]; // Start von Segment i
        var segEnd   = new (double x, double y)[n]; // End   von Segment i
        for (int i = 1; i < n; i++) { segStart[i] = pts[i-1]; segEnd[i] = pts[i]; }

        // Verrundungsbogen an Ecke i: (Eintrittspunkt, arcMid, Austrittspunkt)
        var corner = new (double ex, double ey, double mx, double my, double ax, double ay)?[n];

        // Bogenlänge eines Segments (für t-Begrenzung)
        double SegLen(int i)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a1 = Math.Atan2(pts[i-1].y - g.cy, pts[i-1].x - g.cx);
                double a2 = Math.Atan2(pts[i].y   - g.cy, pts[i].x   - g.cx);
                double sp = a2 - a1;
                if (g.cw) { if (sp > 0) sp -= 2*Math.PI; } else { if (sp < 0) sp += 2*Math.PI; }
                return g.R * Math.Abs(sp);
            }
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            return Math.Sqrt(dx*dx + dy*dy);
        }

        // Vorwärtstangente am Ende von Segment i (am Punkt pts[i])
        (double x, double y) TanEnd(int i)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                return BogenArcTangentAt(g.cx, g.cy, pts[i].x, pts[i].y, g.cw);
            }
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            return len > 1e-10 ? (dx/len, dy/len) : (1, 0);
        }

        // Vorwärtstangente am Anfang von Segment i (am Punkt pts[i-1])
        (double x, double y) TanStart(int i)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                return BogenArcTangentAt(g.cx, g.cy, pts[i-1].x, pts[i-1].y, g.cw);
            }
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            return len > 1e-10 ? (dx/len, dy/len) : (1, 0);
        }

        // Punkt auf Segment i im Abstand t vom Ende (rückwärts)
        (double x, double y) StepBack(int i, double t)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a2  = Math.Atan2(pts[i].y - g.cy, pts[i].x - g.cx);
                double dA  = t / g.R;
                double ang = a2 + (g.cw ? dA : -dA); // rückwärts = gegen Fahrtrichtung
                return (g.cx + g.R * Math.Cos(ang), g.cy + g.R * Math.Sin(ang));
            }
            var tan = TanEnd(i);
            return (pts[i].x - t * tan.x, pts[i].y - t * tan.y);
        }

        // Punkt auf Segment i im Abstand t vom Anfang (vorwärts)
        (double x, double y) StepFwd(int i, double t)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a1  = Math.Atan2(pts[i-1].y - g.cy, pts[i-1].x - g.cx);
                double dA  = t / g.R;
                double ang = a1 + (g.cw ? -dA : dA); // vorwärts = Fahrtrichtung
                return (g.cx + g.R * Math.Cos(ang), g.cy + g.R * Math.Sin(ang));
            }
            var tan = TanStart(i);
            return (pts[i-1].x + t * tan.x, pts[i-1].y + t * tan.y);
        }

        // Bögen verrunden: alle inneren Ecken
        for (int i = 1; i < n - 1; i++)
        {
            double v = i < verrundungen.Count ? verrundungen[i] : 0;
            if (v < 1e-10) continue;

            var tanIn  = TanEnd(i);     // Tangente am Ende von Segment i
            var tanOut = TanStart(i+1); // Tangente am Anfang von Segment i+1

            double cross = tanIn.x * tanOut.y - tanIn.y * tanOut.x;
            if (Math.Abs(cross) < 1e-6) continue; // parallel/antiparallel

            double dot   = Math.Clamp(tanIn.x * tanOut.x + tanIn.y * tanOut.y, -1.0, 1.0);
            double theta = Math.Acos(dot);
            double t     = v * Math.Tan(theta / 2.0);
            t = Math.Min(t, SegLen(i)   * 0.45);
            t = Math.Min(t, SegLen(i+1) * 0.45);
            double rEff  = t / Math.Tan(theta / 2.0);

            var entry = StepBack(i,   t); // Eintrittspunkt (auf Segment i,   t vor Ende)
            var exit_ = StepFwd (i+1, t); // Austrittspunkt (auf Segment i+1, t nach Anfang)

            // Verrundungsbogen-Mitte berechnen
            double sgn = Math.Sign(cross);
            double nx   = -tanIn.y * sgn, ny = tanIn.x * sgn;
            double acx  = entry.x + nx * rEff, acy = entry.y + ny * rEff;
            double aa1  = Math.Atan2(entry.y - acy, entry.x - acx);
            double aa2  = Math.Atan2(exit_.y  - acy, exit_.x  - acx);
            double span = aa2 - aa1;
            if (sgn > 0) { if (span < 0) span += 2 * Math.PI; }
            else         { if (span > 0) span -= 2 * Math.PI; }
            double midX = acx + rEff * Math.Cos(aa1 + span / 2);
            double midY = acy + rEff * Math.Sin(aa1 + span / 2);

            corner[i]   = (entry.x, entry.y, midX, midY, exit_.x, exit_.y);
            segEnd[i]   = entry;
            segStart[i+1] = exit_;
        }

        // Ausgabeliste aufbauen
        var newPts  = new List<(double x, double y)>();
        var newMids = new List<(double mx, double my)?>();
        newPts.Add(pts[0]);
        newMids.Add(null);

        for (int i = 1; i < n; i++)
        {
            var sStart = segStart[i];
            var sEnd   = segEnd[i];

            // arcMid für (ggf. gekürzten) Bogen neu berechnen
            (double mx, double my)? mid = arcMids[i];
            if (geom[i].HasValue)
            {
                var g   = geom[i].Value;
                double a1 = Math.Atan2(sStart.y - g.cy, sStart.x - g.cx);
                double a2 = Math.Atan2(sEnd.y   - g.cy, sEnd.x   - g.cx);
                double sp = a2 - a1;
                if (g.cw) { if (sp > 0) sp -= 2*Math.PI; } else { if (sp < 0) sp += 2*Math.PI; }
                mid = (g.cx + g.R * Math.Cos(a1 + sp / 2), g.cy + g.R * Math.Sin(a1 + sp / 2));
            }

            newPts.Add(sEnd);
            newMids.Add(mid);

            // Verrundungsbogen nach diesem Segment einfügen
            if (i < n - 1 && corner[i].HasValue)
            {
                var c = corner[i].Value;
                newPts.Add((c.ax, c.ay));           // Austrittspunkt
                newMids.Add((c.mx, c.my));           // arcMid = Verrundungsbogen
            }
        }

        return (newPts, newMids);
    }

    // Berechnet pro Punkt den Verrundungsradius für konkave Ecken (für Konturlinie-Vorschau).
    // Konkave Ecken = Innenecken auf der Seite der Radiuskorrektur; sign: +1 = Links, -1 = Rechts.
    internal static List<double> ConcaveCornerRadii(
        List<(double x, double y)> pts,
        List<(double mx, double my)?> arcMids,
        double r, double sign)
    {
        int n = pts.Count;
        var result = new List<double>(new double[n]);
        if (n < 3 || r < 1e-10) return result;

        var geom = new (double cx, double cy, double R, bool cw)?[n];
        for (int i = 1; i < n; i++)
        {
            if (i < arcMids.Count && arcMids[i].HasValue)
            {
                var (cx, cy, arcR, cw) = ArcFrom3Points(
                    pts[i-1].x, pts[i-1].y,
                    arcMids[i].Value.mx, arcMids[i].Value.my,
                    pts[i].x, pts[i].y);
                if (!double.IsInfinity(arcR)) geom[i] = (cx, cy, arcR, cw);
            }
        }

        (double x, double y) TanEnd(int i)
        {
            if (geom[i].HasValue)
                return BogenArcTangentAt(geom[i].Value.cx, geom[i].Value.cy, pts[i].x, pts[i].y, geom[i].Value.cw);
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            return len > 1e-10 ? (dx/len, dy/len) : (1, 0);
        }
        (double x, double y) TanStart(int i)
        {
            if (geom[i].HasValue)
                return BogenArcTangentAt(geom[i].Value.cx, geom[i].Value.cy, pts[i-1].x, pts[i-1].y, geom[i].Value.cw);
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            return len > 1e-10 ? (dx/len, dy/len) : (1, 0);
        }

        for (int i = 1; i < n - 1; i++)
        {
            var tanIn  = TanEnd(i);
            var tanOut = TanStart(i + 1);
            double cross = tanIn.x * tanOut.y - tanIn.y * tanOut.x;
            bool isConcave = Math.Abs(cross) > 0.01 && !(sign > 0 ? cross < 0 : cross > 0);
            if (isConcave) result[i] = r;
        }
        return result;
    }

    /// <summary>
    /// Konkave Ecken für Konturlinie-Vorschau korrekt visualisieren:
    /// Für jede konkave Ecke wird Q (Fräsmittelpunkt-Eckpunkt = Schnittpunkt der
    /// versetzten Segmente) berechnet und ein Kreisbogen mit Radius r um Q eingefügt.
    /// Unterstützt L→L, L→B, B→L, B→B durch geometrisch korrekte Schnittberechnung.
    /// </summary>
    internal static (List<(double x, double y)> pts, List<(double mx, double my)?> arcMids)
        InsertConcaveCircleArcs(
            List<(double x, double y)> pts,
            List<(double mx, double my)?> arcMids,
            List<double> concaveRadii,
            double sign)
    {
        int n = pts.Count;
        if (n < 3) return (pts, arcMids);

        // Bogengeometrie vorberechnen
        var geom = new (double cx, double cy, double R, bool cw)?[n];
        for (int i = 1; i < n; i++)
        {
            if (i < arcMids.Count && arcMids[i].HasValue)
            {
                var (cx, cy, R, cw) = ArcFrom3Points(
                    pts[i-1].x, pts[i-1].y,
                    arcMids[i].Value.mx, arcMids[i].Value.my,
                    pts[i].x, pts[i].y);
                if (!double.IsInfinity(R)) geom[i] = (cx, cy, R, cw);
            }
        }

        (double x, double y) TanEnd(int i)
        {
            if (geom[i].HasValue)
                return BogenArcTangentAt(geom[i].Value.cx, geom[i].Value.cy, pts[i].x, pts[i].y, geom[i].Value.cw);
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            return len > 1e-10 ? (dx/len, dy/len) : (1, 0);
        }
        (double x, double y) TanStart(int i)
        {
            if (geom[i].HasValue)
                return BogenArcTangentAt(geom[i].Value.cx, geom[i].Value.cy, pts[i-1].x, pts[i-1].y, geom[i].Value.cw);
            double dx = pts[i].x - pts[i-1].x, dy = pts[i].y - pts[i-1].y;
            double len = Math.Sqrt(dx*dx + dy*dy);
            return len > 1e-10 ? (dx/len, dy/len) : (1, 0);
        }
        double SegLen(int i)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a1 = Math.Atan2(pts[i-1].y - g.cy, pts[i-1].x - g.cx);
                double a2 = Math.Atan2(pts[i].y   - g.cy, pts[i].x   - g.cx);
                double sp = a2 - a1;
                if (g.cw) { if (sp > 0) sp -= 2*Math.PI; } else { if (sp < 0) sp += 2*Math.PI; }
                return g.R * Math.Abs(sp);
            }
            double dx2 = pts[i].x - pts[i-1].x, dy2 = pts[i].y - pts[i-1].y;
            return Math.Sqrt(dx2*dx2 + dy2*dy2);
        }
        (double x, double y) StepBack(int i, double t)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a2  = Math.Atan2(pts[i].y - g.cy, pts[i].x - g.cx);
                double dA  = t / g.R;
                double ang = a2 + (g.cw ? dA : -dA);
                return (g.cx + g.R * Math.Cos(ang), g.cy + g.R * Math.Sin(ang));
            }
            var tan = TanEnd(i);
            return (pts[i].x - t * tan.x, pts[i].y - t * tan.y);
        }
        (double x, double y) StepFwd(int i, double t)
        {
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a1  = Math.Atan2(pts[i-1].y - g.cy, pts[i-1].x - g.cx);
                double dA  = t / g.R;
                double ang = a1 + (g.cw ? -dA : dA);
                return (g.cx + g.R * Math.Cos(ang), g.cy + g.R * Math.Sin(ang));
            }
            var tan = TanStart(i);
            return (pts[i-1].x + t * tan.x, pts[i-1].y + t * tan.y);
        }

        var segStart = new (double x, double y)[n];
        var segEnd   = new (double x, double y)[n];
        for (int i = 1; i < n; i++) { segStart[i] = pts[i-1]; segEnd[i] = pts[i]; }

        var corner = new (double mx, double my, double ax, double ay)?[n];

        for (int i = 1; i < n - 1; i++)
        {
            double r = i < concaveRadii.Count ? concaveRadii[i] : 0;
            if (r < 1e-10) continue;

            var tanIn  = TanEnd(i);
            var tanOut = TanStart(i + 1);
            double cross = tanIn.x * tanOut.y - tanIn.y * tanOut.x;
            if (Math.Abs(cross) < 1e-6) continue;

            // Offset-Normalen: linke Normale × sign = Offset-Richtung
            double nInX  = -tanIn.y  * sign, nInY  =  tanIn.x  * sign;
            double nOutX = -tanOut.y * sign, nOutY =  tanOut.x * sign;

            bool prevIsArc = geom[i].HasValue;
            bool nextIsArc = geom[i + 1].HasValue;

            // Q = Schnittpunkt der versetzten Segmente (korrekte Bogen-Geometrie)
            (double x, double y) Q;
            if (!prevIsArc && !nextIsArc)
            {
                // Linie → Linie
                var o1 = (x: pts[i].x + r * nInX,  y: pts[i].y + r * nInY);
                var o2 = (x: pts[i].x + r * nOutX, y: pts[i].y + r * nOutY);
                var q = LineIntersect(o1, tanIn, o2, tanOut);
                if (!q.HasValue) continue;
                Q = q.Value;
            }
            else if (!prevIsArc)
            {
                // Linie → Bogen
                var g2 = geom[i + 1].Value;
                double r2 = g2.R + sign * (g2.cw ? 1.0 : -1.0) * r;
                if (r2 < 1e-6) continue;
                var o1 = (x: pts[i].x + r * nInX, y: pts[i].y + r * nInY);
                Q = BogenPickClosest(BogenLineCircleIntersect(o1, tanIn, g2.cx, g2.cy, r2), pts[i], pts[i]);
            }
            else if (!nextIsArc)
            {
                // Bogen → Linie
                var g1 = geom[i].Value;
                double r1 = g1.R + sign * (g1.cw ? 1.0 : -1.0) * r;
                if (r1 < 1e-6) continue;
                var o2 = (x: pts[i].x + r * nOutX, y: pts[i].y + r * nOutY);
                Q = BogenPickClosest(BogenLineCircleIntersect(o2, tanOut, g1.cx, g1.cy, r1), pts[i], pts[i]);
            }
            else
            {
                // Bogen → Bogen
                var g1 = geom[i].Value;
                var g2 = geom[i + 1].Value;
                double r1 = g1.R + sign * (g1.cw ? 1.0 : -1.0) * r;
                double r2 = g2.R + sign * (g2.cw ? 1.0 : -1.0) * r;
                if (r1 < 1e-6 || r2 < 1e-6) continue;
                Q = BogenPickClosest(BogenCircleCircleIntersect(g1.cx, g1.cy, r1, g2.cx, g2.cy, r2), pts[i], pts[i]);
            }

            // Sanity-Check: Q muss mindestens r vom Eckpunkt entfernt sein
            double distQ2 = (Q.x - pts[i].x)*(Q.x - pts[i].x) + (Q.y - pts[i].y)*(Q.y - pts[i].y);
            if (distQ2 < r * r * 0.25) continue;

            // Setback = Projektion von (Q − pts[i]) auf die Tangente
            double stepB = Math.Abs((Q.x - pts[i].x) * tanIn.x  + (Q.y - pts[i].y) * tanIn.y);
            double stepF = Math.Abs((Q.x - pts[i].x) * tanOut.x + (Q.y - pts[i].y) * tanOut.y);

            // Maximal 90% der jeweiligen Segmentlänge
            stepB = Math.Min(stepB, SegLen(i)     * 0.9);
            stepF = Math.Min(stepF, SegLen(i + 1) * 0.9);
            if (stepB < 1e-10 || stepF < 1e-10) continue;

            var A = StepBack(i,     stepB);
            var B = StepFwd (i + 1, stepF);

            // Bogen-Mittelpunkt: der Punkt auf dem Kreisbogen um Q Richtung pts[i]
            double dqx = pts[i].x - Q.x, dqy = pts[i].y - Q.y;
            double dqLen = Math.Sqrt(dqx*dqx + dqy*dqy);
            double cmx, cmy;
            if (dqLen < 1e-10) { cmx = (A.x + B.x) * 0.5; cmy = (A.y + B.y) * 0.5; }
            else               { cmx = Q.x + r * dqx / dqLen; cmy = Q.y + r * dqy / dqLen; }

            corner[i]     = (cmx, cmy, B.x, B.y);
            segEnd[i]     = A;
            segStart[i+1] = B;
        }

        // Ausgabeliste aufbauen
        var newPts  = new List<(double x, double y)>();
        var newMids = new List<(double mx, double my)?>();
        newPts.Add(pts[0]);
        newMids.Add(null);

        for (int i = 1; i < n; i++)
        {
            var sStart = segStart[i];
            var sEnd   = segEnd[i];

            (double mx2, double my2)? mid;
            if (geom[i].HasValue)
            {
                var g = geom[i].Value;
                double a1 = Math.Atan2(sStart.y - g.cy, sStart.x - g.cx);
                double a2 = Math.Atan2(sEnd.y   - g.cy, sEnd.x   - g.cx);
                double sp = a2 - a1;
                if (g.cw) { if (sp > 0) sp -= 2*Math.PI; } else { if (sp < 0) sp += 2*Math.PI; }
                mid = (g.cx + g.R * Math.Cos(a1 + sp / 2), g.cy + g.R * Math.Sin(a1 + sp / 2));
            }
            else mid = null;

            newPts.Add(sEnd);
            newMids.Add(mid);

            if (i < n - 1 && corner[i].HasValue)
            {
                var c = corner[i].Value;
                newPts.Add((c.ax, c.ay));
                newMids.Add((c.mx, c.my));
            }
        }

        return (newPts, newMids);
    }

    // Ecken mit Radius runden (approximiert als Segmente, vor Offset-Berechnung)
    // verrundungen[i] = Radius an Ecke i (für Links/Rechts = Werkstückradius)
    private static List<(double x, double y)> ApplyCornerRounding(
        List<(double x, double y)> pts, List<double> verrundungen, bool closed)
    {
        int n = pts.Count;
        var result = new List<(double x, double y)>(n * 2);

        for (int i = 0; i < n; i++)
        {
            double R = i < verrundungen.Count ? verrundungen[i] : 0;

            // Offene Pfade: kein Runden an den Endpunkten
            if (!closed && (i == 0 || i == n - 1)) { result.Add(pts[i]); continue; }
            if (R < 1e-10)                          { result.Add(pts[i]); continue; }

            int prevIdx = closed ? ((i - 1 + n) % n) : i - 1;
            int nextIdx = closed ? ((i + 1) % n)     : i + 1;

            var a = pts[prevIdx];
            var b = pts[i];
            var c = pts[nextIdx];

            double ax = b.x - a.x, ay = b.y - a.y;
            double lenA = Math.Sqrt(ax * ax + ay * ay);
            double bx = c.x - b.x, by = c.y - b.y;
            double lenB = Math.Sqrt(bx * bx + by * by);
            if (lenA < 1e-10 || lenB < 1e-10) { result.Add(b); continue; }

            (double x, double y) d1 = (ax / lenA, ay / lenA);
            (double x, double y) d2 = (bx / lenB, by / lenB);

            double cross = d1.x * d2.y - d1.y * d2.x;
            if (Math.Abs(cross) < 1e-6) { result.Add(b); continue; } // kollinear

            double dot   = Math.Clamp(d1.x * d2.x + d1.y * d2.y, -1.0, 1.0);
            double theta = Math.Acos(dot);                 // Winkel zwischen den Richtungen
            double t     = R * Math.Tan(theta / 2.0);     // Rückschnitt auf dem Segment

            // Sicherstellen, dass der Bogen in den Segmenten passt
            t = Math.Min(t, lenA * 0.45);
            t = Math.Min(t, lenB * 0.45);
            double Reff = t / Math.Tan(theta / 2.0);      // effektiver Radius nach Kürzung

            double psx = b.x - t * d1.x, psy = b.y - t * d1.y; // Bogenbeginn
            double pex = b.x + t * d2.x, pey = b.y + t * d2.y; // Bogenende

            // Bogenmittelpunkt (senkrecht auf d1, zur Innenseite)
            double sgn = Math.Sign(cross); // +1 = Linkskurve (CCW), -1 = Rechtskurve (CW)
            double nx  = -d1.y * sgn, ny = d1.x * sgn;
            double cx  = psx + nx * Reff, cy = psy + ny * Reff;

            double startAngle = Math.Atan2(psy - cy, psx - cx);
            double endAngle   = Math.Atan2(pey - cy, pex - cx);

            double arcSpan;
            if (sgn > 0) { arcSpan = endAngle - startAngle; if (arcSpan < 0) arcSpan += 2 * Math.PI; }
            else         { arcSpan = startAngle - endAngle; if (arcSpan < 0) arcSpan += 2 * Math.PI; arcSpan = -arcSpan; }

            result.Add((psx, psy));

            const double chordalTol = 0.05;
            double maxStep = 2 * Math.Acos(Math.Clamp(1.0 - chordalTol / Reff, -1.0, 1.0));
            int steps = Math.Clamp((int)Math.Ceiling(Math.Abs(arcSpan) / maxStep), 1, 36);
            for (int s = 1; s <= steps; s++)
            {
                double angle = startAngle + arcSpan * s / steps;
                result.Add((cx + Reff * Math.Cos(angle), cy + Reff * Math.Sin(angle)));
            }
            // letzter hinzugefügter Punkt = pex/pey (Bogenende)
        }
        return result;
    }

    // Offset für offenen Pfad
    private static List<PathMove> ComputeOffsetMoves(
        List<(double x, double y)> pts, double r, double sign, List<double>? verrundungen = null)
    {
        int n = pts.Count;
        var dir = new (double x, double y)[n - 1];
        var nrm = new (double x, double y)[n - 1];
        for (int k = 0; k < n - 1; k++) ComputeDirNrm(pts[k], pts[k + 1], sign, out dir[k], out nrm[k]);

        var result = new List<PathMove>();
        result.Add(new PathMove(pts[0].x + nrm[0].x * r, pts[0].y + nrm[0].y * r));

        for (int i = 0; i < n - 1; i++)
        {
            double bx = pts[i + 1].x + nrm[i].x * r;
            double by = pts[i + 1].y + nrm[i].y * r;

            if (i == n - 2)
            {
                result.Add(new PathMove(bx, by));
            }
            else
            {
                double cross = dir[i].x * dir[i + 1].y - dir[i].y * dir[i + 1].x;
                double R = verrundungen != null && i + 1 < verrundungen.Count ? verrundungen[i + 1] : 0;

                if (R > 1e-10 && Math.Abs(cross) > 1e-9)
                {
                    // Verrundeter Eckpunkt: G2/G3-Bogen direkt im Offset-Pfad
                    double dot   = Math.Clamp(dir[i].x * dir[i + 1].x + dir[i].y * dir[i + 1].y, -1.0, 1.0);
                    double theta = Math.Acos(dot);
                    double lenA  = Math.Sqrt(Math.Pow(pts[i + 1].x - pts[i    ].x, 2) + Math.Pow(pts[i + 1].y - pts[i    ].y, 2));
                    double lenB  = Math.Sqrt(Math.Pow(pts[i + 2].x - pts[i + 1].x, 2) + Math.Pow(pts[i + 2].y - pts[i + 1].y, 2));
                    double t     = R * Math.Tan(theta / 2.0);
                    t = Math.Min(t, lenA * 0.45);
                    t = Math.Min(t, lenB * 0.45);
                    double Reff = t / Math.Tan(theta / 2.0);

                    // Kürzungspunkte auf dem Originalpfad
                    double psx = pts[i + 1].x - t * dir[i    ].x, psy = pts[i + 1].y - t * dir[i    ].y;
                    double pex = pts[i + 1].x + t * dir[i + 1].x, pey = pts[i + 1].y + t * dir[i + 1].y;

                    // Offset der Kürzungspunkte
                    double psxo = psx + nrm[i    ].x * r, psyo = psy + nrm[i    ].y * r;
                    double pexo = pex + nrm[i + 1].x * r, peyo = pey + nrm[i + 1].y * r;

                    // Bogenmittelpunkt (Werkstückpfad, senkrecht zur Eingangsrichtung, zur Kurveninnenseite)
                    double sgn_turn = cross > 0 ? 1.0 : -1.0;
                    double cx = psx + Reff * (-dir[i].y * sgn_turn);
                    double cy = psy + Reff * ( dir[i].x * sgn_turn);

                    // Werkzeugbahn-Bogenradius: r_off = Reff - sgn_turn * sign * r
                    double r_off = Reff - sgn_turn * sign * r;

                    if (r_off > 1e-6)
                    {
                        result.Add(new PathMove(psxo, psyo));
                        result.Add(new PathMove(pexo, peyo, IsArc: true,
                            I: cx - psxo, J: cy - psyo, CW: sgn_turn < 0));
                    }
                    else
                    {
                        // Werkzeugradius zu groß: Schnittpunkt-Fallback
                        double a0x = pts[i    ].x + nrm[i    ].x * r, a0y = pts[i    ].y + nrm[i    ].y * r;
                        double a1x = pts[i + 1].x + nrm[i + 1].x * r, a1y = pts[i + 1].y + nrm[i + 1].y * r;
                        var inter = LineIntersect((a0x, a0y), dir[i], (a1x, a1y), dir[i + 1]);
                        result.Add(inter.HasValue ? new PathMove(inter.Value.x, inter.Value.y) : new PathMove(bx, by));
                    }
                }
                else
                {
                    // Scharfe Ecke (original)
                    bool isConvex = r > 1e-10 && (sign > 0 ? cross < 0 : cross > 0) && Math.Abs(cross) > 0.01;
                    if (isConvex)
                    {
                        double ax = pts[i + 1].x + nrm[i + 1].x * r;
                        double ay = pts[i + 1].y + nrm[i + 1].y * r;
                        result.Add(new PathMove(bx, by));
                        result.Add(new PathMove(ax, ay, IsArc: true,
                            I: pts[i + 1].x - bx, J: pts[i + 1].y - by, CW: sign > 0));
                    }
                    else
                    {
                        double a0x = pts[i    ].x + nrm[i    ].x * r, a0y = pts[i    ].y + nrm[i    ].y * r;
                        double a1x = pts[i + 1].x + nrm[i + 1].x * r, a1y = pts[i + 1].y + nrm[i + 1].y * r;
                        var inter = LineIntersect((a0x, a0y), dir[i], (a1x, a1y), dir[i + 1]);
                        result.Add(inter.HasValue ? new PathMove(inter.Value.x, inter.Value.y) : new PathMove(bx, by));
                    }
                }
            }
        }
        return result;
    }

    // Offset für geschlossenen Pfad (pts: m eindeutige Punkte, kein Duplikat am Ende)
    private static List<PathMove> ComputeClosedOffsetMoves(
        List<(double x, double y)> pts, double r, double sign, List<double>? verrundungen = null)
    {
        int m = pts.Count;
        var dir = new (double x, double y)[m];
        var nrm = new (double x, double y)[m];
        for (int k = 0; k < m; k++) ComputeDirNrm(pts[k], pts[(k + 1) % m], sign, out dir[k], out nrm[k]);

        // Startpunkt = Ecke bei pts[0] (Übergang von Segment[m-1] zu Segment[0])
        double cross0 = dir[m - 1].x * dir[0].y - dir[m - 1].y * dir[0].x;
        double R0     = verrundungen != null && verrundungen.Count > 0 ? verrundungen[0] : 0;
        double a0x    = pts[0].x + nrm[0    ].x * r, a0y = pts[0].y + nrm[0    ].y * r;
        double bm_x   = pts[0].x + nrm[m - 1].x * r, bm_y = pts[0].y + nrm[m - 1].y * r;

        PathMove startPt;
        PathMove[] closingMoves;

        if (R0 > 1e-10 && Math.Abs(cross0) > 1e-9)
        {
            // Verrundete Ecke an pts[0]
            double dot0   = Math.Clamp(dir[m-1].x*dir[0].x + dir[m-1].y*dir[0].y, -1.0, 1.0);
            double theta0 = Math.Acos(dot0);
            double lenA0  = Math.Sqrt(Math.Pow(pts[0].x-pts[m-1].x,2)+Math.Pow(pts[0].y-pts[m-1].y,2));
            double lenB0  = Math.Sqrt(Math.Pow(pts[1].x-pts[0  ].x,2)+Math.Pow(pts[1].y-pts[0  ].y,2));
            double t0     = R0 / Math.Tan(theta0 / 2.0);
            t0 = Math.Min(t0, lenA0 * 0.45);
            t0 = Math.Min(t0, lenB0 * 0.45);
            double Reff0 = t0 * Math.Tan(theta0 / 2.0);

            double psx0 = pts[0].x - t0 * dir[m-1].x, psy0 = pts[0].y - t0 * dir[m-1].y; // Kürzung Eingang
            double pex0 = pts[0].x + t0 * dir[0    ].x, pey0 = pts[0].y + t0 * dir[0  ].y; // Kürzung Ausgang

            double psxo0 = psx0 + nrm[m-1].x * r, psyo0 = psy0 + nrm[m-1].y * r;
            double pexo0 = pex0 + nrm[0    ].x * r, peyo0 = pey0 + nrm[0  ].y * r;

            double sgn0  = cross0 > 0 ? 1.0 : -1.0;
            double cxw0  = psx0 + Reff0 * (-dir[m-1].y * sgn0);
            double cyw0  = psy0 + Reff0 * ( dir[m-1].x * sgn0);
            double roff0 = Reff0 - sgn0 * sign * r;

            startPt = new PathMove(pexo0, peyo0);
            if (roff0 > 1e-6)
            {
                closingMoves = [
                    new PathMove(psxo0, psyo0),
                    new PathMove(pexo0, peyo0, IsArc: true,
                        I: cxw0 - psxo0, J: cyw0 - psyo0, CW: sgn0 < 0)
                ];
            }
            else
            {
                double sx = pts[m-1].x + nrm[m-1].x * r, sy = pts[m-1].y + nrm[m-1].y * r;
                var int0 = LineIntersect((sx, sy), dir[m-1], (a0x, a0y), dir[0]);
                startPt = int0.HasValue ? new PathMove(int0.Value.x, int0.Value.y) : new PathMove(a0x, a0y);
                closingMoves = [startPt];
            }
        }
        else
        {
            // Scharfe Ecke an pts[0]
            bool conv0 = r > 1e-10 && (sign > 0 ? cross0 < 0 : cross0 > 0) && Math.Abs(cross0) > 0.01;
            if (conv0)
            {
                startPt = new PathMove(a0x, a0y);
                closingMoves = [
                    new PathMove(bm_x, bm_y),
                    new PathMove(a0x, a0y, IsArc: true,
                        I: pts[0].x - bm_x, J: pts[0].y - bm_y, CW: sign > 0)
                ];
            }
            else
            {
                double sx = pts[m - 1].x + nrm[m - 1].x * r, sy = pts[m - 1].y + nrm[m - 1].y * r;
                var inter = LineIntersect((sx, sy), dir[m - 1], (a0x, a0y), dir[0]);
                startPt = inter.HasValue ? new PathMove(inter.Value.x, inter.Value.y) : new PathMove(a0x, a0y);
                closingMoves = [startPt];
            }
        }

        var result = new List<PathMove> { startPt };

        // Ecken bei pts[1..m-1]
        for (int i = 1; i < m; i++)
        {
            int prev   = i - 1;
            int nextSeg = i % m; // Richtungsindex des ausgehenden Segments
            double bx  = pts[i].x + nrm[prev   ].x * r, by = pts[i].y + nrm[prev   ].y * r;
            double ax  = pts[i].x + nrm[nextSeg ].x * r, ay = pts[i].y + nrm[nextSeg].y * r;
            double cr  = dir[prev].x * dir[nextSeg].y - dir[prev].y * dir[nextSeg].x;
            double R   = verrundungen != null && i < verrundungen.Count ? verrundungen[i] : 0;

            if (R > 1e-10 && Math.Abs(cr) > 1e-9)
            {
                double dot   = Math.Clamp(dir[prev].x*dir[nextSeg].x + dir[prev].y*dir[nextSeg].y, -1.0, 1.0);
                double theta = Math.Acos(dot);
                int    ni    = (i + 1) % m;
                double lenA  = Math.Sqrt(Math.Pow(pts[i].x-pts[prev].x,2)+Math.Pow(pts[i].y-pts[prev].y,2));
                double lenB  = Math.Sqrt(Math.Pow(pts[ni ].x-pts[i  ].x,2)+Math.Pow(pts[ni ].y-pts[i  ].y,2));
                double t     = R * Math.Tan(theta / 2.0);
                t = Math.Min(t, lenA * 0.45);
                t = Math.Min(t, lenB * 0.45);
                double Reff = t / Math.Tan(theta / 2.0);

                double psx = pts[i].x - t * dir[prev   ].x, psy = pts[i].y - t * dir[prev   ].y;
                double pex = pts[i].x + t * dir[nextSeg].x, pey = pts[i].y + t * dir[nextSeg].y;

                double psxo = psx + nrm[prev   ].x * r, psyo = psy + nrm[prev   ].y * r;
                double pexo = pex + nrm[nextSeg ].x * r, peyo = pey + nrm[nextSeg].y * r;

                double sgn_turn = cr > 0 ? 1.0 : -1.0;
                double cx  = psx + Reff * (-dir[prev].y * sgn_turn);
                double cy  = psy + Reff * ( dir[prev].x * sgn_turn);
                double roff = Reff - sgn_turn * sign * r;

                if (roff > 1e-6)
                {
                    result.Add(new PathMove(psxo, psyo));
                    result.Add(new PathMove(pexo, peyo, IsArc: true,
                        I: cx - psxo, J: cy - psyo, CW: sgn_turn < 0));
                }
                else
                {
                    double p0x = pts[prev].x + nrm[prev   ].x * r, p0y = pts[prev].y + nrm[prev   ].y * r;
                    var inter = LineIntersect((p0x, p0y), dir[prev], (ax, ay), dir[nextSeg]);
                    result.Add(inter.HasValue ? new PathMove(inter.Value.x, inter.Value.y) : new PathMove(bx, by));
                }
            }
            else
            {
                // Scharfe Ecke (original)
                bool conv = r > 1e-10 && (sign > 0 ? cr < 0 : cr > 0) && Math.Abs(cr) > 0.01;
                if (conv)
                {
                    result.Add(new PathMove(bx, by));
                    result.Add(new PathMove(ax, ay, IsArc: true,
                        I: pts[i].x - bx, J: pts[i].y - by, CW: sign > 0));
                }
                else
                {
                    double p0x = pts[prev].x + nrm[prev   ].x * r, p0y = pts[prev].y + nrm[prev   ].y * r;
                    var inter = LineIntersect((p0x, p0y), dir[prev], (ax, ay), dir[nextSeg]);
                    result.Add(inter.HasValue ? new PathMove(inter.Value.x, inter.Value.y) : new PathMove(bx, by));
                }
            }
        }

        result.AddRange(closingMoves);
        return result;
    }

    private static void ComputeDirNrm(
        (double x, double y) a, (double x, double y) b, double sign,
        out (double x, double y) dir, out (double x, double y) nrm)
    {
        double dx = b.x - a.x, dy = b.y - a.y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-10) { dir = (1, 0); nrm = (0, sign); return; }
        dir = (dx / len, dy / len);
        nrm = (-dy / len * sign, dx / len * sign);
    }

    private static (double x, double y)? LineIntersect(
        (double x, double y) a, (double x, double y) da,
        (double x, double y) b, (double x, double y) db)
    {
        double denom = da.x * db.y - da.y * db.x;
        if (Math.Abs(denom) < 1e-10) return null;
        double t = ((b.x - a.x) * db.y - (b.y - a.y) * db.x) / denom;
        return (a.x + t * da.x, a.y + t * da.y);
    }

    // ── Spline-Interpolation ────────────────────────────────────────────

    private static (double x, double y) CatmullRomPoint(
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

    // Diskretisiere Spline-Segment in Linien-Punkte
    private static List<(double x, double y)> InterpolateFullSplineMultiple(
        List<(double x, double y)> pts, string splineMode, double tension, double segmentLength = 0.5)
    {
        if (pts.Count < 2) return new();
        if (pts.Count == 2)
        {
            var result = new List<(double x, double y)> { pts[0] };
            double dx = pts[1].x - pts[0].x;
            double dy = pts[1].y - pts[0].y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(10, (int)Math.Ceiling(dist / segmentLength));

            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                result.Add((pts[0].x + t * dx, pts[0].y + t * dy));
            }
            return result;
        }

        // 3+ Punkte: alle zusammen interpolieren
        var fullResult = new List<(double x, double y)> { pts[0] };
        int n = pts.Count;
        double maxTension = 0.0;

        for (int i = 0; i < n - 1; i++)
        {
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p3 = i < n - 2 ? pts[i + 2] : pts[i + 1];

            double dx = p2.x - p1.x;
            double dy = p2.y - p1.y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(10, (int)Math.Ceiling(dist / segmentLength));

            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                var pt = CatmullRomPoint(p0, p1, p2, p3, t, maxTension);
                fullResult.Add(pt);
            }
        }

        return fullResult;
    }

    private static List<(double x, double y)> InterpolateSplineSegment(
        List<(double x, double y)> allPts, int segIdx, string splineMode, double splineTension, double segmentLength = 0.5)
    {
        var result = new List<(double x, double y)>();

        if (splineMode == "Bézier")
        {
            // Vereinfachte Bézier: Nutze aktuelle und nächste Punkt, plus Nachbarn als Kontrollpunkte
            int n = allPts.Count;
            if (segIdx < 0 || segIdx >= n - 1) return new List<(double x, double y)> { allPts[segIdx + 1] };

            var p1 = allPts[segIdx];
            var p2 = allPts[segIdx + 1];
            var cp0 = segIdx > 0 ? allPts[segIdx - 1] : p1;
            var cp3 = segIdx < n - 2 ? allPts[segIdx + 2] : p2;

            // Quadratische Bézier mit 3 Kontrollpunkten
            int steps = Math.Max(5, (int)Math.Ceiling(Math.Sqrt(
                Math.Pow(p2.x - p1.x, 2) + Math.Pow(p2.y - p1.y, 2)) / segmentLength));

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                double mt = 1 - t;

                // Quadratische Bézier-Kurve
                double x = mt*mt*p1.x + 2*mt*t*cp0.x + t*t*p2.x;
                double y = mt*mt*p1.y + 2*mt*t*cp0.y + t*t*p2.y;
                result.Add((x, y));
            }
        }
        else // Catmull-Rom (default)
        {
            int n = allPts.Count;
            if (segIdx < 0 || segIdx >= n - 1) return new List<(double x, double y)> { allPts[segIdx + 1] };

            var p0 = segIdx > 0 ? allPts[segIdx - 1] : allPts[segIdx];
            var p1 = allPts[segIdx];
            var p2 = allPts[segIdx + 1];
            var p3 = segIdx < n - 2 ? allPts[segIdx + 2] : allPts[segIdx + 1];

            int steps = Math.Max(5, (int)Math.Ceiling(Math.Sqrt(
                Math.Pow(p2.x - p1.x, 2) + Math.Pow(p2.y - p1.y, 2)) / segmentLength));

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                var pt = CatmullRomPoint(p0, p1, p2, p3, t, splineTension);
                result.Add(pt);
            }
        }

        return result;
    }

    public static (double x, double y) ConvertBezugspunkt(string ref_, double xRel, double yRel, double w, double h)
        => ref_ switch
        {
            "Unten links"  => (xRel, yRel),
            "Oben links"   => (xRel, h - yRel),
            "Unten rechts" => (w - xRel, yRel),
            "Oben rechts"  => (w - xRel, h - yRel),
            "Links Mitte"  => (xRel, h / 2 + yRel),
            "Rechts Mitte" => (w - xRel, h / 2 + yRel),
            "Oben Mitte"   => (w / 2 + xRel, h - yRel),
            "Unten Mitte"  => (w / 2 + xRel, yRel),
            "Mitte"  => (w / 2 + xRel, h / 2 + yRel),
            _              => (xRel, yRel)
        };

    // ── Bogen-Mittelpunkt aus PfadPunktParams berechnen ─────────────────

    private static (double mx, double my) ResolveBogenMid(
        (double x, double y) p1, (double x, double y) p2,
        PfadPunktParams p, double workW, double workH)
    {
        string modus = p.BogenModus ?? "Bogenmitte";

        if (modus == "Bogenmitte")
        {
            if (p.Bezugspunkt == "Letzter Punkt")
                return (p1.x + p.XMid, p1.y + p.YMid);
            return ConvertBezugspunkt(p.Bezugspunkt, p.XMid, p.YMid, workW, workH);
        }

        // Geometrie: Sehnenrichtung und linke Senkrechte
        double dx = p2.x - p1.x, dy = p2.y - p1.y;
        double L  = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-10) return ((p1.x + p2.x) / 2, (p1.y + p2.y) / 2);

        double perpX = -dy / L, perpY = dx / L; // links der Fahrtrichtung
        double mcx   = (p1.x + p2.x) / 2, mcy = (p1.y + p2.y) / 2;

        double h; // vorzeichenbehaftete Pfeilhöhe (+ = links)
        if (modus == "Radius")
        {
            double R    = p.XMid;
            double a    = L / 2;
            double absR = Math.Max(Math.Abs(R), a); // Mindestradius = Sehnenhälfte
            h = (absR - Math.Sqrt(Math.Max(0, absR * absR - a * a))) * (R >= 0 ? 1 : -1);
        }
        else // "Pfeilhöhe"
        {
            h = p.XMid;
        }

        return (mcx + h * perpX, mcy + h * perpY);
    }

    // ── Gravieren ────────────────────────────────────────────────────────

    public static string Gravieren(GraviereParams p, double workW, double workH)
    {
        var sb  = new StringBuilder();
        var ctx = p.UseSkia ? BuildTextGeoSk(p, workW, workH) : BuildTextGeo(p, workW, workH);

        double scale  = ctx.Scale;
        double multiH = ctx.MultiH;
        var flat2     = ctx.FlatDisplay;
        double zDepth = -Math.Abs(p.ZTiefe);

        double MX(double wx) => ctx.Ox + wx * scale;
        double MY(double wy) => ctx.Oy + ctx.YOffset + (multiH - wy) * scale;

        double halfRad        = Math.Min(p.SchneidenWinkel, 179.9) / 2.0 * Math.PI / 180.0;
        double vWidth         = 2.0 * Math.Abs(p.ZTiefe) * Math.Tan(halfRad);
        double effectiveWidth = (p.FraeserD > 0) ? Math.Min(vWidth, p.FraeserD) : vWidth;
        sb.AppendLine($"(FraeserD={F(effectiveWidth)})");
        sb.AppendLine("(Gravieren)");
        sb.AppendLine($"(Text: {p.Text.Replace('\n', ' ').Replace('\r', ' ')})");
        sb.AppendLine($"(Font: {p.FontFamily}, {F(p.FontSizeMm)} mm, Winkel={p.SchneidenWinkel}°)");
        sb.AppendLine();
        sb.AppendLine(Sz());

        foreach (var figure in flat2.Figures)
        {
            double sx = MX(figure.StartPoint.X);
            double sy = MY(figure.StartPoint.Y);

            sb.AppendLine($"G00 X{F(sx)} Y{F(sy)}");
            sb.AppendLine($"G01 Z{F(zDepth)} F{(int)(p.Vorschub * 0.3)}");

            foreach (var seg in figure.Segments)
            {
                IEnumerable<System.Windows.Point> pts = seg switch
                {
                    System.Windows.Media.PolyLineSegment pls => pls.Points,
                    System.Windows.Media.LineSegment ls      => [ls.Point],
                    _                                         => []
                };
                foreach (var pt in pts)
                    sb.AppendLine($"G01 X{F(MX(pt.X))} Y{F(MY(pt.Y))} F{(int)p.Vorschub}");
            }

            if (figure.IsClosed)
                sb.AppendLine($"G01 X{F(sx)} Y{F(sy)} F{(int)p.Vorschub}");

            sb.AppendLine(Sz());
        }

        sb.AppendLine(Sz());
        return sb.ToString();
    }

    public record TextGeoCtx(
        System.Windows.Media.Geometry  AlignedGeo,
        System.Windows.Media.PathGeometry Flat,        // grob (tol 2.0) — VCarve-Berechnung
        System.Windows.Media.PathGeometry FlatDisplay, // fein (tol 0.5) — Darstellung
        double Scale, double MultiH,
        double Ox, double Oy, double YOffset);

    public static TextGeoCtx BuildTextGeo(GraviereParams p, double workW, double workH)
    {
        var typef = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily(p.FontFamily),
            System.Windows.FontStyles.Normal,
            System.Windows.FontWeights.Normal,
            System.Windows.FontStretches.Normal);

        const double emSize = 1000.0;
        var ft = new System.Windows.Media.FormattedText(
            string.IsNullOrEmpty(p.Text) ? " " : p.Text,
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            typef, emSize,
            System.Windows.Media.Brushes.Black, 1.0);

        var ftLine = new System.Windows.Media.FormattedText(
            "Ag", CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            typef, emSize, System.Windows.Media.Brushes.Black, 1.0);
        double lineH = ftLine.Height > 1e-6 ? ftLine.Height : 1;

        double scale = p.FontSizeMm > 0 ? p.FontSizeMm / lineH : 1.0;

        bool bezugRechts = p.Bezugspunkt.Contains("rechts", StringComparison.OrdinalIgnoreCase);
        ft.TextAlignment = (p.Ausrichtung == "Rechts" || bezugRechts) ? System.Windows.TextAlignment.Right
                         : p.Ausrichtung == "Mitte"                   ? System.Windows.TextAlignment.Center
                                                                       : System.Windows.TextAlignment.Left;
        if (p.TextBreite > 0 && scale > 0)
            ft.MaxTextWidth = p.TextBreite / scale;

        double multiH  = ft.Height > 1e-6 ? ft.Height : 1;
        double fieldH  = p.TextHoehe > 0 ? p.TextHoehe
                       : (p.FontSizeMm > 0 ? p.FontSizeMm : multiH * scale);
        double yOffset = (fieldH - multiH * scale) / 2.0;

        var geo2        = ft.BuildGeometry(new System.Windows.Point(0, 0));
        var flat2       = geo2.GetFlattenedPathGeometry(2.0, System.Windows.Media.ToleranceType.Absolute);
        var flatDisplay = geo2.GetFlattenedPathGeometry(0.5, System.Windows.Media.ToleranceType.Absolute);

        var (ox, oy) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);

        // Verschiebung damit der gewählte Bezugspunkt an der richtigen Textfeldecke/-kante liegt
        double textWEff = p.TextBreite > 0 ? p.TextBreite : ft.Width * scale;
        if (p.Bezugspunkt.Contains("Oben"))                                       oy -= fieldH;
        if (p.Bezugspunkt.Contains("rechts", StringComparison.OrdinalIgnoreCase)) ox -= textWEff;
        if (p.Bezugspunkt is "Mitte" or "Oben Mitte" or "Unten Mitte")            ox -= textWEff / 2;

        if (flat2.CanFreeze)       flat2.Freeze();
        if (flatDisplay.CanFreeze) flatDisplay.Freeze();
        if (geo2.CanFreeze)        geo2.Freeze();
        return new TextGeoCtx(geo2, flat2, flatDisplay, scale, multiH, ox, oy, yOffset);
    }

    // ── Skia-basierte Textgeometrie (FreeType-Pfade, deutlich schneller) ────
    public static TextGeoCtx BuildTextGeoSk(GraviereParams p, double workW, double workH)
    {
        const float emSize = 1000f;
        using var typeface = SkiaSharp.SKTypeface.FromFamilyName(p.FontFamily)
                             ?? SkiaSharp.SKTypeface.Default;
        using var paint = new SkiaSharp.SKPaint
        {
            Typeface    = typeface,
            TextSize    = emSize,
            IsAntialias = false,
            TextAlign   = SkiaSharp.SKTextAlign.Left,
        };

        var m = paint.FontMetrics;
        // Ascent ist negativ (oberhalb Baseline), Descent positiv (unterhalb)
        float lineH       = m.Descent - m.Ascent;
        float lineSpacing = lineH + m.Leading;

        double scale = p.FontSizeMm > 0 ? p.FontSizeMm / lineH : 1.0;

        bool bezugRechts = p.Bezugspunkt.Contains("rechts", StringComparison.OrdinalIgnoreCase);
        bool alignRight  = p.Ausrichtung == "Rechts" || bezugRechts;
        bool alignCenter = p.Ausrichtung == "Mitte" && !bezugRechts;

        var   lines    = (string.IsNullOrEmpty(p.Text) ? " " : p.Text).Split('\n');
        int   numLines = lines.Length;
        float maxWU    = (p.TextBreite > 0 && scale > 0) ? (float)(p.TextBreite / scale) : float.MaxValue;

        using var fullPath = new SkiaSharp.SKPath();
        for (int i = 0; i < numLines; i++)
        {
            string line      = string.IsNullOrEmpty(lines[i]) ? " " : lines[i];
            float  yBaseline = -m.Ascent + lineSpacing * i;  // Y der Baseline (Textoberseite bei y=0)
            using var lp = paint.GetTextPath(line, 0, yBaseline);

            if (maxWU < float.MaxValue && (alignRight || alignCenter))
            {
                float lw = paint.MeasureText(line);
                float dx = alignRight ? maxWU - lw : (maxWU - lw) * 0.5f;
                if (dx > 0.01f) lp.Offset(dx, 0);
            }
            fullPath.AddPath(lp);
        }

        double multiH = numLines == 1 ? lineH : lineSpacing * (numLines - 1) + lineH;

        // Toleranz in Skia-Font-Einheiten (identisch zu WPF ToleranceType.Absolute bei emSize=1000)
        // 2.0 bzw. 0.5 Einheiten entsprechen bei scale≈0.01 mm/Einheit → 0.02 / 0.005 mm Abweichung
        const float tolCoarse = 2.0f;
        const float tolFine   = 0.5f;

        var flatFigs    = SkFlattenPath(fullPath, tolCoarse);
        var displayFigs = SkFlattenPath(fullPath, tolFine);

        // WPF PathGeometry mit einzelnen LineSegments aufbauen — so erkennt
        // ComputeVCarveCircles alle Punkte als Segmentgrenzen (segBounds[k]=true),
        // und der Tangenten-Dot-Product-Filter unterscheidet echte Ecken von Kurven.
        static System.Windows.Media.PathGeometry ToWpf(
            List<(List<(float X, float Y)> Pts, bool Closed)> figs)
        {
            var geo = new System.Windows.Media.PathGeometry();
            foreach (var (pts, closed) in figs)
            {
                if (pts.Count < 2) continue;
                var fig = new System.Windows.Media.PathFigure
                {
                    StartPoint = new System.Windows.Point(pts[0].X, pts[0].Y),
                    IsClosed   = closed,
                    IsFilled   = closed,
                };
                for (int k = 1; k < pts.Count; k++)
                    fig.Segments.Add(new System.Windows.Media.LineSegment(
                        new System.Windows.Point(pts[k].X, pts[k].Y), false));
                geo.Figures.Add(fig);
            }
            if (geo.CanFreeze) geo.Freeze();
            return geo;
        }

        var flat2       = ToWpf(flatFigs);
        var flatDisplay = ToWpf(displayFigs);

        var (ox, oy) = ConvertBezugspunkt(p.Bezugspunkt, p.XRel, p.YRel, workW, workH);

        double fieldH  = p.TextHoehe > 0 ? p.TextHoehe
                       : (p.FontSizeMm > 0 ? p.FontSizeMm : multiH * scale);
        double yOffset = (fieldH - multiH * scale) / 2.0;

        // Effektive Textbreite aus den Display-Konturen bestimmen
        float maxX = 0f;
        foreach (var (pts, _) in displayFigs)
            foreach (var (px, _) in pts)
                if (px > maxX) maxX = px;
        double textWEff = p.TextBreite > 0 ? p.TextBreite : maxX * scale;

        if (p.Bezugspunkt.Contains("Oben"))                                       oy -= fieldH;
        if (p.Bezugspunkt.Contains("rechts", StringComparison.OrdinalIgnoreCase)) ox -= textWEff;
        if (p.Bezugspunkt is "Mitte" or "Oben Mitte" or "Unten Mitte")            ox -= textWEff / 2;

        return new TextGeoCtx(System.Windows.Media.Geometry.Empty,
                              flat2, flatDisplay, scale, multiH, ox, oy, yOffset);
    }

    // ── Skia-Pfad-Flattening: Bezier → Polylinien ────────────────────────────

    private static List<(List<(float X, float Y)> Pts, bool Closed)>
        SkFlattenPath(SkiaSharp.SKPath path, float tol)
    {
        tol = Math.Max(tol, 0.001f);
        var result  = new List<(List<(float, float)>, bool)>();
        var current = new List<(float, float)>();
        bool curClosed = false;

        using var iter = path.CreateIterator(false);
        var pts = new SkiaSharp.SKPoint[4];
        SkiaSharp.SKPathVerb verb;

        while ((verb = iter.Next(pts)) != SkiaSharp.SKPathVerb.Done)
        {
            switch (verb)
            {
                case SkiaSharp.SKPathVerb.Move:
                    if (current.Count > 1) result.Add((current, curClosed));
                    current   = [(pts[0].X, pts[0].Y)];
                    curClosed = false;
                    break;
                case SkiaSharp.SKPathVerb.Line:
                    current.Add((pts[1].X, pts[1].Y));
                    break;
                case SkiaSharp.SKPathVerb.Quad:
                    SkFlattenQuad(pts[0], pts[1], pts[2], tol, current);
                    break;
                case SkiaSharp.SKPathVerb.Cubic:
                    SkFlattenCubic(pts[0], pts[1], pts[2], pts[3], tol, current);
                    break;
                case SkiaSharp.SKPathVerb.Conic:
                    // Gewicht nahe 1 (TrueType-Kurven) → exakt wie quadratisch; sonst gut genug
                    SkFlattenQuad(pts[0], pts[1], pts[2], tol, current);
                    break;
                case SkiaSharp.SKPathVerb.Close:
                    curClosed = true;
                    if (current.Count > 1) result.Add((current, true));
                    current   = [];
                    curClosed = false;
                    break;
            }
        }
        if (current.Count > 1) result.Add((current, curClosed));
        return result;
    }

    private static SkiaSharp.SKPoint SkMid(SkiaSharp.SKPoint a, SkiaSharp.SKPoint b)
        => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

    private static float SkPtLineDist2(SkiaSharp.SKPoint p,
                                       SkiaSharp.SKPoint a, SkiaSharp.SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-12f) { dx = p.X - a.X; dy = p.Y - a.Y; return dx * dx + dy * dy; }
        float t  = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
        float nx = p.X - (a.X + t * dx), ny = p.Y - (a.Y + t * dy);
        return nx * nx + ny * ny;
    }

    private static void SkFlattenQuad(SkiaSharp.SKPoint p0, SkiaSharp.SKPoint p1,
                                      SkiaSharp.SKPoint p2, float tol,
                                      List<(float, float)> out_, int depth = 0)
    {
        if (depth > 12 || SkPtLineDist2(p1, p0, p2) < tol * tol)
        { out_.Add((p2.X, p2.Y)); return; }
        var m01 = SkMid(p0, p1);
        var m12 = SkMid(p1, p2);
        var m   = SkMid(m01, m12);
        SkFlattenQuad(p0, m01, m,   tol, out_, depth + 1);
        SkFlattenQuad(m,  m12, p2,  tol, out_, depth + 1);
    }

    private static void SkFlattenCubic(SkiaSharp.SKPoint p0, SkiaSharp.SKPoint p1,
                                       SkiaSharp.SKPoint p2, SkiaSharp.SKPoint p3,
                                       float tol, List<(float, float)> out_, int depth = 0)
    {
        if (depth > 16 ||
            (SkPtLineDist2(p1, p0, p3) < tol * tol &&
             SkPtLineDist2(p2, p0, p3) < tol * tol))
        { out_.Add((p3.X, p3.Y)); return; }
        var m01  = SkMid(p0, p1); var m12 = SkMid(p1, p2); var m23 = SkMid(p2, p3);
        var m012 = SkMid(m01, m12); var m123 = SkMid(m12, m23);
        var m    = SkMid(m012, m123);
        SkFlattenCubic(p0, m01, m012, m,   tol, out_, depth + 1);
        SkFlattenCubic(m,  m123, m23, p3,  tol, out_, depth + 1);
    }

    // ── Textfeld-Tasche (Clipper2-basiert, exakter Polygon-Offset) ──────────

    public static string TextfeldTasche(GraviereParams p, double workW, double workH)
    {
        const double allow   = 0.5; // Schlichtaufmaß mm
        const double arcTolMm = 0.05; // Bogentoleranz mm

        var sb  = new StringBuilder();
        var ctx = BuildTextGeo(p, workW, workH);

        double scale  = ctx.Scale;
        double multiH = ctx.MultiH;

        // WPF ↔ CNC-mm Koordinatenumrechnung
        double MX(double wx) => ctx.Ox + wx * scale;
        double MY(double wy) => ctx.Oy + ctx.YOffset + (multiH - wy) * scale;
        double WX(double cx) => (cx - ctx.Ox) / scale;
        double WY(double cy) => multiH - (cy - ctx.Oy - ctx.YOffset) / scale;

        double r     = p.FraeserD / 2.0;
        double step  = Math.Max(0.1, p.FraeserD * 0.5);
        double depth = -Math.Abs(p.ZTiefe);

        // Clipper2 arbeitet in WPF-Koordinaten (Y-down) — 1 WPF-Einheit = scale mm
        double rWpf     = r     / scale;
        double allowWpf = allow / scale;
        double arcTol   = Math.Max(0.5, arcTolMm / scale);

        var geo = ctx.FlatDisplay;
        if (geo.Bounds.IsEmpty || scale < 1e-9)
        { sb.AppendLine("(kein Text)"); return sb.ToString(); }

        // WPF-PathFigure → Clipper2 PathD (in WPF-Koordinaten, Y nach unten)
        Clipper2Lib.PathD FigToPath(System.Windows.Media.PathFigure fig)
        {
            var path = new Clipper2Lib.PathD();
            path.Add(new Clipper2Lib.PointD(fig.StartPoint.X, fig.StartPoint.Y));
            foreach (var seg in fig.Segments)
            {
                IEnumerable<System.Windows.Point> pts = seg switch
                {
                    System.Windows.Media.PolyLineSegment pls => pls.Points,
                    System.Windows.Media.LineSegment ls      => [ls.Point],
                    _                                         => []
                };
                foreach (var pt in pts)
                    path.Add(new Clipper2Lib.PointD(pt.X, pt.Y));
            }
            return path;
        }

        // Exakte Schnittlinienberechnung: horizontale Linie wpfY schneidet die Offset-Pfade
        // Gibt sortierte CNC-mm X-Werte zurück; EvenOdd-Paarung ergibt die Fräsintervalle.
        List<double> Scanline(Clipper2Lib.PathsD paths, double wpfY)
        {
            var xs = new List<double>();
            foreach (var path in paths)
            {
                int n = path.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = path[i]; var b = path[(i + 1) % n];
                    if ((a.y < wpfY) != (b.y < wpfY))
                    {
                        double t = (wpfY - a.y) / (b.y - a.y);
                        xs.Add(MX(a.x + t * (b.x - a.x)));
                    }
                }
            }
            xs.Sort();
            return xs;
        }

        // Prüft ob ein CNC-mm Punkt in den Offset-Pfaden liegt (EvenOdd-Regel)
        bool InOffsetPaths(Clipper2Lib.PathsD paths, double cncX, double cncY)
        {
            var pt    = new Clipper2Lib.PointD(WX(cncX), WY(cncY));
            int count = 0;
            foreach (var path in paths)
                if (Clipper2Lib.Clipper.PointInPolygon(pt, path, 6) !=
                    Clipper2Lib.PointInPolygonResult.IsOutside) count++;
            return (count & 1) == 1;
        }

        // Diagonalen Übergang (lastX/Y → targetX/scanY) auf Kollisionsfreiheit prüfen
        bool CanDiag(Clipper2Lib.PathsD paths, double lx, double ly, double tx, double ty)
        {
            for (int i = 1; i <= 5; i++)
            {
                double t = i / 6.0;
                if (!InOffsetPaths(paths, lx + (tx - lx) * t, ly + (ty - ly) * t)) return false;
            }
            return true;
        }

        sb.AppendLine("(Textfeld-Tasche fräsen)");
        sb.AppendLine($"(Text: {p.Text.Replace('\n', ' ').Replace('\r', ' ')})");
        sb.AppendLine($"(Font: {p.FontFamily}, {F(p.FontSizeMm)} mm)");
        sb.AppendLine();
        sb.AppendLine(Sz());

        var groups = GroupFiguresByLetter(geo.Figures.ToList());

        foreach (var group in groups)
        {
            // Buchstabenpfade für Clipper2 aufbauen
            var letterPaths = new Clipper2Lib.PathsD();
            foreach (var fig in group)
                if (fig.IsClosed) letterPaths.Add(FigToPath(fig));
            if (letterPaths.Count == 0) continue;

            // Exakter Polygon-Offset: Schrupp (r+allow) und Schlicht (r)
            var roughPaths  = Clipper2Lib.Clipper.InflatePaths(letterPaths, -(rWpf + allowWpf),
                                  Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon, 2.0, 6, arcTol);
            var finishPaths = Clipper2Lib.Clipper.InflatePaths(letterPaths, -rWpf,
                                  Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon, 2.0, 6, arcTol);

            // ── Schrupp-Zickzack ──────────────────────────────────────────────
            // EvenOdd-Scanline über alle roughPaths gemeinsam (korrekt für Löcher).
            // Intervalle werden spaltenweise verarbeitet: erst alle linken Segmente
            // (ivs[0]) von unten nach oben, dann alle rechten (ivs[1]) usw.
            // Dadurch je ein Eintauchen pro Streifen statt eines pro Zeile.
            if (roughPaths.Count > 0)
            {
                double allMinY = roughPaths.SelectMany(q => q).Min(pt => MY(pt.y));
                double allMaxY = roughPaths.SelectMany(q => q).Max(pt => MY(pt.y));

                var rows = new List<(double scanY, List<(double x0, double x1)> ivs)>();
                for (double scanY = allMinY; scanY <= allMaxY + step * 0.01; scanY += step)
                {
                    var xCuts = Scanline(roughPaths, WY(scanY));
                    var ivs   = new List<(double x0, double x1)>();
                    for (int i = 0; i + 1 < xCuts.Count; i += 2)
                        if (xCuts[i + 1] > xCuts[i] + 0.01) ivs.Add((xCuts[i], xCuts[i + 1]));
                    rows.Add((scanY, ivs));
                }

                int maxIvs = rows.Count > 0 ? rows.Max(r => r.ivs.Count) : 0;

                for (int pass = 0; pass < maxIvs; pass++)
                {
                    bool   atDepth   = false;
                    double lastX = 0, lastY = 0;
                    bool   rightward = true;
                    int    lastRi    = -1;
                    double lastIvX0  = 0, lastIvX1 = 0;

                    for (int ri = 0; ri < rows.Count; ri++)
                    {
                        var (scanY, ivs) = rows[ri];
                        if (pass >= ivs.Count) { rightward = !rightward; continue; }

                        var iv         = ivs[pass];
                        double targetX = rightward ? iv.x0 : iv.x1;

                        // adjacent: aufeinanderfolgende Zeile UND beide Punkte
                        // (Startpunkt lastX und Ziel targetX) liegen jeweils im
                        // Intervall der anderen Zeile (±step). Bidirektional, damit
                        // Übergänge breites→schmales Intervall nicht fälschlich
                        // als sicher eingestuft werden (z.B. Kreuzung→Arm).
                        bool adjacent = (ri == lastRi + 1) &&
                                        (targetX >= lastIvX0 - step) &&
                                        (targetX <= lastIvX1 + step) &&
                                        (lastX   >= iv.x0    - step) &&
                                        (lastX   <= iv.x1    + step);

                        if (!atDepth)
                        {
                            sb.AppendLine($"G00 X{F(targetX)} Y{F(scanY)}");
                            sb.AppendLine($"G01 Z{F(depth)} F{(int)(p.Vorschub * 0.3)}");
                            atDepth = true;
                        }
                        else if (adjacent || CanDiag(roughPaths, lastX, lastY, targetX, scanY))
                            sb.AppendLine($"G01 X{F(targetX)} Y{F(scanY)} F{(int)p.Vorschub}");
                        else
                        {
                            sb.AppendLine(Sz());
                            sb.AppendLine($"G00 X{F(targetX)} Y{F(scanY)}");
                            sb.AppendLine($"G01 Z{F(depth)} F{(int)(p.Vorschub * 0.3)}");
                        }

                        if (rightward) sb.AppendLine($"G01 X{F(iv.x1)} F{(int)p.Vorschub}");
                        else           sb.AppendLine($"G01 X{F(iv.x0)} F{(int)p.Vorschub}");

                        lastX    = rightward ? iv.x1 : iv.x0;
                        lastY    = scanY;
                        rightward = !rightward;
                        lastRi   = ri;
                        lastIvX0 = iv.x0;
                        lastIvX1 = iv.x1;
                    }
                    if (atDepth) sb.AppendLine(Sz());
                }
            }
            else
                sb.AppendLine("(Buchstabe zu schmal für Schrupp-Durchgang)");

            // ── Schlicht-Kontur im Gegenlauf ──────────────────────────────────
            if (finishPaths.Count == 0)
            { sb.AppendLine("(Buchstabe zu schmal für Schlicht-Kontur)"); continue; }

            foreach (var fPath in finishPaths)
            {
                if (fPath.Count < 3) continue;
                // Konvertierung in CNC-mm + Gegenlauf (Umkehrung)
                var cncPts = fPath.Select(pt => (X: MX(pt.x), Y: MY(pt.y))).ToList();
                cncPts.Reverse();

                double sx = cncPts[0].X, sy = cncPts[0].Y;
                sb.AppendLine($"G00 X{F(sx)} Y{F(sy)}");
                sb.AppendLine($"G01 Z{F(depth)} F{(int)(p.Vorschub * 0.3)}");
                foreach (var pt2 in cncPts.Skip(1))
                    sb.AppendLine($"G01 X{F(pt2.X)} Y{F(pt2.Y)} F{(int)p.Vorschub}");
                sb.AppendLine($"G01 X{F(sx)} Y{F(sy)} F{(int)p.Vorschub}");
                sb.AppendLine(Sz());
            }
        }

        return sb.ToString();
    }

    // Buchstaben-Figuren nach Buchstabe gruppieren:
    // Löcher (Figuren deren Bounds in einer anderen enthalten sind) werden
    // ihrer umgebenden Aussenkontur zugeordnet.
    private static List<List<System.Windows.Media.PathFigure>> GroupFiguresByLetter(
        List<System.Windows.Media.PathFigure> figures)
    {
        if (figures.Count == 0) return [];

        var infos = figures
            .Select(f => { var pg = new System.Windows.Media.PathGeometry(); pg.Figures.Add(f); return (Fig: f, Bounds: pg.Bounds); })
            .Where(t => !t.Bounds.IsEmpty)
            .ToList();

        var isHole = new bool[infos.Count];
        for (int i = 0; i < infos.Count; i++)
            for (int j = 0; j < infos.Count; j++)
                if (i != j && TaBoundsContains(infos[j].Bounds, infos[i].Bounds))
                    { isHole[i] = true; break; }

        var groups = infos
            .Select((t, i) => (t, i))
            .Where(x => !isHole[x.i])
            .OrderBy(x => x.t.Bounds.Left)
            .Select(x => new List<System.Windows.Media.PathFigure> { x.t.Fig })
            .ToList();

        for (int i = 0; i < infos.Count; i++)
        {
            if (!isHole[i]) continue;
            List<System.Windows.Media.PathFigure>? best = null;
            double bestArea = double.MaxValue;
            foreach (var g in groups)
            {
                var pgG = new System.Windows.Media.PathGeometry(); pgG.Figures.Add(g[0]);
                var b = pgG.Bounds;
                if (TaBoundsContains(b, infos[i].Bounds))
                {
                    double a = b.Width * b.Height;
                    if (a < bestArea) { bestArea = a; best = g; }
                }
            }
            best?.Add(infos[i].Fig);
        }
        return groups;
    }

    private static bool TaBoundsContains(System.Windows.Rect outer, System.Windows.Rect inner)
    {
        const double eps = 2.0;
        return inner.Left >= outer.Left - eps && inner.Right  <= outer.Right  + eps &&
               inner.Top  >= outer.Top  - eps && inner.Bottom <= outer.Bottom + eps;
    }

    // ── V-Carve: Einbeschriebene Kreise (Konturabtastung / Medialachs) ───

    /// CNC-Koordinaten (mm), Radius (mm) und Figur-Index eines einbeschriebenen Kreises.
    public record VCarveCircle(double X, double Y, double R, int FigIdx = -1, bool IsFan = false);

    /// <summary>
    /// Läuft entlang einer geschlossenen PathFigure und liefert alle
    /// stepWpf-WPF-Einheiten einen Punkt (x,y) mit Einheitstangente (tx,ty).
    /// </summary>
    private static IEnumerable<(double x, double y, double tx, double ty)>
        WalkFigure(System.Windows.Media.PathFigure fig, double stepWpf)
    {
        // Stützpunkte sammeln
        var pts = new List<(double x, double y)>();
        pts.Add((fig.StartPoint.X, fig.StartPoint.Y));
        foreach (var seg in fig.Segments)
        {
            if (seg is System.Windows.Media.PolyLineSegment pls)
                foreach (var p in pls.Points) pts.Add((p.X, p.Y));
            else if (seg is System.Windows.Media.LineSegment ls)
                pts.Add((ls.Point.X, ls.Point.Y));
        }
        if (pts.Count < 3) yield break;
        int n = pts.Count;

        // Kanten mit kumulativen Startpositionen aufbauen
        var edges = new (double ex, double ey, double len,
                         double utx, double uty, double cum)[n];
        int eCount = 0;
        double cumLen = 0;
        for (int i = 0; i < n; i++)
        {
            int j  = (i + 1) % n;
            double dx = pts[j].x - pts[i].x;
            double dy = pts[j].y - pts[i].y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-10) continue;
            edges[eCount++] = (pts[i].x, pts[i].y, len, dx / len, dy / len, cumLen);
            cumLen += len;
        }
        if (eCount == 0 || cumLen < 1e-9) yield break;

        int ei = 0;
        for (double s = 0.0; s < cumLen - 1e-9; s += stepWpf)
        {
            // Zur richtigen Kante vorrücken
            while (ei + 1 < eCount && edges[ei + 1].cum <= s + 1e-12)
                ei++;
            var e = edges[ei];
            double t = Math.Clamp(s - e.cum, 0.0, e.len);
            yield return (e.ex + e.utx * t, e.ey + e.uty * t, e.utx, e.uty);
        }
    }

    /// <summary>
    /// Berechnet für jeden Konturpunkt alle 0,5 mm den größten einbeschriebenen
    /// Kreis, der innen tangiert und keine andere Konturlinie schneidet.
    /// Gibt CNC-Koordinaten (mm) und Radien zurück.
    /// </summary>
    /// Signierte Fläche einer Polygon-Liste in WPF-Koordinaten (Y-nach-unten).
    /// > 0 → Uhrzeigersinn (CW) auf dem Bildschirm
    /// < 0 → Gegenuhrzeigersinn (CCW)
    private static double SignedArea(System.Windows.Media.PathFigure fig)
    {
        double a = 0;
        double prevX = fig.StartPoint.X, prevY = fig.StartPoint.Y;
        double firstX = prevX, firstY = prevY;
        foreach (var seg in fig.Segments)
        {
            if (seg is System.Windows.Media.PolyLineSegment pls)
                foreach (var pt in pls.Points)
                { a += prevX * pt.Y - pt.X * prevY; prevX = pt.X; prevY = pt.Y; }
            else if (seg is System.Windows.Media.LineSegment ls)
            { var pt = ls.Point; a += prevX * pt.Y - pt.X * prevY; prevX = pt.X; prevY = pt.Y; }
        }
        a += prevX * firstY - firstX * prevY; // closing edge
        return a * 0.5;
    }

    /// Quadratische Distanz Punkt→Segment (kein sqrt nötig für Vergleiche).
    private static double PtSegDist2(double px, double py,
                                     double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-20) return (px - ax) * (px - ax) + (py - ay) * (py - ay);
        double t  = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0.0, 1.0);
        double qx = ax + t * dx - px;
        double qy = ay + t * dy - py;
        return qx * qx + qy * qy;
    }

    // Wrapper: BuildTextGeo muss auf dem UI/STA-Thread aufgerufen werden.
    // Für Hintergrund-Berechnungen: ctx auf UI-Thread holen, dann Überladung mit ctx aufrufen.
    public static List<VCarveCircle> ComputeVCarveCircles(
        GraviereParams p, double workW, double workH, double sampleStepMm = 0.5)
        => ComputeVCarveCircles(p,
               p.UseSkia ? BuildTextGeoSk(p, workW, workH) : BuildTextGeo(p, workW, workH),
               sampleStepMm);

    public static List<VCarveCircle> ComputeVCarveCircles(
        GraviereParams p, TextGeoCtx ctx, double sampleStepMm = 0.5)
    {
        if (ctx.FlatDisplay.Bounds.IsEmpty) return [];

        double scale  = ctx.Scale;
        double multiH = ctx.MultiH;
        double stepW  = sampleStepMm / scale;

        double maxRmm = p.SchneidenWinkel < 179.9
            ? p.ZTiefe * Math.Tan(p.SchneidenWinkel * 0.5 * Math.PI / 180.0)
            : p.FraeserD * 0.5;
        double maxRw = maxRmm / scale;

        // Feine Geometrie (0.5) für Bogenabtastung, Normalen UND InGlyph-Ringe.
        // Ein einheitliches Polygon stellt sicher, dass Kreise die der Binary-Search
        // akzeptiert auch InGlyph bestehen — keine Inkonsistenz zwischen Toleranzen.
        var figs = ctx.FlatDisplay.Figures.Where(f => f.IsClosed).ToList();
        if (figs.Count == 0) return [];

        double probe = 0.1 / scale;

        // ── Schneller Even-Odd Point-in-Polygon (ersetzt WPF fillGeo.FillContains) ──────
        // Alle Figur-Ringe vorab aus den bereits geflatteneten Polygonen aufbauen.
        // Even-Odd Ray-Casting ist identisch zu WPFs FillRule.EvenOdd, aber rein arithmetisch
        // und damit ~50-100× schneller als der WPF-Geometry-Aufruf.
        var allRings  = new (double x, double y)[figs.Count][];
        var ringYMin  = new double[figs.Count];
        var ringYMax  = new double[figs.Count];
        for (int ri = 0; ri < figs.Count; ri++)
        {
            var f  = figs[ri];
            var rg = new List<(double x, double y)>();
            rg.Add((f.StartPoint.X, f.StartPoint.Y));
            foreach (var seg in f.Segments)
            {
                if (seg is System.Windows.Media.PolyLineSegment pls)
                    foreach (var pt in pls.Points) rg.Add((pt.X, pt.Y));
                else if (seg is System.Windows.Media.LineSegment ls)
                    rg.Add((ls.Point.X, ls.Point.Y));
            }
            allRings[ri] = rg.ToArray();
            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var (_, ry) in allRings[ri]) { if (ry < yMin) yMin = ry; if (ry > yMax) yMax = ry; }
            ringYMin[ri] = yMin; ringYMax[ri] = yMax;
        }
        bool InGlyph(double px, double py)
        {
            bool inside = false;
            for (int ri = 0; ri < allRings.Length; ri++)
            {
                if (py < ringYMin[ri] || py > ringYMax[ri]) continue;
                var ring = allRings[ri];
                int n = ring.Length;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = ring[i].x, yi = ring[i].y;
                    double xj = ring[j].x, yj = ring[j].y;
                    if ((yi > py) != (yj > py) &&
                        px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                        inside = !inside;
                }
            }
            return inside;
        }

        // ── Original-Segment-Endpunkte sammeln (zur Unterscheidung echter Ecken vs. Kurven-Approx) ──
        // Punkte aus geraden Segmenten im Original sind echte Ecken; Punkte aus Bezier-Approximation nicht.
        // Quantisierungsfaktor: 64 Einheiten pro Pixel bei emSize=1000 → ~0.016 Einheit Toleranz.
        static long Q(double v) => (long)Math.Round(v * 64);
        var origEndpts = new HashSet<(long, long)>();
        void CollectPg(System.Windows.Media.PathGeometry pg)
        {
            foreach (var pf in pg.Figures)
            {
                origEndpts.Add((Q(pf.StartPoint.X), Q(pf.StartPoint.Y)));
                foreach (var seg in pf.Segments)
                {
                    switch (seg)
                    {
                        case System.Windows.Media.LineSegment lseg:
                            origEndpts.Add((Q(lseg.Point.X), Q(lseg.Point.Y)));
                            break;
                        case System.Windows.Media.PolyLineSegment plseg:
                            foreach (var pt in plseg.Points)
                                origEndpts.Add((Q(pt.X), Q(pt.Y)));
                            break;
                        case System.Windows.Media.BezierSegment bseg:
                            origEndpts.Add((Q(bseg.Point3.X), Q(bseg.Point3.Y)));
                            break;
                        case System.Windows.Media.PolyBezierSegment pbseg:
                            for (int i = 2; i < pbseg.Points.Count; i += 3)
                                origEndpts.Add((Q(pbseg.Points[i].X), Q(pbseg.Points[i].Y)));
                            break;
                        case System.Windows.Media.QuadraticBezierSegment qseg:
                            origEndpts.Add((Q(qseg.Point2.X), Q(qseg.Point2.Y)));
                            break;
                        case System.Windows.Media.PolyQuadraticBezierSegment pqseg:
                            for (int i = 1; i < pqseg.Points.Count; i += 2)
                                origEndpts.Add((Q(pqseg.Points[i].X), Q(pqseg.Points[i].Y)));
                            break;
                        case System.Windows.Media.ArcSegment aseg:
                            origEndpts.Add((Q(aseg.Point.X), Q(aseg.Point.Y)));
                            break;
                    }
                }
            }
        }
        var origGeo = ctx.AlignedGeo;
        if (origGeo is System.Windows.Media.PathGeometry origPg)
            CollectPg(origPg);
        else if (origGeo is System.Windows.Media.GeometryGroup origGg)
            foreach (var child in origGg.Children)
                if (child is System.Windows.Media.PathGeometry childPg)
                    CollectPg(childPg);
        else
            CollectPg(origGeo.GetOutlinedPathGeometry());

        // ── Alle Polygon-Segmente vorberechnen (Einwärts-Normale + Tangente) ─
        var segs = new List<(double ax, double ay, double bx, double by,
                              int figIdx, double inx, double iny,
                              double tsx, double tsy)>(figs.Count * 500);

        // Per-Figur-Daten für die Bogen-Abtastung
        var figPts       = new List<(double x, double y)[]>(figs.Count);
        var figEdgeTsx   = new List<double[]>(figs.Count);
        var figEdgeTsy   = new List<double[]>(figs.Count);
        var figSegBounds = new List<bool[]>(figs.Count); // true = Original-Segment-Grenze

        for (int fi = 0; fi < figs.Count; fi++)
        {
            var fig = figs[fi];
            var ptsList   = new List<(double x, double y)>();
            var segBounds = new List<bool>(); // welche Vertices sind echte Segment-Endpunkte?

            // StartPoint: Grenze zwischen letztem und erstem Segment (geschlossener Pfad)
            ptsList.Add((fig.StartPoint.X, fig.StartPoint.Y));
            segBounds.Add(true);

            foreach (var seg in fig.Segments)
            {
                if (seg is System.Windows.Media.PolyLineSegment pls)
                {
                    for (int pi = 0; pi < pls.Points.Count; pi++)
                    {
                        var pt = pls.Points[pi];
                        ptsList.Add((pt.X, pt.Y));
                        // Echter Segment-Endpunkt wenn: letzter Punkt ODER im Original-Endpunkt-Set
                        // (gerade Original-Segmente haben alle Zwischenpunkte als echte Ecken;
                        //  Bezier-Approximation hat nur den letzten Punkt als echten Endpunkt)
                        bool isBound = pi == pls.Points.Count - 1 ||
                                       origEndpts.Contains((Q(pt.X), Q(pt.Y)));
                        segBounds.Add(isBound);
                    }
                }
                else if (seg is System.Windows.Media.LineSegment ls)
                {
                    ptsList.Add((ls.Point.X, ls.Point.Y));
                    segBounds.Add(true);
                }
            }

            var pts  = ptsList.ToArray();
            int nPts = pts.Length;

            var edgeTsx = new double[nPts];
            var edgeTsy = new double[nPts];

            for (int k = 0; k < nPts; k++)
            {
                int k2 = (k + 1) % nPts;
                double ddx = pts[k2].x - pts[k].x, ddy = pts[k2].y - pts[k].y;
                double sLen = Math.Sqrt(ddx * ddx + ddy * ddy);
                double etsx = 0, etsy = 0, einx = 0, einy = 0;
                if (sLen > 1e-10)
                {
                    etsx = ddx / sLen; etsy = ddy / sLen;
                    double lnx = -etsy, lny = etsx;
                    double mx = (pts[k].x + pts[k2].x) * 0.5;
                    double my = (pts[k].y + pts[k2].y) * 0.5;
                    bool lIn = InGlyph(mx + lnx*probe, my + lny*probe);
                    bool rIn = InGlyph(mx - lnx*probe, my - lny*probe);
                    if      ( lIn && !rIn) { einx =  lnx; einy =  lny; }
                    else if (!lIn &&  rIn) { einx = -lnx; einy = -lny; }
                }
                edgeTsx[k] = etsx; edgeTsy[k] = etsy;
                segs.Add((pts[k].x, pts[k].y, pts[k2].x, pts[k2].y, fi, einx, einy, etsx, etsy));
            }
            figPts.Add(pts);
            figEdgeTsx.Add(edgeTsx);
            figEdgeTsy.Add(edgeTsy);
            figSegBounds.Add(segBounds.ToArray());
        }
        var segsArr = segs.ToArray();
        var result  = new List<VCarveCircle>();

        // ── Räumliches Gitter: nur nahe Segmente bei Binarysuche prüfen ──────
        // cellSz = 2*maxRw → jede Zelle deckt alle Segmente innerhalb maxRw ab.
        // Segmente werden mit maxRw-Puffer eingetragen → Single-Cell-Lookup reicht.
        var   flatBounds = ctx.FlatDisplay.Bounds;
        double gridX0    = flatBounds.X - maxRw;
        double gridY0    = flatBounds.Y - maxRw;
        double cellSz    = Math.Max(maxRw * 2.0, 1e-6);
        int    gCols     = Math.Max(1, (int)Math.Ceiling((flatBounds.Width  + 2 * maxRw) / cellSz));
        int    gRows     = Math.Max(1, (int)Math.Ceiling((flatBounds.Height + 2 * maxRw) / cellSz));
        var    gridCells = new int[gRows * gCols][];
        {
            var tmp = new List<int>[gRows * gCols];
            for (int i = 0; i < tmp.Length; i++) tmp[i] = [];
            for (int si = 0; si < segsArr.Length; si++)
            {
                var sg = segsArr[si];
                int cx0 = Math.Max(0, (int)((Math.Min(sg.ax, sg.bx) - maxRw - gridX0) / cellSz));
                int cx1 = Math.Min(gCols - 1, (int)((Math.Max(sg.ax, sg.bx) + maxRw - gridX0) / cellSz));
                int cy0 = Math.Max(0, (int)((Math.Min(sg.ay, sg.by) - maxRw - gridY0) / cellSz));
                int cy1 = Math.Min(gRows - 1, (int)((Math.Max(sg.ay, sg.by) + maxRw - gridY0) / cellSz));
                for (int gy2 = cy0; gy2 <= cy1; gy2++)
                    for (int gx2 = cx0; gx2 <= cx1; gx2++)
                        tmp[gy2 * gCols + gx2].Add(si);
            }
            for (int i = 0; i < tmp.Length; i++) gridCells[i] = tmp[i].ToArray();
        }

        // ── Pro Figur: Konturkreise + Ecken-Kreise in Bogenreihenfolge ──────
        // Segment-Startindex pro Figur für segsArr-Zugriff
        var figSegStart = new int[figs.Count];
        { int fso = 0; for (int fi2 = 0; fi2 < figs.Count; fi2++) { figSegStart[fi2] = fso; fso += figPts[fi2].Length; } }

        const double cornerStepRad = 2.0 * Math.PI / 180.0;

        // Jede Figur (Buchstabe) ist unabhängig → parallel verarbeiten.
        // fillGeo ist Frozen → thread-sicher; segsArr/gridCells sind read-only.
        var resultsPerFig = new List<VCarveCircle>[figs.Count];

        System.Threading.Tasks.Parallel.For(0, figs.Count, fi =>
        {
            var pts       = figPts[fi];
            int nPts      = pts.Length;
            var edgeTsx   = figEdgeTsx[fi];
            var edgeTsy   = figEdgeTsy[fi];
            var segBounds = figSegBounds[fi];
            int so        = figSegStart[fi];
            var localResult = new List<VCarveCircle>();

            // Bogenlängen
            var arcLen = new double[nPts + 1];
            for (int k = 0; k < nPts; k++)
            {
                int    k2 = (k + 1) % nPts;
                double dx = pts[k2].x - pts[k].x, dy = pts[k2].y - pts[k].y;
                arcLen[k + 1] = arcLen[k] + Math.Sqrt(dx * dx + dy * dy);
            }
            double totalArc = arcLen[nPts];
            if (totalArc < stepW) { resultsPerFig[fi] = localResult; return; }

            // Ecken-Karte
            var cornerAtArc = new Dictionary<double, int>();
            for (int k = 0; k < nPts; k++)
            {
                if (!segBounds[k]) continue;
                int kP = (k - 1 + nPts) % nPts;

                // Tangenten-Fallback: sehr kurze Segmente (letztes Bezier-Approx-Stück)
                // haben tsx=tsy=0 → Dot-Product wäre 0, glatte Kurve würde fälschlich als
                // Ecke erkannt. Daher nächstes gültiges Segment suchen.
                double tsPx = edgeTsx[kP], tsPy = edgeTsy[kP];
                if (tsPx * tsPx + tsPy * tsPy < 1e-18)
                {
                    for (int t = 1; t <= 5; t++)
                    {
                        int kk = (kP - t + nPts) % nPts;
                        if (edgeTsx[kk] * edgeTsx[kk] + edgeTsy[kk] * edgeTsy[kk] > 1e-18)
                        { tsPx = edgeTsx[kk]; tsPy = edgeTsy[kk]; break; }
                    }
                }
                double tsCx = edgeTsx[k], tsCy = edgeTsy[k];
                if (tsCx * tsCx + tsCy * tsCy < 1e-18)
                {
                    for (int t = 1; t <= 5; t++)
                    {
                        int kk = (k + t) % nPts;
                        if (edgeTsx[kk] * edgeTsx[kk] + edgeTsy[kk] * edgeTsy[kk] > 1e-18)
                        { tsCx = edgeTsx[kk]; tsCy = edgeTsy[kk]; break; }
                    }
                }
                // Keine gültige Tangente gefunden → Ecke nicht klassifizierbar → überspringen
                if (tsPx * tsPx + tsPy * tsPy < 1e-18) continue;
                if (tsCx * tsCx + tsCy * tsCy < 1e-18) continue;
                // Glatte Bezier-Übergänge (tangenten-kontinuierlich) überspringen.
                if (tsPx * tsCx + tsPy * tsCy > 0.990) continue;

                // Einwärts-Normale der anliegenden Segmente ermitteln.
                // Fallback: sehr kurze Segmente haben inx=iny=0 → nächstes gültiges suchen.
                var sP = segsArr[so + kP];
                if (sP.inx * sP.inx + sP.iny * sP.iny < 1e-18)
                {
                    for (int t = 1; t <= 5; t++)
                    {
                        var cand = segsArr[so + (kP - t + nPts) % nPts];
                        if (cand.inx * cand.inx + cand.iny * cand.iny > 1e-18) { sP = cand; break; }
                    }
                }
                if (sP.inx * sP.inx + sP.iny * sP.iny < 1e-18) continue;

                var sC = segsArr[so + k];
                if (sC.inx * sC.inx + sC.iny * sC.iny < 1e-18)
                {
                    for (int t = 1; t <= 5; t++)
                    {
                        var cand = segsArr[so + (k + t) % nPts];
                        if (cand.inx * cand.inx + cand.iny * cand.iny > 1e-18) { sC = cand; break; }
                    }
                }
                if (sC.inx * sC.inx + sC.iny * sC.iny < 1e-18) continue;

                double bX = sP.inx + sC.inx, bY = sP.iny + sC.iny;
                if (bX * bX + bY * bY < 1e-18) continue;
                cornerAtArc[arcLen[k]] = k;
            }

            // Abtastpositionen
            var sampleSet = new SortedSet<double>();
            for (double pos = 0; pos < totalArc; pos += stepW) sampleSet.Add(pos);
            foreach (var ap in cornerAtArc.Keys) sampleSet.Add(ap);

            // Interpolator
            (double px, double py, double tx, double ty) PolyInterp(double arcPos)
            {
                int lo = 0, hi = nPts - 1;
                while (lo < hi) { int mid2 = (lo + hi + 1) / 2; if (arcLen[mid2] <= arcPos) lo = mid2; else hi = mid2 - 1; }
                double ax  = pts[lo].x, ay = pts[lo].y;
                double bx2 = pts[(lo + 1) % nPts].x, by2 = pts[(lo + 1) % nPts].y;
                double eL  = arcLen[lo + 1] - arcLen[lo];
                double t   = eL > 1e-12 ? (arcPos - arcLen[lo]) / eL : 0;
                double ddx = bx2 - ax, ddy = by2 - ay;
                double len = Math.Sqrt(ddx * ddx + ddy * ddy);
                return (ax + t * ddx, ay + t * ddy,
                        len > 1e-12 ? ddx / len : 1.0,
                        len > 1e-12 ? ddy / len : 0.0);
            }

            // Größten einbeschriebenen Kreis ab (ox,oy) in Richtung (nx,ny)
            // 10 Iterationen → ~0.015 mm Genauigkeit bei maxRw ≤ 16 mm (reicht für 0.1 mm Schrittweite)
            VCarveCircle? TryPt(double ox, double oy, double nx, double ny, double minRmm = 0.02)
            {
                double rL = 0.0, rH = maxRw;
                for (int it = 0; it < 10; it++)
                {
                    double rm  = (rL + rH) * 0.5;
                    double ecx = ox + nx * rm, ecy = oy + ny * rm;
                    double r2  = rm * rm;
                    bool   ok  = InGlyph(ecx, ecy);
                    if (ok)
                    {
                        int gcx = Math.Clamp((int)((ecx - gridX0) / cellSz), 0, gCols - 1);
                        int gcy = Math.Clamp((int)((ecy - gridY0) / cellSz), 0, gRows - 1);
                        foreach (var si in gridCells[gcy * gCols + gcx])
                        {
                            var sg = segsArr[si];
                            if (PtSegDist2(ecx, ecy, sg.ax, sg.ay, sg.bx, sg.by) < r2 - 1e-9)
                            { ok = false; break; }
                        }
                    }
                    if (ok) rL = rm; else rH = rm;
                }
                if (rL * scale < minRmm) return null;
                double fcx = ox + nx * rL, fcy = oy + ny * rL;
                if (minRmm > 0 && !InGlyph(fcx, fcy)) return null;
                return new VCarveCircle(
                    ctx.Ox + fcx * scale,
                    ctx.Oy + ctx.YOffset + (multiH - fcy) * scale,
                    rL * scale, fi);
            }

            foreach (double arcPos in sampleSet)
            {
                bool isCorner = cornerAtArc.TryGetValue(arcPos, out int ck);

                if (!isCorner)
                {
                    // ── Regulärer Konturkreis ──────────────────────────────
                    var (px, py, tx, ty) = PolyInterp(arcPos);
                    double lnx = -ty, lny = tx;
                    bool leftIn  = InGlyph(px + lnx * probe, py + lny * probe);
                    bool rightIn = InGlyph(px - lnx * probe, py - lny * probe);
                    double cnx = 0, cny = 0;
                    if      ( leftIn && !rightIn) { cnx =  lnx; cny =  lny; }
                    else if (!leftIn &&  rightIn) { cnx = -lnx; cny = -lny; }

                    if (cnx != 0 || cny != 0)
                    {
                        double rLo = 0.0, rHi = maxRw;
                        for (int iter = 0; iter < 10; iter++)
                        {
                            double mid  = (rLo + rHi) * 0.5;
                            double cx   = px + cnx * mid, cy = py + cny * mid;
                            double mid2 = mid * mid;
                            bool   valid = true;
                            int gcx2 = Math.Clamp((int)((cx - gridX0) / cellSz), 0, gCols - 1);
                            int gcy2 = Math.Clamp((int)((cy - gridY0) / cellSz), 0, gRows - 1);
                            foreach (var si in gridCells[gcy2 * gCols + gcx2])
                            {
                                var sg = segsArr[si];
                                if (sg.figIdx == fi && sg.inx * cnx + sg.iny * cny > 0.0 && sg.tsx * tx + sg.tsy * ty > 0.866)
                                    continue;
                                if (PtSegDist2(cx, cy, sg.ax, sg.ay, sg.bx, sg.by) < mid2 - 1e-9)
                                { valid = false; break; }
                            }
                            if (valid) rLo = mid; else rHi = mid;
                        }
                        if (rLo * scale >= 0.05)
                        {
                            double wpfCx = px + cnx * rLo, wpfCy = py + cny * rLo;
                            if (InGlyph(wpfCx, wpfCy))
                                localResult.Add(new VCarveCircle(
                                    ctx.Ox + wpfCx * scale,
                                    ctx.Oy + ctx.YOffset + (multiH - wpfCy) * scale,
                                    rLo * scale, fi));
                        }
                    }
                    continue;
                }

                // ── Ecken-Kreise (konkav + konvex) ────────────────────────
                int  ckP  = (ck - 1 + nPts) % nPts;

                // Fallback: sehr kurze Segmente haben tsx=tsy=inx=iny=0.
                // Gleiches Schema wie in cornerAtArc-Detektion.
                var csP = segsArr[so + ckP];
                if (csP.inx * csP.inx + csP.iny * csP.iny < 1e-18)
                {
                    for (int t = 1; t <= 5; t++)
                    {
                        var cand = segsArr[so + (ckP - t + nPts) % nPts];
                        if (cand.inx * cand.inx + cand.iny * cand.iny > 1e-18) { csP = cand; break; }
                    }
                }
                var csC = segsArr[so + ck];
                if (csC.inx * csC.inx + csC.iny * csC.iny < 1e-18)
                {
                    for (int t = 1; t <= 5; t++)
                    {
                        var cand = segsArr[so + (ck + t) % nPts];
                        if (cand.inx * cand.inx + cand.iny * cand.iny > 1e-18) { csC = cand; break; }
                    }
                }

                double evx = pts[ck].x, evy = pts[ck].y;
                bool isConcave = csP.tsx * csC.tsy - csP.tsy * csC.tsx < 0;

                double a1 = Math.Atan2(csP.iny, csP.inx);
                double a2 = Math.Atan2(csC.iny, csC.inx);
                double da = a2 - a1;
                while (da >  Math.PI) da -= 2 * Math.PI;
                while (da <= -Math.PI) da += 2 * Math.PI;

                if (isConcave)
                {
                    // Konkave Ecke: Fächer von Einwärts-Normal P bis Einwärts-Normal C.
                    // Pro Winkel: Probe-Test ob die Richtung ins Glyphen-Innere zeigt.
                    // Dadurch werden falsch orientierte Winkel (z.B. wenn da-Vorzeichen
                    // durch Floating-Point kippt) still gefiltert, ohne da umzuklappen
                    // (Umklappen würde riesige Fächer erzeugen und die Reihenfolge brechen).
                    int ns = Math.Max(2, (int)Math.Ceiling(Math.Abs(da) / cornerStepRad));
                    for (int s = 0; s <= ns; s++)
                    {
                        double a = a1 + da * s / ns;
                        var c = TryPt(evx, evy, Math.Cos(a), Math.Sin(a), minRmm: 0);
                        if (c != null) localResult.Add(c with { IsFan = true });
                    }
                }
                else
                {
                    // Konvexe (oder falsch klassifizierte) Ecke.
                    // segBounds + dot-product > 0.990 filtern glatte Bezier-Übergänge
                    // bereits zuverlässig → kein zusätzlicher da-Schwellwert nötig.
                    // Ohne Schwellwert werden auch konkave Ecken gefangen, deren
                    // isConcave-Flag durch Pfadorientierung falsch gesetzt ist
                    // (z. B. Innenecke 'r' mit CCW-orientierter Kontur).
                    if (Math.Abs(da) < 0.08) continue;   // nur rein numerisches Rauschen (< 5°) überspringen

                    // Fächer a1 → a2
                    int ns = Math.Max(2, (int)Math.Ceiling(Math.Abs(da) / cornerStepRad));
                    for (int s = 0; s <= ns; s++)
                    {
                        double a = a1 + da * s / ns;
                        var c = TryPt(evx, evy, Math.Cos(a), Math.Sin(a), minRmm: 0);
                        if (c != null) localResult.Add(c with { IsFan = true });
                    }
                }
            }

            // Fenster-Greedy: tauscht nur wenn Kandidat ≥ 2× näher liegt
            // (echter Schleifenfall). Konturlinie-Reihenfolge bleibt für
            // marginal-nähere Kreise unverändert.
            if (localResult.Count >= 3)
            {
                static double distSq(VCarveCircle a, VCarveCircle b)
                { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
                const int W = 100;
                for (int i = 0; i < localResult.Count - 1; i++)
                {
                    // Fächer-Kreise nie verschieben und nicht überspringen
                    if (localResult[i].IsFan || localResult[i + 1].IsFan) continue;
                    double d1  = distSq(localResult[i], localResult[i + 1]);
                    double thr = d1 * 0.25; // Schwelle: √0.25 = 0.5× → mind. 2× näher
                    int best = i + 1;
                    int end = Math.Min(localResult.Count, i + 1 + W);
                    for (int j = i + 2; j < end; j++)
                    {
                        if (localResult[j].IsFan) break; // Fächer-Block nicht überspringen
                        double d = distSq(localResult[i], localResult[j]);
                        if (d < thr) { thr = d; best = j; }
                    }
                    if (best != i + 1)
                    {
                        var tmp = localResult[best];
                        for (int k = best; k > i + 1; k--)
                            localResult[k] = localResult[k - 1];
                        localResult[i + 1] = tmp;
                    }
                }
            }
            resultsPerFig[fi] = localResult;
        });

        // Ergebnisse in Figur-Reihenfolge zusammenführen (FigIdx-Sortierung bleibt erhalten)
        foreach (var r in resultsPerFig)
            if (r != null) result.AddRange(r);

        return result;
    }

    // ── V-Carve G-Code ───────────────────────────────────────────────────
    // Kreise sind bereits in Kontur-Reihenfolge (nach FigIdx + Bogenposition
    // sortiert). Innerhalb einer Figur wird KEIN Rückzug erzeugt – der Fräser
    // durchfährt Innenecken kontinuierlich mit variabler Tiefe.

    /// <summary>
    /// Dedupliziert Kreise (Mittelpunkte näher als <paramref name="dedupMm"/>) und tastet
    /// den Medialachsen-Pfad pro Figur mit gleichmässigem Abstand <paramref name="spacingMm"/>
    /// neu ab. X, Y und R werden linear interpoliert → glatte Z-Tiefenübergänge im G-Code.
    /// </summary>
    internal static List<VCarveCircle> ResampleVCarveCircles(
        List<VCarveCircle> raw, double dedupMm = 0.05, double spacingMm = 0.2, double simplifyMm = 1.0)
    {
        if (raw.Count == 0) return raw;
        double dedupSq = dedupMm * dedupMm;
        var result = new List<VCarveCircle>(raw.Count);

        int i = 0;
        while (i < raw.Count)
        {
            int fi = raw[i].FigIdx;
            int j  = i;
            while (j < raw.Count && raw[j].FigIdx == fi) j++;

            // 1. Konsekutives Dedup (original): entfernt unmittelbar aufeinanderfolgende Duplikate.
            //    Globales Dedup wurde verworfen – es entfernte Kreuzungskreise die für
            //    die Verknüpfung von Medialachsen-Ästen gebraucht werden.
            var dp = new List<VCarveCircle> { raw[i] };
            for (int k = i + 1; k < j; k++)
            {
                var prev = dp[^1];
                double ddx = raw[k].X - prev.X, ddy = raw[k].Y - prev.Y;
                if (ddx * ddx + ddy * ddy >= dedupSq)
                    dp.Add(raw[k]);
            }

            if (dp.Count == 1) { result.Add(dp[0] with { FigIdx = fi * 10000 }); i = j; continue; }

            // 1b. Ast-Segmentierung: Lücken > splitGap trennen verschiedene Medialachsen-Äste.
            //     Jedes Segment wird separat resamplet (verhindert lineare Interpolation über Äste).
            //     Schwellwert gross genug um Eckkreis-Sequenzen (< 1mm) nicht zu trennen.
            double splitGapSq = 3.0 * 3.0;
            var segs2   = new List<List<VCarveCircle>>();
            var curSeg2 = new List<VCarveCircle> { dp[0] };
            for (int k = 1; k < dp.Count; k++)
            {
                double ddx = dp[k].X - dp[k - 1].X, ddy = dp[k].Y - dp[k - 1].Y;
                if (ddx * ddx + ddy * ddy > splitGapSq) { segs2.Add(curSeg2); curSeg2 = []; }
                curSeg2.Add(dp[k]);
            }
            segs2.Add(curSeg2);

            int subFi = fi * 10000;
            foreach (var dpSeg in segs2)
            {
                int segFi = subFi++;
                if (dpSeg.Count == 1) { result.Add(dpSeg[0] with { FigIdx = segFi }); continue; }

                // 2. Kumulative Bogenlänge
                var cum = new double[dpSeg.Count];
                for (int k = 1; k < dpSeg.Count; k++)
                {
                    double dx = dpSeg[k].X - dpSeg[k - 1].X, dy = dpSeg[k].Y - dpSeg[k - 1].Y;
                    cum[k] = cum[k - 1] + Math.Sqrt(dx * dx + dy * dy);
                }
                double total = cum[^1];
                if (total < 1e-9) { result.Add(dpSeg[0] with { FigIdx = segFi }); continue; }

                // 3. Gleichmässige Neuabtastung (deaktiviert – nur berechnete Kreise verwenden)
                var figRes = dpSeg.Select(c => c with { FigIdx = segFi }).ToList();

                // 4. (Rückwärtsschritte werden bereits bei der Erzeugung in ComputeVCarveCircles
                // gefiltert — kein Post-Processing nötig.)

                result.AddRange(figRes);
            }
            i = j;
        }
        return result;
    }

    // ── V-Carve: G01/G00-Markierung (Erstellungsreihenfolge beibehalten) ────────────────────
    //
    // Die Kreise kommen aus ResampleVCarveCircles bereits in Bogenreihenfolge.
    // Diese Funktion vergibt nur neue FigIdx-Werte (→ G00-Rückzug im G-Code),
    // wenn die Lücke zwischen zwei aufeinanderfolgenden Kreisen > connectMm ist.
    // Kein Umsortieren – die Erstellungsreihenfolge wird 1:1 beibehalten.
    internal static List<VCarveCircle> RouteVCarveSegments(
        List<VCarveCircle> flat, double connectMm = 0.5)
    {
        if (flat.Count < 2) return flat;

        double cSq    = connectMm * connectMm;
        var    result = new List<VCarveCircle>(flat.Count);
        int    newFi  = 0;

        result.Add(flat[0] with { FigIdx = newFi });
        for (int i = 1; i < flat.Count; i++)
        {
            var    prev = flat[i - 1];
            var    curr = flat[i];
            // Verschiedene Original-Glyphen (FigIdx-Quotienten aus ResampleVCarveCircles)
            // → immer Abhub (G00), niemals G01-Verbindung egal wie nah die Kreise sind.
            bool diffGlyph = (prev.FigIdx / 10000) != (curr.FigIdx / 10000);
            double dx = curr.X - prev.X, dy = curr.Y - prev.Y;
            if (diffGlyph || dx * dx + dy * dy > cSq)
                newFi++;
            result.Add(curr with { FigIdx = newFi });
        }
        return result;
    }

    public static string VCarve(GraviereParams p, double workW, double workH)
    {
        double step    = p.SampleStepMm > 0 ? p.SampleStepMm : Math.Clamp(p.FontSizeMm / 300.0, 0.02, 0.1);
        var circles = RouteVCarveSegments(
                          ResampleVCarveCircles(
                              ComputeVCarveCircles(p, workW, workH, step),
                              spacingMm:  step,
                              simplifyMm: Math.Max(0.1, p.VereinfachungMm)),
                          connectMm: 3.0);   // ≥ splitGap → Eckkreise via G01 verbunden, kein G00-Abhub
        if (circles.Count == 0) return string.Empty;

        double halfRad = p.SchneidenWinkel * 0.5 * Math.PI / 180.0;
        double tanHalf = Math.Tan(halfRad);
        int    zFeed   = Math.Max(1, (int)(p.Vorschub * 0.3));

        var sb = new StringBuilder();
        sb.AppendLine("(V-Carve Gravur)");
        sb.AppendLine($"(Text: {p.Text.Replace('\n', ' ')})");
        sb.AppendLine($"(Schrift: {p.FontFamily}, Höhe: {p.FontSizeMm:F2} mm)");
        sb.AppendLine($"(Stichel: Winkel={p.SchneidenWinkel}°, D={p.FraeserD:F2} mm)");
        sb.AppendLine($"(Max. Tiefe: {p.ZTiefe:F3} mm)");
        sb.AppendLine();
        sb.AppendLine(Sz());

        int    prevFi = -1;
        double lastX  = 0, lastY = 0;
        foreach (var c in circles)
        {
            double z = -(c.R / tanHalf);
            if (z < -p.ZTiefe) z = -p.ZTiefe;

            if (c.FigIdx != prevFi)
            {
                // Neue Figur: Eckpunkt auf Z=0 fahren, dann Rückzug + Eilgang
                if (prevFi >= 0)
                {
                    sb.AppendLine($"G01 X{F(lastX)} Y{F(lastY)} Z0 F{zFeed}");
                    sb.AppendLine(Sz());
                }
                sb.AppendLine();
                sb.AppendLine($"G00 X{F(c.X)} Y{F(c.Y)}");
                sb.AppendLine($"G01 Z{F(z)} F{zFeed}");
                prevFi = c.FigIdx;
            }
            else
            {
                // Gleiche Figur: kontinuierlicher Schnitt, variable Tiefe, KEIN Rückzug
                sb.AppendLine($"G01 X{F(c.X)} Y{F(c.Y)} Z{F(z)} F{(int)p.Vorschub}");
            }
            lastX = c.X;
            lastY = c.Y;
        }

        sb.AppendLine();
        sb.AppendLine($"G01 X{F(lastX)} Y{F(lastY)} Z0 F{zFeed}");
        sb.AppendLine(Sz());
        return sb.ToString();
    }
}
