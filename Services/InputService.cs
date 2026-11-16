using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    /// <summary>Teclado e mouse virtuais via SendInput (suporta acentos, símbolos, modificadores, arrastar e posição absoluta).</summary>
    public static class InputService
    {
        #region interop
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint n, INPUT[] inputs, int size);

        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
        const uint MOUSEEVENTF_MOVE = 1, LEFTDOWN = 2, LEFTUP = 4, RIGHTDOWN = 8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40,
                   WHEEL = 0x800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
        const int WHEEL_DELTA = 120;
        #endregion

        private static void Enviar(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());

        // teclas "estendidas" (setas, Home, Delete…) precisam da flag para não virar o teclado numérico
        private static readonly HashSet<ushort> Estendidas = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90 };

        private static INPUT Tecla(ushort vk, bool up) => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (Estendidas.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0) } }
        };
        private static INPUT Uni(char c, bool up) => new INPUT { type = INPUT_KEYBOARD, u = { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } } };
        private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
            new INPUT { type = INPUT_MOUSE, u = { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = unchecked((uint)data) } } };

        public static void DigitarTexto(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;
            foreach (char c in texto)
            {
                if (c == '\r') continue;
                if (c == '\n') { TeclaEspecial("enter"); continue; }
                if (c == '\b') { TeclaEspecial("backspace"); continue; }
                if (c == '\t') { TeclaEspecial("tab"); continue; }
                Enviar(Uni(c, false), Uni(c, true));
            }
        }

        /// <summary>Nome da tecla → código virtual do Windows (0 se não existir).</summary>
        public static ushort CodigoDaTecla(string nome)
        {
            var n = (nome ?? "").Trim().ToLowerInvariant();
            if (n.Length == 1 && n[0] >= 'a' && n[0] <= 'z') return (ushort)char.ToUpperInvariant(n[0]);
            if (n.Length == 1 && n[0] >= '0' && n[0] <= '9') return n[0];
            if (n.Length >= 2 && n[0] == 'f' && int.TryParse(n.Substring(1), out int f) && f >= 1 && f <= 24) return (ushort)(0x6F + f);
            return n switch
            {
                "enter" => 0x0D, "backspace" or "apagar" => 0x08, "tab" => 0x09, "esc" or "escape" => 0x1B, "space" or "espaco" => 0x20,
                "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28, "delete" or "del" => 0x2E,
                "home" => 0x24, "end" => 0x23, "pgup" or "pageup" => 0x21, "pgdn" or "pagedown" => 0x22, "win" => 0x5B,
                "playpause" => 0xB3, "next" => 0xB0, "prev" => 0xB1, "stop" => 0xB2,
                "volup" => 0xAF, "voldown" => 0xAE, "mute" => 0xAD,
                "printscreen" or "prtsc" => 0x2C, "capslock" => 0x14, "menu" => 0x5D, "insert" => 0x2D,
                "ctrl" => 0x11, "alt" => 0x12, "shift" => 0x10,
                _ => 0
            };
        }

        private static ushort Modificador(string m) => (m ?? "").Trim().ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11, "alt" => 0x12, "shift" => 0x10, "win" => 0x5B, _ => 0
        };

        /// <summary>Aperta uma tecla com modificadores opcionais ("ctrl,shift"). Aceita também combinações prontas.</summary>
        public static bool TeclaEspecial(string nome, string mods = null)
        {
            var n = (nome ?? "").Trim().ToLowerInvariant();
            // combinações prontas
            switch (n)
            {
                case "altf4": case "alt+f4": Combo(0x12, 0x73); return true;
                case "alttab": case "alt+tab": AltTab(); return true;
                case "desktop": case "win+d": Combo(0x5B, 0x44); return true;
                case "wintab": case "win+tab": Combo(0x5B, 0x09); return true;
                case "copiar": case "ctrl+c": Combo(0x11, 0x43); return true;
                case "colar": case "ctrl+v": Combo(0x11, 0x56); return true;
                case "recortar": case "ctrl+x": Combo(0x11, 0x58); return true;
                case "desfazer": case "ctrl+z": Combo(0x11, 0x5A); return true;
                case "tudo": case "ctrl+a": Combo(0x11, 0x41); return true;
                case "proximajanela": Combo(0x12, 0x09); return true;
                case "janelaanterior": Combo(0x12, 0x10, 0x09); return true;
                case "explorer": case "win+e": Combo(0x5B, 0x45); return true;
                case "gamebar": case "win+g": Combo(0x5B, 0x47); return true;
                case "taskmgr": Combo(0x11, 0x10, 0x1B); return true;
            }
            ushort vk = CodigoDaTecla(n);
            if (vk == 0) return false;
            var lista = new List<ushort>();
            if (!string.IsNullOrWhiteSpace(mods))
                foreach (var m in mods.Split(',', '+', ' '))
                {
                    var c = Modificador(m);
                    if (c != 0 && !lista.Contains(c)) lista.Add(c);
                }
            lista.Add(vk);
            Combo(lista.ToArray());
            return true;
        }

        public static void Combo(params ushort[] vks)
        {
            var l = new List<INPUT>();
            foreach (var v in vks) l.Add(Tecla(v, false));
            for (int i = vks.Length - 1; i >= 0; i--) l.Add(Tecla(vks[i], true));
            Enviar(l.ToArray());
        }

        public static void AltTab() => Combo(0x12, 0x09);

        public static void MoverMouse(int dx, int dy) { if (dx != 0 || dy != 0) Enviar(Mouse(MOUSEEVENTF_MOVE, dx, dy)); }

        /// <summary>NOVO 4.2: posição absoluta na área de trabalho inteira (x e y de 0 a 1) — usado pela prévia da tela no celular.</summary>
        public static void MoverAbsoluto(double x, double y)
        {
            int ax = (int)Math.Round(Math.Clamp(x, 0, 1) * 65535), ay = (int)Math.Round(Math.Clamp(y, 0, 1) * 65535);
            Enviar(Mouse(MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK, ax, ay));
        }

        private static (uint down, uint up) Botoes(string btn) => (btn ?? "left").ToLowerInvariant() switch
        {
            "right" or "direito" => (RIGHTDOWN, RIGHTUP),
            "middle" or "meio" => (MIDDLEDOWN, MIDDLEUP),
            _ => (LEFTDOWN, LEFTUP)
        };

        public static void Clique(bool direito = false) => Clique(direito ? "right" : "left");

        /// <summary>btn = left | right | middle.</summary>
        public static void Clique(string btn, bool duplo = false)
        {
            var (down, up) = Botoes(btn);
            Enviar(Mouse(down), Mouse(up));
            if (duplo) Enviar(Mouse(down), Mouse(up));
        }

        /// <summary>NOVO 4.2: segurar/soltar o botão (arrastar com o dedo no celular).</summary>
        public static void Botao(string btn, bool apertar)
        {
            var (down, up) = Botoes(btn);
            Enviar(Mouse(apertar ? down : up));
        }

        /// <summary>O celular manda passos pequenos (1 / -1); a roda do Windows trabalha em múltiplos de 120.</summary>
        private static int Escalar(int d) => Math.Abs(d) <= 10 ? d * WHEEL_DELTA : d;

        public static void Rolar(int delta) { if (delta != 0) Enviar(Mouse(WHEEL, 0, 0, Escalar(delta))); }

        /// <summary>NOVO 4.2: rolagem horizontal.</summary>
        public static void RolarHorizontal(int delta) { if (delta != 0) Enviar(Mouse(HWHEEL, 0, 0, Escalar(delta))); }

        public static void Zoom(int delta)
        {
            if (delta == 0) return;
            Enviar(Tecla(0x11, false)); Enviar(Mouse(WHEEL, 0, 0, Escalar(delta))); Enviar(Tecla(0x11, true));
        }

        /// <summary>Destrava o mouse preso por um jogo que travou/fechou.</summary>
        public static void LiberarCursor() { try { Native.ClipCursor(IntPtr.Zero); } catch { } }

        /// <summary>Joga o cursor para o canto inferior direito da área de trabalho (não fica no meio da TV).</summary>
        public static void EsconderCursor()
        {
            try
            {
                int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) - 1;
                int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN) - 1;
                Native.SetCursorPos(x, y);
            }
            catch { }
        }
    }
}
