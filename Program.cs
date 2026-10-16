using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

/* ============================================================================
 *  CPU 调节器 —— 一个可以"指定 CPU 占用率"的小工具
 * ============================================================================
 *
 *  【这个程序干什么】
 *    你拖动滑块设定一个目标值（比如 50%），点"启动"后，程序会开几个后台线程
 *    故意空转烧 CPU，让系统总占用率尽量稳定在你设定的数值附近。
 *
 *  【代码结构（从上到下的顺序）】
 *    1. Program         —— 程序入口，只有几行
 *    2. Theme           —— 主题配色表（浅色/深色两套颜色写死在这里）
 *    3. DrawHelper      —— 画图小工具（画圆角矩形、阴影、文字等）
 *    4. CpuBurner       —— 真正烧 CPU 的引擎（多线程 + 精确定时）
 *    5. MainForm        —— 主窗口：界面绘制 + 鼠标交互 + 定时刷新
 *
 *  【给新手的两个关键概念】
 *    · 这个程序没有用 WinForms 的"按钮""滑块"等现成控件，
 *      而是【整个窗口当一张画布】，所有东西都是自己一笔一笔画上去的。
 *      这样做的原因是：现成控件太多时，换主题需要重绘十几次，会卡顿；
 *      自己画则每次重绘只需一遍，非常流畅。
 *      代价是：按钮点击、滑块拖动都要自己算坐标（见 HitTest 方法）。
 *
 *    · "毛玻璃"效果并不是真的模糊了背景，而是分四层画出来的：
 *      ① 模糊背衬 ② 半透明着色 ③ 顶部高光 ④ 边缘描边
 *      详见 PaintGlassPanel / FillGlassControl 两个方法的注释。
 * ========================================================================== */

namespace CpuLoadGui;

/// <summary>
/// 程序入口。Main 是 C# 程序启动后第一个被调用的方法。
/// </summary>
internal static class Program
{
    /// <summary>
    /// [STAThread] 是 WinForms 的硬性要求，表示用"单线程单元"模式运行，
    /// 涉及剪贴板、文件对话框等功能时必须加，忘了会报错。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // 让按钮等系统绘制的元素使用较新的视觉样式（虽然本程序基本都自绘）
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 高分屏适配：用 try-catch 包住是因为老系统上这个 API 可能不存在，
        // 万一失败也不该让程序崩溃，所以直接忽略异常。
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }

        // 创建主窗口并开始消息循环（程序会一直停在这一行，直到窗口关闭）
        Application.Run(new MainForm());
    }
}

// ============================================================================
//  Theme（主题配色表）
// ----------------------------------------------------------------------------
//  这个类就是一张"颜色清单"。想要改配色，只需要改下面 Light / Dark 里的色值。
//
//  颜色怎么写？
//    Color.FromArgb(红, 绿, 蓝)            —— 不透明的颜色，三个值都是 0~255
//    Color.FromArgb(透明度, 红, 绿, 蓝)     —— 带透明度的颜色
//                                            透明度 0 = 完全看不见，255 = 完全不透明
//    注意参数顺序：有 4 个参数时，第一个一定是透明度！
//
//  常见色值参考：
//    纯白 (255,255,255)   纯黑 (0,0,0)   纯红 (255,0,0)
// ============================================================================
internal sealed class Theme
{
    /// <summary>是否为深色主题。程序里有些地方需要根据深浅做不同处理。</summary>
    public bool IsDark;

    /// <summary>背景渐变的起点色和终点色（从左上到右下渐变）。</summary>
    public Color BgFrom, BgTo;

    /// <summary>卡片的主色和次级色（目前主要用于非玻璃的实色场景）。</summary>
    public Color Surface, Surface2;

    /// <summary>卡片投影的阴影颜色，通常用带透明度的深色。</summary>
    public Color Shadow;

    /// <summary>主要文字颜色（标题、大数字）。</summary>
    public Color Text;

    /// <summary>次要文字颜色（"目标 CPU 占用率"这类说明文字），比 Text 淡一些。</summary>
    public Color TextMuted;

    /// <summary>强调渐变的两端颜色（滑块已填充部分、进度条等都用它）。</summary>
    public Color Accent1, Accent2;

    /// <summary>表示"正常/运行中"的绿色，和表示"危险/停止"的红色。</summary>
    public Color Success, Danger;

    /// <summary>进度条/滑块"未填充部分"的轨道颜色。</summary>
    public Color TrackEmpty;

    /// <summary>卡片边框颜色。</summary>
    public Color Border;

    /// <summary>铺在壁纸上面的遮罩色。它能让壁纸变淡，从而突出前面的文字。</summary>
    public Color Overlay;

    /// <summary>
    /// 直接压在壁纸上的文字（标题）的描边光晕色。
    /// 透明度决定光晕强度，用来保证文字在花哨的壁纸上也看得清。
    /// </summary>
    public Color TextHalo;

    // ------------------------------------------------------------------------
    //  毛玻璃参数
    //  毛玻璃的观感由这四个参数共同决定，想调效果就改这里。
    // ------------------------------------------------------------------------

    /// <summary>
    /// 玻璃的着色（相当于给玻璃染上的颜色）。
    /// 【关键】它的透明度决定"通透度"：数字越小越透明，能看清后面的壁纸；
    /// 数字越大越浑浊，越接近实心色块。
    /// </summary>
    public Color GlassTint;

    /// <summary>顶部高光的强度 0~255。模拟光线打在玻璃表面，数字越大反光越明显。</summary>
    public int GlassHighlight;

    /// <summary>描边的亮部（画在卡片的上边和左边，模拟受光面）。</summary>
    public Color GlassEdgeLight;

    /// <summary>描边的暗部（画在卡片的下边和右边，模拟背光面，制造"厚度感"）。</summary>
    public Color GlassEdgeDark;

    /// <summary>
    /// 卡片相对于控件的玻璃强度倍数。
    /// 大于 1 表示卡片比按钮等控件更"实"一点，这样才有层次感，
    /// 否则玻璃叠玻璃会糊成一片。
    /// </summary>
    public double CardGlassStrength;

    // ---- 控件（按钮、进度条轨道等小元素）的毛玻璃参数 ----

    /// <summary>小控件的玻璃着色，透明度同样控制通透度。</summary>
    public Color ControlGlassTint;

    /// <summary>小控件顶部的高光强度。</summary>
    public int ControlHighlight;

    /// <summary>小控件的玻璃描边颜色。</summary>
    public Color ControlEdge;

    // ------------------------------------------------------------------------
    //  颜色插值（用于主题渐变切换动画）
    // ------------------------------------------------------------------------

    /// <summary>
    /// 在两个颜色之间按比例取中间色。
    ///
    /// t 是 0~1 的比例：
    ///   t=0   → 完全等于颜色 a
    ///   t=0.5 → 正好是 a 和 b 的中间色
    ///   t=1   → 完全等于颜色 b
    ///
    /// 每个分量（红、绿、蓝、透明度）各自独立地做线性插值。
    /// 例如红的插值公式是：a的红 + (b的红 - a的红) × t
    /// </summary>
    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);   // 防止传进来的比例超出范围

        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),   // 透明度
            (int)Math.Round(a.R + (b.R - a.R) * t),   // 红
            (int)Math.Round(a.G + (b.G - a.G) * t),   // 绿
            (int)Math.Round(a.B + (b.B - a.B) * t));  // 蓝
    }

    /// <summary>
    /// 生成"过渡中"的主题：把 from 和 to 两套主题的每个颜色都按比例混合。
    ///
    /// 主题切换动画就是靠这个实现的——动画期间每一帧都用不同的 t 调用它，
    /// 得到一连串逐渐变化的中间主题，看起来就是平滑渐变了。
    /// </summary>
    public static Theme Blend(Theme from, Theme to, double t)
    {
        return new Theme
        {
            // IsDark 不能插值（它只有 true/false 两种值），所以过半就切换。
            // 因为它只影响一些细节判断，中途切换不会被看出来。
            IsDark = t >= 0.5 ? to.IsDark : from.IsDark,

            BgFrom = Lerp(from.BgFrom, to.BgFrom, t),
            BgTo = Lerp(from.BgTo, to.BgTo, t),
            Surface = Lerp(from.Surface, to.Surface, t),
            Surface2 = Lerp(from.Surface2, to.Surface2, t),
            Shadow = Lerp(from.Shadow, to.Shadow, t),
            Text = Lerp(from.Text, to.Text, t),
            TextMuted = Lerp(from.TextMuted, to.TextMuted, t),
            Accent1 = Lerp(from.Accent1, to.Accent1, t),
            Accent2 = Lerp(from.Accent2, to.Accent2, t),
            Success = Lerp(from.Success, to.Success, t),
            Danger = Lerp(from.Danger, to.Danger, t),
            TrackEmpty = Lerp(from.TrackEmpty, to.TrackEmpty, t),
            Border = Lerp(from.Border, to.Border, t),
            Overlay = Lerp(from.Overlay, to.Overlay, t),
            TextHalo = Lerp(from.TextHalo, to.TextHalo, t),

            // 毛玻璃参数也要一起过渡，否则玻璃颜色会在动画中途突兀地跳变
            GlassTint = Lerp(from.GlassTint, to.GlassTint, t),
            GlassHighlight = (int)Math.Round(from.GlassHighlight + (to.GlassHighlight - from.GlassHighlight) * t),
            GlassEdgeLight = Lerp(from.GlassEdgeLight, to.GlassEdgeLight, t),
            GlassEdgeDark = Lerp(from.GlassEdgeDark, to.GlassEdgeDark, t),
            CardGlassStrength = from.CardGlassStrength + (to.CardGlassStrength - from.CardGlassStrength) * t,

            ControlGlassTint = Lerp(from.ControlGlassTint, to.ControlGlassTint, t),
            ControlHighlight = (int)Math.Round(from.ControlHighlight + (to.ControlHighlight - from.ControlHighlight) * t),
            ControlEdge = Lerp(from.ControlEdge, to.ControlEdge, t),
        };
    }

    /// <summary>
    /// 浅色主题的全部颜色。=> new() {...} 是 C# 的"表达式体属性"写法，
    /// 等价于写一个 get { return new Theme {...}; }，
    /// 意思是：每次访问 Theme.Light 都会新建一个对象。
    /// </summary>
    public static Theme Light => new()
    {
        IsDark = false,
        BgFrom = Color.FromArgb(234, 246, 252),   // #EAF6FC 淡天蓝
        BgTo = Color.FromArgb(226, 245, 242),     // #E2F5F2 淡青
        Surface = Color.White,
        Surface2 = Color.FromArgb(246, 252, 253),
        Shadow = Color.FromArgb(40, 13, 110, 168),   // 宝石蓝阴影：40 是透明度（很淡）
        Text = Color.FromArgb(12, 44, 66),           // #0C2C42 深水墨蓝
        TextMuted = Color.FromArgb(100, 121, 138),   // #64798A
        Accent1 = Color.FromArgb(13, 110, 168),      // #0D6EA8 宝石蓝
        Accent2 = Color.FromArgb(17, 168, 165),      // #11A8A5 青碧
        Success = Color.FromArgb(13, 158, 122),      // #0D9E7A
        Danger = Color.FromArgb(214, 69, 69),        // #D64545
        TrackEmpty = Color.FromArgb(216, 233, 240),  // #D8E9F0
        Border = Color.FromArgb(30, 13, 110, 168),   // 淡蓝描边
        // 浅色主题的壁纸遮罩：透明度越低，壁纸越清晰通透。
        // 原来是 (150, 240,250,252)，几乎是一层 59% 的白色，会把壁纸压成灰蒙蒙的一片。
        // 现在降到 50：壁纸的颜色和层次基本原样保留，几乎看不出蒙了一层东西。
        // 文字的可读性另外靠下面的 TextHalo 描边来保证，不再依赖这层遮罩。
        Overlay = Color.FromArgb(50, 236, 244, 250),

        // 浅色主题的文字是深色，所以用白色光晕把文字"托"起来
        TextHalo = Color.FromArgb(210, 255, 255, 255),

        // 整窗玻璃：通透但保留主题色，靠亮描边勾出边界
        GlassTint = Color.FromArgb(165, 244, 250, 255),
        GlassHighlight = 70,
        GlassEdgeLight = Color.FromArgb(210, 255, 255, 255),
        GlassEdgeDark = Color.FromArgb(60, 120, 160, 190),
        CardGlassStrength = 1.18,   // 卡片比控件略实，形成内层玻璃

        ControlGlassTint = Color.FromArgb(150, 252, 254, 255),
        ControlHighlight = 70,
        ControlEdge = Color.FromArgb(150, 255, 255, 255),
    };

    /// <summary>深色主题的全部颜色。</summary>
    public static Theme Dark => new()
    {
        IsDark = true,
        BgFrom = Color.FromArgb(7, 22, 34),          // #071622 深海蓝
        BgTo = Color.FromArgb(6, 38, 44),            // #06262C 深海青
        Surface = Color.FromArgb(14, 38, 52),        // #0E2634
        Surface2 = Color.FromArgb(20, 50, 66),
        Shadow = Color.FromArgb(90, 0, 0, 0),        // 纯黑阴影，透明度 90
        Text = Color.FromArgb(232, 244, 248),        // #E8F4F8
        TextMuted = Color.FromArgb(138, 165, 180),   // #8AA5B4
        Accent1 = Color.FromArgb(56, 189, 248),      // #38BDF8 亮天蓝
        Accent2 = Color.FromArgb(45, 212, 191),      // #2DD4BF 亮青
        Success = Color.FromArgb(52, 211, 153),      // #34D399
        Danger = Color.FromArgb(248, 113, 113),      // #F87171
        TrackEmpty = Color.FromArgb(31, 58, 74),     // #1F3A4A
        Border = Color.FromArgb(60, 125, 211, 252),  // 天蓝描边
        Overlay = Color.FromArgb(70, 7, 22, 34),

        // 深色主题的文字是浅色，所以用深色光晕（和浅色主题正好相反）
        TextHalo = Color.FromArgb(170, 0, 0, 0),

        // 深色玻璃：偏深蓝、通透度稍低，靠亮描边勾出玻璃边缘
        GlassTint = Color.FromArgb(150, 14, 36, 52),
        GlassHighlight = 34,        // 深色下高光要弱一些，太强会发灰
        GlassEdgeLight = Color.FromArgb(95, 190, 230, 255),
        GlassEdgeDark = Color.FromArgb(40, 0, 0, 0),
        CardGlassStrength = 1.18,

        ControlGlassTint = Color.FromArgb(140, 30, 60, 80),
        ControlHighlight = 40,
        ControlEdge = Color.FromArgb(75, 190, 230, 255),
    };
}

