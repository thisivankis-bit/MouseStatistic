using System.Drawing.Drawing2D;

namespace MouseClickTracker;

public sealed class HourChart : Panel
{
    private IReadOnlyList<HourBucket> _hours = Array.Empty<HourBucket>();
    private int _fromHour = 0;
    private int _toHour   = 23;

    private static readonly Color ActiveColor   = Color.FromArgb(22, 163, 74);
    private static readonly Color InactiveColor = Color.FromArgb(220, 224, 232);
    private static readonly Color EmptyColor    = Color.FromArgb(245, 246, 250);
    private static readonly Color TextColor     = Color.Gray;

    public HourChart()
    {
        DoubleBuffered = true;
        BackColor      = Color.White;
    }

    public void SetData(IReadOnlyList<HourBucket> hours, string workStart, string workEnd)
    {
        _hours = hours;

        int f = 0, t = 23;
        if (TimeOnly.TryParse(workStart, out var ws) && TimeOnly.TryParse(workEnd, out var we))
        {
            f = ws.Hour;
            t = Math.Max(f, we.Hour - 1);
            if (t > 23) t = 23;
        }
        _fromHour = f;
        _toHour   = t;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.HighQuality;

        var labelH       = 14;
        var topPad       = 4;
        var leftPad      = 6;
        var rightPad     = 6;
        var bottom       = Height - labelH;
        var barAreaH     = bottom - topPad;
        var hourCount    = _toHour - _fromHour + 1;
        if (hourCount <= 0) return;

        var gap   = 3;
        var avail = Width - leftPad - rightPad - gap * (hourCount - 1);
        var barW  = Math.Max(4, avail / hourCount);

        var byHour = new Dictionary<int, HourBucket>();
        foreach (var b in _hours) byHour[b.Hour] = b;

        using var hourFont   = new Font("Segoe UI", 7f);
        using var textBrush  = new SolidBrush(TextColor);
        using var activeBr   = new SolidBrush(ActiveColor);
        using var inactiveBr = new SolidBrush(InactiveColor);
        using var emptyBr    = new SolidBrush(EmptyColor);

        var nowHour = DateTime.Now.Hour;

        for (int i = 0; i < hourCount; i++)
        {
            var hour = _fromHour + i;
            byHour.TryGetValue(hour, out var bucket);
            var activeSec   = bucket?.ActiveSec   ?? 0;
            var inactiveSec = bucket?.InactiveSec ?? 0;
            var total       = Math.Min(3600, activeSec + inactiveSec);
            var activeH     = (int)Math.Round(barAreaH * Math.Min(1.0, activeSec / 3600.0));
            var totalH      = (int)Math.Round(barAreaH * Math.Min(1.0, total / 3600.0));

            var x = leftPad + i * (barW + gap);
            if (totalH == 0)
            {
                g.FillRectangle(emptyBr, x, bottom - 2, barW, 2);
            }
            else
            {
                var yTop = bottom - totalH;
                // inactive portion (top, gray)
                if (totalH > activeH)
                    g.FillRectangle(inactiveBr, x, yTop, barW, totalH - activeH);
                // active portion (bottom, green)
                if (activeH > 0)
                    g.FillRectangle(activeBr, x, bottom - activeH, barW, activeH);
            }

            using var fmt = new StringFormat { Alignment = StringAlignment.Center };
            var lbl = hour.ToString("D2");
            var color = hour == nowHour ? Color.FromArgb(40, 40, 40) : TextColor;
            using var br = new SolidBrush(color);
            g.DrawString(lbl, hourFont, br, new RectangleF(x, bottom + 1, barW, labelH), fmt);
        }
    }
}
