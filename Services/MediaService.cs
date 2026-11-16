using System;
using System.IO;
using System.Threading.Tasks;
using GameModeHub.Core;
using Windows.Media.Control;

namespace GameModeHub.Services
{
    /// <summary>NOVO 4.2: "Tocando agora" (Spotify, YouTube no navegador, etc.) pelo controle de mídia do próprio Windows.</summary>
    public static class MediaService
    {
        public class Info
        {
            public bool Tem { get; set; }
            public string Titulo { get; set; } = "";
            public string Artista { get; set; } = "";
            public string App { get; set; } = "";
            public bool Tocando { get; set; }
            public bool TemCapa { get; set; }
        }

        private static async Task<GlobalSystemMediaTransportControlsSession> Sessao()
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return mgr.GetCurrentSession();
        }

        public static async Task<Info> Ler()
        {
            try
            {
                var s = await Sessao();
                if (s == null) return new Info();
                var p = await s.TryGetMediaPropertiesAsync();
                var pb = s.GetPlaybackInfo();
                var app = s.SourceAppUserModelId ?? "";
                if (app.Contains('!')) app = app.Substring(0, app.IndexOf('!'));
                if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app.Substring(0, app.Length - 4);
                return new Info
                {
                    Tem = true, Titulo = p?.Title ?? "", Artista = p?.Artist ?? "", App = app,
                    Tocando = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    TemCapa = p?.Thumbnail != null
                };
            }
            catch (Exception ex) { Log.Warn("Mídia: " + ex.Message); return new Info(); }
        }

        public static async Task<byte[]> Capa()
        {
            try
            {
                var s = await Sessao();
                if (s == null) return null;
                var p = await s.TryGetMediaPropertiesAsync();
                if (p?.Thumbnail == null) return null;
                using var ras = await p.Thumbnail.OpenReadAsync();
                using var st = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                await st.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        /// <summary>acao = playpause | next | prev. Se não houver sessão, usa as teclas de mídia.</summary>
        public static async Task<bool> Acao(string acao)
        {
            try
            {
                var s = await Sessao();
                if (s != null)
                {
                    switch (acao)
                    {
                        case "playpause": return await s.TryTogglePlayPauseAsync();
                        case "next": return await s.TrySkipNextAsync();
                        case "prev": return await s.TrySkipPreviousAsync();
                    }
                }
            }
            catch { }
            return InputService.TeclaEspecial(acao);
        }
    }
}
