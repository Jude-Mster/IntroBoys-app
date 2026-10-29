using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace IntroBoys.App;

/// <summary>Small helpers that build the app's screens in code (dark guild look, red accent), so there are no layout files to keep in step.</summary>
static class Ui
{
    public static readonly Color Ink = Color.ParseColor("#0E0E10");
    public static readonly Color Card = Color.ParseColor("#17171A");
    public static readonly Color Line = Color.ParseColor("#2C2C31");
    public static readonly Color Field = Color.ParseColor("#232328");
    public static readonly Color Red = Color.ParseColor("#D61F26");
    public static readonly Color Green = Color.ParseColor("#5EE08F");
    public static readonly Color Gold = Color.ParseColor("#FFD23F");
    public static readonly Color Muted = Color.ParseColor("#A7A7AD");
    public static readonly Color Soft = Color.ParseColor("#D9D9DE");
    public static readonly Color White = Color.White;

    public static int Dp(Context c, float dp) =>
        (int)Math.Round(TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, c.Resources!.DisplayMetrics));

    public static GradientDrawable Round(Context c, Color fill, float radiusDp, Color? stroke = null, float strokeDp = 1)
    {
        var d = new GradientDrawable();
        d.SetColor(fill);
        d.SetCornerRadius(Dp(c, radiusDp));
        if (stroke is Color s) d.SetStroke(Math.Max(1, Dp(c, strokeDp)), s);
        return d;
    }

    public static GradientDrawable Circle(Color fill, Color? stroke = null, int strokePx = 0)
    {
        var d = new GradientDrawable();
        d.SetShape(ShapeType.Oval);
        d.SetColor(fill);
        if (stroke is Color s && strokePx > 0) d.SetStroke(strokePx, s);
        return d;
    }

    public static TextView Text(Context c, string text, float sp = 14, Color? color = null, bool bold = false)
    {
        var t = new TextView(c) { Text = text };
        t.SetTextSize(ComplexUnitType.Sp, sp);
        t.SetTextColor(color ?? White);
        if (bold) t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        return t;
    }

    public static TextView Title(Context c, string text, float sp = 16)
    {
        var t = Text(c, text, sp, White, true);
        t.LetterSpacing = 0.04f;
        return t;
    }

    /// <summary>A button: filled red when <paramref name="primary"/>, otherwise outlined.</summary>
    public static Button Button(Context c, string text, bool primary = false, Action? onClick = null, float sp = 14)
    {
        var b = new Button(c) { Text = text, Background = primary ? Round(c, Red, 10) : Round(c, Color.Transparent, 10, Color.ParseColor("#3A3A40")) };
        b.SetAllCaps(false);
        b.SetTextColor(White);
        b.SetTextSize(ComplexUnitType.Sp, sp);
        b.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        b.StateListAnimator = null;
        b.SetMinHeight(0); b.SetMinimumHeight(0);
        b.SetMinWidth(0); b.SetMinimumWidth(0);
        int p = Dp(c, 10);
        b.SetPadding(p, Dp(c, 8), p, Dp(c, 8));
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static LinearLayout Column(Context c, int gapDp = 10)
    {
        var l = new LinearLayout(c) { Orientation = Orientation.Vertical };
        l.ShowDividers = ShowDividers.Middle;
        l.SetDividerDrawable(Gap(c, gapDp, true));
        return l;
    }

    public static LinearLayout Row(Context c, int gapDp = 8)
    {
        var l = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        l.SetGravity(GravityFlags.CenterVertical);
        l.ShowDividers = ShowDividers.Middle;
        l.SetDividerDrawable(Gap(c, gapDp, false));
        return l;
    }

    static Drawable Gap(Context c, int dp, bool vertical)
    {
        var d = new GradientDrawable();
        d.SetColor(Color.Transparent);
        if (vertical) d.SetSize(1, Dp(c, dp)); else d.SetSize(Dp(c, dp), 1);
        return d;
    }

    /// <summary>A rounded card holding a column.</summary>
    public static LinearLayout CardBox(Context c, int gapDp = 10)
    {
        var l = Column(c, gapDp);
        l.Background = Round(c, Card, 14, Line);
        int p = Dp(c, 14);
        l.SetPadding(p, p, p, p);
        return l;
    }

    public static LinearLayout.LayoutParams Wide(int heightPx = ViewGroup.LayoutParams.WrapContent) =>
        new(ViewGroup.LayoutParams.MatchParent, heightPx);

    public static LinearLayout.LayoutParams Weight(float w = 1, int heightPx = ViewGroup.LayoutParams.WrapContent) =>
        new(0, heightPx, w);

    /// <summary>"label ........ value" on one line.</summary>
    public static (LinearLayout row, TextView value) Pair(Context c, string label, string value, Color? valueColor = null)
    {
        var row = Row(c, 8);
        var l = Text(c, label, 14, Soft);
        var v = Text(c, value, 14, valueColor ?? White, true);
        v.Gravity = GravityFlags.End;
        row.AddView(l, Weight());
        row.AddView(v, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return (row, v);
    }
}