// ============================================================================
//  DrawHelper（画图小工具）
// ----------------------------------------------------------------------------
//  这里放的都是"画东西"的通用函数，本身不保存任何状态，随用随调。
// ============================================================================
internal static class DrawHelper
{
    /// <summary>
    /// 生成一个"圆角矩形"的路径。
    ///
    /// 【原理】GDI+ 没有现成的圆角矩形，需要用 4 段圆弧拼出来：
    ///   画矩形时，四个角各画一个 90 度的圆弧，再用直线连起来。
    ///
    /// AddArc 的参数是 (x, y, 宽, 高, 起始角度, 扫过角度)：
    ///   · 圆是"从右边开始、顺时针"计算角度的（3 点钟方向 = 0 度）
    ///   · 左上角从 180 度开始画 90 度，就画出了左上那一小段弧
    ///
    /// 返回值是 GraphicsPath，调用方用完后必须 Dispose（本程序用 using 自动处理）。
    /// </summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();

        // 防御性检查：圆角半径非法、或矩形本身没有面积时，退化成普通矩形，
        // 否则 AddArc 会抛异常。
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }

        // 圆弧的直径。乘 2 是因为 radius 是半径；
        // 再和矩形的宽/高取最小值，防止圆角比矩形本身还大。
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));

        path.AddArc(r.X, r.Y, d, d, 180, 90);                    // 左上角
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);            // 右上角
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);     // 右下角
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);            // 左下角
        path.CloseFigure();   // 首尾闭合，形成完整图形

        return path;
    }

    /// <summary>
    /// 用"多层叠加"的技巧模拟柔和阴影。
    ///
    /// 【为什么这么做】GDI+ 没有内置的模糊/阴影功能。
    /// 这里的办法是：从大到小画很多层圆角矩形，每层都比上一层小一点、
    /// 深一点，叠在一起从边缘看就形成了"由淡到浓"的渐变，很像真实阴影。
    ///
    /// 参数说明：
    ///   radius   —— 阴影跟随的圆角半径
    ///   spread   —— 阴影向外扩散的距离（越大越"蓬松"）
    ///   yOffset  —— 向下偏移几像素，模拟光从上方照下来
    ///   layers   —— 叠加层数（越多越平滑，但越慢）
    /// </summary>
    public static void DrawShadow(Graphics g, Rectangle r, int radius, Color shadow, int spread = 18, int yOffset = 5, int layers = 14)
    {
        // 从最外层（i 最大，范围最大、颜色最淡）往内层画
        for (int i = layers; i >= 1; i--)
        {
            float t = (float)i / layers;               // t 从 1 逐渐降到接近 0
            int grow = (int)Math.Round(spread * t);    // 越外层扩得越大
            int alpha = (int)(shadow.A * (1f - t) * 0.12f);   // 越外层越透明
            if (alpha <= 0) continue;                  // 完全透明就不必画了

            var rr = r;
            rr.Offset(0, yOffset);      // 整体下移，形成投影方向
            rr.Inflate(grow, grow);     // 向外扩张

            using var path = RoundedRect(rr, radius + grow);
            using var b = new SolidBrush(Color.FromArgb(alpha, shadow));
            g.FillPath(b, path);
        }
    }

    /// <summary>
    /// 画一个"渐变色填充"的圆角矩形。
    /// angle 是渐变方向：0 = 从左到右，90 = 从上到下，45 = 斜着。
    /// </summary>
    public static void FillRoundedGradient(Graphics g, Rectangle r, int radius, Color from, Color to, float angle)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedRect(r, radius);
        using var b = new LinearGradientBrush(r, from, to, angle);
        g.FillPath(b, path);
    }

    /// <summary>
    /// 在指定矩形内画一段文字。
    ///
    /// align 控制水平对齐：Near=靠左，Center=居中，Far=靠右
    /// line  控制垂直对齐，取值含义同上
    ///
    /// StringFormatFlags.NoWrap 表示"不要自动换行"，
    /// 因为这里的矩形常常比文字矮，不禁止换行的话文字会被挤到第二行看不见。
    /// </summary>
    public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment align, StringAlignment line)
    {
        using var sf = new StringFormat
        {
            Alignment = align,
            LineAlignment = line,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, rect, sf);
    }

    /// <summary>
    /// 画带"描边光晕"的文字，用于直接压在壁纸上的文字（比如标题）。
    ///
    /// 【为什么要这样做】
    /// 壁纸上的颜色有深有浅，浅色主题的深色文字如果正好落在壁纸的亮部，
    /// 就会糊在一起看不清（实测对比度只有 3.36:1）。
    ///
    /// 常见的两种解决办法：
    ///   ① 给整个壁纸压一层厚厚的遮罩 —— 能看清字，但壁纸也变得灰蒙蒙（就是原来的问题）
    ///   ② 只给文字本身描一圈淡边 —— 文字清楚，壁纸保持通透 ← 这里采用的做法
    ///
    /// 实现方式是先在四周偏移的位置用光晕色画几遍文字（就像加粗的描边），
    /// 最后再在正中间用正常颜色画一遍。
    /// </summary>
    public static void DrawTextWithHalo(Graphics g, string text, Font font, Color color, Color halo,
        RectangleF rect, StringAlignment align, StringAlignment line, float thickness = 1.4f)
    {
        using var sf = new StringFormat
        {
            Alignment = align,
            LineAlignment = line,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        // ① 先在 8 个方向偏移着画光晕，围成一圈
        using (var hb = new SolidBrush(halo))
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;   // 正中间留给正式文字
                    var r = rect;
                    r.Offset(dx * thickness, dy * thickness);
                    g.DrawString(text, font, hb, r, sf);
                }
            }
        }

        // ② 最后画正式文字，盖住上面的光晕
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, rect, sf);
    }
}

// ============================================================================
//  CpuBurner（CPU 负载发生器）—— 程序的核心算法在这里
// ----------------------------------------------------------------------------
//  【要解决的问题】
//    怎么让 CPU 占用率"正好"是 50%？
//
//  【思路：占空比（duty cycle）】
//    把时间切成一个个很短的周期（这里是 50 毫秒），每个周期里：
//      · 前一段时间拼命算数学（忙）      —— CPU 在这段时间是满负荷
//      · 后一段时间睡觉（闲）            —— CPU 在这段时间是空闲
//    如果"忙"占整个周期的 50%，CPU 平均占用率就接近 50%。
//
//    50ms 的周期足够短，Windows 任务管理器根本看不出这是一段段拼起来的，
//    显示出来就是平稳的一条线。
//
//  【为什么开多个线程】
//    一个线程最多只能跑满一个 CPU 核心。如果电脑有 8 个核，
//    单线程全速运行只能让总占用率变成 12.5%。
//    所以这里按照 CPU 核心数开同样多的线程。
//
//  【谁来告诉它该忙多少】
//    外部每隔 0.5 秒调用 SetRatio 更新一次"该忙多少"，
//    MainForm 会根据实际占用率和目标的差距不断微调这个值（类似恒温器的原理）。
// ============================================================================
internal sealed class CpuBurner : IDisposable
{
    /// <summary>要开几个线程，等于 CPU 核心数。</summary>
    private readonly int _threadCount;

    /// <summary>存放所有工作线程的数组。</summary>
    private Thread[]? _threads;

    /// <summary>
    /// 是否正在运行。加 volatile 是因为这个变量会被多个线程同时读写，
    /// volatile 保证某个线程改了之后，其他线程能立刻看到最新值
    /// （否则编译器可能优化成一直读旧值，导致线程停不下来）。
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// 每个周期里"忙"的比例，单位是千分之一。
    /// 500 表示 50%（千分比用整数是为了能用 Interlocked 做无锁读写）。
    /// </summary>
    private int _ratioThousandths;

    /// <summary>
    /// 用来存放计算结果，防止编译器把"没有用的计算"整个优化掉。
    /// 这个技巧叫 sink（水槽）。
    /// </summary>
    private double _sink;

    public CpuBurner(int n) => _threadCount = Math.Max(1, n);   // 至少 1 个线程

    /// <summary>启动所有工作线程。</summary>
    public void Start()
    {
        if (_running) return;    // 已经在跑了，避免重复启动
        _running = true;
        _threads = new Thread[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            // IsBackground = true 表示这是"后台线程"：
            // 主窗口关闭时，这些线程会被系统自动结束，不会阻止程序退出。
            _threads[i] = new Thread(WorkerLoop) { IsBackground = true };
            _threads[i].Start();
        }
    }

    /// <summary>停止所有工作线程并等待它们退出。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;    // 通知所有线程：别再循环了

