using System;
using System.Runtime.InteropServices;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    /// <summary>Teclado e mouse virtuais via SendInput (suporta acentos, símbolos e Enter).</summary>
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
        const uint KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
        const uint MOUSEEVENTF_MOVE = 1, LEFTDOWN = 2, LEFTUP = 4, RIGHTDOWN = 8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40, WHEEL = 0x800;
        const int WHEEL_DELTA = 120;
        #endregion

        private static void Enviar(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());

        private static INPUT Tecla(ushort vk, bool up) => new INPUT { type = INPUT_KEYBOARD, u = { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };
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

        public static bool TeclaEspecial(string nome)
        {
            var n = (nome ?? "").Trim().ToLowerInvariant();
            // combinações prontas (MELHORIA 4.1)
            switch (n)
            {
                case "altf4": case "alt+f4": Combo(0x12, 0x73); return true;
                case "alttab": case "alt+tab": AltTab(); return true;
                case "desktop": case "win+d": Combo(0x5B, 0x44); return true;
                case "copiar": case "ctrl+c": Combo(0x11, 0x43); return true;
                case "colar": case "ctrl+v": Combo(0x11, 0x56); return true;
            }
            ushort vk = n switch
            {
                "enter" => 0x0D, "backspace" or "apagar" => 0x08, "tab" => 0x09, "esc" or "escape" => 0x1B, "space" or "espaco" => 0x20,
                "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28, "delete" or "del" => 0x2E,
                "home" => 0x24, "end" => 0x23, "pgup" or "pageup" => 0x21, "pgdn" or "pagedown" => 0x22, "win" => 0x5B,
                "playpause" => 0xB3, "next" => 0xB0, "prev" => 0xB1, "stop" => 0xB2,
                "volup" => 0xAF, "voldown" => 0xAE, "mute" => 0xAD,
                "printscreen" or "prtsc" => 0x2C, "capslock" => 0x14, "menu" => 0x5D, "insert" => 0x2D,
                _ => 0
            };
            // F1..F24 genérico
            if (vk == 0 && n.Length >= 2 && n[0] == 'f' && int.TryParse(n.Substring(1), out int f) && f >= 1 && f <= 24) vk = (ushort)(0x6F + f);
            if (vk == 0) return false;
            Enviar(Tecla(vk, false), Tecla(vk, true));
            return true;
        }

        public static void Combo(params ushort[] vks)
        {
            var l = new System.Collections.Generic.List<INPUT>();
            foreach (var v in vks) l.Add(Tecla(v, false));
            for (int i = vks.Length - 1; i >= 0; i--) l.Add(Tecla(vks[i], true));
            Enviar(l.ToArray());
        }

        public static void AltTab() => Combo(0x12, 0x09);

        public static void MoverMouse(int dx, int dy) { if (dx != 0 || dy != 0) Enviar(Mouse(MOUSEEVENTF_MOVE, dx, dy)); }

        public static void Clique(bool direito = false) => Clique(direito ? "right" : "left");

        /// <summary>btn = left | right | middle. MELHORIA 4.1: botão do meio e clique duplo.</summary>
        public static void Clique(string btn, bool duplo = false)
        {
            uint down, up;
            switch ((btn ?? "left").ToLowerInvariant())
            {
                case "right": case "direito": down = RIGHTDOWN; up = RIGHTUP; break;
                case "middle": case "meio": down = MIDDLEDOWN; up = MIDDLEUP; break;
                default: down = LEFTDOWN; up = LEFTUP; break;
            }
            Enviar(Mouse(down), Mouse(up));
            if (duplo) Enviar(Mouse(down), Mouse(up));
        }

        /// <summary>
        /// CORREÇÃO 4.1: o app do celular manda passos pequenos (1 / -1); a roda do Windows precisa de múltiplos de 120,
        /// então a rolagem praticamente não andava. Valores pequenos agora viram "cliques" da roda.
        /// </summary>
        private static int Escalar(int d) => Math.Abs(d) <= 10 ? d * WHEEL_DELTA : d;

        public static void Rolar(int delta) { if (delta != 0) Enviar(Mouse(WHEEL, 0, 0, Escalar(delta))); }

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
