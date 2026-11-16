using System;
using System.Collections.Generic;
using System.Windows.Interop;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    /// <summary>Atalhos globais (funcionam dentro do jogo). Formato: "NumPad1", "F6", "Ctrl+Alt+K".</summary>
    public sealed class HotkeyService : IDisposable
    {
        private HwndSource _src;
        private readonly Dictionary<int, Action> _acoes = new();
        private int _prox = 1;

        public void Iniciar()
        {
            var p = new HwndSourceParameters("GMH_Hotkeys") { Width = 0, Height = 0, WindowStyle = 0 };
            p.ParentWindow = new IntPtr(-3); // HWND_MESSAGE: janela invisível só para mensagens
            _src = new HwndSource(p);
            _src.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _acoes.TryGetValue(w.ToInt32(), out var a))
            {
                handled = true;
                System.Threading.Tasks.Task.Run(() => { try { a(); } catch (Exception ex) { Log.Error("Hotkey", ex); } });
            }
            return IntPtr.Zero;
        }

        public void LimparTudo()
        {
            if (_src == null) return;
            foreach (var id in _acoes.Keys) Native.UnregisterHotKey(_src.Handle, id);
            _acoes.Clear(); _prox = 1;
        }

        public bool Registrar(string tecla, Action acao)
        {
            if (_src == null || string.IsNullOrWhiteSpace(tecla)) return false;
            if (!Analisar(tecla, out uint mod, out uint vk)) { Log.Warn("Atalho inválido: " + tecla); return false; }
            int id = _prox++;
            if (!Native.RegisterHotKey(_src.Handle, id, mod | 0x4000 /*NOREPEAT*/, vk)) { Log.Warn("Atalho em uso por outro programa: " + tecla); return false; }
            _acoes[id] = acao;
            return true;
        }

        /// <summary>Compara dois atalhos ignorando maiúsculas, espaços e ordem dos modificadores.</summary>
        public static bool Iguais(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return Analisar(a, out var ma, out var va) && Analisar(b, out var mb, out var vb) && ma == mb && va == vb;
        }

        public static bool Analisar(string texto, out uint mod, out uint vk)
        {
            mod = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(texto)) return false;
            foreach (var parte in texto.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var p = parte.ToLowerInvariant();
                if (p == "ctrl" || p == "control") { mod |= 2; continue; }
                if (p == "alt") { mod |= 1; continue; }
                if (p == "shift") { mod |= 4; continue; }
                if (p == "win") { mod |= 8; continue; }
                if (vk != 0) return false;   // duas teclas "normais" no mesmo atalho
                if (p.StartsWith("numpad") && int.TryParse(p.Substring(6), out int n) && n >= 0 && n <= 9) { vk = (uint)(0x60 + n); continue; }
                if (p.Length >= 2 && p[0] == 'f' && int.TryParse(p.Substring(1), out int f) && f >= 1 && f <= 24) { vk = (uint)(0x6F + f); continue; }
                if (p.Length == 1 && p[0] >= 'a' && p[0] <= 'z') { vk = (uint)char.ToUpperInvariant(p[0]); continue; }
                if (p.Length == 1 && p[0] >= '0' && p[0] <= '9') { vk = (uint)p[0]; continue; }
                // MELHORIA 4.1: mais teclas aceitas
                vk = p switch
                {
                    "home" => 0x24u, "end" => 0x23u, "insert" or "ins" => 0x2Du, "pause" => 0x13u,
                    "delete" or "del" => 0x2Eu, "pageup" or "pgup" => 0x21u, "pagedown" or "pgdn" => 0x22u,
                    "scrolllock" or "scroll" => 0x91u, "up" => 0x26u, "down" => 0x28u, "left" => 0x25u, "right" => 0x27u,
                    "space" => 0x20u, "enter" => 0x0Du, "tab" => 0x09u, "esc" or "escape" => 0x1Bu, "backspace" => 0x08u,
                    "multiply" => 0x6Au, "add" => 0x6Bu, "subtract" => 0x6Du, "decimal" => 0x6Eu, "divide" => 0x6Fu,
                    _ => 0u
                };
                if (vk == 0) return false;
            }
            return vk != 0;
        }

        public void Dispose() { LimparTudo(); _src?.Dispose(); }
    }
}
