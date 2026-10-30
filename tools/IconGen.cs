// OpenDM 图标生成器：纯 System.Drawing 绘制，不依赖第三方库。
// 由 make-icon.ps1 调用；输出多尺寸 .ico（小尺寸用 DIB、大尺寸用 PNG 帧）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public class IcoFrame
{
    public int Size;
    public byte[] Data;
    public bool IsPng;
}

public static class OpenDMIcon
{
    static GraphicsPath Round(RectangleF b, float r)
    {
        GraphicsPath p = new GraphicsPath();
        float d = r * 2f;
        if (d <= 0f) { p.AddRectangle(b); return p; }
        if (d > b.Width) d = b.Width;
        if (d > b.Height) d = b.Height;
        p.AddArc(b.X, b.Y, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>在 S×S 画布上画出「渐变圆角方块 + 白色下箭头 + 落地托盘」。</summary>
    public static Bitmap Render(int S)
    {
        Bitmap bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            // 小尺寸（16-32）单独排一套更粗、更集中的版式，否则箭头和托盘会糊成一团
            bool small = S <= 32;
            float grid = small ? 16f : 256f;
            float k = S / grid;

            float pad = small ? 0.5f : 5f * k;
            RectangleF box = new RectangleF(pad, pad, S - pad * 2f, S - pad * 2f);
            float radius = Math.Min(box.Width, box.Height) * (small ? 0.24f : 0.235f);

            // ---- 背景渐变圆角方块 ----
            using (GraphicsPath bp = Round(box, radius))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    box, Color.FromArgb(255, 143, 190, 255), Color.FromArgb(255, 31, 76, 153), 55f))
                {
                    ColorBlend cb = new ColorBlend(3);
                    cb.Colors = new Color[]
                    {
                        Color.FromArgb(255, 147, 194, 255),
                        Color.FromArgb(255, 76, 141, 255),
                        Color.FromArgb(255, 28, 68, 140)
                    };
                    cb.Positions = new float[] { 0f, 0.46f, 1f };
                    lg.InterpolationColors = cb;
                    g.FillPath(lg, bp);
                }

                // 顶部柔光（上半部白色渐隐），让方块有玻璃质感
                RectangleF hi = new RectangleF(box.X, box.Y, box.Width, box.Height * 0.62f);
                using (GraphicsPath hp = Round(hi, radius))
                using (LinearGradientBrush hb = new LinearGradientBrush(
                    hi, Color.FromArgb(58, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    g.SetClip(bp);
                    g.FillPath(hb, hp);
                    g.ResetClip();
                }

                // 内描边，增强边缘
                using (Pen pen = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(0.6f, 1.6f * k)))
                {
                    g.DrawPath(pen, bp);
                }
            }

            // ---- 白色下箭头 ----
            float ax0 = (small ? 6.2f : 107f) * k, ax1 = (small ? 9.8f : 149f) * k;
            float ax2 = (small ? 12.6f : 185f) * k, ax3 = (small ? 3.4f : 71f) * k;
            float ay0 = (small ? 2.3f : 52f) * k, ay1 = (small ? 7.3f : 122f) * k;
            float ay2 = (small ? 12.1f : 188f) * k;

            PointF[] arrow = new PointF[]
            {
                new PointF(ax0, ay0), new PointF(ax1, ay0),
                new PointF(ax1, ay1), new PointF(ax2, ay1),
                new PointF((small ? 8f : 128f) * k, ay2),
                new PointF(ax3, ay1), new PointF(ax0, ay1)
            };

            using (GraphicsPath ap = new GraphicsPath())
            {
                ap.AddPolygon(arrow);
                RectangleF ab = new RectangleF(ax3, ay0, ax2 - ax3, ay2 - ay0);
                using (LinearGradientBrush ag = new LinearGradientBrush(
                    ab, Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 223, 235, 255), 90f))
                {
                    g.FillPath(ag, ap);
                }
            }