        if (_threads != null)
        {
            // 等待所有工作线程退出。
            //
            // 【为什么用统一的截止时间】如果写成 foreach 里每个都 Join(2000)，
            // 那么 8 个核心最坏要等 8×2=16 秒，界面会明显卡住。
            // 所以这里算一个"最晚等到什么时候"，所有线程共享同一个期限，
            // 整体最多只等 2 秒。
            //
            // 【超时了会怎样】线程的循环里有 while (_running) 判断，
            // 所以即使没等到，它们也会在下一个周期（最长 50 毫秒）内自行退出，
            // 不会永久占用 CPU。
            var deadline = DateTime.UtcNow.AddSeconds(2);
            foreach (var t in _threads)
            {
                if (!t.IsAlive) continue;
                int remainMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainMs <= 0) break;
                t.Join(remainMs);
            }
            _threads = null;
        }
    }

    /// <summary>
    /// 设置"该忙多少"，单位千分之一（0~1000）。
    ///
    /// 用 Interlocked.Exchange 而不是直接赋值，是因为这个方法会被
    /// 界面线程调用，而 _ratioThousandths 会被工作线程读取，
    /// Interlocked 能保证这个读写过程不会被打断（线程安全）。
    /// </summary>
    public void SetRatio(int thousandths)
        => Interlocked.Exchange(ref _ratioThousandths, Math.Clamp(thousandths, 0, 1000));

    /// <summary>
    /// 工作线程的主循环：每个周期"忙一段时间 + 闲一段时间"。
    /// </summary>
    private void WorkerLoop()
    {
        const long periodUs = 50_000;   // 周期 = 50000 微秒 = 50 毫秒

        // Stopwatch.Frequency 是"每秒有多少个计时单位"，
        // 换算一下就知道 50ms 对应多少个计时单位。
        long periodTicks = periodUs * Stopwatch.Frequency / 1_000_000;

        var sw = Stopwatch.StartNew();
        long next = sw.ElapsedTicks + periodTicks;   // 下一个周期的开始时刻
        double x = 1.0;                              // 参与计算的变量

        while (_running)
        {
            // 读取当前设定的忙碌比例（Volatile.Read 保证读到最新值）
            int r = Volatile.Read(ref _ratioThousandths);
            long busyTicks = periodTicks * r / 1000;   // 这个周期该忙多久

            next += periodTicks;    // 预定下一个周期的开始时刻

            // ---- 阶段一：忙 ----
            // 做浮点乘加运算。写成 512 次一组的循环，比每次都判断时间要快。
            long deadline = sw.ElapsedTicks + busyTicks;
            while (sw.ElapsedTicks < deadline && _running)
                for (int i = 0; i < 512; i++) x = x * 1.0000001 + 1e-7;

            Volatile.Write(ref _sink, x);   // 把结果写出去，防止计算被优化掉

            // ---- 阶段二：闲 ----
            long now = sw.ElapsedTicks;
            if (next > now)
            {
                long sleepTicks = next - now;
                int ms = (int)(sleepTicks * 1000 / Stopwatch.Frequency);

                // 睡 (ms - 1) 毫秒，剩下不到 1 毫秒用空转补齐。
                // 因为 Thread.Sleep 的精度只有毫秒级，直接睡 ms 毫秒容易睡过头。
                if (ms > 1) Thread.Sleep(ms - 1);

                // 用极短的空转精确对齐到周期结束
                while (sw.ElapsedTicks < next && _running) { }
            }
            else
            {
                // 已经超时了（比如系统太卡），重新对齐，避免"追赶"式地狂跑
                next = sw.ElapsedTicks + periodTicks;
            }
        }
    }

    /// <summary>
    /// 实现 IDisposable 接口。using 语句块结束时会自动调用它，
    /// 这里顺便把线程也停掉，防止忘记调用 Stop。
    /// </summary>
    public void Dispose() => Stop();
}

// ============================================================================
//  MainForm（主窗口）—— 界面绘制 + 鼠标交互 + 定时刷新
// ----------------------------------------------------------------------------
//  这是整个程序最大的一块，建议按下面的顺序阅读：
//
//    ① 字段区          —— 记录了窗口的所有"状态"（当前主题、目标值、鼠标悬停在哪…）
//    ② 构造函数        —— 窗口创建时做的一次性设置
//    ③ 布局属性        —— 每个控件的坐标和大小（集中定义，改位置只改这里）
//    ④ 鼠标事件        —— 判断点到了哪个控件、拖动滑块、拖动窗口
//    ⑤ 定时刷新        —— 每 0.5 秒读一次真实 CPU 占用率，并调整烧机强度
//    ⑥ 绘制区 OnPaint  —— 把所有东西画出来
//    ⑦ 配置读写        —— 把主题、壁纸、目标值存到文件，下次打开还记得
// ============================================================================
internal sealed class MainForm : Form
{
    // ------------------------------------------------------------------------
    //  调用 Windows 系统功能（P/Invoke）
    //  C# 不能直接调用 Windows 的底层函数，需要用 DllImport 声明一下，
    //  告诉编译器"这个函数在哪个 dll 里、参数和返回值是什么类型"。
    // ------------------------------------------------------------------------

    /// <summary>
    /// 对应 Windows 的 FILETIME 结构体（一个 64 位的时间值，拆成高低两半存放）。
    /// StructLayout 表示内存布局要和系统一致，这样系统才能正确填充这个结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }

    /// <summary>
    /// 读取系统累计的 CPU 时间。这是计算 CPU 占用率的关键 API。
    /// 三个输出参数分别是：系统空闲了多久、内核态花了多久、用户态花了多久。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    /// <summary>释放鼠标捕获，配合下面的 SendMessage 用来拖动无边框窗口。</summary>
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    /// <summary>向窗口发送消息。这里用来模拟"按住标题栏拖动"。</summary>
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>WM_NCLBUTTONDOWN = 在非客户区按下左键；HTCAPTION = 标题栏区域。</summary>
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 0x2;

    /// <summary>
    /// 把两个 32 位半截拼回一个完整的 64 位数值。
    /// &lt;&lt; 32 表示左移 32 位（把高位挪到该在的位置），| 是按位或（合并）。
    /// </summary>
    private static ulong U(FILETIME f) => ((ulong)f.dwHighDateTime << 32) | f.dwLowDateTime;

    // ------------------------------------------------------------------------
    //  状态字段
    //  命名习惯：_ 开头的表示"私有字段"，是类的内部状态。
    // ------------------------------------------------------------------------

    /// <summary>当前使用的主题。初始为浅色，随后会被配置文件覆盖。</summary>
    private Theme _theme = Theme.Light;

    /// <summary>用户选择的壁纸图片（可能为 null，表示没设置壁纸）。</summary>
    private Image? _wallpaper;

    /// <summary>壁纸的文件路径，用于保存到配置文件。</summary>
    private string? _wallpaperPath;

    /// <summary>CPU 负载发生器。</summary>
    private readonly CpuBurner _burner;

