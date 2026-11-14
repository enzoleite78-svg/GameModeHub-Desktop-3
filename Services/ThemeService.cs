using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    public static class ThemeService
    {
        /// <summary>NOVO 4.1: temas (a v3 tinha temas; o remake só tinha o escuro).</summary>
        public static readonly (string id, string nome)[] Temas =
        {
            ("Dark", "Escuro"), ("OLED", "OLED"), ("DarkBlue", "Azul-noite"), ("Light", "Claro")
        };

        public static string TemaAtual { get; private set; } = "Dark";
        public static bool Escuro => TemaAtual != "Light";

        /// <summary>Aceita os nomes da v3 ("Dark Blue", "Light Purple"…) e devolve um dos temas atuais.</summary>
        public static string Normalizar(string tema)
        {
            tema ??= "";
            if (tema.StartsWith("Light", StringComparison.OrdinalIgnoreCase) || tema.Equals("Claro", StringComparison.OrdinalIgnoreCase)) return "Light";
            if (tema.Equals("OLED", StringComparison.OrdinalIgnoreCase)) return "OLED";
            if (tema.IndexOf("Blue", StringComparison.OrdinalIgnoreCase) >= 0 || tema.IndexOf("Azul", StringComparison.OrdinalIgnoreCase) >= 0) return "DarkBlue";
            return "Dark";
        }

        public static void AplicarTema(string tema)
        {
            TemaAtual = Normalizar(tema);
            string[] cores = TemaAtual switch
            {
                //            Bg         Surface    Surface2   Card         Line       Text       Muted      Sidebar      Toast        Danger
                "OLED" =>     new[] { "#000000", "#0B0C0E", "#17181C", "#E60B0C0E", "#22242A", "#F2F3F5", "#8A8F99", "#F2050506", "#F20B0C0E", "#FF5C5C" },
                "DarkBlue" => new[] { "#0B1020", "#121A2E", "#1A2440", "#E6121A2E", "#26304A", "#E6EAF5", "#8C97B2", "#E60A0F1D", "#F2121A2E", "#FF6B6B" },
                "Light" =>    new[] { "#F3F4F7", "#FFFFFF", "#E8EAEF", "#F2FFFFFF", "#D7DAE2", "#15171C", "#5F6675", "#F2FAFAFC", "#F7FFFFFF", "#D93636" },
                _ =>          new[] { "#0F1115", "#171A21", "#1F232C", "#E6171A21", "#2A2F3A", "#E8EAF0", "#8B93A3", "#E60C0E12", "#F0171A21", "#FF5C5C" },
            };
            string[] chaves = { "BgBrush", "SurfaceBrush", "Surface2Brush", "CardBrush", "LineBrush", "TextBrush", "MutedBrush", "SidebarBrush", "ToastBrush", "DangerBrush" };
            var res = Application.Current.Resources;
            for (int i = 0; i < chaves.Length; i++)
                res[chaves[i]] = Congelado(new SolidColorBrush((Color)ColorConverter.ConvertFromString(cores[i])));
            res["SelecaoBrush"] = Congelado(new SolidColorBrush(Escuro ? Colors.White : Color.FromRgb(0x15, 0x17, 0x1C)));
            foreach (Window w in Application.Current.Windows) AplicarBarraTitulo(w);
        }

        public static void AplicarAccent(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? "#3DDC84" : hex);
                var res = Application.Current.Resources;
                res["AccentColor"] = c;
                res["AccentBrush"] = Congelado(new SolidColorBrush(c));
                res["AccentSoftBrush"] = Congelado(new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)));
                // texto sobre o accent: escuro se a cor é clara
                double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                res["OnAccentBrush"] = Congelado(new SolidColorBrush(lum > 0.55 ? Color.FromRgb(6, 20, 12) : Colors.White));
            }
            catch (Exception ex) { Log.Warn("Accent inválido: " + ex.Message); }
        }

        /// <summary>NOVO 4.1: barra de título escura/clara combinando com o tema (Windows 10 20H1+ e 11).</summary>
        public static void AplicarBarraTitulo(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int escuro = Escuro ? 1 : 0;
                if (Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref escuro, 4) != 0)
                    Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref escuro, 4);
                if (Application.Current.Resources["BgBrush"] is SolidColorBrush bg)
                {
                    int cor = bg.Color.R | (bg.Color.G << 8) | (bg.Color.B << 16);
                    Native.DwmSetWindowAttribute(h, Native.DWMWA_CAPTION_COLOR, ref cor, 4);   // só Windows 11; no 10 é ignorado
                }
            }
            catch { }
        }

        public static Color CorDe(string hex, Color padrao)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return padrao; }
        }

        private static SolidColorBrush Congelado(SolidColorBrush b) { b.Freeze(); return b; }
    }

    /// <summary>
    /// Wallpaper com desfoque REAL controlado pelo slider.
    /// Truque leve: decodifica a imagem em resolução menor e deixa o WPF esticar com suavização.
    /// (sem shader, sem custo contínuo de GPU/CPU) — o escurecimento é um slider separado.
    /// </summary>
    public static class WallpaperService
    {
        public static BitmapImage Carregar(string caminho, int desfoque, int larguraFixa = 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return null;
                // 0 = nítido (1920 px) ... 100 = bem desfocado (~48 px de largura)
                int largura = larguraFixa > 0 ? larguraFixa : (int)Math.Max(48, 1920 * Math.Pow(1.0 - desfoque / 100.0, 2.2));
                // CORREÇÃO 4.1: lê os bytes em memória. Antes, ao trocar por outra imagem com o mesmo nome
                // ("wallpaper.jpg"), o WPF mostrava a imagem ANTIGA do cache.
                var bytes = File.ReadAllBytes(caminho);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = largura;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) { Log.Warn("Wallpaper: " + ex.Message); return null; }
        }

        /// <summary>Copia a imagem escolhida para a pasta de dados (o app continua portátil e não perde o arquivo).</summary>
        public static string Importar(string origem, string prefixo = "wallpaper")
        {
            try
            {
                Paths.Ensure();
                var pasta = Path.GetFullPath(Paths.WallpaperDir);
                if (Path.GetFullPath(origem).StartsWith(pasta, StringComparison.OrdinalIgnoreCase)) return origem;
                var destino = Path.Combine(pasta, $"{prefixo}-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(origem).ToLowerInvariant()}");
                File.Copy(origem, destino, true);
                return destino;
            }
            catch (Exception ex) { Log.Warn("Importar wallpaper: " + ex.Message); return origem; }
        }

        /// <summary>Apaga da pasta de dados as imagens que não estão mais em uso (antes ficavam acumulando).</summary>
        public static void LimparNaoUsados()
        {
            try
            {
                var c = ConfigService.Atual;
                var usados = new[] { c.WallpaperPath, c.WallpaperFullscreenPath }
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(Paths.WallpaperDir))
                    if (!usados.Contains(Path.GetFullPath(f))) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }
    }
}
