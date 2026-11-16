using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using GameModeHub.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameModeHub.Services
{
    public class SaidaAudio
    {
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "";
        public bool Padrao { get; set; }
        public override string ToString() => Nome;
    }

    /// <summary>Troca de saída de áudio via COM nativo (sem PowerShell, sem módulos externos).</summary>
    public static class AudioService
    {
        #region COM IPolicyConfig (não documentado, mas estável há anos)
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool def, out IntPtr fmt);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool def, out long a, out long b);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr p);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr m);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr m);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, IntPtr key, out IntPtr v);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, IntPtr key, IntPtr v);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
        }
        #endregion

        public static List<SaidaAudio> ListarSaidas()
        {
            var lista = new List<SaidaAudio>();
            try
            {
                using var en = new MMDeviceEnumerator();
                string padraoId = "";
                try { using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); padraoId = d.ID; } catch { }
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try { lista.Add(new SaidaAudio { Id = d.ID, Nome = d.FriendlyName, Padrao = d.ID == padraoId }); }
                    finally { d.Dispose(); }
                }
            }
            catch (Exception ex) { Log.Error("Listar áudio", ex); }
            return lista.OrderBy(s => s.Nome, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool DefinirPadrao(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                var cfg = (IPolicyConfig)new PolicyConfigClient();
                int r = 0;
                r |= cfg.SetDefaultEndpoint(id, 0); // console
                r |= cfg.SetDefaultEndpoint(id, 1); // multimedia
                r |= cfg.SetDefaultEndpoint(id, 2); // comunicações
                return r == 0;
            }
            catch (Exception ex) { Log.Error("DefinirPadrao áudio", ex); return false; }
        }

        /// <summary>Acha o dispositivo pelo id salvo; se mudou, tenta pelo nome. NÃO sobrescreve a config.</summary>
        public static SaidaAudio Resolver(string id, string nome) => Resolver(id, nome, ListarSaidas());

        public static SaidaAudio Resolver(string id, string nome, List<SaidaAudio> todas)
        {
            var p = string.IsNullOrEmpty(id) ? null : todas.FirstOrDefault(s => s.Id == id);
            if (p != null) return p;
            if (!string.IsNullOrWhiteSpace(nome))
                p = todas.FirstOrDefault(s => s.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)) ??
                    todas.FirstOrDefault(s => s.Nome.IndexOf(nome, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              nome.IndexOf(s.Nome, StringComparison.OrdinalIgnoreCase) >= 0);
            return p;
        }

        /// <summary>slot = "A" ou "B". Retorna o nome usado ou null.</summary>
        public static string AplicarSlot(string slot)
        {
            var c = ConfigService.Atual;
            var alvo = slot == "B" ? Resolver(c.AudioSaidaBCmd, c.AudioSaidaBNome) : Resolver(c.AudioSaidaACmd, c.AudioSaidaANome);
            if (alvo == null) return null;
            if (!DefinirPadrao(alvo.Id)) return null;
            c.AudioSlotAtivo = slot == "B" ? "B" : "A";
            ConfigService.Salvar();
            Log.Info("Áudio → " + alvo.Nome);
            return alvo.Nome;
        }

        public static string Alternar() => AplicarSlot(ConfigService.Atual.AudioSlotAtivo == "A" ? "B" : "A");

        // ----- volume do dispositivo padrão -----
        public static int LerVolume()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            }
            catch { return -1; }
        }

        public static void DefinirVolume(int pct)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(pct, 0, 100) / 100f;
                if (pct > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;   // mexeu no volume = quer ouvir
            }
            catch (Exception ex) { Log.Error("Volume", ex); }
        }

        public static bool LerMudo()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return d.AudioEndpointVolume.Mute;
            }
            catch { return false; }
        }

        /// <summary>NOVO 4.1: liga/desliga o mudo. Retorna o novo estado (null se falhou).</summary>
        public static bool? DefinirMudo(bool? mudo = null)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                d.AudioEndpointVolume.Mute = mudo ?? !d.AudioEndpointVolume.Mute;
                return d.AudioEndpointVolume.Mute;
            }
            catch (Exception ex) { Log.Error("Mudo", ex); return null; }
        }

        // ----- NOVO 4.2: saída por id e mixer por programa (para o celular) -----
        /// <summary>Define a saída padrão pelo id. Se for a A ou a B configurada, atualiza o slot ativo.</summary>
        public static string DefinirSaidaPorId(string id)
        {
            var s = ListarSaidas().FirstOrDefault(x => x.Id == id);
            if (s == null || !DefinirPadrao(s.Id)) return null;
            var c = ConfigService.Atual;
            if (s.Id == c.AudioSaidaACmd) c.AudioSlotAtivo = "A";
            else if (s.Id == c.AudioSaidaBCmd) c.AudioSlotAtivo = "B";
            ConfigService.Salvar();
            Log.Info("Áudio → " + s.Nome);
            return s.Nome;
        }

        public class AppAudio { public int Pid { get; set; } public string Nome { get; set; } = ""; public int Volume { get; set; } public bool Mudo { get; set; } }

        public static List<AppAudio> ListarApps()
        {
            var l = new List<AppAudio>();
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sm = d.AudioSessionManager;
                sm.RefreshSessions();
                var ses = sm.Sessions;
                for (int i = 0; i < ses.Count; i++)
                {
                    var s = ses[i];
                    try
                    {
                        if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                        int pid = (int)s.GetProcessID;
                        string nome;
                        if (s.IsSystemSoundsSession) nome = "Sons do sistema";
                        else
                        {
                            try { using var p = System.Diagnostics.Process.GetProcessById(pid); nome = p.ProcessName; }
                            catch { nome = string.IsNullOrWhiteSpace(s.DisplayName) ? "App " + pid : s.DisplayName; }
                        }
                        if (l.Any(x => x.Pid == pid)) continue;
                        l.Add(new AppAudio { Pid = pid, Nome = nome, Volume = (int)Math.Round(s.SimpleAudioVolume.Volume * 100), Mudo = s.SimpleAudioVolume.Mute });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Warn("Mixer: " + ex.Message); }
            return l.OrderBy(a => a.Nome == "Sons do sistema").ThenBy(a => a.Nome, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool DefinirApp(int pid, int? volume, bool? mudo)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sm = d.AudioSessionManager;
                sm.RefreshSessions();
                var ses = sm.Sessions;
                bool achou = false;
                for (int i = 0; i < ses.Count; i++)
                {
                    var s = ses[i];
                    if ((int)s.GetProcessID != pid) continue;
                    if (volume.HasValue) s.SimpleAudioVolume.Volume = Math.Clamp(volume.Value, 0, 100) / 100f;
                    if (mudo.HasValue) s.SimpleAudioVolume.Mute = mudo.Value;
                    achou = true;
                }
                return achou;
            }
            catch (Exception ex) { Log.Warn("Mixer: " + ex.Message); return false; }
        }

        /// <summary>NOVO 4.1: toca um "ding" curto NAQUELA saída (sem trocar o padrão) para saber qual é qual.</summary>
        public static bool Testar(string id)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var dev = en.GetDevice(id);
                var mix = dev.AudioClient.MixFormat;
                var som = new Ding(mix.SampleRate, Math.Max(1, mix.Channels));
                using var saida = new WasapiOut(dev, AudioClientShareMode.Shared, true, 80);
                saida.Init(new SampleToWaveProvider(som));
                saida.Play();
                var limite = DateTime.UtcNow.AddSeconds(2);
                while (saida.PlaybackState == PlaybackState.Playing && DateTime.UtcNow < limite) Thread.Sleep(40);
                return true;
            }
            catch (Exception ex) { Log.Warn("Testar áudio: " + ex.Message); return false; }
        }

        /// <summary>Duas notas suaves (com envelope, sem estalo).</summary>
        private sealed class Ding : ISampleProvider
        {
            private readonly int _total; private int _pos;
            public WaveFormat WaveFormat { get; }
            public Ding(int rate, int canais) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, canais); _total = (int)(rate * 0.75); }

            private static double Nota(double t, double f) =>
                t < 0 ? 0 : Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 6.5) * Math.Min(1.0, t / 0.006);

            public int Read(float[] buffer, int offset, int count)
            {
                int ch = WaveFormat.Channels, rate = WaveFormat.SampleRate, n = 0;
                int frames = count / ch;
                for (int i = 0; i < frames && _pos < _total; i++, _pos++)
                {
                    double t = (double)_pos / rate;
                    float v = (float)((Nota(t, 659.25) + Nota(t - 0.13, 987.77)) * 0.18);
                    for (int c = 0; c < ch; c++) buffer[offset + n++] = v;
                }
                return n;
            }
        }
    }
}
