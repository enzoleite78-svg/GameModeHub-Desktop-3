using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    /// <summary>NOVO 4.2: prévia da tela para o celular, janela em foco e desempenho (CPU/RAM/GPU).</summary>
    public static class ScreenService
    {
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        /// <summary>Foto da área de trabalho inteira, reduzida para "largura" pixels, em JPEG.</summary>
        public static byte[] Capturar(int largura = 960, int qualidade = 55)
        {
            int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) return null;
            largura = Math.Clamp(largura, 320, 1920);
            int lw = Math.Min(largura, w), lh = Math.Max(1, (int)((long)h * lw / w));
            using var tela = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(tela)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            using var menor = new Bitmap(lw, lh, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(menor)) { g.InterpolationMode = InterpolationMode.Bilinear; g.DrawImage(tela, 0, 0, lw, lh); }
            using var ms = new MemoryStream();
            var p = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(qualidade, 20, 90)) } };
            menor.Save(ms, Jpeg, p);
            return ms.ToArray();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);

        /// <summary>Programa em primeiro plano agora (para o celular mostrar "Em foco: …").</summary>
        public static (string exe, string titulo) JanelaAtiva()
        {
            try
            {
                var h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return ("", "");
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                Native.GetWindowThreadProcessId(h, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                return (p.ProcessName, sb.ToString());
            }
            catch { return ("", ""); }
        }

        // ---- GPU: soma da utilização "3D" dos motores da placa (sem administrador) ----
        private static PerformanceCounter[] _gpu = Array.Empty<PerformanceCounter>();
        private static DateTime _gpuLista = DateTime.MinValue, _gpuLeitura = DateTime.MinValue;
        private static int _gpuUltimo = -1;
        private static readonly object _lock = new object();

        public static int GpuPct()
        {
            lock (_lock)
            {
                try
                {
                    if ((DateTime.UtcNow - _gpuLeitura).TotalMilliseconds < 900) return _gpuUltimo;
                    if ((DateTime.UtcNow - _gpuLista).TotalSeconds > 15)
                    {
                        foreach (var c in _gpu) c.Dispose();
                        var cat = new PerformanceCounterCategory("GPU Engine");
                        _gpu = cat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                                  .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)).ToArray();
                        foreach (var c in _gpu) { try { c.NextValue(); } catch { } }
                        _gpuLista = DateTime.UtcNow;
                        _gpuLeitura = DateTime.UtcNow;
                        return _gpuUltimo;
                    }
                    double soma = 0;
                    foreach (var c in _gpu) { try { soma += c.NextValue(); } catch { } }
                    _gpuLeitura = DateTime.UtcNow;
                    return _gpuUltimo = (int)Math.Clamp(Math.Round(soma), 0, 100);
                }
                catch { return -1; }
            }
        }
    }
}
