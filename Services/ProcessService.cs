using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using GameModeHub.Core;
using Microsoft.Win32;

namespace GameModeHub.Services
{
    public class AppAberto
    {
        public string Exe { get; set; } = "";       // nome sem .exe
        public string Titulo { get; set; } = "";
        public string Caminho { get; set; } = "";
        public IntPtr Janela { get; set; }
        public int Pid { get; set; }
    }

    /// <summary>Um programa encontrado na varredura (tela Programas).</summary>
    public class ProgramaAchado
    {
        public string Exe { get; set; } = "";
        public string Nome { get; set; } = "";       // nome amigável (descrição do .exe)
        public string Caminho { get; set; } = "";
        public string Titulo { get; set; } = "";
        public bool TemJanela { get; set; }
    }

    public static class ProcessService
    {
        // Nunca fechar isto, nem se o usuário pedir sem querer.
        private static readonly HashSet<string> Protegidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer","winlogon","csrss","dwm","services","svchost","lsass","system","smss","wininit","idle","registry",
            "taskmgr","gamemodehub","searchhost","startmenuexperiencehost","shellexperiencehost","sihost","ctfmon",
            "audiodg","fontdrvhost","lockapp","textinputhost","runtimebroker","securityhealthsystray","msmpeng",
            "applicationframehost","systemsettings","widgets","widgetservice","phoneexperiencehost","searchapp","dllhost"
        };

        public static bool EhProtegido(string exe) => Protegidos.Contains(Path.GetFileNameWithoutExtension(exe ?? ""));

