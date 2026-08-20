using System.Drawing;
using System.Drawing.Drawing2D;

namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// 托盘图标设计方案（极简画风，与 AI/token/监控相关）。
/// 每个方案为 16x16 画布上的绘制委托，以状态色为主色，保持与连接状态联动变色。
/// </summary>
public static class TrayIconDesigns
{
    public delegate void DrawIcon(Graphics g, Color color);

    /// <summary>方案 1：脉冲——圆角框 + 监测脉冲线（监控）。</summary>
    public static void Pulse(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawRectangle(pen, 1.5f, 1.5f, 13f, 13f);
        g.DrawLines(pen, new[]
        {
            new PointF(3f, 8.5f), new PointF(5.5f, 8.5f), new PointF(7f, 4.5f),
            new PointF(9f, 12f), new PointF(10.5f, 8.5f), new PointF(13f, 8.5f),
        });
    }

    /// <summary>方案 2：Token 币——圆形代币 + 中间竖线（token）。</summary>
    public static void TokenCoin(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, 2f, 2f, 12f, 12f);
        g.DrawLine(pen, 8f, 5f, 8f, 11f);
    }

    /// <summary>方案 3：脑波——半脑轮廓 + 神经元节点（AI）。</summary>
    public static void BrainWave(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(c);
        // 半脑轮廓（左侧圆弧 + 底部收口）
        g.DrawArc(pen, 2.5f, 2f, 11f, 11.5f, 90f, 180f);
        g.DrawLines(pen, new[] { new PointF(2.5f, 12.5f), new PointF(5f, 13.5f), new PointF(8f, 13.5f) });
        // 神经元节点 + 连线
        g.FillEllipse(brush, 5f, 5.5f, 2f, 2f);
        g.FillEllipse(brush, 8.5f, 8.5f, 2f, 2f);
        g.DrawLine(pen, 6.8f, 7.3f, 8.7f, 8.7f);
    }

    /// <summary>方案 4：仪表盘——半圆弧 + 指针（用量监控）。</summary>
    public static void Gauge(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(c);
        g.DrawArc(pen, 2f, 3.5f, 12f, 10f, 180f, 180f);
        // 指针（指向右上约 45°）
        g.DrawLine(pen, 8f, 11f, 11.5f, 6.5f);
        g.FillEllipse(brush, 6.8f, 9.8f, 2.4f, 2.4f);
    }

    /// <summary>方案 5：信号弧——中心点 + 双层信号弧（连接状态）。</summary>
    public static void SignalArcs(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(c);
        g.FillEllipse(brush, 3f, 10f, 3f, 3f);
        g.DrawArc(pen, 2f, 6f, 9f, 9f, -70f, 70f);
        g.DrawArc(pen, 0.5f, 2.5f, 14f, 14f, -70f, 70f);
    }

    /// <summary>方案表：编号 → (名称, 绘制委托)。</summary>
    public static readonly (string Name, DrawIcon Draw)[] All =
    {
        ("脉冲（监控）", Pulse),
        ("Token 币（代币）", TokenCoin),
        ("脑波（AI）", BrainWave),
        ("仪表盘（用量）", Gauge),
        ("信号弧（连接）", SignalArcs),
    };

    // ---- 融合方案（Token 币 × 脑波/AI） ----

    /// <summary>融合 A：脑波币——币形轮廓 + 内部脑波曲线。</summary>
    public static void FusionWaveCoin(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawEllipse(pen, 2f, 2f, 12f, 12f);
        g.DrawLines(pen, new[]
        {
            new PointF(3.8f, 8f), new PointF(5.6f, 6.2f), new PointF(7.2f, 9.6f),
            new PointF(8.8f, 6.4f), new PointF(10.4f, 9f), new PointF(12.2f, 8f),
        });
    }

    /// <summary>融合 B：神经币——币形轮廓 + 内部三节点神经网络。</summary>
    public static void FusionNeuralCoin(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(c);
        g.DrawEllipse(pen, 2f, 2f, 12f, 12f);
        // 神经连线（三角形网络）
        g.DrawLine(pen, 6.6f, 6.2f, 9.4f, 7.7f);
        g.DrawLine(pen, 9.4f, 8.5f, 6.6f, 10f);
        g.DrawLine(pen, 5.9f, 6.8f, 5.9f, 9.4f);
        // 神经元节点
        g.FillEllipse(brush, 5.1f, 5.3f, 1.9f, 1.9f);
        g.FillEllipse(brush, 9f, 7.2f, 1.9f, 1.9f);
        g.FillEllipse(brush, 5.1f, 9.1f, 1.9f, 1.9f);
    }

    /// <summary>融合 C：脑叶币——币形轮廓 + 中央分隔线 + 左右脑叶弧。</summary>
    public static void FusionBrainCoin(Graphics g, Color c)
    {
        using var pen = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, 2f, 2f, 12f, 12f);
        g.DrawLine(pen, 8f, 4.2f, 8f, 11.8f);                  // 中央分隔线
        g.DrawArc(pen, 4.5f, 4.5f, 3.5f, 7f, 90f, 180f);       // 左脑叶弧
        g.DrawArc(pen, 8f, 4.5f, 3.5f, 7f, 270f, 180f);        // 右脑叶弧
    }

    /// <summary>融合方案表。</summary>
    public static readonly (string Name, DrawIcon Draw)[] Fusions =
    {
        ("A 脑波币", FusionWaveCoin),
        ("B 神经币", FusionNeuralCoin),
        ("C 脑叶币", FusionBrainCoin),
    };
}