    /// <summary>
    /// 定时器，每 500 毫秒触发一次 OnTick，用来自动刷新界面数据。
    /// </summary>
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };

    // ---- 主题渐变动画 ----

    /// <summary>
    /// 驱动主题切换动画的定时器。
    ///
    /// Interval = 10 表示每 10 毫秒触发一次，也就是理论上每秒 100 帧。
    /// 实际帧率会略低一些（系统定时器精度有限），但足以让 200ms 的动画看起来很顺。
    /// </summary>
    private readonly System.Windows.Forms.Timer _themeAnim = new() { Interval = 10 };

    /// <summary>用来测量动画已经播放了多久。</summary>
    private readonly Stopwatch _themeClock = new();

    /// <summary>动画的起始主题（null 表示当前没有动画在播放）。</summary>
    private Theme? _themeFrom;

    /// <summary>动画的目标主题。</summary>
    private Theme? _themeTo;

    /// <summary>
    /// 动画总时长（毫秒）。
    /// 想调快调慢就改这一个数字：越小越干脆，越大越舒缓。
    /// </summary>
    private const int ThemeAnimMs = 200;

    // ---- 计算 CPU 占用率用的"上一次的数值" ----
    // CPU 占用率是"两次采样之间的差值"算出来的，所以必须记住上一次的值。
    private ulong _pIdle, _pKernel, _pUser;

    /// <summary>是否已经采过第一次样。第一次采样只能用于记录基准，算不出占用率。</summary>
    private bool _primed;

    /// <summary>本程序当前贡献的 CPU 占用率（0~100），由 OnTick 不断调整。</summary>
    private double _contributionPct;

    /// <summary>是否正在烧 CPU（对应按钮是"启动"还是"停止"）。</summary>
    private bool _running;

    /// <summary>用户设定的目标占用率（0~100），由滑块控制。</summary>
    private int _targetPct = 50;

    // ---- 用于显示的平滑数值 ----
    /// <summary>最近一次测得的系统总占用率。</summary>
    private double _usagePct;

    /// <summary>
    /// 界面上实际显示的"本程序贡献"数值。
    /// 它缓慢追赶 _contributionPct，这样数字变化时看起来是平滑滚动而不是乱跳。
    /// </summary>
    private double _shownContribution;

    // ---- 鼠标交互状态 ----

    /// <summary>
    /// 每个可点击元素的编号。因为不用现成控件，
    /// 需要给每个热点起个 ID，命中测试时返回对应编号。
    /// </summary>
    private const int ID_THEME = 1, ID_WALL = 2, ID_CLOSE = 3, ID_TOGGLE = 4, ID_SLIDER = 5;

    /// <summary>鼠标当前悬停在哪个元素上（0 表示没有）。用来画 hover 高亮。</summary>
    private int _hotId;

    /// <summary>鼠标按下时按在哪个元素上。用来判断"按下和松开是否在同一个元素"（才算点击）。</summary>
    private int _activeId;

    /// <summary>是否正在拖动滑块。</summary>
    private bool _draggingSlider;

    /// <summary>鼠标是否悬停在滑块区域内（用于让滑块圆点变大、发光）。</summary>
    private bool _hoverSliderThumb;

    // ---- 窗口尺寸 ----
    /// <summary>窗口宽度（像素）。</summary>
    private const int WinW = 500;

    /// <summary>
    /// 窗口高度（像素）。
    /// 比卡片底部（70 + 330 = 400）多留了 42 像素，
    /// 其中 22 像素给卡片阴影自然扩散，剩下的用来放卡片下方那行署名文字。
    /// </summary>
    private const int WinH = 442;

    /// <summary>窗口圆角的半径（像素）。</summary>
    private const int WinRadius = 22;

    // ---- 卡片布局常量 ----
    // 把坐标写成常量集中管理，想调整布局只需要改这一行，
    // 不用在上百行绘制代码里到处找数字。
    private const int CardX = 20, CardY = 70, CardPad = 14, CardRadius = 16, ShadowSpread = 14;

    /// <summary>
    /// 卡片高度。写成固定值而不是"窗口高度减去若干"，是为了让窗口变高时
    /// 卡片保持原尺寸，多出来的空间留给下方的署名文字。
    /// </summary>
    private const int CardH = 330;

    /// <summary>卡片的外框（含阴影留白区域）。=> 这种写法叫"表达式体属性"，等价于只读的计算属性。</summary>
    private static Rectangle CardRect => new(CardX, CardY, WinW - 40, CardH);

    /// <summary>
    /// 卡片真正可见的内框（把四周的 CardPad 留白让出来给阴影）。
    /// 所有卡片内的元素坐标都相对于它来计算。
    /// </summary>
    private static Rectangle CardInner
    {
        get
        {
            var c = CardRect;
            return new Rectangle(c.X + CardPad, c.Y + CardPad, c.Width - CardPad * 2 - 1, c.Height - CardPad * 2 - 1);
        }
    }

    /// <summary>右上角圆形按钮的直径和间距。</summary>
    private const int BtnSize = 34, BtnGap = 8;

    // ---- 字体 ----
    // 字体对象比较耗资源，创建一次反复使用（不要放在 OnPaint 里反复 new）。
    /// <summary>标题"CPU 调节器"用的字体。</summary>
    private readonly Font _fontTitle = new("Microsoft YaHei UI", 14F, FontStyle.Bold);

    /// <summary>"目标 CPU 占用率"等说明文字。</summary>
    private readonly Font _fontCaption = new("Microsoft YaHei UI", 10F);

    /// <summary>更小一号的说明文字（进度条左侧的标签）。</summary>
    private readonly Font _fontCaptionSm = new("Microsoft YaHei UI", 9.5F);

    /// <summary>中间那个很大的百分比数字。</summary>
    private readonly Font _fontBig = new("Microsoft YaHei UI", 34F, FontStyle.Bold);

    /// <summary>进度条右侧的数值文字。</summary>
    private readonly Font _fontValue = new("Microsoft YaHei UI", 10F, FontStyle.Bold);

    /// <summary>显示 emoji 图标（🌓 🖼）需要用专门的 Emoji 字体，否则会显示成方块。</summary>
    private readonly Font _fontEmoji = new("Segoe UI Emoji", 12F);

    /// <summary>"启动/停止"按钮上的文字。</summary>
    private readonly Font _fontToggle = new("Microsoft YaHei UI", 11.5F, FontStyle.Bold);

    /// <summary>关闭按钮的 ✕ 符号。</summary>
    private readonly Font _fontGlyph = new("Segoe UI", 11F, FontStyle.Bold);

    /// <summary>卡片下方署名文字用的字体。比正文字体小一号，不抢主体内容的注意力。</summary>
    private readonly Font _fontSignature = new("Microsoft YaHei UI", 8.5F);

    /// <summary>
    /// 构造函数：创建窗口时执行一次。
    /// 这里做的是"初始化"工作——设置窗口外观、启动定时器。
    /// </summary>
    public MainForm()
    {
        // 按 CPU 核心数创建烧机引擎
        _burner = new CpuBurner(Environment.ProcessorCount);

        // 读取上次保存的配置（主题、壁纸、目标值）
        LoadConfig();

        // None = 没有系统自带的标题栏和边框。
        // 本程序的所有外观（包括右上角的关闭按钮）都是自己画的。
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(WinW, WinH);

        // 开启双缓冲：先在内存里画好整幅图，再一次性贴到屏幕上。
        // 不加这句会看到明显的闪烁（因为能看见"正在一笔一笔画"的过程）。
        DoubleBuffered = true;

        // 这几个 ControlStyles 是自绘窗口的标准配置：
        //   AllPaintingInWmPaint —— 所有绘制都在 OnPaint 里做，不额外擦背景
        //   OptimizedDoubleBuffer —— 优化过的双缓冲
        //   UserPaint            —— 由我们自己负责绘制，系统别插手
        //   ResizeRedraw         —— 窗口大小改变时重绘
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Font = new Font("Microsoft YaHei UI", 9F);

        // 让窗口能接收到键盘事件（虽然目前没用到，保留以便后续扩展）
        KeyPreview = true;

        // 把窗口裁成圆角
        UpdateWindowRegion();
        Resize += (s, e) => UpdateWindowRegion();

        // 每 500 毫秒刷新一次数据
        _tick.Tick += OnTick;
        _tick.Start();

        // 主题切换动画的定时器（先不启动，等用户点了切换按钮才开始）
        _themeAnim.Tick += OnThemeAnimTick;
    }

    /// <summary>
    /// 把矩形的窗口裁剪成圆角。
    ///
    /// 【原理】Windows 的窗口本质永远是矩形。想要圆角，就得给窗口设置一个
    /// "Region（区域）"，告诉系统"只有这个范围内的像素才属于我的窗口"，
    /// 区域之外的角落就会被裁掉、露出后面的桌面。
    ///
    /// 半径做了保护：不能超过窗口短边的一半，否则图形会扭曲报错。
    /// </summary>
    private void UpdateWindowRegion()
    {
        int radius = Math.Min(WinRadius, Math.Min(Width, Height) / 2);
        if (radius <= 0) return;

        using var path = DrawHelper.RoundedRect(new Rectangle(0, 0, Width, Height), radius);

        // 先取出旧的 Region，赋新值后再释放旧的。
        // Region 内部占用系统资源，不释放会造成内存泄漏。
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    // ------------------------------------------------------------------------
    //  布局：所有控件的坐标和大小
    //
    //  这些属性每次访问都会重新计算，好处是改一个常量就能让整体布局跟着变。
    //  Rectangle(x, y, 宽, 高) 中，x/y 是左上角坐标（原点在窗口左上角）。
    // ------------------------------------------------------------------------

    /// <summary>主题切换按钮（🌓），位于右上角三个按钮的最左边。</summary>
    private Rectangle ThemeBtnRect => new(WinW - 24 - BtnSize - (BtnSize + BtnGap) * 2, 20, BtnSize, BtnSize);

    /// <summary>选择壁纸按钮（🖼），中间那个。</summary>
    private Rectangle WallBtnRect => new(WinW - 24 - BtnSize - (BtnSize + BtnGap), 20, BtnSize, BtnSize);

    /// <summary>关闭按钮（✕），最右边那个。</summary>
    private Rectangle CloseBtnRect => new(WinW - 24 - BtnSize, 20, BtnSize, BtnSize);

    /// <summary>滑块的整体区域。高度给得比轨道大，是为了让鼠标更容易点中。</summary>
    private Rectangle SliderRect
    {
        get
        {
            var inner = CardInner;
            return new Rectangle(inner.X + 8, inner.Y + 110, inner.Width - 16, 36);
        }
    }

    /// <summary>"启动/停止"按钮，放在卡片左下角（用 inner.Bottom 从底部往上算）。</summary>
    private Rectangle ToggleBtnRect
    {
        get
        {
            var inner = CardInner;
            return new Rectangle(inner.X + 4, inner.Bottom - 56, 150, 46);
        }
    }

    /// <summary>"系统总占用"的进度条。</summary>
    private Rectangle BarSysRect
    {
        get
        {
            var inner = CardInner;
            return new Rectangle(inner.X + 100, inner.Y + 158, inner.Width - 176, 18);
        }
    }

    /// <summary>"本程序贡献"的进度条。</summary>
    private Rectangle BarConRect
    {
        get
        {
            var inner = CardInner;
            return new Rectangle(inner.X + 100, inner.Y + 192, inner.Width - 176, 18);
        }
    }

    // ------------------------------------------------------------------------
    //  命中测试：判断鼠标点在哪个控件上
    //
    //  因为不用现成控件，就没有"按钮自动知道被点了"这回事，
    //  只能自己判断坐标落在哪个矩形里。
    // ------------------------------------------------------------------------

    /// <summary>
    /// 传入鼠标坐标，返回对应控件的 ID；没点到任何控件就返回 0。
    ///
    /// 注意判断顺序：如果两个区域重叠，先判断的会胜出。
    /// 这里几个区域互不重叠，所以顺序无所谓。
    /// </summary>
    private int HitTest(Point p)
    {
        if (ThemeBtnRect.Contains(p)) return ID_THEME;
        if (WallBtnRect.Contains(p)) return ID_WALL;
        if (CloseBtnRect.Contains(p)) return ID_CLOSE;
        if (ToggleBtnRect.Contains(p)) return ID_TOGGLE;
        if (SliderRect.Contains(p)) return ID_SLIDER;
        return 0;   // 点在空白处
    }

    /// <summary>鼠标移动时触发：更新悬停高亮、光标形状，如果正在拖滑块就更新数值。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        // 正在拖动滑块时，优先处理拖动（忽略其他悬停逻辑）
        if (_draggingSlider)
        {
            UpdateSliderFromMouse(e.X);
            return;
        }

        int hit = HitTest(e.Location);

        // 悬停目标变了才重绘。这个判断很重要——
        // 鼠标每移动一像素就触发一次事件，不加判断会导致疯狂重绘。
        if (hit != _hotId)
        {
            _hotId = hit;
            Invalidate();   // 请求重绘，系统会在空闲时调用 OnPaint
        }

        // 滑块圆点的放大效果
        bool overThumb = SliderRect.Contains(e.Location);
        if (overThumb != _hoverSliderThumb) { _hoverSliderThumb = overThumb; Invalidate(); }

        // 悬停在可点元素上时，鼠标变成"小手"
        Cursor = (hit is ID_THEME or ID_WALL or ID_CLOSE or ID_TOGGLE or ID_SLIDER) ? Cursors.Hand : Cursors.Default;

        base.OnMouseMove(e);
    }

    /// <summary>鼠标离开窗口：清掉所有悬停状态。</summary>
    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hotId != 0) { _hotId = 0; Invalidate(); }
        if (_hoverSliderThumb) { _hoverSliderThumb = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    /// <summary>鼠标按下。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int hit = HitTest(e.Location);

            if (hit == 0)
            {
                // 点在空白处：当作"抓住窗口"来拖动。
                // 无边框窗口没有标题栏可拖，所以需要自己实现这个功能。
                StartWindowDrag();
                base.OnMouseDown(e);
                return;
            }

            // 记住按下时在哪个控件上。松开时只有还在同一个控件上才算"点击"，
            // 这符合一般软件的交互习惯（按下后移开再松开 = 取消）。
            _activeId = hit;

            // 点滑块就直接跳过去，不用先拖
            if (hit == ID_SLIDER) { _draggingSlider = true; UpdateSliderFromMouse(e.X); }

            Invalidate();
        }
        base.OnMouseDown(e);
    }

    /// <summary>鼠标松开：判断是否构成一次有效点击。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _activeId != 0)
        {
            int hit = HitTest(e.Location);
            int wasActive = _activeId;   // 先存下来，因为下面要清零
            _activeId = 0;
            _draggingSlider = false;

            // 按下和松开在同一个控件上，才算真正点击
            if (hit == wasActive) Activate(hit, e);

            Invalidate();
        }
        else if (e.Button == MouseButtons.Right && WallBtnRect.Contains(e.Location))
        {
            // 在壁纸按钮上点右键 = 清除壁纸（这是个隐藏功能）
            SetWallpaper(null, null);
        }
        base.OnMouseUp(e);
    }

    /// <summary>执行某个控件的点击动作。</summary>
    private void Activate(int id, MouseEventArgs e)
    {
        switch (id)
        {
            case ID_THEME:
                // 启动渐变过渡动画（而不是立刻切换）。
                //
                // 【注意】这里必须用 EffectiveTheme 而不是 _theme：
                // 动画播放期间 _theme 是插值出来的中间色，它的 IsDark 会在
                // 动画过半时翻转。如果基于 _theme 判断，快速连点时会出现
                // "点了偶数次却停在了深色"这种错乱。EffectiveTheme 始终返回
                // 用户真正想要的那个主题，连续点击的结果才是稳定可预期的。
                StartThemeTransition(EffectiveTheme.IsDark ? Theme.Light : Theme.Dark,
                                     wantDark: !EffectiveTheme.IsDark);
                SaveConfig();
                break;

            case ID_WALL:
                OnPickWallpaper();
                break;

            case ID_CLOSE:
                Close();   // 关闭窗口
                break;

            case ID_TOGGLE:
                ToggleRunning();
                break;
        }
    }

    // ---- 滑块的几何参数 ----
    /// <summary>轨道左右两端留出的空白，避免滑块拖到最边上时圆点被切掉一半。</summary>
    private const int SliderPad = 14;

    /// <summary>滑块圆点的半径。</summary>
    private const int ThumbR = 10;

    /// <summary>轨道实际可用的宽度（扣掉两端留白）。</summary>
    private int SliderTrackWidth => Math.Max(1, SliderRect.Width - SliderPad * 2);

    /// <summary>
    /// 根据鼠标的横坐标更新目标值。
    ///
    /// Math.Clamp(值, 最小, 最大) 用来把结果限制在合法范围内，
    /// 防止鼠标拖出轨道时算出负数或超过 100。
    /// </summary>
    private void UpdateSliderFromMouse(int x)
    {
        double pct = Math.Clamp((double)(x - SliderRect.X - SliderPad) / SliderTrackWidth, 0, 1);
        int v = (int)Math.Round(pct * 100);

        if (v != _targetPct)
        {
            _targetPct = v;
            SaveConfig();    // 立即保存，这样下次打开还记得
            Invalidate();
        }
    }

    /// <summary>
    /// 开始拖动窗口。
    ///
    /// 【原理】无边框窗口没有标题栏，所以用一个小技巧：
    /// 先释放鼠标捕获，然后给系统发一条"用户在标题栏上按下了左键"的假消息，
    /// 系统就会用自己那套逻辑来拖动窗口，效果和拖真的标题栏一模一样。
    /// </summary>
    private void StartWindowDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    // ------------------------------------------------------------------------
    //  定时刷新：每 500 毫秒执行一次
    // ------------------------------------------------------------------------

    /// <summary>
    /// 定时器回调。做两件事：
    ///   1. 读取当前真实的 CPU 占用率
    ///   2. 根据"目标"和"实际"的差距，调整烧机强度
    ///
    /// 【控制原理】这就是一个最简单的"负反馈"控制，和空调恒温器一个道理：
    ///   · 实际比目标低 → 加大烧机强度
    ///   · 实际比目标高 → 减小烧机强度
    ///   每次只调整差距的一部分（下面的 0.4），避免一下调太猛来回震荡。
    /// </summary>
    private void OnTick(object? s, EventArgs e)
    {
        double usage = ReadCpuUsage();
        _usagePct = usage;

        if (_running)
        {
            double err = _targetPct - usage;      // 差距：正数表示还不够，要加把劲
            _contributionPct = Math.Clamp(_contributionPct + err * 0.4, 0, 100);
            _burner.SetRatio((int)Math.Round(_contributionPct * 10));   // 乘以 10 转成千分比
        }

        // 显示值缓慢追赶真实值，这样数字是"滚动"变化的，不会生硬地跳。
        // 0.35 是追赶速度：越大跟得越快，越小越平滑但越迟钝。
        _shownContribution += (_contributionPct - _shownContribution) * 0.35;

        // 差距足够小就直接对齐，避免无限逼近导致数字一直在小数点后抖动
        if (Math.Abs(_contributionPct - _shownContribution) < 0.05) _shownContribution = _contributionPct;

        Invalidate();   // 请求重绘，把新数值显示出来
    }

    /// <summary>
    /// 读取系统总 CPU 占用率（0~100）。
    ///
    /// 【原理】Windows 不直接告诉你"现在占用率是多少"，
    /// 它只提供三个"自开机以来累计的时间"：空闲时间、内核态时间、用户态时间。
    ///
    /// 所以必须采两次样，用两次之间的差值来算：
    ///     本次总时间 = (内核增量 + 用户增量)
    ///     本次空闲   = 空闲增量
    ///     占用率 = (总时间 - 空闲) / 总时间 × 100%
    ///
    /// 这也解释了为什么第一次调用只能返回 0——还没有"上一次"可以比较。
    /// </summary>
    private double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        // 把高低两半拼成完整的 64 位数值
        ulong i = U(idle), k = U(kernel), u = U(user);

        // 第一次采样：只记录基准值，算不出占用率
        if (!_primed)
        {
            _pIdle = i; _pKernel = k; _pUser = u;
            _primed = true;
            return 0;
        }

        // 本次与上次的差值（增量）
        ulong di = i - _pIdle, dk = k - _pKernel, du = u - _pUser;

        // 把本次值存起来，供下次比较
        _pIdle = i; _pKernel = k; _pUser = u;

        // 注意：kernel 时间里已经包含了 idle 时间，所以总时间就是 kernel + user
        ulong total = dk + du;
        if (total == 0) return 0;   // 防止除以 0

        return Math.Clamp((total - di) * 100.0 / total, 0, 100);
    }

    // ------------------------------------------------------------------------
    //  主题
    // ------------------------------------------------------------------------

    /// <summary>
    /// 当前"最终想要"的主题。用于绘制相关的判断（保存配置请用 _wantDark）。
    /// </summary>
    private Theme EffectiveTheme => _themeTo ?? _theme;

    /// <summary>
    /// 用户真正想要的主题（浅色/深色），用于保存配置。
    ///
    /// 【为什么不能直接用 _theme】
    /// 动画播放期间 _theme 是插值出来的中间色；而动画刚结束的那一刻
    /// _themeTo 会被清成 null，_theme 也不一定正好等于目标主题。
    /// 如果此时保存配置（比如用户点了切换后马上关窗口），
    /// 就可能把中途的混合色当成结果存下来，下次打开颜色就不对了。
    ///
    /// 所以单独用一个布尔量记录"用户想要的"，它只在用户点击切换时改变，
    /// 完全不受动画过程影响。
    /// </summary>
    private bool _wantDark;

    /// <summary>
    /// 开始主题渐变过渡：从现在的外观平滑变化到目标主题。
    /// </summary>
    private void StartThemeTransition(Theme target, bool wantDark)
    {
        _wantDark = wantDark;   // 立刻记下用户的意图，供保存配置使用

        // 以"当前正在显示的主题"作为起点。
        // 这样如果动画还没播完就再点一次，会从当前中间色继续过渡，不会跳变。
        _themeFrom = _theme;
        _themeTo = target;

        _themeClock.Restart();                          // 计时归零，开始算动画进度
        if (!_themeAnim.Enabled) _themeAnim.Start();    // 启动动画定时器
    }

    /// <summary>
    /// 动画结束：定格到目标主题，并停掉定时器。
    /// </summary>
    private void EndThemeTransition()
    {
        _themeAnim.Stop();
        _theme = _themeTo ?? _theme;   // 保险起见再赋一次，确保精确等于目标色
        _themeFrom = _themeTo = null;  // 清空，表示"当前没有动画"
        Invalidate();
    }

    /// <summary>
    /// 动画的每一帧都会调用这里：算出当前应该显示成什么颜色，然后请求重绘。
    ///
    /// 【缓动（easing）是什么】
    /// 如果进度匀速前进（线性），动画看起来会很"机械"。
    /// 这里用的是 easeInOutCubic 曲线：开始慢 → 中间快 → 结束慢，
    /// 更接近现实中物体运动的节奏，观感自然得多。
    /// </summary>
    private void OnThemeAnimTick(object? s, EventArgs e)
    {
        // 已经播放的时间占总时长的比例（0 = 刚开始，1 = 结束）
        double t = _themeClock.Elapsed.TotalMilliseconds / ThemeAnimMs;

        if (t >= 1 || _themeFrom == null || _themeTo == null)
        {
            // 时间到，结束动画
            EndThemeTransition();
        }
        else
        {
            // 把线性的 t 转成带缓动的曲线值
            double eased = t < 0.5
                ? 4 * t * t * t                              // 前半段：加速
                : 1 - Math.Pow(-2 * t + 2, 3) / 2;           // 后半段：减速

            // 按缓动后的比例混合出这一帧的中间主题
            _theme = Theme.Blend(_themeFrom, _themeTo, eased);
        }

        // 请求重绘。整窗一次画完，所以动画很流畅。
        Invalidate();
    }

    // ========================================================================
    //  绘制区 —— 整个界面的外观都由下面这些方法画出来
    // ------------------------------------------------------------------------
    //  【绘制顺序很重要】后画的会盖住先画的，所以顺序是：
    //      先画背景（最底层）→ 再画标题栏 → 最后画卡片（最上层）
    //
    //  【Graphics 是什么】可以理解成"一支画笔 + 一张纸"，
    //  所有画图操作都通过它完成：g.FillRectangle 画填充矩形、
    //  g.DrawString 写字、g.DrawPath 画曲线……
    //
    //  【Invalidate 与 OnPaint 的关系】
    //  程序不会随时乱画，而是调用 Invalidate() 说一句"我这块脏了"，
    //  然后 Windows 会在合适的时机回调 OnPaint 来重画。
    //  这样做的好处是多次请求会被自动合并成一次绘制，避免浪费。
    // ========================================================================

    /// <summary>
    /// 窗口重绘时由系统自动调用。这是所有绘制工作的入口。
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // 抗锯齿：让圆角、斜线、圆圈边缘平滑，不会有难看的锯齿
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 文字的渲染方式，ClearType 在小字号下最清晰
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // 【性能注意事项】不要在这里设置 InterpolationMode.HighQualityBicubic
        // 或 PixelOffsetMode.HighQuality！它们会让位图绘制退化到很慢的重采样路径，
        // 实测能让每帧从 8ms 变成 30ms 以上。缩放已经在生成缓存时做完了。

        PaintBackground(g);   // ① 最底层：渐变背景 + 壁纸
        PaintTitleBar(g);     // ② 中间层：标题文字和右上角三个按钮
        PaintCard(g);         // ③ 最上层：卡片及其内部所有元素
        PaintSignature(g);    // ④ 卡片下方的署名（直接压在壁纸上）
    }

    /// <summary>
    /// 重写这个方法并留空，是为了阻止 WinForms 自动"擦除背景"。
    ///
    /// 【为什么】默认情况下，WinForms 每次重绘前会先用背景色把整块区域刷一遍。
    /// 但我们的背景本来就是自己画的不透明图形，先擦一遍纯属浪费时间，
    /// 而且会造成闪烁（先白一下再画上去）。
    /// 把它留空即表示"我自己会处理背景，你别管"。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    /// <summary>
    /// 清除各种缓存图片。壁纸变化时必须调用，否则会继续显示旧壁纸的缓存。
    /// （缓存里存的是"壁纸缩放后的结果"，不清理的话换了壁纸画面不会变。）
    /// </summary>
    private void InvalidateBgCache()
    {
        _backdropCache?.Dispose();
        _backdropCache = null;
        _backdropSource = null;
        _wallCache?.Dispose();
        _wallCache = null;
        _wallCacheSource = null;
    }

    /// <summary>
    /// 绘制一块毛玻璃面板（卡片用的就是它）。
    ///
    /// 【毛玻璃是怎么"假装"出来的】
    /// 真正的毛玻璃需要把背后的画面做高斯模糊，但逐像素模糊非常慢。
    /// 这里用四层叠加来模拟，效果很像而速度快得多：
    ///
    ///   ① 模糊背衬 —— 贴一张预先做好的"模糊版壁纸"
    ///   ② 半透明着色 —— 盖一层半透明的颜色，让玻璃有"材质感"
    ///   ③ 顶部高光 —— 上半部分加一道白色渐变，模拟光线反射
    ///   ④ 描边 —— 上/左画亮线、下/右画暗线，制造玻璃的"厚度"
    ///
    /// 【SetClip / Restore 的作用】
    /// 因为要画的是圆角矩形，直接填充矩形会画出直角。
    /// 所以先 SetClip(圆角路径) 把绘制范围限制在圆角内，
    /// 画完再用 Restore 恢复成正常范围（否则后面的东西也会被裁掉）。
    /// </summary>
    /// <param name="strength">
    /// 玻璃强度。1.0 = 标准玻璃；大于 1 表示着色更浓、更"实"。
    /// 卡片用 1.18 是为了比按钮略实一点，这样玻璃叠玻璃才有层次感。
    /// </param>
    private void PaintGlassPanel(Graphics g, Rectangle r, int radius, double strength = 1.0)
    {
        using var path = DrawHelper.RoundedRect(r, radius);

        // Save 保存当前绘制状态，配合后面的 Restore 使用
        var save = g.Save();
        g.SetClip(path);

        // ---- ① 模糊背衬 ----
        // 只截取面板所在的那一小块源图，而不是整张 500×420。
        // 这一点是性能关键：整窗贴图在多个控件上累计起来会明显变慢。
        var back = EnsureBlurredBackdrop();
        if (back != null)
        {
            var src = new Rectangle(r.X, r.Y, r.Width, r.Height);
            src.Intersect(new Rectangle(0, 0, back.Width, back.Height));   // 防止越界
            if (src.Width > 0 && src.Height > 0)
                g.DrawImage(back, r, src, GraphicsUnit.Pixel);
        }
        else
        {
            // 没设壁纸时，用主题渐变充当背衬，保证仍然有玻璃感
            using var bb = new LinearGradientBrush(r, _theme.BgFrom, _theme.BgTo, 135f);
            g.FillRectangle(bb, r);
        }

        // ---- ② 半透明主题着色 ----
        // 决定通透度的就是这里的透明度（前面 Theme 里配置的 GlassTint）。
        if (strength >= 1.0)
        {
            using var tint = new SolidBrush(_theme.GlassTint);
            g.FillRectangle(tint, r);
        }
        else
        {
            // 按强度比例调淡着色（让内层面板更透明）
            int a = (int)Math.Round(_theme.GlassTint.A * strength);
            using var tint = new SolidBrush(Color.FromArgb(a,
                _theme.GlassTint.R, _theme.GlassTint.G, _theme.GlassTint.B));
            g.FillRectangle(tint, r);
        }

        // ---- ③ 顶部高光 ----
        // 用"从白色渐变到全透明"来模拟反光。
        // 注意矩形的 Y 起点比面板高 1/4，这样渐变的最亮处落在面板顶部稍上，
        // 视觉上更接近真实的玻璃反光。
        int hlAlpha = (int)Math.Round(_theme.GlassHighlight * strength);
        if (hlAlpha > 0)
        {
            using var hl = new LinearGradientBrush(
                new Rectangle(r.X, r.Y - r.Height / 4, r.Width, r.Height),
                Color.FromArgb(hlAlpha, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255), 90f);   // 90 度 = 从上往下
            g.FillRectangle(hl, r);
        }

        // 恢复绘制范围，这样下一步的描边才能画到圆角外面一点点
        g.Restore(save);

        // ---- ④ 玻璃描边 ----
        // 上边偏亮、下边偏暗，形成玻璃的"厚度感"。
        // 这是让画面显得像玻璃而不是一块半透明色纸的关键一笔。
        Color edgeL = ScaleAlpha(_theme.GlassEdgeLight, strength);
        Color edgeD = ScaleAlpha(_theme.GlassEdgeDark, strength);
        using (var edge = new LinearGradientBrush(
            new Rectangle(r.X, r.Y, r.Width, r.Height), edgeL, edgeD, 90f))
        using (var pen = new Pen(edge, 1f))
        {
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// 按比例缩放颜色的透明度（k=1 不变，k=0.5 变成一半透明）。
    ///
    /// 【注意】必须用 Math.Clamp 把结果限制在 0~255。
    /// 因为调用处会传入大于 1 的强度（比如 CardGlassStrength = 1.18），
    /// 当原透明度较高时（210 × 1.18 = 248）尚可，但一旦超过 255，
    /// Color.FromArgb 会直接抛 ArgumentException。
    /// 用户按注释提示去调大描边透明度时就会踩到这个坑。
    /// </summary>
    private static Color ScaleAlpha(Color c, double k)
        => Color.FromArgb(Math.Clamp((int)Math.Round(c.A * k), 0, 255), c.R, c.G, c.B);

    /// <summary>
    /// 给小控件（按钮、进度条轨道、滑块轨道）填充毛玻璃效果。
    ///
    /// 原理和 PaintGlassPanel 一样，但更轻量：少了描边的渐变处理，
    /// 因为小控件面积小，简单一点反而更清晰。
    /// </summary>
    /// <param name="opacity">
    /// 整体不透明度倍数。鼠标悬停时传 1.25、按下时传 1.5，
    /// 就能做出"越靠近越亮"的反馈效果。
    /// </param>
    private void FillGlassControl(Graphics g, Rectangle r, int radius, double opacity = 1.0)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = DrawHelper.RoundedRect(r, radius);

        var save = g.Save();
        g.SetClip(path);

        // ① 模糊背衬：只截取控件所在的那一小块。
        // 【性能关键】如果每次都贴整张 500×420，8 个玻璃控件累计起来会明显拖慢绘制。
        var back = EnsureBlurredBackdrop();
        if (back != null)
        {
            var src = new Rectangle(r.X, r.Y, r.Width, r.Height);
            src.Intersect(new Rectangle(0, 0, back.Width, back.Height));
            if (src.Width > 0 && src.Height > 0)
                g.DrawImage(back, r, src, GraphicsUnit.Pixel);
        }
        else
        {
            var full = new Rectangle(0, 0, WinW, WinH);
            using var bb = new LinearGradientBrush(full, _theme.BgFrom, _theme.BgTo, 135f);
            g.FillRectangle(bb, full);
        }

        // ② 半透明着色（Math.Min(255, ...) 是防止悬停时 150×1.5 算出来超过颜色上限）
        int a = (int)Math.Round(_theme.ControlGlassTint.A * opacity);
        using (var tint = new SolidBrush(Color.FromArgb(Math.Min(255, a),
            _theme.ControlGlassTint.R, _theme.ControlGlassTint.G, _theme.ControlGlassTint.B)))
            g.FillRectangle(tint, r);

        // ③ 顶部高光（只占控件高度的上 1/3）
        int ha = (int)Math.Round(_theme.ControlHighlight * opacity);
        if (ha > 0 && r.Height > 3)
        {
            var hi = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, Math.Max(1, r.Height / 3));
            using var hl = new LinearGradientBrush(hi,
                Color.FromArgb(Math.Min(255, ha), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
            using var hp = DrawHelper.RoundedRect(hi, Math.Max(1, radius - 1));
            g.FillPath(hl, hp);
        }

        g.Restore(save);

        // ④ 玻璃描边
        using (var pen = new Pen(ScaleAlpha(_theme.ControlEdge, opacity), 1f))
            g.DrawPath(pen, path);
    }

    // ---- 缓存 ----
    // 缓存的意思是：把算一次很贵、但结果不变的东西存起来，下次直接用。
    // 这几个位图都是在"输入没变"的情况下复用，输入一变就置为 null 重新生成。

    /// <summary>整窗尺寸的模糊背衬，供所有玻璃控件共用。</summary>
    private Bitmap? _backdropCache;

    /// <summary>生成 _backdropCache 时用的壁纸。用来判断壁纸是否换了。</summary>
    private Image? _backdropSource;

    private Bitmap? EnsureBlurredBackdrop()
    {
        if (_backdropCache != null && ReferenceEquals(_backdropSource, _wallpaper)) return _backdropCache;
        _backdropCache?.Dispose();
        _backdropCache = null;
        if (_wallpaper == null) { _backdropSource = null; return null; }

        // 先缩到极小尺寸再放大回整窗，用插值得到柔和扩散（等效模糊）
        const int tinyW = 24, tinyH = 20;
        using var tiny = new Bitmap(tinyW, tinyH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var tg = Graphics.FromImage(tiny))
        {
            tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var src = _wallpaper;
            double scale = Math.Max((double)WinW / src.Width, (double)WinH / src.Height);
            int cw = Math.Max(1, (int)(WinW / scale)), ch = Math.Max(1, (int)(WinH / scale));
            int cx = (src.Width - cw) / 2, cy = (src.Height - ch) / 2;
            tg.DrawImage(src, new Rectangle(0, 0, tinyW, tinyH),
                new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
        }

        _backdropCache = new Bitmap(WinW, WinH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var bg = Graphics.FromImage(_backdropCache))
        {
            bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bg.DrawImage(tiny, new Rectangle(0, 0, WinW, WinH));
        }
        _backdropSource = _wallpaper;
        return _backdropCache;
    }

    /// <summary>
    /// 绘制背景：主题渐变 + 壁纸 + 半透明遮罩。
    ///
    /// 【注意】这里是整个程序唯一"不玻璃化"的部分，
    /// 壁纸保持原样（清晰不模糊），玻璃效果只加在前面的各种控件上。
    /// </summary>
    private void PaintBackground(Graphics g)
    {
        var rect = new Rectangle(0, 0, WinW, WinH);

        // 底层：从左上的 BgFrom 渐变到右下的 BgTo（135 度是斜向渐变）
        using (var brush = new LinearGradientBrush(rect, _theme.BgFrom, _theme.BgTo, 135f))
            g.FillRectangle(brush, rect);

        if (_wallpaper != null)
        {
            // 壁纸用"cover"方式铺满：等比缩放到刚好盖住整个窗口，
            // 多出来的部分溢出到窗口外（居中裁掉），不会变形。
            var blur = EnsureScaledWallpaper();
            if (blur != null)
            {
                int dx = (rect.Width - blur.Width) / 2;
                int dy = (rect.Height - blur.Height) / 2;
                g.DrawImageUnscaled(blur, dx, dy);
            }

            // 铺一层半透明遮罩。它的作用是让壁纸"退后"一些，
            // 这样前面的文字才看得清（否则花花绿绿的壁纸会让字很难读）。
            using var ob = new SolidBrush(_theme.Overlay);
            g.FillRectangle(ob, rect);
        }
    }

    /// <summary>壁纸缩放后的结果（按 cover 铺满窗口）。</summary>
    private Bitmap? _wallCache;

    /// <summary>生成 _wallCache 时用的壁纸，用于判断是否需要重新生成。</summary>
    private Image? _wallCacheSource;

    /// <summary>
    /// 取得"缩放好的壁纸"，必要时才重新缩放。
    ///
    /// 【为什么要缓存】把一张大图高质量缩放到窗口尺寸是相当慢的操作（可能几十毫秒）。
    /// 而窗口每次重绘都会调用这里，不缓存的话会卡得没法用。
    /// 因为缩放结果不会变，所以只算一次就够了。
    ///
    /// ReferenceEquals 用来判断"是不是同一张图片对象"，
    /// 换了壁纸就会生成新对象，这里自然就会检测到并重新缩放。
    /// </summary>
    private Bitmap? EnsureScaledWallpaper()
    {
        if (_wallCache != null && ReferenceEquals(_wallCacheSource, _wallpaper)) return _wallCache;

        _wallCache?.Dispose();
        _wallCache = null;
        if (_wallpaper == null) { _wallCacheSource = null; return null; }

        var img = _wallpaper;

        // Math.Max 保证长和宽都至少铺满窗口（等比缩放的关键）
        double scale = Math.Max((double)WinW / img.Width, (double)WinH / img.Height);

        // 【重要】限制缩放结果的总像素数，防止极端宽高比的图片把内存撑爆。
        // 例如一张 60000×4 的图片（文件可能只有 1KB），scale 会算出 105，
        // 结果要分配 6300000×420 的位图 ≈ 10GB，必然失败。
        // 这里把总像素限制在 4000×4000 以内，超了就相应缩小 scale。
        const long maxPixels = 4000L * 4000L;
        scale = Math.Min(scale, Math.Sqrt((double)maxPixels / ((double)img.Width * img.Height)));

        // 缩放后仍不能小于窗口，否则铺不满（会出现空隙）
        int w = Math.Max(WinW, (int)(img.Width * scale));
        int h = Math.Max(WinH, (int)(img.Height * scale));

        try
        {
            _wallCache = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var cg = Graphics.FromImage(_wallCache))
            {
                // 这里用高质量插值，因为只做一次，慢一点没关系，画质优先
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cg.DrawImage(img, new Rectangle(0, 0, w, h));
            }
        }
        catch
        {
            // 万一还是失败（内存不足等），就当作没有壁纸，退回纯渐变背景，
            // 总好过让绘制反复抛异常导致画面异常。
            _wallCache?.Dispose();
            _wallCache = null;
            _wallCacheSource = _wallpaper;
            return null;
        }
        _wallCacheSource = _wallpaper;
        return _wallCache;
    }

    /// <summary>绘制标题文字和右上角的三个圆形按钮。</summary>
    private void PaintTitleBar(Graphics g)
    {
        // 标题直接压在壁纸上，用带光晕的画法保证任何壁纸下都清晰可读
        DrawHelper.DrawTextWithHalo(g, "CPU 调节器", _fontTitle, _theme.Text, _theme.TextHalo,
            new RectangleF(24, 20, 240, 30), StringAlignment.Near, StringAlignment.Center);

        // 三个都是同一种"图标按钮"，只是内容和对应用途不同
        PaintGlyphButton(g, ThemeBtnRect, "🌓", ID_THEME);
        PaintGlyphButton(g, WallBtnRect, "🖼", ID_WALL);
        PaintGlyphButton(g, CloseBtnRect, "✕", ID_CLOSE);
    }

    /// <summary>绘制一个圆形图标按钮（右上角那三个）。</summary>
    private void PaintGlyphButton(Graphics g, Rectangle r, string glyph, int id)
    {
        bool hover = _hotId == id;                          // 鼠标是否悬停
        bool pressed = _activeId == id && hover;            // 是否正被按下

        // 悬停和按下时把玻璃调亮，给出视觉反馈。
        // 1.0 = 正常，1.25 = 悬停，1.5 = 按下
        double op = pressed ? 1.5 : (hover ? 1.25 : 1.0);
        FillGlassControl(g, r, r.Width / 2, op);   // 半径取一半 = 圆形

        // 关闭按钮用普通字体，其他两个是 emoji
        var font = glyph == "✕" ? _fontGlyph : _fontEmoji;
        DrawHelper.DrawText(g, glyph, font, _theme.Text, r, StringAlignment.Center, StringAlignment.Center);
    }

    /// <summary>卡片阴影的缓存位图。</summary>
    private Bitmap? _shadowCache;

    /// <summary>生成 _shadowCache 时用的阴影颜色。</summary>
    private Color _shadowCacheColor;

    /// <summary>
    /// 绘制卡片阴影（带缓存）。
    ///
    /// 【为什么要缓存】DrawShadow 需要构建 12 层圆角路径并逐个填充，
    /// 实测单次要 5~12 毫秒，是绘制过程中最慢的一步。
    /// 但阴影的几何形状和颜色都是固定的，所以可以画一次存成图片，
    /// 之后每次直接贴图（约 1 毫秒），快 10 倍左右。
    ///
    /// 【动画期间的注意点】主题切换时阴影颜色每帧都在变，
    /// 如果按颜色判断缓存是否有效，就会每帧重建一次（每帧多花 5~12ms），
    /// 动画会明显卡顿。所以动画期间这里锁定用"起始主题"的阴影色，
    /// 等动画结束再一次性换成目标色——阴影只是一层淡淡的投影，
    /// 中途保持不变完全看不出来。
    /// </summary>
    private void PaintCachedShadow(Graphics g, Rectangle inner)
    {
        // 动画播放中就用起始主题的阴影色，保证缓存全程有效
        Color want = _themeFrom?.Shadow ?? _theme.Shadow;

        // 缓存不存在、或阴影颜色变了，才重新生成
        if (_shadowCache == null || _shadowCacheColor != want)
        {
            _shadowCache?.Dispose();

            // 用 Argb 格式（带透明度），因为阴影本身就是半透明的
            _shadowCache = new Bitmap(WinW, WinH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var sg = Graphics.FromImage(_shadowCache);
            sg.SmoothingMode = SmoothingMode.AntiAlias;
            DrawHelper.DrawShadow(sg, inner, CardRadius, want, ShadowSpread, 4, 12);
            _shadowCacheColor = want;
        }
        g.DrawImageUnscaled(_shadowCache, 0, 0);
    }

    /// <summary>
    /// 绘制整张卡片及内部的全部元素（这是画面里内容最多的部分）。
    ///
    /// 【坐标是怎么算的】
    /// 所有元素的位置都写成 inner.X + 数字 或 inner.Y + 数字，
    /// inner 是卡片内部的可用区域。这样只要改 CardX/CardY/CardPad 几个常量，
    /// 整个卡片的布局就会跟着移动，不用逐个改坐标。
    /// </summary>
    private void PaintCard(Graphics g)
    {
        var card = CardRect;
        var inner = CardInner;

        // ① 阴影 + 玻璃面板（卡片本体）
        // 注意顺序：阴影必须先画，否则会盖住卡片内容。
        PaintCachedShadow(g, inner);
        PaintGlassPanel(g, inner, CardRadius, _theme.CardGlassStrength);

        // ② "目标 CPU 占用率" 说明文字
        DrawHelper.DrawText(g, "目标 CPU 占用率", _fontCaption, _theme.TextMuted,
            new RectangleF(inner.X + 4, inner.Y + 14, inner.Width - 8, 22), StringAlignment.Near, StringAlignment.Center);

        // ③ 中间的大数字（跟随滑块变化）
        DrawHelper.DrawText(g, _targetPct + "%", _fontBig, _theme.Text,
            new RectangleF(inner.X, inner.Y + 42, inner.Width, 62), StringAlignment.Center, StringAlignment.Center);

        // ④ 滑块
        PaintSlider(g);

        // ⑤ "系统总占用" 这一行：左边标签 + 中间进度条 + 右边数值
        DrawHelper.DrawText(g, "系统总占用", _fontCaptionSm, _theme.TextMuted,
            new RectangleF(inner.X + 4, inner.Y + 158, 92, 18), StringAlignment.Near, StringAlignment.Center);
        PaintProgressBar(g, BarSysRect, _usagePct, _theme.Accent1, _theme.Accent2);
        DrawHelper.DrawText(g, _usagePct.ToString("F1") + "%", _fontValue, _theme.Text,
            new RectangleF(inner.Right - 72, inner.Y + 156, 72, 22), StringAlignment.Far, StringAlignment.Center);

        // ⑥ "本程序贡献" 这一行
        DrawHelper.DrawText(g, "本程序贡献", _fontCaptionSm, _theme.TextMuted,
            new RectangleF(inner.X + 4, inner.Y + 192, 92, 18), StringAlignment.Near, StringAlignment.Center);

        // 在 Success 绿色基础上把三通道各加一点，得到一个更亮的绿作为渐变终点。
        // Math.Min(255, ...) 是必须的：颜色分量最大只能是 255，超了会抛异常。
        var conTo = Color.FromArgb(
            Math.Min(255, _theme.Success.R + 40),
            Math.Min(255, _theme.Success.G + 30),
            Math.Min(255, _theme.Success.B + 50));
        PaintProgressBar(g, BarConRect, _shownContribution, _theme.Success, conTo);
        DrawHelper.DrawText(g, _shownContribution.ToString("F1") + "%", _fontValue, _theme.Text,
            new RectangleF(inner.Right - 72, inner.Y + 190, 72, 22), StringAlignment.Far, StringAlignment.Center);

        // ⑦ 底部的启动/停止按钮和状态文字
        PaintToggle(g);
    }

    /// <summary>
    /// 绘制卡片下方居中显示的署名文字。
    ///
    /// 【位置怎么算的】
    ///   卡片底边 = CardY + CardH = 70 + 330 = 400
    ///   窗口高度 = 442，所以可用空白是 400~442（42 像素）
    ///
    ///   阴影会从卡片底边往下扩散几像素，窗口最底部又是圆角，
    ///   所以文字既要离开 400 一点、也不能贴到 442。
    ///   取 404~426 这个区间（高 22），上下各留出安全距离。
    ///
    /// 【为什么用 Text 而不是 TextMuted】
    /// 这里没有玻璃面板垫底，文字直接压在壁纸上。
    /// TextMuted（灰蓝色）在浅色壁纸上对比度不足，实测看起来发灰、很吃力；
    /// 改用主文字色 + 更亮的光晕，才能保证任何壁纸下都清晰。
    /// </summary>
    private void PaintSignature(Graphics g)
    {
        const int boxH = 22;
        float y = CardY + CardH + 4;   // = 404

        DrawHelper.DrawTextWithHalo(g, "本软件由 Miracles 编译", _fontSignature,
            _theme.Text, _theme.TextHalo,
            new RectangleF(0, y, WinW, boxH), StringAlignment.Center, StringAlignment.Center,
            thickness: 1.6f);   // 光晕加粗一点，小字号更容易读
    }

    /// <summary>
    /// 绘制滑块（拖动条）。
    ///
    /// 滑块由三部分组成：
    ///   ① 轨道（灰底，用毛玻璃）
    ///   ② 已填充部分（彩色渐变，长度反映当前数值）
    ///   ③ 圆形滑块头（跟着数值左右移动）
    /// </summary>
    private void PaintSlider(Graphics g)
    {
        var r = SliderRect;

        // 轨道高度 8 像素，垂直居中（r.Height/2 是中心，减去一半高度就是顶边）
        int trackH = 8;
        int trackY = r.Y + r.Height / 2 - trackH / 2;
        int trackW = SliderTrackWidth;
        var track = new Rectangle(r.X + SliderPad, trackY, trackW, trackH);

        // ① 轨道底：毛玻璃
        FillGlassControl(g, track, trackH / 2);

        // ② 已填充部分：宽度 = 轨道宽度 × 百分比
        double pct = Math.Clamp(_targetPct / 100.0, 0, 1);
        int fillW = (int)Math.Round(trackW * pct);
        if (fillW >= 2)   // 太窄就不画了，避免画出奇怪的小点
        {
            var fill = new Rectangle(track.X, track.Y, fillW, trackH);
            DrawHelper.FillRoundedGradient(g, fill, trackH / 2, _theme.Accent1, _theme.Accent2, 0f);
        }

        // ③ 圆形滑块头：圆心在已填充部分的右端
        int cx = track.X + fillW;
        int cy = trackY + trackH / 2;

        // 光晕：从大到小画多个半透明圆圈叠出来，和阴影是同一个技巧。
        // 鼠标悬停或拖动时范围更大、更亮。
        bool glowOn = _hoverSliderThumb || _draggingSlider;
        int glow = glowOn ? 20 : 15;
        for (int i = glow; i >= 6; i -= 3)
        {
            float a = (1f - (float)i / glow) * 0.30f;
            int alpha = (int)(a * 160);
            if (alpha <= 0) continue;
            using var b = new SolidBrush(Color.FromArgb(alpha, _theme.Accent1));
            g.FillEllipse(b, cx - i, cy - i, i * 2, i * 2);
        }

        // 滑块头本身也做成小玻璃球，鼠标悬停时半径 +1（微微放大的手感）
        int rad = ThumbR + (glowOn ? 1 : 0);
        var thumb = new Rectangle(cx - rad, cy - rad, rad * 2, rad * 2);
        FillGlassControl(g, thumb, rad, 1.1);
        using (var pen = new Pen(_theme.Accent1, 2.5f))
            g.DrawEllipse(pen, thumb);
    }

    /// <summary>
    /// 绘制一个进度条。
    /// </summary>
    /// <param name="pct">百分比 0~100。</param>
    /// <param name="from">填充渐变的起始色。</param>
    /// <param name="toExact">填充渐变的结束色。</param>
    private void PaintProgressBar(Graphics g, Rectangle r, double pct, Color from, Color toExact)
    {
        int h = 12;
        int y = r.Y + (r.Height - h) / 2;   // 垂直居中
        var track = new Rectangle(r.X, y, r.Width, h);

        // 轨道底：毛玻璃
        FillGlassControl(g, track, h / 2);

        double p = Math.Clamp(pct / 100.0, 0, 1);
        int fillW = (int)Math.Round((r.Width - 1) * p);

        // 太短的话圆角会画不出来（宽度小于高度），所以给个最小值
        if (fillW < h) fillW = h;

        // 数值接近 0 时干脆不画，否则会看到一个小色块
        if (p <= 0.001 || fillW <= 2) return;

        var fill = new Rectangle(r.X, y, fillW, h);
        DrawHelper.FillRoundedGradient(g, fill, h / 2, from, toExact, 0f);

        // 内嵌高光：在填充部分的上半部叠一道白色渐变，
        // 让进度条看起来有立体感（像一根圆管，而不是平面色块）
        var hi = new Rectangle(fill.X + 2, fill.Y + 1, fill.Width - 4, fill.Height / 2 - 1);
        if (hi.Width > 2 && hi.Height > 1)
        {
            using var path = DrawHelper.RoundedRect(hi, hi.Height / 2);
            using var b = new LinearGradientBrush(hi,
                Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
            g.FillPath(b, path);
        }
    }

    /// <summary>
    /// 绘制"启动/停止"按钮以及它右边的状态文字。
    ///
    /// 按钮有两种外观：
    ///   · 未运行 → 蓝绿渐变（主题色），文字"启动"
    ///   · 运行中 → 红色渐变，文字"停止"
    /// 这样一眼就能看出当前状态。
    /// </summary>
    private void PaintToggle(Graphics g)
    {
        var r = ToggleBtnRect;
        bool hover = _hotId == ID_TOGGLE;
        bool pressed = _activeId == ID_TOGGLE && hover;

        // ① 决定用什么颜色
        Color c1, c2;
        if (_running)
        {
            // 用主题里的 Danger 色，而不是写死的红色。
            // 这样以后改主题配色时，"停止"按钮的颜色会跟着一起变。
            // c1 比 c2 亮一点，形成渐变的立体感（ControlPaint.Light 是调亮工具）。
            c2 = _theme.Danger;
            c1 = ControlPaint.Light(c2, 0.18f);
        }
        else
        {
            c1 = _theme.Accent1;
            c2 = _theme.Accent2;
        }

        // 按下时变暗、悬停时变亮（ControlPaint 是 WinForms 提供的调色工具）
        if (pressed) { c1 = ControlPaint.Dark(c1, 0.15f); c2 = ControlPaint.Dark(c2, 0.15f); }
        else if (hover) { c1 = ControlPaint.Light(c1, 0.15f); c2 = ControlPaint.Light(c2, 0.15f); }

        // ② 先铺玻璃底
        FillGlassControl(g, r, 14);

        // ③ 再叠一层半透明（透明度 215）的强调色渐变。
        //    不用完全不透明是为了让玻璃质感透出来；
        //    但也要足够浓，才看得出这是"主要操作按钮"。
        using (var path = DrawHelper.RoundedRect(r, 14))
        using (var gb = new LinearGradientBrush(r,
            Color.FromArgb(215, c1), Color.FromArgb(215, c2), 45f))
            g.FillPath(gb, path);

        // ④ 顶部高光（玻璃反射）
        var hi = new Rectangle(r.X + 2, r.Y + 1, r.Width - 4, r.Height / 3);
        using (var path = DrawHelper.RoundedRect(hi, 7))
        using (var b = new LinearGradientBrush(hi, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            g.FillPath(b, path);

        // 玻璃描边
        using (var path = DrawHelper.RoundedRect(r, 14))
        using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
            g.DrawPath(pen, path);

        DrawHelper.DrawText(g, _running ? "停止" : "启动", _fontToggle, Color.White, r,
            StringAlignment.Center, StringAlignment.Center);

        // ⑤ 按钮右边的状态文字。运行中是绿色，停止时是灰色。
        DrawHelper.DrawText(g, _running ? "● 运行中" : "● 已停止",
            _fontCaption, _running ? _theme.Success : _theme.TextMuted,
            new RectangleF(r.Right + 20, r.Y + (r.Height - 20) / 2f, 160, 20),
            StringAlignment.Near, StringAlignment.Center);
    }

    // ------------------------------------------------------------------------
    //  启动 / 停止
    // ------------------------------------------------------------------------

    /// <summary>
    /// 切换运行状态（点击"启动/停止"按钮时调用）。
    ///
    /// 两个方向都把 _contributionPct 归零，是为了让每次启动都从"零负载"开始
    /// 慢慢爬升，而不是沿用上次的数值突然猛烧一下。
    /// </summary>
    private void ToggleRunning()
    {
        if (_running)
        {
            // ---- 停止 ----
            _running = false;
            _burner.Stop();            // 停掉所有烧机线程
            _contributionPct = 0;
            _burner.SetRatio(0);
        }
        else
        {
            // ---- 启动 ----
            _contributionPct = 0;
            _burner.SetRatio(0);       // 先设成 0，再从 0 开始往上加
            _burner.Start();
            _running = true;
        }
        Invalidate();
    }

    // ------------------------------------------------------------------------
    //  壁纸
    // ------------------------------------------------------------------------

    /// <summary>弹出文件选择框让用户挑一张壁纸。</summary>
    private void OnPickWallpaper()
    {
        // using 保证对话框用完就被释放（它占用的系统资源不小）
        using var dlg = new OpenFileDialog
        {
            Title = "选择壁纸",
            // Filter 的格式是"显示的文字|实际匹配的扩展名"，用 | 分隔不同组
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
        };

        // ShowDialog 会阻塞在这里，直到用户点确定或取消
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var img = LoadImageSafe(dlg.FileName);
        if (img == null)
        {
            MessageBox.Show(this, "无法加载该图片。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetWallpaper(img, dlg.FileName);
    }

    /// <summary>
    /// 设置壁纸（统一入口）。
    ///
    /// 【为什么要统一走这里】Image 对象持有非托管内存，必须手动释放。
    /// 换壁纸、清除壁纸、从配置读取这三条路径都要替换 _wallpaper，
    /// 如果每处都直接赋值，就容易漏掉释放旧图片，反复换图会持续泄漏内存。
    /// 集中到一个方法里就不会漏。
    /// </summary>
    /// <param name="img">新的壁纸图片；传 null 表示清除壁纸。</param>
    /// <param name="path">图片路径，用于保存到配置；清除时传 null。</param>
    private void SetWallpaper(Image? img, string? path)
    {
        // 释放旧图片。注意此时 _wallCache / _backdropCache 里还留着
        // 由它生成的缓存位图，所以必须先清缓存再释放，顺序不能反。
        InvalidateBgCache();
        _wallpaper?.Dispose();

        _wallpaper = img;
        _wallpaperPath = path;

        SaveConfig();
        Invalidate();
    }

    /// <summary>
    /// 安全地加载图片。
    ///
    /// 【为什么要先读进内存再转换】Image.FromStream 会"记住"这个文件流，
    /// 如果用完就关掉文件流，之后再画这张图会报错。
    /// 所以这里用 new Bitmap(img) 复制一份独立的数据，
    /// 之后就能放心关掉文件了。
    /// </summary>
    private static Image? LoadImageSafe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);   // 复制成独立对象
        }
        catch { return null; }   // 文件损坏、格式不支持等等，统一返回 null 由调用方处理
    }

    // ------------------------------------------------------------------------
    //  配置读写
    //  把设置存成一个简单的文本文件，格式是每行"键=值"。
    //  存放位置：C:\Users\你的用户名\AppData\Roaming\CpuLoadGui\config.ini
    // ------------------------------------------------------------------------

    /// <summary>配置文件的完整路径。</summary>
    private static string ConfigPath
    {
        get
        {
            // ApplicationData 就是上面说的 Roaming 目录，
            // 用它而不是程序所在目录，可以避免"装在 C 盘却没写入权限"的问题。
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CpuLoadGui");

            // 目录可能还不存在，先创建（已存在时不会报错）
            Directory.CreateDirectory(dir);

            return Path.Combine(dir, "config.ini");
        }
    }

    /// <summary>
    /// 读取配置文件。程序启动时调用一次。
    ///
    /// 整体包在 try-catch 里：配置文件损坏、没权限读等情况都不应该让程序打不开，
    /// 大不了就用默认设置。
    /// </summary>
    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;   // 第一次运行没有配置文件，正常

            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;   // 跳过空行和格式不对的行

                // [..i] 是"切片"语法，表示取从开头到第 i 个字符之前的部分。
                // Trim() 去掉首尾空格容错。
                string k = line[..i].Trim();       // 键，例如 "Theme"
                string v = line[(i + 1)..].Trim(); // 值，例如 "Dark"

                switch (k)
                {
                    case "Theme":
                        _theme = v == "Dark" ? Theme.Dark : Theme.Light;
                        break;

                    case "Wallpaper":
                        // 只有文件真的存在才加载（用户可能把图片删了或移动了）。
                        // 这里不调用 SetWallpaper，因为它会反过来写配置文件，
                        // 在读取过程中写文件容易出问题。直接赋值即可（启动时没有旧图片要释放）。
                        if (File.Exists(v)) { _wallpaperPath = v; _wallpaper = LoadImageSafe(v); }
                        break;

                    case "Target":
                        // TryParse 在解析失败时返回 false 而不抛异常，比 Parse 安全
                        if (int.TryParse(v, out var t)) _targetPct = Math.Clamp(t, 0, 100);
                        break;
                }
            }
        }
        catch { }   // 读配置失败不影响使用，静默忽略
    }

    /// <summary>保存配置。每次设置变化（切主题、换壁纸、拖滑块）时都会调用。</summary>
    private void SaveConfig()
    {
        try
        {
            var sb = new StringBuilder();
            // 用 _wantDark（用户点击时立刻记录的意图），而不是动画中的插值主题。
            // 否则"点了切换马上关窗口"会把动画中途的混合色当成结果存下来。
            sb.AppendLine("Theme=" + (_wantDark ? "Dark" : "Light"));
            // ?? 是"空合并运算符"：如果 _wallpaperPath 是 null 就用空字符串代替
            sb.AppendLine("Wallpaper=" + (_wallpaperPath ?? ""));
            sb.AppendLine("Target=" + _targetPct);
            File.WriteAllText(ConfigPath, sb.ToString());
        }
        catch { }   // 写不进去（比如磁盘满了）也不该让程序崩溃
    }

    /// <summary>
    /// 窗口即将关闭时调用。这里做"收尾工作"：
    /// 保存配置、停止定时器、释放所有占用的资源。
    ///
    /// 【为什么必须释放】Bitmap / Font / Image 这些对象在背后占用着 Windows 的
    /// 系统资源（GDI 句柄、非托管内存），不释放的话虽然程序退出后系统会回收，
    /// 但养成好习惯更安全，也能避免以后改成多窗口时出问题。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveConfig();
        _tick.Stop();           // 停掉定时器，避免关闭过程中还在刷新
        _themeAnim.Stop();      // 停掉主题动画定时器
        _burner.Dispose();      // 停掉所有烧机线程

        // 释放各种缓存位图
        _backdropCache?.Dispose();
        _wallCache?.Dispose();
        _shadowCache?.Dispose();

        // 释放壁纸原图（它可能是一张大图，占用几十 MB 非托管内存）
        _wallpaper?.Dispose();

        // 释放 8 个字体对象
        _fontTitle.Dispose();
        _fontCaption.Dispose();
        _fontCaptionSm.Dispose();
        _fontBig.Dispose();
        _fontValue.Dispose();
        _fontEmoji.Dispose();
        _fontToggle.Dispose();
        _fontGlyph.Dispose();
        _fontSignature.Dispose();

        base.OnFormClosing(e);
    }
}
