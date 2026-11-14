using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    public class TelaInfo
    {
        public string Path { get; set; } = "";       // monitorDevicePath: identifica a tela de forma estável
        public string Nome { get; set; } = "";
        public bool Ativa { get; set; }
        public bool Principal { get; set; }
        public bool HdrSuportado { get; set; }
        public bool HdrLigado { get; set; }
        internal long AdapterLow; internal int AdapterHigh; internal uint TargetId;
        public override string ToString() => Nome + (Ativa ? " (acesa)" : " (apagada)");
    }

    /// <summary>
    /// Escolhe EXATAMENTE quais telas ficam acesas (resolve o bug de 2 monitores + 1 TV).
    /// Usa a API CCD do Windows (SetDisplayConfig) e identifica cada tela pelo caminho estável do monitor,
    /// não por número (que muda quando o Windows reordena).
    /// </summary>
    public static class DisplayService
    {
        #region interop
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct RATIONAL { public uint Num, Den; }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_TARGET
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx;
            public uint outputTechnology; public uint rotation; public uint scaling;
            public RATIONAL refreshRate; public uint scanLineOrdering;
            public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        struct MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public long u0; [FieldOffset(24)] public long u1; [FieldOffset(32)] public long u2;
            [FieldOffset(40)] public long u3; [FieldOffset(48)] public long u4; [FieldOffset(56)] public long u5;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TARGET_NAME
        {
            public HEADER header; public uint flags; public uint outputTechnology;
            public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ADV_COLOR_INFO { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

        [StructLayout(LayoutKind.Sequential)]
        struct ADV_COLOR_SET { public HEADER header; public uint enable; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths, ref uint modeCount, [Out] MODE_INFO[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint pathCount, [In] PATH_INFO[] paths, uint modeCount, [In] MODE_INFO[] modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ADV_COLOR_INFO info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref ADV_COLOR_SET info);

        const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE = 2;
        const uint SDC_TOPOLOGY_SUPPLIED = 0x10, SDC_USE_SUPPLIED = 0x20, SDC_VALIDATE = 0x40, SDC_APPLY = 0x80,
                   SDC_SAVE_TO_DB = 0x200, SDC_ALLOW_CHANGES = 0x400, SDC_ALLOW_PATH_ORDER = 0x2000;
        const uint PATH_ACTIVE = 1;
        const uint IDX_INVALID = 0xFFFFFFFF;
        #endregion

        private static readonly object _lock = new object();

        private static bool Consulta(uint flags, out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            paths = Array.Empty<PATH_INFO>(); modes = Array.Empty<MODE_INFO>();
            for (int tentativa = 0; tentativa < 3; tentativa++)
            {
                if (GetDisplayConfigBufferSizes(flags, out uint pc, out uint mc) != 0) return false;
                var p = new PATH_INFO[pc]; var m = new MODE_INFO[mc];
                int r = QueryDisplayConfig(flags, ref pc, p, ref mc, m, IntPtr.Zero);
                if (r == 122) continue; // ERROR_INSUFFICIENT_BUFFER: tenta de novo
                if (r != 0) return false;
                paths = p.Take((int)pc).ToArray(); modes = m.Take((int)mc).ToArray();
                return true;
            }
            return false;
        }

        private static (string nome, string path) NomeDoAlvo(PATH_TARGET t)
        {
            var n = new TARGET_NAME();
            n.header.type = 2; // GET_TARGET_NAME
            n.header.size = (uint)Marshal.SizeOf<TARGET_NAME>();
            n.header.adapterId = t.adapterId; n.header.id = t.id;
            if (DisplayConfigGetDeviceInfo(ref n) != 0) return (null, null);
            var nome = string.IsNullOrWhiteSpace(n.monitorFriendlyDeviceName) ? "Tela " + t.id : n.monitorFriendlyDeviceName;
            return (nome, n.monitorDevicePath);
        }

        /// <summary>Lista todas as telas físicas conectadas (acesas ou apagadas).</summary>
        public static List<TelaInfo> Listar()
        {
            lock (_lock)
            {
                var res = new Dictionary<string, TelaInfo>();
                if (!Consulta(QDC_ALL_PATHS, out var all, out _)) return new List<TelaInfo>();
                Consulta(QDC_ONLY_ACTIVE, out var ativos, out var modosAtivos);

                foreach (var p in all)
                {
                    if (p.targetInfo.targetAvailable == 0) continue;
                    var (nome, path) = NomeDoAlvo(p.targetInfo);
                    if (string.IsNullOrEmpty(path)) continue;

                    var ativo = ativos.FirstOrDefault(a => a.targetInfo.id == p.targetInfo.id &&
                        a.targetInfo.adapterId.Low == p.targetInfo.adapterId.Low && a.targetInfo.adapterId.High == p.targetInfo.adapterId.High);
                    bool isAtiva = ativos.Any(a => a.targetInfo.id == p.targetInfo.id &&
                        a.targetInfo.adapterId.Low == p.targetInfo.adapterId.Low && a.targetInfo.adapterId.High == p.targetInfo.adapterId.High);

                    if (!res.TryGetValue(path, out var info))
                    {
                        info = new TelaInfo { Path = path, Nome = nome, AdapterLow = p.targetInfo.adapterId.Low, AdapterHigh = p.targetInfo.adapterId.High, TargetId = p.targetInfo.id };
                        res[path] = info;
                    }
                    if (isAtiva) info.Ativa = true;

                    if (isAtiva)
                    {
                        // principal = fonte posicionada em (0,0)
                        int idx = (int)ativo.sourceInfo.modeInfoIdx;
                        if (idx >= 0 && idx < modosAtivos.Length && modosAtivos[idx].infoType == 1)
                        {
                            // SOURCE mode: width(4) height(4) pixelFormat(4) posX(4) posY(4) -> u0 = w|h<<32, u1 = fmt|posX<<32, u2 = posY
                            int posX = (int)(modosAtivos[idx].u1 >> 32);
                            int posY = (int)(modosAtivos[idx].u2 & 0xFFFFFFFF);
                            if (posX == 0 && posY == 0) info.Principal = true;
                        }
                        try
                        {
                            var h = LerHdr(info);
                            info.HdrSuportado = h.sup; info.HdrLigado = h.on;
                        }
                        catch { }
                    }
                }
                return res.Values.OrderByDescending(t => t.Ativa).ThenBy(t => t.Nome).ToList();
            }
        }

        /// <summary>Guarda o layout atual (quais telas estão acesas) para usar depois num modo.</summary>
        public static LayoutTelas CapturarAtual(string nome)
        {
            var ativas = Listar().Where(t => t.Ativa).ToList();
            return new LayoutTelas
            {
                Nome = nome,
                TelasAtivas = ativas.Select(t => t.Path).ToList(),
                NomesAtivos = ativas.Select(t => t.Nome).ToList(),
                PrincipalPath = ativas.FirstOrDefault(t => t.Principal)?.Path ?? ativas.FirstOrDefault()?.Path ?? ""
            };
        }

        /// <summary>
        /// Aplica o valor do campo "Tela" de um modo. Retorna (ok, mensagem).
        /// manter | internal | external | extend | clone | layout:&lt;id&gt;
        /// </summary>
        public static (bool ok, string msg) AplicarModo(string tela)
        {
            if (string.IsNullOrEmpty(tela) || tela == "manter") return (true, "Telas mantidas");
            if (tela.StartsWith("layout:", StringComparison.OrdinalIgnoreCase))
            {
                var id = tela.Substring(7);
                var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == id);
                if (lay == null) return (false, "Layout de telas não encontrado — abra o modo e escolha as telas.");
                return AplicarLayout(lay);
            }
            // topologias simples do Windows (não escolhem tela específica)
            string arg = tela switch { "internal" => "/internal", "external" => "/external", "extend" => "/extend", "clone" => "/clone", _ => null };
            if (arg == null) return (false, "Opção de tela desconhecida: " + tela);
            try
            {
                using var p = Process.Start(new ProcessStartInfo("DisplaySwitch.exe", arg) { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(5000);
                return (true, "Telas: " + tela);
            }
            catch (Exception ex) { Log.Error("DisplaySwitch falhou", ex); return (false, ex.Message); }
        }

        /// <summary>Deixa acesas só as telas do layout; apaga as demais. Valida antes e desfaz se falhar.</summary>
        public static (bool ok, string msg) AplicarLayout(LayoutTelas layout)
        {
            lock (_lock)
            {
                if (layout == null || layout.TelasAtivas.Count == 0) return (false, "Layout sem telas.");

                var telas = Listar();
                var desejadas = telas.Where(t => layout.TelasAtivas.Contains(t.Path, StringComparer.OrdinalIgnoreCase)).ToList();
                if (desejadas.Count == 0)
                    return (false, "Nenhuma das telas deste layout está conectada agora (TV desligada ou cabo solto?). Nada foi alterado.");

                if (!Consulta(QDC_ONLY_ACTIVE, out var backupPaths, out var backupModes))
                    return (false, "Não consegui ler a configuração atual das telas.");
                if (!Consulta(QDC_ALL_PATHS, out var all, out _))
                    return (false, "Não consegui listar as telas.");

                // monta 1 caminho por tela desejada, sem repetir a "fonte" (saída da placa)
                var escolhidos = new List<PATH_INFO>();
                var fontesUsadas = new HashSet<(long, uint)>();
                foreach (var t in desejadas)
                {
                    var candidatos = all.Where(p => p.targetInfo.targetAvailable != 0 && p.targetInfo.id == t.TargetId &&
                            p.targetInfo.adapterId.Low == (uint)t.AdapterLow && p.targetInfo.adapterId.High == t.AdapterHigh)
                        .OrderByDescending(p => (p.flags & PATH_ACTIVE) != 0).ToList();
                    bool achou = false;
                    foreach (var c in candidatos)
                    {
                        var chave = (((long)c.sourceInfo.adapterId.High << 32) | c.sourceInfo.adapterId.Low, c.sourceInfo.id);
                        if (fontesUsadas.Contains(chave)) continue;
                        var p = c;
                        p.flags |= PATH_ACTIVE;
                        p.sourceInfo.modeInfoIdx = IDX_INVALID;
                        p.targetInfo.modeInfoIdx = IDX_INVALID;
                        escolhidos.Add(p); fontesUsadas.Add(chave); achou = true; break;
                    }
                    if (!achou) return (false, $"Não achei uma saída livre para '{t.Nome}'.");
                }

                var arr = escolhidos.ToArray();

                // 1) valida sem mexer em nada
                int v = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_VALIDATE | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER);
                uint modoFlags = SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER;
                if (v != 0)
                {
                    // alguns drivers só aceitam via "use supplied" com mudanças permitidas
                    v = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_VALIDATE | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES);
                    modoFlags = SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES;
                    if (v != 0) { Log.Warn("Layout inválido para o driver, código " + v); return (false, "O Windows recusou esse layout de telas (código " + v + "). Nada foi alterado."); }
                }

                // 2) aplica
                int r = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_APPLY | SDC_SAVE_TO_DB | modoFlags);
                if (r != 0)
                {
                    Log.Error("SetDisplayConfig falhou: " + r);
                    Reverter(backupPaths, backupModes);
                    return (false, "Falhou ao trocar as telas (código " + r + "). Voltei como estava.");
                }

                // 3) confere se o resultado é o esperado
                // CORREÇÃO 4.1: TVs demoram a "acordar"; antes conferia uma vez só após 600 ms e às vezes
                // desfazia uma troca que tinha dado certo. Agora confere até 4 vezes (~2,6 s no máximo).
                HashSet<string> depois = null; bool bate = false;
                for (int tentativa = 0; tentativa < 4 && !bate; tentativa++)
                {
                    System.Threading.Thread.Sleep(tentativa == 0 ? 500 : 700);
                    depois = Listar().Where(t => t.Ativa).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    bate = desejadas.All(d => depois.Contains(d.Path)) && depois.Count == desejadas.Count;
                }
                Log.Info($"Layout '{layout.Nome}' aplicado. Acesas agora: {depois.Count} (esperado {desejadas.Count})");
                if (!bate)
                {
                    Reverter(backupPaths, backupModes);
                    return (false, "O resultado não bateu com o esperado. Voltei as telas como estavam.");
                }
                return (true, "Telas: " + string.Join(" + ", desejadas.Select(d => d.Nome)));
            }
        }

        /// <summary>Botão de pânico: acende todas as telas conectadas (estender).</summary>
        public static (bool ok, string msg) AcenderTodas()
        {
            var lay = new LayoutTelas { Nome = "Todas", TelasAtivas = Listar().Select(t => t.Path).ToList() };
            if (lay.TelasAtivas.Count == 0) return (false, "Nenhuma tela encontrada.");
            var r = AplicarLayout(lay);
            if (!r.ok) return AplicarModo("extend");
            return r;
        }

        private static void Reverter(PATH_INFO[] paths, MODE_INFO[] modes)
        {
            try
            {
                int r = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, SDC_APPLY | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DB);
                if (r != 0) { Log.Error("Reverter falhou: " + r); AplicarModo("extend"); }
            }
            catch (Exception ex) { Log.Error("Reverter exceção", ex); }
        }

        #region HDR
        private static (bool sup, bool on) LerHdr(TelaInfo t)
        {
            var i = new ADV_COLOR_INFO();
            i.header.type = 9; // GET_ADVANCED_COLOR_INFO
            i.header.size = (uint)Marshal.SizeOf<ADV_COLOR_INFO>();
            i.header.adapterId = new LUID { Low = (uint)t.AdapterLow, High = t.AdapterHigh };
            i.header.id = t.TargetId;
            if (DisplayConfigGetDeviceInfo(ref i) != 0) return (false, false);
            return ((i.value & 1) != 0, (i.value & 2) != 0);
        }

        /// <summary>Liga/desliga o HDR de uma tela. Retorna false se a tela não suporta.</summary>
        public static bool DefinirHdr(string telaPath, bool ligar)
        {
            lock (_lock)
            {
                var t = Listar().FirstOrDefault(x => string.Equals(x.Path, telaPath, StringComparison.OrdinalIgnoreCase));
                if (t == null || !t.HdrSuportado) return false;
                if (t.HdrLigado == ligar) return true;
                var s = new ADV_COLOR_SET();
                s.header.type = 10; // SET_ADVANCED_COLOR_STATE
                s.header.size = (uint)Marshal.SizeOf<ADV_COLOR_SET>();
                s.header.adapterId = new LUID { Low = (uint)t.AdapterLow, High = t.AdapterHigh };
                s.header.id = t.TargetId;
                s.enable = ligar ? 1u : 0u;
                return DisplayConfigSetDeviceInfo(ref s) == 0;
            }
        }

        /// <summary>HDR em todas as telas acesas que suportam (usado pelo checkbox HDR do modo).</summary>
        public static int DefinirHdrNasAtivas(bool ligar)
        {
            int n = 0;
            foreach (var t in Listar().Where(x => x.Ativa && x.HdrSuportado))
                if (DefinirHdr(t.Path, ligar)) n++;
            return n;
        }

        public static bool AlgumaAtivaSuportaHdr() => Listar().Any(x => x.Ativa && x.HdrSuportado);
        #endregion
    }
}
