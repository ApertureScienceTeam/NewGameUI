using System;
using System.IO;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Plugin.Controls;

/// <summary>
/// GoldSrc 喷漆贴图（logos/*.bmp）读取：64x64、8 位调色板、无压缩，
/// 调色板索引 255 是透明色。Avalonia 自带的解码器不认识这个约定（会把 255 画成白块），
/// 所以这里自己解一遍，产出 Bgra8888 位图。
/// </summary>
public static class GoldSrcLogo
{
    /// <summary>GoldSrc 约定：调色板最后一个索引是透明色</summary>
    public const byte TransparentIndex = 255;

    /// <summary>按 bmp 自带调色板读（图案原样，不染色）</summary>
    public static Bitmap? Load(string path) => Decode(path, 0, 0, 0, false);

    /// <summary>
    /// 按喷漆颜色重映射后读取。logos/*.bmp 只是索引图（调色板是 0..255 的灰阶斜坡），
    /// 真正上墙的颜色来自引擎用 cl_logocolor 重建的“黑 → 基色”调色板（logos/remapped.bmp，
    /// 实测该斜坡严格线性：pal[i] ≈ 基色 * i / 255），所以这里按 索引/255 乘基色即可。
    /// </summary>
    public static Bitmap? LoadTinted(string path, byte r, byte g, byte b) => Decode(path, r, g, b, true);

    static Bitmap? Decode(string path, byte tintR, byte tintG, byte tintB, bool tinted)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 54 + 1024 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
                return null;

            var dataOffset = BitConverter.ToInt32(bytes, 10);
            var width = BitConverter.ToInt32(bytes, 18);
            var rawHeight = BitConverter.ToInt32(bytes, 22);
            var bpp = BitConverter.ToInt16(bytes, 28);
            var compression = BitConverter.ToInt32(bytes, 30);

            // GoldSrc 的喷漆只可能是 8 位无压缩；其它格式交给系统解码（一般用不到）
            if (bpp != 8 || compression != 0 || width <= 0 || rawHeight == 0)
                return null;

            var height = Math.Abs(rawHeight);
            var bottomUp = rawHeight > 0;
            var stride = (width + 3) / 4 * 4;
            if (dataOffset < 0 || dataOffset + stride * height > bytes.Length)
                return null;

            var palette = new int[256];
            for (var i = 0; i < 256; i++)
            {
                var p = 54 + i * 4;
                if (p + 3 >= bytes.Length)
                    return null;
                // 文件里是 B,G,R,A(reserved)
                var b = bytes[p];
                var g = bytes[p + 1];
                var r = bytes[p + 2];
                // 染色时用索引当亮度：黑色仍是黑的，越亮越接近基色，轮廓和阴影层次都保留
                palette[i] = i == TransparentIndex
                    ? 0
                    : tinted
                        ? (0xFF << 24) | ((tintR * i / 255) << 16) | ((tintG * i / 255) << 8) | (tintB * i / 255)
                        : (0xFF << 24) | (r << 16) | (g << 8) | b;
            }

            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var fb = bitmap.Lock())
            {
                var row = new byte[fb.RowBytes];
                for (var y = 0; y < height; y++)
                {
                    var srcRow = dataOffset + (bottomUp ? height - 1 - y : y) * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var argb = palette[bytes[srcRow + x]];
                        var o = x * 4;
                        row[o] = (byte)(argb & 0xFF);          // B
                        row[o + 1] = (byte)((argb >> 8) & 0xFF); // G
                        row[o + 2] = (byte)((argb >> 16) & 0xFF); // R
                        row[o + 3] = (byte)((argb >> 24) & 0xFF); // A
                    }

                    Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
                }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