            // ---- 底部托盘 ----
            RectangleF tray = small
                ? new RectangleF(3.0f * k, 13.0f * k, 10.0f * k, 2.0f * k)
                : new RectangleF(70 * k, 202 * k, 116 * k, 23 * k);
            using (GraphicsPath tp = Round(tray, tray.Height / 2f))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.FillPath(tb, tp);
            }
        }
        return bmp;
    }

    static byte[] ToPng(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);              // biSize
            bw.Write(w);               // biWidth
            bw.Write(h * 2);           // biHeight = 图像 + 掩码
            bw.Write((short)1);        // biPlanes
            bw.Write((short)32);       // biBitCount
            bw.Write(0);               // biCompression = BI_RGB
            bw.Write(w * h * 4);       // biSizeImage
            bw.Write(0);               // X
            bw.Write(0);               // Y
            bw.Write(0);               // clrUsed
            bw.Write(0);               // clrImportant

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[w * 4];
                for (int y = h - 1; y >= 0; y--)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, w * 4);
                    bw.Write(row);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            byte[] zeros = new byte[maskStride];
            for (int y = 0; y < h; y++) bw.Write(zeros);
            bw.Flush();
            return ms.ToArray();
        }
    }

    public static void Build(string icoPath)
    {
        int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<IcoFrame> frames = new List<IcoFrame>();

        foreach (int s in sizes)
        {
            using (Bitmap bmp = Render(s))
            {
                IcoFrame f = new IcoFrame();
                f.Size = s;
                f.IsPng = s >= 128;               // 大尺寸用 PNG 帧，体积小且 Vista+ 原生支持
                f.Data = f.IsPng ? ToPng(bmp) : ToDib(bmp);
                frames.Add(f);
            }
        }

        string dir = Path.GetDirectoryName(Path.GetFullPath(icoPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (FileStream fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);          // reserved
            bw.Write((short)1);          // type = icon
            bw.Write((short)frames.Count);

            int offset = 6 + 16 * frames.Count;
            foreach (IcoFrame f in frames)
            {
                bw.Write((byte)(f.Size >= 256 ? 0 : f.Size));
                bw.Write((byte)(f.Size >= 256 ? 0 : f.Size));
                bw.Write((byte)0);       // 调色板
                bw.Write((byte)0);       // reserved
                bw.Write((short)1);      // planes
                bw.Write((short)32);     // bpp
                bw.Write(f.Data.Length);
                bw.Write(offset);
                offset += f.Data.Length;
            }
            foreach (IcoFrame f in frames) bw.Write(f.Data);
        }
    }

    /// <summary>顺便导出单张 PNG，供扩展 / 文档使用。</summary>
    public static void ExportPng(int size, string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (Bitmap bmp = Render(size))
        {
            bmp.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>各尺寸按最近邻放大拼成一张对比图，用来肉眼检查小尺寸是否可辨。</summary>
    public static void Preview(string path)
    {
        int[] sizes = new int[] { 16, 20, 24, 32, 48, 64, 128 };
        int cell = 128, gap = 14;
        int W = gap + sizes.Length * (cell + gap);
        int H = cell + gap * 2 + 22;

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (Bitmap canvas = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(255, 20, 24, 30));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;

            using (Font f = new Font("Segoe UI", 9f))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 150, 165, 180)))
            {
                for (int i = 0; i < sizes.Length; i++)
                {
                    int x = gap + i * (cell + gap);
                    using (Bitmap bmp = Render(sizes[i]))
                    {
                        int draw = Math.Min(cell, sizes[i] * (sizes[i] <= 32 ? 4 : 1));
                        int off = (cell - draw) / 2;
                        g.DrawImage(bmp, new Rectangle(x + off, gap + off, draw, draw));
                    }
                    string label = sizes[i] + " px";
                    SizeF sz = g.MeasureString(label, f);
                    g.DrawString(label, f, tb, x + (cell - sz.Width) / 2f, gap + cell + 6);
                }
            }

            canvas.Save(path, ImageFormat.Png);
        }
    }
}
