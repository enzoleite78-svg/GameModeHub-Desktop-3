using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    [Flags]
    public enum Botao : ushort
    {
        Cima = 0x0001, Baixo = 0x0002, Esq = 0x0004, Dir = 0x0008, Start = 0x0010, Back = 0x0020,
        L3 = 0x0040, R3 = 0x0080, LB = 0x0100, RB = 0x0200, Guia = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
    }

    /// <summary>Atalhos globais do controle (segurando LB + RB). Funcionam dentro do jogo.</summary>
    public enum ComboControle { ModoPadrao, Restaurar, FecharJogo, TrocarAudio, VolumeMais, VolumeMenos }

    /// <summary>XInput direto (sem SharpDX): qualquer controle (slots 0-3), botão Guia, repetição ao segurar, combos e vibração.</summary>
    public sealed class GamepadService : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct GAMEPAD { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)]
        struct STATE { public uint Packet; public GAMEPAD Pad; }
        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY { public byte Type; public byte Level; }
        [StructLayout(LayoutKind.Sequential)]
        struct VIBRATION { public ushort Esquerdo; public ushort Direito; }

        [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern int GetStateEx(int slot, out STATE s);
        [DllImport("xinput1_4.dll")] static extern int XInputGetState(int slot, out STATE s);
        [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int slot, byte devType, out BATTERY b);
        [DllImport("xinput1_4.dll")] static extern int XInputSetState(int slot, ref VIBRATION v);

        public event Action<Botao> BotaoPressionado;     // com repetição ao segurar direcional
        public event Action GuiaSegurado;                 // segurou o Guia 3,5 s
        public event Action<int> BateriaMudou;            // 0-100, -1 = desconhecido / cabo
        public event Action<bool> ConexaoMudou;
        public event Action<ComboControle> Combo;         // NOVO 4.1

        private CancellationTokenSource _cts;
        private Task _loop;
        private volatile bool _conectado;
        private volatile int _slot = -1;
        public bool Conectado => _conectado;
        public int Bateria { get; private set; } = -1;
        public bool Pausado { get; set; }
        /// <summary>Se false, Vibrar() não faz nada (Controles → Vibrar).</summary>
        public bool VibracaoAtiva { get; set; } = true;

        public void Iniciar()
        {
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => Loop(_cts.Token));
        }

        private static int Ler(int slot, out STATE s)
        {
            try { return GetStateEx(slot, out s); }
            catch (EntryPointNotFoundException) { return XInputGetState(slot, out s); }
        }

        /// <summary>NOVO 4.1: vibração curta de confirmação (como na v3).</summary>
        public void Vibrar(int ms = 180, ushort forca = 30000)
        {
            int s = _slot;
            if (s < 0 || !VibracaoAtiva) return;
            Task.Run(async () =>
            {
                try
                {
                    var v = new VIBRATION { Esquerdo = forca, Direito = forca };
                    XInputSetState(s, ref v);
                    await Task.Delay(ms);
                    var z = new VIBRATION();
                    XInputSetState(s, ref z);
                }
                catch { }
            });
        }

        private void Loop(CancellationToken ct)
        {
            int slot = -1;
            ushort anterior = 0;
            DateTime guiaDesde = DateTime.MinValue; bool guiaDisparou = false, guiaAvisou = false;
            DateTime proxRepeticao = DateTime.MinValue; ushort repetindo = 0;
            DateTime proxBateria = DateTime.MinValue;
            DateTime proxVolume = DateTime.MinValue;
            var segurandoDesde = new Dictionary<ushort, DateTime>();
            var disparados = new HashSet<ushort>();
            int ultBateria = -2;
            const ushort LBRB = (ushort)(Botao.LB | Botao.RB);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (slot < 0)
                    {
                        for (int i = 0; i < 4; i++) if (Ler(i, out _) == 0) { slot = i; break; }
                        if (slot < 0)
                        {
                            if (_conectado) { _conectado = false; _slot = -1; Bateria = -1; ConexaoMudou?.Invoke(false); }
                            Thread.Sleep(2000); continue;   // sem controle: quase zero CPU
                        }
                        _slot = slot; _conectado = true; ConexaoMudou?.Invoke(true); proxBateria = DateTime.MinValue; ultBateria = -2;
                    }

                    if (Ler(slot, out var st) != 0) { slot = -1; anterior = 0; continue; }

                    ushort b = st.Pad.Buttons;
                    ushort novos = (ushort)(b & ~anterior);
                    var agora = DateTime.UtcNow;
                    bool lbrb = (b & LBRB) == LBRB;

                    if (lbrb)
                    {
                        // ---- combos (LB + RB seguros) ----
                        if ((novos & (ushort)Botao.Dir) != 0) Combo?.Invoke(ComboControle.TrocarAudio);
                        ushort vol = (ushort)(b & (ushort)(Botao.Cima | Botao.Baixo));
                        if ((novos & (ushort)Botao.Cima) != 0) { Combo?.Invoke(ComboControle.VolumeMais); proxVolume = agora.AddMilliseconds(450); }
                        else if ((novos & (ushort)Botao.Baixo) != 0) { Combo?.Invoke(ComboControle.VolumeMenos); proxVolume = agora.AddMilliseconds(450); }
                        else if (vol != 0 && agora >= proxVolume)
                        {
                            Combo?.Invoke((vol & (ushort)Botao.Cima) != 0 ? ComboControle.VolumeMais : ComboControle.VolumeMenos);
                            proxVolume = agora.AddMilliseconds(170);
                        }
                        Segurar(b, (ushort)Botao.Start, 2000, ComboControle.ModoPadrao, segurandoDesde, disparados);
                        Segurar(b, (ushort)Botao.Back, 1500, ComboControle.Restaurar, segurandoDesde, disparados);
                        Segurar(b, (ushort)Botao.Esq, 1500, ComboControle.FecharJogo, segurandoDesde, disparados);
                        repetindo = 0;
                    }
                    else
                    {
                        segurandoDesde.Clear(); disparados.Clear();
                        if (!Pausado)
                        {
                            foreach (Botao bt in Enum.GetValues(typeof(Botao)))
                            {
                                if (bt == Botao.Guia) continue;
                                if ((novos & (ushort)bt) != 0) BotaoPressionado?.Invoke(bt);
                            }
                            // repetição dos direcionais
                            ushort dir = (ushort)(b & 0x000F);
                            if (dir != 0 && (novos & 0x000F) != 0) { repetindo = dir; proxRepeticao = agora.AddMilliseconds(380); }
                            else if (dir != 0 && dir == repetindo && agora >= proxRepeticao)
                            {
                                foreach (Botao bt in new[] { Botao.Cima, Botao.Baixo, Botao.Esq, Botao.Dir })
                                    if ((dir & (ushort)bt) != 0) BotaoPressionado?.Invoke(bt);
                                proxRepeticao = agora.AddMilliseconds(90);
                            }
                            else if (dir == 0) repetindo = 0;
                        }
                    }

                    // Guia segurado (aviso com vibração em 1,5 s, ação em 3,5 s)
                    if ((b & (ushort)Botao.Guia) != 0)
                    {
                        if (guiaDesde == DateTime.MinValue) { guiaDesde = agora; guiaDisparou = false; guiaAvisou = false; }
                        else
                        {
                            var ms = (agora - guiaDesde).TotalMilliseconds;
                            if (!guiaAvisou && ms >= 1500) { guiaAvisou = true; Vibrar(90, 18000); }
                            if (!guiaDisparou && ms >= 3500) { guiaDisparou = true; GuiaSegurado?.Invoke(); }
                        }
                    }
                    else guiaDesde = DateTime.MinValue;

                    // bateria: a cada 30 s, não a cada 16 ms
                    if (agora >= proxBateria)
                    {
                        proxBateria = agora.AddSeconds(30);
                        int nivel = -1;
                        if (XInputGetBatteryInformation(slot, 0, out var bat) == 0)
                            nivel = bat.Type switch { 1 => -1, 0 => -1, _ => bat.Level switch { 0 => 5, 1 => 33, 2 => 66, 3 => 100, _ => -1 } };
                        if (nivel != ultBateria) { ultBateria = nivel; Bateria = nivel; BateriaMudou?.Invoke(nivel); }
                    }
                    anterior = b;
                }
                catch (Exception ex) { Log.Error("Loop do controle", ex); Thread.Sleep(1000); }

                // 60 Hz com controle; a verificação de desconexão já dorme 2 s
                Thread.Sleep(16);
            }
        }

        private void Segurar(ushort b, ushort bit, int ms, ComboControle combo, Dictionary<ushort, DateTime> desde, HashSet<ushort> disparados)
        {
            if ((b & bit) == 0) { desde.Remove(bit); disparados.Remove(bit); return; }
            if (!desde.TryGetValue(bit, out var inicio)) { desde[bit] = DateTime.UtcNow; return; }
            if (!disparados.Contains(bit) && (DateTime.UtcNow - inicio).TotalMilliseconds >= ms)
            {
                disparados.Add(bit);
                Combo?.Invoke(combo);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); _loop?.Wait(500); } catch { }
            _cts?.Dispose();
        }
    }
}