        /// <summary>
        /// Caminho do .exe de um processo. MELHORIA 4.1: usa QueryFullProcessImageName (rápido e funciona até com
        /// processos protegidos), em vez de Process.MainModule (lento e falhava com "Acesso negado").
        /// </summary>
        public static string CaminhoDoProcesso(int pid)
        {
            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                uint n = (uint)sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : "";
            }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>Apps com janela visível (para a tela Programas e para o celular).</summary>
        public static List<AppAberto> ListarAbertos()
        {
            var lista = new List<AppAberto>();
            int eu = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == eu || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    if (Protegidos.Contains(p.ProcessName)) continue;
                    lista.Add(new AppAberto { Exe = p.ProcessName, Titulo = p.MainWindowTitle, Caminho = CaminhoDoProcesso(p.Id), Janela = p.MainWindowHandle, Pid = p.Id });
                }
                catch { }
                finally { p.Dispose(); }
            }
            return lista.OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // pedaços de nome típicos de processos auxiliares (não aparecem na varredura se não tiverem janela)
        private static readonly string[] Auxiliares =
            { "helper", "crashpad", "crashhandler", "crashreport", "updater", "update", "webview", "service", "svc", "elevat", "installer", "setup",
              "conhost", "broker", "watchdog", "launcherhelper", "reporter", "telemetry", "cefsharp", "renderer" };

        /// <summary>
        /// NOVO 4.2: programas abertos do usuário (com janela ou só na bandeja), com o nome amigável do .exe.
        /// Ignora o Windows, o próprio GameModeHub e processos auxiliares sem janela.
        /// </summary>
        public static List<ProgramaAchado> Varrer()
        {
            var res = new Dictionary<string, ProgramaAchado>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ListarAbertos())
                if (!res.ContainsKey(a.Exe))
                    res[a.Exe] = new ProgramaAchado { Exe = a.Exe, Caminho = a.Caminho, TemJanela = true, Titulo = a.Titulo };

            int eu = Environment.ProcessId, sessao;
            using (var atual = Process.GetCurrentProcess()) sessao = atual.SessionId;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == eu || p.SessionId != sessao || res.ContainsKey(p.ProcessName) || Protegidos.Contains(p.ProcessName)) continue;
                    var nome = p.ProcessName.ToLowerInvariant();
                    if (Auxiliares.Any(nome.Contains)) continue;
                    var caminho = CaminhoDoProcesso(p.Id);
                    if (caminho.Length == 0 || caminho.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    if (caminho.IndexOf(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    res[p.ProcessName] = new ProgramaAchado { Exe = p.ProcessName, Caminho = caminho, TemJanela = false };
                }
                catch { }
                finally { p.Dispose(); }
            }
            foreach (var a in res.Values)
            {
                try
                {
                    var d = a.Caminho.Length > 0 ? FileVersionInfo.GetVersionInfo(a.Caminho) : null;
                    var amigavel = d?.FileDescription?.Trim();
                    if (string.IsNullOrWhiteSpace(amigavel) || amigavel.Length > 60) amigavel = d?.ProductName?.Trim();
                    a.Nome = string.IsNullOrWhiteSpace(amigavel) ? a.Exe : amigavel;
                }
                catch { a.Nome = a.Exe; }
            }
            return res.Values.OrderBy(a => a.Nome, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool EstaRodando(string caminhoOuNome)
        {
            if (string.IsNullOrWhiteSpace(caminhoOuNome) || caminhoOuNome.Contains("://")) return false;
            var nome = Path.GetFileNameWithoutExtension(caminhoOuNome);
            try
            {
                var ps = Process.GetProcessesByName(nome);
                bool r = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                return r;
            }
            catch { return false; }
        }

        /// <summary>
        /// Fecha os apps pedidos. Primeiro pede educadamente (salva jogo/abas); só força se não fechar.
        /// Devolve o caminho dos que foram fechados, para reabrir exatamente os mesmos depois.
        /// </summary>
        public static List<string> Fechar(IEnumerable<string> nomes, bool gentil, int esperaMs = 4000)
        {
            var fechados = new List<string>();
            foreach (var nome in nomes.Select(n => Path.GetFileNameWithoutExtension(n ?? "")).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (EhProtegido(nome)) continue;
                Process[] procs;
                try { procs = Process.GetProcessesByName(nome); } catch { continue; }
                if (procs.Length == 0) continue;

                string caminho = "";
                foreach (var p in procs) { try { caminho = CaminhoDoProcesso(p.Id); } catch { } if (caminho.Length > 0) break; }

                if (gentil)
                {
                    bool pediu = false;
                    foreach (var p in procs) { try { if (p.CloseMainWindow()) pediu = true; } catch { } }
                    // CORREÇÃO 4.1: só espera se algum processo tinha janela para receber o pedido
                    // (antes esperava 4 s à toa para apps só de bandeja, como Discord minimizado)
                    if (pediu)
                    {
                        var limite = DateTime.UtcNow.AddMilliseconds(esperaMs);
                        while (DateTime.UtcNow < limite && procs.Any(p => { try { return !p.HasExited; } catch { return false; } }))
                            Thread.Sleep(150);
                    }
                }
                bool algum = false;
                foreach (var p in procs)
                {
                    try { if (!p.HasExited) p.Kill(true); algum = true; }
                    catch (Exception ex) { Log.Warn($"Não consegui fechar {nome}: {ex.Message}"); }
                    finally { p.Dispose(); }
                }
                if (!algum) continue;
                fechados.Add(string.IsNullOrEmpty(caminho) ? nome : caminho);
                Log.Info("Fechado: " + nome);
            }
            return fechados;
        }

        /// <summary>Fecha UM processo (e os filhos dele) pelo PID.</summary>
        public static bool FecharProcesso(int pid, bool gentil, int esperaMs)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (gentil && p.CloseMainWindow()) p.WaitForExit(esperaMs);
                if (!p.HasExited) p.Kill(true);
                return true;
            }
            catch (Exception ex) { Log.Warn("Fechar processo " + pid + ": " + ex.Message); return false; }
        }

        public static void Abrir(string caminhoOuNome)
        {
            if (string.IsNullOrWhiteSpace(caminhoOuNome)) return;
            try { Process.Start(new ProcessStartInfo(caminhoOuNome) { UseShellExecute = true, WorkingDirectory = File.Exists(caminhoOuNome) ? Path.GetDirectoryName(caminhoOuNome) : "" })?.Dispose(); }
            catch (Exception ex) { Log.Warn("Não consegui abrir " + caminhoOuNome + ": " + ex.Message); }
        }

        public static void FocarJanela(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            Native.SetForegroundWindow(h);
        }

        public static bool FocarPorNome(string exe)
        {
            var nome = Path.GetFileNameWithoutExtension(exe ?? "");
            if (nome.Length == 0) return false;
            var a = ListarAbertos().FirstOrDefault(x => x.Exe.Equals(nome, StringComparison.OrdinalIgnoreCase));
            if (a == null) return false;
            FocarJanela(a.Janela); return true;
        }

        /// <summary>
        /// NOVO 4.1: foca a janela; se o programa estiver fechado, tenta abrir
        /// (resolve o "atalhos rápidos só focam janelas já abertas" do app do celular).
        /// </summary>
        public static (bool ok, bool abriu) FocarOuAbrir(string exe)
        {
            if (FocarPorNome(exe)) return (true, false);
            var nome = Path.GetFileNameWithoutExtension(exe ?? "");
            if (nome.Length == 0 || EhProtegido(nome)) return (false, false);
            try
            {
                Process.Start(new ProcessStartInfo(nome + ".exe") { UseShellExecute = true, ErrorDialog = false })?.Dispose();
                return (true, true);
            }
            catch { return (false, false); }
        }

        /// <summary>
        /// Fecha o app em primeiro plano (jogo travado) e solta o mouse.
        /// CORREÇÃO 4.1: identifica pelo processo dono da janela (antes falhava com jogos que têm mais de uma janela)
        /// e fecha só aquele processo, não todos com o mesmo nome.
        /// </summary>
        public static string FecharJanelaAtiva()
        {
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return null;
            string nome;
            try { using var p = Process.GetProcessById((int)pid); nome = p.ProcessName; } catch { return null; }
            if (EhProtegido(nome)) return null;
            FecharProcesso((int)pid, true, 2500);
            InputService.LiberarCursor();
            Log.Info("Jogo/app em foco fechado: " + nome);
            return nome;
        }

        /// <summary>
        /// NOVO 4.1 (volta da v3, opcional por modo): esvazia a memória dos programas em segundo plano.
        /// Retorna quantos bytes de RAM ficaram livres a mais.
        /// </summary>
        public static long LiberarMemoria()
        {
            long antes = StatsService.RamLivreBytes();
            int eu = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == eu || p.Id <= 4 || Protegidos.Contains(p.ProcessName)) continue;
                    var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, (uint)p.Id);
                    if (h == IntPtr.Zero) continue;
                    try { Native.EmptyWorkingSet(h); } finally { Native.CloseHandle(h); }
                }
                catch { }
                finally { p.Dispose(); }
            }
            Thread.Sleep(400);
            return Math.Max(0, StatsService.RamLivreBytes() - antes);
        }
    }

    /// <summary>Acha e abre os launchers sem caminhos fixos.</summary>
    public static class LauncherService
    {
        public static readonly string[] Opcoes = { "Nenhum", "Steam Big Picture", "Steam", "Playnite", "RetroArch", "Outro..." };

        public static string AcharSteam()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var p = k?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(p)) { var exe = Path.Combine(p, "steam.exe"); if (File.Exists(exe)) return Path.GetFullPath(exe); }
            }
            catch { }
            return null;
        }

        public static string AcharPlaynite()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var c in new[] { Path.Combine(local, "Playnite", "Playnite.FullscreenApp.exe"), Path.Combine(pf, "Playnite", "Playnite.FullscreenApp.exe") })
                if (File.Exists(c)) return c;
            return null;
        }

        public static string AcharRetroArch()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var c in new[] { Path.Combine(pf, "RetroArch", "retroarch.exe"), Path.Combine(pf, "RetroArch-Win64", "retroarch.exe"),
                                      Path.Combine(local, "RetroArch", "retroarch.exe"), @"C:\RetroArch-Win64\retroarch.exe", @"C:\RetroArch\retroarch.exe" })
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>Mostra o que foi detectado ("Detectado" no editor do modo).</summary>
        public static string Detectado(string launcher) => launcher switch
        {
            "Steam Big Picture" => AcharSteam(),
            "Steam" => AcharSteam(),
            "Playnite" => AcharPlaynite(),
            "RetroArch" => AcharRetroArch(),
            _ => null
        };

        public static bool Abrir(ModoModel m)
        {
            try
            {
                if (m.Launcher == "Outro...")
                {
                    if (string.IsNullOrWhiteSpace(m.LauncherCaminho) || !File.Exists(m.LauncherCaminho)) return false;
                    ProcessService.Abrir(m.LauncherCaminho); return true;
                }
                switch (m.Launcher)
                {
                    case "Steam Big Picture": if (AcharSteam() == null) return false; ProcessService.Abrir("steam://open/bigpicture"); return true;
                    case "Steam": if (AcharSteam() == null) return false; ProcessService.Abrir("steam://open/main"); return true;
                    case "Playnite": var p = AcharPlaynite(); if (p == null) return false; ProcessService.Abrir(p); return true;
                    case "RetroArch": var r = AcharRetroArch(); if (r == null) return false; ProcessService.Abrir(r); return true;
                    default: return true;
                }
            }
            catch (Exception ex) { Log.Error("Abrir launcher", ex); return false; }
        }

        /// <summary>Texto curto para o cartão do modo.</summary>
        public static string Descricao(ModoModel m)
        {
            if (m.Launcher == "Outro...")
                return string.IsNullOrWhiteSpace(m.LauncherCaminho) ? "programa não escolhido" : Path.GetFileNameWithoutExtension(m.LauncherCaminho);
            return m.Launcher;
        }
    }
}
